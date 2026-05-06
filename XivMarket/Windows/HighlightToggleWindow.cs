using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMarket.Services;

namespace XivMarket.Windows;

public sealed class HighlightToggleWindow : Window, IDisposable
{
    private const string AddonName = "RetainerSellList";

    private readonly Plugin plugin;
    private readonly IAddonLifecycle.AddonEventDelegate onPostSetup;
    private readonly IAddonLifecycle.AddonEventDelegate onPreFinalize;
    private readonly IAddonLifecycle.AddonEventDelegate onPostDraw;
    private bool disposed;

    public HighlightToggleWindow(Plugin plugin)
        : base("XIV Market##highlight-toggle",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        this.ForceMainWindow = true;
        this.RespectCloseHotkey = false;
        this.IsOpen = false;

        this.onPostSetup = this.OnPostSetup;
        this.onPreFinalize = this.OnPreFinalize;
        this.onPostDraw = this.OnPostDraw;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, this.onPostSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, this.onPreFinalize);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, this.onPostDraw);
    }

    private unsafe void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        this.IsOpen = true;
        this.BindPosition(args.Addon);
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        this.IsOpen = false;
    }

    private unsafe void OnPostDraw(AddonEvent type, AddonArgs args)
    {
        this.BindPosition(args.Addon);
    }

    private unsafe void BindPosition(nint addon)
    {
        try
        {
            if (addon == nint.Zero) return;
            var atk = (AtkUnitBase*)addon;
            var x = atk->X + atk->GetScaledWidth(true);
            var y = (float)atk->Y + 2;
            this.Position = new Vector2(x, y);
        }
        catch { /* best effort positioning */ }
    }

    public override void Draw()
    {
        var active = this.plugin.InventoryHighlight.Active;
        if (ImGui.Checkbox("Highlight Marketable Items", ref active))
        {
            this.plugin.InventoryHighlight.Active = active;
            if (!active)
                this.plugin.InventoryHighlight.ClearAllHighlights();
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        try
        {
            Service.AddonLifecycle.UnregisterListener(this.onPostSetup);
            Service.AddonLifecycle.UnregisterListener(this.onPreFinalize);
            Service.AddonLifecycle.UnregisterListener(this.onPostDraw);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "HighlightToggleWindow.Dispose failed");
        }
    }
}
