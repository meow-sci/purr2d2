using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing;

/// <summary>Prepare the exact float32 surfaces uploaded to KSA, before any native mutation.</summary>
internal static class SliceMeshPrecision
{
    internal static bool TryPrepare(SlicePiece piece, double4x4 transform, out SliceFace[][] solids,
        out double3 extents, out string reason, CancellationToken cancellation = default)
    {
        solids = []; extents = default;
        var output = new List<SliceFace[]>();
        var minimum = new double3(double.MaxValue); var maximum = new double3(double.MinValue);
        foreach (var source in piece.ClosedSolids ?? [piece.Faces])
        {
            cancellation.ThrowIfCancellationRequested();
            SliceFace[]? accepted = null;
            reason = "";
            foreach (double tolerance in new[] { 0, 1e-8, 3e-8, 1e-7, 3e-7, 1e-6, 2e-6 })
            {
                var positions = new List<CutPoint>();
                var bins = new Dictionary<(long, long, long), List<int>>();
                // Clipping complex stock trim produces sub-micron slivers. Weld those
                // edges before float export, then require the result to remain closed.
                CutPoint Weld(CutPoint point)
                {
                    if (tolerance == 0) return point;
                    var key = ((long)Math.Floor(point.X / tolerance), (long)Math.Floor(point.Y / tolerance), (long)Math.Floor(point.Z / tolerance));
                    for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                        if (bins.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var candidates))
                            foreach (int i in candidates) if ((positions[i] - point).LengthSquared <= tolerance * tolerance) return positions[i];
                    int index = positions.Count; positions.Add(point);
                    if (!bins.TryGetValue(key, out var bucket)) bins[key] = bucket = new();
                    bucket.Add(index); return point;
                }
                SliceVertex Convert(SliceVertex v)
                {
                    var p = double3.Transform(new(v.Position.X, v.Position.Y, v.Position.Z), transform);
                    var n = double3.TransformNormal(new(v.Normal.X, v.Normal.Y, v.Normal.Z), transform);
                    var position = Weld(new((float)p.X, (float)p.Y, (float)p.Z));
                    return new(position, new CutPoint(n.X, n.Y, n.Z).Normalized, v.U, v.V);
                }
                var faces = new List<SliceFace>();
                foreach (var f in source)
                {
                    var a = Convert(f.A); var b = Convert(f.B); var c = Convert(f.C);
                    if (a.Position == b.Position || a.Position == c.Position || b.Position == c.Position) continue;
                    if (CutPoint.Cross(b.Position - a.Position, c.Position - a.Position).LengthSquared <= 1e-20) continue;
                    faces.Add(new(a, b, c, f.Material));
                }
                // Collapsing a sliver can bring a pair of opposite internal faces
                // onto one another. Cancel that pair rather than leaving a four-use
                // edge in an otherwise closed shell.
                var vertexIds = new Dictionary<CutPoint, int>();
                int Id(CutPoint p)
                {
                    if (!vertexIds.TryGetValue(p, out int id)) vertexIds[p] = id = vertexIds.Count;
                    return id;
                }
                var unique = new Dictionary<(int, int, int), (SliceFace Face, int Winding)>();
                foreach (var face in faces)
                {
                    int a = Id(face.A.Position), b = Id(face.B.Position), c = Id(face.C.Position);
                    var sorted = new[] { a, b, c }; Array.Sort(sorted);
                    var key = (sorted[0], sorted[1], sorted[2]);
                    int winding = (a < b ? 1 : -1) * (b < c ? 1 : -1) * (c < a ? 1 : -1);
                    if (!unique.TryGetValue(key, out var old)) unique[key] = (face, winding);
                    else if (old.Winding != winding) unique.Remove(key);
                }
                var repaired = unique.Values.Select(x => x.Face).ToArray();
                if (PlaneSlicer.TryAudit(repaired, out reason)) { accepted = repaired; break; }
                var stitched = StitchCollapsedEdges(repaired);
                if (stitched != null && PlaneSlicer.TryAudit(stitched, out reason)) { accepted = stitched; break; }
            }
            if (accepted == null)
            { reason = $"Float32 solid {output.Count} ({source.Length} faces; cut={source.Any(f => f.Material == -1)}) cannot retain a closed surface: " + reason; return false; }
            output.Add(accepted);
            foreach (var f in accepted)
                foreach (var v in new[] { f.A, f.B, f.C })
                {
                    var p = new double3(v.Position.X, v.Position.Y, v.Position.Z);
                    minimum = double3.Min(minimum, p); maximum = double3.Max(maximum, p);
                }
        }
        if (!PlaneSlicer.TryAuditCompound(output, out double volume, out reason)) return false;
        if (Math.Abs(volume - piece.Volume) > Math.Max(piece.Volume * .0002, 1e-10))
        { reason = "Float32 fragment volume changed beyond tolerance."; return false; }
        solids = output.ToArray(); extents = maximum - minimum; reason = ""; return true;
    }

    // Float32 can flatten a narrow cap triangle into an edge without merging its
    // three vertices. Removing that triangle leaves a T-junction: split the longer
    // neighboring edge at its existing boundary vertices. Never fill an open hole.
    private static SliceFace[]? StitchCollapsedEdges(SliceFace[] faces)
    {
        var edges = new Dictionary<(CutPoint, CutPoint), int>();
        foreach (var f in faces) { Add(f.A.Position, f.B.Position); Add(f.B.Position, f.C.Position); Add(f.C.Position, f.A.Position); }
        void Add(CutPoint a, CutPoint b)
        {
            if (edges.TryGetValue((b, a), out int n)) edges[(b, a)] = n + 1;
            else edges[(a, b)] = edges.GetValueOrDefault((a, b)) + 1;
        }
        var open = edges.Where(e => e.Value == 1).Select(e => e.Key).ToArray();
        if (open.Length == 0 || open.Length > 2048) return null;
        var vertices = open.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct().ToArray();
        var splits = new Dictionary<(CutPoint, CutPoint), List<(double T, CutPoint P)>>();
        foreach (var (a, b) in open)
        {
            var delta = b - a; double length2 = delta.LengthSquared;
            if (length2 < 1e-18) continue;
            double tolerance = Math.Max(3e-7, Math.Max(a.Length, b.Length) * 2e-7);
            foreach (var p in vertices)
            {
                if (p == a || p == b) continue;
                double t = CutPoint.Dot(p - a, delta) / length2;
                if (t <= 1e-7 || t >= 1 - 1e-7 || (p - (a + delta * t)).LengthSquared > tolerance * tolerance) continue;
                if (!splits.TryGetValue((a, b), out var list)) splits[(a, b)] = list = new();
                list.Add((t, p));
            }
        }
        if (splits.Count == 0) return null;
        var result = new List<SliceFace>(faces.Length + splits.Count * 2);
        foreach (var f in faces)
        {
            var perimeter = new List<SliceVertex>(6);
            Edge(f.A, f.B); Edge(f.B, f.C); Edge(f.C, f.A);
            void Edge(SliceVertex a, SliceVertex b)
            {
                perimeter.Add(a);
                if (!splits.TryGetValue((a.Position, b.Position), out var points)) return;
                foreach (var (t, p) in points.OrderBy(v => v.T)) perimeter.Add(SliceVertex.Lerp(a, b, t) with { Position = p });
            }
            if (perimeter.Count == 3) { result.Add(f); continue; }
            var center = (f.A.Position + f.B.Position + f.C.Position) / 3;
            var v = new SliceVertex(new((float)center.X, (float)center.Y, (float)center.Z),
                (f.A.Normal + f.B.Normal + f.C.Normal).Normalized, (f.A.U + f.B.U + f.C.U) / 3, (f.A.V + f.B.V + f.C.V) / 3);
            for (int i = 0; i < perimeter.Count; i++) result.Add(new(v, perimeter[i], perimeter[(i + 1) % perimeter.Count], f.Material));
        }
        return result.ToArray();
    }
}
