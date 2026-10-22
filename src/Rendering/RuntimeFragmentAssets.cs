using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Brutal;
using Brutal.GltfApi;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Deformation;
using Purr2D2.Cutting;
using RenderCore;

namespace Purr2D2.Rendering;

internal sealed record PreparedFragment(PartTemplate Template, string TemplateId,
    double4x4 SourceToFragment, double3 Extents, float MassKg);

/// <summary>Immutable generated native assets, backed by a startup-reserved part mesh arena.</summary>
internal static class RuntimeFragmentAssets
{
    private const uint VertexCapacity = 32 * 1024 * 1024;
    private const uint IndexCapacity = 16 * 1024 * 1024;
    private const long DiskCapacity = 512L * 1024 * 1024;
    private const int EntryCapacity = 1024;
    private const string Prefix = "Purr2D2_Fragment_";
    private const string CapMaterial = "Purr2D2_CutSurface";
    private static KSA.Mod? _mod;
    private static uint _vertexBase, _indexBase, _vertexUsed, _indexUsed;
    private static bool _initialized, _installed;
    private static double _now;
    private static readonly Dictionary<string, double> HotCaps = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PreparedFragment> Prepared = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FailedPreparations = new(StringComparer.Ordinal);
    private readonly record struct SurfaceGroup(int Solid, int Material, SliceFace[] Faces);

    /// <summary>Must run from ImmediateLoad, before DeviceMeshInterleaved.Shared.Build.</summary>
    internal static void Initialize(KSA.Mod mod)
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
        _vertexBase = Reserve(ref DeviceMeshInterleaved.Shared.RunningVertexBufferSize, VertexCapacity, 32);
        _indexBase = Reserve(ref DeviceMeshInterleaved.Shared.RunningIndexBufferSize, IndexCapacity, 4);
        _mod = mod;
        // Native loading resolves all source materials later, before any save can instantiate parts.
        var directory = Path.Combine(mod.DirectoryPath, "Generated", "Slices");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
            {
                var document = XDocument.Load(path);
                foreach (var atlas in document.Descendants("MeshAtlas"))
                {
                    string source = (string?)atlas.Attribute("Path") ?? "";
                    if (source.Length == 0 || !File.Exists(mod.GetPath(source)))
                        throw new InvalidDataException($"Generated slice '{path}' is missing its GLB '{source}'. Restore the complete Generated/Slices directory before loading its saves.");
                }
                var bundle = XmlLoader.Load<AssetBundle>(path, mod)
                    ?? throw new InvalidDataException($"Could not restore generated slice assets from '{path}'. Keep this file and its GLB with the save.");
                mod.Register(bundle);
            }
        _initialized = true;
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

    internal static void Update(double nowPlayerSeconds) => _now = nowPlayerSeconds;
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

