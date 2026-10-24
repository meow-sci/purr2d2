using Brutal.Numerics;
using KSA;
using Purr2D2.Capes;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly Dictionary<KittenRenderable, CapeController> _capeControllers = new(ReferenceEqualityComparer.Instance);
    private readonly CapeUpdatePolicy.Candidate[] _capeCandidates = new CapeUpdatePolicy.Candidate[CapeUpdatePolicy.MaxSimulatedCapes];
    private readonly bool[] _capeDue = new bool[CapeUpdatePolicy.MaxSimulatedCapes];
    private readonly int[] _capeScheduled = new int[CapeUpdatePolicy.MaxStepCallsPerFrame];
    private int _capeRoundRobinCursor;
    private bool _capeRuntimeFaulted, _capeRuntimeFaultLogged;

    internal void DrawCape(KittenRenderable renderable, IViewport viewport, float4x4 bodyToCamera)
    {
        if (!_ready || !_enabled || _disposed || Program.EditorFlag
            || viewport.Type is not (ViewportType.Main or ViewportType.Secondary)
            || !viewport.Visible
            || !_capeControllers.TryGetValue(renderable, out var controller)
            || controller.Owner.IsDisposed
            || !_currentKittens.Contains(controller.Owner)
            || !_kittenSabers.TryGetValue(controller.Owner, out var saber)
            || !saber.CapeEnabled || saber.CapeFailureLatched)
            return;

        try
        {
            double distance = (controller.Owner.GetPositionEcl() - viewport.GetCamera().PositionEcl).Length();
            if (!CapeUpdatePolicy.CanRender((float)distance)) return;
            if (!controller.Draw(renderable, viewport, bodyToCamera, Program.FrameNumber)) return;
            CaptureCapeFailure(saber, controller);
        }
        catch (Exception error)
        {
            SetCapeFailure(saber, "Cape rendering failed", error);
        }
    }

    private void UpdateCapes(double playerDt)
    {
        if (_capeRuntimeFaulted)
        {
            ReleaseFaultedCapes();
            return;
        }
        try { UpdateCapesCore(playerDt); }
        catch (Exception error)
        {
            // A cape is an optional cosmetic; one failure must not suspend the saber runtime.
            _capeRuntimeFaulted = true;
            if (!_capeRuntimeFaultLogged)
            {
                _capeRuntimeFaultLogged = true;
                Report("Cape runtime update failed", error);
            }
            ReleaseFaultedCapes(error);
        }
    }

    private void UpdateCapesCore(double playerDt)
    {
        if (!_ready || _disposed || !_enabled || Program.EditorFlag) return;

        bool anyEnabledCape = _capeControllers.Count != 0;
        if (!anyEnabledCape)
            foreach (KittenSaber saber in _kittenSabers.Values)
                if (saber.CapeEnabled) { anyEnabledCape = true; break; }
        if (!anyEnabledCape) return;

        CapeUpdatePolicy.ClearCandidates(_capeCandidates);
        foreach (CapeController controller in _capeControllers.Values)
            controller.IsSimulationCandidate = false;

        Camera? mainCamera = null;
        try { if (Program.MainViewport.Visible) mainCamera = Program.MainViewport.GetCamera(); }
        catch { mainCamera = null; }

        ulong frame = Program.FrameNumber;
        for (int i = 0; i < _overlayKittens.Count; i++)
        {
            KittenEva kitten = _overlayKittens[i];
            if (kitten.IsDisposed || !_kittenSabers.TryGetValue(kitten, out var saber)) continue;
            if (!saber.CapeEnabled)
            {
                if (saber.Cape is not null || saber.CapeFailure is not null)
                    DisposeKittenCape(kitten, saber, clearFailure: true);
                continue;
            }
            if (saber.CapeFailureLatched)
            {
                DisposeKittenCape(kitten, saber, clearFailure: false);
                continue;
            }

            if (CapeController.HookFailure is { } hookFailure)
            {
                saber.CapeFailure = hookFailure;
                continue;
            }

            CapeController? controller = saber.Cape;
            if (controller is null)
            {
                try
                {
                    controller = new CapeController(kitten, saber.Type);
                    saber.Cape = controller;
                    _capeControllers.Add(kitten.Renderable, controller);
                    saber.CapeFailure = null;
                    saber.CapeFailureLogged = false;
                }
                catch (Exception error)
                {
                    SetCapeFailure(saber, "Cape setup failed", error);
                    continue;
                }
            }

            try
            {
                controller.SetType(saber.Type);
            }
            catch (Exception error)
            {
                SetCapeFailure(saber, "Cape style could not change", error);
                DisposeKittenCape(kitten, saber, clearFailure: false);
                continue;
            }

            if (mainCamera is null || controller.Failure is not null) continue;
            double distance = (kitten.GetPositionEcl() - mainCamera.PositionEcl).Length();
            if (CapeUpdatePolicy.CanSimulate(saber.CapeEnabled,
                    CapeUpdatePolicy.WasVisibleRecently(frame, controller.LastMainDrawFrame), (float)distance))
                CapeUpdatePolicy.OfferCandidate(_capeCandidates, i, (float)distance);
        }

        double speed = Universe.GetSimulationSpeed();
        bool supportedSpeed = SaberLimits.SupportsSpeed(speed);
        bool advancing = supportedSpeed && speed > 0d && double.IsFinite(playerDt) && playerDt > 0d;
        bool unsupportedWarp = speed > 0d && !supportedSpeed;

        for (int i = 0; i < _capeCandidates.Length; i++)
        {
            int kittenIndex = _capeCandidates[i].Index;
            if (kittenIndex < 0 || (uint)kittenIndex >= (uint)_overlayKittens.Count)
            {
                _capeDue[i] = false;
                continue;
            }

            KittenEva kitten = _overlayKittens[kittenIndex];
            if (!_kittenSabers.TryGetValue(kitten, out var saber) || saber.Cape is not { } controller)
            {
                _capeDue[i] = false;
                continue;
            }

            controller.IsSimulationCandidate = true;
            controller.MarkActive(supportedSpeed);
            if (unsupportedWarp)
                controller.SuspendForUnsupportedSpeed();
            else if (advancing && controller.Failure is null)
                controller.Accumulate(playerDt);
            _capeDue[i] = advancing && controller.Failure is null && controller.IsStepDue;
        }

        int scheduledCount = CapeUpdatePolicy.SelectDue(_capeDue, _capeScheduled, ref _capeRoundRobinCursor);
        for (int i = 0; i < scheduledCount; i++)
        {
            int candidateIndex = _capeScheduled[i];
            int kittenIndex = _capeCandidates[candidateIndex].Index;
            if ((uint)kittenIndex >= (uint)_overlayKittens.Count) continue;
            KittenEva kitten = _overlayKittens[kittenIndex];
            if (!_kittenSabers.TryGetValue(kitten, out var saber) || saber.Cape is not { } controller) continue;
            float stepDt = controller.ConsumeStepTime();
            if (stepDt <= 0f) continue;
            try
            {
                double3 velocityCce = kitten.GetSurfaceVelocityCci().Transform(kitten.Parent.GetCci2Cce());
                double3 velocityBody = velocityCce.Transform(doubleQuat.Inverse(kitten.Body2Cce));
                controller.Step(stepDt, float3.Pack(velocityBody));
                CaptureCapeFailure(saber, controller);
            }
            catch (Exception error)
            {
                SetCapeFailure(saber, "Cape simulation paused", error);
            }
        }

        foreach (CapeController controller in _capeControllers.Values)
        {
            if (controller.IsSimulationCandidate) continue;
            controller.ClearAccumulatedTime();
            if (unsupportedWarp)
                controller.SuspendForUnsupportedSpeed();
            else if (advancing)
                controller.AdvanceIdle(playerDt);
        }
    }

    private bool CapeEnabledFor(KittenEva kitten) =>
        _kittenSabers.TryGetValue(kitten, out var saber) && saber.CapeEnabled;

    private string? CapeStatusFor(KittenEva kitten)
    {
        if (!_kittenSabers.TryGetValue(kitten, out var saber) || !saber.CapeEnabled) return null;
        return saber.CapeFailure ?? CapeController.HookFailure;
    }

    private void SetKittenCapeEnabled(KittenEva kitten, bool enabled)
    {
        if (kitten.IsDisposed || !_currentKittens.Contains(kitten)
            || !_kittenSabers.TryGetValue(kitten, out var saber)) return;
        if (saber.CapeEnabled == enabled) return;
        saber.CapeEnabled = enabled;
        if (enabled) _capeRuntimeFaulted = _capeRuntimeFaultLogged = false;
        // This is only session intent/status. Native cape resources are disposed
        // by UpdateCapes at the next settled handoff.
        saber.CapeFailure = null;
        saber.CapeFailureLogged = false;
        saber.CapeFailureLatched = false;
    }

    private void DisposeKittenCape(KittenEva kitten, KittenSaber saber, bool clearFailure)
    {
        if (saber.Cape is { } controller)
        {
            _capeControllers.Remove(kitten.Renderable);
            Cleanup(controller.Dispose, "Kitten cape");
            saber.Cape = null;
        }
        if (clearFailure)
        {
            saber.CapeFailure = null;
            saber.CapeFailureLogged = false;
            saber.CapeFailureLatched = false;
        }
    }

    private void CaptureCapeFailure(KittenSaber saber, CapeController controller)
    {
        if (controller.Failure is not { } failure) return;
        saber.CapeFailureLatched = true;
        if (saber.CapeFailure == failure) return;
        saber.CapeFailure = failure;
        if (saber.CapeFailureLogged) return;
        saber.CapeFailureLogged = true;
        Console.WriteLine("[purr2d2] Cape simulation stopped for one kitten: " + failure);
    }

    private static void SetCapeFailure(KittenSaber saber, string context, Exception error)
    {
        saber.CapeFailure = context + ": " + error.Message;
        saber.CapeFailureLatched = true;
        if (saber.CapeFailureLogged) return;
        saber.CapeFailureLogged = true;
        Report(context, error);
    }

    private void ReleaseFaultedCapes(Exception? error = null)
    {
        foreach (var pair in _kittenSabers)
        {
            KittenSaber saber = pair.Value;
            if (saber.CapeEnabled && error is not null)
            {
                saber.CapeFailure = "Cape runtime paused: " + error.Message;
                saber.CapeFailureLatched = true;
            }
            DisposeKittenCape(pair.Key, saber, clearFailure: false);
        }
    }
}
