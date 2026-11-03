using System.Runtime.CompilerServices;
using KSA;

namespace Purr2D2.Runtime;

/// <summary>Conservative bookkeeping for native clutter observations. A successful
/// readback is not a resident installation: for a nonempty cell the first verified
/// bubble update must actually add its entry after that readback. A pre-existing,
/// unproven entry may have been installed from an older generation.</summary>
internal sealed class SliceClutterReadiness
{
    private const int MaximumRecords = 4096;
    private readonly object _sync = new();
    private long _generation;
    private long _revision;
    internal long Revision { get { lock (_sync) return _revision; } }
    private void Changed() { unchecked { _revision++; } }
    private readonly Dictionary<CellKey, Stamp> _readbacks = new();
    private readonly Dictionary<ResidentKey, long> _residentGenerations = new();

    private readonly struct CellKey(object ecotype, CubeCellGrid.Cell cell) : IEquatable<CellKey>
    {
        internal readonly object Ecotype = ecotype;
        internal readonly CubeCellGrid.Cell Cell = cell;
        public bool Equals(CellKey other) => ReferenceEquals(Ecotype, other.Ecotype) && Cell.Equals(other.Cell);
        public override bool Equals(object? obj) => obj is CellKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Ecotype), Cell);
    }
    private readonly struct ResidentKey(object bubble, object ecotype, CubeCellGrid.Cell cell) : IEquatable<ResidentKey>
    {
        private readonly object _bubble = bubble;
        private readonly object _ecotype = ecotype;
        private readonly CubeCellGrid.Cell _cell = cell;
        internal bool IsBubble(object bubble) => ReferenceEquals(_bubble, bubble);
        internal bool Matches(object ecotype, CubeCellGrid.Cell? cell) => ReferenceEquals(_ecotype, ecotype) && (!cell.HasValue || _cell.Equals(cell.Value));
        public bool Equals(ResidentKey other) => ReferenceEquals(_bubble, other._bubble)
            && ReferenceEquals(_ecotype, other._ecotype) && _cell.Equals(other._cell);
        public override bool Equals(object? obj) => obj is ResidentKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_bubble), RuntimeHelpers.GetHashCode(_ecotype), _cell);
    }
    private readonly record struct Stamp(int BufferId, int Count, long Generation);
    internal readonly record struct Ticket(object Bubble, object Ecotype, CubeCellGrid.Cell Cell,
        int BufferId, int Count, long Generation, long Revision, bool WasResident);

    /// <summary>Call only after a successful native readback and a checked layout;
    /// currentBufferId must be read after completion from the same grid.</summary>
    internal bool RecordReadback(object ecotype, CubeCellGrid.Cell cell, int bufferId, int currentBufferId, int count)
    {
        lock (_sync)
        {
            Changed();
            var key = new CellKey(ecotype, cell);
            if (bufferId < 0 || bufferId != currentBufferId || count is < 0 or > 256)
            { _readbacks.Remove(key); return false; }
            if (!_readbacks.ContainsKey(key) && _readbacks.Count >= MaximumRecords)
            { _readbacks.Clear(); _residentGenerations.Clear(); return false; }
            // Overflow makes all existing generations ambiguous; fail closed.
            if (_generation == long.MaxValue)
            { _readbacks.Clear(); _residentGenerations.Clear(); _generation = 0; return false; }
            _readbacks[key] = new Stamp(bufferId, count, ++_generation);
            return true;
        }
    }

    /// <summary>Call before a native bubble update with a checked observation of
    /// whether this exact cell is already present in its instance dictionary.</summary>
    internal bool BeginBubble(object bubble, object ecotype, CubeCellGrid.Cell cell, int currentBufferId,
        bool wasResident, out Ticket ticket)
    {
        lock (_sync)
        {
            if (!_readbacks.TryGetValue(new CellKey(ecotype, cell), out var stamp)
                || stamp.BufferId != currentBufferId)
            { ticket = default; return false; }
            ticket = new Ticket(bubble, ecotype, cell, stamp.BufferId, stamp.Count, stamp.Generation, _revision, wasResident);
            return true;
        }
    }

    /// <summary>Call only after that update returns successfully. desired and resident
    /// must come from checked native collections for this bubble's same ecotype list.
    /// For nonempty cells, a pre-existing entry is accepted only if this ledger has
    /// already proved its installation from the same readback generation.</summary>
    internal bool FinishBubble(in Ticket ticket, int currentBufferId, bool desired, bool resident)
    {
        lock (_sync)
        {
            if (ticket.Bubble == null || ticket.Ecotype == null || ticket.Revision != _revision || !desired
                || !_readbacks.TryGetValue(new CellKey(ticket.Ecotype, ticket.Cell), out var stamp)
                || stamp.Generation != ticket.Generation || stamp.BufferId != currentBufferId)
                return false;
            var key = new ResidentKey(ticket.Bubble, ticket.Ecotype, ticket.Cell);
            if (stamp.Count == 0)
            {
                _residentGenerations.Remove(key);
                return !ticket.WasResident && !resident;
            }
            if (!resident || ticket.WasResident && (!_residentGenerations.TryGetValue(key, out var generation)
                || generation != stamp.Generation)) return false;
            if (!_residentGenerations.ContainsKey(key) && _residentGenerations.Count >= MaximumRecords)
            { _residentGenerations.Clear(); return false; }
            _residentGenerations[key] = stamp.Generation;
            return true;
        }
    }

    internal void Invalidate(object ecotype, CubeCellGrid.Cell? cell = null)
    {
        lock (_sync)
        {
            Changed();
            foreach (var key in _readbacks.Keys.Where(key => ReferenceEquals(key.Ecotype, ecotype)
                && (!cell.HasValue || key.Cell.Equals(cell.Value))).ToArray()) _readbacks.Remove(key);
            foreach (var key in _residentGenerations.Keys.Where(key => key.Matches(ecotype, cell)).ToArray())
                _residentGenerations.Remove(key);
        }
    }

    internal void InvalidateBubble(object bubble)
    {
        lock (_sync)
        {
            Changed();
            foreach (var key in _residentGenerations.Keys.Where(key => key.IsBubble(bubble)).ToArray())
                _residentGenerations.Remove(key);
        }
    }

    // Conservative invalidation discards every proof, including in-flight tickets.
    // Hooks must call this before unload, invalidate, resize/reset, dispose and scene teardown.
    internal void Invalidate()
    {
        lock (_sync) { Changed(); _readbacks.Clear(); _residentGenerations.Clear(); }
    }
}
