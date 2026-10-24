using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing;

/// <summary>Prepare the exact float32 surfaces uploaded to KSA, before any native mutation.</summary>
internal static class SliceMeshPrecision
{
    internal static bool TryPrepare(SlicePiece piece, double4x4 transform, out SliceFace[][] solids,
        out double3 extents, out string reason, CancellationToken cancellation = default)
    {
        solids = []; extents = default;
        var output = new List<SliceFace[]>();
        var minimum = new double3(double.MaxValue); var maximum = new double3(double.MinValue);
        foreach (var source in piece.ClosedSolids ?? [piece.Faces])
        {
            cancellation.ThrowIfCancellationRequested();
            SliceVertex Convert(SliceVertex v)
            {
                var p = double3.Transform(new(v.Position.X, v.Position.Y, v.Position.Z), transform);
                var n = double3.TransformNormal(new(v.Normal.X, v.Normal.Y, v.Normal.Z), transform);
                return new(new((float)p.X, (float)p.Y, (float)p.Z), new CutPoint(n.X, n.Y, n.Z).Normalized, v.U, v.V);
            }
            var converted = source.Select(f => new SliceFace(Convert(f.A), Convert(f.B), Convert(f.C), f.Material)).ToArray();
            if (!ClosedMeshPreparation.TryNormalize(converted, out var accepted, out reason, cancellation, float32Conversion: true))
            { reason = $"Float32 solid {output.Count} ({source.Length} faces; cut={source.Any(f => f.Material == -1)}) cannot retain a closed surface: " + reason; return false; }
            output.Add(accepted);
            foreach (var f in accepted)
                foreach (var v in new[] { f.A, f.B, f.C })
                {
                    var p = new double3(v.Position.X, v.Position.Y, v.Position.Z);
                    minimum = double3.Min(minimum, p); maximum = double3.Max(maximum, p);
                }
        }
        if (!PlaneSlicer.TryAuditCompound(output, out double volume, out reason)) return false;
        if (Math.Abs(volume - piece.Volume) > Math.Max(piece.Volume * .0002, 1e-10))
        { reason = "Float32 fragment volume changed beyond tolerance."; return false; }
        solids = output.ToArray(); extents = maximum - minimum; reason = ""; return true;
    }

}
