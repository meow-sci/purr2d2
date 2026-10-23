using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Force;

internal static class ForceControllerChecks
{
    private static int _checks;
    public static void Main()
    {
        RigidHoldAndRelease();
        ThrowAndTopology();
        RollbackContinuesAfterFailure();
        InvalidOwnerAndJoinFailure();
        MixedAngryEitherPrimary();
        CombinedLimitPreflight();
        MixedRollbackAndInvalidation();
        MixedImmediateOffsetAndLocalBubble();
        Console.WriteLine($"Force controller checks passed ({_checks} assertions: rigid mixed groups, exact 2 m vessel surfaces, deduplication, combined 32 limit, once-only release/throw, rollback and world/key cleanup).");
    }

    private static (ForceController Controller, KittenEva Owner, Vehicle Target) Setup(string id)
    {
        Universe.CurrentSystem = new(); Universe.Time = 0;
        Program.EditorFlag = false;
        JobSystems.OrbitSolvers.ThrowOnWait = false;
        JobSystems.NearestOrbitAndPerformanceWorker.ThrowOnWait = false;
        JobSystems.VehicleSolver.ThrowOnWait = false;
        JobSystems.VehicleSolver.Waits = 0;
        ClutterForceTargets.Candidates = [];
        ClutterForceTargets.QueryFails = false;
        var parent = new ParentBody();
        var owner = new KittenEva(Universe.CurrentSystem, parent, "kitten", double3.Zero, new(3, 0, 0));
        owner.BodyRates = new(0, 0, 1);
        Program.ControlledVehicle = owner;
        var target = new Vehicle(Universe.CurrentSystem, parent, "object", new(2, 0, 0), new(100, 0, 0));
        var controller = new ForceController(); controller.Install(new Harmony("purr2d2.force.fixture." + id));
        return (controller, owner, target);
    }

    private static ForcePick Pick(Vehicle target) => new(target, target.Parts.Items[0], target.Parent, target.System, double3.Zero);

    private static void RigidHoldAndRelease()
    {
        var (controller, owner, target) = Setup("follow");
        using (controller)
        {
            Check(controller.Capture(owner, Pick(target), false), "capture accepted");
            Check(controller.HasCapture && controller.CapturedCount == 1 && !target.HasPhysicsBubble, "capture leaves ordinary physics");
            var worker = target.TakeUpdateStateIfOrphaned();
            Check(worker != null && ForcePhysicsPatches.FilterCapturedWorker(worker) == null, "held cached worker is filtered");
            Equal(target.GetVelocityCci(), new(3, 2, 0), "initial release velocity follows owner spin");
            int writes = target.PlanWrites; controller.Update(owner);
            Check(target.PlanWrites == writes, "duplicate unchanged epoch avoids rebuilding trajectory");
            Universe.Time = 1;
            double s = Math.Sqrt(.5);
            owner.SetNativeState(new(10, 0, 0), new(3, 0, 0), new(0, 0, s, s), new(0, 0, 1));
            controller.SetOffset(new(0, 1, 0)); controller.Update(owner);
            Equal(target.GetPositionCci(), new(9, 2, 0), "group is rigid in rotated owner frame");
            Equal(target.GetVelocityCci(), new(1, -1, 0), "group velocity retains full tangential spin");
            Check(target.Orbit.StateVectors.StateTime.Value == 1, "held orbit uses committed epoch");
            Check(controller.TryGetPoseTargetBody(owner, out var aim), "held aim available without saber");
            Equal(aim, new(2, 1, 0), "pose aim includes translated group offset");
            controller.Release(false);
            Check(!controller.HasCapture && ForcePhysicsPatches.FilterCapturedWorker(worker) == worker, "release permits native worker adoption");
            Equal(target.GetPositionCci(), new(9, 2, 0), "release retains current pose");
        }
    }

