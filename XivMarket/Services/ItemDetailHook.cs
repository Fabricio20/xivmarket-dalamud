using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMarket.Models;

namespace XivMarket.Services;

/// <summary>
/// Wires the ItemDetail addon lifecycle to the cache + renderer + injector pipeline. Reads
/// modifier keys (CTRL = quality swap, ALT = force refresh) and the hovered item id, marshals
/// cache-update events back to the framework thread to refresh the visible tooltip.
///
/// All callbacks are wrapped in try/catch - a thrown exception inside a Dalamud lifecycle
/// listener can crash the game, so we log and bail rather than propagate.
/// </summary>
public sealed class ItemDetailHook : IDisposable
{
    private static readonly string[] AddonNames = { "ItemDetail" };

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate preUpdate;
    private readonly IAddonLifecycle.AddonEventDelegate postUpdate;
    private readonly Action<int, int> cacheUpdated;

    private ulong lastHoveredItem;
    private bool altWasDown;
    private bool disposed;

    public ItemDetailHook(Plugin plugin)
    {
        this.plugin = plugin;
        this.preUpdate = this.OnPreUpdate;
        this.postUpdate = this.OnPostUpdate;
        this.cacheUpdated = this.OnCacheUpdated;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, AddonNames, this.preUpdate);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonNames, this.postUpdate);
        plugin.Cache.Updated += this.cacheUpdated;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        try
        {
            this.plugin.Cache.Updated -= this.cacheUpdated;
            Service.AddonLifecycle.UnregisterListener(this.preUpdate);
            Service.AddonLifecycle.UnregisterListener(this.postUpdate);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "ItemDetailHook.Dispose failed");
        }
    }

    private unsafe void OnPreUpdate(AddonEvent type, AddonArgs args)
    {
        if (this.disposed) return;
        try
        {
            this.plugin.Injector.Restore((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "ItemDetail PreUpdate listener failed");
        }
    }

    private unsafe void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (this.disposed) return;
        try
        {
            var hovered = (ulong)Service.GameGui.HoveredItem;
            var itemChanged = hovered != this.lastHoveredItem;
            if (itemChanged)
            {
                this.lastHoveredItem = hovered;
                this.altWasDown = false;     // re-arm ALT edge on item change

                // Fire BEFORE any bail so we can diagnose normalization failures.
                if (this.plugin.Configuration.DebugLogging)
                    Service.PluginLog.Information("[XivMarket] hover-raw raw={Raw}", hovered);
            }

            var altHeld = Service.KeyState[VirtualKey.MENU];
            var altEdge = altHeld && !this.altWasDown;
            this.altWasDown = altHeld;

            var itemId = NormalizeHoveredItem(hovered);
            if (itemId is null) return;
            var worldId = this.GetHomeWorldId();
            if (worldId is null) return;

            CacheEntry entry;
            if (altEdge)
            {
                if (this.plugin.Configuration.DebugLogging)
                    Service.PluginLog.Information(
                        "[XivMarket] alt-refresh item={Item} world={World}",
                        itemId.Value, worldId.Value);
                this.plugin.Cache.Refresh(itemId.Value, worldId.Value);
                entry = new CacheEntry(LookupStatus.Loading, null, DateTimeOffset.UtcNow, null);
            }
            else
            {
                entry = this.plugin.Cache.GetOrRequest(itemId.Value, worldId.Value);
            }

            if (itemChanged && this.plugin.Configuration.DebugLogging)
                this.LogHoverDiagnostic(hovered, itemId.Value, worldId.Value, entry);

            this.RenderInto((AtkUnitBase*)args.Addon.Address, hovered, entry);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "ItemDetail PostUpdate listener failed");
        }
    }

    private void LogHoverDiagnostic(ulong hovered, int itemId, int worldId, CacheEntry entry)
    {
        var isHq = hovered >= 500_000;     // mirrors BuildRenderContext
        var ctrl = Service.KeyState[VirtualKey.CONTROL];
        var alt = Service.KeyState[VirtualKey.MENU];
        var canBeHq = this.plugin.Marketability.CanBeHq(itemId);
        var marketable = this.plugin.Marketability.IsMarketable(itemId);
        Service.PluginLog.Information(
            "[XivMarket] hover raw={Raw} item={Item} world={World} isHq={IsHq} canBeHq={CanBeHq} marketable={Marketable} ctrl={Ctrl} alt={Alt} status={Status}",
            hovered, itemId, worldId, isHq, canBeHq, marketable, ctrl, alt, entry.Status);
    }

    private void OnCacheUpdated(int itemId, int worldId)
    {
        if (this.disposed) return;

        // Cache fires updates from a thread-pool thread - bounce to the framework thread before
        // touching any game state. RunOnFrameworkThread does NOT swallow exceptions.
        Service.Framework.RunOnFrameworkThread(() =>
        {
            if (this.disposed) return;
            try
            {
                var hovered = (ulong)Service.GameGui.HoveredItem;
                var normalized = NormalizeHoveredItem(hovered);
                if (normalized != itemId) return;
                if (this.GetHomeWorldId() != worldId) return;

                var addon = Service.GameGui.GetAddonByName("ItemDetail");
                if (addon.Address == 0 || !addon.IsVisible) return;

                unsafe
                {
                    var atk = (AtkUnitBase*)addon.Address;
                    if (atk == null) return;
                    this.plugin.Injector.Restore(atk);
                    var entry = this.plugin.Cache.GetOrRequest(itemId, worldId);
                    this.RenderInto(atk, hovered, entry);
                }
            }
            catch (Exception ex)
            {
                Service.PluginLog.Error(ex, "ItemDetailHook cache-update refresh failed");
            }
        });
    }

    private unsafe void RenderInto(AtkUnitBase* atk, ulong hovered, CacheEntry entry)
    {
        var ctx = this.BuildRenderContext(hovered);
        var doc = TooltipRenderer.Render(entry, ctx);
        if (this.plugin.Configuration.DebugLogging)
        {
            Service.PluginLog.Information(
                "[XivMarket] render quality-isHq={IsHq} canBeHq={CanBeHq} status={Status} lines={Lines} empty={Empty}",
                ctx.IsHq, ctx.CanBeHq, entry.Status, doc.Lines.Count, doc.IsEmpty);
        }
        if (doc.IsEmpty) return;
        var seString = SeStringAdapter.ToSeString(doc);
        this.plugin.Injector.Inject(atk, seString);
    }

    private RenderContext BuildRenderContext(ulong hovered)
    {
        // hovered = baseId + 500_000 * k, where k flags HQ / collectable / both. Modulo strips
        // any offset; "any offset > 0" implies HQ for our purposes (matches Price Insight).
        // The game itself flips the encoding when CTRL is held, so we don't read CTRL here.
        var isHq = hovered >= 500_000;
        var baseId = (int)(hovered % 500_000);
        return new RenderContext(
            IsHq: isHq,
            CanBeHq: this.plugin.Marketability.CanBeHq(baseId),
            UseCheapestTotalStack: this.plugin.Configuration.UseCheapestTotalStack,
            WorldLookup: this.plugin.Worlds.Lookup,
            Now: DateTimeOffset.UtcNow);
    }

    private int? GetHomeWorldId()
    {
        try
        {
            if (!Service.ClientState.IsLoggedIn) return null;
            var id = (int)Service.PlayerState.HomeWorld.RowId;
            return id > 0 ? id : null;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, "Failed to read home world id");
            return null;
        }
    }

    /// <summary>
    /// Maps the game's hovered-item id to the canonical Lumina item id. Game encoding:
    /// <c>baseId + 500_000 * k</c> where k flags variants (HQ / collectable / both).
    /// We strip any offset via modulo and bail only on event items (≥ 2_000_000), matching
    /// Price Insight's tolerance - earlier versions of this code over-filtered the 1M+ range
    /// and lost HQ inventory hovers.
    /// </summary>
    private static int? NormalizeHoveredItem(ulong hovered)
    {
        if (hovered == 0) return null;
        if (hovered >= 2_000_000) return null;        // event items / non-tradable internal ids
        return (int)(hovered % 500_000);
    }
}
