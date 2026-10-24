using Purr2D2.Core;
using KSA;

namespace Purr2D2.Rendering;

internal static partial class RuntimeFragmentAssets
{
    private static readonly HashSet<UploadJob> UploadJobs = [];
    private static uint _initialVertexCapacity, _initialIndexCapacity;
    private static bool _growthEnabled = true, _growthPending, _growthUnsafe;
    private static uint _maximumVertexBytes = 512u * 1048576u, _maximumIndexBytes = 256u * 1048576u;
    private static ulong _globalVertexBytes, _globalIndexBytes;
    private static int _growthCount;
    private static string _growthStatus = "Ready";

    internal static void ConfigureGrowth(bool enabled, int maximumVertexMiB, int maximumIndexMiB)
    {
        uint vertices = Math.Max(_initialVertexCapacity, checked((uint)Math.Clamp(maximumVertexMiB,
            SliceResourceLimits.MinimumVertexArenaMiB, SliceResourceLimits.MaximumVertexArenaMiB) * 1048576u));
        uint indices = Math.Max(_initialIndexCapacity, checked((uint)Math.Clamp(maximumIndexMiB,
            SliceResourceLimits.MinimumIndexArenaMiB, SliceResourceLimits.MaximumIndexArenaMiB) * 1048576u));
        if (_growthEnabled == enabled && _maximumVertexBytes == vertices && _maximumIndexBytes == indices) return;
        _growthEnabled = enabled; _maximumVertexBytes = vertices; _maximumIndexBytes = indices;
        if (!_growthUnsafe) _growthStatus = enabled ? "Ready" : "Automatic mesh growth is disabled";
    }

    private static bool RequestGrowth(SliceMeshGrowthDemand demand, out string reason)
    {
        reason = demand.Failure ?? _growthStatus;
        if (demand.Failure != null) return false;
        if (_growthUnsafe) return false;
        if (!_growthEnabled)
        { reason = "Runtime slice mesh arena is full. Enable automatic mesh growth or increase the startup mesh limits and restart."; return false; }
        _growthPending = true; _growthStatus = "Waiting for uploads to drain before growing mesh buffers";
        return true;
    }

    /// <summary>
    /// Main thread only, after previous frame recording has joined and before any
    /// new draw/thumbnail recording. The caller must use the audited settled hook.
    /// Existing private uploads drain first; queue exclusion and device idle then
    /// protect copy/publication. One synchronous copy may briefly pause this frame.
    /// </summary>
    internal static void PumpGrowthAtFrameBoundary()
    {
        if (!Program.IsMainThread()) throw new InvalidOperationException("Mesh growth must run on the audited main-thread frame boundary.");
        if (!_initialized || !DeviceMeshInterleaved.Shared.IsBuilt || _arena == null) return;
        _globalVertexBytes = (ulong)DeviceMeshInterleaved.Shared.VertexAllocation.BufferSize;
        _globalIndexBytes = (ulong)DeviceMeshInterleaved.Shared.IndexAllocation.BufferSize;
        if (!_growthPending) return;
        if (!SliceMeshGrowth.TryGetDemand(UploadJobs.Select(j => j.GrowthDemand), out uint vertexRequest, out uint indexRequest))
        {
            _growthPending = false;
            _growthStatus = "Mesh growth request was cancelled";
            return;
        }
        try
        {
            foreach (var job in UploadJobs)
                if (!job.TryDrainForGrowth()) return;
            if (!_growthEnabled) throw new InvalidOperationException("Automatic mesh growth is disabled.");
            if (_globalVertexBytes > uint.MaxValue || _globalIndexBytes > uint.MaxValue)
                throw new InvalidOperationException("The native mesh buffers exceed the supported 32-bit address range.");
            if (!SliceMeshGrowth.TryPlan(_arena, vertexRequest, indexRequest,
                (uint)_globalVertexBytes, (uint)_globalIndexBytes, _maximumVertexBytes, _maximumIndexBytes,
                out var plan, out string reason)) throw new InvalidOperationException(reason);
            if (plan.Grows)
            {
                _arena.PrepareAppend(plan.Vertices.AppendBase, plan.Vertices.AppendBytes,
                    plan.Indices.AppendBase, plan.Indices.AppendBytes);
                using var backend = new NativeMeshGrowthBackend(_arena, plan);
                SliceMeshGrowthTransaction.Execute(plan, backend);
                _growthCount++;
                _globalVertexBytes = plan.Vertices.NewBytes; _globalIndexBytes = plan.Indices.NewBytes;
                _growthStatus = $"Mesh buffers grew successfully ({_growthCount} growth operation(s))";
            }
            else _growthStatus = "Completed uploads freed sufficient mesh space";
        }
        catch (Exception error)
        {
            _growthUnsafe |= error is MeshGrowthUncertainPublicationException;
            _growthStatus = "Mesh growth stopped: " + error.Message;
            foreach (var job in UploadJobs)
                if (job.GrowthDemand is { IsActive: true } demand) demand.Fail(_growthStatus);
        }
        _growthPending = false;
    }
}
