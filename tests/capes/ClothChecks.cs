using System.Diagnostics;
using Brutal.Numerics;
using Purr2D2.Capes;

internal static class ClothChecks
{
    private const double UpdateDt = 1d / 30d;
    private const float CapeLengthMeters = .50f;
    private const float PreviousCapeLengthMeters = .88f;
    private const float CapeFloorY = -.43f;

    // Preserve the prior relative-motion gates as the measured cape is shorter.
    private const float TrailThresholdMeters = .15f * CapeLengthMeters / PreviousCapeLengthMeters;
    private const float HemMotionThresholdMeters = .04f * CapeLengthMeters / PreviousCapeLengthMeters;

    public static void Main()
    {
        BenchmarkFirstSolverSteps();
        GeometryAndRestPose();
        PoseRevisionAndCache();
        SettlingAndMotion();
        ReverseSidewaysAndFastMotion();
        SafeInputsResetAndDispose();
        AssetHostChecks.Run();
        Benchmark(1);
        Benchmark(2);
        Benchmark(4);
        Benchmark(8);
        BenchmarkPoseCache();
        Console.WriteLine("Cape cloth checks passed against the current KSA and Bepu managed assemblies.");
    }

    private static void BenchmarkFirstSolverSteps()
    {
        var firstEverCape = new CapeCloth();
        long start = Stopwatch.GetTimestamp();
        firstEverCape.Step(UpdateDt, new float3(0, 0, 5));
        double firstEverMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Check(firstEverCape.Failure is null, "the first native cape setup and step succeeds");
        firstEverCape.Dispose();

        var jittedCape = new CapeCloth();
        jittedCape.Step(UpdateDt, new float3(0, 0, 5));
        jittedCape.Dispose();

        var subsequentCape = new CapeCloth();
        start = Stopwatch.GetTimestamp();
        subsequentCape.Step(UpdateDt, new float3(0, 0, 5));
        double subsequentMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Check(subsequentCape.Failure is null, "a new cape can set up and step after native JIT warmup");
        subsequentCape.Dispose();

        Console.WriteLine($"Cape first-step setup timing: first-ever={firstEverMs:F3} ms; " +
            $"new cape after JIT warmup={subsequentMs:F3} ms (descriptive only)");
    }

    private static void GeometryAndRestPose()
    {
        Check(CapeGeometry.Columns == 9 && CapeGeometry.Rows == 13 && CapeGeometry.NodeCount == 117,
            "the cape has the expected 9 by 13 mesh");
        Check(MathF.Abs(CapeGeometry.RestPosition(0).X + .24f) < 1e-6f
            && MathF.Abs(CapeGeometry.RestPosition(4).Z + .23f) < 1e-6f
            && MathF.Abs(CapeGeometry.RestPosition(8).Z + .19f) < 1e-6f,
            "the pinned shoulder seam is narrowed and curves from z=-.23 at center to z=-.19 at edges");
        Check(MathF.Abs(CapeRestPose.BackCollisionLimitZ(CapeGeometry.Columns) - CapeGeometry.RestPosition(CapeGeometry.Columns).Z) < 1e-6f
            && MathF.Abs(CapeRestPose.BackCollisionLimitZ(CapeGeometry.Columns + CapeGeometry.Columns / 2)
                - CapeRestPose.BackCollisionZ) < 1e-6f,
            "the first outer dynamic row keeps its authored shoulder clearance while the center uses the torso rear plane");
        int hemLeft = (CapeGeometry.Rows - 1) * CapeGeometry.Columns;
        int hemCenter = hemLeft + CapeGeometry.Columns / 2;
        int hemRight = hemLeft + CapeGeometry.Columns - 1;
        Check(MathF.Abs(CapeGeometry.RestPosition(hemLeft).X + .43f) < 1e-6f
            && MathF.Abs(CapeGeometry.RestPosition(hemCenter).Y + CapeLengthMeters * .8f) < 1e-6f
            && MathF.Abs(CapeGeometry.RestPosition(hemRight).X - .43f) < 1e-6f,
            "the cape hem is .86 m wide and reaches y=-.40 m");
        using var cape = new CapeCloth();
        Check(cape.Positions.Length == CapeGeometry.NodeCount, "the default pose contains all cloth nodes");
        for (int i = 0; i < cape.Positions.Length; i++)
            Check(Distance(cape.Positions[i], CapeGeometry.RestPosition(i)) < 1e-6f,
                "the default pose matches authored rest geometry at node " + i);
        for (int column = 0; column < CapeGeometry.Columns; column++)
        {
            int index = column;
            cape.Step(UpdateDt, new float3(0, 0, 8));
            Check(Distance(cape.Positions[index], CapeGeometry.RestPosition(index)) < 1e-6f,
                "shoulder node remains pinned at index " + index);
        }
        Check(cape.Failure is null, "the first native cloth step succeeds");
    }

