using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivMarket.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("XIV Market##xivmarket-main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.TextDisabled($"API: {this.plugin.Configuration.ApiBaseUrl}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Settings##open"))
            this.plugin.ToggleConfigUi();

        ImGui.Separator();

        ImGui.Text($"Worlds loaded: {this.plugin.Worlds.Count}");
        ImGui.Text($"Cache TTL: {this.plugin.Configuration.CacheTtlSeconds}s");
        ImGui.Text($"Cheapest by: {(this.plugin.Configuration.UseCheapestTotalStack ? "total stack" : "per unit")}");

        ImGui.Spacing();
        ImGui.TextWrapped("Hover any item in-game to see XivMarket prices in the tooltip.");
        ImGui.TextWrapped("CTRL: swap NQ↔HQ.   ALT: force-refresh hovered item.");
    }

    public void Dispose() { }
}