    private static void ThrowAndTopology()
    {
        var (controller, owner, target) = Setup("throw");
        using (controller)
        {
            Check(controller.Capture(owner, Pick(target), false), "throw capture");
            var inherited = target.GetVelocityCci();
            controller.Release(true);
            Equal(target.GetVelocityCci() - inherited, new(25, 0, 0), "throw adds considerable outward velocity once");
            var thrown = target.GetVelocityCci(); controller.Release(true);
            Equal(target.GetVelocityCci(), thrown, "second release never throws twice");
            Check(controller.Capture(owner, Pick(target), false), "recapture after throw");
            target.Parts.Items.Add(new() { Tree = target.Parts });
            controller.Update(owner);
            Check(!controller.HasCapture && ForcePhysicsPatches.FilterCapturedWorker(target.TakeUpdateStateIfOrphaned()) != null,
                "topology changes release surviving source without retaining new fragments");
        }
    }

    private static void RollbackContinuesAfterFailure()
    {
        var (controller, owner, first) = Setup("rollback");
        using (controller)
        {
            var second = new Vehicle(owner.System, owner.Parent, "second", new(3, 0, 0), new(200, 0, 0));
            var third = new Vehicle(owner.System, owner.Parent, "third", new(4, 0, 0), new(300, 0, 0));
            first.FailPlanWrite = 2; // fail its rollback, after its successful initial capture write
            third.FailPlanWrite = 1; // fail the last capture write, after peers were already changed
            Check(!controller.Capture(owner, Pick(first), true), "partial capture fails visibly");
            Check(!controller.HasCapture, "partial failure clears owned group");
            Check(first.PlanWrites == 2 && second.PlanWrites == 2 && third.PlanWrites == 2,
                "each changed source receives one rollback attempt even when earlier restoration fails");
            Equal(second.GetVelocityCci(), new(200, 0, 0), "one rollback failure cannot prevent later peer restoration");
            foreach (var item in new[] { first, second, third })
                Check(ForcePhysicsPatches.FilterCapturedWorker(item.TakeUpdateStateIfOrphaned()) != null, "failed capture never strands a worker");
        }
    }

    private static void MixedAngryEitherPrimary()
    {
        foreach (bool clutterPrimary in new[] { false, true })
        {
            var (controller, owner, target) = Setup("mixed-" + clutterPrimary);
            using (controller)
            {
                var boundaryVessel = new Vehicle(owner.System, owner.Parent, "boundary", new(4, 0, 0), new(40, 0, 0));
                var outsideVessel = new Vehicle(owner.System, owner.Parent, "outside", new(4.001, 0, 0), new(50, 0, 0));
                var primary = new ClutterForceTarget(owner, "rock", new(2, 0, 0), new(60, 0, 0));
                var boundary = new ClutterForceTarget(owner, "tree", new(2, 2, 0), new(70, 0, 0));
                var duplicate = new ClutterForceTarget(owner, "tree", new(2, 2, 0), new(80, 0, 0));
                var outside = new ClutterForceTarget(owner, "far-rock", new(2, 2.001, 0), new(90, 0, 0));
                ClutterForceTargets.Candidates = [primary, boundary, duplicate, outside];
                var pick = clutterPrimary ? new ForcePick(primary, new(2, 0, 0)) : Pick(target);
                var gathered = new List<ForceTarget>();
                Check(ForceTargeting.Gather(owner, pick, true, gathered, out _), "mixed native/vessel query succeeds for either primary type");
                Check(gathered.Count == 4 && gathered.Count(t => t.Vehicle != null) == 2
                    && gathered.Count(t => t.Clutter != null) == 2,
                    "Angry primary of either type includes both kinds through exactly 2 m and excludes 2.001 m");
                Check(controller.Capture(owner, pick, true) && controller.CapturedCount == 4,
                    "mixed capture owns the combined deduplicated group");
                Check(primary.Captures == 1 && boundary.Captures == 1 && duplicate.Captures == 0 && outside.Captures == 0,
                    "same native key is promoted once and out-of-radius clutter remains untouched");
                Check(outsideVessel.HasPhysicsBubble && outsideVessel.PlanWrites == 0,
                    "vessel broad phase cannot capture a surface outside the exact two metre boundary");
                Universe.Time = 1;
                double s = Math.Sqrt(.5);
                owner.SetNativeState(new(10, 0, 0), new(3, 0, 0), new(0, 0, s, s), new(0, 0, 1));
                controller.SetOffset(new(0, 1, 0)); controller.Update(owner);
                Equal(target.GetPositionCci(), new(9, 2, 0), "mixed vessel follows the owner body frame");
                Equal(primary.State.Position, new(9, 2, 0), "mixed clutter uses the same rigid frame and offset");
                Equal(boundary.State.Position - primary.State.Position, new(-2, 0, 0),
                    "mixed group preserves the captured spatial layout under rotation");
                var vesselVelocity = target.GetVelocityCci(); var clutterVelocity = primary.State.Velocity;
                controller.Release(true);
                Check(Math.Abs((target.GetVelocityCci() - vesselVelocity).Length() - ForceMath.ThrowSpeed) < 1e-9
                    && Math.Abs((primary.State.Velocity - clutterVelocity).Length() - ForceMath.ThrowSpeed) < 1e-9,
                    "throw adds the same bounded outward speed to both target kinds");
                Check(primary.Releases == 1 && boundary.Releases == 1 && !primary.Captured && !boundary.Captured,
                    "release restores each native body exactly once");
                int vesselWrites = target.PlanWrites;
                var thrown = primary.State;
                controller.Release(true); controller.Reset();
                Check(primary.Releases == 1 && target.PlanWrites == vesselWrites && primary.State == thrown,
                    "repeated release/reset cannot throw or restore either kind twice");
                Check(controller.Capture(owner, clutterPrimary ? new ForcePick(primary, primary.State.Position) : Pick(target), false)
                    && controller.CapturedCount == 1, "ordinary Force captures only the selected type");
                controller.Release(false);
            }
        }
    }

