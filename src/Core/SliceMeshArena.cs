namespace Purr2D2.Core;

/// <summary>
/// Suballocates only the mod's startup reservation. Published regions remain pinned
/// for the session because registered native templates and old saves can reuse them.
/// An unpublished region may retire only after its upload fence has completed.
/// </summary>
internal sealed class SliceMeshArena
{
    internal sealed class Allocation(SliceMeshArena owner, uint vertexOffset, uint vertexBytes, uint indexOffset, uint indexBytes)
    {
        internal readonly SliceMeshArena Owner = owner;
        internal readonly uint VertexOffset = vertexOffset, VertexBytes = vertexBytes;
        internal readonly uint IndexOffset = indexOffset, IndexBytes = indexBytes;
        internal int State; // 0 uploading, 1 published, 2 retired after GPU completion, 3 reclaimed
    }

    private readonly RangePool _vertices, _indices;
    private readonly List<Allocation> _retired = [];
    internal uint VertexCapacity { get; }
    internal uint IndexCapacity { get; }
    internal uint VertexUsed { get; private set; }
    internal uint IndexUsed { get; private set; }
    internal uint ReclaimableVertexBytes { get; private set; }
    internal uint ReclaimableIndexBytes { get; private set; }
    internal long ReclaimedVertexBytes { get; private set; }
    internal long ReclaimedIndexBytes { get; private set; }

    internal SliceMeshArena(uint vertexCapacity, uint indexCapacity)
    {
        if (vertexCapacity == 0 || indexCapacity == 0 || vertexCapacity % 32 != 0 || indexCapacity % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCapacity));
        VertexCapacity = vertexCapacity; IndexCapacity = indexCapacity;
        _vertices = new(vertexCapacity); _indices = new(indexCapacity);
    }

    internal bool TryAllocate(uint vertexBytes, uint indexBytes, out Allocation? allocation)
    {
        allocation = null;
        if (vertexBytes == 0 || indexBytes == 0 || vertexBytes % 32 != 0 || indexBytes % 4 != 0) return false;
        if (Allocate(out allocation)) return true;
        ReclaimRetired(); // A failed unpublished upload must not strand otherwise usable capacity.
        return Allocate(out allocation);

        bool Allocate(out Allocation? selected)
        {
            selected = null;
            if (!_vertices.TryTake(vertexBytes, out uint vertex)) return false;
            if (!_indices.TryTake(indexBytes, out uint index)) { _vertices.Return(vertex, vertexBytes); return false; }
            selected = new(this, vertex, vertexBytes, index, indexBytes);
            VertexUsed += vertexBytes; IndexUsed += indexBytes;
            return true;
        }
    }

    internal void Publish(Allocation allocation)
    {
        if (!ReferenceEquals(allocation.Owner, this) || allocation.State != 0)
            throw new InvalidOperationException("Only this arena's active upload can be published.");
        allocation.State = 1;
    }

    /// <summary>Caller guarantees no GPU copy remains in flight and no mesh was registered.</summary>
    internal bool RetireUnpublished(Allocation allocation)
    {
        if (!ReferenceEquals(allocation.Owner, this) || allocation.State != 0) return false;
        allocation.State = 2; _retired.Add(allocation);
        ReclaimableVertexBytes += allocation.VertexBytes; ReclaimableIndexBytes += allocation.IndexBytes;
        return true;
    }

    internal int ReclaimRetired()
    {
        int count = _retired.Count;
        foreach (var allocation in _retired)
        {
            _vertices.Return(allocation.VertexOffset, allocation.VertexBytes);
            _indices.Return(allocation.IndexOffset, allocation.IndexBytes);
            VertexUsed -= allocation.VertexBytes; IndexUsed -= allocation.IndexBytes;
            ReclaimedVertexBytes += allocation.VertexBytes; ReclaimedIndexBytes += allocation.IndexBytes;
            allocation.State = 3;
        }
        _retired.Clear(); ReclaimableVertexBytes = ReclaimableIndexBytes = 0;
        return count;
    }

    private sealed class RangePool(uint capacity)
    {
        private readonly List<(uint Start, uint Count)> _free = [(0, capacity)];
        internal bool TryTake(uint count, out uint start)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                var range = _free[i];
                if (range.Count < count) continue;
                start = range.Start;
                if (range.Count == count) _free.RemoveAt(i);
                else _free[i] = (range.Start + count, range.Count - count);
                return true;
            }
            start = 0; return false;
        }
        internal void Return(uint start, uint count)
        {
            int index = 0;
            while (index < _free.Count && _free[index].Start < start) index++;
            _free.Insert(index, (start, count));
            if (index > 0 && _free[index - 1].Start + _free[index - 1].Count == start)
            {
                var previous = _free[index - 1];
                _free[index - 1] = (previous.Start, previous.Count + count);
                _free.RemoveAt(index); index--;
            }
            if (index + 1 < _free.Count && _free[index].Start + _free[index].Count == _free[index + 1].Start)
            {
                _free[index] = (_free[index].Start, _free[index].Count + _free[index + 1].Count);
                _free.RemoveAt(index + 1);
            }
        }
    }
}
