using System;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivMarket.Services;

/// <summary>
/// Owns the AtkTextNode injected into the ItemDetail tooltip. Logic ported from Price Insight's
/// ItemPriceTooltip with extra defensive null checks at every pointer dereference; failure modes
/// log and bail rather than crash the game. The finalizer is a last-resort cleanup if the plugin
/// is unloaded without Dispose() running cleanly.
/// </summary>
public sealed class TooltipInjector : IDisposable
{
    /// <summary>Distinct from Price Insight's 32612 so both plugins can coexist without clobbering each other.</summary>
    private const uint NodeId = 32613;

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private bool disposed;

    public TooltipInjector(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>
    /// Re-injects (or updates) the price text node on the given ItemDetail addon. Safe to call
    /// from the framework thread only - pointer manipulation isn't thread-safe.
    /// </summary>
    public unsafe void Inject(AtkUnitBase* itemTooltip, SeString content)
    {
        if (this.disposed) return;
        if (itemTooltip == null) return;
        if (content is null) return;

        try
        {
            UpdateItemTooltip(itemTooltip, content);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "TooltipInjector.Inject failed");
        }
    }

    /// <summary>
    /// Hides our injected node and restores the tooltip's original layout. Called on
    /// PreRequestedUpdate so the game's own resize logic operates on the unmodified state.
    /// </summary>
    public unsafe void Restore(AtkUnitBase* itemTooltip)
    {
        if (this.disposed) return;
        if (itemTooltip == null) return;

        try
        {
            RestoreToNormal(itemTooltip);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "TooltipInjector.Restore failed");
        }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        // Cleanup must run on the framework thread - caller (Plugin.Dispose) is responsible
        // for marshalling. We only set the flag here so any in-flight Inject/Restore bails.
        try
        {
            this.CleanupNode();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "TooltipInjector cleanup failed during Dispose");
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Last-resort cleanup if the managed Dispose path didn't reach us.</summary>
    ~TooltipInjector()
    {
        try { this.CleanupNode(); }
        catch { /* finalizer must not throw */ }
    }

    /// <summary>
    /// Find and destroy our injected text node if the ItemDetail addon is still around.
    /// Idempotent - safe to call multiple times.
    /// </summary>
    public unsafe void CleanupNode()
    {
        var addon = this.gameGui.GetAddonByName("ItemDetail");
        if (addon.Address == 0) return;
        var atkUnitBase = (AtkUnitBase*)addon.Address;
        if (atkUnitBase == null) return;

        for (var i = 0; i < atkUnitBase->UldManager.NodeListCount; i++)
        {
            var node = atkUnitBase->UldManager.NodeList[i];
            if (node == null) continue;
            if (node->NodeId != NodeId) continue;

            // Detach from sibling chain before destroying.
            if (node->ParentNode != null && node->ParentNode->ChildNode == node)
                node->ParentNode->ChildNode = node->PrevSiblingNode;
            if (node->PrevSiblingNode != null)
                node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
            if (node->NextSiblingNode != null)
                node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;

            atkUnitBase->UldManager.UpdateDrawNodeList();
            node->Destroy(true);
            return;
        }
    }

    // -------- ported pointer manipulation (defensive port from Price Insight) --------

    private static unsafe void RestoreToNormal(AtkUnitBase* itemTooltip)
    {
        for (var i = 0; i < itemTooltip->UldManager.NodeListCount; i++)
        {
            var n = itemTooltip->UldManager.NodeList[i];
            if (n == null) continue;
            if (n->NodeId != NodeId || !n->IsVisible()) continue;

            n->ToggleVisibility(false);
            var insertNode = itemTooltip->GetNodeById(2);
            if (insertNode == null) return;
            if (itemTooltip->WindowNode == null) return;

            var newWindowHeight = (ushort)(itemTooltip->WindowNode->AtkResNode.Height - n->Height - 4);
            itemTooltip->WindowNode->AtkResNode.SetHeight(newWindowHeight);

            var rootNode = itemTooltip->WindowNode->Component != null
                ? itemTooltip->WindowNode->Component->UldManager.RootNode
                : null;
            if (rootNode != null)
            {
                rootNode->SetHeight(newWindowHeight);
                if (rootNode->PrevSiblingNode != null)
                    rootNode->PrevSiblingNode->SetHeight(newWindowHeight);
            }
            insertNode->SetYFloat(insertNode->Y - n->Height - 4);
            return;
        }
    }

    private static unsafe void UpdateItemTooltip(AtkUnitBase* itemTooltip, SeString content)
    {
        AtkTextNode* priceNode = null;
        for (var i = 0; i < itemTooltip->UldManager.NodeListCount; i++)
        {
            var node = itemTooltip->UldManager.NodeList[i];
            if (node == null || node->NodeId != NodeId) continue;
            priceNode = (AtkTextNode*)node;
            break;
        }

        var insertNode = itemTooltip->GetNodeById(2);
        if (insertNode == null) return;

        if (priceNode == null)
        {
            // Use node id 44 as a styling template (existing tooltip text node).
            var baseNode = itemTooltip->GetTextNodeById(44);
            if (baseNode == null) return;

            priceNode = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
            if (priceNode == null) return;

            priceNode->AtkResNode.Type = NodeType.Text;
            priceNode->AtkResNode.NodeId = NodeId;
            priceNode->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop;
            priceNode->AtkResNode.X = 16;
            priceNode->AtkResNode.Width = 50;
            priceNode->AtkResNode.Color = baseNode->AtkResNode.Color;
            priceNode->TextColor = baseNode->TextColor;
            priceNode->EdgeColor = baseNode->EdgeColor;
            priceNode->LineSpacing = 18;
            priceNode->FontSize = 12;
            priceNode->TextFlags = baseNode->TextFlags | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

            var prev = insertNode->PrevSiblingNode;
            priceNode->AtkResNode.ParentNode = insertNode->ParentNode;
            insertNode->PrevSiblingNode = (AtkResNode*)priceNode;
            if (prev != null)
                prev->NextSiblingNode = (AtkResNode*)priceNode;
            priceNode->AtkResNode.PrevSiblingNode = prev;
            priceNode->AtkResNode.NextSiblingNode = insertNode;
            itemTooltip->UldManager.UpdateDrawNodeList();
        }

        priceNode->AtkResNode.ToggleVisibility(true);
        priceNode->SetText(content.Encode());
        priceNode->ResizeNodeForCurrentText();

        if (itemTooltip->WindowNode == null) return;
        priceNode->AtkResNode.SetYFloat(itemTooltip->WindowNode->AtkResNode.Height - 8);
        var newHeight = (ushort)(itemTooltip->WindowNode->AtkResNode.Height + priceNode->AtkResNode.Height + 4);
        itemTooltip->WindowNode->SetHeight(newHeight);
        itemTooltip->WindowNode->AtkResNode.SetHeight(itemTooltip->WindowNode->Height);

        var rootNode = itemTooltip->WindowNode->Component != null
            ? itemTooltip->WindowNode->Component->UldManager.RootNode
            : null;
        if (rootNode != null)
        {
            rootNode->SetHeight(itemTooltip->WindowNode->Height);
            if (rootNode->PrevSiblingNode != null)
                rootNode->PrevSiblingNode->SetHeight(itemTooltip->WindowNode->Height);
        }
        if (itemTooltip->RootNode != null)
            itemTooltip->RootNode->SetHeight(itemTooltip->WindowNode->Height);

        insertNode->SetYFloat(insertNode->Y + priceNode->AtkResNode.Height + 4);
    }
}
