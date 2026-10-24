using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Linq;
using Brutal;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Deformation;
using Purr2D2.Core;
using RenderCore;

namespace Purr2D2.Rendering;

internal sealed record PreparedFragment(PartTemplate Template, string TemplateId,
    double4x4 SourceToFragment, double3 Extents, float MassKg, bool NativeReady = false);

/// <summary>Immutable generated native assets backed by pinned, append-only native mesh ranges.</summary>
internal static partial class RuntimeFragmentAssets
{
    private const string Prefix = "Purr2D2_Fragment_";
    private static KSA.Mod? _mod;
    private static uint _vertexBase, _indexBase;
    private static SliceMeshArena? _arena;
    private static bool _initialized, _installed;
    private static double _now;
    private static readonly Dictionary<string, double> HotCaps = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PreparedFragment> Prepared = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FailedPreparations = new(StringComparer.Ordinal);

    /// <summary>Must run from ImmediateLoad, before DeviceMeshInterleaved.Shared.Build.</summary>
    internal static void Initialize(KSA.Mod mod,
        int vertexMiB = SliceResourceLimits.DefaultVertexArenaMiB,
        int indexMiB = SliceResourceLimits.DefaultIndexArenaMiB,
        int diskMiB = SliceResourceLimits.DefaultAssetDiskMiB,
        int entryLimit = SliceResourceLimits.DefaultAssetEntryLimit)
    {
        if (_initialized) return;
        if (DeviceMeshInterleaved.Shared.IsBuilding || DeviceMeshInterleaved.Shared.IsBuilt)
            throw new InvalidOperationException("Arbitrary slicing requires reserving the native mesh arena before game mesh binding.");
        // The native class has only plain public instance fields. Refuse a changed layout rather
        // than bypassing new constructor-owned state or modifying the global allocation counters.
        string[] fields = ["HostMesh", "HasNormal", "HasUV", "VertexStride", "VerticesOffset", "VerticesSize",
            "IndexCount", "IndexType", "IndicesStride", "IndicesOffset", "IndicesSize"];
        var actual = typeof(DeviceMeshInterleaved).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (actual.Any(f => !f.IsPublic) || !actual.Select(f => f.Name).Order().SequenceEqual(fields.Order()))
            throw new InvalidOperationException("DeviceMeshInterleaved's plain-field layout changed; the runtime fragment adapter must be updated.");
        uint vertexBytes = checked((uint)(Math.Clamp(vertexMiB, SliceResourceLimits.MinimumVertexArenaMiB,
            SliceResourceLimits.MaximumVertexArenaMiB) * SliceResourceLimits.BytesPerMiB));
        uint indexBytes = checked((uint)(Math.Clamp(indexMiB, SliceResourceLimits.MinimumIndexArenaMiB,
            SliceResourceLimits.MaximumIndexArenaMiB) * SliceResourceLimits.BytesPerMiB));
        _vertexBase = Reserve(ref DeviceMeshInterleaved.Shared.RunningVertexBufferSize, vertexBytes, 32);
        _indexBase = Reserve(ref DeviceMeshInterleaved.Shared.RunningIndexBufferSize, indexBytes, 4);
        _arena = new(vertexBytes, indexBytes, _vertexBase, _indexBase);
        _initialVertexCapacity = vertexBytes; _initialIndexCapacity = indexBytes;
        ConfigureStorageLimits(diskMiB, entryLimit);
        _mod = mod;
        // Native loading resolves all source materials later, before any save can instantiate parts.
        var directory = Path.Combine(mod.DirectoryPath, "Generated", "Slices");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
            {
                var document = XDocument.Load(path);
                FragmentTopology.Remember(document);
                foreach (var atlas in document.Descendants("MeshAtlas"))
                {
                    string source = (string?)atlas.Attribute("Path") ?? "";
                    if (source.Length == 0 || !File.Exists(mod.GetPath(source)))
                        throw new InvalidDataException($"Generated slice '{path}' is missing its GLB '{source}'. Restore the complete Generated/Slices directory before loading its saves.");
                }
                var bundle = XmlLoader.Load<AssetBundle>(path, mod)
                    ?? throw new InvalidDataException($"Could not restore generated slice assets from '{path}'. Keep this file and its GLB with the save.");
                mod.Register(bundle);
                _registeredTemplates += bundle.Assets.OfType<PartTemplate>().Count();
            }
        RefreshDiskUsage(directory);
        _initialized = true;
        InstallAuthoredDebrisCallbacks();
    }

