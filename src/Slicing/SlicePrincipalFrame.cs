using Brutal.Numerics;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing;

internal readonly record struct PrincipalFragment(double4x4 SourceToFragment, doubleQuat FragmentToSource,
    CutPoint Centroid, double3 PrincipalSpecificInertia);

/// <summary>Rigid principal-axis authoring makes a full inertia tensor durable in native CustomMass XML.</summary>
internal static class SlicePrincipalFrame
{
    internal static bool TryCreate(SlicePiece piece, out PrincipalFragment frame, out string reason)
    {
        frame = default;
        reason = "Invalid fragment volume or inertia.";
        if (!(piece.Volume > 1e-12) || !double.IsFinite(piece.Volume) || !piece.Centroid.Finite) return false;
        var t = piece.Inertia;
        double[,] matrix = { { t.XX, t.XY, t.XZ }, { t.XY, t.YY, t.YZ }, { t.XZ, t.YZ, t.ZZ } };
        var axes = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            axes[i, i] = 1;
            for (int j = 0; j < 3; j++) if (!double.IsFinite(matrix[i, j])) return false;
        }
        // Symmetric Jacobi rotations; eigenvectors are columns in source coordinates.
        for (int iteration = 0; iteration < 48; iteration++)
        {
            int p = 0, q = 1;
            if (Math.Abs(matrix[0, 2]) > Math.Abs(matrix[p, q])) { p = 0; q = 2; }
            if (Math.Abs(matrix[1, 2]) > Math.Abs(matrix[p, q])) { p = 1; q = 2; }
            double scale = Math.Max(1e-20, Math.Abs(matrix[0, 0]) + Math.Abs(matrix[1, 1]) + Math.Abs(matrix[2, 2]));
            if (Math.Abs(matrix[p, q]) < scale * 1e-13) break;
            double angle = .5 * Math.Atan2(2 * matrix[p, q], matrix[q, q] - matrix[p, p]);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double app = matrix[p, p], aqq = matrix[q, q], apq = matrix[p, q];
            matrix[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            matrix[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
            matrix[p, q] = matrix[q, p] = 0;
            for (int k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    double akp = matrix[k, p], akq = matrix[k, q];
                    matrix[k, p] = matrix[p, k] = c * akp - s * akq;
                    matrix[k, q] = matrix[q, k] = s * akp + c * akq;
                }
                double vkp = axes[k, p], vkq = axes[k, q];
                axes[k, p] = c * vkp - s * vkq;
                axes[k, q] = s * vkp + c * vkq;
            }
        }
        int[] order = Enumerable.Range(0, 3).OrderBy(i => matrix[i, i]).ToArray();
        var moments = new double3(matrix[order[0], order[0]], matrix[order[1], order[1]], matrix[order[2], order[2]]) / piece.Volume;
        if (!(moments.X > 1e-10 && moments.Y > 1e-10 && moments.Z > 1e-10)
            || !double.IsFinite(moments.X + moments.Y + moments.Z)) return false;
        double3 x = Column(order[0]), y = Column(order[1]);
        x = double3.Normalize(x);
        y = double3.Normalize(y - x * double3.Dot(x, y));
        double3 z = double3.Cross(x, y); // Enforce a proper right-handed rotation, never a reflection.
        var rotation = new double4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        doubleQuat quaternion = doubleQuat.Normalize(doubleQuat.CreateFromRotationMatrix(rotation));
        var center = new double3(piece.Centroid.X, piece.Centroid.Y, piece.Centroid.Z);
        frame = new PrincipalFragment(double4x4.CreateTranslation(-center) * double4x4.Transpose(rotation), quaternion,
            piece.Centroid, moments);
        reason = "";
        return true;

        double3 Column(int i) => new(axes[0, i], axes[1, i], axes[2, i]);
    }

    internal static double4x4 Tensor(double xx, double yy, double zz, double xy, double xz, double yz) =>
        new(xx, xy, xz, 0, xy, yy, yz, 0, xz, yz, zz, 0, 0, 0, 0, 1);

    internal static double4x4 ParallelAxis(double mass, double3 r) => Tensor(
        mass * (r.Y * r.Y + r.Z * r.Z), mass * (r.X * r.X + r.Z * r.Z), mass * (r.X * r.X + r.Y * r.Y),
        -mass * r.X * r.Y, -mass * r.X * r.Z, -mass * r.Y * r.Z);

    internal static double3 ApplyTensor(double3 vector, double4x4 tensor) => new(
        vector.X * tensor.M11 + vector.Y * tensor.M21 + vector.Z * tensor.M31,
        vector.X * tensor.M12 + vector.Y * tensor.M22 + vector.Z * tensor.M32,
        vector.X * tensor.M13 + vector.Y * tensor.M23 + vector.Z * tensor.M33);

    internal static bool TryAngularVelocity(double4x4 inertia, double3 momentum, out double3 angularVelocity)
    {
        inertia.W = double4.UnitW;
        if (!double4x4.Invert(inertia, out var inverse)) { angularVelocity = default; return false; }
        angularVelocity = ApplyTensor(momentum, inverse);
        return double.IsFinite(angularVelocity.X) && double.IsFinite(angularVelocity.Y) && double.IsFinite(angularVelocity.Z);
    }
}