    private static void SettlingAndMotion()
    {
        using var still = new CapeCloth();
        using var moving = new CapeCloth();
        float movingFirstStepStretch = 0f;
        float movingPeakStretch = 0f;
        float movingPinnedFirstStepStretch = 0f;
        float movingPinnedPeakStretch = 0f;
        int movingPeakFrame = -1, movingPeakEdgeA = -1, movingPeakEdgeB = -1, movingPinnedPeakColumn = -1;
        for (int frame = 0; frame < 90; frame++)
        {
            still.Step(UpdateDt, float3.Zero);
            moving.Step(UpdateDt, new float3(0, 0, 3));
            EnsureFinite(moving.Positions, "forward cloth remains finite during motion");
            TrackStretch(moving.Positions, frame,
                ref movingFirstStepStretch, ref movingPeakStretch,
                ref movingPinnedFirstStepStretch, ref movingPinnedPeakStretch,
                ref movingPeakFrame, ref movingPeakEdgeA, ref movingPeakEdgeB, ref movingPinnedPeakColumn);
        }

        EnsureFinite(still.Positions, "stationary cloth remains finite");
        EnsureFinite(moving.Positions, "moving cloth remains finite");
        float stillHemY = MeanRowY(still.Positions, CapeGeometry.Rows - 1);
        float movingHemY = MeanRowY(moving.Positions, CapeGeometry.Rows - 1);
        float stillHemZ = MeanRowZ(still.Positions, CapeGeometry.Rows - 1);
        float movingHemZ = MeanRowZ(moving.Positions, CapeGeometry.Rows - 1);
        float movingMaximumY = MaximumY(moving.Positions);
        Console.WriteLine($"Cape motion check after 3s: stationary hem=({stillHemY:F3}y, {stillHemZ:F3}z), " +
            $"at 3m/s=({movingHemY:F3}y, {movingHemZ:F3}z; max y={movingMaximumY:F3}; " +
            $"final/first/peak edge ratio={MaximumStructuralStretchRatio(moving.Positions):F3}/" +
            $"{movingFirstStepStretch:F3}/{movingPeakStretch:F3}; first-row first/peak=" +
            $"{movingPinnedFirstStepStretch:F3}/{movingPinnedPeakStretch:F3} " +
            $"at edge {movingPeakEdgeA}-{movingPeakEdgeB}, frame {movingPeakFrame}, " +
            $"pinned column {movingPinnedPeakColumn})");
        Check(stillHemY >= CapeFloorY - 1e-5f,
            $"stationary hem stays above the measured mesh sole/ground bound (hem Y {stillHemY:F3})");
        Check(stillHemY <= CapeFloorY + .01f,
            $"stationary cape settles within 1 cm of the measured mesh sole/ground (hem Y {stillHemY:F3})");
        Check(movingHemZ < stillHemZ - TrailThresholdMeters,
            $"walking speed trails the cape backward (still Z {stillHemZ:F3}, moving Z {movingHemZ:F3})");
        Check(movingHemY > stillHemY + HemMotionThresholdMeters,
            $"walking speed reduces the cape's drop through bounded lift (still Y {stillHemY:F3}, moving Y {movingHemY:F3})");
        Check(movingMaximumY < .5f, $"walking speed does not launch the cape upward (max Y {movingMaximumY:F3})");
        Check(movingPeakStretch <= 1.3f,
            $"forward motion keeps every structural edge within 1.3x rest length (peak ratio {movingPeakStretch:F3})");
        Check(movingPinnedPeakStretch <= 1.3f,
            $"forward motion keeps the first dynamic row within 1.3x rest length (peak ratio {movingPinnedPeakStretch:F3})");

        float movingStoppedPeakStretch = 0f;
        float movingStoppedPinnedPeakStretch = 0f;
        for (int frame = 0; frame < 90; frame++)
        {
            moving.Step(UpdateDt, float3.Zero);
            TrackStopStretch(moving.Positions, ref movingStoppedPeakStretch, ref movingStoppedPinnedPeakStretch);
        }
        float stoppedHemY = MeanRowY(moving.Positions, CapeGeometry.Rows - 1);
        Console.WriteLine($"Cape motion check after stopping for 3s: hem Y={stoppedHemY:F3}; " +
            $"peak/first-row edge ratio={movingStoppedPeakStretch:F3}/{movingStoppedPinnedPeakStretch:F3}");
        Check(stoppedHemY < movingHemY - HemMotionThresholdMeters,
            $"the cape drops again after movement stops (moving Y {movingHemY:F3}, stopped Y {stoppedHemY:F3})");
        Check(movingStoppedPeakStretch <= 1.3f,
            $"stopped forward motion keeps structural edges within 1.3x rest length (peak {movingStoppedPeakStretch:F3})");
        Check(movingStoppedPinnedPeakStretch <= 1.3f,
            $"stopped forward motion keeps the first dynamic row within 1.3x rest length (peak {movingStoppedPinnedPeakStretch:F3})");
        EnsureFinite(moving.Positions, "stopped cloth remains finite");
        CheckBehindWearer(moving.Positions, "stopped cloth stays behind the wearer");
        Check(moving.Failure is null && still.Failure is null, "steady simulation does not report a failure");
    }

