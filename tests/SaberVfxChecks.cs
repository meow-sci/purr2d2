using System.Runtime.InteropServices;
using Brutal.Numerics;
using Purr2D2.Rendering;
using Purr2D2.Core;

internal static class SaberVfxChecks
{
    public static void Main()
    {
        CpuShaderAbi();
        DetailPolicy();
        TunedDetailPolicy();
        DetailBoundaries();
        StableTimeAndSeed();
        RateScaledClock();
        DarkSparkBudgetAndClock();
        DarkSparkLocalFrame();
        AllocationBudget();
        Console.WriteLine("Saber VFX checks passed (both profiles, ABI, bounded tuning/LOD, Dark spark budget/timing and landed/orbital frames, retraction/end-on, continuous clocks/seeds, zero allocations).");
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

    private static void TunedDetailPolicy()
    {
        var settings = new SaberSettings();
        var light = SaberVfxTuning.FromEffects(settings.LightEffects, SaberType.Light);
        var dark = SaberVfxTuning.FromEffects(settings.DarkEffects, SaberType.Dark);
        Check(light == SaberVfxTuning.LightDefault && dark == SaberVfxTuning.DarkDefault && light != dark,
            "render snapshots retain each shared type profile's distinct defaults");
        var lightDetail = SaberVfx.Detail(200, 8, 2, 1.4f, light);
        var darkDetail = SaberVfx.Detail(200, 8, 2, 1.4f, dark);
        Check(lightDetail.ArcCount == 8 && darkDetail.ArcCount == 12
            && lightDetail.ShimmerStrength == .06f * 1.5f && darkDetail.ShimmerStrength == .06f * 1.5f,
            "Light's calmer profile and Dark's stronger profile reach the detail policy independently");
        settings.LightEffects.ArcCount = 20;
        settings.LightEffects.ShimmerAmount = 4;
        var strong = SaberVfxTuning.FromEffects(settings.LightEffects, SaberType.Light);
        Check(light.ArcCount == 8 && strong.ArcCount == 20 && dark.ArcCount == 12,
            "immutable frame snapshots do not change underneath another type or viewport");
        var strongest = SaberVfx.Detail(200, 8, 2, 1.4f, strong);
        Check(strongest.ArcCount == 20 && strongest.Segments == 10
            && strongest.ArcCount * 2 * (strongest.Segments + 1) == 440,
            "maximum effects stay within twenty ribbon instances and 440 procedural vertices");
        var invalid = new SaberVfxTuning(float.NaN, -1, int.MaxValue, float.PositiveInfinity,
            -1, float.MaxValue, 0).Clamped();
        Check(invalid.ShimmerAmount == 1 && invalid.ShimmerSpeed == 0 && invalid.ArcCount == 20
            && invalid.ArcFrequency == 1 && invalid.ArcAmplitude == 0
            && invalid.ArcBrightness == 6 && invalid.ArcWidth == .25f,
            "render snapshots independently bound unvalidated settings before shader use");
        var maximum = new SaberVfxTuning(4, 4, 20, 10, 3, 6, 4).Clamped();
        Check(maximum.ArcCount == 20 && maximum.ArcFrequency == 10 && maximum.ArcBrightness == 6 && maximum.ArcWidth == 4,
            "render snapshots retain the expanded frequency, brightness and width limits");
        foreach (int count in new[] { -99, 0, 1, 8, 12, 20, int.MaxValue })
        foreach (float pixels in new[] { 0, 24, 80, 180, float.MaxValue })
        foreach (float radius in new[] { 0, 1, 2, 6, float.MaxValue })
        foreach (double distance in new[] { 0, 10, 40, double.MaxValue })
        foreach (float length in new[] { .001f, .12f, .18f, 1.4f, 12f })
        {
            var tuning = new SaberVfxTuning(4, 4, count, 10, 3, 6, 4);
            var detail = SaberVfx.Detail(pixels, radius, distance, length, tuning);
            Check(detail.ArcCount is >= 0 and <= 20 && detail.ArcVisibility is >= 0 and <= 1
                && detail.ShimmerStrength is >= 0 and <= .24f && detail.ArcLevel is >= 0 and <= 20,
                "all supported tuning, footprints, ranges and lengths produce finite bounded detail");
            Check(detail.ArcCount == 0 ? detail.Segments == 0
                : detail.Segments is >= 6 and <= 10 && detail.ArcCount * 2 * (detail.Segments + 1) <= 440,
                "tuning cannot exceed the GPU arc vertex budget");
        }
        foreach (var disabled in new[] { dark with { ArcCount = 0 }, dark with { ArcBrightness = 0 } })
        {
            var detail = SaberVfx.Detail(200, 8, 2, 1.4f, disabled);
            Check(detail.ArcCount == 0 && detail.Segments == 0 && detail.ArcVisibility == 0 && detail.ArcLevel == 0
                && detail.ShimmerStrength == darkDetail.ShimmerStrength,
                "zero arc count or brightness removes the arc draw budget while preserving independent shimmer");
        }
        var noShimmer = SaberVfx.Detail(200, 8, 2, 1.4f, dark with { ShimmerAmount = 0 });
        Check(noShimmer.ShimmerStrength == 0 && noShimmer.ArcCount == darkDetail.ArcCount,
            "disabling shimmer preserves the chosen arc style and budget");
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

    private static void RateScaledClock()
    {
        var clock = new SaberVfxClock();
        clock.Advance(0, .6f, .35f);
        clock.Advance(255.5, .6f, .35f);
        float shimmer = clock.ShimmerTime, burst = clock.BurstTime;
        clock.Advance(256.5, .6f, .35f);
        Check(Near(PhaseDelta(shimmer, clock.ShimmerTime), .6f)
            && Near(PhaseDelta(burst, clock.BurstTime), .35f),
            "fractional rates advance continuously through the unscaled clock's wrap boundary");
        shimmer = clock.ShimmerTime; burst = clock.BurstTime;
        clock.Advance(256.5, 3.25f, 2.75f);
        Check(clock.ShimmerTime == shimmer && clock.BurstTime == burst,
            "changing rates while paused cannot jump either existing phase");
        clock.Advance(257, 3.25f, 2.75f);
        Check(Near(PhaseDelta(shimmer, clock.ShimmerTime), 1.625f)
            && Near(PhaseDelta(burst, clock.BurstTime), 1.375f),
            "new rates affect only elapsed time after a settings change");
        shimmer = clock.ShimmerTime; burst = clock.BurstTime;
        clock.Advance(258, 0, .5f);
        Check(clock.ShimmerTime == shimmer && Near(PhaseDelta(burst, clock.BurstTime), .5f),
            "zero shimmer speed freezes its phase while arc frequency still advances");
        clock.Advance(259, 0, .5f);
        Check(clock.ShimmerTime == shimmer, "multiple frozen frames do not reset shimmer to a fixed pattern");
        clock.Advance(259.25, 2, .5f);
        Check(Near(PhaseDelta(shimmer, clock.ShimmerTime), .5f), "unfreezing resumes continuously from the retained phase");
        shimmer = clock.ShimmerTime; burst = clock.BurstTime;
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            clock.Advance(invalid, 4, 4);
            Check(clock.ShimmerTime == shimmer && clock.BurstTime == burst, "invalid world clocks preserve both visual phases");
        }
        clock.Advance(900, 4, 4);
        Check(clock.ShimmerTime == shimmer && clock.BurstTime == burst,
            "the first valid clock after invalid data reanchors without a catch-up jump");
        clock.Advance(900.125, 4, 4);
        Check(Near(PhaseDelta(shimmer, clock.ShimmerTime), .5f), "motion resumes normally after invalid clock recovery");
        shimmer = clock.ShimmerTime; burst = clock.BurstTime;
        clock.Advance(0, 4, 4);
        Check(clock.ShimmerTime == shimmer && clock.BurstTime == burst,
            "a world-clock reset reanchors without rewinding visible animation");
        clock.Advance(.125, 4, 4);
        Check(Near(PhaseDelta(shimmer, clock.ShimmerTime), .5f), "time after a world-clock reset advances from the preserved phase");

        var fastest = new SaberVfxClock();
        fastest.Advance(0, 4, 10);
        fastest.Advance(.125, 4, 10);
        Check(Near(fastest.ShimmerTime, .5f) && Near(fastest.BurstTime, 1.25f),
            "the maximum arc frequency reaches the shader clock without a lower internal clamp");

        foreach (float rate in new[] { .1f, .35f, .6f, 1.5f, 4 })
        foreach (double uptime in new[] { 1e9, 1e12, double.MaxValue })
        {
            var longSession = new SaberVfxClock();
            longSession.Advance(uptime, rate, rate);
            Check(float.IsFinite(longSession.ShimmerTime) && longSession.ShimmerTime is >= 0 and < 256
                && float.IsFinite(longSession.BurstTime) && longSession.BurstTime is >= 0 and < 256,
                "fractional rates and very large initial clocks cannot overflow shader phases");
            if (uptime == double.MaxValue) continue;
            float before = longSession.ShimmerTime;
            longSession.Advance(uptime + .125, rate, rate);
            Check(Near(PhaseDelta(before, longSession.ShimmerTime), .125f * rate),
                "rate-scaled phases retain sub-frame precision after years of uptime");
        }
        var lightClock = new SaberVfxClock(); var darkClock = new SaberVfxClock();
        lightClock.Advance(100, .6f, .35f); darkClock.Advance(100, 1.5f, 1.5f);
        float lightTime = lightClock.ShimmerTime;
        darkClock.Advance(101, 4, 4);
        Check(lightClock.ShimmerTime == lightTime, "editing Dark's clock cannot advance Light's retained profile state");
        lightClock = default;
        lightClock.Advance(0, .6f, .35f);
        Check(lightClock.ShimmerTime == 0 && lightClock.BurstTime == 0, "world reset can clear both profile phases for a new session");
    }

    private static float PhaseDelta(float before, float after) => (after - before + 256) % 256;

    private static void DarkSparkBudgetAndClock()
    {
        var effects = SaberEffectSettings.Create(SaberType.Dark);
        var tuning = DarkSaberSparkTuning.FromEffects(effects);
        Check(tuning == new DarkSaberSparkTuning(8, 7.2f, .9f, 2.1f, .25f, 1.35f) && tuning.Enabled,
            "default Dark sparks use eight short-lived embers at 7.2 bursts per second with the configured size, brightness, and speed");
        effects.SparkCount = int.MaxValue; effects.SparkFrequency = float.MaxValue;
        effects.SparkSize = float.NaN; effects.SparkBrightness = -1;
        effects.SparkLifetime = float.PositiveInfinity; effects.SparkSpeed = 99;
        effects.Validate(SaberType.Dark);
        Check(DarkSaberSparkTuning.FromEffects(effects) == new DarkSaberSparkTuning(8, 12, .9f, 0, .25f, 3),
            "invalid spark settings cannot exceed the supported emission and size bounds");
        Check(DarkSaberSparkTuning.MaximumBursts * SaberSettings.MaximumSparkCount == 128,
            "all kittens and viewports share a hard ceiling of 128 live sparks");
        foreach (var disabled in new[] { tuning with { Count = 0 }, tuning with { Frequency = 0 }, tuning with { Brightness = 0 } })
            Check(!disabled.Enabled, "zero amount, frequency, or intensity disables new spark work");
        Check(DarkSaberSparkTuning.Visibility(12) == 1 && DarkSaberSparkTuning.Visibility(18) == .5f
            && DarkSaberSparkTuning.Visibility(24) == 0 && DarkSaberSparkTuning.Visibility(double.NaN) == 0,
            "distant sparks fade and are culled before recording GPU commands");
        var clock = new DarkSaberSparkClock();
        Check(clock.TryEmit(10, 4, .25f, out float first), "a newly visible saber can emit immediately");
        for (int view = 0; view < 4; view++)
            Check(!clock.TryEmit(10, 4, .25f, out _), "extra viewports never duplicate emission in one frame");
        Check(!clock.TryEmit(10.249, 4, .25f, out _)
            && clock.TryEmit(10.25, 4, .25f, out float next) && first != next,
            "frequency is bursts per second and each burst has a fresh deterministic seed");
        Check(clock.TryEmit(100, 4, .25f, out _)
            && !clock.TryEmit(100, 4, .25f, out _), "a hitch produces one burst without replaying missed windows");
        Check(!clock.TryEmit(100, 12, .25f, out _) && clock.TryEmit(100.084, 12, .25f, out _),
            "frequency edits take effect without duplicating the current burst");
        Check(clock.TryEmit(0, 4, .25f, out _) && !clock.TryEmit(0, 4, .25f, out _),
            "world clock resets reanchor spark emission without freezing it");
        Check(!clock.TryEmit(double.NaN, 4, .25f, out _) && !clock.TryEmit(0, 0, .25f, out _),
            "invalid clocks and disabled frequency fail closed");
        for (int i = 0; i < 1000; i++) clock.TryEmit(i, 12, .25f, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float total = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (clock.TryEmit(i, 12, .25f, out float seed)) total += seed;
            total += DarkSaberSparkTuning.FromEffects(effects).Speed + DarkSaberSparkTuning.Visibility(i % 30);
        }
        Check(total > 0 && GC.GetAllocatedBytesForCurrentThread() == before,
            "spark scheduling, snapshots and culling allocate no memory per frame");
    }
    private static void DarkSparkLocalFrame()
    {
        var ownerAtBirth = new double3(5, -2, 10);
        var offset = new double3(.25, .5, 1);
        Check(DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth + offset,
            new double3(0, 6000000, 0), out var frame), "capture a camera-relative emission frame");
        foreach (double orbitalSpeed in new[] { 0d, 7500, 30000, 100000 })
        foreach (double age in new[] { 0d, 1d / 60, .25, .8 })
        foreach (double simulationRate in new[] { 0d, 1, 1000 })
        {
            var translation = new double3(orbitalSpeed, orbitalSpeed * .5, -orbitalSpeed * .25)
                * (age * simulationRate);
            foreach (var cameraShift in new[] { double3.Zero, translation, new double3(19, -7, 4) })
            {
                var ownerNow = ownerAtBirth + translation - cameraShift;
                var origin = frame.Origin(ownerNow);
                Check((origin - ownerNow - offset).Length() < 1e-7,
                    "landed, orbital, paused and warped sparks retain their small birth offset in each viewport");
            }
        }
        Check(frame.Origin(ownerAtBirth) == ownerAtBirth + offset,
            "visual aging between physics steps cannot advance an orbital velocity");
        Check(DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth + new double3(-1, 2, 3),
                double3.UnitZ, out var nextFrame)
            && frame.Offset == offset && frame.Up == double3.UnitY && nextFrame.Offset != frame.Offset,
            "moving or rotating the paw cannot drag an existing burst's birth offset or spray direction");
        foreach (var up in new[] { double3.UnitX, -double3.UnitX, double3.UnitY, double3.UnitZ, new double3(2, -3, 4) })
        {
            Check(DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth + offset, up, out var basis)
                && Math.Abs(basis.Right.Length() - 1) < 1e-12 && Math.Abs(basis.Up.Length() - 1) < 1e-12
                && Math.Abs(double3.Dot(basis.Right, basis.Up)) < 1e-12,
                "spray/fall basis remains orthonormal at every celestial pole and orbital orientation");
        }
        Check(!DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth, double3.Zero, out _)
            && !DarkSaberSparkFrame.TryCreate(new double3(double.NaN, 0, 0), ownerAtBirth, double3.UnitY, out _)
            && !DarkSaberSparkFrame.TryCreate(ownerAtBirth, new double3(double.PositiveInfinity, 0, 0), double3.UnitY, out _),
            "invalid local frames fail closed");
        for (int i = 0; i < 1000; i++) DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth + offset, double3.UnitY, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = 0;
        for (int i = 0; i < 10000; i++)
        {
            DarkSaberSparkFrame.TryCreate(ownerAtBirth, ownerAtBirth + offset, double3.UnitY, out var sampled);
            sum += sampled.Origin(ownerAtBirth).X;
        }
        Check(sum > 0 && GC.GetAllocatedBytesForCurrentThread() == before,
            "owner-relative spark frames allocate no memory per submission or draw");
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .0001f;

    private static void AllocationBudget()
    {
        for (int i = 0; i < 1000; i++) SaberVfx.Detail(200, 8, 2, 1.4f);
        var clock = new SaberVfxClock();
        var tuning = SaberVfxTuning.DarkDefault;
        for (int i = 0; i < 1000; i++) clock.Advance(i * .016, .6f, .35f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float accumulated = 0;
        for (int i = 0; i < 10000; i++)
        {
            clock.Advance(i * .016, .6f, .35f);
            accumulated += SaberVfx.Detail(200, 8, 2, 1.4f, tuning).ArcVisibility
                + clock.ShimmerTime + clock.BurstTime + SaberVfx.Clock(i * .016) + SaberVfx.Seed("kitten-one");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(accumulated > 0 && allocated == 0, "per-saber detail, time, and seed policy allocate no frame memory");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
