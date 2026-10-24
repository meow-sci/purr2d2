using KSA;

namespace Purr2D2.Slicing;

/// <summary>Main-thread ownership of pair-local initialization protection. Native
/// narrowphase records real penetrating overlap even when contacts are suppressed,
/// then retires a pair after a clear pass. Cancellation must not release an overlap.</summary>
internal static class QuietSliceContacts
{
    private sealed record Owned(CollisionAvoidancePair Pair, PhysicsBubble Bubble, bool Created, bool Previous)
    {
        internal int Updates;
    }
    private static readonly List<Owned> Pairs = new();
    internal static int Count => Pairs.Count;

    internal static void ProtectExisting(VehicleUpdateState state, IReadOnlySet<VehicleUpdateState> participants)
    {
        if (state.Bubble == null) return;
        foreach (var pair in state.CollisionAvoidancePairs)
            if (!pair.Exonerated && participants.Contains(pair.Other(state)) && pair.Other(state).Bubble == state.Bubble)
                Own(pair, state.Bubble, created: false);
    }

    internal static void Protect(VehicleUpdateState a, VehicleUpdateState b)
    {
        if (a == b || a.Bubble == null || a.Bubble != b.Bubble) return;
        bool found = false;
        // Adoption can copy a relationship that already exists. Suppress every
        // active match: native narrowphase uses the first one, not the newest.
        foreach (var pair in a.CollisionAvoidancePairs)
            if (!pair.Exonerated && pair.Other(a) == b)
            { Own(pair, a.Bubble, created: false); found = true; }
        if (!found) Own(CollisionAvoidancePair.Link(a, b), a.Bubble, created: true);
    }

    private static void Own(CollisionAvoidancePair pair, PhysicsBubble bubble, bool created)
    {
        if (Pairs.Any(p => ReferenceEquals(p.Pair, pair))) return;
        Pairs.Add(new(pair, bubble, created, pair.SuppressAllContacts));
        pair.SuppressAllContacts = true;
    }

    // Run on every joined handoff, including when saber controls are disabled.
    internal static void Pump()
    {
        for (int i = Pairs.Count - 1; i >= 0; i--)
        {
            var owned = Pairs[i]; var pair = owned.Pair;
            if (pair.Exonerated || pair.A.ReadOnlyVehicle.IsDisposed || pair.B.ReadOnlyVehicle.IsDisposed
                || !ReferenceEquals(pair.A.ReadOnlyVehicle.System, Universe.CurrentSystem)
                || !ReferenceEquals(pair.B.ReadOnlyVehicle.System, Universe.CurrentSystem)
                || pair.A.Bubble != owned.Bubble || pair.B.Bubble != owned.Bubble
                || !pair.A.CollisionAvoidancePairs.Contains(pair) || !pair.B.CollisionAvoidancePairs.Contains(pair))
            { Release(owned); Pairs.RemoveAt(i); continue; }
            // Diagnose a trapped overlap; never turn a timeout into a solver kick.
            if (++owned.Updates % 600 == 0 && pair.OverlapThisStep)
                Console.WriteLine($"[purr2d2] Quiet separation still overlapping: {pair.A.Id} / {pair.B.Id}, {owned.Updates} settled updates; protection remains pair-local.");
        }
    }

    internal static void RetireToNative()
    {
        Pump();
        // Unload cannot keep pumping managed ownership. Live same-bubble pairs
        // already belong to native state lists and retain native overlap retirement.
        // Restoring them here would kick still-overlapping retained sources apart.
        Pairs.Clear();
    }

    private static void Release(Owned owned)
    {
        var pair = owned.Pair;
        pair.SuppressAllContacts = owned.Previous;
        if (!owned.Created) return;
        pair.Exonerated = true;
        pair.A.CollisionAvoidancePairs.Remove(pair);
        pair.B.CollisionAvoidancePairs.Remove(pair);
    }
}
