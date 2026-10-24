using Brutal.GlfwApi;
using KSA;
using Purr2D2.Core;
using RenderCore.Input;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    // Called from the KittenEva.OnKey prefix after KSA has passed its editor,
    // popup, console, ImGui, camera and active-controller input gates.
    internal bool FilterSpaceSomersaultKey(KittenEva owner, GlfwKeyEvent keyEvent)
    {
        if (!_settings.SpaceSomersaultEnabled) CancelPendingSpaceSomersault();
        var (window, action, key, _) = keyEvent;
        if (action != GlfwKeyAction.Press || key != GlfwKey.Space || window is null
            || !IsSpaceSomersaultScope(owner, window)) return false;

        // A live, ordinary EVA press belongs to the custom action even when its
        // movement is presently unavailable. This prevents a stock jump from
        // bypassing cooldown, pose, pause, swing or slice eligibility.
        if (_somersaultRequest is { } pending)
        {
            if (ReferenceEquals(pending.Owner, owner)) return true;
            // The prior request belongs to an actor which is no longer controlled.
            _somersaultRequest = null;
        }

        var (directionAtPress, pressTime) = SnapshotSpaceSomersault(window);

        // The stock UI/input route may switch control before the settled runtime
        // binds the new pose. Retain only the current actor's fresh request until
        // that immediate rebind; ordinary requests are checked now.
        if (!ReferenceEquals(_owner, owner))
        {
            if (SomersaultMotion.NormalSpeed(Universe.GetSimulationSpeed()))
                _somersaultRequest = new(owner, directionAtPress, pressTime, FromSpace: true);
            return true;
        }

        if (!SomersaultMotion.NormalSpeed(Universe.GetSimulationSpeed())) return true;
        _somersaultMotion.Update(_owner);
        if (CanStartSomersault(out _, pressTime) && _owner is { } controlled)
            _somersaultRequest = new(controlled, directionAtPress, pressTime, FromSpace: true);
        return true;
    }

    private (SomersaultDirection Direction, double PressTime) SnapshotSpaceSomersault(GlfwWindow window)
    {
        double pressed = _somersaultMotion.InputTime;
        bool up = window.GetKey(GlfwKey.Up) == GlfwKeyAction.Press;
        bool down = window.GetKey(GlfwKey.Down) == GlfwKeyAction.Press;
        bool left = window.GetKey(GlfwKey.Left) == GlfwKeyAction.Press;
        bool right = window.GetKey(GlfwKey.Right) == GlfwKeyAction.Press;
        return (SomersaultKeyboardDirection.Resolve(up, down, left, right), pressed);
    }

    private bool IsSpaceSomersaultScope(KittenEva owner, GlfwWindow? window = null)
    {
        object? system = Universe.CurrentSystem;
        return _enabled && _ready && !_disposed && _fault == null && _settings.SpaceSomersaultEnabled
            && !Program.EditorFlag && (window == null || window == Program.GetWindow())
            && !owner.IsDisposed && !owner.IsEditedVehicle && owner.IsControllable
            && ReferenceEquals(owner, Program.ControlledVehicle) && system != null
            && ReferenceEquals(owner.System, system)
            && owner.FlightComputer.RCSMode == FlightComputerRCSMode.Disabled
            && owner.LocomotionState.Mode is LocomotionMode.Grounded or LocomotionMode.Airborne;
    }

    // Focus loss cancels only an unhanded keyboard intent. Overlay button
    // requests and a somersault already accepted by the motion controller remain
    // governed by their existing lifecycle rules.
    internal void CancelPendingSpaceSomersault()
    {
        if (_somersaultRequest is { FromSpace: true }) _somersaultRequest = null;
    }
}
