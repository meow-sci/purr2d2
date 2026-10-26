using System;
using Brutal.Numerics;
using Purr2D2.Core;

// Pure geometry and latch checks; the runtime harness wires these to actual pose snapshots.
internal static class ClashCoreChecks
{
    internal static void Run()
    {
        CrossingAndParallelSegments();
        DegenerateAndInvalidSegments();
        PartialBladeLengthIsRespected();
        PairLatchAndRearm();
        WarmedGeometryDoesNotAllocate();
    }

    private static void CrossingAndParallelSegments()
    {
        var horizontal = new SaberClashMath.Segment(new(-1, 0, 0), new(1, 0, 0));
        var vertical = new SaberClashMath.Segment(new(0, -1, 0), new(0, 1, 0));
        Check(SaberClashMath.TryCapsuleContact(horizontal, vertical, .04, out var crossing)
            && crossing.DistanceSquared < 1e-20, "crossing blades contact at their finite-segment intersection");
        Near((crossing.OnA + crossing.OnB) * .5, double3.Zero,
            "contact point is the midpoint of the closest segment points");

        var parallel = new SaberClashMath.Segment(new(-1, .12, 0), new(1, .12, 0));
        Check(SaberClashMath.TryClosest(horizontal, parallel, out var near)
            && Math.Abs(near.DistanceSquared - .0144) < 1e-12,
            "parallel blades return their true finite-segment spacing");
        Check(SaberClashMath.TryCapsuleContact(horizontal, parallel, .13, out _)
            && !SaberClashMath.TryCapsuleContact(horizontal, parallel, .11, out _),
            "capsule contact uses squared radius at the boundary");

        var disjoint = new SaberClashMath.Segment(new(2, 2, 0), new(3, 2, 0));
        Check(SaberClashMath.AabbDistanceSquared(horizontal, disjoint) > .25,
            "squared AABB gap cheaply rejects distant segment pairs");
    }

    private static void DegenerateAndInvalidSegments()
    {
        var point = new SaberClashMath.Segment(new(0, 0, 0), new(0, 0, 0));
        var edge = new SaberClashMath.Segment(new(1, 0, 0), new(2, 0, 0));
        Check(SaberClashMath.TryClosest(point, edge, out var onePoint)
            && Math.Abs(onePoint.DistanceSquared - 1) < 1e-12,
            "a degenerate point segment finds the nearest point on a finite segment");
        Check(SaberClashMath.TryClosest(point, point, out var twoPoints)
            && twoPoints.DistanceSquared == 0,
            "two degenerate segments remain finite and contact at one point");
        var invalid = new SaberClashMath.Segment(new(double.NaN, 0, 0), double3.Zero);
        Check(!SaberClashMath.TryClosest(invalid, edge, out _)
            && !SaberClashMath.TryCapsuleContact(edge, point, double.NaN, out _),
            "nonfinite segment coordinates and capsule radii are rejected");

        var parallelA = new SaberClashMath.Segment(new(0, 0, 0), new(2, 0, 0));
        var parallelB = new SaberClashMath.Segment(new(1, .2, 0), new(3, .2, 0));
        Check(SaberClashMath.TryClosest(parallelA, parallelB, out var parallel)
            && Math.Abs(parallel.DistanceSquared - .04) < 1e-12,
            "parallel overlap handles the near-parallel denominator without instability");
    }

    private static void PartialBladeLengthIsRespected()
    {
        var target = new SaberClashMath.Segment(new(0, .8, -.1), new(0, .8, .1));
        var fullBlade = new SaberClashMath.Segment(double3.Zero, new(0, 1, 0));
        var partialBlade = new SaberClashMath.Segment(double3.Zero, new(0, .5, 0));
        Check(SaberClashMath.TryCapsuleContact(fullBlade, target, .05, out _)
            && !SaberClashMath.TryCapsuleContact(partialBlade, target, .05, out _),
            "partial and retracting blade geometry uses its visible endpoint instead of target-on state");
    }

    private static void PairLatchAndRearm()
    {
        var table = new SaberClashLatchTable<object>();
        var a = new object(); var b = new object();
        Check(table.TryLatch(a, b, 1) && !table.TryLatch(b, a, 1.01) && table.Count == 1,
            "a pair latches once regardless of participant order");
        table.Observe(a, b, .01, .04, 1.02, .18);
        Check(table.IsLatched(a, b), "near overlap keeps the pair latched without extending a clash");
        table.Observe(a, b, .09, .04, 1.03, .18);
        Check(table.IsLatched(a, b), "the release distance must exceed the contact distance");
        table.Observe(a, b, .09, .04, 1.13, .18);
        Check(table.IsLatched(a, b), "separation cooldown does not rearm early");
        table.Observe(a, b, .09, .04, 1.22, .18);
        Check(!table.IsLatched(a, b) && table.Count == 0,
            "continuous separation beyond the release radius and cooldown rearms the pair");

        Check(table.TryLatch(a, b, 2), "a separated pair may latch again");
        table.ObserveSeparated(a, b, 2.01, .18);
        table.MarkUnknown(a, b, 2.10);
        table.ObserveSeparated(a, b, 2.19, .18);
        table.ObserveSeparated(a, b, 2.29, .18);
        Check(table.IsLatched(a, b), "even a short unknown pose gap breaks release continuity");
        table.ObserveSeparated(a, b, 2.38, .18);
        Check(!table.IsLatched(a, b), "a new continuous separation interval rearms after its own cooldown");

        Check(table.TryLatch(a, b, 3), "the same owners can latch after rearm");
        table.ObserveSeparated(a, b, 3.01, .18);
        // No calls represent stale or missing poses: a long gap is not separation evidence.
        table.ObserveSeparated(a, b, 4, .18);
        Check(table.IsLatched(a, b), "a stale-pose gap restarts, rather than completes, rearm timing");
        table.Remove(a);
        Check(table.Count == 0, "removal clears every latched reference for an owner");

        bool allPairsAccepted = true;
        for (int i = 0; i < SaberClashLatchTable<object>.MaximumPairs; i++)
        {
            var x = new object(); var y = new object();
            allPairsAccepted &= table.TryLatch(x, y, i);
        }
        Check(allPairsAccepted && table.Count == SaberClashLatchTable<object>.MaximumPairs
            && !table.TryLatch(new object(), new object(), 1),
            "overflow deterministically drops new pairs without evicting active latches");
        table.Clear();
        Check(table.Count == 0, "world reset clears the fixed pair table");
    }

    private static void WarmedGeometryDoesNotAllocate()
    {
        var a = new SaberClashMath.Segment(new(-1, 0, 0), new(1, 0, 0));
        var b = new SaberClashMath.Segment(new(0, -.5, 0), new(0, .5, 0));
        for (int i = 0; i < 8; i++) _ = SaberClashMath.TryClosest(a, b, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) _ = SaberClashMath.TryClosest(a, b, out _);
        Check(GC.GetAllocatedBytesForCurrentThread() == before,
            "warmed finite-segment collision geometry allocates zero managed bytes");
    }

    private static void Near(double3 actual, double3 expected, string name) =>
        Check((actual - expected).Length() < 1e-9, name);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Clash core check failed: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
