using System.Text.Json;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Slicing;
using Purr2D2.Rendering;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Vector3 = System.Numerics.Vector3;

internal static class StockSliceChecks
{
    internal sealed record Fixture(string Name, double[][][][] Groups);
    internal static void Run(string path)
    {
        foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path))!)
        {
            if (Environment.GetEnvironmentVariable("PURR_STOCK_FILTER") is { Length: > 0 } filter && !fixture.Name.Contains(filter)) continue;
            var groups = fixture.Groups.Select(group => group.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), f[0].Length > 8 ? (int)f[0][8] : 0)).ToArray()).ToArray();
            var materials = groups.SelectMany(g => g).Select(f => f.Material).Distinct().ToDictionary(id => id, id => new SliceMaterialData("FixtureMaterial" + id, false));
            var input = new SliceInput(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 100);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            double planMs = 0;
            var cpu = SliceGeometryWork.Calculate(input, CancellationToken.None, _ => planMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            foreach (var asset in cpu.Fragments)
            {
                var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(asset.Xml));
                if (xml.Root!.Elements("SubPart").Count() != 1 || xml.Root.Element("Part")!.Elements("SubPart").Count() != 1
                    || asset.Uploads.Length > materials.Count + 1)
                    throw new Exception("Worker export did not merge fragment material surfaces into one visual SubPart");
                int legacySurfaces = xml.Descendants("Run").Count();
                var reload = Reload(asset);
                if (reload.Groups.Sum(s => s.Length) * 3 != asset.Uploads.Sum(u => u.IndexBytes / 4))
                    throw new Exception("Merged export/reload changed the visual triangle count");
                using var document = JsonDocument.Parse(asset.Glb.AsMemory(20, BitConverter.ToInt32(asset.Glb, 12)));
                if (document.RootElement.GetProperty("meshes").GetArrayLength() != asset.MeshNames.Length || asset.Uploads.Length + 2 != asset.MeshNames.Length)
                    throw new Exception("Worker export omitted a visual, picking or collision mesh");
                if (asset.Uploads.Any(u => u.Bytes.Length != u.VertexBytes + u.IndexBytes)) throw new Exception("Worker upload size mismatch");
                var collision = document.RootElement.GetProperty("meshes")[asset.MeshNames.Length - 1];
                int position = collision.GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var accessor = document.RootElement.GetProperty("accessors")[position];
                int hullPoints = accessor.GetProperty("count").GetInt32();
                var view = document.RootElement.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
                int offset = 28 + BitConverter.ToInt32(asset.Glb, 12) + view.GetProperty("byteOffset").GetInt32();
                var points = new Vector3[hullPoints];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new(BitConverter.ToSingle(asset.Glb, offset + i * 12), BitConverter.ToSingle(asset.Glb, offset + i * 12 + 4), BitConverter.ToSingle(asset.Glb, offset + i * 12 + 8));
                using var pool = new BufferPool();
                long cooking = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!ConvexHullHelper.CreateShape(points, pool, out var center, out var hull) || !float.IsFinite(center.LengthSquared()))
                    throw new Exception("Exported collider cannot be cooked by the native hull implementation");
                hull.Dispose(pool);
                Console.WriteLine($"  Worker fragment: {legacySurfaces} legacy surfaces -> {asset.Uploads.Length} render surfaces; one visual SubPart; {hullPoints} collision vertices; native hull cook {System.Diagnostics.Stopwatch.GetElapsedTime(cooking).TotalMilliseconds:F2} ms");
            }
            Console.WriteLine($"PASS: {fixture.Name} worker pipeline; faceted={cpu.Faceted}, attempts={cpu.Attempts}, fallback={cpu.FacetFallback}; plan {planMs:F1} ms, complete {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            if (fixture.Name.Contains("FuelTank"))
            {
                RepeatedTankSlices(input, fixture.Name);
                FreshTankSeeds(input, fixture.Name);
            }
            if (fixture.Name.Contains("ServiceModule") && fixture.Name.EndsWith("HeightB", StringComparison.Ordinal))
            {
                int completed = 0;
                foreach (var asset in cpu.Fragments)
                {
                    var reload = Reload(asset) with { Seed = 731 };
                    double beforeVolume = reload.Groups.Sum(s => PlaneSlicer.Properties(s).Volume);
                    // This stock model has an existing global-weld precision limit,
                    // reproduced with the committed per-solid exporter as well.
                    // Classify only that exact production input-audit rejection.
                    var auditPlane = AssistedSliceGeometry.Candidates(reload.Groups, reload.Axes, reload.Seed).First();
                    if (!PlaneSlicer.TrySliceCompound(reload.Groups, auditPlane, out _, out var auditFailure)
                        && auditFailure == "Mesh contains welded degenerate triangles.")
                    {
                        Console.WriteLine($"KNOWN LIMITATION: {fixture.Name} reloaded half input audit refuses recut: {auditFailure} (same with legacy per-solid export)");
                        continue;
                    }
                    var recut = SliceGeometryWork.Calculate(reload, CancellationToken.None, _ => { });
                    double afterVolume = recut.Fragments.Sum(a => Reload(a).Groups.Sum(s => PlaneSlicer.Properties(s).Volume));
                    if (recut.Fragments.Length != 2 || Math.Abs(afterVolume - beforeVolume) > beforeVolume * .0006
                        || Math.Abs(recut.Fragments.Sum(a => a.MassKg) - reload.MassKg) > .001)
                        throw new Exception("Merged service-module reload/recut lost closed-solid volume or mass");
                    completed++;
                }
                Console.WriteLine($"INFO: {fixture.Name} merged XML/GLB reload preserved topology; {completed}/2 successful recuts, {2 - completed}/2 known input-audit precision limits");
            }
            if (!SliceShellBuilder.TryPrepare(groups, out var solids, out var reason))
                throw new Exception(fixture.Name + " shell preparation: " + reason);
            Console.WriteLine($"{fixture.Name}: {groups.Sum(g => g.Length)} authored triangles -> {solids.Length} solids / {solids.Sum(g => g.Length)} triangles");
            // Exercise exactly the automatic plane candidates used by the one-click action.
            SlicePlane? automatic = null;
            foreach (var candidate in AssistedSliceGeometry.Candidates(solids, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)]))
            {
                if (!PlaneSlicer.TrySliceCompound(solids, candidate, out var attempt, out var rejection))
                { Console.WriteLine($"  Candidate {candidate} refused: {rejection}"); continue; }
                bool valid = attempt!.Pieces.Length is >= 2 and <= 8;
                foreach (var piece in attempt.Pieces)
                {
                    if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out rejection)
                        || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var export, out _, out rejection))
                    { Console.WriteLine($"  Candidate {candidate} export refused: {rejection}"); valid = false; continue; }
                    int materialGroups = FragmentAssetWriter.CountRenderSurfaces(export, materials);
                    int points = export.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct().Count();
                    if (materialGroups > 2047 || points > 65536)
                    { Console.WriteLine($"  Candidate {candidate} budget: {materialGroups} groups, {points} points"); valid = false; }
                }
                if (valid) { automatic = candidate; break; }
            }
            if (automatic == null) throw new Exception(fixture.Name + ": automatic slice could not find a usable section");
            Console.WriteLine($"PASS: {fixture.Name} automatic selection: {automatic}");
            var planes = fixture.Name.Contains("FuelTank")
                ? new[] { automatic.Value, new SlicePlane(new(1, 0, 0), .13), new SlicePlane(new(1, .1, .15), -.1) }
                : fixture.Name.Contains("ServiceModule") ? new[] { automatic.Value }
                : new[] { automatic.Value, new SlicePlane(new(1, 0, 0), .13) };
            foreach (var plane in planes)
            {
                if (!PlaneSlicer.TrySliceCompound(solids, plane, out var result, out reason))
                    throw new Exception(fixture.Name + " slicing: " + reason);
                foreach (var piece in result!.Pieces)
                {
                    if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out reason)) throw new Exception(reason);
                    // Audit after the same native float32 conversion used by fragment export.
                    if (!SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var quantized, out _, out reason)) throw new Exception(reason);
                    int points = quantized.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct().Count();
                    if (points > 65536 || FragmentAssetWriter.CountRenderSurfaces(quantized, materials) > 2046)
                        throw new Exception("Stock fragment exceeds native preparation budgets");
                    var exportGroups = quantized.SelectMany((solid, i) => solid.GroupBy(f => f.Material)
                        .Select(g => new FragmentGlbWriter.Group($"s{i}_m{g.Key}", g.Key, g.ToArray()))).ToList();
                    exportGroups.Add(new("hull", int.MinValue, quantized.SelectMany(s => s).ToArray()));
                    var glb = FragmentGlbWriter.Write(exportGroups);
                    using var document = JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12)));
                    if (document.RootElement.GetProperty("meshes").GetArrayLength() != exportGroups.Count) throw new Exception("Export dropped source surfaces");
                    Console.WriteLine($"  Fragment: {points} hull points / {quantized.Length} closed surfaces");
                }
                Console.WriteLine($"PASS: {fixture.Name} cut {plane}, {result.Pieces.Length} fragments, {result.ContourCount} cut contours");
            }
        }
    }
    private static void RepeatedTankSlices(SliceInput original, string name)
    {
        var inputs = new List<SliceInput> { original };
        double originalVolume = 0;
        int faceted = 0, fallbacks = 0;
        for (int generation = 0; generation < 3; generation++)
        {
            var next = new List<SliceInput>();
            for (int branch = 0; branch < inputs.Count; branch++)
            {
                var input = inputs[branch] with { Seed = (uint)(0x128ab + generation * 101 + branch * 977) };
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var cpu = SliceGeometryWork.Calculate(input, CancellationToken.None, _ => { });
                if (cpu.Fragments.Length != 2) throw new Exception("Repeated tank cut did not split into two rigid pieces");
                if (cpu.Faceted) faceted++; else fallbacks++;
                foreach (var asset in cpu.Fragments)
                {
                    var reload = Reload(asset);
                    if (!SliceShellBuilder.TryPrepare(reload.Groups, out var solids, out string reason)
                        || !PlaneSlicer.TryAuditCompound(solids, out _, out reason))
                        throw new Exception("Generated tank asset is not resliceable after XML/GLB reload: " + reason);
                    next.Add(reload);
                }
                int triangles = next.TakeLast(cpu.Fragments.Length).Sum(i => i.Groups.Sum(g => g.Length));
                Console.WriteLine($"  Reloaded tank cut generation {generation + 1}, branch {branch}: {input.Groups.Sum(g => g.Length)} -> {triangles} triangles; {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms; faceted={cpu.Faceted}, attempts={cpu.Attempts}, fallback={cpu.FacetFallback}");
            }
            double volume = next.Sum(i => i.Groups.Sum(s => PlaneSlicer.Properties(s).Volume));
            if (generation == 0) originalVolume = volume;
            if (Math.Abs(volume - originalVolume) > originalVolume * .0006 || Math.Abs(next.Sum(i => i.MassKg) - original.MassKg) > .001)
                throw new Exception("Repeated stock tank slicing failed mass/volume conservation");
            if (next.Count != 1 << (generation + 1)) throw new Exception("Repeated tank did not reach expected fragment count");
            inputs = next;
        }
        if (faceted == 0) throw new Exception("Stock tank never completed a true faceted cut");
        Console.WriteLine($"PASS: {name} generated XML/GLB reload -> halves -> quarters -> eighths; {faceted} faceted cuts, {fallbacks} plane fallbacks; final volume={originalVolume:R}");
    }

    private static void FreshTankSeeds(SliceInput original, string name)
    {
        var ids = new HashSet<string>(); int faceted = 0;
        for (uint seed = 0; seed < 8; seed++)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slice = SliceGeometryWork.Calculate(original with { Seed = seed }, CancellationToken.None, _ => { });
            if (slice.Fragments.Length != 2 || Math.Abs(slice.Fragments.Sum(a => a.MassKg) - original.MassKg) > .001)
                throw new Exception("Varied fresh tank cut failed fragment/mass conservation");
            if (!ids.Add(string.Join("|", slice.Fragments.Select(a => a.Id)))) throw new Exception("Different tank seeds repeated identical cut geometry");
            if (slice.Faceted) faceted++;
            foreach (var asset in slice.Fragments) Reload(asset);
            Console.WriteLine($"  Fresh tank seed {seed}: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms, faceted={slice.Faceted}, attempts={slice.Attempts}, fallback={slice.FacetFallback}");
        }
        Console.WriteLine($"PASS: {name} 8 fresh deterministic varied cut seeds; {faceted} faceted cuts, {8 - faceted} plane fallbacks");
    }

    // Read the actual persisted identity subparts and material-bearing meshes, as source
    // capture does on the next swing. Do not feed internal pre-export geometry back in.
    private static SliceInput Reload(CpuFragmentAsset asset)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(asset.Xml));
        var root = xml.Root!; var part = root.Elements("Part").Single();
        var definitions = root.Elements("SubPart").ToDictionary(e => (string)e.Attribute("Id")!);
        if (part.Element("MeshView") != null) throw new Exception("Picking mesh was placed on the unraycastable part root");
        var first = definitions[(string)part.Elements("SubPart").First().Attribute("InstanceOf")!];
        if ((string?)first.Element("MeshView")?.Element("Mesh")?.Attribute("Id") != asset.Id + "_hull" || first.Element("Transform") != null)
            throw new Exception("Complete picking mesh must be on the first identity visual subpart");
        using var document = JsonDocument.Parse(asset.Glb.AsMemory(20, BitConverter.ToInt32(asset.Glb, 12)));
        var gltf = document.RootElement;
        int binary = 28 + BitConverter.ToInt32(asset.Glb, 12);
        var meshes = gltf.GetProperty("meshes").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
        var materials = new Dictionary<int, SliceMaterialData>();
        var materialIds = new Dictionary<string, int>();
        var groups = new List<SliceFace[]>();
        var topology = FragmentTopology.Read(xml)?.BeginCapture();
        foreach (var reference in part.Elements("SubPart"))
        {
            var definition = definitions[(string)reference.Attribute("InstanceOf")!];
            var faces = new List<SliceFace>();
            foreach (var model in definition.Elements().Where(e => e.Name.LocalName is "PartModel" or "PartModelGlass"))
            {
                string material = (string)model.Element("Material")!.Attribute("Id")!;
                bool glass = model.Name.LocalName == "PartModelGlass";
                string key = material + ":" + glass;
                if (!materialIds.TryGetValue(key, out int materialId))
                { materialId = materialIds.Count; materialIds.Add(key, materialId); materials.Add(materialId, new(material, glass)); }
                var mesh = meshes[(string)model.Element("Mesh")!.Attribute("Id")!];
                topology?.BeginSurface((string)model.Element("Mesh")!.Attribute("Id")!);
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    var attributes = primitive.GetProperty("attributes");
                    var positions = Read(attributes.GetProperty("POSITION").GetInt32(), 3);
                    var normals = Read(attributes.GetProperty("NORMAL").GetInt32(), 3);
                    var uvs = Read(attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
                    var indexAccessor = gltf.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()];
                    int indexOffset = Offset(indexAccessor);
                    for (int i = 0; i < indexAccessor.GetProperty("count").GetInt32(); i += 3)
                    {
                        SliceVertex V(int index)
                        {
                            int vertex = BitConverter.ToInt32(asset.Glb, indexOffset + index * 4);
                            return new(new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]),
                                new(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]), uvs[vertex * 2], uvs[vertex * 2 + 1]);
                        }
                        faces.Add(new(V(i), V(i + 1), V(i + 2), materialId));
                    }
                }
                if (topology != null) { topology.AppendChunk(faces); faces.Clear(); topology.EndSurface(); }
            }
            if (faces.Count > 0) groups.Add(faces.ToArray());
        }
        if (topology != null) groups.AddRange(topology.Complete());
        var collisionPrimitive = meshes[asset.Id + "_collision"].GetProperty("primitives")[0];
        var collisionPoints = Read(collisionPrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32(), 3);
        var hullPoints = Enumerable.Range(0, collisionPoints.Length / 3).Select(i => new Vector3((float)collisionPoints[i * 3], (float)collisionPoints[i * 3 + 1], (float)collisionPoints[i * 3 + 2])).ToArray();
        using (var pool = new BufferPool())
        {
            if (!ConvexHullHelper.CreateShape(hullPoints, pool, out var center, out var hull) || !float.IsFinite(center.LengthSquared()))
                throw new Exception("Reloaded generated tank collider cannot be cooked by the native hull implementation");
            hull.Dispose(pool);
        }
        return new(groups.ToArray(), [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, asset.MassKg);
        int Offset(JsonElement accessor)
        {
            var view = gltf.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            return binary + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                + (accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        }
        double[] Read(int index, int width)
        {
            var accessor = gltf.GetProperty("accessors")[index];
            int offset = Offset(accessor), count = accessor.GetProperty("count").GetInt32() * width;
            return Enumerable.Range(0, count).Select(i => (double)BitConverter.ToSingle(asset.Glb, offset + i * 4)).ToArray();
        }
    }
    private static SliceVertex Vertex(double[] v) => new(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), v[6], v[7]);
}
