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
        CheckIdentity(fragment, materials, asset);
        CheckSavedAsset(asset);
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

    private static void CheckIdentity(CpuFragment fragment, Dictionary<int, SliceMaterialData> materials, CpuFragmentAsset asset)
    {
        // Frozen independently from this implementation using the documented
        // length-delimited little-endian v5 stream (including tint fields) and
        // the authored box fixture; the frozen v3 collider fixes native hull order.
        Check(asset.Id == "Purr2D2_Fragment_47a9cf635a2d29699c17d5cde7a4777578598fcc6790d1982e0f58369d163225", "canonical identity has stable versioned little-endian field encoding");
        var reordered = materials.Reverse().ToDictionary(m => m.Key, m => m.Value);
        Check(Export(fragment, reordered).Id == asset.Id, "material dictionary iteration order cannot affect identity");
        var aliases = fragment with { Solids = fragment.Solids.Select(s => s.Select(f => f.Material == 0 ? f with { Material = 1 } : f).ToArray()).ToArray() };
        Check(Export(aliases).Id == asset.Id, "source material aliases use durable identity rather than transient indices");

        Check(Export(ChangeVertex(v => v with { Position = v.Position + new CutPoint(1, 0, 0) })).Id != asset.Id,
            "positions affect immutable identity");
        Check(Export(ChangeVertex(v => v with { Normal = v.Normal + new CutPoint(.125, 0, 0) })).Id != asset.Id,
            "normals affect immutable identity");
        Check(Export(ChangeVertex(v => v with { U = v.U + .125 })).Id != asset.Id, "U coordinates affect immutable identity");
        Check(Export(ChangeVertex(v => v with { V = v.V + .125 })).Id != asset.Id, "V coordinates affect immutable identity");
        var sameFloat = Export(ChangeVertex(v => v with
        {
            Normal = v.Normal with { Z = Math.BitIncrement(v.Normal.Z) }, U = Math.BitIncrement(v.U),
        }));
        Check(sameFloat.Id == asset.Id && sameFloat.Glb.SequenceEqual(asset.Glb) && sameFloat.Xml.SequenceEqual(asset.Xml),
            "normal and UV differences below exported float precision retain identical identity and bytes");
        var pickerOrder = fragment.Solids.Select(s => s.ToArray()).ToArray();
        (pickerOrder[0][1], pickerOrder[0][2]) = (pickerOrder[0][2], pickerOrder[0][1]);
        Check(Export(fragment with { Solids = pickerOrder }).Id != asset.Id,
            "cross-material face order affects the full picker identity even when render batching is unchanged");
        var winding = fragment.Solids.Select(s => s.ToArray()).ToArray();
        winding[0][0] = winding[0][0] with { B = winding[0][0].C, C = winding[0][0].B };
        Check(Export(fragment with { Solids = winding }).Id != asset.Id, "ordered triangle winding affects immutable identity");
        var changedMaterials = new Dictionary<int, SliceMaterialData>(materials) { [3] = new("OtherMetal", false) };
        Check(Export(fragment, changedMaterials).Id != asset.Id, "durable material IDs affect immutable identity");
        changedMaterials[3] = materials[3] with { Glass = true };
        Check(Export(fragment, changedMaterials).Id != asset.Id, "the glass render pass affects immutable identity");
        changedMaterials[3] = materials[3] with { ClutterColor = 0x80a0c003u, ClutterLuminosity = ClutterSurfaceTint.Luminosity(.75f) };
        var tinted = Export(fragment, changedMaterials);
        Check(tinted.Id != asset.Id, "native clutter tint and luminosity cannot alias an ordinary saved surface");
        changedMaterials[3] = changedMaterials[3] with { ClutterColor = 0x80a0c007u };
        Check(Export(fragment, changedMaterials).Id != tinted.Id, "every packed RGB10 color bit affects immutable identity");
        changedMaterials[3] = changedMaterials[3] with { ClutterColor = 0x80a0c003u, ClutterLuminosity = ClutterSurfaceTint.Luminosity(.5f) };
        Check(Export(fragment, changedMaterials).Id != tinted.Id, "native clutter luminosity affects immutable identity");
        Check(Export(fragment with { MassKg = fragment.MassKg + 1 }).Id != asset.Id, "native mass affects immutable identity");
        for (int axis = 0; axis < 3; axis++)
        {
            var inertia = fragment.Frame.PrincipalSpecificInertia;
            var changed = new double3(axis == 0 ? Math.BitIncrement(inertia.X) : inertia.X,
                axis == 1 ? Math.BitIncrement(inertia.Y) : inertia.Y, axis == 2 ? Math.BitIncrement(inertia.Z) : inertia.Z);
            Check(Export(fragment with { Frame = fragment.Frame with { PrincipalSpecificInertia = changed } }).Id != asset.Id,
                "every principal inertia component retains durable double precision");
        }

        // These pairs produce the same old delimiter-joined material text. A
        // byte-length prefix keeps both surface boundaries and literal delimiters.
        var twoMaterials = fragment with { Solids = fragment.Solids.Select(s => s.Select(f => f with { Material = f.Material == 0 ? 0 : 1 }).ToArray()).ToArray() };
        var left = new Dictionary<int, SliceMaterialData> { [0] = new("A", false), [1] = new("B:glass=False|C", false) };
        var right = new Dictionary<int, SliceMaterialData> { [0] = new("A:glass=False|B", false), [1] = new("C", false) };
        Check(Export(twoMaterials, left).Id != Export(twoMaterials, right).Id,
            "length-prefixed material identity cannot alias strings containing field delimiters");
        var unicode = new Dictionary<int, SliceMaterialData>(materials) { [3] = new("δοκιμή|:glass=False🚀", false) };
        Check(Export(fragment, unicode).Id == "Purr2D2_Fragment_096569ba6d234f2faea91075570009e50c0bad4c9cb75ff520175fe50102e120", "material lengths use UTF-8 bytes rather than character counts");
        var longMaterial = new Dictionary<int, SliceMaterialData>(materials) { [3] = new(new string('x', 16_315) + "🚀tail", false) };
        var longAsset = Export(fragment, longMaterial);
        Check(longAsset.Id == Export(fragment, longMaterial).Id && longAsset.Id != asset.Id,
            "UTF-8 strings and geometry remain deterministic across hash buffer boundaries");

        Invalid(() => Export(ChangeVertex(v => v with { U = double.NaN })), "non-finite UVs are rejected while hashing");
        Invalid(() => Export(ChangeVertex(v => v with { Normal = new(double.MaxValue, 0, 1) })), "attributes that overflow float are rejected while hashing");
        Invalid(() => Export(ChangeVertex(v => v with { Position = new(Math.BitIncrement(1d), v.Position.Y, v.Position.Z) })),
            "unprepared double positions cannot alias a different compact picker layout");
        Invalid(() => Export(fragment with { MassKg = float.NaN }), "non-finite native mass is rejected");
        Invalid(() => Export(fragment with { Frame = fragment.Frame with { PrincipalSpecificInertia = new(1, double.PositiveInfinity, 3) } }),
            "non-finite native inertia is rejected");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped = false;
        try { FragmentAssetWriter.Build(fragment, materials, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "cancelled export stops before native serialization");
        Console.WriteLine("PASS: canonical fragment identity includes exported attributes, material/glass, topology, exact picker order, mass and inertia; finite/cancellation guards retained");

        CpuFragmentAsset Export(CpuFragment value, IReadOnlyDictionary<int, SliceMaterialData>? mapping = null)
            => FragmentAssetWriter.Build(value, mapping ?? materials, CancellationToken.None);
        CpuFragment ChangeVertex(Func<SliceVertex, SliceVertex> change)
        {
            var solids = fragment.Solids.Select(s => s.ToArray()).ToArray();
            solids[0][0] = solids[0][0] with { A = change(solids[0][0].A) };
            return fragment with { Solids = solids };
        }
        static void Invalid(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Fragment identity: " + message);
        }
    }

    private static void CheckSavedAsset(CpuFragmentAsset asset)
    {
        var oldXml = File.ReadAllBytes("tests/fixtures/fragment-v3.xml");
        var oldGlb = File.ReadAllBytes("tests/fixtures/fragment-v3.glb");
        var legacy = XDocument.Parse(Encoding.UTF8.GetString(oldXml));
        string oldId = (string)legacy.Root!.Element("Part")!.Attribute("Id")!;
        Check(asset.Id != oldId, "canonical identity uses a new version and cannot replace an existing v3 saved asset");
        var renamed = asset.Glb.ToArray();
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(renamed.AsSpan(12));
        var json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(renamed, 20, jsonLength).Replace(asset.Id, oldId, StringComparison.Ordinal));
        Check(json.Length == jsonLength, "only fixed-width hash-derived names change between identity versions");
        json.CopyTo(renamed, 20);
        Check(renamed.AsSpan().SequenceEqual(oldGlb),
            "buffered GLB serialization is byte-for-byte identical to the pre-change render, full picker and compact collider fixture");
        FragmentTopology.Remember(legacy);
        FragmentTopology.Remember(XDocument.Parse(Encoding.UTF8.GetString(asset.Xml)));
        Check(FragmentTopology.Find(oldId) != null && FragmentTopology.Find(asset.Id) != null
            && (string?)legacy.Root.Element("Part")?.Attribute("Id") == oldId,
            "old and new immutable assets keep their original identities and coexist in the saved topology cache");
        Console.WriteLine("PASS: frozen v3 GLB byte equality and saved topology coexistence");
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
