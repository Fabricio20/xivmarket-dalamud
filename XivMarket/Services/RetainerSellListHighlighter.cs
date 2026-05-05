using System;
using System.Globalization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivMarket.Services;

public sealed class RetainerSellListHighlighter : IDisposable
{
    private const string AddonName = "RetainerSellList";
    private const uint ComponentListNodeId = 11;
    private const uint PriceTextNodeId = 5;
    private const int AtkSlotBase = 15;
    private const int AtkSlotStride = 13;

    private static readonly ByteColor Red = new() { R = 255, G = 80, B = 80, A = 255 };
    private static readonly ByteColor Yellow = new() { R = 255, G = 210, B = 80, A = 255 };

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate onUpdate;
    private readonly IAddonLifecycle.AddonEventDelegate onSellClosed;
    private readonly Action<int, bool, string?> onBatchFetched;
    private readonly Action<int, int> onCacheUpdated;
    private ByteColor? originalColor;
    private bool addonOpen;
    private bool disposed;

    public RetainerSellListHighlighter(Plugin plugin)
    {
        this.plugin = plugin;
        this.onUpdate = this.OnUpdate;
        this.onSellClosed = this.OnSellClosed;
        this.onBatchFetched = this.OnBatchFetched;
        this.onCacheUpdated = this.OnCacheUpdated;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, this.onUpdate);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSell", this.onSellClosed);
        plugin.Cache.BatchFetched += this.onBatchFetched;
        plugin.Cache.Updated += this.onCacheUpdated;
    }

    private void OnSellClosed(AddonEvent type, AddonArgs args) =>
        this.ScheduleRefresh();

    private unsafe void OnUpdate(AddonEvent type, AddonArgs args)
    {
        if (this.disposed) return;
        this.addonOpen = true;
        try
        {
            this.Refresh((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "RetainerSellListHighlighter failed");
        }
    }

    private void OnCacheUpdated(int itemId, int worldId) =>
        this.ScheduleRefresh();

    private void OnBatchFetched(int count, bool success, string? error)
    {
        if (success) this.ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (this.disposed || !this.addonOpen) return;
        Service.Framework.RunOnFrameworkThread(() =>
        {
            if (this.disposed) return;
            try
            {
                unsafe
                {
                    var addonPtr = Service.GameGui.GetAddonByName(AddonName);
                    if (addonPtr.Address == 0 || !addonPtr.IsVisible)
                    {
                        this.addonOpen = false;
                        return;
                    }
                    this.Refresh((AtkUnitBase*)addonPtr.Address);
                }
            }
            catch (Exception ex)
            {
                Service.PluginLog.Error(ex, "RetainerSellListHighlighter failed");
            }
        });
    }

    private unsafe void Refresh(AtkUnitBase* addon)
    {
        if (addon == null) return;

        var componentList = addon->GetComponentListById(ComponentListNodeId);
        if (componentList == null) return;

        var worldId = this.GetHomeWorldId();
        if (worldId is null) return;

        var atkValues = addon->AtkValues;
        if (atkValues == null) return;

        var config = this.plugin.Configuration;
        var manager = InventoryManager.Instance();
        var listLength = componentList->ListLength;

        for (var i = 0; i < listLength; i++)
        {
            var renderer = componentList->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null) continue;

            var priceNode = renderer->GetTextNodeById(PriceTextNodeId);
            if (priceNode == null) continue;

            this.originalColor ??= priceNode->TextColor;

            var displayIndex = renderer->ListItemIndex;
            var atkIdx = AtkSlotBase + displayIndex * AtkSlotStride;
            if (atkIdx < 0 || atkIdx >= addon->AtkValuesCount)
            {
                RestoreNode(priceNode);
                continue;
            }

            if (!TryParseNumber(priceNode->NodeText.ToString(), out var rowPrice))
            {
                RestoreNode(priceNode);
                continue;
            }

            var inventorySlot = atkValues[atkIdx].Int;
            var item = manager->GetInventorySlot(InventoryType.RetainerMarket, inventorySlot);
            if (item == null || item->ItemId == 0)
            {
                RestoreNode(priceNode);
                continue;
            }

            var itemId = (int)item->ItemId;
            var isHq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var canBeHq = this.plugin.Marketability.CanBeHq(itemId);
            var vendorPrice = this.plugin.Marketability.VendorSellPrice(itemId, isHq);

            this.plugin.Cache.TryGet(itemId, worldId.Value, out var entry);
            var recommended = PriceCalculator.GetRecommendedPrice(
                entry?.Data, isHq, canBeHq,
                config.UndercutAmount, Math.Max(1, config.RoundTo), config.RoundUp,
                (PriceScope)config.PriceSourceScope, (QualityMode)config.UndercutQualityMode,
                vendorPrice);

            if (recommended is null || rowPrice == recommended.Value)
            {
                RestoreNode(priceNode);
            }
            else
            {
                priceNode->SetText(priceNode->NodeText.ToString());
                priceNode->TextColor = rowPrice < recommended.Value ? Red : Yellow;
            }
        }
    }

    private unsafe void RestoreNode(AtkTextNode* node)
    {
        node->SetText(node->OriginalTextPointer);
        node->TextColor = this.originalColor!.Value;
    }

    private static bool TryParseNumber(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var digits = new char[text.Length];
        var len = 0;
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
                digits[len++] = c;
        }
        if (len == 0) return false;
        return long.TryParse(digits.AsSpan(0, len), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
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
            Service.AddonLifecycle.UnregisterListener(this.onUpdate);
            Service.AddonLifecycle.UnregisterListener(this.onSellClosed);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "RetainerSellListHighlighter.Dispose failed");
        }
    }
}
