using Brutal.Numerics;
using KSA;
using Purr2D2.Force;
using Purr2D2.Slicing;

internal static class ClutterSlicerChecks
{
    internal static void Run()
    {
        CaptureConsole(() =>
        {
            FallbackWaitsForCommitPermission();
            PreparedSplitWaitsForCommitPermission();
            NativeFailureReportsItsOwnReason();
            FailedPreparationRoutesToActualFallback();
            CommittedCutSurvivesEffectFaults();
            UnexpectedPostCommitFaultRetainsSuccess();
            OwnerAndTargetChangesCancel();
            CancellationDoesNotJoinWorker();
        });
        Console.WriteLine("PASS: production clutter slicing orchestration: deferred native commits, truthful fallback, committed-cut fault containment, bounded effects, stale-scene cancellation and nonblocking retirement");
    }

    private static void PreparedSplitWaitsForCommitPermission()
    {
        ClutterScenario.Reset(); using var slicer = new ClutterSlicer(); var owner = new KittenEva(); Start(slicer, owner);
        Check(SpinWait.SpinUntil(() =>
        { slicer.Update(owner, false, 1, 10); return ClutterScenario.UploadAdvances > 0; }, TimeSpan.FromSeconds(5)),
            "CPU and detached upload preparation can finish while world mutation is deferred");
        for (int i = 0; i < 10; i++) slicer.Update(owner, false, 2, 10);
        Check(slicer.Active && ClutterScenario.Replacements == 0 && ClutterScenario.Topples == 0,
            "a prepared split cannot replace the original without commit permission");
        slicer.Update(owner, true, 3, 10);
        Check(!slicer.Active && ClutterScenario.Replacements == 1 && ClutterScenario.Topples == 0,
            "the first permitted settled update publishes exactly one prepared split");
    }

    private static void FallbackWaitsForCommitPermission()
    {
        ClutterScenario.Reset();
        const string diagnostic = "fixture opacity/transmission shader unsupported";
        ClutterScenario.CaptureError = diagnostic;
        using var slicer = new ClutterSlicer(); var owner = new KittenEva();
        Start(slicer, owner);
        var output = CaptureConsole(() =>
        {
            for (int i = 0; i < 10; i++) slicer.Update(owner, false, i, 10);
            Check(slicer.Active && ClutterScenario.Topples == 0 && ClutterScenario.Replacements == 0,
                "preparation failure cannot mutate native bodies without commit permission");
            Check(!slicer.Status.Contains("opacity") && !slicer.Status.Contains("failed"),
                "deferred interaction does not display a shader failure");
            slicer.Update(owner, true, 11, 10);
        });
        Check(!slicer.Active && ClutterScenario.Topples == 1 && ClutterScenario.CommitThread == ClutterScenario.MainThread,
            "resuming performs exactly one real native fallback on the settled caller thread");
        Check(slicer.Status == ClutterScenario.ToppleReason && !slicer.Status.Contains(diagnostic),
            "successful native toppling has a concise successful status");
        Check(output.Split(diagnostic).Length == 2, "the detailed capture failure is logged exactly once");
    }

    private static void NativeFailureReportsItsOwnReason()
    {
        ClutterScenario.Reset(); ClutterScenario.CaptureError = "fixture mesh unavailable";
        ClutterScenario.ToppleSucceeds = false; ClutterScenario.ToppleReason = "fixture native ownership changed";
        using var slicer = new ClutterSlicer(); var owner = new KittenEva(); Start(slicer, owner);
        slicer.Update(owner, true, 1, 10);
        Check(!slicer.Active && ClutterScenario.Topples == 1 && slicer.Status.Contains(ClutterScenario.ToppleReason)
            && slicer.Status.Contains("failed") && !slicer.Status.Contains("toppling"),
            "a native refusal cannot be reported as a completed cut or topple");
    }

    private static void FailedPreparationRoutesToActualFallback()
    {
        foreach (string phase in new[] { "worker", "upload", "replace" })
        {
            ClutterScenario.Reset();
            if (phase == "worker") ClutterScenario.WorkerError = "fixture worker refusal";
            if (phase == "upload") ClutterScenario.UploadError = "fixture upload refusal";
            if (phase == "replace") { ClutterScenario.ReplaceSucceeds = false; ClutterScenario.ReplaceReason = "fixture original retained"; }
            using var slicer = new ClutterSlicer(); var owner = new KittenEva(); Start(slicer, owner);
            Finish(slicer, owner);
            Check(ClutterScenario.Topples == 1 && slicer.Status == ClutterScenario.ToppleReason,
                phase + " failure really invokes native fallback before claiming success");
            slicer.PumpRetired();
            Check(ClutterScenario.UploadDisposals == (phase == "worker" ? 0 : 1),
                phase + " failure retires its detached resources");
        }
    }

