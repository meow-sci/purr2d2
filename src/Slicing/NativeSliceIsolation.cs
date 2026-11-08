using Brutal.Numerics;
using KSA;

namespace Purr2D2.Slicing;

/// <summary>Only assembly coordinates survive the isolation frame. Physics state
/// must be sampled again at the later swap after the solver has applied.</summary>
internal sealed record IsolatedSliceSource(Part Source, Part[] Parts, Vehicle Owner,
    double4x4 PartTransform, double4x4 SourceToIsolated, doubleQuat SourceToIsolatedRotation) : IDisposable
{
    internal Vehicle[] Neighbors { get; init; } = [];
    internal bool ContentsReleased { get; set; }
    internal double EmptiedMassKg { get; set; }
    public void Dispose()
    {
        if (NativeSlicePhysics.UpdateState(Owner) is { } state) QuietSliceQuiescence.Release(state);
        // Pair protection is owned by the settled cleanup queue until native
        // clearance, even when cancellation retains an overlapping empty shell.
    }
    internal bool IsCurrent(out string reason)
    {
        reason = "The isolated source changed before the fragment swap.";
        if (Owner.IsDisposed || Owner.Parts.IsDisposed || Source.Tree != Owner.Parts || Owner.Parts.OwningVehicle != Owner
            || !ReferenceEquals(Owner.System, Universe.CurrentSystem)
            || Owner.Parts.Count != Parts.Length || Parts.Any(p => p.Tree != Owner.Parts)
            || !NativeSlicePhysics.SourceParts(Source).SequenceEqual(Parts)
            || !Source.MatrixAsmb2VehicleAsmb.Equals(PartTransform)) return false;
        reason = "";
        return true;
    }
}

internal static partial class NativeSlicePhysics
{
    internal static bool TryIsolate(Part struck, IReadOnlyList<PreparedPhysicsFragment> pieces,
        out IsolatedSliceSource? isolated, out string reason, SliceCommitTimings timings)
    {
        isolated = null;
        // Complete the same durable asset, mass, topology and budget checks used by
        // the final swap before the first irreversible native separation.
        if (!ValidateSource(struck, out reason) || !ValidatePreparedFragments(struck, pieces, out reason)) return false;
        var full = struck.StructuralPart;
        var sources = SourceParts(full);
        // A docking Split can restore a recorded root and reverse parent/child
        // pointers. Snapshot every original boundary before the first split.
        var boundaries = new List<(Part Source, Part Neighbor)>();
        if (full.TreeParent is { } originalParent) boundaries.Add((full, originalParent));
        foreach (var source in sources)
            foreach (var child in source.TreeChildren)
                if (!child.IsAttachedInternal) boundaries.Add((source, child));
        SliceFrameDiagnostics.SetSourceDetails(sources.Length, boundaries.Count);
        if (!double4x4.Invert(full.MatrixAsmb2VehicleAsmb, out var oldAssemblyToPart))
        { reason = "Source transformation is singular."; return false; }
        var separatedStates = new HashSet<VehicleUpdateState>();
        var splitVehicles = new HashSet<Vehicle>();
        if (UpdateState(full.Tree!.OwningVehicle!) is { } initialState) separatedStates.Add(initialState);
        var bulk = new IsolationBulkChanges();
        try
        {
            timings.Next(SliceCommitPhase.Isolation);
            // Retain the exact trees, since the parent split gives the struck part
            // a new owner. Each scope nests with Split's own staging scopes and is
            // closed before returning to the frame or destroying any vehicle.
            using (bulk)
            {
                foreach (var boundary in boundaries)
                {
                    bulk.Include(boundary.Source.Tree!);
                    if (!SplitBoundary(boundary.Source, boundary.Neighbor, separatedStates, splitVehicles))
                    { reason = "A connection refused native isolation; the source remains intact with completed native separations."; return false; }
                }
            }
            var owner = full.Tree?.OwningVehicle;
            if (owner is null || owner.IsDisposed || owner.Parts.Count != sources.Length || sources.Any(p => p.Tree != owner.Parts))
            { reason = "Native isolation did not yield the intact source shell and its interior; completed separations remain."; return false; }
            var transform = oldAssemblyToPart * full.MatrixAsmb2VehicleAsmb;
            if (!double4x4.Decompose(transform, out var scale, out var rotation, out _) || (scale - double3.One).Length() > 1e-5)
            { reason = "Native isolation unexpectedly changed source scale; the separated source is retained."; return false; }
            isolated = new(full, sources, owner, full.MatrixAsmb2VehicleAsmb, transform, rotation)
            {
                Neighbors = boundaries.Select(b => b.Neighbor.Tree?.OwningVehicle)
                    .Where(v => v != null && v != owner).Cast<Vehicle>().Distinct().ToArray()
            };
            ProtectIsolation(isolated);
            Quiesce(isolated, timings);
            reason = "Source isolated and emptied; preparing the fragment swap.";
            return true;
        }
        catch (Exception error)
        {
            // Split is itself irreversible: an exception inside its vehicle
            // constructor can leave incomplete native adoption. Never imply
            // that arbitrary host failures restored a usable source graph.
            reason = "Native isolation failed after partial native separation"
                + (isolated?.ContentsReleased == true ? "; source contents were released" : "") + ": " + error.Message;
            return false;
        }
        finally
        {
            // The bulk scopes have closed even on a partial failure. Reclassify
            // only actual split participants using their final control/seat state.
            if (bulk.ClosedSuccessfully)
                foreach (var vehicle in splitVehicles) SliceDebrisClassification.MarkSeparated(vehicle);
        }
    }

