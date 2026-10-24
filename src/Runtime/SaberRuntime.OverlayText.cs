using Brutal.ImGuiApi;
using Brutal.Numerics;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private static void DrawCenteredOverlayStatus(ImDrawListPtr draw, string text, float2 min, float2 max,
        float fontSize, float scale, bool bottomAligned = false)
    {
        float baseFontSize = ImGui.GetFontSize();
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y)
            || !float.IsFinite(fontSize) || fontSize <= 0 || !float.IsFinite(baseFontSize) || baseFontSize <= 0
            || !float.IsFinite(max.X - min.X) || !float.IsFinite(max.Y - min.Y)
            || max.X <= min.X || max.Y <= min.Y)
            return;

        float fontScale = fontSize / baseFontSize;
        var nominalLineSize = ImGui.CalcTextSize("Mg");
        float lineHeight = nominalLineSize.Y * fontScale;
        if (!float.IsFinite(fontScale) || fontScale <= 0 || !float.IsFinite(lineHeight) || lineHeight <= 0)
            return;

        var lines = OverlayStatusTextLayout.Create(text, min, max, lineHeight,
            value => ImGui.CalcTextSize(value).X * fontScale, bottomAligned);
        if (lines.Length == 0) return;

        float shadowOffset = float.IsFinite(scale) && scale > 0 ? scale : 1;
        var shadow = new ImColor8(0, 0, 0, 255);
        var foreground = new ImColor8(255, 255, 255, 255);
        draw.PushClipRect(min, max, true);
        try
        {
            foreach (var line in lines)
            {
                draw.AddText(ImGui.GetFont(), fontSize, line.Position + new float2(shadowOffset), shadow, line.Text, 0);
                draw.AddText(ImGui.GetFont(), fontSize, line.Position, foreground, line.Text, 0);
            }
        }
        finally { draw.PopClipRect(); }
    }
}
