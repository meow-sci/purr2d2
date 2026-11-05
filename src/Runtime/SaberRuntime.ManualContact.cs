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
    private readonly record struct AcceptedManualContact(ManualContactProbe.Contact Contact, KittenEva Owner,
        object PoseOwner, object Scene, Vehicle Vehicle, ulong Gesture, ulong Epoch, bool Manual, bool Queued,
        double3 Emitter, string Detector);
    private readonly List<AcceptedManualContact> _acceptedManualContacts = new(ManualContactProbe.CandidateLimit);
    private long _cosmeticVesselContacts, _confirmedVesselContacts, _admittedVesselCuts, _declinedVesselContacts;
    private string _lastVesselContactOutcome = "";
    private string _activeVesselDetector = "legacy-ribbon";
    private string _lastQueuedVesselStatus = "";
    private long _queueHandoff;
    private bool _vesselCaptureLane, _vesselUploadLane, _vesselCommitLane;
    private bool _passiveStrokeHasPose;
    private bool _manualReleasePending, _manualDrainPending;
    private double3 _manualDrainEmitter;
    private ulong _manualDrainGesture, _manualDrainEpoch;
    private KittenEva? _manualDrainOwner;
    private object? _manualDrainPoseOwner, _manualDrainScene;
    private bool _manualDrainQueued;
    private double _passiveStrokePoseTime;
    private double3 _passiveStrokeBase, _passiveStrokeTip;
    private ManualContactProbe.Sample _manualPose;
    private ManualContactProbe.Sample _manualReleasedPose;
    private KittenEva? _manualReleaseOwner;
    private object? _manualReleasePoseOwner;
    private bool _manualPoseConsumed, _manualReleasedPosePending;
    private bool _manualPoseAvailable, _manualResetPending, _manualCancelPending, _manualSliceActive, _manualIsolated;
    private bool SliceWorkActive => _sliceSequence.Active || _manualSliceActive || _manualDrainPending
        || _acceptedManualContacts.Count != 0 || ClutterSliceWorkActive || _vesselQueue is { Active: true };

    private void BeginManualSwing(float2 pointer = default, float swooshThresholdPixels = 4)
    {
        BeginManualSwingSwoosh(pointer, swooshThresholdPixels);
        _manualGesture.Begin();
        _manualReleasePending = false; ClearManualDrain(); DiscardAcceptedVesselContacts("Contact cancelled by a new swing.");
        ClearReleasedManualPose();
        ClearManualPose(); _manualResetPending = true;
    }
    private void ReleaseManualSwing()
    {
        bool releaseAuthorized = _overlayDrag.Held && _manualGesture.Held
            && !_manualSliceActive && !_slicer.Busy && _manualGesture.CanDetectAt(_playerTime)
            && _sliceActivation.Kind == SliceContactAuthorization.ManualContact
            && ReferenceEquals(_overlayDragOwner, _owner) && ReferenceEquals(_overlayDragPose, _pose);
        if (releaseAuthorized)
        {
            _manualDrainGesture = _manualGesture.Id; _manualDrainEpoch = _sliceActivation.Epoch;
            _manualDrainOwner = _owner; _manualDrainPoseOwner = _pose; _manualDrainScene = Universe.CurrentSystem;
            _manualDrainQueued = _clutterQueue != null && _settings.SliceExecutionMode == "queued"
                && _clutterQueueWorkers == _settings.SliceWorkerCount && _clutterQueueLimit == _settings.SlicePendingTargetLimit
                && _clutterQueueMemory == _settings.SliceWorkingMemoryMiB;
            _manualDrainEmitter = _manualPose.BaseBody;
        }
        // GLFW release arrives before the settled handoff can consume the last
        // rendered held pose. Keep that one event, never an automatic return pose.
        if (releaseAuthorized && _manualPoseAvailable && !_manualPoseConsumed && _manualPose.Gesture == _manualGesture.Id)
        {
            _manualReleasedPose = _manualPose;
            _manualReleasedPosePending = true;
            _manualReleaseOwner = _owner; _manualReleasePoseOwner = _pose;
        }
        _manualSwingSwoosh.Release();
        _overlayDrag.Release(); _manualGesture.Release();
        ClearManualPose(); _manualReleasePending = true;
    }

    private void ClearManualPose()
    {
        _manualPose = default;
        _manualPoseAvailable = false;
    }

    private void ClearReleasedManualPose()
    {
        _manualReleasedPosePending = false; _manualReleasedPose = default;
        _manualReleaseOwner = null; _manualReleasePoseOwner = null;
    }

    private void ClearManualDrain()
    {
        _manualDrainPending = false; _manualDrainGesture = _manualDrainEpoch = 0;
        _manualDrainOwner = null; _manualDrainPoseOwner = _manualDrainScene = null;
        _manualDrainEmitter = default; _manualDrainQueued = false;
    }

    private void ResetVesselContactDiagnostics()
    {
        _cosmeticVesselContacts = _confirmedVesselContacts = _admittedVesselCuts = _declinedVesselContacts = 0;
        _lastVesselContactOutcome = "";
    }

    private void DeclineVesselContact(string reason)
    {
        _declinedVesselContacts++;
        _lastVesselContactOutcome = reason;
    }

    private void DiscardAcceptedVesselContacts(string reason)
    {
        if (_acceptedManualContacts.Count == 0) return;
        _declinedVesselContacts += _acceptedManualContacts.Count;
        _lastVesselContactOutcome = reason;
        _acceptedManualContacts.Clear();
    }

    // Shared with the assisted-slice coordinator so terminal manual outcomes use
    // the same timestamp gate in production and the focused runtime harness.
    private void FinishManualContactAttempt(bool attempted)
    {
        if (attempted) _manualGesture.FinishAttempt(_playerTime);
        else _manualGesture.Finish();
        _manualProbe.ResetMotion();
        ClearManualPose();
        ClearReleasedManualPose();
        _manualReleasePending = false; ClearManualDrain(); DiscardAcceptedVesselContacts("Contact cancelled before admission.");
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
        {
            _manualPose = new(_manualGesture.Id, _playerTime, emitter, tip, _overlayDrag.Offset)
                { VisualScale = visualScale, NominalLength = nominalLength,
                    VisualAxes = visualAxes == default ? float3.One : visualAxes };
            _manualPoseConsumed = false;
        }
        else ClearManualPose();
    }

    // UI callbacks cannot cancel uploads/native isolation while physics is running.
    private void InvalidateManualContact()
    {
        ClearManualPose();
        ClearReleasedManualPose();
        _manualReleasePending = false; ClearManualDrain(); DiscardAcceptedVesselContacts("Contact cancelled by its controls.");
        _manualResetPending = true;
        _manualCancelPending |= _manualSliceActive;
        _manualGesture.Cancel();
    }
    private void ApplyManualContactReset()
    {
        if (_manualReleasePending)
        {
            _manualReleasePending = false;
            _manualDrainPending = _manualDrainGesture != 0
                && _sliceActivation.Kind == SliceContactAuthorization.ManualContact && _manualProbe.Pending;
            if (!_manualDrainPending && !_manualReleasedPosePending) { _manualResetPending = true; ClearManualDrain(); }
        }
        if (_manualResetPending) { _manualProbe.ResetMotion(); _manualResetPending = false; }
        if (!_manualCancelPending) return;
        _manualCancelPending = false;
        if (_manualSliceActive) CancelAssistedSlice("Contact slice cancelled by manual control.");
    }

    private void UpdateManualContact(bool simulationAdvancing)
    {
        // The UI request is consumed by the clutter coordinator later in this
        // handoff, but vessel jobs must stop before their own transaction step.
        if (_cancelQueuedClutterRequested) { _vesselQueue?.Cancel(); ClearManualDrain(); ClearReleasedManualPose(); DiscardAcceptedVesselContacts("Pending contacts cancelled."); _manualProbe.ResetMotion(); }
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
        bool detectionEligible = eligible && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed())
            && _pose.HasFreshBodyFrame && !Program.IsModalOpen() && !Program.HasMouseInputLatch
            && ReferenceEquals(_owner, Program.ControlledVehicle) && !_pose.ClashHeld && !_selectingSlice && !_sliceSequence.Active
            && (!_overlayDrag.Held || manualHeld);
        var releasedPose = _manualReleasedPose;
        bool finalManualPose = _manualReleasedPosePending && _freshPoseThisTick
            && detectionEligible && BFL9KUiVisible && !_manualSliceActive && !_slicer.Busy
            && !_manualGesture.Held && !_manualGesture.Spent && !_manualGesture.InFlight
            && releasedPose.Gesture == _manualGesture.Id && releasedPose.Time >= _manualGesture.CooldownUntil
            && _playerTime >= releasedPose.Time && _playerTime - releasedPose.Time <= ManualContactProbe.MaximumAge
            && ReferenceEquals(_manualReleaseOwner, _owner) && ReferenceEquals(_manualReleasePoseOwner, _pose)
            && _sliceActivation.Kind == SliceContactAuthorization.ManualContact;
        // Only this first settled handoff may transform a released render pose.
        // Waiting for a later frame would use retrospective target poses.
        ClearReleasedManualPose();
        // A held SWING takes precedence even when it has not moved; passive movement
        // must never turn a stationary press into a cut.
        if (_sliceActivation.Update(manualHeld || finalManualPose, _manualGesture.Id, _settings.PassiveContactCutsEnabled, detectionEligible))
        { if (!_manualDrainPending) _manualProbe.ResetMotion(); _passiveStrokeHasPose = false; _vesselProvenance.Observe(_sliceActivation.Epoch); }
        if (_sliceActivation.Kind == SliceContactAuthorization.PassiveContact && _freshPoseThisTick
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
            { if (!_manualDrainPending) _manualProbe.ResetMotion(); _vesselProvenance.Observe(_sliceActivation.Epoch); }
        }
        string vesselDetector = _settings.VesselContactDetector == "cached-ribbon" ? "cached-ribbon" : "legacy-ribbon";
        if (_activeVesselDetector != vesselDetector) { _activeVesselDetector = vesselDetector; ClearManualDrain(); DiscardAcceptedVesselContacts("Contact cancelled because the detector changed."); _manualProbe.Reset(); }
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
        if (_pose.ClashHeld) { ClearManualDrain(); DiscardAcceptedVesselContacts("Contact cancelled by a saber clash."); _manualProbe.ResetMotion(); return; }
        if (_acceptedManualContacts.Count != 0)
        {
            UpdateAcceptedManualContacts(queueConfigured, detectionEligible, simulationAdvancing);
            if (_manualSliceActive || !queueConfigured && _acceptedManualContacts.Count != 0) return;
        }
        bool manual = _sliceActivation.Kind == SliceContactAuthorization.ManualContact;
        bool passive = _sliceActivation.Kind == SliceContactAuthorization.PassiveContact;
        bool draining = _manualDrainPending && !manual;
        ulong drainGesture = _manualDrainGesture, drainEpoch = _manualDrainEpoch;
        var drainEmitter = _manualDrainEmitter;
        if (draining)
        {
            // Only the already-authorized held intervals survive release. Neither
            // automatic return poses nor a later gesture can extend this drain.
            if (!detectionEligible || !BFL9KUiVisible || !_pose.HasFreshBodyFrame
                || !SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) || Program.IsModalOpen() || Program.HasMouseInputLatch
                || _owner == null || !ReferenceEquals(_owner, Program.ControlledVehicle)
                || !ReferenceEquals(_manualDrainOwner, _owner) || !ReferenceEquals(_manualDrainPoseOwner, _pose)
                || !ReferenceEquals(_manualDrainScene, Universe.CurrentSystem) || _manualDrainQueued != queueConfigured
                || _manualGesture.Held || _manualGesture.Spent || _manualGesture.InFlight || _manualGesture.Id != drainGesture
                || _playerTime < _manualGesture.CooldownUntil || _slicer.Busy
                || !queueConfigured && (ClutterSliceWorkActive || _vesselQueue is { Active: true }))
            { ClearManualDrain(); _manualProbe.ResetMotion(); return; }
            _manualProbe.DrainFrozen(_owner, _playerTime, _vesselHits);
            if (!_manualProbe.Pending) { ClearManualDrain(); _manualProbe.ResetMotion(); }
            if (_vesselHits.Count == 0) return;
        }
        else
        {
        var contactPose = finalManualPose ? releasedPose : _manualPose;
        if (!detectionEligible || (!queueConfigured && (ClutterSliceWorkActive || _vesselQueue is { Active: true })) || (!manual && !passive)
            || (!finalManualPose && manual && !(queueConfigured ? _manualGesture.Held : _manualGesture.CanDetectAt(_playerTime)))
            || (!finalManualPose && !_manualPoseAvailable)
            || _playerTime < contactPose.Time
            || contactPose.Time < _manualGesture.CooldownUntil
            || _playerTime - contactPose.Time > ManualContactProbe.MaximumAge
            || _selectingSlice || _sliceSequence.Active || _slicer.Busy)
        { _manualProbe.ResetMotion(); return; }
        // Observe every fresh rendered pose at this joined handoff. A confirmed
        // contact can wait below; neither sampling nor scanning mutates the vessel.
        if (!_freshPoseThisTick) return;
        var sample = contactPose with { Gesture = _sliceActivation.Epoch, PassiveContact = passive };
        if (!finalManualPose) _manualPoseConsumed = true;
        _vesselHits.Clear();
        if (vesselDetector == "cached-ribbon") _manualProbe.AdvanceMany(_owner!, sample, _vesselHits);
        else if (_manualProbe.Advance(_owner!, sample, out var single)) _vesselHits.Add(single);
        if (finalManualPose)
        {
            _manualDrainPending = _manualProbe.Pending;
            if (!_manualDrainPending) ClearManualDrain();
        }
        if (_vesselHits.Count == 0) return;
        }
        _confirmedVesselContacts += _vesselHits.Count;
        bool retained = false;
        foreach (var contact in _vesselHits)
        {
        if (draining && contact.TriggerEpoch != drainEpoch) { DeclineVesselContact("Contact belongs to an expired swing epoch."); continue; }
        var emitter = finalManualPose ? releasedPose.BaseBody : draining ? drainEmitter : _manualPose.BaseBody;
        if (contact.Part.StructuralPart.Tree?.OwningVehicle is not { } vehicle || Universe.CurrentSystem is not { } scene)
        { DeclineVesselContact("Contact source or scene is unavailable."); continue; }
        bool duplicate = false;
        foreach (var old in _acceptedManualContacts)
            if (ReferenceEquals(old.Contact.Part.StructuralPart, contact.Part.StructuralPart)) { duplicate = true; break; }
        if (duplicate) { DeclineVesselContact("Duplicate contact; the original contact is already awaiting admission."); continue; }
        if (_acceptedManualContacts.Count >= (queueConfigured ? ManualContactProbe.CandidateLimit : 1))
        { DeclineVesselContact(queueConfigured ? "Contact buffer is full." : "One serial contact is already retained for this swing."); continue; }
        _acceptedManualContacts.Add(new(contact, _owner!, _pose, scene, vehicle,
            draining ? drainGesture : finalManualPose ? releasedPose.Gesture : _manualGesture.Id,
            contact.TriggerEpoch, manual || draining, queueConfigured, emitter, vesselDetector));
        retained = true;
        }
        if (!queueConfigured && _acceptedManualContacts.Count != 0) _manualProbe.ResetMotion();
        if (retained && !simulationAdvancing) _lastVesselContactOutcome = "Contact confirmed; awaiting a physics step.";
        if (simulationAdvancing) UpdateAcceptedManualContacts(queueConfigured, detectionEligible, simulationAdvancing: true);
    }

    // Contacts are already exact target-local evidence. Keep them bounded while
    // player-time swing frames outnumber solver steps; never replay old body poses.
    private void UpdateAcceptedManualContacts(bool queueConfigured, bool detectionEligible, bool simulationAdvancing)
    {
        for (int index = _acceptedManualContacts.Count - 1; index >= 0; index--)
        {
            var accepted = _acceptedManualContacts[index]; var contact = accepted.Contact;
            bool authorized = accepted.Manual
                ? BFL9KUiVisible && accepted.Gesture == _manualGesture.Id && !_manualGesture.Spent && !_manualGesture.InFlight
                    && contact.SampleTime >= _manualGesture.CooldownUntil && _playerTime >= _manualGesture.CooldownUntil
                    && (!_manualGesture.Held || _sliceActivation.Kind == SliceContactAuthorization.ManualContact && _sliceActivation.Epoch == accepted.Epoch)
                : _settings.PassiveContactCutsEnabled && _sliceActivation.Kind == SliceContactAuthorization.PassiveContact
                    && _sliceActivation.Epoch == accepted.Epoch;
            string? decline = null;
            if (!detectionEligible) decline = "Contact no longer allowed by the saber controls or pose.";
            else if (!authorized || contact.TriggerEpoch != accepted.Epoch) decline = "Contact swing authorization changed.";
            else if (accepted.Queued != queueConfigured) decline = "Contact execution mode changed.";
            else if (_slicer.Busy || !queueConfigured && (ClutterSliceWorkActive || _vesselQueue is { Active: true }))
                decline = "Contact cutter is occupied by another cut.";
            else if (!ReferenceEquals(accepted.Owner, _owner) || !ReferenceEquals(accepted.PoseOwner, _pose)
                || !ReferenceEquals(accepted.Scene, Universe.CurrentSystem)) decline = "Contact owner, pose or scene changed.";
            else if (accepted.Vehicle.IsDisposed || accepted.Vehicle.IsEditedVehicle
                || !ReferenceEquals(accepted.Vehicle.Parent, accepted.Owner.Parent) || contact.Part.StructuralPart.IsAttachedInternal
                || !ReferenceEquals(contact.Part.StructuralPart.Tree?.OwningVehicle, accepted.Vehicle))
                decline = "Contact source ownership or eligibility changed.";
            else if (!contact.Part.StructuralPart.MatrixAsmb2VehicleAsmb.Equals(contact.SourceTransform)
                || !contact.Part.MatrixAsmb2VehicleAsmb.Equals(contact.PartTransform)) decline = "Contact source transform changed.";
            else if (!_manualProbe.IsEvidenceCurrent(contact)) decline = "Contact source geometry or model ownership changed.";
            else if (_playerTime < contact.SampleTime || _playerTime - contact.SampleTime > ManualContactProbe.MaximumQueryAge)
                decline = "Confirmed contact expired before admission.";
            if (decline != null)
            {
                DeclineVesselContact(decline);
                _acceptedManualContacts.RemoveAt(index);
            }
        }
        // Serial Begin and queued admission both stay behind the solver gate.
        if (!simulationAdvancing) return;
        while (_acceptedManualContacts.Count != 0)
        {
        var accepted = _acceptedManualContacts[0]; _acceptedManualContacts.RemoveAt(0);
        var contact = accepted.Contact; var emitter = accepted.Emitter;
        // Consume before cut policy too, so a refused surface gets one throttled attempt.
        if (!queueConfigured && accepted.Manual && !(_manualGesture.Held
            ? _manualGesture.Consume(accepted.Gesture, _playerTime)
            : _manualGesture.ConsumeReleased(accepted.Gesture, _playerTime)))
        { DeclineVesselContact("Contact swing was consumed or expired before admission."); DiscardAcceptedVesselContacts("Contact swing was consumed or expired before admission."); return; }
        if (!queueConfigured) { DiscardAcceptedVesselContacts("Only one serial contact is processed per swing."); _manualProbe.ResetMotion(); ClearManualPose(); ClearManualDrain(); }
        if (!queueConfigured) _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
        if (!_targeting.TryContactTarget(_owner!, contact.Part, contact.PointLocal, contact.DistanceMeters,
            contact.BladeLength, contact.SourceTransform, contact.PartTransform, out var target, out var reason))
        { DeclineVesselContact("Contact target refused: " + reason); if (!queueConfigured) CancelAssistedSlice(reason, manualAttemptCompleted: true); continue; }
        var confirmedTarget = target!;
        var assemblyBlade = double3.TransformNormal(contact.BladeDirectionLocal, contact.PartTransform);
        var assemblyMotion = double3.TransformNormal(contact.MotionLocal, contact.PartTransform);
        if (!ContactSliceGeometry.TryCreate(Point(confirmedTarget.HitAssembly), Point(assemblyBlade), Point(assemblyMotion),
            out var contactSlice, out reason))
        { DeclineVesselContact("Contact plane refused: " + reason); if (!queueConfigured) CancelAssistedSlice("Cannot contact slice: " + reason, manualAttemptCompleted: true); continue; }
        var ribbon = new SliceRibbonMotion(Point(contact.PreviousBaseLocal.Transform(contact.PartTransform)),
            Point(contact.PreviousTipLocal.Transform(contact.PartTransform)), Point(contact.CurrentBaseLocal.Transform(contact.PartTransform)),
            Point(contact.CurrentTipLocal.Transform(contact.PartTransform)), contact.HitTime01, contact.AlongBlade01);
        var planeRequest = new SlicePlaneRequest(_settings.EffectiveContactPlaneStrategy,
            accepted.Manual ? "ManualContact" : "PassiveContact", "Vessel", Point(confirmedTarget.HitAssembly),
            Point(assemblyBlade), Point(assemblyMotion), ribbon);
        OfferSliceReplayContact(contact, accepted.Detector);
        if (queueConfigured)
        {
            bool admitted;
            try
            {
                admitted = _vesselQueue!.Begin(_owner!, confirmedTarget, planeRequest, _clutterQueue?.Count ?? 0, contact.TriggerEpoch, _playerTime,
                    contact.HitMesh, contact.HitPrimitives, () => _manualProbe.IsEvidenceCurrent(contact), accepted.Detector, contact.SampleTime);
            }
            catch (Exception error)
            {
                DiscardAcceptedVesselContacts("Pending contacts cancelled after a vessel queue error.");
                DeclineVesselContact("Vessel queue could not begin: " + error.Message);
                throw;
            }
            if (admitted)
            {
                _admittedVesselCuts++; _lastVesselContactOutcome = "Vessel contact admitted to the cut queue.";
                if (_clutterQueue != null) _clutterQueue.OtherCount = _vesselQueue.Count;
                _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
                _sliceStatus = "Queued vessel cut: " + confirmedTarget.Part.DisplayName; _audio.PlaySlice(_owner!, emitter, _settings.Volume);
            }
            else DeclineVesselContact("Vessel queue declined the contact: duplicate, queue limit or source rejection.");
            continue;
        }
        _manualSliceActive = true; _manualIsolated = false;
        _sliceTarget = confirmedTarget; _sliceStarted = true; _sliceError = null;
        bool began = false;
        try
        {
            _slicer.Begin(confirmedTarget.Part, contactSlice, planeRequest,
                diagnosticDetector: accepted.Detector, diagnosticTime: contact.SampleTime);
            began = true; _admittedVesselCuts++; _lastVesselContactOutcome = "Serial vessel contact admitted to the cutter.";
            _audio.PlaySlice(_owner!, emitter, _settings.Volume);
        }
        catch (Exception error)
        {
            _sliceError = error.Message;
            if (!began) DeclineVesselContact("Vessel cutter could not begin: " + error.Message);
            else _lastVesselContactOutcome = "Vessel cut admitted; its sound could not play: " + error.Message;
        }
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
