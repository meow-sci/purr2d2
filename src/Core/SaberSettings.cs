using System.Text.Json;
using System.Text.Json.Serialization;

namespace Purr2D2.Core;

public enum SaberType { Light, Dark }

public sealed class SaberEffectSettings
{
    public float ShimmerAmount { get; set; }
    public float ShimmerSpeed { get; set; }
    public int ArcCount { get; set; }
    public float ArcFrequency { get; set; }
    public float ArcAmplitude { get; set; }
    public float ArcBrightness { get; set; }
    public float ArcWidth { get; set; }
    // Dark-only cosmetic slag; kept in the shared profile for normal reset/autosave handling.
    public int SparkCount { get; set; }
    public float SparkFrequency { get; set; }
    public float SparkSize { get; set; }
    public float SparkBrightness { get; set; }
    public float SparkLifetime { get; set; }
    public float SparkSpeed { get; set; }

    public SaberEffectSettings() => Reset(SaberType.Light);

    public static SaberEffectSettings Create(SaberType type)
    {
        var effects = new SaberEffectSettings();
        effects.Reset(type);
        return effects;
    }

    public void Reset(SaberType type)
    {
        bool dark = type == SaberType.Dark;
        ShimmerAmount = 1.5f;
        ShimmerSpeed = 1.5f;
        ArcCount = dark ? 12 : 8;
        ArcFrequency = dark ? 6 : 3;
        ArcAmplitude = dark ? 1.45f : .8f;
        ArcBrightness = dark ? 3.5f : 2.5f;
        ArcWidth = dark ? 1.9f : 1.75f;
        SparkCount = dark ? 8 : 4;
        SparkFrequency = dark ? 7.2f : 4;
        SparkSize = dark ? .9f : 1;
        SparkBrightness = dark ? 2.1f : 1;
        SparkLifetime = dark ? .25f : .45f;
        SparkSpeed = dark ? 1.35f : 1;
    }

