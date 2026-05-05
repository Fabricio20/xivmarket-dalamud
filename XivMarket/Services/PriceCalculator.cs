using System;
using XivMarket.Models;

namespace XivMarket.Services;

public enum PriceScope { World, Datacenter, Region }
public enum QualityMode { Any, MatchingQuality, NqOnly, HqOnly }

public static class PriceCalculator
{
    public static long? GetRecommendedPrice(
        ItemTooltip? data,
        bool isHq,
        bool canBeHq,
        int undercutAmount,
        int roundTo,
        bool roundUp,
        PriceScope scope,
        QualityMode qualityMode,
        long vendorPrice = 0)
    {
        if (data is null) return null;

        var scopeData = scope switch
        {
            PriceScope.World => data.World,
            PriceScope.Datacenter => data.Datacenter,
            PriceScope.Region => data.Region,
            _ => data.World,
        };

        var basePrice = GetBasePrice(scopeData, isHq, canBeHq, qualityMode);
        if (basePrice is null) return null;

        var price = (decimal)basePrice.Value;
        price -= undercutAmount;
        if (price < 1) price = 1;

        if (roundTo > 1)
        {
            price = roundUp
                ? Math.Ceiling(price / roundTo) * roundTo
                : Math.Floor(price / roundTo) * roundTo;
        }

        var result = Math.Max(1, (long)price);
        if (vendorPrice > 0)
            result = Math.Max(result, vendorPrice);

        return result;
    }

    private static long? GetBasePrice(Scope scopeData, bool isHq, bool canBeHq, QualityMode qualityMode)
    {
        var effectiveMode = qualityMode;
        if (!canBeHq && (effectiveMode == QualityMode.HqOnly || effectiveMode == QualityMode.MatchingQuality))
            effectiveMode = QualityMode.NqOnly;

        return effectiveMode switch
        {
            QualityMode.Any => GetCheapestAny(scopeData),
            QualityMode.MatchingQuality => GetLeafPrice(scopeData, isHq),
            QualityMode.NqOnly => GetLeafPrice(scopeData, false),
            QualityMode.HqOnly => GetLeafPrice(scopeData, true),
            _ => null,
        };
    }

    private static long? GetLeafPrice(Scope scopeData, bool hq)
    {
        var leaf = hq ? scopeData.Listing.Unit.Hq : scopeData.Listing.Unit.Nq;
        return leaf?.Price;
    }

    private static long? GetCheapestAny(Scope scopeData)
    {
        var nq = scopeData.Listing.Unit.Nq?.Price;
        var hq = scopeData.Listing.Unit.Hq?.Price;
        if (nq is null) return hq;
        if (hq is null) return nq;
        return Math.Min(nq.Value, hq.Value);
    }
}
