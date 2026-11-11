using System.Reflection;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

internal readonly record struct PreparedPhysicsFragment(PreparedFragment Asset, PrincipalFragment Frame, bool PositiveSide,
    DetachedSliceFragment Native);

/// <summary>
/// Commits already durable/bound fragment assets at the joined simulation handoff.
/// Source geometry uses the original vehicle's physical assembly metre frame.
/// </summary>
internal static partial class NativeSlicePhysics
{
    private const int MaximumPieces = 8;
    internal static int VehicleLimit { get; private set; } = SliceResourceLimits.DefaultPhysicsVehicleLimit;
    internal static void ConfigureVehicleLimit(int limit) => VehicleLimit = Math.Clamp(limit,
        SliceResourceLimits.MinimumPhysicsVehicleLimit, SliceResourceLimits.MaximumPhysicsVehicleLimit);
    private static readonly Action<Vehicle, double3>? SetBodyRates = typeof(Vehicle)
        .GetProperty(nameof(Vehicle.BodyRates), BindingFlags.Instance | BindingFlags.Public)?
        .GetSetMethod(true)?.CreateDelegate<Action<Vehicle, double3>>();
    // Narrow adapters for the same native adoption path used by SpawnSubPartDebris.
    // No global patches or alternate physics implementation are involved.
    private static readonly FieldInfo? WorkerState = typeof(Vehicle)
        .GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Action<Vehicle, Vehicle, PhysicsBubble, double3>? AdoptSeparated = typeof(Vehicle)
        .GetMethod("AdoptSeparatedVehicle", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Vehicle), typeof(PhysicsBubble), typeof(double3)])?
        .CreateDelegate<Action<Vehicle, Vehicle, PhysicsBubble, double3>>();

    internal static bool ValidateSource(Part struck, out string reason)
    {
        var full = struck.StructuralPart;
        var owner = full.Tree?.OwningVehicle;
        reason = "The source part is unavailable or outside native physics.";
        if (owner is null || owner.IsDisposed || full.Tree!.IsDisposed || owner.Parts != full.Tree
            || !owner.Parts.Contains(full) || !ReferenceEquals(owner.System, Universe.CurrentSystem)
            || owner is KittenEva || !owner.HasPhysicsBubble || Program.EditorFlag) return false;
        if (SetBodyRates is null) { reason = "The native angular-rate synchronization seam is unavailable."; return false; }
        if (AdoptSeparated is null || WorkerState?.FieldType != typeof(VehicleUpdateState))
        { reason = "The native prepared-fragment adoption seam is unavailable."; return false; }
        var sources = SourceParts(full);
        float mass = SourceMass(full);
        if (!float.IsFinite(mass) || mass <= .001f) { reason = "The part has no finite positive inert mass."; return false; }
        if (!CanBake(full, out reason)) return false;
        foreach (var sub in full.SubParts) if (!CanBake(sub, out reason)) return false;
        if (owner.Parts.Count > 4096) { reason = "Vehicle exceeds the native-isolation budget (4096 parts)."; return false; }
        if (!ValidateTree(owner, out reason)) return false;
        int edges = sources.Sum(p => p.TreeChildren.Count(c => !c.IsAttachedInternal)) + (full.TreeParent is null ? 0 : 1);
        // The stock 30-vehicle constant is a debris eviction policy, not a native
        // slot allocation limit: Split/AdoptSeparatedVehicle use dynamic lists.
        // Keep a bounded mod budget and never evict other vessels implicitly.
        if (owner.BubbleVehicleCount + edges + MaximumPieces > VehicleLimit)
        { reason = $"Slice physics budget is full: {owner.BubbleVehicleCount} local vehicles + {edges} isolation split(s) + up to {MaximumPieces} fragments exceed the {VehicleLimit} vehicle limit. Raise the limit or explicitly remove generated debris in Settings."; return false; }
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
            // seats do not need functional equivalents in the fragments.
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

    internal static bool ValidatePreparedFragments(Part struck, IReadOnlyList<PreparedPhysicsFragment> pieces, out string reason)
    {
        if (pieces.Count is < 2 or > MaximumPieces) { reason = "A slice must produce two to eight bounded fragments."; return false; }
        double massSum = 0;
        foreach (var p in pieces)
        {
            if (!p.Asset.NativeReady || !p.Native.Ready || p.Native.Root.Template != p.Asset.Template
                || p.Asset.Template is null || !float.IsFinite(p.Asset.MassKg) || p.Asset.MassKg <= .0001f
                || !(p.Asset.Extents.X > 0 && p.Asset.Extents.Y > 0 && p.Asset.Extents.Z > 0)
                || !double.IsFinite(p.Asset.Extents.X) || !double.IsFinite(p.Asset.Extents.Y) || !double.IsFinite(p.Asset.Extents.Z))
            { reason = "A prepared slice asset has invalid mass or dimensions."; return false; }
            // Durability boundary: native save instances must resolve the exact registered immutable template.
            if (!ReferenceEquals(ModLibrary.Get<PartTemplate>(p.Asset.Template.Id), p.Asset.Template))
            { reason = "A prepared fragment is not registered for native save restoration."; return false; }
            massSum += p.Asset.MassKg;
        }
        double sourceMass = SourceMass(struck);
        if (!double.IsFinite(sourceMass) || sourceMass <= .001
            || Math.Abs(massSum - sourceMass) > Math.Max(.001, sourceMass * 2e-5))
        { reason = "Prepared slice masses do not conserve the source inert mass."; return false; }
        reason = "";
        return true;
    }

    internal static bool TryReplace(IsolatedSliceSource isolated, IReadOnlyList<PreparedPhysicsFragment> pieces, double3 cutNormal,
        out IReadOnlyList<Vehicle> created, out string reason, SliceCommitTimings timings)
    {
        created = Array.Empty<Vehicle>();
        var full = isolated.Source;
        if (!isolated.IsCurrent(out reason) || !ValidateSource(full, out reason)
            || !ValidatePreparedFragments(full, pieces, out reason)) return false;
        var owner = isolated.Owner;
        var sourceToIsolated = isolated.SourceToIsolated;
        var sourceToIsolatedRotation = isolated.SourceToIsolatedRotation;
        var sourceSystem = owner.System;
        var spawned = new List<Vehicle>(pieces.Count);
        var previouslyPresent = new HashSet<Vehicle>();
        var generatedTemplates = pieces.Select(p => p.Asset.Template).ToHashSet();
        var registeringIds = new HashSet<string>(StringComparer.Ordinal);
        double emptiedMass = isolated.EmptiedMassKg;
        foreach (var item in sourceSystem.All.AsSpan()) if (item is Vehicle vehicle) previouslyPresent.Add(vehicle);
        try
        {
            if (!isolated.ContentsReleased) throw new InvalidOperationException("The source was not safely emptied during isolation.");
            var beforeStates = owner.GetPhysicsStates();
            beforeStates.GetStatesCci(out _, out var beforeVelocity, out var beforeBody2Cci);
            double3 beforeOmega = beforeStates.GetBodyRates().Transform(beforeBody2Cci);

            timings.Next(SliceCommitPhase.Registration);
            if ((WorkerState!.GetValue(owner) as VehicleUpdateState)?.Bubble is not { } bubble)
                throw new InvalidOperationException("The isolated source lost its native physics bubble.");
            var ownerStates = owner.GetPhysicsStates();
            Span<char> idBuffer = stackalloc char[256];
            foreach (var piece in pieces)
            {
                var centroid = new double3(piece.Frame.Centroid.X, piece.Frame.Centroid.Y, piece.Frame.Centroid.Z);
                var rotation = doubleQuat.Concatenate(piece.Frame.FragmentToSource, sourceToIsolatedRotation);
                var deltaAsmb = double3.Transform(centroid, sourceToIsolated) - owner.CenterOfMassAsmb;
                var deltaPhys = deltaAsmb.Transform(ownerStates.Kinematic.Body2Phys);
                string id = owner.GenerateSplitId(idBuffer).ToString();
                registeringIds.Add(id);
                // The actual root, modules, exact inertia, collider and tree were built
                // during earlier frames. Native construction now configures them once.
                var fragment = Vehicle.CreateVehicle(owner.System, doubleQuat.Concatenate(rotation, owner.Body2Cce),
                    owner.BodyRates.Transform(doubleQuat.Inverse(rotation)), owner.Parent, id, piece.Native.Root, owner.Orbit);
                spawned.Add(fragment); // Rollback owns registered vehicles before any later operation can throw.
                piece.Native.TransferredTo(fragment);
                AdoptSeparated!(owner, fragment, bubble, deltaPhys.NormalizeOrZero());
                // Native adoption copies relationships but drops SuppressAllContacts.
                // Restore only this transaction's source/neighbor/sibling policy.
                var worker = UpdateState(fragment)!;
                QuietSliceContacts.Protect(worker, UpdateState(owner)!);
                foreach (var neighbor in isolated.Neighbors)
                    if (!neighbor.IsDisposed && UpdateState(neighbor) is { } neighborState)
                        QuietSliceContacts.Protect(worker, neighborState);
                foreach (var sibling in spawned)
                    if (sibling != fragment) QuietSliceContacts.Protect(worker, UpdateState(sibling)!);
                var state = fragment.GetPhysicsStatesMutable();
                state.Kinematic.PositionPhys = ownerStates.Kinematic.PositionPhys + deltaPhys;
                state.Kinematic.VelocityPhys = ownerStates.Kinematic.VelocityPhys
                    + double3.Cross(owner.BodyRates, deltaAsmb).Transform(ownerStates.Kinematic.Body2Phys);
                state.Props.SetOnRails(false);
                fragment.MarkAsDebris();
                // Flight plan/per-frame data are finalized after the conserved group
                // motion below, avoiding two orbit and navigation refreshes per piece.
            }
            timings.Next(SliceCommitPhase.Motion);
            double actualMass = spawned.Sum(v => (double)v.TotalMass);
            if (Math.Abs(actualMass - owner.TotalMass) > Math.Max(.001, owner.TotalMass * 2e-5))
                throw new InvalidOperationException("Native fragment mass changed during creation.");
            var bodies = spawned.Select((v, i) =>
            {
                v.GetPhysicsStates().GetStatesCci(out var position, out _, out _);
                return new QuietFragmentBody(v.TotalMass, position, pieces[i].PositiveSide);
            }).ToArray();
            var normalCci = cutNormal.Transform(sourceToIsolatedRotation).Transform(beforeBody2Cci).NormalizeOrZero();
            var motion = QuietSliceMotion.Assign(bodies, beforeVelocity, beforeOmega, normalCci);
            for (int i = 0; i < spawned.Count; i++)
            {
                var fragment = spawned[i];
                fragment.GetPhysicsStates().GetStatesCci(out var position, out _, out var body2Cci);
                double3 velocityCci = motion[i].Velocity, omega = motion[i].AngularVelocity;
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
            QuietSliceQuiescence.WatchFirstStep(spawned.Select(v => UpdateState(v)!).ToArray());
            timings.Next(SliceCommitPhase.SourceRemoval);
            // Native destruction clears the isolated shell's crew roster state. Crew
            // in neighboring branches moved with those branches during native Split.
            Universe.DestroyVehicle(owner, CrewDisposition.Kill);
            created = spawned.ToArray();
            reason = $"Quietly replaced with {spawned.Count} inert separating fragments."
                + (emptiedMass > .001 ? $" Released {emptiedMass:F1} kg of stored contents during isolation." : "");
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
                    && (registeringIds.Contains(vehicle.Id)
                        || vehicle.Parts?.Root is { } root && generatedTemplates.Contains(root.Template))) spawned.Add(vehicle);
            foreach (var fragment in spawned)
                try { RollBackFragment(fragment, sourceSystem); }
                catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Fragment rollback cleanup: {cleanup.Message}"); }
            reason = $"Slice commit refused; original source retained{(emptiedMass > .001 ? " with contents released and engines off" : "")}: {error.Message}";
            return false;
        }
    }

    private static void RollBackFragment(Vehicle fragment, CelestialSystem sourceSystem)
    {
        if (WorkerState!.GetValue(fragment) != null)
        {
            if (!fragment.IsDisposed) Universe.DestroyVehicle(fragment);
            return;
        }
        // Astronomical registers before Vehicle's constructor assigns Parts/worker.
        // A failed worker constructor has never reached GameAudio.Register or bubble
        // adoption. Native Dispose sets IsDisposed but then dereferences that missing
        // worker (or an even earlier missing Parts). Finish only the owned resources
        // and raw registration that are known to exist in this exceptional state.
        try { if (!fragment.IsDisposed) fragment.Dispose(endMission: false); }
        catch (Exception cleanup) { Console.WriteLine("[purr2d2] Incomplete native fragment teardown: " + cleanup.Message); }
        finally
        {
            sourceSystem.All.Deregister(fragment);
            if (fragment.Parts is { IsDisposed: false } tree) tree.Dispose();
        }
    }

    internal static float SourceMass(Part struck) => SliceSourceMass.Resolve(struck.StructuralPart, SourceParts(struck));
}
