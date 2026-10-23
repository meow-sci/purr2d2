using System.Text.Json;

namespace Purr2D2.Core;

public sealed class SaberSettings
{
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