    private static bool SplitBoundary(Part full, Part neighbor, HashSet<VehicleUpdateState> participants,
        HashSet<Vehicle> splitVehicles)
    {
        Part.Connection? crossing = null;
        foreach (var edge in full.Connections)
            if (edge.OtherPart(full).FullPart == neighbor.FullPart) { crossing = edge; break; }
        if (crossing is null || full.Tree?.OwningVehicle is not { } owner) return false;
        var endpoint = crossing.Connectors[0].ConnectionPart.FullPart == full ? crossing.Connectors[0] : crossing.Connectors[1];
        if (owner.Split(crossing, endpoint, 0, out _) is not { } separated) return false;
        splitVehicles.Add(owner);
        splitVehicles.Add(separated);
        // Each successful split is irreversible, even if a later boundary or bulk
        // finalization fails. Protect that native relationship immediately.
        if (UpdateState(owner) is { } originalState && UpdateState(separated) is { } separatedState)
        {
            participants.Add(originalState); participants.Add(separatedState);
            QuietSliceContacts.Protect(originalState, separatedState);
            foreach (var state in participants) QuietSliceContacts.ProtectExisting(state, participants);
        }
        // Native Split moves the existing seats but leaves their roster vessel ID
        // unchanged. Keep surviving crew assigned to the actual new vessel while
        // preserving mission time, mission count and the seats themselves.
        foreach (var seat in separated.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero)
                Universe.KittenRoster.Find(seat.AssignedKittenHash)?.TransferToVehicle(separated.Id);
        return true;
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

    private sealed class IsolationBulkChanges : IDisposable
    {
        private readonly List<PartTree> _trees = new();
        internal bool ClosedSuccessfully { get; private set; }
        internal void Include(PartTree tree)
        {
            if (_trees.Contains(tree)) return;
            tree.BeginBulkChange();
            _trees.Add(tree);
        }
        public void Dispose()
        {
            List<Exception>? errors = null;
            for (int i = _trees.Count - 1; i >= 0; i--)
                try { _trees[i].EndBulkChange(); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            _trees.Clear();
            if (errors != null) throw new AggregateException("Could not finish native isolation staging updates.", errors);
            ClosedSuccessfully = true;
        }
    }
}
