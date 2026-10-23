using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private double _preferencesSavedUntil;

    internal void SetInitializationFault(string message)
    {
        _fault = "Initialization failed: " + message;
        _ready = false;
        _windowVisible = true;
        // The caller removes our Harmony hooks after this, so release partial ownership now.
        Cleanup(ResetWorld, "World");
        Cleanup(_audio.Shutdown, "Audio");
        Cleanup(_renderer.Dispose, "Renderer");
        Cleanup(_pose.Dispose, "Pose");
    }

    internal void DrawUi()
    {
        if (!_windowVisible || _disposed) return;
        try
        {
            ImGui.SetNextWindowSize(new float2(480, 540), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new float2(390, 320), new float2(float.MaxValue));
            bool draw = ImGui.Begin("Purr2D2 | Saber controls", ref _windowVisible);
            try
            {
                if (!draw) return;
                bool hasKitten = Program.ControlledVehicle is KittenEva kitten && !kitten.IsDisposed && !Program.EditorFlag;
                DrawSaberControls(hasKitten);
                if (ImGui.Button(_showSaberOverlay ? "Hide saber overlay" : "Show saber overlay", new float2(-1, 0)))
                    SetSaberOverlayVisible(!_showSaberOverlay);

                if (_fault != null)
                {
                    ImGui.TextWrapped(_fault);
                    if (_ready && ImGui.Button("Reset saber")) { ResetWorld(); _fault = null; }
                }
                else if (!_ready) ImGui.TextDisabled("Saber is starting...");
                else if (!hasKitten) ImGui.TextWrapped("Control an EVA kitten to use the saber.");
                else if (_blade.Equipped)
                {
                    ImGui.TextDisabled($"{_blade.Phase}  |  {Length:F2} m");
                    if (!_pose.Eligible) ImGui.TextWrapped(_pose.Status);
                    if (Universe.IsPaused()) ImGui.TextWrapped("Paused: saber suspended.");
                    else if (Universe.GetSimulationSpeed() > SaberLimits.MaximumSimulationSpeed)
                        ImGui.TextWrapped("Saber suspended above 5x timewarp.");
                }
                if (_renderer.LastError != null) ImGui.TextWrapped(_renderer.LastError);
                if (_audio.LastError != null) ImGui.TextWrapped(_audio.LastError);

                ImGui.SeparatorText("Slice");
                DrawAssistedSliceUi(hasKitten);

                ImGui.SeparatorText("Saber settings");
                DrawSaberAppearanceSettings();
                if (ImGui.CollapsingHeader("Character & grip")) DrawSaberCharacterSettings();

                ImGui.Separator();
                if (ImGui.Button("Save preferences"))
                {
                    try { _settings.Save(_settingsPath); _preferencesSavedUntil = _playerTime + 3; }
                    catch (Exception ex) { _fault = "Could not save preferences: " + ex.Message; }
                }
                if (_playerTime < _preferencesSavedUntil)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("Saved");
                }
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
        catch (Exception ex) { Report("Saber panel failed", ex); }
    }

    private void DrawSaberControls(bool hasKitten)
    {
        ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null);
        try
        {
            if (!ImGui.BeginTable("##SaberActions", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX)) return;
            try
            {
                ImGui.TableNextColumn();
                if (ImGui.Button(_blade.Equipped ? "Unequip" : "Equip saber", new float2(-1, 0))) Queue(Command.Equip);
                ShowSaberControlTip("Ctrl+F7");
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(!_blade.Equipped);
                try
                {
                    if (ImGui.Button(_blade.TargetOn ? "Retract" : "Ignite", new float2(-1, 0))) Queue(Command.Ignite);
                    ShowSaberControlTip("Shift+F7");
                    ImGui.TableNextColumn();
                    ImGui.BeginDisabled(_sliceSequence.Active && !_sliceSequence.EndPresented);
                    try
                    {
                        if (ImGui.Button("Swing", new float2(-1, 0))) Queue(Command.Swing);
                        ShowSaberControlTip("Normal saber swipe. Alt+F7.");
                    }
                    finally { ImGui.EndDisabled(); }
                }
                finally { ImGui.EndDisabled(); }
            }
            finally { ImGui.EndTable(); }
        }
        finally { ImGui.EndDisabled(); }
    }

    private void DrawSaberAppearanceSettings()
    {
        if (!BeginSaberSettingsTable("##SaberAppearance")) return;
        bool changed = false, colorChanged = false;
        float length = _settings.BladeLength, volume = _settings.Volume;
        float3 color = AppearanceColor;
        try
        {
            SaberSettingsRow(AppearanceKitten != null ? "Kitten color" : "Color preference");
            colorChanged = ImGui.ColorEdit3("##BladeColor", ref color, ImGuiColorEditFlags.NoInputs);
            changed |= colorChanged;
            ShowSaberControlTip("Per-kitten colors last for this session. New EVA kittens cycle red, green, yellow and purple.");
            SaberSettingsRow("Blade length");
            changed |= ImGui.SliderFloat("##BladeLength", ref length, .25f, SaberLimits.MaximumBladeLength, "%.2f m");
            SaberSettingsRow("Sound volume");
            changed |= ImGui.SliderFloat("##SoundVolume", ref volume, 0, 1, "%.2f");
        }
        finally { ImGui.EndTable(); }
        if (!changed) return;
        _settings.BladeLength = length;
        if (colorChanged) SetAppearanceColor(color);
        _settings.Volume = volume;
        _settings.Validate();
        _preferencesSavedUntil = 0;
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

    private static bool BeginSaberSettingsTable(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.NoPadOuterX)) return false;
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed,
            Math.Max(ImGui.CalcTextSize("Blade direction").X, ImGui.CalcTextSize("Color preference").X));
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