    private static void CombinedLimitPreflight()
    {
        var (controller, owner, target) = Setup("limit");
        using (controller)
        {
            var vessels = new List<Vehicle> { target };
            for (int i = 1; i < 16; i++) vessels.Add(new(owner.System, owner.Parent, "vessel-" + i, new(2, 0, 0), double3.Zero));
            var clutter = Enumerable.Range(0, 17).Select(i => new ClutterForceTarget(owner, "clutter-" + i,
                new(2, 0, 0), double3.Zero)).ToArray();
            ClutterForceTargets.Candidates = clutter;
            Check(!controller.Capture(owner, Pick(target), true) && !controller.HasCapture,
                "33 mixed candidates reject the complete operation");
            Check(vessels.All(v => v.PlanWrites == 0 && v.HasPhysicsBubble) && clutter.All(c => c.Captures == 0),
                "combined overflow rejects before any vessel detach or native promotion");
            ClutterForceTargets.Candidates = clutter[..16];
            Check(controller.Capture(owner, Pick(target), true) && controller.CapturedCount == 32,
                "exactly 32 mixed candidates can be captured");
            controller.Release(false);
            Check(clutter[..16].All(c => c.Captures == 1 && c.Releases == 1), "every accepted native peer restores once");
            ClutterForceTargets.QueryFails = true;
            int writes = target.PlanWrites;
            Check(!controller.Capture(owner, Pick(target), true) && target.PlanWrites == writes,
                "a native query refusal cannot partially capture the already gathered vessels");
        }
    }

