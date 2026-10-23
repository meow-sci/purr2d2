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
        ShimmerAmount = dark ? 1.5f : .5f;
        ShimmerSpeed = dark ? 1.5f : .6f;
        ArcCount = dark ? 3 : 1;
        ArcFrequency = dark ? 1.5f : .35f;
        ArcAmplitude = dark ? 1.3f : .5f;
        ArcBrightness = dark ? 1.1f : .65f;
        ArcWidth = dark ? 1 : .8f;
    }

    public void Validate(SaberType type)
    {
        bool dark = type == SaberType.Dark;
        ShimmerAmount = Clamp(ShimmerAmount, 0, 4, dark ? 1.5f : .5f);
        ShimmerSpeed = Clamp(ShimmerSpeed, 0, 4, dark ? 1.5f : .6f);
        ArcCount = Math.Clamp(ArcCount, 0, SaberSettings.MaximumArcCount);
        ArcFrequency = Clamp(ArcFrequency, .1f, 4, dark ? 1.5f : .35f);
        ArcAmplitude = Clamp(ArcAmplitude, 0, 3, dark ? 1.3f : .5f);
        ArcBrightness = Clamp(ArcBrightness, 0, 4, dark ? 1.1f : .65f);
        ArcWidth = Clamp(ArcWidth, .25f, 3, dark ? 1 : .8f);
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class SaberSettings
{
    public const int MaximumArcCount = 8;

    public float BladeLength { get; set; } = 1.4f;
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
    public bool HideMmu { get; set; } = true;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SaberEffectSettings LightEffects { get; set; } = SaberEffectSettings.Create(SaberType.Light);
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public SaberEffectSettings DarkEffects { get; set; } = SaberEffectSettings.Create(SaberType.Dark);

    public SaberEffectSettings EffectsFor(SaberType type)
    {
        if (type == SaberType.Dark) return DarkEffects ??= SaberEffectSettings.Create(SaberType.Dark);
        return LightEffects ??= SaberEffectSettings.Create(SaberType.Light);
    }

    public void Validate()
    {
        BladeLength = Clamp(BladeLength, .25f, SaberLimits.MaximumBladeLength, 1.4f);
        ColorR = Clamp(ColorR, 0, 1, .12f);
        ColorG = Clamp(ColorG, 0, 1, .75f);
        ColorB = Clamp(ColorB, 0, 1, 1);
        Volume = Clamp(Volume, 0, 1, .55f);
        GripX = Clamp(GripX, -40, 40, -24.5f); GripY = Clamp(GripY, -40, 40, -14.7f); GripZ = Clamp(GripZ, -40, 40, 16);
        AimX = Clamp(AimX, -1, 1, .22f); AimY = Clamp(AimY, -1, 1, .45f); AimZ = Clamp(AimZ, -1, 1, .6f);
        if (AimX * AimX + AimY * AimY + AimZ * AimZ < .0001f) { AimX = .22f; AimY = .45f; AimZ = .6f; }
        FingerCurl = Clamp(FingerCurl, 0, .5f, .12f);
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
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
