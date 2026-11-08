using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
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
            HeldTargetNeverCommitsOrTopples();
            AlternativeFailureNeverTopples();
            CommittedCutSurvivesEffectFaults();
            UnexpectedPostCommitFaultRetainsSuccess();
            OwnerAndTargetChangesCancel();
            CancellationDoesNotJoinWorker();
            QueueIsBoundedAndKeepsAcceptedWork();
            QueueMemoryAdmissionAndRetirement();
            QueueMemorySkipsTemporarilyBlockedIntents();
            QueueReservesNativeVehicleSlots();
            QueueCancellationRetiresWithoutJoiningWorker();
            QueueDisposeDoesNotJoinBlockedWorker();
            SerialJobDiagnostics();
        });
        Console.WriteLine("PASS: production clutter slicing orchestration: deferred native commits, truthful fallback, committed-cut fault containment, bounded effects, stale-scene cancellation and nonblocking retirement");
    }

    private static void HeldTargetNeverCommitsOrTopples()
    {
        foreach (string phase in new[] { "capture", "worker", "upload" })
        {
            ClutterScenario.Reset();
            using var slicer = new ClutterSlicer(); var owner = new KittenEva(); var target = new ClutterForceTarget();
            Start(slicer, owner, target);
            if (phase == "worker")
                Check(SpinWait.SpinUntil(() => { slicer.Update(owner, false, 1, 0); return ClutterScenario.WorkerThread != 0; }, TimeSpan.FromSeconds(5)),
                    "worker fixture starts before Furce captures target");
            if (phase == "upload")
                Check(SpinWait.SpinUntil(() => { slicer.Update(owner, false, 1, 0); return ClutterScenario.UploadAdvances > 0; }, TimeSpan.FromSeconds(5)),
                    "upload fixture prepares before Furce captures target");
            target.Held = true;
            slicer.Update(owner, true, 2, 0);
            Check(!slicer.Active && ClutterScenario.Replacements == 0 && ClutterScenario.Topples == 0
                && slicer.Status.Contains("held by Furce"), phase + " held target cancels without replacement or toppling");
        }
    }

    private static void SerialJobDiagnostics()
    {
        SliceJobDiagnostics.Reset(); DebugTimings.Enabled = true;
        try
        {
            ClutterScenario.Reset(); var owner = new KittenEva();
            // These external boundaries are tiny no-ops. Ensure each consumes a
            // stopwatch tick so the production >0 timing assertions do not depend
            // on clock granularity or whether the fixture methods were tiered.
            ClutterScenario.TimedBoundary = () =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                while (System.Diagnostics.Stopwatch.GetTimestamp() == started) Thread.SpinWait(1);
            };
            using (var slicer = new ClutterSlicer())
            {
                Check(slicer.Begin(owner, new(), default, double3.UnitY, double3.UnitX,
                    diagnosticDetector: "legacy-rays", diagnosticTime: 1), "serial contact job starts with diagnostics");
                Check(SpinWait.SpinUntil(() => { slicer.Update(owner, true, 1.25, 0); return !slicer.Active; }, TimeSpan.FromSeconds(5)),
                    "serial contact job completes");
                var sample = SliceJobDiagnostics.Latest;
                Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.SerialStarted) == 1
                    && SliceJobDiagnostics.CountFor(SliceJobOutcome.Queued) == 0
                    && SliceJobDiagnostics.CountFor(SliceJobOutcome.MeshCut) == 1
                    && SliceJobDiagnostics.CountForMode(SliceJobOutcome.MeshCut, "serial") == 1
                    && SliceJobDiagnostics.CountForMode(SliceJobOutcome.MeshCut, "queued") == 0
                    && sample is { Mode: "serial", Detector: "legacy-rays", Outcome: SliceJobOutcome.MeshCut }
                    && Math.Abs(sample.Value.PlayerLatencyMs - 250) < .001
                    && sample.Value.CaptureMs > 0 && sample.Value.GeometryMs > 0 && sample.Value.LoaderMs > 0
                    && sample.Value.UploadMs > 0 && sample.Value.CommitMs > 0,
                    "serial mesh cut is timed and counted once without a queued admission: " + sample
                    + "; " + SliceJobDiagnostics.Describe());
            }
            Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.Cancelled) == 0,
                "cleanup after a successful serial cut does not add a cancellation");
            ClutterScenario.Reset(); ClutterScenario.CaptureError = "fixture source refusal";
            using (var slicer = new ClutterSlicer())
            {
                Check(slicer.Begin(owner, new(), default, double3.UnitY, double3.UnitX,
                    trigger: "AssistedClutter", diagnosticDetector: "assisted-selection", diagnosticTime: 2), "assisted ground job starts");
                slicer.Update(owner, true, 2.1, 0);
                Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.SerialStarted) == 2
                    && SliceJobDiagnostics.CountFor(SliceJobOutcome.Toppled) == 1
                    && SliceJobDiagnostics.CountForMode(SliceJobOutcome.Toppled, "serial") == 1
                    && SliceJobDiagnostics.CountFor(SliceJobOutcome.MeshCut) == 1
                    && SliceJobDiagnostics.Latest is { Mode: "serial", Detector: "assisted-selection", Outcome: SliceJobOutcome.Toppled },
                    "assisted toppling remains distinct from a completed mesh cut");
            }
            ClutterScenario.Reset();
            using (var slicer = new ClutterSlicer())
            {
                Check(slicer.Begin(owner, new(), default, double3.UnitY, double3.UnitX,
                    diagnosticDetector: "legacy-rays", diagnosticTime: 3), "cancellable serial job starts");
                slicer.Cancel(); slicer.Cancel();
                Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.Cancelled) == 1,
                    "repeated serial cancellation records one terminal outcome");
            }
        }
        finally { DebugTimings.Enabled = false; SliceJobDiagnostics.Reset(); }
    }

    private static void AlternativeFailureNeverTopples()
    {
        foreach (string mode in new[] { "blade-strict", "blade-limited", "sweep-fit" })
        {
            ClutterScenario.Reset(); ClutterScenario.CaptureError = "fixture failure";
            using var slicer = new ClutterSlicer(); var owner = new KittenEva();
            Check(slicer.Begin(owner, new(), default, double3.UnitY, double3.UnitX, mode), "alternative cut starts");
            slicer.Update(owner, true, 1, 0);
            Check(!slicer.Active && ClutterScenario.Topples == 0 && ClutterScenario.Replacements == 0
                && slicer.Status.Contains("fixture failure"), "alternative failure reports rejection without toppling");
        }
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

    private static void QueueIsBoundedAndKeepsAcceptedWork()
    {
        ClutterScenario.Reset();
        SliceJobDiagnostics.Reset();
        using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerRelease = release;
        ClutterScenario.HoldUploads = true;
        using var queue = new ClutterSliceQueue(2, 16)
        { CutSparks = (_, _, _, _) => { }, TreeFire = (_, _, _, _, _) => { }, FragmentCreated = (_, _) => { } };
        var owner = new KittenEva();
        var target = new ClutterForceTarget();
        Check(queue.Begin(owner, target, default, double3.UnitY, double3.UnitX), "queue admits a target");
        Check(!queue.Begin(owner, target, default, double3.UnitY, double3.UnitX), "queue deduplicates an accepted identity");
        for (int i = 1; i < 16; i++)
            Check(queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX), "queue admits a distinct target within its limit");
        Check(queue.Count == 16 && !queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX), "queue enforces pending target limit");
        Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.Queued) == 16
            && SliceJobDiagnostics.CountFor(SliceJobOutcome.Deduplicated) == 1
            && SliceJobDiagnostics.CountFor(SliceJobOutcome.QueueFull) == 1,
            "queue counters distinguish admissions, duplicates and capacity refusals");
        int expectedWorkers = Math.Max(1, Math.Min(2, Environment.ProcessorCount - 2));
        try
        {
            Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 1, 0); return Volatile.Read(ref ClutterScenario.WorkersRunning) == expectedWorkers; }, TimeSpan.FromSeconds(5)),
                "the permitted worker count starts on independent accepted targets");
            var updateTimer = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) queue.Update(owner, false, 1, 0);
            Check(updateTimer.Elapsed.TotalMilliseconds < 100,
                "main-thread queue updates return while worker tasks remain blocked");
            Check(ClutterScenario.MaximumWorkers == expectedWorkers && ClutterScenario.Replacements == 0,
                "worker count is bounded and paused commits do not mutate the world");
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 2, 0); return ClutterScenario.UploadAdvances > 0; }, TimeSpan.FromSeconds(5)),
            "accepted work continues preparing without new input or commit permission");
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 2, 0); return ClutterScenario.UploadsLive > 0; }, TimeSpan.FromSeconds(5)),
            "the first detached upload can remain pending across frames");
        for (int i = 0; i < 30; i++) queue.Update(owner, false, 2, 0);
        Check(ClutterScenario.Replacements == 0 && ClutterScenario.UploadsLive == 1
            && ClutterScenario.MaximumUploads == 1,
            "prepared jobs wait for permission and only one upload remains live");
        ClutterScenario.HoldUploads = false;
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, true, 3, 0); return !queue.Active; }, TimeSpan.FromSeconds(10)),
            "accepted jobs drain after input release");
        Check(ClutterScenario.Replacements == 16 && ClutterScenario.Topples == 0 && ClutterScenario.MaximumUploads == 1,
            "each accepted target commits once and upload jobs are serialized");
        Check(SliceJobDiagnostics.CountFor(SliceJobOutcome.MeshCut) == 16
            && SliceJobDiagnostics.CountFor(SliceJobOutcome.Toppled) == 0,
            "completed mesh cuts are counted separately from displacement fallbacks");
        Check(!queue.Pending.Contains(target.StableKey)
            && queue.Begin(owner, target, default, double3.UnitY, double3.UnitX),
            "a fully retired identity can be accepted again");
    }

    private static void QueueMemoryAdmissionAndRetirement()
    {
        const long MiB = 1024 * 1024;
        ClutterScenario.Reset(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerEntered = entered.Set; ClutterScenario.WorkerRelease = release;
        using var queue = new ClutterSliceQueue(2, 16, 64);
        var owner = new KittenEva();
        var oversized = new ClutterForceTarget { EstimateBytes = 65 * MiB };
        var large = new ClutterForceTarget { EstimateBytes = 60 * MiB, RetainedBytes = 70 * MiB };
        var small = new ClutterForceTarget { EstimateBytes = 4 * MiB };
        Check(queue.Begin(owner, oversized, default, double3.UnitY, double3.UnitX)
            && queue.Begin(owner, large, default, double3.UnitY, double3.UnitX)
            && queue.Begin(owner, small, default, double3.UnitY, double3.UnitX), "memory fixtures admitted as intents");
        try
        {
            Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 1, 0); return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                "oversized preflight refuses without preventing later work");
            Check(!queue.Pending.Contains(oversized.StableKey) && queue.Status.Contains("SliceWorkingMemoryMiB"),
                "oversized source names the setting and releases its identity");
            for (int i = 0; i < 10; i++) queue.Update(owner, false, 1, 0);
            Check(queue.EstimatedBytes == 60 * MiB && queue.RetainedBytes == 70 * MiB && queue.ChargedBytes == 70 * MiB,
                "actual retained input blocks further admissions even above the estimate");
            queue.Cancel();
            Check(queue.EstimatedBytes == 60 * MiB && queue.ChargedBytes >= 70 * MiB,
                "cancellation keeps the reservation while the worker owns its input");
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => { queue.PumpRetired(); return !queue.Active; }, TimeSpan.FromSeconds(5)),
            "retired reservations eventually release");
        Check(queue.EstimatedBytes == 0 && queue.ChargedBytes == 0, "retirement releases all memory charges");
    }

    private static void QueueMemorySkipsTemporarilyBlockedIntents()
    {
        const long MiB = 1024 * 1024;
        ClutterScenario.Reset(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerEntered = entered.Set; ClutterScenario.WorkerRelease = release;
        using var queue = new ClutterSliceQueue(2, 16, 64);
        var owner = new KittenEva();
        Check(queue.Begin(owner, new ClutterForceTarget { EstimateBytes = 50 * MiB }, default, double3.UnitY, double3.UnitX)
            && queue.Begin(owner, new ClutterForceTarget { EstimateBytes = 20 * MiB }, default, double3.UnitY, double3.UnitX)
            && queue.Begin(owner, new ClutterForceTarget { EstimateBytes = 4 * MiB }, default, double3.UnitY, double3.UnitX),
            "waiting sources admitted in order");
        try
        {
            Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 1, 0); return entered.IsSet && queue.EstimatedBytes == 54 * MiB; }, TimeSpan.FromSeconds(5)),
                "a smaller source passes an earlier source that cannot currently fit");
            queue.Cancel();
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => { queue.PumpRetired(); return !queue.Active; }, TimeSpan.FromSeconds(5)),
            "all waiting and running jobs retire after cancellation");
    }

    private static void QueueReservesNativeVehicleSlots()
    {
        ClutterScenario.Reset(); ClutterScenario.HoldUploads = true;
        NativeSlicePhysics.VehicleLimit = 3;
        using var queue = new ClutterSliceQueue(2, 16);
        var owner = new KittenEva();
        Check(queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX)
            && queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX), "vehicle reservation fixtures admitted");
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, false, 1, 0); return queue.ReservedVehicles == 2 && ClutterScenario.UploadsLive == 1; }, TimeSpan.FromSeconds(5)),
            "one ready job reserves the available vehicle slots");
        for (int i = 0; i < 100; i++) queue.Update(owner, false, 1, 0);
        Check(queue.ReservedVehicles == 2 && ClutterScenario.MaximumUploads == 1,
            "other work cannot reserve the same vehicle slots");
        ClutterScenario.HoldUploads = false;
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, true, 2, 0); return !queue.Active; }, TimeSpan.FromSeconds(5)),
            "remaining job is refused when the first commit consumes the capacity");
        Check(ClutterScenario.Replacements == 1 && ClutterScenario.Topples == 0 && queue.ReservedVehicles == 0,
            "native capacity is rechecked without fallback or leaked reservations");
    }

    private static void QueueCancellationRetiresWithoutJoiningWorker()
    {
        ClutterScenario.Reset(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerEntered = entered.Set; ClutterScenario.WorkerRelease = release;
        using var queue = new ClutterSliceQueue(1, 16)
        { CutSparks = (_, _, _, _) => { }, TreeFire = (_, _, _, _, _) => { }, FragmentCreated = (_, _) => { } };
        var owner = new KittenEva(); var target = new ClutterForceTarget();
        Check(queue.Begin(owner, target, default, double3.UnitY, double3.UnitX), "cancellable queue accepts work");
        queue.Update(owner, false, 1, 0); queue.Update(owner, false, 1, 0); queue.Update(owner, false, 1, 0);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "queued worker reaches the controlled blocking point");
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); queue.Cancel();
            Check(elapsed.Elapsed.TotalMilliseconds < 100 && queue.Count == 1,
                "queue cancellation returns promptly while retaining unfinished work for retirement");
            Check(!queue.Begin(owner, target, default, double3.UnitY, double3.UnitX),
                "a cancelled identity remains deduplicated until its worker retires");
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => { queue.Update(owner, true, 2, 0); return queue.Count == 0; }, TimeSpan.FromSeconds(5)),
            "cancelled queue work retires after its worker exits");
        Check(ClutterScenario.Replacements == 0 && ClutterScenario.Topples == 0 && ClutterScenario.WorkersRunning == 0,
            "retired cancellation never mutates the scene or leaves a worker running");
    }

    private static void QueueDisposeDoesNotJoinBlockedWorker()
    {
        ClutterScenario.Reset(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ClutterScenario.WorkerEntered = entered.Set; ClutterScenario.WorkerRelease = release;
        var queue = new ClutterSliceQueue(1, 16)
        { CutSparks = (_, _, _, _) => { }, TreeFire = (_, _, _, _, _) => { }, FragmentCreated = (_, _) => { } };
        var owner = new KittenEva();
        Check(queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX), "disposable queue accepts work");
        queue.Update(owner, false, 1, 0); queue.Update(owner, false, 1, 0); queue.Update(owner, false, 1, 0);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "disposal fixture worker reaches the blocking point");
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); queue.Dispose();
            Check(elapsed.Elapsed.TotalMilliseconds < 100 && ClutterScenario.WorkersRunning == 1,
                "disposing the worker pool returns while its worker is still blocked");
        }
        finally { release.Set(); }
        Check(SpinWait.SpinUntil(() => Volatile.Read(ref ClutterScenario.WorkersRunning) == 0, TimeSpan.FromSeconds(5)),
            "the background worker exits after disposal cancellation is released");
        Check(ClutterScenario.Replacements == 0 && ClutterScenario.Topples == 0,
            "disposed queue work cannot mutate the scene");
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
