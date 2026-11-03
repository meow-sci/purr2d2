using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private enum AssistedClutterResult { Absent, Blocked, Started }
    private readonly AssistedSliceSequence _sliceSequence = new();
    private SliceTarget? _sliceTarget, _sliceHover;
    private bool _sliceHoverAllowed;
    private PartSliceTargeting.Selection? _sliceSelection;
    private Ray? _sliceClutterRay;
    private bool _selectingSlice, _sliceStarted;
    private string? _sliceError;
    private float3 _sliceAim = float3.UnitZ;
    private double _sliceNextSpark;
    private double _sliceMessageUntil;
    private double _sliceObservationTime;
    private string _sliceStatus = "";
    private bool SliceStatusVisible => _sliceStatus.Length > 0
        && (_selectingSlice || SliceWorkActive || _playerTime < _sliceMessageUntil);
    // Hover, fallback selection and stroke-start revalidation share the same assisted allowance.
    private double SliceReach => _pose.TryGetBladeSegment(Length, out var a, out var b)
        ? SaberLimits.SliceSelectionReach((b - a).Length(), _settings.SliceReachMultiplier) : 0;

    private bool TrySliceReach(out double3 baseBody, out double reach, out string reason)
    {
        baseBody = default; reach = SliceReach;
        reason = "Keep the lit saber visible to select a surface within the configured slice reach.";
        if (!SaberHeldForActions || !_blade.Equipped || !_blade.TargetOn || !double.IsFinite(reach) || reach <= 0
            || !_pose.TryGetBladeSegment(Length, out baseBody, out _)) return false;
        reason = "";
        return true;
    }

    private void SelectPartToSlice()
    {
        CancelForcePicker();
        if (!SaberHeldForActions) return;
        if (SliceWorkActive) return;
        if (_selectingSlice) { CancelAssistedSlice("Slice selection cancelled."); return; }
        if (_pose.ClashHeld)
        { _sliceStatus = "Wait for the saber clash to finish before selecting a slice."; return; }
        if (_owner == null) return;
        if (_slicer.Busy) { _sliceStatus = "Finishing the previous slice; try again shortly."; _sliceMessageUntil = _playerTime + 5; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = true;
        _sliceStatus = "Point at a part, tree or rock and click to slice. Right-click or Esc to cancel.";
    }

    private void BeginAssistedSlice(SliceTarget? target, Ray? ray)
    {
        if (!SaberHeldForActions || _pose.ClashHeld || SliceWorkActive || _owner == null || _slicer.Busy) return;
        if (!TrySliceReach(out var baseBody, out var reach, out var reason))
        { _selectingSlice = true; _sliceStatus = reason; return; }
        Ray? groundRay = ray is { } pointerRay ? SnapshotClutterSliceRay(pointerRay) : null;
        if (ray.HasValue && !groundRay.HasValue)
        { BlockAssistedClutter("The pointer ray could not be confirmed."); return; }
        if (target != null && ray is { } pickedRay && groundRay is { } nativeRay)
        {
            if (Program.GetMainCamera() is not { } camera)
            { BlockAssistedClutter("The current view is unavailable."); return; }
            var hitEgo = target.HitAssembly.Transform(target.Vehicle.GetMatrixAsmb2Ego(camera));
            double partDistance = double3.Dot(hitEgo - pickedRay.Origin, pickedRay.Direction.NormalizeOrZero());
            // A native part hover does not include ground clutter. The nearer
            // tree/rock must win even when the vessel behind it is out of reach.
            if (!double.IsFinite(partDistance) || partDistance < 0)
            { BlockAssistedClutter("The selected surface distance could not be confirmed."); return; }
            if (TryBeginAssistedClutterSlice(nativeRay, partDistance, target.Part) != AssistedClutterResult.Absent) return;
        }
        if (target != null && !_targeting.CheckSliceTarget(_owner, target, baseBody, reach, out reason))
        { _selectingSlice = true; _sliceStatus = reason; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = false; _sliceHover = null;
        _sliceTarget = target;
        _sliceClutterRay = groundRay;
        if (target == null && ray is { } pick)
            _sliceSelection = _targeting.BeginSelection(_owner, pick.Origin, pick.Direction, baseBody, reach);
        if (target == null && _sliceSelection == null) return;
        _sliceStarted = false; _sliceError = null;
        _sliceObservationTime = _playerTime;
        _sliceSequence.Start(); _sliceNextSpark = .2;
        _sliceStatus = "Slicing the selected part...";
    }

    private AssistedClutterResult BlockAssistedClutter(string reason)
    {
        _selectingSlice = true;
        _sliceStatus = "Slice blocked: " + reason;
        _sliceMessageUntil = _playerTime + 5;
        return AssistedClutterResult.Blocked;
    }

    private void CancelAssistedSlice(string reason, bool manualAttemptCompleted = false)
    {
        bool finishManualAttempt = manualAttemptCompleted || _manualGesture.InFlight;
        if (_slicer.Isolation is { Owner.IsDisposed: false } && reason.Length > 0)
            reason += _slicer.Isolation.ContentsReleased
                ? " Detached parts remain separated; the selected source remains empty and its engines are off."
                : " Detached parts remain separated.";
        _selectingSlice = false; _sliceHover = null;
        _sliceSequence.Cancel();
        _manualSliceActive = _manualIsolated = false;
        FinishManualContactAttempt(finishManualAttempt);
        _sliceSelection?.Dispose(); _sliceSelection = null;
        _sliceClutterRay = null;
        CancelClutterSlice();
        _slicer.Cancel();
        _sliceTarget = null; _sliceStarted = false; _sliceError = null;
        _pose.ClearSliceSwing();
        _sliceStatus = reason;
        _sliceMessageUntil = reason.Length > 0 ? _playerTime + 5 : 0;
    }

    // Main-thread handoff. Animation never waits for a plane, export or upload.
    private void UpdateAssistedSlice(double dtPlayer)
    {
        if (!_sliceSequence.Active || _owner == null) return;
        if (_fault != null || !_blade.Equipped || !_blade.TargetOn)
        { CancelAssistedSlice("Slice cancelled: saber was stowed."); return; }
        if (_sliceTarget == null)
        {
            _sliceSelection!.Advance();
            if (!_sliceSelection.Done) { _sliceStatus = "Selecting the part under the pointer..."; return; }
            if (_sliceSelection.SearchComplete && _sliceClutterRay is { } groundRay)
            {
                var selectedPart = _sliceSelection.Target;
                double surfaceDistance = _sliceSelection.SurfaceDistance;
                string noPartReason = _sliceSelection.Reason;
                CancelAssistedSlice("");
                if (TryBeginAssistedClutterSlice(groundRay, surfaceDistance, selectedPart?.Part) != AssistedClutterResult.Absent) return;
                if (selectedPart != null) BeginAssistedSlice(selectedPart, null);
                else { _selectingSlice = true; _sliceStatus = noPartReason; }
                return;
            }
            if (_sliceSelection.Target is not { } target)
            {
                string reason = _sliceSelection.Reason;
                CancelAssistedSlice(reason);
                _selectingSlice = true; return;
            }
            _sliceTarget = target;
            _sliceSelection.Dispose(); _sliceSelection = null;
            _sliceClutterRay = null;
        }
        if (!_sliceStarted)
        {
            // A click may have been accepted just before a clash began. Keep its
            // selection/preparation alive, but do not start a new slice worker
            // until the visual hit-stop has ended.
            if (_pose.ClashHeld) return;
            if (!TrySliceReach(out var baseBody, out var reach, out var reason))
            { _sliceStatus = reason; return; }
            if (!_targeting.CheckSliceTarget(_owner, _sliceTarget, baseBody, reach, out reason))
            { CancelAssistedSlice(reason); _selectingSlice = true; return; }
            _sliceStarted = true;
            try { _slicer.Begin(_sliceTarget.Part, diagnosticDetector: "assisted-selection", diagnosticTime: _sliceObservationTime); }
            catch (Exception error) { _sliceError = error.Message; }
        }
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckPendingSliceTarget(_owner, _sliceTarget, out var refusal))
        { _sliceError ??= refusal; _slicer.FinishSerialDiagnostic(SliceJobOutcome.Stale, _playerTime); }
        double speed = Universe.GetSimulationSpeed();
        if (!SaberLimits.SupportsSpeed(speed)) return;
        double3 bladeBaseBody = default;
        if (!_sliceSequence.EndPresented && (!_freshPoseThisTick || !_pose.TryGetBladeSegment(Length, out bladeBaseBody, out _))) return;
        // Ignition or fallback picking may span frames: validate again at the start of the stroke.
        if (!_sliceSequence.Swinging && _sliceSequence.Progress == 0
            && !_targeting.CheckSliceTarget(_owner, _sliceTarget, bladeBaseBody, SliceReach, out refusal))
        { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Stale, _playerTime); CancelAssistedSlice(refusal); _selectingSlice = true; return; }

        if (!_sliceSequence.EndPresented && !_sliceTarget.Vehicle.IsDisposed && Program.GetMainCamera() is { } camera)
        {
            var hitEgo = _sliceTarget.HitAssembly.Transform(_sliceTarget.Vehicle.GetMatrixAsmb2Ego(camera));
            var bodyToEgo = double4x4.CreateFromQuaternion(_owner.Body2Ego)
                * double4x4.CreateTranslation(camera.GetPositionEgo(_owner));
            if (double4x4.Invert(bodyToEgo, out var egoToBody)
                && double4x4.Invert(SaberPoseMath.ModelToBody(1, float3.Zero), out var bodyToModel))
            {
                var direction = hitEgo.Transform(egoToBody) - bladeBaseBody;
                double length = direction.Length();
                if (double.IsFinite(length) && length > .02)
                {
                    var modelDirection = double3.TransformNormal(direction, bodyToModel).NormalizeOrZero();
                    _sliceAim = new((float)modelDirection.X, (float)modelDirection.Y, (float)modelDirection.Z);
                }
            }
        }
        var cue = _sliceSequence.Advance(dtPlayer, _blade.Deployment >= .99f, _freshPoseThisTick || _sliceSequence.EndPresented,
            _slicer.Ready || _sliceError != null);
        if (!_sliceSequence.EndPresented) _pose.SetSliceSwing(_sliceAim, float3.UnitY, _sliceSequence.Progress);
        else _pose.ClearSliceSwing();
        if (_sliceSequence.Progress >= _sliceNextSpark && _sliceNextSpark <= .7
            && !_sliceTarget.Vehicle.IsDisposed && double4x4.Invert(_sliceTarget.PartTransform, out var assemblyToPart))
        {
            _renderer.AddCutSparks(_sliceTarget.Part, _sliceTarget.HitAssembly.Transform(assemblyToPart), double3.UnitY, _playerTime);
            _sliceNextSpark += .25;
        }
        _sliceStatus = !_sliceSequence.EndPresented ? "Slicing " + _sliceTarget.Part.DisplayName + "..." : _slicer.Phase;
        if (cue == SliceCue.Isolate)
        {
            if (_sliceError != null) { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime); CancelAssistedSlice("Cannot slice: " + _sliceError); return; }
            var result = IsolateSliceTarget();
            if (!result.Success) { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime); CancelAssistedSlice("Slice failed: " + result.Message); return; }
            _sliceStatus = _slicer.Phase;
            return;
        }
        if (cue == SliceCue.Commit)
        {
            if (_sliceError != null) { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime); CancelAssistedSlice("Cannot slice: " + _sliceError); return; }
            // Reach was checked at the click and stroke start. Moving afterward does not undo the cut.
            var result = _targeting.CommitAcceptedSlice(_owner, _sliceTarget, default);
            if (!result.Success) _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime);
            CancelAssistedSlice((result.Success ? "Slice complete: " : "Slice failed: ") + result.Message);
        }
    }

    private void HandleSlicePointer()
    {
        _sliceHover = null;
        _sliceHoverAllowed = false;
        if (!_selectingSlice || _owner == null || !Program.DrawUI || Program.EditorFlag) return;
        var io = ImGui.GetIO();
        if (OverlayCapturesPointer || io.WantCaptureMouse || io.WantTextInput || !CursorTarget.IsHitTestViewport(Program.MainViewport)) return;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { Queue(Command.CancelSlice); return; }
        if (_pose.ClashHeld) return;
        if (!TrySliceReach(out var baseBody, out var reach, out var reachReason))
        { _sliceStatus = reachReason; return; }
        var ray = Cursor.GetEgoRay(Program.MainViewport);
        ray.Direction = ray.Direction.NormalizeOrZero(); // Match the native picker's distance units.
        var picker = Program.MainViewport.PartPicker;
        string reason = "Point at a part, tree or rock and click to slice.";
        // Reuse the game's current-frame hover hit. No fleet-wide scan on each mouse move.
        if (picker.Part is { } hovered && double.IsFinite(picker.Distance))
            _sliceHover = _targeting.PreviewPointTarget(_owner, hovered, ray.Origin + ray.Direction * picker.Distance,
                baseBody, reach, out _sliceHoverAllowed, out reason);
        _sliceStatus = _sliceHoverAllowed && _sliceHover is { } hit ? "Click to slice " + hit.Part.DisplayName + "." : reason;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (picker.Part != null && _sliceHover == null) return;
        if (_commands.Count < 16)
        {
            _commands.Enqueue(new(_owner, Command.CutPicked, PartTarget: _sliceHover, PickRay: ray));
            _selectingSlice = false;
        }
    }

}
