using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Detection;

// Settled-thread-only registry. Producers that mutate a mesh in place must call
// Invalidate; unknown mutable geometry must bypass the store altogether.
internal sealed class SliceGeometryRevisionRegistry
{
    private readonly Dictionary<object, ulong> _revisions = new(ReferenceEqualityComparer.Instance);
    internal ulong Epoch { get; private set; }
    internal ulong Revision(object mesh) => _revisions.TryGetValue(mesh, out var r) ? r : 0;
    internal void Invalidate(object mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ulong revision = Revision(mesh);
        if (revision == ulong.MaxValue) throw new InvalidOperationException("Geometry revision exhausted.");
        _revisions[mesh] = revision + 1;
    }
    internal void NewScene()
    {
        if (Epoch == ulong.MaxValue) throw new InvalidOperationException("Geometry scene epoch exhausted.");
        Epoch++; _revisions.Clear();
    }
}

// Settled-thread cache index. Only opaque integer IDs and copied triangles go to
// the worker; native mesh and array identity never enter a build job.
internal sealed class CachedRibbonStore : IDisposable
{
    internal const int MaximumEntries = 256, MaximumPendingBuilds = 2;
    internal readonly record struct Key(object Mesh, object Positions, ulong Epoch, ulong Revision);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key x, Key y) => ReferenceEquals(x.Mesh, y.Mesh) && ReferenceEquals(x.Positions, y.Positions)
            && x.Epoch == y.Epoch && x.Revision == y.Revision;
        public int GetHashCode(Key k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Mesh), RuntimeHelpers.GetHashCode(k.Positions), k.Epoch, k.Revision);
    }
    internal sealed class Entry(CachedRibbonGeometry geometry)
    {
        internal readonly CachedRibbonGeometry Geometry = geometry;
        internal int Leases;
        internal ulong Used;
        internal bool Invalid;
    }
    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;
        private Lease(Entry entry) { _entry = entry; entry.Leases++; }
        internal CachedRibbonGeometry Geometry => _entry?.Geometry ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose() { if (_entry is { } entry) { entry.Leases--; _entry = null; } }
        internal static Lease Create(Entry entry) => new(entry);
    }
    private readonly Dictionary<Key, Entry> _entries = new(new KeyComparer());
    private readonly Dictionary<Key, long> _pending = new(new KeyComparer());
    private readonly Dictionary<long, Key> _jobs = new();
    private readonly Builder _builder = new();
    private ulong _clock;
    private long _nextJob, _bytes;
    private bool _disposed;
    internal long StorageBytes => _bytes;
    internal int EntryCount => _entries.Count;
    internal int PendingCount => _jobs.Count;
    internal int BuildFailures { get; private set; }
    internal int CapacityRefusals { get; private set; }

    internal bool ContainsOrPending(Key key)
    {
        ThrowIfDisposed(); Drain();
        return _pending.ContainsKey(key) || _entries.TryGetValue(key, out var entry) && !entry.Invalid;
    }

    internal Lease? Acquire(Key key)
    {
        ThrowIfDisposed(); Drain();
        if (!_entries.TryGetValue(key, out var entry) || entry.Invalid) return null;
        entry.Used = Tick();
        return Lease.Create(entry);
    }
    // Takes exclusive ownership of the array; caller must not read or write it
    // after a successful call. Scene/native identity stays in this index only.
    internal bool TryBuild(Key key, CutTriangle[] capturedTriangles)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(capturedTriangles); Drain();
        if (_entries.TryGetValue(key, out var existing) && !existing.Invalid || _pending.ContainsKey(key)
            || _jobs.Count >= MaximumPendingBuilds) return false;
        if (_nextJob == long.MaxValue) throw new InvalidOperationException("Geometry build IDs exhausted.");
        long id = ++_nextJob;
        _pending.Add(key, id); _jobs.Add(id, key);
        _builder.Submit(new(id, capturedTriangles));
        return true;
    }
    // Call after each explicit invalidation, array replacement, or scene change.
    // Active leases remain usable only by already-validated callers; pending
    // query owners must separately invalidate their frozen cursor immediately.
    internal void Invalidate(Func<Key, bool> predicate)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(predicate);
        foreach (var pair in _entries)
            if (predicate(pair.Key)) pair.Value.Invalid = true;
        foreach (var pair in _pending.ToArray())
            if (predicate(pair.Key)) _pending.Remove(pair.Key); // job still occupies a slot until drained
        EvictInvalid();
    }
    internal void Drain()
    {
        ThrowIfDisposed();
        while (_builder.TryTake(out var result))
        {
            if (!_jobs.Remove(result.Id, out var key)) continue;
            bool current = _pending.TryGetValue(key, out long expected) && expected == result.Id;
            if (current) _pending.Remove(key);
            if (!current) continue;
            if (result.Geometry == null) { BuildFailures++; continue; }
            EvictInvalid();
            long bytes = result.Geometry.StorageBytes;
            while ((_entries.Count >= MaximumEntries || _bytes + bytes > CachedRibbonGeometry.MaximumBytes) && EvictLeastRecent()) { }
            if (_entries.Count >= MaximumEntries || _bytes + bytes > CachedRibbonGeometry.MaximumBytes)
            { CapacityRefusals++; continue; }
            // An invalid leased entry for the same exact key cannot be replaced
            // until the lease is released; never erase its accounting.
            if (_entries.ContainsKey(key)) { CapacityRefusals++; continue; }
            _entries.Add(key, new(result.Geometry) { Used = Tick() }); _bytes += bytes;
        }
    }
    private bool EvictLeastRecent()
    {
        Key key = default; Entry? oldest = null;
        foreach (var pair in _entries)
            if (pair.Value.Leases == 0 && (oldest == null || pair.Value.Used < oldest.Used))
            { key = pair.Key; oldest = pair.Value; }
        if (oldest == null) return false;
        _entries.Remove(key); _bytes -= oldest.Geometry.StorageBytes; return true;
    }
    private void EvictInvalid()
    {
        foreach (var pair in _entries.ToArray())
            if (pair.Value.Invalid && pair.Value.Leases == 0)
            { _entries.Remove(pair.Key); _bytes -= pair.Value.Geometry.StorageBytes; }
    }
    private ulong Tick() { if (_clock == ulong.MaxValue) throw new InvalidOperationException("Cache clock exhausted."); return ++_clock; }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _entries.Clear(); _pending.Clear(); _jobs.Clear(); _bytes = 0; _builder.Dispose();
    }

    // This object has no cache index or scene references. A single background
    // thread processes at most two submitted copied arrays, at below-normal priority.
    private sealed class Builder : IDisposable
    {
        internal readonly record struct Job(long Id, CutTriangle[] Triangles);
        internal readonly record struct Result(long Id, CachedRibbonGeometry? Geometry);
        private readonly ConcurrentQueue<Job> _jobs = new();
        private readonly ConcurrentQueue<Result> _results = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;
        private volatile bool _stopped;
        internal Builder()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Purr2D2 ribbon cache", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        internal void Submit(Job job) { _jobs.Enqueue(job); _signal.Set(); }
        internal bool TryTake(out Result result) => _results.TryDequeue(out result);
        private void Run()
        {
            while (!_stopped)
            {
                if (!_jobs.TryDequeue(out var job)) { _signal.WaitOne(); continue; }
                CachedRibbonGeometry? result = null;
                try { result = new(job.Triangles); }
                catch (Exception) { } // publish a terminal failure for every rejected build
                if (!_stopped) _results.Enqueue(new(job.Id, result));
            }
            while (_jobs.TryDequeue(out _)) { }
        }
        // Never join a geometry worker from the frame thread.
        public void Dispose() { _stopped = true; _signal.Set(); }
    }
}
