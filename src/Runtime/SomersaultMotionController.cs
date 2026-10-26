using System.Reflection;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

/// <summary>Once-only prepared-worker launch/boost transactions, with native flight between them.</summary>
internal sealed class SomersaultMotionController : IDisposable
{
    private static SomersaultMotionController? _current;
    private static readonly FieldInfo? WorkerField = typeof(Vehicle).GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly SomersaultSequence _sequence = new();
    private readonly SomersaultCombo _combo = new();
    private readonly Func<double> _inputTime;
    private Harmony? _harmony;
    private bool _available, _disposed, _hasAppliedTime;
    private string _unavailableReason = "Somersault physics is unavailable.";
    private object? _system;
    private UniverseTime _launchTime, _issuedThrough, _appliedThrough;
    private double _nativeLaunchTime;
    private SomersaultProfile _launchProfile;
    private bool _boostQueued, _awaitingBoost;
    private double _boostStartElapsed;
    private KittenEva? _jumpReleaseOwner;
    internal bool Active => _sequence.Active;
    internal KittenEva? Owner { get; private set; }
    internal SomersaultDirection Direction { get; private set; }
    internal float RotationRadians => _sequence.RotationRadians;
    internal float Tuck => _sequence.Tuck;
    internal string Status { get; private set; } = "";
    internal long CommandId { get; private set; }
    // Retained across reset so settled presentation can observe an acknowledgement
    // already processed by input polling without replaying an older command.
    internal long AppliedCommandId { get; private set; }
    internal double InputTime => _inputTime();
    internal double ComboIntervalSeconds => _combo.PressWindowSeconds;

    internal void SetComboIntervalSeconds(double seconds) => _combo.ConfigurePressWindow(seconds);

    internal SomersaultMotionController(Func<double>? inputTime = null) => _inputTime = inputTime
        ?? (static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    internal void Install(Harmony harmony)
    {
        if (_disposed || _available) return;
        try
        {
            ValidateHostPreparation();
            if (_current is not null && !ReferenceEquals(_current, this))
                throw new InvalidOperationException("Another somersault physics owner is already installed.");
            harmony.Patch(Require(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), typeof(SimStep)),
                transpiler: new HarmonyMethod(typeof(SomersaultMotionController), nameof(InsertWorkerHandoff)));
            _harmony = harmony;
            _current = this;
            _available = true;
            _unavailableReason = "";
        }
        catch (Exception ex)
        {
            _unavailableReason = "Somersault physics is incompatible with this game build.";
            Status = _unavailableReason;
            Console.WriteLine($"[purr2d2] Somersault handoff unavailable: {ex}");
        }
    }

    internal bool CanStart(KittenEva? owner, out string reason, double? pressTime = null)
    {
        if (Active) return CanBoost(owner, pressTime ?? InputTime, out reason);
        // Availability is common to the four buttons. A selected direction is
        // revalidated by Request and at the actual upcoming worker handoff.
        return CanLaunch(owner, SomersaultDirection.Forward, out reason)
            || CanLaunch(owner, SomersaultDirection.Backward, out reason)
            || CanLaunch(owner, SomersaultDirection.Left, out reason)
            || CanLaunch(owner, SomersaultDirection.Right, out reason);
    }

    private bool CanLaunch(KittenEva? owner, SomersaultDirection direction, out string reason)
    {
        if (!CanControl(owner, out reason)) return false;
        reason = "Land upright on terrain first.";
        var state = owner!.LocomotionState;
        if (state.Mode != LocomotionMode.Grounded || !owner.Props.Situation.HasTerrainContact()
            || owner.Props.Situation.HasOceanContact() || state.JumpFired
            || !float.IsFinite(state.UprightAlignment)
            || state.UprightAlignment < MathF.Cos(KittenLocomotionTuning.Current.FeetDownTiltDeg * MathF.PI / 180)) return false;
        var physics = owner.GetPhysicsStates();
        physics.GetStatesCcf(out var position, out var velocity, out var body2Ccf);
        return SomersaultMotion.TryLaunch(position, velocity, body2Ccf, physics.Kinematic.Body2Phys,
            direction, physics.Environment.GravitationBub.Length(), out _, out reason,
            KittenLocomotionTuning.Current.WalkSpeed, KittenLocomotionTuning.Current.RunSpeed);
    }

