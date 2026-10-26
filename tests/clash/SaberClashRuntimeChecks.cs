using Brutal.Numerics;
using KSA;
using Purr2D2.Runtime;

internal static class SaberClashRuntimeChecks
{
    internal static void Run()
    {
        CandidateGatesAndFarRange();
        PartialLengthAndOneShotLatch();
        UnknownSnapshotAndRearm();
        SupportedClockAndIndependentPairs();
        OwnerRemovalAndWorldReset();
        WarmedDetectorDoesNotAllocate();
    }

    private static void CandidateGatesAndFarRange()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent, PositionCce = new(1000, 0, 0), PositionEcl = new(1000, 0, 0) };
        runtime.AddTestSaber(a);
        runtime.AddTestSaber(b);
        runtime.TickClashesForTest(.016, 4);
        Check(runtime.PairCountForTest == 0 && runtime.SparkCountForTest == 0,
            "far candidates are rejected without creating contact effects");

        runtime = new SaberRuntime();
        a = new KittenEva("a") { Parent = parent };
        b = new KittenEva("b") { Parent = parent };
        runtime.AddTestSaber(a, fresh: false);
        runtime.AddTestSaber(b, eligible: false);
        runtime.TickClashesForTest(.016, 4);
        Check(runtime.CandidateCountForTest == 0 && runtime.SparkCountForTest == 0,
            "stale rendered poses and ineligible pose states never enter the candidate set");

        runtime = new SaberRuntime();
        a = new KittenEva("a") { Parent = parent };
        b = new KittenEva("b") { Parent = parent };
        runtime.AddTestSaber(a, equipped: false);
        runtime.AddTestSaber(b);
        runtime.TickClashesForTest(.016, 4);
        Check(runtime.CandidateCountForTest == 1 && runtime.PairCountForTest == 0,
            "unequipped sabers are excluded before pair testing");

        runtime = new SaberRuntime();
        var overflowOwners = new KittenEva[130];
        for (int i = 0; i < overflowOwners.Length; i++)
        {
            overflowOwners[i] = new KittenEva(i.ToString("D3"))
            { Parent = parent, PositionCce = new(i * 10, 0, 0), PositionEcl = new(i * 10, 0, 0) };
            runtime.AddTestSaber(overflowOwners[i]);
        }
        runtime.TickClashesForTest(.016, 4);
        Check(runtime.CandidateCountForTest == 128 && runtime.HasCandidateForTest(overflowOwners[0])
            && runtime.HasCandidateForTest(overflowOwners[127]) && !runtime.HasCandidateForTest(overflowOwners[128])
            && !runtime.HasCandidateForTest(overflowOwners[129]),
            "candidate overflow deterministically retains the lowest 128 ids in fixed storage");
    }

    private static void PartialLengthAndOneShotLatch()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent };
        var aPose = runtime.AddTestSaber(a, configuredLength: 1, deployment: .5f,
            segmentDirection: double3.UnitY);
        runtime.AddTestSaber(b, configuredLength: .2f, deployment: 1,
            segmentStart: new(0, .75, 0), segmentDirection: double3.UnitX);
        runtime.TickClashesForTest(.01, 11);
        Check(Math.Abs(aPose.LastSampledLength - .5f) < 1e-6
            && runtime.PairCountForTest == 0 && runtime.SparkCountForTest == 0,
            "a partially deployed blade tests only its current visible length");

        runtime.SetDeploymentForTest(a, 1);
        runtime.TickClashesForTest(.01, 11.01);
        var poseB = runtime.PoseForTest(b);
        Check(runtime.PairCountForTest == 1 && runtime.SparkCountForTest == 1
            && runtime.AudioForTest(a).ClashSounds == 1 && runtime.AudioForTest(b).ClashSounds == 0
            && aPose.ClashHeld && poseB.ClashHeld,
            "the first finite-segment overlap atomically holds both sabers and emits one cue and burst");
        Near(runtime.LastSparkPointForTest, new(0, .75, 0),
            "the spark burst uses the closest-point midpoint in the owner body frame");

        runtime.TickClashesForTest(.01, 11.02);
        Check(runtime.PairCountForTest == 1 && runtime.SparkCountForTest == 1
            && runtime.AudioForTest(a).ClashSounds == 1,
            "a persistent overlap remains latched without extending the hold or replaying effects");
        Check(Math.Abs(runtime.VisibleLengthForTest(a) - 1) < 1e-6,
            "a held saber reports its captured visible blade length");
    }

    private static void UnknownSnapshotAndRearm()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent };
        runtime.AddTestSaber(a, segmentDirection: double3.UnitY);
        var poseB = runtime.AddTestSaber(b, segmentStart: new(0, .7, 0), segmentDirection: double3.UnitX);
        runtime.TickClashesForTest(.01, 1);
        Check(runtime.PairCountForTest == 1, "the rearm scenario begins with a latched contact");

        b.PositionCce = b.PositionEcl = new(100, 0, 0);
        runtime.TickClashesForTest(.01, 1.01); // Known separation begins.
        poseB.HasFreshClashSnapshot = false;
        runtime.TickClashesForTest(.08, 1.09); // Short unknown gap must reset continuity.
        poseB.HasFreshClashSnapshot = true;
        runtime.TickClashesForTest(.01, 1.10); // New separation interval starts.
        runtime.TickClashesForTest(.10, 1.20);
        Check(runtime.PairCountForTest == 1,
            "a short missing pose sample restarts the separation cooldown instead of counting as far-away time");
        runtime.TickClashesForTest(.10, 1.30);
        Check(runtime.PairCountForTest == 0, "continuous fresh separation beyond the cooldown rearms the pair");

        runtime.TickClashesForTest(.10, 1.40);
        b.PositionCce = b.PositionEcl = double3.Zero;
        runtime.TickClashesForTest(.01, 1.41);
        Check(runtime.PairCountForTest == 1 && runtime.SparkCountForTest == 2,
            "a rearmed pair can clash again after its hold and separation deadlines");
    }

    private static void SupportedClockAndIndependentPairs()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent, PositionCce = new(0, .7, 0), PositionEcl = new(0, .7, 0) };
        var poseA = runtime.AddTestSaber(a);
        var poseB = runtime.AddTestSaber(b, segmentStart: new(0, 0, 0), segmentDirection: double3.UnitX);
        runtime.TickClashesForTest(.01, 20);
        Check(runtime.LastSparkBornForTest == 20 && Math.Abs(poseA.HoldStartedAt - .01) < 1e-9,
            "effect birth uses renderer player-time while the hit-stop deadline uses supported player-time");
        runtime.TickClashesForTest(0, 200);
        Check(Math.Abs(runtime.ClashTimeForTest - .01) < 1e-9 && poseA.ClashHeld && poseB.ClashHeld,
            "paused frames neither age the clash nor start additional contacts");

        for (int i = 0; i < 4; i++) runtime.TickClashesForTest(.1, 200.1 + i * .1);
        Check(!poseA.ClashHeld && poseA.AngryActive,
            "the first pair releases its held pose while its shorter angry-face window remains active");

        var c = new KittenEva("c") { Parent = parent, PositionCce = new(100, 0, 0), PositionEcl = new(100, 0, 0) };
        var d = new KittenEva("d") { Parent = parent, PositionCce = new(100, .7, 0), PositionEcl = new(100, .7, 0) };
        var poseC = runtime.AddTestSaber(c);
        var poseD = runtime.AddTestSaber(d, segmentStart: new(0, 0, 0), segmentDirection: double3.UnitX);
        runtime.TickClashesForTest(.01, 200.41);
        Check(poseA.AngryActive && poseC.ClashHeld && poseD.ClashHeld
            && runtime.SparkCountForTest == 2,
            "a separate pair may clash while another kitten still displays its anger overlay");
    }

    private static void OwnerRemovalAndWorldReset()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent, PositionCce = new(0, .7, 0), PositionEcl = new(0, .7, 0) };
        var poseA = runtime.AddTestSaber(a);
        runtime.AddTestSaber(b, segmentStart: new(0, 0, 0), segmentDirection: double3.UnitX);
        runtime.TickClashesForTest(.01, 1);
        runtime.RemoveOwnerForTest(a);
        Check(runtime.PairCountForTest == 0 && !runtime.HasCandidateForTest(a) && !poseA.ClashHeld,
            "owner removal clears held pose, pair latch and candidate references");
        runtime.ResetForTest();
        Check(runtime.PairCountForTest == 0 && runtime.CandidateCountForTest == 0
            && runtime.ClashTimeForTest == 0,
            "world reset clears the bounded detector table and supported-time clock");
    }

    private static void WarmedDetectorDoesNotAllocate()
    {
        var runtime = new SaberRuntime();
        var parent = new ParentBody();
        for (int i = 0; i < 64; i++)
        {
            var owner = new KittenEva(i.ToString("D3"))
            {
                Parent = parent,
                PositionCce = new(i * 50, 0, 0),
                PositionEcl = new(i * 50, 0, 0),
            };
            runtime.AddTestSaber(owner);
        }
        for (int i = 0; i < 8; i++) runtime.TickClashesForTest(.016, i * .016);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) runtime.TickClashesForTest(.016, 1 + i * .016);
        Check(GC.GetAllocatedBytesForCurrentThread() == before,
            "the warmed 64-candidate far-pair detector allocates zero managed bytes");

        runtime = new SaberRuntime();
        var a = new KittenEva("a") { Parent = parent };
        var b = new KittenEva("b") { Parent = parent, PositionCce = new(0, .7, 0), PositionEcl = new(0, .7, 0) };
        runtime.AddTestSaber(a);
        runtime.AddTestSaber(b, segmentStart: new(0, 0, 0), segmentDirection: double3.UnitX);
        for (int i = 0; i < 8; i++) runtime.TickClashesForTest(.016, i * .016);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) runtime.TickClashesForTest(.016, 1 + i * .016);
        Check(GC.GetAllocatedBytesForCurrentThread() == before && runtime.SparkCountForTest == 1,
            "the warmed stable-overlap detector allocates zero bytes and keeps one latched event");
    }

    private static void Near(double3 actual, double3 expected, string name) =>
        Check((actual - expected).Length() < 1e-8, name);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Saber clash runtime check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
