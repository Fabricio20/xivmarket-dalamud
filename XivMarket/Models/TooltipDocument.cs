using System;
using System.Collections.Generic;

namespace XivMarket.Models;

/// <summary>
/// Renderer-agnostic AST representing a tooltip's content. Decouples the formatter from
/// Dalamud's SeString types so the renderer is testable on plain net10.0; the hook layer
/// owns the AST → SeString adapter.
/// </summary>
public sealed record TooltipDocument(IReadOnlyList<TooltipLine> Lines)
{
    public bool IsEmpty => this.Lines.Count == 0;

    public static TooltipDocument Empty { get; } = new(Array.Empty<TooltipLine>());
}

public sealed record TooltipLine(IReadOnlyList<TooltipSpan> Spans);

public abstract record TooltipSpan;

public sealed record TextSpan(string Text, TooltipColor Color = TooltipColor.Normal) : TooltipSpan;

public sealed record IconSpan(TooltipIcon Icon, TooltipColor Color = TooltipColor.Normal) : TooltipSpan;

public enum TooltipColor
{
    Normal,
    Dim,
    Highlight,
    Error,
}

public enum TooltipIcon
{
    CrossWorld,
    Home,
    Gil,
    Hq,
    Loading,
    Warning,
}