    private bool CanControl(KittenEva? owner, out string reason)
    {
        reason = _unavailableReason;
        if (!_available || _disposed) return false;
        reason = "Control a live EVA kitten.";
        if (owner is null || owner.IsDisposed || Program.EditorFlag || owner.IsEditedVehicle
            || !ReferenceEquals(owner, Program.ControlledVehicle) || !ReferenceEquals(owner.System, Universe.CurrentSystem)
            || !owner.IsControllable) return false;
        double speed = Universe.GetSimulationSpeed();
        reason = speed == 0 ? "Paused." : "Use 1× simulation speed.";
        if (!SomersaultMotion.NormalSpeed(speed)) return false;
        reason = "Disable RCS first.";
        if (owner.FlightComputer.RCSMode != FlightComputerRCSMode.Disabled) return false;
        reason = "Move into the active physics area.";
        var physics = owner.GetPhysicsStates();
        if (!owner.HasPhysicsBubble || !physics.Environment.InPhysicsRadius) return false;
        reason = "";
        return true;
    }

    private bool CanBoost(KittenEva? owner, double pressTime, out string reason)
    {
        if (!CanControl(owner, out reason)) return false;
        reason = $"Press again within {_combo.PressWindowSeconds:0.##} s; three jumps maximum.";
        if (!ReferenceEquals(Owner, owner) || !ReferenceEquals(_system, Universe.CurrentSystem)
            || _sequence.Phase != SomersaultPhase.Airborne || !_combo.CanQueue(pressTime)
            || owner!.LocomotionState.Mode != LocomotionMode.Airborne || owner.Props.Situation.HasOceanContact()) return false;
        reason = "";
        return true;
    }

    internal bool Request(KittenEva owner, SomersaultDirection direction, double? pressTime = null)
    {
        if (!Enum.IsDefined(direction)) { Status = "Unknown somersault direction."; return false; }
        double pressed = pressTime ?? InputTime;
        if (Active)
        {
            if (!CanBoost(owner, pressed, out var boostReason)) { Status = boostReason; return false; }
            if (!_combo.Queue(pressed)) return false;
            // Keep the original visual axis and horizontal momentum even when
            // another direction's circle was pressed during this combo.
            _boostQueued = true;
            CommandId++;
            Status = "Preparing extra somersault.";
            return true;
        }
        if (!CanLaunch(owner, direction, out var reason)) { Status = reason; return false; }
        if (!_combo.Start(pressed)) { Status = "Jump input time is unavailable."; return false; }
        Direction = direction;
        Owner = owner;
        _system = Universe.CurrentSystem;
        _sequence.Queue();
        CommandId++;
        Status = "Preparing somersault.";
        return true;
    }

