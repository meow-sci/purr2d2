namespace Purr2D2.Core;

/// <summary>Fixed-capacity pair latches; overflow drops new pairs without evicting live contacts.</summary>
internal sealed class SaberClashLatchTable<T> where T : class
{
    internal const int MaximumPairs = 256;
    internal const double SeparationGraceSeconds = .12;

    private struct Slot
    {
        internal T? A, B;
        internal double LastObserved;
        internal double SeparatedSince;
        internal bool Separating;
    }

    private readonly Slot[] _slots = new Slot[MaximumPairs];
    private int _count;

    internal int Count => _count;

    internal bool TryGetAt(int index, out T? a, out T? b)
    {
        if ((uint)index >= (uint)_slots.Length || _slots[index].A is not { } first || _slots[index].B is not { } second)
        {
            a = b = null;
            return false;
        }
        a = first; b = second;
        return true;
    }

    internal bool IsLatched(T a, T b) => Find(a, b) >= 0;

    internal bool CanLatch(T a, T b) => a is not null && b is not null && !ReferenceEquals(a, b)
        && Find(a, b) < 0 && _count < _slots.Length;

    /// <summary>Returns true only for the first contact. At capacity, later contacts are ignored.</summary>
    internal bool TryLatch(T a, T b, double now)
    {
        if (!CanLatch(a, b) || !double.IsFinite(now)) return false;
        int free = FindFree();
        if (free < 0) return false;
        _slots[free] = new Slot { A = a, B = b, LastObserved = now };
        _count++;
        return true;
    }

    /// <summary>Report a measured closest-distance sample for an already latched pair.</summary>
    internal void Observe(T a, T b, double distanceSquared, double releaseDistanceSquared,
        double now, double separationCooldown)
    {
        int index = Find(a, b);
        if (index < 0 || !double.IsFinite(distanceSquared) || distanceSquared < 0
            || !double.IsFinite(releaseDistanceSquared) || releaseDistanceSquared < 0
            || !double.IsFinite(now) || !double.IsFinite(separationCooldown) || separationCooldown < 0) return;
        ref Slot slot = ref _slots[index];
        ObserveTime(ref slot, now);
        if (distanceSquared <= releaseDistanceSquared)
        {
            slot.Separating = false;
            slot.SeparatedSince = 0;
            return;
        }
        BeginOrContinueSeparation(ref slot, now, separationCooldown);
    }

    /// <summary>Report a cheap AABB lower bound that is already beyond the release radius.</summary>
    internal void ObserveSeparated(T a, T b, double now, double separationCooldown)
    {
        int index = Find(a, b);
        if (index < 0 || !double.IsFinite(now) || !double.IsFinite(separationCooldown) || separationCooldown < 0) return;
        ref Slot slot = ref _slots[index];
        BeginOrContinueSeparation(ref slot, now, separationCooldown);
    }

    /// <summary>Break separation continuity when this frame cannot measure the pair.</summary>
    internal void MarkUnknown(T a, T b, double now)
    {
        int index = Find(a, b);
        if (index < 0) return;
        ref Slot slot = ref _slots[index];
        slot.Separating = false;
        slot.SeparatedSince = 0;
        if (double.IsFinite(now)) slot.LastObserved = now;
    }

    internal void Remove(T owner)
    {
        if (owner is null) return;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!ReferenceEquals(_slots[i].A, owner) && !ReferenceEquals(_slots[i].B, owner)) continue;
            _slots[i] = default;
            _count--;
        }
    }

    internal void Clear()
    {
        Array.Clear(_slots);
        _count = 0;
    }

    private void BeginOrContinueSeparation(ref Slot slot, double now, double cooldown)
    {
        bool continuous = slot.Separating && double.IsFinite(slot.LastObserved)
            && now >= slot.LastObserved && now - slot.LastObserved <= SeparationGraceSeconds;
        if (!continuous)
        {
            slot.Separating = true;
            slot.SeparatedSince = now;
        }
        slot.LastObserved = now;
        if (now >= slot.SeparatedSince && now - slot.SeparatedSince >= cooldown)
            slot = default;
        if (slot.A is null) _count--;
    }

    private static void ObserveTime(ref Slot slot, double now)
    {
        // A gap in valid snapshots never accumulates separation time.
        if (!double.IsFinite(slot.LastObserved) || now < slot.LastObserved
            || now - slot.LastObserved > SeparationGraceSeconds)
        {
            slot.Separating = false;
            slot.SeparatedSince = 0;
        }
        slot.LastObserved = now;
    }

    private int Find(T a, T b)
    {
        if (a is null || b is null) return -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            ref Slot slot = ref _slots[i];
            if ((ReferenceEquals(slot.A, a) && ReferenceEquals(slot.B, b))
                || (ReferenceEquals(slot.A, b) && ReferenceEquals(slot.B, a))) return i;
        }
        return -1;
    }

    private int FindFree()
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].A is null) return i;
        return -1;
    }
}
