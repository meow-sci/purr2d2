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
        // Reproduce a float-collapsed cap sliver: one triangle has a split edge,
        // while its neighbour still has the original long edge (a T-junction).
        var original = solid[0]; var face = original[0]; var midpoint = SliceVertex.Lerp(face.A, face.B, .5);
        SliceFace[] junction = [new(face.A, midpoint, face.C, face.Material), new(midpoint, face.B, face.C, face.Material), ..original.Skip(1)];
        var properties = PlaneSlicer.Properties(original);
        var junctionPiece = new SlicePiece(junction, properties.Volume, properties.Centroid, properties.Inertia, true);
        Check(!PlaneSlicer.TryAudit(junction, out _), "precision fixture starts with a real boundary T-junction");
        Check(SliceMeshPrecision.TryPrepare(junctionPiece, double4x4.Identity, out var repaired, out _, out reason)
            && PlaneSlicer.TryAuditCompound(repaired, out _, out _), "float export stitches collapsed edges into a closed surface: " + reason);
        Check(!SliceMeshPrecision.TryPrepare(junctionPiece with { Faces = original.Skip(1).ToArray() }, double4x4.Identity, out _, out _, out _),
            "precision repair refuses a missing surface instead of filling arbitrary holes");
        Check(SliceShellBuilder.TryPrepare([junction], out var repairedInput, out reason, generatedClosedGeometry: true)
            && PlaneSlicer.TryAuditCompound(repairedInput, out var repairedVolume, out _) && Math.Abs(repairedVolume - properties.Volume) < 1e-10,
            "generated input reuses closed-edge repair without adding backing: " + reason);
        var translated = junction.Select(f => new SliceFace(Move(f.A), Move(f.B), Move(f.C), f.Material)).ToArray();
        SliceVertex Move(SliceVertex v) => v with { Position = v.Position + new CutPoint(100_000, -200_000, 300_000) };
        Check(SliceShellBuilder.TryPrepare([translated], out var localRepair, out reason, generatedClosedGeometry: true)
            && PlaneSlicer.TryAuditCompound(localRepair, out var movedVolume, out _) && Math.Abs(movedVolume - properties.Volume) < 1e-9,
            "translated generated T-junction repair uses bounded local tolerance and double positions: " + reason);
        Check(!SliceShellBuilder.TryPrepare([panel], out _, out reason, generatedClosedGeometry: true)
            && reason.Contains("Generated solid"), "damaged generated geometry is never reinterpreted as an authored panel");
        foreach (var invalid in new[] { original.Skip(1).ToArray(), original.Concat([original[0]]).ToArray(), original.Concat(original).ToArray() })
            Check(!SliceShellBuilder.TryPrepare([invalid], out _, out _, generatedClosedGeometry: true),
                "generated closed-input repair refuses holes, nonmanifold edges and duplicate shells");
        var collapsed = original.Select(f => new SliceFace(Flatten(f.A), Flatten(f.B), Flatten(f.C), f.Material)).ToArray();
        SliceVertex Flatten(SliceVertex v) => v with { Position = new(v.Position.X, v.Position.Y, 1e-50 * v.Position.Z) };
        Check(!SliceMeshPrecision.TryPrepare(junctionPiece with { Faces = collapsed }, double4x4.Identity, out _, out _, out _),
            "irrecoverable float32 thickness collapse refuses publication");
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
