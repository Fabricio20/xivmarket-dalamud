using System;

namespace XivMarket.Models;

public record ItemTooltip(
    int Item,
    Scope World,
    Scope Datacenter,
    Scope Region);

public record Scope(
    int? Id,
    string Name,
    ListingGroup Listing,
    LastSale LastSale);

public record ListingGroup(
    ListingPair Unit,
    ListingPair Total);

public record ListingPair(
    ListingLeaf? Nq,
    ListingLeaf? Hq);

public record ListingLeaf(
    long Price,
    int Quantity,
    DateTimeOffset LastUpdated,
    WorldRef World);

public record LastSale(
    SaleLeaf? Nq,
    SaleLeaf? Hq);

public record SaleLeaf(
    long Price,
    int Quantity,
    DateTimeOffset Time,
    WorldRef World);

public record WorldRef(
    int Id,
    string Name);
