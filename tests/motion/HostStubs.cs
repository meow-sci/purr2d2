using System.Runtime.CompilerServices;
using Brutal.Numerics;

// Only the native host boundary is replaced. The fixture runs the production
// controller and its actual Harmony transpiler on an optimized virtual caller.
namespace KSA;

internal static class Program
{
    internal static bool EditorFlag;
    internal static Vehicle? ControlledVehicle;
}

internal static class Universe
{
    internal static object? CurrentSystem = new();
    internal static double Speed = 1;
    internal static SimStep Last;
    internal static Vehicle[] Vehicles = [];
    internal static double GetSimulationSpeed() => Speed;
    internal static SimStep GetLastSimStep() => Last;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void PrepareVehicleWorkers(SimStep step)
    {
        foreach (Vehicle current in Vehicles)
        {
            current.PrepareWorker(step);
            current.TakeUpdateStateIfOrphaned();
        }
    }

    internal static void Apply(SimStep step)
    {
        foreach (var vehicle in Vehicles) vehicle.Apply(step);
        Last = step;
    }
}

internal readonly record struct UniverseTime(double Value)
{
    internal double Seconds() => Value;
    public static UniverseTime operator -(UniverseTime a, UniverseTime b) => new(a.Value - b.Value);
    public static bool operator <(UniverseTime a, UniverseTime b) => a.Value < b.Value;
    public static bool operator >(UniverseTime a, UniverseTime b) => a.Value > b.Value;
    public static bool operator <=(UniverseTime a, UniverseTime b) => a.Value <= b.Value;
    public static bool operator >=(UniverseTime a, UniverseTime b) => a.Value >= b.Value;
}
internal readonly record struct SimStep(UniverseTime PreviousTime, UniverseTime NextTime, double DeltaTime);

[Flags]
internal enum Situation { Free = 0, Terrain = 1, OnRails = 2, Ocean = 4 }
internal static class SituationEx
{
    internal static bool HasTerrainContact(this Situation value) => (value & Situation.Terrain) != 0;
    internal static bool HasOceanContact(this Situation value) => (value & Situation.Ocean) != 0;
}
internal enum LocomotionMode { Grounded, Airborne, Tumbling, Rightening, Ladder, Swimming, Mmu }
internal enum FlightComputerRCSMode { Enabled, Disabled }
internal sealed class FlightComputer { internal FlightComputerRCSMode RCSMode = FlightComputerRCSMode.Disabled; }
internal struct LocomotionState
{
    internal LocomotionMode Mode;
    internal bool JumpFired, JumpConsumed, ContactClearedSinceLiftoff;
    internal double ModeStartTime, LiftoffTime, JumpFiredTime;
    internal float GroundSpeed, UprightAlignment;
    internal float3 FacingDirPhys, UprightDirPhys;
}
internal struct LocomotionCommand
{
    internal bool Active;
    internal float JumpImpulse;
    internal float3 UpDirPhys, UprightDirPhys, FacingDirPhys;
}
[Flags]
internal enum ThrusterMapFlags { None = 0, TranslateUp = 1, TranslateForward = 2, RollRight = 4 }
internal struct ManualControlInputs
{
    internal ThrusterMapFlags ThrusterCommandFlags;
    internal bool GrabHeld, EngineOn;
    internal float EngineThrottle;
}
internal struct CharacterControlInputs { internal bool Copied; }
internal readonly record struct LadderAnchorId(bool Valid);
internal struct LadderWorkerInputs { internal LadderAnchorId PendingAttach, GrabCandidate; }
internal struct LadderNode { }
internal struct KittenLocomotionTuning
{
    internal float FeetDownTiltDeg, LiftoffGraceTime;
    internal float WalkSpeed, RunSpeed;
    internal static KittenLocomotionTuning Current = new() { FeetDownTiltDeg = 45, LiftoffGraceTime = .2f, WalkSpeed = 2, RunSpeed = 5 };
}
internal struct VehicleProperties
{
    internal Situation Situation;
    internal float MotionlessTime;
    internal void SetOnRails(bool rails)
    {
        if (rails) Situation |= Situation.OnRails;
        else { Situation &= ~Situation.OnRails; MotionlessTime = 0; }
    }
}
internal struct KinematicStates
{
    internal double3 PositionPhys, VelocityPhys;
    internal doubleQuat Body2Phys;
}
internal struct PhysicsEnvironment { internal bool InPhysicsRadius; internal double3 GravitationBub; }
internal struct BubbleOrigin { }

internal readonly struct ReadOnlyPhysicsStates(KinematicStates kinematic, PhysicsEnvironment environment, double3 frameVelocity)
{
    internal KinematicStates Kinematic { get; } = kinematic;
    internal PhysicsEnvironment Environment { get; } = environment;
    internal void GetStatesCcf(out double3 position, out double3 velocity, out doubleQuat rotation)
    { position = Kinematic.PositionPhys; velocity = Kinematic.VelocityPhys - frameVelocity; rotation = doubleQuat.Identity; }
}

