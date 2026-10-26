using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly SomersaultMotionController _somersaultMotion = new();
    private readonly SomersaultPoseController _somersaultPose = new();
    private KittenEva? _somersaultPoseOwner;
    private SomersaultRequest? _somersaultRequest;
    private long _somersaultAudioCommandId;
    private readonly record struct SomersaultRequest(KittenEva Owner, SomersaultDirection Direction,
        double PressTime, bool FromSpace = false);

    // Includes a click waiting for the settled handoff: other actions must not
    // acquire the pose between that press and the next physics preparation.
    private bool SomersaultActive => _somersaultRequest.HasValue || _somersaultMotion.Active;

    // Visibility belongs to the controlled EVA, not its momentary contact state.
    // Availability requires ground for the first jump, or a live combo window.
    private bool ShowSomersaultButtons
    {
        get
        {
            ApplySomersaultComboSettings();
            return !Program.EditorFlag
                && _owner is { IsDisposed: false } kitten && ReferenceEquals(Program.ControlledVehicle, kitten);
        }
    }

    private void ApplySomersaultComboSettings()
        => _somersaultMotion.SetComboIntervalSeconds(_settings.JumpComboIntervalSeconds);

    private bool CanStartSomersault(out string reason, double? pressTime = null)
    {
        ApplySomersaultComboSettings();
        if (!_ready || _disposed || _fault != null)
        { reason = "Somersault unavailable."; return false; }
        if (Program.EditorFlag || _owner is not { IsDisposed: false }
            || !ReferenceEquals(Program.ControlledVehicle, _owner))
        { reason = "Control an EVA kitten."; return false; }
        if (_somersaultRequest.HasValue)
        { reason = "Jump press pending."; return false; }
        if (_sliceSequence.Active && !_sliceSequence.EndPresented)
        { reason = "Finishing slice stroke."; return false; }
        if (_pose.Swinging)
        { reason = "Finishing saber swing."; return false; }
        if (!ReferenceEquals(_somersaultPoseOwner, _owner) || !_somersaultPose.Eligible)
        { reason = _somersaultPose.Status; return false; }
        return _somersaultMotion.CanStart(_owner, out reason, pressTime);
    }

    private void QueueSomersault(SomersaultDirection direction)
    {
        if (!_settings.SpaceSomersaultEnabled) CancelPendingSpaceSomersault();
        // UI callbacks only enqueue. One pending request prevents hold/repeat or
        // multiple clicks in one frame from becoming another jump after landing.
        // Native event polling follows applied solver results but precedes our
        // settled update. Observe that result before testing a rapid follow-up.
        // Capture monotonic input time first; frame/solver stalls must not move it.
        double pressed = _somersaultMotion.InputTime;
        ApplySomersaultComboSettings();
        _somersaultMotion.Update(_owner);
        if (Enum.IsDefined(direction) && CanStartSomersault(out _, pressed) && _owner is { } owner)
            _somersaultRequest = new(owner, direction, pressed);
    }

    private void UpdateSomersault()
    {
        ApplySomersaultComboSettings();
        if (!ReferenceEquals(_somersaultPoseOwner, _owner))
        {
            // Input can arrive on the newly controlled EVA before this runtime
            // has rebound its visual pose. Keep only that fresh actor's Space
            // intent through the rebind; old-owner and button requests retain
            // the existing reset behavior.
            SomersaultRequest? freshSpaceRequest = _somersaultRequest is { FromSpace: true } spaceInput
                && ReferenceEquals(spaceInput.Owner, _owner) && IsSpaceSomersaultScope(spaceInput.Owner)
                    ? spaceInput : null;
            ResetSomersault();
            _somersaultRequest = freshSpaceRequest;
            _somersaultPoseOwner = _owner;
            if (_owner != null) _somersaultPose.Bind(_owner);
        }
        _somersaultMotion.Update(_owner);
        if (!_somersaultPose.Eligible && _somersaultMotion.Active)
            _somersaultMotion.Reset();

        // Observe native acknowledgement, including one consumed by input polling.
        // Queued/rejected launches and repeated frames must never replay the cue.
        if (_somersaultAudioCommandId != _somersaultMotion.AppliedCommandId)
        {
            _somersaultAudioCommandId = _somersaultMotion.AppliedCommandId;
            if (_somersaultMotion.Active && ReferenceEquals(_somersaultMotion.Owner, _owner)
                && _owner is { IsDisposed: false } owner && _blade.Equipped && _blade.TargetOn
                && _blade.Deployment > .001f && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()))
            {
                // Offscreen/stale hands fall back to the kitten's body origin.
                _pose.TryGetBladeSegment(Length, out var emitter, out _);
                _audio.PlaySwoosh(owner, emitter, _settings.Volume);
            }
        }

        var request = _somersaultRequest;
        _somersaultRequest = null;
        if (request is { } queued && ReferenceEquals(queued.Owner, _owner)
            && (!queued.FromSpace || IsSpaceSomersaultScope(queued.Owner))
            && CanStartSomersault(out _, queued.PressTime)
            && _somersaultMotion.Request(queued.Owner, queued.Direction, queued.PressTime))
        {
            // Selection has not modified a vessel. A completed stroke's
            // background cut is intentionally allowed to finish while moving.
            if (_selectingSlice) CancelAssistedSlice("Slice selection cancelled by somersault.");
        }

        _somersaultPose.SetMotion(_somersaultMotion.Direction,
            _somersaultMotion.RotationRadians, _somersaultMotion.Tuck, _somersaultMotion.Active);
    }

    private void ResetSomersault()
    {
        _somersaultRequest = null;
        _somersaultMotion.Reset();
        _somersaultAudioCommandId = _somersaultMotion.AppliedCommandId;
        _somersaultPose.Unbind();
        _somersaultPoseOwner = null;
    }
}
