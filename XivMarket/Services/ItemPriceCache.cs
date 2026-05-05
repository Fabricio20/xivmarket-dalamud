using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMarket.Models;

namespace XivMarket.Services;

/// <summary>
/// Cache of <see cref="ItemTooltip"/> entries keyed by (itemId, worldId).
///
/// The renderer calls <see cref="GetOrRequest"/> for hover events; cold lookups return a
/// <see cref="LookupStatus.Loading"/> entry and enqueue a debounced batch fetch. Multiple hovers
/// in rapid succession coalesce into one /tooltip request via the debounce window.
///
/// TTL is checked on every <see cref="GetOrRequest"/>: a stale-but-Loaded entry is returned
/// immediately and a background refresh is enqueued (stale-while-revalidate). Failed entries are
/// sticky and only retry via <see cref="Refresh"/> (the ALT-key path on the hook layer).
/// </summary>
public sealed class ItemPriceCache : IDisposable
{
    private readonly IXivMarketClient client;
    private readonly Func<int, bool> isMarketable;
    private readonly Func<TimeSpan> ttlProvider;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeSpan debounce;
    private readonly int batchLimit;

    private readonly ConcurrentDictionary<(int item, int world), CacheEntry> entries = new();
    private readonly CancellationTokenSource lifetimeCts = new();

    private readonly object queueLock = new();
    private Dictionary<int, HashSet<int>> queueByWorld = new();
    private CancellationTokenSource? debounceCts;

    /// <summary>Fired (on a thread-pool thread) after an entry's status transitions due to a fetch.</summary>
    public event Action<int, int>? Updated;

    /// <summary>Fired (on a thread-pool thread) after a batch HTTP request completes. Args: (count, success, errorMessage?).</summary>
    public event Action<int, bool, string?>? BatchFetched;

    /// <summary>Optional log sink for diagnostic messages (wired by the plugin to IPluginLog).</summary>
    public Action<string, object?[]>? DebugLog { get; set; }

    public ItemPriceCache(
        IXivMarketClient client,
        Func<int, bool> isMarketable,
        Func<TimeSpan> ttlProvider,
        Func<DateTimeOffset>? now = null,
        TimeSpan? debounce = null,
        int batchLimit = XivMarketClient.TooltipBatchLimit)
    {
        this.client = client;
        this.isMarketable = isMarketable;
        this.ttlProvider = ttlProvider;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        this.batchLimit = batchLimit;
    }

    /// <summary>Non-blocking peek. Returns false if the item has never been requested.</summary>
    public bool TryGet(int itemId, int worldId, out CacheEntry? entry) =>
        this.entries.TryGetValue((itemId, worldId), out entry);

    /// <summary>
    /// Returns the current cache entry, queuing a fetch if missing or stale (but-Loaded).
    /// Failed and NonMarketable entries are returned as-is and never auto-retry.
    /// </summary>
    public CacheEntry GetOrRequest(int itemId, int worldId)
    {
        if (this.entries.TryGetValue((itemId, worldId), out var existing))
        {
            if (existing.Status == LookupStatus.Loaded && this.IsExpired(existing))
                this.EnqueueFetch(itemId, worldId);
            return existing;
        }

        if (!this.isMarketable(itemId))
        {
            var nm = new CacheEntry(LookupStatus.NonMarketable, null, this.now(), null);
            this.entries[(itemId, worldId)] = nm;
            return nm;
        }

        var loading = new CacheEntry(LookupStatus.Loading, null, this.now(), null);
        if (this.entries.TryAdd((itemId, worldId), loading))
            this.EnqueueFetch(itemId, worldId);
        else
            this.entries.TryGetValue((itemId, worldId), out loading);

        return loading!;
    }

    /// <summary>
    /// Forces a fetch regardless of TTL or sticky state (ALT-key behaviour). Non-marketable
    /// items are still short-circuited - Refresh respects the Lumina pre-filter.
    /// </summary>
    public void Refresh(int itemId, int worldId)
    {
        if (!this.isMarketable(itemId))
        {
            this.entries[(itemId, worldId)] = new CacheEntry(LookupStatus.NonMarketable, null, this.now(), null);
            return;
        }
        this.entries[(itemId, worldId)] = new CacheEntry(LookupStatus.Loading, null, this.now(), null);
        this.EnqueueFetch(itemId, worldId);
    }

    /// <summary>
    /// Bulk path - bypasses the hover debounce queue and fires batches directly. Items already
    /// Loaded and fresh are skipped. Chunks larger than <see cref="XivMarketClient.TooltipBatchLimit"/>
    /// are split.
    /// </summary>
    public void Prefetch(IReadOnlyCollection<int> itemIds, int worldId)
    {
        var pending = new List<int>();
        foreach (var id in itemIds)
        {
            if (!this.isMarketable(id))
            {
                this.entries[(id, worldId)] = new CacheEntry(LookupStatus.NonMarketable, null, this.now(), null);
                continue;
            }
            if (this.entries.TryGetValue((id, worldId), out var e)
                && e.Status == LookupStatus.Loaded
                && !this.IsExpired(e))
                continue;

            this.entries[(id, worldId)] = new CacheEntry(LookupStatus.Loading, null, this.now(), null);
            pending.Add(id);
        }

        for (var i = 0; i < pending.Count; i += this.batchLimit)
        {
            var chunk = pending.Skip(i).Take(this.batchLimit).ToArray();
            _ = this.FetchBatch(chunk, worldId);
        }
    }

