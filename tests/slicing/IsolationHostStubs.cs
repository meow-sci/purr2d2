using Brutal.Numerics;

// Tests compile the production isolation pass. Only the native Split boundary is
// modelled: it always detaches a child, can reroot that child and changes owners.
namespace KSA
{
    internal readonly record struct KeyHash(uint Code) { internal static readonly KeyHash Zero = new(0); }
    internal sealed class Seat { internal KeyHash AssignedKittenHash = KeyHash.Zero; }
    internal sealed class KittenRecord { internal string VehicleId = ""; internal void TransferToVehicle(string id) => VehicleId = id; }
    internal sealed class Roster
    {
        internal KittenRecord Record = new();
        internal KittenRecord? Find(KeyHash hash) => Record;
    }
    internal static class Universe
    {
        internal static object CurrentSystem = new();
        internal static readonly Roster KittenRoster = new();
    }
    internal static class Program { internal static Vehicle? ControlledVehicle = null; }
    internal sealed class Part
    {
        internal readonly string Id;
        internal bool IsAttachedInternal;
        internal bool HasControl;
        internal Part StructuralPart => this;
        internal Part FullPart => this;
        internal PartTree? Tree;
        internal Part? TreeParent;
        internal readonly List<Part> TreeChildren = new();
        internal readonly List<Connection> Connections = new();
        internal double4x4 MatrixAsmb2VehicleAsmb = double4x4.Identity;
        internal Part(string id) => Id = id;
        internal sealed class Endpoint(Part part) { internal readonly Part ConnectionPart = part; }
        internal sealed class Connection(Part a, Part b)
        {
            internal readonly Endpoint[] Connectors = [new(a), new(b)];
            internal Part OtherPart(Part part) => Connectors[0].ConnectionPart == part ? Connectors[1].ConnectionPart : Connectors[0].ConnectionPart;
        }
        internal static void Attach(Part parent, Part child, bool interior = false)
        {
            parent.TreeChildren.Add(child); child.TreeParent = parent; child.IsAttachedInternal = interior;
            if (interior) return;
            var edge = new Connection(parent, child); parent.Connections.Add(edge); child.Connections.Add(edge);
        }
    }
    internal sealed class PartTree
    {
        internal static readonly List<PartTree> Created = new();
        internal bool IsDisposed, ThrowOnEnd;
        internal Vehicle? OwningVehicle;
        internal readonly List<Part> Members;
        internal int Count => Members.Count;
        internal int Depth, OuterBegins;
        internal PartTree(List<Part> members)
        {
            Members = members; foreach (var p in members) p.Tree = this; Created.Add(this);
        }
        internal void BeginBulkChange() { if (Depth++ == 0) OuterBegins++; }
        internal void EndBulkChange()
        {
            if (--Depth < 0) throw new InvalidOperationException("Unbalanced bulk change");
            if (ThrowOnEnd && Depth == 0) throw new InvalidOperationException("Fixture bulk finalization failure");
        }
    }
    internal sealed class VehicleUpdateState { }
    internal class Vehicle
    {
        internal static readonly List<string> SplitOrder = new();
        internal static readonly List<int> SplitTreeSizes = new();
        internal static int ThrowOnSplit;
        internal static Part? RerootAfterFirstSplit;
        internal readonly PartTree Parts;
        internal readonly object System = Universe.CurrentSystem;
        internal readonly string Id;
        internal bool IsDisposed;
        internal bool IsDebris, IsEditedVehicle;
        internal bool HasControlModule => Parts.Members.Any(part => part.HasControl);
        internal void MarkAsDebris()
        {
            if (PartTree.Created.Any(tree => tree.Depth != 0)) throw new InvalidOperationException("Classified inside bulk change");
            IsDebris = true;
        }
        internal bool Drained, EnginesOff;
        internal readonly VehicleUpdateState Worker = new();
        internal Seat[] Crew = [];
        internal Vehicle(string id, List<Part> members) { Id = id; Parts = new(members) { OwningVehicle = this }; }
        internal Vehicle? Split(Part.Connection edge, Part.Endpoint endpoint, int impulse, out object? pose)
        {
            pose = null;
            if (endpoint.ConnectionPart.Tree?.OwningVehicle != this) throw new InvalidOperationException("Used stale owning vehicle");
            if (Parts.Depth == 0) throw new InvalidOperationException("Missing outer bulk scope");
            var a = edge.Connectors[0].ConnectionPart; var b = edge.Connectors[1].ConnectionPart;
            var child = a.TreeParent == b ? a : b;
            SplitOrder.Add(child.Id); SplitTreeSizes.Add(Parts.Count);
            if (ThrowOnSplit == SplitOrder.Count) throw new InvalidOperationException("Fixture native split failure");
            var moved = new List<Part>();
            void Collect(Part p) { moved.Add(p); foreach (var c in p.TreeChildren) Collect(c); }
            Collect(child);
            child.TreeParent!.TreeChildren.Remove(child); child.TreeParent = null;
            a.Connections.Remove(edge); b.Connections.Remove(edge);
            Parts.Members.RemoveAll(moved.Contains);
            var separated = new Vehicle(Id + "_split", moved);
            Parts.BeginBulkChange(); separated.Parts.BeginBulkChange();
            Parts.EndBulkChange(); separated.Parts.EndBulkChange();
            if (SplitOrder.Count == 1 && RerootAfterFirstSplit is { } root)
            {
                // Restore a pre-dock root one edge below the detached source.
                child.TreeChildren.Remove(root); root.TreeParent = null;
                root.TreeChildren.Add(child); child.TreeParent = root;
            }
            return separated;
        }
    }
    internal sealed class KittenEva : Vehicle { internal KittenEva() : base("kitten", []) { } }
}
namespace Purr2D2.Slicing
{
    internal static class QuietSliceContacts
    {
        internal static int Protections;
        internal static void Protect(KSA.VehicleUpdateState a, KSA.VehicleUpdateState b) => Protections++;
        internal static void ProtectExisting(KSA.VehicleUpdateState state, IReadOnlySet<KSA.VehicleUpdateState> participants) { }
    }
    internal readonly record struct PreparedPhysicsFragment(bool Valid);
    internal static class QuietSliceQuiescence { internal static void Release(KSA.VehicleUpdateState state) { } }
    internal static partial class NativeSlicePhysics
    {
        internal static KSA.VehicleUpdateState? UpdateState(KSA.Vehicle owner) => owner.Worker;
        private static void ProtectIsolation(IsolatedSliceSource isolated) { }
        private static void Quiesce(IsolatedSliceSource isolated, SliceCommitTimings timings)
        {
            if (isolated.Owner.Parts.Count != isolated.Parts.Length) throw new InvalidOperationException("Drained unsplit host");
            isolated.Owner.Drained = isolated.Owner.EnginesOff = isolated.ContentsReleased = true;
            isolated.EmptiedMassKg = 10;
        }
        internal static bool ValidateSource(KSA.Part struck, out string reason)
        {
            reason = "Stale fixture source";
            return struck.Tree is { IsDisposed: false, OwningVehicle.IsDisposed: false }
                && ReferenceEquals(struck.Tree.OwningVehicle.System, KSA.Universe.CurrentSystem);
        }
        internal static bool ValidatePreparedFragments(KSA.Part struck, IReadOnlyList<PreparedPhysicsFragment> pieces, out string reason)
        {
            reason = "Unprepared fixture fragment";
            return pieces.Count >= 2 && pieces.All(p => p.Valid);
        }
    }
}
