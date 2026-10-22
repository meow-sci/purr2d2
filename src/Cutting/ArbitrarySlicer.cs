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
    internal Action<Part, double3, double3, double>? CutSparks;
    internal SliceAttempt PlanAssistedSlice(Part struck, out SlicePlane plane)
    {
        plane = default;
        try
        {
            if (!NativeSlicePhysics.ValidateSource(struck, out string reason)) return new(false, reason);
            var full = struck.StructuralPart;
            if (!TryGather(full, out var source, out _, out reason)) return new(false, reason);
            if (!double4x4.Invert(full.MatrixAsmb2VehicleAsmb, out var inverse))
                return new(false, "Part transform is unavailable.");
            // Local coordinate planes transform by inverse transpose, including nonuniform scale.
            CutPoint[] axes = [new(inverse.M11, inverse.M21, inverse.M31),
                new(inverse.M12, inverse.M22, inverse.M32), new(inverse.M13, inverse.M23, inverse.M33)];
            string failure = "No broad section could be found.";
            foreach (var candidate in AssistedSliceGeometry.Candidates(source, axes))
            {
                if (!PlaneSlicer.TrySliceCompound(source, candidate, out var result, out failure)) continue;
                var check = CheckPieces(result!);
                if (!check.Success) { failure = check.Message; continue; }
                // Reuse the same input on commit; do not apply the slicer's microscopic seam nudge twice.
                plane = candidate;
                return new(true, "Slice ready.");
            }
            return new(false, "This part could not be sliced: " + failure);
        }
        catch (Exception error) { return new(false, "Slice preparation failed: " + error.Message); }
    }

    private static SliceAttempt CheckPieces(PlaneSliceResult result)
    {
        foreach (var piece in result.Pieces)
        {
            if (!SlicePrincipalFrame.TryCreate(piece, out var frame, out string reason)
                || !SliceMeshPrecision.TryPrepare(piece, frame.SourceToFragment, out var solids, out _, out reason))
                return new(false, reason);
            int groups = solids.Sum(s => s.Select(f => f.Material).Distinct().Count());
            int points = solids.SelectMany(s => s).SelectMany(f => new[] { f.A.Position, f.B.Position, f.C.Position }).Distinct().Take(65537).Count();
            if (groups > 2047 || points > 65536 || piece.Faces.Length > 100_000)
                return new(false, "This cut exceeds the native fragment geometry budget; choose a less detailed section.");
        }
        return result.Pieces.Length is >= 2 and <= 8
            ? new(true, "Selected plane can slice this part.")
            : new(false, "Slice would create too many disconnected fragments (maximum 8).");
    }

    internal SliceAttempt Slice(Part struck, SlicePlane plane, double now)
    {
        try
        {
            if (!NativeSlicePhysics.ValidateSource(struck, out string reason)) return new(false, reason);
            if (!TryGather(struck.StructuralPart, out var source, out var materials, out reason)) return new(false, reason);
            if (!PlaneSlicer.TrySliceCompound(source, plane, out var result, out reason))
                return new(false, "Slice refused: " + reason);
            if (result!.Pieces.Length is < 2 or > 8)
                return new(false, "Slice would create too many disconnected fragments (maximum 8).");

            float sourceMass = NativeSlicePhysics.SourceMass(struck);
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
                fragments.Add(new(asset!, frame, piece.PositiveSide));
            }
            // Every closed mesh, collider, persistent template and GPU allocation is ready before
            // native isolation starts. Rejected geometry never falls back to destructive part failure.
            var normal = result.EffectivePlane.Normal;
            if (!NativeSlicePhysics.TryReplace(struck, fragments, new(normal.X, normal.Y, normal.Z), out var created, out reason))
                return new(false, reason);
            foreach (var fragment in fragments) RuntimeFragmentAssets.MarkHot(fragment.Asset, now);
            try
            {
                for (int i = 0; i < created.Count; i++)
                {
                    var caps = result.Pieces[i].Faces.Where(f => f.Material == -1).ToArray();
                    int stride = Math.Max(1, caps.Length / 24);
                    for (int j = 0; j < caps.Length; j += stride)
                    {
                        var face = caps[j]; var center = (face.A.Position + face.B.Position + face.C.Position) / 3;
                        var local = new double3(center.X, center.Y, center.Z).Transform(fragments[i].Frame.SourceToFragment);
                        var n = face.A.Normal;
                        var localNormal = double3.TransformNormal(new(n.X, n.Y, n.Z), fragments[i].Frame.SourceToFragment).NormalizeOrZero();
                        CutSparks?.Invoke(created[i].Parts.Root, local, localNormal, now);
                    }
                }
            }
            catch (Exception effectError) { Console.WriteLine($"[purr2d2] Cut completed; spark effect failed: {effectError.Message}"); }
            return new(true, reason + " Cut faces are cooling.");
        }
        catch (Exception error)
        {
            Console.WriteLine($"[purr2d2] Arbitrary slicing failed: {error}");
            return new(false, "Slice failed: " + error.Message);
        }
    }

    private static bool TryGather(Part full, out SliceFace[][] faces,
        out Dictionary<int, SliceMaterial> materials, out string reason)
    {
        faces = [];
        materials = new();
        var groups = new List<SliceFace[]>();
        int totalFaces = 0;
        var materialIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        // Bake the structural exterior. Attached IVA furnishings are disposed
        // with the source capsule; their dry mass is included in the wreckage,
        // but hundreds of seats/instruments are not separate cutting surfaces.
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
            var models = new List<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)>();
            foreach (var model in part.Modules.Get<PartModelModule>())
            {
                var template = model.PartModel.Template;
                models.Add((template.Mesh, template.Material, false));
            }
            foreach (var model in part.Modules.Get<PartModelDynamicModule>())
                models.Add((model.PartModelDynamic.Template.Mesh, model.PartModelDynamic.Template.Material, false));
            foreach (var model in part.Modules.Get<PartModelGlassModule>())
                models.Add((model.PartModelGlass.Template.Mesh, model.PartModelGlass.Template.Material, true));
            foreach (var model in models)
            {
                var mesh = model.Mesh?.Get();
                var material = model.Material?.Get();
                if (mesh?.HostPrimitives == null || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                { reason = "Part has no accessible native mesh or durable material reference."; return false; }
                string materialKey = material.Id + ":glass=" + model.Glass;
                if (!materialIndices.TryGetValue(materialKey, out int materialId))
                {
                    materialId = materialIndices.Count;
                    if (materialId >= 48) { reason = "Part exceeds the 48-material slicing budget."; return false; }
                    materialIndices.Add(materialKey, materialId);
                    materials.Add(materialId, new(material, model.Glass));
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
                    if (totalFaces + collected.Count + indices.Length / 3 > 120_000)
                    { reason = "Part exceeds the 120,000-triangle slicing budget."; return false; }
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
        return SliceShellBuilder.TryPrepare(groups, out faces, out reason);
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
