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

    public TimeSpan CacheTtl => TimeSpan.FromSeconds(Math.Max(60, this.CacheTtlSeconds));

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
