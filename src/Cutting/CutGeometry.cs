using System;
using System.Collections.Generic;

namespace Purr2D2.Cutting;

// Engine-independent geometry shared by target selection, slicing and visual contact.
internal readonly record struct CutPoint(double X, double Y, double Z)
{
    public override string ToString() => FormattableString.Invariant($"({X:G6}, {Y:G6}, {Z:G6})");
    public static CutPoint operator +(CutPoint a, CutPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CutPoint operator -(CutPoint a, CutPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CutPoint operator *(CutPoint a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static CutPoint operator /(CutPoint a, double b) => a * (1 / b);
    public double LengthSquared => Dot(this, this);
    public double Length => Math.Sqrt(LengthSquared);
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public CutPoint Normalized => Length > 1e-12 ? this / Length : default;
    public static double Dot(CutPoint a, CutPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static CutPoint Cross(CutPoint a, CutPoint b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static CutPoint Min(CutPoint a, CutPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static CutPoint Max(CutPoint a, CutPoint b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
}

internal readonly record struct CutTriangle(CutPoint A, CutPoint B, CutPoint C)
{
    public CutPoint Min => CutPoint.Min(A, CutPoint.Min(B, C));
    public CutPoint Max => CutPoint.Max(A, CutPoint.Max(B, C));
    public CutPoint Center => (A + B + C) / 3;
    public CutPoint Normal => CutPoint.Cross(B - A, C - A).Normalized;
}

internal readonly record struct CutBounds(CutPoint Min, CutPoint Max)
{
    public bool Intersects(CutBounds other) => Min.X <= other.Max.X && Max.X >= other.Min.X
        && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    public static CutBounds Sweep(CutPoint a, CutPoint b, CutPoint c, CutPoint d, double radius)
    {
        var r = new CutPoint(radius, radius, radius);
        return new(CutPoint.Min(CutPoint.Min(a, b), CutPoint.Min(c, d)) - r,
            CutPoint.Max(CutPoint.Max(a, b), CutPoint.Max(c, d)) + r);
    }
}

internal static class CutGeometry
{
    // Two-sided cursor ray: selection follows the visible surface, not nearest-to-kitten proximity.
    internal static bool TryRayTriangle(CutPoint origin, CutPoint direction, CutTriangle triangle, out double distance)
    {
        distance = 0;
        var ab = triangle.B - triangle.A; var ac = triangle.C - triangle.A;
        var p = CutPoint.Cross(direction, ac); double det = CutPoint.Dot(ab, p);
        if (Math.Abs(det) <= 1e-12) return false;
        var fromA = origin - triangle.A; double u = CutPoint.Dot(fromA, p) / det;
        if (u < -1e-9 || u > 1 + 1e-9) return false;
        var q = CutPoint.Cross(fromA, ab); double v = CutPoint.Dot(direction, q) / det;
        if (v < -1e-9 || u + v > 1 + 1e-9) return false;
        distance = CutPoint.Dot(ac, q) / det;
        return double.IsFinite(distance) && distance >= 0;
    }
    // Closest point on triangle, including edges/vertices. Degenerate art is skipped at import.
    public static CutPoint ClosestPoint(CutPoint p, CutTriangle t)
    {
        var ab = t.B - t.A; var ac = t.C - t.A; var ap = p - t.A;
        double d1 = CutPoint.Dot(ab, ap), d2 = CutPoint.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return t.A;
        var bp = p - t.B;
        double d3 = CutPoint.Dot(ab, bp), d4 = CutPoint.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return t.B;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return t.A + ab * (d1 / (d1 - d3));
        var cp = p - t.C;
        double d5 = CutPoint.Dot(ab, cp), d6 = CutPoint.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return t.C;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return t.A + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return t.B + (t.C - t.B) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double inverse = 1 / (va + vb + vc);
        return t.A + ab * (vb * inverse) + ac * (vc * inverse);
    }

}

// Local-space BVH shared by every instance of an immutable native mesh.
internal sealed class CutMesh
{
    public readonly CutTriangle[] Triangles;
    private readonly int[] _indices;
    private readonly List<Node> _nodes = new();
    private readonly record struct Node(CutBounds Bounds, int Start, int Count, int Left, int Right);
    public CutMesh(CutTriangle[] triangles)
    {
        Triangles = triangles;
        _indices = new int[triangles.Length];
        for (int i = 0; i < _indices.Length; i++) _indices[i] = i;
        if (triangles.Length > 0) Build(0, triangles.Length);
    }
    private int Build(int start, int count)
    {
        var min = new CutPoint(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new CutPoint(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int i = start; i < start + count; i++)
        { min = CutPoint.Min(min, Triangles[_indices[i]].Min); max = CutPoint.Max(max, Triangles[_indices[i]].Max); }
        int index = _nodes.Count;
        _nodes.Add(new(new(min, max), start, count, -1, -1));
        if (count > 12)
        {
            var size = max - min;
            int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
            Array.Sort(_indices, start, count, Comparer<int>.Create((a, b) =>
            { var x = Triangles[a].Center; var y = Triangles[b].Center;
                int comparison = (axis == 0 ? x.X.CompareTo(y.X) : axis == 1 ? x.Y.CompareTo(y.Y) : x.Z.CompareTo(y.Z));
                return comparison != 0 ? comparison : a.CompareTo(b); }));
            int left = Build(start, count / 2), right = Build(start + count / 2, count - count / 2);
            _nodes[index] = new(new(min, max), start, 0, left, right);
        }
        return index;
    }
    public void Query(CutBounds bounds, List<int> destination)
    {
        destination.Clear();
        if (_nodes.Count > 0) Visit(0);
        void Visit(int index)
        {
            var node = _nodes[index];
            if (!node.Bounds.Intersects(bounds)) return;
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                    if (new CutBounds(Triangles[_indices[i]].Min, Triangles[_indices[i]].Max).Intersects(bounds))
                        destination.Add(_indices[i]);
            }
            else { Visit(node.Left); Visit(node.Right); }
        }
    }
}
