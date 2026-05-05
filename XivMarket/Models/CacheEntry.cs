using System;

namespace XivMarket.Models;

public enum LookupStatus
{
    /// <summary>A fetch is in progress (initial request or background refresh of an empty entry).</summary>
    Loading,

    /// <summary>The entry holds a valid response from the server.</summary>
    Loaded,

    /// <summary>The most recent fetch failed. Sticky - only cleared by explicit refresh.</summary>
    Failed,

    /// <summary>Item is known to never be marketable (Lumina ItemSearchCategory == 0). No fetch will be attempted.</summary>
    NonMarketable,
}

public sealed record CacheEntry(
    LookupStatus Status,
    ItemTooltip? Data,
    DateTimeOffset FetchedAt,
    string? FailureReason,
    DateTimeOffset? DataFreshness = null)
{
    public static DateTimeOffset? DeriveFreshness(ItemTooltip? data)
    {
        if (data is null) return null;
        DateTimeOffset? max = null;
        CheckLeaf(data.World.Listing.Unit.Nq, ref max);
        CheckLeaf(data.World.Listing.Unit.Hq, ref max);
        CheckLeaf(data.World.Listing.Total.Nq, ref max);
        CheckLeaf(data.World.Listing.Total.Hq, ref max);
        CheckLeaf(data.Datacenter.Listing.Unit.Nq, ref max);
        CheckLeaf(data.Datacenter.Listing.Unit.Hq, ref max);
        CheckLeaf(data.Datacenter.Listing.Total.Nq, ref max);
        CheckLeaf(data.Datacenter.Listing.Total.Hq, ref max);
        CheckLeaf(data.Region.Listing.Unit.Nq, ref max);
        CheckLeaf(data.Region.Listing.Unit.Hq, ref max);
        CheckLeaf(data.Region.Listing.Total.Nq, ref max);
        CheckLeaf(data.Region.Listing.Total.Hq, ref max);
        return max;
    }

    private static void CheckLeaf(ListingLeaf? leaf, ref DateTimeOffset? max)
    {
        if (leaf is null) return;
        if (max is null || leaf.LastUpdated > max.Value)
            max = leaf.LastUpdated;
    }
}
