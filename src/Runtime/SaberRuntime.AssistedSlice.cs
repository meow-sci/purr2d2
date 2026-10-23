using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly AssistedSliceSequence _sliceSequence = new();
    private SliceTarget? _sliceTarget, _sliceHover;
    private bool _sliceHoverAllowed;
    private PartSliceTargeting.Selection? _sliceSelection;
    private bool _selectingSlice, _sliceStarted;
    private string? _sliceError;
    private float3 _sliceAim = float3.UnitZ;
    private double _sliceNextSpark;
    private double _sliceMessageUntil;
    private string _sliceStatus = "";
    private bool SliceStatusVisible => _sliceStatus.Length > 0
        && (_selectingSlice || _sliceSequence.Active || _playerTime < _sliceMessageUntil);
    // Use the rendered blade length and live hilt position, never a kitten-centred allowance.
    private bool TrySliceReach(out double3 baseBody, out double reach, out string reason)
    {
        baseBody = default; reach = Length;
        reason = "Keep the lit saber visible to select a part within blade reach.";
        if (!_blade.Equipped || !_blade.TargetOn || !double.IsFinite(reach) || reach <= 0
            || !_pose.TryGetBladeSegment(reach, out baseBody, out _)) return false;
        reason = "";
        return true;
    }

    private void SelectPartToSlice()
    {
        if (_sliceSequence.Active || _owner == null) return;
        if (_slicer.Busy) { _sliceStatus = "Finishing the previous slice; try again shortly."; _sliceMessageUntil = _playerTime + 5; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = true;
        _sliceStatus = "Point at a part and click to slice. Right-click or Esc to cancel.";
    }

    private void BeginAssistedSlice(SliceTarget? target, Ray? ray)
    {
        if (_sliceSequence.Active || _owner == null || _slicer.Busy) return;
        if (!TrySliceReach(out var baseBody, out var reach, out var reason)
            || (target != null && !_targeting.CheckSliceTarget(_owner, target, baseBody, reach, out reason)))
        { _selectingSlice = true; _sliceStatus = reason; return; }
        EnsureBlade(); _blade.SetIgnited(true);
        _selectingSlice = false; _sliceHover = null;
        _sliceTarget = target;
        if (target == null && ray is { } pick)
            _sliceSelection = _targeting.BeginSelection(_owner, pick.Origin, pick.Direction, baseBody, reach);
        if (target == null && _sliceSelection == null) return;
        _sliceStarted = false; _sliceError = null;
        _sliceSequence.Start(); _sliceNextSpark = .2;
        _sliceStatus = "Slicing the selected part...";
    }

    private void CancelAssistedSlice(string reason)
    {
        _selectingSlice = false; _sliceHover = null;
        _sliceSequence.Cancel();
        _sliceSelection?.Dispose(); _sliceSelection = null;
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
            if (_sliceSelection.Target is not { } target)
            {
                string reason = _sliceSelection.Reason;
                CancelAssistedSlice(reason); _selectingSlice = true; return;
            }
            _sliceTarget = target;
            _sliceSelection.Dispose(); _sliceSelection = null;
        }
        if (!_sliceStarted)
        {
            if (!TrySliceReach(out var baseBody, out var reach, out var reason))
            { _sliceStatus = reason; return; }
            if (!_targeting.CheckSliceTarget(_owner, _sliceTarget, baseBody, reach, out reason))
            { CancelAssistedSlice(reason); _selectingSlice = true; return; }
            _sliceStarted = true;
            try { _slicer.Begin(_sliceTarget.Part); }
            catch (Exception error) { _sliceError = error.Message; }
        }
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckSliceTarget(_owner, _sliceTarget, default, double.PositiveInfinity, out var refusal))
            _sliceError ??= refusal;
        double speed = Universe.GetSimulationSpeed();
        if (!SaberLimits.SupportsSpeed(speed)) return;
        double3 bladeBaseBody = default;
        if (!_sliceSequence.EndPresented && (!_freshPoseThisTick || !_pose.TryGetBladeSegment(Length, out bladeBaseBody, out _))) return;
        // Ignition or fallback picking may span frames: validate again at the start of the stroke.
        if (!_sliceSequence.Swinging && _sliceSequence.Progress == 0
            && !_targeting.CheckSliceTarget(_owner, _sliceTarget, bladeBaseBody, Length, out refusal))
        { CancelAssistedSlice(refusal); _selectingSlice = true; return; }

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
        if (cue == SliceCue.Commit)
        {
            if (_sliceError != null) { CancelAssistedSlice("Cannot slice: " + _sliceError); return; }
            // Reach was checked at the click and stroke start. Moving afterward does not undo the cut.
            var result = _targeting.CommitAssistedSlice(_owner, _sliceTarget, default, double.PositiveInfinity, default);
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
        if (!TrySliceReach(out var baseBody, out var reach, out var reachReason))
        { _sliceStatus = reachReason; return; }
        var ray = Cursor.GetEgoRay(Program.MainViewport);
        ray.Direction = ray.Direction.NormalizeOrZero(); // Match the native picker's distance units.
        var picker = Program.MainViewport.PartPicker;
        string reason = "Point at a vessel part and click to slice.";
        // Reuse the game's current-frame hover hit. No fleet-wide scan on each mouse move.
        if (picker.Part is { } hovered && double.IsFinite(picker.Distance))
            _sliceHover = _targeting.PreviewPointTarget(_owner, hovered, ray.Origin + ray.Direction * picker.Distance,
                baseBody, reach, out _sliceHoverAllowed, out reason);
        _sliceStatus = _sliceHoverAllowed && _sliceHover is { } hit ? "Click to slice " + hit.Part.DisplayName + "." : reason;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (picker.Part != null && !_sliceHoverAllowed) return;
        if (_commands.Count < 16)
        {
            _commands.Enqueue(new(_owner, Command.CutPicked, PartTarget: _sliceHover, PickRay: ray));
            _selectingSlice = false;
        }
    }

    private void DrawAssistedSliceUi(bool hasKitten)
    {
        var owner = Program.ControlledVehicle as KittenEva;
        ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null);
        try
        {
            ImGui.Text("Vessel target");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##VesselTarget", owner?.Target?.Id ?? "None"))
            {
                try
                {
                    if (ImGui.Selectable("None", owner?.Target == null)) QueueTarget(null);
                    if (Universe.CurrentSystem is { } system)
                        foreach (var item in system.All.AsSpan())
                            if (item is Vehicle vessel && vessel is not KittenEva && !vessel.IsDisposed && !vessel.IsEditedVehicle
                                && ImGui.Selectable(vessel.Id + "##SliceTarget" + vessel.Hash, ReferenceEquals(owner?.Target, vessel))) QueueTarget(vessel);
                }
                finally { ImGui.EndCombo(); }
            }
            if (_sliceSequence.Active || _selectingSlice)
            {
                if (ImGui.Button("Cancel slice")) Queue(Command.CancelSlice);
                if (_sliceSequence.Active && !_sliceSequence.EndPresented)
                    ImGui.ProgressBar((float)_sliceSequence.Progress, new float2(-1, 0), "Saber swing");
            }
            else if (ImGui.Button("Select part to slice", new float2(-1, 32))) Queue(Command.SelectSlice);
        }
        finally { ImGui.EndDisabled(); }
        if (SliceStatusVisible) ImGui.TextWrapped(_sliceStatus);
    }
}
