namespace Purr2D2.Core;

internal readonly record struct SliceMeshChannelGrowth(uint OldBytes, uint NewBytes, uint AppendBase, uint AppendBytes)
{
    internal bool Grows => AppendBytes != 0;
}

internal readonly record struct SliceMeshGrowthPlan(SliceMeshChannelGrowth Vertices, SliceMeshChannelGrowth Indices)
{
    internal bool Grows => Vertices.Grows || Indices.Grows;
}

internal sealed class SliceMeshGrowthDemand(uint vertices, uint indices)
{
    internal readonly uint VertexBytes = vertices, IndexBytes = indices;
    internal bool IsActive { get; private set; } = true;
    internal string? Failure { get; private set; }
    internal void Cancel() => IsActive = false;
    internal void Fail(string reason) => Failure = reason;
}

/// <summary>Plans independent append-only ranges at the actual native allocation ends.</summary>
internal static class SliceMeshGrowth
{
    internal static bool TryGetDemand(IEnumerable<SliceMeshGrowthDemand?> demands, out uint vertices, out uint indices)
    {
        vertices = indices = 0;
        foreach (var demand in demands)
            if (demand is { IsActive: true, Failure: null })
            { vertices = Math.Max(vertices, demand.VertexBytes); indices = Math.Max(indices, demand.IndexBytes); }
        return vertices != 0 && indices != 0;
    }

    internal static bool TryPlan(SliceMeshArena arena, uint vertexRequest, uint indexRequest,
        uint globalVertexBytes, uint globalIndexBytes, uint maximumVertexBytes, uint maximumIndexBytes,
        out SliceMeshGrowthPlan plan, out string reason)
    {
        plan = default;
        if (vertexRequest == 0 || indexRequest == 0 || vertexRequest % 32 != 0 || indexRequest % 4 != 0)
        { reason = "Runtime mesh upload sizes are invalid."; return false; }
        if (!Channel(arena.VertexCapacity, arena.LargestVertexRange, arena.VertexTailAvailable(globalVertexBytes),
                vertexRequest, globalVertexBytes, maximumVertexBytes, 32, out var vertex)
            || !Channel(arena.IndexCapacity, arena.LargestIndexRange, arena.IndexTailAvailable(globalIndexBytes),
                indexRequest, globalIndexBytes, maximumIndexBytes, 4, out var index))
        { reason = "Runtime mesh growth limit reached. Increase the maximum mesh limits in Settings to continue cutting."; return false; }
        plan = new(vertex, index); reason = "";
        return true;
    }

    private static bool Channel(uint owned, uint largest, uint tail, uint request, uint globalBytes,
        uint maximum, uint alignment, out SliceMeshChannelGrowth plan)
    {
        plan = new(globalBytes, globalBytes, globalBytes, 0);
        if (largest >= request) return true;
        ulong start = ((ulong)globalBytes + alignment - 1) & ~((ulong)alignment - 1);
        if (start != globalBytes) tail = 0;
        ulong required = request > tail ? request - tail : 0;
        ulong remaining = maximum > owned ? maximum - owned : 0;
        if (required == 0 || required > remaining) return false;
        // Geometric growth amortizes whole-buffer copies; the user limit bounds owned
        // bytes independently from stock/history bytes in the shared allocation.
        ulong additional = Math.Min(remaining, Math.Max((ulong)owned, required));
        additional &= ~((ulong)alignment - 1);
        ulong end = start + additional;
        if (additional < required || end > uint.MaxValue) return false;
        plan = new(globalBytes, (uint)end, (uint)start, (uint)additional);
        return true;
    }
}

/// <summary>A failed publication could not restore all descriptor references. Neither
/// old nor new buffers may be freed until shutdown/device teardown.</summary>
internal sealed class MeshGrowthUncertainPublicationException(string message, Exception inner) : Exception(message, inner);

internal interface IMeshGrowthBackend<T>
{
    T Vertex { get; }
    T Index { get; }
    T Allocate(bool index, uint bytes);
    void CopyAndWait(SliceMeshGrowthPlan plan, T vertex, T index);
    // All fallible work precedes the shared-field commit. Failure must restore old
    // bindings or throw MeshGrowthUncertainPublicationException to retain both pairs.
    void Publish(SliceMeshGrowthPlan plan, T vertex, T index);
    void Release(T buffer);
    void RetainUncertain(T buffer);
}

/// <summary>The same ownership transaction is exercised by the GPU-free fault tests.</summary>
internal static class SliceMeshGrowthTransaction
{
    internal static void Execute<T>(SliceMeshGrowthPlan plan, IMeshGrowthBackend<T> backend)
    {
        if (!plan.Grows) return;
        T oldVertex = backend.Vertex, oldIndex = backend.Index;
        T vertex = oldVertex, index = oldIndex;
        bool ownsVertex = false, ownsIndex = false;
        try
        {
            if (plan.Vertices.Grows) { vertex = backend.Allocate(false, plan.Vertices.NewBytes); ownsVertex = true; }
            if (plan.Indices.Grows) { index = backend.Allocate(true, plan.Indices.NewBytes); ownsIndex = true; }
            backend.CopyAndWait(plan, vertex, index);
            backend.Publish(plan, vertex, index);
        }
        catch (MeshGrowthUncertainPublicationException)
        {
            if (ownsVertex) backend.RetainUncertain(vertex);
            if (ownsIndex) backend.RetainUncertain(index);
            throw;
        }
        catch
        {
            // Never release an old allocation through a copied readonly wrapper.
            if (ownsIndex) backend.Release(index);
            if (ownsVertex) backend.Release(vertex);
            throw;
        }
        // No operation after publication can roll back the new ownership. Release
        // old handles only after copies, fences and descriptor repairs completed.
        if (ownsIndex) backend.Release(oldIndex);
        if (ownsVertex) backend.Release(oldVertex);
    }
}