    private static uint Reserve(ref uint counter, uint capacity, uint alignment)
    {
        while (true)
        {
            uint before = Volatile.Read(ref counter);
            uint start = checked((before + alignment - 1) & ~(alignment - 1));
            uint after = checked(start + capacity);
            if (Interlocked.CompareExchange(ref counter, after, before) == before) return start;
        }
    }

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;
        var target = AccessTools.Method(typeof(PartModel), nameof(PartModel.AddInstance),
            [typeof(PartModel.PerInstanceData), typeof(PerInstanceDent), typeof(IViewport), typeof(int)])
            ?? throw new MissingMethodException("The native cap-emission submission method changed.");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(RuntimeFragmentAssets), nameof(BeforePartInstance)));
        _installed = true;
    }

    internal static void Update(double nowPlayerSeconds)
    {
        _now = nowPlayerSeconds;
        if (_now - _lastHeatPrune >= 1) { PruneExpiredHeat(); _lastHeatPrune = _now; }
    }
    internal static void ClearHeat() => HotCaps.Clear();
    internal static void MarkHot(PreparedFragment prepared, double nowPlayerSeconds)
    {
        HotCaps[prepared.TemplateId] = nowPlayerSeconds;
        _now = nowPlayerSeconds;
    }

    private static void BeforePartInstance(PartModel __instance, ref PartModel.PerInstanceData instanceData)
    {
        string id = __instance.Template.Mesh?.Id ?? "";
        if (!id.StartsWith(Prefix, StringComparison.Ordinal) || !id.EndsWith("_cap", StringComparison.Ordinal)) return;
        int solidMarker = id.IndexOf("_s", Prefix.Length, StringComparison.Ordinal);
        string fragmentId = solidMarker < 0 ? id[..^4] : id[..solidMarker];
        // These flags are changed on this submitted value only, never on templates/other models.
        if (!HotCaps.TryGetValue(fragmentId, out var born) || _now - born >= 18)
        {
            instanceData.StateBitFlag = (instanceData.StateBitFlag | 64) & ~128;
            return;
        }
        double age = Math.Max(0, _now - born);
        uint r = (uint)Math.Clamp(255 * Math.Exp(-age / 7), 0, 255);
        uint g = (uint)Math.Clamp(165 * Math.Exp(-age / 3), 0, 255);
        uint b = (uint)Math.Clamp(40 * Math.Exp(-age / 1.7), 0, 255);
        instanceData.StateBitFlag = (instanceData.StateBitFlag & ~64) | 128;
        instanceData.EmissiveColor = (r << 16) | (g << 8) | b;
    }

    private static DeviceMeshInterleaved MakeDeviceMesh(MeshAsset mesh, uint vertexOffset, uint indexOffset)
    {
        var positions = mesh.GetVertexSpan<float3>(RenderCore.MeshAttribute.Position);
        var normals = mesh.GetVertexSpan<float3>(RenderCore.MeshAttribute.Normal);
        var uvs = mesh.GetVertexSpan<float2>(RenderCore.MeshAttribute.Uv0);
        var indices = mesh.IndexBuffer;
        if (positions.Length == 0 || normals.Length != positions.Length || uvs.Length != positions.Length || indices == null || indices.Stride != 4)
            throw new InvalidDataException("Runtime fragment mesh does not meet the native position/normal/UV/u32 contract.");
        // Verified against the full public field layout in Initialize. Calling the native
        // constructor here would increment fixed-buffer high-water counters after Build and
        // make a later RT rebuild copy beyond the old allocation. No counters are touched here.
        var result = (DeviceMeshInterleaved)RuntimeHelpers.GetUninitializedObject(typeof(DeviceMeshInterleaved));
        result.HostMesh = mesh; result.HasNormal = true; result.HasUV = true;
        result.VertexStride = new ByteSize(32u); result.VerticesOffset = new ByteSize(vertexOffset);
        result.VerticesSize = new ByteSize(checked((uint)positions.Length * 32));
        result.IndexCount = indices.Count; result.IndexType = VkIndexType.UInt32;
        result.IndicesStride = new ByteSize(4u); result.IndicesOffset = new ByteSize(indexOffset);
        result.IndicesSize = new ByteSize(checked((uint)indices.Count * 4));
        return result;
    }

}
