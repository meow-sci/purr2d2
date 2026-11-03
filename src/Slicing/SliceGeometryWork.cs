using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

// Worker inputs contain copied values only: never Part, Vehicle, MeshReference or a native material.
internal sealed record SliceMaterialData(string Id, bool Glass, uint ClutterColor = 0, uint ClutterLuminosity = 0,
    bool Foliage = false, bool DoubleSided = false, bool KeepBackfaceNormals = false);
internal sealed record SliceInput(SliceFace[][] Groups, CutPoint[] Axes,
    IReadOnlyDictionary<int, SliceMaterialData> Materials, float MassKg, uint Seed = 0)
{
    internal SliceGeometryBudget Geometry { get; init; } = SliceGeometryBudget.Current;
    internal bool GeneratedClosedGeometry { get; init; }
    internal bool SourceSolidWood { get; init; }
    internal SliceCutKind CutKind { get; init; }
    internal ContactSlice? Contact { get; init; }
    // Open visual cards never enter closed-solid preparation, mass or collision.
    internal SliceFace[] Foliage { get; init; } = [];
}
internal sealed record SlicePlan(SlicePlane Plane);
internal sealed record CpuFragment(SlicePiece Piece, PrincipalFragment Frame, SliceFace[][] Solids, double3 Extents, float MassKg)
{
    // Canonical faces only. The exporter adds any render-only reversed faces.
    internal SliceFace[] Foliage { get; init; } = [];
    internal SliceCutKind CutKind { get; init; }
}
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
        if (input.SourceSolidWood && input.GeneratedClosedGeometry)
            throw new InvalidDataException("Generated closed fragments cannot be reinterpreted as native tree tubes.");
        if (!geometry.AllowsSource(input.Groups.Length, input.Groups.Sum(g => (long)g.Length) + input.Foliage.LongLength))
            throw new InvalidDataException("The source wood and foliage exceed the configured triangle budget.");
        bool preparedSource = input.SourceSolidWood
            ? TreeSolidPreparation.TryPrepare(input.Groups, out var source, out string reason, cancel, geometry)
            : SliceShellBuilder.TryPrepare(input.Groups, out source, out reason, cancel, input.GeneratedClosedGeometry, geometry);
        if (!preparedSource)
        {
            if (input.Contact is not null) throw new InvalidDataException("Cannot slice at contact: " + reason);
            if (input.Foliage.Length != 0 || input.CutKind == SliceCutKind.Wood) throw new InvalidDataException(reason);
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
            // A faceted section has no single plane along which to clip open cards.
            attemptsToTry = candidates.Take(input.Foliage.Length == 0 && input.CutKind != SliceCutKind.Wood ? 3 : 0).Select((plane, i) => (Plane: plane, Faceted: true, Index: i))
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
            SliceFace[] positiveFoliage = [], negativeFoliage = [];
            if (input.Foliage.Length != 0)
            {
                if (result.Pieces.Count(p => p.PositiveSide) != 1 || result.Pieces.Count(p => !p.PositiveSide) != 1)
                    throw new InvalidDataException("Foliage requires one structural aggregate on each side of the cut.");
                FoliageSliceGeometry.Split(input.Foliage, result.EffectivePlane, out positiveFoliage, out negativeFoliage, cancel);
                long output = result.Pieces.Sum(p => (long)p.Faces.Length)
                    + FoliageSliceGeometry.RenderTriangles(positiveFoliage, input.Materials)
                    + FoliageSliceGeometry.RenderTriangles(negativeFoliage, input.Materials);
                if (output > geometry.OutputTriangles)
                { failure = "The cut wood and foliage exceed the configured output triangle budget."; continue; }
            }
            foreach (var rawPiece in result.Pieces)
            {
                cancel.ThrowIfCancellationRequested();
                var piece = input.CutKind == SliceCutKind.Wood ? WoodCapGeometry.Map(rawPiece, result.EffectivePlane, cancel) : rawPiece;
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out failure)
                    || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var solids, out var extents, out failure, cancel, geometry)) break;
                var foliage = FoliageSliceGeometry.Prepare(piece.PositiveSide ? positiveFoliage : negativeFoliage,
                    frame.SourceToFragment, cancel);
                if (!geometry.AllowsFragment(solids.Length, solids.Sum(s => (long)s.Length)
                    + FoliageSliceGeometry.RenderTriangles(foliage, input.Materials)))
                { failure = "The fragment wood and foliage exceed the configured export triangle budget."; break; }
                var visualGroups = foliage.Length == 0 ? solids : solids.Append(foliage).ToArray();
                if (foliage.Length != 0) extents = FoliageSliceGeometry.Extents(visualGroups);
                var points = new HashSet<CutPoint>();
                foreach (var solid in visualGroups)
                {
                    foreach (var f in solid) { points.Add(f.A.Position); points.Add(f.B.Position); points.Add(f.C.Position); }
                }
                int groups = FragmentAssetWriter.CountRenderSurfaces(visualGroups, input.Materials, cutKind: input.CutKind);
                if (groups > SliceResourceLimits.MaximumRenderSurfaces || points.Count > SliceResourceLimits.MaximumFragmentVertices)
                { failure = $"This cut exceeds the fragment export budget ({groups:N0} / {SliceResourceLimits.MaximumRenderSurfaces:N0} render surfaces; {points.Count:N0} / {SliceResourceLimits.MaximumFragmentVertices:N0} unique positions)."; break; }
                float mass = (float)(input.MassKg * piece.Volume / volume);
                if (!float.IsFinite(mass) || mass <= .0001f) { failure = "Slice fragment mass is invalid."; break; }
                prepared.Add(new(piece, frame, solids, extents, mass) { Foliage = foliage, CutKind = input.CutKind });
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
            if (input.Foliage.Length != 0 || input.CutKind == SliceCutKind.Wood) throw new InvalidDataException("This tree could not be sliced: " + failure);
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

