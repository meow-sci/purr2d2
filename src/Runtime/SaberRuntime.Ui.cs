using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private double _preferencesSavedUntil;
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

                ImGui.SeparatorText("Shared saber settings");
                DrawSaberAppearanceSettings();
                ImGui.TextWrapped("Use the overlay to control kittens. Each kitten's saber menu contains its color, type and shared effects.");
                ImGui.SeparatorText("Force settings");
                DrawForceSettings();
                if (ImGui.CollapsingHeader("Slice resources")) DrawSliceResourceSettings();
                else _confirmSliceDebrisRemoval = false;
                if (ImGui.CollapsingHeader("Control positions")) DrawControlPositionSettings();
                if (ImGui.CollapsingHeader("Character & grip")) DrawSaberCharacterSettings();

                ImGui.Separator();
                DrawSavePreferences();
                if (ImGui.CollapsingHeader("Slice diagnostics"))
                {
                    string diagnostics = $"Purr2D2 {typeof(SaberRuntime).Assembly.GetName().Version}; KSA {typeof(Vehicle).Assembly.GetName().Version}\n"
                        + $"Equipped: {_blade.Equipped}; fresh pose: {_freshPoseThisTick}; pose age: {_pose.FreshnessFrames}\n"
                        + $"Slice active: {_sliceSequence.Active}; parts sliced: {_targeting.SlicedParts}\n{_sliceStatus}\n{_slicer.Diagnostics}";
                    ImGui.TextWrapped(diagnostics);
                    if (ImGui.Button("Copy slice diagnostics")) ImGui.SetClipboardText(diagnostics);
                }
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report("Settings window failed", ex); }
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
            ShowSaberControlTip("Maximum selection distance = visible blade length x this value; 1x adds no extra grace. Applies to assisted SLICE selection before the stroke starts. Use Save preferences to keep changes.");
            double visibleLength = _blade.Deployment * length;
            ImGui.TextWrapped($"Maximum selection: {SaberLimits.SliceSelectionReach(visibleLength, sliceReach):F2} m now; {SaberLimits.SliceSelectionReach(length, sliceReach):F2} m fully extended.");
            SaberSettingsRow("Sound volume");
            changed |= ImGui.SliderFloat("##SoundVolume", ref volume, 0, 1, "%.2f");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.BladeLength = length;
        _settings.SliceReachMultiplier = sliceReach;
        _settings.Volume = volume;
        _settings.Validate();
        _preferencesSavedUntil = 0;
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
            ShowSaberControlTip("Strength of the blade shimmer. 0 keeps the halo steady; 1x is the baseline.");
            SaberSettingsRow("Shimmer speed");
            changed |= ImGui.SliderFloat("##ShimmerSpeed", ref shimmerSpeed, 0, 4, "%.2fx");
            ShowSaberControlTip("Motion speed of the blade shimmer. 0 freezes it; 1x is the baseline.");
            SaberSettingsRow("Arc count");
            changed |= ImGui.SliderInt("##ArcCount", ref arcCount, 0, SaberSettings.MaximumArcCount);
            ShowSaberControlTip("Maximum arcs per saber. Distance and screen size can reduce the count; 0 disables arcs.");
            SaberSettingsRow("Arc frequency");
            changed |= ImGui.SliderFloat("##ArcFrequency", ref arcFrequency, .1f, SaberSettings.MaximumArcFrequency, "%.2fx");
            ShowSaberControlTip("Burst rate per arc. 1x gives one burst every two seconds; higher rates make bursts more frequent and shorter.");
            SaberSettingsRow("Arc amplitude");
            changed |= ImGui.SliderFloat("##ArcAmplitude", ref arcAmplitude, 0, 3, "%.2fx");
            ShowSaberControlTip("How far arcs stray from the blade. 0 removes the outward spread; 1x is the baseline.");
            SaberSettingsRow("Arc brightness");
            changed |= ImGui.SliderFloat("##ArcBrightness", ref arcBrightness, 0, SaberSettings.MaximumArcBrightness, "%.2fx");
            ShowSaberControlTip("Brightness of the electrical arcs. 0 hides them; 1x is the baseline.");
            SaberSettingsRow("Arc width");
            changed |= ImGui.SliderFloat("##ArcWidth", ref arcWidth, .25f, SaberSettings.MaximumArcWidth, "%.2fx");
            ShowSaberControlTip("Width of each electrical arc. 1x is the baseline width.");
        }
        finally { ImGui.EndTable(); }
        if (changed)
        {
            effects.ShimmerAmount = shimmerAmount; effects.ShimmerSpeed = shimmerSpeed;
            effects.ArcCount = arcCount; effects.ArcFrequency = arcFrequency;
            effects.ArcAmplitude = arcAmplitude; effects.ArcBrightness = arcBrightness;
            effects.ArcWidth = arcWidth;
            effects.Validate(type);
            _preferencesSavedUntil = 0;
        }
        if (ImGui.Button($"Reset {typeName} effects"))
        {
            effects.Reset(type);
            _preferencesSavedUntil = 0;
        }
        ShowSaberControlTip($"Restores only the shared {typeName} effects. Use Save preferences to keep the changes.");
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
            ShowSaberControlTip("Capture nearby objects within this distance of the selected surface point.");
            SaberSettingsRow("Force capture limit");
            changed |= ImGui.SliderInt("##ForceCaptureLimit", ref captureLimit, ForceCaptureLimits.Minimum, ForceCaptureLimits.Maximum);
            ShowSaberControlTip($"Maximum total vessels, debris and physical ground objects in one Angry Force capture (default {ForceCaptureLimits.Default}). Higher counts cost more physics work. Groups above the limit are refused without capturing any objects. Applies to the next capture.");
            SaberSettingsRow("Clutter aim tolerance");
            changed |= ImGui.SliderFloat("##ClutterPickTolerance", ref tolerance, 0, .5f, "%.2f m");
            ShowSaberControlTip("After exact picking misses, a small sphere of this radius catches near misses on physical clutter. Exact hits take priority; 0 uses exact hits only.");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.AngryForceRadius = radius;
        _settings.ForceCaptureLimit = captureLimit;
        _settings.ClutterPickTolerance = tolerance;
        _settings.Validate();
        _preferencesSavedUntil = 0;
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
            _preferencesSavedUntil = 0;
        }
        if (ImGui.Button("Reset control positions"))
        {
            _settings.ForceControlsOffsetX = _settings.ForceControlsOffsetY = 0;
            _settings.SaberControlsOffsetX = _settings.SaberControlsOffsetY = 0;
            _preferencesSavedUntil = 0;
        }
    }

    private void DrawSavePreferences()
    {
        if (ImGui.Button("Save preferences"))
        {
            try
            {
                _settings.Save(_settingsPath);
                _preferencesSavedUntil = _playerTime + 3;
                _preferencesSaveError = null;
            }
            catch (Exception ex)
            {
                _preferencesSavedUntil = 0;
                _preferencesSaveError = "Could not save preferences: " + ex.Message;
            }
        }
        if (_playerTime < _preferencesSavedUntil)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Saved");
        }
        if (_preferencesSaveError != null) ImGui.TextWrapped(_preferencesSaveError);
    }

    private void DrawSaberCharacterSettings()
    {
        if (!BeginSaberSettingsTable("##SaberCharacter")) return;
        var grip = new float3(_settings.GripX, _settings.GripY, _settings.GripZ);
        var aim = new float3(_settings.AimX, _settings.AimY, _settings.AimZ);
        float curl = _settings.FingerCurl;
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
            SaberSettingsRow("Equipment");
            changed |= ImGui.Checkbox("Hide MMU controllers", ref hideMmu);
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.GripX = grip.X; _settings.GripY = grip.Y; _settings.GripZ = grip.Z;
        _settings.AimX = aim.X; _settings.AimY = aim.Y; _settings.AimZ = aim.Z;
        _settings.FingerCurl = curl; _settings.HideMmu = hideMmu;
        _settings.Validate();
        _preferencesSavedUntil = 0;
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}
