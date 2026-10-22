using System;
using System.Collections.Generic;

namespace Purr2D2.Cutting;

// Engine-independent doubles: tests exercise the actual contact implementation.
internal readonly record struct CutPoint(double X, double Y, double Z)
{
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
    public static bool MotionWithinLimit(CutPoint previousA, CutPoint previousB, CutPoint a, CutPoint b, double limit) =>
        previousA.Finite && previousB.Finite && a.Finite && b.Finite && double.IsFinite(limit) && limit >= 0
        && Math.Max((previousA - a).LengthSquared, (previousB - b).LengthSquared) <= limit * limit;

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

    public static double SegmentTriangleDistanceSquared(CutPoint a, CutPoint b, CutTriangle triangle, out CutPoint onSurface)
    {
        var delta = b - a;
        var normal = CutPoint.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
        double denominator = CutPoint.Dot(normal, delta);
        if (Math.Abs(denominator) > 1e-14 * Math.Max(1, normal.Length * delta.Length))
        {
            double t = CutPoint.Dot(normal, triangle.A - a) / denominator;
            if (t >= 0 && t <= 1)
            {
                var p = a + delta * t;
                var q = ClosestPoint(p, triangle);
                if ((p - q).LengthSquared < 1e-20) { onSurface = q; return 0; }
            }
        }
        var bestSurface = ClosestPoint(a, triangle);
        double best = (a - bestSurface).LengthSquared;
        var fromEnd = ClosestPoint(b, triangle);
        double endDistance = (b - fromEnd).LengthSquared;
        if (endDistance < best) { best = endDistance; bestSurface = fromEnd; }
        Edge(triangle.A, triangle.B); Edge(triangle.B, triangle.C); Edge(triangle.C, triangle.A);
        onSurface = bestSurface;
        return best;

        void Edge(CutPoint c, CutPoint d)
        {
            double distance = SegmentDistanceSquared(a, b, c, d, out var edgePoint);
            if (distance < best) { best = distance; bestSurface = edgePoint; }
        }
    }

    private static double SegmentDistanceSquared(CutPoint p, CutPoint q, CutPoint r, CutPoint s, out CutPoint onSecond)
    {
        var d1 = q - p; var d2 = s - r; var offset = p - r;
        double a = d1.LengthSquared, e = d2.LengthSquared, f = CutPoint.Dot(d2, offset), u, v;
        if (a <= 1e-24 && e <= 1e-24) { onSecond = r; return (p - r).LengthSquared; }
        if (a <= 1e-24) { u = 0; v = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = CutPoint.Dot(d1, offset);
            if (e <= 1e-24) { v = 0; u = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = CutPoint.Dot(d1, d2), determinant = a * e - b * b;
                u = determinant > 1e-24 ? Math.Clamp((b * f - c * e) / determinant, 0, 1) : 0;
                v = (b * u + f) / e;
                if (v < 0) { v = 0; u = Math.Clamp(-c / a, 0, 1); }
                else if (v > 1) { v = 1; u = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        onSecond = r + d2 * v;
        return (p + d1 * u - onSecond).LengthSquared;
    }

    // Linear relative sweep; a bounded radius expansion covers the distance between samples.
    // Above the motion limit the caller resets history, so teleports never cut along their path.
    public static bool SweepContact(CutPoint previousA, CutPoint previousB, CutPoint a, CutPoint b,
        CutTriangle triangle, double radius, out CutPoint surface)
    {
        double travel = Math.Max((previousA - a).Length, (previousB - b).Length);
        int steps = Math.Clamp((int)Math.Ceiling(travel / Math.Max(radius, .001)), 1, 32);
        double conservativeRadius = radius + travel / (steps * 2);
        double best = double.PositiveInfinity;
        surface = default;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double distance = SegmentTriangleDistanceSquared(previousA + (a - previousA) * t,
                previousB + (b - previousB) * t, triangle, out var hit);
            if (distance < best) { best = distance; surface = hit; }
        }
        return best <= conservativeRadius * conservativeRadius;
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

internal sealed class CutHeat
{
    public double Seconds { get; private set; }
    public bool Advance(bool contact, double delta, double threshold)
    {
        if (!double.IsFinite(delta) || delta <= 0 || delta > .15) return false;
        Seconds = Math.Max(0, Seconds + delta * (contact ? 1 : -.5));
        return Seconds >= Math.Max(.05, threshold);
    }
}