    public void Validate(SaberType type)
    {
        bool dark = type == SaberType.Dark;
        ShimmerAmount = Clamp(ShimmerAmount, 0, 4, 1.5f);
        ShimmerSpeed = Clamp(ShimmerSpeed, 0, 4, 1.5f);
        ArcCount = Math.Clamp(ArcCount, 0, SaberSettings.MaximumArcCount);
        ArcFrequency = Clamp(ArcFrequency, .1f, SaberSettings.MaximumArcFrequency, dark ? 6 : 3);
        ArcAmplitude = Clamp(ArcAmplitude, 0, 3, dark ? 1.45f : .8f);
        ArcBrightness = Clamp(ArcBrightness, 0, SaberSettings.MaximumArcBrightness, dark ? 3.5f : 2.5f);
        ArcWidth = Clamp(ArcWidth, .25f, SaberSettings.MaximumArcWidth, dark ? 1.9f : 1.75f);
        SparkCount = Math.Clamp(SparkCount, 0, SaberSettings.MaximumSparkCount);
        SparkFrequency = Clamp(SparkFrequency, 0, SaberSettings.MaximumSparkFrequency, dark ? 7.2f : 4);
        SparkSize = Clamp(SparkSize, .25f, 3, dark ? .9f : 1);
        SparkBrightness = Clamp(SparkBrightness, 0, 4, dark ? 2.1f : 1);
        SparkLifetime = Clamp(SparkLifetime, .1f, .8f, dark ? .25f : .45f);
        SparkSpeed = Clamp(SparkSpeed, .25f, 3, dark ? 1.35f : 1);
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class SaberSettings
{
    public const int MaximumArcCount = 20;
    public const float MaximumArcFrequency = 10;
    public const float MaximumArcBrightness = 6;
    public const float MaximumArcWidth = 4;
    public const int MaximumSparkCount = 8;
    public const float MaximumSparkFrequency = 12;
    public const int MinimumCapeSimulationLimit = 0;
    public const int MaximumCapeSimulationLimit = 16;
    public const int DefaultCapeSimulationLimit = 4;

    public float BladeLength { get; set; } = 1.4f;
    public float SliceReachMultiplier { get; set; } = SaberLimits.DefaultSliceReachMultiplier;
    public float ColorR { get; set; } = .12f;
    public float ColorG { get; set; } = .75f;
    public float ColorB { get; set; } = 1f;
    public float Volume { get; set; } = .55f;
    public float GripX { get; set; } = -24.5f;
    public float GripY { get; set; } = -14.7f;
    public float GripZ { get; set; } = 16;
    public float AimX { get; set; } = .22f;
    public float AimY { get; set; } = .45f;
    public float AimZ { get; set; } = .6f;
    public float FingerCurl { get; set; } = .12f;
    public float JumpComboIntervalSeconds { get; set; } = .75f;
    public bool SpaceSomersaultEnabled { get; set; } = true;
    public bool HideMmu { get; set; } = true;
    public float AngryForceRadius { get; set; } = 10;
    public int ForceCaptureLimit { get; set; } = ForceCaptureLimits.Default;
    public float ForceMaximumSpread { get; set; } = ForceSpreadLimits.DefaultMaximum;
    public float ClutterPickTolerance { get; set; } = .2f;
    public float ForceBreakupGroanPixels { get; set; } = 6f;
    public float ForceBreakupSplitPixels { get; set; } = 18f;
    public int ForceBreakupPieceLimit { get; set; } = ForceBreakupLimits.Default;
    public float ForceControlsOffsetX { get; set; }
    public float ForceControlsOffsetY { get; set; }
    public float SaberControlsOffsetX { get; set; }
    public float SaberControlsOffsetY { get; set; }
    public int SliceVertexArenaMiB { get; set; } = SliceResourceLimits.DefaultVertexArenaMiB;
    public int SliceIndexArenaMiB { get; set; } = SliceResourceLimits.DefaultIndexArenaMiB;
    public bool SliceAutoGrow { get; set; } = true;
    public int SliceVertexMaximumMiB { get; set; } = SliceResourceLimits.DefaultVertexMaximumMiB;
    public int SliceIndexMaximumMiB { get; set; } = SliceResourceLimits.DefaultIndexMaximumMiB;
    public int SliceAssetDiskMiB { get; set; } = SliceResourceLimits.DefaultAssetDiskMiB;
    public int SliceAssetEntryLimit { get; set; } = SliceResourceLimits.DefaultAssetEntryLimit;
    public int SlicePhysicsVehicleLimit { get; set; } = SliceResourceLimits.DefaultPhysicsVehicleLimit;
    public int CapeSimulationLimit { get; set; } = DefaultCapeSimulationLimit;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SaberEffectSettings LightEffects { get; set; } = SaberEffectSettings.Create(SaberType.Light);
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SaberEffectSettings DarkEffects { get; set; } = SaberEffectSettings.Create(SaberType.Dark);

    private static readonly JsonSerializerOptions PreferencesJsonOptions = new() { WriteIndented = true };

    public SaberEffectSettings EffectsFor(SaberType type)
    {
        if (type == SaberType.Dark) return DarkEffects ??= SaberEffectSettings.Create(SaberType.Dark);
        return LightEffects ??= SaberEffectSettings.Create(SaberType.Light);
    }

    public void Validate()
    {
        BladeLength = Clamp(BladeLength, .25f, SaberLimits.MaximumBladeLength, 1.4f);
        SliceReachMultiplier = SaberLimits.ValidateSliceReachMultiplier(SliceReachMultiplier);
        ColorR = Clamp(ColorR, 0, 1, .12f);
        ColorG = Clamp(ColorG, 0, 1, .75f);
        ColorB = Clamp(ColorB, 0, 1, 1);
        Volume = Clamp(Volume, 0, 1, .55f);
        GripX = Clamp(GripX, -40, 40, -24.5f); GripY = Clamp(GripY, -40, 40, -14.7f); GripZ = Clamp(GripZ, -40, 40, 16);
        AimX = Clamp(AimX, -1, 1, .22f); AimY = Clamp(AimY, -1, 1, .45f); AimZ = Clamp(AimZ, -1, 1, .6f);
        if (AimX * AimX + AimY * AimY + AimZ * AimZ < .0001f) { AimX = .22f; AimY = .45f; AimZ = .6f; }
        FingerCurl = Clamp(FingerCurl, 0, .5f, .12f);
        JumpComboIntervalSeconds = Clamp(JumpComboIntervalSeconds, .1f, 1.5f, .75f);
        AngryForceRadius = Clamp(AngryForceRadius, 1, 50, 10);
        ForceCaptureLimit = Math.Clamp(ForceCaptureLimit, ForceCaptureLimits.Minimum, ForceCaptureLimits.Maximum);
        ForceMaximumSpread = (float)ForceSpreadLimits.ValidateMaximum(ForceMaximumSpread);
        ClutterPickTolerance = Clamp(ClutterPickTolerance, 0, .5f, .2f);
        ForceBreakupGroanPixels = Clamp(ForceBreakupGroanPixels, 1, 64, 6f);
        ForceBreakupSplitPixels = Clamp(ForceBreakupSplitPixels, 2, 128, 18f);
        ForceBreakupSplitPixels = Math.Max(ForceBreakupSplitPixels, ForceBreakupGroanPixels + 1f);
        ForceBreakupPieceLimit = ForceBreakupLimits.Clamp(ForceBreakupPieceLimit);
        ForceControlsOffsetX = Clamp(ForceControlsOffsetX, -1, 1, 0);
        ForceControlsOffsetY = Clamp(ForceControlsOffsetY, -1, 1, 0);
        SaberControlsOffsetX = Clamp(SaberControlsOffsetX, -1, 1, 0);
        SaberControlsOffsetY = Clamp(SaberControlsOffsetY, -1, 1, 0);
        SliceVertexArenaMiB = Math.Clamp(SliceVertexArenaMiB, SliceResourceLimits.MinimumVertexArenaMiB, SliceResourceLimits.MaximumVertexArenaMiB);
        SliceIndexArenaMiB = Math.Clamp(SliceIndexArenaMiB, SliceResourceLimits.MinimumIndexArenaMiB, SliceResourceLimits.MaximumIndexArenaMiB);
        SliceVertexMaximumMiB = Math.Clamp(SliceVertexMaximumMiB, SliceVertexArenaMiB, SliceResourceLimits.MaximumVertexArenaMiB);
        SliceIndexMaximumMiB = Math.Clamp(SliceIndexMaximumMiB, SliceIndexArenaMiB, SliceResourceLimits.MaximumIndexArenaMiB);
        SliceAssetDiskMiB = Math.Clamp(SliceAssetDiskMiB, SliceResourceLimits.MinimumAssetDiskMiB, SliceResourceLimits.MaximumAssetDiskMiB);
        SliceAssetEntryLimit = Math.Clamp(SliceAssetEntryLimit, SliceResourceLimits.MinimumAssetEntryLimit, SliceResourceLimits.MaximumAssetEntryLimit);
        SlicePhysicsVehicleLimit = Math.Clamp(SlicePhysicsVehicleLimit, SliceResourceLimits.MinimumPhysicsVehicleLimit, SliceResourceLimits.MaximumPhysicsVehicleLimit);
        CapeSimulationLimit = Math.Clamp(CapeSimulationLimit, MinimumCapeSimulationLimit, MaximumCapeSimulationLimit);
        EffectsFor(SaberType.Light).Validate(SaberType.Light);
        EffectsFor(SaberType.Dark).Validate(SaberType.Dark);
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public static SaberSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<SaberSettings>(File.ReadAllText(path)) ?? new();
        value.Validate();
        return value;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Snapshot());
        File.Move(temporary, path, overwrite: true);
    }

    internal string Snapshot()
    {
        Validate();
        return JsonSerializer.Serialize(this, PreferencesJsonOptions);
    }
}