    private static void PoseRevisionAndCache()
    {
        using var cape = new CapeCloth();
        var cache = new CapePoseCache();
        Check(cape.PoseRevision == 0, "a new cloth starts at its baseline pose revision");
        Check(cache.Update(cape.Positions, cape.PoseRevision), "a new pose cache calculates its first pose");
        CompareWithOriginalPoseMath(cape.Positions, cache.Poses, "initial rest pose");
        Check(!cache.Update(cape.Positions, cape.PoseRevision), "a repeated rest revision reuses the cached pose");

        cape.Step(double.NaN, new float3(0, 0, 5));
        cape.Step(double.PositiveInfinity, new float3(0, 0, 5));
        cape.Step(-1d, new float3(0, 0, 5));
        cape.Step(1d / 1000d, new float3(0, 0, 5));
        cape.Step(1d / 1000d, new float3(0, 0, 5));
        Check(cape.PoseRevision == 0, "invalid and undersized dt do not dirty the pose cache");
        Check(!cache.Update(cape.Positions, cape.PoseRevision), "invalid and undersized dt leave the cache reusable");

        cape.Step(UpdateDt, new float3(0, 0, 4));
        Check(cape.Failure is null && cape.PoseRevision > 0, "a valid native solver result advances the pose revision");
        Check(cache.Update(cape.Positions, cape.PoseRevision), "a new cloth revision recalculates the pose");
        CompareWithOriginalPoseMath(cape.Positions, cache.Poses, "moving pose");
        Check(!cache.Update(cape.Positions, cape.PoseRevision), "the same moving revision reuses its cached pose");

        // A newly created renderable (for example after a material/style change) has
        // a cold cache and must calculate from the current cloth pose, not from live bones.
        var styleCache = new CapePoseCache();
        Check(styleCache.Update(cape.Positions, cape.PoseRevision), "a newly created style cache calculates the current pose");
        CompareWithOriginalPoseMath(cape.Positions, styleCache.Poses, "new style cache");

        ulong movedRevision = cape.PoseRevision;
        cape.Reset();
        Check(cape.PoseRevision > movedRevision, "restoring a changed cloth pose advances its revision");
        Check(cache.Update(cape.Positions, cape.PoseRevision), "reset pose invalidates the moving cache");
        CompareWithOriginalPoseMath(cape.Positions, cache.Poses, "reset rest pose");
        ulong resetRevision = cape.PoseRevision;
        cape.Reset();
        Check(cape.PoseRevision == resetRevision, "repeated reset at rest does not dirty the pose cache");
        Check(!cache.Update(cape.Positions, cape.PoseRevision), "repeated reset preserves the cached rest pose");

        cape.Step(UpdateDt, new float3(0, 0, 4));
        Check(cape.Failure is null && cape.PoseRevision > resetRevision,
            "a fresh native solver step recovers after reset");
    }

