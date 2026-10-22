namespace Purr2D2.Cutting;

/// <summary>
/// Stock hull art is often a collection of open sheets, not watertight solids.
/// Keep closed solids unchanged; give open authored panels a thin backing and
/// stitch their boundaries, preserving the exterior's positions, UVs and materials.
/// The regular clipper still audits every prepared shell and every resulting cap.
/// </summary>
internal static class SliceShellBuilder
{
    internal const double WallThickness = .004;
    private readonly record struct Edge(int A, int B)
    {
        internal static Edge Of(int a, int b) => a < b ? new(a, b) : new(b, a);
    }

    internal static bool TryPrepare(IReadOnlyList<SliceFace[]> groups, out SliceFace[][] solids, out string reason)
    {
        var result = new List<SliceFace[]>();
        solids = [];
        foreach (var group in groups)
        {
            if (PlaneSlicer.TryAudit(group, out _)) { result.Add(group); continue; }
            if (!PrepareGroup(group, result, out reason)) { reason = $"Hull group {groups.ToList().IndexOf(group)} ({group.Length} triangles): " + reason; return false; }
            if (result.Count > 512 || result.Sum(g => g.Length) > 120_000)
            { reason = "Prepared hull exceeds the shell geometry budget."; return false; }
        }
        solids = result.ToArray(); reason = "";
        return solids.Length > 0;
    }

