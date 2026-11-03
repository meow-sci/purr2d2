using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Planes;

internal sealed class LegacyContactPlaneStrategy : ISlicePlaneStrategy
{
    public bool TrySelect(SlicePlaneRequest request, IReadOnlyList<CutPoint> axes,
        out SlicePlaneSelection selection, out string reason)
    {
        selection = default;
        if (!ContactSliceGeometry.TryCreate(request.Anchor, request.BladeAtHit, request.MotionAtHit, out var contact, out reason)) return false;
        return TrySelect(contact, axes, out selection, out reason);
    }

    internal static bool TrySelect(ContactSlice contact, IReadOnlyList<CutPoint> axes,
        out SlicePlaneSelection selection, out string reason)
    {
        selection = default;
        if (!ContactSliceGeometry.TryCandidates(contact, out var planes, out reason)) return false;
        var axesPlanes = ContactSliceGeometry.AxisCandidates(contact, axes);
        selection = new(planes.Concat(axesPlanes).Select((plane, i) => new SlicePlaneCandidate(plane, i,
            i == 0 ? SlicePlaneFallback.None : i < planes.Length ? SlicePlaneFallback.TiltFallback : SlicePlaneFallback.AxisFallback)).ToArray());
        return true;
    }
}
