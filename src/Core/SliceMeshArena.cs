namespace Purr2D2.Core;

/// <summary>
/// Suballocates disjoint ranges owned by the mod. Published regions remain pinned
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
    internal uint VertexCapacity { get; private set; }
    internal uint IndexCapacity { get; private set; }
    internal uint LargestVertexRange => _vertices.Largest;
    internal uint LargestIndexRange => _indices.Largest;
    internal uint VertexUsed { get; private set; }
    internal uint IndexUsed { get; private set; }
    internal uint ReclaimableVertexBytes { get; private set; }
    internal uint ReclaimableIndexBytes { get; private set; }
    internal long ReclaimedVertexBytes { get; private set; }
    internal long ReclaimedIndexBytes { get; private set; }

    internal SliceMeshArena(uint vertexCapacity, uint indexCapacity, uint vertexBase = 0, uint indexBase = 0)
    {
        if (vertexCapacity == 0 || indexCapacity == 0 || vertexCapacity % 32 != 0 || indexCapacity % 4 != 0
            || vertexBase % 32 != 0 || indexBase % 4 != 0
            || (ulong)vertexBase + vertexCapacity > uint.MaxValue || (ulong)indexBase + indexCapacity > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(vertexCapacity));
        VertexCapacity = vertexCapacity; IndexCapacity = indexCapacity;
        _vertices = new(vertexBase, vertexCapacity); _indices = new(indexBase, indexCapacity);
    }

    internal uint VertexTailAvailable(uint bufferEnd) => _vertices.TailAvailable(bufferEnd);
    internal uint IndexTailAvailable(uint bufferEnd) => _indices.TailAvailable(bufferEnd);

    // Validate both channels and reserve list capacity before GPU publication.
    internal void PrepareAppend(uint vertexBase, uint vertexBytes, uint indexBase, uint indexBytes)
    {
        ValidateAppend(vertexBase, vertexBytes, indexBase, indexBytes);
        _vertices.PrepareAppend(); _indices.PrepareAppend();
    }
    internal void ValidateAppend(uint vertexBase, uint vertexBytes, uint indexBase, uint indexBytes)
    {
        if ((vertexBytes != 0 && vertexBase % 32 != 0) || vertexBytes % 32 != 0
            || (indexBytes != 0 && indexBase % 4 != 0) || indexBytes % 4 != 0
            || (ulong)VertexCapacity + vertexBytes > uint.MaxValue || (ulong)IndexCapacity + indexBytes > uint.MaxValue)
            throw new InvalidOperationException("Appended mesh ranges exceed the supported capacity/alignment.");
        _vertices.ValidateAppend(vertexBase, vertexBytes); _indices.ValidateAppend(indexBase, indexBytes);
    }
    internal void Append(uint vertexBase, uint vertexBytes, uint indexBase, uint indexBytes)
    {
        // Caller already prepared this exact plan. This publication commit cannot
        // allocate or fail after native descriptors and shared fields were switched.
        _vertices.Append(vertexBase, vertexBytes); _indices.Append(indexBase, indexBytes);
        VertexCapacity += vertexBytes; IndexCapacity += indexBytes;
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

    private sealed class RangePool(uint initialBase, uint capacity)
    {
        private readonly List<(uint Start, uint Count)> _free = [(initialBase, capacity)];
        private readonly List<(uint Start, uint Count)> _owned = [(initialBase, capacity)];
        internal uint Largest => _free.Count == 0 ? 0 : _free.Max(r => r.Count);
        internal uint TailAvailable(uint end)
        {
            foreach (var range in _free)
                if ((ulong)range.Start + range.Count == end) return range.Count;
            return 0;
        }
        internal void PrepareAppend()
        {
            _free.EnsureCapacity(_free.Count + 1);
            _owned.EnsureCapacity(_owned.Count + 1);
        }
        internal void ValidateAppend(uint start, uint count)
        {
            if (count == 0) return;
            if ((ulong)start + count > uint.MaxValue)
                throw new InvalidOperationException("An appended mesh range exceeds native offsets.");
            foreach (var range in _owned)
                if ((ulong)range.Start + range.Count > start)
                    throw new InvalidOperationException("An appended mesh range overlaps previous ownership.");
        }
        internal void Append(uint start, uint count)
        {
            if (count == 0) return;
            // Appended buffers are always at the global end, beyond every previous
            // owned segment. Do not claim intervening native/stock mesh bytes.
            _owned.Add((start, count)); Return(start, count);
        }
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
