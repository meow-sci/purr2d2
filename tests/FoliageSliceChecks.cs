using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class FoliageSliceChecks
{
    internal static void Run()
    {
        ClipAttributesAndCoplanarOwnership();
        ExportReloadAndRecut();
        Console.WriteLine("PASS: open foliage area/UV/normal clipping, wood-only physics, visual picking/bounds, durable canonical export/reload and recutting");
    }

    private static void ClipAttributesAndCoplanarOwnership()
    {
        var a = new SliceVertex(new(-1, 0, 0), new(0, 0, 1), 0, 0);
        var b = new SliceVertex(new(1, 0, 0), new CutPoint(0, 1, 1).Normalized, 1, 0);
        var c = new SliceVertex(new(1, 2, 0), new CutPoint(1, 0, 1).Normalized, 1, 1);
        var original = new SliceFace(a, b, c, 42);
        FoliageSliceGeometry.Split([original], new(new(1, 0, 0), 0), out var positive, out var negative);
        Check(positive.Length == 2 && negative.Length == 1 && Math.Abs(Area(positive) + Area(negative) - Area([original])) < 1e-12,
            "an open triangle crossing the plane produces complementary uncapped visual polygons with conserved area");
        Check(positive.All(f => Vertices(f).All(v => v.Position.X >= -1e-12))
            && negative.All(f => Vertices(f).All(v => v.Position.X <= 1e-12))
            && positive.Concat(negative).All(f => f.Material == 42 && Vertices(f).All(v => Math.Abs(v.Normal.Length - 1) < 1e-12)),
            "side ownership, source material and normalized interpolated normals survive open clipping");
        var seam = positive.Concat(negative).SelectMany(Vertices).Where(v => Math.Abs(v.Position.X) < 1e-12).ToArray();
        Check(seam.Any(v => v.Position == new CutPoint(0, 0, 0) && Math.Abs(v.U - .5) < 1e-12 && v.V == 0
                && (v.Normal - SliceVertex.Lerp(a, b, .5).Normal).Length < 1e-12)
            && seam.Any(v => v.Position == new CutPoint(0, 1, 0) && Math.Abs(v.U - .5) < 1e-12 && Math.Abs(v.V - .5) < 1e-12
                && (v.Normal - SliceVertex.Lerp(a, c, .5).Normal).Length < 1e-12),
            "new plane vertices interpolate the original UVs and normals along both crossing edges");
        FoliageSliceGeometry.Split([original], new(new(0, 0, 1), 0), out positive, out negative);
        Check(positive.SequenceEqual([original]) && negative.Length == 0,
            "a fully coplanar card has one deterministic owner and cannot double with each recut");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool stopped = false;
        try { FoliageSliceGeometry.Split([original], new(new(1, 0, 0), 0), out _, out _, cancellation.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "foliage clipping respects worker cancellation");
    }

    private static void ExportReloadAndRecut()
    {
        var (fixture, _) = FragmentAssetChecks.Fixture();
        var wood = fixture.Solids[0].Select(f => f with { Material = 0 }).ToArray();
        uint color = (721u << 22) | (511u << 12) | (208u << 2);
        uint luminosity = ClutterSurfaceTint.Luminosity(.21763764f);
        var materials = new Dictionary<int, SliceMaterialData>
        {
            [0] = new("FixtureTreeWood", false, color, luminosity),
            [1] = new("FixtureTreeLeaf", false, color, luminosity, true, true, true)
        };
        SliceVertex V(double x, double z) => new(new(x, .5, z), new(0, -1, 0), (x + 3) / 7, (z - .1) / .8);
        SliceFace[] leaves = [new(V(-3, .1), V(4, .1), V(4, .9), 1), new(V(-3, .1), V(4, .9), V(-3, .9), 1)];
        var input = new SliceInput([wood], [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 10)
        { GeneratedClosedGeometry = true, Contact = new(new(.5, .5, .5), new(0, 0, 1)) };
        var bare = SliceGeometryWork.Calculate(input, default, _ => { });
        var cut = SliceGeometryWork.Calculate(input with { Foliage = leaves }, default, _ => { });
        Check(cut.Fragments.Length == 2 && !cut.Faceted && !cut.AuthoredSubpartFallback
            && Math.Abs(cut.Fragments.Sum(f => f.MassKg) - input.MassKg) < 1e-6,
            "the production worker makes two real capped wood fragments carrying clipped foliage");
        double area = 0;
        foreach (var asset in cut.Fragments)
        {
            var baseline = bare.Fragments.Single(f => f.PositiveSide == asset.PositiveSide);
            var reload = Reload(asset);
            area += Area(reload.Input.Foliage);
            Check(asset.MassKg == baseline.MassKg && asset.Frame == baseline.Frame
                && XNode.DeepEquals(Xml(asset).Root!.Element("Part")!.Element("CustomMass"), Xml(baseline).Root!.Element("Part")!.Element("CustomMass")),
                "large open foliage cards do not alter rigid wood mass, principal frame or inertia");
            Check(reload.Collision.SequenceEqual(ReadMesh(baseline, baseline.Id + "_collision", 0))
                && PlaneSlicer.TryAuditCompound(reload.Input.Groups, out _, out _),
                "the persisted collision mesh and closed-solid audit contain wood alone");
            Check(asset.Extents.X > 2 || asset.Extents.Y > 2 || asset.Extents.Z > 2,
                "fragment visual bounds extend to the canopy beyond its structural collider");
            var picker = ReadMesh(asset, asset.Id + "_hull", 0).SelectMany(Vertices).Select(v => v.Position).ToHashSet();
            Check(reload.Input.Foliage.SelectMany(Vertices).All(v => picker.Contains(v.Position)),
                "the durable native picker includes the canonical open canopy");
            var xml = Xml(asset);
            var leafMetadata = xml.Root!.Element("Purr2D2SliceTopology")!.Elements("Foliage").Single();
            int canonical = (int)leafMetadata.Attribute("Triangles")!;
            string leafMesh = (string)leafMetadata.Attribute("Mesh")!;
            Check((int)xml.Root.Element("Purr2D2SliceTopology")!.Attribute("Version")! == 2
                && canonical == reload.Input.Foliage.Length && asset.Uploads.Single(u => u.Name == leafMesh).IndexBytes == canonical * 2 * 3 * 4
                && reload.Input.Materials.Values.Single(m => m.Foliage) == materials[1],
                "versioned topology restores canonical leaf count, original material/tint and backface policy while discarding reversed render copies");
            var leafModel = xml.Root.Elements("SubPart").Elements("PartModel").Single(m => (string?)m.Element("Mesh")?.Attribute("Id") == leafMesh);
            Check((bool?)leafModel.Element("ShadowCaster") == false,
                "alpha cards do not enter the opaque native shadow caster");
            Check(xml.Root.Elements("SubPart").Elements().Where(m => m.Name.LocalName is "PartModel" or "PartModelGlass")
                .Where(m => (string?)m.Element("Mesh")?.Attribute("Id") != leafMesh).All(m => (bool?)m.Element("ShadowCaster") == true),
                "structural wood and cut caps retain their native shadow casters");
            var recut = SliceGeometryWork.Calculate(reload.Input, default, _ => { });
            Check(recut.Fragments.Length == 2 && !recut.Faceted
                && Math.Abs(recut.Fragments.Sum(f => f.MassKg) - asset.MassKg) < 1e-6
                && Math.Abs(recut.Fragments.Sum(f => Area(Reload(f).Input.Foliage)) - Area(reload.Input.Foliage)) < 2e-6,
                "a cut loaded from actual XML/GLB can be cut again without treating leaves as wood or multiplying their area");
            var malformed = Xml(asset);
            malformed.Root!.Element("Purr2D2SliceTopology")!.Elements("Foliage").Single().SetAttributeValue("Triangles", canonical + 1);
            ExpectRejected(() => Reload(asset, malformed), "a saved canonical/render count mismatch is rejected before recutting");
        }
        Check(Math.Abs(area - Area(leaves)) < 2e-6, "the two durable fragments conserve the complete original canopy area");
        var properties = PlaneSlicer.Properties(wood);
        var piece = new SlicePiece(wood, properties.Volume, properties.Centroid, properties.Inertia, true) { ClosedSolids = [wood] };
        var exportable = fixture with { Piece = piece, Solids = [wood], Foliage = FoliageSliceGeometry.Prepare(leaves, double4x4.Identity) };
        var kept = FragmentAssetWriter.Build(exportable, materials, default);
        var flipMaterials = new Dictionary<int, SliceMaterialData>(materials) { [1] = materials[1] with { KeepBackfaceNormals = false } };
        var flipped = FragmentAssetWriter.Build(exportable, flipMaterials, default);
        var oneSidedMaterials = new Dictionary<int, SliceMaterialData>(materials) { [1] = materials[1] with { DoubleSided = false } };
        var oneSided = FragmentAssetWriter.Build(exportable, oneSidedMaterials, default);
        Check(kept.Id != flipped.Id && kept.Id != oneSided.Id && flipped.Id != oneSided.Id,
            "durable content identity includes double-sided and backface-normal rendering policy");
        Backfaces(kept, true); Backfaces(flipped, false);
        var oneSidedXml = Xml(oneSided).Root!.Element("Purr2D2SliceTopology")!.Element("Foliage")!;
        Check(ReadMesh(oneSided, (string)oneSidedXml.Attribute("Mesh")!, 1).Length == leaves.Length,
            "one-sided materials export no unnecessary reversed triangles");
        var oversized = fixture with { Piece = piece, Solids = [wood], Foliage = Enumerable.Repeat(leaves[0], 850).ToArray() };
        ExpectRejected(() => FragmentAssetWriter.Build(oversized, materials, default, new(128, 1000)),
            "render-only backface copies are included in the export resource budget");

        void Backfaces(CpuFragmentAsset asset, bool keep)
        {
            var metadata = Xml(asset).Root!.Element("Purr2D2SliceTopology")!.Element("Foliage")!;
            var faces = ReadMesh(asset, (string)metadata.Attribute("Mesh")!, 1);
            Check(faces.Length == leaves.Length * 2, "a double-sided material exports exactly one reversed copy per canonical triangle");
            for (int i = 0; i < leaves.Length; i++)
            {
                var forward = faces[i]; var reverse = faces[i + leaves.Length];
                var expected = new SliceFace(Normal(forward.A), Normal(forward.C), Normal(forward.B), forward.Material);
                Check(reverse == expected, "reversed rendering preserves UVs, flips winding and applies the material's exact backface normal policy");
            }
            SliceVertex Normal(SliceVertex vertex) => keep ? vertex : vertex with { Normal = vertex.Normal * -1 };
        }
    }

    private static (SliceInput Input, SliceFace[] Collision) Reload(CpuFragmentAsset asset, XDocument? document = null)
    {
        var xml = document ?? Xml(asset);
        var topology = FragmentTopology.Read(xml) ?? throw new Exception("Foliage fixture omitted topology.");
        var capture = topology.BeginCapture();
        var materials = new Dictionary<int, SliceMaterialData>();
        foreach (var model in xml.Root!.Elements("SubPart").Elements().Where(e => e.Name.LocalName is "PartModel" or "PartModelGlass"))
        {
            string mesh = (string)model.Element("Mesh")!.Attribute("Id")!;
            string id = (string)model.Element("Material")!.Attribute("Id")!;
            bool leaf = topology.TryFoliage(mesh, out var flags);
            ClutterSurfaceTint.TryRead(mesh, out uint color, out uint luminosity);
            var material = new SliceMaterialData(id, model.Name.LocalName == "PartModelGlass", color, luminosity,
                leaf, flags.DoubleSided, flags.KeepBackfaceNormals);
            int index = materials.FirstOrDefault(p => p.Value == material, new(-1, material)).Key;
            if (index == -1) { index = materials.Count; materials.Add(index, material); }
            capture.BeginSurface(mesh);
            foreach (var chunk in ReadMesh(asset, mesh, index).Chunk(2)) capture.AppendChunk(chunk);
            capture.EndSurface();
        }
        return (new(capture.Complete().ToArray(), [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, asset.MassKg)
        { GeneratedClosedGeometry = true, Foliage = capture.CompleteFoliage() }, ReadMesh(asset, asset.Id + "_collision", 0));
    }

    private static SliceFace[] ReadMesh(CpuFragmentAsset asset, string name, int material)
    {
        using var document = JsonDocument.Parse(asset.Glb.AsMemory(20, BitConverter.ToInt32(asset.Glb, 12)));
        var root = document.RootElement;
        int binary = 28 + BitConverter.ToInt32(asset.Glb, 12);
        var primitive = root.GetProperty("meshes").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        var indices = root.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()];
        var faces = new SliceFace[indices.GetProperty("count").GetInt32() / 3];
        for (int i = 0; i < faces.Length; i++) faces[i] = new(V(i * 3), V(i * 3 + 1), V(i * 3 + 2), material);
        return faces;
        SliceVertex V(int corner)
        {
            int index = BitConverter.ToInt32(asset.Glb, Offset(indices) + corner * 4);
            return new(new(Value("POSITION", 3, 0), Value("POSITION", 3, 1), Value("POSITION", 3, 2)),
                new(Value("NORMAL", 3, 0), Value("NORMAL", 3, 1), Value("NORMAL", 3, 2)), Value("TEXCOORD_0", 2, 0), Value("TEXCOORD_0", 2, 1));
            float Value(string attribute, int width, int component)
            {
                var accessor = root.GetProperty("accessors")[attributes.GetProperty(attribute).GetInt32()];
                return BitConverter.ToSingle(asset.Glb, Offset(accessor) + (index * width + component) * 4);
            }
        }
        int Offset(JsonElement accessor)
        {
            var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            return binary + (view.TryGetProperty("byteOffset", out var offset) ? offset.GetInt32() : 0)
                + (accessor.TryGetProperty("byteOffset", out offset) ? offset.GetInt32() : 0);
        }
    }

    private static XDocument Xml(CpuFragmentAsset asset) => XDocument.Parse(Encoding.UTF8.GetString(asset.Xml));
    private static IEnumerable<SliceVertex> Vertices(SliceFace f) => new[] { f.A, f.B, f.C };
    private static double Area(IEnumerable<SliceFace> faces) => faces.Sum(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length * .5);
    private static void ExpectRejected(Action action, string message)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Foliage: " + message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Foliage: " + message); }
}
