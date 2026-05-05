using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using XivMarket.Models;
using XivMarket.Services;
using Xunit;

namespace XivMarket.Tests;

public class TooltipRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 18, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, WorldInfo> Worlds = new()
    {
        [33]  = new WorldInfo(33, "Twintania", "Light"),
        [66]  = new WorldInfo(66, "Odin", "Light"),
        [403] = new WorldInfo(403, "Raiden", "Light"),
        [80]  = new WorldInfo(80, "Cerberus", "Chaos"),
        [71]  = new WorldInfo(71, "Moogle", "Chaos"),
    };

    // -------- status branches --------

    [Fact]
    public void NonMarketable_RendersEmpty()
    {
        var entry = new CacheEntry(LookupStatus.NonMarketable, null, Now, null);
        var doc = TooltipRenderer.Render(entry, Ctx());
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void Loading_RendersLoadingMessage()
    {
        var entry = new CacheEntry(LookupStatus.Loading, null, Now, null);
        var doc = TooltipRenderer.Render(entry, Ctx());
        var plain = Plain(doc);
        Assert.Contains("Loading marketboard", plain);
    }

    [Fact]
    public void Failed_RendersErrorAndAltHint()
    {
        var entry = new CacheEntry(LookupStatus.Failed, null, Now, "connection refused");
        var doc = TooltipRenderer.Render(entry, Ctx());
        var plain = Plain(doc);
        Assert.Contains("Failed to fetch marketboard info: connection refused", plain);
        Assert.Contains("ALT to retry", plain);
    }

    // -------- de-duplication rules --------

    [Fact]
    public void AllScopesDistinct_RowsSortedByCheapestFirst()
    {
        // Home: Twintania 120; DC: Odin 95; Region: Cerberus 85. Sorted: Region, DC, Home.
        var data = MakeTooltip(
            home:   Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            dcCheapest: Listing(95, 99, ("Odin", 66), age: TimeSpan.FromHours(2)),
            regionCheapest: Listing(85, 1, ("Cerberus", 80), age: TimeSpan.FromHours(3)),
            homeCheapest: Listing(120, 73, ("Twintania", 33), age: TimeSpan.FromMinutes(1)));

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        var lines = SectionLines(doc, "Marketboard Price:");

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("  [XW] Region (", lines[0]);     // 85g - cheapest
        Assert.StartsWith("  [XW] DC (",     lines[1]);     // 95g
        Assert.StartsWith("  [H] Home (",        lines[2]);     // 120g - most expensive, no XW prefix
    }

    [Fact]
    public void HomeIsCheapestEverywhere_ShowsOnlyHomeRow()
    {
        // All three scopes pick the same listing on the home world.
        var homeListing = Listing(100, 1, ("Twintania", 33), TimeSpan.Zero);
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            dcCheapest: homeListing,
            regionCheapest: homeListing,
            homeCheapest: homeListing);

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        var lines = SectionLines(doc, "Marketboard Price:");
        Assert.Single(lines);
        Assert.StartsWith("  [H] Home (", lines[0]);
    }

    [Fact]
    public void RegionPickInHomeDc_SkipsRegionRow()
    {
        // DC cheapest is on Odin (Light); region cheapest also lands in Light → region row would dup DC row.
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            dcCheapest: Listing(95, 99, ("Odin", 66), TimeSpan.FromHours(2)),
            regionCheapest: Listing(95, 99, ("Odin", 66), TimeSpan.FromHours(2)),
            homeCheapest: Listing(120, 1, ("Twintania", 33), TimeSpan.FromMinutes(5)));

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        var lines = SectionLines(doc, "Marketboard Price:");
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("  [XW] DC (", lines[0]);   // 95g - cheapest
        Assert.StartsWith("  [H] Home (",    lines[1]);   // 120g
    }

    [Fact]
    public void NoListingsAndNoSales_RendersFallbackMessage()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"));   // all leaves null

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        Assert.Contains("No marketboard info known.", Plain(doc));
    }

    // -------- modifier keys --------

    [Fact]
    public void Default_NqHovered_ShowsNqPrice()
    {
        var data = MakeTooltipWithBothQualities();
        var doc = TooltipRenderer.Render(Loaded(data), Ctx(isHq: false));
        var plain = Plain(doc);
        Assert.Contains("100", plain);    // NQ price
        Assert.DoesNotContain("250", plain);   // HQ price
    }

    [Fact]
    public void Default_HqHovered_ShowsHqPrice()
    {
        var data = MakeTooltipWithBothQualities();
        var doc = TooltipRenderer.Render(Loaded(data), Ctx(isHq: true));
        var plain = Plain(doc);
        Assert.Contains("250", plain);
        Assert.DoesNotContain("100", plain);
    }

    [Fact]
    public void Quality_IsDeterminedByEncodingOnly_NotByAnyPluginSideKeyState()
    {
        // The renderer trusts ctx.IsHq solely. The game itself flips the hovered-id encoding when
        // the user holds CTRL on an HQ-able item, so isHq=true means "user wants HQ shown",
        // regardless of who flipped it. RenderContext doesn't even carry a CTRL flag.
        var data = MakeTooltipWithBothQualities();

        var asNq = TooltipRenderer.Render(Loaded(data), Ctx(isHq: false));
        Assert.Contains("100", Plain(asNq));
        Assert.DoesNotContain("250", Plain(asNq));

        var asHq = TooltipRenderer.Render(Loaded(data), Ctx(isHq: true));
        Assert.Contains("250", Plain(asHq));
        Assert.DoesNotContain("100", Plain(asHq));
    }

    // -------- settings --------

    [Fact]
    public void UseCheapestTotalStack_PicksTotalListing()
    {
        // Unit cheapest = 95g x 99 (total 9405g); Total cheapest = 120g x 1 (total 120g).
        var unit  = Listing(95,  99, ("Odin", 66), TimeSpan.FromHours(1));
        var total = Listing(120, 1,  ("Raiden", 403), TimeSpan.FromHours(2));
        var dcScope = ScopeWith(id: null, name: "Light", listingUnitNq: unit, listingTotalNq: total);

        var data = new ItemTooltip(
            5057,
            EmptyScope(33, "Twintania"),
            dcScope,
            EmptyScope(null, "EU"));

        var unitDoc  = TooltipRenderer.Render(Loaded(data), Ctx(useTotal: false));
        var totalDoc = TooltipRenderer.Render(Loaded(data), Ctx(useTotal: true));

        Assert.Contains("95",  Plain(unitDoc));
        Assert.Contains("Odin", Plain(unitDoc));
        Assert.Contains("120",  Plain(totalDoc));
        Assert.Contains("Raiden", Plain(totalDoc));
    }

    // -------- formatting --------

    [Fact]
    public void Quantity_GreaterThanOne_IncludesStackSuffix()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            homeCheapest: Listing(100, 99, ("Twintania", 33), TimeSpan.Zero));
        Assert.Contains("x99", Plain(TooltipRenderer.Render(Loaded(data), Ctx())));
    }

    [Fact]
    public void Quantity_EqualsOne_OmitsStackSuffix()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            homeCheapest: Listing(100, 1, ("Twintania", 33), TimeSpan.Zero));
        Assert.DoesNotContain("x", Plain(TooltipRenderer.Render(Loaded(data), Ctx())));
    }

    [Theory]
    [InlineData(0,       "just now")]
    [InlineData(45,      "just now")]      // <60s
    [InlineData(60 * 5,  "5m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(60 * 60 * 25, "1d ago")]
    public void Age_FormattingVariants(int secondsAgo, string expected)
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            homeCheapest: Listing(100, 1, ("Twintania", 33), TimeSpan.FromSeconds(secondsAgo)));
        Assert.Contains($"({expected})", Plain(TooltipRenderer.Render(Loaded(data), Ctx())));
    }

    [Fact]
    public void RegionRow_IncludesWorldAndDc()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            regionCheapest: Listing(85, 1, ("Cerberus", 80), TimeSpan.FromHours(3)));

        var plain = Plain(TooltipRenderer.Render(Loaded(data), Ctx()));
        // "[XW] Region (Cerberus, Chaos): ..."
        Assert.Contains("[XW] Region (", plain);
        Assert.Contains("Cerberus, Chaos", plain);
    }

    [Fact]
    public void RegionLeaf_WorldLookupFails_RegionRowIsSkipped()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"),
            regionCheapest: Listing(85, 1, ("UnknownRealm", 99999), TimeSpan.FromHours(3)),
            homeCheapest: Listing(120, 1, ("Twintania", 33), TimeSpan.Zero));

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        var lines = SectionLines(doc, "Marketboard Price:");
        Assert.Single(lines);
        Assert.StartsWith("  [H] Home (", lines[0]);
    }

    // -------- contextual empty messages (CTRL-swap onto a quality that has no data) --------

    [Fact]
    public void EmptyShownQuality_OtherQualityHasData_ShowsContextualMessage()
    {
        // User hovers the HQ form of an HQ-able item (game encoded it as HQ). API has only NQ data.
        var dataNqOnly = new ItemTooltip(
            5057,
            ScopeWith(33, "Twintania", listingUnitNq: Listing(100, 1, ("Twintania", 33), TimeSpan.Zero)),
            EmptyScope(null, "Light"),
            EmptyScope(null, "EU"));

        var doc = TooltipRenderer.Render(Loaded(dataNqOnly), Ctx(isHq: true, canBeHq: true));
        var plain = Plain(doc);

        Assert.Contains("No HQ marketboard data", plain);
        Assert.Contains("Hold CTRL to view NQ", plain);    // game will flip the encoding on CTRL
        Assert.DoesNotContain("Marketboard Price:", plain);
    }

    [Fact]
    public void NoDataAtAll_ShowsGenericMessage()
    {
        var data = MakeTooltip(
            home: Tooltip(homeWorld: ("Twintania", 33), homeDc: "Light", homeRegion: "EU"));   // truly empty

        var doc = TooltipRenderer.Render(Loaded(data), Ctx());
        var plain = Plain(doc);

        Assert.Contains("No marketboard info known.", plain);
        Assert.DoesNotContain("Hold CTRL", plain);
    }

    [Fact]
    public void HoveringHq_ButNoHqData_ShowsContextualMessage()
    {
        // User hovers HQ item (isHq=true, no CTRL). API returned NQ data only.
        var dataNqOnly = new ItemTooltip(
            5057,
            ScopeWith(33, "Twintania", listingUnitNq: Listing(100, 1, ("Twintania", 33), TimeSpan.Zero)),
            EmptyScope(null, "Light"),
            EmptyScope(null, "EU"));

        var doc = TooltipRenderer.Render(Loaded(dataNqOnly), Ctx(isHq: true, canBeHq: true));
        var plain = Plain(doc);

        Assert.Contains("No HQ marketboard data", plain);
        Assert.Contains("Hold CTRL to view NQ", plain);
    }

    // -------- helpers --------

    private static RenderContext Ctx(
        bool isHq = false,
        bool canBeHq = true,
        bool useTotal = false) =>
        new(isHq, canBeHq, useTotal,
            id => Worlds.TryGetValue(id, out var w) ? w : null,
            Now);

    private static CacheEntry Loaded(ItemTooltip data) =>
        new(LookupStatus.Loaded, data, Now, null);

    private static ListingLeaf Listing(long price, int qty, (string name, int id) world, TimeSpan age) =>
        new(price, qty, Now - age, new WorldRef(world.id, world.name));

    private static SaleLeaf Sale(long price, int qty, (string name, int id) world, TimeSpan age) =>
        new(price, qty, Now - age, new WorldRef(world.id, world.name));

    private sealed record TooltipFrame(string HomeWorldName, int HomeWorldId, string HomeDc, string HomeRegion);

    private static TooltipFrame Tooltip((string name, int id) homeWorld, string homeDc, string homeRegion) =>
        new(homeWorld.name, homeWorld.id, homeDc, homeRegion);

    /// <summary>Builds an ItemTooltip with cheapest-unit listings only (no sales) at each scope.</summary>
    private static ItemTooltip MakeTooltip(
        TooltipFrame home,
        ListingLeaf? dcCheapest = null,
        ListingLeaf? regionCheapest = null,
        ListingLeaf? homeCheapest = null,
        SaleLeaf? dcSale = null,
        SaleLeaf? regionSale = null,
        SaleLeaf? homeSale = null) =>
        new(
            5057,
            ScopeWith(home.HomeWorldId, home.HomeWorldName, listingUnitNq: homeCheapest, lastSaleNq: homeSale),
            ScopeWith(null, home.HomeDc, listingUnitNq: dcCheapest, lastSaleNq: dcSale),
            ScopeWith(null, home.HomeRegion, listingUnitNq: regionCheapest, lastSaleNq: regionSale));

    private static Scope ScopeWith(
        int? id,
        string name,
        ListingLeaf? listingUnitNq = null,
        ListingLeaf? listingUnitHq = null,
        ListingLeaf? listingTotalNq = null,
        ListingLeaf? listingTotalHq = null,
        SaleLeaf? lastSaleNq = null,
        SaleLeaf? lastSaleHq = null) =>
        new(id, name,
            new ListingGroup(
                new ListingPair(listingUnitNq, listingUnitHq),
                new ListingPair(listingTotalNq ?? listingUnitNq, listingTotalHq ?? listingUnitHq)),
            new LastSale(lastSaleNq, lastSaleHq));

    private static Scope EmptyScope(int? id, string name) => ScopeWith(id, name);

    private static ItemTooltip MakeTooltipWithBothQualities()
    {
        var nq = Listing(100, 1, ("Twintania", 33), TimeSpan.Zero);
        var hq = Listing(250, 1, ("Twintania", 33), TimeSpan.Zero);
        return new ItemTooltip(
            5057,
            ScopeWith(33, "Twintania", listingUnitNq: nq, listingUnitHq: hq),
            EmptyScope(null, "Light"),
            EmptyScope(null, "EU"));
    }

    /// <summary>Flatten a TooltipDocument to readable lines (icons rendered as bracketed placeholders).</summary>
    private static string Plain(TooltipDocument doc)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            foreach (var span in doc.Lines[i].Spans)
            {
                switch (span)
                {
                    case TextSpan t: sb.Append(t.Text); break;
                    case IconSpan { Icon: TooltipIcon.CrossWorld }: sb.Append("[XW]"); break;
                    case IconSpan { Icon: TooltipIcon.Home }: sb.Append("[H]"); break;
                    case IconSpan { Icon: TooltipIcon.Gil }: sb.Append('g'); break;
                    case IconSpan { Icon: TooltipIcon.Hq }: sb.Append("[HQ]"); break;
                    case IconSpan { Icon: TooltipIcon.Loading }: sb.Append("[…]"); break;
                    case IconSpan { Icon: TooltipIcon.Warning }: sb.Append("[!]"); break;
                }
            }
        }
        return sb.ToString();
    }

    private static List<string> SectionLines(TooltipDocument doc, string header)
    {
        var plain = Plain(doc);
        var allLines = plain.Split('\n');
        var idx = Array.IndexOf(allLines, header);
        if (idx < 0) return new List<string>();
        var result = new List<string>();
        for (var i = idx + 1; i < allLines.Length; i++)
        {
            // Stop at the next header (empty trailing lines or known headers).
            if (allLines[i].EndsWith(':') && !allLines[i].StartsWith(' '))
                break;
            result.Add(allLines[i]);
        }
        return result;
    }
}
