namespace Purr2D2.Cutting;

/// <summary>
/// The same bounded closed-solid repair is used for copied generated inputs and
/// float32 export. It preserves corner attributes and never backs or fills a hole.
/// </summary>
internal static class ClosedMeshPreparation
{
    internal static bool TryNormalize(SliceFace[] source, out SliceFace[] accepted,
        out string reason, CancellationToken cancellation = default, bool float32Conversion = false)
    {
        accepted = []; reason = "";
        if (source.Length is < 4 or > 240_000 || source.Any(f => new[] { f.A, f.B, f.C }.Any(v =>
            !v.Position.Finite || !v.Normal.Finite || !double.IsFinite(v.U) || !double.IsFinite(v.V))))
        { reason = "Closed solid has invalid attributes or exceeds the normalization budget."; return false; }
        cancellation.ThrowIfCancellationRequested();
        // Preserve already-valid surfaces, including their small triangles and
        // every corner attribute. Numerical cleanup is only a recovery stage.
        if (PlaneSlicer.TryAudit(source, out _)) { accepted = source; return true; }
        // Never use repair to hide pre-existing duplicate shells/faces. Only faces
        // newly made coincident by a bounded positional collapse may cancel below.
        var exact = new HashSet<(CutPoint, CutPoint, CutPoint)>();
        for (int index = 0; !float32Conversion && index < source.Length; index++)
        {
            if ((index & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var f = source[index];
            var p = new[] { f.A.Position, f.B.Position, f.C.Position }.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
            if (p[0] != p[1] && p[1] != p[2] && !exact.Add((p[0], p[1], p[2])))
            { reason = "Closed solid contains duplicate input faces; repair cannot discard them."; return false; }
        }
        double sourceVolume = SignedVolume(source);
        if (!double.IsFinite(sourceVolume) || sourceVolume <= 1e-24)
        { reason = "Closed solid has nonpositive or nonfinite signed volume."; return false; }
        double epsilon = PlaneSlicer.TopologyTolerance(source);
        double maximumRepair = Math.Min(2e-6, epsilon * 10_000);
        int attempt = 0;
        foreach (double tolerance in new[] { 0, 1e-8, 3e-8, 1e-7, 3e-7, 1e-6, 2e-6 }.Where(t => t <= maximumRepair))
        {
            cancellation.ThrowIfCancellationRequested(); attempt++;
            // Sort before welding: material batching or triangle order cannot choose
            // a different first-seen representative for the same immutable positions.
            var positions = new List<CutPoint>();
            var bins = new Dictionary<(long, long, long), List<int>>();
            var canonical = new Dictionary<CutPoint, CutPoint>();
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
            foreach (var point in source.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)) canonical.Add(point, Weld(point));
            SliceVertex Convert(SliceVertex vertex) => vertex with { Position = canonical[vertex.Position] };
            var faces = new List<SliceFace>();
            foreach (var f in source)
            {
                var a = Convert(f.A); var b = Convert(f.B); var c = Convert(f.C);
                if (a.Position == b.Position || a.Position == c.Position || b.Position == c.Position) continue;
                if (CutPoint.Cross(b.Position - a.Position, c.Position - a.Position).LengthSquared <= 1e-20) continue;
                faces.Add(new(a, b, c, f.Material));
            }
            var vertexIds = new Dictionary<CutPoint, int>();
            int Id(CutPoint p)
            {
                if (!vertexIds.TryGetValue(p, out int id)) vertexIds[p] = id = vertexIds.Count;
                return id;
            }
            var unique = new Dictionary<(int, int, int), (SliceFace Face, int Winding)>();
            bool duplicate = false;
            foreach (var face in faces)
            {
                int a = Id(face.A.Position), b = Id(face.B.Position), c = Id(face.C.Position);
                var sorted = new[] { a, b, c }; Array.Sort(sorted);
                var key = (sorted[0], sorted[1], sorted[2]);
                int winding = (a < b ? 1 : -1) * (b < c ? 1 : -1) * (c < a ? 1 : -1);
                if (!unique.TryGetValue(key, out var old)) unique[key] = (face, winding);
                else if (old.Winding != winding) unique.Remove(key);
                else { duplicate = true; break; }
            }
            if (duplicate) { reason = "Repair creates duplicate same-winding faces."; continue; }
            var repaired = unique.Values.Select(x => x.Face).ToArray();
            if (Accept(repaired, out reason)) { accepted = repaired; return true; }
            var stitched = StitchCollapsedEdges(repaired, Math.Min(maximumRepair, Math.Max(3e-7, epsilon * 20)), float32Conversion, cancellation);
            if (stitched != null && Accept(stitched, out reason)) { accepted = stitched; return true; }
        }
        reason = $"Normalization failed after {attempt} bounded attempts (local topology tolerance {epsilon:R} m; max weld {maximumRepair:R} m): " + reason;
        return false;

        bool Accept(SliceFace[] candidate, out string failure)
        {
            if (!PlaneSlicer.TryAudit(candidate, out var volume, out failure)) return false;
            if (Math.Abs(volume - sourceVolume) > Math.Max(sourceVolume * .0002, 1e-10))
            { failure = "Closed-solid repair changes volume beyond 0.02%."; return false; }
            return true;
        }
    }

    private static double SignedVolume(SliceFace[] faces)
    {
        var origin = faces[0].A.Position;
        return faces.Sum(f => CutPoint.Dot(f.A.Position - origin, CutPoint.Cross(f.B.Position - origin, f.C.Position - origin)) / 6);
    }

    // Float32 can flatten a narrow cap triangle into an edge without merging its
    // three vertices. Removing that triangle leaves a T-junction: split the longer
    // neighboring edge at its existing boundary vertices. Never fill an open hole.
    private static SliceFace[]? StitchCollapsedEdges(SliceFace[] faces, double tolerance, bool float32Conversion, CancellationToken cancellation)
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
        var vertices = open.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
        var splits = new Dictionary<(CutPoint, CutPoint), List<(double T, CutPoint P)>>();
        foreach (var (a, b) in open)
        {
            cancellation.ThrowIfCancellationRequested();
            var delta = b - a; double length2 = delta.LengthSquared;
            if (length2 < 1e-18) continue;
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
            if ((result.Count & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var perimeter = new List<SliceVertex>(6);
            Edge(f.A, f.B); Edge(f.B, f.C); Edge(f.C, f.A);
            void Edge(SliceVertex a, SliceVertex b)
            {
                perimeter.Add(a);
                if (!splits.TryGetValue((a.Position, b.Position), out var points)) return;
                foreach (var (t, p) in points.OrderBy(v => v.T)) perimeter.Add(SliceVertex.Lerp(a, b, t) with { Position = p });
            }
            if (perimeter.Count == 3) { result.Add(f); continue; }
            var center = f.A.Position + ((f.B.Position - f.A.Position) + (f.C.Position - f.A.Position)) / 3;
            if (float32Conversion) center = new((float)center.X, (float)center.Y, (float)center.Z);
            var v = new SliceVertex(center,
                (f.A.Normal + f.B.Normal + f.C.Normal).Normalized, (f.A.U + f.B.U + f.C.U) / 3, (f.A.V + f.B.V + f.C.V) / 3);
            for (int i = 0; i < perimeter.Count; i++) result.Add(new(v, perimeter[i], perimeter[(i + 1) % perimeter.Count], f.Material));
        }
        return result.ToArray();
    }
}
