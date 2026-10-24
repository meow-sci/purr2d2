using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using KSA.Rendering.Raytracing;
using Purr2D2.Core;
using NativeQueue = Brutal.VulkanApi.Queue;

namespace Purr2D2.Rendering;

/// <summary>One synchronous replacement at an audited frame boundary. Old offsets
/// are preserved for every stock, historical and published runtime mesh.</summary>
internal sealed class NativeMeshGrowthBackend : IMeshGrowthBackend<BufferEx>, IDisposable
{
    private readonly Renderer _renderer;
    private readonly SliceMeshArena _arena;
    private readonly QueueExclusion _queues;
    private readonly RaytraceBindings? _raytrace;
    // If device loss or an exceptional descriptor rollback makes completion
    // unknowable, retain resources until the device itself is destroyed.
    private static readonly List<BufferEx> UncertainBuffers = [];

    public BufferEx Vertex { get; }
    public BufferEx Index { get; }

    internal NativeMeshGrowthBackend(SliceMeshArena arena, SliceMeshGrowthPlan plan)
    {
        NativeMeshGrowthHost.ValidateLayout();
        _renderer = Program.GetRenderer(); _arena = arena;
        Vertex = DeviceMeshInterleaved.Shared.VertexAllocation;
        Index = DeviceMeshInterleaved.Shared.IndexAllocation;
        ValidateBuffer(Vertex, plan.Vertices.OldBytes, DeviceMeshInterleaved.Shared.RunningVertexBufferSize, false);
        ValidateBuffer(Index, plan.Indices.OldBytes, DeviceMeshInterleaved.Shared.RunningIndexBufferSize, true);
        _raytrace = RaytraceBindings.Capture(Vertex, Index);
        ValidateDeviceLimits(plan, _raytrace != null);
        UncertainBuffers.EnsureCapacity(UncertainBuffers.Count + 2);
        _queues = new(_renderer);
        try { _renderer.Device.WaitIdle(); }
        catch { _queues.Dispose(); throw; }
    }

    private static void ValidateBuffer(BufferEx buffer, uint bytes, uint highWater, bool index)
    {
        var required = VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.TransferDstBit
            | (index ? VkBufferUsageFlags.IndexBufferBit : VkBufferUsageFlags.VertexBufferBit);
        if (buffer.IsNull() || (ulong)buffer.BufferSize != bytes || highWater != bytes
            || buffer.BindOffset != ByteSize.Zero || (buffer.Usage & required) != required)
            throw new NotSupportedException("Shared native mesh buffer sizes, offsets or allocation counters changed; existing meshes were retained.");
        var rt = VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit
            | VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKHR;
        if ((buffer.Usage & rt) != 0 && buffer.Allocator is not RaytraceAllocator)
            throw new NotSupportedException("The shared raytracing buffer allocator changed; its device-address allocation flags cannot be preserved safely.");
    }

    private unsafe void ValidateDeviceLimits(SliceMeshGrowthPlan plan, bool raytracing)
    {
        var properties = _renderer.PhysicalDevice.GetProperties();
        var allocation = new VkPhysicalDeviceMaintenance3Properties();
        var buffer = new VkPhysicalDeviceMaintenance4Properties();
        bool hasMaintenance4 = properties.ApiVersion >= ((1 << 22) | (3 << 12))
            || _renderer.PhysicalDevice.SupportsExtension("VK_KHR_maintenance4");
        if (hasMaintenance4) allocation.Next = &buffer;
        var query = new VkPhysicalDeviceProperties2 { Next = &allocation };
        _renderer.PhysicalDevice.GetProperties2(ref query);
        ulong largest = Math.Max(plan.Vertices.NewBytes, plan.Indices.NewBytes);
        if (allocation.MaxMemoryAllocationSize.Value == 0 || largest > allocation.MaxMemoryAllocationSize.Value
            || (hasMaintenance4 && (buffer.MaxBufferSize.Value == 0 || largest > buffer.MaxBufferSize.Value)))
            throw new NotSupportedException("The requested shared mesh buffers exceed this device's buffer/allocation size limit.");
        // The native RT descriptors bind the complete shared buffers with WHOLE_SIZE.
        uint storageLimit = unchecked((uint)properties.Limits.MaxStorageBufferRange);
        if (raytracing && largest > storageLimit)
            throw new NotSupportedException("The requested shared mesh buffers exceed this device's raytracing storage-buffer range limit.");
    }

