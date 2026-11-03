using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly SaberDropSpinDrag _dropSpinDrag = new();
    private KittenEva? _dropSpinDragOwner;
    private KittenSaber? _dropSpinDragSaber;
    private bool _dropSpinEscapeHeld;
    private bool _dropDragReturning;
    private string _dropSpinValueText = "0.00x";
    private float _dropSpinDisplayed;

    private bool SaberDropButtonAvailable(KittenEva owner, KittenSaber saber) =>
        !owner.IsDisposed && _fault is null && saber.Blade.Equipped
        && saber.TogglePhase is not (SaberTogglePhase.Retracting or SaberTogglePhase.Unequipping)
        && saber.Drop.Phase != SaberDropSequence.Stage.Returning
        && (!saber.Drop.Held || saber.Pose.HasFreshBodyFrame && owner.HasPhysicsBubble);

    private void BeginSaberDropSpinDrag(KittenEva owner, float2 pointer, OverlayLayout layout)
    {
        if (!BFL9KUiVisible || Program.IsModalOpen() || Program.HasMouseInputLatch || ImGui.GetIO().AppFocusLost
            || !ReferenceEquals(owner, AppearanceKitten) || !_currentKittens.Contains(owner)
            || !_kittenSabers.TryGetValue(owner, out var saber) || !SaberDropButtonAvailable(owner, saber)) return;
        // Canceling a reach remains immediate. A dropped hilt selects its return
        // duration on release, while a held hilt selects its release spin.
        if (saber.Drop.Phase == SaberDropSequence.Stage.Reaching)
        { CancelSaberDropSpinDrag(); QueueSaberDrop(owner); return; }
        if (!_dropSpinDrag.Begin(pointer, layout.DropCenter, layout.IgniteRadius, layout.DropPillTravel)) return;
        _dropSpinDragOwner = owner;
        _dropSpinDragSaber = saber;
        _dropDragReturning = saber.Drop.Phase == SaberDropSequence.Stage.Dropped;
        _dropSpinDisplayed = 0;
        _dropSpinValueText = _dropDragReturning ? "1000 ms" : "0.00x";
    }

    private bool SaberDropSpinDragLayoutStillValid(OverlayLayout layout) =>
        _dropSpinDrag.Held && _overlayLayoutAvailable && BFL9KUiVisible && !Program.IsModalOpen() && !Program.HasMouseInputLatch
        && !ImGui.GetIO().AppFocusLost && _dropSpinDragOwner is { IsDisposed: false } owner
        && ReferenceEquals(owner, AppearanceKitten) && _currentKittens.Contains(owner)
        && _kittenSabers.TryGetValue(owner, out var saber) && ReferenceEquals(saber, _dropSpinDragSaber)
        && (_dropDragReturning ? saber.Drop.Phase == SaberDropSequence.Stage.Dropped : saber.Drop.Held)
        && SaberDropButtonAvailable(owner, saber) && _dropSpinDrag.Matches(layout);

    private static bool DropSpinPointerInMainWindow(float2 pointer)
    {
        if (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y)) return false;
        var viewport = ImGui.GetMainViewport();
        var min = viewport.Pos;
        var max = min + viewport.Size;
        return pointer.X >= min.X && pointer.X <= max.X && pointer.Y >= min.Y && pointer.Y <= max.Y;
    }

    private void UpdateSaberDropSpinDrag(float2 pointer)
    {
        if (!_dropSpinDrag.Held) return;
        if (!_overlayLayoutAvailable || !SaberDropSpinDragLayoutStillValid(_overlayLayout)
            || !DropSpinPointerInMainWindow(pointer) || !_dropSpinDrag.Move(pointer))
        { CancelSaberDropSpinDrag(); return; }
        float displayed = _dropDragReturning
            ? (float)Math.Round(_dropSpinDrag.ReturnDurationSeconds * 1000)
            : MathF.Round(_dropSpinDrag.SpinMultiplier, 2);
        if (displayed == _dropSpinDisplayed) return;
        _dropSpinDisplayed = displayed;
        _dropSpinValueText = _dropDragReturning
            ? displayed.ToString("0", CultureInfo.InvariantCulture) + " ms"
            : displayed.ToString("0.00", CultureInfo.InvariantCulture) + "x";
    }

    private void CompleteSaberDropSpinDrag(float2 pointer)
    {
        if (!_dropSpinDrag.Held) return;
        var layout = CurrentSaberOverlayLayout();
        if (!SaberDropSpinDragLayoutStillValid(layout) || !DropSpinPointerInMainWindow(pointer))
        { CancelSaberDropSpinDrag(); return; }
        var owner = _dropSpinDragOwner!;
        bool returning = _dropDragReturning;
        bool finished = _dropSpinDrag.Finish(pointer, out float spin, out double duration);
        CancelSaberDropSpinDrag();
        if (finished) QueueSaberDrop(owner, returning ? 0 : spin,
            returning ? duration : SaberDropSequence.DefaultReturnDuration);
    }

    private void CancelSaberDropSpinDrag()
    {
        _dropSpinDrag.Cancel();
        _dropSpinDragOwner = null;
        _dropSpinDragSaber = null;
        _dropDragReturning = false;
        _dropSpinDisplayed = 0;
        _dropSpinValueText = "0.00x";
    }

    internal bool FilterSaberDropSpinKey(GlfwWindow window, GlfwKey key, GlfwKeyAction action)
    {
        if (window != Program.GetWindow() || key != GlfwKey.Escape) return false;
        if (_dropSpinEscapeHeld)
        {
            if (action == GlfwKeyAction.Release) _dropSpinEscapeHeld = false;
            return true;
        }
        if (!_dropSpinDrag.Held) return false;
        if (Program.IsModalOpen()) { CancelSaberDropSpinDrag(); return false; }
        if (action != GlfwKeyAction.Release)
        {
            _dropSpinEscapeHeld = true;
            CancelSaberDropSpinDrag();
        }
        return true;
    }

    private void DrawSaberDropSpinPill(ImDrawListPtr draw, OverlayLayout layout)
    {
        draw.AddRectFilled(layout.DropPillMin, layout.DropPillMax, new ImColor8(0, 0, 0, 150), layout.IgniteRadius);
        draw.AddRect(layout.DropPillMin, layout.DropPillMax, new ImColor8(255, 255, 255, 175), layout.IgniteRadius,
            ImDrawFlags.None, Math.Max(.5f, layout.Scale));
        var bottom = layout.DropCenter + new float2(0, layout.DropPillTravel);
        draw.AddLine(layout.DropCenter, bottom, new ImColor8(255, 255, 255, 140), Math.Max(1, 2 * layout.Scale));
        float radius = Math.Max(3 * layout.Scale, layout.IgniteRadius * .62f);
        draw.AddCircleFilled(_dropSpinDrag.Thumb, radius, MovementButtonFill, 24);
        draw.AddCircle(_dropSpinDrag.Thumb, radius, new ImColor8(255, 255, 255, 235), 24, Math.Max(.5f, layout.Scale));
    }

    private void DrawSaberDropSpinValueLabel(ImDrawListPtr draw, OverlayLayout layout)
    {
        var position = layout.DropSpinValueLabelPosition(_dropSpinDrag.Thumb.Y);
        var font = ImGui.GetFont();
        float fontSize = ImGui.GetFontSize();
        draw.AddText(font, fontSize, position + new float2(layout.AutoSwingValueShadowOffset),
            new ImColor8(0, 0, 0, 255), _dropSpinValueText, 0);
        draw.AddText(font, fontSize, position, new ImColor8(255, 255, 255, 255), _dropSpinValueText, 0);
    }
}
