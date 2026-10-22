using System.Reflection;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;

namespace Purr2D2.Autopilot;

internal enum AutopilotResult { Running, Completed, OrbitFinished, Aborted }

internal sealed class EvaCuttingAutopilot
{
    private enum Phase { Idle, Captured, Braking, Approach, Hold, Igniting, Orbit }
    private static readonly FieldInfo? ManualInputsField = typeof(Vehicle).GetField(
        "_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed record FlightSnapshot(
        FlightComputerAttitudeMode AttitudeMode, VehicleReferenceFrame AttitudeFrame,
        FlightComputerAttitudeTrackTarget TrackTarget, double3 CustomTarget,
        FlightComputerManualThrustMode ThrustMode, FlightComputerRollMode RollMode,
        FlightComputerRCSMode RcsMode, float RateLimit, KittenControlMode ControlMode);

    private KittenEva? _owner;
    private Vehicle? _target;
    private FlightSnapshot? _snapshot;
    private Phase _phase;
    private CutRing _ring;
    private bool _hasRing;
    private CutMesh? _surface;
    private readonly List<int> _nearTriangles = new();
    private int _topology;
    private double _clearance;
    private double _angle;
    private double _orbitSpeed;
    private double _bladeAxialOffset;
    private bool _cutWhenReady;
    private bool _cutStarted;
    private bool _cuttingMotionReady;
    private int _structuralCountAtStart;

    internal bool Active => _phase is Phase.Braking or Phase.Approach or Phase.Hold or Phase.Igniting or Phase.Orbit;
    internal bool Captured => _hasRing;
    internal bool ReadyToCut => _phase == Phase.Hold;
    internal bool WantsEquipped => Active && _phase != Phase.Braking;
    internal bool WantsIgnited => _phase is Phase.Igniting or Phase.Orbit;
    internal bool AllowsCutting => _phase == Phase.Orbit && _cuttingMotionReady;
    internal float Progress => (float)CylinderOrbitGuidance.Progress(_angle);
    internal double EffectiveOrbitSpeed => _orbitSpeed;
    internal double OrbitAngle => _angle;
    internal double3 FlightCenter => _ring.Center - _ring.Axis * _bladeAxialOffset;
    internal string Status { get; private set; } = "Choose a target vessel, then find a ring or pick a point on its hull.";

    internal bool TryGetPreview(out Vehicle target, out CutRing ring)
    {
        target = _target!; ring = _ring;
        return _hasRing && _target is { IsDisposed: false } && _owner is { IsDisposed: false }
            && ReferenceEquals(_owner.Target, _target) && ReferenceEquals(Program.ControlledVehicle, _owner)
            && Topology(_target) == _topology;
    }

    internal bool Capture(KittenEva owner, SaberSettings settings, double3? preferredPoint = null)
    {
        if (Active) { Status = "Release the autopilot before selecting another ring."; return false; }
        if (!TryTarget(owner, out Vehicle target, out string reason)) return FailCapture(reason);
        settings.Validate();
        if (!TargetSurfaceSnapshot.TryCapture(target, out var triangles, out var bounds, out reason))
            return FailCapture(reason);
        double3 position = (owner.GetPositionCci() - target.GetPositionCci()).Transform(target.GetBody2Cci().Inverse());
        if (!CuttingRingGeometry.TryFind(triangles, bounds, position,
            settings.AutopilotAutoAxis ? -1 : settings.AutopilotAxis, settings.AutopilotAxisReversed,
            settings.AutopilotClearance, out var ring, out reason, out var surface, preferredPoint)) return FailCapture(reason);
        _owner = owner; _target = target; _ring = ring; _hasRing = true;
        _surface = surface; _topology = Topology(target);
        _clearance = settings.AutopilotClearance;
        _angle = _bladeAxialOffset = 0; _cutWhenReady = _cutStarted = _cuttingMotionReady = false;
        _phase = Phase.Captured;
        Status = $"Ring selected on {target.Id}: radius {ring.Radius:F2} m. Approach ring will fly to the marked entry.";
        return true;
    }

