using System.Diagnostics;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using Purr2D2.Core;
using Purr2D2.Force;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using RenderCore;

namespace Purr2D2.Cutting;

/// <summary>Bounded settled-thread copying. Coordinates are scaled object metres; workers never see game meshes.</summary>
internal sealed class ClutterSliceSourceCapture : IDisposable
{
    private readonly ClutterSliceDescription _source;
    private readonly ContactSlice _contact;
    private readonly IEnumerator<int> _steps;
    internal SliceInput? Input { get; private set; }
    internal bool Done { get; private set; }

    internal ClutterSliceSourceCapture(ClutterSliceDescription source, ContactSlice contact)
    { _source = source; _contact = contact; _steps = Copy().GetEnumerator(); }

    internal void Advance()
    {
        if (Done) return;
        long started = Stopwatch.GetTimestamp(); int units = 0;
        do { if (!_steps.MoveNext()) { Done = true; _steps.Dispose(); return; } }
        while (++units < 8 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1.5);
    }

    private IEnumerable<int> Copy()
    {
        var lod = _source.Object.Lods[0];
        if (!ClutterTintShader.Ready) throw new InvalidDataException("Native surface tint shader is unavailable; preserving the ground object's appearance.");
        var parent = (KSA.Celestial)_source.Target.Parent;
        float luminosity = (float)Math.Pow(parent.BodyTemplate.ScatteringReference?.MeanDiffuseLuminosity ?? .5f, 2.2);
        uint luminosityBits = ClutterSurfaceTint.Luminosity(luminosity);
        if (lod.Meshes.Count is < 1 or > 32) throw new InvalidDataException("The ground object's native LOD is unavailable or too complex.");
        var geometry = SliceGeometryBudget.Current;
        var materials = new Dictionary<int, SliceMaterialData>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<SliceFace[]>(); var foliage = new List<SliceFace>(); int total = 0;
        foreach (var mesh in lod.Meshes)
        {
            if (!_source.Target.IsValid) throw new InvalidDataException("The ground object left native physics during capture.");
            if (mesh.HostPrimitives == null || mesh.HostPrimitives.Length != mesh.PrimitiveMaterialIds.Length)
                throw new InvalidDataException("The ground object has no accessible native triangles.");
            var faces = new List<SliceFace>();
            for (int p = 0; p < mesh.HostPrimitives.Length; p++)
            {
                var material = lod.ResolveMaterial(mesh.PrimitiveMaterialIds[p]).Get();
                bool leaf = ClutterSliceGeometry.IsFoliage(material.OpacityMap != null, material.ThicknessMap != null, material.DoubleSided.Value);
                if (leaf && !ClutterTintShader.FoliageReady)
                    throw new InvalidDataException("The native foliage renderer is unavailable; preserving the ground object's appearance.");
                if (!material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                    throw new InvalidDataException("The ground object has no durable surface material.");
                string key = material.Id + ":foliage=" + leaf + ":double=" + material.DoubleSided.Value + ":backface=" + material.KeepBackfaceNormals.Value;
                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    if (id >= 48) throw new InvalidDataException("The ground object exceeds the surface budget.");
                    ids.Add(key, id); materials.Add(id, new(material.Id, false, _source.PackedColor, luminosityBits,
                        leaf, leaf && material.DoubleSided.Value, leaf && material.KeepBackfaceNormals.Value));
                }
                var primitive = mesh.HostPrimitives[p];
                if (primitive.IndexBuffer == null || primitive.IndexBuffer.Stride != 4 || primitive.IndexBuffer.Count % 3 != 0)
                    throw new InvalidDataException("The ground object does not expose indexed triangles.");
                long triangles = (long)total + faces.Count + foliage.Count + primitive.IndexBuffer.Count / 3;
                if (triangles > geometry.SourceTriangles) throw new InvalidDataException("The ground object exceeds the configured source triangle budget.");
                for (int start = 0; start < primitive.IndexBuffer.Count; start += 768)
                {
                    CopyChunk(primitive, start, leaf ? foliage : faces, _source.Scale, id);
                    yield return 0;
                }
            }
            if (faces.Count != 0) { total += faces.Count; groups.Add(faces.ToArray()); }
            yield return 0;
        }
        if (groups.Count == 0 || !geometry.AllowsSource(groups.Count, (long)total + foliage.Count))
            throw new InvalidDataException("The ground object exceeds the configured closed-geometry budget.");
        Input = new(groups.ToArray(), [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], materials, _source.MassKg)
        { Geometry = geometry, Contact = _contact, Foliage = foliage.ToArray(),
            SourceSolidWood = _source.Tree, CutKind = _source.Tree ? SliceCutKind.Wood : SliceCutKind.Metal };
    }

    private static void CopyChunk(MeshAsset primitive, int start, List<SliceFace> faces, double scale, int material)
    {
        var positions = primitive.GetVertexSpan<float3>(MeshAttribute.Position);
        var normals = primitive.GetVertexSpan<float3>(MeshAttribute.Normal);
        var uvs = primitive.GetVertexSpan<float2>(MeshAttribute.Uv0);
        var indices = primitive.IndexBuffer!.ToSpan<int>();
        if (positions.Length != normals.Length || positions.Length != uvs.Length)
            throw new InvalidDataException("The ground mesh lacks native normals or UV0.");
        for (int i = start; i < Math.Min(start + 768, indices.Length); i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length)
                throw new InvalidDataException("The ground mesh contains invalid indices.");
            faces.Add(new(Vertex(positions[a], normals[a], uvs[a], scale), Vertex(positions[b], normals[b], uvs[b], scale),
                Vertex(positions[c], normals[c], uvs[c], scale), material));
        }
    }
    private static SliceVertex Vertex(float3 p, float3 n, float2 uv, double scale) =>
        new(new(p.X * scale, p.Y * scale, p.Z * scale), new CutPoint(n.X, n.Y, n.Z).Normalized, uv.X, uv.Y);
    public void Dispose() => _steps.Dispose();
}