    private static void MixedRollbackAndInvalidation()
    {
        var (controller, owner, target) = Setup("mixed-rollback");
        using (controller)
        {
            var first = new ClutterForceTarget(owner, "first", new(2, 0, 0), new(12, 0, 0));
            var second = new ClutterForceTarget(owner, "second", new(3, 0, 0), new(13, 0, 0)) { FailCapture = true };
            ClutterForceTargets.Candidates = [first, second];
            Check(!controller.Capture(owner, Pick(target), true) && !controller.HasCapture,
                "failed later native promotion rolls back the whole mixed capture");
            Check(first.Captures == 1 && first.Releases == 1 && !first.Captured,
                "successful earlier promotion is restored once after later promotion failure");
            Equal(first.State.Velocity, new(12, 0, 0), "promotion rollback restores the original native velocity");
            second.FailCapture = false; second.FailWrite = 1;
            Check(!controller.Capture(owner, Pick(target), true), "a follow write failure rolls back promoted native peers and vessels");
            Check(first.Releases == 2 && second.Releases == 1 && !first.Captured && !second.Captured,
                "rollback plus defensive disposal does not restore a native peer twice");
            Equal(target.GetVelocityCci(), new(100, 0, 0), "mixed rollback restores the vessel velocity");
            Equal(second.State.Velocity, new(13, 0, 0), "mixed rollback restores the failing native source too");
            second.FailWrite = 0;
            Check(controller.Capture(owner, Pick(target), true), "mixed recapture after rollback");
            first.Live = false; controller.Update(owner);
            Check(!controller.HasCapture && !second.Captured && second.Releases == 2,
                "a stale native identity releases its surviving mixed peers");
            first.Live = true;
            Check(controller.Capture(owner, Pick(target), true), "mixed recapture before world replacement");
            Universe.CurrentSystem = new(); controller.Update(null);
            Check(!controller.HasCapture && !first.Captured && !second.Captured,
                "world replacement clears native ownership without following a stale frame");
        }
    }

    private static void MixedImmediateOffsetAndLocalBubble()
    {
        var (controller, owner, target) = Setup("local-bubble");
        using (controller)
        {
            owner.BodyRates = double3.Zero;
            var clutter = new ClutterForceTarget(owner, "local", new(2, 0, 0), double3.Zero);
            ClutterForceTargets.Candidates = [clutter];
            Check(controller.Capture(owner, Pick(target), true) && controller.CapturedCount == 2,
                "immediate-offset fixture captures a mixed pair");
            controller.SetOffset(new(0, 3, 0));
            controller.Release(true); // Deliberately no Update between changing offset and throwing.
            Equal(target.GetPositionCci(), new(2, 3, 0), "throw commits the final vessel offset before its next update");
            Equal(clutter.State.Position, new(2, 3, 0), "throw commits the final native offset before its next update");
            var thrown = new double3(3, 0, 0) + double3.NormalizeOrZero(new(2, 3, 0)) * ForceMath.ThrowSpeed;
            Equal(target.GetVelocityCci(), thrown, "vessel throw direction uses the final offset");
            Equal(clutter.State.Velocity, thrown, "native throw direction uses the same final offset");
            Check(controller.Capture(owner, Pick(target), true), "recapture before local bubble handoff");
            clutter.Local = false; controller.Update(owner);
            Check(!controller.HasCapture && !clutter.Captured && clutter.Releases == 2,
                "owner bubble handoff releases both kinds while an old native bubble survives");
            clutter.Local = true;
            Check(controller.Capture(owner, Pick(target), true), "recapture before teardown");
            controller.Reset();
            Check(!controller.HasCapture && clutter.Releases == 3 && JobSystems.VehicleSolver.Waits > 0,
                "teardown joins the native solver and restores the native lease exactly once");
        }
    }

    private static void InvalidOwnerAndJoinFailure()
    {
        var (controller, owner, target) = Setup("cleanup");
        using (controller)
        {
            Check(controller.Capture(owner, Pick(target), false), "cleanup capture");
            Program.ControlledVehicle = null; controller.Update(null);
            Check(!controller.HasCapture, "control change releases independently of saber");
            Program.ControlledVehicle = owner;
            Check(controller.Capture(owner, Pick(target), false), "join failure recapture");
            JobSystems.OrbitSolvers.ThrowOnWait = true;
            controller.Release(false);
            Check(!controller.HasCapture && controller.Status.Contains("could not"), "join exception reports and clears ownership");
            Check(ForcePhysicsPatches.FilterCapturedWorker(target.TakeUpdateStateIfOrphaned()) != null, "join exception cannot keep worker suppressed");
            JobSystems.OrbitSolvers.ThrowOnWait = false;
        }
    }

    private static void Equal(double3 actual, double3 expected, string message) =>
        Check((actual - expected).Length() < 1e-9, message + $"; actual {actual}, expected {expected}");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
