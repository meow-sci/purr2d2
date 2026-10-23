using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class FragmentAssetChecks
{
    internal static void Run()
    {
        var (fragment, materials) = Fixture();
        var before = fragment.Solids.Select(s => s.ToArray()).ToArray();
        var asset = FragmentAssetWriter.Build(fragment, materials, CancellationToken.None);
        Check(asset.Uploads.Length == 4 && FragmentAssetWriter.CountRenderSurfaces(fragment.Solids, materials) == 4,
            "three closed solids merge by durable material identity and glass pass; material aliases coalesce");
        Check(before.Zip(fragment.Solids).All(p => p.First.SequenceEqual(p.Second)), "export leaves source topology and cap material indices unchanged");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
        var visual = xml.Root!.Elements("SubPart").Single();
        var root = xml.Root.Element("Part")!;
        Check((string?)root.Elements("SubPart").Single().Attribute("InstanceOf") == (string?)visual.Attribute("Id")
            && root.Element("MeshView") == null && visual.Element("Transform") == null
            && (string?)visual.Element("MeshView")?.Element("Mesh")?.Attribute("Id") == asset.Id + "_hull",
            "one identity visual SubPart owns the complete picking mesh");
        var models = visual.Elements().Where(e => e.Name.LocalName is "PartModel" or "PartModelGlass").ToArray();
        Check(models.Length == 4 && models.Count(m => m.Name == "PartModelGlass") == 1
            && models.Count(m => (string?)m.Element("Material")?.Attribute("Id") == "FixtureMetal") == 2,
            "opaque and glass rendering remain separate even when they use the same native material");
        var original = fragment.Solids.SelectMany(s => s).ToArray();
        var decoded = new List<SliceFace>();
        var topology = (FragmentTopology.Read(xml) ?? throw new Exception("Merged topology was not persisted.")).BeginCapture();
        using var document = JsonDocument.Parse(asset.Glb.AsMemory(20, BitConverter.ToInt32(asset.Glb, 12)));
        var gltf = document.RootElement;
        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i];
            string materialId = (string)model.Element("Material")!.Attribute("Id")!;
            bool glass = model.Name == "PartModelGlass";
            var expected = original.Where(f => Material(f.Material) == new SliceMaterialData(materialId, glass)).ToArray();
            var upload = asset.Uploads[i];
            var mesh = gltf.GetProperty("meshes")[i];
            Check((string?)model.Element("Mesh")?.Attribute("Id") == upload.Name && mesh.GetProperty("name").GetString() == upload.Name,
                "XML, GLB and upload surfaces retain a one-to-one mapping");
            Check(upload.VertexBytes == expected.Length * 3 * 32 && upload.IndexBytes == expected.Length * 3 * 4,
                "merged surface preserves every triangle");
            var primitive = mesh.GetProperty("primitives")[0];
            var attributes = primitive.GetProperty("attributes");
            int material = materials.First(m => m.Value == new SliceMaterialData(materialId, glass)).Key;
            var surfaceFaces = new List<SliceFace>();
            for (int face = 0; face < expected.Length; face++)
            {
                var source = expected[face];
                SliceVertex[] vertices = [source.A, source.B, source.C];
                var loaded = new SliceVertex[3];
                for (int corner = 0; corner < 3; corner++)
                {
                    int index = face * 3 + corner;
                    Check(BinaryPrimitives.ReadInt32LittleEndian(upload.Bytes.AsSpan(upload.VertexBytes + index * 4)) == index,
                        "upload preserves face winding");
                    var v = vertices[corner];
                    double[] values = [v.Position.X, v.Position.Y, v.Position.Z, v.Normal.X, v.Normal.Y, v.Normal.Z, v.U, v.V];
                    for (int attribute = 0; attribute < values.Length; attribute++)
                        Check(BitConverter.ToSingle(upload.Bytes, index * 32 + attribute * 4) == (float)values[attribute],
                            "upload preserves positions, normals and UVs exactly at native float precision");
                    loaded[corner] = new(new(Value("POSITION", index, 3, 0), Value("POSITION", index, 3, 1), Value("POSITION", index, 3, 2)),
                        new(Value("NORMAL", index, 3, 0), Value("NORMAL", index, 3, 1), Value("NORMAL", index, 3, 2)),
                        Value("TEXCOORD_0", index, 2, 0), Value("TEXCOORD_0", index, 2, 1));
                    Check(loaded[corner] == new SliceVertex(new((float)v.Position.X, (float)v.Position.Y, (float)v.Position.Z),
                        new((float)v.Normal.X, (float)v.Normal.Y, (float)v.Normal.Z), (float)v.U, (float)v.V),
                        "GLB preserves ordered triangle corners and all render attributes");
                    Check(Index(index) == index, "GLB preserves face winding");
                }
                surfaceFaces.Add(new(loaded[0], loaded[1], loaded[2], material));
            }
            decoded.AddRange(surfaceFaces);
            topology.BeginSurface(upload.Name);
            // Split through run boundaries, matching bounded live-capture calls.
            foreach (var chunk in surfaceFaces.Chunk(3)) topology.AppendChunk(chunk);
            topology.EndSurface();
            bool cap = materialId == FragmentAssetWriter.CapMaterial;
            Check(upload.Name.EndsWith("_cap", StringComparison.Ordinal) == cap, "only cap surfaces carry the heating marker");
            if (cap) Check(upload.Name[..upload.Name.IndexOf("_s", FragmentAssetWriter.Prefix.Length, StringComparison.Ordinal)] == asset.Id,
                "existing cap owner lookup resolves the merged fragment");

            float Value(string name, int index, int width, int component)
            {
                var accessor = gltf.GetProperty("accessors")[attributes.GetProperty(name).GetInt32()];
                return BitConverter.ToSingle(asset.Glb, Offset(accessor) + (index * width + component) * 4);
            }
            int Index(int index)
            {
                var accessor = gltf.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()];
                return BitConverter.ToInt32(asset.Glb, Offset(accessor) + index * 4);
            }
        }
        var restoredGroups = topology.Complete().ToArray();
        Check(decoded.Count == original.Length && !PlaneSlicer.TryAudit(decoded, out _)
            && restoredGroups.Length == 3 && PlaneSlicer.TryAuditCompound(restoredGroups, out var volume, out _) && Math.Abs(volume - 3) < 1e-8,
            "durable provenance retains independent closure even when closed solids share entire faces");
        Check(SliceShellBuilder.TryPrepare(restoredGroups, out var restored, out var reason)
            && PlaneSlicer.TrySliceCompound(restored, new(new(0, 0, 1), .5), out var recut, out reason)
            && recut!.Pieces.Length == 2 && Math.Abs(recut.Pieces.Sum(p => p.Volume) - 3) < 1e-8,
            "merged XML/GLB geometry remains resliceable with conserved volume: " + reason);
        FragmentTopology.Remember(XDocument.Parse(Encoding.UTF8.GetString(asset.Xml)));
        Check(FragmentTopology.Find(asset.Id) != null, "startup/worker XML parsing restores the generated capture topology");
        var missing = XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
        missing.Root!.Element("Purr2D2SliceTopology")!.Remove();
        Reject(missing, "new merged XML cannot silently fall back to legacy sheet interpretation");
        var malformed = XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
        malformed.Descendants("Run").First().SetAttributeValue("Solid", 9);
        Reject(malformed, "malformed persisted topology cannot enter the capture cache");
        var repeated = FragmentAssetWriter.Build(fragment, materials, CancellationToken.None);
        Check(asset.Id == repeated.Id && asset.Xml.SequenceEqual(repeated.Xml) && asset.Glb.SequenceEqual(repeated.Glb),
            "merged export is deterministic without mutating worker input");
        var combined = FragmentAssetWriter.Build(fragment with { Solids = [original] }, materials, CancellationToken.None);
        Check(combined.Id != asset.Id, "durable IDs distinguish identical render triangles with different closed-solid provenance");
        var incomplete = FragmentTopology.Read(xml)!.BeginCapture();
        bool refused = false;
        try { incomplete.Complete().ToArray(); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "live recapture refuses missing render models instead of cutting partial geometry");
        refused = false;
        try { FragmentTopology.Find("missing-merged-fragment", required: true); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "live merged capture requires its restored topology cache entry");
        var (extraCaps, extraMaterials) = Fixture(glassCap: true);
        var multiple = FragmentAssetWriter.Build(extraCaps, extraMaterials, CancellationToken.None);
        var capNames = multiple.Uploads.Select(u => u.Name).Where(n => n.EndsWith("_cap", StringComparison.Ordinal)).ToArray();
        Check(multiple.Uploads.Length == 5 && capNames.Length == 2 && capNames.Distinct().Count() == 2
            && capNames.All(n => n[..n.IndexOf("_s", FragmentAssetWriter.Prefix.Length, StringComparison.Ordinal)] == multiple.Id),
            "distinct cap material/pass groups have unique names and retain their heat owner");
        Console.WriteLine("PASS: merged fragment surfaces, one visual SubPart, material/glass/cap identity, exact geometry and reslicing; 12 legacy surfaces -> 4 render surfaces");

        SliceMaterialData Material(int index) => index == -1 ? new(FragmentAssetWriter.CapMaterial, false) : materials[index];
        int Offset(JsonElement accessor)
        {
            var view = gltf.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            return 28 + BitConverter.ToInt32(asset.Glb, 12) + view.GetProperty("byteOffset").GetInt32();
        }
        static void Reject(XDocument xml, string message)
        {
            try { FragmentTopology.Remember(xml); }
            catch (InvalidDataException) { return; }
            throw new Exception("Fragment export: " + message);
        }
    }

    internal static (CpuFragment Fragment, Dictionary<int, SliceMaterialData> Materials) Fixture(bool glassCap = false)
    {
        var materials = new Dictionary<int, SliceMaterialData>
        {
            [0] = new("FixtureMetal", false), [1] = new("FixtureMetal", false), [2] = new("FixtureMetal", true),
            [3] = new("FixtureCopper", false), [4] = new(FragmentAssetWriter.CapMaterial, false),
            [5] = new(FragmentAssetWriter.CapMaterial, false), [6] = new(FragmentAssetWriter.CapMaterial, true),
        };
        var solids = Enumerable.Range(0, 3).Select(Box).ToArray();
        var all = solids.SelectMany(s => s).ToArray();
        var properties = PlaneSlicer.Properties(all);
        var piece = new SlicePiece(all, properties.Volume, properties.Centroid, properties.Inertia, true) { ClosedSolids = solids };
        return (new(piece, new(double4x4.Identity, doubleQuat.Identity, new(0, 0, 0), new(1, 2, 3)), solids, new(7, 1, 1), 3), materials);

        SliceFace[] Box(int box)
        {
            CutPoint[] points = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];
            int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]];
            int[] material = [box % 2, 2, 3, glassCap && box == 0 ? 6 : 4 + box % 2, -1, box % 2];
            var faces = new List<SliceFace>();
            for (int i = 0; i < quads.Length; i++)
            {
                var q = quads[i];
                var normal = CutPoint.Cross(points[q[1]] - points[q[0]], points[q[2]] - points[q[0]]).Normalized;
                SliceVertex V(int index)
                {
                    var point = points[index] + new CutPoint(box, 0, 0);
                    return new(point, normal, point.X * .25 + point.Z * .125, point.Y * .5);
                }
                faces.Add(new(V(q[0]), V(q[1]), V(q[2]), material[i]));
                faces.Add(new(V(q[0]), V(q[2]), V(q[3]), material[i]));
            }
            return faces.ToArray();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Fragment export: " + message);
    }
}