    private static void CommittedCutSurvivesEffectFaults()
    {
        foreach (bool fireEnabled in new[] { true, false })
        {
            ClutterScenario.Reset(); ClutterScenario.MarkHotThrows = true;
            using var slicer = new ClutterSlicer(); var owner = new KittenEva(); int sparks = 0, fires = 0;
            slicer.CutSparks = (_, _, _, _) => { sparks++; throw new InvalidOperationException("spark fixture fault"); };
            slicer.TreeFire = (_, _, _, _, _) => fires++;
            Start(slicer, owner); Finish(slicer, owner, fireEnabled ? 10 : 0);
            Check(ClutterScenario.Replacements == 1 && ClutterScenario.Topples == 0
                && slicer.Status.StartsWith("Tree cut into 2 separating fragments."),
                "a committed split stays successful when optional bookkeeping and sparks throw");
            Check(ClutterScenario.HotMarks == 2 && sparks == 8 && fires == (fireEnabled ? 8 : 0),
                "each native fragment gets at most four independent spark/fire anchors and zero duration disables fire");
            Check(ClutterScenario.CaptureThread == ClutterScenario.MainThread
                && ClutterScenario.CommitThread == ClutterScenario.MainThread
                && ClutterScenario.WorkerThread != ClutterScenario.MainThread,
                "capture and world mutation stay on the caller while geometry runs on its real worker");
        }
    }

    private static void UnexpectedPostCommitFaultRetainsSuccess()
    {
        ClutterScenario.Reset(); ClutterScenario.SparkDataThrows = true;
        using var slicer = new ClutterSlicer(); var owner = new KittenEva(); Start(slicer, owner); Finish(slicer, owner);
        Check(ClutterScenario.Replacements == 1 && ClutterScenario.Topples == 0
            && slicer.Status.StartsWith("Tree cut into 2 separating fragments."),
            "an unexpected post-commit fault never retries the removed original or reports a refused cut");
    }

    private static void OwnerAndTargetChangesCancel()
    {
        foreach (bool changeOwner in new[] { true, false })
        {
            ClutterScenario.Reset();
            using var slicer = new ClutterSlicer(); var owner = new KittenEva(); var target = new ClutterForceTarget();
            Start(slicer, owner, target);
            if (!changeOwner) target.IsValid = false;
            slicer.Update(changeOwner ? new KittenEva() : owner, true, 1, 10);
            Check(!slicer.Active && ClutterScenario.Topples == 0 && ClutterScenario.Replacements == 0
                && ClutterScenario.CaptureDisposals == 1 && slicer.Status.Contains("cancelled"),
                "a changed owner/target cancels without touching the old scene");
        }
    }

    private static void CancellationDoesNotJoinWorker()
    {
        ClutterScenario.Reset(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerEntered = entered.Set; ClutterScenario.WorkerRelease = release;
        using var slicer = new ClutterSlicer(); var owner = new KittenEva(); Start(slicer, owner);
        slicer.Update(owner, true, 1, 10); slicer.Update(owner, true, 2, 10);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "the asynchronous geometry fixture starts");
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); slicer.Cancel();
            Check(elapsed.Elapsed.TotalMilliseconds < 100 && !slicer.Active && slicer.Busy,
                "cancellation returns while the worker is still blocked");
            Check(!slicer.Begin(owner, new(), default, double3.UnitY, double3.UnitX),
                "unfinished retired work blocks another cut");
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => { slicer.PumpRetired(); return !slicer.Busy; }, TimeSpan.FromSeconds(5)),
            "retired work drains without publishing its stale result");
        Check(ClutterScenario.Replacements == 0 && ClutterScenario.Topples == 0, "a late cancelled result cannot touch the world");
    }

    private static void Start(ClutterSlicer slicer, KittenEva owner, ClutterForceTarget? target = null) =>
        Check(slicer.Begin(owner, target ?? new(), default, double3.UnitY, double3.UnitX), "the identified local ground cut starts");
    private static void Finish(ClutterSlicer slicer, KittenEva owner, double seconds = 10)
    {
        Check(SpinWait.SpinUntil(() => { slicer.Update(owner, true, 1, seconds); return !slicer.Active; }, TimeSpan.FromSeconds(5)),
            "the nonblocking cut completes");
        slicer.PumpRetired();
    }
    private static string CaptureConsole(Action action)
    {
        var original = Console.Out; using var writer = new StringWriter(); Console.SetOut(writer);
        try { action(); return writer.ToString(); }
        finally { Console.SetOut(original); }
    }
    private static void Check(bool result, string message)
    { if (!result) throw new InvalidOperationException("Clutter runtime: " + message); }
}
