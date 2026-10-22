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
    private SliceTarget? _sliceTarget;
    private SlicePlane _slicePlane;
    private object? _sliceSelectedVessel;
    private double _sliceLastFreshTime;
    private float _sliceBladeLength;
    private double _sliceNextSpark;
    private string _sliceStatus = "";
    private double SliceReach => Math.Max(3, _settings.BladeLength);

    private void BeginAssistedSlice()
    {
        if (_sliceSequence.Active || _owner == null) return;
        if (!SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()))
        { _sliceStatus = "Unpause and use 5x timewarp or less to slice a part."; return; }
        EnsureBlade();
        _blade.SetIgnited(true);
        _sliceTarget = null;
        _sliceSelectedVessel = _owner.Target;
        _sliceBladeLength = _settings.BladeLength;
        _sliceLastFreshTime = _playerTime;
        _sliceSequence.Start();
        _sliceNextSpark = .3;
        _sliceStatus = "Preparing saber for a nearby slice...";
    }

    private void CancelAssistedSlice(string reason)
    {
        _sliceSequence.Cancel();
        _sliceTarget = null;
        _sliceSelectedVessel = null;
        _sliceBladeLength = 0;
        _pose.ClearSliceSwing();
        _sliceStatus = reason;
    }

    // Runs only in BeforePhysics, after the previous visible pose and sim step are consumed.
    private void UpdateAssistedSlice(double dtPlayer, double simulationDt)
    {
        if (!_sliceSequence.Active || _owner == null) return;
        if (_fault != null) { CancelAssistedSlice("Slice cancelled: saber controls are suspended."); return; }
        if (!ReferenceEquals(_sliceSelectedVessel, _owner.Target) || !_blade.Equipped || !_blade.TargetOn)
        { CancelAssistedSlice("Slice cancelled: target or saber state changed."); return; }
        double speed = Universe.GetSimulationSpeed();
        if (!double.IsFinite(speed) || speed > SaberLimits.MaximumSimulationSpeed)
        { CancelAssistedSlice("Slice cancelled: use timewarp of 5x or less."); return; }
        if (speed <= 0) { _sliceLastFreshTime = _playerTime; return; }
        if (!_freshPoseThisTick || !_pose.TryGetBladeSegment(1, out var baseBody, out _))
        {
            if (_playerTime - _sliceLastFreshTime > 3)
                CancelAssistedSlice("Slice cancelled: keep the equipped kitten visible in the main view.");
            return;
        }
        _sliceLastFreshTime = _playerTime;
        if (simulationDt <= 0) return;
        if (_sliceTarget == null)
        {
            if (!_targeting.TryFindSliceTarget(_owner, baseBody, SliceReach, _sliceSelectedVessel as Vehicle,
                out var target, out string reason))
            { CancelAssistedSlice("Cannot slice: " + reason); return; }
            var plan = _slicer.PlanAssistedSlice(target!.Part, out _slicePlane);
            if (!plan.Success) { CancelAssistedSlice(plan.Message); return; }
            _sliceTarget = target;
            _sliceStatus = $"Slicing {target.Part.DisplayName} on {target.Vehicle.Id}...";
        }
        if (!_targeting.CheckSliceTarget(_owner, _sliceTarget, baseBody, SliceReach + .5, out string refusal))
        { CancelAssistedSlice(refusal); return; }

        // Track the locked section in target assembly space while either vessel drifts/rotates.
        var normal = new double3(_slicePlane.Normal.X, _slicePlane.Normal.Y, _slicePlane.Normal.Z);
        var hit = _sliceTarget.HitAssembly;
        var aim = hit + normal * (_slicePlane.Offset - double3.Dot(hit, normal));
        var camera = Program.GetMainCamera()!;
        var targetToEgo = _sliceTarget.Vehicle.GetMatrixAsmb2Ego(camera);
        var bodyToEgo = double4x4.CreateFromQuaternion(_owner.Body2Ego)
            * double4x4.CreateTranslation(camera.GetPositionEgo(_owner));
        if (!double4x4.Invert(bodyToEgo, out var egoToBody)
            || !double4x4.Invert(SaberPoseMath.ModelToBody(1, float3.Zero), out var bodyToModel))
        { CancelAssistedSlice("Slice cancelled: saber alignment is unavailable."); return; }
        var aimBody = aim.Transform(targetToEgo * egoToBody);
        var direction = aimBody - baseBody;
        double length = direction.Length();
        if (!double.IsFinite(length) || length < .02 || length > SaberLimits.MaximumBladeLength - .25)
        { CancelAssistedSlice("Move beside the middle of this part so the saber can reach its cut section."); return; }
        // Temporary extension only; the player's blade-length preference is unchanged.
        _sliceBladeLength = (float)Math.Min(SaberLimits.MaximumBladeLength, Math.Max(_settings.BladeLength, length + .5));
        var modelDirection = double3.TransformNormal(direction, bodyToModel).NormalizeOrZero();
        var modelNormal = double3.TransformNormal(normal, targetToEgo * egoToBody * bodyToModel).NormalizeOrZero();
        var cue = _sliceSequence.Advance(dtPlayer, _blade.Deployment >= .99f, _freshPoseThisTick);
        _pose.SetSliceSwing(new((float)modelDirection.X, (float)modelDirection.Y, (float)modelDirection.Z),
            new((float)modelNormal.X, (float)modelNormal.Y, (float)modelNormal.Z), _sliceSequence.Progress);
        if (_sliceSequence.Progress >= _sliceNextSpark && _sliceNextSpark <= .6
            && double4x4.Invert(_sliceTarget.PartTransform, out var assemblyToPart))
        {
            _renderer.AddCutSparks(_sliceTarget.Part, aim.Transform(assemblyToPart),
                double3.TransformNormal(normal, assemblyToPart).NormalizeOrZero(), _playerTime);
            _sliceNextSpark += .15;
        }
        if (cue == SliceCue.Commit)
        {
            // Sequence consumes the request before this call, so neither an error nor another
            // frame can repeat a structural cut.
            var result = _targeting.CommitAssistedSlice(_owner, _sliceTarget, baseBody, SliceReach + .5, _slicePlane);
            CancelAssistedSlice((result.Success ? "Slice complete: " : "Slice failed: ") + result.Message);
        }
    }

    private void DrawAssistedSliceUi(bool hasKitten)
    {
        var owner = Program.ControlledVehicle as KittenEva;
        ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null || _sliceSequence.Active);
        try
        {
            if (ImGui.BeginCombo("Vessel target", owner?.Target?.Id ?? "Nearest nearby vessel"))
            {
                try
                {
                    if (ImGui.Selectable("Nearest nearby vessel", owner?.Target == null)) QueueTarget(null);
                    if (Universe.CurrentSystem is { } system)
                        foreach (var item in system.All.AsSpan())
                            if (item is Vehicle vessel && vessel is not KittenEva && !vessel.IsDisposed && !vessel.IsEditedVehicle
                                && ImGui.Selectable(vessel.Id + "##SliceTarget" + vessel.Hash, ReferenceEquals(owner?.Target, vessel)))
                                QueueTarget(vessel);
                }
                finally { ImGui.EndCombo(); }
            }
        }
        finally { ImGui.EndDisabled(); }
        ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null);
        try
        {
            if (_sliceSequence.Active)
            {
                if (ImGui.Button("Cancel slice")) Queue(Command.CancelSlice);
                ImGui.ProgressBar((float)_sliceSequence.Progress, new float2(-1, 0), "Saber slice");
            }
            else if (ImGui.Button("Slice nearby part", new float2(-1, 32))) Queue(Command.SliceNearby);
        }
        finally { ImGui.EndDisabled(); }
        ImGui.TextWrapped(Program.ControlledVehicle?.Target is Vehicle currentTarget
            ? "Slice target: " + currentTarget.Id + " (nearest part)" : "Slice target: nearest nearby vessel part");
        ImGui.TextWrapped("One click: equip, ignite, swing, then split the nearest part into pieces. Touching a part makes sparks only; the slice button cuts it.");
        ImGui.TextWrapped("Slices within 3 m or your blade's reach. Tank contents are emptied; occupied vessels are protected.");
        if (_sliceStatus.Length > 0) ImGui.TextWrapped(_sliceStatus);
    }
}
