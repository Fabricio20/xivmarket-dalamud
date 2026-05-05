using System;
using System.IO;
using System.Linq;
using XivMarket.Models;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class TooltipDeserializationTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tooltip_sample.json"));

    [Fact]
    public void Parse_BatchResponse_ReturnsEntryPerItem()
    {
        var result = XivMarketClient.ParseTooltipResponse(LoadFixture());

        Assert.Equal(2, result.Count);
        Assert.Contains(5057, result.Keys);
        Assert.Contains(99999999, result.Keys);
    }

    [Fact]
    public void Parse_KnownItem_PopulatesAllScopes()
    {
        var item = XivMarketClient.ParseTooltipResponse(LoadFixture())[5057];

        Assert.Equal(5057, item.Item);

        // World scope: id is populated, name matches the world we queried
        Assert.Equal(33, item.World.Id);
        Assert.Equal("Twintania", item.World.Name);

        // DC and region scopes: name only, no id
        Assert.Null(item.Datacenter.Id);
        Assert.Equal("Light", item.Datacenter.Name);
        Assert.Null(item.Region.Id);
        Assert.Equal("EU", item.Region.Name);
    }

    [Fact]
    public void Parse_KnownItem_ListingUnitVsTotalAreDistinct()
    {
        var world = XivMarketClient.ParseTooltipResponse(LoadFixture())[5057].World;

        // unit = cheapest per-unit price; total = cheapest total stack outlay.
        // For the captured fixture the NQ unit cheapest is 299 (stack of 99) and
        // the NQ total cheapest is 300 (stack of 73). The two should not be the same listing.
        Assert.NotNull(world.Listing.Unit.Nq);
        Assert.NotNull(world.Listing.Total.Nq);
        Assert.Equal(299, world.Listing.Unit.Nq!.Price);
        Assert.Equal(99, world.Listing.Unit.Nq.Quantity);
        Assert.Equal(300, world.Listing.Total.Nq!.Price);
        Assert.Equal(73, world.Listing.Total.Nq.Quantity);
    }

    [Fact]
    public void Parse_KnownItem_LeafCarriesSourceWorldAtCrossWorldScopes()
    {
        var item = XivMarketClient.ParseTooltipResponse(LoadFixture())[5057];

        // At DC/region scope the leaf must tell us which world the cheapest listing is on.
        Assert.NotNull(item.Datacenter.Listing.Unit.Nq);
        Assert.Equal(66, item.Datacenter.Listing.Unit.Nq!.World.Id);
        Assert.Equal("Odin", item.Datacenter.Listing.Unit.Nq.World.Name);

        Assert.NotNull(item.Region.Listing.Unit.Nq);
        Assert.Equal(66, item.Region.Listing.Unit.Nq!.World.Id);
    }

    [Fact]
    public void Parse_KnownItem_LastSaleHasTimeField()
    {
        var sale = XivMarketClient.ParseTooltipResponse(LoadFixture())[5057].World.LastSale.Nq;

        Assert.NotNull(sale);
        Assert.Equal(300, sale!.Price);
        // Sales use the "time" field (vs "lastUpdated" on listings); confirm it deserialized.
        Assert.True(sale.Time > System.DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Parse_UnknownItem_KeepsScopeShellsButLeavesAreNull()
    {
        var item = XivMarketClient.ParseTooltipResponse(LoadFixture())[99999999];

        // Server returns the item with null leaves rather than 404 - the plugin
        // relies on this to distinguish "no data" from "request failed".
        Assert.Equal(99999999, item.Item);
        Assert.NotNull(item.World);
        Assert.NotNull(item.World.Listing);
        Assert.NotNull(item.World.LastSale);

        Assert.Null(item.World.Listing.Unit.Nq);
        Assert.Null(item.World.Listing.Unit.Hq);
        Assert.Null(item.World.Listing.Total.Nq);
        Assert.Null(item.World.LastSale.Nq);
        Assert.Null(item.World.LastSale.Hq);

        Assert.Null(item.Datacenter.Listing.Unit.Nq);
        Assert.Null(item.Region.LastSale.Hq);
    }

    [Fact]
    public void Parse_EmptyJsonObject_ReturnsEmptyDictionary()
    {
        var result = XivMarketClient.ParseTooltipResponse("{}");
        Assert.Empty(result);
    }
}
