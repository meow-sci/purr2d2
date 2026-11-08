using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Raytracing;
using KSA.Rendering.Thumbnails;
using Purr2D2.Rendering;
using RenderCore;
using VulkanQueue = Brutal.VulkanApi.Queue;

// All assertions inspect real host types and IL. No renderer or Vulkan device is created.
internal static class MeshGrowthHostChecks
{
    private static void Main()
    {
        NativeMeshGrowthHost.ValidateLayout();
        UnsupportedBuilds();
        SharedBufferReferences();
        RaytraceReferences();
        QueueExclusion();
        RecordingBoundary();
        ProductionReplacement();
        DedicatedAllocationCleanup();
        Console.WriteLine("PASS: mesh growth host adapter accepts audited modules and exact field types; real IL verifies current shared handles, raytracing descriptor/cache bindings, queue locking, settled recording order, immediate thumbnails, old-size copies, barriers and global counter publication");
        Console.WriteLine("GPU copies, driver synchronization and rendering still require the in-game growth smoke test.");
    }

    private static void SharedBufferReferences()
    {
        var meshFields = new (string Name, Type Type)[]
        {
            ("HostMesh", typeof(MeshAsset)), ("HasNormal", typeof(bool)), ("HasUV", typeof(bool)),
            ("VertexStride", typeof(ByteSize)), ("VerticesOffset", typeof(ByteSize)), ("VerticesSize", typeof(ByteSize)),
            ("IndexCount", typeof(int)), ("IndexType", typeof(VkIndexType)), ("IndicesStride", typeof(ByteSize)),
            ("IndicesOffset", typeof(ByteSize)), ("IndicesSize", typeof(ByteSize))
        };
        Check(typeof(DeviceMeshInterleaved).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == meshFields.Length,
            "Detached device meshes must not skip new native constructor-owned state.");
        foreach (var (name, type) in meshFields)
            Check(Field(typeof(DeviceMeshInterleaved), name, type, false).IsPublic,
                $"Native detached mesh field {name} must remain a plain public field of its audited type.");
        Check(typeof(BufferEx).IsValueType && typeof(BufferEx).IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute)),
            "BufferEx must remain a readonly value wrapper; duplicate wrappers do not own independent allocations.");
        var vertex = Field(typeof(DeviceMeshInterleaved.Shared), "VertexAllocation", typeof(BufferEx), true);
        var index = Field(typeof(DeviceMeshInterleaved.Shared), "IndexAllocation", typeof(BufferEx), true);
        Field(typeof(DeviceMeshInterleaved.Shared), "RunningVertexBufferSize", typeof(uint), true);
        Field(typeof(DeviceMeshInterleaved.Shared), "RunningIndexBufferSize", typeof(uint), true);
        Check(Il(Method(typeof(DeviceMeshInterleaved), "get_Vertices")).Any(c => Loads(c, vertex))
            && Il(Method(typeof(DeviceMeshInterleaved), "get_Indices")).Any(c => Loads(c, index)),
            "Existing device meshes must read current shared handles instead of caching replaced buffers.");
        var bind = Il(Method(typeof(DeviceMeshInterleaved.Shared), "BindBuffers"));
        Check(bind.Any(c => Loads(c, vertex)) && bind.Any(c => Loads(c, index)),
            "Raster draws must bind current shared handles.");
        foreach (var (type, name) in new (Type, string)[]
        {
            (typeof(PartModelRaytracingData), "AddCommandsRaytraced"), (typeof(PartModelRaytracingData), "AddCommandsPrePass"),
            (typeof(PartModelRaytracingGlassData), "AddCommandsRaytraced"), (typeof(PartModelRaytracingGlassData), "AddCommandsPrePass"),
            (typeof(PartModelRaytracingGlassData), "WriteCommandsColor"), (typeof(PartModelGlass.Shared), "WriteCommandsColor"),
            (typeof(ThumbnailRenderer), "RecordPartRender")
        })
        {
            var draw = Il(Method(type, name));
            Check(draw.Any(c => Loads(c, vertex)) && draw.Any(c => Loads(c, index)),
                $"Native {type.Name}.{name} must bind both current shared handles.");
        }
    }

    private static void RaytraceReferences()
    {
        var createInfo = Field(typeof(RaytracePipeline), "_createInfo", typeof(RaytracePipeline.CreateInfo), false);
        var descriptor = Field(typeof(RaytracePipeline), "_descriptorSet", typeof(VkDescriptorSet), false);
        Check(createInfo == NativeMeshGrowthHost.RaytraceCreateInfoField
            && descriptor == NativeMeshGrowthHost.RaytraceDescriptorField,
            "Production adapter must use the verified raytracing cache and descriptor fields.");
        var vertices = Field(typeof(RaytracePipeline.CreateInfo), "VertexBuffers", typeof(VkBuffer[]), false);
        var indices = Field(typeof(RaytracePipeline.CreateInfo), "IndexBuffers", typeof(VkBuffer[]), false);
        var descriptorIl = Il(Method(typeof(RaytracePipeline), "CreateDescriptorSets"));
        var binding = Field(typeof(VkWriteDescriptorSet), "DstBinding", typeof(int), false);
        foreach (int value in new[] { 3, 4 })
            Check(descriptorIl.Count(c => c.StoresField(binding)) > 0
                && descriptorIl.Select((c, i) => (c, i)).Count(p => p.i > 0 && p.c.StoresField(binding)
                    && descriptorIl[p.i - 1].LoadsConstant(value)) == 1,
                $"Native raytracing storage binding {value} moved or became ambiguous.");
        Check(descriptorIl.Any(c => Loads(c, vertices)) && descriptorIl.Any(c => Loads(c, indices)),
            "Native descriptor creation must consume both cached geometry arrays.");
        var rebuild = Il(Method(typeof(RaytracePipeline), "Rebuild"));
        Check(rebuild.Any(c => Loads(c, createInfo))
            && rebuild.Any(c => c.Calls(Method(typeof(RaytracePipeline), "CreateDescriptorSets"))),
            "Native shader rebuild must continue consuming the repaired cache.");
        var constructor = typeof(RaytracingRenderer).GetConstructors().Single();
        var constructorIl = Il(constructor);
        var sharedVertex = Field(typeof(DeviceMeshInterleaved.Shared), "VertexAllocation", typeof(BufferEx), true);
        var sharedIndex = Field(typeof(DeviceMeshInterleaved.Shared), "IndexAllocation", typeof(BufferEx), true);
        Check(constructorIl.Any(c => Loads(c, sharedVertex)) && constructorIl.Any(c => Loads(c, sharedIndex))
            && constructorIl.Any(c => c.StoresField(vertices)) && constructorIl.Any(c => c.StoresField(indices)),
            "Raytracing initialization must still use shared geometry buffers for its cached arrays.");
        Check(typeof(RaytraceBlasRef).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .All(f => f.FieldType != typeof(VkBuffer) && f.FieldType != typeof(VkDeviceAddress)),
            "BLAS references must not acquire additional cached geometry input handles or addresses.");
        var build = Il(Method(typeof(RaytraceBlasUtils), "CreateBLAS"));
        Check(build.Any(c => c.Calls(Method(typeof(StagingPool), "Wait")))
            && build.Any(c => c.Calls(Method(typeof(StagingPool), "Submit", Type.EmptyTypes))),
            "BLAS creation must finish its native geometry read before returning.");
    }

    private static void QueueExclusion()
    {
        var cache = Field(typeof(Device), "_queues", typeof(Dictionary<(int, int), VulkanQueue>), false);
        var queueLock = Field(typeof(VulkanQueue), "_lock", typeof(Lock), false);
        Check(cache.IsInitOnly && queueLock.IsInitOnly && cache == NativeMeshGrowthHost.QueueCacheField
            && queueLock == NativeMeshGrowthHost.QueueLockField,
            "Production queue exclusion must use the exact native cache and reentrant locks.");
        var get = Il(Method(typeof(Device), "GetQueue"));
        Check(get.Any(c => Loads(c, cache)) && get.Count(c => c.opcode == OpCodes.Newobj
            && c.operand is ConstructorInfo info && info.DeclaringType == typeof(VulkanQueue)) == 1,
            "Device.GetQueue must remain the unique cached queue wrapper constructor.");
        foreach (string name in new[] { "Submit", "Submit2", "WaitIdle", "PresentKHR", "BindSparse" })
        {
            var il = Il(Method(typeof(VulkanQueue), name));
            int enter = il.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Lock) && m.Name == "EnterScope");
            Check(enter > 0 && il.Take(enter).Any(c => Loads(c, queueLock)),
                $"Native queue {name} must enter the same host lock.");
            Check(il.Skip(enter + 1).Any(c => c.operand is MethodInfo m && m.Name == name + "Internal"),
                $"Native queue {name} must invoke its driver operation under that lock.");
        }
        var recursive = new Lock();
        recursive.Enter();
        try { using (recursive.EnterScope()) { Check(recursive.IsHeldByCurrentThread, "Queue locks must support same-thread nested submit."); } }
        finally { recursive.Exit(); }
    }

    private static void RecordingBoundary()
    {
        var prepare = Il(Method(typeof(KSA.Program), "PrepareFrame"));
        Ordered(prepare,
            Method(typeof(Universe), "ApplyOrbitSolvers"), Method(typeof(Universe), "ApplyVehicleSolvers"),
            Method(typeof(Universe), "ApplyClothSolvers"), Method(typeof(InputEvents), "ApplyInputEvents"),
            Method(typeof(Universe), "ExecuteNextClothSolvers"), Method(typeof(Universe), "ExecuteNextVehicleSolvers"));
        Ordered(Il(Method(typeof(KSA.Program), "OnFrame")), Method(typeof(KSA.Program), "PrepareFrame"),
            Method(typeof(KSA.Program), "OnFrameEditor"), Method(typeof(KSA.Program), "OnPreRender"),
            Method(typeof(KSA.Program), "Render"), Method(typeof(KSA.Program), "PostRender"));
        Check(Il(Method(typeof(VehicleEditor), "OnPreRender")).Any(c => c.Calls(Method(typeof(ThumbnailDynamic), "Render"))),
            "Dynamic thumbnail recording must remain in the later pre-render phase.");
        var thumbnail = Il(Method(typeof(ThumbnailDynamic), "Render"));
        int record = thumbnail.FindIndex(c => c.Calls(Method(typeof(ThumbnailRenderer), "RecordPartRender")));
        int end = thumbnail.FindIndex(record + 1, c => c.operand is MethodInfo m && m.Name == "End");
        int submit = thumbnail.FindIndex(end + 1, c => c.operand is MethodInfo m && m.Name == "Submit");
        Check(record >= 0 && end > record && submit > end,
            "Dynamic thumbnails must finish and submit their recorded buffer before returning.");
        var startupThumbnail = Il(Method(typeof(ThumbnailCreator), "PreparePartThumbnails"));
        Check(startupThumbnail.Any(c => c.operand is MethodInfo m && m.Name == "WaitForFence"),
            "Startup thumbnail generation must finish GPU work before normal frames begin.");
    }

    private static FieldInfo Field(Type type, string name, Type expected, bool isStatic)
    {
        var field = AccessTools.DeclaredField(type, name);
        Check(field != null && field.FieldType == expected && field.IsStatic == isStatic,
            $"Native field {type.Name}.{name} changed.");
        return field!;
    }

    private static void ProductionReplacement()
    {
        var copy = Il(Method(typeof(NativeMeshGrowthBackend), "CopyAndWait"));
        Check(copy.Count(c => c.operand is MethodInfo m && m.Name == "get_OldBytes") == 2
            && !copy.Any(c => c.operand is MethodInfo m && m.Name == "get_NewBytes"),
            "Native copies must read exactly the old allocation lengths, never the larger replacement sizes.");
        var source = Field(typeof(VkMemoryBarrier), "SrcAccessMask", typeof(VkAccessFlags), false);
        var destination = Field(typeof(VkMemoryBarrier), "DstAccessMask", typeof(VkAccessFlags), false);
        Check(StoresConstant(copy, source, (long)VkAccessFlags.MemoryWriteBit)
            && StoresConstant(copy, destination, (long)VkAccessFlags.TransferReadBit)
            && StoresConstant(copy, source, (long)VkAccessFlags.TransferWriteBit)
            && StoresConstant(copy, destination, (long)VkAccessFlags.MemoryReadBit),
            "Native replacement must order earlier writes before copying and expose copied bytes to later readers.");
        Check(copy.Count(c => c.operand is MethodInfo m && m.Name == "PipelineBarrier") == 2
            && copy.Any(c => c.LoadsConstant((long)VkPipelineStageFlags.AllCommandsBit))
            && copy.Any(c => c.LoadsConstant((long)VkPipelineStageFlags.TransferBit)),
            "Copy barriers must include all consumers and the transfer stage.");
        var publish = Il(Method(typeof(NativeMeshGrowthBackend), "Publish"));
        string[] fields = ["VertexAllocation", "IndexAllocation", "RunningVertexBufferSize", "RunningIndexBufferSize"];
        int previous = -1;
        foreach (string name in fields)
        {
            var field = AccessTools.DeclaredField(typeof(DeviceMeshInterleaved.Shared), name)!;
            var stores = publish.Select((c, i) => (c, i)).Where(p => p.c.StoresField(field)).Select(p => p.i).ToArray();
            Check(stores.Length == 1 && stores[0] > previous, "Production publication must update each shared handle/counter exactly once.");
            previous = stores[0];
        }
        Check(publish.Count(c => c.operand is MethodInfo m && m.Name == "get_NewBytes") == 2,
            "Published native counters must advance to actual replacement buffer sizes.");
        int repair = publish.FindIndex(c => c.operand is MethodInfo m && m.Name == "Replace");
        int firstStore = publish.FindIndex(c => c.opcode == OpCodes.Stsfld);
        int append = publish.FindIndex(c => c.operand is MethodInfo m && m.Name == "Append");
        Check(repair >= 0 && repair < firstStore && append > previous,
            "Fallible descriptor repair must precede global handle/counter and arena commits.");
        Check(!copy.Concat(publish).Any(c => c.Calls(Method(typeof(DeviceMeshInterleaved.Shared), "Rebuild"))),
            "The native Rebuild method must never be used for allocation growth.");
    }

    private static bool StoresConstant(List<CodeInstruction> il, FieldInfo field, long value) =>
        il.Select((c, i) => (c, i)).Any(p => p.i > 0 && p.c.StoresField(field) && il[p.i - 1].LoadsConstant(value));

    private static void DedicatedAllocationCleanup()
    {
        var method = Method(typeof(DedicatedMeshBufferAllocator), "AllocateBuffer");
        var il = Il(method);
        Check(method.GetMethodBody()!.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause)
            && il.Any(c => c.operand is MethodInfo m && m.Name == "DestroyBuffer")
            && il.Any(c => c.operand is MethodInfo m && m.Name == "FreeMemory"),
            "Partial dedicated allocation failures must release both raw native resources.");
        int destroy = il.FindIndex(c => c.operand is MethodInfo m && m.Name == "DestroyBuffer");
        int free = il.FindIndex(c => c.operand is MethodInfo m && m.Name == "FreeMemory");
        Check(destroy < free, "A bound buffer must be destroyed before its dedicated memory is freed.");
        Check(il.Any(c => c.operand is MethodInfo m && m.Name == "get_Next" && m.DeclaringType == typeof(AllocationInfo.CreateInfo))
            && il.Any(c => c.StoresField(AccessTools.DeclaredField(typeof(VkMemoryDedicatedAllocateInfo), "Next")))
            && il.Any(c => c.StoresField(AccessTools.DeclaredField(typeof(VkMemoryDedicatedAllocateInfo), "Buffer")))
            && il.Any(c => c.StoresField(AccessTools.DeclaredField(typeof(VkMemoryAllocateInfo), "Next"))),
            "Dedicated allocation must preserve the incoming raytracing allocation flag chain.");
        int flags = il.FindIndex(c => c.operand is MethodInfo m && m.Name == "get_AllocateFlags"
            && m.DeclaringType == typeof(AllocationInfo.CreateInfo));
        int create = il.FindIndex(c => c.operand is MethodInfo m && m.Name == "CreateBuffer");
        Check(flags >= 0 && create > flags && il.Take(create).Any(c => c.opcode == OpCodes.Newobj
            && c.operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(NotSupportedException)),
            "Dedicated allocation must refuse AllocateFlags it cannot chain, before creating any native buffer.");
        // The native RT wrapper must keep supplying DEVICE_ADDRESS through Next, the
        // only flag path the dedicated replacement allocator forwards.
        var raytrace = Il(Method(typeof(RaytraceAllocator), "AllocateBuffer"));
        var flagField = AccessTools.DeclaredField(typeof(VkMemoryAllocateFlagsInfo), "Flags");
        Check(flagField != null && StoresConstant(raytrace, flagField, (long)VkMemoryAllocateFlags.DeviceAddressBit)
            && raytrace.Any(c => c.operand is MethodInfo m && m.Name == "set_Next" && m.DeclaringType == typeof(AllocationInfo.CreateInfo))
            && !raytrace.Any(c => c.operand is MethodInfo m && m.Name == "set_AllocateFlags"),
            "RaytraceAllocator must chain its device-address allocation flag through AllocationInfo.CreateInfo.Next.");
        var freeIl = Il(Method(typeof(DedicatedMeshBufferAllocator), "FreeAllocation"));
        Check(freeIl.Any(c => c.operand is MethodInfo m && m.Name == "get_Allocator")
            && freeIl.Any(c => c.opcode.FlowControl == FlowControl.Cond_Branch)
            && freeIl.Any(c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ctor
                && ctor.DeclaringType == typeof(InvalidOperationException)),
            "Dedicated memory must reject disposal through an unrelated allocator.");
    }

    private static MethodInfo Method(Type type, string name, Type[]? parameters = null) =>
        (parameters == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, parameters))
        ?? throw new Exception($"Missing native method {type.Name}.{name}.");

    private static List<CodeInstruction> Il(MethodBase method) => PatchProcessor.GetOriginalInstructions(method);

    private static bool Loads(CodeInstruction code, FieldInfo field) => code.LoadsField(field) || code.LoadsField(field, true);

    private static void UnsupportedBuilds()
    {
        Guid[] modules = [typeof(KSA.Program).Module.ModuleVersionId, typeof(Device).Module.ModuleVersionId,
            typeof(BufferEx).Module.ModuleVersionId, typeof(Core.Renderer).Module.ModuleVersionId,
            typeof(Core.CommandBufferPool).Module.ModuleVersionId];
        for (int index = 0; index < modules.Length; index++)
        {
            var candidate = (Guid[])modules.Clone(); candidate[index] = Guid.Empty;
            try { NativeMeshGrowthHost.ValidateBuild(candidate[0], candidate[1], candidate[2], candidate[3], candidate[4]); }
            catch (NotSupportedException) { continue; }
            throw new Exception("The production adapter accepted an unsupported module combination.");
        }
        // Neither earlier audited module set is covered by the current audit.
        try
        {
            NativeMeshGrowthHost.ValidateBuild(new("a6318490-1e4f-4d4c-9f1a-b48f97f44be4"), new("088d08a2-d0b4-43bc-93fe-68b321308a47"),
                new("5b3e5098-d585-4a1c-be2b-33832dc4dbbb"), new("8b56e057-d5e5-45a1-b050-7148531f6907"), new("ff08b769-d391-48e9-91ba-ec4495e7e897"));
            throw new Exception("The production adapter accepted the previous audited 5707 host build.");
        }
        catch (NotSupportedException) { }
        try
        {
            NativeMeshGrowthHost.ValidateBuild(new("80bf516c-4284-4284-9ce1-a0462e9664e1"), new("72531e11-98ac-4f77-afd9-b9130e71655c"),
                new("3b2d5dbf-8d4a-4533-81db-d19bce4736a3"), new("333349df-2847-43e8-9a59-edc42cca6673"), new("2c27b4cd-7d63-415f-8516-bec2ab985fc2"));
            throw new Exception("The production adapter accepted the previous audited host build.");
        }
        catch (NotSupportedException) { }
        try { NativeMeshGrowthHost.RequireField(typeof(Device), "_queues", typeof(object)); }
        catch (NotSupportedException) { return; }
        throw new Exception("The production adapter accepted an incorrect private field type.");
    }

    private static void Ordered(List<CodeInstruction> il, params MethodInfo[] methods)
    {
        int previous = -1;
        foreach (var method in methods)
        {
            var matches = il.Select((c, i) => (c, i)).Where(p => p.c.Calls(method)).Select(p => p.i).ToArray();
            Check(matches.Length == 1 && matches[0] > previous, $"Native frame ordering moved or duplicated {method.Name}.");
            previous = matches[0];
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Mesh growth host check failed: " + message);
    }
}
