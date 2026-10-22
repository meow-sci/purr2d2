using System.Reflection;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Autopilot;

internal enum AutopilotResult { Running, Completed, Aborted }

internal sealed class EvaCuttingAutopilot
{
    private enum Phase { Idle, Captured, Approach, Hold, Igniting, Orbit }

    private static readonly FieldInfo? ManualInputsField = typeof(Vehicle).GetField(
        "_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed record FlightSnapshot(
        FlightComputerAttitudeMode AttitudeMode,
        VehicleReferenceFrame AttitudeFrame,
        FlightComputerAttitudeTrackTarget TrackTarget,
        double3 CustomTarget,
        FlightComputerManualThrustMode ThrustMode,
        FlightComputerRollMode RollMode,
        FlightComputerRCSMode RcsMode,
        float RateLimit,
        KittenControlMode ControlMode);

    private KittenEva? _owner;
    private Vehicle? _target;
    private FlightSnapshot? _snapshot;
    private Phase _phase;
    private double3 _axisBody;
    private double3 _startRadialBody;
    private double _axialPosition;
    private double _surfaceRadius;
    private double _orbitRadius;
    private double _angle;
    private bool _cutWhenReady;
    private bool _cutStarted;
    private bool _cuttingMotionReady;
    private int _structuralCountAtStart;

    internal bool Active => _phase is Phase.Approach or Phase.Hold or Phase.Igniting or Phase.Orbit;
    internal bool Captured => _phase != Phase.Idle;
    internal bool WantsEquipped => Active;
    internal bool WantsIgnited => _phase is Phase.Igniting or Phase.Orbit;
    internal bool AllowsCutting => _phase == Phase.Orbit && _cuttingMotionReady;
    internal float Progress => (float)CylinderOrbitGuidance.Progress(_angle);
    internal string Status { get; private set; } = "Set a stock vessel target, choose its cylinder axis, then capture the cut ring.";

    internal bool Capture(KittenEva owner, SaberSettings settings)
    {
        if (Active)
        {
            Status = "Release the active autopilot before capturing another ring.";
            return false;
        }
        if (owner.Target is not Vehicle target || target.IsDisposed || ReferenceEquals(target, owner))
            return FailCapture("Select a vessel with KSA's stock targeting system first.");
        if (!ReferenceEquals(owner.Parent, target.Parent))
            return FailCapture("The kitten and target must orbit the same parent body.");
        if (!target.HasPhysicsBubble)
            return FailCapture("The target must be nearby and inside the active physics bubble.");
        if (owner.LocomotionState.Mode != LocomotionMode.Mmu)
            return FailCapture("Enable the kitten's MMU before using the cutting autopilot.");
        if (!owner.FlightComputer.VehicleConfig.HasRcs)
            return FailCapture("The controlled kitten has no available MMU thrusters.");
        if (owner.ControlPart != null || owner.ControlConnector != null)
            return FailCapture("Clear the kitten's custom control part before using the cutting autopilot.");
        if (ManualInputsField == null)
            return FailCapture("This KSA build does not expose the expected manual-control input field.");

        settings.Validate();
        double3 axis = CylinderOrbitGuidance.Axis(settings.AutopilotAxis, settings.AutopilotAxisReversed);
        doubleQuat targetBody2Cci = target.GetBody2Cci();
        double3 relativeBody = (owner.GetPositionCci() - target.GetPositionCci()).Transform(targetBody2Cci.Inverse());
        if (!CylinderOrbitGuidance.TryRadial(relativeBody, axis, out double axial, out double3 radial))
            return FailCapture("Move away from the selected cylinder axis before capturing the ring.");
        if (!TryFindSurface(target, axis, axial, radial, out double surfaceRadius))
            return FailCapture("No closed target surface was found at this axial station. Move beside the hull and capture again.");

        _owner = owner;
        _target = target;
        _axisBody = axis;
        _startRadialBody = radial;
        _axialPosition = axial;
        _surfaceRadius = surfaceRadius;
        _orbitRadius = surfaceRadius + settings.AutopilotClearance;
        _angle = 0;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        _phase = Phase.Captured;
        Status = $"Captured {target.Id}: hull radius {surfaceRadius:F2} m, flight radius {_orbitRadius:F2} m. Align and inspect the saber orientation.";
        return true;
    }

    internal bool BeginAlign(KittenEva owner, SaberSettings settings)
    {
        if (Active)
        {
            Status = "The MMU autopilot is already active; use Abort before starting another alignment.";
            return false;
        }
        if (!Captured || !ReferenceEquals(owner, _owner) || !ReferenceEquals(owner.Target, _target))
            if (!Capture(owner, settings)) return false;
        if (_target is not { IsDisposed: false } || owner.LocomotionState.Mode != LocomotionMode.Mmu)
            return FailCapture("The captured kitten or target is no longer available in MMU flight.");
        CaptureFlightSettings(owner);
        _phase = Phase.Approach;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        _angle = 0;
        Status = "Approaching the captured ring with MMU pulse control. Use Abort to take over immediately.";
        return true;
    }

