using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
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
        GroupSpreadAroundCentroid();
        BreakupKeepsMixedHold();
        BreakupPreflightAndPartialFailure();
        ConfigurableAngryRadius();
        CombinedLimitPreflight();
        InvalidCaptureLimits();
        MixedRollbackAndInvalidation();
        MixedImmediateOffsetAndLocalBubble();
        Console.WriteLine($"Force controller checks passed ({_checks} assertions: rigid mixed groups, configurable exact surface radius and combined capture limit, deduplication, once-only release/throw, rollback and world/key cleanup).");
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
        ClutterForceTargets.GatherCalls = 0;
        var parent = new ParentBody();
        var owner = new KittenEva(Universe.CurrentSystem, parent, "kitten", double3.Zero, new(3, 0, 0));
        owner.BodyRates = new(0, 0, 1);
        Program.ControlledVehicle = owner;
        var target = new Vehicle(Universe.CurrentSystem, parent, "object", new(2, 0, 0), new(100, 0, 0));
        var controller = new ForceController(); controller.Install(new Harmony("purr2d2.force.fixture." + id));
        return (controller, owner, target);
    }

    private static ForcePick Pick(Vehicle target) => new(target, target.Parts.Items[0], target.Parent, target.System, double3.Zero);

    private static void BreakupKeepsMixedHold()
    {
        var (controller, owner, target) = Setup("breakup-mixed");
        using (controller)
        {
            target.BreakupOffsets = [new(-.5, 0, 0), new(.5, 0, 0)];
            var neighbor = new Vehicle(owner.System, owner.Parent, "neighbor", new(6, 0, 0), double3.Zero);
            var clutter = new ClutterForceTarget(owner, "breakup-clutter", new(4, 0, 0), double3.Zero);
            ClutterForceTargets.Candidates = [clutter];
            Check(controller.Capture(owner, Pick(target), true, 10), "mixed breakup group captures");
            controller.SetOffset(new(1, 2, 0)); controller.SetSpread(.5); controller.Update(owner);
            var originalCenter = target.GetPositionCci(); var neighborPosition = neighbor.GetPositionCci();
            var clutterPosition = clutter.State.Position;
            controller.TryGetPoseTargetBody(owner, out var aim);
            Check(target.BreakupCommits == 0, "changing displayed spread alone does not break a captured vehicle");
            controller.BreakApart(owner);
            var pieces = owner.System.All.Items.Where(v => v.Id.StartsWith("object_piece")).OrderBy(v => v.Id).ToArray();
            Check(target.Drained && target.IsDisposed && target.BreakupCommits == 1 && pieces.Length == 2
                && controller.CapturedCount == 4, "breakup replaces only the intact vehicle and retains all resulting debris");
            Equal(pieces[0].GetPositionCci(), originalCenter + new double3(-.5, 0, 0), "first piece starts on the displayed compressed source");
            Equal(pieces[1].GetPositionCci(), originalCenter + new double3(.5, 0, 0), "second piece starts on the displayed compressed source");
            Equal(neighbor.GetPositionCci(), neighborPosition, "breakup cannot move an unchanged captured neighbor");
            Equal(clutter.State.Position, clutterPosition, "breakup cannot move or release held clutter");
            controller.TryGetPoseTargetBody(owner, out var newAim); Equal(newAim, aim, "primary pose target does not jump during replacement");
            Check(pieces.All(p => !p.HasPhysicsBubble && ForcePhysicsPatches.FilterCapturedWorker(p.TakeUpdateStateIfOrphaned()) == null),
                "every replacement is detached and published to the ordinary held-worker filter");
            controller.SetSpread(1); controller.Update(owner);
            Equal(pieces[1].GetPositionCci() - pieces[0].GetPositionCci(), new(2, 0, 0),
                "subsequent spread scales the new pieces around their existing hold frame");
            var before = pieces.Select(p => p.GetPositionCci()).ToArray();
            controller.BreakApart(owner);
            Check(controller.CapturedCount == 4 && target.BreakupCommits == 1, "ordinary debris does not recursively break up");
            controller.SetOffset(new(2, 2, 0)); controller.Update(owner);
            for (int i = 0; i < pieces.Length; i++) Equal(pieces[i].GetPositionCci(), before[i] + double3.UnitX,
                "existing translation moves replacement debris normally");
            controller.Release(true);
            Check(!controller.HasCapture && !clutter.Captured && pieces.All(p => ForcePhysicsPatches.FilterCapturedWorker(p.TakeUpdateStateIfOrphaned()) != null),
                "throw releases every replacement and the original mixed peers into ordinary physics");
        }
    }

    private static void BreakupPreflightAndPartialFailure()
    {
        var (controller, owner, target) = Setup("breakup-failures");
        using (controller)
        {
            target.BreakupOffsets = [new(-.5, 0, 0), new(.5, 0, 0)];
            var second = new Vehicle(owner.System, owner.Parent, "second", new(6, 0, 0), double3.Zero)
                { BreakupOffsets = [new(0, -.5, 0), new(0, .5, 0)], FailBreakupPreparation = true };
            Check(controller.Capture(owner, Pick(target), true, 10), "two intact vehicles capture before breakup failure");
            controller.BreakApart(owner);
            Check(!target.Drained && !second.Drained && target.BreakupCommits == 0 && controller.CapturedCount == 2
                && controller.StatusSeverity == ForceStatusSeverity.Warning,
                "all sources remain intact when any peer fails native preparation");
            second.FailBreakupPreparation = false;
            target.BreakupOffsets = new double3[ForceCaptureLimits.Maximum + 1];
            controller.BreakApart(owner);
            Check(!target.Drained && !second.Drained && controller.Status.Contains("exceed"),
                "the complete replacement count is bounded before any irreversible operation");
            target.BreakupOffsets = [new(-.5, 0, 0), new(.5, 0, 0)];
            second.FailBreakupCommit = true;
            controller.BreakApart(owner);
            Check(target.Drained && !second.Drained && controller.CapturedCount == 3,
                "a later commit failure retains both already committed debris and the surviving source");
            controller.SetOffset(new(0, 1, 0)); controller.Update(owner);
            Check(controller.HasCapture && controller.CapturedCount == 3 && controller.StatusSeverity == ForceStatusSeverity.Warning,
                "partial native breakup remains a valid ordinary hold for movement and release");
            controller.Release(false);
        }
    }

    private static void RigidHoldAndRelease()
    {
        var (controller, owner, target) = Setup("follow");
        using (controller)
        {
            Check(controller.Capture(owner, Pick(target), false)
                && controller.StatusSeverity == ForceStatusSeverity.None, "capture accepted without a warning");
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
            Check(controller.StatusSeverity == ForceStatusSeverity.None, "successful release clears prior Force warnings");
            Equal(target.GetPositionCci(), new(9, 2, 0), "release retains current pose");

            Check(controller.Capture(owner, Pick(target), false), "capture before a follow failure");
            controller.SetOffset(new(0, 1, 0));
            target.FailPlanWrite = target.PlanWrites + 1;
            controller.Update(owner);
            Check(!controller.HasCapture && controller.StatusSeverity == ForceStatusSeverity.Error,
                "a failed follow update releases ownership and records an explicit error");
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
            inherited = target.GetVelocityCci();
            controller.Release(true, 200);
            Equal(target.GetVelocityCci() - inherited, new(200, 0, 0),
                "release snapshots and applies a selected maximum throw speed");
            Check(controller.Capture(owner, Pick(target), false), "recapture after a selected-speed throw");
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
            Check(controller.StatusSeverity == ForceStatusSeverity.Error,
                "a failed capture rollback records an explicit error severity");
            Check(!controller.HasCapture, "partial failure clears owned group");
            Check(first.PlanWrites == 2 && second.PlanWrites == 2 && third.PlanWrites == 2,
                "each changed source receives one rollback attempt even when earlier restoration fails");
            Equal(second.GetVelocityCci(), new(200, 0, 0), "one rollback failure cannot prevent later peer restoration");
            foreach (var item in new[] { first, second, third })
                Check(ForcePhysicsPatches.FilterCapturedWorker(item.TakeUpdateStateIfOrphaned()) != null, "failed capture never strands a worker");
        }

        var (releaseController, releaseOwner, releaseTarget) = Setup("partial-release");
        using (releaseController)
        {
            var badRelease = new ClutterForceTarget(releaseOwner, "bad-release", new(2, 0, 0), double3.Zero)
                { FailRelease = true };
            ClutterForceTargets.Candidates = [badRelease];
            Check(releaseController.Capture(releaseOwner, Pick(releaseTarget), true),
                "mixed target captured before partial release failure");
            releaseController.Release(false);
            Check(!releaseController.HasCapture && releaseController.StatusSeverity == ForceStatusSeverity.Warning,
                "a partial release failure records a warning while clearing controller ownership");
            releaseController.Reset();
            Check(releaseController.StatusSeverity == ForceStatusSeverity.None,
                "reset clears a stale release warning after cleanup");
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
                Check(ForceTargeting.Gather(owner, pick, true, gathered, out _, 2), "mixed native/vessel query succeeds for either primary type");
                Check(gathered.Count == 4 && gathered.Count(t => t.Vehicle != null) == 2
                    && gathered.Count(t => t.Clutter != null) == 2,
                    "Angry primary of either type includes both kinds through exactly 2 m and excludes 2.001 m");
                Check(controller.Capture(owner, pick, true, 2) && controller.CapturedCount == 4,
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

    private static void GroupSpreadAroundCentroid()
    {
        var (controller, owner, first) = Setup("spread-centroid");
        using (controller)
        {
            double half = Math.Sqrt(.5);
            var second = new Vehicle(owner.System, owner.Parent, "spread-second", new(4, 0, 0), new(20, 0, 0));
            second.SetNativeState(new(4, 0, 0), new(20, 0, 0), new(0, 0, half, half), new(0, 0, 0));
            var clutter = new ClutterForceTarget(owner, "spread-rock", new(6, 0, 0), new(30, 0, 0));
            ClutterForceTargets.Candidates = [clutter];
            var primaryPick = new ForcePick(first, first.Parts.Items[0], first.Parent, first.System, new(0, .5, 0));
            Check(controller.Capture(owner, primaryPick, true, angryRadius: 10) && controller.CapturedCount == 3,
                "a mixed three-object group captures before spread changes");
            Check(controller.GroupSpread == 1, "every new capture starts at unit spread");
            Check(controller.TryGetPoseTargetBody(owner, out var initialAim), "the selected Force hit point remains available as a pose anchor");
            Equal(initialAim, new(2, .5, 0), "the primary hit point retains its local offset before spreading");
            var secondRotation = second.GetBody2Cci();

            controller.SetSpread(.5);
            controller.Update(owner); // Universe.Time is unchanged: spread dirtiness must defeat the early-out.
            Equal(first.GetPositionCci(), new(3, 0, 0), "compressed primary stays centered around the group centroid");
            Equal(second.GetPositionCci(), new(4, 0, 0), "the group centroid object stays at the same point");
            Equal(clutter.State.Position, new(5, 0, 0), "mixed physical clutter compresses with captured vessels");
            Equal((first.GetPositionCci() + second.GetPositionCci() + clutter.State.Position) / 3,
                new(4, 0, 0), "uniform compression preserves the original group centroid");
            Check(Math.Abs(doubleQuat.Dot(second.GetBody2Cci(), secondRotation)) > 1 - 1e-12,
                "spread changes preserve each object's captured orientation");
            Check(controller.TryGetPoseTargetBody(owner, out var compressedAim), "a compressed group keeps an available pose target");
            Equal(compressedAim, new(3, .5, 0), "the selected hit point follows its primary object while preserving the unscaled local surface offset");

            controller.SetOffset(new(0, 1, 0)); controller.Update(owner);
            Equal((first.GetPositionCci() + second.GetPositionCci() + clutter.State.Position) / 3,
                new(4, 1, 0), "group translation moves the centroid while retaining compressed spacing");
            Check(controller.TryGetPoseTargetBody(owner, out var translatedAim), "translated spread retains its pose target");
            Equal(translatedAim, new(3, 1.5, 0), "pose target follows the compressed primary plus group translation");
            controller.Release(false);
            Check(!controller.HasCapture && controller.GroupSpread == 1,
                "release uses displayed spacing and clears spread ownership for the next capture");
            Equal(first.GetPositionCci(), new(3, 1, 0), "released primary keeps the compressed displayed pose");
            Equal(second.GetPositionCci(), new(4, 1, 0), "released vessel keeps its compressed displayed pose");
            Equal(clutter.State.Position, new(5, 1, 0), "released clutter keeps the same compressed displayed pose");
        }

        var (singleController, singleOwner, singleTarget) = Setup("single-spread");
        using (singleController)
        {
            var singlePick = new ForcePick(singleTarget, singleTarget.Parts.Items[0], singleTarget.Parent,
                singleTarget.System, new(0, .75, 0));
            Check(singleController.Capture(singleOwner, singlePick, false), "single-object spread capture starts normally");
            Check(singleController.TryGetPoseTargetBody(singleOwner, out var singleAim), "single capture exposes its selected hit point");
            Equal(singleAim, new(2, .75, 0), "single-capture hit point begins at its primary surface");
            singleController.SetSpread(ForceMath.MinimumGroupSpread);
            singleController.Update(singleOwner);
            Check(singleController.TryGetPoseTargetBody(singleOwner, out var compressedSingleAim), "compressed single capture retains its pose target");
            Equal(compressedSingleAim, singleAim, "compressing one object leaves its selected hit point stationary");
            singleController.SetSpread(ForceMath.MaximumGroupSpread);
            singleController.Update(singleOwner);
            Equal(singleTarget.GetPositionCci(), new(2, 0, 0), "spreading a single capture cannot translate its centroid");
            Check(singleController.TryGetPoseTargetBody(singleOwner, out var expandedSingleAim), "expanded single capture retains its pose target");
            Equal(expandedSingleAim, singleAim, "expanding one object also leaves its selected hit point stationary");
            Check(singleController.GroupOffsetBody == double3.Zero, "spread has no effect on single-capture translation state");
        }
    }

    private static void ConfigurableAngryRadius()
    {
        Check(ForceMath.DefaultAngryRadius == 10, "Angry Force defaults to a ten metre capture radius");
        foreach (double radius in new[] { 1.0, 10, 50 })
        foreach (bool clutterPrimary in new[] { false, true })
        {
            var (controller, owner, target) = Setup("radius-" + radius + "-" + clutterPrimary);
            using (controller)
            {
                var boundaryVessel = new Vehicle(owner.System, owner.Parent, "boundary", new(2 + radius, 0, 0), double3.Zero);
                var outsideVessel = new Vehicle(owner.System, owner.Parent, "outside", new(2 + radius + .001, 0, 0), double3.Zero);
                var primary = new ClutterForceTarget(owner, "primary", new(2, 0, 0), double3.Zero);
                var boundary = new ClutterForceTarget(owner, "boundary-rock", new(2, radius, 0), double3.Zero);
                var outside = new ClutterForceTarget(owner, "outside-rock", new(2, radius + .001, 0), double3.Zero);
                ClutterForceTargets.Candidates = [primary, boundary, outside];
                var pick = clutterPrimary ? new ForcePick(primary, new(2, 0, 0)) : Pick(target);
                bool captured = radius == 10 ? controller.Capture(owner, pick, true)
                    : controller.Capture(owner, pick, true, radius);
                Check(captured && controller.CapturedCount == 4 && !boundaryVessel.HasPhysicsBubble
                    && boundary.Captures == 1 && outsideVessel.HasPhysicsBubble && outside.Captures == 0,
                    $"radius {radius} m includes the vessel/clutter boundary and excludes surfaces .001 m beyond it for either primary");
                controller.Release(false);
                foreach (double invalid in new[] { .999, 50.001, double.NaN, double.PositiveInfinity })
                {
                    int writes = target.PlanWrites, promotions = primary.Captures;
                    Check(!controller.Capture(owner, pick, true, invalid) && target.PlanWrites == writes
                        && primary.Captures == promotions, "invalid radius rejects the query before any source mutation");
                }
            }
        }
    }

    private static void CombinedLimitPreflight()
    {
        Check(ForceCaptureLimits.Default == 128, "the default group limit increases from 32 to 128 objects");

        var (controller, owner, target) = Setup("nearest-overflow");
        using (controller)
        {
            var nearVessel = new Vehicle(owner.System, owner.Parent, "near-vessel", new(2.2, 0, 0), double3.Zero);
            var farVessel = new Vehicle(owner.System, owner.Parent, "far-vessel", new(4, 0, 0), double3.Zero);
            var nearClutter = new ClutterForceTarget(owner, "near-clutter", new(2.4, 0, 0), double3.Zero);
            var farClutter = new ClutterForceTarget(owner, "far-clutter", new(3, 0, 0), double3.Zero);
            ClutterForceTargets.Candidates = [farClutter, nearClutter];
            Check(controller.Capture(owner, Pick(target), true, captureLimit: 3)
                && controller.CapturedCount == 3 && controller.Status == "Holding 3 objects.",
                "ordinary mixed overflow succeeds at the exact configured bound without an overflow warning");
            Check(!target.HasPhysicsBubble && !nearVessel.HasPhysicsBubble && nearClutter.Captured,
                "overflow always retains the selected primary and chooses the closest vessel and clutter origins");
            Check(farVessel.HasPhysicsBubble && !farClutter.Captured,
                "farther eligible vessel and clutter candidates remain untouched");
            controller.Release(false);

            owner.System.All.Items.Reverse();
            ClutterForceTargets.Candidates = [nearClutter, farClutter];
            Check(controller.Capture(owner, Pick(target), true, captureLimit: 3) && controller.CapturedCount == 3
                && nearClutter.Captured && !farClutter.Captured && farVessel.HasPhysicsBubble,
                "nearest mixed selection is deterministic when vessel and native enumeration order reverses");
            controller.Release(false);
        }

        (controller, owner, target) = Setup("one-primary");
        using (controller)
        {
            _ = new Vehicle(owner.System, owner.Parent, "ignored-vessel", new(2.1, 0, 0), double3.Zero);
            ClutterForceTargets.Candidates = [new(owner, "ignored-clutter", new(2.1, 0, 0), double3.Zero)];
            ClutterForceTargets.QueryFails = true;
            Check(controller.Capture(owner, Pick(target), true, captureLimit: 1)
                && controller.CapturedCount == 1 && ClutterForceTargets.GatherCalls == 0,
                "a one-object cap captures the selected primary without scanning native proximity geometry");
            controller.Release(false);
        }

        (controller, owner, target) = Setup("maximum-overflow");
        using (controller)
        {
            var clutter = Enumerable.Range(0, ForceCaptureLimits.Maximum + 3)
                .Select(i => new ClutterForceTarget(owner, $"clutter-{i:000}", new(2.001 + i * .001, 0, 0), double3.Zero))
                .ToArray();
            ClutterForceTargets.Candidates = clutter.Reverse().ToArray();
            Check(controller.Capture(owner, Pick(target), true, captureLimit: ForceCaptureLimits.Maximum)
                && controller.CapturedCount == ForceCaptureLimits.Maximum,
                "the maximum 512-object cap truncates a larger eligible set to exactly 512 captures");
            Check(clutter.Take(ForceCaptureLimits.Maximum - 1).All(candidate => candidate.Captured)
                && clutter.Skip(ForceCaptureLimits.Maximum - 1).All(candidate => !candidate.Captured),
                "maximum overflow retains the 511 closest peers beside the selected primary");
            controller.Release(false);
        }

        (controller, owner, target) = Setup("query-refusal");
        using (controller)
        {
            ClutterForceTargets.QueryFails = true;
            Check(!controller.Capture(owner, Pick(target), true, captureLimit: 2)
                && target.PlanWrites == 0 && !controller.HasCapture,
                "a genuine native query refusal cannot partially capture already gathered vessels");
        }

        foreach (int limit in new[] { 1, 32, ForceCaptureLimits.Default, ForceCaptureLimits.Maximum })
        {
            Matrix(limit, "vessels", clutterPrimary: false);
            Matrix(limit, "clutter", clutterPrimary: true);
            Matrix(limit, "mixed", clutterPrimary: false);
            Matrix(limit, "mixed", clutterPrimary: true);
        }

        void Matrix(int limit, string composition, bool clutterPrimary)
        {
            var fixture = Setup($"matrix-{limit}-{composition}-{clutterPrimary}");
            using var matrixController = fixture.Controller;
            var matrixOwner = fixture.Owner; var matrixTarget = fixture.Target;
            int total = limit + 2;
            var vessels = new List<Vehicle>();
            var clutter = new List<ClutterForceTarget>();
            if (composition != "clutter")
            {
                vessels.Add(matrixTarget);
                int vesselCount = composition == "vessels" ? total : Math.Max(1, total / 2);
                for (int i = 1; i < vesselCount; i++)
                    vessels.Add(new(matrixOwner.System, matrixOwner.Parent, $"vessel-{i:000}",
                        new(2 + i * .001, 0, 0), double3.Zero));
            }
            else matrixOwner.System.All.Items.Remove(matrixTarget);
            int clutterCount = total - vessels.Count;
            for (int i = 0; i < clutterCount; i++)
                clutter.Add(new(matrixOwner, $"clutter-{i:000}",
                    new(2 + (vessels.Count + i) * .001, 0, 0), double3.Zero));
            ClutterForceTarget? clutterPick = clutterPrimary ? clutter[^1] : null;
            ClutterForceTargets.Candidates = clutterPick == null ? [.. clutter]
                : [.. clutter, clutterPick]; // A repeated primary identity consumes no second slot.
            var pick = clutterPick == null ? Pick(matrixTarget) : new ForcePick(clutterPick, clutterPick.State.Position);
            bool captured = limit == ForceCaptureLimits.Default
                ? matrixController.Capture(matrixOwner, pick, true)
                : matrixController.Capture(matrixOwner, pick, true, captureLimit: limit);
            int mutated = vessels.Count(vehicle => !vehicle.HasPhysicsBubble) + clutter.Count(candidate => candidate.Captured);
            Check(captured && matrixController.CapturedCount == limit && mutated == limit
                && matrixController.Status == $"Holding {limit} object{(limit == 1 ? "" : "s")}.",
                $"{composition} overflow captures exactly cap {limit} without warning using the {(limit == ForceCaptureLimits.Default ? "default" : "explicit")} setting");
            Check(clutterPick?.Captured ?? !matrixTarget.HasPhysicsBubble,
                $"{composition} overflow at cap {limit} always retains its selected {(clutterPrimary ? "clutter" : "vessel")} primary");
            matrixController.Release(false);
        }
    }

    private static void InvalidCaptureLimits()
    {
        var (controller, owner, target) = Setup("invalid-limit");
        using (controller)
        foreach (int invalid in new[] { int.MinValue, 0, ForceCaptureLimits.Maximum + 1, int.MaxValue })
        foreach (bool angry in new[] { false, true })
        {
            var gathered = new List<ForceTarget>();
            Check(!ForceTargeting.Gather(owner, Pick(target), angry, gathered, out _, captureLimit: invalid)
                && gathered.Count == 0, "invalid capture limits reject proximity gathering before adding candidates");
            Check(!controller.Capture(owner, Pick(target), angry, captureLimit: invalid)
                && target.PlanWrites == 0 && !controller.HasCapture,
                "invalid capture limits cannot allocate an unbounded group or mutate a source");
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
            Check(!controller.HasCapture && !first.Captured && !second.Captured
                && controller.StatusSeverity == ForceStatusSeverity.Warning,
                "world replacement clears native ownership without following a stale frame and records a warning");
            controller.Reset();
            Check(controller.StatusSeverity == ForceStatusSeverity.None,
                "reset clears the obsolete world-change warning");
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
            Check(!controller.HasCapture && clutter.Releases == 3 && JobSystems.VehicleSolver.Waits > 0
                && controller.StatusSeverity == ForceStatusSeverity.None,
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
            Check(!controller.HasCapture && controller.StatusSeverity == ForceStatusSeverity.Warning,
                "control change releases independently of saber and records a lifecycle warning");
            Program.ControlledVehicle = owner;
            Check(controller.Capture(owner, Pick(target), false), "join failure recapture");
            JobSystems.OrbitSolvers.ThrowOnWait = true;
            controller.Release(false);
            Check(!controller.HasCapture && controller.Status.Contains("could not")
                && controller.StatusSeverity == ForceStatusSeverity.Error,
                "join exception reports an explicit error and clears ownership");
            Check(ForcePhysicsPatches.FilterCapturedWorker(target.TakeUpdateStateIfOrphaned()) != null, "join exception cannot keep worker suppressed");
            JobSystems.OrbitSolvers.ThrowOnWait = false;
        }
    }

    private static void Equal(double3 actual, double3 expected, string message) =>
        Check((actual - expected).Length() < 1e-9, message + $"; actual {actual}, expected {expected}");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
