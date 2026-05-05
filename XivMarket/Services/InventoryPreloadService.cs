using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivMarket.Services;

public sealed class InventoryPreloadService : IDisposable
{
    private static readonly string[] InventoryAddons = { "Inventory", "InventoryLarge", "InventoryExpansion" };
    private static readonly string[] SaddlebagAddons = { "InventoryBuddy" };
    private static readonly string[] ArmouryAddons = { "ArmouryBoard" };

    private static readonly InventoryType[] PlayerBags =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private static readonly InventoryType[] SaddleBags =
    {
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    };

    private static readonly InventoryType[] ArmourySlots =
    {
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    };

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate onInventoryUpdate;
    private readonly IAddonLifecycle.AddonEventDelegate onSaddlebagUpdate;
    private readonly IAddonLifecycle.AddonEventDelegate onArmouryUpdate;

    private DateTimeOffset lastInventoryPreload = DateTimeOffset.MinValue;
    private DateTimeOffset lastSaddlebagPreload = DateTimeOffset.MinValue;
    private DateTimeOffset lastArmouryPreload = DateTimeOffset.MinValue;
    private bool disposed;

    public InventoryPreloadService(Plugin plugin)
    {
        this.plugin = plugin;
        this.onInventoryUpdate = this.OnInventoryUpdate;
        this.onSaddlebagUpdate = this.OnSaddlebagUpdate;
        this.onArmouryUpdate = this.OnArmouryUpdate;

        Service.AddonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate, InventoryAddons, this.onInventoryUpdate);
        Service.AddonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate, SaddlebagAddons, this.onSaddlebagUpdate);
        Service.AddonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate, ArmouryAddons, this.onArmouryUpdate);

        plugin.Cache.BatchFetched += this.OnBatchFetched;
    }

    private void OnBatchFetched(int count, bool success, string? error)
    {
        if (this.disposed) return;
        if (success)
            Service.PluginLog.Information("[XivMarket] preload: fetched {Count} items", count);
        else
            Service.PluginLog.Warning("[XivMarket] preload: batch of {Count} items failed: {Error}", count, error ?? "unknown");
    }

    private void OnInventoryUpdate(AddonEvent type, AddonArgs args) =>
        this.TryPreload(ref this.lastInventoryPreload, PlayerBags, "inventory");

    private void OnSaddlebagUpdate(AddonEvent type, AddonArgs args) =>
        this.TryPreload(ref this.lastSaddlebagPreload, SaddleBags, "saddlebag");

    private void OnArmouryUpdate(AddonEvent type, AddonArgs args) =>
        this.TryPreload(ref this.lastArmouryPreload, ArmourySlots, "armoury");

    private void TryPreload(ref DateTimeOffset lastPreload, InventoryType[] bags, string label)
    {
        if (this.disposed) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastPreload < TimeSpan.FromSeconds(60)) return;
            lastPreload = now;

            if (!Service.ClientState.IsLoggedIn) return;
            var worldId = (int)Service.PlayerState.HomeWorld.RowId;
            if (worldId <= 0) return;

            var itemIds = ScanContainers(bags);
            if (itemIds.Count == 0) return;

            var marketable = itemIds.Where(this.plugin.Marketability.IsMarketable).ToList();

            if (this.plugin.Configuration.DebugLogging)
                Service.PluginLog.Information(
                    "[XivMarket] {Label} preload: {Total} items scanned, {Marketable} marketable",
                    label, itemIds.Count, marketable.Count);

            if (marketable.Count == 0) return;

            this.plugin.Cache.Prefetch(marketable, worldId);

            Service.PluginLog.Information(
                "[XivMarket] {Label} preload: queued {Count} items", label, marketable.Count);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "{Label} preload failed", label);
        }
    }

    private static unsafe List<int> ScanContainers(InventoryType[] bags)
    {
        var items = new HashSet<int>();
        var manager = InventoryManager.Instance();
        foreach (var bag in bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = &container->Items[i];
                if (slot->ItemId != 0)
                    items.Add((int)slot->ItemId);
            }
        }
        var sorted = items.ToList();
        sorted.Sort();
        return sorted;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        try
        {
            this.plugin.Cache.BatchFetched -= this.OnBatchFetched;
            Service.AddonLifecycle.UnregisterListener(this.onInventoryUpdate);
            Service.AddonLifecycle.UnregisterListener(this.onSaddlebagUpdate);
            Service.AddonLifecycle.UnregisterListener(this.onArmouryUpdate);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "InventoryPreloadService.Dispose failed");
        }
    }
}
