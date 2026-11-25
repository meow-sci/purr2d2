using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering.Raytracing;
using NativeQueue = Brutal.VulkanApi.Queue;

namespace Purr2D2.Rendering;

/// <summary>Fail-closed private adapter for the audited 2026.11.25.5949 host.</summary>
internal static class NativeMeshGrowthHost
{
    internal static readonly Guid AuditedKsaModule = new("1e1e0a8c-ba04-4f83-bfc4-663993881b08");
    internal static readonly Guid AuditedVulkanModule = new("52acb320-b412-4d37-82d8-0f5036a87c3f");
    internal static readonly Guid AuditedAbstractionsModule = new("64b0e253-f675-44c8-83ea-94a473555e8f");
    internal static readonly Guid AuditedRenderCoreModule = new("1cabda1d-6d7c-481a-b592-d12396de3c21");
    internal static readonly Guid AuditedCoreModule = new("c3840f09-0347-4d95-89c2-c1f30da86c34");
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
            throw new NotSupportedException("Native mesh growth requires the audited KSA 2026.11.25.5949 rendering modules; this host build needs a new compatibility audit.");
    }

    internal static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType || field.IsStatic)
            throw new NotSupportedException($"Native mesh growth is unavailable: {type.FullName}.{name} changed.");
        return field;
    }
}
