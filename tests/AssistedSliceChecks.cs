using Purr2D2.Core;
using Purr2D2.Cutting;

internal static class AssistedSliceChecks
{
    internal static void Run()
    {
        foreach (int fps in new[] { 30, 60, 144 })
        {
            var timing = new AssistedSliceSequence(); timing.Start();
            Check(timing.Advance(1.0 / fps, true, true, false) == SliceCue.BeginSwing, "stroke starts before any background result exists");
            int frames = 0;
            while (timing.Progress < 1 && frames < fps * 2) { timing.Advance(1.0 / fps, true, true, false); frames++; }
            Check(frames / (double)fps >= 1.24 && frames / (double)fps < 1.24 + 1.01 / fps,
                "assisted slicing takes 1.24 seconds at half speed independently of preparation latency or frame rate");
        }
        Check(SaberSwing.Duration == .62 && AssistedSliceSequence.SwingSeconds == 1.24,
            "ordinary swing retains .62 seconds while target slicing takes twice as long");
        Check(SaberSwing.Angle(0) == 0 && SaberSwing.Angle(1) == 0
            && Math.Abs(SaberSwing.Angle(.25) - 1.05) < 1e-6 && Math.Abs(SaberSwing.Angle(.75) + 1.05) < 1e-6,
            "normal swing keeps the original swipe and full recovery without an initial snap");
        var action = new AssistedSliceSequence();
        action.Start();
        for (int i = 0; i < 30; i++) Check(action.Advance(.1, false, true, true) == SliceCue.None, "waits for ignition");
        Check(action.Advance(.02, true, true, true) == SliceCue.BeginSwing, "begins a visible stroke");
        double before = action.Progress;
        for (int i = 0; i < 40; i++)
        {
            Check(action.Advance(0, true, true, true) == SliceCue.None, "pause cannot commit");
            Check(action.Advance(.1, true, false, true) == SliceCue.None, "stale/hidden pose cannot commit");
        }
        Check(action.Progress == before, "suspended motion does not skip the animation");
        for (int i = 0; i < 100; i++) Check(action.Advance(.02, true, true, true) != SliceCue.Commit, "ready result cannot skip presentation of final pose");
        Check(action.Progress == 1 && action.Active && !action.Swinging, "full swing finishes before breakup");
        action.NotifyRendered();
        Check(action.Advance(.02, true, true, true) == SliceCue.Commit, "ready result commits on first update after final rendered pose");
        for (int i = 0; i < 100; i++) Check(action.Advance(.02, true, true, true) == SliceCue.None, "exactly one structural commit");
        action.Start();
        for (int i = 0; i < 100; i++) { action.Advance(.02, true, true, false); action.NotifyRendered(); }
        Check(action.EndPresented && action.Active && action.Progress == 1, "slow worker never prevents the full animation");
        Check(action.Advance(.02, true, true, true) == SliceCue.Commit, "late result commits immediately when ready");
        action.Start(); action.Advance(.02, true, true, true); action.Advance(.1, true, true, true); action.Cancel();
        for (int i = 0; i < 100; i++) Check(action.Advance(.1, true, true, true) == SliceCue.None, "cancel removes pending cut");
        action.Start();
        Check(action.Advance(double.NaN, true, true, true) == SliceCue.None, "invalid frame time cannot advance");

        SliceVertex V(double x, double y, double z) => new(new(x, y, z), new(0, 0, 1), 0, 0);
        SliceFace[][] source = [[new(V(10, -2, -1), V(18, 2, -1), V(18, 2, 1), 0)]];
        CutPoint[] axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        var planes = AssistedSliceGeometry.Candidates(source, axes, 17).ToArray();
        Check(planes.Length is > 0 and <= 15 && planes.All(p => p.Normal.Finite && Math.Abs(p.Normal.Length - 1) < 1e-10
            && double.IsFinite(p.Offset)), "bounded candidate sections have finite unit normals");
        var points = source.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).ToArray();
        Check(planes.All(p => points.Min(v => CutPoint.Dot(v, p.Normal)) < p.Offset
            && points.Max(v => CutPoint.Dot(v, p.Normal)) > p.Offset), "all candidates cross the actual source bounds");
        Check(planes.SequenceEqual(AssistedSliceGeometry.Candidates(source, axes, 17))
            && !planes.SequenceEqual(AssistedSliceGeometry.Candidates(source, axes, 18)),
            "per-stroke seeds give reproducible but distinct candidate sections");
        Check(planes.Any(p => Math.Abs(p.Normal.X) > .01 && Math.Abs(p.Normal.Y) > .01),
            "initial candidate sections include visible oblique cuts");
        Check(!AssistedSliceGeometry.Candidates([], [new(1, 0, 0)]).Any(), "empty geometry has no candidate cut");
        Console.WriteLine("PASS: assisted slice animation, cancellation, exactly-once commit and plane selection");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Assisted slice: " + message); }
}
