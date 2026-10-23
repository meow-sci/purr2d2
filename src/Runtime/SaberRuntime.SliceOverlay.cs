using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    internal void DrawSliceOverlay()
    {
        if (!_ready || _disposed || Program.EditorFlag) return;
        try
        {
            UpdateForceFrame();
            DrawSaberOverlay();
            HandleSlicePointer();
            if ((_sliceHover ?? _sliceTarget) is not { } slice
                || slice.Vehicle.IsDisposed || slice.Part.Tree?.OwningVehicle != slice.Vehicle
                || !ReferenceEquals(_owner, Program.ControlledVehicle)) return;
            var viewport = Program.MainViewport;
            if (!viewport.Visible) return;
            var camera = viewport.GetCamera();
            var ui = ImGui.GetMainViewport();
            var framebuffer = camera.FramebufferSize;
            if (framebuffer.X <= 0 || framebuffer.Y <= 0) return;
            var ego = (slice.HitAssembly - slice.Vehicle.CenterOfMassAsmb).Transform(slice.Vehicle.Body2Cce) + camera.GetPositionEgo(slice.Vehicle);
            if (!double.IsFinite(ego.LengthSquared()) || double3.Dot(ego, camera.GetForwardEcl()) <= .001) return;
            var pixels = camera.EgoToScreen(ego);
            var position = ui.Pos + new float2(pixels.X * ui.Size.X / framebuffer.X, pixels.Y * ui.Size.Y / framebuffer.Y);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return;
            var draw = ImGui.GetBackgroundDrawList(ui);
            bool allowed = _selectingSlice ? _sliceHoverAllowed : _sliceError == null;
            var color = allowed ? new ImColor8(90, 255, 135, 255) : new ImColor8(255, 80, 80, 255);
            float scale = Math.Clamp(ImGui.GetFontSize() / 16f, 1, 2.5f);
            float radius = 22 * scale;
            draw.PushClipRect(ui.Pos, ui.Pos + ui.Size);
            try
            {
                draw.AddCircle(position, radius, new ImColor8(0, 0, 0, 200), 48, 6 * scale);
                draw.AddCircle(position, radius, color, 48, 3 * scale);
                draw.AddCircleFilled(position, 4 * scale, color, 16);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    draw.AddLine(position + new float2(sign * 10 * scale, 0), position + new float2(sign * 29 * scale, 0), color, 2 * scale);
                    draw.AddLine(position + new float2(0, sign * 10 * scale), position + new float2(0, sign * 29 * scale), color, 2 * scale);
                }
                string label = slice.Part.DisplayName + (_selectingSlice ? allowed ? " — click to slice" : " — cannot slice" : "");
                if (_selectingSlice && !allowed) label += "\n" + _sliceStatus;
                var labelSize = ImGui.CalcTextSize(label);
                var labelPosition = new float2(
                    Math.Max(ui.Pos.X + 8, Math.Min(position.X + 34 * scale, ui.Pos.X + ui.Size.X - labelSize.X - 8)),
                    Math.Max(ui.Pos.Y + 8, Math.Min(position.Y - labelSize.Y / 2, ui.Pos.Y + ui.Size.Y - labelSize.Y - 8)));
                draw.AddText(labelPosition + new float2(1, 1), new ImColor8(0, 0, 0, 255), label);
                draw.AddText(labelPosition, color, label);
            }
            finally { draw.PopClipRect(); }
        }
        catch (Exception ex) { Report("Slice marker failed", ex); }
    }
}
