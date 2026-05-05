using System;
using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivMarket.Services;

/// <summary>
/// Wraps Lumina lookups for marketability and HQ-capability. Both flags are cached per item id
/// since they're static metadata. Any Lumina exception falls back to (false, false).
/// </summary>
public sealed class MarketabilityProvider
{
    private readonly IDataManager data;
    private readonly IPluginLog? log;
    private readonly ConcurrentDictionary<int, (bool Marketable, bool CanBeHq, uint PriceLow, uint PriceMid)> cache = new();

    public MarketabilityProvider(IDataManager data, IPluginLog? log = null)
    {
        this.data = data;
        this.log = log;
    }

    public bool IsMarketable(int itemId) => this.GetOrCompute(itemId).Marketable;

    public bool CanBeHq(int itemId) => this.GetOrCompute(itemId).CanBeHq;

    public long VendorSellPrice(int itemId, bool isHq = false)
    {
        var entry = this.GetOrCompute(itemId);
        var basePrice = entry.PriceLow > 0
            ? (long)entry.PriceLow
            : (long)(entry.PriceMid / 50);
        if (isHq && basePrice > 0)
            basePrice = (long)Math.Ceiling(basePrice * 1.1m);
        return basePrice;
    }

    private (bool Marketable, bool CanBeHq, uint PriceLow, uint PriceMid) GetOrCompute(int itemId) =>
        this.cache.GetOrAdd(itemId, this.Compute);

    private (bool Marketable, bool CanBeHq, uint PriceLow, uint PriceMid) Compute(int itemId)
    {
        if (itemId <= 0)
            return (false, false, 0, 0);
        try
        {
            var sheet = this.data.GetExcelSheet<Item>();
            if (!sheet.TryGetRow((uint)itemId, out var row))
                return (false, false, 0, 0);
            return (row.ItemSearchCategory.RowId != 0, row.CanBeHq, row.PriceLow, row.PriceMid);
        }
        catch (Exception ex)
        {
            this.log?.Warning(ex, "Lumina lookup failed for item {Id}", itemId);
            return (false, false, 0, 0);
        }
    }
}