    /// <summary>Called only after all previous vehicle solver results were joined and applied.</summary>
    internal void Update(KittenEva? controlledOwner)
    {
        if (_disposed) return;
        if (_jumpReleaseOwner is { } guarded && (!ReferenceEquals(guarded, controlledOwner) || guarded.IsDisposed)) _jumpReleaseOwner = null;
        if (!Active) return;
        if (Owner is null || Owner.IsDisposed || !ReferenceEquals(Owner, controlledOwner)
            || !ReferenceEquals(Owner, Program.ControlledVehicle) || !ReferenceEquals(_system, Universe.CurrentSystem)
            || !ReferenceEquals(Owner.System, Universe.CurrentSystem) || Program.EditorFlag || Owner.IsEditedVehicle
            || !Owner.IsControllable || !Owner.HasPhysicsBubble || !Owner.GetPhysicsStates().Environment.InPhysicsRadius)
        { Release("Somersault ended after a control or world change."); return; }
        if (Owner.FlightComputer.RCSMode != FlightComputerRCSMode.Disabled)
        { Release("Somersault ended: RCS enabled."); return; }
        double speed = Universe.GetSimulationSpeed();
        if (speed == 0)
        {
            if (_sequence.Phase == SomersaultPhase.Queued) Release("Paused; somersault cancelled.");
            else { _boostQueued = false; _combo.Close(); Status = "Paused; jump combo closed."; }
            return;
        }
        if (!SomersaultMotion.NormalSpeed(speed))
        { Release("Somersault ended: use 1× simulation speed."); return; }
        SimStep applied = Universe.GetLastSimStep();
        if (_hasAppliedTime && applied.NextTime == _appliedThrough) return;
        if (_hasAppliedTime && applied.NextTime < _appliedThrough)
        { Release("Somersault ended after a simulation reset."); return; }
        _hasAppliedTime = true;
        _appliedThrough = applied.NextTime;
        if (_sequence.Phase == SomersaultPhase.Queued) return;
        if (applied.NextTime < _issuedThrough) return;
        // Pause can replace GetLastSimStep with a zero-dt snapshot before the
        // positive launch result was observed here. Its applied time and public
        // launch marker remain authoritative when the player resumes.
        bool stationaryStep = applied.DeltaTime == 0 && applied.NextTime == applied.PreviousTime;
        if (!stationaryStep && !SomersaultMotion.ValidStep(applied.DeltaTime))
        { Release("Somersault ended after an unsupported physics step."); return; }
        var state = Owner.LocomotionState;
        if (_sequence.Phase == SomersaultPhase.AwaitingAcknowledgement || _awaitingBoost)
        {
            if (!double.IsFinite(state.JumpFiredTime) || Math.Abs(state.JumpFiredTime - _nativeLaunchTime) > 1e-5)
            { Release("Native launch was not acknowledged."); return; }
            if (_awaitingBoost)
            {
                if (!_sequence.AddTurn(_boostStartElapsed)) { Release("Extra turn was not acknowledged."); return; }
                _awaitingBoost = false;
            }
            else _sequence.Acknowledge();
            _combo.Acknowledge();
            AppliedCommandId = CommandId;
        }
        bool airborne = state.Mode == LocomotionMode.Airborne;
        bool terrain = Owner.Props.Situation.HasTerrainContact();
        bool interrupted = state.Mode is not (LocomotionMode.Airborne or LocomotionMode.Grounded);
        _sequence.AdvanceTo((applied.NextTime - _launchTime).Seconds(), airborne, terrain,
            state.ContactClearedSinceLiftoff, interrupted, KittenLocomotionTuning.Current.LiftoffGraceTime);
        if (_sequence.Phase == SomersaultPhase.Recovering) { _combo.Close(); _boostQueued = false; }
        if (!Active)
        {
            Owner = null;
            _system = null;
            _combo.Reset();
            _hasAppliedTime = false;
            Status = "";
            return;
        }
        Status = _sequence.Phase == SomersaultPhase.Recovering ? "Recovering." : "Somersault.";
    }

