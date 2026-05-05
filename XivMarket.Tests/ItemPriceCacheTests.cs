using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMarket.Models;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class ItemPriceCacheTests
{
    private const int World = 33;
    private static readonly TimeSpan ZeroDebounce = TimeSpan.Zero;
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ColdLookup_ReturnsLoading_ThenLoadedAfterFetch()
    {
        var fake = new FakeClient();
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        var initial = cache.GetOrRequest(5057, World);
        Assert.Equal(LookupStatus.Loading, initial.Status);

        await WaitForUpdate(cache, 5057);

        Assert.True(cache.TryGet(5057, World, out var loaded));
        Assert.Equal(LookupStatus.Loaded, loaded!.Status);
        Assert.NotNull(loaded.Data);
        Assert.Equal(5057, loaded.Data!.Item);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task HotLookup_WithinTtl_DoesNotRefetch()
    {
        var fake = new FakeClient();
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        cache.GetOrRequest(5057, World);
        await WaitForUpdate(cache, 5057);

        var hit = cache.GetOrRequest(5057, World);
        Assert.Equal(LookupStatus.Loaded, hit.Status);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task HotLookup_PastTtl_ServesStaleAndQueuesRefresh()
    {
        var fake = new FakeClient();
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        cache.GetOrRequest(5057, World);
        await WaitForUpdate(cache, 5057);
        Assert.Single(fake.Calls);

        // Advance past TTL.
        clock.Advance(FiveMinutes + TimeSpan.FromSeconds(1));

        var stale = cache.GetOrRequest(5057, World);
        // Stale-while-revalidate: caller still gets Loaded data immediately.
        Assert.Equal(LookupStatus.Loaded, stale.Status);

        await WaitForUpdate(cache, 5057);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task Refresh_ForcesFetch_EvenWhenFresh()
    {
        var fake = new FakeClient();
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        cache.GetOrRequest(5057, World);
        await WaitForUpdate(cache, 5057);
        Assert.Single(fake.Calls);

        cache.Refresh(5057, World);
        Assert.True(cache.TryGet(5057, World, out var afterRefresh));
        Assert.Equal(LookupStatus.Loading, afterRefresh!.Status);

        await WaitForUpdate(cache, 5057);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task FailedState_IsSticky_UntilRefresh()
    {
        var fake = new FakeClient { FailUntilCallNumber = 1 };
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        cache.GetOrRequest(5057, World);
        await WaitForUpdate(cache, 5057);
        Assert.True(cache.TryGet(5057, World, out var failed));
        Assert.Equal(LookupStatus.Failed, failed!.Status);
        Assert.Single(fake.Calls);

        // GetOrRequest while Failed must NOT auto-retry.
        var hit = cache.GetOrRequest(5057, World);
        Assert.Equal(LookupStatus.Failed, hit.Status);
        Assert.Single(fake.Calls);

        // Refresh recovers.
        cache.Refresh(5057, World);
        await WaitForUpdate(cache, 5057);
        Assert.True(cache.TryGet(5057, World, out var recovered));
        Assert.Equal(LookupStatus.Loaded, recovered!.Status);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public void NonMarketable_ShortCircuits_NoFetch()
    {
        var fake = new FakeClient();
        using var cache = NewCache(fake, new Clock(), ZeroDebounce, isMarketable: id => id != 12345);

        var entry = cache.GetOrRequest(12345, World);

        Assert.Equal(LookupStatus.NonMarketable, entry.Status);
        Assert.Null(entry.Data);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task BatchCoalescing_MultipleHovers_FireOneRequest()
    {
        var fake = new FakeClient();
        // Use a small but nonzero debounce so distinct GetOrRequest calls land in the same window.
        using var cache = NewCache(fake, new Clock(), TimeSpan.FromMilliseconds(50));

        cache.GetOrRequest(101, World);
        cache.GetOrRequest(102, World);
        cache.GetOrRequest(103, World);

        await WaitForUpdates(cache, expected: 3);

        Assert.Single(fake.Calls);
        Assert.Equal(new[] { 101, 102, 103 }, fake.Calls[0].OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Prefetch_LargeList_ChunksByBatchLimit()
    {
        var fake = new FakeClient();
        // Use a small batchLimit (5) so we can verify chunking without setting up 100+ items.
        using var cache = NewCache(fake, new Clock(), ZeroDebounce, batchLimit: 5);

        // Prefetch fires FetchBatch directly (no debounce), and FakeClient completes
        // synchronously - Updated would fire before WaitForUpdates can subscribe. Subscribe first.
        var items = Enumerable.Range(1000, 12).ToArray();
        var waitTask = WaitForUpdates(cache, expected: 12);
        cache.Prefetch(items, World);
        await waitTask;

        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal(12, fake.Calls.Sum(c => c.Length));
        Assert.True(fake.Calls.All(c => c.Length <= 5));
    }

    [Fact]
    public async Task Prefetch_SkipsAlreadyFreshEntries()
    {
        var fake = new FakeClient();
        var clock = new Clock();
        using var cache = NewCache(fake, clock, ZeroDebounce);

        cache.GetOrRequest(5057, World);
        await WaitForUpdate(cache, 5057);
        Assert.Single(fake.Calls);

        // Prefetching the same id (still fresh) must not refire.
        cache.Prefetch(new[] { 5057 }, World);
        await Task.Delay(50);   // give any spurious fetch time to fire
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task ConcurrentRequests_SameItem_DedupToOneFetch()
    {
        var fake = new FakeClient();
        using var cache = NewCache(fake, new Clock(), TimeSpan.FromMilliseconds(50));

        // Fire many concurrent requests for the same item before the debounce window closes.
        Parallel.For(0, 50, _ => cache.GetOrRequest(5057, World));

        await WaitForUpdate(cache, 5057);

        Assert.Single(fake.Calls);
        Assert.Single(fake.Calls[0]);
    }

    // -------- helpers --------

    private static ItemPriceCache NewCache(
        FakeClient client,
        Clock clock,
        TimeSpan debounce,
        Func<int, bool>? isMarketable = null,
        int batchLimit = 100) =>
        new(
            client,
            isMarketable ?? (_ => true),
            ttlProvider: () => FiveMinutes,
            now: () => clock.Now,
            debounce: debounce,
            batchLimit: batchLimit);

    private static Task WaitForUpdate(ItemPriceCache cache, int itemId, int timeoutMs = 2000) =>
        WaitForUpdates(cache, expected: 1, predicate: id => id == itemId, timeoutMs);

    private static async Task WaitForUpdates(
        ItemPriceCache cache, int expected, Func<int, bool>? predicate = null, int timeoutMs = 2000)
    {
        var seen = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> handler = (id, _) =>
        {
            if (predicate != null && !predicate(id)) return;
            if (Interlocked.Increment(ref seen) >= expected)
                tcs.TrySetResult(true);
        };
        cache.Updated += handler;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => tcs.TrySetException(new TimeoutException(
                $"Expected {expected} update(s) within {timeoutMs}ms, only saw {seen}.")));
            await tcs.Task;
        }
        finally
        {
            cache.Updated -= handler;
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan delta) => this.Now += delta;
    }

    private sealed class FakeClient : IXivMarketClient
    {
        public List<int[]> Calls { get; } = new();
        private readonly object lk = new();

        /// <summary>If set, the first N calls fail before succeeding. Resets after threshold.</summary>
        public int? FailUntilCallNumber { get; set; }

        public Task<IReadOnlyDictionary<int, ItemTooltip>> GetTooltipAsync(
            int worldId, IReadOnlyCollection<int> itemIds, CancellationToken ct = default)
        {
            int callNumber;
            int[] ids = itemIds.ToArray();
            lock (this.lk)
            {
                this.Calls.Add(ids);
                callNumber = this.Calls.Count;
            }

            if (this.FailUntilCallNumber.HasValue && callNumber <= this.FailUntilCallNumber.Value)
            {
                return Task.FromException<IReadOnlyDictionary<int, ItemTooltip>>(
                    new InvalidOperationException("simulated fetch failure"));
            }

            var dict = ids.ToDictionary(id => id, StubItemTooltip);
            return Task.FromResult<IReadOnlyDictionary<int, ItemTooltip>>(dict);
        }

        public Task<IReadOnlyList<StaticWorldView>> GetWorldsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StaticWorldView>>(System.Array.Empty<StaticWorldView>());

        private static ItemTooltip StubItemTooltip(int id) => new(
            id,
            EmptyScope(33, "Twintania"),
            EmptyScope(null, "Light"),
            EmptyScope(null, "EU"));

        private static Scope EmptyScope(int? worldId, string name) => new(
            worldId, name,
            new ListingGroup(new ListingPair(null, null), new ListingPair(null, null)),
            new LastSale(null, null));
    }
}
