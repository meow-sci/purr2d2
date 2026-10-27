using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private SliceResourceUsage _sliceResourceUsage;
    private int _sliceResourceVehicleCount, _sliceResourceDebrisCount;
    private string _sliceResourceStatus = "";
    private double _sliceResourceNextRefresh;
    private CelestialSystem? _sliceResourceRequestSystem;
    private bool _sliceCacheClearPending, _sliceDebrisClearPending;

    private bool CanMaintainSliceResources => _ready && !_disposed && _enabled && !Program.EditorFlag
        && Universe.CurrentSystem != null && !SliceWorkActive && !_manualGesture.CanDetect && !_slicer.Busy
        && _commands.Count == 0 && !ForceHolding && _sliceResourceUsage.ActiveUploadJobs == 0;

    private void ApplySliceResourceSettings()
    {
        _settings.Validate();
        SliceGeometryBudget.Configure(_settings.SliceSolidLimit, _settings.SliceTriangleLimit);
        RuntimeFragmentAssets.ConfigureStorageLimits(_settings.SliceAssetDiskMiB, _settings.SliceAssetEntryLimit);
        RuntimeFragmentAssets.ConfigureGrowth(_settings.SliceAutoGrow, _settings.SliceVertexMaximumMiB, _settings.SliceIndexMaximumMiB);
        NativeSlicePhysics.ConfigureVehicleLimit(_settings.SlicePhysicsVehicleLimit);
        _sliceResourceNextRefresh = 0;
    }

    private void QueueSliceCacheClear()
    {
        if (!CanMaintainSliceResources) return;
        _sliceResourceRequestSystem = Universe.CurrentSystem;
        _sliceCacheClearPending = true;
    }

    private void QueueSliceDebrisClear()
    {
        if (!CanMaintainSliceResources) return;
        _sliceResourceRequestSystem = Universe.CurrentSystem;
        _sliceDebrisClearPending = true;
    }

    // Called only in the settled handoff. GUI code consumes the resulting snapshot
    // and queues requests; it never destroys vehicles or inspects live physics.
    private void UpdateSliceResources()
    {
        bool clearCache = _sliceCacheClearPending, clearDebris = _sliceDebrisClearPending;
        var requestSystem = _sliceResourceRequestSystem;
        _sliceCacheClearPending = _sliceDebrisClearPending = false;
        _sliceResourceRequestSystem = null;
        bool requested = clearCache || clearDebris;
        if (!requested && (!_windowVisible || _playerTime < _sliceResourceNextRefresh)) return;
        try
        {
            if (requested)
            {
                if (!CanMaintainSliceResources || !ReferenceEquals(requestSystem, Universe.CurrentSystem))
                    _sliceResourceStatus = "Cleanup cancelled: the world changed or a slice/Furce hold is active.";
                else
                {
                    CancelForcePicker();
                    CancelAssistedSlice("Slice selection cancelled for cleanup.");
                    if (clearDebris)
                    {
                        _renderer.ClearEffects();
                        _sliceResourceStatus = SliceResourceMaintenance.ClearGeneratedDebris(requestSystem!);
                    }
                    if (clearCache)
                    {
                        string result = RuntimeFragmentAssets.ClearUnusedCaches();
                        _sliceResourceStatus = clearDebris ? _sliceResourceStatus + "\n" + result : result;
                    }
                }
            }
            _sliceResourceUsage = RuntimeFragmentAssets.GetUsage();
            _sliceResourceVehicleCount = Program.ControlledVehicle?.BubbleVehicleCount ?? 0;
            _sliceResourceDebrisCount = Universe.CurrentSystem is { } system
                ? SliceResourceMaintenance.CountEligible(system) : 0;
        }
        catch (Exception error)
        {
            _sliceResourceStatus = "Slice resource maintenance failed: " + error.Message;
            Report("Slice resource maintenance", error);
        }
        _sliceResourceNextRefresh = _playerTime + .5;
    }

    private void ResetSliceResourceRequests()
    {
        _sliceCacheClearPending = _sliceDebrisClearPending = false;
        _sliceResourceRequestSystem = null;
        _sliceResourceNextRefresh = 0;
        _sliceResourceVehicleCount = _sliceResourceDebrisCount = 0;
        _sliceResourceStatus = "";
        _confirmSliceDebrisRemoval = false;
    }
}
