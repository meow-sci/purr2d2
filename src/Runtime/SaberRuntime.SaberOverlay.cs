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
    // Space + arrow keys are the active somersault controls; keep the overlay
    // buttons available in code for easy restoration when desired.
    private const bool SomersaultOverlayControlsEnabled = false;

    private static readonly ImColor8 MovementButtonFill = new(0, 0, 255, 64);
    private static readonly ImColor8 MovementButtonText = new(210, 230, 255, 255);
    private static readonly ImColor8 MovementButtonDisabledText = new(135, 145, 165, 255);

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

    private bool KittenColorPickerVisible => KittenListVisible && (_kittenColorPickerRequested || _kittenColorPickerOpen)
        && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);

    private bool KittenColorPickerContainsPointer(float2 pointer) => KittenColorPickerVisible
        && _kittenColorPickerBoundsAvailable && pointer.X >= _kittenColorPickerMin.X && pointer.X <= _kittenColorPickerMax.X
        && pointer.Y >= _kittenColorPickerMin.Y && pointer.Y <= _kittenColorPickerMax.Y;

    private bool OverlayVisible => _enabled && _showSaberOverlay && _ready && !_disposed && Program.DrawUI
        && !Program.EditorFlag && Universe.CurrentSystem != null && Program.MainViewport.Visible;
    private bool KittenListVisible => OverlayVisible && _settings.KittenListUiEnabled;
    private bool LightsaberUiVisible => OverlayVisible && _settings.LightsaberUiEnabled;
    private bool ForceUiVisible => OverlayVisible && _settings.ForceUiEnabled;
    private bool OverlayCanSwing => _fault == null && _owner is { IsDisposed: false }
        && ReferenceEquals(_owner, Program.ControlledVehicle) && _blade.Equipped && _pose.Eligible
        && !_selectingSlice && (!_sliceSequence.Active || _sliceSequence.EndPresented);
    private bool OverlayCanStartSwing => OverlayCanSwing && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed());

    // The picker runs after this overlay; retain consumption for quick press/release pairs too.
    internal bool OverlayCapturesPointer => OverlayVisible && (ForcePicking || KittenColorPickerContainsPointer(ImGui.GetIO().MousePos) || SettingsContainsPointer(ImGui.GetIO().MousePos) || _overlayUiButtons != 0
        || _overlayUiEventFrame == Program.FrameNumber || _overlayPointerCapture.AnyHeld
        || _overlayPointerCapture.EventFrame == Program.FrameNumber
        || (_overlayLayoutAvailable && !ImGui.GetIO().WantCaptureMouse
            && ImGui.GetIO().MouseHoveredViewport == ImGui.GetMainViewport().ID
            && (_overlayLayout.Hit(ImGui.GetIO().MousePos) != OverlayHit.None
                || (_forceLayoutAvailable && _forceLayout.Hit(ImGui.GetIO().MousePos) != ForceOverlayHit.None))));

    private void SetSaberOverlayVisible(bool visible)
    {
        _showSaberOverlay = visible;
        _overlayLayoutAvailable = false;
        if (!visible) { ResetOverlaySwing(); ResetForceGestures(); DismissKittenColorPicker(); }
    }

    private void SetKittenListUiVisible(bool visible)
    {
        _settings.KittenListUiEnabled = visible;
        _overlayLayout = _overlayLayout with { ShowKittenList = visible };
        if (!visible) DismissKittenColorPicker();
    }

    private void SetLightsaberUiVisible(bool visible)
    {
        _settings.LightsaberUiEnabled = visible;
        _overlayLayout = _overlayLayout with { ShowLightsaberControls = visible };
        if (!visible) ResetOverlaySwing();
    }

    private void SetForceUiVisible(bool visible)
    {
        _settings.ForceUiEnabled = visible;
        if (!visible) ResetForceGestures();
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
                menuInset, ShowSomersaultButtons && SomersaultOverlayControlsEnabled) with
            {
                ShowKittenList = KittenListVisible,
                ShowLightsaberControls = LightsaberUiVisible,
            };
            _overlayLayout = baseLayout.WithControlsOffset(viewport.WorkPos, viewport.WorkSize,
                new(_settings.SaberControlsOffsetX, _settings.SaberControlsOffsetY), menuInset);
            _overlayLayoutAvailable = _overlayLayout.Valid;
            if (!_overlayLayoutAvailable) { DismissKittenColorPicker(); ResetForceGestures(); return; }
            var layout = _overlayLayout;
            _forceLayout = ForceOverlayLayout.Create(baseLayout, viewport.WorkPos, viewport.WorkSize,
                ImGui.GetFontSize(), ImGui.CalcTextSize("200 m/s").X)
                .WithControlsOffset(viewport.WorkPos, viewport.WorkSize,
                    new(_settings.ForceControlsOffsetX, _settings.ForceControlsOffsetY), menuInset);
            _forceLayoutAvailable = ForceUiVisible && _forceLayout.Valid;
            bool canSomersault = CanStartSomersault(out var somersaultReason);
            float fontSize = Math.Min(ImGui.GetFontSize(), 16 * layout.Scale);
            _overlayListScroll = Math.Clamp(_overlayListScroll, 0, Math.Max(0, kittens.Count * layout.RowHeight - layout.ListBodyHeight));
            DrawKittenColorPicker();
            // The viewport background stays above the scene and below every ImGui window.
            var draw = ImGui.GetBackgroundDrawList(viewport);
            var white = new ImColor8(255, 255, 255, 255);
            var muted = new ImColor8(195, 200, 210, 255);
            var dragCircleWhite = new ImColor8(255, 255, 255, 204);
            var dragCircleMuted = new ImColor8(195, 200, 210, 204);
            void DrawControls(float2 clipMin, float2 clipMax)
            {
                if (clipMin.X >= clipMax.X || clipMin.Y >= clipMax.Y) return;
                draw.PushClipRect(clipMin, clipMax);
                try
                {
                    DrawForceOverlay(draw);
                    if (layout.ShowKittenList)
                    {
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
                        if (layout.ListFooterHeight > layout.Padding)
                            draw.AddText(new(layout.ListMin.X + layout.Padding, layout.ListBodyTop + layout.ListBodyHeight), muted, "Scroll for more kittens");
                    }

                    if (layout.ShowLightsaberControls)
                    {
                        draw.AddCircleFilled(layout.SwingCenter, layout.SwingRadius, new ImColor8(0, 0, 0, 128), 64);
                        draw.AddCircle(layout.SwingCenter, layout.SwingRadius, new ImColor8(255, 255, 255, 64), 64,
                            Math.Max(.5f, layout.Scale));
                        draw.AddCircleFilled(layout.SliceCenter, layout.SliceRadius, new ImColor8(224, 24, 24, 128), 48);
                        draw.AddCircleFilled(layout.IgniteCenter, layout.IgniteRadius, MovementButtonFill, 48);
                        var toggleKitten = AppearanceKitten;
                        var toggleText = toggleKitten != null && _fault == null
                            ? MovementButtonText : MovementButtonDisabledText;
                        draw.AddCircle(layout.IgniteCenter, layout.IgniteRadius, new ImColor8(255, 255, 255, 64), 48,
                            Math.Max(.5f, layout.Scale));
                        CenterOverlayText(draw, layout.IgniteCenter, toggleText,
                            toggleKitten != null && SaberEnabledFor(toggleKitten) ? "RETRACT" : "IGNITE", layout.IgniteRadius * 1.6f);
                        var swingText = OverlayCanStartSwing ? dragCircleWhite : dragCircleMuted;
                        CenterOverlayText(draw, layout.SwingCenter - new float2(0, fontSize * .55f), swingText, "SWING", layout.SwingRadius * 1.6f, fontSize);
                        CenterOverlayText(draw, layout.SwingCenter + new float2(0, fontSize * .65f), swingText, "hold + drag", layout.SwingRadius * 1.6f, fontSize);
                        CenterOverlayText(draw, layout.SliceCenter, SomersaultActive && !_selectingSlice ? muted : white,
                            _selectingSlice ? "CANCEL" : "SLICE", layout.SliceRadius * 1.6f);
                        // Temporarily hidden while somersaults use Space + arrow keys.
                        // Set SomersaultOverlayControlsEnabled to true and uncomment to restore.
                        // DrawSomersaultButtons(draw, layout, canSomersault);
                        // The small reticle moves inside the fixed circle; the control itself never moves.
                        if (_overlayDrag.Active)
                        {
                            var point = layout.SwingIndicator(_overlayDrag.Offset);
                            draw.AddCircle(point, layout.SwingMarkerRadius, white, 16, 1.5f);
                        }
                        string status = SliceStatusVisible ? _sliceStatus
                            : layout.ShowSomersaultButtons && !canSomersault ? somersaultReason : "";
                        if (!string.IsNullOrWhiteSpace(status))
                            DrawCenteredOverlayStatus(draw, status, layout.StatusMin, layout.StatusMax, fontSize, layout.Scale);
                    }
                }
                finally { draw.PopClipRect(); }
            }
            DrawControls(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize);
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
        bool available)
    {
        // Disabled controls retain the requested fill, and still own their visible circle's clicks.
        draw.AddCircleFilled(center, radius, MovementButtonFill, 48);
        var color = available ? MovementButtonText : MovementButtonDisabledText;
        draw.AddCircle(center, radius, new ImColor8(255, 255, 255, 64), 48, Math.Max(.5f, radius / 27));
        var tip = center + direction * radius * .25f;
        var tail = center - direction * radius * .25f;
        var normal = new float2(-direction.Y, direction.X);
        float thickness = Math.Max(.5f, radius / 18);
        draw.AddLine(tail, tip, color, thickness);
        draw.AddLine(tip, tip - direction * radius * .21f + normal * radius * .18f, color, thickness);
        draw.AddLine(tip, tip - direction * radius * .21f - normal * radius * .18f, color, thickness);
    }

    private static void DrawSomersaultButtons(ImDrawListPtr draw, OverlayLayout layout, bool available)
    {
        if (!layout.ShowSomersaultButtons) return;
        DrawSomersaultButton(draw, layout.ForwardCenter, layout.SomersaultRadius, new(0, -1), available);
        DrawSomersaultButton(draw, layout.LeftCenter, layout.SomersaultRadius, new(-1, 0), available);
        DrawSomersaultButton(draw, layout.RightCenter, layout.SomersaultRadius, new(1, 0), available);
        DrawSomersaultButton(draw, layout.BackwardCenter, layout.SomersaultRadius, new(0, 1), available);
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
            if (button == GlfwMouseButton.Left)
            {
                ReleaseManualSwing(); _forceDrag.Release(); ReleaseForceSettingDrag();
                CompleteForceThrowSlider(OverlayPointer(window));
            }
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
        // Native windows cover these controls. Keep an existing overlay drag paired through release.
        if (!_overlayPointerCapture.AnyHeld && ImGui.GetIO().WantCaptureMouse) return false;
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
        if (_forceThrowDrag.Held && button == GlfwMouseButton.Right)
        { CancelForceThrowDrag(); return true; }
        if (ForcePicking && button == GlfwMouseButton.Right)
        { HandleForcePickerPress(button, pointer); return true; }
        if (forcePick) { HandleForcePickerPress(button, pointer); return true; }
        if (button != GlfwMouseButton.Left) return true;
        if (forceHit != ForceOverlayHit.None) return HandleForceOverlayPress(forceHit, pointer);

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
        if (!ShowSomersaultButtons) return;
        QueueSomersault(direction);
    }

    /// <summary>Keep held drags, including movement outside the circle, away from camera controllers.</summary>
    internal bool FilterOverlayCursor(GlfwWindow window)
    {
        if (_forceThrowDrag.Held)
        {
            if (!ForceUiVisible || Program.IsModalOpen() || ImGui.GetIO().AppFocusLost || !ForceCanUse || !ForceHolding
                || !ReferenceEquals(_forceThrowOwner, _owner) || !ReferenceEquals(_force.Owner, _owner))
                CancelForceThrowDrag();
            else MoveForcePointer(OverlayPointer(window));
            return true;
        }
        if (_forceSettingDrag.Held)
        {
            if (Program.IsModalOpen()) ReleaseForceSettingDrag();
            else MoveForcePointer(OverlayPointer(window));
            return true;
        }
        if (_overlayUiButtons != 0) return true;
        if (ForcePicking) return true;
        if (!_overlayPointerCapture.AnyHeld) return !Program.HasMouseInputLatch
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)));
        if (_overlayDrag.Held) _overlayDrag.Move(OverlayPointer(window));
        if (_forceDrag.Held) MoveForcePointer(OverlayPointer(window));
        return true;
    }

    /// <summary>Keep overlay-owned gestures out of ImGui, even after moving over a window.</summary>
    internal bool SuppressOverlayImGuiMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action)
    {
        if (!_overlayPointerCapture.SuppressImGuiButton((int)button, action == GlfwButtonAction.Press, Program.FrameNumber)) return false;
        if (button == GlfwMouseButton.Left && action == GlfwButtonAction.Release)
        {
            ReleaseManualSwing(); _forceDrag.Release(); ReleaseForceSettingDrag();
            CompleteForceThrowSlider(OverlayPointer(window));
        }
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
        if (window == Program.GetWindow() && _forceThrowDrag.Held)
        {
            if (!ForceUiVisible || Program.IsModalOpen() || ImGui.GetIO().AppFocusLost || !ForceCanUse || !ForceHolding
                || !ReferenceEquals(_forceThrowOwner, _owner) || !ReferenceEquals(_force.Owner, _owner))
                CancelForceThrowDrag();
            return true;
        }
        if (Program.HasMouseInputLatch) return false;
        if (window == Program.GetWindow() && !_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)) || _overlayUiButtons != 0)) return true;
        if (!_overlayPointerCapture.AnyHeld && ImGui.GetIO().WantCaptureMouse) return false;
        if (window == Program.GetWindow() && ForceUiVisible && !Program.IsModalOpen()
            && !Program.HasMouseInputLatch && double.IsFinite(offset.Y)
            && (_forceDrag.Held || (!_overlayPointerCapture.AnyHeld && _overlayLayoutAvailable && _forceLayoutAvailable
                && _overlayLayout.Hit(OverlayPointer(window)) == OverlayHit.None
                && _forceLayout.Hit(OverlayPointer(window)) == ForceOverlayHit.Move)))
        {
            ScrollForcePointer(offset.Y, ForceShiftHeld(window));
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
        if (window == Program.GetWindow() && _forceThrowDrag.Held) return true;
        if (Program.HasMouseInputLatch) return false;
        if (!_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(OverlayPointer(window)) || SettingsContainsPointer(OverlayPointer(window)) || _overlayUiButtons != 0)) return false;
        if (!_overlayPointerCapture.AnyHeld && ImGui.GetIO().WantCaptureMouse) return false;
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
        bool valid = KittenListVisible && _kittenColorPickerOwner is { IsDisposed: false } kitten && _currentKittens.Contains(kitten);
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
            bool capeEnabled = CapeEnabledFor(owner);
            if (ImGui.Checkbox("Cape", ref capeEnabled))
                SetKittenCapeEnabled(owner, capeEnabled);
            ImGui.TextWrapped("This kitten's color, saber type, and cape option last for this session.");
            if (CapeStatusFor(owner) is { } capeStatus)
                ImGui.TextWrapped(capeStatus);
            ImGui.Text("Color");
            ImGui.SetNextItemWidth(Math.Min(220 * scale, ImGui.GetContentRegionAvail().X));
            float3 color = SaberColorFor(owner);
            if (ImGui.ColorPicker3("##SaberColor", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha
                | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.InputRGB))
                SetKittenSaberColor(owner, color);
            DrawSaberEffectsSettings(type);
            ImGui.Separator();
        }
        finally
        {
            ImGui.End();
            if (!_kittenColorPickerOpen) DismissKittenColorPicker();
        }
    }
}
