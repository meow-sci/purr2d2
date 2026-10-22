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

namespace Purr2D2.Rendering;

/// <summary>Native PBR hilt, depth-tested HDR blade, local light spill and part-anchored cut scars.</summary>
public sealed unsafe class SaberRenderer : IDisposable
{
    private const int MaxScars = 384;
    private const double ScarLifetime = 180;
    private static SaberRenderer? _instance;
    private readonly Dictionary<int, List<Blade>> _blades = new();
    private readonly List<Scar> _scars = new();
    private readonly Dictionary<PassKey, PipelinePair> _pipelines = new();
    private StaticMeshRenderable? _hilt;
    private VkPipelineLayout _layout;
    private bool _hasLayout;
    private bool _installed;
    private bool _disposed;
    private bool _hiltFailed;
    private bool _gpuFailed;
    private KittenEva? _emitterOwner;
    private double4x4 _emitterToBody;
    private float _length;
    private float3 _color;
    private double _now;

    private readonly record struct Blade(float4x4 EmitterToEgo, float Length, float3 Color);
    private readonly record struct Scar(Part Part, double3 A, double3 B, double3 Normal, double Born);
    private readonly record struct PassKey(VkSampleCountFlags Samples, VkFormat Color, VkFormat Depth);
    private readonly record struct PipelinePair(VkPipeline Glow, VkPipeline Char);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GlowPush
    {
        public float4x4 Mvp;
        public float4 Color;
        public float4 Shape;
    }

    public int ScarCount => _scars.Count;
    public bool HiltReady => _hilt != null;
    public bool GlowReady => _hasLayout && !_gpuFailed;
    public string? LastError { get; private set; }

