using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMarket.Models;

namespace XivMarket.Services;

public sealed class InventoryHighlighter : IDisposable
{
    private static readonly string[] ParentAddons =
    {
        "Inventory", "InventoryLarge", "InventoryExpansion",
        "InventoryRetainer", "InventoryRetainerLarge",
    };

    private static readonly string[] CompactGrids = { "InventoryGrid" };
    private static readonly string[] LargeGrids = { "InventoryGrid0", "InventoryGrid1", "InventoryGrid2" };
    private static readonly string[] ExpansionGrids = { "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E" };

    private static readonly string[] RetainerCompactGrids = { "RetainerGrid" };
    private static readonly string[] RetainerLargeGrids = { "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4" };

    private const int ItemsPerPage = 35;

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate onParentPreDraw;
    private readonly IAddonLifecycle.AddonEventDelegate onRetainerSellListSetup;
    private readonly IAddonLifecycle.AddonEventDelegate onRetainerSellListFinalize;
    private readonly Action<int, bool, string?> onBatchFetched;
    private readonly Action<int, int> onCacheUpdated;

    private readonly HashSet<nint> highlightedNodes = new();
    private bool retainerOpen;
    private bool disposed;

    public bool Active { get; set; }

    public InventoryHighlighter(Plugin plugin)
    {
        this.plugin = plugin;
        this.onParentPreDraw = this.OnParentPreDraw;
        this.onRetainerSellListSetup = this.OnRetainerSellListSetup;
        this.onRetainerSellListFinalize = this.OnRetainerSellListFinalize;
        this.onBatchFetched = this.OnBatchFetched;
        this.onCacheUpdated = this.OnCacheUpdated;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSellList", this.onRetainerSellListSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSellList", this.onRetainerSellListFinalize);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, ParentAddons, this.onParentPreDraw);

        plugin.Cache.BatchFetched += this.onBatchFetched;
        plugin.Cache.Updated += this.onCacheUpdated;
    }

    private void OnRetainerSellListSetup(AddonEvent type, AddonArgs args)
    {
        this.retainerOpen = true;
    }

    private void OnRetainerSellListFinalize(AddonEvent type, AddonArgs args)
    {
        this.retainerOpen = false;
        this.Active = false;
        this.ClearAllHighlights();
    }

    private void OnBatchFetched(int count, bool success, string? error)
    {
        if (success && this.Active && this.retainerOpen)
            this.ScheduleRefresh();
    }

    private void OnCacheUpdated(int itemId, int worldId)
    {
        if (this.Active && this.retainerOpen)
            this.ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (this.disposed) return;
        Service.Framework.RunOnFrameworkThread(() =>
        {
            if (this.disposed || !this.Active || !this.retainerOpen) return;
            try
            {
                this.RefreshHighlights();
            }
            catch (Exception ex)
            {
                Service.PluginLog.Error(ex, "InventoryHighlighter.ScheduleRefresh failed");
            }
        });
    }

    private unsafe void OnParentPreDraw(AddonEvent type, AddonArgs args)
    {
        if (this.disposed) return;

        if (!this.Active || !this.retainerOpen)
        {
            if (this.highlightedNodes.Count > 0)
                this.ClearAllHighlights();
            return;
        }

        try
        {
            this.RefreshHighlights();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "InventoryHighlighter.OnParentPreDraw failed");
        }
    }

    private unsafe void RefreshHighlights()
    {
        var worldId = this.GetHomeWorldId();
        if (worldId is null) return;

        var layout = DetectActiveLayout();
        if (layout is null) return;

        var (tabIndex, pagesPerView, gridAddonNames, sorterPtr) = layout.Value;
        var sorter = (ItemOrderModuleSorter*)sorterPtr;
        if (sorter == null) return;

        var startIndex = tabIndex * pagesPerView * ItemsPerPage;

        var slotNodes = CollectVisibleSlotNodes(gridAddonNames);
        if (slotNodes.Count == 0)
        {
            if (this.highlightedNodes.Count > 0)
                this.ClearAllHighlights();
            return;
        }

        var config = this.plugin.Configuration;
        var vendorMultiplier = Math.Max(0.1f, config.HighlightVendorMultiplier);
        var minPrice = Math.Max(0, config.HighlightMinPrice);
        var minPriceIsTotal = config.HighlightMinPriceIsTotal;
        var nodesStillHighlighted = new HashSet<nint>();

        for (var i = 0; i < slotNodes.Count; i++)
        {
            var nodePtr = slotNodes[i];
            var node = (AtkResNode*)nodePtr;
            var sorterIndex = startIndex + i;

            var item = GetInventoryItemFromSorter(sorter, sorterIndex);
            if (item == null || item->ItemId == 0)
            {
                this.ClearNode(node);
                continue;
            }

            var itemId = (int)item->ItemId;
            if (!this.plugin.Marketability.IsMarketable(itemId))
            {
                this.ClearNode(node);
                continue;
            }

            this.plugin.Cache.TryGet(itemId, worldId.Value, out var entry);
            if (entry is null || entry.Status != LookupStatus.Loaded || entry.Data is null)
            {
                this.ClearNode(node);
                continue;
            }

            var isHq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var marketPrice = GetMarketPrice(entry.Data, isHq, this.plugin.Marketability.CanBeHq(itemId), config);
            if (marketPrice is null)
            {
                this.ClearNode(node);
                continue;
            }

            var quantity = item->Quantity;
            var vendorPrice = this.plugin.Marketability.VendorSellPrice(itemId, isHq);
            var isWorthSelling = IsWorthSelling(marketPrice.Value, vendorPrice, vendorMultiplier, minPrice, minPriceIsTotal, quantity);

            this.ApplyColor(node, isWorthSelling);
            nodesStillHighlighted.Add(nodePtr);
        }

        foreach (var ptr in this.highlightedNodes)
        {
            if (!nodesStillHighlighted.Contains(ptr))
            {
                try
                {
                    var node = (AtkResNode*)ptr;
                    node->AddRed = 0;
                    node->AddGreen = 0;
                    node->AddBlue = 0;
                }
                catch { /* node may have been freed */ }
            }
        }
        this.highlightedNodes.Clear();
        foreach (var ptr in nodesStillHighlighted)
            this.highlightedNodes.Add(ptr);
    }

    private static unsafe List<nint> CollectVisibleSlotNodes(string[] gridAddonNames)
    {
        var nodes = new List<nint>();
        foreach (var name in gridAddonNames.OrderBy(n => n))
        {
            var addon = Service.GameGui.GetAddonByName(name);
            if (addon.Address == 0 || !addon.IsVisible) continue;

            var grid = (AddonInventoryGrid*)addon.Address;
            for (var i = 0; i < ItemsPerPage; i++)
            {
                var slot = grid->Slots[i];
                if (slot.Value == null) continue;
                var ownerNode = slot.Value->OwnerNode;
                if (ownerNode == null || !ownerNode->IsVisible()) continue;
                nodes.Add((nint)ownerNode);
            }
        }
        return nodes;
    }

    private static unsafe (int TabIndex, int PagesPerView, string[] GridNames, nint Sorter)?
        DetectActiveLayout()
    {
        var orderModule = ItemOrderModule.Instance();
        if (orderModule == null) return null;

        var inv = Service.GameGui.GetAddonByName("Inventory");
        if (inv.Address != 0 && inv.IsVisible)
        {
            var addon = (AddonInventory*)inv.Address;
            var tab = addon->TabIndex;
            if (tab < 0 || tab > 3) return null;
            return (tab, 1, CompactGrids, (nint)orderModule->InventorySorter);
        }

        var large = Service.GameGui.GetAddonByName("InventoryLarge");
        if (large.Address != 0 && large.IsVisible)
        {
            var addon = (AddonInventoryLarge*)large.Address;
            var tab = addon->TabIndex;
            if (tab < 0 || tab > 1) return null;
            return (tab, 2, LargeGrids, (nint)orderModule->InventorySorter);
        }

        var expansion = Service.GameGui.GetAddonByName("InventoryExpansion");
        if (expansion.Address != 0 && expansion.IsVisible)
        {
            var addon = (AddonInventoryExpansion*)expansion.Address;
            var tab = addon->TabIndex;
            if (tab != 0) return null;
            return (0, 4, ExpansionGrids, (nint)orderModule->InventorySorter);
        }

        var retainer = Service.GameGui.GetAddonByName("InventoryRetainer");
        if (retainer.Address != 0 && retainer.IsVisible)
        {
            var addon = (AddonInventoryRetainer*)retainer.Address;
            var tab = addon->TabIndex;
            if (tab < 0 || tab > 4) return null;
            var sorter = orderModule->GetActiveRetainerSorter();
            if (sorter == null) return null;
            return (tab, 1, RetainerCompactGrids, (nint)sorter);
        }

        var retainerLarge = Service.GameGui.GetAddonByName("InventoryRetainerLarge");
        if (retainerLarge.Address != 0 && retainerLarge.IsVisible)
        {
            var addon = (AddonInventoryRetainerLarge*)retainerLarge.Address;
            var tab = addon->TabIndex;
            if (tab < 0 || tab > 3) return null;
            var sorter = orderModule->GetActiveRetainerSorter();
            if (sorter == null) return null;
            return (tab, 2, RetainerLargeGrids, (nint)sorter);
        }

        return null;
    }

    private static long? GetMarketPrice(ItemTooltip data, bool isHq, bool canBeHq, Configuration config)
    {
        var scope = (PriceScope)config.PriceSourceScope switch
        {
            PriceScope.World => data.World,
            PriceScope.Datacenter => data.Datacenter,
            PriceScope.Region => data.Region,
            _ => data.World,
        };

        var qualityMode = (QualityMode)config.UndercutQualityMode;
        var effectiveMode = qualityMode;
        if (!canBeHq && (effectiveMode == QualityMode.HqOnly || effectiveMode == QualityMode.MatchingQuality))
            effectiveMode = QualityMode.NqOnly;

        return effectiveMode switch
        {
            QualityMode.Any => GetCheapestAny(scope),
            QualityMode.MatchingQuality => GetLeafPrice(scope, isHq),
            QualityMode.NqOnly => GetLeafPrice(scope, false),
            QualityMode.HqOnly => GetLeafPrice(scope, true),
            _ => null,
        };
    }

    private static long? GetLeafPrice(Scope scope, bool hq)
    {
        var leaf = hq ? scope.Listing.Unit.Hq : scope.Listing.Unit.Nq;
        return leaf?.Price;
    }

    private static long? GetCheapestAny(Scope scope)
    {
        var nq = scope.Listing.Unit.Nq?.Price;
        var hq = scope.Listing.Unit.Hq?.Price;
        if (nq is null) return hq;
        if (hq is null) return nq;
        return Math.Min(nq.Value, hq.Value);
    }

    private static bool IsWorthSelling(long marketPrice, long vendorPrice, float vendorMultiplier, int minPrice, bool minPriceIsTotal, int quantity)
    {
        if (minPrice > 0)
        {
            var priceToCheck = minPriceIsTotal ? marketPrice * Math.Max(1, quantity) : marketPrice;
            if (priceToCheck < minPrice)
                return false;
        }
        if (vendorPrice > 0 && marketPrice < (long)(vendorPrice * vendorMultiplier))
            return false;
        return true;
    }

    private unsafe void ApplyColor(AtkResNode* node, bool isWorthSelling)
    {
        if (node == null) return;
        this.highlightedNodes.Add((nint)node);

        if (isWorthSelling)
        {
            node->AddRed = 0;
            node->AddGreen = 100;
            node->AddBlue = 0;
        }
        else
        {
            node->AddRed = 100;
            node->AddGreen = 0;
            node->AddBlue = 0;
        }
    }

    private unsafe void ClearNode(AtkResNode* node)
    {
        if (node == null) return;
        if (!this.highlightedNodes.Contains((nint)node)) return;
        node->AddRed = 0;
        node->AddGreen = 0;
        node->AddBlue = 0;
        this.highlightedNodes.Remove((nint)node);
    }

    public unsafe void ClearAllHighlights()
    {
        foreach (var ptr in this.highlightedNodes)
        {
            try
            {
                var node = (AtkResNode*)ptr;
                node->AddRed = 0;
                node->AddGreen = 0;
                node->AddBlue = 0;
            }
            catch { /* node may have been freed */ }
        }
        this.highlightedNodes.Clear();
    }

    private static unsafe InventoryItem* GetInventoryItemFromSorter(ItemOrderModuleSorter* sorter, int index)
    {
        if (sorter == null) return null;
        if (index < 0 || index >= (int)sorter->Items.Count) return null;

        var entry = sorter->Items[index].Value;
        if (entry == null) return null;

        var container = InventoryManager.Instance()->GetInventoryContainer(sorter->InventoryType + entry->Page);
        if (container == null) return null;

        return container->GetInventorySlot(entry->Slot);
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
            this.plugin.Cache.BatchFetched -= this.onBatchFetched;
            this.plugin.Cache.Updated -= this.onCacheUpdated;
            Service.AddonLifecycle.UnregisterListener(this.onParentPreDraw);
            Service.AddonLifecycle.UnregisterListener(this.onRetainerSellListSetup);
            Service.AddonLifecycle.UnregisterListener(this.onRetainerSellListFinalize);
            this.ClearAllHighlights();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "InventoryHighlighter.Dispose failed");
        }
    }
}
