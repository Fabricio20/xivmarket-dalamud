using System;

namespace XivMarket.Models;

/// <summary>
/// Per-render input the tooltip formatter needs that isn't part of the cached data.
/// Note: there's no CTRL flag here - the game itself flips the hovered-id encoding when the user
/// holds CTRL on an HQ-able item, so <see cref="IsHq"/> already reflects what the user wants to see.
/// </summary>
public sealed record RenderContext(
    bool IsHq,
    bool CanBeHq,
    bool UseCheapestTotalStack,
    Func<int, WorldInfo?> WorldLookup,
    DateTimeOffset Now);

/// <summary>Static metadata about a world - populated from /static/worlds.json at plugin startup.</summary>
public sealed record WorldInfo(int Id, string Name, string Datacenter);
