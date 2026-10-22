using Purr2D2.Core;
using Purr2D2.Cutting;

internal static class AssistedSliceChecks
{
    internal static void Run()
    {
        var action = new AssistedSliceSequence();
        action.Start();
        for (int i = 0; i < 30; i++) Check(action.Advance(.1, false, true) == SliceCue.None, "waits for ignition");
        Check(action.Advance(.02, true, true) == SliceCue.BeginSwing, "begins a visible stroke");
        double before = action.Progress;
        for (int i = 0; i < 40; i++)
        {
            Check(action.Advance(0, true, true) == SliceCue.None, "pause cannot commit");
            Check(action.Advance(.1, true, false) == SliceCue.None, "stale/hidden pose cannot commit");
        }
        Check(action.Progress == before, "suspended motion does not skip the animation");
        int commits = 0;
        for (int i = 0; i < 100; i++) if (action.Advance(.02, true, true) == SliceCue.Commit) commits++;
        Check(commits == 1 && !action.Active, "exactly one commit even if the callback fails or more frames arrive");
        action.Start(); action.Advance(.02, true, true); action.Advance(.1, true, true); action.Cancel();
        for (int i = 0; i < 100; i++) Check(action.Advance(.1, true, true) == SliceCue.None, "cancel removes pending cut");
        action.Start();
        Check(action.Advance(double.NaN, true, true) == SliceCue.None, "invalid frame time cannot advance");

        SliceVertex V(double x, double y, double z) => new(new(x, y, z), new(0, 0, 1), 0, 0);
        SliceFace[][] source = [[new(V(10, -2, -1), V(18, 2, -1), V(18, 2, 1), 0)]];
        var planes = AssistedSliceGeometry.Candidates(source, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)]).ToArray();
        Check(planes.Length == 9 && planes[0] == new SlicePlane(new(1, 0, 0), 14), "center cut follows the longest part axis");
        Check(planes.All(p => p.Normal.Finite && double.IsFinite(p.Offset)), "bounded fallback cuts stay finite");
        Check(!AssistedSliceGeometry.Candidates([], [new(1, 0, 0)]).Any(), "empty geometry has no candidate cut");
        Console.WriteLine("PASS: assisted slice animation, cancellation, exactly-once commit and plane selection");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Assisted slice: " + message); }
}
