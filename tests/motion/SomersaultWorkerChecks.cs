using System.Reflection;
using System.Reflection.Emit;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
using Purr2D2.Runtime;

internal static class SomersaultWorkerChecks
{
    private static int Main()
    {
        HookTransformation();
        ConfigurableComboIntervals();
        OneShotAndAppliedAcknowledgement();
        RunningLaunchUsesPreparedSpeed();
        PausedBoundaryAndInterruption();
        FinalCopiesAndFailureAtomicity();
        AirborneComboTransactions();
        ComboPauseAndLifecycle();
        InputHandoffAcknowledgement();
        return 0;
    }

    private static SomersaultMotionController Setup(out KittenEva owner, out KittenEva other)
    {
        Universe.CurrentSystem = new(); Universe.Speed = 1; Universe.Last = Step(0, 0);
        Program.EditorFlag = false;
        Program.PlayerTime = 0;
        owner = new(); other = new();
        Program.ControlledVehicle = owner;
        Universe.Vehicles = [owner, other];
        var motion = new SomersaultMotionController(() => Program.PlayerTime);
        motion.Install(new Harmony("purr2d2.motion-fixture." + Guid.NewGuid().ToString("N")));
        Check(motion.CanStart(owner, out var reason), "real Harmony installs the guarded production handoff: " + reason);
        return motion;
    }

    private static void InputHandoffAcknowledgement()
    {
        using var motion = Setup(out var owner, out _);
        SaberRuntime.RunSomersaultInputChecks(motion, owner);
    }

    private static void Launch(SomersaultMotionController motion, KittenEva owner)
    {
        Check(motion.Request(owner, SomersaultDirection.Backward), "first jump starts a grounded combo");
        var step = Step(0, .02);
        Universe.PrepareVehicleWorkers(step); Universe.Apply(step); motion.Update(owner);
    }

    private static void ConfigurableComboIntervals()
    {
        using var motion = Setup(out var owner, out _);
        Check(motion.ComboIntervalSeconds == .75, "the production controller starts with a 0.75-second combo interval");
        Launch(motion, owner);
        Program.PlayerTime = .750001;
        Check(!motion.CanStart(owner, out var reason) && reason.Contains("0.75 s")
            && !motion.Request(owner, SomersaultDirection.Forward),
            "expired default combo feedback reports 0.75 seconds and rejects a late follow-up");
        Program.PlayerTime = .75;
        Check(motion.Request(owner, SomersaultDirection.Forward), "the production controller accepts the inclusive default boundary");

        motion.Reset(); SetGrounded(owner); Program.PlayerTime = 2;
        motion.SetComboIntervalSeconds(.1); Launch(motion, owner);
        Program.PlayerTime = 2.100001;
        Check(!motion.CanStart(owner, out reason) && reason.Contains("0.1 s")
            && !motion.Request(owner, SomersaultDirection.Forward),
            "the minimum configured interval controls both availability feedback and timing");
        Program.PlayerTime = 2.1;
        Check(motion.Request(owner, SomersaultDirection.Forward), "the minimum interval accepts its exact deadline");

        motion.Reset(); SetGrounded(owner); Program.PlayerTime = 4;
        motion.SetComboIntervalSeconds(1.5); Launch(motion, owner);
        Program.PlayerTime = 5.500001;
        Check(!motion.Request(owner, SomersaultDirection.Forward), "the maximum interval still rejects presses beyond its deadline");
        Program.PlayerTime = 5.5;
        Check(motion.Request(owner, SomersaultDirection.Forward), "the maximum interval accepts its exact deadline");
    }

