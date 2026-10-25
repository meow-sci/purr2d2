using Brutal.Numerics;
using KSA;
using Purr2D2.Capes;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly Dictionary<KittenRenderable, CapeController> _capeControllers = new(ReferenceEqualityComparer.Instance);
    private readonly CapeUpdatePolicy.Candidate[] _capeCandidates = new CapeUpdatePolicy.Candidate[CapeUpdatePolicy.MaximumSimulatedCapes];
    private readonly bool[] _capeDue = new bool[CapeUpdatePolicy.MaximumSimulatedCapes];
    private readonly int[] _capeScheduled = new int[CapeUpdatePolicy.MaxStepCallsPerFrame];
    private readonly List<KittenEva> _capeOwners = new();
    private int _capeRoundRobinCursor;
    private int _capePendingCreationCount;
    private bool _capeRuntimeFaulted, _capeRuntimeFaultLogged;
    private bool _capeFaultCleanupPending, _capePendingCleanup;

    // The main settled update only enters the manager when an opted-in owner or a
    // deferred opt-out cleanup exists. Render hooks have a separate cheap gate.
    internal bool HasCapeWork => _capeOwners.Count != 0;
    internal bool HasCapeRenderers => _ready && _enabled && !_disposed && !Program.EditorFlag
        && _capeControllers.Count != 0;

    internal void DrawCape(KittenRenderable renderable, IViewport viewport, float4x4 bodyToCamera)
    {
        if (!HasCapeRenderers
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
            double distanceSquared = (controller.Owner.GetPositionEcl() - viewport.GetCamera().PositionEcl).LengthSquared();
            if (!CapeUpdatePolicy.CanRenderSquared(distanceSquared)) return;
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
        if (!HasCapeWork) return;
        if (_capeRuntimeFaulted)
        {
            if (_capeFaultCleanupPending)
                ReleaseFaultedCapes();
            else if (_capePendingCleanup)
                PruneCapeOwners();
            return;
        }

        try { UpdateCapesCore(playerDt); }
        catch (Exception error)
        {
            // A cape is an optional cosmetic; one failure must not suspend the saber runtime.
            _capeRuntimeFaulted = true;
            _capeFaultCleanupPending = true;
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

        // Prune before candidate collection so every stored index refers to the
        // same compact owner list for the full scheduling pass. The index is left
        // in place after RemoveAt, so the shifted entry is inspected next.
        PruneCapeOwners();
        int ownerCount = _capeOwners.Count;
        if (ownerCount == 0) return;

        int simulationLimit = Math.Clamp(_settings.CapeSimulationLimit,
            SaberSettings.MinimumCapeSimulationLimit, SaberSettings.MaximumCapeSimulationLimit);
        Span<CapeUpdatePolicy.Candidate> candidates = _capeCandidates.AsSpan(0, simulationLimit);
        Span<bool> due = _capeDue.AsSpan(0, simulationLimit);
        CapeUpdatePolicy.ClearCandidates(candidates);
        due.Clear();

        Camera? mainCamera = null;
        bool shouldCaptureCreationCameras = _capePendingCreationCount > 0 && CapeController.HookFailure is null;
        Span<double3> creationCameraPositions = shouldCaptureCreationCameras
            ? stackalloc double3[ViewportRegistry.MAX_VIEWPORTS]
            : Span<double3>.Empty;
        int creationCameraCount = 0;
        bool capturedCreationCameras = shouldCaptureCreationCameras;
        // New renderables are created only when a visible game view is within
        // render range. This bounded stack snapshot is lazy: mature registries
        // with no pending setup never read the viewport registry.
        if (shouldCaptureCreationCameras)
        {
            creationCameraCount = CaptureCreationCameraPositions(creationCameraPositions, out Camera? visibleMainCamera);
            if (simulationLimit > 0) mainCamera = visibleMainCamera;
        }

        // A zero budget skips simulation camera/speed/velocity work. If setup
        // cameras were already sampled, the main camera is reused for scheduling.
        if (simulationLimit > 0 && !capturedCreationCameras)
        {
            try { if (Program.MainViewport.Visible) mainCamera = Program.MainViewport.GetCamera(); }
            catch { mainCamera = null; }
        }

        ulong frame = Program.FrameNumber;
        for (int i = 0; i < ownerCount; i++)
        {
            KittenEva kitten = _capeOwners[i];
            if (!_kittenSabers.TryGetValue(kitten, out var saber) || !saber.CapeEnabled)
                continue; // Pruned above; a future UI request may have changed intent.

            double3 ownerPosition = default;
            bool hasOwnerPosition = false;
            CapeController? controller = saber.Cape;
            if (controller is not null) controller.IsSimulationCandidate = false;
            if (saber.CapeFailureLatched)
            {
                DisposeKittenCape(kitten, saber, clearFailure: false);
                continue;
            }

            if (CapeController.HookFailure is { } hookFailure)
            {
                if (simulationLimit == 0) controller?.SuspendSimulation();
                saber.CapeFailure = hookFailure;
                continue;
            }

            if (controller is null)
            {
                if (creationCameraCount == 0
                    || !TryGetCreationPosition(kitten, creationCameraPositions[..creationCameraCount], out double3 creationPosition))
                    continue;

                try
                {
                    controller = new CapeController(kitten, saber.Type);
                    saber.Cape = controller;
                    _capeControllers.Add(kitten.Renderable, controller);
                    SetCapeNeedsSetup(saber, false);
                    saber.CapeFailure = null;
                    saber.CapeFailureLogged = false;
                }
                catch (Exception error)
                {
                    SetCapeNeedsSetup(saber, false);
                    SetCapeFailure(saber, "Cape setup failed", error);
                    continue;
                }

                // Keep the one setup position for this owner's simulation-distance
                // check below; do not read the kitten twice in this settled pass.
                ownerPosition = creationPosition;
                hasOwnerPosition = true;
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

            if (simulationLimit == 0)
            {
                controller.SuspendSimulation();
                continue;
            }

            if (mainCamera is null || controller.Failure is not null) continue;
            if (!hasOwnerPosition) ownerPosition = kitten.GetPositionEcl();
            double distanceSquared = (ownerPosition - mainCamera.PositionEcl).LengthSquared();
            if (CapeUpdatePolicy.CanSimulateSquared(saber.CapeEnabled,
                    CapeUpdatePolicy.WasVisibleRecently(frame, controller.LastMainDrawFrame), distanceSquared))
                CapeUpdatePolicy.OfferCandidateSquared(candidates, i, distanceSquared);
        }

        if (simulationLimit == 0) return;

        int candidateCount = 0;
        while (candidateCount < candidates.Length && candidates[candidateCount].Index >= 0) candidateCount++;
        if (candidateCount == 0)
        {
            // No candidate can advance. We still inspect speed to preserve the
            // unsupported-warp reset and idle-release behavior of owned capes.
            AdvanceIdleCapes(playerDt);
            return;
        }

        Span<CapeUpdatePolicy.Candidate> activeCandidates = candidates[..candidateCount];
        Span<bool> activeDue = due[..candidateCount];

        double speed = Universe.GetSimulationSpeed();
        bool supportedSpeed = SaberLimits.SupportsSpeed(speed);
        bool advancing = supportedSpeed && speed > 0d && double.IsFinite(playerDt) && playerDt > 0d;
        bool unsupportedWarp = speed > 0d && !supportedSpeed;

        for (int i = 0; i < activeCandidates.Length; i++)
        {
            int ownerIndex = activeCandidates[i].Index;
            if ((uint)ownerIndex >= (uint)_capeOwners.Count)
            {
                activeDue[i] = false;
                continue;
            }

            KittenEva kitten = _capeOwners[ownerIndex];
            if (!_kittenSabers.TryGetValue(kitten, out var saber) || saber.Cape is not { } controller)
            {
                activeDue[i] = false;
                continue;
            }

            controller.IsSimulationCandidate = true;
            controller.MarkActive(supportedSpeed);
            if (unsupportedWarp)
                controller.SuspendForUnsupportedSpeed();
            else if (advancing && controller.Failure is null)
                controller.Accumulate(playerDt);
            activeDue[i] = advancing && controller.Failure is null && controller.IsStepDue;
        }

        int scheduledCount = CapeUpdatePolicy.SelectDue(activeCandidates, activeDue,
            _capeScheduled, ref _capeRoundRobinCursor);
        for (int i = 0; i < scheduledCount; i++)
        {
            int candidateIndex = _capeScheduled[i];
            int ownerIndex = activeCandidates[candidateIndex].Index;
            if ((uint)ownerIndex >= (uint)_capeOwners.Count) continue;
            KittenEva kitten = _capeOwners[ownerIndex];
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

        AdvanceIdleCapes(playerDt, advancing, unsupportedWarp);
    }

    private void AdvanceIdleCapes(double playerDt)
    {
        if (_capeControllers.Count == 0) return;
        double speed = Universe.GetSimulationSpeed();
        bool supportedSpeed = SaberLimits.SupportsSpeed(speed);
        bool advancing = supportedSpeed && speed > 0d && double.IsFinite(playerDt) && playerDt > 0d;
        bool unsupportedWarp = speed > 0d && !supportedSpeed;
        AdvanceIdleCapes(playerDt, advancing, unsupportedWarp);
    }

    private void AdvanceIdleCapes(double playerDt, bool advancing, bool unsupportedWarp)
    {
        for (int i = 0; i < _capeOwners.Count; i++)
        {
            KittenEva kitten = _capeOwners[i];
            if (!_kittenSabers.TryGetValue(kitten, out var saber) || saber.Cape is not { } controller
                || controller.IsSimulationCandidate)
                continue;
            controller.ClearAccumulatedTime();
            if (unsupportedWarp)
                controller.SuspendForUnsupportedSpeed();
            else if (advancing)
                controller.AdvanceIdle(playerDt);
        }
    }

    private void PruneCapeOwners()
    {
        for (int i = 0; i < _capeOwners.Count;)
        {
            KittenEva kitten = _capeOwners[i];
            if (_kittenSabers.TryGetValue(kitten, out var saber)
                && saber.CapeEnabled && !kitten.IsDisposed && _currentKittens.Contains(kitten))
            {
                i++;
                continue;
            }

            if (_kittenSabers.TryGetValue(kitten, out saber))
            {
                SetCapeNeedsSetup(saber, false);
                DisposeKittenCape(kitten, saber, clearFailure: true);
            }
            _capeOwners.RemoveAt(i);
        }
        _capePendingCleanup = false;
    }

    private void RemoveCapeOwner(KittenEva kitten)
    {
        int index = FindCapeOwner(kitten);
        if (index >= 0 && _kittenSabers.TryGetValue(kitten, out var saber))
            SetCapeNeedsSetup(saber, false);
        if (index >= 0) _capeOwners.RemoveAt(index);
    }

    private void SetCapeNeedsSetup(KittenSaber saber, bool needsSetup)
    {
        if (saber.CapeNeedsSetup == needsSetup) return;
        saber.CapeNeedsSetup = needsSetup;
        _capePendingCreationCount += needsSetup ? 1 : -1;
    }

    private static int CaptureCreationCameraPositions(Span<double3> positions, out Camera? mainCamera)
    {
        mainCamera = null;
        int count = 0;
        ReadOnlySpan<IGameViewport> gameViews = ViewportRegistry.GameViews;
        for (int i = 0; i < gameViews.Length && count < positions.Length; i++)
        {
            IGameViewport viewport = gameViews[i];
            if (!viewport.Visible || viewport.Type is not (ViewportType.Main or ViewportType.Secondary)) continue;
            try
            {
                Camera camera = viewport.GetCamera();
                positions[count++] = camera.PositionEcl;
                if (viewport.Type == ViewportType.Main) mainCamera = camera;
            }
            catch
            {
                // A temporarily unavailable game view must not block other views.
            }
        }
        return count;
    }

    private static bool TryGetCreationPosition(KittenEva kitten, ReadOnlySpan<double3> cameraPositions,
        out double3 kittenPosition)
    {
        kittenPosition = default;
        if (cameraPositions.Length == 0) return false;
        kittenPosition = kitten.GetPositionEcl();
        for (int i = 0; i < cameraPositions.Length; i++)
            if (CapeUpdatePolicy.CanRenderSquared((kittenPosition - cameraPositions[i]).LengthSquared())) return true;
        return false;
    }

    private int FindCapeOwner(KittenEva kitten)
    {
        for (int i = 0; i < _capeOwners.Count; i++)
            if (ReferenceEquals(_capeOwners[i], kitten)) return i;
        return -1;
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
        if (saber.CapeEnabled == enabled)
        {
            if (enabled && FindCapeOwner(kitten) < 0) _capeOwners.Add(kitten);
            if (enabled && saber.Cape is null && !saber.CapeFailureLatched)
                SetCapeNeedsSetup(saber, true);
            return;
        }

        saber.CapeEnabled = enabled;
        if (enabled)
        {
            if (FindCapeOwner(kitten) < 0) _capeOwners.Add(kitten);
            _capeRuntimeFaulted = _capeRuntimeFaultLogged = false;
            _capeFaultCleanupPending = false;
        }
        else
        {
            // Keep the owner through this UI handoff. The next settled manager
            // update disposes its native resources; a quick re-enable reuses the
            // same list slot and cannot create a duplicate entry.
            _capePendingCleanup = true;
            SetCapeNeedsSetup(saber, false);
        }

        // Session intent and status change immediately; resource ownership stays
        // on the settled update path.
        saber.CapeFailure = null;
        saber.CapeFailureLogged = false;
        saber.CapeFailureLatched = false;
        if (enabled && saber.Cape is null) SetCapeNeedsSetup(saber, true);
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
        for (int i = 0; i < _capeOwners.Count;)
        {
            KittenEva kitten = _capeOwners[i];
            if (!_kittenSabers.TryGetValue(kitten, out var saber))
            {
                _capeOwners.RemoveAt(i);
                continue;
            }

            if (!saber.CapeEnabled || kitten.IsDisposed || !_currentKittens.Contains(kitten))
            {
                SetCapeNeedsSetup(saber, false);
                DisposeKittenCape(kitten, saber, clearFailure: true);
                _capeOwners.RemoveAt(i);
                continue;
            }

            SetCapeNeedsSetup(saber, false);
            if (error is not null)
            {
                saber.CapeFailure = "Cape runtime paused: " + error.Message;
                saber.CapeFailureLatched = true;
                saber.CapeFailureLogged = true;
            }
            DisposeKittenCape(kitten, saber, clearFailure: false);
            i++;
        }
        _capeFaultCleanupPending = false;
        _capePendingCleanup = false;
    }
}
