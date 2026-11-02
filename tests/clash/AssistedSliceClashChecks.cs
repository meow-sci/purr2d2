using Brutal.ImGuiApi;
using KSA;
using Brutal.Numerics;
using Purr2D2.Runtime;

internal static class AssistedSliceClashChecks
{
    internal static void Run()
    {
        var runtime = new SaberRuntime();
        runtime.HoldClashForTest(true);
        runtime.BeginSelectionForTest();
        runtime.BeginSliceForTest(SaberRuntime.NewTargetForTest());
        Check(!runtime.SelectingForTest && !runtime.SliceSequenceActiveForTest
            && runtime.SliceBeginCountForTest == 0,
            "a clash blocks new assisted selection, click acceptance, and slice worker startup");

        runtime = new SaberRuntime();
        runtime.BeginSelectionForTest();
        runtime.HoldClashForTest(true);
        Program.DrawUI = true;
        ImGui.RightClick = true;
        runtime.HandleSlicePointerForTest();
        ImGui.RightClick = false;
        Program.DrawUI = false;
        Check(runtime.QueuedCommandsForTest == 1,
            "selection cancellation remains available during hit-stop when the UI owns the pointer");

        runtime = new SaberRuntime();
        var target = SaberRuntime.NewTargetForTest();
        runtime.AcceptSliceBeforeClashForTest(target);
        Check(runtime.SliceSequenceActiveForTest && !runtime.SliceStartedForTest,
            "a click accepted before hit-stop retains its pending transaction state");
        runtime.PumpSliceForTest(0);
        Check(!runtime.SliceStartedForTest && runtime.SliceBeginCountForTest == 0,
            "hit-stop defers a newly starting worker for an already accepted click");
        runtime.HoldClashForTest(false);
        runtime.PumpSliceForTest(.016);
        Check(runtime.SliceStartedForTest && runtime.SliceBeginCountForTest == 1,
            "the accepted slice starts once the clash hold ends");

        runtime = new SaberRuntime();
        runtime.MarkSliceAlreadyStartedForTest(SaberRuntime.NewTargetForTest());
        runtime.SetSliceReadyForTest(true);
        runtime.HoldClashForTest(true);
        runtime.PumpSliceForTest(0);
        Check(runtime.SliceAdvanceCountForTest == 1 && runtime.SliceSequenceActiveForTest
            && runtime.SliceCommitCountForTest == 0,
            "an accepted worker keeps pumping during hit-stop while its visual sequence and native commit wait");

        Program.ActiveCamera = new();
        target = SaberRuntime.NewTargetForTest() with { HitAssembly = new double3(0, 10, 0) };
        runtime = new SaberRuntime();
        runtime.BeginPointerSliceForTest(target, clutterDistance: 2, rejectPart: true);
        Check(runtime.ClutterStartsForTest == 1 && !runtime.SliceSequenceActiveForTest,
            "foreground clutter wins even when the native hovered vessel behind it is refused");
        runtime = new SaberRuntime();
        runtime.BeginPointerSliceForTest(target, clutterDistance: 20);
        Check(runtime.ClutterStartsForTest == 0 && runtime.SliceSequenceActiveForTest,
            "a foreground vessel retains selection priority over clutter behind it");
        runtime = new SaberRuntime();
        runtime.BeginPointerSliceForTest(target, clutterDistance: 20, rejectPart: true);
        Check(runtime.ClutterStartsForTest == 0 && !runtime.SliceSequenceActiveForTest,
            "a refused foreground vessel cannot authorize a cut through to background clutter");

        foreach (string blocker in new[] { "Unsupported foreground clutter.", "Foreground clutter beyond reach.",
            "Foreground capture refused.", "Terrain blocks the ray.", "Kitten blocks the ray.",
            "Native query budget exhausted.", "Nearest query failed.", "Another vessel is nearer." })
        {
            runtime = new SaberRuntime();
            runtime.BeginPointerSliceForTest(target, clutterDistance: 2, blocker: blocker);
            runtime.PumpSliceForTest(.016);
            Check(runtime.ClutterStartsForTest == 0 && !runtime.SliceSequenceActiveForTest
                && runtime.SliceBeginCountForTest == 0 && runtime.SelectingForTest && runtime.SliceStatusForTest.Contains(blocker),
                "hovered background part stays intact after blocked result: " + blocker);
            runtime = new SaberRuntime();
            runtime.BeginDeferredPointerSliceForTest(target, clutterDistance: 2, blocker: blocker);
            runtime.PumpSliceForTest(.016);
            Check(runtime.ClutterStartsForTest == 0 && !runtime.SliceSequenceActiveForTest
                && runtime.SliceBeginCountForTest == 0 && runtime.SelectingForTest && runtime.SliceStatusForTest.Contains(blocker),
                "completed background selection stays intact after blocked result: " + blocker);
        }
        runtime = new SaberRuntime();
        runtime.BeginDeferredPointerSliceForTest(target, clutterDistance: 2);
        Check(runtime.ClutterStartsForTest == 1 && !runtime.SliceSequenceActiveForTest,
            "accepted foreground clutter wins after completed background part selection");
        runtime = new SaberRuntime();
        runtime.BeginDeferredPointerSliceForTest(target, clutterDistance: 20);
        runtime.PumpSliceForTest(.016);
        Check(runtime.ClutterStartsForTest == 0 && runtime.SliceSequenceActiveForTest && runtime.SliceBeginCountForTest == 1,
            "confirmed absent foreground clutter preserves unobstructed deferred part slicing");
        runtime = new SaberRuntime();
        runtime.BeginDeferredPointerSliceForTest(target, clutterDistance: 2, searchComplete: false);
        Check(runtime.ClutterAttemptsForTest == 0 && runtime.SliceBeginCountForTest == 0 && !runtime.SliceSequenceActiveForTest,
            "incomplete part discovery authorizes neither foreground capture nor a background part cut");
        runtime = new SaberRuntime();
        runtime.BeginPointerSliceForTest(target, clutterDistance: 20, snapshotUnavailable: true);
        Check(runtime.ClutterAttemptsForTest == 0 && runtime.SliceBeginCountForTest == 0 && !runtime.SliceSequenceActiveForTest,
            "failed hover ray snapshot cannot bypass native occlusion validation");
        runtime = new SaberRuntime();
        runtime.BeginDeferredPointerSliceForTest(target, clutterDistance: 20, snapshotUnavailable: true);
        Check(runtime.ClutterAttemptsForTest == 0 && runtime.SliceBeginCountForTest == 0 && !runtime.SliceSequenceActiveForTest,
            "failed deferred ray snapshot cannot bypass native occlusion validation");
        Program.ActiveCamera = null;

        Console.WriteLine("PASS: assisted slicing clash gates and accepted worker pumping");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assisted slice clash check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