    private static void AirborneComboTransactions()
    {
        using var motion = Setup(out var owner, out _);
        owner.FrameVelocity = new(4200, -1700, 300);
        owner.Kinematic.VelocityPhys = owner.FrameVelocity;
        Launch(motion, owner);
        var firstVelocity = owner.Kinematic.VelocityPhys;
        double upSpeed = owner.FrameVelocity.Z - firstVelocity.Z;
        float angle = motion.RotationRadians;
        Program.PlayerTime = .750001;
        Check(!motion.Request(owner, SomersaultDirection.Forward), "just outside the first player-time window rejects a boost");
        Program.PlayerTime = .75;
        Check(motion.Request(owner, SomersaultDirection.Forward), "exactly 0.75 player seconds accepts a second jump despite stale terrain at launch");
        Check(!motion.Request(owner, SomersaultDirection.Left) && motion.Direction == SomersaultDirection.Backward,
            "pending duplicate presses cannot add jumps or change the original rotation direction");
        Check(owner.Kinematic.VelocityPhys == firstVelocity && motion.RotationRadians == angle,
            "a second press changes neither live velocity nor current pose before worker acknowledgement");
        Program.PlayerTime = .8; // Delayed worker may run after the deadline.
        var boost = Step(.02, .12);
        Universe.PrepareVehicleWorkers(boost);
        var boosted = owner.Worker.Kinematic.VelocityPhys;
        Check((boosted - firstVelocity - new double3(0, 0, -upSpeed)).Length() < 1e-8,
            "the timed press survives worker delay and adds exactly one upward speed to current ascent");
        Check(owner.Worker.LocomotionState.LiftoffTime == 0
            && !owner.Worker.LocomotionState.ContactClearedSinceLiftoff && owner.Worker.LocomotionCommand.JumpImpulse == 0,
            "follow-up leaves native contact history intact and never arms another native jump impulse");
        Universe.Apply(boost); motion.Update(owner);
        motion.Update(owner);
        Program.PlayerTime = 1;
        Check(motion.Request(owner, SomersaultDirection.Right), "third press uses the second accepted press time rather than original launch time");
        // A descending fixture proves the boost adds rather than imposing an
        // ascent target; horizontal and large moving-frame motion remain intact.
        owner.Kinematic.VelocityPhys = boosted with { Z = owner.FrameVelocity.Z + 12 };
        var descending = owner.Kinematic.VelocityPhys;
        var third = Step(.12, .22);
        Universe.PrepareVehicleWorkers(third);
        Check((owner.Worker.Kinematic.VelocityPhys - descending - new double3(0, 0, -upSpeed)).Length() < 1e-8,
            "third jump adds upward momentum during descent without cancelling remaining descent or horizontal travel");
        Universe.Apply(third); motion.Update(owner);
        var finalVelocity = owner.Kinematic.VelocityPhys;
        Check(!motion.Request(owner, SomersaultDirection.Backward) && !motion.CanStart(owner, out _),
            "fourth jump is refused within an otherwise valid window");
        for (int i = 0; i < 10; i++)
        {
            var next = Step(.22 + i * .2, .42 + i * .2);
            Universe.PrepareVehicleWorkers(next);
            Check(owner.Worker.Kinematic.VelocityPhys == finalVelocity, "later prepared snapshots never replay any accepted impulse");
            owner.Worker.LocomotionState.ContactClearedSinceLiftoff = true;
            Universe.Apply(next); motion.Update(owner);
        }
        Check(Math.Abs(motion.RotationRadians - 3 * MathF.Tau) < 1e-5,
            "production controller presents three complete acknowledged rotations");
        owner.LocomotionState.Mode = owner.Worker.LocomotionState.Mode = LocomotionMode.Grounded;
        var contact = Step(2.22, 2.42);
        Universe.PrepareVehicleWorkers(contact); Universe.Apply(contact); motion.Update(owner);
        Check(!motion.CanStart(owner, out _), "landing immediately closes follow-up eligibility during recovery");
        var recover = Step(2.42, 2.62);
        Universe.PrepareVehicleWorkers(recover); Universe.Apply(recover); motion.Update(owner);
        Check(!motion.Active, "contact-driven recovery releases the complete combo");
        SetGrounded(owner);
        Check(motion.Request(owner, SomersaultDirection.Forward), "landing resets the count for a new explicit grounded jump");
    }

