using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

namespace Purr2D2.Autopilot;

/// <summary>A bounded CPU geometry copy, relative to the target's center of mass.</summary>
internal static class TargetSurfaceSnapshot
{
    private const int MaximumParts = 256;
    private const int MaximumMeshes = 1024;
    private const int MaximumPrimitives = 8192;
    private const int MaximumTriangles = 150_000;

    internal static bool TryCapture(Vehicle target, out CutTriangle[] triangles,
        out CutBounds[] candidateBounds, out string reason)
    {
        triangles = [];
        candidateBounds = [];
        reason = "";
        if (target.IsDisposed)
        {
            reason = "The target is no longer available.";
            return false;
        }

        try
        {
            double3 centerOfMass = target.CenterOfMassAsmb;
            Require(Finite(centerOfMass), "The target has an invalid center of mass.");

            var captured = new List<CutTriangle>();
            var parts = new List<Part>();
            var seenParts = new HashSet<Part>(ReferenceEqualityComparer.Instance);
            var seenMeshes = new HashSet<MeshReference>(ReferenceEqualityComparer.Instance);
            var bounds = new Dictionary<Part, CutBounds>(ReferenceEqualityComparer.Instance);
            var boundsOrder = new List<Part>();
            int meshCount = 0, primitiveCount = 0, triangleCount = 0;

            foreach (Part fullPart in target.Parts.Parts) AddPart(fullPart);
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                Part part = parts[partIndex];
                foreach (Part subPart in part.SubParts) AddPart(subPart);

                double4x4 partToAssembly = PartTransform(part);
                Require(Finite(partToAssembly), "A target part has an invalid geometry transform.");
                seenMeshes.Clear();
                bool hasRenderMesh = false;
                // Render geometry describes the visible hull. Picking meshes are optional and
                // may be coarse, empty or unloaded even when that complete hull is available.
                foreach (PartModelModule model in part.Modules.Get<PartModelModule>())
                {
                    if (model.PartModel.Template.Mesh is not { } meshId) continue;
                    hasRenderMesh = true;
                    MeshReference? mesh = meshId.Get();
                    Require(mesh != null, "A target render mesh is unavailable; wait for loading to finish.");
                    CaptureMesh(mesh!);
                }
                if (!hasRenderMesh)
                    foreach (MeshViewModule view in part.Modules.Get<MeshViewModule>())
                    {
                        Require(view.MeshView != null, "A target picking mesh is unavailable; wait for loading to finish.");
                        CaptureMesh(view.MeshView!);
                    }

                void CaptureMesh(MeshReference mesh)
                {
                    // Reusing a mesh on another part still needs that part's transform.
                    if (!seenMeshes.Add(mesh)) return;
                    Require(++meshCount <= MaximumMeshes, "The target exceeds the autopilot mesh budget.");
                    Require(mesh.PositionsCompare is { Length: > 0 },
                        "Target CPU geometry is unavailable; wait for loading to finish.");
                    int start = captured.Count;
                    foreach (double3[]? primitive in mesh.PositionsCompare!)
                    {
                        Require(++primitiveCount <= MaximumPrimitives,
                            "The target exceeds the autopilot geometry budget.");
                        Require(primitive != null && primitive.Length % 3 == 0,
                            "A target mesh has incomplete triangle geometry.");
                        int count = primitive!.Length / 3;
                        Require(count <= MaximumTriangles - triangleCount,
                            "The target exceeds the autopilot triangle budget.");
                        // Count even degenerate triangles so invalid art cannot evade the work bound.
                        triangleCount += count;
                        for (int i = 0; i < primitive.Length; i += 3)
                        {
                            CutPoint a = Transform(primitive[i]);
                            CutPoint b = Transform(primitive[i + 1]);
                            CutPoint c = Transform(primitive[i + 2]);
                            var triangle = new CutTriangle(a, b, c);
                            double areaSquared = CutPoint.Cross(b - a, c - a).LengthSquared;
                            Require(double.IsFinite(areaSquared), "A target triangle has invalid dimensions.");
                            if (areaSquared <= 1e-20) continue;
                            captured.Add(triangle);
                            Part fullPart = part.FullPart;
                            if (bounds.TryGetValue(fullPart, out CutBounds previous))
                                bounds[fullPart] = new(CutPoint.Min(previous.Min, triangle.Min),
                                    CutPoint.Max(previous.Max, triangle.Max));
                            else
                            {
                                bounds.Add(fullPart, new(triangle.Min, triangle.Max));
                                boundsOrder.Add(fullPart);
                            }
                        }
                    }
                    Require(captured.Count > start, "A target mesh contains no usable triangle geometry.");
                }

                CutPoint Transform(double3 vertex)
                {
                    Require(Finite(vertex), "A target mesh contains an invalid vertex.");
                    double3 body = vertex.Transform(partToAssembly) - centerOfMass;
                    Require(Finite(body), "A target geometry transform produced an invalid vertex.");
                    return new(body.X, body.Y, body.Z);
                }
            }

            Require(captured.Count > 0, "The target has no CPU surface geometry for autopilot guidance.");
            CutTriangle[] completeTriangles = captured.ToArray();
            CutBounds[] completeBounds = boundsOrder.Select(part => bounds[part]).ToArray();
            triangles = completeTriangles;
            candidateBounds = completeBounds;
            return true;

            void AddPart(Part part)
            {
                if (!seenParts.Add(part)) return;
                Require(parts.Count < MaximumParts, "The target exceeds the autopilot part budget.");
                parts.Add(part);
            }
        }
        catch (SnapshotRejected failure)
        {
            reason = failure.Message;
            return false;
        }
        catch (Exception failure)
        {
            reason = $"Target geometry could not be read ({failure.GetType().Name}).";
            return false;
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new SnapshotRejected(reason);
    }

    private static double4x4 PartTransform(Part part)
    {
        // Equivalent to MatrixAsmb2VehicleAsmb, without filling the game's shared lazy
        // transform caches while this read-only snapshot runs alongside vehicle workers.
        double4x4 transform = double4x4.Identity;
        int depth = 0;
        for (Part? current = part; current != null; current = current.PartParent)
        {
            Require(++depth <= MaximumParts, "A target part has an invalid geometry hierarchy.");
            transform *= double4x4.CreateScale(current.Scale)
                * double4x4.CreateFromQuaternion(current.Asmb2ParentAsmb)
                * double4x4.CreateTranslation(current.PositionParentAsmb);
        }
        return transform;
    }

    private static bool Finite(double3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static bool Finite(double4x4 value) =>
        double.IsFinite(value.M11) && double.IsFinite(value.M12) && double.IsFinite(value.M13) && double.IsFinite(value.M14)
        && double.IsFinite(value.M21) && double.IsFinite(value.M22) && double.IsFinite(value.M23) && double.IsFinite(value.M24)
        && double.IsFinite(value.M31) && double.IsFinite(value.M32) && double.IsFinite(value.M33) && double.IsFinite(value.M34)
        && double.IsFinite(value.M41) && double.IsFinite(value.M42) && double.IsFinite(value.M43) && double.IsFinite(value.M44);

    private sealed class SnapshotRejected(string reason) : Exception(reason);
}
