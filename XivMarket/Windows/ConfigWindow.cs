using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivMarket.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string apiUrlBuf;
    private int ttlSecondsBuf;
    private bool useTotalStackBuf;
    private bool debugLoggingBuf;

    public ConfigWindow(Plugin plugin)
        : base("XIV Market - Settings##xivmarket-config")
    {
        this.plugin = plugin;
        // Wide enough to fit the longest description without horizontal scroll. AlwaysAutoResize
        // would only grow vertically; long TextDisabled lines were getting clipped on the right.
        Size = new Vector2(640, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.apiUrlBuf = plugin.Configuration.ApiBaseUrl;
        this.ttlSecondsBuf = plugin.Configuration.CacheTtlSeconds;
        this.useTotalStackBuf = plugin.Configuration.UseCheapestTotalStack;
        this.debugLoggingBuf = plugin.Configuration.DebugLogging;
    }

    public override void Draw()
    {
        // Use the window's full content width for inputs; otherwise ImGui caps them narrowly.
        ImGui.PushItemWidth(-1);

        ImGui.InputText("##api-url", ref this.apiUrlBuf, 256);
        ImGui.TextDisabled("API base URL");

        ImGui.Spacing();
        ImGui.InputInt("##ttl", ref this.ttlSecondsBuf);
        ImGui.TextDisabled("Cache TTL (seconds). Min 60, default 300. ALT-hover bypasses regardless.");
        if (this.ttlSecondsBuf < 60) this.ttlSecondsBuf = 60;

        ImGui.Spacing();
        ImGui.Checkbox("Use cheapest total stack", ref this.useTotalStackBuf);
        ImGui.TextWrapped("If on, picks the listing with the lowest total cost (may be a smaller stack) instead of the lowest per-unit price.");

        ImGui.Spacing();
        ImGui.Checkbox("Verbose debug logging", ref this.debugLoggingBuf);
        ImGui.TextWrapped("Logs per-hover diagnostic info to /xllog. Enable when troubleshooting.");

        ImGui.PopItemWidth();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Save"))
        {
            this.plugin.Configuration.ApiBaseUrl = this.apiUrlBuf.Trim();
            this.plugin.Configuration.CacheTtlSeconds = Math.Max(60, this.ttlSecondsBuf);
            this.plugin.Configuration.UseCheapestTotalStack = this.useTotalStackBuf;
            this.plugin.Configuration.DebugLogging = this.debugLoggingBuf;
            this.plugin.Configuration.Save();
        }
    }

    public void Dispose() { }
}
