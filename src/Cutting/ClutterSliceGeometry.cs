using Purr2D2.Slicing;

namespace Purr2D2.Cutting;

/// <summary>Shared classification and policy: visual foliage is separate from structural wood.</summary>
internal static class ClutterSliceGeometry
{
    internal static bool IsFoliage(bool opacity, bool transmission, bool doubleSided) => opacity || transmission || doubleSided;

    internal static CpuSlice Calculate(SliceInput input, bool tree, CancellationToken cancel, Action<SlicePlan> publish)
    {
        if (tree) input = input with { SourceSolidWood = !input.GeneratedClosedGeometry, CutKind = SliceCutKind.Wood };
        try { return SliceGeometryWork.Calculate(input, cancel, publish); }
        catch (InvalidDataException) when (!tree && (input.Contact is not null || input.PlaneRequest is not null)
            && (input.PlaneRequest?.AllowsLegacyFallbacks ?? true))
        {
            cancel.ThrowIfCancellationRequested();
            return SliceGeometryWork.Calculate(input with { Contact = null, PlaneRequest = null }, cancel, publish);
        }
    }
}