    /// <summary>
    /// Merges live game data into the cache. Always wins over API data (game is freshest source).
    /// </summary>
    public void Splice(SpliceUpdate update)
    {
        this.entries.TryGetValue((update.ItemId, update.WorldId), out var existing);
        var result = SpliceLogic.ApplySplice(existing?.Data, update);

        this.DebugLog?.Invoke(
            "[XivMarket] splice item={Item} {Quality} {Price}g x{Qty} world={World} dc={DcDec} region={RegionDec}",
            new object?[] { update.ItemId, update.IsHq ? "HQ" : "NQ", update.Price, update.Quantity, update.WorldName, result.DcDecision, result.RegionDecision });

        var newEntry = new CacheEntry(
            LookupStatus.Loaded,
            result.Tooltip,
            existing?.FetchedAt ?? this.now(),
            null,
            update.Timestamp);

        this.entries[(update.ItemId, update.WorldId)] = newEntry;
        this.Updated?.Invoke(update.ItemId, update.WorldId);
    }

    private bool IsExpired(CacheEntry e) => this.now() - e.FetchedAt > this.ttlProvider();

    private void EnqueueFetch(int itemId, int worldId)
    {
        lock (this.queueLock)
        {
            if (!this.queueByWorld.TryGetValue(worldId, out var set))
            {
                set = new HashSet<int>();
                this.queueByWorld[worldId] = set;
            }
            set.Add(itemId);

            this.debounceCts?.Cancel();
            this.debounceCts = new CancellationTokenSource();
            var token = this.debounceCts.Token;

            _ = Task.Delay(this.debounce, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                this.FlushQueue();
            }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }
    }

    private void FlushQueue()
    {
        Dictionary<int, HashSet<int>> snapshot;
        lock (this.queueLock)
        {
            if (this.queueByWorld.Count == 0) return;
            snapshot = this.queueByWorld;
            this.queueByWorld = new Dictionary<int, HashSet<int>>();
        }

        foreach (var (worldId, items) in snapshot)
        {
            var ids = items.ToArray();
            for (var i = 0; i < ids.Length; i += this.batchLimit)
            {
                var chunk = ids.Skip(i).Take(this.batchLimit).ToArray();
                _ = this.FetchBatch(chunk, worldId);
            }
        }
    }

    private async Task FetchBatch(int[] itemIds, int worldId)
    {
        try
        {
            var result = await this.client
                .GetTooltipAsync(worldId, itemIds, this.lifetimeCts.Token)
                .ConfigureAwait(false);

            var fetchedAt = this.now();
            foreach (var id in itemIds)
            {
                if (!result.TryGetValue(id, out var data))
                {
                    if (!this.ShouldOverwriteWithFailure(id, worldId))
                        continue;
                    this.entries[(id, worldId)] = new CacheEntry(LookupStatus.Failed, null, fetchedAt, "Server omitted item from response");
                    this.Updated?.Invoke(id, worldId);
                    continue;
                }

                var freshness = CacheEntry.DeriveFreshness(data);
                if (this.entries.TryGetValue((id, worldId), out var existing)
                    && existing.DataFreshness.HasValue
                    && freshness.HasValue
                    && freshness.Value <= existing.DataFreshness.Value)
                {
                    this.DebugLog?.Invoke(
                        "[XivMarket] freshness gate: skipping API data for item={Item} (api={ApiFreshness}, cached={CachedFreshness})",
                        new object?[] { id, freshness.Value, existing.DataFreshness.Value });
                    continue;
                }

                var entry = new CacheEntry(LookupStatus.Loaded, data, fetchedAt, null, freshness);
                this.entries[(id, worldId)] = entry;
                this.Updated?.Invoke(id, worldId);
            }
            this.BatchFetched?.Invoke(itemIds.Length, true, null);
        }
        catch (OperationCanceledException) when (this.lifetimeCts.IsCancellationRequested)
        {
            // Cache shutting down - leave entries as Loading; they'll be discarded with the cache.
        }
        catch (Exception ex)
        {
            this.DebugLog?.Invoke(
                "[XivMarket] fetch failed: items=[{Items}] error={Error}",
                new object?[] { string.Join(",", itemIds), ex.Message });

            var fetchedAt = this.now();
            foreach (var id in itemIds)
            {
                if (!this.ShouldOverwriteWithFailure(id, worldId))
                    continue;
                this.entries[(id, worldId)] = new CacheEntry(LookupStatus.Failed, null, fetchedAt, ex.Message);
                this.Updated?.Invoke(id, worldId);
            }
            this.BatchFetched?.Invoke(itemIds.Length, false, ex.Message);
        }
    }

    private bool ShouldOverwriteWithFailure(int itemId, int worldId)
    {
        if (!this.entries.TryGetValue((itemId, worldId), out var existing))
            return true;
        return existing.Status != LookupStatus.Loaded;
    }

    public void Dispose()
    {
        this.lifetimeCts.Cancel();
        this.lifetimeCts.Dispose();
        lock (this.queueLock)
        {
            this.debounceCts?.Cancel();
            this.debounceCts?.Dispose();
            this.debounceCts = null;
        }
    }
}
