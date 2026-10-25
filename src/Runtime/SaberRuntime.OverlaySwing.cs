using Purr2D2.Core;
using KSA;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private void ApplyOverlaySwing(double playerDt)
    {
        if (!_overlayDrag.Active) return;
        if (!LightsaberUiVisible || !OverlayCanSwing || !ReferenceEquals(_overlayDragOwner, _owner)
            || !ReferenceEquals(_overlayDragPose, _pose))
        {
            ResetOverlaySwing();
            return;
        }
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
        _overlayDrag.Reset();
        _overlayDragOwner = null;
        _overlayDragPose = null;
    }

}
