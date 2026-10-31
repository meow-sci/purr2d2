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
        SettingsAutosaveSnapshots();
        SliceResourcePreferences();
        FragmentGlbWriterRoundTrip();
    }

    private static void BladeStateTransitions()
    {
        var blade = new BladeState();
        Check(blade.Phase == "Unequipped" && blade.Deployment == 0 && !blade.TargetOn,
            "blade starts unequipped and retracted");
        Check(!blade.SetManualDeploymentTarget(.5f) && blade.ManualTargetDeployment == 0,
            "manual target changes are rejected while the saber is unequipped");

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

        Check(blade.HoldDeployment(.37f) && blade.ManualDeployment && blade.TargetOn
            && blade.ManualTargetDeployment == .37f && blade.Deployment == .37f && blade.Phase == "Partial",
            "the immediate setup API holds an exact partial fraction and reports a partial phase");
        blade.Advance(.1);
        Check(blade.Deployment == .37f, "automatic extension does not advance a held manual fraction");
        Check(!blade.HoldDeployment(float.NaN) && blade.Deployment == .37f && blade.ManualTargetDeployment == .37f,
            "nonfinite manual deployment is rejected without changing the held fraction");
        Check(blade.HoldDeployment(2) && blade.ManualTargetDeployment == 1 && blade.Deployment == 1 && blade.TargetOn,
            "manual deployment clamps above one to the fully extended endpoint");
        Check(blade.HoldDeployment(0) && blade.ManualDeployment && !blade.TargetOn && blade.ManualTargetDeployment == 0
            && blade.Deployment == 0 && blade.Equipped && blade.Phase == "Retracted",
            "manual zero keeps the saber equipped while lowering the blade exactly");
        blade.SetIgnited(false);
        Check(!blade.ManualDeployment && !blade.TargetOn,
            "an unchanged off request resumes automatic deployment mode");
        Check(blade.HoldDeployment(.4f), "a held fraction can be set again");
        blade.SetIgnited(true);
        Check(!blade.ManualDeployment && blade.TargetOn && blade.Deployment == .4f,
            "an unchanged on request clears manual mode without snapping the blade");
        blade.Advance(.1);
        Check(blade.Deployment > .4f, "ignition after manual hold resumes ordinary extension");

        float beforeManualTarget = blade.Deployment;
        Check(blade.SetManualDeploymentTarget(.25f) && blade.ManualDeployment && blade.TargetOn
            && blade.ManualTargetDeployment == .25f && blade.Deployment == beforeManualTarget
            && blade.Phase == "Retracting",
            "the animated manual setter records a lower target without snapping the visible blade");
        blade.Advance(0); blade.Advance(-.1); blade.Advance(double.NaN); blade.Advance(double.PositiveInfinity);
        Check(blade.Deployment == beforeManualTarget,
            "pause and invalid time keep an in-flight manual blade frozen");
        blade.Advance(.05);
        float beforeRetarget = blade.Deployment;
        Check(blade.Deployment < beforeManualTarget && blade.Deployment > .25f,
            "manual travel moves monotonically toward a lower partial target without overshooting");
        Check(blade.SetManualDeploymentTarget(.8f) && blade.Deployment == beforeRetarget
            && blade.ManualTargetDeployment == .8f && blade.Phase == "Extending",
            "retargeting reverses from the current fraction without a discontinuity");
        blade.Advance(.05);
        float afterRetargetStep = blade.Deployment;
        Check(afterRetargetStep > beforeRetarget && afterRetargetStep < .8f,
            "a reversed manual target continues at the fixed deployment rate");
        Check(blade.SetManualDeploymentTarget(.8f), "repeating the same manual target remains accepted");
        blade.Advance(.05);
        Check(blade.Deployment == .8f && blade.Phase == "Partial",
            "manual travel clamps exactly at its target and reports a settled partial phase");
        for (int i = 0; i < 20; i++) blade.Advance(.1);
        Check(blade.Deployment == .8f,
            "a settled manual fraction holds indefinitely without a transition timer");
        Check(!blade.SetManualDeploymentTarget(float.NaN) && blade.ManualTargetDeployment == .8f
            && blade.Deployment == .8f,
            "nonfinite animated targets are rejected without changing current or target deployment");

        Check(blade.SetManualDeploymentTarget(0) && !blade.TargetOn && blade.Phase == "Retracting"
            && blade.Equipped && blade.Deployment == .8f,
            "manual zero begins a smooth retraction while preserving the equipped hilt");
        blade.Advance(.1);
        Check(blade.Deployment > 0 && blade.Deployment < .8f,
            "manual zero retracts at the same fixed rate as extension");
        blade.Advance(.05);
        Check(blade.Deployment == 0 && blade.Phase == "Retracted" && blade.Equipped,
            "manual retraction reaches exact zero and retains the raised hilt");

        Check(blade.SetManualDeploymentTarget(1) && blade.Deployment == 0
            && blade.Phase == "Extending", "full-range manual deployment starts from the current zero fraction");
        blade.Advance(.1);
        Check(Math.Abs(blade.Deployment - 2f / 3f) < 1e-6f && blade.Phase == "Extending",
            "manual deployment advances by a capped fixed-rate step");
        blade.Advance(.05);
        Check(blade.Deployment == 1 && blade.Phase == "Lit",
            "full-range manual deployment reaches the exact endpoint in 0.15 player seconds");
        blade.SetIgnited(false);
        Check(!blade.ManualDeployment && !blade.TargetOn && blade.Deployment == 1,
            "ordinary toggles supersede a manual target without snapping the visible fraction");
        blade.Advance(.1);
        Check(blade.Deployment < 1 && blade.Deployment > 0,
            "ordinary retraction resumes from the actual manual fraction using its normal timing");

        blade.HoldDeployment(.4f);
        Check(blade.ManualTargetDeployment == .4f && blade.Deployment == .4f,
            "the immediate setup API continues to snap actual state and synchronizes its target");
        long beforeManualAllocations = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) blade.Advance(.01);
        long manualAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeManualAllocations;
        Check(manualAllocations == 0,
            "warmed manual target advancement allocates no managed memory");

        blade.Equip(false);
        Check(blade.Phase == "Unequipped" && blade.Deployment == 0 && !blade.TargetOn
            && !blade.ManualDeployment && blade.ManualTargetDeployment == 0,
            "unequipping resets blade fraction, target and mode");
        Pass("blade state transitions");
    }

    private static void SettingsValidationAndPersistence()
    {
        var defaults = new SaberSettings();
        Check(!defaults.DebugTimingsEnabled, "debug timing collection and console output are off by default");
        Check(defaults.BladeLength == 1.4f && defaults.GripX == -24.5f && defaults.GripY == -14.7f && defaults.GripZ == 16
            && defaults.AimX == .22f && defaults.AimY == .45f && defaults.AimZ == .6f && defaults.FingerCurl == .12f,
            "new preferences use the chosen blade reach and measured kitten grip");
        Check(defaults.AngryForceRadius == 10 && defaults.ForceControlsOffsetX == 0 && defaults.ForceControlsOffsetY == 0
            && defaults.SaberControlsOffsetX == 0 && defaults.SaberControlsOffsetY == 0,
            "new preferences use a ten metre Angry Force radius and default control positions");
        Check(defaults.ClutterPickTolerance == .2f, "new preferences allow a twenty centimetre near miss on clutter");
        Check(defaults.ForceCaptureLimit == 128, "new preferences increase the combined Force capture limit to 128");
        Check(defaults.SliceReachMultiplier == 2, "new preferences preserve the existing two-times assisted slice reach");
        Check(defaults.JumpComboIntervalSeconds == .75f, "new preferences use a 0.75-second somersault combo interval");
        Check(DefaultEffects(defaults.LightEffects, SaberType.Light) && DefaultEffects(defaults.DarkEffects, SaberType.Dark),
            "Light and Dark begin with distinct intentional effect profiles");
        Check(!ReferenceEquals(defaults.LightEffects, defaults.DarkEffects)
            && ReferenceEquals(defaults.LightEffects, defaults.EffectsFor(SaberType.Light))
            && ReferenceEquals(defaults.DarkEffects, defaults.EffectsFor(SaberType.Dark)),
            "all sabers of a type share one profile while the other type remains isolated");
        defaults.EffectsFor(SaberType.Light).ArcCount = 6;
        Check(defaults.EffectsFor(SaberType.Light).ArcCount == 6 && defaults.DarkEffects.ArcCount == 12,
            "editing a shared Light profile updates subsequent Light lookups without changing Dark");

        var invalidEffects = new SaberEffectSettings
        {
            ShimmerAmount = float.NaN, ShimmerSpeed = -1, ArcCount = 999,
            ArcFrequency = float.PositiveInfinity, ArcAmplitude = -1, ArcBrightness = 99, ArcWidth = 0,
        };
        invalidEffects.Validate(SaberType.Light);
        Check(invalidEffects.ShimmerAmount == 1.5f && invalidEffects.ShimmerSpeed == 0 && invalidEffects.ArcCount == 20
            && invalidEffects.ArcFrequency == 3 && invalidEffects.ArcAmplitude == 0
            && invalidEffects.ArcBrightness == 6 && invalidEffects.ArcWidth == .25f,
            "profile validation clamps invalid controls and uses that type's fallback values");
        var invalidDark = new SaberEffectSettings
        {
            ShimmerAmount = float.NegativeInfinity, ShimmerSpeed = float.NaN, ArcCount = -1,
            ArcFrequency = 0, ArcAmplitude = float.PositiveInfinity,
            ArcBrightness = float.NaN, ArcWidth = float.NaN,
        };
        invalidDark.Validate(SaberType.Dark);
        Check(invalidDark.ShimmerAmount == 1.5f && invalidDark.ShimmerSpeed == 1.5f && invalidDark.ArcCount == 0
            && invalidDark.ArcFrequency == .1f && invalidDark.ArcAmplitude == 1.45f
            && invalidDark.ArcBrightness == 3.5f && invalidDark.ArcWidth == 1.9f,
            "Dark's nonfinite controls recover Dark defaults and zero arc count remains disabled");
        var maximumEffects = new SaberEffectSettings
        {
            ArcCount = 20, ArcFrequency = 10, ArcBrightness = 6, ArcWidth = 4,
        };
        maximumEffects.Validate(SaberType.Light);
        Check(maximumEffects.ArcCount == 20 && maximumEffects.ArcFrequency == 10
            && maximumEffects.ArcBrightness == 6 && maximumEffects.ArcWidth == 4,
            "the expanded arc limits remain available after validation");
        maximumEffects.ArcFrequency = 99; maximumEffects.ArcWidth = 99;
        maximumEffects.Validate(SaberType.Dark);
        Check(maximumEffects.ArcFrequency == 10 && maximumEffects.ArcWidth == 4,
            "frequency and width cannot exceed the expanded render limits");
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
            AngryForceRadius = 100,
            ForceControlsOffsetX = -2,
            ForceControlsOffsetY = 2,
            SaberControlsOffsetX = float.NaN,
            SaberControlsOffsetY = float.PositiveInfinity,
        };
        settings.Validate();
        Check(settings.BladeLength == 12f && settings.ColorR == 0 && settings.ColorG == .75f && settings.ColorB == 1 && settings.Volume == 0,
            "settings clamp high, low, and nonfinite values to supported boundaries");
        Check(settings.AngryForceRadius == 50 && settings.ForceControlsOffsetX == -1 && settings.ForceControlsOffsetY == 1
            && settings.SaberControlsOffsetX == 0 && settings.SaberControlsOffsetY == 0,
            "radius and normalized control offsets clamp to safe values and recover nonfinite preferences");
        foreach (var (value, expected) in new[] { (0f, .1f), (.1f, .1f), (.75f, .75f), (1.5f, 1.5f), (2f, 1.5f), (float.NaN, .75f) })
        {
            settings.JumpComboIntervalSeconds = value;
            settings.Validate();
            Check(settings.JumpComboIntervalSeconds == expected,
                "somersault combo interval preserves its bounds and recovers nonfinite preferences");
        }
        settings.AngryForceRadius = -1;
        settings.Validate();
        Check(settings.AngryForceRadius == 1, "Angry Force radius keeps its one metre minimum");
        settings.AngryForceRadius = float.NaN;
        settings.Validate();
        Check(settings.AngryForceRadius == 10, "a nonfinite Angry Force radius recovers the default");
        foreach (var (value, expected) in new[] { (int.MinValue, 1), (0, 1), (1, 1), (32, 32), (128, 128), (512, 512), (int.MaxValue, 512) })
        {
            settings.ForceCaptureLimit = value;
            settings.Validate();
            Check(settings.ForceCaptureLimit == expected, "Force capture limits preserve supported values and clamp extreme integers");
        }
        foreach (var (value, expected) in new[] { (-1f, 0f), (2f, .5f), (0f, 0f), (.5f, .5f),
            (float.NaN, .2f), (float.PositiveInfinity, .2f), (float.NegativeInfinity, .2f) })
        {
            settings.ClutterPickTolerance = value;
            settings.Validate();
            Check(settings.ClutterPickTolerance == expected,
                "clutter tolerance preserves exact-only and boundary values, clamps extremes and restores nonfinite defaults");
        }
        foreach (var (value, expected) in new[] { (-1f, 1f), (0f, 1f), (1f, 1f), (2f, 2f), (3.25f, 3.25f),
            (4f, 4f), (99f, 4f), (float.NaN, 2f), (float.PositiveInfinity, 2f), (float.NegativeInfinity, 2f) })
        {
            settings.SliceReachMultiplier = value;
            settings.Validate();
            Check(settings.SliceReachMultiplier == expected,
                "slice grace preserves 1x–4x settings, clamps finite extremes and restores nonfinite defaults");
        }

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-checks-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            Check(SaberSettings.Load(path).BFL9KUiEnabled, "BFL9K controls remain visible by default");
            foreach (bool visible in new[] { false, true })
            {
                File.WriteAllText(path, "{\"BFL9KUiEnabled\":" + (visible ? "true" : "false") + "}");
                var visibility = SaberSettings.Load(path);
                Check(visibility.BFL9KUiEnabled == visible, "BFL9K UI visibility loads from preferences");
                visibility.Save(path);
                Check(SaberSettings.Load(path).BFL9KUiEnabled == visible,
                    "BFL9K UI visibility survives saving and reloading");
                using var savedVisibility = JsonDocument.Parse(File.ReadAllText(path));
                Check(savedVisibility.RootElement.GetProperty("BFL9KUiEnabled").GetBoolean() == visible,
                    "saved UI visibility uses the BFL9K preference key");
            }
            File.Delete(path);
            Check(!SaberSettings.Load(path).DebugTimingsEnabled,
                "missing preferences must leave debug timing collection off");
            foreach (bool debug in new[] { true, false })
            {
                var debugSettings = new SaberSettings { DebugTimingsEnabled = debug };
                debugSettings.Save(path);
                Check(SaberSettings.Load(path).DebugTimingsEnabled == debug,
                    "debug timing preference must survive saving and loading in both states");
            }
            File.Delete(path);
            Check(!new SaberSettings().ExtendedEnvironmentLighting && !SaberSettings.Load(path).ExtendedEnvironmentLighting,
                "extended environment lighting is off for new settings and missing preferences");
            Check(DefaultEffects(SaberSettings.Load(path).DarkEffects, SaberType.Dark),
                "a missing preferences file starts with Dark defaults");
            Check(SaberSettings.Load(path).SliceReachMultiplier == 2,
                "a missing preferences file retains the exact prior assisted selection allowance");
            Check(SaberSettings.Load(path).JumpComboIntervalSeconds == .75f,
                "legacy preferences acquire the default somersault combo interval");
            File.WriteAllText(path, "{\"BladeLength\":0.85,\"GripX\":-13,\"GripY\":-8,\"GripZ\":18,\"AimX\":0.05,\"AimY\":0.8,\"AimZ\":0.6,\"FingerCurl\":0.2}");
            var explicitPreferences = SaberSettings.Load(path);
            Check(!explicitPreferences.DebugTimingsEnabled, "legacy preferences acquire disabled debug timings");
            Check(!explicitPreferences.ExtendedEnvironmentLighting,
                "legacy preferences keep the stock environment-light budget");
            Check(explicitPreferences.BladeLength == .85f && explicitPreferences.GripX == -13 && explicitPreferences.GripY == -8
                && explicitPreferences.GripZ == 18 && explicitPreferences.AimX == .05f && explicitPreferences.AimY == .8f
                && explicitPreferences.AimZ == .6f && explicitPreferences.FingerCurl == .2f,
                "existing explicit preferences survive the new defaults unchanged");
            Check(DefaultEffects(explicitPreferences.LightEffects, SaberType.Light)
                && DefaultEffects(explicitPreferences.DarkEffects, SaberType.Dark),
                "legacy JSON without profiles acquires the distinct Light and Dark defaults");
            Check(explicitPreferences.DarkEffects.SparkCount == 8 && explicitPreferences.DarkEffects.SparkLifetime == .25f,
                "old preferences acquire the new Dark spark defaults");
            Check(explicitPreferences.AngryForceRadius == 10 && explicitPreferences.ForceControlsOffsetX == 0
                && explicitPreferences.ForceControlsOffsetY == 0 && explicitPreferences.SaberControlsOffsetX == 0
                && explicitPreferences.SaberControlsOffsetY == 0,
                "legacy preferences acquire the new radius and control position defaults");
            Check(explicitPreferences.ClutterPickTolerance == .2f,
                "legacy preferences acquire the clutter near-miss default without a migration");
            Check(explicitPreferences.ForceCaptureLimit == 128,
                "legacy preferences acquire the higher Force capture limit without a migration");
            Check(explicitPreferences.SliceReachMultiplier == 2
                && SaberLimits.SliceSelectionReach(explicitPreferences.BladeLength, explicitPreferences.SliceReachMultiplier)
                    == explicitPreferences.BladeLength * 2.0,
                "legacy preferences retain exactly twice their saved visible blade length without a migration");
            File.WriteAllText(path, "{\"SliceReachMultiplier\":9}");
            Check(SaberSettings.Load(path).SliceReachMultiplier == 4, "loaded finite slice grace clamps to the UI maximum");
            File.WriteAllText(path, "{\"SliceReachMultiplier\":-9}");
            Check(SaberSettings.Load(path).SliceReachMultiplier == 1, "loaded finite slice grace clamps to the UI minimum");
            File.WriteAllText(path, "{\"LightEffects\":null,\"DarkEffects\":null}");
            var loaded = SaberSettings.Load(path);
            Check(DefaultEffects(loaded.LightEffects, SaberType.Light) && DefaultEffects(loaded.DarkEffects, SaberType.Dark)
                && !ReferenceEquals(loaded.LightEffects, loaded.DarkEffects),
                "null JSON profiles recover independent defaults before rendering");
            File.WriteAllText(path, "{\"LightEffects\":{\"ArcCount\":0},\"DarkEffects\":{\"ArcCount\":8}}");
            loaded = SaberSettings.Load(path);
            Check(loaded.LightEffects.ArcCount == 0 && loaded.LightEffects.ShimmerAmount == 1.5f
                && loaded.LightEffects.ArcFrequency == 3 && loaded.DarkEffects.ArcCount == 8
                && loaded.DarkEffects.ShimmerAmount == 1.5f && loaded.DarkEffects.ArcFrequency == 6,
                "partial profiles preserve omitted members from the matching type's defaults");

            File.WriteAllText(path, "{\"LightEffects\":{\"ShimmerAmount\":0.5,\"ShimmerSpeed\":0.6,\"ArcCount\":1,\"ArcFrequency\":0.35,\"ArcAmplitude\":0.5,\"ArcBrightness\":0.65,\"ArcWidth\":0.8},\"DarkEffects\":{\"ShimmerAmount\":1.5,\"ShimmerSpeed\":1.5,\"ArcCount\":3,\"ArcFrequency\":1.5,\"ArcAmplitude\":1.3,\"ArcBrightness\":1.1,\"ArcWidth\":1}}");
            loaded = SaberSettings.Load(path);
            Check(loaded.LightEffects.ShimmerAmount == .5f && loaded.LightEffects.ShimmerSpeed == .6f
                && loaded.LightEffects.ArcCount == 1 && loaded.LightEffects.ArcFrequency == .35f
                && loaded.LightEffects.ArcAmplitude == .5f && loaded.LightEffects.ArcBrightness == .65f && loaded.LightEffects.ArcWidth == .8f
                && loaded.DarkEffects.ArcCount == 3 && loaded.DarkEffects.ArcFrequency == 1.5f
                && loaded.DarkEffects.ArcAmplitude == 1.3f && loaded.DarkEffects.ArcBrightness == 1.1f && loaded.DarkEffects.ArcWidth == 1,
                "explicit saved effect values survive the stronger defaults unchanged");

            SetEffects(settings.LightEffects, 2.5f, .75f, 7, 1.75f, 2, .5f, 1.5f);
            SetEffects(settings.DarkEffects, 0, 0, 0, .25f, 0, 0, .25f);
            settings.DarkEffects.SparkCount = 7; settings.DarkEffects.SparkFrequency = 9;
            settings.DarkEffects.SparkSize = 2; settings.DarkEffects.SparkBrightness = 3;
            settings.DarkEffects.SparkLifetime = .6f; settings.DarkEffects.SparkSpeed = 2.5f;
            settings.AngryForceRadius = 32.5f;
            settings.ClutterPickTolerance = .35f;
            settings.ForceCaptureLimit = 256;
            settings.SliceReachMultiplier = 3.25f;
            settings.JumpComboIntervalSeconds = 1.25f;
            settings.ForceControlsOffsetX = -.25f; settings.ForceControlsOffsetY = .125f;
            settings.SaberControlsOffsetX = .5f; settings.SaberControlsOffsetY = -.75f;
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
            Check(loaded.DarkEffects.SparkCount == 7 && loaded.DarkEffects.SparkFrequency == 9
                && loaded.DarkEffects.SparkSize == 2 && loaded.DarkEffects.SparkBrightness == 3
                && loaded.DarkEffects.SparkLifetime == .6f && loaded.DarkEffects.SparkSpeed == 2.5f,
                "all six Dark spark controls survive preferences save and reload");
            loaded.DarkEffects.Reset(SaberType.Dark);
            Check(loaded.DarkEffects.SparkCount == 8 && loaded.DarkEffects.SparkFrequency == 7.2f
                && loaded.DarkEffects.SparkSize == .9f && loaded.DarkEffects.SparkBrightness == 2.1f
                && loaded.DarkEffects.SparkLifetime == .25f && loaded.DarkEffects.SparkSpeed == 1.35f,
                "reset Dark effects restores all spark controls");
            Check(loaded.AngryForceRadius == 32.5f && loaded.ForceControlsOffsetX == -.25f && loaded.ForceControlsOffsetY == .125f
                && loaded.SaberControlsOffsetX == .5f && loaded.SaberControlsOffsetY == -.75f,
                "Angry Force radius and both control groups' normalized offsets survive save and reload");
            Check(loaded.ClutterPickTolerance == .35f, "custom clutter aim tolerance survives save and reload");
            Check(loaded.ForceCaptureLimit == 256, "the configured combined Force capture limit survives save and reload");
            Check(loaded.SliceReachMultiplier == 3.25f, "custom assisted slice grace survives preference save and reload");
            Check(loaded.JumpComboIntervalSeconds == 1.25f, "custom somersault combo interval survives save and reload");
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
                Check(saved.RootElement.TryGetProperty("LightEffects", out _) && saved.RootElement.TryGetProperty("DarkEffects", out _)
                    && !saved.RootElement.TryGetProperty("ShimmerAmount", out _),
                    "saved preferences contain two profiles rather than ambiguous top-level effect controls");
            settings.ClutterPickTolerance = 0;
            settings.Save(path);
            Check(SaberSettings.Load(path).ClutterPickTolerance == 0,
                "saving exact-only clutter picking preserves zero instead of replacing it with the default");

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

    private static void SettingsAutosaveSnapshots()
    {
        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-autosave-checks-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            var settings = new SaberSettings();
            var autosaver = new SaberSettingsAutosaver(path, settings);
            Check(!autosaver.Observe(settings, 0) && !autosaver.HasPendingChanges && !File.Exists(path),
                "unchanged defaults do not create a preferences file");

            settings.SaberControlsOffsetX = .375f;
            settings.ExtendedEnvironmentLighting = true;
            Check(!autosaver.Observe(settings, 0) && autosaver.HasPendingChanges && !File.Exists(path),
                "overlay position changes become pending without writing immediately");
            settings.DarkEffects.ArcWidth = 2.25f;
            Check(!autosaver.Observe(settings, .8) && autosaver.HasPendingChanges,
                "nested effect changes reset the quiet period for the whole settings snapshot");
            Check(!autosaver.Observe(settings, 1.79) && !File.Exists(path),
                "autosave waits until one second has passed since the latest change");
            Check(autosaver.Observe(settings, 1.8) && !autosaver.HasPendingChanges && File.Exists(path),
                "autosave writes once the complete settings snapshot settles");
            var loaded = SaberSettings.Load(path);
            Check(loaded.SaberControlsOffsetX == .375f && loaded.DarkEffects.ArcWidth == 2.25f,
                "autosave persists overlay positions and nested saber effects together");
            Check(loaded.ExtendedEnvironmentLighting, "autosave preserves the opted-in environment-light budget");
            settings.ExtendedEnvironmentLighting = false;
            Check(autosaver.Flush(settings) && !SaberSettings.Load(path).ExtendedEnvironmentLighting,
                "disabling only extended lighting is detected and persisted");

            var defaults = new SaberSettings();
            Check(!defaults.ExtendedEnvironmentLighting, "Reset all settings restores the stock environment-light budget");
            Check(autosaver.DeletePreferencesAndRebase(defaults) && !File.Exists(path) && !autosaver.HasPendingChanges,
                "reset deletes saved preferences and clears the autosave baseline");
            Check(!autosaver.Observe(defaults, 20) && !File.Exists(path),
                "observing reset defaults does not recreate the deleted preferences file");
            defaults.SliceAssetEntryLimit = 9000;
            Check(autosaver.Flush(defaults) && SaberSettings.Load(path).SliceAssetEntryLimit == 9000,
                "shutdown flush writes a pending setting immediately");

            string blockedParent = Path.Combine(directory, "file-parent");
            File.WriteAllText(blockedParent, "block directory creation");
            var blocked = new SaberSettingsAutosaver(Path.Combine(blockedParent, "settings.json"), new SaberSettings());
            var changed = new SaberSettings { BladeLength = 2 };
            Check(!blocked.Observe(changed, 0) && !blocked.Observe(changed, 1)
                && blocked.HasPendingChanges && !string.IsNullOrWhiteSpace(blocked.LastError),
                "autosave preserves pending settings and exposes write failures");
            File.Delete(blockedParent);
            Directory.CreateDirectory(blockedParent);
            Check(blocked.Observe(changed, 6) && blocked.LastError == null
                && SaberSettings.Load(Path.Combine(blockedParent, "settings.json")).BladeLength == 2,
                "autosave retries a failed write after its backoff and clears the error");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Pass("settings autosave snapshots and debounce");
    }

    private static void SliceResourcePreferences()
    {
        static (int, int, int, int, int) Values(SaberSettings settings) => (settings.SliceVertexArenaMiB,
            settings.SliceIndexArenaMiB, settings.SliceAssetDiskMiB, settings.SliceAssetEntryLimit, settings.SlicePhysicsVehicleLimit);
        Check(Values(new()) == (128, 64, 2048, 8192, 128),
            "new slice preferences reserve larger graphics arenas, bounded storage and a larger physics vehicle limit");
        var growthDefaults = new SaberSettings();
        Check(growthDefaults.SliceSolidLimit == 2048 && growthDefaults.SliceTriangleLimit == 120_000,
            "geometry preferences preserve the original closed-solid and source-triangle defaults");
        Check(growthDefaults.SliceAutoGrow && growthDefaults.SliceVertexMaximumMiB == 512 && growthDefaults.SliceIndexMaximumMiB == 256,
            "automatic mesh growth defaults to bounded 512/256 MiB ceilings");
        foreach (var (value, expected) in new[]
        {
            (int.MinValue, (32, 16, 128, 128, 30)),
            (int.MaxValue, (1024, 512, 16384, 65536, 512))
        })
        {
            var settings = new SaberSettings { SliceVertexArenaMiB = value, SliceIndexArenaMiB = value,
                SliceAssetDiskMiB = value, SliceAssetEntryLimit = value, SlicePhysicsVehicleLimit = value,
                SliceSolidLimit = value, SliceTriangleLimit = value };
            settings.Validate();
            Check(Values(settings) == expected, "slice resource preferences clamp extreme integers to each supported limit");
            Check(settings.SliceSolidLimit == (value < 0 ? SliceResourceLimits.MinimumSolidLimit : SliceResourceLimits.MaximumSolidLimit)
                && settings.SliceTriangleLimit == (value < 0 ? SliceResourceLimits.MinimumTriangleLimit : SliceResourceLimits.MaximumTriangleLimit),
                "geometry preferences clamp extreme integers before deriving output allowances");
            settings.SliceVertexMaximumMiB = value; settings.SliceIndexMaximumMiB = value;
            settings.Validate();
            Check(settings.SliceVertexMaximumMiB >= settings.SliceVertexArenaMiB
                && settings.SliceVertexMaximumMiB <= SliceResourceLimits.MaximumVertexArenaMiB
                && settings.SliceIndexMaximumMiB >= settings.SliceIndexArenaMiB
                && settings.SliceIndexMaximumMiB <= SliceResourceLimits.MaximumIndexArenaMiB,
                "growth ceilings cannot exceed hard limits or fall below initial reservations");
        }
        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-resource-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            Check(Values(SaberSettings.Load(path)) == (128, 64, 2048, 8192, 128),
                "a missing preferences file acquires all slice resource defaults");
            File.WriteAllText(path, "{\"BladeLength\":2.5,\"AngryForceRadius\":18}");
            var loaded = SaberSettings.Load(path);
            Check(Values(loaded) == (128, 64, 2048, 8192, 128) && loaded.BladeLength == 2.5f && loaded.AngryForceRadius == 18,
                "legacy preferences acquire resource defaults while preserving existing saber and Force choices");
            Check(loaded.SliceAutoGrow && loaded.SliceVertexMaximumMiB == 512 && loaded.SliceIndexMaximumMiB == 256,
                "legacy preferences acquire automatic growth and its bounded ceilings");
            Check(loaded.SliceSolidLimit == 2048 && loaded.SliceTriangleLimit == 120_000,
                "legacy preferences acquire geometry defaults without migration");
            loaded.SliceVertexArenaMiB = 384; loaded.SliceIndexArenaMiB = 192;
            loaded.SliceAssetDiskMiB = 4096; loaded.SliceAssetEntryLimit = 20000; loaded.SlicePhysicsVehicleLimit = 256;
            loaded.SliceAutoGrow = false; loaded.SliceVertexMaximumMiB = 768; loaded.SliceIndexMaximumMiB = 320;
            loaded.SliceSolidLimit = 4096; loaded.SliceTriangleLimit = 240_000;
            loaded.Save(path);
            var saved = SaberSettings.Load(path);
            Check(Values(saved) == (384, 192, 4096, 20000, 256) && saved.BladeLength == 2.5f && saved.AngryForceRadius == 18,
                "all five resource limits survive saving and loading alongside existing preferences");
            Check(!saved.SliceAutoGrow && saved.SliceVertexMaximumMiB == 768 && saved.SliceIndexMaximumMiB == 320,
                "growth opt-out and custom ceilings survive saving and loading");
            Check(saved.SliceSolidLimit == 4096 && saved.SliceTriangleLimit == 240_000,
                "custom geometry limits survive saving and loading");
            File.WriteAllText(path, "{\"SliceVertexArenaMiB\":768,\"SliceIndexArenaMiB\":384}");
            var largeLegacy = SaberSettings.Load(path);
            Check(largeLegacy.SliceVertexMaximumMiB == 768 && largeLegacy.SliceIndexMaximumMiB == 384,
                "legacy large startup reservations raise growth ceilings instead of becoming invalid");
            File.WriteAllText(path, "{\"SliceVertexArenaMiB\":0,\"SliceIndexArenaMiB\":2147483647,\"SliceAssetDiskMiB\":-1,\"SliceAssetEntryLimit\":2147483647,\"SlicePhysicsVehicleLimit\":0}");
            Check(Values(SaberSettings.Load(path)) == (32, 512, 128, 65536, 30),
                "loading edited resource preferences validates every limit before runtime allocation");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Pass("slice resource preferences and persistence");
    }

    private static bool DefaultEffects(SaberEffectSettings settings, SaberType type) => type == SaberType.Dark
        ? settings.ShimmerAmount == 1.5f && settings.ShimmerSpeed == 1.5f && settings.ArcCount == 12
            && settings.ArcFrequency == 6 && settings.ArcAmplitude == 1.45f && settings.ArcBrightness == 3.5f && settings.ArcWidth == 1.9f
        : settings.ShimmerAmount == 1.5f && settings.ShimmerSpeed == 1.5f && settings.ArcCount == 8
            && settings.ArcFrequency == 3 && settings.ArcAmplitude == .8f && settings.ArcBrightness == 2.5f && settings.ArcWidth == 1.75f;

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