    private static void ReverseSidewaysAndFastMotion()
    {
        using var backward = new CapeCloth();
        using var sideways = new CapeCloth();
        using var fast = new CapeCloth();
        float backwardFirstStepStretch = 0f, backwardPeakStretch = 0f;
        float sidewaysFirstStepStretch = 0f, sidewaysPeakStretch = 0f;
        float fastFirstStepStretch = 0f, fastPeakStretch = 0f;
        float backwardPinnedFirstStepStretch = 0f, backwardPinnedPeakStretch = 0f;
        float sidewaysPinnedFirstStepStretch = 0f, sidewaysPinnedPeakStretch = 0f;
        float fastPinnedFirstStepStretch = 0f, fastPinnedPeakStretch = 0f;
        int backwardPeakFrame = -1, backwardPeakEdgeA = -1, backwardPeakEdgeB = -1, backwardPinnedPeakColumn = -1;
        int sidewaysPeakFrame = -1, sidewaysPeakEdgeA = -1, sidewaysPeakEdgeB = -1, sidewaysPinnedPeakColumn = -1;
        int fastPeakFrame = -1, fastPeakEdgeA = -1, fastPeakEdgeB = -1, fastPinnedPeakColumn = -1;
        for (int frame = 0; frame < 90; frame++)
        {
            backward.Step(UpdateDt, new float3(0, 0, -3));
            sideways.Step(UpdateDt, new float3(3, 0, 0));
            fast.Step(UpdateDt, new float3(0, 0, 8));
            EnsureFinite(backward.Positions, "backward cloth remains finite during motion");
            EnsureFinite(sideways.Positions, "sideways cloth remains finite during motion");
            EnsureFinite(fast.Positions, "maximum-speed cloth remains finite during motion");
            TrackStretch(backward.Positions, frame,
                ref backwardFirstStepStretch, ref backwardPeakStretch,
                ref backwardPinnedFirstStepStretch, ref backwardPinnedPeakStretch,
                ref backwardPeakFrame, ref backwardPeakEdgeA, ref backwardPeakEdgeB, ref backwardPinnedPeakColumn);
            TrackStretch(sideways.Positions, frame,
                ref sidewaysFirstStepStretch, ref sidewaysPeakStretch,
                ref sidewaysPinnedFirstStepStretch, ref sidewaysPinnedPeakStretch,
                ref sidewaysPeakFrame, ref sidewaysPeakEdgeA, ref sidewaysPeakEdgeB, ref sidewaysPinnedPeakColumn);
            TrackStretch(fast.Positions, frame,
                ref fastFirstStepStretch, ref fastPeakStretch,
                ref fastPinnedFirstStepStretch, ref fastPinnedPeakStretch,
                ref fastPeakFrame, ref fastPeakEdgeA, ref fastPeakEdgeB, ref fastPinnedPeakColumn);
        }

        EnsureFinite(backward.Positions, "backward airflow remains finite");
        EnsureFinite(sideways.Positions, "sideways airflow remains finite");
        EnsureFinite(fast.Positions, "maximum bounded airflow remains finite");
        CheckBehindWearer(backward.Positions, "backward airflow stays behind the wearer");
        CheckBehindWearer(sideways.Positions, "sideways airflow stays behind the wearer");
        CheckBehindWearer(fast.Positions, "maximum airflow stays behind the wearer");
        CheckPinned(backward.Positions, "backward airflow keeps shoulder pins fixed");
        CheckPinned(sideways.Positions, "sideways airflow keeps shoulder pins fixed");
        CheckPinned(fast.Positions, "maximum airflow keeps shoulder pins fixed");

        Console.WriteLine($"Cape structural stretch check over 3s: " +
            $"backward first/peak={backwardFirstStepStretch:F3}/{backwardPeakStretch:F3} " +
            $"(first-row={backwardPinnedFirstStepStretch:F3}/{backwardPinnedPeakStretch:F3}), " +
            $"sideways first/peak={sidewaysFirstStepStretch:F3}/{sidewaysPeakStretch:F3} " +
            $"(first-row={sidewaysPinnedFirstStepStretch:F3}/{sidewaysPinnedPeakStretch:F3}), " +
            $"8m/s first/peak={fastFirstStepStretch:F3}/{fastPeakStretch:F3} " +
            $"(first-row={fastPinnedFirstStepStretch:F3}/{fastPinnedPeakStretch:F3})\n" +
            $"Cape peak edges: backward {backwardPeakEdgeA}-{backwardPeakEdgeB} at frame {backwardPeakFrame}, " +
            $"pin col {backwardPinnedPeakColumn}; sideways {sidewaysPeakEdgeA}-{sidewaysPeakEdgeB} " +
            $"at frame {sidewaysPeakFrame}, pin col {sidewaysPinnedPeakColumn}; 8m/s " +
            $"{fastPeakEdgeA}-{fastPeakEdgeB} at frame {fastPeakFrame}, pin col {fastPinnedPeakColumn}");
        Check(backwardPeakStretch <= 1.3f, $"backward motion bounds peak structural stretch (ratio {backwardPeakStretch:F3})");
        Check(sidewaysPeakStretch <= 1.3f, $"sideways motion bounds peak structural stretch (ratio {sidewaysPeakStretch:F3})");
        Check(fastPeakStretch <= 1.3f, $"8m/s motion bounds peak structural stretch (ratio {fastPeakStretch:F3})");
        Check(backwardPinnedPeakStretch <= 1.3f, $"backward motion bounds first-row peak stretch (ratio {backwardPinnedPeakStretch:F3})");
        Check(sidewaysPinnedPeakStretch <= 1.3f, $"sideways motion bounds first-row peak stretch (ratio {sidewaysPinnedPeakStretch:F3})");
        Check(fastPinnedPeakStretch <= 1.3f, $"8m/s motion bounds first-row peak stretch (ratio {fastPinnedPeakStretch:F3})");
        Check(backward.Failure is null && sideways.Failure is null && fast.Failure is null,
            "reverse, sideways and maximum airflow do not poison simulation");

        float backwardHemY = MeanRowY(backward.Positions, CapeGeometry.Rows - 1);
        float sidewaysHemY = MeanRowY(sideways.Positions, CapeGeometry.Rows - 1);
        float fastHemY = MeanRowY(fast.Positions, CapeGeometry.Rows - 1);
        float backwardStoppedPeakStretch = 0f, backwardStoppedPinnedPeakStretch = 0f;
        float sidewaysStoppedPeakStretch = 0f, sidewaysStoppedPinnedPeakStretch = 0f;
        float fastStoppedPeakStretch = 0f, fastStoppedPinnedPeakStretch = 0f;
        Console.WriteLine($"Cape hem before stop: backward={backwardHemY:F3}, sideways={sidewaysHemY:F3}, 8m/s={fastHemY:F3}");
        for (int frame = 0; frame < 90; frame++)
        {
            backward.Step(UpdateDt, float3.Zero);
            sideways.Step(UpdateDt, float3.Zero);
            fast.Step(UpdateDt, float3.Zero);
            TrackStopStretch(backward.Positions, ref backwardStoppedPeakStretch, ref backwardStoppedPinnedPeakStretch);
            TrackStopStretch(sideways.Positions, ref sidewaysStoppedPeakStretch, ref sidewaysStoppedPinnedPeakStretch);
            TrackStopStretch(fast.Positions, ref fastStoppedPeakStretch, ref fastStoppedPinnedPeakStretch);
        }
        float backwardStoppedHemY = MeanRowY(backward.Positions, CapeGeometry.Rows - 1);
        float sidewaysStoppedHemY = MeanRowY(sideways.Positions, CapeGeometry.Rows - 1);
        float fastStoppedHemY = MeanRowY(fast.Positions, CapeGeometry.Rows - 1);
        Console.WriteLine($"Cape hem after stop: backward={backwardStoppedHemY:F3}, sideways={sidewaysStoppedHemY:F3}, 8m/s={fastStoppedHemY:F3}");
        Console.WriteLine($"Cape stopping stretch peaks: backward={backwardStoppedPeakStretch:F3}/" +
            $"{backwardStoppedPinnedPeakStretch:F3}, sideways={sidewaysStoppedPeakStretch:F3}/" +
            $"{sidewaysStoppedPinnedPeakStretch:F3}, 8m/s={fastStoppedPeakStretch:F3}/" +
            $"{fastStoppedPinnedPeakStretch:F3} (full/first-row)");
        CheckHemDropsOrSettlesAtFloor(backwardHemY, backwardStoppedHemY, "backward movement");
        CheckHemDropsOrSettlesAtFloor(sidewaysHemY, sidewaysStoppedHemY, "sideways movement");
        CheckHemDropsOrSettlesAtFloor(fastHemY, fastStoppedHemY, "maximum-speed movement");
        Check(backwardStoppedPeakStretch <= 1.3f && backwardStoppedPinnedPeakStretch <= 1.3f,
            $"stopped backward motion keeps full/first-row stretch within 1.3x ({backwardStoppedPeakStretch:F3}/{backwardStoppedPinnedPeakStretch:F3})");
        Check(sidewaysStoppedPeakStretch <= 1.3f && sidewaysStoppedPinnedPeakStretch <= 1.3f,
            $"stopped sideways motion keeps full/first-row stretch within 1.3x ({sidewaysStoppedPeakStretch:F3}/{sidewaysStoppedPinnedPeakStretch:F3})");
        Check(fastStoppedPeakStretch <= 1.3f && fastStoppedPinnedPeakStretch <= 1.3f,
            $"stopped maximum-speed motion keeps full/first-row stretch within 1.3x ({fastStoppedPeakStretch:F3}/{fastStoppedPinnedPeakStretch:F3})");
        EnsureFinite(backward.Positions, "cape remains finite after backward motion stops");
        EnsureFinite(sideways.Positions, "cape remains finite after sideways motion stops");
        EnsureFinite(fast.Positions, "cape remains finite after maximum motion stops");
        CheckBehindWearer(backward.Positions, "backward-motion cape stays behind wearer after stopping");
        CheckBehindWearer(sideways.Positions, "sideways-motion cape stays behind wearer after stopping");
        CheckBehindWearer(fast.Positions, "fast-motion cape stays behind wearer after stopping");
        Check(backward.Failure is null && sideways.Failure is null && fast.Failure is null,
            "reverse, sideways and maximum motion stay healthy after stopping");
    }

