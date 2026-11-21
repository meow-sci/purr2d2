using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering.Raytracing;
using NativeQueue = Brutal.VulkanApi.Queue;

namespace Purr2D2.Rendering;

/// <summary>Fail-closed private adapter for the audited 2026.11.16.5901 host.</summary>
internal static class NativeMeshGrowthHost
{
    internal static readonly Guid AuditedKsaModule = new("ded30488-a614-431f-ab2e-8c395b14bb59");
    internal static readonly Guid AuditedVulkanModule = new("2edf3fc0-2178-449e-ad38-08f06406147d");
    internal static readonly Guid AuditedAbstractionsModule = new("f02cf35a-670a-499c-8d23-02594ae1edee");
    internal static readonly Guid AuditedRenderCoreModule = new("e5d0db6b-5ee7-4026-9e2e-2e74971e585e");
    internal static readonly Guid AuditedCoreModule = new("0b627248-1c28-4c7f-8784-8b4a4ecb2f60");
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
            throw new NotSupportedException("Native mesh growth requires the audited KSA 2026.11.16.5901 rendering modules; this host build needs a new compatibility audit.");
    }

    internal static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType || field.IsStatic)
            throw new NotSupportedException($"Native mesh growth is unavailable: {type.FullName}.{name} changed.");
        return field;
    }
}