    internal bool Nudge(KittenEva owner, SaberSettings settings, double offset)
    {
        if (!_hasRing || Active) return false;
        if (_target == null || Topology(_target) != _topology)
        { Status = "Target geometry changed; find or select a new ring."; return false; }
        CutRing previousRing = _ring;
        CutMesh? previousSurface = _surface;
        KittenEva? previousOwner = _owner;
        Vehicle? previousTarget = _target;
        int previousTopology = _topology;
        double previousClearance = _clearance;
        // Keep the selected axis when moving a chosen station, even if Auto found it.
        bool auto = settings.AutopilotAutoAxis;
        int previousAxis = settings.AutopilotAxis;
        bool reversed = settings.AutopilotAxisReversed;
        settings.AutopilotAutoAxis = false;
        settings.AutopilotAxis = Math.Abs(_ring.Axis.X) > .5 ? 0 : Math.Abs(_ring.Axis.Y) > .5 ? 1 : 2;
        settings.AutopilotAxisReversed = _ring.Axis.X + _ring.Axis.Y + _ring.Axis.Z < 0;
        try
        {
            double3 requestedCenter = previousRing.Center + previousRing.Axis * offset;
            bool captured = Capture(owner, settings, requestedCenter);
            if (captured && (_ring.Center - requestedCenter).Length() < .01
                && (offset != 0 || Math.Abs(_ring.Radius - previousRing.Radius) < .01)) return true;
            string failure = captured ? "That station would move the ring onto a different cylinder; select its hull explicitly." : Status;
            _ring = previousRing; _surface = previousSurface; _owner = previousOwner; _target = previousTarget;
            _topology = previousTopology; _clearance = previousClearance; _hasRing = true; _phase = Phase.Captured;
            Status = failure + " Previous preview retained.";
            return false;
        }
        finally { settings.AutopilotAutoAxis = auto; settings.AutopilotAxis = previousAxis; settings.AutopilotAxisReversed = reversed; }
    }

    internal bool BeginAlign(KittenEva owner, SaberSettings settings, double3? preferredPoint = null)
    {
        if (Active) { Status = "Release the current autopilot before starting another approach."; return false; }
        if (!ValidateFlight(owner, out string reason)) { Status = reason; return false; }
        bool needsCapture = !_hasRing || !ReferenceEquals(owner, _owner) || !ReferenceEquals(owner.Target, _target);
        if (needsCapture && !Capture(owner, settings, preferredPoint)) return false;
        // Recheck the exact selected station using current geometry and clearance.
        // Selection must never silently move to another cut location on engagement.
        CutRing selectedRing = _ring;
        if (!needsCapture && !Nudge(owner, settings, 0)) return false;
        _ring = selectedRing; // Preserve the entry marker that the player approved.
        if (_target == null || !WithinEnvelope(owner, _target, out reason)) { Status = reason; return false; }
        double authority = AvailablePulseAcceleration(owner);
        if (!(authority > .0001)) { Status = "No usable MMU translation thrust. Enable RCS and check backpack propellant."; return false; }
        double3 position = (owner.GetPositionCci() - _target.GetPositionCci()).Transform(_target.GetBody2Cci().Inverse());
        double3 velocity = RelativeVelocity(owner, _target).Transform(_target.GetBody2Cci().Inverse());
        if (!PathClear(position, RingPoint(_ring.Radius + _clearance, 0)) || !StoppingPathClear(position, velocity, authority))
        { Status = "Too much drift toward the hull for this approach. Match velocity first, then select the ring again."; return false; }
        if (!CanTrackRotation(_target, authority, 0, out _, out reason)) { Status = reason; return false; }
        CaptureFlightSettings(owner);
        _phase = Phase.Approach;
        _cutWhenReady = _cutStarted = _cuttingMotionReady = false;
        _angle = 0;
        Status = "Approaching the previewed entry point. Abort / release returns manual MMU control.";
        return true;
    }

    internal bool BeginMatchVelocity(KittenEva owner)
    {
        if (Active) { Status = "Release the current autopilot before matching velocity."; return false; }
        if (!TryTarget(owner, out Vehicle target, out string reason) || !ValidateFlight(owner, out reason)
            || !WithinEnvelope(owner, target, out reason)) { Status = reason; return false; }
        if (!(AvailablePulseAcceleration(owner) > .0001))
        { Status = "No usable MMU translation thrust. Enable RCS and check backpack propellant."; return false; }
        if (!ReferenceEquals(_target, target) || !ReferenceEquals(_owner, owner)) InvalidateCapture();
        _owner = owner; _target = target;
        CaptureFlightSettings(owner);
        owner.FlightComputer.SetInertialNullRot();
        _phase = Phase.Braking;
        _cutWhenReady = _cutStarted = _cuttingMotionReady = false;
        Status = "Matching the target vessel's velocity with the MMU.";
        return true;
    }

