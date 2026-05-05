using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XivMarket.Models;

namespace XivMarket.Services;

public interface IXivMarketClient
{
    /// <summary>
    /// Fetches tooltip data for one or more items. Capped at 100 items per request - caller is
    /// responsible for chunking larger sets.
    /// </summary>
    Task<IReadOnlyDictionary<int, ItemTooltip>> GetTooltipAsync(
        int worldId, IReadOnlyCollection<int> itemIds, CancellationToken ct = default);

    Task<IReadOnlyList<StaticWorldView>> GetWorldsAsync(CancellationToken ct = default);
}

public sealed class XivMarketClient : IXivMarketClient, IDisposable
{
    /// <summary>Maximum number of items the /tooltip endpoint accepts in a single request.</summary>
    public const int TooltipBatchLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http;
    private readonly Func<string> baseUrlProvider;

    public XivMarketClient(Func<string> baseUrlProvider)
        : this(baseUrlProvider, CreateDefaultHttpClient())
    {
    }

    public XivMarketClient(Func<string> baseUrlProvider, HttpClient http)
    {
        this.baseUrlProvider = baseUrlProvider;
        this.http = http;
    }

    public string BaseUrl => this.baseUrlProvider().TrimEnd('/');

    /// <summary>
    /// Fetches tooltip data (cheapest current listing + last sale, per scope) for one or more items.
    /// The response is keyed by item id. Items the server has no marketboard data for are still
    /// present in the dictionary, with their leaf fields (Listing.Unit/Total.Nq/Hq, LastSale.Nq/Hq)
    /// set to null; this is distinct from the item being absent from the dictionary.
    /// </summary>
    /// <param name="worldId">Home world id. Determines which datacenter/region the response covers.</param>
    /// <param name="itemIds">
    /// Item ids to look up. Capped at <see cref="TooltipBatchLimit"/> (100) per request - the server
    /// rejects larger batches with HTTP 400. To query larger sets (e.g. an entire inventory), chunk
    /// the input into batches of 100 and merge the resulting dictionaries.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="itemIds"/> is empty or exceeds 100 entries.</exception>
    public async Task<IReadOnlyDictionary<int, ItemTooltip>> GetTooltipAsync(
        int worldId, IReadOnlyCollection<int> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0)
            throw new ArgumentException("itemIds must contain at least one item.", nameof(itemIds));
        if (itemIds.Count > TooltipBatchLimit)
            throw new ArgumentException(
                $"/tooltip accepts at most {TooltipBatchLimit} items per request; got {itemIds.Count}.",
                nameof(itemIds));

        var url = $"{this.BaseUrl}/tooltip?world={worldId}&items={string.Join(",", itemIds)}";
        var json = await this.http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseTooltipResponse(json);
    }

    /// <summary>Fetches the full /static/worlds.json payload - used to build the world→DC lookup.</summary>
    public async Task<IReadOnlyList<StaticWorldView>> GetWorldsAsync(CancellationToken ct = default)
    {
        var url = $"{this.BaseUrl}/static/worlds.json";
        var json = await this.http.GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<StaticWorldView>>(json, JsonOptions)
               ?? new List<StaticWorldView>();
    }

    /// <summary>
    /// Parses a /tooltip response body into a dictionary keyed by item id.
    /// Exposed for testing - the response shape is stable enough that round-tripping
    /// captured JSON through this method is the cheapest way to catch model drift.
    /// </summary>
    public static IReadOnlyDictionary<int, ItemTooltip> ParseTooltipResponse(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, ItemTooltip>>(json, JsonOptions);
        if (raw is null)
            return new Dictionary<int, ItemTooltip>();
        return raw.ToDictionary(
            kv => int.Parse(kv.Key, CultureInfo.InvariantCulture),
            kv => kv.Value);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XivMarket-Dalamud/0.0.1");
        return http;
    }

    public void Dispose() => this.http.Dispose();
}
