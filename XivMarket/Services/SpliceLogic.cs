using System;
using XivMarket.Models;

namespace XivMarket.Services;

public static class SpliceLogic
{
    public sealed record SpliceResult(ItemTooltip Tooltip, SpliceDecision WorldDecision, SpliceDecision DcDecision, SpliceDecision RegionDecision);

    public enum SpliceDecision
    {
        Replaced,
        Skipped,
        NoExistingScope,
    }

    public static SpliceResult ApplySplice(ItemTooltip? current, SpliceUpdate update)
    {
        var leaf = new ListingLeaf(
            update.Price, update.Quantity, update.Timestamp,
            new WorldRef(update.WorldId, update.WorldName));

        if (current is null)
        {
            var partial = BuildPartial(update, leaf);
            return new SpliceResult(partial, SpliceDecision.Replaced, SpliceDecision.NoExistingScope, SpliceDecision.NoExistingScope);
        }

        var (worldScope, worldDec) = SpliceScope(current.World, leaf, update.IsHq, update.WorldId, alwaysReplace: true);
        var (dcScope, dcDec) = SpliceScope(current.Datacenter, leaf, update.IsHq, update.WorldId, alwaysReplace: false);
        var (regionScope, regionDec) = SpliceScope(current.Region, leaf, update.IsHq, update.WorldId, alwaysReplace: false);

        var tooltip = current with { World = worldScope, Datacenter = dcScope, Region = regionScope };
        return new SpliceResult(tooltip, worldDec, dcDec, regionDec);
    }

    private static (Scope, SpliceDecision) SpliceScope(Scope scope, ListingLeaf incoming, bool isHq, int spliceWorldId, bool alwaysReplace)
    {
        var existingLeaf = isHq ? scope.Listing.Unit.Hq : scope.Listing.Unit.Nq;

        if (!alwaysReplace && !ShouldReplaceLeaf(existingLeaf, incoming, spliceWorldId))
            return (scope, SpliceDecision.Skipped);

        var decision = existingLeaf is null ? SpliceDecision.NoExistingScope : SpliceDecision.Replaced;
        var newScope = ReplaceLeaf(scope, incoming, isHq);
        return (newScope, decision);
    }

    private static bool ShouldReplaceLeaf(ListingLeaf? existing, ListingLeaf incoming, int spliceWorldId)
        => existing is null
        || existing.World.Id == spliceWorldId
        || incoming.Price <= existing.Price;

    private static Scope ReplaceLeaf(Scope scope, ListingLeaf leaf, bool isHq)
    {
        var unit = isHq
            ? scope.Listing.Unit with { Hq = leaf }
            : scope.Listing.Unit with { Nq = leaf };
        var total = isHq
            ? scope.Listing.Total with { Hq = leaf }
            : scope.Listing.Total with { Nq = leaf };
        return scope with { Listing = new ListingGroup(unit, total) };
    }

    private static ItemTooltip BuildPartial(SpliceUpdate update, ListingLeaf leaf)
    {
        var emptyListingGroup = new ListingGroup(new ListingPair(null, null), new ListingPair(null, null));
        var emptySale = new LastSale(null, null);

        var worldPair = update.IsHq
            ? new ListingPair(null, leaf)
            : new ListingPair(leaf, null);
        var worldListings = new ListingGroup(worldPair, worldPair);
        var worldScope = new Scope(update.WorldId, update.WorldName, worldListings, emptySale);

        var dcScope = new Scope(null, "", emptyListingGroup, emptySale);
        var regionScope = new Scope(null, "", emptyListingGroup, emptySale);

        return new ItemTooltip(update.ItemId, worldScope, dcScope, regionScope);
    }
}
