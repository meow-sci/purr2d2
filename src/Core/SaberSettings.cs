using System.Text.Json;

namespace Purr2D2.Core;

public sealed class SaberSettings
{
    public float BladeLength { get; set; } = .85f;
    public float ColorR { get; set; } = .12f;
    public float ColorG { get; set; } = .75f;
    public float ColorB { get; set; } = 1f;
    public float Volume { get; set; } = .55f;
    public bool CuttingEnabled { get; set; } = true;
    public bool FragmentStruckPart { get; set; }
    public bool ArbitrarySlicing { get; set; }
    public float ContactSeconds { get; set; } = .65f;
    public float GripX { get; set; } = -13;
    public float GripY { get; set; } = -8;
    public float GripZ { get; set; } = 18;
    public float AimX { get; set; } = .05f;
    public float AimY { get; set; } = .8f;
    public float AimZ { get; set; } = .6f;
    public float FingerCurl { get; set; } = .12f;
    public bool HideMmu { get; set; } = true;
    public float AutopilotClearance { get; set; } = .55f;
    public float AutopilotOrbitSpeed { get; set; } = .15f;
    public bool AutopilotAutoAxis { get; set; } = true;
    public int AutopilotAxis { get; set; }
    public bool AutopilotAxisReversed { get; set; }
    public bool AutopilotClockwise { get; set; }
    public int AutopilotRollQuarterTurns { get; set; }

    public void Validate()
    {
        BladeLength = Clamp(BladeLength, .25f, 1.5f, .85f);
        ColorR = Clamp(ColorR, 0, 1, .12f);
        ColorG = Clamp(ColorG, 0, 1, .75f);
        ColorB = Clamp(ColorB, 0, 1, 1);
        Volume = Clamp(Volume, 0, 1, .55f);
        ContactSeconds = Clamp(ContactSeconds, .1f, 5, .65f);
        GripX = Clamp(GripX, -40, 40, -13); GripY = Clamp(GripY, -40, 40, -8); GripZ = Clamp(GripZ, -40, 40, 18);
        AimX = Clamp(AimX, -1, 1, .05f); AimY = Clamp(AimY, -1, 1, .8f); AimZ = Clamp(AimZ, -1, 1, .6f);
        if (AimX * AimX + AimY * AimY + AimZ * AimZ < .0001f) { AimX = .05f; AimY = .8f; AimZ = .6f; }
        FingerCurl = Clamp(FingerCurl, 0, .5f, .12f);
        AutopilotClearance = Clamp(AutopilotClearance, .3f, 1.5f, .55f);
        AutopilotOrbitSpeed = Clamp(AutopilotOrbitSpeed, .04f, .4f, .15f);
        AutopilotAxis = Math.Clamp(AutopilotAxis, 0, 2);
        AutopilotRollQuarterTurns = ((AutopilotRollQuarterTurns % 4) + 4) % 4;
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
