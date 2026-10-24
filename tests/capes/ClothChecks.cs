using System.Diagnostics;
using Brutal.Numerics;
using Purr2D2.Capes;

internal static class ClothChecks
{
    private const double UpdateDt = 1d / 30d;

    public static void Main()
    {
        GeometryAndRestPose();
        SettlingAndMotion();
        ReverseSidewaysAndFastMotion();
        SafeInputsResetAndDispose();
        AssetHostChecks.Run();
        Benchmark(1);
        Benchmark(2);
        Benchmark(4);
        Benchmark(8);
        Console.WriteLine("Cape cloth checks passed against the current KSA and Bepu managed assemblies.");
    }

    private static void GeometryAndRestPose()
    {
        Check(CapeGeometry.Columns == 9 && CapeGeometry.Rows == 13 && CapeGeometry.NodeCount == 117,
            "the cape has the expected 9 by 13 mesh");
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
        for (int frame = 0; frame < 90; frame++)
        {
            still.Step(UpdateDt, float3.Zero);
            moving.Step(UpdateDt, new float3(0, 0, 3));
        }

        EnsureFinite(still.Positions, "stationary cloth remains finite");
        EnsureFinite(moving.Positions, "moving cloth remains finite");
        float stillHemY = MeanRowY(still.Positions, CapeGeometry.Rows - 1);
        float movingHemY = MeanRowY(moving.Positions, CapeGeometry.Rows - 1);
        float stillHemZ = MeanRowZ(still.Positions, CapeGeometry.Rows - 1);
        float movingHemZ = MeanRowZ(moving.Positions, CapeGeometry.Rows - 1);
        float movingMaximumY = MaximumY(moving.Positions);
        float movingStretchRatio = MaximumStructuralStretchRatio(moving.Positions);
        Console.WriteLine($"Cape motion check after 3s: stationary hem=({stillHemY:F3}y, {stillHemZ:F3}z), " +
            $"at 3m/s=({movingHemY:F3}y, {movingHemZ:F3}z; max y={movingMaximumY:F3}; max edge ratio={movingStretchRatio:F3})");
        Check(stillHemY < -.79f, $"stationary cape settles under gravity (hem Y {stillHemY:F3})");
        Check(movingHemZ < stillHemZ - .15f,
            $"walking speed trails the cape backward (still Z {stillHemZ:F3}, moving Z {movingHemZ:F3})");
        Check(movingHemY > stillHemY + .04f,
            $"walking speed reduces the cape's drop through bounded lift (still Y {stillHemY:F3}, moving Y {movingHemY:F3})");
        Check(movingMaximumY < .5f, $"walking speed does not launch the cape upward (max Y {movingMaximumY:F3})");
        Check(movingStretchRatio <= 1.3f,
            $"walking motion keeps structural edges within 1.3x rest length (ratio {movingStretchRatio:F3})");

        for (int frame = 0; frame < 90; frame++) moving.Step(UpdateDt, float3.Zero);
        float stoppedHemY = MeanRowY(moving.Positions, CapeGeometry.Rows - 1);
        Console.WriteLine($"Cape motion check after stopping for 3s: hem Y={stoppedHemY:F3}");
        Check(stoppedHemY < movingHemY - .04f,
            $"the cape drops again after movement stops (moving Y {movingHemY:F3}, stopped Y {stoppedHemY:F3})");
        EnsureFinite(moving.Positions, "stopped cloth remains finite");
        CheckBehindWearer(moving.Positions, "stopped cloth stays behind the wearer");
        Check(moving.Failure is null && still.Failure is null, "steady simulation does not report a failure");
    }

    private static void ReverseSidewaysAndFastMotion()
    {
        using var backward = new CapeCloth();
        using var sideways = new CapeCloth();
        using var fast = new CapeCloth();
        for (int frame = 0; frame < 90; frame++)
        {
            backward.Step(UpdateDt, new float3(0, 0, -3));
            sideways.Step(UpdateDt, new float3(3, 0, 0));
            fast.Step(UpdateDt, new float3(0, 0, 8));
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

        float backwardStretch = MaximumStructuralStretchRatio(backward.Positions);
        float sidewaysStretch = MaximumStructuralStretchRatio(sideways.Positions);
        float fastStretch = MaximumStructuralStretchRatio(fast.Positions);
        Console.WriteLine($"Cape stretch check after 3s: backward={backwardStretch:F3}, sideways={sidewaysStretch:F3}, 8m/s={fastStretch:F3}");
        Check(backwardStretch <= 1.3f, $"backward motion bounds structural stretch (ratio {backwardStretch:F3})");
        Check(sidewaysStretch <= 1.3f, $"sideways motion bounds structural stretch (ratio {sidewaysStretch:F3})");
        Check(fastStretch <= 1.3f, $"8m/s motion bounds structural stretch (ratio {fastStretch:F3})");
        Check(backward.Failure is null && sideways.Failure is null && fast.Failure is null,
            "reverse, sideways and maximum airflow do not poison simulation");

        float backwardHemY = MeanRowY(backward.Positions, CapeGeometry.Rows - 1);
        float sidewaysHemY = MeanRowY(sideways.Positions, CapeGeometry.Rows - 1);
        float fastHemY = MeanRowY(fast.Positions, CapeGeometry.Rows - 1);
        for (int frame = 0; frame < 90; frame++)
        {
            backward.Step(UpdateDt, float3.Zero);
            sideways.Step(UpdateDt, float3.Zero);
            fast.Step(UpdateDt, float3.Zero);
        }
        Check(MeanRowY(backward.Positions, CapeGeometry.Rows - 1) < backwardHemY - .04f,
            "cape drops after backward movement stops");
        Check(MeanRowY(sideways.Positions, CapeGeometry.Rows - 1) < sidewaysHemY - .04f,
            "cape drops after sideways movement stops");
        Check(MeanRowY(fast.Positions, CapeGeometry.Rows - 1) < fastHemY - .04f,
            "cape drops after maximum movement stops");
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
            Check(positions[i].Z <= -.22f + 1e-5f, message + " at node " + i);
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
        float maximum = 0f;
        for (int row = 0; row < CapeGeometry.Rows; row++)
        for (int column = 0; column < CapeGeometry.Columns; column++)
        {
            int index = row * CapeGeometry.Columns + column;
            if (column + 1 < CapeGeometry.Columns)
                maximum = MathF.Max(maximum, EdgeRatio(positions, index, index + 1));
            if (row + 1 < CapeGeometry.Rows)
                maximum = MathF.Max(maximum, EdgeRatio(positions, index, index + CapeGeometry.Columns));
        }
        return maximum;
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
