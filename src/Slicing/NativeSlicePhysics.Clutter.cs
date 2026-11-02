using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;
using Purr2D2.Force;

namespace Purr2D2.Slicing;

internal static partial class NativeSlicePhysics
{
    /// <summary>All preparation is detached. Native ownership changes only after both fragments are alive.</summary>
    internal static bool TryReplaceClutter(KittenEva owner, ClutterSliceDescription source,
        IReadOnlyList<PreparedPhysicsFragment> pieces, SlicePlane plane,
        out IReadOnlyList<Vehicle> created, out string reason)
    {
        created = []; reason = "The ground-object replacement is unavailable.";
        if (SetBodyRates == null || AdoptSeparated == null || WorkerState == null || !source.Target.IsLocalTo(owner)
            || !ClutterForceNative.TrySliceFrame(source, out var frame) || pieces.Count is < 2 or > MaximumPieces) return false;
        if (owner.BubbleVehicleCount + pieces.Count > VehicleLimit)
        { reason = "Ground cut exceeds the configured local physics-vehicle budget."; return false; }
        double mass = 0;
        foreach (var piece in pieces)
        {
            if (!piece.Native.Ready || !piece.Asset.NativeReady || !float.IsFinite(piece.Asset.MassKg) || piece.Asset.MassKg <= .0001f
                || !ReferenceEquals(piece.Native.Root.Template, piece.Asset.Template)
                || !ReferenceEquals(ModLibrary.Get<PartTemplate>(piece.Asset.TemplateId), piece.Asset.Template)) return false;
            mass += piece.Asset.MassKg;
        }
        if (Math.Abs(mass - source.MassKg) > Math.Max(.001, source.MassKg * 2e-5))
        { reason = "Ground fragments do not conserve source mass."; return false; }
        var spawned = new List<Vehicle>(pieces.Count);
        var system = owner.System;
        var present = new HashSet<Vehicle>();
        foreach (var item in system.All.AsSpan()) if (item is Vehicle vehicle) present.Add(vehicle);
        var templateSet = pieces.Select(p => p.Asset.Template).ToHashSet();
        bool sourceRemoved = false;
        try
        {
            var bubble = source.Target.Bubble;
            var cci2Cce = owner.Parent.GetCci2Cce();
            var normal = new double3(plane.Normal.X, plane.Normal.Y, plane.Normal.Z).Transform(frame.Object2Cci).NormalizeOrZero();
            double positiveMass = pieces.Where(p => p.PositiveSide).Sum(p => (double)p.Asset.MassKg);
            Span<char> idBuffer = stackalloc char[256];
            foreach (var piece in pieces)
            {
                var centroid = new double3(piece.Frame.Centroid.X, piece.Frame.Centroid.Y, piece.Frame.Centroid.Z);
                var delta = centroid.Transform(frame.Object2Cci);
                var rotation = piece.Frame.FragmentToSource.Concatenate(frame.Object2Cci);
                var fragment = Vehicle.CreateVehicle(system, rotation.Concatenate(cci2Cce), frame.OmegaCci.Transform(rotation.Inverse()),
                    owner.Parent, owner.GenerateSplitId(idBuffer).ToString(), piece.Native.Root, owner.Orbit);
                spawned.Add(fragment);
                piece.Native.TransferredTo(fragment);
                AdoptSeparated(owner, fragment, bubble, double3.Zero);
                var worker = UpdateState(fragment)!;
                QuietSliceContacts.Protect(worker, UpdateState(owner)!);
                foreach (var sibling in spawned)
                    if (!ReferenceEquals(sibling, fragment)) QuietSliceContacts.Protect(worker, UpdateState(sibling)!);
                // Equal-and-opposite gentle separation conserves the native object's
                // linear momentum. There is no explosive per-piece kick.
                double sideSpeed = .12 * ((piece.PositiveSide ? 1 : 0) - positiveMass / mass);
                var velocity = frame.VelocityCci + double3.Cross(frame.OmegaCci, delta) + normal * sideSpeed;
                var position = frame.PositionCci + delta;
                var state = fragment.GetPhysicsStatesMutable();
                var cci2Ccf = VehicleReferenceFrameEx.CciStatesToCcf(owner.Parent, state.Origin.Time,
                    position, velocity, out var positionCcf, out var velocityCcf);
                if (state.Origin.BubFrame != BubbleFrame.Ccf)
                    throw new InvalidOperationException("Ground fragment left its native surface bubble.");
                state.Kinematic.PositionPhys = positionCcf - state.Origin.PositionBub;
                state.Kinematic.VelocityPhys = velocityCcf - state.Origin.VelocityBub;
                state.Kinematic.Body2Phys = rotation.Concatenate(cci2Ccf);
                state.Kinematic.AngularVelocityPhys = (frame.OmegaCci - owner.Parent.GetAngularVelocityCci()).Transform(cci2Ccf);
                state.Props.SetOnRails(false);
                SetBodyRates(fragment, frame.OmegaCci.Transform(rotation.Inverse()));
                fragment.MarkAsDebris();
                fragment.SetFlightPlan(new FlightPlan(state.ComputeOrbit(fragment.OrbitColor), fragment.Hash));
                fragment.UpdatePerFrameData();
            }
            if (spawned.Count != pieces.Count || spawned.Any(v => v.IsDisposed || !v.HasPhysicsBubble))
                throw new InvalidOperationException("A replacement fragment failed native preflight.");
            QuietSliceQuiescence.WatchFirstStep(spawned.Select(v => UpdateState(v)!).ToArray());
            // No original collider, placement or render bit was changed before this point.
            if (!ClutterForceNative.RemoveSlicedOriginal(source, out reason))
                throw new InvalidOperationException(reason);
            sourceRemoved = true;
            created = spawned.ToArray(); return true;
        }
        catch (Exception error)
        {
            if (sourceRemoved)
            { created = spawned.ToArray(); reason = "Ground replacement completed; finalization reported: " + error.Message; return true; }
            foreach (var item in system.All.AsSpan())
                if (item is Vehicle vehicle && !present.Contains(vehicle) && !spawned.Contains(vehicle)
                    && vehicle.Parts?.Root is { } root && templateSet.Contains(root.Template)) spawned.Add(vehicle);
            foreach (var fragment in spawned)
                try { RollBackFragment(fragment, system); }
                catch (Exception cleanup) { Console.WriteLine("[purr2d2] Ground fragment rollback: " + cleanup.Message); }
            reason = "Original ground object retained: " + error.Message; return false;
        }
    }
}
