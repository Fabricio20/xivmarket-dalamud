using System;
using System.Collections.Generic;
using XivMarket.Models;

namespace XivMarket.Services;

/// <summary>
/// Pure formatter: turns a <see cref="CacheEntry"/> + <see cref="RenderContext"/> into a
/// <see cref="TooltipDocument"/>. No I/O, no Dalamud types - the hook layer translates the
/// document to a SeString afterwards.
/// </summary>
public static class TooltipRenderer
{
    public static TooltipDocument Render(CacheEntry entry, RenderContext context) => entry.Status switch
    {
        LookupStatus.NonMarketable => TooltipDocument.Empty,
        LookupStatus.Loading       => RenderLoading(),
        LookupStatus.Failed        => RenderFailed(entry.FailureReason),
        LookupStatus.Loaded        => RenderLoaded(entry.Data!, context),
        _                          => TooltipDocument.Empty,
    };

    private static TooltipDocument RenderLoading() => new(new[]
    {
        new TooltipLine(new TooltipSpan[]
        {
            new IconSpan(TooltipIcon.Loading),
            new TextSpan(" Loading marketboard info...", TooltipColor.Dim),
        }),
    });

    private static TooltipDocument RenderFailed(string? reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "Failed to fetch marketboard info."
            : $"Failed to fetch marketboard info: {reason}";
        return new TooltipDocument(new[]
        {
            new TooltipLine(new TooltipSpan[]
            {
                new IconSpan(TooltipIcon.Warning),
                new TextSpan($" {detail}", TooltipColor.Error),
            }),
            new TooltipLine(new TooltipSpan[]
            {
                new TextSpan("Press ALT to retry.", TooltipColor.Dim),
            }),
        });
    }

    private static TooltipDocument RenderLoaded(ItemTooltip data, RenderContext ctx)
    {
        var quality = ResolveDisplayQuality(ctx);
        var marketboard = BuildSection(data, quality, ctx, sales: false);
        var lastSale = BuildSection(data, quality, ctx, sales: true);

        var lines = new List<TooltipLine>();
        if (marketboard.Count > 0)
        {
            lines.Add(Header("Marketboard Price:"));
            lines.AddRange(marketboard);
        }
        if (lastSale.Count > 0)
        {
            lines.Add(Header("Last Sale:"));
            lines.AddRange(lastSale);
        }
        if (lines.Count == 0)
            lines.AddRange(BuildEmptyMessage(data, quality, ctx));

        return new TooltipDocument(lines);
    }

    /// <summary>
    /// When a section ends up empty, we need to tell the user *why*: did the API have nothing
    /// for this item at all, or did we just filter to a quality that has no data? The latter is
    /// the common case when CTRL-swapping on an HQ-able item that's only been listed in NQ.
    /// </summary>
    private static IEnumerable<TooltipLine> BuildEmptyMessage(ItemTooltip data, Quality shown, RenderContext ctx)
    {
        var other = shown == Quality.Hq ? Quality.Nq : Quality.Hq;
        var otherHasData = HasAnyData(data, other, ctx);

        if (otherHasData && ctx.CanBeHq)
        {
            var shownName = shown == Quality.Hq ? "HQ" : "NQ";
            var otherName = shown == Quality.Hq ? "NQ" : "HQ";
            yield return Message($"No {shownName} marketboard data for this item.");
            yield return Message($"Hold CTRL to view {otherName} prices.");
        }
        else
        {
            yield return Message("No marketboard info known.");
        }
    }

    private static bool HasAnyData(ItemTooltip data, Quality q, RenderContext ctx)
    {
        foreach (var scope in new[] { data.World, data.Datacenter, data.Region })
        {
            if (AsLeaf(scope.Listing, q, ctx.UseCheapestTotalStack) != null) return true;
            if (AsLeaf(scope.LastSale, q) != null) return true;
        }
        return false;
    }

    private enum Quality { Nq, Hq }

    /// <summary>
    /// The displayed quality is purely a function of the hovered item's encoding. The game already
    /// flips the encoding when the user holds CTRL on an HQ-able item (HQ-form id 1_047_166 vs
    /// NQ-form id 47_166), so doing our own CTRL swap would double-flip. We just trust IsHq.
    /// </summary>
    private static Quality ResolveDisplayQuality(RenderContext ctx) =>
        ctx.IsHq ? Quality.Hq : Quality.Nq;

    private enum LineKind { Dc, Region, Home }

    /// <summary>Common shape for both listings and sales - collapses the field-name difference (LastUpdated vs Time).</summary>
    private sealed record LeafView(long Price, int Quantity, DateTimeOffset At, int WorldId, string WorldName);

