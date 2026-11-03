using System.Text.Json;
using Purr2D2.Cutting;
using Purr2D2.Slicing;
using Purr2D2.Rendering;

internal static class ClutterStockSliceChecks
{
    internal sealed record Surface(int Index, string Id, bool Foliage, bool DoubleSided, bool KeepBackfaceNormals);
    internal sealed record Fixture(string Name, bool Tree, double[][][][] Groups, double[][][] Foliage, Surface[] Materials);
    internal static void Run(string path)
    {
        Purr2D2.Core.DebugTimings.Enabled = false;
        int rocks = 0, trees = 0;
        foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path))!)
        {
            var groups = fixture.Groups.Select(g => g.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), (int)f[0][8])).ToArray()).ToArray();
            const uint packedColor = (917u << 22) | (301u << 12) | (72u << 2);
            uint luminosity = ClutterSurfaceTint.Luminosity(.21763764f);
            var materials = fixture.Materials.ToDictionary(s => s.Index,
                s => new SliceMaterialData(s.Id, false, packedColor, luminosity, s.Foliage, s.DoubleSided, s.KeepBackfaceNormals));
            var foliage = fixture.Foliage.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), (int)f[0][8])).ToArray();
            var input = new SliceInput(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 100) { Foliage = foliage };
            var vertices = groups.SelectMany(g => g).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
            var center = new CutPoint((vertices.Min(p => p.X) + vertices.Max(p => p.X)) / 2,
                (vertices.Min(p => p.Y) + vertices.Max(p => p.Y)) / 2, (vertices.Min(p => p.Z) + vertices.Max(p => p.Z)) / 2);
            double height = fixture.Tree ? vertices.Min(p => p.Y) + (vertices.Max(p => p.Y) - vertices.Min(p => p.Y)) * .12 : center.Y;
            var contact = new ContactSlice(new(center.X, height, center.Z), new(0, 1, 0));
            if (fixture.Tree) trees++;
            {
                var cut = ClutterSliceGeometry.Calculate(input with { Contact = contact }, fixture.Tree, CancellationToken.None, _ => { });
                Check(cut, input.MassKg, packedColor, luminosity);
                if (!fixture.Tree)
                {
                    rocks++;
                    // Every anchored candidate is outside the object; production
                    // must retry a true central rock section instead of reporting a cut.
                    var far = new ContactSlice(center + new CutPoint(1e6, 1e6, 1e6), new(0, 1, 0));
                    Check(ClutterSliceGeometry.Calculate(input with { Contact = far }, false, CancellationToken.None, _ => { }), input.MassKg, packedColor, luminosity);
                    Console.WriteLine($"PASS: {fixture.Name}: contact cut and rejected-contact central fallback exported real closed fragments ({groups.Sum(g => g.Length)} source triangles).");
                }
                else
                {
                    if (foliage.Length == 0) throw new Exception("Stock full-tree fixture omitted foliage.");
                    double restoredArea = 0; int leafPieces = 0;
                    foreach (var fragment in cut.Fragments)
                    {
                        var restored = Restore(fragment, materials);
                        restoredArea += Area(restored.Foliage);
                        if (restored.Foliage.Length == 0) continue;
                        leafPieces++;
                        var recut = SliceGeometryWork.Calculate(restored, CancellationToken.None, _ => { });
                        Check(recut, fragment.MassKg, packedColor, luminosity);
                        Near(recut.Fragments.Sum(f => Area(Restore(f, materials).Foliage)), Area(restored.Foliage));
                    }
                    if (leafPieces == 0) throw new Exception("Stock tree cut discarded its canopy.");
                    Near(restoredArea, Area(foliage));
                    Console.WriteLine($"PASS: {fixture.Name}: actual full tree exports {cut.Fragments.Length} capped fragments; {foliage.Length} leaf triangles survive reload/recut without duplication.");
                }
            }
        }
        if (rocks != 3 || trees != 9) throw new Exception($"Expected 3 rocks and all 9 active stock trees, got {rocks}/{trees}.");
        Console.WriteLine("PASS: all nine active stock tree/shrub variants actually split; refusal and whole-tree fallback never count as success.");
    }

    private static double Area(IEnumerable<SliceFace> faces) => faces.Sum(f => CutPoint.Cross(f.B.Position - f.A.Position, f.C.Position - f.A.Position).Length / 2);
    private static void Near(double actual, double expected)
    { if (Math.Abs(actual - expected) > Math.Max(1e-7, expected * .00002)) throw new Exception($"Foliage area lost/duplicated: {actual:R} != {expected:R}"); }

    internal static SliceInput Restore(CpuFragmentAsset fragment, IReadOnlyDictionary<int, SliceMaterialData> materials)
    {
        var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(fragment.Xml));
        var topology = FragmentTopology.Read(xml)!;
        var capture = topology.BeginCapture();
        var capturedMaterials = materials.ToDictionary(m => m.Key, m => m.Value);
        var ids = capturedMaterials.ToDictionary(m => m.Value.Id, m => m.Key, StringComparer.Ordinal);
        var models = xml.Root!.Elements("SubPart").SelectMany(p => p.Elements("PartModel"))
            .ToDictionary(m => (string)m.Element("Mesh")!.Attribute("Id")!, m => m);
        foreach (var upload in fragment.Uploads)
        {
            var model = models[upload.Name];
            string id = (string)model.Element("Material")!.Attribute("Id")!;
            if (!ids.TryGetValue(id, out int material))
            {
                if (!FragmentAssetWriter.IsCapMaterial(id)) throw new Exception("Unexpected restored material: " + id);
                material = capturedMaterials.Keys.DefaultIfEmpty(-1).Max() + 1;
                ids.Add(id, material);
                capturedMaterials.Add(material, new(id, false));
            }
            var faces = new SliceFace[upload.IndexBytes / 12];
            for (int f = 0; f < faces.Length; f++) faces[f] = new(Read(f * 3), Read(f * 3 + 1), Read(f * 3 + 2), material);
            capture.BeginSurface(upload.Name); capture.AppendChunk(faces); capture.EndSurface();
            if (topology.TryFoliage(upload.Name, out var leaf))
            {
                if (faces.Length != leaf.Triangles * (leaf.DoubleSided ? 2 : 1)) throw new Exception("Backface export does not match canonical foliage topology.");
                if ((bool?)model.Element("ShadowCaster") != false) throw new Exception("Leaf cards would cast opaque rectangular shadows.");
            }
            SliceVertex Read(int index)
            {
                int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(upload.Bytes.AsSpan(upload.VertexBytes + index * 4)) * 32;
                float Value(int n) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(upload.Bytes.AsSpan(offset + n * 4));
                return new(new(Value(0), Value(1), Value(2)), new(Value(3), Value(4), Value(5)), Value(6), Value(7));
            }
        }
        return new(capture.Complete().ToArray(), [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], capturedMaterials, fragment.MassKg)
        { GeneratedClosedGeometry = true, Foliage = capture.CompleteFoliage(), CutKind = topology.CutKind };
    }
    private static void Check(CpuSlice cut, float mass, uint color, uint luminosity)
    {
        if (cut.Fragments.Length is < 2 or > 8 || Math.Abs(cut.Fragments.Sum(f => f.MassKg) - mass) > .001)
            throw new Exception("Actual clutter slice lost fragment count or mass.");
        foreach (var asset in cut.Fragments)
        {
            if (asset.Glb.Length == 0 || asset.Xml.Length == 0 || asset.Sparks.Length == 0 || asset.Uploads.Any(u => u.Bytes.Length != u.VertexBytes + u.IndexBytes))
                throw new Exception("Actual clutter slice omitted durable geometry, caps or bounded native upload data.");
            bool tinted = false;
            foreach (var surface in asset.Uploads)
            {
                if (surface.Name.EndsWith("_cap", StringComparison.Ordinal))
                {
                    if (ClutterSurfaceTint.TryRead(surface.Name, out _, out _)) throw new Exception("Actual cut cap inherited exterior terrain tint.");
                }
                else if (!ClutterSurfaceTint.TryRead(surface.Name, out uint actualColor, out uint actualLuminosity)
                    || actualColor != color || actualLuminosity != luminosity)
                    throw new Exception("Actual stock fragment lost its durable exterior RGB10 tint.");
                else tinted = true;
            }
            if (!tinted) throw new Exception("Actual stock fragment has no tinted exterior.");
        }
    }
    private static SliceVertex Vertex(double[] v) => new(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), v[6], v[7]);
}