    public BufferEx Allocate(bool index, uint bytes)
    {
        var original = index ? Index : Vertex;
        IBufferAllocator allocator = original.Allocator;
        if (allocator is RaytraceAllocator raytrace)
        {
            // DeviceEx's native factory does not roll back a raw VkBuffer when
            // VkAllocateMemory fails. Preserve native RT flags around a dedicated
            // allocator that owns every partial creation step instead.
            if (raytrace.Parent is not DeviceEx && raytrace.Parent is not DedicatedMeshBufferAllocator)
                throw new NotSupportedException("The raytracing buffer allocator changed; safe partial-allocation cleanup is unavailable.");
            allocator = new RaytraceAllocator(new DedicatedMeshBufferAllocator(_renderer.Device));
        }
        return allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = index ? "Purr2D2 grown shared mesh indices" : "Purr2D2 grown shared mesh vertices",
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
            BufferSize = new ByteSize(bytes), BufferUsage = original.Usage,
            BufferSharingMode = VkSharingMode.Exclusive
        });
    }

    public unsafe void CopyAndWait(SliceMeshGrowthPlan plan, BufferEx vertex, BufferEx index)
    {
        var device = _renderer.Device;
        VkCommandPool commandPool = default; VkFence fence = default;
        bool submitted = false, complete = false;
        try
        {
            commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo
            { QueueFamilyIndex = _renderer.Graphics.Family, Flags = VkCommandPoolCreateFlags.None }, null);
            var commands = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
            { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary });
            commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            var sourceBarrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.MemoryWriteBit,
                DstAccessMask = VkAccessFlags.TransferReadBit };
            commands.PipelineBarrier(VkPipelineStageFlags.AllCommandsBit, VkPipelineStageFlags.TransferBit,
                VkDependencyFlags.None, new ReadOnlySpan<VkMemoryBarrier>(ref sourceBarrier), default, default);
            // Copy exactly the OLD allocation lengths. Native Shared.Rebuild copies
            // the new lengths and is unsafe for actual growth.
            if (plan.Vertices.Grows) commands.CopyBuffer(Vertex.VkBuffer, vertex.VkBuffer, new ByteSize(plan.Vertices.OldBytes));
            if (plan.Indices.Grows) commands.CopyBuffer(Index.VkBuffer, index.VkBuffer, new ByteSize(plan.Indices.OldBytes));
            var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.MemoryReadBit };
            commands.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.AllCommandsBit,
                VkDependencyFlags.None, new ReadOnlySpan<VkMemoryBarrier>(ref barrier), default, default);
            commands.End();
            fence = device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.None }, null);
            submitted = true; // A submit exception can mean device loss: retain both pairs.
            _renderer.Graphics.Submit(default, default, new Span<CommandBuffer>(ref commands), default, fence);
            if (device.WaitForFences(new ReadOnlySpan<VkFence>(ref fence), true, -1) != VkResult.Success)
                throw new InvalidOperationException("The mesh replacement copy did not complete.");
            complete = true;
        }
        catch (Exception error) when (submitted && !complete)
        {
            throw new MeshGrowthUncertainPublicationException(
                "Mesh copy completion became unknown; mesh growth is disabled for this session and all buffer handles are retained.", error);
        }
        finally
        {
            // No StagingPool.Dispose here: it would submit a partially recorded
            // command buffer on failure. Destroy unsubmitted resources directly.
            if (!submitted || complete)
            {
                if (!fence.IsNull()) device.DestroyFence(fence, null);
                if (!commandPool.IsNull()) device.DestroyCommandPool(commandPool, null);
            }
        }
    }

    public void Publish(SliceMeshGrowthPlan plan, BufferEx vertex, BufferEx index)
    {
        _raytrace?.Replace(vertex.VkBuffer, index.VkBuffer);
        // All fallible native calls completed. No readers can record/submit until
        // this method returns and queue exclusion is released.
        DeviceMeshInterleaved.Shared.VertexAllocation = vertex;
        DeviceMeshInterleaved.Shared.IndexAllocation = index;
        DeviceMeshInterleaved.Shared.RunningVertexBufferSize = plan.Vertices.NewBytes;
        DeviceMeshInterleaved.Shared.RunningIndexBufferSize = plan.Indices.NewBytes;
        _arena.Append(plan.Vertices.AppendBase, plan.Vertices.AppendBytes, plan.Indices.AppendBase, plan.Indices.AppendBytes);
    }

    public void Release(BufferEx buffer) => buffer.Dispose();
    public void RetainUncertain(BufferEx buffer) => UncertainBuffers.Add(buffer);
    public void Dispose() => _queues.Dispose();

    private sealed class QueueExclusion : IDisposable
    {
        private readonly List<System.Threading.Lock> _entered = [];
        internal QueueExclusion(Renderer renderer)
        {
            var cache = NativeMeshGrowthHost.QueueCacheField.GetValue(renderer.Device)
                as Dictionary<(int, int), NativeQueue> ?? throw new NotSupportedException("The native device queue cache changed.");
            var known = new[] { renderer.Graphics, renderer.Compute, renderer.Transfer, renderer.Present, renderer.GraphicsAndCompute };
            if (cache.Count == 0 || known.Any(q => q == null || !cache.TryGetValue((q.Family, q.Index), out var cached) || !ReferenceEquals(q, cached))
                || cache.Any(p => !ReferenceEquals(p.Value.Device, renderer.Device) || p.Key != (p.Value.Family, p.Value.Index)))
                throw new NotSupportedException("The native queue cache is incomplete; safe mesh growth is unavailable.");
            _entered.EnsureCapacity(cache.Count);
            try
            {
                foreach (var queue in cache.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2))
                {
                    var gate = NativeMeshGrowthHost.QueueLockField.GetValue(queue.Value) as System.Threading.Lock
                        ?? throw new NotSupportedException("The native queue submission lock changed.");
                    if (_entered.Contains(gate)) continue;
                    gate.Enter(); _entered.Add(gate);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int i = _entered.Count - 1; i >= 0; i--) _entered[i].Exit();
            _entered.Clear();
        }
    }

    private sealed class RaytraceBindings(RaytracePipeline pipeline, VkDescriptorSet descriptor,
        VkBuffer[] vertices, VkBuffer[] indices, Device device)
    {
        internal static RaytraceBindings? Capture(BufferEx vertex, BufferEx index)
        {
            var renderer = Program.Instance.RaytracingRenderer;
            if (renderer == null)
            {
                if (Program.Instance.IsRaytracingActive)
                    throw new NotSupportedException("The active raytracing renderer is unavailable for mesh descriptor updates.");
                return null;
            }
            var pipeline = renderer.RaytracePipeline;
            if (NativeMeshGrowthHost.RaytraceCreateInfoField.GetValue(pipeline) is not RaytracePipeline.CreateInfo info
                || info.VertexBuffers is not { Length: 1 } || info.IndexBuffers is not { Length: 1 }
                || info.VertexBuffers[0].VkHandle != vertex.VkBuffer.VkHandle || info.IndexBuffers[0].VkHandle != index.VkBuffer.VkHandle
                || NativeMeshGrowthHost.RaytraceDescriptorField.GetValue(pipeline) is not VkDescriptorSet descriptor || descriptor.IsNull())
                throw new NotSupportedException("The native raytracing mesh descriptor contract changed; existing meshes were retained.");
            return new(pipeline, descriptor, info.VertexBuffers, info.IndexBuffers, Program.GetRenderer().Device);
        }

        internal void Replace(VkBuffer vertex, VkBuffer index)
        {
            VkBuffer oldVertex = vertices[0], oldIndex = indices[0];
            try { Write(vertex, index); }
            catch (Exception error)
            {
                try { Write(oldVertex, oldIndex); }
                catch (Exception rollback)
                { throw new MeshGrowthUncertainPublicationException("Raytracing descriptors could not be restored; all buffer handles are retained until shutdown.", new AggregateException(error, rollback)); }
                throw;
            }
            // These arrays are also held in the private CreateInfo struct. Updating
            // them prevents native pipeline Rebuild from resurrecting old handles.
            vertices[0] = vertex; indices[0] = index;
            GC.KeepAlive(pipeline);
        }

        private unsafe void Write(VkBuffer vertex, VkBuffer index)
        {
            var vertexInfo = new VkDescriptorBufferInfo { Buffer = vertex, Range = VK.WHOLE_SIZE };
            var indexInfo = new VkDescriptorBufferInfo { Buffer = index, Range = VK.WHOLE_SIZE };
            Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[2];
            writes[0] = new VkWriteDescriptorSet { DstSet = descriptor, DstBinding = 3, DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &vertexInfo };
            writes[1] = new VkWriteDescriptorSet { DstSet = descriptor, DstBinding = 4, DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &indexInfo };
            device.UpdateDescriptorSets(writes, default);
        }
    }
}
