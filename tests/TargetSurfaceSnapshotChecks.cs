using Brutal.Numerics;
using KSA;
using Purr2D2.Autopilot;
using Purr2D2.Cutting;

internal static class TargetSurfaceSnapshotChecks
{
    internal static void Run()
    {
        TransformedSubpartsAndBounds();
        ReusedMeshOnDifferentParts();
        RenderOnlyPrimitives();
        RenderGeometrySupersedesPicking();
        InvalidGeometryIsAtomic();
        WorkBudgetsAreAtomic();
        Console.WriteLine("PASS: target surface snapshots, transforms, completeness and budgets");
    }

    private static void TransformedSubpartsAndBounds()
    {
        var target = new Vehicle { CenterOfMassAsmb = new(8, 19, 27) };
        var full = new Part { Tree = target.Parts, PositionParentAsmb = new(10, 20, 30) };
        var sub = new Part
        {
            Tree = target.Parts, PartParent = full, PositionParentAsmb = new(3, 4, 5),
            Scale = new(2, 3, 4), Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2)
        };
        MeshReference mesh = Mesh();
        full.Modules.Add(new MeshViewModule(mesh));
        full.Modules.Add(RenderModel(mesh));
        sub.Modules.Add(new MeshViewModule(mesh));
        full.SubParts.Add(sub);
        // Some callers enumerate a subpart both in the full tree and through its parent.
        target.Parts.Parts.AddRange([full, sub, full]);
        Check(TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out var reason), reason);
        Check(triangles.Length == 2 && bounds.Length == 1, "part and per-part mesh references deduplicate");
        Near(triangles[0].A, new(2, 1, 3), "full-part COM-relative origin");
        Near(triangles[1].A, new(5, 5, 8), "subpart translation and COM subtraction");
        Near(triangles[1].B, new(5, 7, 8), "subpart scale and rotation");
        Near(triangles[1].C, new(2, 5, 8), "subpart scale and rotation on second axis");
        Near(bounds[0].Min, new(2, 1, 3), "bounds minimum includes full-part geometry");
        Near(bounds[0].Max, new(5, 7, 8), "bounds maximum includes transformed subpart geometry");
        Check(target.SplitCalls == 0 && target.FragmentCalls == 0, "geometry capture does not mutate native structure");
    }

    private static void ReusedMeshOnDifferentParts()
    {
        var target = new Vehicle();
        MeshReference mesh = Mesh();
        var first = new Part();
        var second = new Part { PositionParentAsmb = new(10, 0, 0) };
        first.Modules.Add(new MeshViewModule(mesh));
        second.Modules.Add(new MeshViewModule(mesh));
        target.Parts.Parts.AddRange([first, second]);
        Check(TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out var reason), reason);
        Check(triangles.Length == 2 && bounds.Length == 2, "shared meshes retain both physical instances");
        Near(triangles[1].A, new(10, 0, 0), "second mesh instance has its own transform");
    }

    private static void RenderOnlyPrimitives()
    {
        var target = new Vehicle();
        var full = new Part();
        var sub = new Part { PartParent = full };
        sub.Modules.Add(RenderModel(new()
        {
            PositionsCompare =
            [
                [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                [new(5, 0, 0), new(6, 0, 0), new(5, 1, 0)],
                // Degenerate art contributes no surface and must not create phantom bounds.
                [new(100, 0, 0), new(100, 0, 0), new(100, 0, 0)]
            ]
        }));
        full.SubParts.Add(sub);
        target.Parts.Parts.Add(full);
        Check(TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out var reason), reason);
        Check(triangles.Length == 2 && bounds.Length == 1, "all render-only primitives on a meshless parent are captured");
        Near(bounds[0].Max, new(6, 1, 0), "degenerate triangles do not enlarge candidate geometry");
    }

    private static void InvalidGeometryIsAtomic()
    {
        Reject(new() { PositionsCompare = null }, "missing CPU geometry");
        Reject(new() { PositionsCompare = [] }, "empty CPU geometry");
        Reject(new() { PositionsCompare = [null!] }, "null primitive");
        Reject(new() { PositionsCompare = [[new(0, 0, 0)]] }, "incomplete triangle");
        Reject(new() { PositionsCompare = [[new(double.NaN, 0, 0), new(1, 0, 0), new(0, 1, 0)]] }, "nonfinite vertex");
        Reject(new() { PositionsCompare = [[new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)]] }, "fully degenerate mesh");

        var target = Target();
        target.Parts.Parts[0].PositionParentAsmb = new(double.PositiveInfinity, 0, 0);
        Rejected(target, "invalid transform");
        target = Target();
        target.CenterOfMassAsmb = new(0, double.NaN, 0);
        Rejected(target, "invalid COM");
        target = Target();
        target.IsDisposed = true;
        Rejected(target, "disposed target");
        Rejected(new(), "target without surface geometry");

        void Reject(MeshReference invalid, string description)
        {
            var target = Target();
            // Valid geometry preceding the bad mesh must never escape as a partial snapshot.
            target.Parts.Parts[0].Modules.Add(RenderModel(invalid));
            Rejected(target, description);
        }
    }

    private static void RenderGeometrySupersedesPicking()
    {
        var target = new Vehicle();
        var full = new Part();
        full.Modules.Add(new MeshViewModule(new() { PositionsCompare = [] }));
        full.Modules.Add(new MeshViewModule(null!));
        // A coarse picking mesh must not inflate the visible geometry or candidate bounds.
        full.Modules.Add(new MeshViewModule(new()
        {
            PositionsCompare = [[new(100, 0, 0), new(101, 0, 0), new(100, 1, 0)]]
        }));
        full.Modules.Add(RenderModel(Mesh()));
        target.Parts.Parts.Add(full);
        Check(TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out var reason), reason);
        Check(triangles.Length == 1 && bounds.Length == 1,
            "available render geometry permits empty, missing and coarse optional picking meshes");
        Near(bounds[0].Max, new(1, 1, 0), "render geometry determines the actual hull bounds");

        target = Target();
        target.Parts.Parts[0].Modules.Add(new PartModelModule
        {
            PartModel = new() { Template = new() { Mesh = null } }
        });
        Check(TargetSurfaceSnapshot.TryCapture(target, out triangles, out bounds, out reason), reason);
        Check(triangles.Length == 1 && bounds.Length == 1, "picking geometry remains the fallback when no render mesh is declared");
    }

    private static void WorkBudgetsAreAtomic()
    {
        var target = Target();
        for (int i = 0; i < 256; i++) target.Parts.Parts.Add(new Part());
        Rejected(target, "part budget");

        target = Target();
        for (int i = 0; i < 1024; i++) target.Parts.Parts[0].Modules.Add(new MeshViewModule(Mesh()));
        Rejected(target, "mesh budget");

        var primitives = new double3[8193][];
        for (int i = 0; i < primitives.Length; i++) primitives[i] = [];
        primitives[0] = Mesh().PositionsCompare![0];
        target = Target();
        target.Parts.Parts[0].Modules.Add(RenderModel(new() { PositionsCompare = primitives }));
        Rejected(target, "primitive budget including empty primitives");

        target = Target();
        target.Parts.Parts[0].Modules.Add(RenderModel(new()
        {
            PositionsCompare = [new double3[150_001 * 3]]
        }));
        Rejected(target, "triangle budget including degenerate triangles");
    }

    private static Vehicle Target()
    {
        var target = new Vehicle();
        var full = new Part();
        full.Modules.Add(new MeshViewModule(Mesh()));
        target.Parts.Parts.Add(full);
        return target;
    }

    private static MeshReference Mesh() => new()
    {
        PositionsCompare = [[new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]]
    };

    private static PartModelModule RenderModel(MeshReference mesh) => new()
    {
        PartModel = new() { Template = new() { Mesh = mesh } }
    };

    private static void Rejected(Vehicle target, string description)
    {
        Check(!TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out var reason), description);
        Check(triangles.Length == 0 && bounds.Length == 0 && reason.Length > 0,
            description + " returns only a reason and no partial geometry");
    }

    private static void Near(CutPoint actual, CutPoint expected, string description) =>
        Check((actual - expected).Length < 1e-9, description);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
