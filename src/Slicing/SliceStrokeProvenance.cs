namespace Purr2D2.Slicing;

/// <summary>Bounded transient identities for current and immediately preceding strokes.</summary>
internal sealed class SliceStrokeProvenance<T> where T : class
{
    private HashSet<T> _current = new(ReferenceEqualityComparer.Instance);
    private HashSet<T> _previous = new(ReferenceEqualityComparer.Instance);
    private ulong _stroke, _previousStroke;
    private bool _full, _previousFull;
    internal void Observe(ulong stroke)
    {
        if (stroke == _stroke) return;
        (_current, _previous) = (_previous, _current);
        _previousStroke = _stroke; _previousFull = _full;
        _current.Clear(); _stroke = stroke; _full = false;
    }
    internal void Mark(T identity, ulong originatingStroke)
    {
        if (originatingStroke == _stroke) Add(_current, identity, ref _full);
        else if (originatingStroke == _previousStroke) Add(_previous, identity, ref _previousFull);
    }
    private static void Add(HashSet<T> set, T identity, ref bool full)
    {
        if (set.Count >= 4096 && !set.Contains(identity)) { full = true; return; }
        set.Add(identity);
    }
    // On overflow reject admissions rather than lose identities and recut a fragment.
    internal bool Suppress(T identity, ulong stroke) => stroke == _stroke ? _full || _current.Contains(identity)
        : stroke == _previousStroke && (_previousFull || _previous.Contains(identity));
    internal void Clear() { _current.Clear(); _previous.Clear(); _full = _previousFull = false; _stroke = _previousStroke = 0; }
}
