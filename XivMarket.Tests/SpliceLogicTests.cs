using System;
using XivMarket.Models;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class SpliceLogicTests
{
    private static readonly DateTimeOffset Now = new(2025, 5, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorldRef HomeWorld = new(33, "Twintania");
    private static readonly WorldRef OtherWorld = new(34, "Shiva");

    [Fact]
    public void NullCurrent_BuildsPartialTooltip_WorldScopePopulated()
    {
        var update = MakeUpdate(isHq: false, price: 1000, quantity: 5);
        var result = SpliceLogic.ApplySplice(null, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Replaced, result.WorldDecision);
        Assert.Equal(SpliceLogic.SpliceDecision.NoExistingScope, result.DcDecision);
        Assert.Equal(SpliceLogic.SpliceDecision.NoExistingScope, result.RegionDecision);

        Assert.NotNull(result.Tooltip.World.Listing.Unit.Nq);
        Assert.Equal(1000, result.Tooltip.World.Listing.Unit.Nq!.Price);
        Assert.Equal(5, result.Tooltip.World.Listing.Unit.Nq.Quantity);
        Assert.Null(result.Tooltip.World.Listing.Unit.Hq);
        Assert.Null(result.Tooltip.Datacenter.Listing.Unit.Nq);
    }

    [Fact]
    public void NullCurrent_HqUpdate_PopulatesHqLeafOnly()
    {
        var update = MakeUpdate(isHq: true, price: 2000, quantity: 1);
        var result = SpliceLogic.ApplySplice(null, update);

        Assert.Null(result.Tooltip.World.Listing.Unit.Nq);
        Assert.NotNull(result.Tooltip.World.Listing.Unit.Hq);
        Assert.Equal(2000, result.Tooltip.World.Listing.Unit.Hq!.Price);
    }

    [Fact]
    public void NqUpdate_ReplacesWorldNqLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, dcNqPrice: 4000, regionNqPrice: 3000);
        var update = MakeUpdate(isHq: false, price: 4500, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(4500, result.Tooltip.World.Listing.Unit.Nq!.Price);
        Assert.Equal(HomeWorld.Id, result.Tooltip.World.Listing.Unit.Nq.World.Id);
    }

    [Fact]
    public void HqUpdate_ReplacesWorldHqLeaf()
    {
        var tooltip = MakeTooltip(worldHqPrice: 8000);
        var update = MakeUpdate(isHq: true, price: 7000, quantity: 2);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(7000, result.Tooltip.World.Listing.Unit.Hq!.Price);
        Assert.Equal(2, result.Tooltip.World.Listing.Unit.Hq.Quantity);
    }

    [Fact]
    public void CheaperThanDc_ReplacesDcLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, dcNqPrice: 4000, dcNqWorld: OtherWorld);
        var update = MakeUpdate(isHq: false, price: 3500, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Replaced, result.DcDecision);
        Assert.Equal(3500, result.Tooltip.Datacenter.Listing.Unit.Nq!.Price);
        Assert.Equal(HomeWorld.Id, result.Tooltip.Datacenter.Listing.Unit.Nq.World.Id);
    }

    [Fact]
    public void MoreExpensive_DifferentWorld_DoesNotReplaceDcLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, dcNqPrice: 4000, dcNqWorld: OtherWorld);
        var update = MakeUpdate(isHq: false, price: 4500, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Skipped, result.DcDecision);
        Assert.Equal(4000, result.Tooltip.Datacenter.Listing.Unit.Nq!.Price);
        Assert.Equal(OtherWorld.Id, result.Tooltip.Datacenter.Listing.Unit.Nq.World.Id);
    }

    [Fact]
    public void SameWorldAsDc_AlwaysReplacesDcLeaf_EvenIfMoreExpensive()
    {
        var tooltip = MakeTooltip(worldNqPrice: 3000, dcNqPrice: 3000, dcNqWorld: HomeWorld);
        var update = MakeUpdate(isHq: false, price: 3500, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Replaced, result.DcDecision);
        Assert.Equal(3500, result.Tooltip.Datacenter.Listing.Unit.Nq!.Price);
    }

    [Fact]
    public void NullDcLeaf_AlwaysReplaces()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000);
        var update = MakeUpdate(isHq: false, price: 4000, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.NoExistingScope, result.DcDecision);
        Assert.Equal(4000, result.Tooltip.Datacenter.Listing.Unit.Nq!.Price);
    }

    [Fact]
    public void CheaperThanRegion_ReplacesRegionLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, regionNqPrice: 4000, regionNqWorld: OtherWorld);
        var update = MakeUpdate(isHq: false, price: 3000, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Replaced, result.RegionDecision);
        Assert.Equal(3000, result.Tooltip.Region.Listing.Unit.Nq!.Price);
    }

    [Fact]
    public void MoreExpensive_DifferentWorld_DoesNotReplaceRegionLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, regionNqPrice: 2000, regionNqWorld: OtherWorld);
        var update = MakeUpdate(isHq: false, price: 3000, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(SpliceLogic.SpliceDecision.Skipped, result.RegionDecision);
        Assert.Equal(2000, result.Tooltip.Region.Listing.Unit.Nq!.Price);
    }

    [Fact]
    public void UpdatesUnitAndTotalPairs()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000);
        var update = MakeUpdate(isHq: false, price: 4000, quantity: 3);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(4000, result.Tooltip.World.Listing.Unit.Nq!.Price);
        Assert.Equal(4000, result.Tooltip.World.Listing.Total.Nq!.Price);
        Assert.Equal(3, result.Tooltip.World.Listing.Total.Nq.Quantity);
    }

    [Fact]
    public void PreservesOtherQualityLeaf()
    {
        var tooltip = MakeTooltip(worldNqPrice: 5000, worldHqPrice: 8000);
        var update = MakeUpdate(isHq: false, price: 4000, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(4000, result.Tooltip.World.Listing.Unit.Nq!.Price);
        Assert.Equal(8000, result.Tooltip.World.Listing.Unit.Hq!.Price);
    }

    [Fact]
    public void PreservesLastSaleData()
    {
        var sale = new LastSale(
            new SaleLeaf(3000, 1, Now.AddHours(-2), HomeWorld),
            null);
        var tooltip = MakeTooltipWithSale(worldNqPrice: 5000, worldSale: sale);
        var update = MakeUpdate(isHq: false, price: 4000, quantity: 1);

        var result = SpliceLogic.ApplySplice(tooltip, update);

        Assert.Equal(3000, result.Tooltip.World.LastSale.Nq!.Price);
    }

    // -------- helpers --------

    private static SpliceUpdate MakeUpdate(bool isHq, long price, int quantity) =>
        new(ItemId: 5057, WorldId: HomeWorld.Id, WorldName: HomeWorld.Name,
            IsHq: isHq, Price: price, Quantity: quantity, Timestamp: Now);

    private static ItemTooltip MakeTooltip(
        long? worldNqPrice = null, long? worldHqPrice = null,
        long? dcNqPrice = null, long? dcHqPrice = null,
        long? regionNqPrice = null, long? regionHqPrice = null,
        WorldRef? dcNqWorld = null, WorldRef? regionNqWorld = null)
    {
        return new ItemTooltip(
            5057,
            MakeScope(33, "Twintania", worldNqPrice, worldHqPrice, HomeWorld, HomeWorld),
            MakeScope(null, "Light", dcNqPrice, dcHqPrice, dcNqWorld ?? HomeWorld, HomeWorld),
            MakeScope(null, "EU", regionNqPrice, regionHqPrice, regionNqWorld ?? HomeWorld, HomeWorld));
    }

    private static ItemTooltip MakeTooltipWithSale(long worldNqPrice, LastSale worldSale)
    {
        var emptyListings = new ListingGroup(new ListingPair(null, null), new ListingPair(null, null));
        var emptySale = new LastSale(null, null);
        var worldLeaf = new ListingLeaf(worldNqPrice, 1, Now.AddMinutes(-5), HomeWorld);
        var worldListings = new ListingGroup(
            new ListingPair(worldLeaf, null),
            new ListingPair(worldLeaf, null));
        return new ItemTooltip(
            5057,
            new Scope(33, "Twintania", worldListings, worldSale),
            new Scope(null, "Light", emptyListings, emptySale),
            new Scope(null, "EU", emptyListings, emptySale));
    }

    private static Scope MakeScope(int? id, string name, long? nqPrice, long? hqPrice, WorldRef nqWorld, WorldRef hqWorld)
    {
        var nq = nqPrice.HasValue ? new ListingLeaf(nqPrice.Value, 1, Now.AddMinutes(-5), nqWorld) : null;
        var hq = hqPrice.HasValue ? new ListingLeaf(hqPrice.Value, 1, Now.AddMinutes(-5), hqWorld) : null;
        var listings = new ListingGroup(new ListingPair(nq, hq), new ListingPair(nq, hq));
        return new Scope(id, name, listings, new LastSale(null, null));
    }
}
