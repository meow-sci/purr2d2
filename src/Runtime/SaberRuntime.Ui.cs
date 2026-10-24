using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
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
        if (_disposed || !ImGui.BeginMenu("Purr2D2")) return;
        try
        {
            ((IGameViewportLifecycle)Program.HoveredViewport).SetMenuBarInUse(true);
            if (ImGui.MenuItem("Enabled", default(ImString), _enabled, _ready)) SetEnabled(!_enabled);
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

                if (ImGui.CollapsingHeader("Global lightsaber settings")) DrawSaberAppearanceSettings();
                if (ImGui.CollapsingHeader("Force settings")) DrawForceSettings();
                if (ImGui.CollapsingHeader("Overlay UI Positions")) DrawControlPositionSettings();
                if (ImGui.CollapsingHeader("Character & grip")) DrawSaberCharacterSettings();
                if (ImGui.CollapsingHeader("Slice diagnostics"))
                {
                    string diagnostics = $"Purr2D2 {typeof(SaberRuntime).Assembly.GetName().Version}; KSA {typeof(Vehicle).Assembly.GetName().Version}\n"
                        + $"Equipped: {_blade.Equipped}; fresh pose: {_freshPoseThisTick}; pose age: {_pose.FreshnessFrames}\n"
                        + $"Slice active: {SliceWorkActive}; parts sliced: {_targeting.SlicedParts}\n{_sliceStatus}\n{_slicer.Diagnostics}\n"
                        + $"Manual contact: {_manualProbe.LastUpdateMilliseconds:F3} ms last / {_manualProbe.MaximumUpdateMilliseconds:F3} ms maximum; "
                        + $"discovery {_manualProbe.DiscoveryUnits}, frames {_manualProbe.FrameReads}, primitives {_manualProbe.PrimitiveReads}, triangles {_manualProbe.TestedTriangles}; "
                        + $"budget stops {_manualProbe.Saturations}, expired queries {_manualProbe.ExpiredQueries}";
                    ImGui.TextWrapped(diagnostics);
                    if (ImGui.Button("Copy slice diagnostics")) ImGui.SetClipboardText(diagnostics);
                }
                if (ImGui.CollapsingHeader("Slice resources")) DrawSliceResourceSettings();
                else _confirmSliceDebrisRemoval = false;

                ImGui.TextDisabled("Settings save automatically after a brief pause.");
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
        if (!BeginSaberSettingsTable("##SaberAppearance", "Slice grace / reach")) return;
        bool changed = false;
        float length = _settings.BladeLength, volume = _settings.Volume, sliceReach = _settings.SliceReachMultiplier;
        try
        {
            SaberSettingsRow("Blade length");
            changed |= ImGui.SliderFloat("##BladeLength", ref length, .25f, SaberLimits.MaximumBladeLength, "%.2f m");
            SaberSettingsRow("Slice grace / reach");
            changed |= ImGui.SliderFloat("##SliceReachMultiplier", ref sliceReach,
                SaberLimits.MinimumSliceReachMultiplier, SaberLimits.MaximumSliceReachMultiplier, "%.2fx");
            ShowSaberControlTip("Select parts within visible blade length × this value.\n\nApplies before an assisted slice starts.");
            double visibleLength = _blade.Deployment * length;
            ImGui.TextWrapped($"Maximum selection: {SaberLimits.SliceSelectionReach(visibleLength, sliceReach):F2} m now; {SaberLimits.SliceSelectionReach(length, sliceReach):F2} m fully extended.");
            SaberSettingsRow("Sound volume");
            changed |= ImGui.SliderFloat("##SoundVolume", ref volume, 0, 1, "%.2f");
            ShowSaberControlTip("Overall volume for lightsaber sound effects and hum.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.BladeLength = length;
        _settings.SliceReachMultiplier = sliceReach;
        _settings.Volume = volume;
        _settings.Validate();
    }

    private void DrawSaberEffectsSettings(SaberType type)
    {
        string typeName = type == SaberType.Dark ? "Dark" : "Light";
        ImGui.Separator();
        ImGui.TextWrapped($"{typeName} effects — shared by all {typeName} sabers");
        var effects = _settings.EffectsFor(type);
        if (!BeginSaberSettingsTable("##SaberEffects", "Shimmer amount")) return;
        float shimmerAmount = effects.ShimmerAmount, shimmerSpeed = effects.ShimmerSpeed;
        int arcCount = effects.ArcCount;
        float arcFrequency = effects.ArcFrequency, arcAmplitude = effects.ArcAmplitude;
        float arcBrightness = effects.ArcBrightness, arcWidth = effects.ArcWidth;
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
        }
        finally { ImGui.EndTable(); }
        if (changed)
        {
            effects.ShimmerAmount = shimmerAmount; effects.ShimmerSpeed = shimmerSpeed;
            effects.ArcCount = arcCount; effects.ArcFrequency = arcFrequency;
            effects.ArcAmplitude = arcAmplitude; effects.ArcBrightness = arcBrightness;
            effects.ArcWidth = arcWidth;
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
        if (!BeginSaberSettingsTable("##ForceSettings", "Clutter aim tolerance")) return;
        float radius = _settings.AngryForceRadius, tolerance = _settings.ClutterPickTolerance;
        int captureLimit = _settings.ForceCaptureLimit;
        bool changed;
        try
        {
            SaberSettingsRow("Angry Force radius");
            changed = ImGui.SliderFloat("##AngryForceRadius", ref radius, 1, 50, "%.1f m");
            ShowSaberControlTip("Capture objects within this distance of the selected surface point.");
            SaberSettingsRow("Force capture limit");
            changed |= ImGui.SliderInt("##ForceCaptureLimit", ref captureLimit, ForceCaptureLimits.Minimum, ForceCaptureLimits.Maximum);
            ShowSaberControlTip($"Maximum vessels, debris, and ground objects per Angry Force capture (default {ForceCaptureLimits.Default}); applies next capture.\n\nThe selected target is included first, then nearest objects fill remaining slots.");
            SaberSettingsRow("Clutter aim tolerance");
            changed |= ImGui.SliderFloat("##ClutterPickTolerance", ref tolerance, 0, .5f, "%.2f m");
            ShowSaberControlTip("After an exact miss, this radius catches nearby physical clutter.\n\nExact hits take priority; 0 allows exact hits only.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.AngryForceRadius = radius;
        _settings.ForceCaptureLimit = captureLimit;
        _settings.ClutterPickTolerance = tolerance;
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
            SaberSettingsRow("Force (left) X");
            changed |= ImGui.SliderFloat("##ForceControlsOffsetX", ref forceX, -1, 1, "%.2f width");
            SaberSettingsRow("Force (left) Y");
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
        if (!BeginSaberSettingsTable("##SaberCharacter", "Jump combo interval")) return;
        var grip = new float3(_settings.GripX, _settings.GripY, _settings.GripZ);
        var aim = new float3(_settings.AimX, _settings.AimY, _settings.AimZ);
        float curl = _settings.FingerCurl, jumpComboInterval = _settings.JumpComboIntervalSeconds;
        bool hideMmu = _settings.HideMmu, changed = false;
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
            SaberSettingsRow("Equipment");
            changed |= ImGui.Checkbox("Hide MMU controllers", ref hideMmu);
            ShowSaberControlTip("Hides standard MMU hand controllers while the lightsaber is equipped.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.GripX = grip.X; _settings.GripY = grip.Y; _settings.GripZ = grip.Z;
        _settings.AimX = aim.X; _settings.AimY = aim.Y; _settings.AimZ = aim.Z;
        _settings.FingerCurl = curl; _settings.JumpComboIntervalSeconds = jumpComboInterval; _settings.HideMmu = hideMmu;
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

    private static void ShowSaberControlTip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ShowSaberTooltip(text);
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
