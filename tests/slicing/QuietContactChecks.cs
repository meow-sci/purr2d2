using KSA;
using Purr2D2.Slicing;

internal static class QuietContactChecks
{
    private static void Main()
    {
        var bubble = new PhysicsBubble();
        var source = State("source", bubble); var neighbor = State("neighbor", bubble); var unrelated = State("unrelated", bubble);
        var native = CollisionAvoidancePair.Link(source, neighbor);
        var duplicate = CollisionAvoidancePair.Link(source, neighbor);
        native.OverlapThisStep = duplicate.OverlapThisStep = true;
        QuietSliceContacts.Protect(source, neighbor);
        Check(source.CollisionAvoidancePairs.Count == 2 && native.SuppressAllContacts && duplicate.SuppressAllContacts,
            "all native duplicate matches are protected without creating another pair");
        Check(unrelated.CollisionAvoidancePairs.Count == 0, "unrelated craft and terrain receive no contact protection");
        for (int i = 0; i < 605; i++) QuietSliceContacts.Pump();
        Check(QuietSliceContacts.Count == 2 && native.SuppressAllContacts,
            "cancellation/disable pumping cannot expire an overlap on a timer");
        native.Exonerated = duplicate.Exonerated = true;
        QuietSliceContacts.Pump();
        Check(QuietSliceContacts.Count == 0 && !native.SuppressAllContacts,
            "native clear-overlap retirement restores borrowed flags");

        var fragment = State("fragment", bubble); var sibling = State("sibling", bubble);
        var copied = CollisionAvoidancePair.Link(fragment, neighbor);
        QuietSliceContacts.Protect(fragment, neighbor); QuietSliceContacts.Protect(fragment, sibling);
        Check(copied.SuppressAllContacts && fragment.CollisionAvoidancePairs.Count == 2,
            "replacement relationships regain suppression and siblings get one owned pair");
        var created = fragment.CollisionAvoidancePairs.Single(p => p.Other(fragment) == sibling);
        sibling.Bubble = new(); QuietSliceContacts.Pump();
        Check(created.Exonerated && !fragment.CollisionAvoidancePairs.Contains(created) && !sibling.CollisionAvoidancePairs.Contains(created),
            "bubble changes clean both lists of owned relationships");
        Universe.CurrentSystem = new(); QuietSliceContacts.Pump();
        Check(QuietSliceContacts.Count == 0 && !copied.SuppressAllContacts && !copied.Exonerated,
            "world changes release managed ownership while preserving borrowed native relationships");

        source = State("disposed source", bubble); neighbor = State("survivor", bubble);
        QuietSliceContacts.Protect(source, neighbor);
        source.ReadOnlyVehicle.IsDisposed = true; QuietSliceContacts.Pump();
        Check(QuietSliceContacts.Count == 0 && neighbor.CollisionAvoidancePairs.Count == 0,
            "disposed source releases owned pairs from surviving neighbors");

        source = State("retained empty source", bubble); neighbor = State("retained neighbor", bubble);
        QuietSliceContacts.Protect(source, neighbor);
        var pending = source.CollisionAvoidancePairs.Single();
        QuietSliceContacts.RetireToNative();
        Check(QuietSliceContacts.Count == 0 && pending.SuppressAllContacts && !pending.Exonerated,
            "unload transfers a live overlap to native retirement without leaked managed references or contact kick");
        pending.Exonerated = true; // Native clear pass owns retirement after unload.
        Console.WriteLine("PASS: quiet contact ownership, native clearance, cancellation, replacement, world/bubble change and unload");
    }
    private static VehicleUpdateState State(string id, PhysicsBubble bubble) => new(new(id)) { Bubble = bubble };
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); Console.WriteLine("PASS: " + reason); }
}

namespace KSA
{
    internal sealed class Vehicle(string id) { internal string Id = id; internal bool IsDisposed = false; internal object System = Universe.CurrentSystem; }
    internal sealed class PhysicsBubble { }
    internal sealed class VehicleUpdateState(Vehicle vehicle)
    {
        internal Vehicle ReadOnlyVehicle = vehicle;
        internal string Id => ReadOnlyVehicle.Id;
        internal PhysicsBubble? Bubble;
        internal List<CollisionAvoidancePair> CollisionAvoidancePairs = new();
    }
    internal static class Universe { internal static object CurrentSystem = new(); }
    internal sealed class CollisionAvoidancePair(VehicleUpdateState a, VehicleUpdateState b)
    {
        internal readonly VehicleUpdateState A = a, B = b;
        internal bool Exonerated, SuppressAllContacts, OverlapThisStep;
        internal VehicleUpdateState Other(VehicleUpdateState self) => self == A ? B : A;
        internal static CollisionAvoidancePair Link(VehicleUpdateState a, VehicleUpdateState b)
        { var pair = new CollisionAvoidancePair(a, b); a.CollisionAvoidancePairs.Add(pair); b.CollisionAvoidancePairs.Add(pair); return pair; }
    }
}
