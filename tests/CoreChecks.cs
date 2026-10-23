using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

internal static class CoreChecks
{
    public static void Run()
    {
        BladeStateTransitions();
        SettingsValidationAndPersistence();
        FragmentGlbWriterRoundTrip();
    }

    private static void BladeStateTransitions()
    {
        var blade = new BladeState();
        Check(blade.Phase == "Unequipped" && blade.Deployment == 0 && !blade.TargetOn,
            "blade starts unequipped and retracted");

        blade.SetIgnited(true);
        blade.Advance(.1);
        Check(!blade.TargetOn && blade.Deployment == 0, "ignition is ignored while unequipped");

        blade.Equip(true);
        Check(blade.Phase == "Retracted", "equipping leaves the blade retracted");
        blade.SetIgnited(true);
        Check(blade.Phase == "Extending" && blade.Deployment == 0,
            "ignition begins at zero deployment without a flash");
        blade.Advance(.15);
        Check(Math.Abs(blade.Deployment - 1f / 3f) < 1e-6f, "extension advances continuously and caps large frame steps");
        blade.Advance(.15);
        Check(Math.Abs(blade.Deployment - 2f / 3f) < 1e-6f, "extension continues over time");

        blade.SetIgnited(false);
        Check(blade.Phase == "Retracting", "reversing ignition during extension starts retraction");
        blade.Advance(.15);
        Check(blade.Deployment > 0 && blade.Deployment < 2f / 3f,
            "retraction reverses smoothly from the current blade length");

        float pausedLength = blade.Deployment;
        blade.Advance(0);
        blade.Advance(-.1);
        blade.Advance(double.NaN);
        blade.Advance(double.PositiveInfinity);
        Check(blade.Deployment == pausedLength, "pause and invalid elapsed times do not advance the blade");

        blade.SetIgnited(true);
        float beforeInvalidDuration = blade.Deployment;
        blade.Advance(.1, float.NaN, .22f);
        Check(blade.Deployment == beforeInvalidDuration, "invalid deployment duration is ignored");
        blade.Advance(.15);
        Check(blade.Deployment > beforeInvalidDuration, "ignition can reverse during retraction");

        blade.Equip(false);
        Check(blade.Phase == "Unequipped" && blade.Deployment == 0 && !blade.TargetOn,
            "unequipping resets blade state and target");
        Pass("blade state transitions");
    }

