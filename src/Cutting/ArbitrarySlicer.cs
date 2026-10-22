using Brutal.Numerics;
using Brutal.Render.Mesh;
using KSA;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using RenderCore;

namespace Purr2D2.Cutting;

internal readonly record struct SliceAttempt(bool Success, string Message);

/// <summary>Atomic geometry preparation followed by native topology mutation at the settled solver seam.</summary>
internal sealed class ArbitrarySlicer
{
    internal SliceAttempt Slice(Part struck, SlicePlane plane, double now)
    {
        try
        {
            if (!NativeSlicePhysics.ValidateSource(struck, out string reason)) return new(false, reason);
            if (!TryGather(struck.FullPart, out var source, out var materials, out reason)) return new(false, reason);
            if (!PlaneSlicer.TrySliceCompound(source, plane, out var result, out reason))
                return new(false, "Slice refused: " + reason);
            if (result!.Pieces.Length is < 2 or > 8)
                return new(false, "Slice would create too many disconnected fragments (maximum 8).");

            float sourceMass = struck.FullPart.ComputeSubtreeInertMass();
            double volume = result.Pieces.Sum(piece => piece.Volume);
            if (!float.IsFinite(sourceMass) || sourceMass <= 0 || !double.IsFinite(volume) || volume <= 0)
                return new(false, "Source has no valid inert mass or solid volume.");
            var fragments = new List<PreparedPhysicsFragment>(result.Pieces.Length);
            foreach (var piece in result.Pieces)
            {
                if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out reason)) return new(false, reason);
                float mass = (float)(sourceMass * piece.Volume / volume);
                if (!RuntimeFragmentAssets.TryPrepare(piece, materials, frame.SourceToFragment, mass,
                    frame.PrincipalSpecificInertia, out var asset, out reason)) return new(false, reason);
                fragments.Add(new(asset!, frame));
            }
            // Every closed mesh, collider, persistent template and GPU allocation is ready before
            // native isolation starts. Rejected geometry never falls back to destructive part failure.
            if (!NativeSlicePhysics.TryReplace(struck, fragments, out var created, out reason))
                return new(false, reason);
            foreach (var fragment in fragments) RuntimeFragmentAssets.MarkHot(fragment.Asset, now);
            return new(true, reason + " Cut faces are cooling.");
        }
        catch (Exception error)
        {
            Console.WriteLine($"[purr2d2] Arbitrary slicing failed: {error}");
            return new(false, "Slice failed: " + error.Message);
        }
    }

    private static bool TryGather(Part full, out SliceFace[][] faces,
        out Dictionary<int, PbrMaterialReference> materials, out string reason)
    {
        faces = [];
        materials = new();
        var groups = new List<SliceFace[]>();
        int totalFaces = 0;
        var materialIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodes = new List<Part> { full };
        nodes.AddRange(full.SubParts);
        foreach (Part part in nodes.Distinct())
        {
            var collected = new List<SliceFace>();
            var transform = part.MatrixAsmb2VehicleAsmb;
            if (!double4x4.Invert(transform, out var inverse))
            { reason = "Part has a singular mesh transform."; return false; }
            double determinant = transform.M11 * (transform.M22 * transform.M33 - transform.M23 * transform.M32)
                - transform.M12 * (transform.M21 * transform.M33 - transform.M23 * transform.M31)
                + transform.M13 * (transform.M21 * transform.M32 - transform.M22 * transform.M31);
            foreach (var model in part.Modules.Get<PartModelModule>())
            {
                var template = model.PartModel.Template;
                if (template.Internal) { reason = "Internal render layers are not supported by the solid slicer."; return false; }
                var mesh = template.Mesh?.Get();
                var material = template.Material?.Get();
                if (mesh?.HostPrimitives == null || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                { reason = "Part has no accessible native mesh or durable material reference."; return false; }
                if (!materialIndices.TryGetValue(material.Id, out int materialId))
                {
                    materialId = materialIndices.Count;
                    if (materialId >= 48) { reason = "Part exceeds the 48-material slicing budget."; return false; }
                    materialIndices.Add(material.Id, materialId);
                    materials.Add(materialId, material);
                }
                foreach (var primitive in mesh.HostPrimitives)
                {
                    var positions = primitive.GetVertexSpan<float3>(MeshAttribute.Position);
                    var normals = primitive.GetVertexSpan<float3>(MeshAttribute.Normal);
                    var uvs = primitive.GetVertexSpan<float2>(MeshAttribute.Uv0);
                    var indexBuffer = primitive.IndexBuffer;
                    if (indexBuffer == null || indexBuffer.Stride != 4)
                    { reason = "Part mesh does not expose the native 32-bit triangle-index format."; return false; }
                    var indices = indexBuffer.ToSpan<int>();
                    if (indices.Length % 3 != 0 || indices.Length == 0 || positions.Length != normals.Length || positions.Length != uvs.Length)
                    { reason = "Part mesh lacks complete indexed triangles, normals or UV0."; return false; }
                    if (totalFaces + collected.Count + indices.Length / 3 > 40_000)
                    { reason = "Part exceeds the 40,000-triangle slicing budget."; return false; }
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length)
                        { reason = "Part mesh contains invalid vertex indices."; return false; }
                        var va = Vertex(positions[a], normals[a], uvs[a], transform, inverse);
                        var vb = Vertex(positions[b], normals[b], uvs[b], transform, inverse);
                        var vc = Vertex(positions[c], normals[c], uvs[c], transform, inverse);
                        collected.Add(determinant < 0 ? new(va, vc, vb, materialId) : new(va, vb, vc, materialId));
                    }
                }
            }
            if (collected.Count > 0)
            {
                totalFaces += collected.Count;
                groups.Add(collected.ToArray());
            }
        }
        if (groups.Count == 0) { reason = "No supported native part-model surfaces found."; return false; }
        faces = groups.ToArray(); reason = ""; return true;
    }

    private static SliceVertex Vertex(float3 position, float3 normal, float2 uv, double4x4 transform, double4x4 inverse)
    {
        var point = new double3(position.X, position.Y, position.Z).Transform(transform);
        // Row-vector positions need inverse-transpose normals, including nonuniform scale.
        var n = new CutPoint(normal.X * inverse.M11 + normal.Y * inverse.M12 + normal.Z * inverse.M13,
            normal.X * inverse.M21 + normal.Y * inverse.M22 + normal.Z * inverse.M23,
            normal.X * inverse.M31 + normal.Y * inverse.M32 + normal.Z * inverse.M33).Normalized;
        return new(new(point.X, point.Y, point.Z), n, uv.X, uv.Y);
    }
}
