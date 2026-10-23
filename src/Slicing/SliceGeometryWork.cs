using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

// Worker inputs contain copied values only: never Part, Vehicle, MeshReference or a native material.
internal sealed record SliceMaterialData(string Id, bool Glass);
internal sealed record SliceInput(SliceFace[][] Groups, CutPoint[] Axes,
    IReadOnlyDictionary<int, SliceMaterialData> Materials, float MassKg);
internal sealed record SlicePlan(SlicePlane Plane);
internal sealed record CpuFragment(SlicePiece Piece, PrincipalFragment Frame, SliceFace[][] Solids, double3 Extents, float MassKg);
internal sealed record CpuSlice(SlicePlane Plane, CpuFragmentAsset[] Fragments);

internal static class SliceGeometryWork
{
    internal static CpuSlice Calculate(SliceInput input, CancellationToken cancel, Action<SlicePlan> publish)
    {
        cancel.ThrowIfCancellationRequested();
        if (!SliceShellBuilder.TryPrepare(input.Groups, out var source, out string reason, cancel)) throw new InvalidDataException(reason);
        string failure = "No broad section could be found.";
        foreach (var candidate in AssistedSliceGeometry.Candidates(source, input.Axes))
        {
            cancel.ThrowIfCancellationRequested();
            if (!PlaneSlicer.TrySliceCompound(source, candidate, out var result, out failure, cancellation: cancel)) continue;
            var prepared = new List<CpuFragment>();
            double volume = result!.Pieces.Sum(p => p.Volume);
            if (result.Pieces.Length is < 2 or > 8 || !double.IsFinite(volume) || volume <= 0) continue;
            foreach (var piece in result.Pieces)
            {
                cancel.ThrowIfCancellationRequested();
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out failure)
                    || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var solids, out var extents, out failure, cancel)) break;
                int groups = 0; var points = new HashSet<CutPoint>();
                foreach (var solid in solids)
                {
                    groups += solid.Select(f => f.Material).Distinct().Count();
                    foreach (var f in solid) { points.Add(f.A.Position); points.Add(f.B.Position); points.Add(f.C.Position); }
                }
                if (groups > 2046 || points.Count > 65536 || solids.Sum(s => s.Length) > 200_000)
                { failure = "This cut exceeds the native fragment geometry budget."; break; }
                float mass = (float)(input.MassKg * piece.Volume / volume);
                if (!float.IsFinite(mass) || mass <= .0001f) { failure = "Slice fragment mass is invalid."; break; }
                prepared.Add(new(piece, frame, solids, extents, mass));
            }
            if (prepared.Count != result.Pieces.Length) continue;
            // Start the visible stroke as soon as its exact plane is known. Hulls, serialization
            // and file I/O continue on this worker while the main view animates the saber.
            publish(new(candidate));
            var assets = new CpuFragmentAsset[prepared.Count];
            for (int i = 0; i < assets.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                assets[i] = FragmentAssetWriter.Build(prepared[i], input.Materials, cancel);
            }
            return new(result.EffectivePlane, assets);
        }
        throw new InvalidDataException("This part could not be sliced: " + failure);
    }
}