    private static void SafeInputsResetAndDispose()
    {
        using var cape = new CapeCloth();
        cape.Step(double.NaN, new float3(0, 0, 10));
        cape.Step(double.PositiveInfinity, new float3(0, 0, 10));
        cape.Step(-1, new float3(0, 0, 10));
        Check(Distance(cape.Positions[12 * CapeGeometry.Columns + 4], CapeGeometry.RestPosition(12 * CapeGeometry.Columns + 4)) < 1e-6f,
            "invalid or paused dt freezes the cape");

        float extreme = float.MaxValue;
        cape.Step(.1, new float3(extreme, extreme, extreme));
        cape.Step(.1, new float3(float.PositiveInfinity, 0, float.NaN));
        EnsureFinite(cape.Positions, "extreme dt and velocity are sanitized");
        Check(cape.Failure is null, "extreme inputs do not poison the native simulation");

        cape.Reset();
        for (int i = 0; i < cape.Positions.Length; i++)
            Check(Distance(cape.Positions[i], CapeGeometry.RestPosition(i)) < 1e-6f,
                "reset restores rest pose node " + i);
        cape.Step(UpdateDt, new float3(0, 0, 4));
        Check(cape.Failure is null, "reset permits a fresh private simulation");

        var disposed = new CapeCloth();
        disposed.Step(UpdateDt, new float3(0, 0, 4));
        disposed.Dispose();
        disposed.Dispose();
        float3 before = disposed.Positions[12 * CapeGeometry.Columns + 4];
        disposed.Step(1, new float3(0, 0, 8));
        Check(Distance(before, disposed.Positions[12 * CapeGeometry.Columns + 4]) < 1e-6f,
            "disposed simulation stays frozen and repeated disposal is safe");
    }

