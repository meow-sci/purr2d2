using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using Purr2D2.Core;

namespace Purr2D2.Rendering;

/// <summary>Native PBR hilt, depth-tested HDR blade/VFX, local light spill and bounded cut sparks.</summary>
public sealed unsafe partial class SaberRenderer : IDisposable
{
    private static SaberRenderer? _instance;
    private readonly Dictionary<int, List<Blade>> _blades = new();
    private readonly List<Spark> _sparks = new();
    private readonly Dictionary<PassKey, PipelinePair> _pipelines = new();
    private StaticMeshRenderable? _hilt;
    private VkPipelineLayout _layout;
    private bool _hasLayout;
    private bool _installed;
    private bool _disposed;
    private bool _hiltFailed;
    private bool _gpuFailed;
    private readonly Dictionary<KittenEva, Emitter> _emitters = new(ReferenceEqualityComparer.Instance);
    private double _now;
    private SaberVfxTuning _lightVfx = SaberVfxTuning.LightDefault;
    private SaberVfxTuning _darkVfx = SaberVfxTuning.DarkDefault;
    private SaberVfxClock _lightClock;
    private SaberVfxClock _darkClock;

    private readonly record struct Blade(float4x4 EmitterToEgo, float Length, float3 Color, float Seed, SaberType Type);
    private readonly record struct Emitter(double4x4 ToBody, float Length, float3 Color);
    private readonly record struct Spark(Part Part, double3 A, double3 B, double3 Normal, double Born);
    private readonly record struct PassKey(VkSampleCountFlags Samples, VkFormat Color, VkFormat Depth);
    private readonly record struct PipelinePair(VkPipeline Light, VkPipeline Dark);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GlowPush
    {
        public float4x4 Mvp;
        public float4 Color;
        public float4 Shape;
        public float4 Animation;
        public float4 Camera;
    }

    public bool HiltReady => _hilt != null;
    public bool GlowReady => _hasLayout && !_gpuFailed;
    public string? LastError { get; private set; }

    /// <summary>Snapshot live preferences on the main thread, outside command recording.</summary>
    public void ConfigureVfx(SaberSettings settings)
    {
        if (_disposed) return;
        _lightVfx = SaberVfxTuning.FromEffects(settings.EffectsFor(SaberType.Light), SaberType.Light);
        _darkVfx = SaberVfxTuning.FromEffects(settings.EffectsFor(SaberType.Dark), SaberType.Dark);
        _slagTuning = DarkSaberSparkTuning.FromEffects(settings.EffectsFor(SaberType.Dark));
    }

