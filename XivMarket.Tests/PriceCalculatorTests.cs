using System;
using XivMarket.Models;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class PriceCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2025, 5, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorldRef World = new(33, "Twintania");

    [Fact]
    public void NullData_ReturnsNull()
    {
        var result = PriceCalculator.GetRecommendedPrice(
            null, false, false, 0, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Null(result);
    }

    [Fact]
    public void NoListings_ReturnsNull()
    {
        var tooltip = MakeTooltip(null, null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Null(result);
    }

    [Fact]
    public void BasicPrice_NoUndercutNoRounding()
    {
        var tooltip = MakeTooltip(nqPrice: 1000, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Equal(1000, result);
    }

    [Fact]
    public void Undercut_SubtractsFromPrice()
    {
        var tooltip = MakeTooltip(nqPrice: 1000, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 5, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Equal(995, result);
    }

    [Fact]
    public void RoundDown_FloorToNearest10()
    {
        var tooltip = MakeTooltip(nqPrice: 1234, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 10, false, PriceScope.World, QualityMode.Any);
        Assert.Equal(1230, result);
    }

    [Fact]
    public void RoundUp_CeilingToNearest10()
    {
        var tooltip = MakeTooltip(nqPrice: 1234, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 10, true, PriceScope.World, QualityMode.Any);
        Assert.Equal(1240, result);
    }

    [Fact]
    public void RoundUp_ExactMultiple_StaysSame()
    {
        var tooltip = MakeTooltip(nqPrice: 1230, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 10, true, PriceScope.World, QualityMode.Any);
        Assert.Equal(1230, result);
    }

    [Fact]
    public void UndercutThenRound_OrderMatters()
    {
        var tooltip = MakeTooltip(nqPrice: 1005, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 5, 10, true, PriceScope.World, QualityMode.Any);
        Assert.Equal(1000, result);
    }

    [Fact]
    public void MinimumPrice_NeverBelowOne()
    {
        var tooltip = MakeTooltip(nqPrice: 3, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 100, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Equal(1, result);
    }

    [Fact]
    public void QualityMode_MatchingQuality_Hq()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: 2000);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, true, true, 0, 1, false, PriceScope.World, QualityMode.MatchingQuality);
        Assert.Equal(2000, result);
    }

    [Fact]
    public void QualityMode_MatchingQuality_Nq()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: 2000);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, true, 0, 1, false, PriceScope.World, QualityMode.MatchingQuality);
        Assert.Equal(500, result);
    }

    [Fact]
    public void QualityMode_Any_PicksCheapest()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: 2000);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, true, true, 0, 1, false, PriceScope.World, QualityMode.Any);
        Assert.Equal(500, result);
    }

    [Fact]
    public void QualityMode_HqOnly_ForcesHq()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: 2000);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, true, 0, 1, false, PriceScope.World, QualityMode.HqOnly);
        Assert.Equal(2000, result);
    }

    [Fact]
    public void QualityMode_HqOnly_NotCanBeHq_FallsBackToNq()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: 2000);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 1, false, PriceScope.World, QualityMode.HqOnly);
        Assert.Equal(500, result);
    }

    [Fact]
    public void Scope_Datacenter()
    {
        var tooltip = MakeTooltipMultiScope(worldNq: 1000, dcNq: 800, regionNq: 600);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 1, false, PriceScope.Datacenter, QualityMode.Any);
        Assert.Equal(800, result);
    }

    [Fact]
    public void Scope_Region()
    {
        var tooltip = MakeTooltipMultiScope(worldNq: 1000, dcNq: 800, regionNq: 600);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false, 0, 1, false, PriceScope.Region, QualityMode.Any);
        Assert.Equal(600, result);
    }

    [Fact]
    public void DefaultSettings_RoundUpToNearest10()
    {
        var tooltip = MakeTooltip(nqPrice: 2853, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false,
            undercutAmount: 0, roundTo: 10, roundUp: true,
            PriceScope.World, QualityMode.MatchingQuality);
        Assert.Equal(2860, result);
    }

    [Fact]
    public void VendorFloor_PreventsGoingBelowNpcPricePlusTax()
    {
        var tooltip = MakeTooltip(nqPrice: 50, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false,
            undercutAmount: 10, roundTo: 1, roundUp: false,
            PriceScope.World, QualityMode.Any,
            vendorPrice: 100);
        // ceil(100 / 0.95) = 106 -- listing at 106 nets 100.7 after 5% tax
        Assert.Equal(106, result);
    }

    [Fact]
    public void VendorFloor_DoesNotAffectWhenPriceIsHigher()
    {
        var tooltip = MakeTooltip(nqPrice: 500, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false,
            undercutAmount: 0, roundTo: 1, roundUp: false,
            PriceScope.World, QualityMode.Any,
            vendorPrice: 100);
        Assert.Equal(500, result);
    }

    [Fact]
    public void VendorFloor_ZeroVendorPrice_NoEffect()
    {
        var tooltip = MakeTooltip(nqPrice: 5, hqPrice: null);
        var result = PriceCalculator.GetRecommendedPrice(
            tooltip, false, false,
            undercutAmount: 10, roundTo: 1, roundUp: false,
            PriceScope.World, QualityMode.Any,
            vendorPrice: 0);
        Assert.Equal(1, result);
    }

    // -------- helpers --------

    private static ItemTooltip MakeTooltip(long? nqPrice, long? hqPrice)
    {
        var nq = nqPrice.HasValue ? new ListingLeaf(nqPrice.Value, 1, Now, World) : null;
        var hq = hqPrice.HasValue ? new ListingLeaf(hqPrice.Value, 1, Now, World) : null;
        var listings = new ListingGroup(new ListingPair(nq, hq), new ListingPair(nq, hq));
        var scope = new Scope(33, "Twintania", listings, new LastSale(null, null));
        var empty = new Scope(null, "", new ListingGroup(new ListingPair(null, null), new ListingPair(null, null)), new LastSale(null, null));
        return new ItemTooltip(5057, scope, empty, empty);
    }

    private static ItemTooltip MakeTooltipMultiScope(long worldNq, long dcNq, long regionNq)
    {
        var worldLeaf = new ListingLeaf(worldNq, 1, Now, World);
        var dcLeaf = new ListingLeaf(dcNq, 1, Now, new WorldRef(34, "Shiva"));
        var regionLeaf = new ListingLeaf(regionNq, 1, Now, new WorldRef(35, "Moogle"));

        var worldScope = new Scope(33, "Twintania",
            new ListingGroup(new ListingPair(worldLeaf, null), new ListingPair(worldLeaf, null)),
            new LastSale(null, null));
        var dcScope = new Scope(null, "Light",
            new ListingGroup(new ListingPair(dcLeaf, null), new ListingPair(dcLeaf, null)),
            new LastSale(null, null));
        var regionScope = new Scope(null, "EU",
            new ListingGroup(new ListingPair(regionLeaf, null), new ListingPair(regionLeaf, null)),
            new LastSale(null, null));

        return new ItemTooltip(5057, worldScope, dcScope, regionScope);
    }
}
