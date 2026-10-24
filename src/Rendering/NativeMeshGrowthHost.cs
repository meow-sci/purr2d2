using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering.Raytracing;
using NativeQueue = Brutal.VulkanApi.Queue;

namespace Purr2D2.Rendering;

/// <summary>Fail-closed private adapter for the audited 2026.10.19.5638 host.</summary>
internal static class NativeMeshGrowthHost
{
    internal static readonly Guid AuditedKsaModule = new("80bf516c-4284-4284-9ce1-a0462e9664e1");
    internal static readonly Guid AuditedVulkanModule = new("72531e11-98ac-4f77-afd9-b9130e71655c");
    internal static readonly Guid AuditedAbstractionsModule = new("3b2d5dbf-8d4a-4533-81db-d19bce4736a3");
    internal static readonly Guid AuditedRenderCoreModule = new("333349df-2847-43e8-9a59-edc42cca6673");
    internal static readonly Guid AuditedCoreModule = new("2c27b4cd-7d63-415f-8516-bec2ab985fc2");
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
            throw new NotSupportedException("Native mesh growth requires the audited KSA 2026.10.19.5638 rendering modules; this host build needs a new compatibility audit.");
    }

    internal static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType || field.IsStatic)
            throw new NotSupportedException($"Native mesh growth is unavailable: {type.FullName}.{name} changed.");
        return field;
    }
}
