using System.Text.Json;
using Purr2D2.Cutting;
using Purr2D2.Slicing.Detection;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

var root = Path.Combine(Path.GetTempPath(), "purr2d2-slice-replay-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "capture.json");
    var points = new[]
    {
        new CutPoint(0, -.2, -.2), new CutPoint(0, .2, -.2), new CutPoint(0, 0, .2)
    };
    var sweep = new BladeSweep(new(-.1, -1, 0), new(-.1, 1, 0),
        new(.1, -1, 0), new(.1, 1, 0), .0025);
    var capture = new SliceReplayFixtureCapture<CutPoint>();
    Check(capture.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Idle,
        "runtime replay capture starts disabled");
    Check(capture.Arm(path) && capture.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Armed,
        "developer action arms one replay fixture");
    bool sourceCurrent = true;
    Check(capture.Offer([points], static p => p, sweep, .5, .5, true, "legacy-ribbon", () => sourceCurrent),
        "only a valid confirmed mesh interval is admitted to capture");
    capture.Advance(primitiveBudget: 1, triangleBudget: 1, millisecondBudget: 1000);
    Check(capture.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Copying,
        "mesh copy can span bounded settled updates");
    capture.Advance(primitiveBudget: 1, triangleBudget: 1, millisecondBudget: 1000);
    Check(SpinWait.SpinUntil(() => capture.State != SliceReplayFixtureCapture<CutPoint>.CaptureState.Writing, TimeSpan.FromSeconds(5))
        && capture.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Complete,
        "detached JSON writing completes asynchronously");
    var info = new FileInfo(path);
    using (var document = JsonDocument.Parse(File.ReadAllText(path)))
    {
        var fixture = document.RootElement;
        Check(info.Length <= SliceReplayFixtureCapture<CutPoint>.MaximumFixtureBytes
            && fixture.GetProperty("triangles").GetArrayLength() == 1
            && fixture.GetProperty("intervals").GetArrayLength() == 1,
            "saved fixture matches SliceReplay schema and remains within its file bound");
        Check(fixture.GetProperty("frame").GetString() == "hit-part-local"
            && fixture.GetProperty("source").GetString() == "runtime-copied-mesh"
            && fixture.GetProperty("detector").GetString() == "legacy-ribbon"
            && fixture.GetProperty("fullMeshScan").GetBoolean(),
            "fixture records non-identifying frame and evidence metadata");
    }

    var twisted = new BladeSweep(new(-1, -1, 0), new(-1, 1, 0), new(1, -1, 0), new(1, 1, 1),
        BladeSweep.ApproximationMeters / Math.Sqrt(3));
    var twistedPoints = new[] { new CutPoint(0, -2, -1), new CutPoint(0, 2, -1), new CutPoint(0, 0, 3) };
    var twistedPath = Path.Combine(root, "twisted.json");
    var twistedCapture = new SliceReplayFixtureCapture<CutPoint>();
    Check(twisted.Subdivisions > BladeSweep.MaximumSubdivisions && twistedCapture.Arm(twistedPath)
        && twistedCapture.Offer([twistedPoints], static p => p, twisted, .5, 0, true, "legacy-ribbon", static () => true),
        "one-shot capture accepts a confirmed twisted interval supported by bounded splitting");
    twistedCapture.Advance(primitiveBudget: 2, triangleBudget: 2, millisecondBudget: 1000);
    Check(SpinWait.SpinUntil(() => twistedCapture.State != SliceReplayFixtureCapture<CutPoint>.CaptureState.Writing, TimeSpan.FromSeconds(5))
        && twistedCapture.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Complete,
        "supported twisted contact produces its replay fixture");
    using (var document = JsonDocument.Parse(File.ReadAllText(twistedPath)))
    {
        var fixture = document.RootElement;
        var interval = fixture.GetProperty("intervals")[0];
        Check(fixture.GetProperty("intervals").GetArrayLength() == 1
            && interval.GetProperty("base0")[0].GetDouble() == -1 && interval.GetProperty("base1")[0].GetDouble() == 1
            && interval.GetProperty("tip1")[2].GetDouble() == 1
            && interval.GetProperty("tolerance").GetDouble() == twisted.Tolerance
            && fixture.GetProperty("hitTime01").GetDouble() == .5 && fixture.GetProperty("alongBlade01").GetDouble() == 0,
            "twisted capture preserves the original endpoints, tolerance and global hit coordinates");
    }
    var unsupported = new SliceReplayFixtureCapture<CutPoint>();
    Check(unsupported.Arm(Path.Combine(root, "unsupported.json"))
        && !unsupported.Offer([twistedPoints], static p => p,
            new(twisted.Base0, twisted.Tip0, twisted.Base1, twisted.Tip1, 1e-20), .5, 0, true, "legacy-ribbon", static () => true)
        && unsupported.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Failed,
        "one-shot capture still refuses motion beyond the shared split bound");

    var stale = new SliceReplayFixtureCapture<CutPoint>();
    Check(stale.Arm(Path.Combine(root, "stale.json"))
        && stale.Offer([points], static p => p, sweep, .5, .5, false, "cached-ribbon", () => sourceCurrent),
        "stale-source fixture starts copying while evidence is current");
    stale.Advance(primitiveBudget: 1, triangleBudget: 1, millisecondBudget: 1000);
    sourceCurrent = false;
    stale.Advance(primitiveBudget: 1, triangleBudget: 1, millisecondBudget: 1000);
    Check(stale.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Failed
        && !File.Exists(Path.Combine(root, "stale.json")),
        "source invalidation aborts capture before export");

    var oversized = new SliceReplayFixtureCapture<CutPoint>();
    Check(oversized.Arm(Path.Combine(root, "oversized.json")), "second developer request can be armed after completion");
    var tooMany = new CutPoint[(SliceReplayFixtureCapture<CutPoint>.MaximumTriangles + 1) * 3];
    Check(!oversized.Offer([tooMany], static p => p, sweep, .5, .5, true, "legacy-ribbon", static () => true)
        && oversized.State == SliceReplayFixtureCapture<CutPoint>.CaptureState.Failed
        && !File.Exists(Path.Combine(root, "oversized.json")),
        "triangle cap rejects oversized source without copying or writing a partial fixture");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
