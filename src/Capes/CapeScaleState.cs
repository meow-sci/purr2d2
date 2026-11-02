using Brutal.Numerics;
using Purr2D2.Pose;

namespace Purr2D2.Capes;

/// <summary>Defers one kitten's cloth reset from render observation to the settled update.</summary>
internal sealed class CapeScaleState
{
    private float3 _axes;
    private bool _observed, _resetPending;

    internal bool Observe(float3 axes)
    {
        bool changed = _observed && KittenVisualScale.Changed(_axes, axes);
        _axes = axes;
        _observed = true;
        _resetPending |= changed;
        return changed;
    }

    internal bool ConsumeReset()
    {
        bool reset = _resetPending;
        _resetPending = false;
        return reset;
    }
}