    private static void ComboPauseAndLifecycle()
    {
        using var motion = Setup(out var owner, out _);
        Launch(motion, owner);
        Program.PlayerTime = .2;
        Check(motion.Request(owner, SomersaultDirection.Backward), "a follow-up can wait at the pause boundary");
        var velocity = owner.Kinematic.VelocityPhys;
        Universe.Speed = 0; motion.Update(owner);
        Universe.PrepareVehicleWorkers(Step(.02, .02)); Universe.Apply(Step(.02, .02));
        Universe.Speed = 1; motion.Update(owner);
        Universe.PrepareVehicleWorkers(Step(.02, .04)); Universe.Apply(Step(.02, .04)); motion.Update(owner);
        Check(owner.Kinematic.VelocityPhys == velocity && !motion.Request(owner, SomersaultDirection.Backward),
            "pause cancels an undispatched boost and closes the combo instead of replaying it after resume");
        motion.Reset(); SetGrounded(owner); Program.PlayerTime = 1;
        Launch(motion, owner);
        Program.PlayerTime = 1.2; Check(motion.Request(owner, SomersaultDirection.Backward), "a fresh combo accepts another follow-up");
        var boost = Step(.02, .04);
        Universe.PrepareVehicleWorkers(boost); Universe.Apply(boost);
        var boosted = owner.Kinematic.VelocityPhys;
        Universe.Speed = 0; motion.Update(owner);
        for (int i = 0; i < 3; i++)
        { Universe.PrepareVehicleWorkers(Step(.04, .04)); Universe.Apply(Step(.04, .04)); motion.Update(owner); }
        Universe.Speed = 1; motion.Update(owner);
        Universe.PrepareVehicleWorkers(Step(.04, .06)); Universe.Apply(Step(.04, .06)); motion.Update(owner);
        Check(motion.Active && owner.Kinematic.VelocityPhys == boosted,
            "a boost applied immediately before pause acknowledges on resume without a duplicate impulse");
        for (int i = 0; i < 7; i++)
        {
            var flight = Step(.06 + i * .2, .26 + i * .2);
            Universe.PrepareVehicleWorkers(flight); Universe.Apply(flight); motion.Update(owner); motion.Update(owner);
        }
        Check(Math.Abs(motion.RotationRadians - 2 * MathF.Tau) < 1e-5
            && owner.Kinematic.VelocityPhys == boosted,
            "pause-boundary boost produces exactly one extra full turn across repeated settled ticks");
        motion.Reset(); SetGrounded(owner); Program.PlayerTime = 2; Launch(motion, owner);
        Program.PlayerTime = 2.2; motion.Request(owner, SomersaultDirection.Backward);
        owner.PendingLadderAttach = true;
        velocity = owner.Kinematic.VelocityPhys;
        Universe.PrepareVehicleWorkers(Step(.02, .04));
        Check(!motion.Active && owner.Worker.Kinematic.VelocityPhys == velocity,
            "final worker failure clears the combo without a half-applied boost");
        owner.PendingLadderAttach = false; SetGrounded(owner);
        Check(motion.Request(owner, SomersaultDirection.Forward), "failure reset allows a new grounded command");
        owner.HasPhysicsBubble = false; motion.Update(owner);
        Check(!motion.Active && motion.Owner is null, "loss of the native physics area clears pending combo ownership");
        owner.HasPhysicsBubble = true; SetGrounded(owner); Program.PlayerTime = 3;
        Launch(motion, owner);
        Program.PlayerTime = 3.2; motion.Request(owner, SomersaultDirection.Forward);
        owner.LocomotionState.Mode = owner.Worker.LocomotionState.Mode = LocomotionMode.Grounded;
        velocity = owner.Kinematic.VelocityPhys;
        Universe.PrepareVehicleWorkers(Step(.02, .04));
        Check(!motion.Active && owner.Worker.Kinematic.VelocityPhys == velocity,
            "landing between press and worker preparation discards the boost without adding momentum");
    }

