using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

internal static class TargetSelectionChecks
{
    private static void Main() { AssistedSliceSelection(); BladeReach(); PointerOcclusion(); IncrementalSelection(); ContactEffectsChecks.Run(); Console.WriteLine("PASS: explicit part selection and commit guards"); }
    private static MeshReference NewMesh() => new() { PositionsCompare = [[new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)]] };
    private static void BladeReach()
    {
        var f = new Fixture();
        var hilt = new double3(0, -.25, 0);
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, .25, out var target, out _),
            "a selected surface exactly one blade length from the hilt is reachable");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -.2501, 0), .25, out _, out _),
            "short blade has no minimum range or extra reach allowance");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -2, 0), 1, out _, out _)
            && f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -2, 0), 2, out _, out _),
            "increasing actual blade length makes a farther surface reachable");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, .125, out _, out _),
            "a partially extended blade cannot use its configured full reach");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, 0, out _, out _)
            && !f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, double.NaN, out _, out _),
            "retracted or invalid blade length cannot select a part");
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, hilt, .25, out _, out _)
            && !f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, new(0, -.2501, 0), .25, out _, out _),
            "fallback picking uses the same hilt-to-surface range independently of camera distance");
        Check(!f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -.3, 0), .25, default).Success && f.SliceCalls == 0,
            "movement beyond reach before the stroke cannot trigger a cut");

        f.Wielder.PositionEgo = new(1000, 2, -3000);
        f.Target.PositionEgo = new(1000, 0, -3000);
        f.Wielder.Body2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2);
        var rotatedHilt = new double3(-1.75, 0, 0);
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, f.Target.PositionEgo, rotatedHilt, .251, out _, out _)
            && f.Cutter.TryFindSliceTarget(f.Wielder, f.Target.PositionEgo - double3.UnitY * 80,
                double3.UnitY, rotatedHilt, .251, out _, out _),
            "rotated hilt determines reach even when the kitten centre is farther than blade length");
    }
    private static void PointerOcclusion()
    {
        var fixture = new Fixture();
        fixture.Wielder.PositionEgo = new(0, 2, 0);
        var rear = new Part { Tree = fixture.Target.Parts, PositionParentAsmb = new(0, 1, 0) };
        rear.Modules.Add(new MeshViewModule(NewMesh()));
        fixture.Target.Parts.Parts.Add(rear);
        Check(fixture.Cutter.TryFindSliceTarget(fixture.Wielder, new(0, -10, 0), double3.UnitY, default, 3, out var picked, out _)
            && picked!.Part == fixture.Hull, "cursor chooses the front surface even when another part is closer to the kitten");
        Check(fixture.Cutter.TryPointTarget(fixture.Wielder, new Part { PartParent = rear }, new(0, 1, 0), default, 3, out picked, out _)
            && picked!.Part == rear, "native subpart hover selects its full structural part");
    }
    private static void IncrementalSelection()
    {
        var fixture = new Fixture(addMesh: false);
        var vertices = new double3[30_000];
        for (int i = 0; i < vertices.Length; i += 3)
        { vertices[i] = new(-1, 0, -1); vertices[i + 1] = new(1, 0, -1); vertices[i + 2] = new(0, 0, 1); }
        fixture.Hull.Modules.Add(new MeshViewModule(new() { PositionsCompare = [vertices] }));
        using var selection = fixture.Cutter.BeginSelection(fixture.Wielder, new(0, -1, 0), double3.UnitY, default, 3);
        selection.Advance();
        Check(!selection.Done && fixture.SliceCalls == 0, "detailed target search yields before scanning an entire mesh");
        int updates = 1;
        while (!selection.Done && updates++ < 200) selection.Advance();
        Check(selection.Done && selection.Target?.Part == fixture.Hull && updates >= 5 && fixture.SliceCalls == 0,
            "target search resumes across frames and cannot cut while preparing");
        fixture.Target.IsDisposed = true;
        Check(!fixture.Cutter.CheckSliceTarget(fixture.Wielder, selection.Target!, new(0, -1, 0), 3, out _),
            "disposing a target invalidates the prepared selection");
    }
    private static void AssistedSliceSelection()
    {
        var f = new Fixture();
        // No Update/contact/heat or slicing-mode toggle is required by the explicit action.
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), double3.UnitY, default, 3, out var target, out var reason)
            && target!.Part == f.Hull, "cursor ray selects the visible hull without prior contact: " + reason);
        Check(f.SliceCalls == 0, "selection alone never mutates a vessel");
        var cut = f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, new(new(1, 0, 0), 0));
        Check(cut.Success && f.SliceCalls == 1 && f.Cutter.SlicedParts == 1,
            "explicit action invokes mesh slicing without any contact-cutting mode");
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, default, 3, out _, out _),
            "zooming the camera out does not change reach from the kitten");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(8, -1, 0), double3.UnitY, default, 3, out _, out _),
            "clicking empty space never falls back to a nearby part");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), -double3.UnitY, default, 3, out _, out _),
            "a part behind the camera ray cannot be selected");
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, default, 3, out var hovered, out _) && hovered!.Part == f.Hull,
            "native hover can lock the exact clicked part without scanning");
        f.Target.Crew = [new IVASeat { AssignedKittenHash = new(1) }];
        Check(!f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, default).Success
            && f.SliceCalls == 1, "crew protection is rechecked after the swing");
        f.Target.Crew = [];
        f.Target.PositionEgo = new(10, 0, 0);
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, default, 3, out _), "out-of-reach click is refused");
        Check(f.Cutter.CheckSliceTarget(f.Wielder, target!, default, double.PositiveInfinity, out _), "moving after an in-range stroke does not cancel the pending result");
        f.Target.PositionEgo = default;
        f.Hull.PositionParentAsmb = new(0, 1, 0);
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, new(0, -1, 0), 3, out _), "part transform changes invalidate the prepared plane");
        f.Hull.PositionParentAsmb = default;
        f.Hull.Tree = new Vehicle().Parts;
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, new(0, -1, 0), 3, out _), "reparented part cannot use the old vessel plane");

        var transformed = new Fixture(addMesh: false);
        transformed.Target.PositionEgo = new(1000, 2000, -3000);
        transformed.Wielder.PositionEgo = transformed.Target.PositionEgo;
        transformed.Target.CenterOfMassAsmb = new(.2, .4, -.7);
        transformed.Target.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .6);
        var sub = new Part { PartParent = transformed.Hull, Tree = transformed.Target.Parts, PositionParentAsmb = new(.1, .2, .3) };
        sub.Modules.Add(new PartModelDynamicModule { PartModelDynamic = new() { Template = new() { Mesh = NewMesh() } } });
        transformed.Hull.SubParts.Add(sub);
        var matrix = sub.MatrixAsmb2Ego(transformed.Target.GetMatrixAsmb2Ego(KSA.Program.Camera!));
        var point = double3.Zero.Transform(matrix);
        var direction = double3.TransformNormal(double3.UnitY, matrix).NormalizeOrZero();
        Check(transformed.Cutter.TryFindSliceTarget(transformed.Wielder, point - direction * 10, direction, default, 3, out var picked, out reason)
            && picked!.Part == transformed.Hull && transformed.Cutter.CheckSliceTarget(transformed.Wielder, picked, new(0, -1, 0), 3, out _),
            "transformed render-only tank subpart resolves to the full part in assembly coordinates: " + reason);
    }

    private sealed class Fixture
    {
        public readonly KittenEva Wielder = new();
        public readonly Vehicle Target = new();
        public readonly Part Root = new() { DisplayName = "root" };
        public readonly Part Hull = new();
        public readonly PartSliceTargeting Cutter = new();
        public int SliceCalls;
        private readonly Part.Connection _boundary;

        public Fixture(bool addMesh = true)
        {
            KSA.Program.EditorFlag = false;
            KSA.Program.Camera = new();
            Target.Parent = Wielder.Parent;
            Root.Tree = Hull.Tree = Target.Parts;
            Target.Parts.Parts.AddRange([Root, Hull]);
            Root.TreeChildren.Add(Hull);
            Hull.TreeParent = Root;
            _boundary = new(Root, Hull);
            Root.Connections.Add(_boundary);
            Hull.Connections.Add(_boundary);
            if (addMesh) Hull.Modules.Add(new MeshViewModule(NewMesh()));
            Universe.CurrentSystem = new();
            Universe.CurrentSystem.All.Items = [Wielder, Target];
            Cutter.SliceHandler = (part, plane) => { SliceCalls++; return new(true, "fixture slice"); };
        }

    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Target selection regression failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
