using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using OverlayHit = Purr2D2.Core.SaberOverlayHit;
using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    internal bool FilterOverlayScroll(GlfwWindow window, double2 offset)
    {
        // The GLFW key state is authoritative at callback time; pass one snapshot
        // through the complete game-side routing decision.
        bool shift = window == Program.GetWindow() && ForceShiftHeld(window);
        if (_autoSwingDrag.Held || _dropSpinDrag.Held) return true;
        if (_saberDeploymentDrag.Held) return true;
        if (_forceCaptureRadiusDrag.Held) return true;
        if (window == Program.GetWindow() && _forceThrowDrag.Held)
        {
            if (!ForceUiVisible || Program.IsModalOpen() || ImGui.GetIO().AppFocusLost || !ForceCanUse || !ForceHolding
                || !ReferenceEquals(_forceThrowOwner, _owner) || !ReferenceEquals(_force.Owner, _owner))
                CancelForceThrowDrag();
            return true;
        }
        if (Program.HasMouseInputLatch) return false;

        var pointer = OverlayPointer(window);
        if (window == Program.GetWindow() && !_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(pointer) || SettingsContainsPointer(pointer) || _overlayUiButtons != 0)) return true;
        if (!_overlayPointerCapture.AnyHeld && ImGui.GetIO().WantCaptureMouse) return false;

        if (OwnsForceScroll(window, pointer, shift))
        {
            if (TryGetForceScrollTicks(offset, shift, out double ticks))
                ScrollForcePointer(ticks, shift);
            return true;
        }

        if (!OverlayOwnsScroll(window, shift, out var hit)) return false;
        if (hit == OverlayHit.Kittens && double.IsFinite(offset.Y))
            _overlayListScroll = Math.Clamp(_overlayListScroll - (float)offset.Y * _overlayLayout.RowHeight * 3,
                0, Math.Max(0, OverlayKittens.Count * _overlayLayout.RowHeight - _overlayLayout.ListBodyHeight));
        return true;
    }

    // The backend asks only whether it must suppress native ImGui scroll. This
    // path deliberately shares the routing predicate but never applies a delta.
    internal bool SuppressOverlayImGuiScroll(GlfwWindow window)
    {
        bool shift = window == Program.GetWindow() && ForceShiftHeld(window);
        return OverlayOwnsScroll(window, shift, out _);
    }

    private bool OverlayOwnsScroll(GlfwWindow window, bool shift, out OverlayHit hit)
    {
        hit = OverlayHit.None;
        if (_autoSwingDrag.Held || _dropSpinDrag.Held || _saberDeploymentDrag.Held || _forceCaptureRadiusDrag.Held) return true;
        if (window == Program.GetWindow() && _forceThrowDrag.Held) return true;
        if (Program.HasMouseInputLatch) return false;
        var pointer = OverlayPointer(window);
        if (!_overlayPointerCapture.AnyHeld
            && (KittenColorPickerContainsPointer(pointer) || SettingsContainsPointer(pointer) || _overlayUiButtons != 0)) return false;
        if (!_overlayPointerCapture.AnyHeld && ImGui.GetIO().WantCaptureMouse) return false;
        if (window != Program.GetWindow() || !OverlayVisible || !_overlayLayoutAvailable || Program.IsModalOpen()) return false;
        hit = _overlayLayout.Hit(pointer);
        bool forceScroll = OwnsForceScroll(window, pointer, shift);
        return _overlayPointerCapture.OwnsScroll(ForcePicking || hit != OverlayHit.None || forceScroll
            || ForceOverlayHitAt(pointer) != ForceOverlayHit.None);
    }

    /// <summary>Shared, side-effect-free ownership test for depth and spread wheel routes.</summary>
    private bool OwnsForceScroll(GlfwWindow window, float2 pointer, bool shift)
    {
        var io = ImGui.GetIO();
        var owner = _owner;
        if (owner is null || owner.IsDisposed) return false;
        if (window != Program.GetWindow() || !OverlayVisible || !ForceUiVisible || Program.IsModalOpen()
            || Program.HasMouseInputLatch || io.AppFocusLost || io.WantCaptureMouse || io.WantTextInput
            || !ForceCanUse || !ForceHolding
            || !ReferenceEquals(owner, Program.ControlledVehicle) || !ReferenceEquals(_force.Owner, owner)
            || ForcePicking || !_overlayLayoutAvailable || !_forceLayoutAvailable
            || !CursorTarget.IsHitTestViewport(Program.MainViewport)
            || KittenColorPickerContainsPointer(pointer) || SettingsContainsPointer(pointer) || _overlayUiButtons != 0
            || _overlayLayout.Hit(pointer) != OverlayHit.None
            || _autoSwingDrag.Held || _dropSpinDrag.Held || _saberDeploymentDrag.Held || _forceCaptureRadiusDrag.Held
            || _forceThrowDrag.Held || _forceSettingDrag.Held || _overlayDrag.Held)
            return false;

        if (_forceDrag.Held)
            return _overlayPointerCapture.AnyHeld && ReferenceEquals(_forceMoveOwner, owner);
        if (_overlayPointerCapture.AnyHeld) return false;

        var forceHit = ForceOverlayHitAt(pointer);
        return forceHit == ForceOverlayHit.Move
            || (shift && forceHit == ForceOverlayHit.None && _forceLayout.Hit(pointer) == ForceOverlayHit.None);
    }

    private static bool TryGetForceScrollTicks(double2 offset, bool shift, out double ticks)
    {
        ticks = 0;
        if (!double.IsFinite(offset.Y)) return false;
        if (offset.Y != 0)
        {
            ticks = offset.Y;
            return true;
        }
        if (!shift || !double.IsFinite(offset.X) || offset.X == 0) return false;
        ticks = offset.X;
        return true;
    }
}
