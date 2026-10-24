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
        _manualPoseAvailable = false; _manualResetPending = true;
    }
    private void ReleaseManualSwing()
    {
        _overlayDrag.Release(); _manualGesture.Release();
        _manualPoseAvailable = false; _manualResetPending = true;
    }

    // Render only publishes the pose and gesture identity. All discovery, reads of
    // target transforms, capture and native mutation occur in BeforePhysics.
    private void PublishManualPose(double3 emitter, double3 tip)
    {
        _manualPoseAvailable = _overlayDrag.Held && _manualGesture.CanDetect;
        if (_manualPoseAvailable)
            _manualPose = new(_manualGesture.Id, _playerTime, emitter, tip, _overlayDrag.Offset);
    }

    // UI callbacks cannot cancel uploads/native isolation while physics is running.
    private void InvalidateManualContact()
    {
        _manualPoseAvailable = false;
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
        bool eligible = _fault == null && _owner is { IsDisposed: false } && OverlayVisible
            && _blade.Equipped && _blade.TargetOn && _blade.Deployment >= .99f && _pose.Eligible
            && !SomersaultActive && !ForceHolding && !ForcePicking;
        if (_manualSliceActive)
        {
            if (!eligible) { CancelAssistedSlice("Contact slice cancelled: saber pose or controls unavailable."); return; }
            UpdateManualSlice(simulationAdvancing && _freshPoseThisTick); return;
        }
        if (!eligible || !_manualGesture.CanDetect || !_overlayDrag.Held || !_manualPoseAvailable
            || _manualPose.Gesture != _manualGesture.Id || _playerTime < _manualPose.Time
            || _playerTime - _manualPose.Time > ManualContactProbe.MaximumAge
            || _selectingSlice || _sliceSequence.Active || _slicer.Busy)
        { _manualProbe.Reset(); return; }
        // Render and solver cadence need not agree. Ordinary intervening handoffs
        // preserve the frozen query/cursor; age and lifecycle checks above bound it.
        if (!simulationAdvancing || !_freshPoseThisTick) return;
        if (!_manualProbe.Advance(_owner!, _manualPose, out var contact)) return;
        // Consume before checking cut policy too: a refused surface gets one message
        // per deliberate press, never a per-frame rejection/preparation flood.
        if (!_manualGesture.Consume(_manualPose.Gesture)) return;
        _manualProbe.Reset(); _manualPoseAvailable = false;
        _renderer.AddCutSparks(contact.Part, contact.PointLocal, contact.NormalLocal, _playerTime);
        if (!_targeting.TryContactTarget(_owner!, contact.Part, contact.PointLocal, contact.DistanceMeters,
            contact.BladeLength, contact.SourceTransform, contact.PartTransform, out var target, out var reason))
        { _manualGesture.Finish(); _sliceStatus = reason; _sliceMessageUntil = _playerTime + 5; return; }
        _manualSliceActive = true; _manualIsolated = false;
        _sliceTarget = target; _sliceStarted = true; _sliceError = null;
        try { _slicer.Begin(target!.Part); }
        catch (Exception error) { _sliceError = error.Message; }
        _sliceStatus = "Contact slicing " + target!.Part.DisplayName + "...";
    }

    private void UpdateManualSlice(bool permitNative)
    {
        _slicer.Advance();
        _sliceError ??= _slicer.Error;
        if (!_targeting.CheckPendingSliceTarget(_owner!, _sliceTarget!, out var reason)) _sliceError ??= reason;
        if (_sliceError != null) { CancelAssistedSlice("Cannot contact slice: " + _sliceError); return; }
        _sliceStatus = _slicer.Phase;
        if (!_slicer.Ready || !permitNative) return;
        if (!_manualIsolated)
        {
            var result = IsolateSliceTarget();
            if (!result.Success) { CancelAssistedSlice("Contact slice failed: " + result.Message); return; }
            _manualIsolated = true;
            return; // Native swap must wait for a separate settled update.
        }
        var commit = _targeting.CommitAcceptedSlice(_owner!, _sliceTarget!, default);
        CancelAssistedSlice((commit.Success ? "Contact slice complete: " : "Contact slice failed: ") + commit.Message);
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
}
