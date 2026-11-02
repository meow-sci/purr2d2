using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering;
using KSA.Rendering.Water.Rendering;
using Purr2D2.Core;
using RenderCore;

namespace Purr2D2.Rendering;

public sealed unsafe partial class SaberRenderer
{
    private static readonly FieldInfo? OceanTargetField=OceanField("_target",typeof(RenderTarget));
    private static readonly FieldInfo? OceanDepthCopyField=OceanField("_depthCopyTarget",typeof(RenderTarget));
    private static readonly FieldInfo? OceanSharedLayoutField=OceanField("_sharedOceanDataDescriptorSetLayout",typeof(DescriptorSetLayoutEx));
    private static readonly FieldInfo? OceanIndividualLayoutField=OceanField("_oceanDataDescriptorSetLayout",typeof(DescriptorSetLayoutEx));
    private static readonly FieldInfo? OceanSharedSetField=OceanField("_sharedDataDescriptorSet",typeof(VkDescriptorSet));
    private readonly SaberWaterFrameGate _waterGate=new();
    private bool _waterHooksReady,_waterFailed,_waterReady,_drawingWater,_hasWaterLayout,_waterBufferReady,_waterMemoryReady;
    private VkPipelineLayout _waterLayout;
    private DescriptorSetLayoutEx? _waterOwnLayout;
    private DescriptorPoolEx? _waterPool;
    private VkSampler _waterSampler;
    private bool _hasWaterSampler;
    private BufferEx _waterBuffer;
    private MappedMemory _waterMemory;
    private ByteSize _waterStride;
    private VkDescriptorSet[] _waterSets=[];
    private VkShaderModule? _waterDarkFragment,_waterLightFragment;
    private nint _waterSharedLayoutHandle,_waterIndividualLayoutHandle,_waterGlobalLayoutHandle;
    private readonly Dictionary<ResolvedWaterKey,WaterPipelines> _waterPipelines=[];
    private WaterFrame? _waterFrame;

    private readonly record struct ResolvedWaterKey(VkFormat Color,VkFormat Depth,bool Stencil);
    private readonly record struct WaterPipelines(PipelinePair Water,PipelinePair Fallback);
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    private struct WaterUniform { public float4 Values; }
    private sealed record WaterFrame(OceanRenderer Ocean,Celestial Planet,OceanRenderData Data,
        IViewport Viewport,RenderTarget Target,RenderImage Opaque,RenderImage Color,RenderImage Depth,
        VkDescriptorSet GlobalSet,VkDescriptorSet SharedSet,VkDescriptorSet IndividualSet,int FrameIndex);

