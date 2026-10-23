using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Force;

internal static class ForceControllerChecks
{
    public static void Main()
    {
        RigidHoldAndRelease();
        ThrowAndTopology();
        RollbackContinuesAfterFailure();
        InvalidOwnerAndJoinFailure();
        Console.WriteLine("Force controller checks passed (worker freeze, rigid group/epoch/offset, release/throw, topology/control changes, rollback and exception cleanup).");
    }

    private static (ForceController Controller, KittenEva Owner, Vehicle Target) Setup(string id)
    {
        Universe.CurrentSystem = new(); Universe.Time = 0;
        Program.EditorFlag = false;
        JobSystems.OrbitSolvers.ThrowOnWait = false;
        JobSystems.NearestOrbitAndPerformanceWorker.ThrowOnWait = false;
        ForceTargeting.Candidates = [];
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
            ForceTargeting.Candidates = [first, second, third];
            first.FailPlanWrite = 2; // fail its rollback, after its successful initial capture write
            third.FailPlanWrite = 1; // fail the last capture write, after peers were already changed
            Check(!controller.Capture(owner, Pick(first), true), "partial capture fails visibly");
            Check(!controller.HasCapture, "partial failure clears owned group");
            Equal(second.GetVelocityCci(), new(200, 0, 0), "one rollback failure cannot prevent later peer restoration");
            foreach (var item in ForceTargeting.Candidates)
                Check(ForcePhysicsPatches.FilterCapturedWorker(item.TakeUpdateStateIfOrphaned()) != null, "failed capture never strands a worker");
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
    { if (!condition) throw new InvalidOperationException(message); }
}
