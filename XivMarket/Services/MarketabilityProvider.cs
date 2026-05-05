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
    private readonly ConcurrentDictionary<int, (bool Marketable, bool CanBeHq)> cache = new();

    public MarketabilityProvider(IDataManager data, IPluginLog? log = null)
    {
        this.data = data;
        this.log = log;
    }

    public bool IsMarketable(int itemId) => this.GetOrCompute(itemId).Marketable;

    public bool CanBeHq(int itemId) => this.GetOrCompute(itemId).CanBeHq;

    private (bool Marketable, bool CanBeHq) GetOrCompute(int itemId) =>
        this.cache.GetOrAdd(itemId, this.Compute);

    private (bool Marketable, bool CanBeHq) Compute(int itemId)
    {
        if (itemId <= 0)
            return (false, false);
        try
        {
            var sheet = this.data.GetExcelSheet<Item>();
            if (!sheet.TryGetRow((uint)itemId, out var row))
                return (false, false);
            return (row.ItemSearchCategory.RowId != 0, row.CanBeHq);
        }
        catch (Exception ex)
        {
            this.log?.Warning(ex, "Lumina lookup failed for item {Id}", itemId);
            return (false, false);
        }
    }
}
