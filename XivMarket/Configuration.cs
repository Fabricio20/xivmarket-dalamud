using System;
using Dalamud.Configuration;

namespace XivMarket;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string ApiBaseUrl { get; set; } = "https://xivapi.notfab.net";

    /// <summary>How long a Loaded cache entry is considered fresh. Min 60s, default 300s.</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>If true, pick the listing with the lowest *total* outlay instead of lowest per-unit price.</summary>
    public bool UseCheapestTotalStack { get; set; } = false;

    /// <summary>If true, log per-hover diagnostic info (hovered id, isHq, ctrl, cache status). Verbose.</summary>
    public bool DebugLogging { get; set; } = false;

    /// <summary>Gil to subtract from cheapest price. 0 = match cheapest exactly.</summary>
    public int UndercutAmount { get; set; } = 0;

    /// <summary>Round to nearest multiple. 1 = no rounding, 10 = round to 10s, etc.</summary>
    public int RoundTo { get; set; } = 10;

    /// <summary>If true, round UP (encourages stable prices). If false, round DOWN.</summary>
    public bool RoundUp { get; set; } = true;

    /// <summary>Which scope to pull the base price from. 0=World, 1=DC, 2=Region.</summary>
    public int PriceSourceScope { get; set; } = 0;

    /// <summary>Quality comparison mode. 0=Any, 1=MatchingQuality, 2=NqOnly, 3=HqOnly.</summary>
    public int UndercutQualityMode { get; set; } = 1;

    /// <summary>Vendor price multiplier for inventory highlighting. Items with market price below vendor * this are red.</summary>
    public float HighlightVendorMultiplier { get; set; } = 2.0f;

    /// <summary>Absolute minimum market price for green highlight. 0 = disabled.</summary>
    public int HighlightMinPrice { get; set; } = 1000;

    /// <summary>If true, the minimum price floor checks total stack value (price * quantity) instead of per-unit.</summary>
    public bool HighlightMinPriceIsTotal { get; set; } = true;

    public TimeSpan CacheTtl => TimeSpan.FromSeconds(Math.Max(60, this.CacheTtlSeconds));

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