    private void AfterPrepareWorker(Vehicle vehicle, SimStep step)
    {
        if (_disposed || vehicle is not KittenEva owner) return;
        bool owned = Active && ReferenceEquals(Owner, owner);
        bool jumpGuard = ReferenceEquals(_jumpReleaseOwner, owner);
        if (!owned && !jumpGuard) return;
        if (!ReferenceEquals(owner, Program.ControlledVehicle) || owner.IsDisposed
            || !ReferenceEquals(owner.System, Universe.CurrentSystem))
        { if (owned) Release("Somersault ended after a control change."); else _jumpReleaseOwner = null; return; }
        var worker = WorkerField!.GetValue(owner) as VehicleUpdateState;
        if (worker is null || !ReferenceEquals(worker.ReadOnlyVehicle, owner))
        { Release("The native kitten worker is unavailable."); return; }
        // Rocket modes may retain KittenEva while selecting ordinary vessel physics.
        // Release our intent before touching that mode's prepared motion or inputs.
        if (!worker.IsKitten)
        { Release("Somersault cancelled: vehicle physics owns this kitten."); return; }
        if (owned)
        {
            if (!ReferenceEquals(_system, Universe.CurrentSystem) || !SomersaultMotion.NormalSpeed(Universe.GetSimulationSpeed())
                || owner.FlightComputer.RCSMode != FlightComputerRCSMode.Disabled)
            {
                // A paused already-launched move keeps its pose and ownership;
                // a pending click never becomes a launch when time resumes.
                if (Universe.GetSimulationSpeed() == 0 && _sequence.Phase != SomersaultPhase.Queued)
                { _boostQueued = false; _combo.Close(); SuppressOwnedInputs(owner, worker); return; }
                Release("Somersault cancelled by a simulation or RCS change."); return;
            }
            if (!SomersaultMotion.ValidStep(step.DeltaTime) || step.NextTime <= step.PreviousTime
                || Math.Abs((step.NextTime - step.PreviousTime).Seconds() - step.DeltaTime) > 1e-6)
            { Release("Unsupported upcoming physics step."); return; }
            if (_sequence.Phase == SomersaultPhase.Queued)
            {
                if (!CanLaunch(owner, Direction, out var reason)) { Release(reason); return; }
                if (worker.LocomotionState.Mode != LocomotionMode.Grounded || worker.LadderWorkerInputs.PendingAttach.Valid)
                { Release("Finish landing or grabbing first."); return; }
                var physics = worker.GetReadOnlyStates();
                physics.GetStatesCcf(out var position, out var velocity, out var body2Ccf);
                if (!SomersaultMotion.TryLaunch(position, velocity, body2Ccf, physics.Kinematic.Body2Phys,
                    Direction, physics.Environment.GravitationBub.Length(), out var launch, out reason,
                    KittenLocomotionTuning.Current.WalkSpeed, KittenLocomotionTuning.Current.RunSpeed))
                { Release(reason); return; }
                CommitLaunch(worker, step, launch);
            }
            else if (_boostQueued)
            {
                // The captured press was already timed at request acceptance;
                // worker latency must not turn an in-window press into a miss.
                if (!CanControl(owner, out var reason) || worker.LocomotionState.Mode != LocomotionMode.Airborne
                    || worker.LadderWorkerInputs.PendingAttach.Valid || owner.Props.Situation.HasOceanContact())
                { Release(string.IsNullOrEmpty(reason) ? "Extra jump interrupted by contact or grabbing." : reason); return; }
                var physics = worker.GetReadOnlyStates();
                physics.GetStatesCcf(out var position, out _, out var body2Ccf);
                if (!SomersaultProfile.TryCreate(physics.Environment.GravitationBub.Length(), out _)
                    || !SomersaultMotion.TryBoost(position, body2Ccf, physics.Kinematic.Body2Phys,
                        _launchProfile.UpSpeed, out var delta) || !SomersaultMotion.Finite(worker.Kinematic.VelocityPhys + delta))
                { Release("The extra jump surface frame is unavailable."); return; }
                CommitBoost(worker, step, delta);
            }
            SuppressOwnedInputs(owner, worker);
        }
        else GuardHeldJump(owner, worker);
    }

