using Purr2D2.Cutting;
using Purr2D2.Slicing.Detection;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static CachedRibbonGeometry.Progress Finish(CachedRibbonGeometry.Query q, int nodes = 4096, int triangles = 4096)
{
    CachedRibbonGeometry.Progress progress;
    int iterations = 0;
    do { progress = q.Advance(new(nodes, triangles)); Check(++iterations < 10000, "query must finish"); }
    while (progress.Status == CachedRibbonGeometry.QueryStatus.Pending);
    return progress;
}
var wall = new CutTriangle(new(0, -.2, -.2), new(0, .2, -.2), new(0, 0, .2));
var sweep = new BladeSweep(new(-.1, -1, 0), new(-.1, 1, 0), new(.1, -1, 0), new(.1, 1, 0));
var original = new[] { wall };
var geometry = new CachedRibbonGeometry(original);
original[0] = new(new(50, 0, 0), new(50, 1, 0), new(50, 0, 1));
var result = Finish(geometry.Begin(sweep));
Check(result.Status == CachedRibbonGeometry.QueryStatus.Complete && result.Best is { } hit
    && Math.Abs(hit.Time - .5) < 1e-8 && hit.TriangleIndex == 0, "captured geometry stays independent and confirms surface");
var query = geometry.Begin(sweep);
var paused = query.Advance(new(0, 0));
Check(paused.Status == CachedRibbonGeometry.QueryStatus.Pending && paused.Best is null, "zero budget is incomplete");
Check(Finish(query, 1, 1).Best.HasValue, "resumed cursor completes");
var invalid = geometry.Begin(new BladeSweep(default, new(0, 1, 0), new(1, 0, 0), new(1, 1, 1), 1e-20));
Check(Finish(invalid).Status == CachedRibbonGeometry.QueryStatus.UnsupportedMotion, "unrepresentable motion is not a miss");
Check(Finish(geometry.Begin(new BladeSweep(new(double.NaN, 0, 0), default, default, default))).Status
    == CachedRibbonGeometry.QueryStatus.UnsupportedMotion, "invalid motion is explicit");
Check(Finish(new CachedRibbonGeometry([]).Begin(sweep)).Status == CachedRibbonGeometry.QueryStatus.Complete, "empty geometry completes");

// Compare many target-local fixtures to a direct full scan using the same kernel.
var random = new Random(217);
for (int fixture = 0; fixture < 100; fixture++)
{
    var faces = new CutTriangle[90];
    for (int i = 0; i < faces.Length; i++)
    {
        double x = random.NextDouble() * 4 - 2, y = random.NextDouble() * 4 - 2;
        faces[i] = new(new(x, y, -.2), new(x, y + .3, -.2), new(x, y + .15, .2));
    }
    var tree = new CachedRibbonGeometry(faces);
    var s = new BladeSweep(new(-2, -1, 0), new(-2, 1, 0), new(2, -1, 0), new(2, 1, 0));
    double bestTime = double.PositiveInfinity, bestAlong = double.PositiveInfinity;
    foreach (var face in faces)
        if (s.TryHit(face, out _, out double along, out double time)
            && (time < bestTime - 1e-10 || (Math.Abs(time - bestTime) <= 1e-10 && along < bestAlong - 1e-10)))
        { bestTime = time; bestAlong = along; }
    var actual = Finish(tree.Begin(s), 3, 2);
    Check(actual.Status == CachedRibbonGeometry.QueryStatus.Complete && actual.Best.HasValue == double.IsFinite(bestTime), "tree and scan agree on presence");
    if (actual.Best is { } found)
        Check(Math.Abs(found.Time - bestTime) < 1e-9 && Math.Abs(found.Along - bestAlong) < 1e-9, "tree and scan agree on earliest hit");
}
var targetRelative = new BladeSweep(new(-2, 0, 0), new(-1, 0, 0), new(1, 0, 0), new(2, 0, 0));
Check(Finish(geometry.Begin(targetRelative)).Best is { } axial && Math.Abs(axial.Time - 1d / 3) < 1e-8,
    "target-local axial sweep uses existing degenerate ribbon handling");
Console.WriteLine("Cached ribbon checks passed.");

var pieces = new List<(BladeSweep Sweep, double Start, double End)>(16);
Check(sweep.TrySplit(pieces) && pieces.Count == 1 && pieces[0] == (sweep, 0d, 1d),
    "representable sweep keeps its complete original interval");
Check(!new BladeSweep(default, new(0, 1, 0), new(1, 0, 0), new(1, 1, 1), 1e-20).TrySplit(pieces)
    && pieces.Count == 0, "unsupported splitting clears a reused output instead of exposing a partial interval");
Check(!new BladeSweep(new(double.NaN, 0, 0), default, default, default).TrySplit(pieces)
    && pieces.Count == 0, "nonfinite motion cannot produce split intervals");
