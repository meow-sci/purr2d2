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
        DynamicAndGlassContact();
        RenderFallbackAfterEmptyContactMesh();
        DeferredMeshLoad();
        DuplicateMeshMiss();
        StockSizedContactScan();
        BudgetWarningPreservesCutRefusal();
        RingCoverageKeepsGaps();
        FiveMinuteRingCut();
        FiveMinuteRingCut(5.0 / 60);
        FiveMinuteRingCut(.25);
        LongBladeContact();
        DeliberateSwingCuts();
        RingCutRefusals();
        RingCommitFailureAndNativeModes();
        Console.WriteLine("PASS: production SaberCutter contact integration checks");
    }

    private static void RingCoverageKeepsGaps()
    {
        var ring = new RingCutCoverage(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), 1);
        for (int i = 0; i <= 720; i++)
        {
            double angle = i * Math.PI / 360;
            if (angle > 1 && angle < 1.4) { ring.BreakStroke(); continue; }
            ring.Add(new(0, Math.Cos(angle), Math.Sin(angle)));
        }
        Check(!ring.Complete && ring.Progress > .9 && ring.Progress < .97,
            "a complete orbit with a missed hull sector does not count as a completed ring cut");
        double before = ring.Progress;
        Check(!ring.Add(new(.3, 1, 0)) && ring.Progress == before,
            "contact on a different axial plane does not fill the selected cut");
        ring.BreakStroke();
        for (int i = 100; i <= 170; i++)
        { double angle = i * Math.PI / 360; ring.Add(new(0, Math.Cos(angle), Math.Sin(angle))); }
        Check(ring.Complete, "returning to the missed sector completes retained coverage across stroke breaks");
        var jump = new RingCutCoverage(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), 1);
        jump.Add(new(0, 1, 0)); jump.Add(new(0, -1, 0));
        Check(jump.Progress < .04, "a contact teleport cannot invent a half-ring cut");
    }

    private static void FiveMinuteRingCut(double dt = .02)
    {
        var f = new Fixture(addMesh: false);
        f.Configure(CutMode.Slice);
        f.Hull.Modules.Add(new MeshViewModule(CylinderMesh()));
        f.Target.CenterOfMassAsmb = new(.2, .3, -.4);
        double3 center = new double3(.4, 0, 0) - f.Target.CenterOfMassAsmb;
        int checks = 0;
        SlicePlane checkedPlane = default, committedPlane = default;
        f.Cutter.SliceCheckHandler = (_, plane) => { checks++; checkedPlane = plane; return new(true, "supported"); };
        f.Cutter.SliceHandler = (_, plane) => { f.SliceCalls++; committedPlane = plane; return new(true, "fixture slice"); };
        f.Cutter.BeginRingCut(f.Wielder, f.Target, center, double3.UnitX, double3.UnitY, 1);
        int steps = (int)Math.Ceiling(300 / dt);
        for (int i = 0; i <= steps; i++)
        {
            double angle = i * 2 * Math.PI / steps;
            double3 radial = new(0, Math.Cos(angle), Math.Sin(angle));
            if (i == steps / 2) f.Cutter.Suspend();
            f.Step(center + radial * 1.2, center + radial * .8, dt);
        }
        Check(f.Cutter.RingFailure == null && f.Cutter.RingProgress > .999999,
            $"five-minute production contact lap at {dt:F3} s/frame retains its whole cut, including across suspension");
        Check(checks == 1 && f.SliceCalls == 0,
            "ring geometry validates once on initial contact and ordinary heat cannot slice early");
        var result = f.Cutter.FinishRingCut(302);
        Check(result.Success && f.SliceCalls == 1 && f.Cutter.SlicedParts == 1,
            "closing a fully contacted ring invokes exactly one structural slice");
        Check(checkedPlane == committedPlane && Math.Abs(committedPlane.Offset - .4) < 1e-9
            && (committedPlane.Normal - new CutPoint(1, 0, 0)).Length < 1e-9,
            "the selected ring plane is stable and converts target COM coordinates to assembly coordinates");
        Check(!f.Cutter.FinishRingCut(303).Success && f.SliceCalls == 1,
            "a consumed ring cut cannot invoke the slicer twice");
    }

    private static void LongBladeContact()
    {
        var f = new Fixture();
        f.Target.BoundingSphereRadiusBody = 1.5;
        // Both the old 3 m segment cap and the old 4 m midpoint broadphase
        // would silently discard this real contact near the tip of a 12 m blade.
        f.Step(new(0, -11.8, 0), new(0, .2, 0));
        Check(f.Cutter.Contacts == 1, "12 m blade reaches a small hull near its tip");
        f.Step(new(0, -11.8, 0), new(2, .03216, 0));
        Check(f.Cutter.DiscontinuousParts == 0 && f.Cutter.Contacts == 1,
            "long blade sweep accepts ordinary angular travel without increasing the hilt teleport allowance");
        f.Target.BoundingSphereRadiusBody = 20;
        f.Step(new(5, -11.8, 0), new(7, .03216, 0));
        Check(f.Cutter.DiscontinuousParts > 0 && f.Cutter.Contacts == 0, "translating a long blade five metres is still a discontinuity");
    }

    private static void DynamicAndGlassContact()
    {
        foreach (bool glass in new[] { false, true })
        {
            var f = new Fixture(addMesh: false);
            var model = new PartModel { Template = new() { Mesh = NewMesh() } };
            if (glass) f.Hull.Modules.Add(new PartModelGlassModule { PartModelGlass = model });
            else f.Hull.Modules.Add(new PartModelDynamicModule { PartModelDynamic = model });
            f.Step(new(0, -.2, 0), new(0, .2, 0));
            Check(f.Cutter.Contacts == 1, $"{(glass ? "glass" : "dynamic")} render geometry supports cutting without a picking mesh");
        }
    }

    private static void DeliberateSwingCuts()
    {
        var f = new Fixture();
        f.Cutter.ArbitrarySlicing = true; f.Cutter.SwingCut = true; f.Cutter.ContactSeconds = .65;
        f.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(f.SliceCalls == 0, "swing command alone does not bypass the need for moving blade contact");
        f.Step(new(.02, -.2, 0), new(.02, .2, 0));
        f.Step(new(.04, -.2, 0), new(.04, .2, 0));
        Check(f.SliceCalls == 1, "deliberate moving swing cuts without a stationary 0.65 second dwell");
        var held = new Fixture();
        held.Cutter.ArbitrarySlicing = true; held.Cutter.SwingCut = true; held.Cutter.ContactSeconds = .65;
        for (int i = 0; i < 10; i++) held.Step(new(0, -.2, 0), new(0, .2, 0));
        Check(held.SliceCalls == 0, "stationary contact still respects configured heating time");
    }

    private static void RingCutRefusals()
    {
        foreach (var failure in new[] { "crew", "unsupported", "missing contact", "partial", "mode changed" })
        {
            var f = new Fixture(addMesh: false);
            f.Configure(CutMode.Slice);
            f.Hull.Modules.Add(new MeshViewModule(CylinderMesh()));
            if (failure == "crew") f.Target.Crew = [new() { AssignedKittenHash = new(7) }];
            f.Cutter.SliceCheckHandler = (_, _) => failure == "unsupported"
                ? new(false, "Unsupported active module FuelTank; geometry retained.") : new(true, "supported");
            f.Cutter.BeginRingCut(f.Wielder, f.Target, double3.Zero, double3.UnitX, double3.UnitY, 1);
            if (failure == "mode changed") f.Cutter.ArbitrarySlicing = false;
            if (failure == "missing contact")
                for (int i = 0; i < 510; i++) f.Step(new(0, 3, 0), new(0, 2.5, 0));
            else
                for (int i = 0; i < 50; i++) f.Step(new(0, 1.2, 0), new(0, .8, 0));
            if (failure != "partial") Check(f.Cutter.RingFailure != null,
                failure + ": refusal is available during the orbit, before completing a lap");
            Check(!f.Cutter.FinishRingCut(20).Success, failure + ": completion reports failure rather than a successful cut");
            f.AssertUnchanged("ring " + failure);
        }
    }

    private static MeshReference CylinderMesh()
    {
        var vertices = new List<double3>();
        for (int i = 0; i < 96; i++)
        {
            double a = i * Math.PI / 48, b = (i + 1) * Math.PI / 48;
            double3 p = new(-2, Math.Cos(a), Math.Sin(a)), q = new(-2, Math.Cos(b), Math.Sin(b));
            double3 r = new(2, Math.Cos(a), Math.Sin(a)), s = new(2, Math.Cos(b), Math.Sin(b));
            vertices.AddRange([p, q, r, q, s, r]);
        }
        return new() { PositionsCompare = [vertices.ToArray()] };
    }

    private static void RingCommitFailureAndNativeModes()
    {
        var failed = new Fixture(addMesh: false);
        failed.Configure(CutMode.Slice);
        failed.Hull.Modules.Add(new MeshViewModule(CylinderMesh()));
        failed.Cutter.SliceCheckHandler = (_, _) => new(true, "supported");
        failed.Cutter.SliceHandler = (_, _) => { failed.SliceCalls++; return new(false, "Native replacement refused; source retained."); };
        failed.Cutter.BeginRingCut(failed.Wielder, failed.Target, double3.Zero, double3.UnitX, double3.UnitY, 1);
        for (int i = 0; i <= 720; i++)
        {
            double a = i * Math.PI / 360;
            double3 radial = new(0, Math.Cos(a), Math.Sin(a));
            failed.Step(radial * 1.2, radial * .8);
        }
        var result = failed.Cutter.FinishRingCut(20);
        Check(failed.Cutter.RingProgress > .999999 && !result.Success && failed.SliceCalls == 1
            && failed.Cutter.SlicedParts == 0 && result.Message.Contains("Native replacement refused"),
            "100% contact coverage preserves a native slice refusal and never increments successful slices");
        foreach (var mode in new[] { CutMode.Boundary, CutMode.Fragment })
        {
            var f = new Fixture(addMesh: false);
            f.Configure(mode);
            f.Hull.Modules.Add(new MeshViewModule(CylinderMesh()));
            f.Cutter.BeginRingCut(f.Wielder, f.Target, double3.Zero, double3.UnitX, double3.UnitY, 1);
            for (int i = 0; i < 3; i++) f.Step(new(0, 1.2, 0), new(0, .8, 0));
            Check(f.Cutter.SeveredConnections + f.Cutter.FragmentedParts == 1,
                mode + ": ring tracking preserves native contact-triggered cutting");
        }
        var multiple = new Fixture(addMesh: false);
        multiple.Configure(CutMode.Slice);
        multiple.Root.Modules.Add(new MeshViewModule(CylinderMesh()));
        multiple.Hull.Modules.Add(new MeshViewModule(CylinderMesh()));
        multiple.Cutter.SliceCheckHandler = (_, _) => new(true, "supported");
        multiple.Cutter.BeginRingCut(multiple.Wielder, multiple.Target, double3.Zero, double3.UnitX, double3.UnitY, 1);
        multiple.Step(new(0, 1.2, 0), new(0, .8, 0));
        Check(multiple.Cutter.RingFailure?.Contains("multiple full parts") == true,
            "a multi-part ring is refused before any partial structural replacement");
        multiple.AssertUnchanged("multi-part ring");
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

        public void Step(double3 a, double3 b, double dt = .02)
        {
            Cutter.Update(Wielder, a, b, dt, _now);
            _now += dt;
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