    internal bool BeginCut(int structuralCount)
    {
        if (!Active)
        {
            Status = "Align and approach the captured ring before starting the cut.";
            return false;
        }
        if (_cutWhenReady)
        {
            Status = _phase is Phase.Igniting or Phase.Orbit
                ? "The cut orbit is already running."
                : "The cut orbit is already armed; finishing the approach first.";
            return true;
        }
        _structuralCountAtStart = structuralCount;
        _cutWhenReady = true;
        _cutStarted = true;
        if (_phase == Phase.Hold) _phase = Phase.Igniting;
        Status = _phase == Phase.Igniting
            ? "Igniting the saber; the orbit begins after full deployment."
            : "Cut orbit armed; finishing the approach first.";
        return true;
    }

    internal AutopilotResult Update(SaberSettings settings, double simulationDt,
        float bladeDeployment, int structuralCount, bool cuttingPoseReady,
        double3 bladeDirectionOwnerBody)
    {
        if (!Active || _owner == null || _target == null) return AutopilotResult.Running;
        KittenEva owner = _owner;
        Vehicle target = _target;
        if (owner.IsDisposed)
            return AbortInternal("The controlled kitten is no longer available.");
        if (target.IsDisposed)
        {
            if (_cutStarted && structuralCount > _structuralCountAtStart)
                return CompleteInternal("The target separated during the cut; MMU control released.");
            return AbortInternal("The targeted vessel is no longer available.");
        }
        if (!ReferenceEquals(owner.Target, target))
            return AbortInternal("The stock flight-computer target changed; MMU control released.");
        if (owner.LocomotionState.Mode != LocomotionMode.Mmu)
            return AbortInternal("The kitten left MMU mode; autopilot released.");
        if (!ReferenceEquals(owner.Parent, target.Parent))
            return AbortInternal("The kitten and target no longer share a reference body.");
        if (!target.HasPhysicsBubble)
            return AbortInternal("The targeted vessel left the active physics bubble; MMU control released.");
        if (!double.IsFinite(simulationDt) || simulationDt < 0 || simulationDt > .15)
            return AbortInternal("The simulation step became unsafe for close-proximity guidance.");

        settings.Validate();
        _orbitRadius = _surfaceRadius + settings.AutopilotClearance;
        doubleQuat targetBody2Cci = target.GetBody2Cci();
        doubleQuat cci2TargetBody = targetBody2Cci.Inverse();
        double3 relativeCci = owner.GetPositionCci() - target.GetPositionCci();
        double3 targetOmegaCci = target.BodyRates.Transform(targetBody2Cci);
        double3 relativeVelocityCci = owner.GetVelocityCci() - target.GetVelocityCci()
            - double3.Cross(targetOmegaCci, relativeCci);
        double3 positionBody = relativeCci.Transform(cci2TargetBody);
        double3 velocityBody = relativeVelocityCci.Transform(cci2TargetBody);

        if (!Finite(positionBody) || !Finite(velocityBody))
            return AbortInternal("The target-relative navigation state became invalid; MMU control released.");

        if (positionBody.Length() > Math.Max(25, target.BoundingSphereRadiusBody + 10)
            || velocityBody.Length() > 8)
            return AbortInternal("Close-proximity safety limit exceeded; MMU control released.");

        double angularVelocity = (settings.AutopilotClockwise ? -1 : 1)
            * settings.AutopilotOrbitSpeed / _orbitRadius;
        double3 currentRingPosition = CylinderOrbitGuidance.Point(
            _axisBody, _axialPosition, _startRadialBody, _orbitRadius, _angle);
        double3 currentRingVelocity = CylinderOrbitGuidance.Velocity(
            _axisBody, _startRadialBody, _orbitRadius, _angle, angularVelocity);
        _cuttingMotionReady = _phase == Phase.Orbit && cuttingPoseReady
            && (positionBody - currentRingPosition).Length() < .25
            && (velocityBody - currentRingVelocity).Length() < .5;
        if (_cuttingMotionReady)
            _angle = CylinderOrbitGuidance.AdvanceAngle(_angle, settings.AutopilotOrbitSpeed,
                _orbitRadius, simulationDt, settings.AutopilotClockwise);
        double3 desiredPosition = CylinderOrbitGuidance.Point(
            _axisBody, _axialPosition, _startRadialBody, _orbitRadius, _angle);
        double3 desiredVelocity = _cuttingMotionReady
            ? CylinderOrbitGuidance.Velocity(_axisBody, _startRadialBody, _orbitRadius, _angle, angularVelocity)
            : double3.Zero;
        double3 accelerationBody = CylinderOrbitGuidance.Acceleration(
            positionBody, velocityBody, desiredPosition, desiredVelocity);

        ApplyAttitude(owner, targetBody2Cci, desiredPosition, settings, bladeDirectionOwnerBody);
        SetTranslation(owner, FlagsFor(accelerationBody.Transform(targetBody2Cci)
            .Transform(owner.GetBody2Cci().Inverse()), positionBody - desiredPosition, velocityBody - desiredVelocity));

        double positionError = (positionBody - desiredPosition).Length();
        double velocityError = (velocityBody - desiredVelocity).Length();
        if (_phase == Phase.Approach && positionError < .12 && velocityError < .15)
        {
            _phase = _cutWhenReady ? Phase.Igniting : Phase.Hold;
            Status = _cutWhenReady
                ? "Aligned at the hull; igniting the saber."
                : "Aligned and holding. Inspect the orientation, use ±90° if needed, then start the cut orbit.";
        }
        else if (_phase == Phase.Hold)
            Status = $"Holding at the captured ring ({positionError:F2} m position error).";
        else if (_phase == Phase.Igniting && bladeDeployment >= .98f && cuttingPoseReady)
        {
            _phase = Phase.Orbit;
            _angle = 0;
            Status = "Cutting around the cylinder.";
        }
        else if (_phase == Phase.Orbit)
        {
            Status = !cuttingPoseReady
                ? "Holding the current point until a fresh rendered saber pose is available."
                : !_cuttingMotionReady
                    ? "Reacquiring the current ring point before resuming the cut."
                    : $"Cutting around the cylinder: {Progress:P0}.";
            if (Math.Abs(_angle) >= CylinderOrbitGuidance.FullTurn)
                return CompleteInternal(structuralCount > _structuralCountAtStart
                    ? "Cut orbit complete; structural cut registered and MMU control released."
                    : "Cut orbit complete; no structural cut was accepted by the selected cutting mode.");
        }
        return AutopilotResult.Running;
    }

