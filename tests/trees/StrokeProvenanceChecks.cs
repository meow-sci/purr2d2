using Brutal.Numerics;
using KSA;
using Purr2D2.Force;
using Purr2D2.Slicing;

internal static class StrokeProvenanceChecks
{
    internal static void Run()
    {
        ClutterScenario.Reset();
        var provenance = new SliceStrokeProvenance<Part>();
        provenance.Observe(42);
        var generated = new List<Part>();
        using var queue = new ClutterSliceQueue(1, 16)
        {
            FragmentCreated = (part, stroke) => { generated.Add(part); provenance.Mark(part, stroke); }
        };
        var owner = new KittenEva();
        if (!queue.Begin(owner, new ClutterForceTarget(), default, double3.UnitY, double3.UnitX, stroke: 42))
            throw new Exception("Clutter stroke provenance fixture could not admit its target.");
        if (!SpinWait.SpinUntil(() => { queue.Update(owner, true, 1, 0); return !queue.Active; }, TimeSpan.FromSeconds(10)))
            throw new Exception("Clutter stroke provenance fixture did not complete.");
        if (generated.Count != 2 || generated.Any(part => !provenance.Suppress(part, 42)))
            throw new Exception("Clutter fragment callbacks lost their original stroke or failed to suppress a vessel recut.");
        provenance.Observe(43);
        if (generated.Any(part => provenance.Suppress(part, 43)))
            throw new Exception("A later stroke must be allowed to recut an old fragment.");
        if (generated.Any(part => !provenance.Suppress(part, 42)))
            throw new Exception("A frozen contact from the released stroke must still suppress its own fragments.");
        var late = new Part(); provenance.Mark(late, 42);
        if (provenance.Suppress(late, 43) || !provenance.Suppress(late, 42))
            throw new Exception("A late result must retain its original stroke without suppressing a new stroke.");
        Console.WriteLine("PASS: clutter fragment stroke is retained, prevents same-stroke vessel recuts, and expires for later strokes");
        LoaderLane();
        SharedLaneReservations();
    }

    private static void LoaderLane()
    {
        ClutterScenario.Reset();
        using var releaseWorkers = new ManualResetEventSlim();
        using var releaseLoader = new ManualResetEventSlim();
        ClutterScenario.WorkerRelease = releaseWorkers; ClutterScenario.LoaderRelease = releaseLoader;
        using var queue = new ClutterSliceQueue(4, 16);
        var owner = new KittenEva();
        int workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
        for (int i = 0; i < workers; i++)
            if (!queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX)) throw new Exception("Could not admit geometry fixture.");
        try
        {
            if (!SpinWait.SpinUntil(() => { queue.Update(owner, false, 1, 0); return Volatile.Read(ref ClutterScenario.WorkersRunning) == workers; }, TimeSpan.FromSeconds(10)))
                throw new Exception("Configured geometry workers did not overlap.");
            releaseWorkers.Set();
            int expectedSlots = Math.Min(2, workers);
            if (!SpinWait.SpinUntil(() => { queue.Update(owner, false, 2, 0); return queue.LoadSlots == expectedSlots
                && Volatile.Read(ref ClutterScenario.WorkersRunning) == 0 && Volatile.Read(ref ClutterScenario.LoadersRunning) == 1; }, TimeSpan.FromSeconds(10)))
                throw new Exception("Geometry results did not reach the bounded loader slots.");
            if (queue.LoadSlots > 2 || ClutterScenario.MaximumWorkers != workers || ClutterScenario.MaximumLoaders != 1)
                throw new Exception("Geometry/load worker limits were not respected.");
        }
        finally { releaseWorkers.Set(); releaseLoader.Set(); }
        if (!SpinWait.SpinUntil(() => { queue.Update(owner, true, 3, 0); return !queue.Active; }, TimeSpan.FromSeconds(10)))
            throw new Exception("Jobs did not drain after releasing the loader.");
        Console.WriteLine($"PASS: {workers} geometry workers overlap while the single loader and two result slots remain bounded");
    }

    private static void SharedLaneReservations()
    {
        ClutterScenario.Reset();
        using var queue = new ClutterSliceQueue(4, 16);
        var owner = new KittenEva();
        if (!queue.Begin(owner, new(), default, double3.UnitY, double3.UnitX)) throw new Exception("Could not admit shared lane fixture.");
        for (int i = 0; i < 10; i++) queue.Update(owner, false, 1, 0, permitCapture: false);
        if (ClutterScenario.WorkerThread != 0 || queue.ReservationCount != 0)
            throw new Exception("A blocked shared capture lane started work.");
        for (int i = 0; i < 10; i++) queue.Update(owner, false, 1, 0, otherReservations: queue.WorkerCount);
        if (ClutterScenario.WorkerThread != 0 || queue.ReservationCount != 0)
            throw new Exception("External worker reservations were ignored.");
        if (!SpinWait.SpinUntil(() => { queue.Update(owner, false, 2, 0, otherLoadSlots: 2); return ClutterScenario.WorkerThread != 0; }, TimeSpan.FromSeconds(10)))
            throw new Exception("Geometry did not start after external reservations drained.");
        for (int i = 0; i < 30; i++) queue.Update(owner, false, 2, 0, otherLoadSlots: 2);
        if (queue.LoadSlots != 0 || ClutterScenario.UploadAdvances != 0)
            throw new Exception("External loaded-result reservations were ignored.");
        if (!SpinWait.SpinUntil(() => { queue.Update(owner, false, 3, 0, permitUpload: false); return queue.LoadSlots == 1; }, TimeSpan.FromSeconds(10)))
            throw new Exception("The loader did not start after external slots drained.");
        if (ClutterScenario.UploadAdvances != 0) throw new Exception("A blocked shared upload lane advanced.");
        if (!SpinWait.SpinUntil(() => { queue.Update(owner, true, 4, 0); return !queue.Active; }, TimeSpan.FromSeconds(10)))
            throw new Exception("Shared lane fixture did not drain.");
        Console.WriteLine("PASS: cross-domain capture, worker, loader and upload reservations gate queued clutter");
    }
}