internal class Vehicle
{
    private readonly VehicleUpdateState _threadWorkerUpdateState;
    internal VehicleUpdateState Worker => _threadWorkerUpdateState;
    internal bool IsDisposed = false, IsEditedVehicle = false;
    internal bool IsControllable = true, HasPhysicsBubble = true;
    internal object? System = Universe.CurrentSystem;
    internal readonly FlightComputer FlightComputer = new();
    internal VehicleProperties Props = new() { Situation = Situation.Terrain | Situation.OnRails, MotionlessTime = 100 };
    internal PhysicsEnvironment Environment = new() { InPhysicsRadius = true, GravitationBub = new(0, 0, 9.81) };
    internal KinematicStates Kinematic = new() { PositionPhys = new(0, 0, -1000), Body2Phys = doubleQuat.Identity };
    internal double3 FrameVelocity;
    internal ManualControlInputs Inputs;
    internal LocomotionState LocomotionState = new() { Mode = LocomotionMode.Grounded, UprightAlignment = 1, FacingDirPhys = new(1, 0, 0) };
    internal int NativePrepares, StockJumps;
    internal Vehicle() => _threadWorkerUpdateState = new(this);

    public virtual void PrepareWorker(SimStep step)
    {
        NativePrepares++;
        _threadWorkerUpdateState.PrepareFromVehicle(false, Inputs);
    }
    protected void PrepareCharacterWorkerState(in CharacterControlInputs inputs, in KittenLocomotionTuning tuning)
        => Worker.PrepareCharacterFromVehicle(in inputs, in tuning);
    public VehicleUpdateState TakeUpdateStateIfOrphaned() => Worker;
    internal ReadOnlyPhysicsStates GetPhysicsStates() => new(Kinematic, Environment, FrameVelocity);
    internal void Apply(SimStep step)
    {
        if (step.DeltaTime > 0)
        {
            // The native jump latch resets only on actual release, then stock
            // grounded locomotion may emit its own jump. This is the behavior
            // the production scoped worker-input guard must coexist with.
            bool held = (Worker.ManualControlInputs.ThrusterCommandFlags & ThrusterMapFlags.TranslateUp) != 0;
            if (!held) Worker.LocomotionState.JumpConsumed = false;
            if (held && !Worker.LocomotionState.JumpConsumed && Worker.LocomotionState.Mode == LocomotionMode.Grounded)
            { StockJumps++; Worker.LocomotionState.JumpConsumed = true; }
            Worker.UpdateData.NewLocomotionState = Worker.LocomotionState;
        }
        Kinematic = Worker.Kinematic;
        Props = Worker.UpdateData.NewVehicleProperties ?? Props;
        if (Worker.UpdateData.NewLocomotionState is { } state) LocomotionState = state;
    }
}

internal sealed class KittenEva : Vehicle
{
    private readonly CharacterControlInputs _characterControlInputs = new() { Copied = true };
    internal bool PendingLadderAttach;
    public override void PrepareWorker(SimStep step)
    {
        base.PrepareWorker(step);
        UpdateLadderDetection();
        PrepareCharacterWorkerState(in _characterControlInputs, in KittenLocomotionTuning.Current);
    }
    private void UpdateLadderDetection()
    {
        var inputs = new LadderWorkerInputs { PendingAttach = new(PendingLadderAttach), GrabCandidate = new(true) };
        Worker.PrepareLadderFromVehicle(in inputs, []);
    }
}

internal sealed class VehicleUpdateData(Vehicle vehicle)
{
    internal KinematicStates NewKinematicStates;
    internal VehicleProperties? NewVehicleProperties;
    internal LocomotionState? NewLocomotionState;
    internal void Prepare(in BubbleOrigin origin, in KinematicStates kinematic, FlightComputer flightComputer)
    {
        NewKinematicStates = kinematic;
        NewVehicleProperties = null;
        NewLocomotionState = null;
    }
    internal Vehicle Vehicle { get; } = vehicle;
}

internal sealed class VehicleUpdateState
{
    internal readonly Vehicle ReadOnlyVehicle;
    internal readonly VehicleUpdateData UpdateData;
    internal LocomotionState LocomotionState;
    internal LocomotionCommand LocomotionCommand;
    internal ManualControlInputs ManualControlInputs;
    internal CharacterControlInputs CharacterControlInputs;
    internal LadderWorkerInputs LadderWorkerInputs;
    internal int LadderNodeCount;
    internal ref KinematicStates Kinematic => ref UpdateData.NewKinematicStates;
    internal VehicleUpdateState(Vehicle vehicle)
    { ReadOnlyVehicle = vehicle; UpdateData = new(vehicle); LocomotionState = vehicle.LocomotionState; }
    internal VehicleProperties GetReadOnlyProps() => UpdateData.NewVehicleProperties ?? ReadOnlyVehicle.Props;
    internal ReadOnlyPhysicsStates GetReadOnlyStates() => new(Kinematic, ReadOnlyVehicle.Environment, ReadOnlyVehicle.FrameVelocity);
    internal void PrepareFromVehicle(bool highFidelity, ManualControlInputs inputs)
    {
        BubbleOrigin origin = default;
        UpdateData.Prepare(in origin, in ReadOnlyVehicle.Kinematic, ReadOnlyVehicle.FlightComputer);
        ManualControlInputs = inputs;
    }
    internal void PrepareCharacterFromVehicle(in CharacterControlInputs inputs, in KittenLocomotionTuning tuning)
        => CharacterControlInputs = inputs;
    internal void PrepareLadderFromVehicle(in LadderWorkerInputs inputs, ReadOnlySpan<LadderNode> nodes)
    { LadderWorkerInputs = inputs; LadderNodeCount = nodes.Length; }
}