    private static void SettingsValidationAndPersistence()
    {
        var defaults = new SaberSettings();
        Check(defaults.BladeLength == 1.4f && defaults.GripX == -24.5f && defaults.GripY == -14.7f && defaults.GripZ == 16
            && defaults.AimX == .22f && defaults.AimY == .45f && defaults.AimZ == .6f && defaults.FingerCurl == .12f,
            "new preferences use the chosen blade reach and measured kitten grip");
        Check(DefaultEffects(defaults.LightEffects, SaberType.Light) && DefaultEffects(defaults.DarkEffects, SaberType.Dark),
            "Light and Dark begin with distinct intentional effect profiles");
        Check(!ReferenceEquals(defaults.LightEffects, defaults.DarkEffects)
            && ReferenceEquals(defaults.LightEffects, defaults.EffectsFor(SaberType.Light))
            && ReferenceEquals(defaults.DarkEffects, defaults.EffectsFor(SaberType.Dark)),
            "all sabers of a type share one profile while the other type remains isolated");
        defaults.EffectsFor(SaberType.Light).ArcCount = 6;
        Check(defaults.EffectsFor(SaberType.Light).ArcCount == 6 && defaults.DarkEffects.ArcCount == 3,
            "editing a shared Light profile updates subsequent Light lookups without changing Dark");

        var invalidEffects = new SaberEffectSettings
        {
            ShimmerAmount = float.NaN, ShimmerSpeed = -1, ArcCount = 999,
            ArcFrequency = float.PositiveInfinity, ArcAmplitude = -1, ArcBrightness = 99, ArcWidth = 0,
        };
        invalidEffects.Validate(SaberType.Light);
        Check(invalidEffects.ShimmerAmount == .5f && invalidEffects.ShimmerSpeed == 0 && invalidEffects.ArcCount == 8
            && invalidEffects.ArcFrequency == .35f && invalidEffects.ArcAmplitude == 0
            && invalidEffects.ArcBrightness == 4 && invalidEffects.ArcWidth == .25f,
            "profile validation clamps invalid controls and uses that type's fallback values");
        var invalidDark = new SaberEffectSettings
        {
            ShimmerAmount = float.NegativeInfinity, ShimmerSpeed = float.NaN, ArcCount = -1,
            ArcFrequency = 0, ArcAmplitude = float.PositiveInfinity,
            ArcBrightness = float.NaN, ArcWidth = float.NaN,
        };
        invalidDark.Validate(SaberType.Dark);
        Check(invalidDark.ShimmerAmount == 1.5f && invalidDark.ShimmerSpeed == 1.5f && invalidDark.ArcCount == 0
            && invalidDark.ArcFrequency == .1f && invalidDark.ArcAmplitude == 1.3f
            && invalidDark.ArcBrightness == 1.1f && invalidDark.ArcWidth == 1,
            "Dark's nonfinite controls recover Dark defaults and zero arc count remains disabled");
        defaults.BladeLength = 2.5f; defaults.ColorR = .4f; defaults.Volume = .3f;
        defaults.GripX = -12; defaults.HideMmu = false;
        defaults.DarkEffects.ArcBrightness = 2.75f;
        var selectedProfile = defaults.EffectsFor(SaberType.Light);
        selectedProfile.Reset(SaberType.Light);
        Check(ReferenceEquals(selectedProfile, defaults.LightEffects) && DefaultEffects(selectedProfile, SaberType.Light)
            && defaults.DarkEffects.ArcBrightness == 2.75f && defaults.BladeLength == 2.5f && defaults.ColorR == .4f
            && defaults.Volume == .3f && defaults.GripX == -12 && !defaults.HideMmu,
            "reset selected effects preserves shared profile identity, the other type, color, reach, audio and grip");
        defaults.DarkEffects.Reset(SaberType.Dark);
        Check(DefaultEffects(defaults.DarkEffects, SaberType.Dark) && DefaultEffects(defaults.LightEffects, SaberType.Light),
            "resetting Dark restores Dark defaults without altering Light");
        Check(SaberLimits.AppliedDelta(true, 5, .2) == .2, "5x consumes all applied simulation time without clipping to 0.1 seconds");
        Check(SaberLimits.AppliedDelta(false, 5, .2) == 0 && SaberLimits.AppliedDelta(true, 0, .2) == 0
            && SaberLimits.AppliedDelta(true, 10, .1) == 0 && SaberLimits.AppliedDelta(true, 5, .3) == 0,
            "duplicate steps, pause, excessive warp and stalled frames do not advance cutting");
        var settings = new SaberSettings
        {
            BladeLength = 99,
            ColorR = -1,
            ColorG = float.NaN,
            ColorB = 2,
            Volume = -2,
        };
        settings.Validate();
        Check(settings.BladeLength == 12f && settings.ColorR == 0 && settings.ColorG == .75f && settings.ColorB == 1 && settings.Volume == 0,
            "settings clamp high, low, and nonfinite values to supported boundaries");

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-checks-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            Check(DefaultEffects(SaberSettings.Load(path).DarkEffects, SaberType.Dark),
                "a missing preferences file starts with Dark defaults");
            File.WriteAllText(path, "{\"BladeLength\":0.85,\"GripX\":-13,\"GripY\":-8,\"GripZ\":18,\"AimX\":0.05,\"AimY\":0.8,\"AimZ\":0.6,\"FingerCurl\":0.2}");
            var explicitPreferences = SaberSettings.Load(path);
            Check(explicitPreferences.BladeLength == .85f && explicitPreferences.GripX == -13 && explicitPreferences.GripY == -8
                && explicitPreferences.GripZ == 18 && explicitPreferences.AimX == .05f && explicitPreferences.AimY == .8f
                && explicitPreferences.AimZ == .6f && explicitPreferences.FingerCurl == .2f,
                "existing explicit preferences survive the new defaults unchanged");
            Check(DefaultEffects(explicitPreferences.LightEffects, SaberType.Light)
                && DefaultEffects(explicitPreferences.DarkEffects, SaberType.Dark),
                "legacy JSON without profiles acquires the distinct Light and Dark defaults");
            File.WriteAllText(path, "{\"LightEffects\":null,\"DarkEffects\":null}");
            var loaded = SaberSettings.Load(path);
            Check(DefaultEffects(loaded.LightEffects, SaberType.Light) && DefaultEffects(loaded.DarkEffects, SaberType.Dark)
                && !ReferenceEquals(loaded.LightEffects, loaded.DarkEffects),
                "null JSON profiles recover independent defaults before rendering");
            File.WriteAllText(path, "{\"LightEffects\":{\"ArcCount\":0},\"DarkEffects\":{\"ArcCount\":8}}");
            loaded = SaberSettings.Load(path);
            Check(loaded.LightEffects.ArcCount == 0 && loaded.LightEffects.ShimmerAmount == .5f
                && loaded.LightEffects.ArcFrequency == .35f && loaded.DarkEffects.ArcCount == 8
                && loaded.DarkEffects.ShimmerAmount == 1.5f && loaded.DarkEffects.ArcFrequency == 1.5f,
                "partial profiles preserve omitted members from the matching type's defaults");

            SetEffects(settings.LightEffects, 2.5f, .75f, 7, 1.75f, 2, .5f, 1.5f);
            SetEffects(settings.DarkEffects, 0, 0, 0, .25f, 0, 0, .25f);
            settings.Save(path);
            loaded = SaberSettings.Load(path);
            Check(loaded.BladeLength == 12f && loaded.ColorR == 0 && loaded.ColorG == .75f && loaded.ColorB == 1 && loaded.Volume == 0,
                "settings save and load preserves validated preferences");
            Check(loaded.LightEffects.ShimmerAmount == 2.5f && loaded.LightEffects.ShimmerSpeed == .75f
                && loaded.LightEffects.ArcCount == 7 && loaded.LightEffects.ArcFrequency == 1.75f
                && loaded.LightEffects.ArcAmplitude == 2 && loaded.LightEffects.ArcBrightness == .5f && loaded.LightEffects.ArcWidth == 1.5f,
                "all seven Light controls survive save and reload");
            Check(loaded.DarkEffects.ShimmerAmount == 0 && loaded.DarkEffects.ShimmerSpeed == 0
                && loaded.DarkEffects.ArcCount == 0 && loaded.DarkEffects.ArcFrequency == .25f
                && loaded.DarkEffects.ArcAmplitude == 0 && loaded.DarkEffects.ArcBrightness == 0 && loaded.DarkEffects.ArcWidth == .25f,
                "all seven Dark controls persist independently, including disabled effects and frozen shimmer");
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
                Check(saved.RootElement.TryGetProperty("LightEffects", out _) && saved.RootElement.TryGetProperty("DarkEffects", out _)
                    && !saved.RootElement.TryGetProperty("ShimmerAmount", out _),
                    "saved preferences contain two profiles rather than ambiguous top-level effect controls");

            File.WriteAllText(path, "{\"BladeLength\":-3,\"ColorR\":2,\"ColorG\":0.5,\"ColorB\":0.5,\"Volume\":2,\"ContactSeconds\":9}");
            loaded = SaberSettings.Load(path);
            Check(loaded.BladeLength == .25f && loaded.ColorR == 1 && loaded.Volume == 1,
                "loading old contact settings ignores retired fields and validates remaining preferences");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Pass("settings validation and profile persistence");
    }

