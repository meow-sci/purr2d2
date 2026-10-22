using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

internal static class TargetSelectionChecks
{
    private static void Main() { AssistedSliceSelection(); ContactEffectsChecks.Run(); Console.WriteLine("PASS: explicit part selection and commit guards"); }
    private static MeshReference NewMesh() => new() { PositionsCompare = [[new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)]] };
    private static void AssistedSliceSelection()
    {
        var f = new Fixture();
        // No Update/contact/heat or slicing-mode toggle is required by the explicit action.
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), 3, null, out var target, out var reason)
            && target!.Part == f.Hull, "explicit slice selects nearby visible hull without prior contact: " + reason);
        Check(f.SliceCalls == 0, "selection alone never mutates a vessel");
        var cut = f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, new(new(1, 0, 0), 0));
        Check(cut.Success && f.SliceCalls == 1 && f.Cutter.SlicedParts == 1,
            "explicit action invokes mesh slicing without any contact-cutting mode");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -8, 0), 3, null, out _, out _), "distant parts cannot be selected");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), 3, new Vehicle(), out _, out _),
            "selected vessel never falls back to a different nearby vessel");
        f.Target.Crew = [new IVASeat { AssignedKittenHash = new(1) }];
        Check(!f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, default).Success
            && f.SliceCalls == 1, "crew protection is rechecked after the swing");
        f.Target.Crew = [];
        f.Target.PositionEgo = new(10, 0, 0);
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, new(0, -1, 0), 3, out _), "target moving away cancels the pending cut");
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
        Check(transformed.Cutter.TryFindSliceTarget(transformed.Wielder, new(0, -1, 0), 3, transformed.Target, out var picked, out reason)
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
