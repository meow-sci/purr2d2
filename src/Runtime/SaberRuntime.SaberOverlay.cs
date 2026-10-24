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
    private int _overlayUiButtons;
    private ulong _overlayUiEventFrame = ulong.MaxValue;

    private bool KittenColorPickerVisible => OverlayVisible && (_kittenColorPickerRequested || _kittenColorPickerOpen)
        && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);

    private bool KittenColorPickerContainsPointer(float2 pointer) => KittenColorPickerVisible
        && _kittenColorPickerBoundsAvailable && pointer.X >= _kittenColorPickerMin.X && pointer.X <= _kittenColorPickerMax.X
        && pointer.Y >= _kittenColorPickerMin.Y && pointer.Y <= _kittenColorPickerMax.Y;

    private bool OverlayVisible => _enabled && _showSaberOverlay && _ready && !_disposed && Program.DrawUI
        && !Program.EditorFlag && Universe.CurrentSystem != null && Program.MainViewport.Visible;
    private bool OverlayCanSwing => _fault == null && _owner is { IsDisposed: false }
        && ReferenceEquals(_owner, Program.ControlledVehicle) && _blade.Equipped && _pose.Eligible
        && !_selectingSlice && (!_sliceSequence.Active || _sliceSequence.EndPresented);
    private bool OverlayCanStartSwing => OverlayCanSwing && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed());

    // The picker runs after this overlay; retain consumption for quick press/release pairs too.
    internal bool OverlayCapturesPointer => OverlayVisible && (ForcePicking || KittenColorPickerContainsPointer(ImGui.GetIO().MousePos) || SettingsContainsPointer(ImGui.GetIO().MousePos) || _overlayUiButtons != 0
        || _overlayUiEventFrame == Program.FrameNumber || _overlayPointerCapture.AnyHeld
        || _overlayPointerCapture.EventFrame == Program.FrameNumber
        || (_overlayLayoutAvailable && ImGui.GetIO().MouseHoveredViewport == ImGui.GetMainViewport().ID
            && (_overlayLayout.Hit(ImGui.GetIO().MousePos) != OverlayHit.None
                || (_forceLayoutAvailable && _forceLayout.Hit(ImGui.GetIO().MousePos) != ForceOverlayHit.None))));

    private void SetSaberOverlayVisible(bool visible)
    {
        _showSaberOverlay = visible;
        _overlayLayoutAvailable = false;
        if (!visible) { ResetOverlaySwing(); ResetForceGestures(); DismissKittenColorPicker(); }
    }

    private void DrawSaberOverlay()
    {
        if (!OverlayVisible)
        {
            DrawKittenColorPicker();
            _overlayLayoutAvailable = false;
            ResetOverlaySwing();
            ResetForceGestures();
            return;
        }
        try
        {
            var io = ImGui.GetIO();
            if (io.AppFocusLost) OverlayFocusLost();
            if (_overlayDrag.Held)
            {
                // Overlay-owned buttons never enter ImGui, so its MouseDown is deliberately false.
                if (!_overlayPointerCapture.IsHeld((int)GlfwMouseButton.Left)) ReleaseManualSwing();
                else _overlayDrag.Move(io.MousePos);
            }

            var viewport = ImGui.GetMainViewport();
            var kittens = OverlayKittens;
            float menuInset = OverlayMenuBarInset(viewport);
            var baseLayout = OverlayLayout.Create(viewport.WorkPos, viewport.WorkSize, ImGui.GetFontSize(), kittens.Count,
                menuInset, ShowSomersaultButtons);
            _overlayLayout = baseLayout.WithControlsOffset(viewport.WorkPos, viewport.WorkSize,
                new(_settings.SaberControlsOffsetX, _settings.SaberControlsOffsetY), menuInset);
            _overlayLayoutAvailable = _overlayLayout.Valid;
            if (!_overlayLayoutAvailable) { DismissKittenColorPicker(); ResetForceGestures(); return; }
            var layout = _overlayLayout;
            _forceLayout = ForceOverlayLayout.Create(baseLayout, viewport.WorkPos, viewport.WorkSize)
                .WithControlsOffset(viewport.WorkPos, viewport.WorkSize,
                    new(_settings.ForceControlsOffsetX, _settings.ForceControlsOffsetY), menuInset);
            _forceLayoutAvailable = _forceLayout.Valid;
            bool canSomersault = CanStartSomersault(out var somersaultReason);
            float fontSize = Math.Min(ImGui.GetFontSize(), 16 * layout.Scale);
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
                    DrawForceOverlay(draw);
                    // List/swing/slice retain 50% alpha; blue action buttons use 25% below.
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
                            bool selected = KittenColorPickerVisible && ReferenceEquals(_kittenColorPickerOwner, kitten);
                            draw.AddRect(swatch, swatch + new float2(layout.ColorSwatchSize), selected ? white : muted,
                                0, ImDrawFlags.None, selected ? 2 : 1);
                            var cue = color.X * .2126f + color.Y * .7152f + color.Z * .0722f > .55f
                                ? new ImColor8(10, 10, 15, 255) : white;
                            float size = layout.ColorSwatchSize;
                            CenterOverlayText(draw, swatch + new float2(size * .5f), cue,
                                SaberTypeFor(kitten) == SaberType.Dark ? "D" : "L", size * .8f, size * .65f);
                        }
                    }
                    finally { draw.PopClipRect(); }
                    if (layout.ListFooterHeight > layout.SettingsFooterHeight)
                        draw.AddText(new(layout.ListMin.X + layout.Padding, layout.ListBodyTop + layout.ListBodyHeight), muted, "Scroll for more kittens");
                    draw.AddRectFilled(layout.SettingsMin, layout.SettingsMax, new ImColor8(45, 50, 60, 210));
                    draw.AddRect(layout.SettingsMin, layout.SettingsMax, _windowVisible ? white : muted);
                    CenterOverlayText(draw, (layout.SettingsMin + layout.SettingsMax) * .5f, white, "Settings",
                        layout.SettingsMax.X - layout.SettingsMin.X - layout.Padding * 2, fontSize);

                    draw.AddCircleFilled(layout.SwingCenter, layout.SwingRadius, new ImColor8(0, 0, 0, 128), 64);
                    draw.AddCircleFilled(layout.SliceCenter, layout.SliceRadius, new ImColor8(224, 24, 24, 128), 48);
                    draw.AddCircleFilled(layout.IgniteCenter, layout.IgniteRadius, new ImColor8(0, 0, 255, 64), 48);
                    var toggleKitten = AppearanceKitten;
                    var toggleText = toggleKitten != null && _fault == null ? white : muted;
                    draw.AddCircle(layout.IgniteCenter, layout.IgniteRadius, new ImColor8(100, 160, 255, 255), 48,
                        Math.Max(.5f, layout.Scale));
                    CenterOverlayText(draw, layout.IgniteCenter, toggleText,
                        toggleKitten != null && SaberEnabledFor(toggleKitten) ? "RETRACT" : "IGNITE", layout.IgniteRadius * 1.6f);
                    var swingText = OverlayCanStartSwing ? white : muted;
                    CenterOverlayText(draw, layout.SwingCenter - new float2(0, fontSize * .55f), swingText, "SWING", layout.SwingRadius * 1.6f, fontSize);
                    CenterOverlayText(draw, layout.SwingCenter + new float2(0, fontSize * .65f), swingText, "hold + drag", layout.SwingRadius * 1.6f, fontSize);
                    CenterOverlayText(draw, layout.SliceCenter, SomersaultActive && !_selectingSlice ? muted : white,
                        _selectingSlice ? "CANCEL" : "SLICE", layout.SliceRadius * 1.6f);
                    if (layout.ShowSomersaultButtons)
                    {
                        DrawSomersaultButton(draw, layout.ForwardCenter, layout.SomersaultRadius, new(0, -1), "FWD", canSomersault);
                        DrawSomersaultButton(draw, layout.LeftCenter, layout.SomersaultRadius, new(-1, 0), "LEFT", canSomersault);
                        DrawSomersaultButton(draw, layout.RightCenter, layout.SomersaultRadius, new(1, 0), "RIGHT", canSomersault);
                        DrawSomersaultButton(draw, layout.BackwardCenter, layout.SomersaultRadius, new(0, 1), "BACK", canSomersault);
                    }
                    // The small reticle moves inside the fixed circle; the control itself never moves.
                    if (_overlayDrag.Active)
                    {
                        var point = layout.SwingIndicator(_overlayDrag.Offset);
                        draw.AddCircle(point, layout.SwingMarkerRadius, white, 16, 1.5f);
                    }
                    string status = SliceStatusVisible ? _sliceStatus
                        : layout.ShowSomersaultButtons && !canSomersault ? somersaultReason : "";
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        float width = layout.StatusMax.X - layout.StatusMin.X;
                        var position = layout.StatusMin;
                        float availableHeight = layout.StatusMax.Y - position.Y;
                        var measured = ImGui.CalcTextSize(status, false, width * ImGui.GetFontSize() / fontSize)
                            * (fontSize / ImGui.GetFontSize());
                        // Long failure messages can widen to the left instead of disappearing below the screen.
                        if (availableHeight > 0 && measured.Y > availableHeight)
                        {
                            width = Math.Min(layout.StatusMax.X - viewport.WorkPos.X, width * measured.Y / availableHeight);
                            position.X = layout.StatusMax.X - width;
                        }
                        draw.PushClipRect(new(viewport.WorkPos.X, layout.StatusMin.Y), layout.StatusMax, true);
                        try
                        {
                            draw.AddText(ImGui.GetFont(), fontSize, position + new float2(layout.Scale), new ImColor8(0, 0, 0, 255), status, width);
                            draw.AddText(ImGui.GetFont(), fontSize, position, white, status, width);
                        }
                        finally { draw.PopClipRect(); }
                    }
                }
                finally { draw.PopClipRect(); }
            }
            var viewportMax = viewport.WorkPos + viewport.WorkSize;
            // Foreground controls must leave native windows unobscured.
            void DrawUncovered(float2 clipMin, float2 clipMax, int window)
            {
                if (clipMin.X >= clipMax.X || clipMin.Y >= clipMax.Y) return;
                if (window == 2) { DrawControls(clipMin, clipMax); return; }
                bool visible = window == 0 ? _kittenColorPickerBoundsAvailable : _settingsWindowBoundsAvailable && _windowVisible;
                var min = window == 0 ? _kittenColorPickerMin : _settingsWindowMin;
                var max = window == 0 ? _kittenColorPickerMax : _settingsWindowMax;
                min = new(Math.Max(min.X, clipMin.X), Math.Max(min.Y, clipMin.Y));
                max = new(Math.Min(max.X, clipMax.X), Math.Min(max.Y, clipMax.Y));
                if (!visible || min.X >= max.X || min.Y >= max.Y)
                { DrawUncovered(clipMin, clipMax, window + 1); return; }
                DrawUncovered(clipMin, new(clipMax.X, min.Y), window + 1);
                DrawUncovered(new(clipMin.X, max.Y), clipMax, window + 1);
                DrawUncovered(new(clipMin.X, min.Y), new(min.X, max.Y), window + 1);
                DrawUncovered(new(max.X, min.Y), new(clipMax.X, max.Y), window + 1);
            }
            DrawUncovered(viewport.WorkPos, viewportMax, 0);
        }
        catch (Exception error)
        {
            ResetOverlaySwing();
            ResetForceGestures();
            _overlayLayoutAvailable = false;
            Report("Saber overlay failed", error);
        }
    }

    private static void CenterOverlayText(ImDrawListPtr draw, float2 center, ImColor8 color, string text,
        float maxWidth = float.MaxValue, float fontSize = 0)
    {
        var size = ImGui.CalcTextSize(text);
        float scale = fontSize > 0 ? Math.Min(1, fontSize / ImGui.GetFontSize()) : 1;
        if (size.X > 0) scale = Math.Min(scale, maxWidth / size.X);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - size * (scale * .5f), color, text);
    }

    private static void DrawSomersaultButton(ImDrawListPtr draw, float2 center, float radius, float2 direction,
        string label, bool available)
    {
        // Disabled controls retain the requested fill, and still own their visible circle's clicks.
        draw.AddCircleFilled(center, radius, new ImColor8(0, 0, 255, 64), 48);
        var color = available ? new ImColor8(210, 230, 255, 255) : new ImColor8(135, 145, 165, 255);
        draw.AddCircle(center, radius, color, 48, Math.Max(.5f, radius / 27));
        var arrow = center - new float2(0, radius * .24f);
        var tip = arrow + direction * radius * .25f;
        var tail = arrow - direction * radius * .25f;
        var normal = new float2(-direction.Y, direction.X);
        float thickness = Math.Max(.5f, radius / 18);
        draw.AddLine(tail, tip, color, thickness);
        draw.AddLine(tip, tip - direction * radius * .21f + normal * radius * .18f, color, thickness);
        draw.AddLine(tip, tip - direction * radius * .21f - normal * radius * .18f, color, thickness);
        CenterOverlayText(draw, center + new float2(0, radius * .35f), color, label, radius * 1.65f, radius * .48f);
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
            if (button == GlfwMouseButton.Left) { ReleaseManualSwing(); _forceDrag.Release(); }
            return true;
        }
        // Native settings/picker gestures belong to ImGui. Consume only the game's route;
        // the backend must receive the same presses, releases and motion normally.
        if ((uint)button < 32)
        {
            int bit = 1 << (int)button;
            if (action == GlfwButtonAction.Release && (_overlayUiButtons & bit) != 0)
            {
                _overlayUiButtons &= ~bit;
                _overlayUiEventFrame = Program.FrameNumber;
                return true;
            }
            if (action == GlfwButtonAction.Press && !_overlayPointerCapture.AnyHeld && window == Program.GetWindow()
                && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)) || _overlayUiButtons != 0)
                && !Program.HasMouseInputLatch)
            {
                _overlayUiButtons |= bit;
                _overlayUiEventFrame = Program.FrameNumber;
                return true;
            }
        }
        if (action != GlfwButtonAction.Press || window != Program.GetWindow() || !OverlayVisible || !_overlayLayoutAvailable
            || Program.IsModalOpen() || Program.HasMouseInputLatch) return false;
        var pointer = OverlayPointer(window);
        var hit = _overlayLayout.Hit(pointer);
        // Saber/list controls are drawn after Force and take priority if custom positions overlap.
        var forceHit = hit == OverlayHit.None && _forceLayoutAvailable ? _forceLayout.Hit(pointer) : ForceOverlayHit.None;
        var io = ImGui.GetIO();
        bool forcePick = ForcePicking && hit == OverlayHit.None && forceHit == ForceOverlayHit.None
            && !io.WantCaptureMouse && !io.WantTextInput && CursorTarget.IsHitTestViewport(Program.MainViewport);
        if (!_overlayPointerCapture.Consume((int)button, true,
            hit != OverlayHit.None || forceHit != ForceOverlayHit.None || forcePick, Program.HasMouseInputLatch,
            Program.FrameNumber, out bool firstPress)) return false;
        if (!firstPress) return true;
        if (ForcePicking && button == GlfwMouseButton.Right)
        { HandleForcePickerPress(button, pointer); return true; }
        if (forcePick) { HandleForcePickerPress(button, pointer); return true; }
        if (button != GlfwMouseButton.Left) return true;
        if (forceHit != ForceOverlayHit.None) return HandleForceOverlayPress(forceHit, pointer);

        switch (hit)
        {
            case OverlayHit.Settings:
                ToggleSettingsWindow();
                break;
            case OverlayHit.Kittens:
                if (_fault != null || pointer.Y < _overlayLayout.ListBodyTop
                    || pointer.Y >= _overlayLayout.ListBodyTop + _overlayLayout.ListBodyHeight) break;
                int row = (int)((pointer.Y - _overlayLayout.ListBodyTop + _overlayListScroll) / _overlayLayout.RowHeight);
                var kittens = OverlayKittens;
                if ((uint)row < (uint)kittens.Count && !kittens[row].IsDisposed)
                {
                    if (_overlayLayout.IsColorSwatch(pointer, row, _overlayListScroll))
                    {
                        if (KittenColorPickerVisible && ReferenceEquals(_kittenColorPickerOwner, kittens[row]))
                            DismissKittenColorPicker();
                        else
                        {
                            ResetOverlaySwing();
                            ResetForceGestures();
                            _kittenColorPickerOwner = kittens[row];
                            _kittenColorPickerAnchor = new(_overlayLayout.ListMin.X - _overlayLayout.Padding, pointer.Y);
                            _kittenColorPickerRequested = true;
                        }
                    }
                    else QueueKittenSaber(kittens[row], !SaberEnabledFor(kittens[row]));
                }
                break;
            case OverlayHit.Swing:
                if (OverlayCanStartSwing && _overlayDrag.Begin(pointer, _overlayLayout.SwingTravelRadius,
                    _pose.GripOffsetFromChestCm, _pose.BladeDirectionModel))
                {
                    BeginManualSwing();
                    _overlayDragOwner = _owner;
                    _overlayDragPose = _pose;
                }
                break;
            case OverlayHit.Slice:
                ResetOverlaySwing();
                if ((!SomersaultActive || _selectingSlice) && _fault == null && _owner is { IsDisposed: false }
                    && ReferenceEquals(_owner, Program.ControlledVehicle))
                    Queue(Command.SelectSlice);
                break;
            case OverlayHit.Ignite:
                QueueControlledSaberToggle();
                break;
            case OverlayHit.SomersaultForward: TryQueueOverlaySomersault(SomersaultDirection.Forward); break;
            case OverlayHit.SomersaultBackward: TryQueueOverlaySomersault(SomersaultDirection.Backward); break;
            case OverlayHit.SomersaultLeft: TryQueueOverlaySomersault(SomersaultDirection.Left); break;
            case OverlayHit.SomersaultRight: TryQueueOverlaySomersault(SomersaultDirection.Right); break;
        }
        return true;
    }

    private void TryQueueOverlaySomersault(SomersaultDirection direction)
    {
        // Revalidate after drawing: controlled kitten/contact may have changed since that frame.
        if (!ShowSomersaultButtons || !CanStartSomersault(out _)) return;
        QueueSomersault(direction);
    }

    /// <summary>Keep held drags, including movement outside the circle, away from camera controllers.</summary>
    internal bool FilterOverlayCursor(GlfwWindow window)
    {
        if (_overlayUiButtons != 0) return true;
        if (ForcePicking) return true;
        if (!_overlayPointerCapture.AnyHeld) return !Program.HasMouseInputLatch
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)));
        if (_overlayDrag.Held) _overlayDrag.Move(OverlayPointer(window));
        if (_forceDrag.Held) MoveForcePointer(OverlayPointer(window));
        return true;
    }

    /// <summary>Called before the backend queues a button that would activate a covered ImGui widget.</summary>
    internal bool SuppressOverlayImGuiMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action)
    {
        if (!_overlayPointerCapture.SuppressImGuiButton((int)button, action == GlfwButtonAction.Press, Program.FrameNumber)) return false;
        if (button == GlfwMouseButton.Left && action == GlfwButtonAction.Release)
        { ReleaseManualSwing(); _forceDrag.Release(); }
        return true;
    }

    internal void OverlayFocusLost()
    {
        ResetOverlaySwing();
        ResetForceGestures();
        _forceEscapeHeld = false;
        _overlayPointerCapture.Reset();
        _overlayUiButtons = 0;
        _overlayUiEventFrame = ulong.MaxValue;
        DismissKittenColorPicker();
    }

    internal bool FilterOverlayScroll(GlfwWindow window, double2 offset)
    {
        if (Program.HasMouseInputLatch) return false;
        if (window == Program.GetWindow() && !_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)) || _overlayUiButtons != 0)) return true;
        if (window == Program.GetWindow() && OverlayVisible && !Program.IsModalOpen()
            && !Program.HasMouseInputLatch && double.IsFinite(offset.Y)
            && (_forceDrag.Held || (!_overlayPointerCapture.AnyHeld && _overlayLayoutAvailable && _forceLayoutAvailable
                && _overlayLayout.Hit(OverlayPointer(window)) == OverlayHit.None
                && _forceLayout.Hit(OverlayPointer(window)) == ForceOverlayHit.Move)))
        {
            ScrollForcePointer(offset.Y);
            return true;
        }
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
        if (Program.HasMouseInputLatch) return false;
        if (!_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)) || _overlayUiButtons != 0)) return false;
        if (window != Program.GetWindow() || !OverlayVisible || !_overlayLayoutAvailable || Program.IsModalOpen()) return false;
        hit = _overlayLayout.Hit(OverlayPointer(window));
        return _overlayPointerCapture.OwnsScroll(ForcePicking || hit != OverlayHit.None
            || (_forceLayoutAvailable && _forceLayout.Hit(OverlayPointer(window)) != ForceOverlayHit.None));
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
        _kittenColorPickerOwner = null;
        _kittenColorPickerRequested = false;
        _kittenColorPickerOpen = false;
        _kittenColorPickerBoundsAvailable = false;
    }

    private void DrawKittenColorPicker()
    {
        _kittenColorPickerBoundsAvailable = false;
        bool valid = OverlayVisible && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);
        if (!valid) { DismissKittenColorPicker(); return; }
        if (_kittenColorPickerRequested)
        {
            _kittenColorPickerOpen = true;
            _kittenColorPickerRequested = false;
        }
        if (!_kittenColorPickerOpen) return;
        var viewport = ImGui.GetMainViewport();
        float scale = Math.Clamp(ImGui.GetFontSize() / 16, .75f, 2);
        float topInset = OverlayMenuBarInset(viewport);
        float width = Math.Min(440 * scale, viewport.WorkSize.X - 16);
        float height = Math.Min(690 * scale, viewport.WorkSize.Y - topInset - 16);
        var minimum = viewport.WorkPos + new float2(8, topInset + 8);
        var maximum = minimum + new float2(Math.Max(0, viewport.WorkSize.X - width - 16),
            Math.Max(0, viewport.WorkSize.Y - topInset - height - 16));
        var position = new float2(Math.Clamp(_kittenColorPickerAnchor.X - width, minimum.X, maximum.X),
            Math.Clamp(_kittenColorPickerAnchor.Y, minimum.Y, maximum.Y));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new float2(Math.Min(320 * scale, width), Math.Min(240 * scale, height)),
            new float2(float.MaxValue));
        bool draw = ImGui.Begin($"Purr2D2 | {KittenLabel(_kittenColorPickerOwner!)} saber###Purr2D2KittenSaber",
            ref _kittenColorPickerOpen);
        try
        {
            _kittenColorPickerMin = ImGui.GetWindowPos();
            _kittenColorPickerMax = _kittenColorPickerMin + ImGui.GetWindowSize();
            _kittenColorPickerBoundsAvailable = _kittenColorPickerOpen;
            if (!draw) return;
            var owner = _kittenColorPickerOwner!;
            var type = SaberTypeFor(owner);
            ImGui.Text("Saber type");
            if (ImGui.RadioButton("Light", type == SaberType.Light))
            {
                SetKittenSaberType(owner, SaberType.Light);
                type = SaberType.Light;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Dark", type == SaberType.Dark))
            {
                SetKittenSaberType(owner, SaberType.Dark);
                type = SaberType.Dark;
            }
            ImGui.TextWrapped("This kitten's color and type last for this session.");
            ImGui.Text("Color");
            ImGui.SetNextItemWidth(Math.Min(220 * scale, ImGui.GetContentRegionAvail().X));
            float3 color = SaberColorFor(owner);
            if (ImGui.ColorPicker3("##SaberColor", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha
                | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.InputRGB))
                SetKittenSaberColor(owner, color);
            DrawSaberEffectsSettings(type);
            ImGui.Separator();
            DrawSavePreferences();
        }
        finally
        {
            ImGui.End();
            if (!_kittenColorPickerOpen) DismissKittenColorPicker();
        }
    }
}
