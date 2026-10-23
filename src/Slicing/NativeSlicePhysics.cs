using System.Reflection;
using Brutal.Numerics;
using KSA;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

internal readonly record struct PreparedPhysicsFragment(PreparedFragment Asset, PrincipalFragment Frame, bool PositiveSide);

/// <summary>
/// Commits already durable/bound fragment assets at the joined simulation handoff.
/// Source geometry uses the original vehicle's physical assembly metre frame.
/// </summary>
internal static class NativeSlicePhysics
{
    private const int MaximumPieces = 8;
    private static readonly Action<Vehicle, double3>? SetBodyRates = typeof(Vehicle)
        .GetProperty(nameof(Vehicle.BodyRates), BindingFlags.Instance | BindingFlags.Public)?
        .GetSetMethod(true)?.CreateDelegate<Action<Vehicle, double3>>();

    internal static bool ValidateSource(Part struck, out string reason)
    {
        var full = struck.StructuralPart;
        var owner = full.Tree?.OwningVehicle;
        reason = "The source part is unavailable or outside native physics.";
        if (owner is null || owner.IsDisposed || owner is KittenEva || !owner.HasPhysicsBubble || Program.EditorFlag) return false;
        if (SetBodyRates is null) { reason = "The native angular-rate synchronization seam is unavailable."; return false; }
        foreach (var seat in owner.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero) { reason = "Crewed vessels cannot be sliced."; return false; }
        var sources = SourceParts(full);
        float mass = SourceMass(full);
        if (!float.IsFinite(mass) || mass <= .001f) { reason = "The part has no finite positive inert mass."; return false; }
        if (!CanBake(full, out reason)) return false;
        foreach (var sub in full.SubParts) if (!CanBake(sub, out reason)) return false;
        if (owner.Parts.Count > 4096) { reason = "Vehicle exceeds the native-isolation budget (4096 parts)."; return false; }
        if (!ValidateTree(owner, out reason)) return false;
        int edges = sources.Sum(p => p.TreeChildren.Count(c => !c.IsAttachedInternal)) + (full.TreeParent is null ? 0 : 1);
        if (owner.BubbleVehicleCount + edges + MaximumPieces > PartFailure.MaximumVehiclesPerBubble)
        { reason = "Insufficient native physics slots for safe isolation and slice fragments."; return false; }
        reason = "";
        return true;
    }

    private static bool CanBake(Part part, out string reason)
    {
        foreach (var module in part.Modules.GetUsing<ModuleBase>())
        {
            if (module is FxDeformation deformation)
            {
                if (part.Tree is { } tree && FxDeformation.TryGetFrom(tree.States, out var states) && states.GetState(deformation).Count > 0)
                { reason = "Dented geometry cannot be sliced until the actual deformed surface can be baked."; return false; }
                continue;
            }
            // Modules are intentionally destroyed with the original part. The
            // replacement is wreckage, so tanks, controls, batteries, engines and
            // empty seats do not need functional equivalents in the fragments.
            if (module is Parachute chute && chute.State is ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing or ChuteState.Full)
            { reason = "Retract or cut away the deployed parachute before slicing its housing."; return false; }
        }
        reason = "";
        return true;
    }

    private static bool ValidateTree(Vehicle owner, out string reason)
    {
        var edges = new HashSet<Part.Connection>();
        var pairs = new HashSet<(uint, uint)>();
        foreach (var full in owner.Parts.Parts)
        {
            if (!Check(full, out reason)) return false;
            foreach (var sub in full.SubParts) if (!Check(sub, out reason)) return false;
        }
        // Every tree boundary must have an actual unique native connection, not just a parent pointer.
        foreach (var part in owner.Parts.Parts)
            if (!part.IsAttachedInternal && part.TreeParent is { } parent && !pairs.Contains(Pair(part, parent)))
            { reason = "A native tree boundary has no unique separable connection."; return false; }
        reason = "";
        return true;

        bool Check(Part part, out string error)
        {
            foreach (var edge in part.Connections)
            {
                if (!edges.Add(edge)) continue;
                var a = edge.Connectors[0].ConnectionPart.FullPart;
                var b = edge.Connectors[1].ConnectionPart.FullPart;
                if (a == b) continue;
                if (edge.Connectors[0].ConnectionPart != a || edge.Connectors[1].ConnectionPart != b
                    || a.Tree != owner.Parts || b.Tree != owner.Parts || !(a.TreeParent == b || b.TreeParent == a)
                    || !pairs.Add(Pair(a, b)))
                { error = "Reinforced or non-tree connections prevent safe native isolation."; return false; }
            }
            error = "";
            return true;
        }
    }

    private static (uint, uint) Pair(Part a, Part b) => a.InstanceId < b.InstanceId ? (a.InstanceId, b.InstanceId) : (b.InstanceId, a.InstanceId);

