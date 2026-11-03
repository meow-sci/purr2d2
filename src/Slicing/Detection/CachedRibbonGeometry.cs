using System.Runtime.CompilerServices;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Detection;

// Pure, immutable worker result. The constructor copies the caller's already-captured
// geometry; no native mesh, span, transform or scene object crosses this boundary.
internal sealed class CachedRibbonGeometry
{
    private const int LeafSize = 8;
    internal const long MaximumBytes = 64L * 1024 * 1024;
    private readonly CutTriangle[] _triangles;
    private readonly int[] _indices;
    private readonly Node[] _nodes;
    private readonly record struct Node(CutBounds Bounds, int Start, int Count, int Left, int Right);
    internal int TriangleCount => _triangles.Length;
    internal int NodeCount => _nodes.Length;
    internal long StorageBytes => (long)_triangles.Length * Unsafe.SizeOf<CutTriangle>()
        + (long)_indices.Length * sizeof(int) + (long)_nodes.Length * Unsafe.SizeOf<Node>();

    internal CachedRibbonGeometry(CutTriangle[] copiedTriangles)
    {
        ArgumentNullException.ThrowIfNull(copiedTriangles);
        // A balanced binary tree has fewer than 2*n nodes. Refuse before allocation.
        long maximum = (long)copiedTriangles.Length * (Unsafe.SizeOf<CutTriangle>() + sizeof(int) + 2L * Unsafe.SizeOf<Node>());
        if (maximum > MaximumBytes) throw new ArgumentException("Geometry exceeds the cache build limit.", nameof(copiedTriangles));
        _triangles = (CutTriangle[])copiedTriangles.Clone();
        _indices = new int[_triangles.Length];
        for (int i = 0; i < _indices.Length; i++)
        {
            var t = _triangles[i];
            if (!t.A.Finite || !t.B.Finite || !t.C.Finite || !t.Center.Finite)
                throw new ArgumentException("Geometry must contain finite coordinates.", nameof(copiedTriangles));
            _indices[i] = i;
        }
        var nodes = new List<Node>();
        if (_indices.Length > 0) Build(0, _indices.Length, nodes);
        _nodes = nodes.ToArray();
    }