    private static void Benchmark(int capeCount)
    {
        using var group = new CapeGroup(capeCount);
        const int warmupUpdates = 180;
        const int measuredUpdates = 600;
        var samples = new double[measuredUpdates];
        var velocity = new float3(0, 0, 5);

        for (int frame = 0; frame < warmupUpdates; frame++) group.Step(UpdateDt, velocity);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < measuredUpdates; frame++)
        {
            long start = Stopwatch.GetTimestamp();
            group.Step(UpdateDt, velocity);
            samples[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double allocatedPerUpdate = allocatedBytes / (double)measuredUpdates;

        Array.Sort(samples);
        double median = (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) * .5;
        double p95 = samples[(int)Math.Ceiling(samples.Length * .95) - 1];
        Console.WriteLine($"Cape CPU benchmark: capes={capeCount}, nodes={capeCount * CapeGeometry.NodeCount}, " +
            $"warmup={warmupUpdates}, measured={measuredUpdates}, median/update={median:F3} ms, p95/update={p95:F3} ms, " +
            $"allocated/update={allocatedPerUpdate:F1} B");
        Check(allocatedBytes == 0, $"warmed {capeCount}-cape Step updates allocate no managed memory ({allocatedBytes} bytes total)");
    }

    private static void BenchmarkPoseCache()
    {
        using var cape = new CapeCloth();
        cape.Step(UpdateDt, new float3(0, 0, 5));
        ReadOnlySpan<float3> positions = cape.Positions;
        const int iterations = 1000;
        var recompute = new CapePoseCache();
        recompute.Update(positions, 0);
        for (int i = 1; i < 10; i++) recompute.Update(positions, (ulong)i);

        var timer = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (ulong revision = 10; revision < 10 + iterations; revision++)
            recompute.Update(positions, revision);
        long recomputeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        timer.Stop();
        double recomputeMs = timer.Elapsed.TotalMilliseconds;

        var reuse = new CapePoseCache();
        reuse.Update(positions, 42);
        var copiedPose = new CapeNodePose[CapeGeometry.NodeCount];
        timer.Restart();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            reuse.Update(positions, 42);
            reuse.Poses.CopyTo(copiedPose);
        }
        long reuseAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        timer.Stop();
        double reuseMs = timer.Elapsed.TotalMilliseconds;

        Console.WriteLine($"Cape pose-cache calculation check (no native skinning or GPU): iterations={iterations}, " +
            $"recompute={recomputeMs:F3} ms/{recomputeAllocated} B; " +
            $"revision check + cached 117-node copy={reuseMs:F3} ms/{reuseAllocated} B (descriptive timing)");
        Check(recomputeAllocated == 0, $"pose recomputation allocates no managed memory ({recomputeAllocated} bytes total)");
        Check(reuseAllocated == 0, $"warmed pose-cache reuse allocates no managed memory ({reuseAllocated} bytes total)");
    }

