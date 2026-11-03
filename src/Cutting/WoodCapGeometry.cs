namespace Purr2D2.Cutting;

/// <summary>New wood cuts receive one centered end-grain field per connected section.</summary>
internal static class WoodCapGeometry
{
    internal static SlicePiece Map(SlicePiece piece, SlicePlane plane, CancellationToken cancellation = default)
    {
        var mapped = (piece.ClosedSolids ?? [piece.Faces]).Select(MapSolid).ToArray();
        return piece with { Faces = mapped.SelectMany(s => s).ToArray(), ClosedSolids = mapped };

        SliceFace[] MapSolid(SliceFace[] solid)
        {
            var result = solid.ToArray();
            var incident = new Dictionary<CutPoint, List<int>>();
            for (int i = 0; i < solid.Length; i++)
                if (solid[i].Material == -1)
                    foreach (var point in Points(solid[i]))
                    {
                        if (!incident.TryGetValue(point, out var list)) incident.Add(point, list = new());
                        list.Add(i);
                    }
            var visited = new HashSet<int>();
            foreach (int seed in incident.Values.SelectMany(v => v))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(seed)) continue;
                var component = new List<int>(); var pending = new Queue<int>(); pending.Enqueue(seed);
                while (pending.TryDequeue(out int face))
                {
                    component.Add(face);
                    foreach (var point in Points(solid[face]))
                        foreach (int other in incident[point]) if (visited.Add(other)) pending.Enqueue(other);
                }
                var outer = OuterContour(component.Select(i => solid[i]), plane);
                var field = Field(outer, plane);
                foreach (int i in component)
                {
                    var f = solid[i];
                    result[i] = new(Uv(f.A), Uv(f.B), Uv(f.C), f.Material);
                }
                SliceVertex Uv(SliceVertex vertex)
                {
                    var delta = vertex.Position - field.Center;
                    return vertex with { U = .5 + CutPoint.Dot(delta, field.U) * field.ScaleU,
                        V = .5 + CutPoint.Dot(delta, field.V) * field.ScaleV };
                }
            }
            return result;
        }
    }

    private static CutPoint[] OuterContour(IEnumerable<SliceFace> faces, SlicePlane plane)
    {
        var edges = new Dictionary<(CutPoint, CutPoint), int>();
        foreach (var face in faces)
        {
            Edge(face.A.Position, face.B.Position); Edge(face.B.Position, face.C.Position); Edge(face.C.Position, face.A.Position);
        }
        void Edge(CutPoint a, CutPoint b)
        { if (!edges.Remove((b, a))) edges[(a, b)] = 1; }
        var next = new Dictionary<CutPoint, CutPoint>();
        foreach (var edge in edges.Keys)
            if (!next.TryAdd(edge.Item1, edge.Item2)) throw new InvalidDataException("Wood cut contour has ambiguous boundaries.");
        AssistedSliceGeometry.Basis(plane.Normal, out var u, out var v);
        var loops = new List<CutPoint[]>();
        while (next.Count != 0)
        {
            var first = next.First().Key; var point = first; var loop = new List<CutPoint>();
            do
            {
                loop.Add(point);
                if (!next.Remove(point, out var target)) throw new InvalidDataException("Wood cut contour is open.");
                point = target;
            } while (point != first);
            if (loop.Count < 3) throw new InvalidDataException("Wood cut contour is degenerate.");
            // Both complementary caps use identical ordering for stable PCA/UVs.
            if (Area(loop) < 0) loop.Reverse();
            int start = Enumerable.Range(0, loop.Count).OrderBy(i => loop[i].X).ThenBy(i => loop[i].Y).ThenBy(i => loop[i].Z).First();
            loops.Add(Enumerable.Range(0, loop.Count).Select(i => loop[(i + start) % loop.Count]).ToArray());
        }
        if (loops.Count == 0) throw new InvalidDataException("Wood cut has no outer contour.");
        return loops.MaxBy(loop => Math.Abs(Area(loop)))!;
        double Area(IReadOnlyList<CutPoint> loop)
        {
            var origin = loop[0]; double sum = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i] - origin; var b = loop[(i + 1) % loop.Count] - origin;
                sum += CutPoint.Dot(a, u) * CutPoint.Dot(b, v) - CutPoint.Dot(a, v) * CutPoint.Dot(b, u);
            }
            return sum / 2;
        }
    }

    private readonly record struct UvField(CutPoint Center, CutPoint U, CutPoint V, double ScaleU, double ScaleV);
    private static UvField Field(CutPoint[] contour, SlicePlane plane)
    {
        AssistedSliceGeometry.Basis(plane.Normal, out var u, out var v);
        var origin = contour[0];
        var points = contour.Select(p => (X: CutPoint.Dot(p - origin, u), Y: CutPoint.Dot(p - origin, v))).ToArray();
        double area2 = 0, centerX = 0, centerY = 0, xx = 0, yy = 0, xy = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            double cross = a.X * b.Y - b.X * a.Y;
            area2 += cross; centerX += (a.X + b.X) * cross; centerY += (a.Y + b.Y) * cross;
            xx += (a.X * a.X + a.X * b.X + b.X * b.X) * cross;
            yy += (a.Y * a.Y + a.Y * b.Y + b.Y * b.Y) * cross;
            xy += (2 * a.X * a.Y + a.X * b.Y + b.X * a.Y + 2 * b.X * b.Y) * cross;
        }
        if (area2 <= 1e-20) throw new InvalidDataException("Wood cut contour has no area.");
        centerX /= 3 * area2; centerY /= 3 * area2;
        xx = xx / (6 * area2) - centerX * centerX;
        yy = yy / (6 * area2) - centerY * centerY;
        xy = xy / (12 * area2) - centerX * centerY;
        double angle = .5 * Math.Atan2(2 * xy, xx - yy);
        var major = u * Math.Cos(angle) + v * Math.Sin(angle);
        var minor = u * -Math.Sin(angle) + v * Math.Cos(angle);
        var center = origin + u * centerX + v * centerY;
        double radiusU = contour.Max(p => Math.Abs(CutPoint.Dot(p - center, major)));
        double radiusV = contour.Max(p => Math.Abs(CutPoint.Dot(p - center, minor)));
        if (radiusU <= 1e-10 || radiusV <= 1e-10) throw new InvalidDataException("Wood cut contour is too thin for end grain.");
        return new(center, major, minor, .47 / radiusU, .47 / radiusV);
    }

    private static IEnumerable<CutPoint> Points(SliceFace face) => new[] { face.A.Position, face.B.Position, face.C.Position };
}
