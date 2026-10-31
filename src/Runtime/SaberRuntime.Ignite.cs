using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using OverlayLayout = Purr2D2.Core.SaberOverlayLayout;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly SaberIgniteDrag _igniteDrag = new();
    private KittenEva? _igniteDragOwner;

    private float SaberDeploymentFor(KittenEva owner, KittenSaber saber)
    {
        // Re-grabbing before the next simulation handoff must keep the latest target.
        for (int i = _kittenSaberRequests.Count - 1; i >= 0; i--)
        {
            var request = _kittenSaberRequests[i];
            if (ReferenceEquals(request.Kitten, owner))
                return request.ManualDeployment ?? saber.Blade.Deployment;
        }
        return saber.DeferredManualDeployment ?? (saber.Blade.ManualDeployment
            ? saber.Blade.ManualTargetDeployment : saber.Blade.Deployment);
    }

    private void BeginSaberIgniteDrag(KittenEva owner, float2 pointer, OverlayLayout layout)
    {
        if (!BFL9KUiVisible || Program.IsModalOpen() || ImGui.GetIO().AppFocusLost || _fault != null
            || !ReferenceEquals(owner, _owner) || !ReferenceEquals(owner, Program.ControlledVehicle)
            || !_currentKittens.Contains(owner) || !_kittenSabers.TryGetValue(owner, out var saber)) return;
        if (_igniteDrag.Begin(pointer, SaberDeploymentFor(owner, saber), SaberEnabledFor(owner),
            layout.IgniteCenter, layout.IgniteRadius, layout.IgnitePillTravel, 4 * layout.Scale))
            _igniteDragOwner = owner;
    }

    private bool SaberIgniteDragLayoutStillValid(OverlayLayout layout) =>
        layout.Valid && _igniteDrag.Held && BFL9KUiVisible && !Program.IsModalOpen() && !Program.HasMouseInputLatch
        && !ImGui.GetIO().AppFocusLost && _fault == null && _igniteDragOwner is { IsDisposed: false } owner
        && ReferenceEquals(owner, _owner) && ReferenceEquals(owner, Program.ControlledVehicle)
        && _currentKittens.Contains(owner) && _kittenSabers.ContainsKey(owner)
        && _igniteDrag.MatchesLayout(layout.IgniteCenter, layout.IgniteRadius, layout.IgnitePillTravel);

    private void UpdateSaberIgniteDrag(float2 pointer)
    {
        if (!_igniteDrag.Held) return;
        if (!_overlayLayoutAvailable || !SaberIgniteDragLayoutStillValid(_overlayLayout))
        {
            CancelSaberIgniteDrag();
            return;
        }
        float value = _igniteDrag.Move(pointer);
        if (_igniteDrag.Dragged && _igniteDragOwner is { } owner)
            QueueKittenBladeDeployment(owner, value);
    }

    private void CompleteSaberIgniteDrag(float2 pointer)
    {
        if (!_igniteDrag.Held) return;
        var layout = CurrentSaberOverlayLayout();
        if (!SaberIgniteDragLayoutStillValid(layout) || !_igniteDrag.Release(pointer, out float deployment))
        {
            CancelSaberIgniteDrag();
            return;
        }
        var owner = _igniteDragOwner!;
        bool dragged = _igniteDrag.Dragged;
        CancelSaberIgniteDrag();
        if (dragged) QueueKittenBladeDeployment(owner, deployment, rememberIntent: true);
        else QueueKittenSaber(owner, deployment > 0);
    }

    private void CancelSaberIgniteDrag()
    {
        _igniteDrag.Reset();
        _igniteDragOwner = null;
    }

    private void DrawSaberIgnitePill(ImDrawListPtr draw, OverlayLayout layout)
    {
        var min = layout.IgnitePillMin;
        var max = layout.IgnitePillMax;
        draw.AddRectFilled(min, max, new ImColor8(0, 0, 0, 150), layout.IgniteRadius);
        draw.AddRect(min, max, new ImColor8(255, 255, 255, 175), layout.IgniteRadius,
            ImDrawFlags.None, Math.Max(.5f, layout.Scale));
        var top = layout.IgniteCenter;
        var bottom = top + new float2(0, layout.IgnitePillTravel);
        draw.AddLine(top, bottom, new ImColor8(255, 255, 255, 140), Math.Max(1, 2 * layout.Scale));
        var marker = top + new float2(0, layout.IgnitePillTravel * _igniteDrag.Value);
        float radius = Math.Max(3 * layout.Scale, layout.IgniteRadius * .62f);
        draw.AddCircleFilled(marker, radius, MovementButtonFill, 24);
        draw.AddCircle(marker, radius, new ImColor8(255, 255, 255, 235), 24, Math.Max(.5f, layout.Scale));
    }
}
