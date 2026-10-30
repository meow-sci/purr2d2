using Brutal;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Purr2D2.Rendering;

/// <summary>Dedicated device-local replacements with exception-safe creation.
/// Native DeviceEx.AllocateBuffer leaks its raw buffer if allocation/binding fails.
/// RaytraceAllocator wraps this allocator to supply its native device-address flags.</summary>
internal sealed class DedicatedMeshBufferAllocator(DeviceEx device) : IBufferAllocator
{
    public VkAllocator? VkAllocator => device.VkAllocator;
    public Device GetDevice() => device;
    public Ptr MapMemory(in AllocationMapInfo info) => throw new NotSupportedException("Shared mesh buffers use staging transfers.");
    public void UnmapMemory(in AllocationMapInfo info) => throw new NotSupportedException("Shared mesh buffers use staging transfers.");

    public unsafe BufferAllocationInfo AllocateBuffer(in string name, in VkBufferCreateInfo bufferInfo, in AllocationInfo.CreateInfo allocationInfo)
    {
        // RaytraceAllocator chains its device-address flags through Next. Flags in
        // the separate AllocateFlags channel would be dropped here, so refuse them.
        if (allocationInfo.AllocateFlags != VkMemoryAllocateFlags.None)
            throw new NotSupportedException("Shared mesh growth cannot preserve allocation flags supplied outside the native Next chain.");
        VkBuffer buffer = default; VkDeviceMemory memory = default;
        try
        {
            buffer = device.CreateBuffer(in bufferInfo, VkAllocator);
            var requirements = device.GetBufferMemoryRequirements(buffer);
            int memoryType = device.PhysicalDevice.FindMemoryType(requirements.MemoryTypeBits,
                allocationInfo.RequiredProperties, allocationInfo.PreferredProperties);
            // One allocation owns one buffer. Explicit dedication also satisfies
            // implementations that require it for large replacement buffers; keep
            // the native RT device-address flags chained behind this structure.
            var dedicated = new VkMemoryDedicatedAllocateInfo { Buffer = buffer, Next = allocationInfo.Next };
            var allocate = new VkMemoryAllocateInfo { AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType, Next = &dedicated };
            memory = device.AllocateMemory(in allocate, VkAllocator);
            device.BindBufferMemory(buffer, memory, ByteSize.Zero);
            return new BufferAllocationInfo(buffer, ByteSize.Zero, bufferInfo.Size, bufferInfo.Usage,
                new AllocationInfo(this, memory, requirements.Size, ByteSize.Zero, memoryType, 0));
        }
        catch
        {
            if (!buffer.IsNull()) device.DestroyBuffer(buffer, VkAllocator);
            if (!memory.IsNull()) device.FreeMemory(memory, VkAllocator);
            throw;
        }
    }

    public void FreeAllocation(in BufferAllocationInfo allocation)
    {
        if (!ReferenceEquals(allocation.AllocationInfo.Allocator, this))
            throw new InvalidOperationException("A shared mesh buffer was returned to the wrong allocator.");
        device.DestroyBuffer(allocation.VkBuffer, VkAllocator);
        device.FreeMemory(allocation.AllocationInfo.Memory, VkAllocator);
    }
}
