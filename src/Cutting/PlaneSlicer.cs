using System;
using System.Collections.Generic;
using System.Linq;
using Purr2D2.Core;

namespace Purr2D2.Cutting;

internal readonly record struct SliceVertex(CutPoint Position, CutPoint Normal, double U, double V)
{
    public static SliceVertex Lerp(SliceVertex a, SliceVertex b, double t) => new(
        a.Position + (b.Position - a.Position) * t,
        (a.Normal + (b.Normal - a.Normal) * t).Normalized,
        a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t);
}
internal readonly record struct SliceFace(SliceVertex A, SliceVertex B, SliceVertex C, int Material);
internal readonly record struct SlicePlane(CutPoint Normal, double Offset);
internal readonly record struct SliceInertia(double XX, double YY, double ZZ, double XY, double XZ, double YZ);
internal sealed record SlicePiece(SliceFace[] Faces, double Volume, CutPoint Centroid, SliceInertia Inertia, bool PositiveSide)
{
    // In Faces order: preserves validated closed-solid provenance across float32 export.
    public SliceFace[][]? ClosedSolids { get; init; }
}
internal sealed record PlaneSliceResult(SlicePiece[] Pieces, SlicePlane EffectivePlane, double SourceVolume,
    int ContourCount, int HoleCount);

/// <summary>
/// Plane clipping of consistently wound, closed triangle solids. It never edits the source.
/// Caps use an even/odd trapezoidal subdivision, preserving holes and disconnected regions.
/// Side and cap edges share every introduced subdivision point; output must pass a welded,
/// oriented manifold audit and volume-conservation check before a result is published.
/// </summary>
internal static class PlaneSlicer
{
    // Contour complexity and cap work are independent of source triangle/solid admission.
    private const int MaximumContourVertices = 2_048;
    private const int MaximumCapCells = 30_000;

    public static bool TrySlice(IReadOnlyList<SliceFace> source, SlicePlane plane,
        out PlaneSliceResult? result, out string failure, int capMaterial = -1, SliceGeometryBudget? geometry = null)
    {
        result = null;
        try
        {
            result = new Operation(source, plane, capMaterial, geometry ?? SliceGeometryBudget.Current).Run();
            failure = "";
            return true;
        }
        catch (SliceRejected error) { failure = error.Message; return false; }
    }