    private static FieldInfo? OceanField(string name,Type type)
    {
        var field=typeof(OceanRenderer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
        return field?.FieldType == type ? field : null;
    }

    private void InstallWater(Harmony harmony)
    {
        try
        {
            if (new[]{OceanTargetField,OceanDepthCopyField,OceanSharedLayoutField,OceanIndividualLayoutField,OceanSharedSetField}.Any(f=>f is null))
                throw new MissingMemberException("The native ocean descriptor contract changed.");
            var copy=AccessTools.DeclaredMethod(typeof(OceanRenderer),"PerformScreenCopy",[typeof(CommandBuffer),typeof(IViewport)])
                ?? throw new MissingMethodException("The native opaque ocean copy changed.");
            var late=AccessTools.DeclaredMethod(typeof(SunbloomRenderer),nameof(SunbloomRenderer.Render),[typeof(CommandBuffer),typeof(IViewport),typeof(int)])
                ?? throw new MissingMethodException("The resolved pre-bloom rendering seam changed.");
            harmony.Patch(copy,postfix:new HarmonyMethod(typeof(SaberRenderer),nameof(AfterOceanScreenCopy)));
            harmony.Patch(late,prefix:new HarmonyMethod(typeof(SaberRenderer),nameof(BeforeWaterBloom)));
            _waterHooksReady=true;
        }
        catch(Exception ex) { _waterFailed=true; Report("water rendering unavailable",ex); }
    }

    private void PrepareWater()
    {
        if (!_waterHooksReady || _waterFailed || _disposed || _gpuFailed || !_hasLayout) return;
        try
        {
            var renderer=Program.GetRenderer(); var ocean=Program.GetOceanRenderer();
            if (ocean is null || OceanSharedLayoutField?.GetValue(ocean) is not DescriptorSetLayoutEx shared
                || OceanIndividualLayoutField?.GetValue(ocean) is not DescriptorSetLayoutEx individual) return;
            if (_waterOwnLayout is not null && (_waterSharedLayoutHandle != shared.VkHandle
                || _waterIndividualLayoutHandle != individual.VkHandle
                || _waterGlobalLayoutHandle != GlobalShaderBindings.DescriptorSetLayout.VkHandle
                || _waterSets.Length != renderer.MaxFramesInFlight))
            {
                // Native rebuilds happen outside recording. Retire only our resources here;
                // image/set handles themselves are always read live at the actual copy.
                renderer.Device.WaitIdle(); ReleaseWaterResources(renderer.Device);
            }
            if (_waterOwnLayout is null) CreateWaterResources(renderer,shared,individual);
            var target=Program.MainViewport.OffscreenTarget;
            if (target.ColorImage is not { } color || target.DepthImage is not { } depth) return;
            var key=new ResolvedWaterKey(color.Format,depth.Format,depth.HasStencil);
            if (!_waterPipelines.ContainsKey(key))
            {
                PipelinePair water=default,fallback=default;
                try
                {
                    water=CreateResolvedPair(renderer,key,water:true);
                    fallback=CreateResolvedPair(renderer,key,water:false);
                    _waterPipelines.Add(key,new(water,fallback));
                }
                catch
                {
                    DestroyPair(renderer.Device,water); DestroyPair(renderer.Device,fallback); throw;
                }
            }
            _waterReady=true;
        }
        catch(Exception ex)
        {
            _waterReady=false; _waterFailed=true;
            try
            {
                var device=Program.GetRenderer().Device;
                device.WaitIdle(); ReleaseWaterResources(device);
            }
            catch(Exception cleanup) { Report("water resource cleanup",cleanup); }
            Report("water rendering unavailable",ex);
        }
    }

    private void CreateWaterResources(Renderer renderer,DescriptorSetLayoutEx shared,DescriptorSetLayoutEx individual)
    {
        var device=renderer.Device;
        Span<VkDescriptorSetLayoutBinding> bindings=stackalloc VkDescriptorSetLayoutBinding[2];
        bindings[0]=new(){Binding=0,DescriptorType=VkDescriptorType.CombinedImageSampler,DescriptorCount=1,StageFlags=VkShaderStageFlags.FragmentBit};
        bindings[1]=new(){Binding=1,DescriptorType=VkDescriptorType.UniformBuffer,DescriptorCount=1,StageFlags=VkShaderStageFlags.FragmentBit};
        _waterOwnLayout=device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo{Bindings=bindings},null);
        int count=renderer.MaxFramesInFlight;
        Span<VkDescriptorPoolSize> sizes=stackalloc VkDescriptorPoolSize[2];
        sizes[0]=new(){Type=VkDescriptorType.CombinedImageSampler,DescriptorCount=count};
        sizes[1]=new(){Type=VkDescriptorType.UniformBuffer,DescriptorCount=count};
        _waterPool=device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo{MaxSets=count,PoolSizes=sizes},null);
        _waterSets=new VkDescriptorSet[count];
        device.AllocateDescriptorSets(_waterPool,_waterOwnLayout,_waterSets);
        _waterSampler=device.CreateSampler(Presets.Sampler.SamplerPointClamped,null); _hasWaterSampler=true;
        _waterStride=ByteSize.Of<WaterUniform>().AlignTo(Utils.MinUniformBufferOffsetAlignment);
        _waterBuffer=renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name="Purr2D2.WaterView",BufferSize=_waterStride*count,BufferUsage=VkBufferUsageFlags.UniformBufferBit,
            AllocRequiredProperties=VkMemoryPropertyFlags.HostVisibleBit|VkMemoryPropertyFlags.HostCoherentBit
        });
        _waterBufferReady=true;
        _waterMemory=_waterBuffer.Map(); _waterMemoryReady=true;
        Span<VkDescriptorSetLayout> layouts=stackalloc VkDescriptorSetLayout[4];
        layouts[0]=GlobalShaderBindings.DescriptorSetLayout; layouts[1]=_waterOwnLayout;
        layouts[2]=shared; layouts[3]=individual;
        var range=new VkPushConstantRange{StageFlags=VkShaderStageFlags.VertexBit|VkShaderStageFlags.FragmentBit,Offset=ByteSize.Zero,Size=ByteSize.Of<GlowPush>()};
        _waterLayout=device.CreatePipelineLayout(layouts,new ReadOnlySpan<VkPushConstantRange>(ref range),null); _hasWaterLayout=true;
        _waterSharedLayoutHandle=shared.VkHandle; _waterIndividualLayoutHandle=individual.VkHandle;
        _waterGlobalLayoutHandle=GlobalShaderBindings.DescriptorSetLayout.VkHandle;
        // ShaderC's real native-relative resolver uses this installed debug filename.
        // Our includes are flattened first; no CWD or installation-relative traversal assumptions.
        var native=ModLibrary.Get<ShaderReference>("UnderwaterPostProcessCompute");
        var own=ModLibrary.Get<ShaderReference>("Purr2D2_Glow_Frag");
        if (native is null || own is null || !File.Exists(Path.Combine(Path.GetDirectoryName(native.ModPath)!,"OceanFunctions.glsl")))
            throw new InvalidDataException("The installed native water shader source is unavailable.");
        string root=Path.GetDirectoryName(own.ModPath)!;
        _waterDarkFragment=CompileWater("SaberWaterGlow.frag");
        _waterLightFragment=CompileWater("SaberWaterLight.frag");
        VkShaderModule CompileWater(string name) => ShaderModuleUtils.FromString(device,
            Encoding.UTF8.GetBytes(SaberWaterShaderSource.Expand(Path.Combine(root,name),root)),VkShaderStageFlags.FragmentBit,
            debugName:Encoding.UTF8.GetBytes(native.ModPath+"\0"));
    }

    private PipelinePair CreateResolvedPair(Renderer renderer,ResolvedWaterKey key,bool water)
    {
        var dark=CreateResolvedPipeline(renderer,key,SaberType.Dark,water);
        try { return new(CreateResolvedPipeline(renderer,key,SaberType.Light,water),dark); }
        catch { renderer.Device.DestroyPipeline(dark,null); throw; }
    }

    private VkPipeline CreateResolvedPipeline(Renderer renderer,ResolvedWaterKey key,SaberType type,bool water)
    {
        bool light=type==SaberType.Light;
        var vertex=ModLibrary.Get<ShaderReference>(light?"Purr2D2_Light_Vert":"Purr2D2_Glow_Vert")?.Shader
            ?? throw new InvalidOperationException("Saber vertex shader is unavailable.");
        var fragment=water ? (light?_waterLightFragment:_waterDarkFragment)
            : ModLibrary.Get<ShaderReference>(light?"Purr2D2_Light_Frag":"Purr2D2_Glow_Frag")?.Shader;
        if (!fragment.HasValue) throw new InvalidOperationException("Saber fragment shader is unavailable.");
        Span<VkPipelineShaderStageCreateInfo> stages=stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0]=new(){Name=Presets.Entrypoint.Main,Stage=VkShaderStageFlags.VertexBit,Module=vertex};
        stages[1]=new(){Name=Presets.Entrypoint.Main,Stage=VkShaderStageFlags.FragmentBit,Module=fragment.Value};
        var attachment=SaberBlendState.CreateAttachment();
        var blend=new VkPipelineColorBlendStateCreateInfo{AttachmentCount=1,Attachments=&attachment};
        var samples=new VkPipelineMultisampleStateCreateInfo{RasterizationSamples=VkSampleCountFlags._1Bit};
        VkFormat colorFormat=key.Color;
        var rendering=new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount=1,ColorAttachmentFormats=&colorFormat,
            DepthAttachmentFormat=water?VkFormat.Undefined:key.Depth,
            StencilAttachmentFormat=!water&&key.Stencil?key.Depth:VkFormat.Undefined
        };
        var noDepth=new VkPipelineDepthStencilStateCreateInfo{DepthTestEnable=false,DepthWriteEnable=false,StencilTestEnable=false};
        fixed(VkPipelineShaderStageCreateInfo* stagePtr=stages)
        {
            var info=new VkGraphicsPipelineCreateInfo
            {
                Next=&rendering,Layout=water?_waterLayout:_layout,StageCount=2,Stages=stagePtr,
                DynamicState=renderer.DynamicStateInfo,ViewportState=renderer.ViewportState,
                VertexInputState=Presets.VertexInput.None.Ptr,InputAssemblyState=Presets.InputAssembly.TriangleStrip,
                RasterizationState=Presets.Rasterization.Fill.CullNone,MultisampleState=&samples,
                DepthStencilState=water?&noDepth:RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite,
                ColorBlendState=&blend
            };
            // Explicit resolved1x output; target.SetupGraphicsPipeline would reintroduce native MSAA.
            return renderer.Device.CreateGraphicsPipeline(default(VkPipelineCache),info,null);
        }
    }

    private void CaptureWater(OceanRenderer ocean,CommandBuffer command,IViewport viewport)
    {
        if (!_waterReady || _waterFailed || _disposed || Program.EditorFlag || !viewport.Visible
            || !ReferenceEquals(viewport,Program.MainViewport) || !ReferenceEquals(viewport,Program.RenderedViewport)
            || !_blades.TryGetValue(viewport.ShaderSlot,out var blades) || blades.Count==0
            || OceanTargetField?.GetValue(ocean) is not RenderTarget target || !ReferenceEquals(target,viewport.OffscreenTarget)
            || OceanDepthCopyField?.GetValue(ocean) is not RenderTarget copy
            || target.ColorImage is not { } color || target.DepthImage is not { } depth || copy.ColorImage is not { } opaque
            || opaque.Format!=VkFormat.R32SFloat || opaque.Width!=color.Width || opaque.Height!=color.Height
            || depth.Width!=color.Width || depth.Height!=color.Height || !WaterLayoutsMatch(ocean)) return;
        int frame=Program.Instance.ResourceFrameIndex;
        var planet=viewport.GetCamera().NearbyCelestial;
        if (frame<0 || frame>=_waterSets.Length || planet is null
            || !ocean._celestialToOceanRenderData.TryGetValue(planet.Hash,out var data)
            || frame>=data.OceanDataDescriptorSets.Length || OceanSharedSetField?.GetValue(ocean) is not VkDescriptorSet shared) return;
        double altitude=viewport.GetCamera().GetPositionEgo(planet).Length()-data.OceanRadius;
        float scale=ocean.UnderwaterExtinctionScale;
        if (!double.IsFinite(altitude) || !float.IsFinite((float)altitude) || !float.IsFinite(scale)
            || !double.IsFinite(data.MaxDisplacement) || data.MaxDisplacement<0) return;
        var key=new ResolvedWaterKey(color.Format,depth.Format,depth.HasStencil);
        if (!_waterPipelines.ContainsKey(key)) return;
        var buffer=new VkDescriptorBufferInfo{Buffer=_waterBuffer.VkBuffer,Offset=_waterStride*frame,Range=ByteSize.Of<WaterUniform>()};
        var image=new VkDescriptorImageInfo{Sampler=_waterSampler,ImageView=depth.ImageView,ImageLayout=VkImageLayout.DepthStencilReadOnlyOptimal};
        Span<VkWriteDescriptorSet> writes=stackalloc VkWriteDescriptorSet[2];
        writes[0]=new(){DstSet=_waterSets[frame],DstBinding=0,DescriptorType=VkDescriptorType.CombinedImageSampler,DescriptorCount=1,ImageInfo=&image};
        writes[1]=new(){DstSet=_waterSets[frame],DstBinding=1,DescriptorType=VkDescriptorType.UniformBuffer,DescriptorCount=1,BufferInfo=&buffer};
        Program.GetRenderer().Device.UpdateDescriptorSets(writes,default(ReadOnlySpan<VkCopyDescriptorSet>));
        _waterMemory.Offset(_waterStride*frame).As<WaterUniform>()=new(){Values=new((float)altitude,Math.Max(0,scale),altitude>data.MaxDisplacement+2?1:0,0)};
        _waterFrame=new(ocean,planet,data,viewport,target,opaque,color,depth,
            GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot),shared,data.OceanDataDescriptorSets[frame],frame);
        _waterGate.Capture(Program.FrameNumber,viewport,target,command.VkHandle,color.ImageView.VkHandle,depth.ImageView.VkHandle,opaque.ImageView.VkHandle);
    }

    private bool WaterLayoutsMatch(OceanRenderer ocean) =>
        OceanSharedLayoutField?.GetValue(ocean) is DescriptorSetLayoutEx shared && shared.VkHandle==_waterSharedLayoutHandle
        && OceanIndividualLayoutField?.GetValue(ocean) is DescriptorSetLayoutEx individual && individual.VkHandle==_waterIndividualLayoutHandle
        && GlobalShaderBindings.DescriptorSetLayout.VkHandle==_waterGlobalLayoutHandle;

    private bool CurrentWaterFrame(IViewport viewport,CommandBuffer command,out WaterFrame frame)
    {
        frame=_waterFrame!;
        return _waterReady && !_waterFailed && frame is not null && ReferenceEquals(viewport,Program.MainViewport)
            && ReferenceEquals(viewport,Program.RenderedViewport) && ReferenceEquals(frame.Target,viewport.OffscreenTarget)
            && ReferenceEquals(frame.Target.ColorImage,frame.Color) && ReferenceEquals(frame.Target.DepthImage,frame.Depth)
            && OceanDepthCopyField?.GetValue(frame.Ocean) is RenderTarget copy && ReferenceEquals(copy.ColorImage,frame.Opaque)
            && OceanSharedSetField?.GetValue(frame.Ocean) is VkDescriptorSet shared && shared.Equals(frame.SharedSet)
            && WaterLayoutsMatch(frame.Ocean) && Program.Instance.ResourceFrameIndex==frame.FrameIndex
            && _waterGate.Matches(Program.FrameNumber,viewport,frame.Target,command.VkHandle,
                frame.Color.ImageView.VkHandle,frame.Depth.ImageView.VkHandle,frame.Opaque.ImageView.VkHandle);
    }

    private bool TryDeferWaterBlades(IViewport viewport,CommandBuffer command)
    {
        if (!CurrentWaterFrame(viewport,command,out var frame)) return false;
        return _waterGate.TryDefer(Program.FrameNumber,viewport,frame.Target,command.VkHandle,
            frame.Color.ImageView.VkHandle,frame.Depth.ImageView.VkHandle,frame.Opaque.ImageView.VkHandle);
    }

    private void DrawWaterLate(CommandBuffer command,IViewport viewport,int frameIndex)
    {
        if (!_waterGate.HasPending(Program.FrameNumber,viewport,command.VkHandle) || _disposed) return;
        bool water=CurrentWaterFrame(viewport,command,out var frame) && frame.FrameIndex==frameIndex;
        var target=viewport.OffscreenTarget;
        if (target.ColorImage is not { } color || target.DepthImage is not { } depth
            || !_waterPipelines.TryGetValue(new(color.Format,depth.Format,depth.HasStencil),out var pipelines))
        { _waterGate.ConsumePending(Program.FrameNumber,viewport,command.VkHandle); return; }
        // Consume before recording. A partial failure must never replay additive light.
        if (!_waterGate.ConsumePending(Program.FrameNumber,viewport,command.VkHandle)) return;
        Span<VkImageMemoryBarrier2> images=stackalloc VkImageMemoryBarrier2[3];
        var barriers=new BarrierBatch(images);
        barriers.Add(color,ImageBarrierInfo.Presets.ColorAttachmentReadWrite,inForceBarrier:true);
        barriers.Add(depth,water?ImageBarrierInfo.Presets.DepthSampledReadF:ImageBarrierInfo.Presets.DepthStencilAttachmentReadF,inForceBarrier:true);
        if (water) barriers.Add(frame.Opaque,ImageBarrierInfo.Presets.StorageReadFc,inForceBarrier:true);
        barriers.SubmitAndFlush(command);
        var colorInfo=new VkRenderingAttachmentInfo{ImageView=color.ImageView,ImageLayout=VkImageLayout.ColorAttachmentOptimal,
            LoadOp=VkAttachmentLoadOp.Load,StoreOp=VkAttachmentStoreOp.Store,ResolveMode=VkResolveModeFlags.None};
        var depthInfo=new VkRenderingAttachmentInfo{ImageView=depth.ImageView,ImageLayout=VkImageLayout.DepthStencilReadOnlyOptimal,
            LoadOp=VkAttachmentLoadOp.Load,StoreOp=VkAttachmentStoreOp.Store,ResolveMode=VkResolveModeFlags.None};
        var rendering=new VkRenderingInfo{RenderArea=new VkRect2D(target.Extent),LayerCount=1,ColorAttachmentCount=1,ColorAttachments=&colorInfo,
            DepthAttachment=water?null:&depthInfo,StencilAttachment=!water&&depth.HasStencil?&depthInfo:null};
        command.BeginRendering(in rendering);
        try
        {
            _drawingWater=water; Program.SetViewport(command);
            if (water)
            {
                Span<VkDescriptorSet> sets=stackalloc VkDescriptorSet[4];
                sets[0]=frame.GlobalSet; sets[1]=_waterSets[frameIndex]; sets[2]=frame.SharedSet; sets[3]=frame.IndividualSet;
                ByteSize32 offset=frameIndex*OceanRenderer.DynamicOceanDataStride;
                command.BindDescriptorSets(VkPipelineBindPoint.Graphics,_waterLayout,0,sets,new Span<ByteSize32>(ref offset));
            }
            DrawBlades(viewport,command,water?pipelines.Water:pipelines.Fallback);
        }
        finally
        {
            _drawingWater=false; command.EndRendering();
            barriers.Add(color,ImageBarrierInfo.Presets.StorageReadFc,inForceBarrier:true);
            barriers.Add(depth,ImageBarrierInfo.Presets.DepthSampledReadVfc,inForceBarrier:true);
            barriers.SubmitAndFlush(command);
        }
    }

    private void ResetWaterFrame() { _waterGate.Reset(); _waterFrame=null; }
    private static void AfterOceanScreenCopy(OceanRenderer __instance,CommandBuffer inCommandBuffer,IViewport inViewport,bool __runOriginal)
    {
        if (!__runOriginal || _instance is not { } self) return;
        try { self.CaptureWater(__instance,inCommandBuffer,inViewport); }
        catch(Exception ex) { self.ResetWaterFrame(); self.Report("water copy context",ex); }
    }
    private static void BeforeWaterBloom(CommandBuffer commandBuffer,IViewport viewport,int frameIndex)
    {
        if (_instance is not { } self) return;
        try { self.DrawWaterLate(commandBuffer,viewport,frameIndex); }
        catch(Exception ex) { self._waterFailed=true; self.ResetWaterFrame(); self.Report("water blade rendering",ex); }
    }
    private static void DestroyPair(Device device,PipelinePair pair)
    {
        if (!pair.Light.IsNull()) device.DestroyPipeline(pair.Light,null);
        if (!pair.Dark.IsNull()) device.DestroyPipeline(pair.Dark,null);
    }
    private void ReleaseWaterResources(Device device)
    {
        ResetWaterFrame(); _waterReady=false;
        foreach(var pair in _waterPipelines.Values) { DestroyPair(device,pair.Water); DestroyPair(device,pair.Fallback); }
        _waterPipelines.Clear();
        if (_waterDarkFragment is { } dark) device.DestroyShaderModule(dark,null);
        if (_waterLightFragment is { } light) device.DestroyShaderModule(light,null);
        _waterDarkFragment=_waterLightFragment=null;
        if (_hasWaterLayout) device.DestroyPipelineLayout(_waterLayout,null); _hasWaterLayout=false;
        _waterPool?.Dispose(); _waterPool=null; _waterOwnLayout?.Dispose(); _waterOwnLayout=null;
        if (_hasWaterSampler) device.DestroySampler(_waterSampler,null); _hasWaterSampler=false;
        if (_waterMemoryReady) { _waterMemory.Dispose(); _waterMemoryReady=false; }
        if (_waterBufferReady) { _waterBuffer.Dispose(); _waterBufferReady=false; }
        _waterSets=[];
    }
}
