using System.Diagnostics;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using RenderCore;

namespace Purr2D2.Cutting;

/// <summary>Copies native buffers in small main-thread chunks. The worker receives no live references.</summary>
internal sealed class SliceSourceCapture : IDisposable
{
    private static int _cutSequence = Environment.TickCount;
    private readonly uint _seed = unchecked((uint)Interlocked.Increment(ref _cutSequence));
    private readonly SliceGeometryBudget _geometry = SliceGeometryBudget.Current;
    private sealed record Node(Part Part, double4x4 Transform, MeshReference?[] Meshes, SliceMaterialData[] Materials);
    private readonly Part _full;
    private readonly Vehicle _vehicle;
    private readonly List<Node> _nodes = new();
    private readonly IEnumerator<int> _steps;
    private readonly float _mass;
    internal SliceInput? Input { get; private set; }
    internal bool Done { get; private set; }
    internal SliceSourceCapture(Part struck, ContactSlice? contact = null)
    {
        _full = struck.StructuralPart;
        _vehicle = _full.Tree?.OwningVehicle ?? throw new InvalidOperationException("Source vessel is unavailable.");
        if (!NativeSlicePhysics.ValidateSource(_full, out var reason)) throw new InvalidDataException(reason);
        _mass = NativeSlicePhysics.SourceMass(_full);
        _steps = Copy(contact).GetEnumerator();
    }
    internal void Advance()
    {
        if (Done) return;
        long start = Stopwatch.GetTimestamp(); int units = 0;
        do { if (!_steps.MoveNext()) { Done = true; _steps.Dispose(); break; } }
        while (++units < 8 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1.5);
    }
    internal bool IsCurrent(Part source, IsolatedSliceSource? isolated = null)
    {
        if (source.StructuralPart != _full || _full.SubParts.Length + 1 != _nodes.Count) return false;
        if (isolated == null)
        {
            if (_vehicle.IsDisposed || _full.Tree?.OwningVehicle != _vehicle) return false;
        }
        else if (isolated.Source != _full || !isolated.IsCurrent(out _)) return false;
        foreach (var node in _nodes)
        {
            var expected = isolated == null ? node.Transform : node.Transform * isolated.SourceToIsolated;
            // Split can rotate/rebase an assembly, including docking-port roots.
            // Compare transformed capture points with numerical tolerance while
            // retaining the exact owner/tree gate above against later edits.
            if (node.Part.FullPart != _full || (isolated == null
                ? !node.Part.MatrixAsmb2VehicleAsmb.Equals(expected) : !SliceSourceFrame.SameTransform(node.Part.MatrixAsmb2VehicleAsmb, expected))) return false;
            var models = Models(node.Part);
            if (models.Count != node.Meshes.Length) return false;
            for (int i = 0; i < models.Count; i++)
                if (!ReferenceEquals(models[i].Mesh?.Get(), node.Meshes[i])
                    || models[i].Material?.Get()?.Id != node.Materials[i].Id || models[i].Glass != node.Materials[i].Glass) return false;
        }
        return Math.Abs(NativeSlicePhysics.SourceMass(_full) - _mass) <= Math.Max(.001, _mass * 2e-5);
    }
    private IEnumerable<int> Copy(ContactSlice? contact)
    {
        var groups = new List<SliceFace[]>();
        var materials = new Dictionary<int, SliceMaterialData>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = new[] { _full }.Concat(_full.SubParts.ToArray()).ToArray();
        var provenance = FragmentTopology.Find(_full.Template.Id,
            required: parts.Any(p => p.Template.Id == _full.Template.Id + "_visual"));
        var topology = provenance?.BeginCapture();
        int total = 0;
        foreach (var part in parts)
        {
            if (_vehicle.IsDisposed || part.FullPart.Tree?.OwningVehicle != _vehicle) throw new InvalidOperationException("Source changed during capture.");
            var transform = part.MatrixAsmb2VehicleAsmb;
            if (!double4x4.Invert(transform, out var inverse)) throw new InvalidDataException("Part has a singular mesh transform.");
            double determinant = transform.M11 * (transform.M22 * transform.M33 - transform.M23 * transform.M32)
                - transform.M12 * (transform.M21 * transform.M33 - transform.M23 * transform.M31)
                + transform.M13 * (transform.M21 * transform.M32 - transform.M22 * transform.M31);
            var models = Models(part);
            _nodes.Add(new(part, transform, models.Select(m => m.Mesh?.Get()).ToArray(),
                models.Select(m => new SliceMaterialData(m.Material?.Get()?.Id ?? "", m.Glass)).ToArray()));
            var faces = new List<SliceFace>();
            foreach (var model in models)
            {
                var mesh = model.Mesh?.Get(); var material = model.Material?.Get();
                if (mesh?.HostPrimitives == null || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                    throw new InvalidDataException("Part has no accessible native mesh or durable material reference.");
                topology?.BeginSurface(mesh.Id);
                FragmentFoliageRun visual = default;
                bool leaf = provenance?.TryFoliage(mesh.Id, out visual) == true;
                if (leaf && !ClutterTintShader.FoliageReady)
                    throw new InvalidDataException("The native foliage renderer is unavailable; preserving this fragment's appearance.");
                ClutterSurfaceTint.TryRead(mesh.Id, out uint clutterColor, out uint clutterLuminosity);
                string key = material.Id + ":glass=" + model.Glass + ":clutter=" + clutterColor + ":lum=" + clutterLuminosity
                    + ":foliage=" + leaf + ":double=" + visual.DoubleSided + ":backface=" + visual.KeepBackfaceNormals;
                if (!indices.TryGetValue(key, out int materialId))
                {
                    materialId = indices.Count;
                    if (materialId >= 48) throw new InvalidDataException("Part exceeds the 48-material slicing budget.");
                    indices.Add(key, materialId); materials.Add(materialId, new(material.Id, model.Glass, clutterColor, clutterLuminosity,
                        leaf, visual.DoubleSided, visual.KeepBackfaceNormals));
                }
                foreach (var primitive in mesh.HostPrimitives)
                {
                    if (primitive.IndexBuffer == null || primitive.IndexBuffer.Stride != 4 || primitive.IndexBuffer.Count % 3 != 0)
                        throw new InvalidDataException("Part mesh does not expose native indexed triangles.");
                    long triangles = (long)total + faces.Count + primitive.IndexBuffer.Count / 3;
                    if (triangles > _geometry.SourceTriangles)
                        throw new InvalidDataException($"Part exceeds the source triangle budget ({triangles:N0} / {_geometry.SourceTriangles:N0}). {SliceGeometryBudget.SettingsHint}");
                    for (int start = 0; start < primitive.IndexBuffer.Count; start += 768)
                    {
                        CopyChunk(primitive, start, faces, transform, inverse, determinant < 0, materialId);
                        if (topology != null)
                        {
                            topology.AppendChunk(faces);
                            total += faces.Count;
                            faces.Clear();
                        }
                        yield return 0;
                    }
                }
                topology?.EndSurface();
            }
            if (faces.Count > 0) { total += faces.Count; groups.Add(faces.ToArray()); }
            yield return 0;
        }
        if (topology != null)
            foreach (var solid in topology.Complete()) { groups.Add(solid); yield return 0; }
        if (groups.Count == 0) throw new InvalidDataException("No supported native part-model surfaces found.");
        if (!_geometry.AllowsSource(groups.Count, total))
            throw new InvalidDataException($"Captured hull exceeds the geometry budget ({_geometry.SourceUsage(groups.Count, total)}). {SliceGeometryBudget.SettingsHint}");
        if (!double4x4.Invert(_full.MatrixAsmb2VehicleAsmb, out var inv)) throw new InvalidDataException("Part transform is unavailable.");
        CutPoint[] axes = [new(inv.M11, inv.M21, inv.M31), new(inv.M12, inv.M22, inv.M32), new(inv.M13, inv.M23, inv.M33)];
        Input = new(groups.ToArray(), axes, materials, _mass, _seed)
        {
            GeneratedClosedGeometry = RuntimeFragmentAssets.IsGeneratedTemplate(_full.Template.Id),
            Geometry = _geometry,
            Contact = contact,
            CutKind = provenance?.CutKind ?? SliceCutKind.Metal,
            Foliage = topology?.CompleteFoliage() ?? []
        };
        if (!IsCurrent(_full)) throw new InvalidOperationException("Source changed during capture; try again.");
    }
    private static List<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)> Models(Part part)
    {
        var models = new List<(MeshReference?, PbrMaterialReference?, bool)>();
        foreach (var model in part.Modules.Get<PartModelModule>()) models.Add((model.PartModel.Template.Mesh, model.PartModel.Template.Material, false));
        foreach (var model in part.Modules.Get<PartModelDynamicModule>()) models.Add((model.PartModelDynamic.Template.Mesh, model.PartModelDynamic.Template.Material, false));
        foreach (var model in part.Modules.Get<PartModelGlassModule>()) models.Add((model.PartModelGlass.Template.Mesh, model.PartModelGlass.Template.Material, true));
        return models;
    }
    private static void CopyChunk(MeshAsset primitive, int start, List<SliceFace> faces, double4x4 transform, double4x4 inverse, bool reflected, int material)
    {
        var positions = primitive.GetVertexSpan<float3>(MeshAttribute.Position);
        var normals = primitive.GetVertexSpan<float3>(MeshAttribute.Normal);
        var uvs = primitive.GetVertexSpan<float2>(MeshAttribute.Uv0);
        var indices = primitive.IndexBuffer!.ToSpan<int>();
        if (positions.Length != normals.Length || positions.Length != uvs.Length) throw new InvalidDataException("Part mesh lacks normals or UV0.");
        for (int i = start; i < Math.Min(start + 768, indices.Length); i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length) throw new InvalidDataException("Part mesh contains invalid vertex indices.");
            var va = Vertex(positions[a], normals[a], uvs[a], transform, inverse);
            var vb = Vertex(positions[b], normals[b], uvs[b], transform, inverse);
            var vc = Vertex(positions[c], normals[c], uvs[c], transform, inverse);
            faces.Add(reflected ? new(va, vc, vb, material) : new(va, vb, vc, material));
        }
    }
    private static SliceVertex Vertex(float3 p, float3 normal, float2 uv, double4x4 transform, double4x4 inverse)
    {
        var position = new double3(p.X, p.Y, p.Z).Transform(transform);
        var n = new CutPoint(normal.X * inverse.M11 + normal.Y * inverse.M12 + normal.Z * inverse.M13,
            normal.X * inverse.M21 + normal.Y * inverse.M22 + normal.Z * inverse.M23,
            normal.X * inverse.M31 + normal.Y * inverse.M32 + normal.Z * inverse.M33).Normalized;
        return new(new(position.X, position.Y, position.Z), n, uv.X, uv.Y);
    }
    public void Dispose() => _steps.Dispose();
}