    internal void Suspend()
    {
        if (_owner != null) SetTranslation(_owner, ThrusterMapFlags.None);
    }

    internal void InvalidateCapture()
    {
        if (Active) return;
        _phase = Phase.Idle;
        _owner = null;
        _target = null;
        _angle = 0;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        Status = "Cylinder axis changed; capture the target ring again.";
    }

    internal void Abort(string reason)
    {
        if (Active) ReleaseFlightSettings();
        _phase = _target is { IsDisposed: false } ? Phase.Captured : Phase.Idle;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        Status = reason;
    }

    internal void Reset()
    {
        if (Active) ReleaseFlightSettings();
        _owner = null;
        _target = null;
        _snapshot = null;
        _phase = Phase.Idle;
        _angle = 0;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        Status = "Set a stock vessel target, choose its cylinder axis, then capture the cut ring.";
    }

    private AutopilotResult AbortInternal(string message)
    {
        Abort(message);
        return AutopilotResult.Aborted;
    }

    private AutopilotResult CompleteInternal(string message)
    {
        ReleaseFlightSettings();
        _phase = _target is { IsDisposed: false } ? Phase.Captured : Phase.Idle;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        Status = message;
        return AutopilotResult.Completed;
    }

    private bool FailCapture(string message)
    {
        _phase = Phase.Idle;
        _owner = null;
        _target = null;
        _cutWhenReady = false;
        _cutStarted = false;
        _cuttingMotionReady = false;
        Status = message;
        return false;
    }

    private void CaptureFlightSettings(KittenEva owner)
    {
        if (_snapshot != null) return;
        FlightComputer computer = owner.FlightComputer;
        _snapshot = new(computer.AttitudeMode, computer.AttitudeFrame, computer.AttitudeTrackTarget,
            computer.CustomAttitudeTarget, computer.ManualThrustMode, computer.RollMode,
            computer.RCSMode, computer.RateLimit, owner.ControlMode);
        owner.SetControlMode(KittenControlMode.Direct);
        computer.RCSMode = FlightComputerRCSMode.Enabled;
        computer.ManualThrustMode = FlightComputerManualThrustMode.Pulse;
    }