    private void CommitLaunch(VehicleUpdateState worker, SimStep step, SomersaultLaunch launch)
    {
        // Precompute every value before mutating the prepared worker. No public
        // vehicle state is touched; worker results provide the acknowledgement.
        var kinematic = worker.Kinematic;
        kinematic.VelocityPhys += launch.DeltaVelocityPhys;
        var props = worker.GetReadOnlyProps();
        props.SetOnRails(false); // Clears MotionlessTime; native UpdateSimFromVehicle wakes the Bepu body.
        var state = worker.LocomotionState;
        state.Mode = LocomotionMode.Airborne;
        state.ModeStartTime = state.LiftoffTime = state.JumpFiredTime = step.PreviousTime.Seconds();
        state.JumpConsumed = state.JumpFired = true;
        state.ContactClearedSinceLiftoff = false;
        state.GroundSpeed = 0;
        state.FacingDirPhys = float3.Pack(launch.FacingPhys);
        state.UprightDirPhys = float3.Pack(launch.UpPhys);
        var command = new LocomotionCommand
        {
            Active = false,
            JumpImpulse = 0,
            UpDirPhys = float3.Pack(launch.UpPhys),
            UprightDirPhys = float3.Pack(launch.UpPhys),
            FacingDirPhys = state.FacingDirPhys
        };
        _launchTime = step.PreviousTime;
        _issuedThrough = step.NextTime;
        _nativeLaunchTime = state.JumpFiredTime;
        _launchProfile = launch.Profile;
        worker.UpdateData.NewKinematicStates = kinematic;
        worker.UpdateData.NewVehicleProperties = props;
        worker.LocomotionState = state;
        worker.LocomotionCommand = command;
        worker.UpdateData.NewLocomotionState = state;
        _sequence.Dispatched(launch.Profile); // Consumed here; no subsequent preparation can add another delta.
        Status = "Launching somersault.";
    }

    private void CommitBoost(VehicleUpdateState worker, SimStep step, double3 delta)
    {
        var kinematic = worker.Kinematic;
        kinematic.VelocityPhys += delta;
        var props = worker.GetReadOnlyProps();
        props.SetOnRails(false);
        var state = worker.LocomotionState;
        state.JumpConsumed = state.JumpFired = true;
        state.JumpFiredTime = step.PreviousTime.Seconds();
        var command = worker.LocomotionCommand;
        command.Active = false;
        command.JumpImpulse = 0;
        _issuedThrough = step.NextTime;
        _nativeLaunchTime = state.JumpFiredTime;
        _boostStartElapsed = (step.PreviousTime - _launchTime).Seconds();
        worker.UpdateData.NewKinematicStates = kinematic;
        worker.UpdateData.NewVehicleProperties = props;
        worker.LocomotionState = state;
        worker.LocomotionCommand = command;
        worker.UpdateData.NewLocomotionState = state;
        _boostQueued = false; // Consume before another preparation can see it.
        _awaitingBoost = true;
        Status = "Launching extra somersault.";
    }

    private void SuppressOwnedInputs(KittenEva owner, VehicleUpdateState worker)
    {
        bool heldJump = (worker.ManualControlInputs.ThrusterCommandFlags & ThrusterMapFlags.TranslateUp) != 0;
        _jumpReleaseOwner = heldJump ? owner : null;
        var inputs = worker.ManualControlInputs;
        // Keeping held jump present AND consumed prevents native Advance from
        // resetting JumpConsumed. Only this prepared owner loses movement/grab.
        inputs.ThrusterCommandFlags = heldJump ? ThrusterMapFlags.TranslateUp : ThrusterMapFlags.None;
        inputs.GrabHeld = false;
        worker.ManualControlInputs = inputs;
        worker.LadderWorkerInputs = default;
        worker.LadderNodeCount = 0;
        worker.LocomotionState.JumpConsumed = true;
    }

    private void GuardHeldJump(KittenEva owner, VehicleUpdateState worker)
    {
        if ((worker.ManualControlInputs.ThrusterCommandFlags & ThrusterMapFlags.TranslateUp) == 0)
        { _jumpReleaseOwner = null; return; }
        if (ReferenceEquals(_jumpReleaseOwner, owner)) worker.LocomotionState.JumpConsumed = true;
    }

    private void Release(string status)
    {
        _sequence.Reset();
        _combo.Reset();
        _boostQueued = _awaitingBoost = false;
        _launchProfile = default;
        Owner = _jumpReleaseOwner = null;
        _system = null;
        _hasAppliedTime = false;
        Status = status;
    }

