using Purr2D2.Cutting;

namespace Purr2D2.Slicing;

// Evidence is expressed in the structural source's local frame, never the
// owning vehicle's assembly frame. Resolve it only after revalidating source.
internal readonly record struct VesselContactEvidence(CutPoint Point, CutPoint Blade, CutPoint Motion);

/// <summary>
/// Settled-thread-only transaction arbitration for future queued vessel cuts.
/// The caller must normalize subparts to their structural source and supply a
/// source validator covering the complete geometry revision. This class does
/// not inspect native state or authorize ownership rebases.
/// </summary>
internal sealed class VesselIntentQueue<TSource, TOwner, TRevision>
    where TSource : class where TOwner : class
{
    internal sealed class Intent
    {
        internal TSource Source { get; }
        internal TOwner Owner { get; }
        internal TRevision Revision { get; }
        internal VesselContactEvidence Evidence { get; }
        internal bool Active { get; set; }
        internal Intent(TSource source, TOwner owner, TRevision revision, VesselContactEvidence evidence)
        { Source = source; Owner = owner; Revision = revision; Evidence = evidence; }
    }

    internal enum AcquireResult { None, Acquired, Stale }
    internal readonly record struct SourceState(TSource? Source, TOwner? Owner, bool GeometryCurrent);
    private readonly List<Intent> _intents = [];
    private readonly HashSet<TSource> _sources = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TOwner> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly int _limit;
    internal int Count => _intents.Count;

    internal VesselIntentQueue(int limit) => _limit = Math.Max(1, limit);

    internal bool Enqueue(TSource source, TOwner owner, TRevision revision, VesselContactEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);
        if (_intents.Count >= _limit || !_sources.Add(source)) return false;
        _intents.Add(new(source, owner, revision, evidence));
        return true;
    }

    // Inspector runs only on the settled game thread and must fail closed on
    // unavailable, changed, or uncertain source/owner/mesh state. An exception
    // is also stale; it must not strand the owner's transaction lane.
    internal AcquireResult TryAcquire(Func<Intent, SourceState> inspect, out Intent? intent)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        intent = null;
        foreach (var candidate in _intents)
        {
            if (candidate.Active || _owners.Contains(candidate.Owner)) continue;
            bool current;
            try
            {
                var state = inspect(candidate);
                current = ReferenceEquals(state.Source, candidate.Source) && ReferenceEquals(state.Owner, candidate.Owner)
                    && state.GeometryCurrent;
            }
            catch { current = false; }
            intent = candidate;
            if (!current) { Remove(candidate); return AcquireResult.Stale; }
            candidate.Active = true;
            _owners.Add(candidate.Owner);
            return AcquireResult.Acquired;
        }
        return AcquireResult.None;
    }

    // Keep the lease through isolation and a later settled-step swap. Caller
    // must cancel/complete explicitly even if isolation leaves a detached source.
    internal bool Complete(Intent intent)
    {
        if (!intent.Active || !_intents.Contains(intent)) return false;
        _owners.Remove(intent.Owner);
        Remove(intent);
        return true;
    }

    // Retain active leases until their native cleanup has actually completed.
    internal int CancelPending()
    {
        int removed = 0;
        for (int i = _intents.Count - 1; i >= 0; i--)
            if (!_intents[i].Active) { Remove(_intents[i]); removed++; }
        return removed;
    }

    private void Remove(Intent intent)
    {
        _intents.Remove(intent);
        _sources.Remove(intent.Source);
    }
}
