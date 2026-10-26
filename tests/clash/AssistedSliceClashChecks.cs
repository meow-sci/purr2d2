using Brutal.ImGuiApi;
using KSA;
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

        Console.WriteLine("PASS: assisted slicing clash gates and accepted worker pumping");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assisted slice clash check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
