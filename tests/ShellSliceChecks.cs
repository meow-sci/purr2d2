using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

internal static class ShellSliceChecks
{
    internal static void Run()
    {
        SliceVertex Vertex(double x, double y) => new(new(x, y, 0), new(0, 0, 1), x, y);
        var a = Vertex(0, 0); var b = Vertex(1, 0); var c = Vertex(1, 1); var d = Vertex(0, 1);
        SliceFace[] panel = [new(a, b, c, 5), new(a, c, d, 5)];
        Check(SliceShellBuilder.TryPrepare([panel], out var solid, out var reason), "open textured panel acquires a closed backing: " + reason);
        Check(solid[0].Contains(panel[0]) && solid[0].Contains(panel[1]), "backing preserves exterior vertices, normals, UVs and material");
        Check(PlaneSlicer.TrySliceCompound(solid, new(new(1, 0, 0), .5), out var result, out reason), "open panel becomes two capped pieces: " + reason);
        Check(result!.Pieces.All(p => p.Faces.Any(f => f.Material == -1)), "both sides expose a metal cutting rim");
        Check(Math.Abs(result.SourceVolume - SliceShellBuilder.WallThickness) < 1e-8, "panel backing volume equals area times wall thickness");
        foreach (var piece in result.Pieces)
        {
            Check(SlicePrincipalFrame.TryCreate(piece, out var frame, out reason), "thin shell has finite positive inertia");
            Check(SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var exported, out _, out reason), "thin shell survives native precision: " + reason);
            Check(PlaneSlicer.TryAuditCompound(exported, out _, out _), "exported shell remains closed");
        }
        Check(SliceShellBuilder.TryPrepare(solid, out var unchanged, out reason) && ReferenceEquals(unchanged[0], solid[0]),
            "existing closed geometry is preserved without adding a second wall");
        Check(SliceShellBuilder.TryPrepare([panel.Concat(panel).ToArray()], out var deduplicated, out reason)
            && Math.Abs(PlaneSlicer.Properties(deduplicated[0]).Volume - SliceShellBuilder.WallThickness) < 1e-8,
            "duplicate authored sheet faces do not produce doubled mass or non-manifold edges");
        SliceFace[] touching = [..panel, new(Vertex(1, 1), Vertex(2, 1), Vertex(2, 2), 6), new(Vertex(1, 1), Vertex(2, 2), Vertex(1, 2), 6)];
        Check(SliceShellBuilder.TryPrepare([touching], out var separate, out reason)
            && separate.All(s => PlaneSlicer.TryAudit(s, out _)), "panels touching only at a vertex form independent closed shells");
        var damaged = panel.Select(f => f with { A = f.A with { Position = new(double.NaN, 0, 0) } }).ToArray();
        Check(!SliceShellBuilder.TryPrepare([damaged], out _, out _), "nonfinite art is refused before asset creation");
        Console.WriteLine("PASS: open shell preparation and native mesh precision checks");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Shell regression: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
