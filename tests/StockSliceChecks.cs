using System.Text.Json;
using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Slicing;
using Purr2D2.Rendering;

internal static class StockSliceChecks
{
    internal sealed record Fixture(string Name, double[][][][] Groups);
    internal static void Run(string path)
    {
        foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(path))!)
        {
            if (Environment.GetEnvironmentVariable("PURR_STOCK_FILTER") is { Length: > 0 } filter && !fixture.Name.Contains(filter)) continue;
            var groups = fixture.Groups.Select(group => group.Select(f => new SliceFace(Vertex(f[0]), Vertex(f[1]), Vertex(f[2]), f[0].Length > 8 ? (int)f[0][8] : 0)).ToArray()).ToArray();
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
                    int materialGroups = export.Sum(s => s.Select(f => f.Material).Distinct().Count());
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
                    if (points > 65536 || quantized.Length > 2047) throw new Exception("Stock fragment exceeds native preparation budgets");
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
    private static SliceVertex Vertex(double[] v) => new(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), v[6], v[7]);
}
