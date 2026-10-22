using System.Text.Json;
using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

internal static class StockSliceChecks
{
    internal sealed record Fixture(string Name, double[][][][] Groups);
    internal static void Run(string path)
    {
        foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path))!)
        {
            var groups = fixture.Groups.Select(group => group.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), 0)).ToArray()).ToArray();
            if (!SliceShellBuilder.TryPrepare(groups, out var solids, out var reason))
                throw new Exception(fixture.Name + " shell preparation: " + reason);
            Console.WriteLine($"{fixture.Name}: {groups.Sum(g => g.Length)} authored triangles -> {solids.Length} solids / {solids.Sum(g => g.Length)} triangles");
            var planes = fixture.Name.Contains("FuelTank")
                ? new[] { new SlicePlane(new(1, 0, 0), .13), new SlicePlane(new(1, .1, .15), -.1) }
                : new[] { new SlicePlane(new(1, 0, 0), .13) };
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
                    if (points > 32768 || quantized.Length > 511) throw new Exception("Stock fragment exceeds native preparation budgets");
                    Console.WriteLine($"  Fragment: {points} hull points / {quantized.Length} closed surfaces");
                }
                Console.WriteLine($"PASS: {fixture.Name} cut {plane}, {result.Pieces.Length} fragments, {result.ContourCount} cut contours");
            }
        }
    }
    private static SliceVertex Vertex(double[] v) => new(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), v[6], v[7]);
}