    internal bool BeginCut(SaberSettings settings, int structuralCount)
    {
        if (_phase != Phase.Hold)
        { Status = "Wait until the kitten is holding at the ring, inspect the pose, then start the cut."; return false; }
        if (_owner == null || _target == null) return false;
        if (!settings.CuttingEnabled) { Status = "Enable Cut vessels on contact before starting the ring cut."; return false; }
        settings.Validate();
        if (!CanTrackRotation(_target, AvailablePulseAcceleration(_owner), settings.AutopilotOrbitSpeed,
            out _orbitSpeed, out string reason)) { Status = reason; return false; }
        _structuralCountAtStart = structuralCount;
        _cutWhenReady = _cutStarted = true;
        _phase = Phase.Igniting;
        Status = $"Igniting the saber; cut speed {_orbitSpeed:F3} m/s. The orbit begins after full deployment.";
        return true;
    }

    internal AutopilotResult Update(SaberSettings settings, double simulationDt,
        float bladeDeployment, int structuralCount, bool cuttingPoseReady, double3 bladeDirectionOwnerBody,
        double3? bladeBaseOwnerBody = null)
    {
        if (!Active || _owner == null || _target == null) return AutopilotResult.Running;
        KittenEva owner = _owner; Vehicle target = _target;
        if (owner.IsDisposed) return AbortInternal("The controlled kitten is no longer available.");
        if (target.IsDisposed || (_cutStarted && structuralCount > _structuralCountAtStart))
            return _cutStarted && structuralCount > _structuralCountAtStart
                ? CompleteInternal("The target separated; MMU control released.")
                : AbortInternal("The targeted vessel is no longer available.");
        if (!ReferenceEquals(owner.Target, target)) return AbortInternal("Target changed; MMU control released.");
        if (!ValidateFlight(owner, out string reason) || !WithinEnvelope(owner, target, out reason)) return AbortInternal(reason);
        if (!double.IsFinite(simulationDt) || simulationDt < 0 || simulationDt > SaberLimits.MaximumSimulationStep)
            return AbortInternal("The simulation step became unsafe for close-proximity guidance.");
        if (simulationDt == 0) { Suspend(); return AutopilotResult.Running; }
        double authority = AvailablePulseAcceleration(owner);
        if (!(authority > .0001)) return AbortInternal("MMU translation thrust is unavailable; autopilot released.");
        doubleQuat targetBody2Cci = target.GetBody2Cci();
        doubleQuat cci2Body = owner.GetBody2Cci().Inverse();
        double3 relativeCci = owner.GetPositionCci() - target.GetPositionCci();
        double3 velocityCci = owner.GetVelocityCci() - target.GetVelocityCci();
        if (_phase == Phase.Braking)
        {
            double speed = velocityCci.Length();
            if (speed < .045) return CompleteInternal("Target velocity matched; manual control restored.");
            double3 acceleration = EvaApproachGuidance.Acceleration(double3.Zero, velocityCci,
                double3.Zero, double3.Zero, authority, 0);
            SetTranslation(owner, FlagsFor(acceleration.Transform(cci2Body), authority));
            Status = $"Matching velocity: {speed:F2} m/s relative speed.";
            return AutopilotResult.Running;
        }
        if (!_hasRing || Topology(target) != _topology)
            return AbortInternal("Target geometry changed; select a new ring before approaching.");
        bool orbitRequested = _phase is Phase.Igniting or Phase.Orbit;
        if (!CanTrackRotation(target, authority, orbitRequested ? settings.AutopilotOrbitSpeed : 0,
            out _orbitSpeed, out reason)) return AbortInternal(reason);
        double3 positionBody = relativeCci.Transform(targetBody2Cci.Inverse());
        double3 velocityBody = RelativeVelocity(owner, target).Transform(targetBody2Cci.Inverse());
        if (!Finite(positionBody) || !Finite(velocityBody)) return AbortInternal("Invalid target-relative navigation state.");
        if (cuttingPoseReady && bladeBaseOwnerBody is { } bladeBase && Finite(bladeBase))
        {
            // Put the blade, rather than the kitten's COM, in the selected cutting plane.
            double3 offset = bladeBase.Transform(owner.GetBody2Cci()).Transform(targetBody2Cci.Inverse());
            _bladeAxialOffset = double3.Dot(offset, _ring.Axis);
        }
        double ringRadius = _ring.Radius + _clearance;
        double angularVelocity = (settings.AutopilotClockwise ? -1 : 1) * _orbitSpeed / ringRadius;
        double3 currentPoint = RingPoint(ringRadius, _angle);
        double3 tangentVelocity = CylinderOrbitGuidance.Velocity(_ring.Axis, _ring.Radial, ringRadius, _angle, angularVelocity);
        _cuttingMotionReady = _phase == Phase.Orbit && cuttingPoseReady
            && (positionBody - currentPoint).Length() < .25 && (velocityBody - tangentVelocity).Length() < .3;
        if (_cuttingMotionReady)
            _angle = CylinderOrbitGuidance.AdvanceAngle(_angle, _orbitSpeed,
                ringRadius, simulationDt, settings.AutopilotClockwise);
        double3 desiredPosition = RingPoint(ringRadius, _angle);
        double3 desiredVelocity = _cuttingMotionReady
            ? CylinderOrbitGuidance.Velocity(_ring.Axis, _ring.Radial, ringRadius, _angle, angularVelocity) : double3.Zero;
        // Validate the current route again: drift must not turn a clear initial entry into a hull crossing.
        if (!PathClear(positionBody, desiredPosition) || !StoppingPathClear(positionBody, velocityBody, authority))
        {
            _phase = Phase.Braking; _cutWhenReady = _cutStarted = _cuttingMotionReady = false;
            SetTranslation(owner, FlagsFor((-velocityCci).Transform(cci2Body), authority));
            Status = "Approach corridor lost; matching velocity. Select another ring after stopping.";
            return AutopilotResult.Running;
        }
        double3 accelerationBody = EvaApproachGuidance.Acceleration(positionBody, velocityBody,
            desiredPosition, desiredVelocity, authority, _phase == Phase.Approach ? 1.5 : .4);
        ApplyAttitude(owner, targetBody2Cci, desiredPosition, settings, bladeDirectionOwnerBody);
        SetTranslation(owner, FlagsFor(accelerationBody.Transform(targetBody2Cci).Transform(cci2Body), authority));
        double positionError = (positionBody - desiredPosition).Length();
        double velocityError = (velocityBody - desiredVelocity).Length();
        if (_phase == Phase.Approach && positionError < .15 && velocityError < .10)
        { _phase = Phase.Hold; Status = "Holding at the ring. Ready for Cut ring."; }
        else if (_phase == Phase.Approach)
            Status = $"Approaching ring: {positionError:F1} m to entry, {velocityBody.Length():F2} m/s relative speed.";
        else if (_phase == Phase.Hold) Status = $"Holding at ring: {positionError:F2} m error. Inspect the pose before cutting.";
        else if (_phase == Phase.Igniting && bladeDeployment >= .98f && cuttingPoseReady)
        { _phase = Phase.Orbit; _angle = 0; Status = "Cutting around the selected ring."; }
        else if (_phase == Phase.Orbit)
        {
            Status = !cuttingPoseReady ? "Holding until a fresh rendered saber pose is available."
                : !_cuttingMotionReady ? "Reacquiring the ring point before resuming the cut."
                : $"Ring travel: {Progress:P0}, {_orbitSpeed:F3} m/s"
                    + (_orbitSpeed < settings.AutopilotOrbitSpeed * .99 ? " (speed reduced to suit MMU thrust)." : ".");
            // Overlap the start to close the seam even when the kitten trails its guidance point.
            double overlap = 2 * Math.Asin(Math.Clamp(.25 / ringRadius, 0, 1)) + .05;
            if (Math.Abs(_angle) >= CylinderOrbitGuidance.FullTurn + overlap)
            {
                Abort("Lap finished; checking the structural cut result.");
                return AutopilotResult.OrbitFinished;
            }
        }
        return AutopilotResult.Running;
    }

