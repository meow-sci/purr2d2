using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing;

/// <summary>Prepare the exact float32 surfaces uploaded to KSA, before any native mutation.</summary>
internal static class SliceMeshPrecision
{
    internal static bool TryPrepare(SlicePiece piece, double4x4 transform, out SliceFace[][] solids,
        out double3 extents, out string reason)
    {
        solids = []; extents = default;
        var output = new List<SliceFace[]>();
        var minimum = new double3(double.MaxValue); var maximum = new double3(double.MinValue);
        foreach (var source in piece.ClosedSolids ?? [piece.Faces])
        {
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
}
