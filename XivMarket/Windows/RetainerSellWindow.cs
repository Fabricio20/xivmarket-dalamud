using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMarket.Services;

namespace XivMarket.Windows;

public sealed class RetainerSellWindow : Window, IDisposable
{
    private const string AddonName = "RetainerSell";

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate onPostSetup;
    private readonly IAddonLifecycle.AddonEventDelegate onPreFinalize;
    private readonly IAddonLifecycle.AddonEventDelegate onPostDraw;
    private readonly Action<int, int> onCacheUpdated;

    private uint currentItemId;
    private bool currentIsHq;
    private int currentWorldId;
    private long? recommendedPrice;
    private string qualityLabel = "NQ";
    private string? statusMessage;
    private bool disposed;

    public RetainerSellWindow(Plugin plugin)
        : base("XIV Market##retainer-sell", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        this.ForceMainWindow = true;
        this.RespectCloseHotkey = false;
        this.IsOpen = false;

        this.onPostSetup = this.OnPostSetup;
        this.onPreFinalize = this.OnPreFinalize;
        this.onPostDraw = this.OnPostDraw;
        this.onCacheUpdated = this.OnCacheUpdated;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, this.onPostSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, this.onPreFinalize);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, this.onPostDraw);
        plugin.Cache.Updated += this.onCacheUpdated;
    }

    private unsafe void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        this.IsOpen = true;
        this.BindPosition(args.Addon);
        this.Recompute();
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        this.IsOpen = false;
        this.currentItemId = 0;
    }

    private unsafe void OnPostDraw(AddonEvent type, AddonArgs args)
    {
        this.BindPosition(args.Addon);
    }

    private void OnCacheUpdated(int itemId, int worldId)
    {
        if (!this.IsOpen) return;
        if (itemId != (int)this.currentItemId || worldId != this.currentWorldId) return;
        Service.Framework.RunOnFrameworkThread(this.Recompute);
    }

    private unsafe void Recompute()
    {
        this.currentItemId = this.GetCurrentItemId();
        this.currentIsHq = this.GetCurrentItemIsHq();

        if (this.currentItemId == 0)
        {
            this.statusMessage = "No item selected.";
            this.recommendedPrice = null;
            return;
        }

        var worldId = this.GetHomeWorldId();
        if (worldId is null)
        {
            this.statusMessage = "Not logged in.";
            this.recommendedPrice = null;
            return;
        }
        this.currentWorldId = worldId.Value;

        var canBeHq = this.plugin.Marketability.CanBeHq((int)this.currentItemId);
        this.qualityLabel = this.currentIsHq && canBeHq ? "HQ" : "NQ";

        this.plugin.Cache.TryGet((int)this.currentItemId, worldId.Value, out var entry);
        var data = entry?.Data;

        if (this.plugin.Configuration.DebugLogging)
            Service.PluginLog.Information(
                "[XivMarket] retainer-sell: item={Item} world={World} status={Status} hasData={HasData}",
                this.currentItemId, worldId.Value, entry?.Status, data != null);

        var vendorPrice = (long)this.plugin.Marketability.VendorSellPrice((int)this.currentItemId);
        var config = this.plugin.Configuration;
        this.recommendedPrice = PriceCalculator.GetRecommendedPrice(
            data,
            this.currentIsHq,
            canBeHq,
            config.UndercutAmount,
            Math.Max(1, config.RoundTo),
            config.RoundUp,
            (PriceScope)config.PriceSourceScope,
            (QualityMode)config.UndercutQualityMode,
            vendorPrice);

        this.statusMessage = this.recommendedPrice is null ? "No price data available." : null;
    }

    private unsafe void BindPosition(nint addon)
    {
        try
        {
            if (addon == nint.Zero) return;
            var atk = (AtkUnitBase*)addon;
            var x = atk->X + atk->GetScaledWidth(true);
            var y = (float)atk->Y + 2;
            this.Position = new Vector2(x, y);
        }
        catch { /* best effort positioning */ }
    }

    public override void Draw()
    {
        if (this.statusMessage != null)
        {
            ImGui.Text($"[{this.qualityLabel}]");
            ImGui.SameLine();
            ImGui.TextDisabled(this.statusMessage);
            return;
        }

        ImGui.Text($"[{this.qualityLabel}] Recommended:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"{this.recommendedPrice!.Value:N0}g");

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy to Game"))
        {
            this.SetAskingPrice((int)this.recommendedPrice.Value);
        }
    }

    private unsafe void SetAskingPrice(int price)
    {
        try
        {
            var addon = Service.GameGui.GetAddonByName(AddonName);
            if (addon.Address == 0) return;
            var retainerSell = (AddonRetainerSell*)addon.Address;
            if (retainerSell->AskingPrice == null) return;
            retainerSell->AskingPrice->SetValue(price);

            if (this.plugin.Configuration.DebugLogging)
                Service.PluginLog.Information("[XivMarket] copy to game: {Price}g", price);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Failed to set asking price");
        }
    }

    private unsafe uint GetCurrentItemId()
    {
        try
        {
            var slot = InventoryManager.Instance()->GetInventorySlot(InventoryType.BlockedItems, 0);
            if (slot == null) return 0;
            return slot->ItemId;
        }
        catch { return 0; }
    }

    private unsafe bool GetCurrentItemIsHq()
    {
        try
        {
            var slot = InventoryManager.Instance()->GetInventorySlot(InventoryType.BlockedItems, 0);
            if (slot == null) return false;
            return (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        }
        catch { return false; }
    }

    private int? GetHomeWorldId()
    {
        try
        {
            if (!Service.ClientState.IsLoggedIn) return null;
            var id = (int)Service.PlayerState.HomeWorld.RowId;
            return id > 0 ? id : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        try
        {
            this.plugin.Cache.Updated -= this.onCacheUpdated;
            Service.AddonLifecycle.UnregisterListener(this.onPostSetup);
            Service.AddonLifecycle.UnregisterListener(this.onPreFinalize);
            Service.AddonLifecycle.UnregisterListener(this.onPostDraw);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "RetainerSellWindow.Dispose failed");
        }
    }
}
