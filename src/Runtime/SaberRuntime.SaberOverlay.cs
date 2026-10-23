using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;
using OverlayHit = Purr2D2.Core.SaberOverlayHit;
using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly SaberOverlayDrag _overlayDrag = new();
    private readonly SaberOverlayPointerCapture _overlayPointerCapture = new();
    private bool _showSaberOverlay;
    private bool _overlayLayoutAvailable;
    private OverlayLayout _overlayLayout;
    private KittenEva? _overlayDragOwner;
    private SaberPoseController? _overlayDragPose;
    private float _overlayListScroll;

    private bool OverlayVisible => _showSaberOverlay && _ready && !_disposed && Program.DrawUI
        && !Program.EditorFlag && Universe.CurrentSystem != null && Program.MainViewport.Visible;
    private bool OverlayCanSwing => _fault == null && _owner is { IsDisposed: false }
        && ReferenceEquals(_owner, Program.ControlledVehicle) && _blade.Equipped && _pose.Eligible
        && !_selectingSlice && (!_sliceSequence.Active || _sliceSequence.EndPresented);
    private bool OverlayCanStartSwing => OverlayCanSwing && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed());

    // The picker runs after this overlay; retain consumption for quick press/release pairs too.
    internal bool OverlayCapturesPointer => OverlayVisible && (_overlayPointerCapture.AnyHeld
        || _overlayPointerCapture.EventFrame == Program.FrameNumber
        || (_overlayLayoutAvailable && ImGui.GetIO().MouseHoveredViewport == ImGui.GetMainViewport().ID
            && _overlayLayout.Hit(ImGui.GetIO().MousePos) != OverlayHit.None));

    private void SetSaberOverlayVisible(bool visible)
    {
        _showSaberOverlay = visible;
        _overlayLayoutAvailable = false;
        if (!visible) ResetOverlaySwing();
    }

    private void DrawSaberOverlay()
    {
        if (!OverlayVisible)
        {
            _overlayLayoutAvailable = false;
            ResetOverlaySwing();
            return;
        }
        try
        {
            var io = ImGui.GetIO();
            if (io.AppFocusLost) OverlayFocusLost();
            if (_overlayDrag.Held)
            {
                // Overlay-owned buttons never enter ImGui, so its MouseDown is deliberately false.
                if (!_overlayPointerCapture.IsHeld((int)GlfwMouseButton.Left)) _overlayDrag.Release();
                else _overlayDrag.Move(io.MousePos);
            }

            var viewport = ImGui.GetMainViewport();
            var kittens = OverlayKittens;
            _overlayLayout = OverlayLayout.Create(viewport.WorkPos, viewport.WorkSize, ImGui.GetFontSize(), kittens.Count, OverlayMenuBarInset(viewport));
            _overlayLayoutAvailable = _overlayLayout.Valid;
            if (!_overlayLayoutAvailable) return;
            var layout = _overlayLayout;
            _overlayListScroll = Math.Clamp(_overlayListScroll, 0, Math.Max(0, kittens.Count * layout.RowHeight - layout.ListBodyHeight));
            var draw = ImGui.GetForegroundDrawList(viewport);
            var white = new ImColor8(255, 255, 255, 255);
            var muted = new ImColor8(195, 200, 210, 255);
            draw.PushClipRect(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize);
            try
            {
                // Only these fills are surfaces: each uses exactly 50% alpha, with no window chrome.
                draw.AddRectFilled(layout.ListMin, layout.ListMax, new ImColor8(0, 0, 0, 128));
                draw.AddText(layout.ListMin + new float2(layout.Padding, layout.Padding), white, "EVA lightsabers");
                if (kittens.Count == 0)
                    draw.AddText(new float2(layout.ListMin.X + layout.Padding, layout.ListBodyTop), muted, "No EVA kittens");
                draw.PushClipRect(new(layout.ListMin.X, layout.ListBodyTop), new(layout.ListMax.X, layout.ListBodyTop + layout.ListBodyHeight), true);
                try
                {
                    int first = Math.Max(0, (int)(_overlayListScroll / layout.RowHeight));
                    int last = Math.Min(kittens.Count, first + (int)MathF.Ceiling(layout.ListBodyHeight / layout.RowHeight) + 1);
                    for (int index = first; index < last; index++)
                    {
                        var kitten = kittens[index];
                        if (kitten.IsDisposed) continue;
                        float y = layout.ListBodyTop + index * layout.RowHeight - _overlayListScroll;
                        var checkMin = new float2(layout.ListMin.X + layout.Padding, y + layout.RowHeight * .18f);
                        float box = layout.RowHeight * .62f;
                        draw.AddRect(checkMin, checkMin + new float2(box), muted, 0, ImDrawFlags.None, 1);
                        if (SaberEnabledFor(kitten))
                        {
                            draw.AddLine(checkMin + new float2(box * .18f, box * .52f), checkMin + new float2(box * .42f, box * .76f), white, 2);
                            draw.AddLine(checkMin + new float2(box * .42f, box * .76f), checkMin + new float2(box * .85f, box * .20f), white, 2);
                        }
                        draw.AddText(new(checkMin.X + box + layout.Padding, y + layout.RowHeight * .12f),
                            ReferenceEquals(kitten, Program.ControlledVehicle) ? white : muted, KittenLabel(kitten));
                    }
                }
                finally { draw.PopClipRect(); }
                if (layout.ListFooterHeight > 0)
                    draw.AddText(new(layout.ListMin.X + layout.Padding, layout.ListMax.Y - layout.ListFooterHeight), muted, "Scroll for more kittens");

                draw.AddCircleFilled(layout.SwingCenter, layout.SwingRadius, new ImColor8(0, 0, 0, 128), 64);
                draw.AddCircleFilled(layout.SliceCenter, layout.SliceRadius, new ImColor8(224, 24, 24, 128), 48);
                var swingText = OverlayCanStartSwing ? white : muted;
                CenterOverlayText(draw, layout.SwingCenter - new float2(0, ImGui.GetFontSize() * .55f), swingText, "SWING");
                CenterOverlayText(draw, layout.SwingCenter + new float2(0, ImGui.GetFontSize() * .65f), swingText, "hold + drag");
                CenterOverlayText(draw, layout.SliceCenter, white, "SLICE");
                // The small reticle moves inside the fixed circle; the control itself never moves.
                if (_overlayDrag.Active)
                {
                    var point = layout.SwingCenter + _overlayDrag.Offset * (layout.SwingRadius * .62f);
                    draw.AddCircle(point, 5 * layout.Scale, white, 16, 1.5f);
                }
                if (SliceStatusVisible && !_windowVisible)
                {
                    float width = layout.ListMax.X - layout.ListMin.X;
                    var position = new float2(layout.ListMax.X - width, layout.SwingCenter.Y + layout.SwingRadius + layout.Padding);
                    float availableHeight = viewport.WorkPos.Y + viewport.WorkSize.Y - position.Y - layout.Padding;
                    var measured = ImGui.CalcTextSize(_sliceStatus, false, width);
                    // Long failure messages can widen to the left instead of disappearing below the screen.
                    if (availableHeight > 0 && measured.Y > availableHeight)
                    {
                        width = Math.Min(viewport.WorkSize.X - layout.Padding * 2, width * measured.Y / availableHeight);
                        position.X = layout.ListMax.X - width;
                    }
                    draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), position + new float2(1), new ImColor8(0, 0, 0, 255), _sliceStatus, width);
                    draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), position, white, _sliceStatus, width);
                }
            }
            finally { draw.PopClipRect(); }
        }
        catch (Exception error)
        {
            ResetOverlaySwing();
            _overlayLayoutAvailable = false;
            Report("Saber overlay failed", error);
        }
    }

    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text) =>
        draw.AddText(center - ImGui.CalcTextSize(text) * .5f, color, text);

    private static float OverlayMenuBarInset(ImGuiViewportPtr viewport)
    {
        var bar = ImGui.Internal.FindWindowByName("Menu Bar"u8);
        if (bar.IsNull() || (!bar.Active && !bar.WasActive)) return 0;
        float bottom = bar.Pos.Y + bar.Size.Y - viewport.WorkPos.Y;
        return float.IsFinite(bottom) && bottom > 0 ? bottom + ImGui.GetFontSize() * .5f : 0;
    }

    /// <summary>Called from Program.OnMouseButton before its viewport latch or game dispatch.</summary>
    internal bool FilterOverlayMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action)
    {
        if (action == GlfwButtonAction.Release
            && _overlayPointerCapture.Consume((int)button, false, false, false, Program.FrameNumber))
        {
            if (button == GlfwMouseButton.Left) _overlayDrag.Release();
            return true;
        }
        if (action != GlfwButtonAction.Press || window != Program.GetWindow() || !OverlayVisible || !_overlayLayoutAvailable
            || Program.IsModalOpen() || Program.HasMouseInputLatch) return false;
        var pointer = OverlayPointer(window);
        var hit = _overlayLayout.Hit(pointer);
        if (!_overlayPointerCapture.Consume((int)button, true, hit != OverlayHit.None, Program.HasMouseInputLatch, Program.FrameNumber)) return false;
        if (button != GlfwMouseButton.Left) return true;

        switch (hit)
        {
            case OverlayHit.Kittens:
                if (_fault != null || pointer.Y < _overlayLayout.ListBodyTop
                    || pointer.Y >= _overlayLayout.ListBodyTop + _overlayLayout.ListBodyHeight) break;
                int row = (int)((pointer.Y - _overlayLayout.ListBodyTop + _overlayListScroll) / _overlayLayout.RowHeight);
                var kittens = OverlayKittens;
                if ((uint)row < (uint)kittens.Count && !kittens[row].IsDisposed)
                    QueueKittenSaber(kittens[row], !SaberEnabledFor(kittens[row]));
                break;
            case OverlayHit.Swing:
                if (OverlayCanStartSwing && _overlayDrag.Begin(pointer, _overlayLayout.SwingRadius,
                    _pose.GripOffsetFromChestCm, _pose.BladeDirectionModel))
                {
                    _overlayDragOwner = _owner;
                    _overlayDragPose = _pose;
                }
                break;
            case OverlayHit.Slice:
                ResetOverlaySwing();
                if (_fault == null && _owner is { IsDisposed: false } && ReferenceEquals(_owner, Program.ControlledVehicle))
                    Queue(Command.SelectSlice);
                break;
        }
        return true;
    }

    /// <summary>Keep held drags, including movement outside the circle, away from camera controllers.</summary>
    internal bool FilterOverlayCursor(GlfwWindow window)
    {
        if (!_overlayPointerCapture.AnyHeld) return false;
        if (_overlayDrag.Held) _overlayDrag.Move(OverlayPointer(window));
        return true;
    }

    /// <summary>Called before the backend queues a button that would activate a covered ImGui widget.</summary>
    internal bool SuppressOverlayImGuiMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action)
    {
        if (!_overlayPointerCapture.SuppressImGuiButton((int)button, action == GlfwButtonAction.Press, Program.FrameNumber)) return false;
        if (button == GlfwMouseButton.Left && action == GlfwButtonAction.Release) _overlayDrag.Release();
        return true;
    }

    internal void OverlayFocusLost()
    {
        ResetOverlaySwing();
        _overlayPointerCapture.Reset();
    }

    internal bool FilterOverlayScroll(GlfwWindow window, double2 offset)
    {
        if (!OverlayOwnsScroll(window, out var hit)) return false;
        if (hit == OverlayHit.Kittens && double.IsFinite(offset.Y))
            _overlayListScroll = Math.Clamp(_overlayListScroll - (float)offset.Y * _overlayLayout.RowHeight * 3,
                0, Math.Max(0, OverlayKittens.Count * _overlayLayout.RowHeight - _overlayLayout.ListBodyHeight));
        return true;
    }

    // This query shares the raw scroll decision without applying the list scroll a second time.
    internal bool SuppressOverlayImGuiScroll(GlfwWindow window) => OverlayOwnsScroll(window, out _);

    private bool OverlayOwnsScroll(GlfwWindow window, out OverlayHit hit)
    {
        hit = OverlayHit.None;
        if (window != Program.GetWindow() || !OverlayVisible || !_overlayLayoutAvailable || Program.IsModalOpen()) return false;
        hit = _overlayLayout.Hit(OverlayPointer(window));
        return _overlayPointerCapture.OwnsScroll(hit != OverlayHit.None);
    }

    private static float2 OverlayPointer(GlfwWindow window)
    {
        var pointer = window.CursorPos;
        if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        {
            pointer.X += window.X;
            pointer.Y += window.Y;
        }
        return new((float)pointer.X, (float)pointer.Y);
    }

    private void ApplyOverlaySwing(double playerDt)
    {
        if (!OverlayVisible || !OverlayCanSwing || !ReferenceEquals(_overlayDragOwner, _owner)
            || !ReferenceEquals(_overlayDragPose, _pose))
        {
            ResetOverlaySwing();
            return;
        }
        // Preserve the displayed offset while paused or outside supported warp, including a return.
        if (!_overlayDrag.Advance(SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) ? playerDt : 0,
            out var grip, out var aim)) return;
        _pose.GripOffsetFromChestCm = grip;
        _pose.BladeDirectionModel = aim;
        if (!_overlayDrag.Active) { _overlayDragOwner = null; _overlayDragPose = null; }
    }

    private void ResetOverlaySwing()
    {
        if (_overlayDrag.Active && _overlayDragPose != null)
        {
            _overlayDragPose.GripOffsetFromChestCm = _overlayDrag.OriginalGrip;
            _overlayDragPose.BladeDirectionModel = _overlayDrag.OriginalAim;
        }
        _overlayDrag.Reset();
        _overlayDragOwner = null;
        _overlayDragPose = null;
    }

}
