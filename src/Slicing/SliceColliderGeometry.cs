using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Purr2D2.Cutting;
using Vector3 = System.Numerics.Vector3;

namespace Purr2D2.Slicing;

internal static class SliceColliderGeometry
{
    // Cook the expensive hull search on a private worker-owned pool. Persist only hull
    // vertices/faces so native lazy collider creation has hundreds, not 65,536 points.
    internal static SliceFace[] Build(SliceFace[] faces, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var unique = new HashSet<CutPoint>();
        foreach (var f in faces) { unique.Add(f.A.Position); unique.Add(f.B.Position); unique.Add(f.C.Position); }
        var points = unique.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToArray();
        using var pool = new BufferPool();
        ConvexHullHelper.ComputeHull(points, pool, out var hull);
        try
        {
            cancel.ThrowIfCancellationRequested();
            if (hull.FaceStartIndices.Length < 4) throw new InvalidDataException("Fragment collision hull has no volume.");
            var output = new List<SliceFace>();
            for (int i = 0; i < hull.FaceStartIndices.Length; i++)
            {
                hull.GetFace(i, out var face);
                var a = Vertex(points[face[0]]);
                for (int j = 2; j < face.VertexCount; j++) output.Add(new(a, Vertex(points[face[j - 1]]), Vertex(points[face[j]]), int.MinValue));
            }
            double volume = PlaneSlicer.Properties(output).Volume;
            if (!double.IsFinite(volume) || Math.Abs(volume) < 1e-12)
                throw new InvalidDataException("Fragment collision hull has no finite volume.");
            if (volume < 0) output = output.Select(f => new SliceFace(f.C, f.B, f.A, f.Material)).ToList();
            // This mesh supplies points to native ConvexHull, not render or cut surfaces.
            // Bepu's hull tolerance can retain nearly coincident hull vertices; applying the
            // slicer's welding/manifold audit would merge them and reject valid point clouds.
            return output.ToArray();
        }
        finally { hull.Dispose(pool); }
    }
    private static SliceVertex Vertex(Vector3 p) => new(new(p.X, p.Y, p.Z), new(0, 0, 1), 0, 0);
}