    private static List<TooltipLine> BuildSection(ItemTooltip data, Quality q, RenderContext ctx, bool sales)
    {
        var rows = new List<(long Price, TooltipLine Line)>();
        var homeWorld = data.World.Name;
        var homeDc = data.Datacenter.Name;

        // DC line - show only if cheapest world is different from home (otherwise it'd duplicate the Home row).
        var dcLeaf = sales
            ? AsLeaf(data.Datacenter.LastSale, q)
            : AsLeaf(data.Datacenter.Listing, q, ctx.UseCheapestTotalStack);
        if (dcLeaf != null && dcLeaf.WorldName != homeWorld)
            rows.Add((dcLeaf.Price, BuildRow(LineKind.Dc, dcLeaf, ctx)));

        // Region line - show only if cheapest is in a different DC than home.
        var regionLeaf = sales
            ? AsLeaf(data.Region.LastSale, q)
            : AsLeaf(data.Region.Listing, q, ctx.UseCheapestTotalStack);
        if (regionLeaf != null)
        {
            var regionDc = ctx.WorldLookup(regionLeaf.WorldId)?.Datacenter;
            // If lookup fails (worlds map not loaded yet), skip rather than risk a duplicate row.
            if (regionDc != null && regionDc != homeDc)
                rows.Add((regionLeaf.Price, BuildRow(LineKind.Region, regionLeaf, ctx, regionDc: regionDc)));
        }

        // Home line - always show if we have data.
        var homeLeaf = sales
            ? AsLeaf(data.World.LastSale, q)
            : AsLeaf(data.World.Listing, q, ctx.UseCheapestTotalStack);
        if (homeLeaf != null)
            rows.Add((homeLeaf.Price, BuildRow(LineKind.Home, homeLeaf, ctx, homeWorld: homeWorld)));

        // Sort cheapest first - more useful than fixed scope order, the user can spot the deal.
        rows.Sort((a, b) => a.Price.CompareTo(b.Price));
        return rows.ConvertAll(r => r.Line);
    }

    private static LeafView? AsLeaf(ListingGroup group, Quality q, bool useTotal)
    {
        var pair = useTotal ? group.Total : group.Unit;
        var leaf = q == Quality.Hq ? pair.Hq : pair.Nq;
        return leaf is null
            ? null
            : new LeafView(leaf.Price, leaf.Quantity, leaf.LastUpdated, leaf.World.Id, leaf.World.Name);
    }

    private static LeafView? AsLeaf(LastSale sale, Quality q)
    {
        var leaf = q == Quality.Hq ? sale.Hq : sale.Nq;
        return leaf is null
            ? null
            : new LeafView(leaf.Price, leaf.Quantity, leaf.Time, leaf.World.Id, leaf.World.Name);
    }

    private static TooltipLine BuildRow(
        LineKind kind, LeafView leaf, RenderContext ctx, string? regionDc = null, string? homeWorld = null)
    {
        var spans = new List<TooltipSpan>();
        spans.Add(new TextSpan("  "));    // 2-space indent - section headers stay flush-left
        AppendScopeLabel(spans, kind, leaf.WorldName, regionDc, homeWorld);
        spans.Add(new TextSpan($"{leaf.Price:N0}", TooltipColor.Highlight));
        spans.Add(new IconSpan(TooltipIcon.Gil, TooltipColor.Highlight));
        if (leaf.Quantity > 1)
            spans.Add(new TextSpan($" - x{leaf.Quantity}", TooltipColor.Dim));
        spans.Add(new TextSpan($"  ({FormatAge(ctx.Now - leaf.At)})", TooltipColor.Dim));
        return new TooltipLine(spans);
    }

    private static void AppendScopeLabel(
        List<TooltipSpan> spans, LineKind kind, string leafWorld, string? regionDc, string? homeWorld)
    {
        // Cross-world icon prefixes the entire scope label (not the world name) so DC and Region
        // rows visually align: "[XW] DC (Cactuar): ..." / "[XW] Region (Diabolos, Crystal): ...".
        // Home rows have no XW prefix since they're on the player's own world.
        switch (kind)
        {
            case LineKind.Dc:
                spans.Add(new IconSpan(TooltipIcon.CrossWorld));
                spans.Add(new TextSpan($" DC ({leafWorld}): "));
                break;
            case LineKind.Region:
                spans.Add(new IconSpan(TooltipIcon.CrossWorld));
                spans.Add(new TextSpan($" Region ({leafWorld}, {regionDc}): "));
                break;
            case LineKind.Home:
                spans.Add(new IconSpan(TooltipIcon.Home));
                spans.Add(new TextSpan($" Home ({homeWorld}): "));
                break;
        }
    }

    private static string FormatAge(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    private static TooltipLine Header(string text) =>
        new(new TooltipSpan[] { new TextSpan(text) });

    private static TooltipLine Message(string text) =>
        new(new TooltipSpan[] { new TextSpan(text, TooltipColor.Dim) });
}