    private void ReleaseFlightSettings()
    {
        if (_owner is not { IsDisposed: false } owner || _snapshot == null)
        {
            _snapshot = null;
            return;
        }
        SetTranslation(owner, ThrusterMapFlags.None);
        FlightComputer computer = owner.FlightComputer;
        computer.AttitudeMode = _snapshot.AttitudeMode;
        computer.AttitudeFrame = _snapshot.AttitudeFrame;
        computer.AttitudeTrackTarget = _snapshot.TrackTarget;
        computer.CustomAttitudeTarget = _snapshot.CustomTarget;
        computer.ManualThrustMode = _snapshot.ThrustMode;
        computer.RollMode = _snapshot.RollMode;
        computer.RCSMode = _snapshot.RcsMode;
        computer.RateLimit = _snapshot.RateLimit;
        owner.SetControlMode(_snapshot.ControlMode);
        _snapshot = null;
    }

    private void ApplyAttitude(KittenEva owner, doubleQuat targetBody2Cci,
        double3 desiredPositionBody, SaberSettings settings, double3 bladeDirectionOwnerBody)
    {
        double3 outwardBody = desiredPositionBody - _axisBody * double3.Dot(desiredPositionBody, _axisBody);
        double3 inwardCci = (-outwardBody.NormalizeOrZero()).Transform(targetBody2Cci);
        double3 axisCci = _axisBody.Transform(targetBody2Cci);
        doubleQuat desiredBody2Cci = CylinderOrbitGuidance.DesiredBody2Cci(
            bladeDirectionOwnerBody, inwardCci, axisCci, settings.AutopilotRollQuarterTurns);
        doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(owner.Orbit.Parent.GetCce2Cci());
        doubleQuat frame2Desired = doubleQuat.Concatenate(desiredBody2Cci, frame2Cci.Inverse());
        FlightComputer computer = owner.FlightComputer;
        computer.AttitudeMode = FlightComputerAttitudeMode.Auto;
        computer.AttitudeFrame = VehicleReferenceFrame.EclBody;
        computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        computer.CustomAttitudeTarget = VehicleReferenceFrame.EclBody.QuaternionToEulerAngles(frame2Desired);
        computer.RollMode = FlightComputerRollMode.Decoupled;
        computer.RateLimit = .8f;
        computer.RCSMode = FlightComputerRCSMode.Enabled;
        computer.ManualThrustMode = FlightComputerManualThrustMode.Pulse;
    }

    private static ThrusterMapFlags FlagsFor(double3 accelerationOwnerBody,
        double3 positionErrorTargetBody, double3 velocityErrorTargetBody)
    {
        if (positionErrorTargetBody.Length() < .035 && velocityErrorTargetBody.Length() < .035)
            return ThrusterMapFlags.None;
        const double threshold = .06;
        ThrusterMapFlags flags = ThrusterMapFlags.None;
        if (accelerationOwnerBody.X > threshold) flags |= ThrusterMapFlags.TranslateForward;
        else if (accelerationOwnerBody.X < -threshold) flags |= ThrusterMapFlags.TranslateBackward;
        if (accelerationOwnerBody.Y > threshold) flags |= ThrusterMapFlags.TranslateRight;
        else if (accelerationOwnerBody.Y < -threshold) flags |= ThrusterMapFlags.TranslateLeft;
        if (accelerationOwnerBody.Z > threshold) flags |= ThrusterMapFlags.TranslateDown;
        else if (accelerationOwnerBody.Z < -threshold) flags |= ThrusterMapFlags.TranslateUp;
        return flags;
    }

    private static void SetTranslation(Vehicle vehicle, ThrusterMapFlags flags)
    {
        if (ManualInputsField == null) return;
        if (ManualInputsField.GetValue(vehicle) is not ManualControlInputs inputs) return;
        inputs.ThrusterCommandFlags = flags;
        ManualInputsField.SetValue(vehicle, inputs);
    }

    private static bool TryFindSurface(Vehicle target, double3 axisBody, double axial,
        double3 radialBody, out double radius)
    {
        radius = 0;
        double extent = Math.Max(1, target.BoundingSphereRadiusBody) + 2;
        double3 axisCce = axisBody.Transform(target.Body2Cce);
        double3 radialCce = radialBody.Transform(target.Body2Cce);
        var ray = new Ray(axisCce * axial + radialCce * extent, -radialCce);
        double4x4 matrix = target.GetMatrixAsmb2Ego(double3.Zero);
        double nearest = double.PositiveInfinity;
        double3 nearestAsmb = double3.Zero;
        foreach (Part part in target.Parts.Parts)
        {
            if (part.RayCastEgo(in matrix, ray, out double distance, out _, out double3 nearAsmb,
                out _, out _, out _, out _, out _) && distance >= 0 && distance < nearest)
            {
                nearest = distance;
                nearestAsmb = nearAsmb;
            }
        }
        if (!double.IsFinite(nearest)) return false;
        double3 hitBody = target.PosAsmbToBody(nearestAsmb);
        radius = double3.Dot(hitBody - axisBody * axial, radialBody);
        return double.IsFinite(radius) && radius > .02 && radius <= extent;
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
