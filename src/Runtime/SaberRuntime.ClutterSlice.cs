using Brutal.Numerics;
using KSA;
using Purr2D2.Force;
using Purr2D2.Slicing;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ClutterSlicer _clutterSlicer = new();
    private readonly Dictionary<ClutterForceIdentity, double> _clutterContacts = [];
    private double3 _clutterPreviousBase, _clutterPreviousTip;
    private double _clutterPreviousTime, _clutterNextAttempt;
    private float _clutterPreviousNominalLength;
    private float3 _clutterPreviousAxes;
    private bool _clutterHasPrevious;
    private bool ClutterSliceWorkActive => _clutterSlicer.Active || _clutterSlicer.Busy;

    // Called at the existing joined handoff, after manual part contact. Falling,
    // jumping and walking all use this same native surface-bubble query.
    private void UpdateClutterSlicing(bool simulationAdvancing)
    {
        _clutterSlicer.CutSparks ??= _renderer.AddCutSparks;
        _clutterSlicer.TreeFire ??= _renderer.AddTreeCutFire;
        bool wasActive = _clutterSlicer.Active;
        bool eligible = _fault == null && _owner is { IsDisposed: false } && SaberHeldForActions
            && _blade.Equipped && _blade.TargetOn && _blade.Deployment > .001f && _pose.Eligible
            && !ForceHolding && !ForcePicking && !_pose.ClashHeld;
        if (!eligible && _clutterSlicer.Active) _clutterSlicer.Cancel();
        _clutterSlicer.Update(_owner, eligible && simulationAdvancing && _freshPoseThisTick,
            _playerTime, _settings.TreeCutFireSeconds);
        if (wasActive && !_clutterSlicer.Active && _clutterSlicer.Status.Length != 0)
        { _sliceStatus = _clutterSlicer.Status; _sliceMessageUntil = _playerTime + 5; }
        if (!eligible || _sliceSequence.Active || _manualSliceActive || _slicer.Busy || _selectingSlice || ClutterSliceWorkActive)
        { _clutterHasPrevious = false; return; }
        if (!simulationAdvancing || !_freshPoseThisTick) return;
        if (!_pose.TryGetBladeSegment(Length, out var baseBody, out var tipBody))
        { _clutterHasPrevious = false; return; }
        var rotation = _owner!.GetBody2Cci();
        var position = _owner.GetPositionCci();
        var bubble = ClutterForceNative.OwnerBubble(_owner);
        if (bubble == null || bubble.Origin.BubFrame != BubbleFrame.Ccf)
        { _clutterHasPrevious = false; return; }
        var cci2Ccf = _owner.Parent.GetCci2Ccf(bubble.Origin.Time);
        var ccf2Cci = cci2Ccf.Inverse();
        // Surface motion is measured in the native fixed frame. Comparing CCI
        // endpoints would treat planetary rotation as a high-speed saber swing.
        var bladeBase = (position + baseBody.Transform(rotation)).Transform(cci2Ccf);
        var bladeTip = (position + tipBody.Transform(rotation)).Transform(cci2Ccf);
        double elapsed = _playerTime - _clutterPreviousTime;
        var baseMotion = bladeBase - _clutterPreviousBase;
        var tipMotion = bladeTip - _clutterPreviousTip;
        double travel = Math.Max(baseMotion.LengthSquared(), tipMotion.LengthSquared());
        double length = (bladeTip - bladeBase).Length();
        var axes = _pose.VisualScaleAxes;
        double hiltLimit = Math.Max(1, _pose.VisualScale);
        double endpointLimit = Math.Max(hiltLimit, Math.Max(1, length * 1.5));
        bool moving = _clutterHasPrevious && elapsed > 0 && elapsed <= .2 && travel >= .000004
            && baseMotion.LengthSquared() <= hiltLimit * hiltLimit && travel <= endpointLimit * endpointLimit
            && Math.Abs(Length - _clutterPreviousNominalLength) <= .001
            && SameClutterVisualAxis(axes.X, _clutterPreviousAxes.X)
            && SameClutterVisualAxis(axes.Y, _clutterPreviousAxes.Y) && SameClutterVisualAxis(axes.Z, _clutterPreviousAxes.Z);
        var previousBase = _clutterPreviousBase; var previousTip = _clutterPreviousTip;
        _clutterPreviousBase = bladeBase; _clutterPreviousTip = bladeTip;
        _clutterPreviousTime = _playerTime; _clutterHasPrevious = true;
        _clutterPreviousNominalLength = Length; _clutterPreviousAxes = axes;
        if (!moving || _playerTime < _clutterNextAttempt) return;
        if (_clutterContacts.Count >= 128)
        {
            foreach (var old in _clutterContacts.Where(p => p.Value <= _playerTime).Select(p => p.Key).ToArray()) _clutterContacts.Remove(old);
            if (_clutterContacts.Count >= 128) return;
        }
        // The current blade and eight blade-point trajectories cover a bounded
        // moving ribbon. Native ray hits are accepted only inside that segment.
        if (TryClutterSegment(bladeBase.Transform(ccf2Cci), bladeTip.Transform(ccf2Cci),
            (bladeTip - bladeBase).Transform(ccf2Cci), (baseMotion + tipMotion).Transform(ccf2Cci))) return;
        for (int i = 1; i <= 8; i++)
        {
            double t = i / 8.0;
            var before = previousBase + (previousTip - previousBase) * t;
            var after = bladeBase + (bladeTip - bladeBase) * t;
            if (TryClutterSegment(before.Transform(ccf2Cci), after.Transform(ccf2Cci),
                (bladeTip - bladeBase).Transform(ccf2Cci), (after - before).Transform(ccf2Cci))) return;
        }
    }

    private static bool SameClutterVisualAxis(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && a > 0 && b > 0
        && Math.Abs(a - b) <= Math.Max(a, b) * .0002;

    private Ray? SnapshotClutterSliceRay(Ray egoRay)
    {
        if (_owner == null || !ForceTargeting.TrySnapshotRay(_owner, egoRay, out var snapshot)) return null;
        var ray = snapshot.OwnerRelativeCci;
        ray.Origin += _owner.GetPositionCci();
        return ray;
    }

    private bool TryClutterSegment(double3 from, double3 to, double3 blade, double3 motion)
    {
        double length = (to - from).Length();
        if (!double.IsFinite(length) || length < .001 || motion.LengthSquared() < 1e-12) return false;
        var ray = new Ray(from, (to - from) / length);
        if (!ClutterForceTargets.TryPick(_owner!, ray, out var target, out double distance, out var contact, out _)
            || target == null || distance < 0 || distance > length + .005
            || _clutterContacts.TryGetValue(target.StableKey, out double until) && _playerTime < until) return false;
        bool accepted = _clutterSlicer.Begin(_owner!, target, contact, blade, motion);
        _clutterContacts[target.StableKey] = _playerTime + (accepted ? 10 : .25);
        _clutterNextAttempt = _playerTime + .25;
        if (accepted) { _sliceStatus = "Preparing " + target.DisplayName + " cut..."; _sliceMessageUntil = _playerTime + 5; }
        return accepted;
    }

    // A completed query can confirm absence; inability to query or capture is a
    // blocker, never authorization to cut a previously selected surface behind it.
    private AssistedClutterResult TryBeginAssistedClutterSlice(Ray ray,
        double maximumRayDistance = double.PositiveInfinity, Part? selectedPart = null)
    {
        if (_owner == null || double.IsNaN(maximumRayDistance) || !SaberHeldForActions || _sliceSequence.Active
            || _manualSliceActive || _slicer.Busy || ClutterSliceWorkActive)
            return BlockAssistedClutter("The saber or its previous cut is unavailable.");
        if (!TrySliceReach(out var baseBody, out double reach, out string reason)) return BlockAssistedClutter(reason);
        var snapshot = new ForcePickRay(_owner.System, _owner.Parent,
            new Ray(ray.Origin - _owner.GetPositionCci(), ray.Direction));
        // Reuse the unified native picker for nearest-surface and terrain
        // occlusion, with zero aim tolerance so this remains a physical hit.
        if (!ForceTargeting.TryPick(_owner, snapshot, out var picked, out reason, clutterTolerance: 0) || picked == null)
            return BlockAssistedClutter(reason.Length > 0 ? reason : "The nearest surface could not be confirmed.");
        var contact = picked.HitCci;
        double distance = double3.Dot(contact - ray.Origin, ray.Direction.NormalizeOrZero());
        if (!double.IsFinite(distance) || distance < 0) return BlockAssistedClutter("The nearest surface has an invalid distance.");
        if (picked.Clutter is not { } target)
        {
            // Native picker meshes and rendered triangles can differ slightly on
            // one structural part. A different clearly nearer vessel remains an
            // occluder to a stale/background selection.
            bool samePart = selectedPart != null && ReferenceEquals(picked.Part?.StructuralPart, selectedPart.StructuralPart);
            double tolerance = Math.Max(.001, Math.Abs(maximumRayDistance) * 1e-8);
            if (!samePart && (!double.IsFinite(maximumRayDistance) || distance < maximumRayDistance - tolerance))
                return BlockAssistedClutter("Another vessel surface is in front of the selected part.");
            return AssistedClutterResult.Absent;
        }
        if (distance >= maximumRayDistance) return AssistedClutterResult.Absent;
        var rotation = _owner.GetBody2Cci();
        var emitter = _owner.GetPositionCci() + baseBody.Transform(rotation);
        if ((contact - emitter).Length() > reach)
            return BlockAssistedClutter("The foreground ground object is beyond saber reach.");
        if (!_pose.TryGetBladeSegment(Length, out var a, out var b)) return BlockAssistedClutter("The current saber pose is unavailable.");
        var blade = (b - a).Transform(rotation);
        var motion = double3.Cross(blade, _owner.GetPositionCci().NormalizeOrZero());
        if (motion.LengthSquared() < 1e-12) motion = double3.Cross(blade, double3.UnitX);
        if (!_clutterSlicer.Begin(_owner, target, contact, blade, motion))
            return BlockAssistedClutter("The foreground ground object cannot be sliced safely.");
        _selectingSlice = false; _sliceHover = null; _sliceStatus = "Preparing ground-object cut...";
        _clutterContacts[target.StableKey] = _playerTime + 10;
        return AssistedClutterResult.Started;
    }

    private void ResetClutterSlicing()
    {
        _clutterSlicer.Cancel(); _clutterContacts.Clear();
        _clutterHasPrevious = false; _clutterNextAttempt = 0;
        _renderer.ClearTreeFire();
    }
    private void CancelClutterSlice() { _clutterSlicer.Cancel(); _clutterHasPrevious = false; }
    private void DisposeClutterSlicing() => _clutterSlicer.Dispose();
    private void PumpClutterSliceRetired() => _clutterSlicer.PumpRetired();
}
