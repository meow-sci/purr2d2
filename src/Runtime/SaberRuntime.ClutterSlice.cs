using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Slicing;
using Purr2D2.Slicing.Detection;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ClutterSlicer _clutterSlicer = new();
    private ClutterSliceQueue? _clutterQueue;
    private int _clutterQueueWorkers, _clutterQueueLimit, _clutterQueueMemory;
    private bool _cancelQueuedClutterRequested;
    private string _activeClutterDetector = "legacy-rays";
    private string _lastQueuedClutterStatus = "";
    private readonly AllHitClutterDetector _allClutterHits = new();
    // Keep frozen manual and passive evidence separate: disabling passive cuts
    // must not discard an already observed manual interval.
    private readonly CapsuleClutterDetector _manualCapsuleHits = new();
    private readonly CapsuleClutterDetector _passiveCapsuleHits = new();
    private readonly List<ClutterCapsuleContact> _capsuleHitBuffer = new();
    private readonly List<ClutterRayContact> _clutterHitBuffer = new();
    private readonly Dictionary<ClutterForceIdentity, double> _clutterContacts = [];
    private readonly HashSet<ClutterForceIdentity> _legacyDiagnosticHits = [];
    private double3 _clutterPreviousBase, _clutterPreviousTip;
    private double _clutterPreviousTime, _clutterNextAttempt;
    private float _clutterPreviousNominalLength;
    private float3 _clutterPreviousAxes;
    private bool _clutterHasPrevious;
    private ulong _clutterActivationEpoch;
    private float2 _clutterPreviousDrag;
    private bool ClutterSliceWorkActive => _clutterSlicer.Active || _clutterSlicer.Busy || _clutterQueue is { Active: true };

    // Called at the existing joined handoff, after manual part contact. Falling,
    // jumping and walking all use this same native surface-bubble query.
    private void UpdateClutterSlicing(bool simulationAdvancing)
    {
        if (_cancelQueuedClutterRequested)
        {
            _cancelQueuedClutterRequested = false;
            _clutterQueue?.Cancel();
            _vesselQueue?.Cancel();
            _clutterSlicer.Cancel();
            _clutterContacts.Clear();
            _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset();
            _clutterHasPrevious = false;
            CancelAssistedSlice("Pending cuts cancelled.");
        }
        _clutterSlicer.CutSparks ??= _renderer.AddCutSparks;
        _clutterSlicer.TreeFire ??= _renderer.AddTreeCutFire;
        bool wasActive = _clutterSlicer.Active;
        bool eligible = _fault == null && _owner is { IsDisposed: false } && SaberHeldForActions
            && _blade.Equipped && _blade.TargetOn && _blade.Deployment > .001f && _pose.Eligible
            && !ForceHolding && !ForcePicking && !_pose.ClashHeld;
        if (!eligible && _clutterSlicer.Active) _clutterSlicer.Cancel();
        _clutterSlicer.Update(_owner, eligible && simulationAdvancing && _freshPoseThisTick,
            _playerTime, _settings.TreeCutFireSeconds);
        bool wantsQueue = _settings.SliceExecutionMode == "queued";
        bool reconfigure = _clutterQueue != null && (_clutterQueueWorkers != _settings.SliceWorkerCount
            || _clutterQueueLimit != _settings.SlicePendingTargetLimit || _clutterQueueMemory != _settings.SliceWorkingMemoryMiB);
        if ((!wantsQueue || reconfigure) && _clutterQueue is { Active: false } && _vesselQueue is not { Active: true })
        { _clutterQueue.Dispose(); _clutterQueue = null; }
        if (wantsQueue && _clutterQueue == null && !_clutterSlicer.Active && !_clutterSlicer.Busy && !_sliceSequence.Active && !_manualSliceActive && !_slicer.Busy)
        {
            _clutterQueue = new(_settings.SliceWorkerCount, _settings.SlicePendingTargetLimit, _settings.SliceWorkingMemoryMiB)
            { CutSparks = _renderer.AddCutSparks, TreeFire = _renderer.AddTreeCutFire,
                FragmentCreated = (part, stroke) => _vesselProvenance.Mark(part.StructuralPart, stroke) };
            _clutterQueueWorkers = _settings.SliceWorkerCount; _clutterQueueLimit = _settings.SlicePendingTargetLimit;
            _clutterQueueMemory = _settings.SliceWorkingMemoryMiB;
            reconfigure = false;
        }
        _clutterQueue?.Update(_owner, !_vesselCommitLane && simulationAdvancing && _owner is { IsDisposed: false },
            _playerTime, _settings.TreeCutFireSeconds, !_vesselCaptureLane, !_vesselUploadLane,
            _vesselQueue?.ChargedBytes ?? 0, _vesselQueue?.ReservedVehicles ?? 0, _vesselQueue?.ReservationCount ?? 0,
            _vesselQueue?.LoadSlots ?? 0);
        if (_clutterQueue is { Status.Length: > 0 } queue && queue.Status != _lastQueuedClutterStatus)
        { _lastQueuedClutterStatus = queue.Status; _sliceStatus = queue.Status; _sliceMessageUntil = _playerTime + 5; }
        bool queued = wantsQueue && !reconfigure && _clutterQueue != null;
        if (wasActive && !_clutterSlicer.Active && _clutterSlicer.Status.Length != 0)
        { _sliceStatus = _clutterSlicer.Status; _sliceMessageUntil = _playerTime + 5; }
        if (_clutterActivationEpoch != _sliceActivation.Epoch)
        { _clutterActivationEpoch = _sliceActivation.Epoch; _clutterHasPrevious = false; }
        string detector = _settings.ClutterContactDetector switch
        { "all-hit-rays" => "all-hit-rays", "capsule-sweep" => "capsule-sweep", _ => "legacy-rays" };
        if (detector != _activeClutterDetector)
        {
            _activeClutterDetector = detector; _clutterHasPrevious = false; _allClutterHits.Reset();
            _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset();
        }
        if (!_settings.PassiveContactCutsEnabled) _passiveCapsuleHits.Reset();
        if (!eligible || _sliceSequence.Active || _manualSliceActive || _slicer.Busy || _selectingSlice)
        {
            _clutterHasPrevious = false;
            _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset();
            return;
        }
        if ((!queued && ClutterSliceWorkActive) || (wantsQueue && !queued) || (!wantsQueue && _clutterQueue is { Active: true }))
        { _clutterHasPrevious = false; return; }
        if (!simulationAdvancing) return;

        // Drain frozen intervals even when SWING has been released or no new
        // pose is available. Detect(false) never submits post-release motion.
        long capsuleBudgetStarted = Stopwatch.GetTimestamp();
        int capsuleRaysRemaining = 256;
        if (detector == "capsule-sweep" && (_manualCapsuleHits.HasPending || _passiveCapsuleHits.HasPending))
        {
            var confirmationBubble = ClutterForceNative.OwnerBubble(_owner!);
            if (confirmationBubble == null || confirmationBubble.Origin.BubFrame != BubbleFrame.Ccf)
            { _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset(); _clutterHasPrevious = false; return; }
            var confirmationRotation = _owner!.Parent.GetCci2Ccf(confirmationBubble.Origin.Time).Inverse();
            if (_manualCapsuleHits.HasPending)
                capsuleRaysRemaining -= DrainCapsuleContacts(_manualCapsuleHits, SliceContactAuthorization.ManualContact,
                    confirmationRotation, queued, capsuleBudgetStarted, capsuleRaysRemaining);
            if (_passiveCapsuleHits.HasPending && (queued || !ClutterSliceWorkActive))
                capsuleRaysRemaining -= DrainCapsuleContacts(_passiveCapsuleHits, SliceContactAuthorization.PassiveContact,
                    confirmationRotation, queued, capsuleBudgetStarted, capsuleRaysRemaining);
            if (!queued && ClutterSliceWorkActive) { _clutterHasPrevious = false; return; }
        }
        if (_sliceActivation.Kind == SliceContactAuthorization.None)
        { _clutterHasPrevious = false; return; }
        if (!_freshPoseThisTick) return;
        if (_sliceActivation.Kind == SliceContactAuthorization.ManualContact && !_manualPoseAvailable)
        { _clutterHasPrevious = false; return; }
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
            && (_sliceActivation.Kind == SliceContactAuthorization.PassiveContact
                || _manualPoseAvailable && (_manualPose.Drag - _clutterPreviousDrag).LengthSquared() >= .000001f)
            && baseMotion.LengthSquared() <= hiltLimit * hiltLimit && travel <= endpointLimit * endpointLimit
            && Math.Abs(Length - _clutterPreviousNominalLength) <= .001
            && SameClutterVisualAxis(axes.X, _clutterPreviousAxes.X)
            && SameClutterVisualAxis(axes.Y, _clutterPreviousAxes.Y) && SameClutterVisualAxis(axes.Z, _clutterPreviousAxes.Z);
        var previousBase = _clutterPreviousBase; var previousTip = _clutterPreviousTip;
        _clutterPreviousBase = bladeBase; _clutterPreviousTip = bladeTip;
        _clutterPreviousTime = _playerTime; _clutterHasPrevious = true;
        _clutterPreviousDrag = _manualPose.Drag;
        _clutterPreviousNominalLength = Length; _clutterPreviousAxes = axes;
        if (!moving || !queued && _playerTime < _clutterNextAttempt) return;
        if (_clutterContacts.Count >= 128)
        {
            foreach (var old in _clutterContacts.Where(p => p.Value <= _playerTime).Select(p => p.Key).ToArray()) _clutterContacts.Remove(old);
            if (_clutterContacts.Count >= 128) return;
        }
        var sweep = _settings.EffectiveContactPlaneStrategy == "sweep-fit"
            ? new SliceRibbonMotion(Point(previousBase.Transform(ccf2Cci)), Point(previousTip.Transform(ccf2Cci)),
                Point(bladeBase.Transform(ccf2Cci)), Point(bladeTip.Transform(ccf2Cci)), 0, 0)
            : (SliceRibbonMotion?)null;
        if (detector == "capsule-sweep")
        {
            var selected = _sliceActivation.Kind == SliceContactAuthorization.ManualContact ? _manualCapsuleHits : _passiveCapsuleHits;
            var progress = selected.Detect(_owner, previousBase, previousTip, bladeBase, bladeTip,
                ccf2Cci, _playerTime, true, _clutterQueue?.Pending, _capsuleHitBuffer,
                capsuleBudgetStarted, capsuleRaysRemaining, _sliceActivation.Epoch);
            AdmitCapsuleContacts(_sliceActivation.Kind, queued);
            ReportCapsuleProgress(progress);
            return;
        }
        if (detector == "all-hit-rays")
        {
            var progress = _allClutterHits.Detect(_owner, previousBase, previousTip, bladeBase, bladeTip,
                ccf2Cci, _clutterQueue?.Pending, _clutterHitBuffer);
            foreach (var hit in _clutterHitBuffer)
            {
                if (_clutterContacts.TryGetValue(hit.Target.StableKey, out double until) && _playerTime < until) continue;
                bool accepted = queued
                    ? _clutterQueue!.Begin(_owner, hit.Target, hit.PointCci, hit.BladeCci, hit.MotionCci,
                        _settings.EffectiveContactPlaneStrategy, _sliceActivation.Kind.ToString(),
                        sweep is { } ribbon ? (ribbon with { HitTime01 = hit.HitTime01, AlongBlade01 = hit.AlongBlade01 }).ToSamples() : null,
                        _sliceActivation.Epoch, "all-hit-rays", _playerTime)
                    : _clutterSlicer.Begin(_owner, hit.Target, hit.PointCci, hit.BladeCci, hit.MotionCci,
                        _settings.EffectiveContactPlaneStrategy, _sliceActivation.Kind.ToString(),
                        sweep is { } ribbon2 ? (ribbon2 with { HitTime01 = hit.HitTime01, AlongBlade01 = hit.AlongBlade01 }).ToSamples() : null,
                        "all-hit-rays", _playerTime);
                if (accepted) _clutterContacts[hit.Target.StableKey] = _playerTime + 10;
                if (!accepted && _clutterQueue is { Full: true })
                { _sliceStatus = "Ground cut queue is full; some contacts were not queued."; _sliceMessageUntil = _playerTime + 5; break; }
                if (accepted && !queued) { _clutterNextAttempt = _playerTime + .25; break; }
            }
            if (!progress.Complete)
            {
                _sliceStatus = progress.CoverageUnavailable ? "Clutter collision coverage is unavailable."
                    : $"Clutter detection incomplete: {progress.UnprocessedRays} paths, {progress.CandidateOverflow} overflow.";
                _sliceMessageUntil = _playerTime + 5;
            }
            return;
        }
        // The current blade and eight blade-point trajectories cover a bounded
        // moving ribbon. Native ray hits are accepted only inside that segment.
        long detectorStarted = SliceDetectionDiagnostics.Start();
        _legacyDiagnosticHits.Clear();
        try
        {
            if (TryClutterSegment(bladeBase.Transform(ccf2Cci), bladeTip.Transform(ccf2Cci),
                (bladeTip - bladeBase).Transform(ccf2Cci), (baseMotion + tipMotion).Transform(ccf2Cci), sweep, true, 0)) return;
            for (int i = 1; i <= 8; i++)
            {
                double t = i / 8.0;
                var before = previousBase + (previousTip - previousBase) * t;
                var after = bladeBase + (bladeTip - bladeBase) * t;
                if (TryClutterSegment(before.Transform(ccf2Cci), after.Transform(ccf2Cci),
                    (bladeTip - bladeBase).Transform(ccf2Cci), (after - before).Transform(ccf2Cci), sweep, false, t)) return;
            }
        }
        finally { SliceDetectionDiagnostics.Record(SliceDetectorKind.LegacyRays, detectorStarted, _legacyDiagnosticHits.Count, false); }
    }

    private int DrainCapsuleContacts(CapsuleClutterDetector detector, SliceContactAuthorization trigger,
        doubleQuat ccf2Cci, bool queued, long budgetStarted, int remainingRays)
    {
        var progress = detector.Detect(_owner!, default, default, default, default, ccf2Cci,
            _playerTime, false, _clutterQueue?.Pending, _capsuleHitBuffer, budgetStarted, remainingRays);
        AdmitCapsuleContacts(trigger, queued);
        ReportCapsuleProgress(progress);
        return progress.Rays;
    }

    private void AdmitCapsuleContacts(SliceContactAuthorization trigger, bool queued)
    {
        foreach (var hit in _capsuleHitBuffer)
        {
            // The detector revalidates before confirmation; check again at the
            // admission boundary so stale or migrated targets never start a job.
            var target = hit.Target;
            if (!hit.AnchorRefined || _owner == null || !target.IsLocalTo(_owner)
                || !ReferenceEquals(target.Bubble, ClutterForceNative.OwnerBubble(_owner))
                || !target.TryResolve(out _, out _, out _, out _)) continue;
            if (_clutterContacts.TryGetValue(target.StableKey, out double until) && _playerTime < until) continue;
            if (_clutterContacts.Count >= 128)
            {
                foreach (var old in _clutterContacts.Where(p => p.Value <= _playerTime).Select(p => p.Key).ToArray()) _clutterContacts.Remove(old);
                if (_clutterContacts.Count >= 128) break;
            }
            bool accepted = queued
                ? _clutterQueue!.Begin(_owner, target, hit.PointCci, hit.BladeCci, hit.MotionCci,
                    _settings.EffectiveContactPlaneStrategy, trigger.ToString(), hit.SamplesCci, hit.Stroke, "capsule-sweep", hit.DetectionTime)
                : _clutterSlicer.Begin(_owner, target, hit.PointCci, hit.BladeCci, hit.MotionCci,
                    _settings.EffectiveContactPlaneStrategy, trigger.ToString(), hit.SamplesCci, "capsule-sweep", hit.DetectionTime);
            if (accepted) _clutterContacts[target.StableKey] = _playerTime + 10;
            if (!accepted && _clutterQueue is { Full: true })
            { _sliceStatus = "Ground cut queue is full; some contacts were not queued."; _sliceMessageUntil = _playerTime + 5; break; }
            if (accepted && !queued) { _clutterNextAttempt = _playerTime + .25; break; }
        }
    }

    private void ReportCapsuleProgress(ClutterCapsuleProgress progress)
    {
        if (progress.CoverageUnavailable || progress.Overflow > 0 || progress.Expired > 0 || progress.Stale > 0
            || progress.Unresolved > 0 || progress.Unconfirmed > 0 || progress.UnsupportedShape || progress.Reason.Length > 0)
        {
            _sliceStatus = progress.CoverageUnavailable ? "Clutter collision coverage is unavailable."
                : $"Experimental capsule detection incomplete: {progress.Overflow} overflow, {progress.Expired} expired, {progress.Stale} stale, {progress.Unresolved} unresolved, {progress.Unconfirmed} unconfirmed{(progress.UnsupportedShape ? ", unsupported shape" : "")}{(progress.Reason.Length > 0 ? ": " + progress.Reason : ".")}";
            _sliceMessageUntil = _playerTime + 5;
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

    private bool TryClutterSegment(double3 from, double3 to, double3 blade, double3 motion,
        SliceRibbonMotion? sweep, bool bladeSegment, double bladeAlong)
    {
        double length = (to - from).Length();
        if (!double.IsFinite(length) || length < .001 || motion.LengthSquared() < 1e-12) return false;
        var ray = new Ray(from, (to - from) / length);
        if (!ClutterForceTargets.TryPick(_owner!, ray, out var target, out double distance, out var contact, out _)
            || target == null || distance < 0 || distance > length + .005) return false;
        _legacyDiagnosticHits.Add(target.StableKey);
        if (_clutterContacts.TryGetValue(target.StableKey, out double until) && _playerTime < until) return false;
        bool queued = _settings.SliceExecutionMode == "queued" && _clutterQueue != null;
        double fraction = Math.Clamp(distance / length, 0, 1);
        SliceSweepSamples? samples = sweep is { } ribbon ? (ribbon with
        { HitTime01 = bladeSegment ? 1 : fraction, AlongBlade01 = bladeSegment ? fraction : bladeAlong }).ToSamples() : null;
        bool accepted = queued ? _clutterQueue!.Begin(_owner!, target, contact, blade, motion,
                _settings.EffectiveContactPlaneStrategy, _sliceActivation.Kind.ToString(), samples, _sliceActivation.Epoch, "legacy-rays", _playerTime)
            : _clutterSlicer.Begin(_owner!, target, contact, blade, motion,
                _settings.EffectiveContactPlaneStrategy, _sliceActivation.Kind.ToString(), samples, "legacy-rays", _playerTime);
        _clutterContacts[target.StableKey] = _playerTime + (accepted ? 10 : .25);
        if (!queued) _clutterNextAttempt = _playerTime + .25;
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
        if (!_clutterSlicer.Begin(_owner, target, contact, blade, motion,
            trigger: "AssistedClutter", diagnosticDetector: "assisted-selection", diagnosticTime: _playerTime))
            return BlockAssistedClutter("The foreground ground object cannot be sliced safely.");
        _selectingSlice = false; _sliceHover = null; _sliceStatus = "Preparing ground-object cut...";
        _clutterContacts[target.StableKey] = _playerTime + 10;
        return AssistedClutterResult.Started;
    }

    private void ResetClutterSlicing()
    {
        _clutterSlicer.Cancel(); _clutterContacts.Clear();
        _clutterQueue?.Cancel(); _vesselQueue?.Cancel(); _vesselProvenance.Clear(); _vesselQueue?.ClearProvenance(); _allClutterHits.Reset(); _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset();
        _lastQueuedClutterStatus = "";
        _clutterHasPrevious = false; _clutterNextAttempt = 0;
        _renderer.ClearTreeFire();
    }
    private void CancelClutterSlice() { _clutterSlicer.Cancel(); _manualCapsuleHits.Reset(); _passiveCapsuleHits.Reset(); _clutterHasPrevious = false; }
    private void DisposeClutterSlicing() { _clutterSlicer.Dispose(); _vesselQueue?.Dispose(); _vesselQueue = null; _clutterQueue?.Dispose(); _clutterQueue = null; _manualCapsuleHits.Dispose(); _passiveCapsuleHits.Dispose(); _manualProbe.DisposeCache(); }
    private void PumpClutterSliceRetired()
    {
        _clutterSlicer.PumpRetired();
        bool vesselOwns = _vesselQueue is { HasUpload: true };
        _vesselQueue?.PumpRetired(vesselOwns || _clutterQueue is not { HasUpload: true });
        if (!vesselOwns) _clutterQueue?.PumpRetired();
    }
}
