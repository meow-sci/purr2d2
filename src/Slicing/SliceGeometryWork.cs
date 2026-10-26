using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

// Worker inputs contain copied values only: never Part, Vehicle, MeshReference or a native material.
internal sealed record SliceMaterialData(string Id, bool Glass);
internal sealed record SliceInput(SliceFace[][] Groups, CutPoint[] Axes,
    IReadOnlyDictionary<int, SliceMaterialData> Materials, float MassKg, uint Seed = 0)
{
    internal SliceGeometryBudget Geometry { get; init; } = SliceGeometryBudget.Current;
    internal bool GeneratedClosedGeometry { get; init; }
    internal ContactSlice? Contact { get; init; }
}
internal sealed record SlicePlan(SlicePlane Plane);
internal sealed record CpuFragment(SlicePiece Piece, PrincipalFragment Frame, SliceFace[][] Solids, double3 Extents, float MassKg);
internal sealed record CpuSlice(SlicePlane Plane, CpuFragmentAsset[] Fragments)
{
    internal bool Faceted { get; init; }
    internal int Attempts { get; init; }
    internal string FacetFallback { get; init; } = "";
    internal bool AuthoredSubpartFallback { get; init; }
    internal bool ContactDirectionFallback { get; init; }
}

internal static class SliceGeometryWork
{
    internal static CpuSlice Calculate(SliceInput input, CancellationToken cancel, Action<SlicePlan> publish)
    {
        cancel.ThrowIfCancellationRequested();
        var geometry = input.Geometry;
        if (!SliceShellBuilder.TryPrepare(input.Groups, out var source, out string reason, cancel, input.GeneratedClosedGeometry, geometry))
        {
            if (input.Contact is not null) throw new InvalidDataException("Cannot slice at contact: " + reason);
            if (QuietSubpartBreakup.TryBuild(input, out var authored, out var authoredFailure, cancel))
            { publish(new(authored!.Plane)); return authored with { FacetFallback = reason }; }
            throw new InvalidDataException(reason + " Authored-subpart fallback: " + authoredFailure);
        }
        IEnumerable<(SlicePlane Plane, bool Faceted, int Index)> attemptsToTry;
        if (input.Contact is { } contact)
        {
            if (!ContactSliceGeometry.TryCandidates(contact, out var contactCandidates, out var contactReason))
                throw new InvalidDataException("Cannot slice at contact: " + contactReason);
            attemptsToTry = contactCandidates.Concat(ContactSliceGeometry.AxisCandidates(contact, input.Axes))
                .Select((plane, i) => (Plane: plane, Faceted: false, Index: i));
        }
        else
        {
            var candidates = AssistedSliceGeometry.Candidates(source, input.Axes, input.Seed).ToArray();
            attemptsToTry = candidates.Take(3).Select((plane, i) => (Plane: plane, Faceted: true, Index: i))
                .Concat(candidates.Select((plane, i) => (Plane: plane, Faceted: false, Index: i)));
        }

        string failure = input.Contact is null ? "No broad section could be found." : "No valid section passed through the contact point.";
        string facetFailure = ""; int attempts = 0;
        // Manual cuts use only the bounded anchored planes; assisted cuts retain
        // their existing three faceted attempts followed by the oblique/axis fallbacks.
        foreach (var attempt in attemptsToTry)
        {
            var candidate = attempt.Plane; attempts++;
            cancel.ThrowIfCancellationRequested();
            PlaneSliceResult? result;
            bool sliced = attempt.Faceted
                ? FacetedSliceGeometry.TrySlice(source, candidate, input.Seed + (uint)attempt.Index * 0x9e3779b9u, out result, out failure, cancel, geometry)
                : PlaneSlicer.TrySliceCompound(source, candidate, out result, out failure, cancellation: cancel, geometry: geometry);
            if (!sliced) { if (attempt.Faceted) facetFailure = failure; continue; }
            var prepared = new List<CpuFragment>();
            double volume = result!.Pieces.Sum(p => p.Volume);
            if (result.Pieces.Length is < 2 or > 8 || !double.IsFinite(volume) || volume <= 0)
            {
                if (input.Contact is not null) failure = "The anchored plane did not produce two valid fragments.";
                continue;
            }
            foreach (var piece in result.Pieces)
            {
                cancel.ThrowIfCancellationRequested();
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out failure)
                    || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var solids, out var extents, out failure, cancel, geometry)) break;
                var points = new HashSet<CutPoint>();
                foreach (var solid in solids)
                {
                    foreach (var f in solid) { points.Add(f.A.Position); points.Add(f.B.Position); points.Add(f.C.Position); }
                }
                int groups = FragmentAssetWriter.CountRenderSurfaces(solids, input.Materials);
                if (groups > SliceResourceLimits.MaximumRenderSurfaces || points.Count > SliceResourceLimits.MaximumFragmentVertices)
                { failure = $"This cut exceeds the fragment export budget ({groups:N0} / {SliceResourceLimits.MaximumRenderSurfaces:N0} render surfaces; {points.Count:N0} / {SliceResourceLimits.MaximumFragmentVertices:N0} unique positions)."; break; }
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
                    assets[i] = FragmentAssetWriter.Build(prepared[i], input.Materials, cancel, geometry);
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
            return new(result.EffectivePlane, assets)
            {
                Faceted = attempt.Faceted, Attempts = attempts, FacetFallback = attempt.Faceted ? "" : facetFailure,
                ContactDirectionFallback = input.Contact is not null && attempt.Index >= ContactSliceGeometry.CandidateCount
            };
        }
        if (input.Contact is null)
        {
            if (QuietSubpartBreakup.TryBuild(input, out var fallback, out var fallbackFailure, cancel))
            {
                publish(new(fallback!.Plane));
                return fallback with { Attempts = attempts, FacetFallback = failure };
            }
            throw new InvalidDataException("This part could not be sliced: " + failure + " Authored-subpart fallback: " + fallbackFailure);
        }
        if (string.IsNullOrWhiteSpace(failure)) failure = "The anchored plane did not produce exportable fragments.";
        throw new InvalidDataException("Cannot slice at contact: " + failure);
    }
}
