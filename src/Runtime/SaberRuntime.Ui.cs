using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

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

    private void ConfigureCutter()
    {
        _cutter.Enabled = _settings.CuttingEnabled;
        _cutter.ContactSeconds = _settings.ContactSeconds;
        _cutter.FragmentStruckPart = _settings.FragmentStruckPart;
        _cutter.ArbitrarySlicing = _settings.ArbitrarySlicing;
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
                ImGui.Text("F7 panel  |  Ctrl+F7 equip  |  Shift+F7 ignite  |  Alt+F7 swing");
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
                    ImGui.SameLine();
                    if (ImGui.Button("Swing")) Queue(Command.Swing);
                    ImGui.EndDisabled();
                }
                finally { ImGui.EndDisabled(); }
                ImGui.Text($"{_blade.Phase}  |  {Length:F2} m");
                if (_blade.Equipped) ImGui.TextWrapped(_pose.Status);
                if (Universe.IsPaused()) ImGui.TextWrapped("Paused: blade, sound and cutting are suspended.");
                else if (Universe.GetSimulationSpeed() > 1) ImGui.TextWrapped("Return to 1x speed to use the saber.");
                ImGui.Separator();

                bool changed = false;
                float length = _settings.BladeLength, volume = _settings.Volume, contact = _settings.ContactSeconds;
                float3 color = Color;
                changed |= ImGui.SliderFloat("Blade length", ref length, .25f, 1.5f, "%.2f m");
                changed |= ImGui.ColorEdit3("Blade color", ref color);
                changed |= ImGui.SliderFloat("Sound volume", ref volume, 0, 1, "%.2f");
                bool cutting = _settings.CuttingEnabled;
                changed |= ImGui.Checkbox("Cut vessels on contact", ref cutting);
                changed |= ImGui.SliderFloat("Contact to cut", ref contact, .1f, 5, "%.2f s");
                bool slicing = _settings.ArbitrarySlicing;
                changed |= ImGui.Checkbox("Arbitrary plane slicing (experimental)", ref slicing);
                bool fragment = _settings.FragmentStruckPart;
                ImGui.BeginDisabled(slicing || !_cutter.FragmentationAvailable);
                changed |= ImGui.Checkbox("Fragment struck part", ref fragment);
                ImGui.EndDisabled();
                if (slicing)
                    ImGui.TextWrapped("The blade and swing define a cutting plane. Closed inert parts become new solid fragments with sealed, cooling cut faces. Unsupported parts remain intact. Concave fragments use enclosing convex colliders. Keep the Generated/Slices folder with saves containing sliced parts.");
                else if (fragment)
                    ImGui.TextWrapped("Uses native part failure without an explosion. The struck part is destroyed; up to two of its authored subparts become debris. Crewed vessels are excluded.");
                else
                    ImGui.TextWrapped("Contact heats the surface, then separates an eligible existing part connection. Cuts follow the vessel's authored structure.");
                if (changed)
                {
                    _settings.BladeLength = length;
                    _settings.ColorR = color.X; _settings.ColorG = color.Y; _settings.ColorB = color.Z;
                    _settings.Volume = volume; _settings.ContactSeconds = contact;
                    _settings.CuttingEnabled = cutting; _settings.FragmentStruckPart = fragment;
                    _settings.ArbitrarySlicing = slicing;
                    _settings.Validate();
                }
                if (ImGui.CollapsingHeader("Cylinder cutting autopilot"))
                {
                    ImGui.TextWrapped("Uses the controlled kitten's stock vessel target and MMU backpack. Capture from beside the desired cross-section, align, inspect the pose, then run one circular cut. Abort immediately restores manual MMU control.");
                    string targetName = (Program.ControlledVehicle as KittenEva)?.Target is Vehicle target && !target.IsDisposed
                        ? target.Id : "none";
                    ImGui.Text($"Stock target: {targetName}");

                    int axis = _settings.AutopilotAxis;
                    bool axisChanged = false;
                    ImGui.BeginDisabled(_autopilot.Active);
                    bool xClicked = ImGui.RadioButton("Cylinder X", axis == 0);
                    if (xClicked) { axis = 0; axisChanged = true; }
                    ImGui.SameLine();
                    bool yClicked = ImGui.RadioButton("Y", axis == 1);
                    if (yClicked) { axis = 1; axisChanged = true; }
                    ImGui.SameLine();
                    bool zClicked = ImGui.RadioButton("Z", axis == 2);
                    if (zClicked) { axis = 2; axisChanged = true; }
                    bool reverseAxis = _settings.AutopilotAxisReversed;
                    axisChanged |= ImGui.Checkbox("Reverse cylinder axis", ref reverseAxis);
                    ImGui.EndDisabled();

                    bool clockwise = _settings.AutopilotClockwise;
                    bool autoChanged = ImGui.Checkbox("Clockwise orbit", ref clockwise);
                    float clearance = _settings.AutopilotClearance;
                    float orbitSpeed = _settings.AutopilotOrbitSpeed;
                    autoChanged |= ImGui.SliderFloat("Hull clearance", ref clearance, .3f, 1.5f, "%.2f m");
                    autoChanged |= ImGui.SliderFloat("Orbit speed", ref orbitSpeed, .04f, .4f, "%.2f m/s");

                    ImGui.Text($"Kitten roll: {_settings.AutopilotRollQuarterTurns * 90} degrees");
                    if (ImGui.Button("Rotate -90°"))
                    {
                        _settings.AutopilotRollQuarterTurns--;
                        autoChanged = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Rotate +90°"))
                    {
                        _settings.AutopilotRollQuarterTurns++;
                        autoChanged = true;
                    }

                    if (axisChanged || autoChanged)
                    {
                        _settings.AutopilotAxis = axis;
                        _settings.AutopilotAxisReversed = reverseAxis;
                        _settings.AutopilotClockwise = clockwise;
                        _settings.AutopilotClearance = clearance;
                        _settings.AutopilotOrbitSpeed = orbitSpeed;
                        _settings.Validate();
                        if (axisChanged) _autopilot.InvalidateCapture();
                    }

                    ImGui.BeginDisabled(!hasKitten || _autopilot.Active);
                    if (ImGui.Button("1. Capture target ring")) Queue(Command.CaptureRing);
                    ImGui.SameLine();
                    ImGui.BeginDisabled(!_autopilot.Captured);
                    if (ImGui.Button("2. Align / approach")) Queue(Command.AlignRing);
                    ImGui.EndDisabled();
                    ImGui.EndDisabled();

                    ImGui.BeginDisabled(!_autopilot.Active);
                    if (ImGui.Button("3. Cut one orbit")) Queue(Command.StartCutOrbit);
                    ImGui.SameLine();
                    if (ImGui.Button("Abort / release")) Queue(Command.AbortAutopilot);
                    ImGui.EndDisabled();
                    if (_autopilot.Active || _autopilot.Progress > 0)
                        ImGui.ProgressBar(_autopilot.Progress, new float2(-1, 0));
                    ImGui.TextWrapped(_autopilot.Status);
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
                ImGui.TextWrapped(_cutter.Status);
                ImGui.Text($"Touching parts: {_cutter.Contacts}  |  Contact heat: {_cutter.HottestContactSeconds:F2} s");
                ImGui.Text($"Severed: {_cutter.SeveredConnections}  Fragmented: {_cutter.FragmentedParts}  Sliced: {_cutter.SlicedParts}");
                if (ImGui.CollapsingHeader("Contact diagnostics"))
                {
                    string diagnostics = $"Purr2D2 {typeof(SaberRuntime).Assembly.GetName().Version}; KSA {typeof(Vehicle).Assembly.GetName().Version}\n"
                        + $"Equipped: {_blade.Equipped}; cutting enabled: {_settings.CuttingEnabled}; fresh pose: {_freshPoseThisTick}; pose age: {_pose.FreshnessFrames}\n"
                        + _cutter.GetDiagnostics();
                    ImGui.TextWrapped(diagnostics);
                    if (ImGui.Button("Copy contact diagnostics"))
                    {
                        ImGui.SetClipboardText(diagnostics);
                        Console.WriteLine("[purr2d2] Contact diagnostics\n" + diagnostics);
                    }
                }
                if (_renderer.LastError != null) ImGui.TextWrapped(_renderer.LastError);
                if (_audio.LastError != null) ImGui.TextWrapped(_audio.LastError);
                if (_cutter.BudgetLimited) ImGui.TextWrapped("Contact geometry budget reached; only checked surfaces can be cut.");
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex) { Report("Saber panel failed", ex); }
    }
}
