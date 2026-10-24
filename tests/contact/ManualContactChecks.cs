using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

internal static class ManualContactChecks
{
    internal static void Run()
    {
        Geometry(); Gestures(); Probe();
    }
    private static void Geometry()
    {
        var wall = new CutTriangle(new(0, -.2, -.2), new(0, .2, -.2), new(0, 0, .2));
        var sweep = new BladeSweep(new(-.1, -1, 0), new(-.1, 1, 0), new(.1, -1, 0), new(.1, 1, 0));
        Check(sweep.TryHit(wall, out var hit, out double along) && Math.Abs(hit.X) < 1e-8 && along >= 0 && along <= 1,
            "swept blade detects a thin surface missed by both endpoint segments");
        Check(!BladeSweep.Segment(sweep.Base0, sweep.Tip0, wall, out _) && !BladeSweep.Segment(sweep.Base1, sweep.Tip1, wall, out _),
            "thin-surface fixture really requires swept detection");
        var coplanar = new CutTriangle(new(-.02, -.1, 0), new(.02, -.1, 0), new(0, .1, 0));
        Check(sweep.TryHit(coplanar, out _, out _), "coplanar ribbon confirms contained surface triangles");
        Check(BladeSweep.Segment(new(-1, .2, -.2), new(1, .2, -.2), wall, out _), "triangle vertex graze is inclusive");
        Check(new BladeSweep(new(-2, 0, 0), new(-1, 0, 0), new(1, 0, 0), new(2, 0, 0)).TryHit(wall, out _, out _),
            "axial thrust confirms a crossing even when ribbon triangles are degenerate");
        Check(new BladeSweep(new(0, 0, 0), new(0, 2, 0), new(.3, 0, 0), new(1.5, 1.5, 0)).Subdivisions == 1,
            "large planar rotation does not exhaust the nonplanarity budget");
        Check(new BladeSweep(default, new(0, 1, 0), new(1, 0, 0), new(1, 1, .05)).Subdivisions > 1,
            "nonplanar ribbons subdivide their checked triangle approximation");
        Check(!new BladeSweep(default, new(0, 1, 0), new(1, 0, 0), new(1, 1, 1), 1e-9).TryHit(wall, out _, out _),
            "excessive nonplanarity refuses instead of exceeding subdivision work");
        Check(!new BladeSweep(new(double.NaN, 0, 0), default, default, default).TryHit(wall, out _, out _), "nonfinite sweep refuses safely");
        Check(!sweep.TryHit(new(new(5, 0, 0), new(5, 1, 0), new(5, 0, 1)), out _, out _), "bounds-space proximity cannot authorize a missed triangle");
    }
    private static void Gestures()
    {
        var state = new ManualContactGesture(); state.Begin(); ulong first = state.Id;
        Check(state.Consume(first) && !state.Consume(first), "gesture accepts exactly one transaction");
        state.Release(); state.Begin();
        Check(state.Id > first && !state.CanDetect, "regrab during return has a new ID while one transaction stays in flight");
        state.Finish(); Check(state.CanDetect && state.Consume(state.Id), "new deliberate gesture can cut after the previous transaction finishes");
        state.Cancel(); Check(!state.CanDetect && !state.InFlight, "cancellation cannot silently rearm a held gesture");
    }
    private static (KittenEva Owner, Vehicle Vehicle, Part Part, MeshReference Mesh) Scene(int misses = 0)
    {
        var owner = new KittenEva(); var vessel = new Vehicle { Parent = owner.Parent };
        var part = new Part { Tree = vessel.Parts };
        var vertices = new double3[(misses + 1) * 3];
        for (int i = 0; i < misses; i++) { vertices[i * 3] = new(5, -1, -1); vertices[i * 3 + 1] = new(5, 1, -1); vertices[i * 3 + 2] = new(5, 0, 1); }
        vertices[^3] = new(0, -.2, -.2); vertices[^2] = new(0, .2, -.2); vertices[^1] = new(0, 0, .2);
        var mesh = new MeshReference { PositionsCompare = [vertices], BoundingSphereRadius = 10 };
        part.Modules.Add(new MeshViewModule(mesh)); vessel.Parts.Parts.Add(part);
        Universe.CurrentSystem = new(); Universe.CurrentSystem.All.Items = [owner, vessel];
        KSA.Program.EditorFlag = false; KSA.Program.Camera = new();
        return (owner, vessel, part, mesh);
    }
    private static ManualContactProbe.Sample Pose(double x, double time, float drag = 0, ulong gesture = 1) => new(gesture, time, new(x, -1, 0), new(x, 1, 0), new(drag, 0));
    private static void Probe()
    {
        var scene = Scene(); var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity };
        Check(!probe.Advance(scene.Owner, Pose(-.1, 0), out _), "first pose only seeds the target-relative baseline");
        Check(probe.Advance(scene.Owner, Pose(.1, .02, .1f), out var contact) && contact.Part == scene.Part,
            "production probe confirms manual thin-surface sweep");
        var targeting = new PartSliceTargeting();
        Check(targeting.TryContactTarget(scene.Owner, contact.Part, contact.PointLocal, contact.DistanceMeters, contact.BladeLength,
            contact.SourceTransform, contact.PartTransform, out var target, out _) && target!.Part == scene.Part,
            "confirmed physical contact resolves its structural slice target without assisted grace");
        Check(!targeting.TryContactTarget(scene.Owner, contact.Part, contact.PointLocal, 2.01, 2,
            contact.SourceTransform, contact.PartTransform, out _, out _), "contact beyond actual physical length refuses");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, .1), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, .12), out _), "body/target movement with an unmoved SWING control does not cut");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, .2), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, .4, .1f), out _), "pose gap discards sweep history");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, .5), out _);
        Check(!probe.Advance(scene.Owner, Pose(3, .52, .1f), out _), "teleport/discontinuity discards sweep history");
        foreach (double fps in new[] { 30d, 60d, 144d })
        {
            scene = Scene(); probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, .6), out _);
            Check(probe.Advance(scene.Owner, Pose(.1, .6 + 1 / fps, .1f), out _), $"thin-surface sweep confirms at {fps} FPS sample spacing");
        }
        scene = Scene(); scene.Mesh.PositionsCompare = [[new(0, 4.8, -.2), new(0, 5.2, -.2), new(0, 5, .2)]];
        probe.Reset(); probe.Advance(scene.Owner, new(1, .8, default, new(-3, 11, 0), default), out _);
        Check(probe.Advance(scene.Owner, new(1, .82, default, new(3, 11, 0), new(.1f, 0)), out _),
            "long-blade angular motion is not mistaken for a hilt teleport");
        // A frozen sweep survives a new, already-moved-away pose while its cursor resumes.
        scene = Scene(ManualContactProbe.TriangleBudget + 7); probe.Reset();
        probe.Advance(scene.Owner, Pose(-.1, 1), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, 1.02, .1f), out _) && probe.Pending && probe.TestedTriangles == ManualContactProbe.TriangleBudget,
            "triangle work stops at its hard per-update limit");
        Check(probe.Advance(scene.Owner, Pose(.5, 1.04, .3f), out _) && probe.TestedTriangles == 8,
            "deferred query resumes its frozen ribbon instead of replacing it with the latest pose");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 2), out _); probe.Advance(scene.Owner, Pose(.1, 2.02, .1f), out _);
        Check(!probe.Advance(scene.Owner, Pose(.5, 2.3, .3f), out _) && probe.ExpiredQueries > 0, "old frozen queries explicitly expire");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 3), out _); probe.Advance(scene.Owner, Pose(.1, 3.02, .1f), out _);
        scene.Part.PositionParentAsmb = new(.2, 0, 0);
        Check(!probe.Advance(scene.Owner, Pose(.5, 3.04, .3f), out _), "source transform changes invalidate deferred queries");
        // Exact corresponding target frames: owner and target translate together.
        scene = Scene(); probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 4), out _);
        scene.Owner.PositionEgo = scene.Vehicle.PositionEgo = new(10000, 20000, -5000);
        Check(probe.Advance(scene.Owner, Pose(.1, 4.02, .1f), out _), "common owner/target translation preserves sweep contact");
        scene = Scene(); scene.Part.Scale = new(2, .5, 3); scene.Vehicle.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .5);
        probe.Reset();
        var rotation = scene.Vehicle.Asmb2Ego;
        var a0 = new double3(-.1, -1, 0).Transform(scene.Part.MatrixAsmb2VehicleAsmb).Transform(rotation);
        var b0 = new double3(-.1, 1, 0).Transform(scene.Part.MatrixAsmb2VehicleAsmb).Transform(rotation);
        probe.Advance(scene.Owner, new(1, 5, a0, b0, default), out _);
        scene.Vehicle.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .7); rotation = scene.Vehicle.Asmb2Ego;
        var a1 = new double3(.1, -1, 0).Transform(scene.Part.MatrixAsmb2VehicleAsmb).Transform(rotation);
        var b1 = new double3(.1, 1, 0).Transform(scene.Part.MatrixAsmb2VehicleAsmb).Transform(rotation);
        Check(probe.Advance(scene.Owner, new(1, 5.02, a1, b1, new(.1f, 0)), out _), "rotating nonuniformly scaled target uses corresponding local frames");
        scene = Scene(); scene.Part.Scale = new(.01); probe.Reset();
        probe.Advance(scene.Owner, new(1, 5.2, new(-.0004, -.01, 0), new(-.0004, .01, 0), default), out _);
        Check(!probe.Advance(scene.Owner, new(1, 5.22, new(.0004, -.01, 0), new(.0004, .01, 0), new(.1f, 0)), out _),
            "motion gate measures physical displacement rather than tiny target local scale");
        // A visual subpart is resolved through its parent structural part.
        scene = Scene(ManualContactProbe.TriangleBudget + 7);
        var sub = new Part { PartParent = scene.Part, PositionParentAsmb = new(0, 1, 0) };
        sub.Modules.Add(new MeshViewModule(scene.Mesh)); scene.Part.SubParts.Add(sub);
        // Remove the root visual by using a new source without root modules.
        var root = new Part { Tree = scene.Vehicle.Parts }; sub.PartParent = root; root.SubParts.Add(sub);
        scene.Vehicle.Parts.Parts.Clear(); scene.Vehicle.Parts.Parts.Add(root);
        probe.Reset(); probe.Advance(scene.Owner, new(1, 6, new(-.1, 0, 0), new(-.1, 2, 0), default), out _);
        probe.Advance(scene.Owner, new(1, 6.02, new(.1, 0, 0), new(.1, 2, 0), new(.1f, 0)), out _);
        sub.PositionParentAsmb = new(0, 2, 0);
        Check(!probe.Advance(scene.Owner, new(1, 6.04, new(.2, 0, 0), new(.2, 2, 0), new(.2f, 0)), out _), "animated subpart movement invalidates frozen local contact");
        scene = Scene(); var visibleSub = new Part { PartParent = scene.Part, PositionParentAsmb = new(0, 1, 0) };
        visibleSub.Modules.Add(new MeshViewModule(scene.Mesh)); scene.Part.SubParts.Add(visibleSub);
        root = new Part { Tree = scene.Vehicle.Parts }; visibleSub.PartParent = root; root.SubParts.Add(visibleSub);
        scene.Vehicle.Parts.Parts.Clear(); scene.Vehicle.Parts.Parts.Add(root);
        probe.Reset(); probe.Advance(scene.Owner, new(1, 6.2, new(-.1, 0, 0), new(-.1, 2, 0), default), out _);
        Check(probe.Advance(scene.Owner, new(1, 6.22, new(.1, 0, 0), new(.1, 2, 0), new(.1f, 0)), out contact)
            && targeting.TryContactTarget(scene.Owner, contact.Part, contact.PointLocal, contact.DistanceMeters, contact.BladeLength,
                contact.SourceTransform, contact.PartTransform, out target, out _) && target!.Part == root,
            "a translated visible subpart resolves a cut on its live structural parent");
        scene = Scene(); scene.Mesh.PositionsCompare = Enumerable.Range(0, ManualContactProbe.PrimitiveBudget + 2).Select(_ => Array.Empty<double3>()).ToArray();
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 6.4), out _); probe.Advance(scene.Owner, Pose(.1, 6.42, .1f), out _);
        Check(probe.PrimitiveReads == ManualContactProbe.PrimitiveBudget && probe.TestedTriangles == 0 && probe.Pending,
            "empty primitive discovery has a separate hard bound and resumable cursor");
        // Scene traversal itself is bounded even if every object is irrelevant.
        scene = Scene(); Universe.CurrentSystem!.All.Items = Enumerable.Range(0, 5000).Select(_ => (object)new Vehicle()).ToArray();
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 7), out _);
        Check(probe.DiscoveryUnits == ManualContactProbe.DiscoveryBudget && probe.FrameReads == 0 && probe.TestedTriangles == 0,
            "cold discovery respects its own bound without fleet snapshots or triangle work");
        probe.Reset(); Check(!probe.Pending, "cancellation clears frozen contact and candidate history");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
