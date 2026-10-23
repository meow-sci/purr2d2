using System.Runtime.InteropServices;
using Purr2D2.Rendering;

internal static class SaberVfxChecks
{
    public static void Main()
    {
        CpuShaderAbi();
        DetailPolicy();
        DetailBoundaries();
        StableTimeAndSeed();
        AllocationBudget();
        Console.WriteLine("Saber VFX checks passed (ABI, LOD bounds, retraction/end-on, clocks/seeds, zero allocations).");
    }

    private static void CpuShaderAbi()
    {
        Check(Marshal.SizeOf<GlowPush>() == 128, "production push constants fit Vulkan's guaranteed 128-byte range");
        foreach ((string field, int offset) in new[] { ("Mvp", 0), ("Color", 64), ("Shape", 80),
                     ("Animation", 96), ("Camera", 112) })
            Check(Marshal.OffsetOf<GlowPush>(field).ToInt32() == offset,
                $"production {field} offset matches the reflected shader block");
    }

    private static void DetailPolicy()
    {
        SaberVfxDetail near = SaberVfx.Detail(200, 8, 2, 1.4f);
        Check(near.ArcCount == 3 && near.Segments == 10 && near.ArcVisibility == 1,
            "a large nearby saber receives the full bounded effect");
        Check(SaberVfx.Detail(1, .1f, 2, 1.4f) == default,
            "subpixel sabers do not spend work or shimmer");
        Check(SaberVfx.Detail(200, 8, 64, 1.4f) == default,
            "distant sabers shed cosmetic detail");
        Check(SaberVfx.Detail(0, 48, 2, 1.4f).ArcCount > 0,
            "an end-on blade can retain detail through its visible tip diameter");
        foreach (float length in new[] { .0001f, .01f, .06f, .12f })
            Check(SaberVfx.Detail(200, 8, 2, length).ArcCount == 0,
                "retraction removes arcs before a short blade reaches the hilt");
        SaberVfxDetail firstView = SaberVfx.Detail(200, 8, 2, 1.4f);
        SaberVfxDetail secondView = SaberVfx.Detail(10, .2f, 35, 1.4f);
        Check(firstView.ArcCount > secondView.ArcCount
            && firstView == SaberVfx.Detail(200, 8, 2, 1.4f),
            "different viewports compute independent detail without retaining another view's state");

        foreach (float pixels in new[] { 0, 8, 24, 36, 80, 180, 2000, float.MaxValue })
        foreach (float radius in new[] { 0, .4f, 1, 1.8f, 2, 3.5f, 6, 500, float.MaxValue })
        foreach (double distance in new[] { 0, 8, 10, 18, 32, 40, 64, double.MaxValue })
        foreach (float length in new[] { .001f, .12f, .18f, .24f, 1.4f, 12f })
        {
            SaberVfxDetail detail = SaberVfx.Detail(pixels, radius, distance, length);
            Check(detail.ArcCount is >= 0 and <= 3 && detail.ArcVisibility is >= 0 and <= 1
                && detail.ShimmerStrength is >= 0 and <= .06f && detail.ArcLevel is >= 0 and <= 3,
                "all finite sizes, ranges, and supported blade lengths retain finite bounded detail");
            Check(detail.ArcCount == 0 ? detail.Segments == 0
                : detail.Segments is >= 6 and <= 10 && detail.ArcCount * 2 * (detail.Segments + 1) <= 66,
                "each saber stays within 66 procedural vertices and three strips");
        }
        foreach (float invalid in new[] { -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Check(SaberVfx.Detail(invalid, 8, 2, 1.4f) == default
                && SaberVfx.Detail(200, invalid, 2, 1.4f) == default
                && SaberVfx.Detail(200, 8, 2, invalid) == default,
                "invalid dimensions fail closed");
        }
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(SaberVfx.Detail(200, 8, invalid, 1.4f) == default, "invalid camera ranges fail closed");
        Check(SaberVfx.Detail(200, 8, 2, 0) == default, "a fully retracted blade has no VFX");
    }

    private static void DetailBoundaries()
    {
        // Validate visible strength rather than integer draw counts, which may change
        // while the newly admitted strip still contributes zero light.
        float previousVisibility = 1, previousShimmer = .06f;
        for (int step = 0; step <= 640; step++)
        {
            SaberVfxDetail detail = SaberVfx.Detail(200, 8, step * .1, 1.4f);
            Check(detail.ArcVisibility <= previousVisibility + 1e-6f
                && detail.ShimmerStrength <= previousShimmer + 1e-6f,
                "detail never grows as the camera recedes");
            Check(previousVisibility - detail.ArcVisibility < .01f,
                "distance culling fades continuously");
            previousVisibility = detail.ArcVisibility;
            previousShimmer = detail.ShimmerStrength;
        }
        previousVisibility = 0;
        for (int step = 0; step <= 300; step++)
        {
            SaberVfxDetail detail = SaberVfx.Detail(200, 8, 2, step * .001f);
            Check(detail.ArcVisibility + 1e-6f >= previousVisibility
                && detail.ArcVisibility - previousVisibility < .014f,
                "ignition and retraction fade arcs continuously at the short-blade boundary");
            previousVisibility = detail.ArcVisibility;
        }
    }

    private static void StableTimeAndSeed()
    {
        foreach (double time in new[] { 0, .1, 255.5, 255.9999999, 256, 256.5,
                     256 * 10000 + 255.9999999, 1e6, 1e12, double.MaxValue })
        {
            float clock = SaberVfx.Clock(time);
            Check(float.IsFinite(clock) && clock >= 0 && clock < 256,
                "long sessions keep bounded finite shader time");
        }
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(SaberVfx.Clock(invalid) == 0, "invalid clocks cannot poison rendering");
        Check(Math.Abs(SaberVfx.Clock(123.125) - SaberVfx.Clock(123.125 + 256 * 10000)) < 1e-6,
            "the periodic clock retains the same phase after many wraps");
        double uptime = 1e9;
        float advance = SaberVfx.Clock(uptime + 1.0 / 60) - SaberVfx.Clock(uptime);
        Check(advance > .016f && advance < .017f, "shader motion still advances at 60 Hz after years of uptime");
        var seeds = new HashSet<float>();
        Check(SaberVfx.Seed("kitten-one") == .7634735107421875f
            && SaberVfx.Seed("Kitten_123") == .3117218017578125f,
            "owner phases stay reproducible across process launches");
        for (int index = 0; index < 256; index++)
        {
            string id = "kitten-" + index;
            float seed = SaberVfx.Seed(id);
            Check(seed is >= 0 and < 1 && seed == SaberVfx.Seed(new string(id.ToCharArray())),
                "owner seeds depend on stable IDs rather than string instances");
            seeds.Add(seed);
        }
        Check(seeds.Count >= 245, "different kittens have distinct phases with a small collision budget");
    }

    private static void AllocationBudget()
    {
        for (int i = 0; i < 1000; i++) SaberVfx.Detail(200, 8, 2, 1.4f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float accumulated = 0;
        for (int i = 0; i < 10000; i++)
            accumulated += SaberVfx.Detail(200, 8, 2, 1.4f).ArcVisibility
                + SaberVfx.Clock(i * .016) + SaberVfx.Seed("kitten-one");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(accumulated > 0 && allocated == 0, "per-saber detail, time, and seed policy allocate no frame memory");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
