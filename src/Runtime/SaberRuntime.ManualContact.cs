using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Force;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using Purr2D2.Slicing.Detection;
using Purr2D2.Slicing.Planes;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ManualContactGesture _manualGesture = new();
    private readonly ManualContactProbe _manualProbe = new();
    private readonly SliceActivationPolicy _sliceActivation = new();
    private VesselSliceQueue? _vesselQueue;
    private readonly SliceStrokeProvenance<Part> _vesselProvenance = new();
    private readonly List<ManualContactProbe.Contact> _vesselHits = [];
    private string _activeVesselDetector = "legacy-ribbon";
    private string _lastQueuedVesselStatus = "";
    private long _queueHandoff;
    private bool _vesselCaptureLane, _vesselUploadLane, _vesselCommitLane;
    private bool _passiveStrokeHasPose;
    private bool _manualReleasePending, _manualDrainPending;
    private double3 _manualDrainEmitter;
    private double _passiveStrokePoseTime;
    private double3 _passiveStrokeBase, _passiveStrokeTip;
    private ManualContactProbe.Sample _manualPose;
    private bool _manualPoseAvailable, _manualResetPending, _manualCancelPending, _manualSliceActive, _manualIsolated;
    private bool SliceWorkActive => _sliceSequence.Active || _manualSliceActive || ClutterSliceWorkActive || _vesselQueue is { Active: true };

    private void BeginManualSwing(float2 pointer = default, float swooshThresholdPixels = 4)
    {
        BeginManualSwingSwoosh(pointer, swooshThresholdPixels);
        _manualGesture.Begin();
        _manualReleasePending = _manualDrainPending = false;
        ClearManualPose(); _manualResetPending = true;
    }
    private void ReleaseManualSwing()
    {
        _manualSwingSwoosh.Release();
        _overlayDrag.Release(); _manualGesture.Release();
        _manualDrainEmitter = _manualPose.BaseBody;
        ClearManualPose(); _manualReleasePending = true;
    }

    private void ClearManualPose()
    {
        _manualPose = default;
        _manualPoseAvailable = false;
    }

    // Shared with the assisted-slice coordinator so terminal manual outcomes use
    // the same timestamp gate in production and the focused runtime harness.
    private void FinishManualContactAttempt(bool attempted)
    {
        if (attempted) _manualGesture.FinishAttempt(_playerTime);
        else _manualGesture.Finish();
        _manualProbe.Reset();
        ClearManualPose();
        _manualReleasePending = _manualDrainPending = false;
    }

    // Render only publishes the pose and gesture identity. All discovery, reads of
    // target transforms, capture and native mutation occur in BeforePhysics.
    private void PublishManualPose(double3 emitter, double3 tip, float visualScale = 1f, double nominalLength = 0d,
        float3 visualAxes = default)
    {
        bool queued = _clutterQueue != null && _settings.SliceExecutionMode == "queued";
        _manualPoseAvailable = (_overlayDrag.Held && (queued ? _manualGesture.Held : _manualGesture.CanDetectAt(_playerTime)))
            || (!_overlayDrag.Held && _settings.PassiveContactCutsEnabled && _playerTime >= _manualGesture.CooldownUntil);
        if (_manualPoseAvailable)
            _manualPose = new(_manualGesture.Id, _playerTime, emitter, tip, _overlayDrag.Offset)
                { VisualScale = visualScale, NominalLength = nominalLength,
                    VisualAxes = visualAxes == default ? float3.One : visualAxes };
        else ClearManualPose();
    }

    // UI callbacks cannot cancel uploads/native isolation while physics is running.
    private void InvalidateManualContact()
    {
        ClearManualPose();
        _manualReleasePending = _manualDrainPending = false;
        _manualResetPending = true;
        _manualCancelPending |= _manualSliceActive;
        _manualGesture.Cancel();
    }
    private void ApplyManualContactReset()
    {
        if (_manualReleasePending)
        {
            _manualReleasePending = false;
            _manualDrainPending = _clutterQueue != null && _settings.SliceExecutionMode == "queued"
                && _sliceActivation.Kind == SliceContactAuthorization.ManualContact && _manualProbe.Pending;
            if (!_manualDrainPending) _manualResetPending = true;
        }
        if (_manualResetPending) { _manualProbe.Reset(); _manualResetPending = false; }
        if (!_manualCancelPending) return;
        _manualCancelPending = false;
        if (_manualSliceActive) CancelAssistedSlice("Contact slice cancelled by manual control.");
    }

    private void UpdateManualContact(bool simulationAdvancing)
    {
        // The UI request is consumed by the clutter coordinator later in this
        // handoff, but vessel jobs must stop before their own transaction step.
        if (_cancelQueuedClutterRequested) { _vesselQueue?.Cancel(); _manualDrainPending = false; _manualProbe.Reset(); }
        bool queueConfigured = _clutterQueue != null && _settings.SliceExecutionMode == "queued"
            && _clutterQueueWorkers == _settings.SliceWorkerCount && _clutterQueueLimit == _settings.SlicePendingTargetLimit
            && _clutterQueueMemory == _settings.SliceWorkingMemoryMiB;
        if (queueConfigured && _vesselQueue == null)
        {
            _vesselQueue = new(_clutterQueue!.Scheduler, _clutterQueue.LoaderScheduler, _clutterQueue.WorkerCount,
                _settings.SlicePendingTargetLimit, _settings.SliceWorkingMemoryMiB, _vesselProvenance)
            { CutSparks = _renderer.AddCutSparks };
            _vesselProvenance.Observe(_sliceActivation.Epoch);
        }
        _queueHandoff++;
        _vesselCaptureLane = _vesselQueue is { WantsCapture: true }
            && (_clutterQueue is not { Active: true } || (_queueHandoff & 1) == 0);
        _vesselUploadLane = _vesselQueue is { WantsUpload: true } && _clutterQueue is not { HasUpload: true };
        _vesselCommitLane = simulationAdvancing && _vesselQueue is { WantsCommit: true };
        _vesselQueue?.Update(_owner, _vesselCommitLane, _playerTime, _vesselCaptureLane, _vesselUploadLane,
            _clutterQueue?.ChargedBytes ?? 0, _clutterQueue?.ReservedVehicles ?? 0, _clutterQueue?.ReservationCount ?? 0,
            _clutterQueue?.LoadSlots ?? 0);
        if (_vesselQueue is { Status.Length: > 0 } vesselQueue && vesselQueue.Status != _lastQueuedVesselStatus)
        { _lastQueuedVesselStatus = vesselQueue.Status; _sliceStatus = vesselQueue.Status; _sliceMessageUntil = _playerTime + 5; }
        if (_clutterQueue != null) _clutterQueue.OtherCount = _vesselQueue?.Count ?? 0;
        if (!queueConfigured && _vesselQueue is { Active: false }) { _vesselQueue.Dispose(); _vesselQueue = null; }
        bool eligible = _fault == null && _owner is { IsDisposed: false } && SaberHeldForActions
            && _blade.Equipped && _blade.TargetOn
            && (_blade.Deployment >= .99f || _blade.ManualDeployment && _blade.Deployment > .001f) && _pose.Eligible
            && !ForceHolding && !ForcePicking;
        bool manualHeld = _overlayDrag.Held && _manualGesture.Held && BFL9KUiVisible
            && ReferenceEquals(_overlayDragOwner, _owner) && ReferenceEquals(_overlayDragPose, _pose);
        bool detectionEligible = eligible && !_pose.ClashHeld && !_selectingSlice && !_sliceSequence.Active
            && (!_overlayDrag.Held || manualHeld);
        // A held SWING takes precedence even when it has not moved; passive movement
        // must never turn a stationary press into a cut.
        if (_sliceActivation.Update(manualHeld, _manualGesture.Id, _settings.PassiveContactCutsEnabled, detectionEligible))
        { if (!_manualDrainPending) _manualProbe.Reset(); _passiveStrokeHasPose = false; _vesselProvenance.Observe(_sliceActivation.Epoch); }
        if (_sliceActivation.Kind == SliceContactAuthorization.PassiveContact && simulationAdvancing && _freshPoseThisTick
            && _owner != null && _pose.TryGetBladeSegment(Length, out var passiveBase, out var passiveTip)
            && ClutterForceNative.OwnerBubble(_owner) is { } passiveBubble && passiveBubble.Origin.BubFrame == BubbleFrame.Ccf)
        {
            var toCcf = _owner.Parent.GetCci2Ccf(passiveBubble.Origin.Time);
            var rotation = _owner.GetBody2Cci(); var position = _owner.GetPositionCci();
            var sampleBase = (position + passiveBase.Transform(rotation)).Transform(toCcf);
            var sampleTip = (position + passiveTip.Transform(rotation)).Transform(toCcf);
            double elapsed = _playerTime - _passiveStrokePoseTime;
            double travel = Math.Max((sampleBase - _passiveStrokeBase).LengthSquared(), (sampleTip - _passiveStrokeTip).LengthSquared());
            bool moving = _passiveStrokeHasPose && elapsed > 0 && elapsed <= .2 && travel >= .000004;
            _passiveStrokeBase = sampleBase; _passiveStrokeTip = sampleTip; _passiveStrokePoseTime = _playerTime;
            _passiveStrokeHasPose = true;
            if (_sliceActivation.ObservePassiveMotion(_playerTime, moving))
            { if (!_manualDrainPending) _manualProbe.Reset(); _vesselProvenance.Observe(_sliceActivation.Epoch); }
        }
        string vesselDetector = _settings.VesselContactDetector == "cached-ribbon" ? "cached-ribbon" : "legacy-ribbon";
        if (_activeVesselDetector != vesselDetector) { _activeVesselDetector = vesselDetector; _manualDrainPending = false; _manualProbe.Reset(); }
        if (_manualSliceActive)
        {
            if (!eligible || (_manualGesture.InFlight && !BFL9KUiVisible))
            {
                // A lifecycle/control invalidation cancels this held gesture outright;
                // only a normal manual-attempt outcome receives the timed rearm.
                _manualGesture.Cancel();
                ClearManualPose();
                CancelAssistedSlice("Contact slice cancelled: saber pose or controls unavailable.");
                return;
            }
            UpdateManualSlice(simulationAdvancing && _freshPoseThisTick); return;
        }
        // A clash blocks new contact gestures, but accepted native slice work above keeps pumping.
        if (_pose.ClashHeld) { _manualDrainPending = false; _manualProbe.Reset(); return; }
        bool manual = _sliceActivation.Kind == SliceContactAuthorization.ManualContact;
        bool passive = _sliceActivation.Kind == SliceContactAuthorization.PassiveContact;
        bool draining = _manualDrainPending && !manual;
        if (draining)
        {
            if (!queueConfigured || !detectionEligible || _owner == null)
            { _manualDrainPending = false; _manualProbe.Reset(); return; }
            _manualProbe.DrainFrozen(_owner, _playerTime, _vesselHits);
            if (!_manualProbe.Pending) { _manualDrainPending = false; _manualProbe.Reset(); }
            if (_vesselHits.Count == 0) return;
        }
        else
        {
        if (!detectionEligible || (!queueConfigured && (ClutterSliceWorkActive || _vesselQueue is { Active: true })) || (!manual && !passive)
            || (manual && !(queueConfigured ? _manualGesture.Held : _manualGesture.CanDetectAt(_playerTime))) || !_manualPoseAvailable
            || _playerTime < _manualPose.Time
            || _manualPose.Time < _manualGesture.CooldownUntil
            || _playerTime - _manualPose.Time > ManualContactProbe.MaximumAge
            || _selectingSlice || _sliceSequence.Active || _slicer.Busy)
        { _manualProbe.Reset(); return; }
        // Render and solver cadence need not agree. Ordinary intervening handoffs
        // preserve the frozen query/cursor; age and lifecycle checks above bound it.
        if (!simulationAdvancing || !_freshPoseThisTick) return;
        var sample = _manualPose with { Gesture = _sliceActivation.Epoch, PassiveContact = passive };
        _vesselHits.Clear();
        if (vesselDetector == "cached-ribbon") _manualProbe.AdvanceMany(_owner!, sample, _vesselHits);
        else if (_manualProbe.Advance(_owner!, sample, out var single)) _vesselHits.Add(single);
        if (_vesselHits.Count == 0) return;
        }
        foreach (var contact in _vesselHits)
        {
        // Consume before checking cut policy too: a refused surface gets one attempt
        // until the .25-second cooldown expires and fresh motion arrives, never a per-frame flood.
        if (!queueConfigured && manual && !_manualGesture.Consume(_manualGesture.Id, _playerTime)) return;
        var emitter = draining ? _manualDrainEmitter : _manualPose.BaseBody;
        if (!queueConfigured) { _manualProbe.Reset(); ClearManualPose(); }
        if (!queueConfigured) _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
        if (!_targeting.TryContactTarget(_owner!, contact.Part, contact.PointLocal, contact.DistanceMeters,
            contact.BladeLength, contact.SourceTransform, contact.PartTransform, out var target, out var reason))
        { if (!queueConfigured) CancelAssistedSlice(reason, manualAttemptCompleted: true); continue; }
        var confirmedTarget = target!;
        var assemblyBlade = double3.TransformNormal(contact.BladeDirectionLocal, contact.PartTransform);
        var assemblyMotion = double3.TransformNormal(contact.MotionLocal, contact.PartTransform);
        if (!ContactSliceGeometry.TryCreate(Point(confirmedTarget.HitAssembly), Point(assemblyBlade), Point(assemblyMotion),
            out var contactSlice, out reason))
        { if (!queueConfigured) CancelAssistedSlice("Cannot contact slice: " + reason, manualAttemptCompleted: true); continue; }
        var ribbon = new SliceRibbonMotion(Point(contact.PreviousBaseLocal.Transform(contact.PartTransform)),
            Point(contact.PreviousTipLocal.Transform(contact.PartTransform)), Point(contact.CurrentBaseLocal.Transform(contact.PartTransform)),
            Point(contact.CurrentTipLocal.Transform(contact.PartTransform)), contact.HitTime01, contact.AlongBlade01);
        var planeRequest = new SlicePlaneRequest(_settings.EffectiveContactPlaneStrategy,
            !draining && passive ? "PassiveContact" : "ManualContact", "Vessel", Point(confirmedTarget.HitAssembly),
            Point(assemblyBlade), Point(assemblyMotion), ribbon);
        OfferSliceReplayContact(contact, vesselDetector);
        if (queueConfigured)
        {
            if (_vesselQueue!.Begin(_owner!, confirmedTarget, planeRequest, _clutterQueue?.Count ?? 0, contact.TriggerEpoch, _playerTime,
                contact.HitMesh, contact.HitPrimitives, () => _manualProbe.IsEvidenceCurrent(contact), vesselDetector, contact.SampleTime))
            {
                if (_clutterQueue != null) _clutterQueue.OtherCount = _vesselQueue.Count;
                _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
                _sliceStatus = "Queued vessel cut: " + confirmedTarget.Part.DisplayName; _audio.PlaySlice(_owner!, emitter, _settings.Volume);
            }
            continue;
        }
        _manualSliceActive = true; _manualIsolated = false;
        _sliceTarget = confirmedTarget; _sliceStarted = true; _sliceError = null;
        try
        {
            _slicer.Begin(confirmedTarget.Part, contactSlice, planeRequest,
                diagnosticDetector: vesselDetector, diagnosticTime: contact.SampleTime);
            _audio.PlaySlice(_owner!, emitter, _settings.Volume);
        }
        catch (Exception error) { _sliceError = error.Message; }
        _sliceStatus = "Contact slicing " + confirmedTarget.Part.DisplayName + "...";
        break;
        }
    }

    private void UpdateManualSlice(bool permitNative)
    {
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckPendingSliceTarget(_owner!, _sliceTarget!, out var reason))
        { _sliceError ??= reason; _slicer.FinishSerialDiagnostic(SliceJobOutcome.Stale, _playerTime); }
        if (_sliceError != null)
        { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime); CancelAssistedSlice("Cannot contact slice: " + _sliceError, manualAttemptCompleted: true); return; }
        _sliceStatus = _slicer.Phase;
        if (!_slicer.Ready || !permitNative) return;
        if (!_manualIsolated)
        {
            var result = IsolateSliceTarget();
            if (!result.Success) { _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime); CancelAssistedSlice("Contact slice failed: " + result.Message, manualAttemptCompleted: true); return; }
            _manualIsolated = true;
            return; // Native swap must wait for a separate settled update.
        }
        var commit = _targeting.CommitAcceptedSlice(_owner!, _sliceTarget!, default);
        if (!commit.Success) _slicer.FinishSerialDiagnostic(SliceJobOutcome.Failed, _playerTime);
        CancelAssistedSlice((commit.Success ? "Contact slice complete: " : "Contact slice failed: ") + commit.Message,
            manualAttemptCompleted: true);
    }

    // Shared isolation/rebase coordinator for both triggers. Only our successful
    // native separation can authorize the captured source's new ownership frame.
    private SliceAttempt IsolateSliceTarget()
    {
        var result = _slicer.Isolate(_sliceTarget!.Part);
        if (!result.Success) return result;
        var isolated = _slicer.Isolation!;
        _sliceTarget = _sliceTarget with { Vehicle = isolated.Owner, PartTransform = isolated.PartTransform,
            HitAssembly = _sliceTarget.HitAssembly.Transform(isolated.SourceToIsolated) };
        return result;
    }

    private static CutPoint Point(double3 value) => new(value.X, value.Y, value.Z);
}