    private int Build(int start, int count, List<Node> nodes)
    {
        var min = new CutPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new CutPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var cmin = min; var cmax = max;
        for (int i = start; i < start + count; i++)
        {
            var t = _triangles[_indices[i]];
            min = CutPoint.Min(min, t.Min); max = CutPoint.Max(max, t.Max);
            cmin = CutPoint.Min(cmin, t.Center); cmax = CutPoint.Max(cmax, t.Center);
        }
        int node = nodes.Count;
        nodes.Add(new(new(min, max), start, count, -1, -1));
        if (count <= LeafSize) return node;
        var extent = cmax - cmin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_indices, start, count, Comparer<int>.Create((a, b) =>
        {
            var x = _triangles[a].Center; var y = _triangles[b].Center;
            int cmp = axis == 0 ? x.X.CompareTo(y.X) : axis == 1 ? x.Y.CompareTo(y.Y) : x.Z.CompareTo(y.Z);
            return cmp != 0 ? cmp : a.CompareTo(b);
        }));
        int half = count / 2;
        int left = Build(start, half, nodes), right = Build(start + half, count - half, nodes);
        nodes[node] = new(new(min, max), 0, 0, left, right);
        return node;
    }

    // Each candidate owns one cursor; callers share a budget among all cursors.
    internal Query Begin(BladeSweep targetLocalSweep) => new(this, targetLocalSweep);
    internal readonly record struct Contact(CutPoint Point, CutPoint Normal, double Time, double Along, int TriangleIndex);
    internal enum QueryStatus { Pending, Complete, UnsupportedMotion }
    internal readonly record struct Progress(QueryStatus Status, Contact? Best, int NodesVisited, int TrianglesTested);
    internal sealed class Budget
    {
        internal int Nodes, Triangles;
        internal readonly Func<bool>? TimeAvailable;
        internal Budget(int nodes, int triangles, Func<bool>? timeAvailable = null)
        { Nodes = Math.Max(0, nodes); Triangles = Math.Max(0, triangles); TimeAvailable = timeAvailable; }
        internal bool HasTime => TimeAvailable?.Invoke() ?? true;
    }

    internal sealed class Query
    {
        private readonly CachedRibbonGeometry _geometry;
        private readonly List<(BladeSweep Sweep, double Start, double End)> _intervals = new(16);
        private readonly int[] _stack = new int[64]; // balanced tree of any permitted size fits
        private int _interval, _stackCount, _leafStart, _leafEnd;
        private Contact? _best;
        private QueryStatus _status;
        internal Query(CachedRibbonGeometry geometry, BladeSweep sweep)
        {
            _geometry = geometry;
            if (!sweep.Finite || !double.IsFinite(sweep.Tolerance) || sweep.Tolerance <= 0 || !Split(sweep, 0, 1, 0))
            { _intervals.Clear(); _status = QueryStatus.UnsupportedMotion; }
            else if (geometry._nodes.Length == 0) _status = QueryStatus.Complete;
            else { _status = QueryStatus.Pending; _stack[_stackCount++] = 0; }
        }
        private bool Split(BladeSweep s, double start, double end, int depth)
        {
            if (s.Subdivisions <= BladeSweep.MaximumSubdivisions)
            { _intervals.Add((s, start, end)); return true; }
            if (depth == 4) return false;
            var bm = (s.Base0 + s.Base1) * .5; var tm = (s.Tip0 + s.Tip1) * .5;
            double mid = (start + end) * .5;
            return Split(new(s.Base0, s.Tip0, bm, tm, s.Tolerance), start, mid, depth + 1)
                && Split(new(bm, tm, s.Base1, s.Tip1, s.Tolerance), mid, end, depth + 1);
        }
        internal Progress Advance(Budget budget)
        {
            ArgumentNullException.ThrowIfNull(budget);
            int nodes = 0, triangles = 0;
            while (_status == QueryStatus.Pending && budget.HasTime)
            {
                var interval = _intervals[_interval];
                if (_leafStart < _leafEnd)
                {
                    if (budget.Triangles == 0) break;
                    int index = _geometry._indices[_leafStart++]; budget.Triangles--; triangles++;
                    var triangle = _geometry._triangles[index];
                    if (interval.Sweep.TryHit(triangle, out var point, out double along, out double t))
                    {
                        var hit = new Contact(point, triangle.Normal, interval.Start + (interval.End - interval.Start) * t, along, index);
                        if (Better(hit, _best)) _best = hit;
                    }
                    continue;
                }
                if (_stackCount == 0)
                {
                    // Include a following interval at the same time boundary: it can
                    // improve the along-blade or point tie even when time is equal.
                    if (++_interval == _intervals.Count || (_best.HasValue
                        && _intervals[_interval].Start > _best.Value.Time + 1e-10))
                    { _status = QueryStatus.Complete; break; }
                    _stack[_stackCount++] = 0;
                    continue;
                }
                if (budget.Nodes == 0) break;
                var node = _geometry._nodes[_stack[--_stackCount]]; budget.Nodes--; nodes++;
                var s = interval.Sweep;
                var bounds = CutBounds.Sweep(s.Base0, s.Tip0, s.Base1, s.Tip1, Math.Max(s.Tolerance, 1e-8));
                if (!node.Bounds.Intersects(bounds)) continue;
                if (node.Count > 0) { _leafStart = node.Start; _leafEnd = node.Start + node.Count; }
                else { _stack[_stackCount++] = node.Right; _stack[_stackCount++] = node.Left; }
            }
            return new(_status, _best, nodes, triangles);
        }
        private static bool Better(Contact a, Contact? old)
        {
            if (!old.HasValue) return true;
            var b = old.Value; const double epsilon = 1e-10;
            if (a.Time < b.Time - epsilon) return true;
            if (Math.Abs(a.Time - b.Time) > epsilon) return false;
            if (a.Along < b.Along - epsilon) return true;
            if (Math.Abs(a.Along - b.Along) > epsilon) return false;
            int c = a.Point.X.CompareTo(b.Point.X);
            if (c == 0) c = a.Point.Y.CompareTo(b.Point.Y);
            if (c == 0) c = a.Point.Z.CompareTo(b.Point.Z);
            if (c == 0) c = a.Normal.X.CompareTo(b.Normal.X);
            if (c == 0) c = a.Normal.Y.CompareTo(b.Normal.Y);
            if (c == 0) c = a.Normal.Z.CompareTo(b.Normal.Z);
            return c < 0 || (c == 0 && a.TriangleIndex < b.TriangleIndex);
        }
    }
}