    internal static void Preflight(PreparedFragment prepared)
    {
        // Called in a preparation frame, never inside the atomic breakup transaction.
        var part = new Part("Purr2D2 slice preflight", prepared.Template);
        try
        {
            if (!(part.ComputeSubtreeInertMass() > 0))
                throw new InvalidOperationException("Prepared fragment has no native mass.");
            if (part.Modules.Get<ColliderModule>().IsEmpty)
                throw new InvalidOperationException("Prepared fragment has no native collider module.");
            foreach (var collider in part.Modules.Get<ColliderModule>())
                if (!collider.Handle.Exists) throw new InvalidOperationException("Prepared fragment collider could not be cooked.");
        }
        finally { part.Dispose(); }
    }

    internal static bool TryReplace(Part struck, IReadOnlyList<PreparedPhysicsFragment> pieces, double3 cutNormal,
        out IReadOnlyList<Vehicle> created, out string reason)
    {
        created = Array.Empty<Vehicle>();
        if (!ValidateSource(struck, out reason)) return false;
        if (pieces.Count is < 2 or > MaximumPieces) { reason = "A slice must produce two to eight bounded fragments."; return false; }
        var full = struck.StructuralPart;
        var sources = SourceParts(full);
        var owner = full.Tree?.OwningVehicle;
        if (owner is null) { reason = "The source tree changed before commit."; return false; }
        double massSum = 0;
        foreach (var p in pieces)
        {
            if (!p.Asset.NativeReady || p.Asset.Template is null || !float.IsFinite(p.Asset.MassKg) || p.Asset.MassKg <= .0001f
                || !(p.Asset.Extents.X > 0 && p.Asset.Extents.Y > 0 && p.Asset.Extents.Z > 0))
            { reason = "A prepared slice asset has invalid mass or dimensions."; return false; }
            // Durability boundary: native save instances must resolve the exact registered immutable template.
            if (!ReferenceEquals(ModLibrary.Get<PartTemplate>(p.Asset.Template.Id), p.Asset.Template))
            { reason = "A prepared fragment is not registered for native save restoration."; return false; }
            massSum += p.Asset.MassKg;
        }
        double sourceMass = SourceMass(full);
        if (Math.Abs(massSum - sourceMass) > Math.Max(.001, sourceMass * 2e-5))
        { reason = "Prepared slice masses do not conserve the source inert mass."; return false; }
        double4x4 originalPartToAssembly = full.MatrixAsmb2VehicleAsmb;
        var sourceSystem = owner.System;
        var spawned = new List<Vehicle>(pieces.Count);
        var previouslyPresent = new HashSet<Vehicle>();
        var generatedTemplates = pieces.Select(p => p.Asset.Template).ToHashSet();
        double emptiedMass = 0;
        foreach (var item in sourceSystem.All.AsSpan()) if (item is Vehicle vehicle) previouslyPresent.Add(vehicle);
        try
        {
            // Assets and geometry are validated before the first native structural mutation.
            // If a later spawn fails, the isolated source remains intact rather than being deleted.
            foreach (var source in sources)
                foreach (var child in source.TreeChildren.Where(c => !c.IsAttachedInternal).ToArray())
                    if (!SplitBoundary(source, child)) { reason = "A child connection refused native isolation; the source remains intact."; return false; }
            if (full.TreeParent is { } parent && !SplitBoundary(full, parent))
            { reason = "The parent connection refused native isolation; the source remains intact."; return false; }
            owner = full.Tree?.OwningVehicle;
            if (owner is null || owner.IsDisposed || owner.Parts.Count != sources.Length || sources.Any(p => p.Tree?.OwningVehicle != owner))
            { reason = "Native isolation did not yield the intact source shell and its interior."; return false; }
            if (!double4x4.Invert(originalPartToAssembly, out var oldAssemblyToPart))
            { reason = "Source transformation is singular."; return false; }
            double4x4 sourceToIsolated = oldAssemblyToPart * full.MatrixAsmb2VehicleAsmb;
            if (!double4x4.Decompose(sourceToIsolated, out var scale, out var sourceToIsolatedRotation, out _)
                || (scale - double3.One).Length() > 1e-5)
            { reason = "Native isolation unexpectedly changed physical source scale."; return false; }
            // Only the isolated cut part is drained. Neighboring tanks and their
            // resource networks have already been separated by native Split.
            // Preserve the assembly's world pose when removing off-centre fuel.
            var oldCom = owner.CenterOfMassAsmb;
            emptiedMass = owner.TotalMass;
            owner.DepleteConsumables();
            owner.UpdateVehicleConfiguration();
            var drainedState = owner.GetPhysicsStatesMutable();
            var newCom = drainedState.Props.CenterOfMassAsmb;
            var shift = (new double3(newCom.X, newCom.Y, newCom.Z) - oldCom).Transform(drainedState.Kinematic.Body2Phys);
            drainedState.Kinematic.PositionPhys += shift;
            drainedState.Kinematic.VelocityPhys += double3.Cross(drainedState.Kinematic.AngularVelocityPhys, shift);
            drainedState.Props.SetOnRails(false);
            owner.SetFlightPlan(new FlightPlan(drainedState.ComputeOrbit(owner.OrbitColor), owner.Hash));
            owner.UpdatePerFrameData();
            emptiedMass -= owner.TotalMass;
            var beforeStates = owner.GetPhysicsStates();
            beforeStates.GetStatesCci(out var beforePosition, out var beforeVelocity, out var beforeBody2Cci);
            double3 beforeOmega = beforeStates.GetBodyRates().Transform(beforeBody2Cci);
            var nativeInertia = owner.TotalMassPropsBody.Inertia;
            var sourceRotation = double4x4.CreateFromQuaternion(beforeBody2Cci);
            var sourceInertia = double4x4.Transpose(sourceRotation)
                * SlicePrincipalFrame.Tensor(nativeInertia.XX, nativeInertia.YY, nativeInertia.ZZ,
                    nativeInertia.YX, nativeInertia.ZX, nativeInertia.ZY) * sourceRotation;
            var sourceAngularMomentum = SlicePrincipalFrame.ApplyTensor(beforeOmega, sourceInertia);

            foreach (var piece in pieces)
            {
                var centroid = new double3(piece.Frame.Centroid.X, piece.Frame.Centroid.Y, piece.Frame.Centroid.Z);
                var descriptor = new Part("Purr2D2 fragment spawn descriptor", piece.Asset.Template)
                {
                    PositionParentAsmb = double3.Transform(centroid, sourceToIsolated),
                    Asmb2ParentAsmb = doubleQuat.Concatenate(piece.Frame.FragmentToSource, sourceToIsolatedRotation),
                };
                try
                {
                    descriptor.ResetCachedPosMatrixValues();
                    var fragment = owner.SpawnSubPartDebris(descriptor, piece.Asset.Extents, piece.Asset.MassKg, 0);
                    if (fragment is null) throw new InvalidOperationException("Native fragment creation refused a prepared piece.");
                    spawned.Add(fragment);
                    // SpawnSubPartDebris installs a temporary box inertia; restore the durable exact native template mass.
                    if (!fragment.Parts.Root.RecomputeInertMass()) throw new InvalidOperationException("Fragment template has no persistent inertia.");
                    fragment.UpdateVehicleConfiguration();
                }
                finally { descriptor.Dispose(); }
            }
            double actualMass = spawned.Sum(v => (double)v.TotalMass);
            if (Math.Abs(actualMass - owner.TotalMass) > Math.Max(.001, owner.TotalMass * 2e-5))
                throw new InvalidOperationException("Native fragment mass changed during creation.");
            double3 groupCentre = double3.Zero;
            foreach (var fragment in spawned)
            {
                fragment.GetPhysicsStates().GetStatesCci(out var position, out _, out _);
                groupCentre += position * fragment.TotalMass;
            }
            groupCentre /= actualMass;
            double4x4 groupInertia = default;
            foreach (var fragment in spawned)
            {
                fragment.GetPhysicsStates().GetStatesCci(out var position, out _, out var body2Cci);
                var inertia = fragment.TotalMassPropsBody.Inertia;
                var rotation = double4x4.CreateFromQuaternion(body2Cci);
                groupInertia += double4x4.Transpose(rotation)
                    * SlicePrincipalFrame.Tensor(inertia.XX, inertia.YY, inertia.ZZ, inertia.YX, inertia.ZX, inertia.ZY) * rotation
                    + SlicePrincipalFrame.ParallelAxis(fragment.TotalMass, position - groupCentre);
            }
            // A uniform-density cut can change mass COM without moving the visible geometry.
            // Preserve angular momentum about the old COM, including the new group's orbital term.
            double3 totalMomentum = beforeVelocity * owner.TotalMass;
            double3 spinMomentum = sourceAngularMomentum
                - double3.Cross(groupCentre - beforePosition, totalMomentum);
            // A gentle opening impulse makes the hot cut visible immediately.
            // Remove its net linear and angular momentum before assigning spin.
            var normalCci = cutNormal.Transform(sourceToIsolatedRotation).Transform(beforeBody2Cci).NormalizeOrZero();
            double meanSign = pieces.Select((p, i) => (p.PositiveSide ? 1 : -1) * spawned[i].TotalMass).Sum() / actualMass;
            var separation = pieces.Select(p => normalCci * ((p.PositiveSide ? 1 : -1) - meanSign) * .12).ToArray();
            for (int i = 0; i < spawned.Count; i++)
            {
                spawned[i].GetPhysicsStates().GetStatesCci(out var position, out _, out _);
                spinMomentum -= double3.Cross(position - groupCentre, separation[i] * spawned[i].TotalMass);
            }
            if (!SlicePrincipalFrame.TryAngularVelocity(groupInertia, spinMomentum, out var omega)
                || omega.Length() > 300000) throw new InvalidOperationException("Conserved fragment angular velocity is invalid.");
            for (int i = 0; i < spawned.Count; i++)
            {
                var fragment = spawned[i];
                fragment.GetPhysicsStates().GetStatesCci(out var position, out _, out var body2Cci);
                double3 velocityCci = totalMomentum / actualMass + double3.Cross(omega, position - groupCentre) + separation[i];
                var state = fragment.GetPhysicsStatesMutable();
                if (state.Origin.BubFrame == BubbleFrame.Ccf)
                {
                    var cci2Ccf = VehicleReferenceFrameEx.CciStatesToCcf(state.Origin.Parent, state.Origin.Time,
                        position, velocityCci, out _, out var velocityCcf);
                    state.Kinematic.VelocityPhys = velocityCcf - state.Origin.VelocityBub;
                    state.Kinematic.AngularVelocityPhys = (omega - state.Origin.Parent.GetAngularVelocityCci()).Transform(cci2Ccf);
                }
                else
                {
                    state.Kinematic.VelocityPhys = velocityCci - state.Origin.VelocityBub;
                    state.Kinematic.AngularVelocityPhys = omega;
                }
                state.Props.SetOnRails(false);
                SetBodyRates!(fragment, omega.Transform(doubleQuat.Inverse(body2Cci)));
                fragment.SetFlightPlan(new FlightPlan(state.ComputeOrbit(fragment.OrbitColor), fragment.Hash));
                fragment.UpdatePerFrameData();
            }
            // This is the only deletion. Both complete fragments exist with native collision and durable IDs.
            Universe.DestroyVehicle(owner);
            created = spawned.ToArray();
            reason = $"Sliced into {spawned.Count} separating fragments."
                + (emptiedMass > .001 ? $" Released {emptiedMass:F1} kg of stored propellant." : "");
            return true;
        }
        catch (Exception error)
        {
            // Native disposal sets IsDisposed before tearing resources down. Once that
            // irreversible deletion starts, never delete the successfully created replacements.
            if (owner is { IsDisposed: true } && spawned.Count == pieces.Count)
            {
                created = spawned.ToArray();
                reason = $"Sliced into {spawned.Count} fragments; native source teardown reported: {error.Message}";
                Console.WriteLine($"[purr2d2] {reason}");
                return true;
            }
            // Native creation registers before its final kinematic setup. If the native
            // method itself threw before returning, recover any newly registered generated
            // vehicles too; detached neighboring branches have different templates.
            foreach (var item in sourceSystem.All.AsSpan())
                if (item is Vehicle vehicle && !previouslyPresent.Contains(vehicle) && !spawned.Contains(vehicle)
                    && vehicle.Parts?.Root is { } root && generatedTemplates.Contains(root.Template)) spawned.Add(vehicle);
            foreach (var fragment in spawned)
                try { if (!fragment.IsDisposed) Universe.DestroyVehicle(fragment); }
                catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Fragment rollback cleanup: {cleanup.Message}"); }
            reason = $"Slice commit refused; original source retained{(emptiedMass > .001 ? " with tanks emptied" : "")}: {error.Message}";
            return false;
        }
    }

    private static bool SplitBoundary(Part full, Part neighbor)
    {
        Part.Connection? crossing = null;
        foreach (var edge in full.Connections)
            if (edge.OtherPart(full).FullPart == neighbor.FullPart) { crossing = edge; break; }
        if (crossing is null || full.Tree?.OwningVehicle is not { } owner) return false;
        var endpoint = crossing.Connectors[0].ConnectionPart.FullPart == full ? crossing.Connectors[0] : crossing.Connectors[1];
        return owner.Split(crossing, endpoint, 0, out _) is not null;
    }
    // A capsule and its attached IVA belong to one structural shell. Native
    // internal attachments have no separable connector and must travel with it.
    internal static Part[] SourceParts(Part struck)
    {
        var parts = new List<Part>();
        void Add(Part part)
        {
            parts.Add(part);
            foreach (var child in part.TreeChildren) if (child.IsAttachedInternal) Add(child);
        }
        Add(struck.StructuralPart);
        return parts.ToArray();
    }

    internal static float SourceMass(Part struck) => SourceParts(struck).Sum(p => p.ComputeSubtreeInertMass());
}
