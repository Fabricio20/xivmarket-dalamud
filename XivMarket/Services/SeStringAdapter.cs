using System.Collections.Generic;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XivMarket.Models;

namespace XivMarket.Services;

/// <summary>
/// Translates the renderer-agnostic <see cref="TooltipDocument"/> AST into a Dalamud
/// <see cref="SeString"/>. Color and icon mappings live here, not in the renderer.
/// </summary>
public static class SeStringAdapter
{
    // FFXIV in-game font glyphs at private-use code points. Written as \u escapes because the
    // raw chars don't always survive editor/tool round-trips.
    private const string HqGlyph = "\uE03C";
    private const string GilGlyph = "\uE049";

    public static SeString ToSeString(TooltipDocument doc)
    {
        var payloads = new List<Payload>();
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            if (i > 0)
                payloads.Add(new TextPayload("\n"));
            foreach (var span in doc.Lines[i].Spans)
                AppendSpan(payloads, span);
        }
        return new SeString(payloads);
    }

    private static void AppendSpan(List<Payload> payloads, TooltipSpan span)
    {
        switch (span)
        {
            case TextSpan t:
                AppendColored(payloads, t.Color, p => p.Add(new TextPayload(t.Text)));
                break;
            case IconSpan icon:
                AppendColored(payloads, icon.Color, p => AppendIcon(p, icon.Icon));
                break;
        }
    }

    private static void AppendColored(List<Payload> payloads, TooltipColor color, System.Action<List<Payload>> body)
    {
        var needsColor = color != TooltipColor.Normal;
        if (needsColor) payloads.Add(new UIForegroundPayload(ColorCode(color)));
        body(payloads);
        if (needsColor) payloads.Add(new UIForegroundPayload(0));
    }

    private static void AppendIcon(List<Payload> payloads, TooltipIcon icon)
    {
        switch (icon)
        {
            case TooltipIcon.CrossWorld:
                payloads.Add(new IconPayload(BitmapFontIcon.CrossWorld));
                break;
            case TooltipIcon.Home:
                payloads.Add(new IconPayload(BitmapFontIcon.Aetheryte));
                break;
            case TooltipIcon.Loading:
                payloads.Add(new IconPayload(BitmapFontIcon.LevelSync));
                break;
            case TooltipIcon.Warning:
                payloads.Add(new IconPayload(BitmapFontIcon.Warning));
                break;
            case TooltipIcon.Hq:
                payloads.Add(new TextPayload(HqGlyph));
                break;
            case TooltipIcon.Gil:
                payloads.Add(new TextPayload(GilGlyph));
                break;
        }
    }

    private static ushort ColorCode(TooltipColor c) => c switch
    {
        TooltipColor.Dim => 20,
        TooltipColor.Highlight => 506,
        TooltipColor.Error => 17,
        _ => 0,
    };
}
