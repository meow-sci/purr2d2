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
    private readonly Queue<ForceRequest> _forceRequests = new();
    private ForceOverlayLayout _forceLayout;
    private bool _forceLayoutAvailable, _selectingForce, _angryForce, _forceDisposed, _forceEscapeHeld;
    private KittenEva? _forcePickOwner, _forceMoveOwner;
    private ForcePick? _forceHover;
    private double3 _forceMoveOffset;
    private bool _forceMovePending;
    private bool _forceClickPending;
    private float2 _forceClickPointer;
    private double _forceNextHoverTime;
    private ForceQuery? _forceQuery;
    private string _forceStatus = "";
    private enum ForceAction { Capture, Release, Throw }
    private readonly record struct ForceRequest(KittenEva Owner, ForceAction Action, ForcePick? Target = null, bool Angry = false);
    private readonly record struct ForceQuery(KittenEva Owner, ForcePickRay Ray, bool Capture, bool Angry);

    internal bool ForceHolding => !_forceDisposed && _force.HasCapture;
    internal KittenEva? ForceOwner => !_forceDisposed ? _force.Owner : null;
    private bool ForcePicking => _selectingForce && OverlayVisible;
    private bool ForceCanUse => _ready && !_disposed && !_forceDisposed && !Program.EditorFlag
        && _owner is { IsDisposed: false } && ReferenceEquals(_owner, Program.ControlledVehicle);

    private void InitializeForce(Harmony harmony) => _force.Install(harmony);

    // Called only after the host joins and applies the previous simulation step.
    private void UpdateForceBeforePhysics(double dtPlayer)
    {
        if (_forceDisposed) return;
        try
        {
            // Commit the last movement before advancing or releasing/throwing the group.
            if (_forceMovePending)
            {
                if (ForceCanUse && _force.HasCapture && ReferenceEquals(_forceMoveOwner, _owner)
                    && ReferenceEquals(_force.Owner, _owner)) _force.SetOffset(_forceMoveOffset);
                _forceMovePending = false;
            }
            bool wasHolding = _force.HasCapture;
            _force.Update(_owner);
            if (wasHolding && !_force.HasCapture) _forceStatus = _force.Status;
            ResolveForceQuery();
            while (_forceRequests.TryDequeue(out var request))
            {
                if (!ForceCanUse || !ReferenceEquals(request.Owner, _owner) || request.Owner.IsDisposed)
                {
                    _forceStatus = "Force request cancelled: the controlled kitten changed.";
                    continue;
                }
                if (request.Action == ForceAction.Capture && request.Target is { } pick)
                {
                    if (_force.Capture(request.Owner, pick, request.Angry))
                    {
                        _forceDrag.Reset(); _forceMovePending = false; _forceMoveOwner = null;
                    }
                    _forceStatus = _force.Status;
                }
                else if (ReferenceEquals(_force.Owner, request.Owner))
                {
                    _force.Release(request.Action == ForceAction.Throw);
                    _forceStatus = _force.Status;
                }
            }
            if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) _forceDrag.Release();
        }
        catch (Exception error)
        {
            _forceStatus = "Force suspended: " + error.Message;
            _forceRequests.Clear(); _forceMovePending = false;
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
        if (query is not { } pending || !ForcePicking || !ForceCanUse || KittenColorPickerVisible
            || ImGui.GetIO().AppFocusLost || Program.IsModalOpen() || !ReferenceEquals(pending.Owner, _owner)
            || !ReferenceEquals(_forcePickOwner, _owner)) return;
        try
        {
            ForceTargeting.TryPick(pending.Owner, pending.Ray, out _forceHover, out var reason);
            _forceStatus = _forceHover != null
                ? $"Click to hold {_forceHover.DisplayName}." + (pending.Angry ? " Includes nearby objects within 2 m of the hit." : "")
                : reason;
            if (pending.Capture && _forceHover != null && _forceRequests.Count < 32)
            {
                _forceRequests.Enqueue(new(pending.Owner, ForceAction.Capture, _forceHover, pending.Angry));
                _forceStatus = "Capturing " + _forceHover.DisplayName + "...";
                CancelForcePicker();
            }
        }
        catch (Exception error)
        {
            _forceStatus = "Force picker failed: " + error.Message;
            CancelForcePicker();
            Report("Force picker failed", error);
        }
    }

    // Frame visibility can change even when KSA skips the overlay drawing hook.
    private void UpdateForceVisibility()
    {
        if (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner))
            _forceStatus = "Force selection cancelled: the controlled kitten changed.";
        if (!OverlayVisible || !ForceCanUse || (_forcePickOwner != null && !ReferenceEquals(_forcePickOwner, _owner)))
            ResetForceGestures();
        if (_forceDrag.Held && !ReferenceEquals(_forceMoveOwner, _owner)) _forceDrag.Release();
    }

    // Snapshot the fresh camera ray; resolve it after workers join next frame.
    private void UpdateForceFrame()
    {
        try { UpdateForceFrameCore(); }
        catch (Exception error)
        {
            _forceStatus = "Force picker failed: " + error.Message;
            CancelForcePicker();
            Report("Force picker failed", error);
        }
    }

    private void UpdateForceFrameCore()
    {
        UpdateForceVisibility();
        if (!ForcePicking || !ForceCanUse || KittenColorPickerVisible) return;
        var io = ImGui.GetIO();
        if (io.AppFocusLost) { ResetForceGestures(); return; }
        if (_forceQuery is { Capture: true }) return;
        var pointer = _forceClickPending ? _forceClickPointer : io.MousePos;
        if ((!_forceClickPending && (io.WantCaptureMouse || !CursorTarget.IsHitTestViewport(Program.MainViewport)))
            || io.WantTextInput || Program.IsModalOpen()
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
            _forceQuery = new(_owner!, snapshot, _forceClickPending, _angryForce);
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
        _forceStatus = angry ? "Angry Force: select a target and objects within 2 m of the hit. Right-click or Esc cancels."
            : "Force: point at a vessel, debris or nearby rock and click. Right-click or Esc cancels.";
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

    private bool HandleForceOverlayPress(ForceOverlayHit hit, float2 pointer)
    {
        if (hit == ForceOverlayHit.None) return false;
        if (!ForceCanUse) { _forceStatus = "Control an EVA kitten to use the Force."; return true; }
        switch (hit)
        {
            case ForceOverlayHit.Force:
                if (_force.HasCapture) QueueForceAction(ForceAction.Release);
                else BeginForcePicker(false);
                break;
            case ForceOverlayHit.AngryForce:
                if (_force.HasCapture) QueueForceAction(ForceAction.Throw);
                else BeginForcePicker(true);
                break;
            case ForceOverlayHit.Move:
                if (!_force.HasCapture || !ReferenceEquals(_force.Owner, _owner)) break;
                var camera = Program.MainViewport.GetCamera();
                var bodyToEgo = double4x4.CreateFromQuaternion(_owner!.Body2Ego);
                if (!double4x4.Invert(bodyToEgo, out var egoToBody)) break;
                double3 current = _forceMovePending && ReferenceEquals(_forceMoveOwner, _owner)
                    ? _forceMoveOffset : _force.GroupOffsetBody;
                if (_forceDrag.Begin(pointer, _forceLayout.MoveRadius, current,
                    double3.TransformNormal(camera.GetRightEcl(), egoToBody),
                    double3.TransformNormal(camera.GetUpEcl(), egoToBody),
                    double3.TransformNormal(camera.GetForwardEcl(), egoToBody))) _forceMoveOwner = _owner;
                break;
        }
        return true;
    }

    private void MoveForcePointer(float2 pointer)
    {
        if (!_forceDrag.Held) return;
        _forceDrag.Move(pointer);
        _forceMoveOffset = _forceDrag.Offset;
        _forceMovePending = true;
    }

    private bool HandleForcePickerPress(GlfwMouseButton button, float2 pointer)
    {
        if (!ForcePicking) return false;
        if (button == GlfwMouseButton.Right) { CancelForcePicker(); return true; }
        if (button != GlfwMouseButton.Left || !ForceCanUse) return true;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || io.WantTextInput || Program.IsModalOpen()
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
        if (!ForcePicking || KittenColorPickerVisible || io.WantTextInput || io.WantCaptureKeyboard || Program.IsModalOpen()) return false;
        if (action != GlfwKeyAction.Release) { _forceEscapeHeld = true; CancelForcePicker(); }
        return true;
    }

    private void ResetForceGestures()
    {
        CancelForcePicker(); _forceDrag.Release(); _forceLayoutAvailable = false;
    }

    private void ResetForce()
    {
        ResetForceGestures(); _forceDrag.Reset(); _forceRequests.Clear();
        _forceMovePending = false; _forceMoveOwner = null; _forceEscapeHeld = false; _forceStatus = "";
        if (!_forceDisposed) _force.Reset();
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
            _forceStatus = "Force overlay failed: " + error.Message;
            CancelForcePicker();
            Report("Force overlay failed", error);
        }
    }

    private void DrawForceOverlayCore(ImDrawListPtr draw)
    {
        if (!_forceLayoutAvailable) return;
        var layout = _forceLayout;
        var white = new ImColor8(225, 245, 255, 255);
        var muted = new ImColor8(130, 155, 175, 255);
        var color = ForceCanUse ? white : muted;
        draw.AddCircleFilled(layout.ForceCenter, layout.ButtonRadius, new ImColor8(15, 80, 130, 128), 48);
        draw.AddCircleFilled(layout.AngryCenter, layout.ButtonRadius, new ImColor8(135, 30, 35, 128), 48);
        draw.AddCircle(layout.ForceCenter, layout.ButtonRadius, color, 48, 1);
        draw.AddCircle(layout.AngryCenter, layout.ButtonRadius, color, 48, 1);
        CenterOverlayText(draw, layout.ForceCenter, color, ForceHolding ? "RELEASE" : "FORCE", layout.ButtonRadius * 1.75f);
        if (ForceHolding) CenterOverlayText(draw, layout.AngryCenter, color, "THROW", layout.ButtonRadius * 1.75f);
        else
        {
            float labelFont = Math.Min(ImGui.GetFontSize(), 12 * layout.Scale);
            CenterOverlayText(draw, layout.AngryCenter - new float2(0, labelFont * .5f), color,
                "ANGRY", layout.ButtonRadius * 1.75f, labelFont);
            CenterOverlayText(draw, layout.AngryCenter + new float2(0, labelFont * .5f), color,
                "FORCE", layout.ButtonRadius * 1.75f, labelFont);
        }
        draw.AddCircleFilled(layout.MoveCenter, layout.MoveRadius, new ImColor8(0, 0, 0, 128), 64);
        draw.AddCircle(layout.MoveCenter, layout.MoveRadius, ForceHolding ? white : muted, 64, Math.Max(.5f, layout.Scale));
        float font = Math.Min(ImGui.GetFontSize(), 16 * layout.Scale);
        CenterOverlayText(draw, layout.MoveCenter - new float2(0, font), ForceHolding ? white : muted,
            "MOVE FORCE", layout.MoveRadius * 1.7f, font);
        CenterOverlayText(draw, layout.MoveCenter + new float2(0, font * .5f), muted,
            "drag + wheel depth", layout.MoveRadius * 1.7f, font);
        if (_forceDrag.Held)
            draw.AddCircle(layout.MoveCenter + _forceDrag.PointerOffset * (layout.MoveRadius * .65f),
                Math.Max(2, 5 * layout.Scale), white, 16, 1);
        string status = ForceHolding ? $"Holding {_force.CapturedCount} object(s).\n" + _force.Status : _forceStatus;
        if (string.IsNullOrEmpty(status)) status = "Force: select one object.\nAngry Force: include objects within 2 m.";
        draw.PushClipRect(layout.StatusMin, layout.StatusMax, true);
        try { draw.AddText(ImGui.GetFont(), font, layout.StatusMin, color, status, layout.StatusMax.X - layout.StatusMin.X); }
        finally { draw.PopClipRect(); }
        if (!KittenColorPickerVisible && !SettingsContainsPointer(ImGui.GetIO().MousePos)
            && layout.Hit(ImGui.GetIO().MousePos) == ForceOverlayHit.Move)
            ImGui.SetTooltip("Hold and drag to move in the camera plane. Wheel up brings objects closer; down moves them away. Release keeps their position. Re-grab to move farther.");
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
            string label = _forceHover.DisplayName + (_angryForce ? " + objects within 2 m" : " — click to hold");
            float wrap = Math.Max(100, Math.Min(320, ui.Size.X - 24));
            var labelSize = ImGui.CalcTextSize(label, false, wrap);
            var labelPosition = new float2(Math.Clamp(position.X + 25, ui.Pos.X + 8,
                ui.Pos.X + Math.Max(8, ui.Size.X - wrap - 8)), Math.Clamp(position.Y,
                ui.Pos.Y + 8, ui.Pos.Y + Math.Max(8, ui.Size.Y - labelSize.Y - 8)));
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition + new float2(1),
                new ImColor8(0, 0, 0, 255), label, wrap);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition, green, label, wrap);
        }
    }
}
