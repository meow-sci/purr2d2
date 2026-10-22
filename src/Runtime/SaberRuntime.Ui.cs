using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
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
            ImGui.SetNextWindowSize(new float2(440, 720), ImGuiCond.FirstUseEver);
            bool draw = ImGui.Begin("Purr2D2 | Saber controls", ref _windowVisible);
            try
            {
                if (!draw) return;
                ImGui.TextColored(new float4(.25f, .85f, 1, 1), "May the paws be with you.");
                ImGui.Text("F7 panel  |  Ctrl+F7 equip  |  Shift+F7 ignite  |  Alt+F7 slice");
                ImGui.Separator();
                if (_fault != null)
                {
                    ImGui.TextWrapped(_fault);
                    if (_ready && ImGui.Button("Reset saber")) { ResetWorld(); _fault = null; }
                }
                bool hasKitten = Program.ControlledVehicle is KittenEva kitten && !kitten.IsDisposed && !Program.EditorFlag;
                if (!hasKitten) ImGui.TextWrapped("Take control of an EVA kitten to equip a saber.");
                ImGui.BeginDisabled(!_ready || !hasKitten || _fault != null);
                try
                {
                    if (ImGui.Button(_blade.Equipped ? "Unequip" : "Equip saber")) Queue(Command.Equip);
                    ImGui.SameLine();
                    ImGui.BeginDisabled(!_blade.Equipped);
                    if (ImGui.Button(_blade.TargetOn ? "Retract" : "Ignite")) Queue(Command.Ignite);
                    ImGui.EndDisabled();
                }
                finally { ImGui.EndDisabled(); }
                ImGui.Text($"{_blade.Phase}  |  {Length:F2} m");
                if (_blade.Equipped) ImGui.TextWrapped(_pose.Status);
                if (Universe.IsPaused()) ImGui.TextWrapped("Paused: blade, sound and cutting are suspended.");
                else if (Universe.GetSimulationSpeed() > SaberLimits.MaximumSimulationSpeed) ImGui.TextWrapped("Saber suspended: use timewarp of 5x or less.");
                ImGui.Separator();

                DrawAssistedSliceUi(hasKitten);
                ImGui.Separator();

                bool changed = false;
                float length = _settings.BladeLength, volume = _settings.Volume;
                float3 color = Color;
                changed |= ImGui.SliderFloat("Blade length", ref length, .25f, SaberLimits.MaximumBladeLength, "%.2f m");
                changed |= ImGui.ColorEdit3("Blade color", ref color);
                changed |= ImGui.SliderFloat("Sound volume", ref volume, 0, 1, "%.2f");
                if (changed)
                {
                    _settings.BladeLength = length;
                    _settings.ColorR = color.X; _settings.ColorG = color.Y; _settings.ColorB = color.Z;
                    _settings.Volume = volume;
                    _settings.Validate();
                }
                if (ImGui.CollapsingHeader("Paw alignment"))
                {
                    var grip = new float3(_settings.GripX, _settings.GripY, _settings.GripZ);
                    var aim = new float3(_settings.AimX, _settings.AimY, _settings.AimZ);
                    float curl = _settings.FingerCurl;
                    bool hideMmu = _settings.HideMmu;
                    bool poseChanged = ImGui.SliderFloat3("Grip offset (cm)", ref grip, -40, 40, "%.1f");
                    poseChanged |= ImGui.SliderFloat3("Blade direction", ref aim, -1, 1, "%.2f");
                    poseChanged |= ImGui.SliderFloat("Finger curl", ref curl, 0, .5f, "%.2f");
                    poseChanged |= ImGui.Checkbox("Hide MMU hand controllers", ref hideMmu);
                    if (poseChanged)
                    {
                        _settings.GripX = grip.X; _settings.GripY = grip.Y; _settings.GripZ = grip.Z;
                        _settings.AimX = aim.X; _settings.AimY = aim.Y; _settings.AimZ = aim.Z;
                        _settings.FingerCurl = curl; _settings.HideMmu = hideMmu;
                        _settings.Validate();
                    }
                }
                if (ImGui.Button("Save preferences"))
                {
                    try { _settings.Save(_settingsPath); }
                    catch (Exception ex) { _fault = "Could not save preferences: " + ex.Message; }
                }
                ImGui.Separator();
                ImGui.Text($"Parts sliced: {_targeting.SlicedParts}");
                if (ImGui.CollapsingHeader("Slice diagnostics"))
                {
                    string diagnostics = $"Purr2D2 {typeof(SaberRuntime).Assembly.GetName().Version}; KSA {typeof(Vehicle).Assembly.GetName().Version}\n"
                        + $"Equipped: {_blade.Equipped}; fresh pose: {_freshPoseThisTick}; pose age: {_pose.FreshnessFrames}\n"
                        + $"Slice active: {_sliceSequence.Active}; parts sliced: {_targeting.SlicedParts}\n{_sliceStatus}";
                    ImGui.TextWrapped(diagnostics);
                    if (ImGui.Button("Copy slice diagnostics")) ImGui.SetClipboardText(diagnostics);
                }
                if (_renderer.LastError != null) ImGui.TextWrapped(_renderer.LastError);
                if (_audio.LastError != null) ImGui.TextWrapped(_audio.LastError);
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report("Saber panel failed", ex); }
    }
}
