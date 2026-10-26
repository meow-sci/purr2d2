using Brutal.Numerics;
using Purr2D2.Core;
using KSA;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private ManualSwingSwooshGate _manualSwingSwoosh = new();

    private void BeginManualSwingSwoosh(float2 pointer, float thresholdPixels)
        => _manualSwingSwoosh.Begin(pointer, thresholdPixels);

    // Both the ImGui fallback and native cursor path feed this one sampler. It
    // deliberately only records movement; audio is emitted from the settled handoff.
    private void MoveManualSwing(float2 pointer)
        => MoveManualSwing(pointer, Environment.TickCount64);

    private void MoveManualSwing(float2 pointer, long now)
    {
        _overlayDrag.Move(pointer);
        _manualSwingSwoosh.Move(pointer, now, ManualSwingSwooshSamplingAllowed());
    }

    private bool ManualSwingSwooshSamplingAllowed()
        => _overlayDrag.Held && LightsaberUiVisible && OverlayCanSwing
            && _owner is { IsDisposed: false } owner
            && ReferenceEquals(_overlayDragOwner, owner) && ReferenceEquals(_overlayDragPose, _pose)
            && ReferenceEquals(owner, Program.ControlledVehicle)
            && _blade.Equipped && _blade.Deployment > .001f
            && _pose.Eligible && _pose.HasFreshBodyFrame && !_pose.ClashHeld
            && SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed())
            && !Program.IsModalOpen() && !Program.HasMouseInputLatch;

    private void UpdateManualSwingSwoosh(long now)
    {
        if (!ManualSwingSwooshSamplingAllowed() || !_freshPoseThisTick
            || !_pose.TryGetBladeSegment(Length, out var emitter, out _))
        {
            _manualSwingSwoosh.TryConsume(now, false);
            return;
        }

        if (_manualSwingSwoosh.TryConsume(now, true))
            _audio.PlayManualSwing(_owner!, emitter, _settings.Volume);
    }

    private void ApplyOverlaySwing(double playerDt)
    {
        if (!_overlayDrag.Active) return;
        if (!LightsaberUiVisible || !OverlayCanSwing || !ReferenceEquals(_overlayDragOwner, _owner)
            || !ReferenceEquals(_overlayDragPose, _pose))
        {
            ResetOverlaySwing();
            return;
        }
        // Preserve the exact displayed calibration while a clash holds the pose.
        if (_pose.ClashHeld) return;
        // Preserve the displayed offset while paused or outside supported warp, including a return.
        if (!_overlayDrag.Advance(SaberLimits.SupportsSpeed(Universe.GetSimulationSpeed()) ? playerDt : 0,
            out var grip, out var aim)) return;
        _pose.GripOffsetFromChestCm = grip;
        _pose.BladeDirectionModel = aim;
        if (!_overlayDrag.Active) { _overlayDragOwner = null; _overlayDragPose = null; }
    }

    private void ResetOverlaySwing()
    {
        if (_overlayDrag.Active && _overlayDragPose != null)
        {
            _overlayDragPose.GripOffsetFromChestCm = _overlayDrag.OriginalGrip;
            _overlayDragPose.BladeDirectionModel = _overlayDrag.OriginalAim;
        }
        InvalidateManualContact();
        _manualSwingSwoosh.Cancel();
        _overlayDrag.Reset();
        _overlayDragOwner = null;
        _overlayDragPose = null;
    }

}
