using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering.Raytracing;
using NativeQueue = Brutal.VulkanApi.Queue;

namespace Purr2D2.Rendering;

/// <summary>Fail-closed private adapter for the audited 2026.10.25.5707 host.</summary>
internal static class NativeMeshGrowthHost
{
    internal static readonly Guid AuditedKsaModule = new("a6318490-1e4f-4d4c-9f1a-b48f97f44be4");
    internal static readonly Guid AuditedVulkanModule = new("088d08a2-d0b4-43bc-93fe-68b321308a47");
    internal static readonly Guid AuditedAbstractionsModule = new("5b3e5098-d585-4a1c-be2b-33832dc4dbbb");
    internal static readonly Guid AuditedRenderCoreModule = new("8b56e057-d5e5-45a1-b050-7148531f6907");
    internal static readonly Guid AuditedCoreModule = new("ff08b769-d391-48e9-91ba-ec4495e7e897");
    internal static readonly FieldInfo QueueCacheField = RequireField(typeof(Device), "_queues", typeof(Dictionary<(int, int), NativeQueue>));
    internal static readonly FieldInfo QueueLockField = RequireField(typeof(NativeQueue), "_lock", typeof(System.Threading.Lock));
    internal static readonly FieldInfo RaytraceCreateInfoField = RequireField(typeof(RaytracePipeline), "_createInfo", typeof(RaytracePipeline.CreateInfo));
    internal static readonly FieldInfo RaytraceDescriptorField = RequireField(typeof(RaytracePipeline), "_descriptorSet", typeof(VkDescriptorSet));

    internal static void ValidateLayout()
    {
        ValidateBuild(typeof(Program).Module.ModuleVersionId, typeof(Device).Module.ModuleVersionId,
            typeof(BufferEx).Module.ModuleVersionId, typeof(global::Core.Renderer).Module.ModuleVersionId,
            typeof(global::Core.CommandBufferPool).Module.ModuleVersionId);
        // These members are deliberately touched here as well as by their static
        // initializers, so host checks exercise the production field contract.
        _ = QueueCacheField; _ = QueueLockField; _ = RaytraceCreateInfoField; _ = RaytraceDescriptorField;
    }

    internal static void ValidateBuild(Guid ksa, Guid vulkan, Guid abstractions, Guid renderCore, Guid core)
    {
        if (ksa != AuditedKsaModule || vulkan != AuditedVulkanModule || abstractions != AuditedAbstractionsModule
            || renderCore != AuditedRenderCoreModule || core != AuditedCoreModule)
            throw new NotSupportedException("Native mesh growth requires the audited KSA 2026.10.25.5707 rendering modules; this host build needs a new compatibility audit.");
    }

    internal static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType || field.IsStatic)
            throw new NotSupportedException($"Native mesh growth is unavailable: {type.FullName}.{name} changed.");
        return field;
    }
}
