using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ForceController _force = new();
    private readonly ForceOverlayDrag _forceDrag = new();
    private readonly ForceOffsetSmoother _forceOffsetSmoother = new();
    private readonly ForceOverlaySliderDrag _forceSettingDrag = new();
    private readonly Queue<ForceRequest> _forceRequests = new();
    private ForceOverlayLayout _forceLayout;
    private bool _forceLayoutAvailable, _selectingForce, _angryForce, _forceDisposed, _forceEscapeHeld;
    private KittenEva? _forcePickOwner, _forceMoveOwner, _forceSettingOwner;
    private ForcePick? _forceHover;
    private double3 _forceMoveOffset;
    private bool _forceMovePending;
    private bool _forceClickPending;
    private float2 _forceClickPointer;
    private double _forceNextHoverTime;
    private ForceQuery? _forceQuery;
    private string _forceStatus = "";
    private ForceStatusSeverity _forceStatusSeverity;
    private enum ForceAction { Capture, Release, Throw }
    private readonly record struct ForceRequest(KittenEva Owner, ForceAction Action, ForcePick? Target = null,
        bool Angry = false, double AngryRadius = 10, int CaptureLimit = ForceCaptureLimits.Default);
    private readonly record struct ForceQuery(KittenEva Owner, ForcePickRay Ray, bool Capture, bool Angry,
        double AngryRadius, double ClutterTolerance = .2, int CaptureLimit = ForceCaptureLimits.Default);

    internal bool ForceHolding => !_forceDisposed && _force.HasCapture;
    internal KittenEva? ForceOwner => !_forceDisposed ? _force.Owner : null;
    private bool ForcePicking => _selectingForce && OverlayVisible;
    private bool ForceCanUse => _ready && !_disposed && !_forceDisposed && !Program.EditorFlag
        && _owner is { IsDisposed: false } && ReferenceEquals(_owner, Program.ControlledVehicle);

    private void InitializeForce(Harmony harmony)
    {
        _force.Install(harmony);
        SyncForceStatusFromController();
    }

    private void SetForceStatus(string status, ForceStatusSeverity severity)
    {
        _forceStatus = status;
        _forceStatusSeverity = severity;
    }

    private void SyncForceStatusFromController()
    {
        if (_force.StatusSeverity != ForceStatusSeverity.None)
            SetForceStatus(_force.Status, _force.StatusSeverity);
    }

    // Called only after the host joins and applies the previous simulation step.
    private void UpdateForceBeforePhysics(double dtPlayer)
    {
        if (_forceDisposed) return;
        try
        {
            // Input callbacks only publish a target. Advance the displayed offset here,
            // after workers have joined and before the Force controller follows it.
            bool ownsCapture = ForceCanUse && _force.HasCapture
                && ReferenceEquals(_forceMoveOwner, _owner) && ReferenceEquals(_force.Owner, _owner);
            if (ownsCapture)
            {
                if (_forceMovePending)
                {
                    _forceOffsetSmoother.SetTarget(_forceMoveOffset);
                    _forceMovePending = false;
                }
                double3 displayed = _forceOffsetSmoother.Advance(dtPlayer);
                if (displayed != _force.GroupOffsetBody) _force.SetOffset(displayed);
            }
            else if (!_force.HasCapture) ResetForceMovement();

            bool wasHolding = _force.HasCapture;
            _force.Update(_owner);
            if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
            else if (wasHolding) SetForceStatus(_force.Status, ForceStatusSeverity.None);
            if (wasHolding && !_force.HasCapture)
            {
                ResetForceMovement();
            }
            ResolveForceQuery();
            while (_forceRequests.TryDequeue(out var request))
            {
                if (!ForceCanUse || !ReferenceEquals(request.Owner, _owner) || request.Owner.IsDisposed)
                {
                    SetForceStatus("Force request cancelled: the controlled kitten changed.", ForceStatusSeverity.None);
                    continue;
                }
                if (request.Action == ForceAction.Capture && request.Target is { } pick)
                {
                    if (_force.Capture(request.Owner, pick, request.Angry, request.AngryRadius, request.CaptureLimit))
                        ResetForceMovement();
                    if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
                    else SetForceStatus(_force.Status, ForceStatusSeverity.None);
                }
                else if (ReferenceEquals(_force.Owner, request.Owner))
                {
                    _force.Release(request.Action == ForceAction.Throw);
                    if (_force.StatusSeverity != ForceStatusSeverity.None) SyncForceStatusFromController();
                    else SetForceStatus(_force.Status, ForceStatusSeverity.None);
                    ResetForceMovement();
                }
            }
            if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) _forceDrag.Release();
        }
        catch (Exception error)
        {
            SetForceStatus("Force suspended: " + error.Message, ForceStatusSeverity.Error);
            _forceRequests.Clear();
            ResetForceMovement();
            ResetForceGestures();
            try { _force.Reset(); }
            catch (Exception cleanup) { Report("Force cleanup failed", cleanup); }
            Report("Force handoff failed", error);
        }
    }

    // Clutter queries read live Bepu bodies. Only the settled phase may resolve
    // a frame's immutable ray snapshot; UI callbacks never touch that simulation.
    private void ResolveForceQuery()
    {
        var query = _forceQuery;
        _forceQuery = null;
        if (query is not { } pending || !ForcePicking || !ForceCanUse || KittenColorPickerContainsPointer(ImGui.GetIO().MousePos)
            || ImGui.GetIO().AppFocusLost || Program.IsModalOpen() || !ReferenceEquals(pending.Owner, _owner)
            || !ReferenceEquals(_forcePickOwner, _owner)) return;
        try
        {
            ForceTargeting.TryPick(pending.Owner, pending.Ray, out _forceHover, out var reason, pending.ClutterTolerance);
            if (pending.Capture && _forceHover == null)
                SetForceStatus(string.IsNullOrWhiteSpace(reason) ? "Force could not find a target." : reason,
                    ForceStatusSeverity.Warning);
            if (pending.Capture && _forceHover != null && _forceRequests.Count < 32)
            {
                _forceRequests.Enqueue(new(pending.Owner, ForceAction.Capture, _forceHover, pending.Angry, pending.AngryRadius, pending.CaptureLimit));
                SetForceStatus("Capturing " + _forceHover.DisplayName + "...", ForceStatusSeverity.None);
                CancelForcePicker();
            }
        }
        catch (Exception error)
        {
            SetForceStatus("Force picker failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            Report("Force picker failed", error);
        }
    }

    // Frame visibility can change even when KSA skips the overlay drawing hook.
    private void UpdateForceVisibility()
    {
        if (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner))
            SetForceStatus("Force selection cancelled: the controlled kitten changed.", ForceStatusSeverity.None);
        if (!OverlayVisible || Program.IsModalOpen() || !ForceCanUse
            || (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner)))
            ResetForceGestures();
        if (_forceDrag.Held && !ReferenceEquals(_forceMoveOwner, _owner)) _forceDrag.Release();
        if (_forceSettingDrag.Held && !ReferenceEquals(_forceSettingOwner, _owner)) ReleaseForceSettingDrag();
    }

    // Snapshot the fresh camera ray; resolve it after workers join next frame.
    private void UpdateForceFrame()
    {
        try { UpdateForceFrameCore(); }
        catch (Exception error)
        {
            SetForceStatus("Force picker failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            Report("Force picker failed", error);
        }
    }

    private void UpdateForceFrameCore()
    {
        UpdateForceVisibility();
        if (!ForcePicking || !ForceCanUse) return;
        var io = ImGui.GetIO();
        if (io.AppFocusLost) { ResetForceGestures(); return; }
        if (_forceQuery is { Capture: true }) return;
        var pointer = _forceClickPending ? _forceClickPointer : io.MousePos;
        if ((!_forceClickPending && (io.WantCaptureMouse || !CursorTarget.IsHitTestViewport(Program.MainViewport)))
            || io.WantTextInput || Program.IsModalOpen() || KittenColorPickerContainsPointer(pointer)
            || (_overlayLayoutAvailable && _overlayLayout.Hit(pointer) != SaberOverlayHit.None)
            || (_forceLayoutAvailable && _forceLayout.Hit(pointer) != ForceOverlayHit.None))
        { _forceHover = null; _forceClickPending = false; _forceQuery = null; return; }
        if (!_forceClickPending && _playerTime < _forceNextHoverTime) return;
        _forceNextHoverTime = _playerTime + .05;
        var viewport = Program.MainViewport;
        var local = Cursor.ToViewportLocal(viewport, pointer);
        if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)
            || local.X < 0 || local.Y < 0 || local.X >= viewport.Width || local.Y >= viewport.Height)
        { _forceClickPending = false; _forceHover = null; _forceQuery = null; return; }
        var ray = viewport.GetCamera().ScreenToEgoRay(local);
        ray.Direction = double3.NormalizeOrZero(ray.Direction);
        if (ForceTargeting.TrySnapshotRay(_owner!, ray, out var snapshot))
        {
            _forceQuery = new(_owner!, snapshot, _forceClickPending, _angryForce, _settings.AngryForceRadius,
                _settings.ClutterPickTolerance, _settings.ForceCaptureLimit);
        }
        else { _forceQuery = null; _forceHover = null; }
        _forceClickPending = false;
    }

    private void BeginForcePicker(bool angry)
    {
        if (!ForceCanUse || _force.HasCapture) return;
        if (_selectingSlice) Queue(Command.CancelSlice);
        _forcePickOwner = _owner; _angryForce = angry; _selectingForce = true;
        _forceHover = null; _forceClickPending = false; _forceQuery = null; _forceNextHoverTime = 0;
        SetForceStatus(angry ? $"Angry Force: select a target and objects within {_settings.AngryForceRadius:0.#} m of the hit. Right-click or Esc cancels."
            : "Force: point at a vessel, debris, rock or tree trunk and click. Right-click or Esc cancels.", ForceStatusSeverity.None);
        SyncForceStatusFromController();
    }

    private void CancelForcePicker()
    {
        _selectingForce = false; _forcePickOwner = null; _forceHover = null; _forceClickPending = false; _forceQuery = null;
    }

    private void QueueForceAction(ForceAction action)
    {
        if (!ForceCanUse || _forceRequests.Count >= 32) return;
        _forceRequests.Enqueue(new(_owner!, action));
        CancelForcePicker();
        _forceDrag.Release();
        // Retain a final drag offset until the settled handoff processes the release.
    }

    private void ToggleForcePicker(bool angry)
    {
        if (ForcePicking && _angryForce == angry)
        {
            CancelForcePicker();
            SetForceStatus("Force selection cancelled.", ForceStatusSeverity.None);
        }
        else BeginForcePicker(angry);
    }

    private bool HandleForceOverlayPress(ForceOverlayHit hit, float2 pointer)
    {
        if (hit == ForceOverlayHit.None) return false;
        if (hit is ForceOverlayHit.CaptureLimit or ForceOverlayHit.AngryRadius)
        {
            CancelForcePicker();
            _forceDrag.Release();
            var center = hit == ForceOverlayHit.CaptureLimit
                ? _forceLayout.CaptureLimitCenter : _forceLayout.AngryRadiusCenter;
            double current = hit == ForceOverlayHit.CaptureLimit
                ? _settings.ForceCaptureLimit : _settings.AngryForceRadius;
            double maximum = hit == ForceOverlayHit.CaptureLimit ? ForceCaptureLimits.Maximum : 50;
            if (_forceSettingDrag.Begin(hit, pointer, center, _forceLayout.ButtonRadius,
                _forceLayout.SliderTravel, current, 1, maximum, hit == ForceOverlayHit.CaptureLimit))
                _forceSettingOwner = _owner;
            return true;
        }
        if (!ForceCanUse) { SetForceStatus("Control an EVA kitten to use the Force.", ForceStatusSeverity.None); return true; }
        switch (hit)
        {
            case ForceOverlayHit.Force:
                if (_force.HasCapture) QueueForceAction(ForceAction.Release);
                else ToggleForcePicker(false);
                break;
            case ForceOverlayHit.AngryForce:
                if (_force.HasCapture) QueueForceAction(ForceAction.Throw);
                else ToggleForcePicker(true);
                break;
            case ForceOverlayHit.Move:
                if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) break;
                var camera = Program.MainViewport.GetCamera();
                var bodyToEgo = double4x4.CreateFromQuaternion(_owner!.Body2Ego);
                if (!double4x4.Invert(bodyToEgo, out var egoToBody)) break;
                // Re-grab at the visible position. A raw pointer target can be ahead
                // of the group while the spring is still moving toward it.
                double3 current = _force.GroupOffsetBody;
                if (_forceDrag.Begin(pointer, _forceLayout.MoveRadius, current,
                    double3.TransformNormal(camera.GetRightEcl(), egoToBody),
                    double3.TransformNormal(camera.GetUpEcl(), egoToBody),
                    double3.TransformNormal(camera.GetForwardEcl(), egoToBody)))
                {
                    _forceMoveOwner = _owner;
                    _forceMoveOffset = current;
                    _forceMovePending = true;
                }
                break;
        }
        return true;
    }

    private void MoveForcePointer(float2 pointer)
    {
        if (_forceSettingDrag.Move(pointer, out double setting))
        {
            if (_forceSettingDrag.Control == ForceOverlayHit.CaptureLimit)
                _settings.ForceCaptureLimit = (int)setting;
            else if (_forceSettingDrag.Control == ForceOverlayHit.AngryRadius)
                _settings.AngryForceRadius = (float)setting;
            return;
        }
        if (!_forceDrag.Held) return;
        _forceDrag.Move(pointer);
        _forceMoveOffset = _forceDrag.Offset;
        _forceMovePending = true;
    }

    // GLFW input only updates the pending body-frame target. The settled handoff
    // smooths and applies it to every captured vessel and native clutter body together.
    private bool ScrollForcePointer(double ticks)
    {
        if (!ForceCanUse || !_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) return false;
        var bodyToEgo = double4x4.CreateFromQuaternion(_owner!.Body2Ego);
        if (!double4x4.Invert(bodyToEgo, out var egoToBody)) return false;
        double3 current = _forceMovePending && ReferenceEquals(_forceMoveOwner, _owner)
            ? _forceMoveOffset : _forceOffsetSmoother.Target;
        var forward = double3.TransformNormal(Program.MainViewport.GetCamera().GetForwardEcl(), egoToBody);
        if (!_forceDrag.Scroll(ticks, current, forward)) return false;
        _forceMoveOwner = _owner;
        _forceMoveOffset = _forceDrag.Offset;
        _forceMovePending = true;
        return true;
    }

    private bool HandleForcePickerPress(GlfwMouseButton button, float2 pointer)
    {
        if (!ForcePicking) return false;
        if (button == GlfwMouseButton.Right) { CancelForcePicker(); return true; }
        if (button != GlfwMouseButton.Left || !ForceCanUse) return true;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || io.WantTextInput || Program.IsModalOpen() || KittenColorPickerContainsPointer(pointer)
            || !CursorTarget.IsHitTestViewport(Program.MainViewport)) return true;
        _forceClickPointer = pointer;
        _forceClickPending = true;
        return true;
    }

    internal bool FilterForceKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action)
    {
        if (window != Program.GetWindow() || key != GlfwKey.Escape) return false;
        if (_forceEscapeHeld)
        {
            if (action == GlfwKeyAction.Release) _forceEscapeHeld = false;
            return true;
        }
        var io = ImGui.GetIO();
        if ((!ForcePicking && !_forceSettingDrag.Held) || KittenColorPickerContainsPointer(io.MousePos)
            || io.WantTextInput || io.WantCaptureKeyboard || Program.IsModalOpen()) return false;
        if (action != GlfwKeyAction.Release)
        {
            _forceEscapeHeld = true;
            CancelForcePicker();
            ReleaseForceSettingDrag();
        }
        return true;
    }

    private void ReleaseForceSettingDrag()
    {
        _forceSettingDrag.Release();
        _forceSettingOwner = null;
    }

    private void ResetForceGestures()
    {
        CancelForcePicker(); _forceDrag.Release(); ReleaseForceSettingDrag(); _forceLayoutAvailable = false;
    }

    private void ResetForceMovement()
    {
        _forceDrag.Reset();
        _forceOffsetSmoother.Reset();
        _forceMoveOffset = double3.Zero;
        _forceMovePending = false;
        _forceMoveOwner = null;
    }

    private void ResetForce()
    {
        ResetForceGestures(); ResetForceMovement(); _forceSettingDrag.Reset(); _forceRequests.Clear();
        _forceEscapeHeld = false; SetForceStatus("", ForceStatusSeverity.None);
        if (!_forceDisposed)
        {
            _force.Reset();
            SyncForceStatusFromController();
        }
    }

    private void DisposeForce()
    {
        if (_forceDisposed) return;
        ResetForce();
        _forceDisposed = true;
        _force.Dispose();
    }

    private void DrawForceOverlay(ImDrawListPtr draw)
    {
        try { DrawForceOverlayCore(draw); }
        catch (Exception error)
        {
            SetForceStatus("Force overlay failed: " + error.Message, ForceStatusSeverity.Error);
            CancelForcePicker();
            ReleaseForceSettingDrag();
            Report("Force overlay failed", error);
        }
    }

    private void DrawForceOverlayCore(ImDrawListPtr draw)
    {
        if (!_forceLayoutAvailable) return;
        var layout = _forceLayout;
        // A resize, DPI change or custom offset invalidates press-time geometry.
        // Collapse visually; the pointer capture still suppresses input until release.
        if (_forceSettingDrag.Held && !_forceSettingDrag.Matches(layout)) ReleaseForceSettingDrag();
        var white = new ImColor8(255, 255, 255, 255);
        var muted = new ImColor8(195, 200, 210, 255);
        var color = ForceCanUse ? white : muted;
        var buttonColor = ForceCanUse ? MovementButtonText : MovementButtonDisabledText;
        bool forcePicking = !ForceHolding && ForcePicking && !_angryForce;
        bool angryPicking = !ForceHolding && ForcePicking && _angryForce;
        var settingsFill = new ImColor8(0, 0, 0, 128);
        var faintWhiteRim = new ImColor8(255, 255, 255, 64);
        draw.AddCircleFilled(layout.ForceCenter, layout.ButtonRadius, MovementButtonFill, 48);
        draw.AddCircleFilled(layout.AngryCenter, layout.ButtonRadius,
            angryPicking ? new ImColor8(224, 24, 24, 210) : new ImColor8(224, 24, 24, 128), 48);
        draw.AddCircle(layout.ForceCenter, layout.ButtonRadius, faintWhiteRim, 48,
            forcePicking ? Math.Max(2, 3 * layout.Scale) : Math.Max(.5f, layout.Scale));
        CenterOverlayText(draw, layout.ForceCenter, buttonColor, ForceHolding ? "RELEASE" : forcePicking ? "CANCEL" : "FORCE", layout.ButtonRadius * 1.75f);
        if (ForceHolding) CenterOverlayText(draw, layout.AngryCenter, color, "THROW", layout.ButtonRadius * 1.75f);
        else if (angryPicking) CenterOverlayText(draw, layout.AngryCenter, color, "CANCEL", layout.ButtonRadius * 1.75f);
        else
        {
            float labelFont = Math.Min(ImGui.GetFontSize(), 14 * layout.Scale);
            CenterOverlayText(draw, layout.AngryCenter - new float2(0, labelFont * .5f), color,
                "ANGRY", layout.ButtonRadius * 1.75f, labelFont);
            CenterOverlayText(draw, layout.AngryCenter + new float2(0, labelFont * .5f), color,
                "FORCE", layout.ButtonRadius * 1.75f, labelFont);
        }
        draw.AddCircleFilled(layout.MoveCenter, layout.MoveRadius, new ImColor8(0, 0, 0, 128), 64);
        draw.AddCircle(layout.MoveCenter, layout.MoveRadius, faintWhiteRim, 64, Math.Max(.5f, layout.Scale));
        float font = Math.Min(ImGui.GetFontSize(), 16 * layout.Scale);
        var moveText = ForceHolding ? white : muted;
        CenterOverlayText(draw, layout.MoveCenter - new float2(0, font * .55f), moveText,
            "MOVE FORCE", layout.MoveRadius * 1.6f, font);
        CenterOverlayText(draw, layout.MoveCenter + new float2(0, font * .65f), moveText,
            "drag + wheel", layout.MoveRadius * 1.6f, font);
        if (_forceDrag.Held)
            draw.AddCircle(layout.MoveCenter + _forceDrag.PointerOffset * (layout.MoveRadius * .65f),
                Math.Max(2, 5 * layout.Scale), white, 16, 1);
        if (_forceSettingDrag.Held && _forceSettingDrag.Control == ForceOverlayHit.CaptureLimit)
        {
            DrawForceSettingControl(ForceOverlayHit.AngryRadius, layout.AngryRadiusCenter,
                _settings.AngryForceRadius, "DIST", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}m", "1m", "50m");
            DrawForceSettingControl(ForceOverlayHit.CaptureLimit, layout.CaptureLimitCenter,
                _settings.ForceCaptureLimit, "COUNT", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}", "1", ForceCaptureLimits.Maximum.ToString());
        }
        else if (_forceSettingDrag.Held && _forceSettingDrag.Control == ForceOverlayHit.AngryRadius)
        {
            DrawForceSettingControl(ForceOverlayHit.CaptureLimit, layout.CaptureLimitCenter,
                _settings.ForceCaptureLimit, "COUNT", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}", "1", ForceCaptureLimits.Maximum.ToString());
            DrawForceSettingControl(ForceOverlayHit.AngryRadius, layout.AngryRadiusCenter,
                _settings.AngryForceRadius, "DIST", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}m", "1m", "50m");
        }
        else
        {
            DrawForceSettingControl(ForceOverlayHit.CaptureLimit, layout.CaptureLimitCenter,
                _settings.ForceCaptureLimit, "COUNT", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}", "1", ForceCaptureLimits.Maximum.ToString());
            DrawForceSettingControl(ForceOverlayHit.AngryRadius, layout.AngryRadiusCenter,
                _settings.AngryForceRadius, "DIST", value => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}m", "1m", "50m");
        }
        if (_forceStatusSeverity is ForceStatusSeverity.Warning or ForceStatusSeverity.Error
            && !string.IsNullOrWhiteSpace(_forceStatus))
            DrawCenteredOverlayStatus(draw, _forceStatus, layout.StatusMin, layout.StatusMax,
                font, layout.Scale, bottomAligned: true);
        var overlayPointer = ImGui.GetIO().MousePos;
        if (!KittenColorPickerContainsPointer(overlayPointer) && !SettingsContainsPointer(overlayPointer))
        {
            var overlayHit = layout.Hit(overlayPointer);
            if (overlayHit == ForceOverlayHit.Move)
                ShowSaberTooltip("Drag to move held objects in the camera plane.\n\nScroll to change depth; release to keep the position.");
            else if (overlayHit == ForceOverlayHit.CaptureLimit)
                ShowSaberTooltip("max # objects force can capture");
            else if (overlayHit == ForceOverlayHit.AngryRadius)
                ShowSaberTooltip("force capture bubble size");
        }
        if (_forceHover != null && ForcePicking && Program.GetMainCamera() is { } camera
            && ForceTargeting.TryHitEgo(_forceHover, camera, out var hit) && double3.Dot(hit, camera.GetForwardEcl()) > .001)
        {
            var ui = ImGui.GetMainViewport();
            var buffer = camera.FramebufferSize;
            if (buffer.X <= 0 || buffer.Y <= 0) return;
            var pixels = camera.EgoToScreen(hit);
            var position = ui.Pos + new float2(pixels.X * ui.Size.X / buffer.X, pixels.Y * ui.Size.Y / buffer.Y);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return;
            var green = new ImColor8(100, 255, 180, 255);
            draw.AddCircle(position, 19, new ImColor8(0, 0, 0, 220), 48, 5);
            draw.AddCircle(position, 19, green, 48, 2);
            draw.AddCircleFilled(position, 3, green, 16);
            string label = _forceHover.DisplayName + (_angryForce ? $" + objects within {_settings.AngryForceRadius:0.#} m" : " — click to hold");
            float wrap = Math.Max(100, Math.Min(320, ui.Size.X - 24));
            var labelSize = ImGui.CalcTextSize(label, false, wrap);
            var labelPosition = new float2(Math.Clamp(position.X + 25, ui.Pos.X + 8,
                ui.Pos.X + Math.Max(8, ui.Size.X - wrap - 8)), Math.Clamp(position.Y,
                ui.Pos.Y + 8, ui.Pos.Y + Math.Max(8, ui.Size.Y - labelSize.Y - 8)));
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition + new float2(1),
                new ImColor8(0, 0, 0, 255), label, wrap);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition, green, label, wrap);
        }

        void DrawForceSettingControl(ForceOverlayHit control, float2 center, double current, string caption,
            Func<double, string> format, string minimum, string maximum)
        {
            float labelFont = Math.Min(ImGui.GetFontSize(), 11 * layout.Scale);
            if (_forceSettingDrag.Held && _forceSettingDrag.Control == control)
            {
                draw.AddRectFilled(_forceSettingDrag.PillMin, _forceSettingDrag.PillMax,
                    settingsFill, layout.ButtonRadius);
                draw.AddRect(_forceSettingDrag.PillMin, _forceSettingDrag.PillMax, faintWhiteRim,
                    layout.ButtonRadius, ImDrawFlags.None, Math.Max(.5f, layout.Scale));
                CenterOverlayText(draw, new(center.X, _forceSettingDrag.TrackTop), muted,
                    minimum, layout.ButtonRadius * 1.65f, labelFont * .8f);
                CenterOverlayText(draw, new(center.X, _forceSettingDrag.TrackBottom), muted,
                    maximum, layout.ButtonRadius * 1.65f, labelFont * .8f);
                var thumb = _forceSettingDrag.Thumb(current);
                float pillWidth = _forceSettingDrag.PillMax.X - _forceSettingDrag.PillMin.X;
                float thumbRadius = Math.Max(2, pillWidth * .4f);
                draw.AddCircleFilled(thumb, thumbRadius, new ImColor8(45, 105, 210, 230), 24);
                draw.AddCircle(thumb, thumbRadius, white, 24, Math.Max(1, layout.Scale));
                string value = format(current);
                float fontSize = ImGui.GetFontSize();
                var labelPosition = new float2(_forceSettingDrag.PillMax.X + layout.SliderValueLabelGap,
                    thumb.Y - fontSize * .5f);
                draw.AddText(ImGui.GetFont(), fontSize, labelPosition + new float2(layout.Scale),
                    new ImColor8(0, 0, 0, 255), value, 0);
                draw.AddText(ImGui.GetFont(), fontSize, labelPosition, white, value, 0);
            }
            else
            {
                draw.AddCircleFilled(center, layout.ButtonRadius, settingsFill, 48);
                draw.AddCircle(center, layout.ButtonRadius, faintWhiteRim, 48, Math.Max(.5f, layout.Scale));
                CenterOverlayText(draw, center - new float2(0, labelFont * .5f), color,
                    caption, layout.ButtonRadius * 1.75f, labelFont);
                CenterOverlayText(draw, center + new float2(0, labelFont * .55f), color,
                    format(Math.Round(current, MidpointRounding.AwayFromZero)), layout.ButtonRadius * 1.75f, labelFont);
            }
        }
    }
}