    internal static bool TryPrepare(SlicePiece piece, IReadOnlyDictionary<int, PbrMaterialReference> materials,
        double4x4 sourceToFragment, float massKg, double3 principalSpecificInertia,
        out PreparedFragment? prepared, out string reason)
    {
        prepared = null; reason = "";
        string? preparingId = null;
        bool touchedNativeRegistry = false;
        if (!_initialized || _mod == null) { reason = "Runtime slice assets were not initialized before native mesh binding."; return false; }
        if (piece.Faces.Length == 0 || piece.Faces.Length > 100_000 || !float.IsFinite(massKg) || massKg <= 0
            || !Finite(principalSpecificInertia) || principalSpecificInertia.X <= 0 || principalSpecificInertia.Y <= 0 || principalSpecificInertia.Z <= 0)
        { reason = "Fragment triangle budget, mass or principal inertia is invalid."; return false; }
        try
        {
            var transformed = new SliceFace[piece.Faces.Length];
            var minimum = new double3(double.MaxValue);
            var maximum = new double3(double.MinValue);
            for (int i = 0; i < transformed.Length; i++)
            {
                var face = piece.Faces[i];
                if (face.Material != -1 && (!materials.TryGetValue(face.Material, out var material)
                    || !material.IsReferenceable || string.IsNullOrEmpty(material.Id)))
                { reason = "A source surface material has no persistent native asset ID."; return false; }
                var a = Transform(face.A); var b = Transform(face.B); var c = Transform(face.C);
                // Audit the actual float32 positions the GLB/native GPU will receive, not only
                // the double-precision clipper output. Collapsed slivers cannot enter physics.
                if (CutPoint.Cross(b.Position - a.Position, c.Position - a.Position).LengthSquared <= 1e-20)
                { reason = "A clipped triangle collapses after float32 export; move the cut away from the grazing edge."; return false; }
                // Re-sliced old cap surfaces retain cap shading/cooling rather than becoming
                // ordinary always-emissive outer surfaces under a new material group number.
                int materialId = face.Material == -1 || materials[face.Material].Id == CapMaterial ? -1 : face.Material;
                transformed[i] = new SliceFace(a, b, c, materialId);
            }
            double3 extents = maximum - minimum;
            if (!Finite(extents) || extents.X <= 1e-6 || extents.Y <= 1e-6 || extents.Z <= 1e-6)
            { reason = "The exported fragment has degenerate three-dimensional bounds."; return false; }
            bool audited;
            double exportedVolume;
            string auditReason;
            var quantizedSolids = new List<SliceFace[]>();
            if (piece.ClosedSolids is { } closedSolids)
            {
                if (closedSolids.Sum(s => s.Length) != transformed.Length)
                    throw new InvalidDataException("Fragment closed-solid ranges do not match the exported surface faces.");
                int first = 0;
                foreach (var solid in closedSolids)
                {
                    quantizedSolids.Add(transformed.AsSpan(first, solid.Length).ToArray());
                    first += solid.Length;
                }
                audited = PlaneSlicer.TryAuditCompound(quantizedSolids, out exportedVolume, out auditReason);
            }
            else
            {
                quantizedSolids.Add(transformed);
                audited = PlaneSlicer.TryAudit(transformed, out exportedVolume, out auditReason);
            }
            if (!audited)
            { reason = "Float32 export changed the fragment's closed mesh: " + auditReason; return false; }
            if (Math.Abs(exportedVolume - piece.Volume) > Math.Max(piece.Volume * 0.0002, 1e-10))
            { reason = "Float32 export changed fragment volume beyond tolerance; choose a cut with larger features."; return false; }
            int hullPoints = transformed.SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct().Take(8193).Count();
            if (hullPoints > 8192)
            { reason = "Fragment exceeds the bounded 8192-point native convex-hull budget."; return false; }
            var shapeGroups = quantizedSolids.SelectMany((solid, solidIndex) => solid.GroupBy(f => f.Material)
                .OrderBy(g => g.Key).Select(g => new SurfaceGroup(solidIndex, g.Key, g.ToArray()))).ToArray();
            if (shapeGroups.Length + 1 > 64)
            { reason = "Fragment exceeds 63 persisted solid/material groups; the source was not changed."; return false; }
            // Seed excludes mesh names; names can then contain the immutable geometry/material hash.
            var seedGroups = shapeGroups.Select(g => new FragmentGlbWriter.Group($"solid_{g.Solid}_surface_{g.Material}", g.Material, g.Faces)).ToArray();
            var seed = FragmentGlbWriter.Write(seedGroups);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(seed);
            hash.AppendData(Encoding.UTF8.GetBytes(string.Join("|", shapeGroups.Select(g => g.Material == -1 ? CapMaterial : materials[g.Material].Id))));
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"|{massKg:R}|{principalSpecificInertia.X:R}|{principalSpecificInertia.Y:R}|{principalSpecificInertia.Z:R}")));
            string id = Prefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            preparingId = id;
            if (FailedPreparations.TryGetValue(id, out reason!)) return false;
            if (Prepared.TryGetValue(id, out var cached))
            { prepared = cached with { SourceToFragment = sourceToFragment }; return true; }
            if (ModLibrary.TryGet<PartTemplate>(id, out var restored) && restored != null)
            {
                prepared = new PreparedFragment(restored, id, sourceToFragment, extents, massKg);
                Prepared[id] = prepared;
                return true;
            }
            uint vertexBytes = checked((uint)transformed.Length * 3 * 32);
            uint indexBytes = checked((uint)transformed.Length * 3 * 4);
            if (vertexBytes > VertexCapacity - _vertexUsed || indexBytes > IndexCapacity - _indexUsed)
            { reason = "The fixed runtime slice mesh arena is full for this session. Save and restart to reclaim it."; return false; }
            var groups = shapeGroups.Select(g => new FragmentGlbWriter.Group($"{id}_s{g.Solid}" + (g.Material == -1 ? "_cap" : "_m" + g.Material), g.Material, g.Faces)).ToList();
            groups.Add(new FragmentGlbWriter.Group(id + "_hull", int.MinValue, transformed));
            var bytes = FragmentGlbWriter.Write(groups);
            string relative = "Generated/Slices/" + id;
            string directory = Path.Combine(_mod.DirectoryPath, "Generated", "Slices");
            Directory.CreateDirectory(directory);
            string glbPath = Path.Combine(directory, id + ".glb");
            string xmlPath = Path.Combine(directory, id + ".xml");
            var xml = AssetXml(id, relative + ".glb", groups, shapeGroups, materials, massKg, principalSpecificInertia);
            byte[] xmlBytes = Encoding.UTF8.GetBytes(xml.ToString());
            long existingBytes = 0;
            int entries = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                existingBytes = checked(existingBytes + new FileInfo(path).Length);
                if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) entries++;
            }
            long additionalBytes = (File.Exists(glbPath) ? 0 : bytes.LongLength) + (File.Exists(xmlPath) ? 0 : xmlBytes.LongLength);
            if (existingBytes + additionalBytes > DiskCapacity || (!File.Exists(xmlPath) && entries >= EntryCapacity))
            { reason = "The persistent slice asset budget is full (512 MiB / 1024 fragments). Existing save assets are retained; no source was changed."; return false; }
            WriteImmutable(glbPath, bytes);
            WriteImmutable(xmlPath, xmlBytes);
            // Claim the whole request before uploads; a failed preparation consumes only this
            // bounded region, never rolls global counters back, and never changes source parts.
            uint vertexCursor = _vertexBase + _vertexUsed;
            uint indexCursor = _indexBase + _indexUsed;
            _vertexUsed += vertexBytes; _indexUsed += indexBytes;
            using (var gltf = new GltfLoader(glbPath))
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var mesh = new MeshReference { Id = groups[i].Name, Simple = false, Interleaved = true };
                    mesh.OnDataLoad(_mod);
                    mesh.Load(gltf, i, createDeviceMesh: false);
                    if (!ModLibrary.Register(mesh)) throw new InvalidOperationException("A generated mesh asset ID was already registered unexpectedly.");
                    touchedNativeRegistry = true;
                    if (i == groups.Count - 1) continue; // Complete collider CPU mesh, never rendered or uploaded.
                    var device = MakeDeviceMesh(mesh.HostMesh, vertexCursor, indexCursor);
                    vertexCursor += checked((uint)device.VerticesSize);
                    indexCursor += checked((uint)device.IndicesSize);
                    if (vertexCursor > _vertexBase + _vertexUsed || indexCursor > _indexBase + _indexUsed)
                        throw new InvalidDataException("Generated mesh bytes exceeded their preflight allocation.");
                    mesh.DeviceMeshesInterleaved = [device];
                    var renderer = Program.GetRenderer();
                    using var staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
                    mesh.Bind(renderer, staging);
                }
            }
            // Register only after complete upload. Template instantiation now sees bound meshes.
            var bundle = XmlLoader.Load<AssetBundle>(xmlPath, _mod)
                ?? throw new InvalidDataException("Generated native Part XML could not be registered.");
            _mod.Register(bundle);
            var template = ModLibrary.Get<PartTemplate>(id);
            prepared = new PreparedFragment(template, id, sourceToFragment, extents, massKg);
            Prepared[id] = prepared;
            return true;

            SliceVertex Transform(SliceVertex vertex)
            {
                var p = new double3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z).Transform(sourceToFragment);
                p = double3.Unpack(float3.Pack(p));
                var n = double3.TransformNormal(new double3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z), sourceToFragment);
                double length = n.Length();
                if (!Finite(p) || !Finite(n) || length < 1e-12) throw new InvalidDataException("A fragment vertex or normal is invalid.");
                n /= length;
                minimum = double3.Min(minimum, p); maximum = double3.Max(maximum, p);
                return new SliceVertex(new CutPoint(p.X, p.Y, p.Z), new CutPoint(n.X, n.Y, n.Z), vertex.U, vertex.V);
            }
        }
        catch (Exception error)
        {
            reason = "Could not prepare native slice assets: " + error.Message;
            if (touchedNativeRegistry && preparingId != null)
            {
                reason += " This fragment has partially registered native assets; save and restart before retrying this same cut. The source geometry was not changed.";
                FailedPreparations[preparingId] = reason;
            }
            prepared = null;
            return false;
        }
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

    private static XDocument AssetXml(string id, string glbPath, IReadOnlyList<FragmentGlbWriter.Group> groups,
        IReadOnlyList<SurfaceGroup> surfaces, IReadOnlyDictionary<int, PbrMaterialReference> materials, float mass, double3 inertia)
    {
        var part = new XElement("Part", new XAttribute("Id", id), new XAttribute("DisplayName", "Sliced metal fragment"));
        part.Add(new XElement("MeshView", new XAttribute("Id", id + "_pick"),
            new XElement("Mesh", new XAttribute("Id", id + "_hull"))));
        var subparts = new List<XElement>();
        foreach (int solid in surfaces.Select(s => s.Solid).Distinct())
        {
            string subpartId = id + "_solid" + solid;
            var subpart = new XElement("SubPart", new XAttribute("Id", subpartId));
            for (int i = 0; i < surfaces.Count; i++)
            {
                if (surfaces[i].Solid != solid) continue;
                var group = groups[i];
                subpart.Add(new XElement("PartModel", new XAttribute("Id", group.Name + "_model"),
                    new XElement("Mesh", new XAttribute("Id", group.Name)),
                    new XElement("Material", new XAttribute("Id", group.Material == -1 ? CapMaterial : materials[group.Material].Id)),
                    new XElement("ShadowCaster", true)));
            }
            subparts.Add(subpart);
            part.Add(new XElement("SubPart", new XAttribute("InstanceOf", subpartId)));
        }
        part.Add(new XElement("Collider", new XAttribute("Id", id + "_collider"),
            new XElement("ConvexHull", new XAttribute("Id", id + "_hullCollider"),
                new XElement("Mesh", new XAttribute("Id", id + "_hull")))));
        part.Add(new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(mass))),
            new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
        return new XDocument(new XElement("Assets", new XElement("MeshAtlas", new XAttribute("Id", id + "_atlas"),
            new XAttribute("Path", glbPath)), subparts, part));
    }

    private static string F(double number) => number.ToString("R", CultureInfo.InvariantCulture);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static void WriteImmutable(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("A persistent slice asset was modified: " + path);
            return;
        }
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
