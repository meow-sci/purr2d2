using Purr2D2.Slicing;

namespace Purr2D2.Cutting;

/// <summary>Shared pure policy: preserve native foliage; try an anchored rock cut before a central section.</summary>
internal static class ClutterSliceGeometry
{
    internal static bool RequiresNativeFoliage(bool opacity, bool transmission, bool doubleSided) => opacity || transmission || doubleSided;

    internal static CpuSlice Calculate(SliceInput input, bool tree, CancellationToken cancel, Action<SlicePlan> publish)
    {
        try { return SliceGeometryWork.Calculate(input, cancel, publish); }
        catch (InvalidDataException) when (!tree && input.Contact is not null)
        {
            cancel.ThrowIfCancellationRequested();
            return SliceGeometryWork.Calculate(input with { Contact = null }, cancel, publish);
        }
    }
}