bool checkedBisect = false;
foreach (double tolerance in new[] { .0005, .001, .002, .004, .008, .016 })
{
    var twisted = new BladeSweep(new(-1, -1, 0), new(-1, 1, 0), new(1, -1, 0), new(1, 1, 1), tolerance);
    if (twisted.Subdivisions <= BladeSweep.MaximumSubdivisions) continue;
    bool split = twisted.TrySplit(pieces);
    var bisected = Finish(geometry.Begin(twisted), 2, 1);
    Check(split == (bisected.Status != CachedRibbonGeometry.QueryStatus.UnsupportedMotion),
        "shared splitting and cached query agree on supported motion");
    if (!split) continue;
    Check(pieces.Count is > 1 and <= 16 && pieces[0].Start == 0 && pieces[^1].End == 1
        && pieces.All(p => p.Sweep.Subdivisions <= BladeSweep.MaximumSubdivisions)
        && pieces.Zip(pieces.Skip(1)).All(pair => pair.First.End == pair.Second.Start
            && pair.First.Sweep.Base1 == pair.Second.Sweep.Base0 && pair.First.Sweep.Tip1 == pair.Second.Sweep.Tip0),
        "shared split remains bounded, chronological and continuous in original time");
    double referenceTime = double.PositiveInfinity, referenceAlong = double.PositiveInfinity;
    for (int i = 0; i < 16; i++)
    {
        double t0 = i / 16d, t1 = (i + 1) / 16d;
        CutPoint Lerp(CutPoint a, CutPoint b, double t) => a + (b - a) * t;
        var piece = new BladeSweep(Lerp(twisted.Base0, twisted.Base1, t0), Lerp(twisted.Tip0, twisted.Tip1, t0),
            Lerp(twisted.Base0, twisted.Base1, t1), Lerp(twisted.Tip0, twisted.Tip1, t1), tolerance);
        Check(piece.Subdivisions <= BladeSweep.MaximumSubdivisions, "reference subinterval is representable");
        if (piece.TryHit(wall, out _, out double along, out double time))
        {
            double global = t0 + (t1 - t0) * time;
            if (global < referenceTime - 1e-10 || (Math.Abs(global - referenceTime) <= 1e-10 && along < referenceAlong))
            { referenceTime = global; referenceAlong = along; }
        }
    }
    // Different valid subdivisions approximate a twisted ribbon differently, so
    // verify the contact is in its original time range rather than exact equality.
    Check(bisected.Best.HasValue == double.IsFinite(referenceTime), "bisected motion preserves contact");
    if (bisected.Best is { } b) Check(Math.Abs(b.Time - referenceTime) < .01, "bisected time returns to original interval");
    checkedBisect = true;
    break;
}
Check(checkedBisect, "fixture exercises successful motion bisection");
Console.WriteLine("Bisection checks passed.");

var registry = new SliceGeometryRevisionRegistry();
object mesh = new(), positions = new();
var key = new CachedRibbonStore.Key(mesh, positions, registry.Epoch, registry.Revision(mesh));
using (var store = new CachedRibbonStore())
{
    Check(store.TryBuild(key, [wall]), "cache accepts captured geometry");
    Check(!store.TryBuild(key, [wall]), "same identity has only one pending build");
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (store.PendingCount > 0 && DateTime.UtcNow < deadline) { store.Drain(); Thread.Yield(); }
    Check(store.PendingCount == 0 && store.EntryCount == 1 && store.StorageBytes > 0, "worker publishes accounted geometry");
    using var lease = store.Acquire(key);
    Check(lease != null && Finish(lease.Geometry.Begin(sweep)).Best.HasValue, "cache lease returns copied mesh");
    registry.Invalidate(mesh);
    store.Invalidate(k => ReferenceEquals(k.Mesh, mesh));
    Check(store.Acquire(key) == null && store.EntryCount == 1, "invalidation hides but retains active lease");
    var replacement = new CachedRibbonStore.Key(mesh, positions, registry.Epoch, registry.Revision(mesh));
    Check(store.TryBuild(replacement, [wall]), "revision allows replacement build");
    while (store.PendingCount > 0 && DateTime.UtcNow < deadline) { store.Drain(); Thread.Yield(); }
    using var next = store.Acquire(replacement);
    Check(next != null, "new revision is cached");
    next!.Dispose();
    lease!.Dispose();
    store.Invalidate(k => k.Epoch == registry.Epoch);
    Check(store.EntryCount == 0 && store.StorageBytes == 0, "released invalid entries free accounting");
    registry.NewScene();
    Check(registry.Epoch == 1 && registry.Revision(mesh) == 0, "scene advances epoch and drops old identities");
}
Console.WriteLine("Cache and revision checks passed.");

var primitives = new[] { new[] { wall.A, wall.B, wall.C, wall.A, wall.B, wall.C }, Array.Empty<CutPoint>() };
var capture = new CachedRibbonCapture<CutPoint>(primitives, p => p);
int totalCopied = 0, totalReads = 0;
CachedRibbonCapture<CutPoint>.Progress captureProgress;
do
{
    captureProgress = capture.Advance(1, 1, () => true);
    Check(captureProgress.PrimitiveReads <= 1 && captureProgress.TrianglesCopied <= 1, "copy respects shared operation budgets");
    totalCopied += captureProgress.TrianglesCopied; totalReads += captureProgress.PrimitiveReads;
} while (captureProgress.Status == CachedRibbonCapture<CutPoint>.State.Pending);
Check(captureProgress.Status == CachedRibbonCapture<CutPoint>.State.Complete && totalCopied == 2 && totalReads >= 4,
    "bounded primitive count and copying complete incrementally");
var captured = capture.Take();
primitives[0][0] = new(100, 0, 0);
Check(captured[0].A == wall.A, "completed capture no longer aliases native source");
var staleCapture = new CachedRibbonCapture<CutPoint>(primitives, p => p);
Check(staleCapture.Advance(1, 1, () => false).Status == CachedRibbonCapture<CutPoint>.State.Invalid,
    "stale source cannot submit a worker build");
Console.WriteLine("Capture checks passed.");
