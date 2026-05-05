using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XivMarket.Models;

namespace XivMarket.Services;

/// <summary>
/// Loads the world→DC→region map from the API once at plugin startup. Until loaded, lookups
/// return null and the renderer silently drops the Region row (per the dedup rules).
/// </summary>
public sealed class WorldsService
{
    private readonly IXivMarketClient client;
    private readonly IPluginLog? log;
    private readonly Dictionary<int, WorldInfo> map = new();
    private readonly object lk = new();

    public WorldsService(IXivMarketClient client, IPluginLog? log = null)
    {
        this.client = client;
        this.log = log;
    }

    public WorldInfo? Lookup(int worldId)
    {
        lock (this.lk)
            return this.map.TryGetValue(worldId, out var w) ? w : null;
    }

    public int Count
    {
        get { lock (this.lk) return this.map.Count; }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var worlds = await this.client.GetWorldsAsync(ct).ConfigureAwait(false);
            lock (this.lk)
            {
                this.map.Clear();
                foreach (var w in worlds)
                    this.map[w.Id] = new WorldInfo(w.Id, w.Name, w.Datacenter);
            }
            this.log?.Information("Loaded {Count} worlds from XivMarket", worlds.Count);
        }
        catch (OperationCanceledException)
        {
            // Plugin disposed mid-load - no log.
        }
        catch (Exception ex)
        {
            this.log?.Warning(ex, "Failed to load worlds; region rows will be unavailable until next load.");
        }
    }
}