    private static void HookTransformation()
    {
        var caller = AccessTools.Method(typeof(Universe), nameof(Universe.PrepareVehicleWorkers));
        var original = PatchProcessor.GetOriginalInstructions(caller);
        var transformed = SomersaultMotionController.InsertWorkerHandoff(original).ToList();
        var native = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.PrepareWorker));
        var wrapped = AccessTools.Method(typeof(SomersaultMotionController), nameof(SomersaultMotionController.PrepareWorkerAndHandoff));
        Check(original.Count(c => c.Calls(native)) == 1 && transformed.All(c => !c.Calls(native))
            && transformed.Count(c => c.Calls(wrapped)) == 1, "production transformer replaces exactly one virtual call without duplicating native preparation");
        var missing = original.Where(c => !c.Calls(native)).ToList();
        Throws(() => SomersaultMotionController.InsertWorkerHandoff(missing).ToList(), "missing handoff rejects the feature before launch");
        var duplicate = original.Select(c => new CodeInstruction(c)).ToList();
        duplicate.Add(new(OpCodes.Callvirt, native));
        Throws(() => SomersaultMotionController.InsertWorkerHandoff(duplicate).ToList(), "ambiguous handoffs reject the feature before launch");
    }

    private static void OneShotAndAppliedAcknowledgement()
    {
        using var motion = Setup(out var owner, out var other);
        owner.FrameVelocity = new(4200, -1700, 300);
        owner.Kinematic.VelocityPhys = owner.FrameVelocity + new double3(1.2, 0, 0);
        owner.Inputs = new() { ThrusterCommandFlags = ThrusterMapFlags.TranslateForward | ThrusterMapFlags.TranslateUp,
            GrabHeld = true, EngineOn = true, EngineThrottle = .7f };
        other.Inputs = new() { ThrusterCommandFlags = ThrusterMapFlags.TranslateForward, GrabHeld = true };
        var originalVelocity = owner.Kinematic.VelocityPhys;
        Check(motion.Request(owner, SomersaultDirection.Right) && motion.Active && motion.RotationRadians == 0,
            "a click queues one owner without rotating or launching immediately");
        Check(!motion.Request(owner, SomersaultDirection.Left), "a repeated click cannot queue a second launch");
        SimStep launchStep = Step(0, .02);
        Universe.PrepareVehicleWorkers(launchStep);
        var launchedVelocity = owner.Worker.Kinematic.VelocityPhys;
        Check(owner.NativePrepares == 1 && other.NativePrepares == 1, "the replacement caller preserves one native virtual preparation per vehicle");
        Check(owner.Kinematic.VelocityPhys == originalVelocity && motion.RotationRadians == 0,
            "launch mutates only the prepared worker, and pose waits for applied results");
        Check((launchedVelocity - originalVelocity).Length() > 1 && (launchedVelocity - owner.FrameVelocity).Length() < 6,
            "one bounded local correction preserves large frame motion");
        Check(owner.Worker.UpdateData.NewVehicleProperties is { MotionlessTime: 0 } p
            && (p.Situation & Situation.OnRails) == 0 && p.Situation.HasTerrainContact(),
            "stationary on-rails kitten wakes while stale terrain contact remains available to native liftoff grace");
        Check(owner.Worker.LocomotionState.Mode == LocomotionMode.Airborne
            && !owner.Worker.LocomotionCommand.Active && owner.Worker.LocomotionCommand.JumpImpulse == 0,
            "the prepared snapshot replaces stale grounded traction and the native jump impulse");
        Check(owner.Worker.CharacterControlInputs.Copied && !owner.Worker.LadderWorkerInputs.GrabCandidate.Valid
            && !owner.Worker.ManualControlInputs.GrabHeld && owner.Worker.ManualControlInputs.ThrusterCommandFlags == ThrusterMapFlags.TranslateUp,
            "ownership runs after the final derived character/ladder copies and keeps held jump consumed");
        Check(owner.Worker.ManualControlInputs.EngineOn && owner.Worker.ManualControlInputs.EngineThrottle == .7f,
            "unrelated engine settings survive scoped input ownership");
        Check(other.Worker.ManualControlInputs.GrabHeld && other.Worker.ManualControlInputs.ThrusterCommandFlags == ThrusterMapFlags.TranslateForward,
            "another kitten's worker inputs remain intact");
        Universe.Apply(launchStep);
        motion.Update(owner);
        Check(motion.Active && owner.StockJumps == 0 && owner.LocomotionState.Mode == LocomotionMode.Airborne,
            "an applied native launch acknowledges without emitting a stock jump");
        motion.Update(owner);
        float sameAngle = motion.RotationRadians;
        motion.Update(owner);
        Check(motion.RotationRadians == sameAngle, "duplicate settled updates cannot advance pose progress");
        SimStep next = Step(.02, .22);
        Universe.PrepareVehicleWorkers(next);
        Check(owner.Worker.Kinematic.VelocityPhys == launchedVelocity, "the next worker preparation cannot add the launch a second time");
        owner.Worker.LocomotionState.ContactClearedSinceLiftoff = true;
        Universe.Apply(next); motion.Update(owner);
        Check(motion.RotationRadians > 0 && motion.Tuck > 0, "confirmed applied flight drives the procedural pose");
        owner.Worker.LocomotionState.Mode = LocomotionMode.Grounded;
        SimStep contact = Step(.22, .42);
        Universe.PrepareVehicleWorkers(contact); Universe.Apply(contact); motion.Update(owner);
        SimStep recover = Step(.42, .62);
        Universe.PrepareVehicleWorkers(recover); Universe.Apply(recover); motion.Update(owner);
        Check(!motion.Active && owner.StockJumps == 0, "real contact and recovery finish the action under a held stock jump");
        SimStep held = Step(.62, .82);
        Universe.PrepareVehicleWorkers(held); Universe.Apply(held); motion.Update(owner);
        Check(owner.StockJumps == 0 && owner.Worker.ManualControlInputs.GrabHeld
            && (owner.Worker.ManualControlInputs.ThrusterCommandFlags & ThrusterMapFlags.TranslateForward) != 0,
            "after landing the narrow jump-release guard restores movement/grab without replaying held jump");
        owner.Inputs.ThrusterCommandFlags &= ~ThrusterMapFlags.TranslateUp;
        SimStep release = Step(.82, 1.02);
        Universe.PrepareVehicleWorkers(release); Universe.Apply(release);
        owner.Inputs.ThrusterCommandFlags |= ThrusterMapFlags.TranslateUp;
        SimStep press = Step(1.02, 1.22);
        Universe.PrepareVehicleWorkers(press); Universe.Apply(press);
        Check(owner.StockJumps == 1, "a genuine new stock jump press works after release");
    }

    private static void PausedBoundaryAndInterruption()
    {
        using var motion = Setup(out var owner, out _);
        Check(motion.Request(owner, SomersaultDirection.Forward), "pause-boundary launch is accepted");
        var launch = Step(0, .02);
        Universe.PrepareVehicleWorkers(launch); Universe.Apply(launch);
        // The first positive result arrives on the frame pause is selected. The
        // native zero-dt snapshots replace Last before the next resumed update.
        Universe.Speed = 0;
        motion.Update(owner);
        float frozen = motion.RotationRadians;
        for (int i = 0; i < 3; i++)
        {
            var paused = Step(.02, .02);
            Universe.PrepareVehicleWorkers(paused); Universe.Apply(paused); motion.Update(owner);
            Check(motion.Active && motion.RotationRadians == frozen, "paused zero-dt snapshots freeze the unacknowledged action");
        }
        var velocity = owner.Kinematic.VelocityPhys;
        Universe.Speed = 1;
        motion.Update(owner);
        Check(motion.Active && !motion.Status.Contains("unsupported", StringComparison.OrdinalIgnoreCase),
            "resuming after replacement zero-dt snapshots retains the applied launch acknowledgement");
        Universe.PrepareVehicleWorkers(Step(.02, .04));
        Check(owner.Worker.Kinematic.VelocityPhys == velocity, "resume cannot replay the launch velocity change");
        Universe.Apply(Step(.02, .04)); motion.Update(owner);
        float progressed = motion.RotationRadians;
        // Repeat the precise boundary after acknowledgement: an additional
        // positive result is first seen while paused, then Last becomes dt=0.
        Universe.PrepareVehicleWorkers(Step(.04, .06)); Universe.Apply(Step(.04, .06));
        Universe.Speed = 0; motion.Update(owner);
        Universe.PrepareVehicleWorkers(Step(.06, .06)); Universe.Apply(Step(.06, .06)); motion.Update(owner);
        Check(motion.Active && motion.RotationRadians == progressed,
            "an acknowledged flight freezes when a new positive result first arrives during pause");
        Universe.Speed = 1; motion.Update(owner);
        Check(motion.Active && motion.RotationRadians > progressed,
            "an acknowledged flight resumes from actual applied time after replacement zero-dt snapshots");
        Universe.PrepareVehicleWorkers(Step(.06, .08));
        Check(owner.Worker.Kinematic.VelocityPhys == velocity, "midflight resume cannot issue the launch again");
        Universe.Apply(Step(.06, .08)); motion.Update(owner);
        Universe.Speed = 2; motion.Update(owner);
        Check(!motion.Active && owner.Kinematic.VelocityPhys == velocity, "unsupported warp releases ownership without changing ballistic velocity");
        Universe.Speed = 0;
        Check(!motion.Request(owner, SomersaultDirection.Forward) && motion.Status.Contains("Paused"),
            "paused clicks do not create deferred launches");
        Universe.Speed = 1;
        SetGrounded(owner);
        Check(motion.Request(owner, SomersaultDirection.Forward), "a fresh grounded request can follow an interruption");
        Universe.Speed = 0;
        motion.Update(owner);
        Check(!motion.Active, "a queued but unlaunched request is discarded on pause");
        Universe.Speed = 1;
        Check(motion.Request(owner, SomersaultDirection.Forward), "a later explicit request is accepted");
        Program.ControlledVehicle = null; motion.Update(null);
        Check(!motion.Active && motion.Owner is null, "control switching clears pending ownership");
    }

    private static void FinalCopiesAndFailureAtomicity()
    {
        using var motion = Setup(out var owner, out _);
        var initial = owner.Kinematic.VelocityPhys;
        Check(motion.Request(owner, SomersaultDirection.Forward), "a normal request can await final worker validation");
        owner.PendingLadderAttach = true;
        Universe.PrepareVehicleWorkers(Step(0, .02));
        Check(!motion.Active && owner.Worker.Kinematic.VelocityPhys == initial
            && owner.Worker.LadderWorkerInputs.PendingAttach.Valid,
            "a final native pending ladder attach refuses atomically and preserves native ladder ownership");
        owner.PendingLadderAttach = false;
        Check(motion.Request(owner, SomersaultDirection.Forward), "a request is accepted before an upcoming-step rejection");
        Universe.PrepareVehicleWorkers(Step(0, .5));
        Check(!motion.Active && owner.Worker.Kinematic.VelocityPhys == initial
            && owner.Worker.UpdateData.NewVehicleProperties is null,
            "an unsupported upcoming step cannot leave half-applied velocity or wake state");
        owner.FlightComputer.RCSMode = FlightComputerRCSMode.Enabled;
        Check(!motion.CanStart(owner, out var reason) && reason.Contains("RCS"), "RCS has an explicit eligibility reason");
        owner.FlightComputer.RCSMode = FlightComputerRCSMode.Disabled;
        owner.Kinematic.VelocityPhys = new(8, 0, 0);
        Check(motion.Request(owner, SomersaultDirection.Backward),
            "a running backward request fits the bounded directional correction");
        Universe.CurrentSystem = new(); motion.Update(owner);
        Check(!motion.Active, "world replacement clears a pending launch");
    }

    private static void RunningLaunchUsesPreparedSpeed()
    {
        foreach (SomersaultDirection direction in Enum.GetValues<SomersaultDirection>())
        {
            using var motion = Setup(out var owner, out _);
            owner.FrameVelocity = new(4200, -1700, 300);
            owner.Kinematic.VelocityPhys = owner.FrameVelocity + new double3(2, 0, 0);
            Check(motion.Request(owner, direction), "a moving flip can queue before reaching full running speed");
            owner.Kinematic.VelocityPhys = owner.FrameVelocity + new double3(5, 0, 0);
            var step = Step(0, .02);
            Universe.PrepareVehicleWorkers(step);
            var velocity = owner.Worker.Kinematic.VelocityPhys - owner.FrameVelocity;
            Check(Math.Abs(Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y) - 5.5) < 1e-8,
                "prepared launch uses current running momentum, preserving the moving frame");
            Check(Math.Abs(velocity.Z * velocity.Z / (2 * 9.81) - 1.495) < 1e-8,
                "prepared running launch reaches the higher apex");
            Universe.Apply(step); motion.Update(owner);
            var launched = owner.Kinematic.VelocityPhys;
            Universe.PrepareVehicleWorkers(Step(.02, .04));
            Check(owner.Worker.Kinematic.VelocityPhys == launched,
                "running boost is applied once and retained in subsequent airborne preparation");
        }
    }

    private static void SetGrounded(KittenEva owner)
    {
        owner.LocomotionState.Mode = owner.Worker.LocomotionState.Mode = LocomotionMode.Grounded;
        owner.LocomotionState.JumpFired = owner.Worker.LocomotionState.JumpFired = false;
        owner.Kinematic.VelocityPhys = owner.FrameVelocity;
        owner.Props.Situation = Situation.Terrain;
    }
    private static SimStep Step(double previous, double next) => new(new(previous), new(next), next - previous);
    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, message); return; }
        throw new InvalidOperationException("Somersault worker check failed: " + message);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Somersault worker check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
