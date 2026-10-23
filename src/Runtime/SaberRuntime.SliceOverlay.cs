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
            var color = _selectingSlice ? new ImColor8(90, 255, 160, 255) : new ImColor8(255, 185, 60, 255);
            draw.PushClipRect(ui.Pos, ui.Pos + ui.Size);
            try
            {
                draw.AddCircle(position, 7, color, 16, 2);
                draw.AddLine(position - new float2(10, 0), position + new float2(10, 0), color);
                draw.AddLine(position - new float2(0, 10), position + new float2(0, 10), color);
                draw.AddText(position + new float2(12, -8), color, slice.Part.DisplayName + (_selectingSlice ? " — click to slice" : ""));
            }
            finally { draw.PopClipRect(); }
        }
        catch (Exception ex) { Report("Slice marker failed", ex); }
    }
}
