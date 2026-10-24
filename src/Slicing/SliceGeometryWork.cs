using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

// Worker inputs contain copied values only: never Part, Vehicle, MeshReference or a native material.
internal sealed record SliceMaterialData(string Id, bool Glass);
internal sealed record SliceInput(SliceFace[][] Groups, CutPoint[] Axes,
    IReadOnlyDictionary<int, SliceMaterialData> Materials, float MassKg, uint Seed = 0)
{
    internal bool GeneratedClosedGeometry { get; init; }
}
internal sealed record SlicePlan(SlicePlane Plane);
internal sealed record CpuFragment(SlicePiece Piece, PrincipalFragment Frame, SliceFace[][] Solids, double3 Extents, float MassKg);
internal sealed record CpuSlice(SlicePlane Plane, CpuFragmentAsset[] Fragments)
{
    internal bool Faceted { get; init; }
    internal int Attempts { get; init; }
    internal string FacetFallback { get; init; } = "";
    internal bool AuthoredSubpartFallback { get; init; }
}

internal static class SliceGeometryWork
{
    internal static CpuSlice Calculate(SliceInput input, CancellationToken cancel, Action<SlicePlan> publish)
    {
        cancel.ThrowIfCancellationRequested();
        if (!SliceShellBuilder.TryPrepare(input.Groups, out var source, out string reason, cancel))
        {
            if (QuietSubpartBreakup.TryBuild(input, out var authored, out var authoredFailure, cancel))
            { publish(new(authored!.Plane)); return authored with { FacetFallback = reason }; }
            throw new InvalidDataException(reason + " Authored-subpart fallback: " + authoredFailure);
        }
        string failure = "No broad section could be found.";
        var candidates = AssistedSliceGeometry.Candidates(source, input.Axes, input.Seed).ToArray();
        string facetFailure = ""; int attempts = 0;
        // Three coarse facet attempts, then the bounded oblique/axis plane fallbacks.
        foreach (var attempt in candidates.Take(3).Select((plane, i) => (Plane: plane, Faceted: true, Index: i))
            .Concat(candidates.Select((plane, i) => (Plane: plane, Faceted: false, Index: i))))
        {
            var candidate = attempt.Plane; attempts++;
            cancel.ThrowIfCancellationRequested();
            PlaneSliceResult? result;
            bool sliced = attempt.Faceted
                ? FacetedSliceGeometry.TrySlice(source, candidate, input.Seed + (uint)attempt.Index * 0x9e3779b9u, out result, out failure, cancel)
                : PlaneSlicer.TrySliceCompound(source, candidate, out result, out failure, cancellation: cancel);
            if (!sliced) { if (attempt.Faceted) facetFailure = failure; continue; }
            var prepared = new List<CpuFragment>();
            double volume = result!.Pieces.Sum(p => p.Volume);
            if (result.Pieces.Length is < 2 or > 8 || !double.IsFinite(volume) || volume <= 0) continue;
            foreach (var piece in result.Pieces)
            {
                cancel.ThrowIfCancellationRequested();
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out failure)
                    || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var solids, out var extents, out failure, cancel)) break;
                var points = new HashSet<CutPoint>();
                foreach (var solid in solids)
                {
                    foreach (var f in solid) { points.Add(f.A.Position); points.Add(f.B.Position); points.Add(f.C.Position); }
                }
                int groups = FragmentAssetWriter.CountRenderSurfaces(solids, input.Materials);
                if (groups > 2046 || points.Count > 65536 || solids.Sum(s => s.Length) > 200_000)
                { failure = "This cut exceeds the native fragment geometry budget."; break; }
                float mass = (float)(input.MassKg * piece.Volume / volume);
                if (!float.IsFinite(mass) || mass <= .0001f) { failure = "Slice fragment mass is invalid."; break; }
                prepared.Add(new(piece, frame, solids, extents, mass));
            }
            if (prepared.Count != result.Pieces.Length) { if (attempt.Faceted) facetFailure = failure; continue; }
            var assets = new CpuFragmentAsset[prepared.Count];
            try
            {
                for (int i = 0; i < assets.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    assets[i] = FragmentAssetWriter.Build(prepared[i], input.Materials, cancel);
                }
            }
            catch (InvalidDataException error)
            {
                failure = "Fragment export/collider: " + error.Message;
                if (attempt.Faceted) facetFailure = failure;
                continue;
            }
            // Publish only a fully exportable section. The visible swing starts
            // independently, while hull cooking and serialization stay on this worker.
            publish(new(result.EffectivePlane));
            return new(result.EffectivePlane, assets) { Faceted = attempt.Faceted, Attempts = attempts, FacetFallback = attempt.Faceted ? "" : facetFailure };
        }
        if (QuietSubpartBreakup.TryBuild(input, out var fallback, out var fallbackFailure, cancel))
        {
            publish(new(fallback!.Plane));
            return fallback with { Attempts = attempts, FacetFallback = failure };
        }
        throw new InvalidDataException("This part could not be sliced: " + failure + " Authored-subpart fallback: " + fallbackFailure);
    }
}