    /// <summary>
    /// A native rigid part may contain overlapping closed authored solids (rods, bolts, plates),
    /// rather than a Boolean-unioned surface. Validate each authored group, slice its closed
    /// solids independently, then retain the original aggregate rigidity on each side. Volumes
    /// are additive exactly as in the authored compound; this is deliberately not mesh union.
    /// </summary>
    public static bool TrySliceCompound(IReadOnlyList<SliceFace[]> authoredGroups, SlicePlane plane,
        out PlaneSliceResult? result, out string failure, int capMaterial = -1, CancellationToken cancellation = default,
        SliceGeometryBudget? geometry = null)
    {
        geometry ??= SliceGeometryBudget.Current;
        result = null;
        try
        {
            long triangleCount = authoredGroups.Sum(x => (long)x.Length);
            Require(authoredGroups.Count > 0 && triangleCount >= 4 && geometry.AllowsSource(authoredGroups.Count, triangleCount),
                $"Compound source exceeds the geometry budget ({geometry.SourceUsage(authoredGroups.Count, triangleCount)}). {SliceGeometryBudget.SettingsHint}");
            // Audit once with each group's own topology policy. Canonical seams
            // before choosing the one common plane, including carried-through solids.
            var prepared = new List<(SliceFace[] Faces, List<List<int>> Components, double Epsilon)>();
            foreach (var group in authoredGroups)
            {
                cancellation.ThrowIfCancellationRequested();
                Require(TryAudit(group, out _, out var components, out _, out var auditFailure, geometry),
                    $"Compound input group {prepared.Count} is not a valid solid: " + auditFailure);
                prepared.Add((CanonicalPositions(group), components, TopologyTolerance(group)));
            }
            var allPoints = prepared.SelectMany(x => x.Faces).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
            Require(allPoints.All(x => x.Finite), "Compound source has nonfinite positions.");
            var min = allPoints.Aggregate(new CutPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity), CutPoint.Min);
            var max = allPoints.Aggregate(new CutPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity), CutPoint.Max);
            double diagonal = (max - min).Length;
            Require(double.IsFinite(diagonal) && diagonal > 1e-7 && diagonal < 1e7, "Compound bounds are outside supported precision.");
            double epsilon = Math.Max(Math.Max(1e-9, diagonal * 1e-8), prepared.Max(g => g.Epsilon));
            var effectivePlane = StablePlane(allPoints, plane, epsilon);
            var positive = new List<SliceFace>(); var negative = new List<SliceFace>();
            var positiveSolids = new List<SliceFace[]>(); var negativeSolids = new List<SliceFace[]>();
            double sourceVolume = 0; int contours = 0, holes = 0, solidCount = 0;
            foreach (var (group, boundaryComponents, topologyEpsilon) in prepared)
            {
                cancellation.ThrowIfCancellationRequested();
                var shells = boundaryComponents.Select(c => c.Select(i => group[i]).ToArray()).ToArray();
                var properties = shells.Select(Properties).ToArray();
                var solids = new List<(List<SliceFace> Faces, double OuterVolume)>();
                var cavities = new List<int>();
                for (int i = 0; i < shells.Length; i++)
                {
                    Require(Math.Abs(properties[i].Volume) > topologyEpsilon * topologyEpsilon * topologyEpsilon * 100, "Compound contains a zero-volume shell.");
                    if (properties[i].Volume > 0) solids.Add((shells[i].ToList(), properties[i].Volume));
                    else cavities.Add(i);
                }
                foreach (int cavity in cavities)
                {
                    int enclosing = -1; double bestVolume = double.PositiveInfinity;
                    for (int i = 0; i < solids.Count; i++)
                        if (solids[i].OuterVolume < bestVolume && ContainsSolid(solids[i].Faces, shells[cavity][0].A.Position))
                        { enclosing = i; bestVolume = solids[i].OuterVolume; }
                    Require(enclosing >= 0, "Compound inward shell has no enclosing solid.");
                    solids[enclosing].Faces.AddRange(shells[cavity]);
                }
                foreach (var solid in solids)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Require(++solidCount <= geometry.Solids,
                        $"Compound exceeds the closed-solid limit ({solidCount:N0} / {geometry.Solids:N0}). {SliceGeometryBudget.SettingsHint}");
                    var faces = solid.Faces.ToArray(); var props = Properties(faces);
                    Require(props.Volume > topologyEpsilon * topologyEpsilon * topologyEpsilon * 100, "Compound solid has nonpositive material volume.");
                    sourceVolume += props.Volume;
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                    foreach (var face in faces)
                        foreach (var vertex in new[] { face.A, face.B, face.C })
                        {
                            double distance = CutPoint.Dot(effectivePlane.Normal, vertex.Position) - effectivePlane.Offset;
                            lo = Math.Min(lo, distance); hi = Math.Max(hi, distance);
                        }
                    if (lo > 0) { positive.AddRange(faces); positiveSolids.Add(faces); }
                    else if (hi < 0) { negative.AddRange(faces); negativeSolids.Add(faces); }
                    else
                    {
                        PlaneSliceResult sliced;
                        try { sliced = new Operation(faces, effectivePlane, capMaterial, geometry, fixedPlane: true).Run(); }
                        catch (SliceRejected error) { throw new SliceRejected("Closed compound solid cannot be sliced: " + error.Message); }
                        Require(sliced != null && (sliced.EffectivePlane.Normal - effectivePlane.Normal).Length < 1e-12
                            && Math.Abs(sliced.EffectivePlane.Offset - effectivePlane.Offset) < epsilon,
                            "Compound slice cannot use one consistent cutting plane.");
                        foreach (var piece in sliced!.Pieces)
                        {
                            (piece.PositiveSide ? positive : negative).AddRange(piece.Faces);
                            (piece.PositiveSide ? positiveSolids : negativeSolids).Add(piece.Faces);
                        }
                        contours += sliced.ContourCount; holes += sliced.HoleCount;
                    }
                    Require(positive.Count + negative.Count <= geometry.OutputTriangles,
                        $"Compound slice exceeds the output triangle budget ({positive.Count + negative.Count:N0} / {geometry.OutputTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
                }
            }
            Require(contours > 0 && positive.Count > 0 && negative.Count > 0, "Plane does not cut material in this rigid compound.");
            var positiveProps = Properties(positive); var negativeProps = Properties(negative);
            Require(positiveProps.Volume > sourceVolume * 1e-9 && negativeProps.Volume > sourceVolume * 1e-9, "Compound slice creates a microscopic rigid half.");
            Require(Math.Abs(positiveProps.Volume + negativeProps.Volume - sourceVolume) <= Math.Max(sourceVolume * 2e-6, epsilon * diagonal * diagonal * 8),
                "Compound slice failed additive volume conservation.");
            result = new(new[] { new SlicePiece(positive.ToArray(), positiveProps.Volume, positiveProps.Centroid, positiveProps.Inertia, true) { ClosedSolids = positiveSolids.ToArray() },
                new SlicePiece(negative.ToArray(), negativeProps.Volume, negativeProps.Centroid, negativeProps.Inertia, false) { ClosedSolids = negativeSolids.ToArray() } },
                effectivePlane, sourceVolume, contours, holes);
            failure = ""; return true;
        }
        catch (SliceRejected error) { failure = error.Message; return false; }
    }

    /// <summary>Revalidate generated/float-quantized geometry before exposing it to native physics.</summary>
    public static bool TryAudit(IReadOnlyList<SliceFace> faces, out string failure, SliceGeometryBudget? geometry = null)
        => TryAudit(faces, out _, out failure, geometry);
    public static bool TryAuditCompound(IReadOnlyList<SliceFace[]> solids, out double volume, out string failure,
        SliceGeometryBudget? geometry = null)
        => TryAuditCompound(solids, out volume, out _, out failure, geometry);

    internal static bool TryAuditCompound(IReadOnlyList<SliceFace[]> solids, out double volume, out int solidCount,
        out string failure, SliceGeometryBudget? geometry = null)
    {
        geometry ??= SliceGeometryBudget.Current;
        volume = 0; solidCount = 0;
        if (solids.Count == 0 || solids.Count > geometry.Solids || solids.Sum(s => (long)s.Length) > geometry.OutputTriangles)
        { failure = $"Generated closed-solid compound exceeds the audit budget ({solids.Count:N0} / {geometry.Solids:N0} solids; {solids.Sum(s => (long)s.Length):N0} / {geometry.OutputTriangles:N0} output triangles). {SliceGeometryBudget.SettingsHint}"; return false; }
        foreach (var solid in solids)
        {
            if (!TryAuditSolids(solid, out double solidVolume, out int count, out failure, geometry)) return false;
            solidCount += count;
            if (solidCount > geometry.Solids)
            { failure = $"Generated compound exceeds the closed-solid limit ({solidCount:N0} / {geometry.Solids:N0}). {SliceGeometryBudget.SettingsHint}"; return false; }
            volume += solidVolume;
        }
        failure = ""; return double.IsFinite(volume) && volume > 0;
    }
    public static bool TryAudit(IReadOnlyList<SliceFace> faces, out double volume, out string failure, SliceGeometryBudget? geometry = null)
        => TryAuditSolids(faces, out volume, out _, out failure, geometry);

    internal static bool TryAuditSolids(IReadOnlyList<SliceFace> faces, out double volume, out int solidCount, out string failure,
        SliceGeometryBudget? geometry = null)
        => TryAudit(faces, out volume, out _, out solidCount, out failure, geometry ?? SliceGeometryBudget.Current);

    private static bool TryAudit(IReadOnlyList<SliceFace> faces, out double volume, out List<List<int>> components, out int solidCount, out string failure,
        SliceGeometryBudget geometry)
    {
        volume = 0; components = []; solidCount = 0;
        try
        {
            Require(faces.Count >= 4 && faces.Count <= geometry.OutputTriangles,
                $"Generated mesh triangle count is outside the audit budget ({faces.Count:N0} / {geometry.OutputTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
            var min = new CutPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new CutPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (var face in faces)
                foreach (var vertex in new[] { face.A, face.B, face.C })
                {
                    Require(vertex.Position.Finite && vertex.Normal.Finite && double.IsFinite(vertex.U) && double.IsFinite(vertex.V), "Generated mesh has nonfinite attributes.");
                    min = CutPoint.Min(min, vertex.Position); max = CutPoint.Max(max, vertex.Position);
                }
            double diagonal = (max - min).Length;
            Require(double.IsFinite(diagonal) && diagonal > 1e-7 && diagonal < 1e7, "Generated bounds are degenerate or outside supported precision.");
            double epsilon = TopologyTolerance(faces);
            foreach (var face in faces)
                Require(CutPoint.Cross(face.B.Position - face.A.Position, face.C.Position - face.A.Position).Length > epsilon * epsilon,
                    "Generated mesh contains degenerate triangles.");
            Audit(faces, epsilon, out components);
            var properties = Properties(faces);
            Require(properties.Volume > epsilon * epsilon * epsilon * 100, "Generated mesh has nonpositive solid volume.");
            Require(properties.Inertia.XX > 0 && properties.Inertia.YY > 0 && properties.Inertia.ZZ > 0
                && double.IsFinite(properties.Inertia.XX + properties.Inertia.YY + properties.Inertia.ZZ), "Generated mesh has invalid inertia.");
            var shells = components.Select(c => c.Select(i => faces[i]).ToArray()).ToArray();
            var shellVolumes = shells.Select(s => Properties(s).Volume).ToArray();
            solidCount = shellVolumes.Count(v => v > 0);
            Require(solidCount <= geometry.Solids,
                $"Generated mesh exceeds the closed-solid limit ({solidCount:N0} / {geometry.Solids:N0}). {SliceGeometryBudget.SettingsHint}");
            for (int i = 0; i < shells.Length; i++)
            {
                Require(Math.Abs(shellVolumes[i]) > epsilon * epsilon * epsilon * 100, "Generated mesh has a zero-volume shell.");
                if (shellVolumes[i] < 0)
                    Require(Enumerable.Range(0, shells.Length).Any(j => shellVolumes[j] > 0 && ContainsSolid(shells[j], shells[i][0].A.Position)),
                        "Generated inward shell has no enclosing solid.");
            }
            volume = properties.Volume; failure = ""; return true;
        }
        catch (SliceRejected error) { failure = error.Message; return false; }
    }

    internal static double TopologyTolerance(IReadOnlyList<SliceFace> faces)
    {
        // Radius around the vertex mean is invariant under rotation. Unique,
        // sorted points also remove tessellation weighting and face-order drift.
        var points = faces.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        if (points.Length == 0) return 1e-9;
        var origin = points[0]; var sum = default(CutPoint);
        foreach (var point in points) sum += point - origin;
        var center = origin + sum / points.Length;
        return Math.Max(1e-9, points.Max(p => (p - center).Length) * 2e-8);
    }

    /// <summary>Give accepted epsilon-welded seams one exact position before further subdivision.</summary>
    internal static SliceFace[] CanonicalPositions(IReadOnlyList<SliceFace> faces)
    {
        var points = faces.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
        var positions = new List<CutPoint>();
        var weld = new PositionWeld(TopologyTolerance(faces), positions);
        foreach (var point in points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)) weld.Add(point);
        SliceVertex Convert(SliceVertex vertex) => vertex with { Position = positions[weld.Add(vertex.Position)] };
        return faces.Select(f => new SliceFace(Convert(f.A), Convert(f.B), Convert(f.C), f.Material)).ToArray();
    }

    private sealed class SliceRejected(string message) : Exception(message);
    private static void Require(bool condition, string message) { if (!condition) throw new SliceRejected(message); }
    private readonly record struct Edge(int A, int B)
    {
        public static Edge Of(int a, int b) => a < b ? new(a, b) : new(b, a);
    }
    private readonly record struct IndexedFace(SliceFace Face, int A, int B, int C);
    private readonly record struct ClipVertex(SliceVertex Vertex, int CutId);
    private sealed record Polygon(List<ClipVertex> Vertices, int Material);
    private readonly record struct Point2(double X, double Y)
    {
        public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
        public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    }
    private sealed record ContourEdge(int A, int B);

    private sealed class Operation
    {
        private readonly IReadOnlyList<SliceFace> _input;
        private readonly SlicePlane _requested;
        private readonly int _capMaterial;
        private readonly bool _fixedPlane;
        private readonly SliceGeometryBudget _geometry;
        private readonly List<IndexedFace> _source = new();
        private readonly List<CutPoint> _positions = new();
        private readonly Dictionary<Edge, int> _intersectionByEdge = new();
        private readonly List<CutPoint> _cutPoints = new();
        private readonly List<Point2> _projected = new();
        private readonly List<ContourEdge> _contourEdges = new();
        private readonly Dictionary<Edge, int> _contourEdgeIds = new();
        private readonly List<Polygon> _positive = new(), _negative = new();
        private readonly List<List<int>> _loops = new();
        private readonly Dictionary<(int Edge, int Level), CutPoint> _levelPointsByEdge = new();
        private readonly List<List<CutPoint>> _levelPoints = new();
        private readonly List<double> _levels = new();
        private double _epsilon;
        private CutPoint _u, _v;
        private SlicePlane _plane;

        public Operation(IReadOnlyList<SliceFace> input, SlicePlane plane, int capMaterial, SliceGeometryBudget geometry, bool fixedPlane = false)
        { _input = input; _requested = plane; _capMaterial = capMaterial; _geometry = geometry; _fixedPlane = fixedPlane; }

        public PlaneSliceResult Run()
        {
            Require(_input.Count >= 4 && _input.Count <= _geometry.SourceTriangles,
                $"Source triangle count is outside the slicing budget ({_input.Count:N0} / {_geometry.SourceTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
            Require(_requested.Normal.Finite && _requested.Normal.Length > 1e-12 && double.IsFinite(_requested.Offset), "Slice plane is invalid.");
            var minimum = new CutPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var maximum = new CutPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (var face in _input)
                foreach (var vertex in new[] { face.A, face.B, face.C })
                {
                    Require(vertex.Position.Finite && vertex.Normal.Finite && double.IsFinite(vertex.U) && double.IsFinite(vertex.V), "Source has nonfinite attributes.");
                    minimum = CutPoint.Min(minimum, vertex.Position); maximum = CutPoint.Max(maximum, vertex.Position);
                }
            double diagonal = (maximum - minimum).Length;
            Require(double.IsFinite(diagonal) && diagonal > 1e-7 && diagonal < 1e7, "Source bounds are degenerate or outside supported precision.");
            _epsilon = TopologyTolerance(_input);
            var weld = new PositionWeld(_epsilon, _positions);
            foreach (var point in _input.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)) weld.Add(point);
            foreach (var face in _input)
            {
                int a = weld.Add(face.A.Position), b = weld.Add(face.B.Position), c = weld.Add(face.C.Position);
                Require(a != b && b != c && a != c, "Source has collapsed triangles after seam welding.");
                var normal = CutPoint.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
                Require(normal.Length > _epsilon * _epsilon, "Source has a degenerate triangle.");
                var canonical = new SliceFace(Canonical(face.A, a, normal), Canonical(face.B, b, normal), Canonical(face.C, c, normal), face.Material);
                _source.Add(new(canonical, a, b, c));
            }
            var canonicalFaces = _source.Select(x => x.Face).ToArray();
            List<List<int>> sourceComponents;
            try { Audit(canonicalFaces, _epsilon, out sourceComponents); }
            catch (SliceRejected error) { throw new SliceRejected("Canonical source: " + error.Message); }
            int solidCount = sourceComponents.Count(c => Properties(c.Select(i => canonicalFaces[i]).ToArray()).Volume > 0);
            Require(solidCount <= _geometry.Solids,
                $"Source exceeds the closed-solid limit ({solidCount:N0} / {_geometry.Solids:N0}). {SliceGeometryBudget.SettingsHint}");
            var sourceProperties = Properties(canonicalFaces);
            Require(sourceProperties.Volume > _epsilon * _epsilon * _epsilon * 100, "Source has nonpositive signed solid volume; check winding.");
            ChoosePlane();
            foreach (var face in _source) Clip(face);
            Require(_contourEdges.Count >= 3 && _positive.Count > 0 && _negative.Count > 0, "Plane does not divide the solid.");
            Require(_cutPoints.Count <= MaximumContourVertices, "Cut contour exceeds the runtime budget.");
            BuildContours();
            int holes = CheckContours();
            BuildLevels();
            var positiveFaces = EmitSides(_positive);
            var negativeFaces = EmitSides(_negative);
            AddCaps(positiveFaces, negativeFaces);
            Require(positiveFaces.Count + negativeFaces.Count <= _geometry.OutputTriangles,
                $"Sliced geometry exceeds the output budget ({positiveFaces.Count + negativeFaces.Count:N0} / {_geometry.OutputTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
            List<List<int>> positiveComponents, negativeComponents;
            try { Audit(positiveFaces, _epsilon, out positiveComponents); Audit(negativeFaces, _epsilon, out negativeComponents); }
            catch (SliceRejected error) { throw new SliceRejected("Clipped output: " + error.Message); }
            var pieces = new List<SlicePiece>();
            AddPieces(positiveFaces, positiveComponents, true);
            AddPieces(negativeFaces, negativeComponents, false);
            double totalVolume = pieces.Sum(x => x.Volume);
            Require(Math.Abs(totalVolume - sourceProperties.Volume) <= Math.Max(sourceProperties.Volume * 2e-6, _epsilon * diagonal * diagonal * 8),
                "Slicing failed volume conservation; source remains unchanged.");
            return new(pieces.ToArray(), _plane, sourceProperties.Volume, _loops.Count, holes);

            SliceVertex Canonical(SliceVertex vertex, int id, CutPoint fallback) => vertex with
            { Position = _positions[id], Normal = vertex.Normal.Length > 1e-12 ? vertex.Normal.Normalized : fallback.Normalized };
            void AddPieces(List<SliceFace> faces, List<List<int>> components, bool positive)
            {
                Require(components.Count <= 64, "Slice creates too many separate boundary components.");
                var shells = components.Select(component => component.Select(i => faces[i]).ToArray()).ToArray();
                var propertiesByShell = shells.Select(Properties).ToArray();
                var solidShells = new List<(List<SliceFace> Faces, double OuterVolume)>();
                var cavities = new List<int>();
                for (int i = 0; i < shells.Length; i++)
                {
                    Require(Math.Abs(propertiesByShell[i].Volume) > sourceProperties.Volume * 1e-9, "Slice creates a microscopic boundary component.");
                    if (propertiesByShell[i].Volume > 0) solidShells.Add((shells[i].ToList(), propertiesByShell[i].Volume));
                    else cavities.Add(i);
                }
                // A sealed internal cavity has its own inward-wound boundary component, but
                // belongs to the surrounding solid rather than becoming negative-mass debris.
                foreach (int cavity in cavities)
                {
                    int parent = -1; double parentVolume = double.PositiveInfinity;
                    var point = shells[cavity][0].A.Position;
                    for (int i = 0; i < solidShells.Count; i++)
                        if (solidShells[i].OuterVolume < parentVolume && ContainsSolid(solidShells[i].Faces, point))
                        { parent = i; parentVolume = solidShells[i].OuterVolume; }
                    Require(parent >= 0, "Inward boundary has no enclosing solid; source winding is ambiguous.");
                    solidShells[parent].Faces.AddRange(shells[cavity]);
                }
                foreach (var shell in solidShells)
                {
                    var selected = shell.Faces.ToArray();
                    var properties = Properties(selected);
                    Require(properties.Volume > sourceProperties.Volume * 1e-9, "Slice creates a zero-volume or microscopic component.");
                    Require(properties.Inertia.XX > 0 && properties.Inertia.YY > 0 && properties.Inertia.ZZ > 0,
                        "Slice creates invalid component inertia.");
                    pieces.Add(new(selected, properties.Volume, properties.Centroid, properties.Inertia, positive) { ClosedSolids = new[] { selected } });
                }
            }
        }

        private void ChoosePlane()
        {
            _plane = _fixedPlane ? _requested : StablePlane(_positions, _requested, _epsilon);
            var normal = _plane.Normal;
            var reference = Math.Abs(normal.X) < .8 ? new CutPoint(1, 0, 0) : new CutPoint(0, 1, 0);
            _u = CutPoint.Cross(reference, normal).Normalized;
            _v = CutPoint.Cross(normal, _u).Normalized;
        }

        private void Clip(IndexedFace indexed)
        {
            var vertices = new[] { indexed.Face.A, indexed.Face.B, indexed.Face.C };
            var ids = new[] { indexed.A, indexed.B, indexed.C };
            var distances = vertices.Select(x => CutPoint.Dot(_plane.Normal, x.Position) - _plane.Offset).ToArray();
            bool anyPositive = distances.Any(x => x > 0), anyNegative = distances.Any(x => x < 0);
            if (!anyPositive) { _negative.Add(new(vertices.Select(x => new ClipVertex(x, -1)).ToList(), indexed.Face.Material)); return; }
            if (!anyNegative) { _positive.Add(new(vertices.Select(x => new ClipVertex(x, -1)).ToList(), indexed.Face.Material)); return; }
            var cutIds = new List<int>(2);
            var plus = ClipSide(true); var minus = ClipSide(false);
            _positive.Add(new(plus, indexed.Face.Material)); _negative.Add(new(minus, indexed.Face.Material));
            Require(cutIds.Distinct().Count() == 2, "Ambiguous triangle-plane intersection.");
            int a = cutIds[0], b = cutIds[1];
            var key = Edge.Of(a, b);
            Require(!_contourEdgeIds.ContainsKey(key), "Overlapping cut contours from duplicate surfaces.");
            _contourEdgeIds[key] = _contourEdges.Count; _contourEdges.Add(new(a, b));

            List<ClipVertex> ClipSide(bool positive)
            {
                var output = new List<ClipVertex>(4);
                for (int i = 0; i < 3; i++)
                {
                    int next = (i + 1) % 3;
                    bool inside = positive ? distances[i] > 0 : distances[i] < 0;
                    bool nextInside = positive ? distances[next] > 0 : distances[next] < 0;
                    if (inside) output.Add(new(vertices[i], -1));
                    if (inside == nextInside) continue;
                    double t = distances[i] / (distances[i] - distances[next]);
                    var vertex = SliceVertex.Lerp(vertices[i], vertices[next], t);
                    var edge = Edge.Of(ids[i], ids[next]);
                    if (!_intersectionByEdge.TryGetValue(edge, out int id))
                    {
                        id = _cutPoints.Count;
                        var point = vertex.Position - _plane.Normal * (CutPoint.Dot(_plane.Normal, vertex.Position) - _plane.Offset);
                        _intersectionByEdge.Add(edge, id); _cutPoints.Add(point);
                        _projected.Add(new(CutPoint.Dot(point, _u), CutPoint.Dot(point, _v)));
                    }
                    output.Add(new(vertex with { Position = _cutPoints[id] }, id));
                    if (positive) cutIds.Add(id);
                }
                return output;
            }
        }

        private void BuildContours()
        {
            var adjacent = Enumerable.Range(0, _cutPoints.Count).Select(_ => new List<int>(2)).ToArray();
            foreach (var edge in _contourEdges) { adjacent[edge.A].Add(edge.B); adjacent[edge.B].Add(edge.A); }
            Require(adjacent.All(x => x.Count == 2), "Cut contour is open, touching, or nonmanifold.");
            var visited = new HashSet<int>();
            for (int seed = 0; seed < adjacent.Length; seed++)
            {
                if (visited.Contains(seed)) continue;
                var loop = new List<int>(); int previous = -1, current = seed;
                do
                {
                    Require(visited.Add(current), "Cut contours intersect at a welded vertex.");
                    loop.Add(current);
                    var next = adjacent[current]; int chosen = next[0] == previous ? next[1] : next[0];
                    previous = current; current = chosen;
                } while (current != seed);
                Require(loop.Count >= 3, "Cut contour is degenerate."); _loops.Add(loop);
            }
        }

        private int CheckContours()
        {
            for (int i = 0; i < _contourEdges.Count; i++)
                for (int j = i + 1; j < _contourEdges.Count; j++)
                {
                    var a = _contourEdges[i]; var b = _contourEdges[j];
                    if (a.A == b.A || a.A == b.B || a.B == b.A || a.B == b.B) continue;
                    Require(!SegmentsTouch(_projected[a.A], _projected[a.B], _projected[b.A], _projected[b.B], _epsilon),
                        "Projected cut contours cross or touch; source may self-intersect.");
                }
            int holes = 0;
            foreach (var loop in _loops)
            {
                var point = _projected[loop[0]]; int nesting = 0;
                foreach (var other in _loops) if (!ReferenceEquals(other, loop) && Contains(other, point)) nesting++;
                if (nesting % 2 == 1) holes++;
            }
            return holes;
        }

        private bool Contains(List<int> loop, Point2 p)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                var a = _projected[loop[i]]; var b = _projected[loop[j]];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        private void BuildLevels()
        {
            foreach (double y in _projected.Select(p => p.Y).Order())
                if (_levels.Count == 0 || y - _levels[^1] > _epsilon) _levels.Add(y);
            Require(_levels.Count >= 2, "Cut contour has no planar area.");
            // Native float meshes have almost-equal symmetry coordinates. Canonicalize actual
            // contour heights as well as levels, avoiding tiny edges inside merged slabs.
            for (int i = 0; i < _projected.Count; i++)
            {
                double y = _projected[i].Y;
                int level = _levels.BinarySearch(y);
                if (level < 0) level = Math.Max(0, ~level - 1);
                double snapped = _levels[level];
                Require(Math.Abs(snapped - y) <= _epsilon * 1.000001, "Contour level canonicalization exceeded tolerance.");
                _cutPoints[i] += _v * (snapped - y);
                _projected[i] = _projected[i] with { Y = snapped };
            }
            CheckContours();
            for (int level = 0; level < _levels.Count; level++)
            {
                var points = new List<CutPoint>();
                for (int edgeIndex = 0; edgeIndex < _contourEdges.Count; edgeIndex++)
                {
                    var edge = _contourEdges[edgeIndex];
                    var a = _projected[edge.A]; var b = _projected[edge.B]; double y = _levels[level];
                    if (y < Math.Min(a.Y, b.Y) - _epsilon || y > Math.Max(a.Y, b.Y) + _epsilon) continue;
                    if (Math.Abs(a.Y - b.Y) <= _epsilon)
                    { points.Add(_cutPoints[edge.A]); points.Add(_cutPoints[edge.B]); continue; }
                    double t = Math.Clamp((y - a.Y) / (b.Y - a.Y), 0, 1);
                    var point = _cutPoints[edge.A] + (_cutPoints[edge.B] - _cutPoints[edge.A]) * t;
                    if (t < 1e-12) point = _cutPoints[edge.A];
                    else if (t > 1 - 1e-12) point = _cutPoints[edge.B];
                    points.Add(point); _levelPointsByEdge[(edgeIndex, level)] = point;
                    Require(_levelPointsByEdge.Count <= 100_000, "Shared cap subdivision exceeds the runtime budget.");
                }
                points.Sort((a, b) => CutPoint.Dot(a, _u).CompareTo(CutPoint.Dot(b, _u)));
                var unique = new List<CutPoint>();
                foreach (var point in points)
                    if (unique.Count == 0 || (point - unique[^1]).Length > _epsilon) unique.Add(point);
                _levelPoints.Add(unique);
            }
        }

        private List<SliceFace> EmitSides(List<Polygon> polygons)
        {
            var output = new List<SliceFace>();
            foreach (var polygon in polygons)
            {
                var refined = new List<SliceVertex>();
                for (int i = 0; i < polygon.Vertices.Count; i++)
                {
                    var a = polygon.Vertices[i]; var b = polygon.Vertices[(i + 1) % polygon.Vertices.Count];
                    if (a.CutId >= 0) a = a with { Vertex = a.Vertex with { Position = _cutPoints[a.CutId] } };
                    if (b.CutId >= 0) b = b with { Vertex = b.Vertex with { Position = _cutPoints[b.CutId] } };
                    refined.Add(a.Vertex);
                    if (a.CutId < 0 || b.CutId < 0) continue;
                    int edgeId = _contourEdgeIds[Edge.Of(a.CutId, b.CutId)];
                    var pa = _projected[a.CutId]; var pb = _projected[b.CutId];
                    if (Math.Abs(pa.Y - pb.Y) <= _epsilon) continue;
                    var inserted = new List<(double T, CutPoint Position)>();
                    for (int level = 0; level < _levels.Count; level++)
                    {
                        double t = (_levels[level] - pa.Y) / (pb.Y - pa.Y);
                        if (t > _epsilon / (b.Vertex.Position - a.Vertex.Position).Length
                            && t < 1 - _epsilon / (b.Vertex.Position - a.Vertex.Position).Length
                            && _levelPointsByEdge.TryGetValue((edgeId, level), out var point)) inserted.Add((t, point));
                    }
                    foreach (var item in inserted.OrderBy(x => x.T))
                        refined.Add(SliceVertex.Lerp(a.Vertex, b.Vertex, item.T) with { Position = item.Position });
                }
                Fan(refined, polygon.Material, output);
                Require(output.Count <= _geometry.OutputTriangles,
                    $"Side subdivision exceeds the output budget ({output.Count:N0} / {_geometry.OutputTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
            }
            return output;
        }

        private void AddCaps(List<SliceFace> positive, List<SliceFace> negative)
        {
            int cells = 0;
            for (int slab = 0; slab < _levels.Count - 1; slab++)
            {
                double midpoint = (_levels[slab] + _levels[slab + 1]) * .5;
                var active = new List<(int Edge, double X)>();
                for (int edgeId = 0; edgeId < _contourEdges.Count; edgeId++)
                {
                    var edge = _contourEdges[edgeId]; var a = _projected[edge.A]; var b = _projected[edge.B];
                    if (!(midpoint > Math.Min(a.Y, b.Y) && midpoint < Math.Max(a.Y, b.Y))) continue;
                    double t = (midpoint - a.Y) / (b.Y - a.Y);
                    active.Add((edgeId, a.X + (b.X - a.X) * t));
                }
                active.Sort((a, b) => a.X.CompareTo(b.X));
                Require(active.Count % 2 == 0, "Odd contour crossing count in cap slab.");
                for (int i = 0; i < active.Count; i += 2)
                {
                    Require(++cells <= MaximumCapCells, "Cap decomposition exceeds the runtime budget.");
                    Require(active[i + 1].X - active[i].X > _epsilon, "Cap slab collapses between touching contours.");
                    var leftLow = At(active[i].Edge, slab); var leftHigh = At(active[i].Edge, slab + 1);
                    var rightLow = At(active[i + 1].Edge, slab); var rightHigh = At(active[i + 1].Edge, slab + 1);
                    var points = new List<CutPoint>();
                    AddRow(slab, leftLow, rightLow, false);
                    AddRow(slab + 1, leftHigh, rightHigh, true);
                    RemoveDuplicates(points);
                    Require(points.Count >= 3, "Cap cell has no area.");
                    var minus = points.Select(p => new SliceVertex(p, _plane.Normal, CutPoint.Dot(p, _u), CutPoint.Dot(p, _v))).ToList();
                    Fan(minus, _capMaterial, negative);
                    var plus = minus.AsEnumerable().Reverse().Select(x => x with { Normal = _plane.Normal * -1 }).ToList();
                    Fan(plus, _capMaterial, positive);
                    Require(positive.Count + negative.Count <= _geometry.OutputTriangles,
                        $"Cap triangulation exceeds the output budget ({positive.Count + negative.Count:N0} / {_geometry.OutputTriangles:N0}). {SliceGeometryBudget.SettingsHint}");

                    void AddRow(int level, CutPoint left, CutPoint right, bool reverse)
                    {
                        double lo = CutPoint.Dot(left, _u), hi = CutPoint.Dot(right, _u);
                        var row = new List<CutPoint> { left };
                        foreach (var point in _levelPoints[level])
                        {
                            double x = CutPoint.Dot(point, _u);
                            if (x > lo + _epsilon && x < hi - _epsilon) row.Add(point);
                        }
                        row.Add(right);
                        if (reverse) row.Reverse(); points.AddRange(row);
                    }
                }
            }
            CutPoint At(int edge, int level)
            {
                Require(_levelPointsByEdge.TryGetValue((edge, level), out var point), "Missing shared cap boundary vertex.");
                return point;
            }
        }

        private void RemoveDuplicates(List<CutPoint> points)
        {
            for (int i = points.Count - 1; i > 0; i--) if ((points[i] - points[i - 1]).Length <= _epsilon) points.RemoveAt(i);
            if (points.Count > 1 && (points[0] - points[^1]).Length <= _epsilon) points.RemoveAt(points.Count - 1);
        }

        private void Fan(List<SliceVertex> polygon, int material, List<SliceFace> destination)
        {
            // Most source triangles do not intersect this cut. Reusing them avoids
            // tripling every untouched surface at each generation of wreckage slicing.
            if (polygon.Count == 3)
            {
                if (CutPoint.Cross(polygon[1].Position - polygon[0].Position, polygon[2].Position - polygon[0].Position).Length > _epsilon * _epsilon)
                    destination.Add(new(polygon[0], polygon[1], polygon[2], material));
                return;
            }
            var position = default(CutPoint); var normal = default(CutPoint); double u = 0, v = 0;
            foreach (var point in polygon) { position += point.Position; normal += point.Normal; u += point.U; v += point.V; }
            var center = new SliceVertex(position / polygon.Count, normal.Normalized, u / polygon.Count, v / polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                if (CutPoint.Cross(a.Position - center.Position, b.Position - center.Position).Length <= _epsilon * _epsilon) continue;
                destination.Add(new(center, a, b, material));
            }
        }
    }

    private static SlicePlane StablePlane(IReadOnlyList<CutPoint> positions, SlicePlane requested, double epsilon)
    {
        Require(requested.Normal.Finite && requested.Normal.Length > 1e-12 && double.IsFinite(requested.Offset), "Slice plane is invalid.");
        double length = requested.Normal.Length; var normal = requested.Normal / length; double offset = requested.Offset / length;
        // One globally chosen microscopic offset keeps shared compound cut faces coplanar.
        for (int attempt = 0; attempt < 33; attempt++)
        {
            double shift = attempt == 0 ? 0 : ((attempt + 1) / 2) * epsilon * 4 * (attempt % 2 == 1 ? 1 : -1);
            double candidate = offset + shift;
            if (positions.All(p => Math.Abs(CutPoint.Dot(normal, p) - candidate) > epsilon)) return new(normal, candidate);
        }
        throw new SliceRejected("Plane grazes too many coincident vertices; choose a nearby slice.");
    }

    private sealed class PositionWeld(double epsilon, List<CutPoint> positions)
    {
        private readonly Dictionary<(long, long, long), List<int>> _cells = new();
        public int Add(CutPoint p)
        {
            Require(p.Finite, "Mesh has nonfinite positions.");
            double x = Math.Floor(p.X / epsilon), y = Math.Floor(p.Y / epsilon), z = Math.Floor(p.Z / epsilon);
            Require(Math.Abs(x) < long.MaxValue - 2.0 && Math.Abs(y) < long.MaxValue - 2.0 && Math.Abs(z) < long.MaxValue - 2.0, "Mesh coordinates exceed welding precision.");
            var cell = ((long)x, (long)y, (long)z);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var ids))
                    foreach (int id in ids) if ((positions[id] - p).LengthSquared <= epsilon * epsilon) return id;
            int result = positions.Count; positions.Add(p);
            if (!_cells.TryGetValue(cell, out var bucket)) _cells.Add(cell, bucket = new());
            bucket.Add(result); return result;
        }
    }

    private static void Audit(IReadOnlyList<SliceFace> faces, double epsilon, out List<List<int>> components)
    {
        var positions = new List<CutPoint>(); var weld = new PositionWeld(epsilon, positions);
        foreach (var point in faces.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)) weld.Add(point);
        var edges = new Dictionary<Edge, (int Count, int Direction, int FirstFace)>();
        var adjacency = Enumerable.Range(0, faces.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i]; int a = weld.Add(f.A.Position), b = weld.Add(f.B.Position), c = weld.Add(f.C.Position);
            Require(a != b && a != c && b != c, $"Mesh contains welded degenerate triangles (face {i}, tolerance {epsilon:R} m, shortest edge {Math.Min((f.A.Position - f.B.Position).Length, Math.Min((f.B.Position - f.C.Position).Length, (f.C.Position - f.A.Position).Length)):R} m).");
            Add(a, b, i); Add(b, c, i); Add(c, a, i);
        }
        foreach (var edge in edges)
            if (edge.Value.Count != 2 || edge.Value.Direction != 0)
                throw new SliceRejected($"Mesh is not a closed consistently oriented 2-manifold (edge {positions[edge.Key.A]} to {positions[edge.Key.B]}, uses {edge.Value.Count}, winding {edge.Value.Direction}).");
        components = new(); var visited = new bool[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            if (visited[i]) continue;
            var component = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(i); visited[i] = true;
            while (queue.Count > 0)
            {
                int face = queue.Dequeue(); component.Add(face);
                foreach (int next in adjacency[face]) if (!visited[next]) { visited[next] = true; queue.Enqueue(next); }
            }
            components.Add(component);
        }
        void Add(int a, int b, int face)
        {
            var key = Edge.Of(a, b); int direction = a < b ? 1 : -1;
            if (!edges.TryGetValue(key, out var use)) edges.Add(key, (1, direction, face));
            else
            {
                edges[key] = (use.Count + 1, use.Direction + direction, use.FirstFace);
                adjacency[face].Add(use.FirstFace); adjacency[use.FirstFace].Add(face);
            }
        }
    }

    internal static (double Volume, CutPoint Centroid, SliceInertia Inertia) Properties(IReadOnlyList<SliceFace> faces)
    {
        var origin = faces[0].A.Position;
        double volume = 0, xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        var moment = default(CutPoint);
        foreach (var face in faces)
        {
            var a = face.A.Position - origin; var b = face.B.Position - origin; var c = face.C.Position - origin;
            double v = CutPoint.Dot(a, CutPoint.Cross(b, c)) / 6;
            volume += v; moment += (a + b + c) * (v / 4);
            xx += Square(a.X, b.X, c.X) * v / 10; yy += Square(a.Y, b.Y, c.Y) * v / 10; zz += Square(a.Z, b.Z, c.Z) * v / 10;
            xy += Product(a.X, b.X, c.X, a.Y, b.Y, c.Y) * v / 20;
            xz += Product(a.X, b.X, c.X, a.Z, b.Z, c.Z) * v / 20;
            yz += Product(a.Y, b.Y, c.Y, a.Z, b.Z, c.Z) * v / 20;
        }
        Require(double.IsFinite(volume) && Math.Abs(volume) > 1e-24, "Mesh volume is zero or nonfinite.");
        var center = moment / volume;
        var inertia = new SliceInertia(yy + zz - volume * (center.Y * center.Y + center.Z * center.Z),
            xx + zz - volume * (center.X * center.X + center.Z * center.Z),
            xx + yy - volume * (center.X * center.X + center.Y * center.Y),
            -xy + volume * center.X * center.Y, -xz + volume * center.X * center.Z, -yz + volume * center.Y * center.Z);
        return (volume, center + origin, inertia);
        static double Square(double a, double b, double c) => a * a + b * b + c * c + a * b + a * c + b * c;
        static double Product(double a, double b, double c, double d, double e, double f) => 2 * (a * d + b * e + c * f) + a * e + a * f + b * d + b * f + c * d + c * e;
    }

    private static bool ContainsSolid(IReadOnlyList<SliceFace> faces, CutPoint point)
    {
        double winding = 0;
        foreach (var face in faces)
        {
            var a = face.A.Position - point; var b = face.B.Position - point; var c = face.C.Position - point;
            double denominator = a.Length * b.Length * c.Length + CutPoint.Dot(a, b) * c.Length
                + CutPoint.Dot(b, c) * a.Length + CutPoint.Dot(c, a) * b.Length;
            winding += 2 * Math.Atan2(CutPoint.Dot(a, CutPoint.Cross(b, c)), denominator);
        }
        return Math.Abs(winding) > Math.PI * 2;
    }

    private static bool SegmentsTouch(Point2 a, Point2 b, Point2 c, Point2 d, double epsilon)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) - epsilon || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) - epsilon
            || Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) - epsilon || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y) - epsilon) return false;
        double abC = Point2.Cross(b - a, c - a), abD = Point2.Cross(b - a, d - a);
        double cdA = Point2.Cross(d - c, a - c), cdB = Point2.Cross(d - c, b - c);
        if ((abC > 0 && abD < 0 || abC < 0 && abD > 0)
            && (cdA > 0 && cdB < 0 || cdA < 0 && cdB > 0)) return true;
        // Use a distance in metres for near contact. A shared cross-product
        // tolerance scaled by both lengths lets a long edge inflate the allowed
        // distance from a tiny edge, falsely rejecting thin, nonintersecting details.
        return Near(a, b, c) || Near(a, b, d) || Near(c, d, a) || Near(c, d, b);
        bool Near(Point2 p, Point2 q, Point2 r)
        {
            var edge = q - p; var delta = r - p;
            double lengthSquared = edge.X * edge.X + edge.Y * edge.Y;
            double t = lengthSquared > 0 ? Math.Clamp((delta.X * edge.X + delta.Y * edge.Y) / lengthSquared, 0, 1) : 0;
            double x = delta.X - edge.X * t, y = delta.Y - edge.Y * t;
            return x * x + y * y <= epsilon * epsilon;
        }
    }
}
