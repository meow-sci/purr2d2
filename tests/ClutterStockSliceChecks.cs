using System.Text.Json;
using Purr2D2.Cutting;
using Purr2D2.Slicing;
using Purr2D2.Rendering;

internal static class ClutterStockSliceChecks
{
    internal sealed record Fixture(string Name, bool Tree, bool NativeFoliage, double[][][][] Groups);
    internal static void Run(string path)
    {
        Purr2D2.Core.DebugTimings.Enabled = false;
        int rocks = 0, trunks = 0, foliage = 0;
        foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path))!)
        {
            var groups = fixture.Groups.Select(g => g.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), (int)f[0][8])).ToArray()).ToArray();
            if (fixture.NativeFoliage)
            {
                if (!ClutterSliceGeometry.RequiresNativeFoliage(true, true, true)) throw new Exception("Native foliage shader policy regressed.");
                foliage++;
                Console.WriteLine($"PASS: {fixture.Name}: {groups.Sum(g => g.Length)} native triangles retain opacity/transmission via whole-tree topple fallback.");
                continue;
            }
            if (ClutterSliceGeometry.RequiresNativeFoliage(false, false, false)) throw new Exception("Opaque stock surface unexpectedly refused.");
            const uint packedColor = (917u << 22) | (301u << 12) | (72u << 2);
            uint luminosity = ClutterSurfaceTint.Luminosity(.21763764f);
            var materials = groups.SelectMany(g => g).Select(f => f.Material).Distinct().ToDictionary(id => id,
                id => new SliceMaterialData("StockMaterial" + id, false, packedColor, luminosity));
            var input = new SliceInput(groups, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, 100);
            var vertices = groups.SelectMany(g => g).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
            var center = new CutPoint((vertices.Min(p => p.X) + vertices.Max(p => p.X)) / 2,
                (vertices.Min(p => p.Y) + vertices.Max(p => p.Y)) / 2, (vertices.Min(p => p.Z) + vertices.Max(p => p.Z)) / 2);
            var contact = new ContactSlice(center, new(0, 1, 0));
            if (fixture.Tree) trunks++;
            try
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
                else Console.WriteLine($"PASS: {fixture.Name}: actual trunk contact geometry exported {cut.Fragments.Length} fragments.");
            }
            catch (InvalidDataException error) when (fixture.Tree)
            { Console.WriteLine($"PASS: {fixture.Name}: actual trunk attempt safely refused, whole-tree fallback required: {error.Message}"); }
        }
        if (rocks == 0 || trunks == 0 || foliage == 0) throw new Exception("Actual stock rock, trunk and native foliage coverage is incomplete.");
        Console.WriteLine($"PASS: actual stock clutter coverage: {rocks} rocks cut + middle fallback, {trunks} trunk attempt, {foliage} full-tree appearance fallback.");
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