    private static bool DefaultEffects(SaberEffectSettings settings, SaberType type) => type == SaberType.Dark
        ? settings.ShimmerAmount == 1.5f && settings.ShimmerSpeed == 1.5f && settings.ArcCount == 3
            && settings.ArcFrequency == 1.5f && settings.ArcAmplitude == 1.3f && settings.ArcBrightness == 1.1f && settings.ArcWidth == 1
        : settings.ShimmerAmount == .5f && settings.ShimmerSpeed == .6f && settings.ArcCount == 1
            && settings.ArcFrequency == .35f && settings.ArcAmplitude == .5f && settings.ArcBrightness == .65f && settings.ArcWidth == .8f;

    private static void SetEffects(SaberEffectSettings settings, float amount, float speed, int count,
        float frequency, float amplitude, float brightness, float width)
    {
        settings.ShimmerAmount = amount; settings.ShimmerSpeed = speed; settings.ArcCount = count;
        settings.ArcFrequency = frequency; settings.ArcAmplitude = amplitude;
        settings.ArcBrightness = brightness; settings.ArcWidth = width;
    }

    private static void FragmentGlbWriterRoundTrip()
    {
        var a = new SliceVertex(new(1, 2, 3), new(0, 0, 1), .1, .2);
        var b = new SliceVertex(new(2, 2, 3), new(0, 0, 1), .9, .2);
        var c = new SliceVertex(new(1, 3, 3), new(0, 0, 1), .1, .8);
        var face = new SliceFace(a, b, c, 7);
        var bytes = FragmentGlbWriter.Write(new[] { new FragmentGlbWriter.Group("SaberCutTest", 7, new[] { face }) });

        Check(ReadU32(bytes, 0) == 0x46546c67 && ReadU32(bytes, 4) == 2 && ReadU32(bytes, 8) == bytes.Length,
            "fragment writer emits a valid GLB 2 header and total length");
        int jsonLength = checked((int)ReadU32(bytes, 12));
        Check(jsonLength > 0 && jsonLength % 4 == 0 && ReadU32(bytes, 16) == 0x4e4f534a,
            "fragment GLB JSON chunk has the correct type and four-byte padding");
        int binaryHeader = checked(20 + jsonLength);
        int binaryLength = checked((int)ReadU32(bytes, binaryHeader));
        int binaryStart = binaryHeader + 8;
        Check(binaryLength % 4 == 0 && ReadU32(bytes, binaryHeader + 4) == 0x004e4942
            && binaryStart + binaryLength == bytes.Length,
            "fragment GLB binary chunk is typed, padded, and ends at the file boundary");

        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = json.RootElement;
        Check(root.GetProperty("asset").GetProperty("version").GetString() == "2.0",
            "fragment GLB declares glTF 2.0");
        var mesh = root.GetProperty("meshes")[0];
        Check(mesh.GetProperty("name").GetString() == "SaberCutTest"
            && root.GetProperty("nodes")[0].GetProperty("name").GetString() == "SaberCutTest"
            && root.GetProperty("nodes")[0].GetProperty("mesh").GetInt32() == 0,
            "fragment GLB node and mesh names match");
        var primitive = mesh.GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        int positionAccessor = attributes.GetProperty("POSITION").GetInt32();
        int normalAccessor = attributes.GetProperty("NORMAL").GetInt32();
        int uvAccessor = attributes.GetProperty("TEXCOORD_0").GetInt32();
        Check(accessors[positionAccessor].GetProperty("count").GetInt32() == 3
            && accessors[normalAccessor].GetProperty("count").GetInt32() == 3
            && accessors[uvAccessor].GetProperty("count").GetInt32() == 3,
            "fragment GLB retains nonempty position, normal, and UV0 streams");

        float Position(int vertex, int component) => ReadAttribute(positionAccessor, vertex, component, 3);
        float Normal(int vertex, int component) => ReadAttribute(normalAccessor, vertex, component, 3);
        float Uv(int vertex, int component) => ReadAttribute(uvAccessor, vertex, component, 2);
        float ReadAttribute(int accessorIndex, int vertex, int component, int width)
        {
            var accessor = accessors[accessorIndex];
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            int offset = checked((int)view.GetProperty("byteOffset").GetInt32()
                + vertex * width * sizeof(float) + component * sizeof(float));
            return ReadF32(bytes, binaryStart + offset);
        }

        Check(Position(0, 0) == 1 && Position(0, 1) == 2 && Position(0, 2) == 3
            && Position(2, 0) == 1 && Position(2, 1) == 3 && Position(2, 2) == 3,
            "fragment GLB round-trips triangle positions");
        Check(Normal(0, 0) == 0 && Normal(0, 1) == 0 && Normal(0, 2) == 1
            && Math.Abs(Uv(0, 0) - .1f) < 1e-6f && Math.Abs(Uv(2, 1) - .8f) < 1e-6f,
            "fragment GLB round-trips normals and UV coordinates");
        int indexAccessor = primitive.GetProperty("indices").GetInt32();
        var indexView = views[accessors[indexAccessor].GetProperty("bufferView").GetInt32()];
        int indexStart = binaryStart + indexView.GetProperty("byteOffset").GetInt32();
        Check(ReadU32(bytes, indexStart) == 0 && ReadU32(bytes, indexStart + 4) == 1
            && ReadU32(bytes, indexStart + 8) == 2,
            "fragment GLB has a round-trippable indexed triangle");

        var tinyFace = new SliceFace(
            new(new(0, 0, 0), new(0, 0, 1), 0, 0),
            new(new(1e-7, 0, 0), new(0, 0, 1), 1, 0),
            new(new(0, 1e-7, 0), new(0, 0, 1), 0, 1), 7);
        var tinyBytes = FragmentGlbWriter.Write(new[] { new FragmentGlbWriter.Group("TinyClip", 7, new[] { tinyFace }) });
        int tinyJsonLength = checked((int)ReadU32(tinyBytes, 12));
        using var tinyJson = JsonDocument.Parse(tinyBytes.AsMemory(20, tinyJsonLength));
        int tinyPosition = tinyJson.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var tinyAccessor = tinyJson.RootElement.GetProperty("accessors")[tinyPosition];
        int tinyView = tinyAccessor.GetProperty("bufferView").GetInt32();
        int tinyOffset = checked(20 + tinyJsonLength + 8
            + tinyJson.RootElement.GetProperty("bufferViews")[tinyView].GetProperty("byteOffset").GetInt32()
            + 3 * sizeof(float));
        Check(ReadF32(tinyBytes, tinyOffset) == (float)1e-7,
            "fragment GLB float encoding preserves a small clipped edge near the local origin");

        var d = new SliceVertex(new(2, 3, 3), new(0, 0, 1), 1, 1);
        var colliderBytes = FragmentGlbWriter.Write(new[]
        {
            new FragmentGlbWriter.Group("Collider", int.MinValue, new[] { face, new SliceFace(a, c, d, 7) }),
        });
        int colliderJsonLength = checked((int)ReadU32(colliderBytes, 12));
        using var colliderJson = JsonDocument.Parse(colliderBytes.AsMemory(20, colliderJsonLength));
        var colliderRoot = colliderJson.RootElement;
        var colliderPrimitive = colliderRoot.GetProperty("meshes")[0].GetProperty("primitives")[0];
        int colliderPosition = colliderPrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
        int colliderIndices = colliderPrimitive.GetProperty("indices").GetInt32();
        Check(colliderRoot.GetProperty("accessors")[colliderPosition].GetProperty("count").GetInt32() == 4
            && colliderRoot.GetProperty("accessors")[colliderIndices].GetProperty("count").GetInt32() == 6,
            "collider GLB deduplicates shared triangle vertices while retaining the full index stream");

        var cubePoints = new[]
        {
            new CutPoint(-1, -1, -1), new CutPoint(1, -1, -1), new CutPoint(1, 1, -1), new CutPoint(-1, 1, -1),
            new CutPoint(-1, -1, 1), new CutPoint(1, -1, 1), new CutPoint(1, 1, 1), new CutPoint(-1, 1, 1),
        };
        int[,] triangles =
        {
            { 0, 2, 1 }, { 0, 3, 2 }, { 4, 5, 6 }, { 4, 6, 7 },
            { 0, 4, 7 }, { 0, 7, 3 }, { 1, 2, 6 }, { 1, 6, 5 },
            { 0, 1, 5 }, { 0, 5, 4 }, { 3, 7, 6 }, { 3, 6, 2 },
        };
        var cubeFaces = new SliceFace[triangles.GetLength(0)];
        for (int i = 0; i < cubeFaces.Length; i++)
        {
            SliceVertex Vertex(int index) => new(cubePoints[index], new(0, 0, 1), 0, 0);
            cubeFaces[i] = new SliceFace(Vertex(triangles[i, 0]), Vertex(triangles[i, 1]), Vertex(triangles[i, 2]), 7);
        }
        var cubeBytes = FragmentGlbWriter.Write(new[]
        {
            new FragmentGlbWriter.Group("ColliderCube", int.MinValue, cubeFaces),
        });
        int cubeJsonLength = checked((int)ReadU32(cubeBytes, 12));
        using var cubeJson = JsonDocument.Parse(cubeBytes.AsMemory(20, cubeJsonLength));
        var cubeRoot = cubeJson.RootElement;
        var cubePrimitive = cubeRoot.GetProperty("meshes")[0].GetProperty("primitives")[0];
        int cubePosition = cubePrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
        int cubeIndices = cubePrimitive.GetProperty("indices").GetInt32();
        Check(cubeRoot.GetProperty("accessors")[cubePosition].GetProperty("count").GetInt32() == 8
            && cubeRoot.GetProperty("accessors")[cubeIndices].GetProperty("count").GetInt32() == 36,
            "collider GLB deduplicates a twelve-triangle cube to eight vertices and keeps all indices");

        ExpectInvalidData(() => FragmentGlbWriter.Write(Array.Empty<FragmentGlbWriter.Group>()), "empty output groups rejected");
        ExpectInvalidData(() => FragmentGlbWriter.Write(new[] { new FragmentGlbWriter.Group("Empty", 0, Array.Empty<SliceFace>()) }),
            "empty material group rejected");
        var nonFinite = face with { C = face.C with { U = double.NaN } };
        ExpectInvalidData(() => FragmentGlbWriter.Write(new[] { new FragmentGlbWriter.Group("BadUv", 0, new[] { nonFinite }) }),
            "non-finite fragment attributes rejected");
        Pass("fragment GLB writer structural round-trip and input checks");

        static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
        static float ReadF32(byte[] data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int))));
        static void ExpectInvalidData(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { Check(true, message); return; }
            Check(false, message);
        }
    }

    private static void Pass(string message) => Console.WriteLine("PASS: " + message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Core check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
