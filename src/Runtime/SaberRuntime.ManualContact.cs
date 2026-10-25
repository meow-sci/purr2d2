using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ManualContactGesture _manualGesture = new();
    private readonly ManualContactProbe _manualProbe = new();
    private ManualContactProbe.Sample _manualPose;
    private bool _manualPoseAvailable, _manualResetPending, _manualCancelPending, _manualSliceActive, _manualIsolated;
    private bool SliceWorkActive => _sliceSequence.Active || _manualSliceActive;

    private void BeginManualSwing()
    {
        _manualGesture.Begin();
        ClearManualPose(); _manualResetPending = true;
    }
    private void ReleaseManualSwing()
    {
        _overlayDrag.Release(); _manualGesture.Release();
        ClearManualPose(); _manualResetPending = true;
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
    }

    // Render only publishes the pose and gesture identity. All discovery, reads of
    // target transforms, capture and native mutation occur in BeforePhysics.
    private void PublishManualPose(double3 emitter, double3 tip)
    {
        _manualPoseAvailable = _overlayDrag.Held && _manualGesture.CanDetectAt(_playerTime);
        if (_manualPoseAvailable)
            _manualPose = new(_manualGesture.Id, _playerTime, emitter, tip, _overlayDrag.Offset);
        else ClearManualPose();
    }

    // UI callbacks cannot cancel uploads/native isolation while physics is running.
    private void InvalidateManualContact()
    {
        ClearManualPose();
        _manualResetPending = true;
        _manualCancelPending |= _manualSliceActive;
        _manualGesture.Cancel();
    }
    private void ApplyManualContactReset()
    {
        if (_manualResetPending) { _manualProbe.Reset(); _manualResetPending = false; }
        if (!_manualCancelPending) return;
        _manualCancelPending = false;
        if (_manualSliceActive) CancelAssistedSlice("Contact slice cancelled by manual control.");
    }

    private void UpdateManualContact(bool simulationAdvancing)
    {
        bool eligible = _fault == null && _owner is { IsDisposed: false } && LightsaberUiVisible
            && _blade.Equipped && _blade.TargetOn && _blade.Deployment >= .99f && _pose.Eligible
            && !SomersaultActive && !ForceHolding && !ForcePicking;
        if (_manualSliceActive)
        {
            if (!eligible)
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
        if (!eligible || !_manualGesture.CanDetectAt(_playerTime) || !_overlayDrag.Held || !_manualPoseAvailable
            || _manualPose.Gesture != _manualGesture.Id || _playerTime < _manualPose.Time
            || _manualPose.Time < _manualGesture.CooldownUntil
            || _playerTime - _manualPose.Time > ManualContactProbe.MaximumAge
            || _selectingSlice || _sliceSequence.Active || _slicer.Busy)
        { _manualProbe.Reset(); return; }
        // Render and solver cadence need not agree. Ordinary intervening handoffs
        // preserve the frozen query/cursor; age and lifecycle checks above bound it.
        if (!simulationAdvancing || !_freshPoseThisTick) return;
        if (!_manualProbe.Advance(_owner!, _manualPose, out var contact)) return;
        // Consume before checking cut policy too: a refused surface gets one attempt
        // until the .25-second cooldown expires and fresh motion arrives, never a per-frame flood.
        if (!_manualGesture.Consume(_manualPose.Gesture, _playerTime)) return;
        _manualProbe.Reset(); ClearManualPose();
        _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
        if (!_targeting.TryContactTarget(_owner!, contact.Part, contact.PointLocal, contact.DistanceMeters,
            contact.BladeLength, contact.SourceTransform, contact.PartTransform, out var target, out var reason))
        { CancelAssistedSlice(reason, manualAttemptCompleted: true); return; }
        var confirmedTarget = target!;
        var assemblyBlade = double3.TransformNormal(contact.BladeDirectionLocal, contact.PartTransform);
        var assemblyMotion = double3.TransformNormal(contact.MotionLocal, contact.PartTransform);
        if (!ContactSliceGeometry.TryCreate(Point(confirmedTarget.HitAssembly), Point(assemblyBlade), Point(assemblyMotion),
            out var contactSlice, out reason))
        { CancelAssistedSlice("Cannot contact slice: " + reason, manualAttemptCompleted: true); return; }
        _manualSliceActive = true; _manualIsolated = false;
        _sliceTarget = confirmedTarget; _sliceStarted = true; _sliceError = null;
        try { _slicer.Begin(confirmedTarget.Part, contactSlice); }
        catch (Exception error) { _sliceError = error.Message; }
        _sliceStatus = "Contact slicing " + confirmedTarget.Part.DisplayName + "...";
    }

    private void UpdateManualSlice(bool permitNative)
    {
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckPendingSliceTarget(_owner!, _sliceTarget!, out var reason)) _sliceError ??= reason;
        if (_sliceError != null) { CancelAssistedSlice("Cannot contact slice: " + _sliceError, manualAttemptCompleted: true); return; }
        _sliceStatus = _slicer.Phase;
        if (!_slicer.Ready || !permitNative) return;
        if (!_manualIsolated)
        {
            var result = IsolateSliceTarget();
            if (!result.Success) { CancelAssistedSlice("Contact slice failed: " + result.Message, manualAttemptCompleted: true); return; }
            _manualIsolated = true;
            return; // Native swap must wait for a separate settled update.
        }
        var commit = _targeting.CommitAcceptedSlice(_owner!, _sliceTarget!, default);
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