    public void Initialize(Harmony harmony)
    {
        if (_installed || _disposed) return;
        _instance = this;
        harmony.Patch(AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.ClearBuckets)),
            postfix: new HarmonyMethod(typeof(SaberRenderer), nameof(AfterClearBuckets)));
        harmony.Patch(AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.RenderTranslucencyPass)),
            postfix: new HarmonyMethod(typeof(SaberRenderer), nameof(AfterTranslucencyPass)));
        harmony.Patch(AccessTools.Method(typeof(Program), nameof(Program.UpdateRenderingResources)),
            prefix: new HarmonyMethod(typeof(SaberRenderer), nameof(BeforeRenderResources)));
        // StarMapUnload occurs after the Vulkan device has gone away. Free before KSA tears it down.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Program), nameof(Program.Dispose)),
            prefix: new HarmonyMethod(typeof(SaberRenderer), nameof(BeforeProgramDispose)));
        _installed = true;
    }

    /// <summary>Call from the main thread outside command recording, once the game is loaded.</summary>
    public void Prepare()
    {
        if (_disposed || Program.Instance == null) return;
        if (_hilt == null && !_hiltFailed)
        {
            try
            {
                var system = Program.Instance.SuperMeshRenderSystem;
                var gltf = system.GltfSystem.GetOrLoad(new AssetName("Purr2D2_Hilt_Glb"));
                var material = system.MaterialSystem.GetOrLoad(new AssetName("Purr2D2_Hilt_Material"));
                for (var i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;
                _hilt = new StaticMeshRenderable(system.MeshRendererStaticPbr,
                    new AssetName("Purr2D2_Hilt_Glb"), system.MeshRendererStaticPrePass);
            }
            catch (Exception ex) { _hiltFailed = true; Report("hilt initialization", ex); }
        }
        if (_gpuFailed) return;
        try
        {
            var renderer = Program.GetRenderer();
            if (!_hasLayout)
            {
                var range = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<GlowPush>(),
                };
                _layout = renderer.Device.CreatePipelineLayout(default(ReadOnlySpan<VkDescriptorSetLayout>),
                    new ReadOnlySpan<VkPushConstantRange>(ref range), null);
                _hasLayout = true;
            }
            foreach (var viewport in ViewportRegistry.Views)
            {
                if (!viewport.Visible || viewport.OffscreenTarget is not { } target) continue;
                var key = Key(target);
                if (_pipelines.ContainsKey(key)) continue;
                var dark = BuildPipeline(renderer, target, SaberType.Dark);
                VkPipeline light = default;
                bool hasLight = false;
                try
                {
                    light = BuildPipeline(renderer, target, SaberType.Light);
                    hasLight = true;
                    _pipelines.Add(key, new PipelinePair(light, dark));
                }
                catch
                {
                    if (hasLight) renderer.Device.DestroyPipeline(light, null);
                    renderer.Device.DestroyPipeline(dark, null);
                    throw;
                }
            }
        }
        catch (Exception ex) { _gpuFailed = true; Report("glow initialization", ex); }
    }

    /// <summary>Publish the last posed emitter in the kitten's COM/body frame for next-frame light submission.</summary>
    public void SetEmitter(KittenEva? kitten, double4x4 emitterToBody, float lengthMeters, float3 color)
    {
        if (_disposed) return;
        if (kitten == null) { ClearEmitters(); return; }
        _emitters[kitten] = new(emitterToBody, float.Clamp(lengthMeters, 0, SaberLimits.MaximumBladeLength), color);
    }

    public void ClearEmitters() => _emitters.Clear();
    public void RemoveEmitter(KittenEva kitten) => _emitters.Remove(kitten);

    /// <summary>Call only in the pose callback, after this viewport's final wrist transform is available.</summary>
    public void DrawSaber(KittenEva kitten, IViewport viewport, float4x4 emitterToEgo,
        float lengthMeters, float3 color, SaberType type)
    {
        if (_disposed || !viewport.Visible) return;
        try
        {
            if (_hilt != null)
            {
                _hilt.Transform = emitterToEgo;
                _hilt.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport));
            }
            if (lengthMeters <= 0.0001f) return;
            if (!_blades.TryGetValue(viewport.ShaderSlot, out var list))
                _blades.Add(viewport.ShaderSlot, list = new List<Blade>(1));
            list.Add(new Blade(emitterToEgo, float.Clamp(lengthMeters, 0, SaberLimits.MaximumBladeLength),
                color, SaberVfx.Seed(kitten.Id), type == SaberType.Dark ? SaberType.Dark : SaberType.Light));
            if (type == SaberType.Dark)
                SubmitSlag(kitten, viewport, emitterToEgo, lengthMeters, color);
        }
        catch (Exception ex) { Report("saber submission", ex); }
    }

    internal void AddCutSparks(Part part, double3 pointLocal, double3 normalLocal, double now)
    {
        if (_disposed || !Finite(pointLocal) || !Finite(normalLocal)) return;
        if (_sparks.Count >= 64) _sparks.RemoveAt(0);
        _sparks.Add(new(part, pointLocal, pointLocal, normalLocal, now));
    }

    /// <summary>Player-time seconds: cooling remains visible while simulation is paused.</summary>
    public void Update(double now)
    {
        UpdateSlag(now);
        _lightClock.Advance(now, _lightVfx.ShimmerSpeed, _lightVfx.ArcFrequency);
        _darkClock.Advance(now, _darkVfx.ShimmerSpeed, _darkVfx.ArcFrequency);
        _now = now;
        for (int i = _sparks.Count - 1; i >= 0; i--)
            if (now - _sparks[i].Born > .7 || _sparks[i].Part.Tree?.OwningVehicle is not { IsDisposed: false }) _sparks.RemoveAt(i);
    }

    public void ClearEffects()
    {
        _sparks.Clear();
        ClearSlag();
    }

    private static PassKey Key(RenderTarget target) => new(target.Samples,
        target.ColorImage?.Format ?? VkFormat.Undefined, target.DepthImage?.Format ?? VkFormat.Undefined);

    private VkPipeline BuildPipeline(Renderer renderer, RenderTarget target, SaberType type)
    {
        bool light = type == SaberType.Light;
        var vertex = ModLibrary.Get<ShaderReference>(light ? "Purr2D2_Light_Vert" : "Purr2D2_Glow_Vert");
        var fragment = ModLibrary.Get<ShaderReference>(light ? "Purr2D2_Light_Frag" : "Purr2D2_Glow_Frag");
        if (vertex?.Shader is not { } vert || fragment?.Shader is not { } frag)
            throw new InvalidOperationException("Purr2D2 glow shader assets did not load");
        var blendAttachment = SaberBlendState.CreateAttachment();
        var blendState = new VkPipelineColorBlendStateCreateInfo
        {
            AttachmentCount = 1,
            Attachments = &blendAttachment,
        };
        Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo
        {
            Name = Presets.Entrypoint.Main, Stage = VkShaderStageFlags.VertexBit, Module = vert,
        };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            Name = Presets.Entrypoint.Main, Stage = VkShaderStageFlags.FragmentBit, Module = frag,
        };
        fixed (VkPipelineShaderStageCreateInfo* stagePtr = stages)
        {
            var info = new VkGraphicsPipelineCreateInfo
            {
                Layout = _layout,
                StageCount = 2,
                Stages = stagePtr,
                DynamicState = renderer.DynamicStateInfo,
                ViewportState = renderer.ViewportState,
                VertexInputState = Presets.VertexInput.None.Ptr,
                InputAssemblyState = Presets.InputAssembly.TriangleStrip,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite,
                ColorBlendState = &blendState,
            };
            // Each view can use a different sample count. Cache by formats+samples, never target extent.
            target.SetupGraphicsPipeline(ref info);
            return renderer.Device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
        }
    }

    private void DrawPass(IViewport viewport, CommandBuffer commands, bool useCustomRenderPass)
    {
        if (_disposed || _gpuFailed || !viewport.Visible
            || !_pipelines.TryGetValue(Key(viewport.OffscreenTarget), out var pair)) return;
        if (_sparks.Count == 0 && _slagCount == 0
            && (!_blades.TryGetValue(viewport.ShaderSlot, out var blades) || blades.Count == 0)) return;

        // The blade has no depth writes. Drawing before atmosphere/cloud composition makes
        // those passes attenuate its light using the distant planet's depth instead of the blade's.
        // KSA normally closes its custom translucency scope before this postfix runs; callers
        // with useCustomRenderPass=false still own an open scope, which we must not nest or end.
        if (!useCustomRenderPass)
        {
            DrawEffects(viewport, commands, pair);
            return;
        }

        var target = viewport.OffscreenTarget;
        if (target.ColorAttachment is not { } color || target.DepthAttachment is not { } depth) return;
        Span<VkImageMemoryBarrier2> images = stackalloc VkImageMemoryBarrier2[2];
        var barriers = new BarrierBatch(images);
        barriers.Add(color, ImageBarrierInfo.Presets.ColorAttachmentReadWrite, inForceBarrier: true);
        barriers.Add(depth, ImageBarrierInfo.Presets.DepthStencilAttachmentReadWrite, inForceBarrier: true);
        barriers.SubmitAndFlush(commands);

        // Load the actual attachments (MSAA when enabled), not their resolved images. Leave
        // resolving to KSA after translucent particles so the blade is included in bloom/AA.
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            ImageView = color.ImageView,
            ResolveMode = VkResolveModeFlags.None,
            LoadOp = VkAttachmentLoadOp.Load,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            ImageView = depth.ImageView,
            ResolveMode = VkResolveModeFlags.None,
            LoadOp = VkAttachmentLoadOp.Load,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var rendering = new VkRenderingInfo
        {
            RenderArea = new VkRect2D(target.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthAttachment = &depthAttachment,
            StencilAttachment = depth.HasStencil ? &depthAttachment : null,
        };
        commands.BeginRendering(in rendering);
        try { DrawEffects(viewport, commands, pair); }
        finally { commands.EndRendering(); }
    }

    private void DrawEffects(IViewport viewport, CommandBuffer commands, PipelinePair pair)
    {
        var camera = viewport.GetCamera();
        var projection = camera.MVP.viewProjection;
        float time = SaberVfx.Clock(_now);
        Program.SetViewport(commands);

        // Sparks deliberately retain the original hot branch, without either style's shimmer.
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pair.Dark);
        DrawSlag(camera, commands, projection);
        bool lightBound = false;
        foreach (var spark in _sparks)
        {
            double age = Math.Max(0, _now - spark.Born);
            if (age > .7 || !TrySparkFrame(spark, camera, out var origin, out _, out var normal)) continue;
            var tangent = double3.Cross(normal, Math.Abs(normal.X) < .8 ? double3.UnitX : double3.UnitY).NormalizeOrZero();
            var bitangent = double3.Cross(normal, tangent);
            for (int i = 0; i < 3; i++)
            {
                double phase = spark.Born * 37 + i * 2.399;
                var velocity = normal * (.4 + .2 * i) + (tangent * Math.Cos(phase) + bitangent * Math.Sin(phase)) * (1 + .35 * i);
                var tip = origin + velocity * age;
                DrawRibbon(commands, projection, tip - velocity * .025, tip, double3.Zero, .018f, .003f,
                    new float4(1, (float)(.65 * (1 - age / .7)), .025f, (float)(9 * Math.Pow(1 - age / .7, 2))),
                    isChar: false, billboard: true);
            }
        }
        if (!_blades.TryGetValue(viewport.ShaderSlot, out var blades)) return;
        foreach (var blade in blades)
        {
            bool light = blade.Type == SaberType.Light;
            var tuning = light ? _lightVfx : _darkVfx;
            var clock = light ? _lightClock : _darkClock;
            float shimmerTime = clock.ShimmerTime;
            if (light != lightBound)
            {
                commands.BindPipeline(VkPipelineBindPoint.Graphics, light ? pair.Light : pair.Dark);
                lightBound = light;
            }
            var start = double3.Unpack(blade.EmitterToEgo.Translation);
            var z = double3.Unpack(blade.EmitterToEgo.Z.XYZ);
            var magnitude = z.Length();
            if (magnitude < 1e-9) continue;
            var end = start + z / magnitude * blade.Length;
            var detail = BladeDetail(viewport, camera, start, end, blade.Length, tuning);
            DrawRibbon(commands, projection, start, end, double3.Zero, 0.065f, 0.009f,
                new float4(blade.Color, 8), isChar: false, billboard: true,
                new float4(shimmerTime, blade.Seed, blade.Length * .5f, detail.ShimmerStrength));
            // A line ribbon is edge-on when looking along the blade. A camera-facing end cap
            // keeps the tip round and luminous from that angle as well.
            DrawBladeTip(commands, projection, end, new float4(blade.Color, 4),
                new float4(shimmerTime, blade.Seed, blade.Length, detail.ShimmerStrength));
            if (detail.ArcCount > 0)
                DrawArcs(commands, projection, blade, start, z / magnitude, time, clock.BurstTime, detail, tuning);
        }
    }

    private static SaberVfxDetail BladeDetail(IViewport viewport, Camera camera, double3 start,
        double3 end, float length, SaberVfxTuning tuning)
    {
        var middle = (start + end) * .5;
        // Includes maximum configured arc displacement/width as well as the blade halo.
        double outsideRadius = Math.Max(.1, .020 + .059 * tuning.ArcAmplitude + .008 * tuning.ArcWidth);
        if (!Finite(middle) || camera.IsSphereOutsideFrustum(middle, length * .5 + outsideRadius)) return default;
        var projection = camera.MVP.viewProjection;
        var a = float4.Transform(new float4(float3.Pack(in start), 1), projection);
        var b = float4.Transform(new float4(float3.Pack(in end), 1), projection);
        var center = float4.Transform(new float4(float3.Pack(in middle), 1), projection);
        if (center.W <= .0001f) return default;

        var extent = viewport.OffscreenTarget.Extent;
        float pixelsPerMeter = Math.Abs(camera.MVP.projection.M22) * extent.Height / (2 * center.W);
        float lengthPixels = 0;
        if (a.W > .0001f && b.W > .0001f)
        {
            float dx = (a.X / a.W - b.X / b.W) * extent.Width * .5f;
            float dy = (a.Y / a.W - b.Y / b.W) * extent.Height * .5f;
            lengthPixels = MathF.Sqrt(dx * dx + dy * dy);
        }
        // Distance to the nearest blade point, so a long blade near the camera stays detailed.
        var axis = (end - start) / length;
        var nearest = start + axis * double.Clamp(-double3.Dot(start, axis), 0, length);
        return SaberVfx.Detail(lengthPixels, .065f * pixelsPerMeter, nearest.Length(), length, tuning);
    }

    private void DrawArcs(CommandBuffer commands, float4x4 projection, Blade blade, double3 start,
        double3 axis, float time, float burstTime, SaberVfxDetail detail, SaberVfxTuning tuning)
    {
        // Remove rig scale/shear. The procedural path stays in the posed saber frame, while
        // its narrow ribbon faces this viewport's camera. No vertex buffer or per-frame arrays.
        var right = double3.Unpack(blade.EmitterToEgo.X.XYZ);
        right -= axis * double3.Dot(right, axis);
        if (right.Length() < 1e-8) right = double3.Cross(axis, double3.UnitY);
        if (right.Length() < 1e-8) right = double3.Cross(axis, double3.UnitX);
        right /= right.Length();
        var up = double3.Cross(axis, right);
        var model = new double4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            axis.X, axis.Y, axis.Z, 0,
            start.X, start.Y, start.Z, 1);
        var cameraLocal = new float3((float)-double3.Dot(start, right), (float)-double3.Dot(start, up),
            (float)-double3.Dot(start, axis));
        var push = new GlowPush
        {
            Mvp = float4x4.Pack(in model) * projection,
            // Arc mode reuses color.a for amplitude and shape.z for the independent burst clock.
            Color = new float4(blade.Color, tuning.ArcAmplitude),
            Shape = new float4(blade.Length, .008f * tuning.ArcWidth, burstTime, 2 + tuning.ArcCount),
            Animation = new float4(time, blade.Seed, detail.Segments, detail.ArcLevel),
            Camera = new float4(cameraLocal, detail.ArcVisibility * 2.5f * tuning.ArcBrightness),
        };
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero, push);
        // Each instance is one contiguous strip: 8 arcs x 10 segments is the hard maximum.
        commands.Draw(2 * (detail.Segments + 1), detail.ArcCount, 0, 0);
    }

    private static bool TrySparkFrame(Spark spark, Camera camera, out double3 a, out double3 b, out double3 normal)
    {
        a = b = normal = double3.Zero;
        if (spark.Part.Tree?.OwningVehicle is not { } vehicle) return false;
        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        var matrix = spark.Part.MatrixAsmb2Ego(in vehicleMatrix);
        a = spark.A.Transform(matrix);
        b = spark.B.Transform(matrix);
        // Normals require inverse-transpose when the authored part has nonuniform scale.
        if (!double4x4.Invert(matrix, out var inverse)) return false;
        normal = double3.TransformNormal(spark.Normal, double4x4.Transpose(inverse));
        var n = normal.Length();
        if (!Finite(a) || !Finite(b) || n < 1e-9) return false;
        normal /= n;
        a += normal * 0.0025;
        b += normal * 0.0025;
        return true;
    }

    private void DrawRibbon(CommandBuffer commands, float4x4 projection, double3 a, double3 b,
        double3 normal, float radius, float core, float4 color, bool isChar, bool billboard,
        float4 animation = default)
    {
        var segment = b - a;
        var length = segment.Length();
        var axis = length > 1e-8 ? segment / length : double3.UnitY;
        var middle = (a + b) * 0.5;
        var side = double3.Cross(axis, billboard ? -middle : normal);
        if (side.Length() < 1e-8) side = double3.Cross(axis, double3.UnitX);
        if (side.Length() < 1e-8) side = double3.Cross(axis, double3.UnitZ);
        side /= side.Length();
        var depth = double3.Cross(side, axis);
        var half = length * 0.5;
        var model = new double4x4(
            side.X * radius, side.Y * radius, side.Z * radius, 0,
            axis.X * (half + radius), axis.Y * (half + radius), axis.Z * (half + radius), 0,
            depth.X, depth.Y, depth.Z, 0,
            middle.X, middle.Y, middle.Z, 1);
        var push = new GlowPush
        {
            Mvp = float4x4.Pack(in model) * projection,
            Color = color,
            Shape = new float4((float)half, radius, core, isChar ? 1 : 0),
            Animation = animation,
        };
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero, push);
        commands.Draw(4, 1, 0, 0);
    }

    private void DrawBladeTip(CommandBuffer commands, float4x4 projection, double3 point, float4 color,
        float4 animation)
    {
        const float radius = 0.065f;
        var towardCamera = point.Length() > 1e-8 ? -point / point.Length() : double3.UnitZ;
        var right = double3.Cross(double3.UnitY, towardCamera);
        if (right.Length() < 1e-8) right = double3.Cross(double3.UnitX, towardCamera);
        right /= right.Length();
        var up = double3.Cross(towardCamera, right);
        var model = new double4x4(
            right.X * radius, right.Y * radius, right.Z * radius, 0,
            up.X * radius, up.Y * radius, up.Z * radius, 0,
            towardCamera.X, towardCamera.Y, towardCamera.Z, 0,
            point.X, point.Y, point.Z, 1);
        var push = new GlowPush
        {
            Mvp = float4x4.Pack(in model) * projection,
            Color = color,
            Shape = new float4(0, radius, 0.009f, 0),
            Animation = animation,
        };
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero, push);
        commands.Draw(4, 1, 0, 0);
    }

    private void SubmitLights()
    {
        if (_disposed || _emitters.Count == 0) return;
        foreach (var viewport in ViewportRegistry.Views)
        {
            if (!viewport.Visible || viewport.LightMode == ViewportLightMode.None) continue;
            var camera = viewport.GetCamera();
            foreach (var pair in _emitters)
            {
                var owner = pair.Key;
                var source = pair.Value;
                if (owner.IsDisposed || source.Length <= 0.0001f) continue;
                var bodyToEgo = double4x4.CreateFromQuaternion(owner.Body2Ego)
                                * double4x4.CreateTranslation(camera.GetPositionEgo(owner));
                var emitter = source.ToBody * bodyToEgo;
                var direction = emitter.Z.XYZ;
                var axisLength = direction.Length();
                if (axisLength <= 1e-9) continue;
                direction /= axisLength;
                int lightCount = Math.Clamp((int)Math.Ceiling(source.Length / 1.5f), 2, 8);
                for (var i = 0; i < lightCount; i++)
                {
                    var point = emitter.Translation + direction * (source.Length * (i + .5) / lightCount);
                    Program.LightSystem.CreateLightInstance(Light.CreatePointLight(point, 3.6f, source.Color,
                        12 * float.Clamp(source.Length / 0.9f, 0, 1)), viewport);
                }
            }
        }
    }

    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private void Report(string operation, Exception error)
    {
        var message = $"purr2d2: {operation} failed: {error.Message}";
        if (LastError != message) Console.WriteLine(message);
        LastError = message;
    }

    private static void AfterClearBuckets(IViewport viewport)
    {
        if (_instance?._blades.TryGetValue(viewport.ShaderSlot, out var list) == true) list.Clear();
    }

    private static void AfterTranslucencyPass(IViewport viewport, CommandBuffer commandBuffer, bool useCustomRenderPass)
    {
        if (_instance is not { } self) return;
        try { self.DrawPass(viewport, commandBuffer, useCustomRenderPass); }
        catch (Exception ex) { self._gpuFailed = true; self.Report("glow rendering", ex); }
    }

    private static void BeforeRenderResources()
    {
        if (_instance is not { } self) return;
        try { self.SubmitLights(); }
        catch (Exception ex) { self.Report("light submission", ex); }
    }

    private static void BeforeProgramDispose() => _instance?.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_instance, this)) _instance = null;
        _emitters.Clear();
        _blades.Clear();
        _sparks.Clear();
        ClearSlag();
        // Initialization can fail before the host renderer exists. In that case there is
        // nothing GPU-owned to free, and looking up the device would itself be unsafe.
        if (!_hasLayout && _hilt == null && _pipelines.Count == 0) return;
        // Called outside recording, or before Program.Dispose destroys SuperMesh/the device.
        var device = Program.GetRenderer().Device;
        device.WaitIdle();
        _hilt?.Dispose();
        _hilt = null;
        foreach (var pair in _pipelines.Values)
        {
            device.DestroyPipeline(pair.Light, null);
            device.DestroyPipeline(pair.Dark, null);
        }
        _pipelines.Clear();
        if (_hasLayout) device.DestroyPipelineLayout(_layout, null);
        _hasLayout = false;
    }
}
