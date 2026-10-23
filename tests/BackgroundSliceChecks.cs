using System.Diagnostics;
using Purr2D2.Core;

internal static class BackgroundSliceChecks
{
    internal static void Run()
    {
        int mainThread = Environment.CurrentManagedThreadId, workerThread = mainThread;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var work = new BackgroundSliceWork<string, string>((cancel, publish) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            publish("plane ready"); entered.Set(); release.Wait();
            return "prepared pieces";
        });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "worker starts without a game-thread join");
        Check(workerThread != mainThread && work.Plan == "plane ready", "plane is safely published before expensive export finishes");
        for (int i = 0; i < 10000; i++) Check(!work.TryTake(out _, out _), "polling never waits for unfinished work");
        long start = Stopwatch.GetTimestamp(); work.Cancel();
        Check(Stopwatch.GetElapsedTime(start).TotalMilliseconds < 100 && !work.Finished, "cancel does not join an uncooperative worker");
        release.Set();
        Check(SpinWait.SpinUntil(() => work.Finished, TimeSpan.FromSeconds(5)), "cancelled worker finishes independently");
        Check(work.TryTake(out var stale, out var cancellation) && stale == null && cancellation != null, "late result cannot survive cancellation");
        var failed = new BackgroundSliceWork<string, string>((_, _) => throw new InvalidDataException("fixture failure"));
        Check(SpinWait.SpinUntil(() => failed.Finished, TimeSpan.FromSeconds(5)), "worker failures complete");
        Check(failed.TryTake(out _, out var error) && error == "fixture failure", "worker failure is observed and reported without throwing into frame loop");
        Console.WriteLine("PASS: nonblocking background publication, cancellation, stale-result rejection and failure handoff");
    }
    private static void Check(bool result, string message) { if (!result) throw new Exception("Background slice: " + message); }
}
