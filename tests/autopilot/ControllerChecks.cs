using Brutal.Numerics;
using KSA;
using Purr2D2.Autopilot;
using Purr2D2.Core;

internal static class ControllerChecks
{
    private static void Main()
    {
        CaptureAndEngagement();
        MatchVelocityAndRelease();
        SuspensionAndAbort();
        ActiveResetRestoresFlight();
        SelectionRevalidation();
        HoldCorridorLoss();
        SpinRefusal();
        foreach (string reason in new[] { "target", "topology", "center of mass", "spin", "MMU", "propellant", "step", "range" })
            InvalidFlightReleases(reason);
        CutStateMachine();
        ApproachWithAutomaticCutSpeed();
        NativePulseApproach(10, new(6, 6, 6));
        NativePulseApproach(100, new(6, 6, 6));
        NativePulseApproach(10, new(4, 8, 12));
        NativePulseApproach(2.2, new(40, 60, 80), mass: 150, pulseTime: .001);
        NativePulseCutOrbit();
        Console.WriteLine("PASS: production EVA autopilot controller integration checks");
    }

    private static void CaptureAndEngagement()
    {
        var f = new Fixture();
        Check(!f.Pilot.BeginCut(f.Settings, 0), "cut cannot start before the controller holds at a ring");
        f.Owner.LocomotionState.Mode = LocomotionMode.Other;
        Check(!f.Pilot.BeginAlign(f.Owner, f.Settings) && !f.Pilot.Active,
            "non-MMU locomotion refuses engagement");
        f.AssertRestored("failed engagement");
        f.Owner.LocomotionState.Mode = LocomotionMode.Mmu;
        Check(f.Pilot.Capture(f.Owner, f.Settings), "production geometry captures a cylindrical ring");
        Check(f.Pilot.Captured && !f.Pilot.Active && f.Pilot.TryGetPreview(out _, out _),
            "capture exposes a preview without taking control");
        f.AssertRestored("ring preview");
        Check(f.Pilot.BeginAlign(f.Owner, f.Settings), "selected ring engages a clear approach");
        Check(f.Pilot.Active && f.Pilot.WantsEquipped && !f.Pilot.WantsIgnited && !f.Pilot.AllowsCutting,
            "approach equips the saber with ignition and cutting inhibited");
        Check(f.Owner.ControlMode == KittenControlMode.Direct
            && f.Owner.FlightComputer.RCSMode == FlightComputerRCSMode.Enabled
            && f.Owner.FlightComputer.ManualThrustMode == FlightComputerManualThrustMode.Pulse,
            "engagement selects native direct-control pulse RCS");
        Check(!f.Pilot.Capture(f.Owner, f.Settings), "an active approach cannot replace its selected ring");
        f.Pilot.Abort("fixture cleanup");
        f.AssertRestored("engagement release");
    }

