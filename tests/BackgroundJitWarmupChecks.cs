using System.Diagnostics;
using Purr2D2.Core;

internal static class BackgroundJitWarmupChecks
{
    internal static void Run()
    {
        OnceAndPublication();
        FailureReleasesReadiness();
        CancellationNeverJoins();
        Console.WriteLine("PASS: one-shot JIT warmup concurrency, completion publication, failure fallback and nonblocking unload");
    }

    private static void OnceAndPublication()
    {
        int calls = 0, reports = 0, workerThread = 0, mainThread = Environment.CurrentManagedThreadId;
        string? published = null, summary = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var warmup = new BackgroundJitWarmup(_ =>
        {
            Interlocked.Increment(ref calls); workerThread = Environment.CurrentManagedThreadId;
            entered.Set(); release.Wait(); published = "all selected methods attempted"; return "prepared fixture";
        }, message => { summary = message; Interlocked.Increment(ref reports); });
        Parallel.For(0, 64, _ => warmup.Start());
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "the asynchronous attempt starts");
        Check(calls == 1 && workerThread != mainThread, "concurrent starts schedule exactly one worker");
        for (int i = 0; i < 10000; i++) Check(!warmup.Completed, "readiness waits for the complete attempt without joining it");
        release.Set(); Wait(warmup);
        warmup.Start(); warmup.Start();
        Check(published == "all selected methods attempted" && calls == 1 && reports == 1 && summary!.Contains("prepared fixture"),
            "completion safely publishes the result and the one-time summary without retrying");
    }

    private static void FailureReleasesReadiness()
    {
        int calls = 0, reports = 0; string? summary = null;
        using var warmup = new BackgroundJitWarmup(_ =>
        { Interlocked.Increment(ref calls); throw new MissingMethodException("fixture changed signature"); },
            message => { summary = message; Interlocked.Increment(ref reports); });
        warmup.Start(); Wait(warmup); warmup.Start();
        Check(calls == 1 && reports == 1 && summary!.Contains("normal JIT fallback") && summary.Contains("changed signature"),
            "a failed attempt releases readiness and reports a visible one-time fallback");

        using var badLog = new BackgroundJitWarmup(_ => throw new InvalidDataException("fixture failure"), _ => throw new IOException("log unavailable"));
        badLog.Start(); Wait(badLog);
        Check(badLog.Completed, "a failed report sink cannot fault the worker or strand readiness");
    }

    private static void CancellationNeverJoins()
    {
        int calls = 0, reports = 0; string? summary = null;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var warmup = new BackgroundJitWarmup(_ =>
        { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return "late preparation"; },
            message => { summary = message; Interlocked.Increment(ref reports); });
        warmup.Start(); Check(entered.Wait(TimeSpan.FromSeconds(5)), "the cancellable attempt starts");
        long before = Stopwatch.GetTimestamp(); warmup.Dispose();
        Check(Stopwatch.GetElapsedTime(before).TotalMilliseconds < 100 && !warmup.Completed,
            "unload returns before an uncooperative in-flight preparation finishes");
        warmup.Start(); release.Set(); Wait(warmup); warmup.Dispose();
        Check(calls == 1 && reports == 1 && summary!.Contains("cancelled on unload"), "late cancellation reports once and cannot restart");

        using var notStarted = new BackgroundJitWarmup(_ => throw new Exception("must not start"), _ => { });
        notStarted.Dispose(); notStarted.Start();
        Check(notStarted.Completed, "unloading before scheduling safely releases the attempt without starting work");
    }

    private static void Wait(BackgroundJitWarmup warmup) => Check(SpinWait.SpinUntil(() => warmup.Completed, TimeSpan.FromSeconds(5)), "the worker completes independently");
    private static void Check(bool result, string message) { if (!result) throw new Exception("JIT warmup: " + message); }
}
