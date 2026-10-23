using Purr2D2.Pose;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly ForcePoseController _forcePose = new();

    // Hand animation follows capture ownership, never the saber's equip state.
    private void UpdateForcePose(double playerDt)
    {
        try
        {
            if (!ReferenceEquals(_forcePose.Kitten, _owner)) _forcePose.Unbind();
            if (ForceHolding && ForceOwner is { } owner)
            {
                if (!ReferenceEquals(_forcePose.Kitten, owner)) _forcePose.Bind(owner);
                _forcePose.Update(playerDt, true,
                    _force.TryGetPoseTargetBody(owner, out var aim) ? aim : null);
            }
            else
            {
                _forcePose.Update(playerDt, false);
                if (_forcePose.Blend <= 0) _forcePose.Unbind();
            }
        }
        catch (Exception ex)
        {
            _forcePose.Unbind();
            Report("Force hand pose unavailable", ex);
        }
    }
}
