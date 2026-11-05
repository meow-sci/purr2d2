using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing.Detection;

internal static class ManualContactChecks
{
    internal static void Run()
    {
        Geometry(); Gestures(); PassiveStrokeEpochs(); CachedProbe(); MultiTargetProbe(); FrozenDrain(); FirstPassContacts();
        foreach (bool debug in new[] { false, true })
        {
            DebugTimings.Enabled = debug;
            try { Probe(); }
            finally { DebugTimings.Enabled = false; }
        }
    }
    private static void PassiveStrokeEpochs()
    {
        var state = new SliceActivationPolicy();
        Check(state.Update(false, 0, true, true), "passive eligibility begins an epoch");
        ulong first = state.Epoch;
        Check(!state.ObservePassiveMotion(0, true) && !state.ObservePassiveMotion(.2, true)
            && !state.ObservePassiveMotion(.44, false) && state.Epoch == first,
            "continuous passive movement and quiet alone retain the stroke");
        Check(state.ObservePassiveMotion(.46, true) && state.Epoch == first + 1,
            "movement after 250 ms of quiet starts a fresh passive stroke");
        ulong second = state.Epoch;
        Check(state.Update(true, 3, true, true) && state.Epoch == second + 1
            && !state.ObservePassiveMotion(1, true), "manual authorization starts its own epoch");
    }
    private static void Geometry()
    {
        var wall = new CutTriangle(new(0, -.2, -.2), new(0, .2, -.2), new(0, 0, .2));
        var sweep = new BladeSweep(new(-.1, -1, 0), new(-.1, 1, 0), new(.1, -1, 0), new(.1, 1, 0));
        Check(sweep.TryHit(wall, out var hit, out double along, out double time)
            && Math.Abs(hit.X) < 1e-8 && Math.Abs(along - .45) < 1e-7 && Math.Abs(time - .5) < 1e-8,
            "swept blade reports earliest time and lowest blade position on a thin surface");
        Check(!BladeSweep.Segment(sweep.Base0, sweep.Tip0, wall, out _) && !BladeSweep.Segment(sweep.Base1, sweep.Tip1, wall, out _),
            "thin-surface fixture really requires swept detection");
        var coplanar = new CutTriangle(new(-.02, -.1, 0), new(.02, -.1, 0), new(0, .1, 0));
        Check(sweep.TryHit(coplanar, out _, out _, out time) && Math.Abs(time - .4) < 1e-7,
            "coplanar ribbon reports its earliest overlap endpoint");
        var endpoint = new BladeSweep(new(0, -1, 0), new(0, 1, 0), new(.1, -1, 0), new(.1, 1, 0));
        Check(endpoint.TryHit(wall, out _, out _, out time) && Math.Abs(time) < 1e-8,
            "sweep includes the initial endpoint contact at time zero");
        Check(BladeSweep.Segment(new(-1, .2, -.2), new(1, .2, -.2), wall, out _), "triangle vertex graze is inclusive");
        Check(new BladeSweep(new(-2, 0, 0), new(-1, 0, 0), new(1, 0, 0), new(2, 0, 0))
            .TryHit(wall, out _, out along, out time) && Math.Abs(time - 1d / 3) < 1e-8 && along == 1,
            "axial thrust reports its earliest tip crossing when ribbon triangles are degenerate");
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
        Check(state.Consume(first, 0) && !state.Consume(first, 0), "gesture accepts exactly one transaction");
        state.Release(); state.Begin();
        Check(state.Id > first && !state.CanDetect, "regrab during return has a new ID while one transaction stays in flight");
        ulong regrabbed = state.Id;
        state.FinishAttempt(1);
        Check(state.Id == regrabbed && state.Held && !state.CanDetectAt(1.249)
            && state.CanDetectAt(1.25), "regrab keeps its identity and observes the exact 250 ms completion cooldown");
        Check(state.Consume(state.Id, 1.25), "held gesture rearms at the cooldown boundary");
        state.FinishAttempt(1.25);
        state.Cancel(); Check(!state.CanDetect && !state.InFlight, "cancellation cannot silently rearm a held gesture");
        state.Begin();
        Check(!state.CanDetectAt(1.499) && state.CanDetectAt(1.5), "a new press cannot bypass an active cooldown");
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
    private static double3[] TriangleAt(double x) => [new(x, -.2, -.2), new(x, .2, -.2), new(x, 0, .2)];
    private static void CachedProbe()
    {
        var scene = Scene();
        var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity, CacheableMesh = _ => true };
        try
        {
            probe.Advance(scene.Owner, Pose(-.1, 0), out _);
            Check(probe.Advance(scene.Owner, Pose(.1, .01, .1f), out _), "cold cached mode uses confirmed triangle scanner");
            int sequence = 2;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (probe.CacheHits == 0 && DateTime.UtcNow < deadline)
            {
                double x = sequence % 2 == 0 ? -.1 : .1;
                probe.Advance(scene.Owner, Pose(x, sequence * .01, sequence * .1f), out _);
                sequence++; Thread.Yield();
            }
            Check(probe.CacheFallbacks > 0 && probe.CacheHits > 0, "future frozen sweeps use the completed copied BVH");
            int before = probe.CacheFallbacks;
            probe.InvalidateMeshGeometry(scene.Mesh);
            probe.Advance(scene.Owner, Pose(sequence % 2 == 0 ? -.1 : .1, sequence * .01, sequence * .1f), out _);
            Check(probe.CacheFallbacks > before, "explicit mesh revision invalidates the cached query");
        }
        finally { probe.DisposeCache(); }
        scene = Scene();
        scene.Mesh.PositionsCompare = [[new(0, -2, -1), new(0, 2, -1), new(0, 0, 3)]];
        probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity, CacheableMesh = _ => false };
        var contacts = new List<ManualContactProbe.Contact>();
        try
        {
            var before = new ManualContactProbe.Sample(1, 0, new(-1, -1, 0), new(-1, 1, 0), default)
                { NominalLength = 2, VisualScale = 3, VisualAxes = new(3) };
            var after = new ManualContactProbe.Sample(1, .01, new(1, -1, 0), new(1, 1, 1), new(.1f, 0))
                { NominalLength = 2, VisualScale = 3, VisualAxes = new(3) };
            var sweep = new BladeSweep(new(-1, -1, 0), new(-1, 1, 0), new(1, -1, 0), new(1, 1, 1),
                BladeSweep.ApproximationMeters / Math.Sqrt(3));
            Check(sweep.Subdivisions > BladeSweep.MaximumSubdivisions, "cold-path fixture requires ribbon bisection");
            probe.AdvanceMany(scene.Owner, before, contacts);
            int count = probe.AdvanceMany(scene.Owner, after, contacts);
            Check(count == 1 && Math.Abs(contacts[0].HitTime01 - .5) < .01,
                "cold multi-target scan bisects a twisted ribbon and restores original hit time");
            probe.Reset();
            probe.Advance(scene.Owner, before, out _);
            Check(probe.Advance(scene.Owner, after, out var hit) && Math.Abs(hit.HitTime01 - .5) < .01,
                "default mesh scan confirms the same nonplanar swing in original interval time");
        }
        finally { probe.DisposeCache(); }

    }
    private static void MultiTargetProbe()
    {
        var scene = Scene();
        scene.Mesh.PositionsCompare = [TriangleAt(-.05)];
        var other = new Part { Tree = scene.Vehicle.Parts };
        var otherMesh = new MeshReference { PositionsCompare = [TriangleAt(.05)], BoundingSphereRadius = 10 };
        other.Modules.Add(new MeshViewModule(otherMesh)); scene.Vehicle.Parts.Parts.Add(other);
        var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity, CacheableMesh = _ => true };
        var contacts = new List<ManualContactProbe.Contact>();
        try
        {
            Check(probe.AdvanceMany(scene.Owner, Pose(-.1, 0), contacts) == 0,
                "multi-target detector primes target-relative historical samples");
            Check(probe.AdvanceMany(scene.Owner, Pose(.1, .01, .1f), contacts) == 2
                && contacts[0].Part == scene.Part && contacts[1].Part == other,
                "multi-target detector returns two ordered structural targets in one sweep");
            Check(Math.Abs(contacts[0].HitTime01 - .25) < 1e-8 && Math.Abs(contacts[1].HitTime01 - .75) < 1e-8
                && contacts[0].PreviousBaseLocal.X == -.1 && contacts[0].CurrentBaseLocal.X == .1,
                "multi-target evidence preserves frozen endpoints and per-target hit time");
            var observed = contacts[0];
            Check(probe.IsEvidenceCurrent(observed), "accepted evidence records its observed mesh identity and revision");
            Check(probe.TestedTriangles <= ManualContactProbe.TriangleBudget && probe.FrameReads <= ManualContactProbe.FrameBudget
                && probe.DiscoveryUnits <= ManualContactProbe.DiscoveryBudget && probe.PrimitiveReads <= ManualContactProbe.PrimitiveBudget,
                "multi-target traversal respects shared per-update operation limits");
            int sequence = 2; var deadline = DateTime.UtcNow.AddSeconds(10);
            while (probe.CacheHits == 0 && DateTime.UtcNow < deadline)
            {
                probe.AdvanceMany(scene.Owner, Pose(sequence % 2 == 0 ? -.1 : .1, sequence * .01, sequence * .1f), contacts);
                sequence++; Thread.Yield();
            }
            Check(probe.CacheHits > 0, "multi-target probe uses the completed copied BVH on later intervals");
            scene.Mesh.PositionsCompare = [TriangleAt(-.05)];
            Check(!probe.IsEvidenceCurrent(observed), "replaced source arrays invalidate previously confirmed evidence");
            scene.Mesh.PositionsCompare = observed.HitPrimitives;
            Check(probe.IsEvidenceCurrent(observed), "restoring the same unmodified source preserves observed identity");
            probe.InvalidateMeshGeometry(scene.Mesh);
            Check(!probe.IsEvidenceCurrent(observed), "explicit in-place mesh revision invalidates confirmed evidence");
        }
        finally { probe.DisposeCache(); }
        scene = Scene(5000);
        other = new Part { Tree = scene.Vehicle.Parts };
        otherMesh = new MeshReference { PositionsCompare = [scene.Mesh.PositionsCompare![0]], BoundingSphereRadius = 10 };
        other.Modules.Add(new MeshViewModule(otherMesh)); scene.Vehicle.Parts.Parts.Add(other);
        probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity, CacheableMesh = _ => true };
        contacts.Clear(); var found = new HashSet<Part>();
        try
        {
            probe.AdvanceMany(scene.Owner, Pose(-.1, 0), contacts);
            for (int step = 1; step <= 7 && found.Count < 2; step++)
            {
                probe.AdvanceMany(scene.Owner, Pose(.1, step * .01, .1f), contacts);
                foreach (var hit in contacts) found.Add(hit.Part);
                Check(probe.TestedTriangles <= ManualContactProbe.TriangleBudget,
                    "resumed multi-target cursors share one triangle budget");
            }
            Check(found.SetEquals([scene.Part, other]),
                "independent dense target cursors resume and both publish their frozen contacts");
        }
        finally { probe.DisposeCache(); }
    }
    private static void FrozenDrain()
    {
        foreach (bool cached in new[] { false, true })
        {
            var scene = Scene(5000);
            var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity, CacheableMesh = _ => false };
            var hits = new List<ManualContactProbe.Contact>();
            try
            {
                if (cached)
                { probe.AdvanceMany(scene.Owner, Pose(-.1, 0, gesture: 42), hits); probe.AdvanceMany(scene.Owner, Pose(.1, .01, .1f, 42), hits); }
                else
                { probe.Advance(scene.Owner, Pose(-.1, 0, gesture: 42), out _); probe.Advance(scene.Owner, Pose(.1, .01, .1f, 42), out _); }
                Check(probe.Pending && hits.Count == 0, "manual dense query remains pending at release");
                scene.Vehicle.PositionEgo = new(100, -50, 20);
                scene.Vehicle.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .4);
                Check(probe.DrainFrozen(scene.Owner, .02, hits) == 1 && hits[0].Part == scene.Part
                    && hits[0].TriggerEpoch == 42 && hits[0].PreviousBaseLocal.X == -.1
                    && hits[0].CurrentBaseLocal.X == .1 && probe.DiscoveryUnits == 0 && probe.FrameReads == 0,
                    "released manual query completes only its originally authorized frozen interval");
                scene.Vehicle.PositionEgo = default; scene.Vehicle.Asmb2Ego = doubleQuat.Identity;
                Check(probe.DrainFrozen(scene.Owner, .03, hits) == 0 && !probe.Pending,
                    "draining an empty probe never discovers or samples new contacts");
                if (cached)
                { probe.Reset(); probe.AdvanceMany(scene.Owner, Pose(-.1, .1, gesture: 43), hits); probe.AdvanceMany(scene.Owner, Pose(.1, .11, .1f, 43), hits); }
                else
                { probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, .1, gesture: 43), out _); probe.Advance(scene.Owner, Pose(.1, .11, .1f, 43), out _); }
                Check(probe.Pending && probe.DrainFrozen(scene.Owner, .27, hits) == 0 && !probe.Pending,
                    "released manual query expires rather than querying an old interval");
                probe.Reset();
                if (cached)
                { probe.AdvanceMany(scene.Owner, Pose(-.1, .28, gesture: 45), hits); probe.AdvanceMany(scene.Owner, Pose(.1, .29, .1f, 45), hits); }
                else
                { probe.Advance(scene.Owner, Pose(-.1, .28, gesture: 45), out _); probe.Advance(scene.Owner, Pose(.1, .29, .1f, 45), out _); }
                scene.Part.PositionParentAsmb = new(.01, 0, 0);
                Check(probe.Pending && probe.DrainFrozen(scene.Owner, .30, hits) == 0 && !probe.Pending,
                    "released manual query rejects a changed source transform");
                scene.Part.PositionParentAsmb = default;
                probe.Reset();
                var passive0 = Pose(-.1, .3, gesture: 44) with { PassiveContact = true };
                var passive1 = Pose(.1, .31, .1f, 44) with { PassiveContact = true };
                if (cached) { probe.AdvanceMany(scene.Owner, passive0, hits); probe.AdvanceMany(scene.Owner, passive1, hits); }
                else { probe.Advance(scene.Owner, passive0, out _); probe.Advance(scene.Owner, passive1, out _); }
                Check(probe.Pending && probe.DrainFrozen(scene.Owner, .32, hits) == 0 && !probe.Pending,
                    "passive provisional work is discarded by the manual-release drain");
            }
            finally { probe.DisposeCache(); }
        }
    }
    private static void PlaceTriangle(double3[] vertices, int triangle, double x) => Array.Copy(TriangleAt(x), 0, vertices, triangle * 3, 3);
    private static void FirstPassContacts()
    {
        var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity };
        var scene = Scene();
        bool found = false;
        for (int i = 0; i < 21; i++)
            found |= probe.Advance(scene.Owner, Pose(-.0095 + i * .001, i / 144d, i * .00055f), out _);
        Check(found, "slow deliberate 1 mm steps confirm a thin surface on the first pass");

        probe.Reset(); found = false;
        for (int i = 0; i <= 18; i++)
        {
            int step = Math.Min(i, 12);
            found |= probe.Advance(scene.Owner, Pose(-.003 + step * .0005, i / 60d, step * .0001f), out _);
            if (i < 10 && found) throw new Exception("surface accepted before the drag threshold");
        }
        Check(found, "a crossing just before the drag threshold survives until the gesture is authorized");

        probe.Reset();
        probe.Advance(scene.Owner, new(1, 0, new(-.1, -.1, 0), new(-.1, .1, 0), default), out _);
        probe.Advance(scene.Owner, new(1, .01, new(-.1, .25, 0), new(-.1, .45, 0), new(.00055f, 0)), out _);
        Check(!probe.Advance(scene.Owner, new(1, .02, new(.1, .25, 0), new(.1, .45, 0), new(.0011f, 0)), out _),
            "accumulated pointer movement cannot invent a diagonal cut across a path around a wall");

        scene = Scene(); probe.Reset();
        probe.Advance(scene.Owner, Pose(-.01, 0), out _);
        probe.Advance(scene.Owner, Pose(-.009, .01, .0005f), out _);
        Check(!probe.Advance(scene.Owner, Pose(.01, .02, .0005f), out _)
            && !probe.Advance(scene.Owner, Pose(.02, .03, .0016f), out _),
            "a stationary pointer discards a body-only crossing instead of arming it on later drag");
        Check(!probe.Advance(scene.Owner, Pose(-.02, .04, float.NaN), out _) && !probe.Pending,
            "nonfinite drag cannot authorize a moving blade");

        scene = Scene(); probe.Reset();
        // The last primitive's bounds may be much smaller than the first one's in KSA.
        scene.Mesh.PositionsCompare = [TriangleAt(2), TriangleAt(0)];
        scene.Mesh.BoundingSphereRadius = .3;
        probe.Advance(scene.Owner, Pose(1.9, 0), out _);
        Check(probe.Advance(scene.Owner, Pose(2.1, .02, .1f), out var hit) && hit.PointLocal.X == 2,
            "an underestimated multi-primitive radius cannot hide a confirmed triangle");

        // An out-and-back completes before the dense miss finishes. Verify both
        // a second mesh and a later sweep of the very same mesh.
        foreach (bool sameMesh in new[] { false, true })
        {
            scene = Scene(ManualContactProbe.TriangleBudget * 3);
            var target = scene.Part;
            if (!sameMesh)
            {
                PlaceTriangle(scene.Mesh.PositionsCompare![0], ManualContactProbe.TriangleBudget * 3, 5);
                target = new Part { Tree = scene.Vehicle.Parts };
                target.Modules.Add(new MeshViewModule(new MeshReference
                    { PositionsCompare = [TriangleAt(0)], BoundingSphereRadius = 1 }));
                scene.Vehicle.Parts.Parts.Add(target);
            }
            probe.Reset(); found = false;
            double[] path = [-.2, -.1, .1, -.1];
            for (int i = 0; i < 12; i++)
            {
                double x = path[Math.Min(i, path.Length - 1)];
                if (probe.Advance(scene.Owner, Pose(x, i * .01, (float)x), out hit))
                    found |= hit.Part == target && hit.PointLocal.X == 0
                        && hit.PreviousBaseLocal.X < 0 && hit.CurrentBaseLocal.X > 0;
                if (probe.TestedTriangles > ManualContactProbe.TriangleBudget || probe.FrameReads > ManualContactProbe.FrameBudget)
                    throw new Exception("following a reversal exceeded the shared work budget");
            }
            Check(found, sameMesh ? "a busy mesh retains its own later crossing through a reversal"
                : "a dense miss on one mesh cannot erase an out-and-back crossing on its neighbor");
        }

        // With the sole hit late in a cold mesh, allocating 256 of every 4096
        // triangle units to copying would expire the query one update short.
        scene = Scene(34999);
        var model = new Part { Tree = scene.Vehicle.Parts };
        model.Modules.Add(new PartModelModule { PartModel = new PartModel
            { Template = new PartModelModule.Template { Mesh = scene.Mesh } } });
        scene.Vehicle.Parts.Parts.Clear(); scene.Vehicle.Parts.Parts.Add(model);
        probe.Reset(); var contacts = new List<ManualContactProbe.Contact>(); found = false;
        try
        {
            probe.AdvanceMany(scene.Owner, Pose(-.1, 0), contacts);
            for (int i = 1; i <= 11; i++)
            {
                probe.AdvanceMany(scene.Owner, Pose(.1, i / 59d, .1f), contacts);
                found |= contacts.Exists(c => c.Part == model);
                if (probe.TestedTriangles > ManualContactProbe.TriangleBudget)
                    throw new Exception("cache copy and detection exceeded the shared triangle budget");
            }
            Check(found, "cold cache copying gives current contact enough of the same budget to finish before expiry");
        }
        finally { probe.DisposeCache(); }
    }
    private static double LayeredContactX(bool reverse, ManualContactProbe probe, double time)
    {
        var scene = Scene(); var vertices = new double3[6];
        PlaceTriangle(vertices, reverse ? 0 : 1, .05);
        PlaceTriangle(vertices, reverse ? 1 : 0, -.05);
        scene.Mesh.PositionsCompare = [vertices];
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, time), out _);
        return probe.Advance(scene.Owner, Pose(.1, time + .02, .1f), out var contact) ? contact.PointLocal.X : double.NaN;
    }
    private static void Probe()
    {
        var scene = Scene(); var probe = new ManualContactProbe { SoftBudgetMilliseconds = double.PositiveInfinity };
        Check(!probe.Advance(scene.Owner, Pose(-.1, 0), out _), "first pose only seeds the target-relative baseline");
        Check(probe.Advance(scene.Owner, Pose(.1, .02, .1f), out var contact) && contact.Part == scene.Part,
            "production probe confirms manual thin-surface sweep");
        Check((contact.BladeDirectionLocal - new double3(0, 2, 0)).LengthSquared() < 1e-10
            && (contact.MotionLocal - new double3(.2, 0, 0)).LengthSquared() < 1e-10,
            "contact carries local blade direction and hit-point motion vectors");
        scene = Scene(); probe.Reset();
        probe.Advance(scene.Owner, new(1, .03, new(0, 0, 0), new(0, .5, 0), default), out _);
        Check(probe.Advance(scene.Owner, new(1, .05, new(0, 0, 0), new(0, 0, .5), new(.1f, 0)), out contact)
            && contact.MotionLocal.LengthSquared() > 1e-8,
            "fixed hilt contact uses the moving blade endpoint as its motion fallback");
        var targeting = new PartSliceTargeting();
        Check(targeting.TryContactTarget(scene.Owner, contact.Part, contact.PointLocal, contact.DistanceMeters, contact.BladeLength,
            contact.SourceTransform, contact.PartTransform, out var target, out _) && target!.Part == scene.Part,
            "confirmed physical contact resolves its structural slice target without assisted grace");
        Check(!targeting.TryContactTarget(scene.Owner, contact.Part, contact.PointLocal, 2.01, 2,
            contact.SourceTransform, contact.PartTransform, out _, out _), "contact beyond actual physical length refuses");
        double forward = LayeredContactX(false, probe, .1);
        double reversed = LayeredContactX(true, probe, .2);
        Check(Math.Abs(forward + .05) < 1e-8 && Math.Abs(reversed + .05) < 1e-8,
            "entry contact wins over the later exit regardless of triangle order");
        scene = Scene();
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
        scene = Scene(); scene.Mesh.PositionsCompare = [[new(0, 48, -.2), new(0, 50, -.2), new(0, 49, .2)]];
        scene.Mesh.BoundingSphereRadius = 60;
        probe.Reset(); probe.Advance(scene.Owner, new(1, .84, default, new(-10, 49, 0), default), out _);
        Check(probe.Advance(scene.Owner, new(1, .86, default, new(10, 49, 0), new(.1f, 0)), out _),
            "a 50-metre blade confirms fast angular contact near its actual tip");
        scene = Scene(); probe.Reset();
        probe.Advance(scene.Owner, new(1, .88, new(-2, -10, 0), new(-2, 10, 0), default)
            { VisualScale = 10, VisualAxes = new(10), NominalLength = 2 }, out _);
        Check(probe.Advance(scene.Owner, new(1, .9, new(2, -10, 0), new(2, 10, 0), new(.1f, 0))
            { VisualScale = 10, VisualAxes = new(10), NominalLength = 2 }, out _),
            "a giant kitten's ordinary scaled hilt sweep is not mistaken for a stock-size teleport");
        scene = Scene(); probe.Reset();
        probe.Advance(scene.Owner, new(1, .92, new(-.1, -1, 0), new(-.1, 1, 0), default)
            { VisualScale = 4, VisualAxes = new(4, 2, 3), NominalLength = 2 }, out _);
        Check(probe.Advance(scene.Owner, new(1, .94, new(.1, -2, 0), new(.1, 2, 0), new(.1f, 0))
            { VisualScale = 4, VisualAxes = new(4, 2, 3), NominalLength = 2 }, out _),
            "nonuniform visual scale may change physical blade length during a stable nominal-length swing");
        scene = Scene(); probe.Reset();
        probe.Advance(scene.Owner, new(1, .96, new(-.1, -1, 0), new(-.1, 1, 0), default)
            { VisualScale = 4, VisualAxes = new(4, 2, 3), NominalLength = 2 }, out _);
        Check(!probe.Advance(scene.Owner, new(1, .98, new(.1, -2, 0), new(.1, 2, 0), new(.1f, 0))
            { VisualScale = 4, VisualAxes = new(4, 3, 3), NominalLength = 2 }, out _),
            "resizing one avatar axis discards sweep history even when the maximum scale is unchanged");
        // A frozen sweep survives a new, already-moved-away pose while its cursor resumes.
        scene = Scene(ManualContactProbe.TriangleBudget + 7); probe.Reset();
        probe.Advance(scene.Owner, Pose(-.1, 1), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, 1.02, .1f), out _) && probe.Pending && probe.TestedTriangles == ManualContactProbe.TriangleBudget,
            "triangle work stops at its hard per-update limit");
        Check(probe.Advance(scene.Owner, Pose(.5, 1.04, .3f), out _) && probe.TestedTriangles == 8,
            "deferred query resumes its frozen ribbon instead of replacing it with the latest pose");
        // The first batch finds a later exit surface. A later batch contains the entry;
        // best-so-far survives continuation and the dense query publishes on its bounded
        // second refinement update while still fresh.
        scene = Scene();
        int denseCount = ManualContactProbe.TriangleBudget * 4;
        var dense = new double3[denseCount * 3];
        for (int i = 0; i < denseCount; i++) PlaceTriangle(dense, i, 5);
        PlaceTriangle(dense, 0, .05);
        PlaceTriangle(dense, ManualContactProbe.TriangleBudget, -.05);
        scene.Mesh.PositionsCompare = [dense];
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 1.2), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, 1.22, .1f), out _) && probe.Pending
            && probe.TestedTriangles == ManualContactProbe.TriangleBudget,
            "dense contact query defers its first candidate at the triangle budget");
        Check(!probe.Advance(scene.Owner, Pose(.5, 1.24, .3f), out _) && probe.Pending
            && probe.TestedTriangles == ManualContactProbe.TriangleBudget,
            "first continuation improves the retained contact while the query continues");
        Check(probe.Advance(scene.Owner, Pose(.6, 1.26, .4f), out contact) && !probe.Pending
            && probe.TestedTriangles == ManualContactProbe.TriangleBudget && Math.Abs(contact.PointLocal.X + .05) < 1e-8,
            "dense mesh publishes its best confirmed entry after two bounded refinements");
        // A hit first discovered in the last 50 ms of the existing age window publishes
        // at that update's budget boundary instead of expiring before refinements can run.
        scene = Scene(); denseCount = ManualContactProbe.TriangleBudget * 3;
        dense = new double3[denseCount * 3];
        for (int i = 0; i < denseCount; i++) PlaceTriangle(dense, i, 5);
        PlaceTriangle(dense, ManualContactProbe.TriangleBudget, -.05);
        scene.Mesh.PositionsCompare = [dense];
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 1.4), out _);
        Check(!probe.Advance(scene.Owner, Pose(.1, 1.42, .1f), out _) && probe.Pending
            && probe.TestedTriangles == ManualContactProbe.TriangleBudget,
            "late-hit fixture first consumes a bounded miss batch");
        Check(probe.Advance(scene.Owner, Pose(.5, 1.54, .3f), out contact) && !probe.Pending
            && probe.TestedTriangles == ManualContactProbe.TriangleBudget && Math.Abs(contact.PointLocal.X + .05) < 1e-8,
            "late first hit publishes while still current in the final age window");
        probe.Reset(); probe.Advance(scene.Owner, Pose(-.1, 2), out _); probe.Advance(scene.Owner, Pose(.1, 2.02, .1f), out _);
        Check(!probe.Advance(scene.Owner, Pose(.5, 2.3, .3f), out _) && !probe.Pending
            && (!DebugTimings.Enabled || probe.ExpiredQueries > 0), "old frozen queries explicitly expire with diagnostics on or off");
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
        if (!DebugTimings.Enabled)
            Check(probe.LastUpdateMilliseconds == 0 && probe.MaximumUpdateMilliseconds == 0
                && probe.ExpiredQueries == 0 && probe.Saturations == 0,
                "disabled probing must skip optional timing and cumulative diagnostic stats");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
