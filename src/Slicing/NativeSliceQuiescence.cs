using Brutal.Numerics;
using KSA;

namespace Purr2D2.Slicing;

internal static partial class NativeSlicePhysics
{
    internal static VehicleUpdateState? UpdateState(Vehicle vehicle) => WorkerState?.GetValue(vehicle) as VehicleUpdateState;

    private static void ProtectIsolation(IsolatedSliceSource isolated)
    {
        var states = isolated.Neighbors.Append(isolated.Owner).Select(UpdateState).Where(s => s != null).Cast<VehicleUpdateState>().ToHashSet();
        foreach (var state in states) QuietSliceContacts.ProtectExisting(state, states);
    }

    private static void Quiesce(IsolatedSliceSource isolated, SliceCommitTimings timings)
    {
        var owner = isolated.Owner;
        var worker = UpdateState(owner) ?? throw new InvalidOperationException("The isolated source lost its native worker state.");
        // Hold only this shell's functional module tick across the required solver
        // interval. Colliders, terrain, gravity and all neighboring modules still run.
        QuietSliceQuiescence.Hold(worker);
        foreach (var engine in owner.Parts.Modules.Get<EngineController>())
            new InputEvents.IActivateInputData { Operation = InputEvents.ActivateOp.Engine, EngineController = engine, ActivationState = false }.Apply();
        foreach (var thruster in owner.Parts.Modules.Get<ThrusterController>())
            new InputEvents.IActivateInputData { Operation = InputEvents.ActivateOp.Thruster, ThrusterController = thruster, ActivationState = false }.Apply();
        if (KeyframeAnimationModule.TryGetFrom(owner.Parts.States, out var animations))
            foreach (var animation in owner.Parts.Modules.Get<KeyframeAnimationModule>())
                animation.TimeGoal = animations.GetState(animation).TimeCurrent;

        timings.Next(SliceCommitPhase.Drain);
        var state = owner.GetPhysicsStatesMutable();
        if (state.Props.Situation.IsOnRails())
            state.UpdateFromAnalytic(owner.Orbit, in owner.Orbit.StateVectors, owner.Body2Cce, owner.BodyRates, Situation.Maneuvering);
        state.Props.SetOnRails(false);
        var oldCom = owner.CenterOfMassAsmb;
        double oldMass = owner.TotalMass;
        // Even a partially failed drain is irreversible: cancellation reports it.
        isolated.ContentsReleased = true;
        owner.DepleteConsumables();
        foreach (var nozzle in owner.Parts.RocketNozzles.Modules)
        {
            // Joined handoff: clear the persistent cache too, so cancellation
            // before the next solver cannot revive a previously firing nozzle.
            var entry = owner.Parts.RocketNozzles.GetModuleAndAllMutableStatesForInitialization(nozzle);
            entry.State.Performance = RocketPerformance.Zero;
            entry.State.ThrustFraction = 0;
            entry.State.ThrustTime = 0;
            entry.State.AverageThrustFraction = 0;
            entry.State.DutyCycle = 0;
        }
        owner.UpdateAfterPartTreeModification();
        var drained = owner.GetPhysicsStatesMutable();
        var newCom = drained.Props.CenterOfMassAsmb;
        var correction = QuietSliceMotion.DrainCorrection(oldCom, new(newCom.X, newCom.Y, newCom.Z),
            drained.Kinematic.Body2Phys, drained.Kinematic.AngularVelocityPhys);
        drained.Kinematic.PositionPhys += correction.Position;
        drained.Kinematic.VelocityPhys += correction.Velocity;
        drained.Props.SetOnRails(false);
        owner.SetFlightPlan(new FlightPlan(drained.ComputeOrbit(owner.OrbitColor), owner.Hash));
        owner.UpdatePerFrameData();
        isolated.EmptiedMassKg = Math.Max(0, oldMass - owner.TotalMass);
        Console.WriteLine($"[purr2d2] Quiet source isolated: {owner.Id}; mass {oldMass:F3} -> {owner.TotalMass:F3} kg; COM shift {correction.Position}; body rates {owner.BodyRates}.");
    }
}