/// <summary>Open-card clipping, preserving canonical UVs and normals independently of rigid wood.</summary>
internal static class FoliageSliceGeometry
{
    internal static void Split(IReadOnlyList<SliceFace> source, SlicePlane plane, out SliceFace[] positive,
        out SliceFace[] negative, CancellationToken cancel = default)
    {
        if (!plane.Normal.Finite || !double.IsFinite(plane.Offset) || plane.Normal.LengthSquared < 1e-20)
            throw new InvalidDataException("Foliage cutting plane is invalid.");
        var above = new List<SliceFace>(); var below = new List<SliceFace>();
        foreach (var face in source)
        {
            cancel.ThrowIfCancellationRequested();
            SliceVertex[] vertices = [face.A, face.B, face.C];
            if (vertices.Any(v => !v.Position.Finite || !v.Normal.Finite || !double.IsFinite(v.U) || !double.IsFinite(v.V)))
                throw new InvalidDataException("Foliage contains nonfinite vertex attributes.");
            var distances = vertices.Select(v => CutPoint.Dot(plane.Normal, v.Position) - plane.Offset).ToArray();
            if (distances.All(d => d >= 0)) { above.Add(face); continue; }
            if (distances.All(d => d <= 0)) { below.Add(face); continue; }
            Clip(true, above); Clip(false, below);
            void Clip(bool side, List<SliceFace> output)
            {
                var polygon = new List<SliceVertex>(4);
                for (int i = 0; i < 3; i++)
                {
                    int next = (i + 1) % 3;
                    bool inside = side ? distances[i] >= 0 : distances[i] <= 0;
                    bool nextInside = side ? distances[next] >= 0 : distances[next] <= 0;
                    if (inside) polygon.Add(vertices[i]);
                    if (inside != nextInside && distances[i] != 0 && distances[next] != 0)
                    {
                        var intersection = SliceVertex.Lerp(vertices[i], vertices[next], distances[i] / (distances[i] - distances[next]));
                        // Both halves use exactly the same point even for non-unit planes.
                        double residual = CutPoint.Dot(plane.Normal, intersection.Position) - plane.Offset;
                        polygon.Add(intersection with { Position = intersection.Position - plane.Normal * (residual / plane.Normal.LengthSquared) });
                    }
                }
                for (int i = 1; i + 1 < polygon.Count; i++)
                    if (CutPoint.Cross(polygon[i].Position - polygon[0].Position, polygon[i + 1].Position - polygon[0].Position).LengthSquared > 0)
                        output.Add(new(polygon[0], polygon[i], polygon[i + 1], face.Material));
            }
        }
        positive = above.ToArray(); negative = below.ToArray();
    }

    internal static SliceFace[] Prepare(IReadOnlyList<SliceFace> faces, double4x4 transform, CancellationToken cancel = default)
    {
        var result = new List<SliceFace>(faces.Count);
        foreach (var face in faces)
        {
            cancel.ThrowIfCancellationRequested();
            var converted = new SliceFace(Convert(face.A), Convert(face.B), Convert(face.C), face.Material);
            if (CutPoint.Cross(converted.B.Position - converted.A.Position, converted.C.Position - converted.A.Position).LengthSquared > 0)
                result.Add(converted);
        }
        return result.ToArray();
        SliceVertex Convert(SliceVertex vertex)
        {
            var p = double3.Transform(new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), transform);
            var n = double3.TransformNormal(new(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z), transform);
            var result = new SliceVertex(new((float)p.X, (float)p.Y, (float)p.Z), new CutPoint(n.X, n.Y, n.Z).Normalized, vertex.U, vertex.V);
            if (!result.Position.Finite || !result.Normal.Finite || !float.IsFinite((float)result.U) || !float.IsFinite((float)result.V))
                throw new InvalidDataException("Foliage cannot be represented at native float precision.");
            return result;
        }
    }

    internal static long RenderTriangles(IEnumerable<SliceFace> faces, IReadOnlyDictionary<int, SliceMaterialData> materials)
    {
        long triangles = 0;
        foreach (var face in faces)
        {
            if (!materials.TryGetValue(face.Material, out var material) || !material.Foliage)
                throw new InvalidDataException("An open foliage card has no durable foliage material.");
            triangles += material.DoubleSided ? 2 : 1;
        }
        return triangles;
    }

    internal static double3 Extents(IEnumerable<SliceFace[]> groups)
    {
        var min = new double3(double.PositiveInfinity); var max = new double3(double.NegativeInfinity);
        foreach (var face in groups.SelectMany(g => g))
            foreach (var vertex in new[] { face.A, face.B, face.C })
            {
                var p = new double3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                min = double3.Min(min, p); max = double3.Max(max, p);
            }
        return max - min;
    }
}