    private static void CompareWithOriginalPoseMath(
        ReadOnlySpan<float3> positions,
        ReadOnlySpan<CapeNodePose> actual,
        string context)
    {
        Check(positions.Length == CapeGeometry.NodeCount && actual.Length == CapeGeometry.NodeCount,
            context + " has all 117 cached node poses");
        Span<float4x4> inverseRest = stackalloc float4x4[CapeGeometry.NodeCount];
        ReadOnlySpan<float3> rest = CapeRestPose.All;
        for (int node = 0; node < CapeGeometry.NodeCount; node++)
        {
            Check(float4x4.Invert(ReferenceSurfaceFrame(rest, node), out inverseRest[node]),
                context + " reference rest frame inverts at node " + node);
        }
        for (int node = 0; node < CapeGeometry.NodeCount; node++)
        {
            floatQuat expected = floatQuat.CreateFromRotationMatrix(inverseRest[node] * ReferenceSurfaceFrame(positions, node));
            Check(Distance(actual[node].Translation, positions[node]) < 1e-6f,
                context + " preserves node translation at " + node);
            Check(QuaternionDistance(actual[node].Rotation, expected) < 1e-5f,
                context + " preserves authored rest-frame rotation at " + node);
        }
    }

    private static float4x4 ReferenceSurfaceFrame(ReadOnlySpan<float3> positions, int index)
    {
        int row = index / CapeGeometry.Columns;
        int column = index % CapeGeometry.Columns;
        int left = Math.Max(column - 1, 0);
        int right = Math.Min(column + 1, CapeGeometry.Columns - 1);
        int above = Math.Max(row - 1, 0);
        int below = Math.Min(row + 1, CapeGeometry.Rows - 1);
        float3 across = positions[row * CapeGeometry.Columns + right] - positions[row * CapeGeometry.Columns + left];
        float3 upward = positions[above * CapeGeometry.Columns + column] - positions[below * CapeGeometry.Columns + column];
        float3 x = Normalize(across, float3.UnitX);
        float3 y = upward - x * float3.Dot(upward, x);
        y = Normalize(y, float3.UnitY);
        float3 z = Normalize(float3.Cross(x, y), float3.UnitZ);
        y = Normalize(float3.Cross(z, x), y);
        return new float4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private static float3 Normalize(float3 value, float3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-10f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static float QuaternionDistance(floatQuat a, floatQuat b)
    {
        float same = MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y) + MathF.Abs(a.Z - b.Z) + MathF.Abs(a.W - b.W);
        float opposite = MathF.Abs(a.X + b.X) + MathF.Abs(a.Y + b.Y) + MathF.Abs(a.Z + b.Z) + MathF.Abs(a.W + b.W);
        return MathF.Min(same, opposite);
    }

    private static float MeanRowY(ReadOnlySpan<float3> positions, int row)
    {
        float sum = 0;
        for (int column = 0; column < CapeGeometry.Columns; column++)
            sum += positions[row * CapeGeometry.Columns + column].Y;
        return sum / CapeGeometry.Columns;
    }

    private static float MeanRowZ(ReadOnlySpan<float3> positions, int row)
    {
        float sum = 0;
        for (int column = 0; column < CapeGeometry.Columns; column++)
            sum += positions[row * CapeGeometry.Columns + column].Z;
        return sum / CapeGeometry.Columns;
    }

    private static float MaximumY(ReadOnlySpan<float3> positions)
    {
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < positions.Length; i++) maximum = MathF.Max(maximum, positions[i].Y);
        return maximum;
    }

    private static void CheckBehindWearer(ReadOnlySpan<float3> positions, string message)
    {
        for (int i = CapeGeometry.Columns; i < positions.Length; i++)
            Check(positions[i].Z <= CapeRestPose.BackCollisionLimitZ(i) + 1e-5f, message + " at node " + i);
    }

    private static void CheckHemDropsOrSettlesAtFloor(float movingHemY, float stoppedHemY, string motion)
    {
        if (movingHemY > CapeFloorY + HemMotionThresholdMeters)
        {
            Check(stoppedHemY < movingHemY - HemMotionThresholdMeters,
                $"cape drops after {motion} stops (moving {movingHemY:F3}, stopped {stoppedHemY:F3})");
            return;
        }

        Check(stoppedHemY >= CapeFloorY - 1e-5f,
            $"cape stays above the mesh sole/ground after {motion} stops (hem Y {stoppedHemY:F3})");
        Check(stoppedHemY <= CapeFloorY + .01f,
            $"cape settles within 1 cm of the mesh sole/ground after {motion} stops (hem Y {stoppedHemY:F3})");
        Check(stoppedHemY <= movingHemY + 1e-5f,
            $"cape does not rise after {motion} stops (moving {movingHemY:F3}, stopped {stoppedHemY:F3})");
    }

