using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Autopilot;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private bool _ringSelectionArmed;
    private bool _ringSelectionOwnsLeftRelease;
    private int _ringSelectionArmedFrame;
    private KittenEva? _ringSelectionOwner;
    private Vehicle? _ringSelectionTarget;
    private Vehicle? _selectedRingPointTarget;
    private double3 _selectedRingPointBody;
    private string _ringSelectionStatus = "";

    private void DrawAutopilotUi(bool hasKitten)
    {
        if (!ImGui.CollapsingHeader("Cylinder cutting autopilot")) return;
        ImGui.TextWrapped("Choose a vessel and Approach ring. The autopilot finds a clear ring, flies to it, aligns the saber and holds. You can optionally select a hull point first.");
        var owner = Program.ControlledVehicle as KittenEva;
        bool controlsAvailable = _ready && hasKitten && _fault == null;
        bool geometryChanged = false;

        ImGui.BeginDisabled(!controlsAvailable || _autopilot.Active);
        try
        {
            if (ImGui.BeginCombo("Vessel target", owner?.Target?.Id ?? "None"))
            {
                try
                {
                    if (ImGui.Selectable("None##PurrTargetNone", owner?.Target == null)) SelectTarget(null);
                    var vessels = new List<Vehicle>();
                    if (Universe.CurrentSystem is { } system)
                        foreach (var item in system.All.AsSpan())
                            if (item is Vehicle vessel && vessel is not KittenEva
                                && !vessel.IsDisposed && !vessel.IsEditedVehicle)
                                vessels.Add(vessel);
                    foreach (Vehicle vessel in vessels.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase))
                        if (ImGui.Selectable(vessel.Id + "##PurrTarget" + vessel.Hash,
                            ReferenceEquals(owner?.Target, vessel))) SelectTarget(vessel);
                }
                finally { ImGui.EndCombo(); }
            }

            ImGui.BeginDisabled(owner?.Target is not Vehicle { IsDisposed: false });
            try
            {
                if (ImGui.Button("Approach ring"))
                {
                    _ringSelectionArmed = false;
                    _ringSelectionStatus = "";
                    Queue(Command.AlignRing);
                }
                ImGui.SameLine();
                if (ImGui.Button(_ringSelectionArmed ? "Cancel point selection" : "Select hull point"))
                {
                    _ringSelectionArmed = !_ringSelectionArmed;
                    _ringSelectionArmedFrame = ImGui.GetFrameCount();
                    _ringSelectionOwner = owner;
                    _ringSelectionTarget = owner?.Target as Vehicle;
                    _ringSelectionStatus = _ringSelectionArmed
                        ? "Click a visible surface of the selected vessel, then Approach ring. Esc cancels selection."
                        : "Point selection cancelled.";
                }
            }
            finally { ImGui.EndDisabled(); }

            if (ImGui.CollapsingHeader("Ring setup (optional)"))
            {
                bool automatic = _settings.AutopilotAutoAxis;
                int axis = _settings.AutopilotAxis;
                if (ImGui.RadioButton("Auto axis", automatic)) { automatic = true; geometryChanged = true; }
                ImGui.SameLine();
                if (ImGui.RadioButton("X##CutAxis", !automatic && axis == 0)) { automatic = false; axis = 0; geometryChanged = true; }
                ImGui.SameLine();
                if (ImGui.RadioButton("Y##CutAxis", !automatic && axis == 1)) { automatic = false; axis = 1; geometryChanged = true; }
                ImGui.SameLine();
                if (ImGui.RadioButton("Z##CutAxis", !automatic && axis == 2)) { automatic = false; axis = 2; geometryChanged = true; }
                bool reverse = _settings.AutopilotAxisReversed;
                geometryChanged |= ImGui.Checkbox("Reverse cylinder axis", ref reverse);
                float clearance = _settings.AutopilotClearance;
                geometryChanged |= ImGui.SliderFloat("Hull clearance", ref clearance, .3f, 1.5f, "%.2f m");
                if (geometryChanged)
                {
                    _settings.AutopilotAutoAxis = automatic;
                    _settings.AutopilotAxis = axis;
                    _settings.AutopilotAxisReversed = reverse;
                    _settings.AutopilotClearance = clearance;
                    _settings.Validate();
                    _autopilot.InvalidateCapture();
                    _ringSelectionStatus = "Geometry changed; find a new preview or select a hull point.";
                }

                bool clockwise = _settings.AutopilotClockwise;
                float speed = _settings.AutopilotOrbitSpeed;
                bool motionChanged = ImGui.Checkbox("Clockwise orbit", ref clockwise);
                motionChanged |= ImGui.SliderFloat("Maximum cut speed", ref speed, .04f, .4f, "%.2f m/s");
                ImGui.TextWrapped("Cutting speed automatically reduces to match the available MMU thrust.");
                if (motionChanged)
                {
                    _settings.AutopilotClockwise = clockwise;
                    _settings.AutopilotOrbitSpeed = speed;
                    _settings.Validate();
                }

                ImGui.BeginDisabled(owner?.Target is not Vehicle);
                try
                {
                    if (ImGui.Button("Find ring preview"))
                    {
                        _selectedRingPointTarget = null;
                        _ringSelectionArmed = false;
                        _ringSelectionStatus = "";
                        Queue(Command.CaptureRing);
                    }
                }
                finally { ImGui.EndDisabled(); }

                ImGui.BeginDisabled(!_autopilot.Captured);
                try
                {
                    if (ImGui.Button("Ring -0.25 m")) { _ringSelectionStatus = ""; Queue(Command.NudgeRingBack); }
                    ImGui.SameLine();
                    if (ImGui.Button("Ring +0.25 m")) { _ringSelectionStatus = ""; Queue(Command.NudgeRingForward); }
                }
                finally { ImGui.EndDisabled(); }
                ImGui.BeginDisabled(owner?.Target is not Vehicle { IsDisposed: false });
                try
                {
                    if (ImGui.Button("Match target velocity"))
                    {
                        _ringSelectionArmed = false;
                        _ringSelectionStatus = "";
                        Queue(Command.MatchVelocity);
                    }
                }
                finally { ImGui.EndDisabled(); }
            }
        }
        finally { ImGui.EndDisabled(); }

        ImGui.BeginDisabled(!controlsAvailable);
        try
        {
            ImGui.BeginDisabled(!_autopilot.Active);
            try
            {
                ImGui.BeginDisabled(!_autopilot.ReadyToCut);
                try
                {
                    if (ImGui.Button("Cut ring")) Queue(Command.StartCutOrbit);
                }
                finally { ImGui.EndDisabled(); }
                ImGui.SameLine();
                if (ImGui.Button("Abort / release")) { _ringSelectionArmed = false; Queue(Command.AbortAutopilot); }
            }
            finally { ImGui.EndDisabled(); }
            if (ImGui.CollapsingHeader("Saber orientation (optional)"))
            {
                ImGui.Text($"Kitten roll: {_settings.AutopilotRollQuarterTurns * 90} degrees");
                if (ImGui.Button("Rotate -90°")) { _settings.AutopilotRollQuarterTurns--; _settings.Validate(); }
                ImGui.SameLine();
                if (ImGui.Button("Rotate +90°")) { _settings.AutopilotRollQuarterTurns++; _settings.Validate(); }
            }
        }
        finally { ImGui.EndDisabled(); }
        if (_autopilot.WantsIgnited || _autopilot.Progress > 0)
            ImGui.ProgressBar(_autopilot.Progress, new float2(-1, 0));
        if (_ringSelectionStatus.Length > 0) ImGui.TextWrapped(_ringSelectionStatus);
        ImGui.TextWrapped(_autopilot.Status);
        ImGui.TextDisabled("Cyan: cut preview  |  Amber: MMU path and entry");
        if (ImGui.CollapsingHeader("Autopilot diagnostics"))
        {
            string diagnostics = _autopilot.GetDiagnostics(owner, _settings);
            if (ImGui.Button("Copy autopilot diagnostics")) ImGui.SetClipboardText(diagnostics);
            ImGui.TextWrapped(diagnostics);
        }

        void SelectTarget(Vehicle? target)
        {
            _ringSelectionArmed = false;
            _selectedRingPointTarget = null;
            _ringSelectionStatus = "";
            QueueTarget(target);
        }
    }

    // Called before stock mouse dispatch. ImGui receives the platform event separately;
    // swallowing both halves here leaves stock targeting and camera click state untouched.
    internal bool ConsumeRingSelectionMouse(GlfwMouseButton button, GlfwButtonAction action)
    {
        if (button != GlfwMouseButton.Number1) return false;
        if (action == GlfwButtonAction.Release)
        {
            bool consumed = _ringSelectionOwnsLeftRelease;
            _ringSelectionOwnsLeftRelease = false;
            return consumed;
        }
        if (action != GlfwButtonAction.Press || !_ringSelectionArmed || !_ready || _disposed
            || !Program.DrawUI || Program.EditorFlag || _autopilot.Active
            || !ReferenceEquals(Program.ControlledVehicle, _ringSelectionOwner)
            || !ReferenceEquals(_ringSelectionOwner?.Target, _ringSelectionTarget)
            || _ringSelectionTarget?.IsDisposed != false || ImGui.GetIO().WantCaptureMouse
            || Program.GetCursorMode() != GlfwCursorMode.Normal
            || !ReferenceEquals(Program.HoveredViewport, Program.MainViewport)) return false;
        _ringSelectionOwnsLeftRelease = true;
        return true;
    }

    internal void DrawAutopilotOverlay()
    {
        if (!_ready || _disposed || Program.EditorFlag || Program.ControlledVehicle is not KittenEva { IsDisposed: false } owner)
        {
            _ringSelectionArmed = false;
            _selectedRingPointTarget = null;
            return;
        }
        try
        {
            if (_ringSelectionArmed && (_autopilot.Active || !ReferenceEquals(owner, _ringSelectionOwner)
                || !ReferenceEquals(owner.Target, _ringSelectionTarget) || _ringSelectionTarget?.IsDisposed != false))
            {
                _ringSelectionArmed = false;
                _ringSelectionStatus = "Point selection cancelled because the controlled kitten or target changed.";
            }
            if (!ReferenceEquals(owner.Target, _selectedRingPointTarget)) _selectedRingPointTarget = null;
            if (_ringSelectionArmed && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                _ringSelectionArmed = false;
                _ringSelectionStatus = "Point selection cancelled.";
            }

            IGameViewport viewport = Program.MainViewport;
            if (!viewport.Visible) return;
            Camera camera = viewport.GetCamera();
            var uiViewport = ImGui.GetMainViewport();
            // The main scene fills this ImGui viewport. Camera projection uses framebuffer
            // pixels; ImGui positions and the cursor use logical pixels plus the OS origin.
            float2 origin = uiViewport.Pos;
            float2 size = uiViewport.Size;
            int2 framebuffer = camera.FramebufferSize;
            if (!OverlayFinite(origin) || !OverlayFinite(size) || size.X <= 0 || size.Y <= 0
                || framebuffer.X <= 0 || framebuffer.Y <= 0) return;
            float2 toUi = new(size.X / framebuffer.X, size.Y / framebuffer.Y);
            var draw = ImGui.GetBackgroundDrawList(uiViewport);
            draw.PushClipRect(origin, origin + size);
            try
            {
                if (_autopilot.TryGetPreview(out Vehicle target, out CutRing ring)
                    && !target.IsDisposed && ReferenceEquals(owner.Target, target)) DrawPreview(target, ring);
                if (_selectedRingPointTarget is { IsDisposed: false } selected)
                {
                    double3 ego = camera.GetPositionEgo(selected) + _selectedRingPointBody.Transform(selected.Body2Cce);
                    if (Project(ego, out float2 screen)) Marker(screen, "Selected hull point", Amber());
                }
                if (_ringSelectionArmed)
                {
                    Label(origin + new float2(18, 76), "SELECT CUT STATION: click the target hull  |  Esc cancels", Amber());
                    float2 cursor = ImGui.GetMousePos() - origin;
                    if (!ImGui.GetIO().WantCaptureMouse && OverlayFinite(cursor)
                        && cursor.X >= 0 && cursor.Y >= 0 && cursor.X < size.X && cursor.Y < size.Y)
                    {
                        float2 pointer = origin + cursor;
                        draw.AddCircle(pointer, 8, Amber(.85f), 16);
                        draw.AddLine(pointer - new float2(11, 0), pointer + new float2(11, 0), Amber(.85f));
                        draw.AddLine(pointer - new float2(0, 11), pointer + new float2(0, 11), Amber(.85f));
                    }
                    TrySelectPoint();
                }
            }
            finally { draw.PopClipRect(); }

            void DrawPreview(Vehicle target, CutRing ring)
            {
                double3 targetEgo = camera.GetPositionEgo(target);
                doubleQuat body2Ego = target.Body2Cce;
                double3 radial = ring.Radial.NormalizeOrZero();
                double3 tangent = double3.Cross(ring.Axis, radial).NormalizeOrZero();
                double3 cameraBody = (-targetEgo).Transform(body2Ego.Inverse());
                double flightRadius = ring.Radius + _settings.AutopilotClearance;
                const int segments = 96;
                for (int i = 0; i < segments; i++)
                {
                    double a = i * Math.PI * 2 / segments;
                    double b = (i + 1) * Math.PI * 2 / segments;
                    double3 first = radial * Math.Cos(a) + tangent * Math.Sin(a);
                    double3 second = radial * Math.Cos(b) + tangent * Math.Sin(b);
                    bool far = double3.Dot(first + second, cameraBody - ring.Center) < 0;
                    if (!far || i % 4 < 2)
                        BodyLine(ring.Center + first * ring.Radius, ring.Center + second * ring.Radius,
                            Cyan(far ? .38f : 1), far ? 1.2f : 2.2f);
                    if (i % 3 != 2)
                        BodyLine(ring.Center + first * flightRadius, ring.Center + second * flightRadius,
                            Amber(far ? .3f : .7f), 1.1f);
                }
                double3 entry = ring.Center + radial * flightRadius;
                double3 surface = ring.Center + radial * ring.Radius;
                BodyLine(surface, entry, Amber(.7f), 1.4f);
                double3 entryEgo = targetEgo + entry.Transform(body2Ego);
                Line(camera.GetPositionEgo(owner), entryEgo, Amber(.45f), 1);
                if (Project(targetEgo + surface.Transform(body2Ego), out float2 cutScreen))
                    Marker(cutScreen, _autopilot.Active ? "CUT RING" : "CUT PREVIEW", Cyan());
                if (Project(entryEgo, out float2 entryScreen)) Marker(entryScreen, "MMU entry", Amber());

                void BodyLine(double3 first, double3 second, ImColor8 color, float width) =>
                    Line(targetEgo + first.Transform(body2Ego), targetEgo + second.Transform(body2Ego), color, width);
            }

            void TrySelectPoint()
            {
                var io = ImGui.GetIO();
                if (ImGui.GetFrameCount() <= _ringSelectionArmedFrame || io.WantCaptureMouse
                    || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || ImGui.IsAnyItemHovered()
                    || Program.GetCursorMode() != GlfwCursorMode.Normal
                    || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
                float2 local = ImGui.GetMousePos() - origin;
                if (!OverlayFinite(local) || local.X < 0 || local.Y < 0 || local.X >= size.X || local.Y >= size.Y) return;
                if (_ringSelectionTarget is not { IsDisposed: false } target) return;
                if (!TargetSurfaceSnapshot.TryCapture(target, out var triangles, out _, out string reason))
                {
                    _ringSelectionStatus = reason;
                    return;
                }
                Ray ray = camera.ScreenToEgoRay(new float2(local.X / toUi.X, local.Y / toUi.Y));
                doubleQuat ego2Body = target.Body2Cce.Inverse();
                double3 bodyOrigin = (ray.Origin - camera.GetPositionEgo(target)).Transform(ego2Body);
                double3 bodyDirection = ray.Direction.Transform(ego2Body);
                if (!CuttingRingGeometry.TryPick(triangles, bodyOrigin, bodyDirection, out double3 hit))
                {
                    _ringSelectionStatus = "That click missed the selected vessel. Click a visible hull surface, or rotate the camera and try again.";
                    return;
                }
                _selectedRingPointTarget = target;
                _selectedRingPointBody = hit;
                _ringSelectionArmed = false;
                _ringSelectionStatus = "Selected hull point shown in amber.";
                QueueRingPoint(target, hit);
            }

            bool Project(double3 ego, out float2 screen)
            {
                screen = default;
                if (!OverlayFinite(ego) || double3.Dot(ego, camera.GetForwardEcl()) <= .001) return false;
                float2 pixels = camera.EgoToScreen(ego);
                screen = origin + new float2(pixels.X * toUi.X, pixels.Y * toUi.Y);
                return OverlayFinite(screen) && Math.Abs(screen.X - origin.X) < 1e6f && Math.Abs(screen.Y - origin.Y) < 1e6f;
            }

            void Line(double3 first, double3 second, ImColor8 color, float width)
            {
                if (Project(first, out float2 a) && Project(second, out float2 b)) draw.AddLine(a, b, color, width);
            }

            void Marker(float2 position, string label, ImColor8 color)
            {
                draw.AddCircleFilled(position, 5.5f, ImColor8.Black, 16);
                draw.AddCircle(position, 5, color, 16, 2);
                draw.AddLine(position - new float2(8, 0), position + new float2(8, 0), color);
                draw.AddLine(position - new float2(0, 8), position + new float2(0, 8), color);
                Label(position + new float2(10, -8), label, color);
            }

            void Label(float2 position, string text, ImColor8 color)
            {
                draw.AddText(position + new float2(1, 1), ImColor8.Black, text);
                draw.AddText(position, color, text);
            }
        }
        catch (Exception failure)
        {
            _ringSelectionStatus = "Ring preview could not be drawn: " + failure.Message;
        }
    }

    private static ImColor8 Cyan(float alpha = 1) => (ImColor8)new float4(.15f, .92f, 1, alpha);
    private static ImColor8 Amber(float alpha = 1) => (ImColor8)new float4(1, .73f, .2f, alpha);
    private static bool OverlayFinite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool OverlayFinite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
