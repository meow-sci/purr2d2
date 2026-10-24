namespace Purr2D2.Cutting;

/// <summary>A ruled endpoint ribbon in a target's local frame. Endpoints are sampled with
/// that target's own transform at both instants, rather than mixing moving body frames.</summary>
internal readonly record struct BladeSweep(CutPoint Base0, CutPoint Tip0, CutPoint Base1, CutPoint Tip1, double Tolerance = .0025)
{
    internal const int MaximumSubdivisions = 16;
    internal const double ApproximationMeters = .0025;
    internal bool Finite => Base0.Finite && Tip0.Finite && Base1.Finite && Tip1.Finite;
    internal int Subdivisions { get; } = Count(Base0, Tip0, Base1, Tip1, Tolerance);
    private static int Count(CutPoint a0, CutPoint b0, CutPoint a1, CutPoint b1, double tolerance)
    {
        if (!a0.Finite || !b0.Finite || !a1.Finite || !b1.Finite || !double.IsFinite(tolerance) || tolerance <= 0)
            return MaximumSubdivisions + 1;
        var twist = (b1 - b0) - (a1 - a0);
        for (int count = 1; count <= MaximumSubdivisions; count++)
        {
            bool acceptable = true;
            for (int i = 0; i < count; i++)
            {
                var a = Lerp(a0, a1, i / (double)count); var b = Lerp(b0, b1, i / (double)count);
                var c = Lerp(a0, a1, (i + 1) / (double)count); var d = Lerp(b0, b1, (i + 1) / (double)count);
                // A bilinear patch's maximum signed distance from either planar
                // triangle is its projected twist / 4. Check both planes in every
                // strip; planar swings remain exact even for large rotations.
                double error = Math.Max(Math.Abs(CutPoint.Dot(twist, new CutTriangle(a, b, d).Normal)),
                    Math.Abs(CutPoint.Dot(twist, new CutTriangle(a, d, c).Normal))) / (4 * count);
                if (error > tolerance) { acceptable = false; break; }
            }
            if (acceptable) return count;
        }
        return MaximumSubdivisions + 1;
    }
    internal bool TryHit(CutTriangle surface, out CutPoint hit, out double along)
    {
        hit = default; along = 0;
        int count = Subdivisions;
        if (count > MaximumSubdivisions || !surface.A.Finite || !surface.B.Finite || !surface.C.Finite
            || surface.Normal.LengthSquared < .5) return false;
        // Collinear axial motion has no ribbon area, but its endpoint segments and
        // trajectories still describe real contacts (including a tip thrust).
        if (Segment(Base0, Tip0, surface, out hit)) { along = Fraction(hit, Base0, Tip0); return true; }
        if (Segment(Base1, Tip1, surface, out hit)) { along = Fraction(hit, Base1, Tip1); return true; }
        if (Segment(Tip0, Tip1, surface, out hit)) { along = 1; return true; }
        if (Segment(Base0, Base1, surface, out hit)) { along = 0; return true; }
        for (int i = 0; i < count; i++)
        {
            double t0 = i / (double)count, t1 = (i + 1) / (double)count;
            var a = Lerp(Base0, Base1, t0); var b = Lerp(Tip0, Tip1, t0);
            var c = Lerp(Base0, Base1, t1); var d = Lerp(Tip0, Tip1, t1);
            if (Patch(new(a, b, d), 0, 1, 1, surface, out hit, out along)
                || Patch(new(a, d, c), 0, 1, 0, surface, out hit, out along)) return true;
        }
        return false;
    }
    private static double Fraction(CutPoint hit, CutPoint a, CutPoint b) => (b - a).LengthSquared > 1e-20
        ? Math.Clamp(CutPoint.Dot(hit - a, b - a) / (b - a).LengthSquared, 0, 1) : 0;
    private static CutPoint Lerp(CutPoint a, CutPoint b, double t) => a + (b - a) * t;
    private static bool Patch(CutTriangle ribbon, double sa, double sb, double sc, CutTriangle surface, out CutPoint hit, out double along)
    {
        hit = default; along = 0;
        if (ribbon.Normal.LengthSquared < .5) return false;
        if (!(Segment(ribbon.A, ribbon.B, surface, out hit) || Segment(ribbon.B, ribbon.C, surface, out hit)
            || Segment(ribbon.C, ribbon.A, surface, out hit) || Segment(surface.A, surface.B, ribbon, out hit)
            || Segment(surface.B, surface.C, ribbon, out hit) || Segment(surface.C, surface.A, ribbon, out hit))) return false;
        var e0 = ribbon.B - ribbon.A; var e1 = ribbon.C - ribbon.A; var p = hit - ribbon.A;
        double aa = CutPoint.Dot(e0, e0), ab = CutPoint.Dot(e0, e1), bb = CutPoint.Dot(e1, e1);
        double denominator = aa * bb - ab * ab;
        if (!double.IsFinite(denominator) || denominator <= 1e-24) return false;
        double u = (bb * CutPoint.Dot(p, e0) - ab * CutPoint.Dot(p, e1)) / denominator;
        double v = (aa * CutPoint.Dot(p, e1) - ab * CutPoint.Dot(p, e0)) / denominator;
        along = Math.Clamp(sa * (1 - u - v) + sb * u + sc * v, 0, 1);
        return hit.Finite && double.IsFinite(along);
    }
    // Inclusive two-sided segment/triangle intersection, including coplanar overlaps
    // and edge/vertex grazes. Bounds never count as confirmation.
    internal static bool Segment(CutPoint a, CutPoint b, CutTriangle t, out CutPoint hit)
    {
        hit = default;
        var n = t.Normal;
        if (!a.Finite || !b.Finite || !t.A.Finite || !t.B.Finite || !t.C.Finite || n.LengthSquared < .5) return false;
        double da = CutPoint.Dot(a - t.A, n), db = CutPoint.Dot(b - t.A, n);
        const double epsilon = 1e-8;
        if (Math.Abs(da) <= epsilon && Math.Abs(db) <= epsilon)
        {
            if (Inside(a, t)) { hit = a; return true; }
            if (Inside(b, t)) { hit = b; return true; }
            if (Crosses(a, b, t.A, t.B, n, out hit) || Crosses(a, b, t.B, t.C, n, out hit)
                || Crosses(a, b, t.C, t.A, n, out hit)) return true;
            return false;
        }
        if ((da > epsilon && db > epsilon) || (da < -epsilon && db < -epsilon)) return false;
        double divisor = da - db;
        if (Math.Abs(divisor) <= 1e-20) return false;
        double fraction = Math.Clamp(da / divisor, 0, 1);
        hit = a + (b - a) * fraction;
        return hit.Finite && Inside(hit, t);
    }
    private static bool Inside(CutPoint p, CutTriangle t)
    {
        const double epsilon = -1e-9;
        return CutPoint.Dot(CutPoint.Cross(t.B - t.A, p - t.A), t.Normal) >= epsilon
            && CutPoint.Dot(CutPoint.Cross(t.C - t.B, p - t.B), t.Normal) >= epsilon
            && CutPoint.Dot(CutPoint.Cross(t.A - t.C, p - t.C), t.Normal) >= epsilon;
    }
    private static bool Crosses(CutPoint a, CutPoint b, CutPoint c, CutPoint d, CutPoint normal, out CutPoint hit)
    {
        hit = default;
        var u = b - a; var v = d - c; var delta = c - a;
        double denominator = CutPoint.Dot(CutPoint.Cross(u, v), normal);
        if (Math.Abs(denominator) < 1e-20) return false; // Collinear ends already covered by Inside.
        double s = CutPoint.Dot(CutPoint.Cross(delta, v), normal) / denominator;
        double t = CutPoint.Dot(CutPoint.Cross(delta, u), normal) / denominator;
        if (s < -1e-9 || s > 1 + 1e-9 || t < -1e-9 || t > 1 + 1e-9) return false;
        hit = a + u * Math.Clamp(s, 0, 1); return hit.Finite;
    }
}