    private static bool PrepareGroup(SliceFace[] source, List<SliceFace[]> output, out string reason)
    {
        reason = "";
        var positions = new List<CutPoint>();
        var bins = new Dictionary<(long, long, long), List<int>>();
        // Native meshes are float32. Rejoin duplicated seam vertices within ten microns.
        const double weld = .00001;
        int Index(CutPoint p)
        {
            var key = ((long)Math.Floor(p.X / weld), (long)Math.Floor(p.Y / weld), (long)Math.Floor(p.Z / weld));
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (bins.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var candidates))
                    foreach (int i in candidates) if ((positions[i] - p).LengthSquared <= weld * weld) return i;
            int index = positions.Count; positions.Add(p);
            if (!bins.TryGetValue(key, out var bucket)) bins[key] = bucket = new();
            bucket.Add(index); return index;
        }
        var faces = new List<SliceFace>();
        var vertices = new List<int[]>();
        var duplicates = new HashSet<(int, int, int)>();
        foreach (var face in source)
        {
            if (!face.A.Position.Finite || !face.B.Position.Finite || !face.C.Position.Finite)
            { reason = "Hull contains nonfinite vertices."; return false; }
            int a = Index(face.A.Position), b = Index(face.B.Position), c = Index(face.C.Position);
            if (a == b || a == c || b == c) continue;
            var sorted = new[] { a, b, c }; Array.Sort(sorted);
            if (!duplicates.Add((sorted[0], sorted[1], sorted[2]))) continue;
            if (CutPoint.Cross(positions[b] - positions[a], positions[c] - positions[a]).LengthSquared < 1e-18) continue;
            faces.Add(new(face.A with { Position = positions[a] }, face.B with { Position = positions[b] },
                face.C with { Position = positions[c] }, face.Material));
            vertices.Add([a, b, c]);
        }
        if (faces.Count == 0) return true; // Degenerate decorative faces contribute no surface.
        var edges = new Dictionary<Edge, List<(int Face, bool Forward)>>();
        for (int f = 0; f < faces.Count; f++)
            for (int j = 0; j < 3; j++)
            {
                int a = vertices[f][j], b = vertices[f][(j + 1) % 3];
                var edge = Edge.Of(a, b);
                if (!edges.TryGetValue(edge, out var list)) edges.Add(edge, list = new());
                list.Add((f, a < b));
            }
        var visited = new bool[faces.Count]; var reversed = new bool[faces.Count];
        for (int seed = 0; seed < faces.Count; seed++)
        {
            if (visited[seed]) continue;
            var component = new List<int>(); var pending = new Queue<int>();
            visited[seed] = true; pending.Enqueue(seed);
            while (pending.TryDequeue(out int current))
            {
                component.Add(current);
                for (int j = 0; j < 3; j++)
                {
                    int a = vertices[current][j], b = vertices[current][(j + 1) % 3];
                    var links = edges[Edge.Of(a, b)];
                    if (links.Count != 2) continue;
                    var other = links.First(x => x.Face != current);
                    // Treat a folded sheet's sharp corners as separate backed
                    // panels. Averaging opposed normals can collapse the backing
                    // at a crease and produce a non-manifold vertical edge.
                    CutPoint Normal(int index) => CutPoint.Cross(faces[index].B.Position - faces[index].A.Position,
                        faces[index].C.Position - faces[index].A.Position).Normalized;
                    if (Math.Abs(CutPoint.Dot(Normal(current), Normal(other.Face))) < .3
                        || CutPoint.Dot(Normal(current), Normal(other.Face)) < -.8) continue;
                    bool flip = reversed[current] ^ ((a < b) == other.Forward);
                    if (visited[other.Face])
                    {
                        if (reversed[other.Face] != flip)
                        { reason = "Hull sheet has inconsistent winding that cannot be repaired."; return false; }
                    }
                    else { visited[other.Face] = true; reversed[other.Face] = flip; pending.Enqueue(other.Face); }
                }
            }
            var patch = component.Select(i => reversed[i] ? Reverse(faces[i]) : faces[i]).ToArray();
            // A closed authored island can be inside an otherwise open mesh.
            if (patch.Sum(f => CutPoint.Dot(f.A.Position, CutPoint.Cross(f.B.Position, f.C.Position))) < 0)
                patch = patch.Select(Reverse).ToArray();
            if (PlaneSlicer.TryAudit(patch, out _)) { output.Add(patch); continue; }
            // Do not use a closed volume's signed-volume heuristic for open panels.
            patch = component.Select(i => reversed[i] ? Reverse(faces[i]) : faces[i]).ToArray();
            patch = SeparatePinchedVertices(patch);
            var normals = new Dictionary<CutPoint, CutPoint>();
            var boundary = new Dictionary<(CutPoint, CutPoint), (SliceVertex A, SliceVertex B, int Material)>();
            foreach (var face in patch)
            {
                var normal = CutPoint.Cross(face.B.Position - face.A.Position, face.C.Position - face.A.Position).Normalized;
                foreach (var v in new[] { face.A, face.B, face.C })
                    normals[v.Position] = normals.GetValueOrDefault(v.Position) + normal;
                AddEdge(face.A, face.B, face.Material); AddEdge(face.B, face.C, face.Material); AddEdge(face.C, face.A, face.Material);
            }
            void AddEdge(SliceVertex a, SliceVertex b, int material)
            {
                if (!boundary.Remove((b.Position, a.Position))) boundary[(a.Position, b.Position)] = (a, b, material);
            }
            SliceVertex Inner(SliceVertex v) => v with
            {
                Position = v.Position - normals[v.Position].Normalized * WallThickness,
                Normal = v.Normal * -1
            };
            var shell = new List<SliceFace>(patch.Length * 2 + boundary.Count * 2);
            foreach (var face in patch)
            {
                shell.Add(face);
                shell.Add(new(Inner(face.C), Inner(face.B), Inner(face.A), face.Material));
            }
            foreach (var (a, b, material) in boundary.Values)
            {
                var ia = Inner(a); var ib = Inner(b);
                var normal = CutPoint.Cross(ia.Position - a.Position, b.Position - a.Position).Normalized;
                shell.Add(new(a with { Normal = normal }, ia with { Normal = normal }, b with { Normal = normal }, material));
                shell.Add(new(b with { Normal = normal }, ia with { Normal = normal }, ib with { Normal = normal }, material));
            }
            var prepared = shell.ToArray();
            if (!PlaneSlicer.TryAudit(prepared, out string failure))
            { reason = $"Could not close an authored hull panel ({patch.Length} triangles, {boundary.Count} border edges): " + failure; return false; }
            output.Add(prepared);
        }
        return true;
    }

    private static SliceFace Reverse(SliceFace face) => new(
        face.C with { Normal = face.C.Normal * -1 }, face.B with { Normal = face.B.Normal * -1 },
        face.A with { Normal = face.A.Normal * -1 }, face.Material);

    private static SliceFace[] SeparatePinchedVertices(SliceFace[] faces)
    {
        // Some stock details touch only at one vertex. Extruding that shared
        // point creates an edge used by four walls. Separate disconnected fans
        // by 30 microns, below visible detail but above float32 mesh precision.
        var incident = new Dictionary<CutPoint, List<int>>();
        for (int i = 0; i < faces.Length; i++)
            foreach (var v in new[] { faces[i].A, faces[i].B, faces[i].C })
            {
                if (!incident.TryGetValue(v.Position, out var list)) incident[v.Position] = list = new();
                list.Add(i);
            }
        var result = faces.ToArray();
        foreach (var (point, list) in incident)
        {
            var neighbors = new Dictionary<CutPoint, List<int>>();
            foreach (int index in list)
                foreach (var v in new[] { faces[index].A, faces[index].B, faces[index].C })
                    if (v.Position != point)
                    {
                        if (!neighbors.TryGetValue(v.Position, out var sharing)) neighbors[v.Position] = sharing = new();
                        sharing.Add(index);
                    }
            var visited = new HashSet<int>(); int fanNumber = 0;
            foreach (int seed in list)
            {
                if (!visited.Add(seed)) continue;
                var fan = new List<int>(); var pending = new Queue<int>(); pending.Enqueue(seed);
                while (pending.TryDequeue(out int index))
                {
                    fan.Add(index);
                    foreach (var v in new[] { faces[index].A, faces[index].B, faces[index].C })
                        if (v.Position != point && neighbors[v.Position].Count == 2)
                            foreach (int other in neighbors[v.Position]) if (visited.Add(other)) pending.Enqueue(other);
                }
                if (fanNumber++ == 0) continue;
                CutPoint inward = default;
                foreach (int index in fan)
                    inward += (faces[index].A.Position + faces[index].B.Position + faces[index].C.Position) / 3 - point;
                var separated = point + inward.Normalized * (.00003 * fanNumber);
                SliceVertex Move(SliceVertex v) => v.Position == point ? v with { Position = separated } : v;
                foreach (int index in fan)
                {
                    var f = result[index];
                    result[index] = new(Move(f.A), Move(f.B), Move(f.C), f.Material);
                }
            }
        }
        return result;
    }
}
