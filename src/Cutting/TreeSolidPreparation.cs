using Purr2D2.Core;

namespace Purr2D2.Cutting;

/// <summary>Native tree bark tubes describe solid wood; unrelated open detail remains backed art.</summary>
internal static class TreeSolidPreparation
{
    private readonly record struct Edge(int A, int B)
    {
        internal static Edge Of(int a, int b) => a < b ? new(a, b) : new(b, a);
    }
    private readonly record struct Link(int Face, int A, int B);

    internal static bool TryPrepare(IReadOnlyList<SliceFace[]> groups, out SliceFace[][] solids, out string reason,
        CancellationToken cancellation = default, SliceGeometryBudget? geometry = null)
    {
        geometry ??= SliceGeometryBudget.Current;
        solids = [];
        if (!geometry.AllowsSource(groups.Count, groups.Sum(g => (long)g.Length)))
        { reason = "Native tree source exceeds the geometry budget."; return false; }
        var components = new List<SliceFace[]>();
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            if (PlaneSlicer.TryAudit(group, out _, geometry)) { components.Add(group); continue; }
            SplitComponents(group, components, cancellation, geometry);
        }
        // Only verified sealed tubes take the solid path. Every rejected component
        // retains the existing authored-sheet policy, including sharp-crease handling.
        return SliceShellBuilder.TryPrepare(components, out solids, out reason, cancellation, geometry: geometry);
    }

    private static void SplitComponents(SliceFace[] source, List<SliceFace[]> output, CancellationToken cancellation, SliceGeometryBudget geometry)
    {
        const double weld = .00001;
        var positions = new List<CutPoint>();
        var bins = new Dictionary<(long, long, long), List<int>>();
        int Id(CutPoint point)
        {
            if (!point.Finite) throw new InvalidDataException("Native tree has nonfinite bark positions.");
            var key = ((long)Math.Floor(point.X / weld), (long)Math.Floor(point.Y / weld), (long)Math.Floor(point.Z / weld));
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (bins.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var nearby))
                    foreach (int index in nearby) if ((positions[index] - point).LengthSquared <= weld * weld) return index;
            int result = positions.Count; positions.Add(point);
            if (!bins.TryGetValue(key, out var bucket)) bins[key] = bucket = new();
            bucket.Add(result); return result;
        }
        var faces = new List<SliceFace>(); var vertices = new List<int[]>();
        var unique = new HashSet<(int, int, int)>();
        foreach (var face in source)
        {
            int a = Id(face.A.Position), b = Id(face.B.Position), c = Id(face.C.Position);
            if (a == b || b == c || a == c) continue;
            var sorted = new[] { a, b, c }; Array.Sort(sorted);
            if (!unique.Add((sorted[0], sorted[1], sorted[2]))) continue;
            if (CutPoint.Cross(positions[b] - positions[a], positions[c] - positions[a]).LengthSquared < 1e-18) continue;
            faces.Add(new(face.A with { Position = positions[a] }, face.B with { Position = positions[b] }, face.C with { Position = positions[c] }, face.Material));
            vertices.Add([a, b, c]);
        }
        var edges = new Dictionary<Edge, List<Link>>();
        for (int i = 0; i < faces.Count; i++)
            for (int j = 0; j < 3; j++)
            {
                int a = vertices[i][j], b = vertices[i][(j + 1) % 3];
                if (!edges.TryGetValue(Edge.Of(a, b), out var links)) edges.Add(Edge.Of(a, b), links = new());
                links.Add(new(i, a, b));
            }
        var visited = new bool[faces.Count]; var reversed = new bool[faces.Count];
        for (int seed = 0; seed < faces.Count; seed++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (visited[seed]) continue;
            var members = new List<int>(); var pending = new Queue<int>();
            bool manifold = true, orientable = true;
            visited[seed] = true; pending.Enqueue(seed);
            while (pending.TryDequeue(out int face))
            {
                members.Add(face);
                for (int j = 0; j < 3; j++)
                {
                    int a = vertices[face][j], b = vertices[face][(j + 1) % 3];
                    var links = edges[Edge.Of(a, b)];
                    if (links.Count > 2) manifold = false;
                    foreach (var other in links.Where(l => l.Face != face))
                    {
                        bool flip = reversed[face] ^ (a == other.A);
                        if (visited[other.Face]) { if (reversed[other.Face] != flip) orientable = false; }
                        else { visited[other.Face] = true; reversed[other.Face] = flip; pending.Enqueue(other.Face); }
                    }
                }
            }
            var patch = members.Select(i => reversed[i] ? Reverse(faces[i]) : faces[i]).ToArray();
            if (manifold && orientable && TrySealTube(patch, out var sealedTube, cancellation, geometry)) output.Add(sealedTube);
            else output.Add(patch);
        }
    }

    private static bool TrySealTube(SliceFace[] patch, out SliceFace[] result, CancellationToken cancellation, SliceGeometryBudget geometry)
    {
        result = [];
        var boundary = new Dictionary<(CutPoint, CutPoint), (SliceVertex A, SliceVertex B, int Material)>();
        foreach (var face in patch) { Edge(face.A, face.B, face.Material); Edge(face.B, face.C, face.Material); Edge(face.C, face.A, face.Material); }
        void Edge(SliceVertex a, SliceVertex b, int material)
        { if (!boundary.Remove((b.Position, a.Position))) boundary[(a.Position, b.Position)] = (a, b, material); }
        if (boundary.Count == 0)
        {
            var closed = SignedVolume(patch) < 0 ? patch.Select(Reverse).ToArray() : patch;
            if (!PlaneSlicer.TryAudit(closed, out _, geometry)) return false;
            result = closed; return true;
        }
        if (boundary.Count > 2048 || boundary.Count < 6) return false;
        var next = new Dictionary<CutPoint, (SliceVertex A, SliceVertex B, int Material)>();
        var incoming = new HashSet<CutPoint>();
        foreach (var edge in boundary.Values)
            if (!next.TryAdd(edge.A.Position, edge) || !incoming.Add(edge.B.Position)) return false;
        if (!incoming.SetEquals(next.Keys)) return false;
        var loops = new List<List<SliceVertex>>(); var materials = new List<int>();
        while (next.Count != 0)
        {
            var first = next.First(); var point = first.Key; var loop = new List<SliceVertex>();
            materials.Add(first.Value.Material);
            do
            {
                if (!next.Remove(point, out var edge)) return false;
                loop.Add(edge.A); point = edge.B.Position;
            } while (point != first.Key);
            if (loop.Count < 3) return false;
            loops.Add(loop);
        }
        // A tube has two separate ends. A single nonplanar sheet perimeter must
        // never be bridged into an invented volume; extra branch holes also refuse.
        if (loops.Count != 2) return false;
        CutPoint Center(List<SliceVertex> loop) => loop.Aggregate(default(CutPoint), (sum, v) => sum + v.Position) / loop.Count;
        double Diameter(List<SliceVertex> loop) => (loop.Select(v => v.Position).Aggregate(CutPoint.Max) - loop.Select(v => v.Position).Aggregate(CutPoint.Min)).Length;
        double diameter = Math.Max(Diameter(loops[0]), Diameter(loops[1]));
        if ((Center(loops[0]) - Center(loops[1])).Length < diameter * .25) return false;
        var accepted = new List<SliceFace>(patch);
        for (int i = 0; i < loops.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            // Reverse each boundary to share its exact original positions with
            // opposite edge winding. Projection is used only to choose diagonals.
            loops[i].Reverse();
            if (!TryCap(loops[i], materials[i], accepted)) return false;
        }
        if (accepted.Count > geometry.SourceTriangles) return false;
        var candidate = accepted.ToArray();
        if (SignedVolume(candidate) < 0) candidate = candidate.Select(Reverse).ToArray();
        if (!PlaneSlicer.TryAudit(candidate, out _, geometry)) return false;
        result = candidate; return true;
    }

    private static bool TryCap(List<SliceVertex> loop, int material, List<SliceFace> output)
    {
        var center = loop.Aggregate(default(CutPoint), (sum, vertex) => sum + vertex.Position) / loop.Count;
        CutPoint normal = default;
        for (int i = 0; i < loop.Count; i++) normal += CutPoint.Cross(loop[i].Position - center, loop[(i + 1) % loop.Count].Position - center);
        if (normal.Length < 1e-14) return false;
        normal = normal.Normalized;
        double diameter = (loop.Select(v => v.Position).Aggregate(CutPoint.Max) - loop.Select(v => v.Position).Aggregate(CutPoint.Min)).Length;
        double epsilon = Math.Max(1e-10, diameter * 1e-8);
        AssistedSliceGeometry.Basis(normal, out var u, out var v);
        var points = loop.Select(p => (X: CutPoint.Dot(p.Position - center, u), Y: CutPoint.Dot(p.Position - center, v))).ToArray();
        double Cross(int a, int b, int c) => (points[b].X - points[a].X) * (points[c].Y - points[a].Y)
            - (points[b].Y - points[a].Y) * (points[c].X - points[a].X);
        // A conservative star-shaped check prevents a source closure spanning a
        // concave gap or self-intersecting loop. Stock root and tip loops pass it.
        for (int i = 0; i < points.Length; i++)
        {
            int j = (i + 1) % points.Length;
            if (points[i].X * points[j].Y - points[i].Y * points[j].X <= epsilon * epsilon) return false;
            for (int k = i + 1; k < points.Length; k++)
            {
                int l = (k + 1) % points.Length;
                if (i == k || i == l || j == k || j == l) continue;
                // Opposite sides (including a collinear touch) would make a cap
                // overlap itself even though its abstract edge graph is closed.
                double a = Cross(i, j, k), b = Cross(i, j, l), c = Cross(k, l, i), d = Cross(k, l, j);
                if (Math.Max(points[i].X, points[j].X) + epsilon >= Math.Min(points[k].X, points[l].X)
                    && Math.Max(points[k].X, points[l].X) + epsilon >= Math.Min(points[i].X, points[j].X)
                    && Math.Max(points[i].Y, points[j].Y) + epsilon >= Math.Min(points[k].Y, points[l].Y)
                    && Math.Max(points[k].Y, points[l].Y) + epsilon >= Math.Min(points[i].Y, points[j].Y)
                    && Math.Sign(a) * Math.Sign(b) <= 0 && Math.Sign(c) * Math.Sign(d) <= 0) return false;
            }
        }
        var polygon = Enumerable.Range(0, loop.Count).ToList();
        while (polygon.Count > 3)
        {
            bool ear = false;
            for (int i = 0; i < polygon.Count; i++)
            {
                int a = polygon[(i + polygon.Count - 1) % polygon.Count], b = polygon[i], c = polygon[(i + 1) % polygon.Count];
                if (Cross(a, b, c) <= epsilon * epsilon) continue;
                if (polygon.Any(p => p != a && p != b && p != c && Cross(a, b, p) >= -epsilon * epsilon
                    && Cross(b, c, p) >= -epsilon * epsilon && Cross(c, a, p) >= -epsilon * epsilon)) continue;
                Emit(a, b, c); polygon.RemoveAt(i); ear = true; break;
            }
            if (!ear) return false;
        }
        if (Cross(polygon[0], polygon[1], polygon[2]) <= epsilon * epsilon) return false;
        Emit(polygon[0], polygon[1], polygon[2]); return true;
        void Emit(int a, int b, int c)
        {
            var faceNormal = CutPoint.Cross(loop[b].Position - loop[a].Position, loop[c].Position - loop[a].Position).Normalized;
            output.Add(new(loop[a] with { Normal = faceNormal }, loop[b] with { Normal = faceNormal }, loop[c] with { Normal = faceNormal }, material));
        }
    }

    private static double SignedVolume(IEnumerable<SliceFace> faces) => faces.Sum(f => CutPoint.Dot(f.A.Position, CutPoint.Cross(f.B.Position, f.C.Position))) / 6;
    private static SliceFace Reverse(SliceFace face) => new(face.C with { Normal = face.C.Normal * -1 },
        face.B with { Normal = face.B.Normal * -1 }, face.A with { Normal = face.A.Normal * -1 }, face.Material);
}
