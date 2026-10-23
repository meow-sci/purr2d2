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
    private KittenEva? _kittenColorPickerOwner;
    private bool _kittenColorPickerRequested, _kittenColorPickerOpen, _kittenColorPickerBoundsAvailable;
    private float2 _kittenColorPickerAnchor, _kittenColorPickerMin, _kittenColorPickerMax;
    private int _kittenColorPickerButtons;
    private ImGuiID _kittenColorPickerId;
    private ulong _kittenColorPickerEventFrame = ulong.MaxValue;

    private bool KittenColorPickerVisible => OverlayVisible && (_kittenColorPickerRequested || _kittenColorPickerOpen)
        && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);

    private bool OverlayVisible => _showSaberOverlay && _ready && !_disposed && Program.DrawUI
        && !Program.EditorFlag && Universe.CurrentSystem != null && Program.MainViewport.Visible;
    private bool OverlayCanSwing => _fault == null && _owner is { IsDisposed: false }
        && ReferenceEquals(_owner, Program.ControlledVehicle) && _blade.Equipped && _pose.Eligible
        && !_selectingSlice && (!_sliceSequence.Active || _sliceSequence.EndPresented);
    private bool OverlayCanStartSwing => OverlayCanSwing && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed());

    // The picker runs after this overlay; retain consumption for quick press/release pairs too.
    internal bool OverlayCapturesPointer => OverlayVisible && (KittenColorPickerVisible || _kittenColorPickerButtons != 0
        || _kittenColorPickerEventFrame == Program.FrameNumber || _overlayPointerCapture.AnyHeld
        || _overlayPointerCapture.EventFrame == Program.FrameNumber
        || (_overlayLayoutAvailable && ImGui.GetIO().MouseHoveredViewport == ImGui.GetMainViewport().ID
            && _overlayLayout.Hit(ImGui.GetIO().MousePos) != OverlayHit.None));

    private void SetSaberOverlayVisible(bool visible)
    {
        _showSaberOverlay = visible;
        _overlayLayoutAvailable = false;
        if (!visible) { ResetOverlaySwing(); DismissKittenColorPicker(); }
    }

    private void DrawSaberOverlay()
    {
        if (!OverlayVisible)
        {
            DrawKittenColorPicker();
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
            DrawKittenColorPicker();
            var draw = ImGui.GetForegroundDrawList(viewport);
            var white = new ImColor8(255, 255, 255, 255);
            var muted = new ImColor8(195, 200, 210, 255);
            void DrawControls(float2 clipMin, float2 clipMax)
            {
                if (clipMin.X >= clipMax.X || clipMin.Y >= clipMax.Y) return;
                draw.PushClipRect(clipMin, clipMax);
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
                            var swatch = layout.ColorSwatchMin(index, _overlayListScroll);
                            draw.PushClipRect(new(checkMin.X + box + layout.Padding, y), new(swatch.X - layout.Padding, y + layout.RowHeight), true);
                            try
                            {
                                draw.AddText(new(checkMin.X + box + layout.Padding, y + layout.RowHeight * .12f),
                                    ReferenceEquals(kitten, Program.ControlledVehicle) ? white : muted, KittenLabel(kitten));
                            }
                            finally { draw.PopClipRect(); }
                            var color = SaberColorFor(kitten);
                            draw.AddRectFilled(swatch, swatch + new float2(layout.ColorSwatchSize),
                                new ImColor8((byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255), 255));
                            draw.AddRect(swatch, swatch + new float2(layout.ColorSwatchSize), muted);
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
                    CenterOverlayText(draw, layout.SliceCenter, white, _selectingSlice ? "CANCEL" : "SLICE", layout.SliceRadius * 1.6f);
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
            var viewportMax = viewport.WorkPos + viewport.WorkSize;
            if (_kittenColorPickerBoundsAvailable)
            {
                // Foreground draw lists are above native popups. Cut out the popup
                // rectangle so even a small viewport keeps the picker unobscured.
                var min = new float2(Math.Clamp(_kittenColorPickerMin.X, viewport.WorkPos.X, viewportMax.X),
                    Math.Clamp(_kittenColorPickerMin.Y, viewport.WorkPos.Y, viewportMax.Y));
                var max = new float2(Math.Clamp(_kittenColorPickerMax.X, viewport.WorkPos.X, viewportMax.X),
                    Math.Clamp(_kittenColorPickerMax.Y, viewport.WorkPos.Y, viewportMax.Y));
                DrawControls(viewport.WorkPos, new(viewportMax.X, min.Y));
                DrawControls(new(viewport.WorkPos.X, max.Y), viewportMax);
                DrawControls(new(viewport.WorkPos.X, min.Y), new(min.X, max.Y));
                DrawControls(new(max.X, min.Y), new(viewportMax.X, max.Y));
            }
            else DrawControls(viewport.WorkPos, viewportMax);
        }
        catch (Exception error)
        {
            ResetOverlaySwing();
            _overlayLayoutAvailable = false;
            Report("Saber overlay failed", error);
        }
    }

    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text, float maxWidth = float.MaxValue)
    {
        var size = ImGui.CalcTextSize(text);
        float scale = size.X > 0 ? Math.Min(1, maxWidth / size.X) : 1;
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - size * (scale * .5f), color, text);
    }

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
        // Native picker gestures belong to ImGui. Consume only the game's route;
        // the backend must receive the same presses, releases and motion normally.
        if ((uint)button < 32)
        {
            int bit = 1 << (int)button;
            if (action == GlfwButtonAction.Release && (_kittenColorPickerButtons & bit) != 0)
            {
                _kittenColorPickerButtons &= ~bit;
                _kittenColorPickerEventFrame = Program.FrameNumber;
                return true;
            }
            if (action == GlfwButtonAction.Press && window == Program.GetWindow() && (KittenColorPickerVisible || _kittenColorPickerButtons != 0)
                && !Program.HasMouseInputLatch)
            {
                _kittenColorPickerButtons |= bit;
                _kittenColorPickerEventFrame = Program.FrameNumber;
                return true;
            }
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
                {
                    if (_overlayLayout.IsColorSwatch(pointer, row, _overlayListScroll))
                    {
                        ResetOverlaySwing();
                        _kittenColorPickerOwner = kittens[row];
                        _kittenColorPickerAnchor = new(_overlayLayout.ListMin.X - _overlayLayout.Padding, pointer.Y);
                        _kittenColorPickerRequested = true;
                    }
                    else QueueKittenSaber(kittens[row], !SaberEnabledFor(kittens[row]));
                }
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
        if (KittenColorPickerVisible || _kittenColorPickerButtons != 0) return true;
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
        _kittenColorPickerButtons = 0;
        _kittenColorPickerEventFrame = ulong.MaxValue;
        DismissKittenColorPicker();
    }

    internal bool FilterOverlayScroll(GlfwWindow window, double2 offset)
    {
        if (window == Program.GetWindow() && (KittenColorPickerVisible || _kittenColorPickerButtons != 0)) return true;
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
        if (KittenColorPickerVisible || _kittenColorPickerButtons != 0) return false;
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

    private void DismissKittenColorPicker()
    {
        if (_kittenColorPickerOpen)
        {
            // This also runs while the HUD is hidden or control changes, when no
            // BeginPopup can run. Close only our popup and any child it owns.
            var context = ImGui.GetCurrentContext();
            if (!context.IsNull())
            {
                var popups = context.OpenPopupStack.Span;
                for (int index = 0; index < popups.Length; index++)
                    if (popups[index].PopupId == _kittenColorPickerId)
                    {
                        ImGui.Internal.ClosePopupToLevel(index, false);
                        break;
                    }
            }
        }
        _kittenColorPickerOwner = null;
        _kittenColorPickerRequested = false;
        _kittenColorPickerOpen = false;
        _kittenColorPickerBoundsAvailable = false;
    }

    private void DrawKittenColorPicker()
    {
        const string id = "##Purr2D2KittenColorPicker";
        _kittenColorPickerBoundsAvailable = false;
        bool valid = OverlayVisible && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);
        if (_kittenColorPickerRequested && valid)
        {
            _kittenColorPickerId = ImGui.GetID(id);
            ImGui.OpenPopup(id);
            _kittenColorPickerRequested = false;
        }
        if (!ImGui.IsPopupOpen(id))
        {
            _kittenColorPickerOpen = false;
            DismissKittenColorPicker();
            return;
        }
        // Auto-sizing may hide a popup's first BeginPopup frame. It still owns
        // input during that frame and must already take priority over the rows.
        _kittenColorPickerOpen = true;
        var viewport = ImGui.GetMainViewport();
        float width = Math.Clamp(248 * _overlayLayout.Scale, 150, 320);
        var position = new float2(Math.Max(viewport.WorkPos.X + 8, _kittenColorPickerAnchor.X - width),
            Math.Clamp(_kittenColorPickerAnchor.Y, viewport.WorkPos.Y + 8, viewport.WorkPos.Y + Math.Max(8, viewport.WorkSize.Y - width - 70)));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(position, ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove)) return;
        try
        {
            if (!valid) { ImGui.CloseCurrentPopup(); return; }
            var owner = _kittenColorPickerOwner!;
            ImGui.Text(KittenLabel(owner));
            ImGui.SetNextItemWidth(width);
            float3 color = SaberColorFor(owner);
            if (ImGui.ColorPicker3("##SaberColor", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha
                | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.InputRGB))
                SetKittenSaberColor(owner, color);
            if (ImGui.Button("Done", new float2(-1, 0))) ImGui.CloseCurrentPopup();
            _kittenColorPickerMin = ImGui.GetWindowPos();
            _kittenColorPickerMax = _kittenColorPickerMin + ImGui.GetWindowSize();
            _kittenColorPickerBoundsAvailable = true;
        }
        finally
        {
            ImGui.EndPopup();
            _kittenColorPickerOpen = ImGui.IsPopupOpen(id);
            if (!_kittenColorPickerOpen) DismissKittenColorPicker();
        }
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