    private static void CheckPinned(ReadOnlySpan<float3> positions, string message)
    {
        for (int column = 0; column < CapeGeometry.Columns; column++)
        {
            int index = column;
            Check(Distance(positions[index], CapeGeometry.RestPosition(index)) < 1e-5f,
                message + " at node " + index);
        }
    }

    private static float MaximumStructuralStretchRatio(ReadOnlySpan<float3> positions)
    {
        return MaximumStructuralStretchRatio(positions, out _, out _);
    }

    private static float MaximumStructuralStretchRatio(ReadOnlySpan<float3> positions, out int edgeA, out int edgeB)
    {
        float maximum = 0f;
        edgeA = -1;
        edgeB = -1;
        for (int row = 0; row < CapeGeometry.Rows; row++)
        for (int column = 0; column < CapeGeometry.Columns; column++)
        {
            int index = row * CapeGeometry.Columns + column;
            if (column + 1 < CapeGeometry.Columns)
            {
                float ratio = EdgeRatio(positions, index, index + 1);
                if (ratio > maximum) (maximum, edgeA, edgeB) = (ratio, index, index + 1);
            }
            if (row + 1 < CapeGeometry.Rows)
            {
                float ratio = EdgeRatio(positions, index, index + CapeGeometry.Columns);
                if (ratio > maximum) (maximum, edgeA, edgeB) = (ratio, index, index + CapeGeometry.Columns);
            }
        }
        return maximum;
    }

    private static float MaximumPinnedRowStretchRatio(ReadOnlySpan<float3> positions)
    {
        return MaximumPinnedRowStretchRatio(positions, out _);
    }

    private static float MaximumPinnedRowStretchRatio(ReadOnlySpan<float3> positions, out int column)
    {
        float maximum = 0f;
        column = -1;
        for (int candidateColumn = 0; candidateColumn < CapeGeometry.Columns; candidateColumn++)
        {
            int pinned = candidateColumn;
            float ratio = EdgeRatio(positions, pinned, pinned + CapeGeometry.Columns);
            if (ratio > maximum)
            {
                maximum = ratio;
                column = candidateColumn;
            }
        }
        return maximum;
    }

    private static void TrackStretch(ReadOnlySpan<float3> positions, int frame,
        ref float firstStep, ref float peak,
        ref float firstPinnedStep, ref float pinnedPeak,
        ref int peakFrame, ref int peakEdgeA, ref int peakEdgeB, ref int pinnedPeakColumn)
    {
        float stretch = MaximumStructuralStretchRatio(positions, out int edgeA, out int edgeB);
        float pinnedStretch = MaximumPinnedRowStretchRatio(positions, out int pinnedColumn);
        if (frame == 0)
        {
            firstStep = stretch;
            firstPinnedStep = pinnedStretch;
        }
        if (stretch > peak)
        {
            peak = stretch;
            peakFrame = frame;
            peakEdgeA = edgeA;
            peakEdgeB = edgeB;
        }
        if (pinnedStretch > pinnedPeak)
        {
            pinnedPeak = pinnedStretch;
            pinnedPeakColumn = pinnedColumn;
        }
    }

    private static void TrackStopStretch(ReadOnlySpan<float3> positions,
        ref float peak, ref float pinnedPeak)
    {
        peak = MathF.Max(peak, MaximumStructuralStretchRatio(positions));
        pinnedPeak = MathF.Max(pinnedPeak, MaximumPinnedRowStretchRatio(positions));
    }

    private static float EdgeRatio(ReadOnlySpan<float3> positions, int a, int b)
    {
        float restLength = Distance(CapeGeometry.RestPosition(a), CapeGeometry.RestPosition(b));
        return Distance(positions[a], positions[b]) / restLength;
    }

    private static void EnsureFinite(ReadOnlySpan<float3> positions, string message)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            float3 p = positions[i];
            Check(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z), message + " at " + i);
        }
    }

    private static float Distance(float3 a, float3 b) => (a - b).Length();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cape cloth check failed: " + message);
    }

    private sealed class CapeGroup : IDisposable
    {
        private readonly CapeCloth[] _capes;

        public CapeGroup(int count)
        {
            _capes = new CapeCloth[count];
            for (int i = 0; i < count; i++) _capes[i] = new CapeCloth();
        }

        public void Step(double dt, float3 velocity)
        {
            for (int i = 0; i < _capes.Length; i++) _capes[i].Step(dt, velocity);
        }

        public void Dispose()
        {
            for (int i = 0; i < _capes.Length; i++) _capes[i].Dispose();
        }
    }
}