    private double3 RingPoint(double radius, double angle) => FlightCenter
        + CylinderOrbitGuidance.Point(_ring.Axis, 0, _ring.Radial, radius, angle);
    private static double3 RelativeVelocity(KittenEva owner, Vehicle target) =>
        owner.GetVelocityCci() - target.GetVelocityCci()
        - double3.Cross(target.BodyRates.Transform(target.GetBody2Cci()), owner.GetPositionCci() - target.GetPositionCci());

    private bool PathClear(double3 start, double3 end)
    {
        if (_surface == null) return false;
        var a = new CutPoint(start.X, start.Y, start.Z); var b = new CutPoint(end.X, end.Y, end.Z);
        _surface.Query(CutBounds.Sweep(a, b, a, b, CuttingRingGeometry.KittenRadius), _nearTriangles);
        if (_nearTriangles.Count > AutopilotGeometryLimits.RouteTriangles) return false;
        foreach (int index in _nearTriangles)
            if (CutGeometry.SegmentTriangleDistanceSquared(a, b, _surface.Triangles[index], out _)
                < CuttingRingGeometry.KittenRadius * CuttingRingGeometry.KittenRadius) return false;
        return true;
    }
    private bool StoppingPathClear(double3 position, double3 velocity, double authority)
    {
        double speed = velocity.Length();
        return speed < .045 || PathClear(position,
            position + velocity / speed * EvaApproachGuidance.StoppingDistance(speed, authority));
    }
    private bool CanTrackRotation(Vehicle target, double authority, double requestedSpeed, out double speed, out string reason) =>
        CylinderOrbitGuidance.TryTrackingSpeed(target.BodyRates.Length(), _ring.Center.Length(),
            _ring.Radius + _clearance, authority, requestedSpeed, out speed, out reason);