    public void Initialize(Harmony harmony)
    {
        if (_installed || _disposed) return;
        _instance = this;
        harmony.Patch(AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.ClearBuckets)),
            postfix: new HarmonyMethod(typeof(SaberRenderer), nameof(AfterClearBuckets)));
        harmony.Patch(AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.RenderMainPass)),
            postfix: new HarmonyMethod(typeof(SaberRenderer), nameof(AfterMainPass)));
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
                var glow = BuildPipeline(renderer, target, additive: true);
                try { _pipelines.Add(key, new PipelinePair(glow, BuildPipeline(renderer, target, additive: false))); }
                catch { renderer.Device.DestroyPipeline(glow, null); throw; }
            }
        }
        catch (Exception ex) { _gpuFailed = true; Report("glow initialization", ex); }
    }

    /// <summary>Publish the last posed emitter in the kitten's COM/body frame for next-frame light submission.</summary>
    public void SetEmitter(KittenEva? kitten, double4x4 emitterToBody, float lengthMeters, float3 color)
    {
        _emitterOwner = kitten;
        _emitterToBody = emitterToBody;
        _length = float.Clamp(lengthMeters, 0, 2);
        _color = color;
    }

    /// <summary>Call only in the pose callback, after this viewport's final wrist transform is available.</summary>
    public void DrawSaber(KittenEva kitten, IViewport viewport, float4x4 emitterToEgo,
        float lengthMeters, float3 color)
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
            list.Add(new Blade(emitterToEgo, float.Clamp(lengthMeters, 0, 2), color));
        }
        catch (Exception ex) { Report("saber submission", ex); }
    }

    public void AddScar(Part part, double3 aLocal, double3 bLocal, double3 normalLocal, double now)
    {
        if (_disposed || !Finite(aLocal) || !Finite(bLocal) || !Finite(normalLocal)) return;
        if (_scars.Count == MaxScars) _scars.RemoveAt(0);
        _scars.Add(new Scar(part, aLocal, bLocal, normalLocal, now));
    }

    /// <summary>Player-time seconds: cooling remains visible while simulation is paused.</summary>
    public void Update(double now)
    {
        _now = now;
        _scars.RemoveAll(s => now - s.Born > ScarLifetime || s.Part.Tree?.OwningVehicle == null);
    }

    public void ClearScars() => _scars.Clear();

    private static PassKey Key(RenderTarget target) => new(target.Samples,
        target.ColorImage?.Format ?? VkFormat.Undefined, target.DepthImage?.Format ?? VkFormat.Undefined);

    private VkPipeline BuildPipeline(Renderer renderer, RenderTarget target, bool additive)
    {
        var vertex = ModLibrary.Get<ShaderReference>("Purr2D2_Glow_Vert");
        var fragment = ModLibrary.Get<ShaderReference>("Purr2D2_Glow_Frag");
        if (vertex?.Shader is not { } vert || fragment?.Shader is not { } frag)
            throw new InvalidOperationException("Purr2D2 glow shader assets did not load");
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
                ColorBlendState = additive ? RenderingPresets.BlendState.BlendAdditive : RenderingPresets.BlendState.BlendColorAlphaOver,
            };
            // Each view can use a different sample count. Cache by formats+samples, never target extent.
            target.SetupGraphicsPipeline(ref info);
            return renderer.Device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
        }
    }

    private void DrawPass(IViewport viewport, CommandBuffer commands)
    {
        if (_disposed || _gpuFailed || !_pipelines.TryGetValue(Key(viewport.OffscreenTarget), out var pair)) return;
        var camera = viewport.GetCamera();
        var projection = camera.MVP.viewProjection;
        Program.SetViewport(commands);

        if (_scars.Count > 0)
        {
            commands.BindPipeline(VkPipelineBindPoint.Graphics, pair.Char);
            foreach (var scar in _scars)
                if (TryScarFrame(scar, camera, out var a, out var b, out var normal))
                    DrawRibbon(commands, projection, a, b, normal, 0.026f, 0.007f,
                        new float4(0.018f, 0.012f, 0.01f, 0.88f), isChar: true, billboard: false);
        }
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pair.Glow);
        foreach (var scar in _scars)
        {
            var age = Math.Max(0, _now - scar.Born);
            if (age > 18 || !TryScarFrame(scar, camera, out var a, out var b, out var normal)) continue;
            var brightness = (float)(7 * Math.Exp(-age / 3.5));
            var green = (float)Math.Max(0.04, 0.48 * Math.Exp(-age / 4));
            DrawRibbon(commands, projection, a, b, normal, 0.023f, 0.0055f,
                new float4(1, green, 0.015f, brightness), isChar: false, billboard: false);
        }
        if (!_blades.TryGetValue(viewport.ShaderSlot, out var blades)) return;
        foreach (var blade in blades)
        {
            var start = double3.Unpack(blade.EmitterToEgo.Translation);
            var z = double3.Unpack(blade.EmitterToEgo.Z.XYZ);
            var magnitude = z.Length();
            if (magnitude < 1e-9) continue;
            var end = start + z / magnitude * blade.Length;
            DrawRibbon(commands, projection, start, end, double3.Zero, 0.065f, 0.009f,
                new float4(blade.Color, 8), isChar: false, billboard: true);
            // A line ribbon is edge-on when looking along the blade. A camera-facing end cap
            // keeps the tip round and luminous from that angle as well.
            DrawBladeTip(commands, projection, end, new float4(blade.Color, 4));
        }
    }

    private static bool TryScarFrame(Scar scar, Camera camera, out double3 a, out double3 b, out double3 normal)
    {
        a = b = normal = double3.Zero;
        if (scar.Part.Tree?.OwningVehicle is not { } vehicle) return false;
        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        var matrix = scar.Part.MatrixAsmb2Ego(in vehicleMatrix);
        a = scar.A.Transform(matrix);
        b = scar.B.Transform(matrix);
        // Normals require inverse-transpose when the authored part has nonuniform scale.
        if (!double4x4.Invert(matrix, out var inverse)) return false;
        normal = double3.TransformNormal(scar.Normal, double4x4.Transpose(inverse));
        var n = normal.Length();
        if (!Finite(a) || !Finite(b) || n < 1e-9) return false;
        normal /= n;
        a += normal * 0.0025;
        b += normal * 0.0025;
        return true;
    }

    private void DrawRibbon(CommandBuffer commands, float4x4 projection, double3 a, double3 b,
        double3 normal, float radius, float core, float4 color, bool isChar, bool billboard)
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
        };
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero, push);
        commands.Draw(4, 1, 0, 0);
    }

    private void DrawBladeTip(CommandBuffer commands, float4x4 projection, double3 point, float4 color)
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
        };
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            ByteSize.Zero, push);
        commands.Draw(4, 1, 0, 0);
    }

    private void SubmitLights()
    {
        if (_disposed || _emitterOwner is not { IsDisposed: false } owner || _length <= 0.0001f) return;
        foreach (var viewport in ViewportRegistry.Views)
        {
            if (!viewport.Visible || viewport.LightMode == ViewportLightMode.None) continue;
            var camera = viewport.GetCamera();
            var bodyToEgo = double4x4.CreateFromQuaternion(owner.Body2Ego)
                            * double4x4.CreateTranslation(camera.GetPositionEgo(owner));
            var emitter = _emitterToBody * bodyToEgo;
            var direction = emitter.Z.XYZ;
            var axisLength = direction.Length();
            if (axisLength <= 1e-9) continue;
            direction /= axisLength;
            for (var i = 0; i < 2; i++)
            {
                var point = emitter.Translation + direction * (_length * (i == 0 ? 0.25 : 0.75));
                Program.LightSystem.CreateLightInstance(Light.CreatePointLight(point, 3.6f, _color,
                    12 * float.Clamp(_length / 0.9f, 0, 1)), viewport);
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

    private static void AfterMainPass(IViewport viewport, CommandBuffer commandBuffer)
    {
        if (_instance is not { } self) return;
        try { self.DrawPass(viewport, commandBuffer); }
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
        _emitterOwner = null;
        _blades.Clear();
        _scars.Clear();
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
            device.DestroyPipeline(pair.Glow, null);
            device.DestroyPipeline(pair.Char, null);
        }
        _pipelines.Clear();
        if (_hasLayout) device.DestroyPipelineLayout(_layout, null);
        _hasLayout = false;
    }
}
