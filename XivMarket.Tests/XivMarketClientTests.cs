using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class XivMarketClientTests
{
    [Fact]
    public async Task GetTooltipAsync_EmptyItemIds_Throws()
    {
        using var client = new XivMarketClient(() => "http://test.local");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetTooltipAsync(33, Array.Empty<int>()));
    }

    [Fact]
    public async Task GetTooltipAsync_OverLimit_Throws()
    {
        using var client = new XivMarketClient(() => "http://test.local");
        var ids = Enumerable.Range(1, XivMarketClient.TooltipBatchLimit + 1).ToArray();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetTooltipAsync(33, ids));
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public async Task GetTooltipAsync_AtLimit_DoesNotThrowOnArgumentCheck()
    {
        // Use a stub handler so we don't actually hit the network - we only want to
        // confirm the argument-count check accepts exactly TooltipBatchLimit ids.
        var handler = new StubHandler("{}");
        using var http = new HttpClient(handler);
        using var client = new XivMarketClient(() => "http://test.local", http);

        var ids = Enumerable.Range(1, XivMarketClient.TooltipBatchLimit).ToArray();
        var result = await client.GetTooltipAsync(33, ids);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTooltipAsync_BuildsUrlWithCommaSeparatedIds()
    {
        var handler = new StubHandler("{}");
        using var http = new HttpClient(handler);
        using var client = new XivMarketClient(() => "http://test.local/", http);

        await client.GetTooltipAsync(33, new[] { 5057, 4, 12 });

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal("http://test.local/tooltip?world=33&items=5057,4,12", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public void BaseUrl_TrimsTrailingSlash()
    {
        using var client = new XivMarketClient(() => "http://test.local/");
        Assert.Equal("http://test.local", client.BaseUrl);
    }

    [Fact]
    public void BaseUrl_ReflectsProviderChanges()
    {
        var url = "http://first.local";
        using var client = new XivMarketClient(() => url);
        Assert.Equal("http://first.local", client.BaseUrl);

        url = "http://second.local";
        Assert.Equal("http://second.local", client.BaseUrl);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string body;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(string body) => this.body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(this.body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