    internal string GetDiagnostics(KittenEva? owner, SaberSettings settings)
    {
        if (owner == null) return "Control an EVA kitten to see autopilot diagnostics.";
        double authority = AvailablePulseAcceleration(owner);
        Vehicle? target = owner.Target as Vehicle;
        double spin = target?.BodyRates.Length() ?? 0;
        double plannedSpeed = 0;
        string tracking = "Select a ring to evaluate tracking.";
        if (_hasRing && ReferenceEquals(target, _target))
        {
            CanTrackRotation(target!, authority, settings.AutopilotOrbitSpeed, out plannedSpeed, out tracking);
            if (tracking.Length == 0) tracking = "Tracking is feasible at the planned cut speed.";
        }
        return FormattableString.Invariant($"""
            Status: {Status}
            Phase: {_phase}; target: {target?.Id ?? "none"}; ring selected: {_hasRing}
            MMU pulse acceleration: {authority:G4} m/s²; kitten mass: {owner.TotalMass:F2} kg
            Target spin: {spin:G4} rad/s ({spin * 180 / Math.PI:F3} degrees/s)
            Flight ring radius: {_ring.Radius + _clearance:F3} m; centre offset: {_ring.Center.Length():F3} m
            Cut speed requested: {settings.AutopilotOrbitSpeed:F3} m/s; planned: {plannedSpeed:F3} m/s
            Blade plane alignment offset: {_bladeAxialOffset:F3} m
            {tracking}
            """);
    }
    private static bool TryTarget(KittenEva owner, out Vehicle target, out string reason)
    {
        target = owner.Target as Vehicle ?? null!;
        reason = "Choose a vessel in the target dropdown first.";
        if (target == null || target is KittenEva || target.IsDisposed || target.IsEditedVehicle || ReferenceEquals(owner, target)) return false;
        if (!ReferenceEquals(owner.Parent, target.Parent)) { reason = "The kitten and target must orbit the same parent body."; return false; }
        if (!target.HasPhysicsBubble) { reason = "The target must be nearby and loaded in the physics bubble."; return false; }
        reason = ""; return true;
    }
    private static bool ValidateFlight(KittenEva owner, out string reason)
    {
        reason = "";
        if (owner.LocomotionState.Mode != LocomotionMode.Mmu) reason = "Enable the kitten's MMU before flying to the ring or matching velocity.";
        else if (!owner.FlightComputer.VehicleConfig.HasRcs) reason = "The kitten has no available MMU thrusters.";
        else if (owner.ControlPart != null || owner.ControlConnector != null) reason = "Clear the kitten's custom control part before using the autopilot.";
        else if (ManualInputsField == null) reason = "This game build has an incompatible MMU input field.";
        return reason.Length == 0;
    }
    private static bool WithinEnvelope(KittenEva owner, Vehicle target, out string reason)
    {
        reason = "";
        if (!ReferenceEquals(owner.Parent, target.Parent) || !target.HasPhysicsBubble)
            reason = "Target left local flight range; autopilot released.";
        else if (!Finite(owner.GetPositionCci() - target.GetPositionCci()) || !Finite(owner.GetVelocityCci() - target.GetVelocityCci())
            || (owner.GetPositionCci() - target.GetPositionCci()).Length() > target.BoundingSphereRadiusBody + 500
            || (owner.GetVelocityCci() - target.GetVelocityCci()).Length() > 20)
            reason = "Local EVA guidance needs a target within 500 m of its bounds and less than 20 m/s relative speed.";
        return reason.Length == 0;
    }
    private static int Topology(Vehicle target)
    {
        var hash = new HashCode();
        hash.Add(target.CenterOfMassAsmb);
        var visited = new HashSet<Part>(ReferenceEqualityComparer.Instance);
        foreach (var part in target.Parts.Parts) Add(part);
        return hash.ToHashCode();
        void Add(Part part)
        {
            if (!visited.Add(part)) return;
            hash.Add(part.InstanceId); hash.Add(part.PositionParentAsmb); hash.Add(part.Asmb2ParentAsmb); hash.Add(part.Scale);
            foreach (var sub in part.SubParts) Add(sub);
        }
    }
    private static double AvailablePulseAcceleration(KittenEva owner)
    {
        double3 positive = double3.Zero, negative = double3.Zero;
        var states = ModuleStateful<ThrusterController, ThrusterControllerState, ThrusterControllerGlobalState, EmptyStruct>
            .InitializeHotPathList(owner.Parts.States);
        foreach (var entry in states.ModulesAndStates)
        {
            if (!entry.Module.IsActive || !entry.State.IsPropellantAvailable) continue;
            // Convert a manual minimum-impulse pulse to average acceleration at its 0.15 s cadence.
            var force = entry.State.IntendedForce;
            double duty = Math.Clamp(entry.Module.MinimumPulseTime / .15, 0, 1);
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateForward) != 0) positive.X += Math.Max(0, force.X) * duty;
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateBackward) != 0) negative.X += Math.Max(0, -force.X) * duty;
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateRight) != 0) positive.Y += Math.Max(0, force.Y) * duty;
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateLeft) != 0) negative.Y += Math.Max(0, -force.Y) * duty;
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateDown) != 0) positive.Z += Math.Max(0, force.Z) * duty;
            if ((entry.State.ControlMap & ThrusterMapFlags.TranslateUp) != 0) negative.Z += Math.Max(0, -force.Z) * duty;
        }
        double minimum = Math.Min(Math.Min(Math.Min(positive.X, positive.Y), positive.Z),
            Math.Min(Math.Min(negative.X, negative.Y), negative.Z));
        return owner.TotalMass > 0 && double.IsFinite(minimum) ? minimum / owner.TotalMass * .5 : 0;
    }

    internal void Suspend() { if (_owner is { IsDisposed: false }) SetTranslation(_owner, ThrusterMapFlags.None); }
    internal void InvalidateCapture()
    {
        if (Active) return;
        _phase = Phase.Idle; _hasRing = false; _surface = null; _owner = null; _target = null; _angle = _orbitSpeed = 0;
        _cutWhenReady = _cutStarted = _cuttingMotionReady = false;
        Status = "Selection changed; find a ring or pick a point on the target hull.";
    }
    internal void Abort(string reason)
    {
        if (Active) ReleaseFlightSettings();
        _phase = _hasRing && _target is { IsDisposed: false } ? Phase.Captured : Phase.Idle;
        _cutWhenReady = _cutStarted = _cuttingMotionReady = false; Status = reason;
    }
    internal void Reset()
    {
        if (Active) ReleaseFlightSettings();
        _snapshot = null; _phase = Phase.Idle; InvalidateCapture();
    }
    private AutopilotResult AbortInternal(string message) { Abort(message); return AutopilotResult.Aborted; }
    private AutopilotResult CompleteInternal(string message) { Abort(message); return AutopilotResult.Completed; }
    private bool FailCapture(string message) { InvalidateCapture(); Status = message; return false; }
    private void CaptureFlightSettings(KittenEva owner)
    {
        if (_snapshot != null) return;
        FlightComputer computer = owner.FlightComputer;
        _snapshot = new(computer.AttitudeMode, computer.AttitudeFrame, computer.AttitudeTrackTarget,
            computer.CustomAttitudeTarget, computer.ManualThrustMode, computer.RollMode, computer.RCSMode, computer.RateLimit, owner.ControlMode);
        owner.SetControlMode(KittenControlMode.Direct);
        computer.RCSMode = FlightComputerRCSMode.Enabled; computer.ManualThrustMode = FlightComputerManualThrustMode.Pulse;
    }
    private void ReleaseFlightSettings()
    {
        if (_owner is not { IsDisposed: false } owner || _snapshot == null) { _snapshot = null; return; }
        SetTranslation(owner, ThrusterMapFlags.None);
        FlightComputer computer = owner.FlightComputer;
        computer.AttitudeMode = _snapshot.AttitudeMode; computer.AttitudeFrame = _snapshot.AttitudeFrame;
        computer.AttitudeTrackTarget = _snapshot.TrackTarget; computer.CustomAttitudeTarget = _snapshot.CustomTarget;
        computer.ManualThrustMode = _snapshot.ThrustMode; computer.RollMode = _snapshot.RollMode;
        computer.RCSMode = _snapshot.RcsMode; computer.RateLimit = _snapshot.RateLimit;
        owner.SetControlMode(_snapshot.ControlMode); _snapshot = null;
    }
    private void ApplyAttitude(KittenEva owner, doubleQuat targetBody2Cci,
        double3 desiredPositionBody, SaberSettings settings, double3 bladeDirectionOwnerBody)
    {
        double3 outwardBody = desiredPositionBody - _ring.Center;
        outwardBody -= _ring.Axis * double3.Dot(outwardBody, _ring.Axis);
        double3 inwardCci = (-outwardBody.NormalizeOrZero()).Transform(targetBody2Cci);
        double3 axisCci = _ring.Axis.Transform(targetBody2Cci);
        doubleQuat desiredBody2Cci = CylinderOrbitGuidance.DesiredBody2Cci(bladeDirectionOwnerBody, inwardCci, axisCci, settings.AutopilotRollQuarterTurns);
        doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(owner.Orbit.Parent.GetCce2Cci());
        doubleQuat frame2Desired = doubleQuat.Concatenate(desiredBody2Cci, frame2Cci.Inverse());
        FlightComputer computer = owner.FlightComputer;
        computer.AttitudeMode = FlightComputerAttitudeMode.Auto; computer.AttitudeFrame = VehicleReferenceFrame.EclBody;
        computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        computer.CustomAttitudeTarget = VehicleReferenceFrame.EclBody.QuaternionToEulerAngles(frame2Desired);
        computer.RollMode = FlightComputerRollMode.Up; computer.RateLimit = .8f;
        computer.RCSMode = FlightComputerRCSMode.Enabled; computer.ManualThrustMode = FlightComputerManualThrustMode.Pulse;
    }
    private static ThrusterMapFlags FlagsFor(double3 acceleration, double authority)
    {
        double threshold = Math.Min(.02, authority * .1);
        ThrusterMapFlags flags = ThrusterMapFlags.None;
        if (acceleration.X > threshold) flags |= ThrusterMapFlags.TranslateForward;
        else if (acceleration.X < -threshold) flags |= ThrusterMapFlags.TranslateBackward;
        if (acceleration.Y > threshold) flags |= ThrusterMapFlags.TranslateRight;
        else if (acceleration.Y < -threshold) flags |= ThrusterMapFlags.TranslateLeft;
        if (acceleration.Z > threshold) flags |= ThrusterMapFlags.TranslateDown;
        else if (acceleration.Z < -threshold) flags |= ThrusterMapFlags.TranslateUp;
        return flags;
    }
    private static void SetTranslation(Vehicle vehicle, ThrusterMapFlags flags)
    {
        if (ManualInputsField?.GetValue(vehicle) is not ManualControlInputs inputs) return;
        inputs.ThrusterCommandFlags = flags; ManualInputsField.SetValue(vehicle, inputs);
    }
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