    private static void MatchVelocityAndRelease()
    {
        var f = new Fixture();
        f.Owner.Velocity = new(.3, -.2, .1);
        Check(f.Pilot.BeginMatchVelocity(f.Owner), "velocity matching engages without selecting geometry");
        f.Step();
        ThrusterMapFlags flags = f.Owner.ManualInputs.ThrusterCommandFlags;
        Check(flags.HasFlag(ThrusterMapFlags.TranslateBackward)
            && flags.HasFlag(ThrusterMapFlags.TranslateRight) && flags.HasFlag(ThrusterMapFlags.TranslateUp),
            "velocity matching maps body X/Y/Z braking to the native translation signs");
        f.Owner.Velocity = double3.Zero;
        Check(f.Step() == AutopilotResult.Completed && !f.Pilot.Active,
            "matching target velocity completes and releases control");
        f.AssertRestored("velocity match completion");

        var rotated = new Fixture();
        rotated.Owner.Body2Cci = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2);
        rotated.Owner.Velocity = new(.3, -.2, .1);
        Check(rotated.Pilot.BeginMatchVelocity(rotated.Owner), "rotated kitten engages velocity matching");
        rotated.Step();
        ThrusterMapFlags rotatedFlags = rotated.Owner.ManualInputs.ThrusterCommandFlags;
        Check(rotatedFlags.HasFlag(ThrusterMapFlags.TranslateForward)
            && rotatedFlags.HasFlag(ThrusterMapFlags.TranslateRight) && rotatedFlags.HasFlag(ThrusterMapFlags.TranslateUp),
            "native translation directions follow the rotated kitten body frame");
        rotated.Pilot.Abort("rotated fixture cleanup");
        rotated.AssertRestored("rotated match release");
    }

    private static void SuspensionAndAbort()
    {
        var f = new Fixture();
        f.Engage();
        f.Step();
        Check(f.Owner.ManualInputs.ThrusterCommandFlags != ThrusterMapFlags.None,
            "a clear approach sends native translation commands");
        Check(f.Owner.FlightComputer.RollMode == FlightComputerRollMode.Up,
            "guidance couples roll so the approved saber orientation is controlled");
        f.Pilot.Suspend();
        Check(f.Pilot.Active && f.Owner.ManualInputs.ThrusterCommandFlags == ThrusterMapFlags.None,
            "suspension clears translation without losing the active phase");
        Check(f.Step(0) == AutopilotResult.Running
            && f.Owner.ManualInputs.ThrusterCommandFlags == ThrusterMapFlags.None,
            "a paused simulation leaves no translation command latched");
        f.Pilot.Abort("fixture abort");
        Check(!f.Pilot.Active && !f.Pilot.WantsIgnited && !f.Pilot.AllowsCutting,
            "abort ends guidance and inhibits cutting");
        f.AssertRestored("explicit abort");
        f.Pilot.Reset();
        Check(!f.Pilot.Captured && !f.Pilot.TryGetPreview(out _, out _), "reset discards the selected geometry");
    }

    private static void ActiveResetRestoresFlight()
    {
        var f = new Fixture();
        f.Engage();
        f.Step();
        f.Pilot.Reset();
        Check(!f.Pilot.Active && !f.Pilot.Captured && !f.Pilot.WantsIgnited && !f.Pilot.AllowsCutting,
            "reset during an active approach releases control and discards its capture");
        f.AssertRestored("active reset");
    }

    private static void SelectionRevalidation()
    {
        var f = new Fixture();
        Check(!f.Pilot.BeginAlign(f.Owner, f.Settings, new double3(20, 0, 0)) && !f.Pilot.Active,
            "one-click approach does not replace an invalid explicit hull station with an automatic ring");
        Check(f.Pilot.Capture(f.Owner, f.Settings) && f.Pilot.TryGetPreview(out _, out _),
            "selection fixture starts with an inspectable ring");
        f.Pilot.TryGetPreview(out _, out CutRing previous);
        Check(!f.Pilot.Nudge(f.Owner, f.Settings, 10), "a nudge beyond the cylinder end is refused");
        Check(f.Pilot.TryGetPreview(out _, out CutRing retained) && retained == previous,
            "a refused nudge preserves the exact previous ring preview");
        f.Target.CenterOfMassAsmb = new(.1, 0, 0);
        Check(!f.Pilot.TryGetPreview(out _, out _), "a target COM shift invalidates the body-relative preview");
        Check(!f.Pilot.BeginAlign(f.Owner, f.Settings) && !f.Pilot.Active,
            "engagement refuses stale geometry instead of silently selecting a new ring");
        f.AssertRestored("stale preview refusal");
    }

    private static void HoldCorridorLoss()
    {
        var f = new Fixture();
        f.Engage();
        f.Pilot.TryGetPreview(out _, out CutRing ring);
        double3 entry = ring.Center + ring.Radial * (ring.Radius + f.Settings.AutopilotClearance);
        f.Owner.Position = entry;
        f.Step();
        Check(f.Pilot.Status.StartsWith("Holding", StringComparison.Ordinal), "corridor fixture reaches Hold");
        // The hull is unchanged, but a disturbed kitten on the far side no longer
        // has a clear route back to the held point. Revalidation must run in Hold.
        f.Owner.Position = ring.Center - ring.Radial * (ring.Radius + f.Settings.AutopilotClearance);
        f.Owner.Velocity = new(0, .1, 0);
        Check(f.Step() == AutopilotResult.Running && f.Pilot.Active && !f.Pilot.WantsIgnited
            && !f.Pilot.AllowsCutting && f.Pilot.Status.StartsWith("Approach corridor lost", StringComparison.Ordinal),
            "a blocked Hold route switches to braking with ignition and cutting inhibited");
        Check(!f.Pilot.BeginCut(f.Settings, 0), "a lost Hold corridor cannot start a cut");
        f.Pilot.Abort("hold corridor fixture cleanup");
        f.AssertRestored("hold corridor release");
    }

    private static void SpinRefusal()
    {
        var f = new Fixture();
        f.Target.BodyRates = new(0, 0, .2);
        Check(!f.Pilot.BeginAlign(f.Owner, f.Settings) && !f.Pilot.Active,
            "a fast spinning target refuses approach before flight ownership is taken");
        f.AssertRestored("spin refusal");
    }

    private static void InvalidFlightReleases(string reason)
    {
        var f = new Fixture();
        f.Engage();
        f.Step();
        double dt = .02;
        switch (reason)
        {
            case "target": f.Owner.Target = new Vehicle { Parent = f.Owner.Parent }; break;
            case "topology": f.Target.Parts.Parts.Add(new()); break;
            case "center of mass": f.Target.CenterOfMassAsmb = new(.1, 0, 0); break;
            case "spin": f.Target.BodyRates = new(0, 0, .2); break;
            case "MMU": f.Owner.LocomotionState.Mode = LocomotionMode.Other; break;
            case "propellant": foreach (var thruster in f.Owner.Parts.States.ModulesAndStates) thruster.State.IsPropellantAvailable = false; break;
            case "step": dt = .2; break;
            case "range": f.Owner.Position = new(0, 600, 0); break;
        }
        Check(f.Step(dt) == AutopilotResult.Aborted && !f.Pilot.Active,
            reason + " change aborts the production controller");
        f.AssertRestored(reason + " abort");
    }

    private static void CutStateMachine()
    {
        var f = new Fixture();
        f.Engage();
        Check(f.Pilot.TryGetPreview(out _, out CutRing ring), "engaged controller retains its preview");
        f.Owner.Position = ring.Center + ring.Radial * (ring.Radius + f.Settings.AutopilotClearance);
        f.Owner.Velocity = double3.Zero;
        f.Step();
        Check(f.Pilot.BeginCut(f.Settings, 0) && f.Pilot.WantsIgnited && !f.Pilot.AllowsCutting,
            "a held ring enters ignition with cutting inhibited");
        f.Step(blade: .7f);
        Check(!f.Pilot.AllowsCutting && f.Pilot.Progress == 0,
            "partial blade deployment does not advance the orbit");
        f.Step(blade: 1, pose: false);
        Check(!f.Pilot.AllowsCutting && f.Pilot.Progress == 0,
            "a missing fresh pose does not advance the orbit");
        f.Step(blade: 1);
        f.Step(blade: 1);
        Check(f.Pilot.AllowsCutting && f.Pilot.Progress > 0,
            "full deployment and a fresh pose enable orbit progress and cutting");
        Check(f.Step(blade: 1, cuts: 1) == AutopilotResult.Completed,
            "a structural separation completes the cut and releases control");
        f.AssertRestored("structural cut completion");
    }

    private static void ApproachWithAutomaticCutSpeed()
    {
        var f = new Fixture(new(40, 60, 80), mass: 150, pulseTime: .001);
        f.Settings.AutopilotOrbitSpeed = .4f;
        f.Owner.Position = new(0, 2.2, 0);
        Check(f.Pilot.BeginAlign(f.Owner, f.Settings), "one approach action finds and engages a ring with stock-duration MMU pulses");
        Check(f.Pilot.TryGetPreview(out _, out var ring), "automatic approach exposes the chosen ring without a separate capture step");
        f.Owner.Position = ring.Center + ring.Radial * (ring.Radius + f.Settings.AutopilotClearance);
        f.Step();
        Check(f.Pilot.ReadyToCut && !f.Pilot.WantsIgnited && !f.Pilot.AllowsCutting,
            "arrival enables Cut ring without automatically igniting or cutting");
        Check(f.Pilot.BeginCut(f.Settings, 0) && f.Pilot.EffectiveOrbitSpeed > .001
            && f.Pilot.EffectiveOrbitSpeed < .04 && f.Settings.AutopilotOrbitSpeed == .4f,
            "Cut ring selects a feasible speed while preserving the requested ceiling");
        f.Step(blade: 1);
        f.Step(blade: 1);
        double speed = f.Pilot.EffectiveOrbitSpeed;
        double expected = speed * .02 / (ring.Radius + f.Settings.AutopilotClearance) / CylinderOrbitGuidance.FullTurn;
        Check(f.Pilot.AllowsCutting && Math.Abs(f.Pilot.Progress - expected) < 1e-7,
            "orbit progress uses the feasible speed, not the requested ceiling");
        foreach (var thruster in f.Owner.Parts.States.ModulesAndStates) thruster.State.IntendedForce *= .25;
        f.Step(blade: 1);
        Check(f.Pilot.Active && f.Pilot.EffectiveOrbitSpeed < speed,
            "cutting speed adapts when available thrust decreases");
        Check(f.Pilot.GetDiagnostics(f.Owner, f.Settings).Contains("MMU pulse acceleration"),
            "copyable autopilot diagnostics expose authority and planned cut speed");
        f.Pilot.Abort("automatic-speed cleanup");
        f.AssertRestored("automatic-speed release");
    }

    private static void NativePulseApproach(double distance, double3 axisForces, double mass = 10, double pulseTime = .03)
    {
        var f = new Fixture(axisForces, mass, pulseTime);
        f.Owner.Position = new(0, distance, 0);
        f.Engage();
        Check(f.Pilot.TryGetPreview(out _, out CutRing ring), "pulse approach has a selected entry");
        double3 entry = ring.Center + ring.Radial * (ring.Radius + f.Settings.AutopilotClearance);
        const double dt = .01;
        double nextPulse = .073;
        double maxOvershoot = 0, maxHoldError = 0, maxHoldSpeed = 0;
        double arrival = double.PositiveInfinity;
        double3 initialRadial = double3.NormalizeOrZero(f.Owner.Position - entry);
        for (int step = 0; step < 24_000; step++)
        {
            double time = step * dt;
            AutopilotResult result = f.Step(dt);
            if (f.Pilot.Status.StartsWith("Approach corridor lost", StringComparison.Ordinal))
                throw new InvalidOperationException($"Corridor lost at {time:F2}s: position {f.Owner.Position}, velocity {f.Owner.Velocity}, entry {entry}, "
                    + $"lookahead {EvaApproachGuidance.StoppingDistance(f.Owner.Velocity.Length(), Math.Min(axisForces.X, Math.Min(axisForces.Y, axisForces.Z)) * .01):F3}");
            if (result != AutopilotResult.Running || !f.Pilot.Active)
                throw new InvalidOperationException("Controller pulse approach unexpectedly released: " + f.Pilot.Status);
            if (time + 1e-9 >= nextPulse)
            {
                // Native impulses use full available minimum-pulse thrust. The
                // controller schedules with half the weakest axis's authority.
                ThrusterMapFlags flags = f.Owner.ManualInputs.ThrusterCommandFlags;
                double3 impulse = double3.Zero;
                foreach (var thruster in f.Owner.Parts.States.ModulesAndStates)
                    if ((thruster.State.ControlMap & flags) != 0 && thruster.State.IsPropellantAvailable)
                        impulse += thruster.State.IntendedForce * thruster.Module.MinimumPulseTime;
                f.Owner.Velocity += impulse / f.Owner.TotalMass;
                nextPulse += .15;
            }
            f.Owner.Position += f.Owner.Velocity * dt;
            double error = (f.Owner.Position - entry).Length();
            maxOvershoot = Math.Max(maxOvershoot, -double3.Dot(f.Owner.Position - entry, initialRadial));
            if (error < .15 && f.Owner.Velocity.Length() < .1 && double.IsPositiveInfinity(arrival)) arrival = time;
            if (time > 220)
            {
                maxHoldError = Math.Max(maxHoldError, error);
                maxHoldSpeed = Math.Max(maxHoldSpeed, f.Owner.Velocity.Length());
            }
        }
        string label = $"{distance:F1} m production approach with axis forces {axisForces}, {pulseTime * 1000:F0} ms pulses";
        Check(arrival < 180 && maxOvershoot < .25, label + " arrives without gross overshoot");
        Check(maxHoldError < .15 && maxHoldSpeed < .1,
            $"{label} holds after full native pulses ({maxHoldError:F3} m, {maxHoldSpeed:F3} m/s)");
        Check(f.Pilot.ReadyToCut, label + " automatically enables Cut ring after arrival");
        f.Pilot.Abort("pulse fixture cleanup");
        f.AssertRestored("pulse approach release");
    }

    private static void NativePulseCutOrbit()
    {
        var f = new Fixture(new(40, 60, 80), mass: 150, pulseTime: .001);
        f.Owner.Position = new(0, 1.55, 0);
        f.Settings.AutopilotOrbitSpeed = .4f;
        f.Engage();
        f.Step();
        Check(f.Pilot.ReadyToCut && f.Pilot.BeginCut(f.Settings, 0), "stock-pulse orbit starts from the automatic hold state");
        f.Pilot.TryGetPreview(out _, out var ring);
        const double dt = .01;
        double nextPulse = .073, maximumError = 0, minimumClearance = double.PositiveInfinity;
        bool completed = false;
        for (int step = 0; step < 90_000; step++)
        {
            AutopilotResult result = f.Step(dt, blade: 1);
            if (result == AutopilotResult.Completed)
            {
                completed = f.Pilot.Status.StartsWith("Cut orbit complete", StringComparison.Ordinal);
                break;
            }
            if (result != AutopilotResult.Running || f.Pilot.Status.StartsWith("Approach corridor lost", StringComparison.Ordinal))
                throw new InvalidOperationException("Stock-pulse cut orbit lost guidance: " + f.Pilot.Status);
            if (step * dt + 1e-9 >= nextPulse)
            {
                ThrusterMapFlags flags = f.Owner.ManualInputs.ThrusterCommandFlags;
                foreach (var thruster in f.Owner.Parts.States.ModulesAndStates)
                    if ((thruster.State.ControlMap & flags) != 0 && thruster.State.IsPropellantAvailable)
                        f.Owner.Velocity += thruster.State.IntendedForce * thruster.Module.MinimumPulseTime / f.Owner.TotalMass;
                nextPulse += .15;
            }
            f.Owner.Position += f.Owner.Velocity * dt;
            double3 expected = ring.Center + CylinderOrbitGuidance.Point(ring.Axis, 0, ring.Radial,
                ring.Radius + f.Settings.AutopilotClearance, f.Pilot.Progress * CylinderOrbitGuidance.FullTurn);
            maximumError = Math.Max(maximumError, (f.Owner.Position - expected).Length());
            minimumClearance = Math.Min(minimumClearance, (f.Owner.Position - ring.Center).Length() - ring.Radius);
        }
        Check(completed && maximumError < .25 && minimumClearance > .25,
            $"stock-duration pulses complete a full cut lap ({maximumError:F3} m max tracking error, {minimumClearance:F3} m hull clearance)");
        f.AssertRestored("full cut orbit completion");
    }

    private sealed class Fixture
    {
        public readonly KittenEva Owner = new();
        public readonly Vehicle Target = new();
        public readonly SaberSettings Settings = new() { AutopilotAutoAxis = false, AutopilotAxis = 0 };
        public readonly EvaCuttingAutopilot Pilot = new();
        public Fixture(double3? axisForces = null, double mass = 10, double pulseTime = .03)
        {
            Program.ControlledVehicle = Owner;
            Owner.Target = Target;
            Owner.TotalMass = mass;
            Target.Parent = Owner.Parent;
            Owner.Position = new(0, 10, 0);
            var hull = new Part();
            hull.Modules.Add(new MeshViewModule(new() { PositionsCompare = [Cylinder()] }));
            Target.Parts.Parts.Add(hull);
            double3 forces = axisForces ?? new(6, 6, 6);
            foreach (var (direction, flag, force) in new[]
            {
                (double3.UnitX, ThrusterMapFlags.TranslateForward, forces.X),
                (-double3.UnitX, ThrusterMapFlags.TranslateBackward, forces.X),
                (double3.UnitY, ThrusterMapFlags.TranslateRight, forces.Y),
                (-double3.UnitY, ThrusterMapFlags.TranslateLeft, forces.Y),
                (double3.UnitZ, ThrusterMapFlags.TranslateDown, forces.Z),
                (-double3.UnitZ, ThrusterMapFlags.TranslateUp, forces.Z),
            })
                Owner.Parts.States.ModulesAndStates.Add(new(new() { MinimumPulseTime = pulseTime },
                    new() { IntendedForce = direction * force, ControlMap = flag }));
        }
        public void Engage() => Check(Pilot.BeginAlign(Owner, Settings), "fixture engages: " + Pilot.Status);
        public AutopilotResult Step(double dt = .02, float blade = 0, int cuts = 0, bool pose = true) =>
            Pilot.Update(Settings, dt, blade, cuts, pose, double3.UnitY);
        public void AssertRestored(string context)
        {
            FlightComputer fc = Owner.FlightComputer;
            Check(Owner.ManualInputs.ThrusterCommandFlags == ThrusterMapFlags.None
                && Owner.ControlMode == KittenControlMode.View && fc.AttitudeMode == FlightComputerAttitudeMode.Manual
                && fc.AttitudeFrame == VehicleReferenceFrame.Other && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.Other
                && (fc.CustomAttitudeTarget - new double3(.3, -.4, .5)).Length() < 1e-10
                && fc.ManualThrustMode == FlightComputerManualThrustMode.Direct
                && fc.RollMode == FlightComputerRollMode.Coupled && fc.RCSMode == FlightComputerRCSMode.Disabled
                && fc.RateLimit == .37f, context + " restores all borrowed flight settings and clears translation");
        }
    }

    private static double3[] Cylinder()
    {
        var vertices = new List<double3>();
        const int segments = 48;
        for (int i = 0; i < segments; i++)
        {
            double a = i * 2 * Math.PI / segments, b = (i + 1) * 2 * Math.PI / segments;
            double3 p = new(-2, Math.Cos(a), Math.Sin(a)), q = new(-2, Math.Cos(b), Math.Sin(b));
            double3 r = new(2, Math.Cos(a), Math.Sin(a)), s = new(2, Math.Cos(b), Math.Sin(b));
            vertices.AddRange([p, q, r, q, s, r, new(-2, 0, 0), q, p, new(2, 0, 0), r, s]);
        }
        return vertices.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Controller regression failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
