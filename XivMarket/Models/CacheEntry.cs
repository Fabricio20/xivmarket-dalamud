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
    string? FailureReason);