    internal void Reset() => Release("");

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        _available = false;
        if (ReferenceEquals(_current, this)) _current = null;
        _harmony?.Unpatch(Require(typeof(Universe), nameof(Universe.PrepareVehicleWorkers), typeof(SimStep)),
            Require(typeof(SomersaultMotionController), nameof(InsertWorkerHandoff), typeof(IEnumerable<CodeInstruction>)));
        _harmony = null;
    }

    private static MethodInfo Require(Type type, string name, params Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);

    private static void ValidateHostPreparation()
    {
        if (WorkerField?.FieldType != typeof(VehicleUpdateState)) throw new MissingFieldException(typeof(Vehicle).FullName, "_threadWorkerUpdateState");
        var prepare = Require(typeof(Vehicle), nameof(Vehicle.PrepareWorker), typeof(SimStep));
        if (!prepare.IsVirtual || prepare.IsStatic) throw new InvalidOperationException("Vehicle preparation is no longer virtual.");
        var kittenPrepare = Require(typeof(KittenEva), nameof(KittenEva.PrepareWorker), typeof(SimStep));
        RequireOrdered(kittenPrepare, prepare, Require(typeof(KittenEva), "UpdateLadderDetection"),
            Require(typeof(Vehicle), "PrepareCharacterWorkerState", typeof(CharacterControlInputs).MakeByRefType(), typeof(KittenLocomotionTuning).MakeByRefType()));
        RequireOrdered(prepare, Require(typeof(VehicleUpdateState), nameof(VehicleUpdateState.PrepareFromVehicle), typeof(bool), typeof(ManualControlInputs)));
        RequireOrdered(Require(typeof(VehicleUpdateState), nameof(VehicleUpdateState.PrepareFromVehicle), typeof(bool), typeof(ManualControlInputs)),
            Require(typeof(VehicleUpdateData), nameof(VehicleUpdateData.Prepare), typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(), typeof(FlightComputer)));
    }

    private static void RequireOrdered(MethodBase caller, params MethodInfo[] methods)
    {
        var codes = PatchProcessor.GetOriginalInstructions(caller);
        int previous = -1;
        foreach (var method in methods)
        {
            int index = codes.FindIndex(c => c.Calls(method));
            if (index <= previous || codes.Count(c => c.Calls(method)) != 1)
                throw new InvalidOperationException($"Native kitten preparation changed at {caller.Name}/{method.Name}.");
            previous = index;
        }
    }

    /// <summary>The wrapper runs after the derived kitten's final ladder and character input copies.</summary>
    internal static IEnumerable<CodeInstruction> InsertWorkerHandoff(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        var native = Require(typeof(Vehicle), nameof(Vehicle.PrepareWorker), typeof(SimStep));
        int index = codes.FindIndex(c => c.Calls(native));
        var takeWorker = Require(typeof(Vehicle), nameof(Vehicle.TakeUpdateStateIfOrphaned));
        int takeIndex = codes.FindIndex(c => c.Calls(takeWorker));
        if (index < 0 || codes.Count(c => c.Calls(native)) != 1 || codes[index].opcode != OpCodes.Callvirt
            || takeIndex <= index || codes.Count(c => c.Calls(takeWorker)) != 1)
            throw new InvalidOperationException("Purr2D2 cannot locate an unambiguous native vehicle preparation handoff.");
        codes[index].opcode = OpCodes.Call;
        codes[index].operand = Require(typeof(SomersaultMotionController), nameof(PrepareWorkerAndHandoff), typeof(Vehicle), typeof(SimStep));
        return codes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void PrepareWorkerAndHandoff(Vehicle vehicle, SimStep step)
    {
        vehicle.PrepareWorker(step);
        try { _current?.AfterPrepareWorker(vehicle, step); }
        catch (Exception ex)
        {
            _current?.Release("Somersault suspended after a physics handoff error.");
            Console.WriteLine($"[purr2d2] Somersault preparation failed: {ex}");
        }
    }
}
