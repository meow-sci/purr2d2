using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using System.Diagnostics;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private string? _preferencesSaveError;
    private bool _settingsWindowBoundsAvailable;
    private float2 _settingsWindowMin, _settingsWindowMax;

    private bool SettingsContainsPointer(float2 pointer) => _enabled && _windowVisible && Program.DrawUI
        && _settingsWindowBoundsAvailable && pointer.X >= _settingsWindowMin.X && pointer.X <= _settingsWindowMax.X
        && pointer.Y >= _settingsWindowMin.Y && pointer.Y <= _settingsWindowMax.Y;

    internal void DrawMenu()
    {
        if (_disposed || !ImGui.BeginMenu("purr2d2")) return;
        try
        {
            ((IGameViewportLifecycle)Program.HoveredViewport).SetMenuBarInUse(true);
            if (ImGui.MenuItem("Enabled", default(ImString), _enabled, _ready)) SetEnabled(!_enabled);
            if (ImGui.MenuItem("Kitten List", default(ImString), _settings.KittenListUiEnabled))
                SetKittenListUiVisible(!_settings.KittenListUiEnabled);
            if (ImGui.MenuItem("The Furce HUD", default(ImString), _settings.ForceUiEnabled))
                SetForceUiVisible(!_settings.ForceUiEnabled);
            if (ImGui.MenuItem("Big Fucking Laster 9000 HUD", default(ImString), _settings.BFL9KUiEnabled))
                SetBFL9KUiVisible(!_settings.BFL9KUiEnabled);
            ImGui.Separator();
            if (ImGui.MenuItem("Settings", default(ImString), _windowVisible, _enabled)) ToggleSettingsWindow();
        }
        finally { ImGui.EndMenu(); }
    }

    private void ToggleSettingsWindow()
    {
        _windowVisible = !_windowVisible;
        _settingsWindowBoundsAvailable = false;
        if (!_windowVisible) return;
        ResetOverlaySwing();
        ResetForceGestures();
        DismissKittenColorPicker();
    }

    internal void SetInitializationFault(string message)
    {
        _fault = "Initialization failed: " + message;
        _ready = false;
        EnvironmentLightingPatch.Release();
        _windowVisible = _enabled;
        // The caller removes our Harmony hooks after this, so release partial ownership now.
        Cleanup(ResetWorld, "World");
        Cleanup(DisposeForce, "Force capture");
        Cleanup(_forcePose.Dispose, "Force pose");
        Cleanup(_somersaultMotion.Dispose, "Somersault motion");
        Cleanup(_somersaultPose.Dispose, "Somersault pose");
        Cleanup(_audio.Shutdown, "Audio");
        Cleanup(_renderer.Dispose, "Renderer");
        Cleanup(_pose.Dispose, "Pose");
    }

    internal void DrawUi()
    {
        _settingsWindowBoundsAvailable = false;
        ObserveSettingsAutosave();
        if (!_enabled || !_windowVisible || _disposed || !Program.DrawUI)
        { _confirmSliceDebrisRemoval = false; return; }
        try
        {
            ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
            ImGui.SetNextWindowSize(new float2(480, 440), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new float2(390, 260), new float2(float.MaxValue));
            bool draw = ImGui.Begin("Purr2D2 | Settings", ref _windowVisible);
            try
            {
                _settingsWindowMin = ImGui.GetWindowPos();
                _settingsWindowMax = _settingsWindowMin + ImGui.GetWindowSize();
                _settingsWindowBoundsAvailable = _windowVisible;
                if (!draw) return;
                if (_fault != null) ImGui.TextWrapped(_fault);
                if (_renderer.LastError != null) ImGui.TextWrapped(_renderer.LastError);
                if (_audio.LastError != null) ImGui.TextWrapped(_audio.LastError);
                if (_preferencesSaveError != null) ImGui.TextWrapped(_preferencesSaveError);
                if (ImGui.CollapsingHeader("Global Big Fuckig Laser 9000 Settings")) DrawSaberAppearanceSettings();
                if (ImGui.CollapsingHeader("The Furce Settings")) DrawForceSettings();
                if (ImGui.CollapsingHeader("HUD Overlay Positions")) DrawControlPositionSettings();
                if (ImGui.CollapsingHeader("Cape Settings")) DrawCapeSettings();
                if (ImGui.CollapsingHeader("Character & Grip")) DrawSaberCharacterSettings();
                if (ImGui.CollapsingHeader("Clutter Slicing")) DrawClutterSliceSettings();
                if (ImGui.CollapsingHeader("Slicing methods")) DrawSliceMethodSettings();
                if (ImGui.CollapsingHeader("Slicing Resources")) DrawSliceResourceSettings();
                else _confirmSliceDebrisRemoval = false;
                if (ImGui.CollapsingHeader("Slicing Diagnostics"))
                {
                    bool debugTimings = _settings.DebugTimingsEnabled;
                    if (ImGui.Checkbox("Debug timing stats and console logging", ref debugTimings))
                        _settings.DebugTimingsEnabled = debugTimings;
                    ShowSaberControlTip("Collects detailed slice, frame and physics-worker timings and prints them to the console. Off by default; enabling adds diagnostic overhead. Applies on the next simulation update and saves automatically.");
                    string diagnostics = $"Purr2D2 {typeof(SaberRuntime).Assembly.GetName().Version}; KSA {typeof(Vehicle).Assembly.GetName().Version}\n"
                        + $"Equipped: {_blade.Equipped}; fresh pose: {_freshPoseThisTick}; pose age: {_pose.FreshnessFrames}\n"
                        + $"Slice active: {SliceWorkActive}; parts sliced: {_targeting.SlicedParts}\n{_sliceStatus}\n";
                    diagnostics += DebugTimings.Enabled ? _slicer.Diagnostics + "\n"
                        + $"Manual contact: {_manualProbe.LastUpdateMilliseconds:F3} ms last / {_manualProbe.MaximumUpdateMilliseconds:F3} ms maximum; "
                        + $"discovery {_manualProbe.DiscoveryUnits}, frames {_manualProbe.FrameReads}, primitives {_manualProbe.PrimitiveReads}, triangles {_manualProbe.TestedTriangles}; "
                        + $"budget stops {_manualProbe.Saturations}, expired queries {_manualProbe.ExpiredQueries}"
                        : "Debug timing collection and console logging are off.";
                    diagnostics += $"\nMethods: vessel {_activeVesselDetector}, ground {_activeClutterDetector}, plane {_settings.EffectiveContactPlaneStrategy}"
                        + $"\nQueued targets: {_vesselQueue?.Count ?? 0} vessel / {_clutterQueue?.Count ?? 0} ground; "
                        + $"geometry workers configured {_clutterQueue?.WorkerCount ?? 0}; loader slots {(_vesselQueue?.LoadSlots ?? 0) + (_clutterQueue?.LoadSlots ?? 0)}; "
                        + $"tracked memory {((_vesselQueue?.ChargedBytes ?? 0) + (_clutterQueue?.ChargedBytes ?? 0)) / (1024d * 1024d):F1} MiB"
                        + $"\nCached vessel: {_manualProbe.CacheHits} hits, {_manualProbe.CacheFallbacks} cold fallbacks, {_manualProbe.CacheIncomplete} unfinished, {_manualProbe.CacheUnsupported} unsupported."
                        + "\nDetector timing is per call, not cut completion; the last 256 timed calls are retained.";
                    foreach (SliceDetectorKind kind in Enum.GetValues<SliceDetectorKind>())
                        diagnostics += "\n" + SliceDetectionDiagnostics.Describe(kind);
                    if (_clutterQueue?.Scheduler is Purr2D2.Slicing.SliceWorkerPool pool)
                        diagnostics += $"\nGeometry workers active {pool.ActiveCount}/{pool.WorkerCount}, scheduled {pool.PendingCount}.";
                    diagnostics += "\n" + SliceJobDiagnostics.Describe();
                    ImGui.TextWrapped(diagnostics);
                    if (ImGui.Button("Copy slice diagnostics")) ImGui.SetClipboardText(diagnostics);
                    if (ImGui.Button("Reset detector counters")) { SliceDetectionDiagnostics.Reset(); SliceJobDiagnostics.Reset(); }
                    ImGui.Separator();
                    if (ImGui.Button("Capture next confirmed vessel hit for SliceReplay")) ArmSliceReplayCapture();
                    ShowSaberControlTip("One-shot developer capture. Copies only the confirmed hit mesh and its target-local blade interval, with a 40,000-triangle cap and a 0.25 ms copy budget per settled update. The background writer is capped at 16 MiB. This does not change cut behavior; the capture is disabled until armed.");
                    ImGui.TextWrapped(_sliceReplayCapture.Status);
                    if (_sliceReplayCapture.OutputPath is { } replayPath) ImGui.TextWrapped("Replay fixture: " + replayPath);
                }
                ImGui.Separator();
                if (ImGui.Button("Reset all settings")) ResetAllSettings();
                ShowSaberControlTip("Restores all Purr2D2 settings to their defaults and deletes saved preferences.\n\nStartup graphics reservations update after restarting KSA.");
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report("Settings window failed", ex); }
        finally { ObserveSettingsAutosave(); }
    }

    private void ObserveSettingsAutosave()
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _settingsAutosaver.Observe(_settings, now);
        _preferencesSaveError = _settingsAutosaver.LastError is { } error
            ? "Could not update saved preferences: " + error
            : null;
    }

    private void ResetAllSettings()
    {
        _settings = new SaberSettings();
        _settings.Validate();
        _settingsAutosaver.DeletePreferencesAndRebase(_settings);
        _preferencesSaveError = _settingsAutosaver.LastError is { } error
            ? "Could not reset saved preferences: " + error
            : null;
        ApplySliceResourceSettings();
    }

    private void DrawSaberAppearanceSettings()
    {
        if (!BeginSaberSettingsTable("##SaberAppearance", "Extended environment lighting")) return;
        bool changed = false;
        bool extendedLighting = _settings.ExtendedEnvironmentLighting;
        float length = _settings.BladeLength, volume = _settings.Volume, sliceReach = _settings.SliceReachMultiplier;
        try
        {
            SaberSettingsRow("Default blade length");
            changed |= ImGui.SliderFloat("##BladeLength", ref length, .25f, SaberLimits.MaximumBladeLength, "%.2f m");
            SaberSettingsRow("Slice grace / reach");
            changed |= ImGui.SliderFloat("##SliceReachMultiplier", ref sliceReach,
                SaberLimits.MinimumSliceReachMultiplier, SaberLimits.MaximumSliceReachMultiplier, "%.2fx");
            ShowSaberControlTip("The blade length multiplier fudge factor for how far away a part slice is detected");
            float effectiveLength = _activeSaber?.BladeLengthOverride ?? length;
            double visibleLength = _pose.ClashHeld ? _pose.ClashHeldBladeLength : _blade.Deployment * effectiveLength;
            double bladeScale = _pose.TryGetBodyFrame(out var frame) ? frame.Z.XYZ.Length() : 1;
            if (!double.IsFinite(bladeScale) || bladeScale <= 0) bladeScale = 1;
            ImGui.TextWrapped($"Maximum selection: {SaberLimits.SliceSelectionReach(visibleLength * bladeScale, sliceReach):F2} m now; {SaberLimits.SliceSelectionReach(effectiveLength * bladeScale, sliceReach):F2} m fully extended.");
            SaberSettingsRow("Sound volume");
            changed |= ImGui.SliderFloat("##SoundVolume", ref volume, 0, 1, "%.2f");
            ShowSaberControlTip("Overall volume for BFL9K sound effects and hum.");
            SaberSettingsRow("Extended environment lighting");
            ImGui.BeginDisabled(EnvironmentLightingPatch.UnavailableReason != null);
            try { changed |= ImGui.Checkbox("##ExtendedEnvironmentLighting", ref extendedLighting); }
            finally { ImGui.EndDisabled(); }
            ShowSaberControlTip("Lets more nearby sabers light the ground: raises the main view's environment-light limit from 8 to 16. Each ordinary saber uses two lights, and other scene lamps share the limit.\n\nOff by default. Applies immediately; extra graphics work occurs only when more than eight lights are selected.");
            if (EnvironmentLightingPatch.UnavailableReason is { } reason) ImGui.TextWrapped(reason);
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.BladeLength = length;
        _settings.SliceReachMultiplier = sliceReach;
        _settings.Volume = volume;
        _settings.ExtendedEnvironmentLighting = extendedLighting;
        _settings.Validate();
    }

    private void DrawClutterSliceSettings()
    {
        if (!BeginSaberSettingsTable("##ClutterSlicing", "Tree cut fire duration")) return;
        float fireSeconds = _settings.TreeCutFireSeconds;
        bool changed = false;
        try
        {
            SaberSettingsRow("Tree cut fire duration");
            changed = ImGui.SliderFloat("##TreeCutFireSeconds", ref fireSeconds, 0, 60, "%.1f s");
            ShowSaberControlTip("How long flames linger on freshly cut tree ends. Set to 0 to disable cut fire. Default: 10 seconds.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.TreeCutFireSeconds = fireSeconds;
        _settings.Validate();
    }

    private void DrawCapeSettings()
    {
        if (!BeginSaberSettingsTable("##CapeSettings", "Maximum simulated capes")) return;
        int simulationLimit = _settings.CapeSimulationLimit;
        bool changed = false;
        try
        {
            SaberSettingsRow("Maximum simulated capes");
            changed |= ImGui.SliderInt("##CapeSimulationLimit", ref simulationLimit,
                SaberSettings.MinimumCapeSimulationLimit, SaberSettings.MaximumCapeSimulationLimit);
            ShowSaberControlTip("Among enabled capes visible within 25 m, the nearest up to this many receive cloth simulation. A value of 0 keeps the cape visible with cloth motion off. Default: 4.\n\nAt most two capes update per frame, so higher limits share the same update budget and refresh motion less often.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.CapeSimulationLimit = simulationLimit;
        _settings.Validate();
    }

    private void DrawSaberEffectsSettings(SaberType type)
    {
        string typeName = type == SaberType.Dark ? "Dark" : "Light";
        ImGui.Separator();
        ImGui.TextWrapped($"{typeName} effects — shared by all {typeName} sabers");
        var effects = _settings.EffectsFor(type);
        if (!BeginSaberSettingsTable("##SaberEffects", type == SaberType.Dark ? "Spark spray speed" : "Shimmer amount")) return;
        float shimmerAmount = effects.ShimmerAmount, shimmerSpeed = effects.ShimmerSpeed;
        int arcCount = effects.ArcCount;
        float arcFrequency = effects.ArcFrequency, arcAmplitude = effects.ArcAmplitude;
        float arcBrightness = effects.ArcBrightness, arcWidth = effects.ArcWidth;
        int sparkCount = effects.SparkCount;
        float sparkFrequency = effects.SparkFrequency, sparkSize = effects.SparkSize;
        float sparkBrightness = effects.SparkBrightness, sparkLifetime = effects.SparkLifetime, sparkSpeed = effects.SparkSpeed;
        bool changed = false;
        try
        {
            SaberSettingsRow("Shimmer amount");
            changed |= ImGui.SliderFloat("##ShimmerAmount", ref shimmerAmount, 0, 4, "%.2fx");
            ShowSaberControlTip("Shimmer strength; 0 keeps the halo steady.");
            SaberSettingsRow("Shimmer speed");
            changed |= ImGui.SliderFloat("##ShimmerSpeed", ref shimmerSpeed, 0, 4, "%.2fx");
            ShowSaberControlTip("Shimmer speed; 0 freezes the motion.");
            SaberSettingsRow("Arc count");
            changed |= ImGui.SliderInt("##ArcCount", ref arcCount, 0, SaberSettings.MaximumArcCount);
            ShowSaberControlTip("Maximum arcs per saber; 0 disables them.\n\nDistance may reduce the visible count.");
            SaberSettingsRow("Arc frequency");
            changed |= ImGui.SliderFloat("##ArcFrequency", ref arcFrequency, .1f, SaberSettings.MaximumArcFrequency, "%.2fx");
            ShowSaberControlTip("Burst rate per arc; 1× gives one burst every two seconds.\n\nHigher rates create shorter, more frequent bursts.");
            SaberSettingsRow("Arc amplitude");
            changed |= ImGui.SliderFloat("##ArcAmplitude", ref arcAmplitude, 0, 3, "%.2fx");
            ShowSaberControlTip("Arc spread from the blade; 0 removes outward spread.");
            SaberSettingsRow("Arc brightness");
            changed |= ImGui.SliderFloat("##ArcBrightness", ref arcBrightness, 0, SaberSettings.MaximumArcBrightness, "%.2fx");
            ShowSaberControlTip("Arc brightness; 0 hides them.");
            SaberSettingsRow("Arc width");
            changed |= ImGui.SliderFloat("##ArcWidth", ref arcWidth, .25f, SaberSettings.MaximumArcWidth, "%.2fx");
            ShowSaberControlTip("Arc width; 1× is the baseline.");
            if (type == SaberType.Dark)
            {
                SaberSettingsRow("Spark amount");
                changed |= ImGui.SliderInt("##SparkCount", ref sparkCount, 0, SaberSettings.MaximumSparkCount);
                ShowSaberControlTip("Colored welding sparks per burst; 0 disables them. Sparks fade as they fall away.");
                SaberSettingsRow("Spark frequency");
                changed |= ImGui.SliderFloat("##SparkFrequency", ref sparkFrequency, 0, SaberSettings.MaximumSparkFrequency, "%.1f bursts/s");
                ShowSaberControlTip("Spark bursts per second; 0 disables emission.");
                SaberSettingsRow("Spark size");
                changed |= ImGui.SliderFloat("##SparkSize", ref sparkSize, .25f, 3, "%.2fx");
                SaberSettingsRow("Spark brightness");
                changed |= ImGui.SliderFloat("##SparkBrightness", ref sparkBrightness, 0, 4, "%.2fx");
                ShowSaberControlTip("Spark intensity in this kitten's saber color; 0 hides new sparks.");
                SaberSettingsRow("Spark lifetime");
                changed |= ImGui.SliderFloat("##SparkLifetime", ref sparkLifetime, .1f, .8f, "%.2f s");
                SaberSettingsRow("Spark spray speed");
                changed |= ImGui.SliderFloat("##SparkSpeed", ref sparkSpeed, .25f, 3, "%.2fx");
            }
        }
        finally { ImGui.EndTable(); }
        if (changed)
        {
            effects.ShimmerAmount = shimmerAmount; effects.ShimmerSpeed = shimmerSpeed;
            effects.ArcCount = arcCount; effects.ArcFrequency = arcFrequency;
            effects.ArcAmplitude = arcAmplitude; effects.ArcBrightness = arcBrightness;
            effects.ArcWidth = arcWidth;
            effects.SparkCount = sparkCount; effects.SparkFrequency = sparkFrequency; effects.SparkSize = sparkSize;
            effects.SparkBrightness = sparkBrightness; effects.SparkLifetime = sparkLifetime; effects.SparkSpeed = sparkSpeed;
            effects.Validate(type);
        }
        if (ImGui.Button($"Reset {typeName} effects"))
        {
            effects.Reset(type);
        }
        ShowSaberControlTip($"Restores shared {typeName} effects for all sabers of that type.");
    }

    private void DrawForceSettings()
    {
        if (!BeginSaberSettingsTable("##ForceSettings", "Radius bubble line length")) return;
        float tolerance = _settings.ClutterPickTolerance;
        float bubbleLineLength = _settings.ForceBubbleLineLengthPercent;
        float maximumSpread = _settings.ForceMaximumSpread;
        float breakupGroan = _settings.ForceBreakupGroanPixels, breakupSplit = _settings.ForceBreakupSplitPixels;
        int captureLimit = _settings.ForceCaptureLimit, breakupPieceLimit = _settings.ForceBreakupPieceLimit;
        bool changed;
        try
        {
            SaberSettingsRow("Furce capture limit");
            changed = ImGui.SliderInt("##ForceCaptureLimit", ref captureLimit, ForceCaptureLimits.Minimum, ForceCaptureLimits.Maximum);
            ShowSaberControlTip($"Maximum vessels, debris, and ground objects per Angry Furce capture (default {ForceCaptureLimits.Default}); applies next capture.\n\nThe selected target is included first, then nearest objects fill remaining slots.");
            SaberSettingsRow("Radius bubble line length");
            changed |= ImGui.SliderFloat("##ForceBubbleLineLengthPercent", ref bubbleLineLength, 0, 100,
                bubbleLineLength <= 0 ? "3 px" : bubbleLineLength >= 100 ? "Original" : "%.0f%%");
            ShowSaberControlTip("Length of each Angry Furce radius bubble dash: 0% is 3 pixels, 100% is the original length (the maximum and default).\n\nShorter projected dashes keep their original length. Saves automatically and applies immediately.");
            SaberSettingsRow("Clutter aim tolerance");
            changed |= ImGui.SliderFloat("##ClutterPickTolerance", ref tolerance, 0, .5f, "%.2f m");
            ShowSaberControlTip("After an exact miss, this radius catches nearby physical clutter.\n\nExact hits take priority; 0 allows exact hits only.");
            SaberSettingsRow("Maximum Furce spread");
            changed |= ImGui.SliderFloat("##ForceMaximumSpread", ref maximumSpread,
                ForceSpreadLimits.MinimumMaximum, ForceSpreadLimits.Maximum, "%.1fx");
            ShowSaberControlTip($"Caps shared Shift+wheel Furce group spacing; individual object sizes are unchanged. Default: {ForceSpreadLimits.DefaultMaximum:0.0}x. Larger spreads move objects farther from the group center. Saves automatically; changes apply immediately, and lowering the cap eases a held group back within it.");
            SaberSettingsRow("Furce breakup groan (px)");
            changed |= ImGui.SliderFloat("##ForceBreakupGroanPixels", ref breakupGroan, 1, 64, "%.1f px");
            ShowSaberControlTip("Continuous outward Shift+scroll distance normalized to pixels before the Furce creak plays; inward Shift+scroll unwinds.");
            SaberSettingsRow("Furce breakup split (px)");
            changed |= ImGui.SliderFloat("##ForceBreakupSplitPixels", ref breakupSplit, 2, 128, "%.1f px");
            ShowSaberControlTip("Continuous outward Shift+scroll distance normalized to pixels before Furce breakup occurs; inward Shift+scroll unwinds.");
            SaberSettingsRow("Breakup debris limit");
            changed |= ImGui.SliderInt("##ForceBreakupPieceLimit", ref breakupPieceLimit,
                ForceBreakupLimits.Minimum, ForceBreakupLimits.Maximum);
            ShowSaberControlTip($"Maximum new debris pieces across all vehicles in one breakup (default {ForceBreakupLimits.Default}); applies to the next breakup.\n\nLarger limits create more physics objects. Smaller and deeper geometry stays grouped into fewer pieces.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.ForceCaptureLimit = captureLimit;
        _settings.ForceBubbleLineLengthPercent = bubbleLineLength;
        _settings.ClutterPickTolerance = tolerance;
        _settings.ForceMaximumSpread = maximumSpread;
        _settings.ForceBreakupGroanPixels = breakupGroan;
        _settings.ForceBreakupSplitPixels = breakupSplit;
        _settings.ForceBreakupPieceLimit = breakupPieceLimit;
        _settings.Validate();
    }

    private void DrawControlPositionSettings()
    {
        ImGui.TextWrapped("Offsets from the default positions, as fractions of screen width and height. Positive X moves right; positive Y moves down.");
        if (!BeginSaberSettingsTable("##ControlPositions", "Saber (right) X")) return;
        float forceX = _settings.ForceControlsOffsetX, forceY = _settings.ForceControlsOffsetY;
        float saberX = _settings.SaberControlsOffsetX, saberY = _settings.SaberControlsOffsetY;
        bool changed = false;
        try
        {
            SaberSettingsRow("Furce (left) X");
            changed |= ImGui.SliderFloat("##ForceControlsOffsetX", ref forceX, -1, 1, "%.2f width");
            SaberSettingsRow("Furce (left) Y");
            changed |= ImGui.SliderFloat("##ForceControlsOffsetY", ref forceY, -1, 1, "%.2f height");
            SaberSettingsRow("Saber (right) X");
            changed |= ImGui.SliderFloat("##SaberControlsOffsetX", ref saberX, -1, 1, "%.2f width");
            SaberSettingsRow("Saber (right) Y");
            changed |= ImGui.SliderFloat("##SaberControlsOffsetY", ref saberY, -1, 1, "%.2f height");
        }
        finally { ImGui.EndTable(); }
        if (changed)
        {
            _settings.ForceControlsOffsetX = forceX; _settings.ForceControlsOffsetY = forceY;
            _settings.SaberControlsOffsetX = saberX; _settings.SaberControlsOffsetY = saberY;
            _settings.Validate();
        }
        if (ImGui.Button("Reset control positions"))
        {
            _settings.ForceControlsOffsetX = _settings.ForceControlsOffsetY = 0;
            _settings.SaberControlsOffsetX = _settings.SaberControlsOffsetY = 0;
        }
    }

    private void DrawSaberCharacterSettings()
    {
        if (!BeginSaberSettingsTable("##SaberCharacter", "Space-bar somersaults")) return;
        var grip = new float3(_settings.GripX, _settings.GripY, _settings.GripZ);
        var aim = new float3(_settings.AimX, _settings.AimY, _settings.AimZ);
        float curl = _settings.FingerCurl, jumpComboInterval = _settings.JumpComboIntervalSeconds;
        bool spaceSomersaultEnabled = _settings.SpaceSomersaultEnabled;
        bool changed = false;
        try
        {
            SaberSettingsRow("Grip position");
            changed |= ImGui.SliderFloat3("##GripOffset", ref grip, -40, 40, "%.1f");
            ShowSaberControlTip("Chest-relative X, Y and Z offsets in centimetres.");
            SaberSettingsRow("Blade direction");
            changed |= ImGui.SliderFloat3("##BladeDirection", ref aim, -1, 1, "%.2f");
            ShowSaberControlTip("X, Y and Z direction in kitten model space.");
            SaberSettingsRow("Finger curl");
            changed |= ImGui.SliderFloat("##FingerCurl", ref curl, 0, .5f, "%.2f rad");
            SaberSettingsRow("Jump combo interval");
            changed |= ImGui.SliderFloat("##JumpComboIntervalSeconds", ref jumpComboInterval, .1f, 1.5f, "%.2f s");
            ShowSaberControlTip("Maximum time between jump presses for a somersault combo.");
            SaberSettingsRow("Space-bar somersaults");
            changed |= ImGui.Checkbox("##SpaceSomersaultEnabled", ref spaceSomersaultEnabled);
            ShowSaberControlTip("Bare Space starts a forward somersault; arrow keys choose the direction. Turn this off to keep Space's usual jump behavior.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.GripX = grip.X; _settings.GripY = grip.Y; _settings.GripZ = grip.Z;
        _settings.AimX = aim.X; _settings.AimY = aim.Y; _settings.AimZ = aim.Z;
        _settings.FingerCurl = curl; _settings.JumpComboIntervalSeconds = jumpComboInterval;
        _settings.SpaceSomersaultEnabled = spaceSomersaultEnabled;
        _settings.Validate();
    }

    private static bool BeginSaberSettingsTable(string id, string? widestLabel = null)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.NoPadOuterX)) return false;
        float labelWidth = Math.Max(ImGui.CalcTextSize("Blade direction").X, ImGui.CalcTextSize("Color preference").X);
        if (widestLabel != null) labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(widestLabel).X);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed,
            labelWidth);
        ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    private static void SaberSettingsRow(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
    }

    private void ShowSaberControlTip(string text)
    {
        if (!_autoSwingDrag.Held && !_dropSpinDrag.Held && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ShowSaberTooltip(text);
    }

    private static void ShowSaberTooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26);
        try { ImGui.TextWrapped(text); }
        finally
        {
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
