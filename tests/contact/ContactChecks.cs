using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

internal static class ContactChecks
{
    private static readonly double3[] Triangle = [new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)];

    private static void Main()
    {
        StationaryContact();
        SweptContact();
        NarrowPhaseMiss();
        ScarThrottleKeepsContactStatus();
        foreach (var condition in new[] { "crew", "no bubble", "different parent" })
            foreach (var mode in Enum.GetValues<CutMode>()) ProtectedTargetStillScorches(condition, mode);
        foreach (var mode in Enum.GetValues<CutMode>()) DestructionPositiveControl(mode);
        TransformedSubpart();
        RenderOnlyMesh();
        RenderFallbackAfterEmptyContactMesh();
        DeferredMeshLoad();
        DuplicateMeshMiss();
        StockSizedContactScan();
        BudgetWarningPreservesCutRefusal();
        Console.WriteLine("PASS: production SaberCutter contact integration checks");
    }

    private static void StationaryContact()
    {
        var f = new Fixture();
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 1 && f.Cutter.TriangleTests > 0 && f.Scars.Count == 1,
            "stationary blade hits an uncrewed vessel through production Update");
        Check(f.Cutter.HottestContactSeconds > 0, "detected contact accumulates heat");
        Check(f.Cutter.VehiclesInRange == 1 && f.Cutter.CandidateParts == 1 && f.Cutter.TestedParts == 1
            && f.Cutter.MeshesTested == 1 && f.Cutter.MissingMeshes == 0 && f.Cutter.TriangleTests == 1,
            "contact diagnostics account for the tested vessel, part, mesh and triangle");
    }

    private static void SweptContact()
    {
        var f = new Fixture();
        f.Step(new(0, -.3, 0), new(0, -.2, 0));
        Check(f.Cutter.Contacts == 0, "sweep begins clear of the hull");
        f.Step(new(0, .2, 0), new(0, .3, 0));
        Check(f.Cutter.Contacts == 1 && f.Scars.Count == 1,
            "motion between two clear blade samples strikes the hull");
    }

    private static void NarrowPhaseMiss()
    {
        var f = new Fixture();
        // This crosses the triangle's AABB, outside its slanted edge: the broadphase
        // must run but neither endpoint nor the intervening sweep touches the face.
        f.Step(new(.9, -.2, .9), new(.9, .2, .9));
        f.Step(new(.8, -.2, .9), new(.8, .2, .9));
        Check(f.Cutter.TriangleTests > 0 && f.Cutter.Contacts == 0 && f.Scars.Count == 0
            && f.Cutter.HottestContactSeconds == 0, "triangle narrowphase rejects a genuine nearby miss");
        f.AssertUnchanged("clear blade");
    }

    private static void ScarThrottleKeepsContactStatus()
    {
        var f = new Fixture();
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        var firstStatus = f.Cutter.Status;
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Scars.Count == 1, "scar emission is throttled within forty milliseconds");
        Check(f.Cutter.Contacts == 1 && f.Cutter.Status == firstStatus
            && f.Cutter.Status.Contains("Heating", StringComparison.OrdinalIgnoreCase),
            "contact status persists on a frame where scar emission is throttled");
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Scars.Count == 2, "scar emission resumes after the throttle interval");
    }

    private static void ProtectedTargetStillScorches(string condition, CutMode mode)
    {
        var f = new Fixture();
        if (condition == "crew") f.Target.Crew = [new() { AssignedKittenHash = new(7) }];
        if (condition == "no bubble") f.Target.HasPhysicsBubble = false;
        if (condition == "different parent") f.Target.Parent = new();
        f.Configure(mode);
        string? refusal = null;
        for (int i = 0; i < 5; i++)
        {
            f.Step(new(0, -.2, 0), new(0, .2, 0));
            Check(f.Cutter.Contacts == 1, $"{condition}, {mode}: protected vessel remains detectable at step {i}");
            if (i == 2) refusal = f.Cutter.Status;
            if (i > 2) Check(f.Cutter.Status == refusal,
                $"{condition}, {mode}: structural refusal persists while contact continues");
        }
        Check(f.Scars.Count >= 2 && f.Cutter.HottestContactSeconds >= .09,
            $"{condition}, {mode}: sustained contact emits scars and accumulates heat");
        string reason = condition switch { "crew" => "Crew", "no bubble" => "physics bubble", _ => "parent body" };
        Check(f.Cutter.Status.Contains(reason, StringComparison.OrdinalIgnoreCase),
            $"{condition}, {mode}: status explains why structural cutting was refused");
        f.AssertUnchanged($"{condition}, {mode}");
    }

    private static void DestructionPositiveControl(CutMode mode)
    {
        var f = new Fixture();
        f.Target.Crew = [new() { AssignedKittenHash = KeyHash.Zero }];
        f.Configure(mode);
        for (int i = 0; i < 3; i++) f.Step(new(0, -.2, 0), new(0, .2, 0));
        int calls = mode switch
        {
            CutMode.Boundary => f.Target.SplitCalls,
            CutMode.Fragment => f.Target.FragmentCalls,
            _ => f.SliceCalls,
        };
        Check(calls == 1, $"{mode}: unprotected positive control reaches the destructive host operation");
    }

    private static void TransformedSubpart()
    {
        var f = new Fixture(addMesh: false);
        f.Target.PositionEgo = new(7, -2, 3);
        f.Target.CenterOfMassAsmb = new(.3, -.2, .1);
        f.Target.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .43);
        f.Wielder.PositionEgo = new(6, -1, 4);
        f.Wielder.Body2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitX, -.29);
        f.Hull.Scale = new(1.2, .8, 1.1);
        f.Hull.Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitY, .61);
        f.Hull.PositionParentAsmb = new(.3, -.4, .2);
        var surface = new Part
        {
            PartParent = f.Hull, Tree = f.Target.Parts,
            Scale = new(.6, 1.5, .9),
            Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitZ, -.36),
            PositionParentAsmb = new(.5, .2, -.3),
        };
        surface.Modules.Add(new MeshViewModule(NewMesh()));
        f.Hull.SubParts.Add(surface);

        // Derive blade inputs independently from authored fixture poses. Both the
        // moving wielder BODY frame and target COM offset participate in the test.
        var localToAssembly = double4x4.CreateScale(.6, 1.5, .9) * double4x4.CreateRotationZ(-.36)
            * double4x4.CreateTranslation(.5, .2, -.3)
            * double4x4.CreateScale(1.2, .8, 1.1) * double4x4.CreateRotationY(.61)
            * double4x4.CreateTranslation(.3, -.4, .2);
        var assemblyToEgo = double4x4.CreateRotationZ(.43)
            * double4x4.CreateTranslation(f.Target.PositionEgo - f.Target.CenterOfMassAsmb.Transform(f.Target.Asmb2Ego));
        var bodyToEgo = double4x4.CreateRotationX(-.29) * double4x4.CreateTranslation(f.Wielder.PositionEgo);
        Check(double4x4.Invert(bodyToEgo, out var egoToBody), "fixture body transform is invertible");
        var localToBody = localToAssembly * assemblyToEgo * egoToBody;
        f.Step(new double3(.15, -.2, .1).Transform(localToBody), new double3(.15, .2, .1).Transform(localToBody));
        Check(f.Cutter.Contacts == 1 && f.Scars.Count == 1, "rotated, translated, nonuniformly scaled subpart is hit");
        var scar = f.Scars.Single();
        Check(ReferenceEquals(scar.Surface, surface) && (scar.End - new double3(.15, 0, .1)).Length() < 1e-8
            && (scar.Normal + double3.UnitY).Length() < 1e-8,
            "scar identifies the struck subpart and preserves its local hit and normal");

        f.Cutter.Suspend();
        f.Step(new double3(2, -.2, .1).Transform(localToBody), new double3(2, .2, .1).Transform(localToBody));
        Check(f.Cutter.Contacts == 0 && f.Scars.Count == 1, "transformed blade beyond the subpart misses");
    }

    private static void RenderOnlyMesh()
    {
        var f = new Fixture(addMesh: false);
        f.Hull.Modules.Add(RenderModel(NewMesh()));
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 1 && f.Scars.Count == 1, "render-only PartModel geometry participates in contact");
    }

    private static void RenderFallbackAfterEmptyContactMesh()
    {
        var f = new Fixture(addMesh: false);
        f.Hull.Modules.Add(new MeshViewModule(new() { PositionsCompare = [] }));
        f.Hull.Modules.Add(RenderModel(NewMesh()));
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 1 && f.Scars.Count == 1, "empty MeshView geometry falls back to the rendered hull");
        Check(f.Cutter.MissingMeshes == 1 && f.Cutter.MeshesTested == 1,
            "fallback diagnostics identify unavailable picking geometry and tested render geometry");
    }

    private static void DeferredMeshLoad()
    {
        var f = new Fixture(addMesh: false);
        var mesh = new MeshReference();
        f.Hull.Modules.Add(new MeshViewModule(mesh));
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 0 && f.Cutter.MissingMeshes == 1 && f.Cutter.MeshesTested == 0,
            "unavailable CPU geometry is reported without authorizing contact");
        mesh.PositionsCompare = [Triangle.ToArray()];
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 1 && f.Scars.Count == 1 && f.Cutter.MissingMeshes == 0,
            "CPU geometry loaded on the next step is retried instead of cached as missing");
    }

    private static void DuplicateMeshMiss()
    {
        var f = new Fixture(addMesh: false);
        var mesh = NewMesh();
        f.Hull.Modules.Add(new MeshViewModule(mesh));
        f.Hull.Modules.Add(RenderModel(mesh));
        f.Step(new(.9, -.2, .9), new(.9, .2, .9));
        Check(f.Cutter.Contacts == 0 && f.Cutter.MeshesTested == 1 && f.Cutter.TriangleTests == 1,
            "a shared render and picking mesh is tested only once even when every triangle misses");
    }

    private static MeshReference NewMesh() => new() { PositionsCompare = [Triangle.ToArray()] };

    private static void StockSizedContactScan()
    {
        var f = new Fixture(addMesh: false);
        // Match the reported Gemini7 candidate count. Give each distant surface
        // its own mesh reference to exceed both the old part and mesh-cache caps.
        for (int i = 0; i < 623; i++)
        {
            var miss = new Part { Tree = f.Target.Parts };
            miss.Modules.Add(new MeshViewModule(new()
            {
                PositionsCompare = [Triangle.Select(p => p + new double3(10, 0, 0)).ToArray()]
            }));
            f.Target.Parts.Parts.Add(miss);
        }
        var hit = new Part { Tree = f.Target.Parts, PositionParentAsmb = new(1, 0, 0) };
        hit.Modules.Add(new MeshViewModule(new()
        {
            PositionsCompare = [Triangle.Select(p => p - new double3(1, 0, 0)).ToArray()]
        }));
        f.Target.Parts.Parts.Add(hit);
        // Duplicate enumeration must not spend budget or advance heat twice.
        f.Target.Parts.Parts.Add(hit);
        for (int i = 0; i < 3; i++) f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.CandidateParts == 624 && f.Cutter.TestedParts == 624 && f.Cutter.MeshesTested == 624,
            "stock-sized contact scan covers all 624 unique candidates and meshes");
        Check(!f.Cutter.BudgetLimited && f.Cutter.Contacts == 1 && f.Scars.All(s => ReferenceEquals(s.Surface, hit)),
            "contact beyond both former budgets is detected without a scan-limit warning");
        Check(Math.Abs(f.Cutter.HottestContactSeconds - .06) < 1e-9, "duplicate enumeration cannot double heat accumulation");
    }

    private static void BudgetWarningPreservesCutRefusal()
    {
        var f = new Fixture();
        f.Target.Crew = [new() { AssignedKittenHash = new(7) }];
        f.Configure(CutMode.Boundary);
        var oversized = new Part { Tree = f.Target.Parts };
        oversized.Modules.Add(new MeshViewModule(new() { PositionsCompare = [new double3[500_001 * 3]] }));
        f.Target.Parts.Parts.Add(oversized);
        for (int i = 0; i < 5; i++) f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.Cutter.BudgetLimited && f.Cutter.Contacts == 1 && f.Cutter.Status.Contains("Crew aboard")
            && f.Cutter.Status.Contains("Contact scan limited"), "scan warnings preserve the actual structural-cut refusal");
        Check(f.Target.SplitCalls == 0 && f.Target.FragmentCalls == 0 && f.SliceCalls == 0,
            "increased scan budgets do not bypass crew protection");
    }
    private static PartModelModule RenderModel(MeshReference mesh) => new()
    {
        PartModel = new() { Template = new() { Mesh = mesh } },
    };

    private enum CutMode { Boundary, Fragment, Slice }
    private readonly record struct Scar(Part Surface, double3 End, double3 Normal);

    private sealed class Fixture
    {
        public readonly KittenEva Wielder = new();
        public readonly Vehicle Target = new();
        public readonly Part Root = new() { DisplayName = "root" };
        public readonly Part Hull = new();
        public readonly SaberCutter Cutter = new() { ContactSeconds = 100 };
        public readonly List<Scar> Scars = [];
        public int SliceCalls;
        private readonly Part.Connection _boundary;
        private double _now = 1;

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
            Cutter.Scar = (surface, start, end, normal, now) => Scars.Add(new(surface, end, normal));
            Cutter.SliceHandler = (part, plane) => { SliceCalls++; return new(true, "fixture slice"); };
        }

        public void Configure(CutMode mode)
        {
            Cutter.ContactSeconds = .05;
            Cutter.FragmentStruckPart = mode == CutMode.Fragment;
            Cutter.ArbitrarySlicing = mode == CutMode.Slice;
        }

        public void Step(double3 a, double3 b)
        {
            Cutter.Update(Wielder, a, b, .02, _now);
            _now += .02;
        }

        public void AssertUnchanged(string context)
        {
            Check(Target.SplitCalls == 0 && Target.FragmentCalls == 0 && SliceCalls == 0
                && Cutter.SeveredConnections == 0 && Cutter.FragmentedParts == 0 && Cutter.SlicedParts == 0,
                $"{context}: no destructive operation is invoked");
            Check(Target.Parts.Parts.Count == 2 && Hull.Tree == Target.Parts && Root.Tree == Target.Parts
                && Hull.TreeParent == Root && Root.TreeChildren.SequenceEqual([Hull])
                && Root.Connections.SequenceEqual([_boundary]) && Hull.Connections.SequenceEqual([_boundary]),
                $"{context}: vessel membership and connection graph remain intact");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Contact regression failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
