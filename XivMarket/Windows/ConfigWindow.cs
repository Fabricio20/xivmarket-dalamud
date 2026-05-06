using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivMarket.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] ScopeLabels = { "World", "Datacenter", "Region" };
    private static readonly string[] QualityModeLabels = { "Any", "Matching Quality", "NQ Only", "HQ Only" };

    private readonly Plugin plugin;
    private string apiUrlBuf;
    private int ttlSecondsBuf;
    private bool useTotalStackBuf;
    private bool debugLoggingBuf;
    private int undercutAmountBuf;
    private int roundToBuf;
    private bool roundUpBuf;
    private int priceScopeBuf;
    private int qualityModeBuf;
    private float highlightVendorMultiplierBuf;
    private int highlightMinPriceBuf;
    private bool highlightMinPriceIsTotalBuf;

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
        this.undercutAmountBuf = plugin.Configuration.UndercutAmount;
        this.roundToBuf = plugin.Configuration.RoundTo;
        this.roundUpBuf = plugin.Configuration.RoundUp;
        this.priceScopeBuf = plugin.Configuration.PriceSourceScope;
        this.qualityModeBuf = plugin.Configuration.UndercutQualityMode;
        this.highlightVendorMultiplierBuf = plugin.Configuration.HighlightVendorMultiplier;
        this.highlightMinPriceBuf = plugin.Configuration.HighlightMinPrice;
        this.highlightMinPriceIsTotalBuf = plugin.Configuration.HighlightMinPriceIsTotal;
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
        ImGui.TextDisabled("If ON, picks the listing with the lowest total cost instead of the lowest per-unit price.");

        ImGui.Spacing();
        ImGui.Checkbox("Verbose debug logging", ref this.debugLoggingBuf);
        ImGui.TextDisabled("Logs per-hover diagnostic info to /xllog. Enable when troubleshooting.");

        ImGui.PopItemWidth();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Retainer Pricing");
        ImGui.PushItemWidth(-1);

        ImGui.Spacing();
        ImGui.InputInt("##undercut-amount", ref this.undercutAmountBuf);
        ImGui.TextDisabled("Undercut amount (gil to subtract from cheapest). 0 = match price.");
        if (this.undercutAmountBuf < 0) this.undercutAmountBuf = 0;

        ImGui.Spacing();
        ImGui.InputInt("##round-to", ref this.roundToBuf);
        ImGui.TextDisabled("Round to nearest (1 = no rounding, 10 = nearest 10, 100 = nearest 100).");
        if (this.roundToBuf < 1) this.roundToBuf = 1;

        ImGui.Spacing();
        ImGui.Checkbox("Round up", ref this.roundUpBuf);
        ImGui.TextDisabled("If checked, round UP (encourages stable prices). If unchecked, round down.");

        ImGui.Spacing();
        ImGui.Combo("##price-scope", ref this.priceScopeBuf, ScopeLabels, ScopeLabels.Length);
        ImGui.TextDisabled("Price source scope (which listing to compare against).");

        ImGui.Spacing();
        ImGui.Combo("##quality-mode", ref this.qualityModeBuf, QualityModeLabels, QualityModeLabels.Length);
        ImGui.TextDisabled("Quality comparison mode for price calculation.");

        ImGui.PopItemWidth();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Inventory Highlighting");
        ImGui.PushItemWidth(-1);

        ImGui.Spacing();
        ImGui.SliderFloat("##highlight-vendor-multiplier", ref this.highlightVendorMultiplierBuf, 1.0f, 10.0f, "%.1fx");
        ImGui.TextDisabled("Vendor price multiplier. Items with market price below vendor sell price * this = red.");

        ImGui.Spacing();
        ImGui.InputInt("##highlight-min-price", ref this.highlightMinPriceBuf);
        ImGui.TextDisabled("Minimum market price for green (absolute floor). 0 = disabled.");
        if (this.highlightMinPriceBuf < 0) this.highlightMinPriceBuf = 0;

        ImGui.Spacing();
        ImGui.Checkbox("Use total stack value for minimum price", ref this.highlightMinPriceIsTotalBuf);
        ImGui.TextDisabled("If ON, considers the total stack value instead.");

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
            this.plugin.Configuration.UndercutAmount = Math.Max(0, this.undercutAmountBuf);
            this.plugin.Configuration.RoundTo = Math.Max(1, this.roundToBuf);
            this.plugin.Configuration.RoundUp = this.roundUpBuf;
            this.plugin.Configuration.PriceSourceScope = Math.Clamp(this.priceScopeBuf, 0, 2);
            this.plugin.Configuration.UndercutQualityMode = Math.Clamp(this.qualityModeBuf, 0, 3);
            this.plugin.Configuration.HighlightVendorMultiplier = Math.Max(1.0f, this.highlightVendorMultiplierBuf);
            this.plugin.Configuration.HighlightMinPrice = Math.Max(0, this.highlightMinPriceBuf);
            this.plugin.Configuration.HighlightMinPriceIsTotal = this.highlightMinPriceIsTotalBuf;
            this.plugin.Configuration.Save();
        }
    }

    public void Dispose() { }
}
