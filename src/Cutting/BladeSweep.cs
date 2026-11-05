namespace Purr2D2.Cutting;

/// <summary>A ruled endpoint ribbon in a target's local frame. Endpoints are sampled with
/// that target's own transform at both instants, rather than mixing moving body frames.</summary>
internal readonly record struct BladeSweep(CutPoint Base0, CutPoint Tip0, CutPoint Base1, CutPoint Tip1, double Tolerance = .0025)
{
    internal const int MaximumSubdivisions = 16;
    internal const double ApproximationMeters = .0025;
    private const double TimeTieEpsilon = 1e-10;
    internal bool Finite => Base0.Finite && Tip0.Finite && Base1.Finite && Tip1.Finite;
    internal int Subdivisions { get; } = Count(Base0, Tip0, Base1, Tip1, Tolerance);

    private readonly record struct ParamTriangle(CutTriangle Triangle, double TimeA, double TimeB, double TimeC,
        double AlongA, double AlongB, double AlongC, bool EdgeMapped = false)
    {
        internal static ParamTriangle Edge(CutPoint a, CutPoint b, double timeA, double timeB, double alongA, double alongB) =>
            new(new(a, b, a), timeA, timeB, timeA, alongA, alongB, alongA, true);
    }

    private struct Hit
    {
        internal bool Found;
        internal CutPoint Point;
        internal double Time, Along;
    }

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
                // triangle is its projected twist / 4. Check both planes in every strip;
                // planar swings remain exact even for large rotations.
                double error = Math.Max(Math.Abs(CutPoint.Dot(twist, new CutTriangle(a, b, d).Normal)),
                    Math.Abs(CutPoint.Dot(twist, new CutTriangle(a, d, c).Normal))) / (4 * count);
                if (error > tolerance) { acceptable = false; break; }
            }
            if (acceptable) return count;
        }
        return MaximumSubdivisions + 1;
    }

    // Keep large twisted intervals bounded without discarding their motion. Every
    // consumer uses the same at-most-16 pieces and original interval time mapping.
    internal bool TrySplit(List<(BladeSweep Sweep, double Start, double End)> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        intervals.Clear();
        if (Finite && double.IsFinite(Tolerance) && Tolerance > 0 && Split(this, 0, 1, 0, intervals)) return true;
        intervals.Clear();
        return false;
    }

    private static bool Split(BladeSweep sweep, double start, double end, int depth,
        List<(BladeSweep Sweep, double Start, double End)> intervals)
    {
        if (sweep.Subdivisions <= MaximumSubdivisions)
        { intervals.Add((sweep, start, end)); return true; }
        if (depth == 4) return false;
        var baseMid = (sweep.Base0 + sweep.Base1) * .5;
        var tipMid = (sweep.Tip0 + sweep.Tip1) * .5;
        double middle = (start + end) * .5;
        return Split(new(sweep.Base0, sweep.Tip0, baseMid, tipMid, sweep.Tolerance), start, middle, depth + 1, intervals)
            && Split(new(baseMid, tipMid, sweep.Base1, sweep.Tip1, sweep.Tolerance), middle, end, depth + 1, intervals);
    }

    // Preserve the original API for callers that only need a confirmed point.
    internal bool TryHit(CutTriangle surface, out CutPoint hit, out double along) => TryHit(surface, out hit, out along, out _);

    // Returns the earliest contact on this surface triangle. A ribbon strip is affine in
    // time and blade position, so the minimum lies at an intersection polygon endpoint.
    internal bool TryHit(CutTriangle surface, out CutPoint hit, out double along, out double time)
    {
        hit = default; along = time = 0;
        int count = Subdivisions;
        // Most vessel faces miss the ribbon bounds. Reject them before normalizing
        // the surface cross product; bounds remain a rejection, never a hit.
        if (count > MaximumSubdivisions || !Finite || !surface.A.Finite || !surface.B.Finite || !surface.C.Finite
            || !BoundsOverlap(Min, Max, surface.Min, surface.Max) || surface.Normal.LengthSquared < .5) return false;

        Hit best = default;
        for (int i = 0; i < count; i++)
        {
            double t0 = i / (double)count, t1 = (i + 1) / (double)count;
            if (best.Found && t0 > best.Time + TimeTieEpsilon) break;

            var a = Lerp(Base0, Base1, t0); var b = Lerp(Tip0, Tip1, t0);
            var c = Lerp(Base0, Base1, t1); var d = Lerp(Tip0, Tip1, t1);
            // The outer edges also cover zero-area axial/thrust ribbons. They are cheap
            // and are checked strip-by-strip so a proven earlier hit avoids later strips.
            CollectSegmentHits(a, b, surface, ParamTriangle.Edge(a, b, t0, t0, 0, 1), ref best);
            CollectSegmentHits(c, d, surface, ParamTriangle.Edge(c, d, t1, t1, 0, 1), ref best);
            CollectSegmentHits(b, d, surface, ParamTriangle.Edge(b, d, t0, t1, 1, 1), ref best);
            CollectSegmentHits(a, c, surface, ParamTriangle.Edge(a, c, t0, t1, 0, 0), ref best);

            var first = new CutTriangle(a, b, d);
            var second = new CutTriangle(a, d, c);
            CollectPatch(first, surface, t0, t0, t1, 0, 1, 1, ref best);
            CollectPatch(second, surface, t0, t1, t1, 0, 1, 0, ref best);

            // Every later strip starts at t1. Once the best hit is strictly earlier,
            // neither a later time nor an equal-time along-blade tie can improve it.
            if (best.Found && t1 > best.Time + TimeTieEpsilon) break;
        }

        if (!best.Found) return false;
        hit = best.Point; along = best.Along; time = best.Time;
        return true;
    }

    private CutPoint Min => CutPoint.Min(CutPoint.Min(Base0, Tip0), CutPoint.Min(Base1, Tip1));
    private CutPoint Max => CutPoint.Max(CutPoint.Max(Base0, Tip0), CutPoint.Max(Base1, Tip1));
    private static CutPoint Lerp(CutPoint a, CutPoint b, double t) => a + (b - a) * t;

    private static void CollectPatch(CutTriangle ribbon, CutTriangle surface,
        double timeA, double timeB, double timeC, double alongA, double alongB, double alongC, ref Hit best)
    {
        // Bounds and a one-sided plane test reject most misses before edge intersection work.
        if (!BoundsOverlap(ribbon.Min, ribbon.Max, surface.Min, surface.Max) || ribbon.Normal.LengthSquared < .5) return;
        var n = surface.Normal;
        double da = CutPoint.Dot(ribbon.A - surface.A, n), db = CutPoint.Dot(ribbon.B - surface.A, n), dc = CutPoint.Dot(ribbon.C - surface.A, n);
        const double epsilon = 1e-8;
        if ((da > epsilon && db > epsilon && dc > epsilon) || (da < -epsilon && db < -epsilon && dc < -epsilon)) return;

        var mapping = new ParamTriangle(ribbon, timeA, timeB, timeC, alongA, alongB, alongC);
        CollectSegmentHits(ribbon.A, ribbon.B, surface, mapping, ref best);
        CollectSegmentHits(ribbon.B, ribbon.C, surface, mapping, ref best);
        CollectSegmentHits(ribbon.C, ribbon.A, surface, mapping, ref best);
        CollectSegmentHits(surface.A, surface.B, ribbon, mapping, ref best);
        CollectSegmentHits(surface.B, surface.C, ribbon, mapping, ref best);
        CollectSegmentHits(surface.C, surface.A, ribbon, mapping, ref best);
    }

    // Collect every possible endpoint of the segment/triangle intersection. In a
    // coplanar overlap this includes contained endpoints, edge crossings, and collinear
    // triangle vertices, so the caller's time/along ordering is independent of edge order.
    private static void CollectSegmentHits(CutPoint a, CutPoint b, CutTriangle target, ParamTriangle mapping, ref Hit best)
    {
        if (!a.Finite || !b.Finite || !target.A.Finite || !target.B.Finite || !target.C.Finite
            || !BoundsOverlap(CutPoint.Min(a, b), CutPoint.Max(a, b), target.Min, target.Max)) return;
        var n = target.Normal;
        if (n.LengthSquared < .5) return;
        double da = CutPoint.Dot(a - target.A, n), db = CutPoint.Dot(b - target.A, n);
        const double epsilon = 1e-8;
        if (Math.Abs(da) <= epsilon && Math.Abs(db) <= epsilon)
        {
            if (Inside(a, target)) Consider(a, mapping, ref best);
            if (Inside(b, target)) Consider(b, mapping, ref best);
            if (Crosses(a, b, target.A, target.B, n, out var hit)) Consider(hit, mapping, ref best);
            if (Crosses(a, b, target.B, target.C, n, out hit)) Consider(hit, mapping, ref best);
            if (Crosses(a, b, target.C, target.A, n, out hit)) Consider(hit, mapping, ref best);
            if (OnSegment(target.A, a, b)) Consider(target.A, mapping, ref best);
            if (OnSegment(target.B, a, b)) Consider(target.B, mapping, ref best);
            if (OnSegment(target.C, a, b)) Consider(target.C, mapping, ref best);
            return;
        }
        if ((da > epsilon && db > epsilon) || (da < -epsilon && db < -epsilon)) return;
        double divisor = da - db;
        if (Math.Abs(divisor) <= 1e-20) return;
        double fraction = Math.Clamp(da / divisor, 0, 1);
        var point = a + (b - a) * fraction;
        if (point.Finite && Inside(point, target)) Consider(point, mapping, ref best);
    }

    private static void Consider(CutPoint point, ParamTriangle mapping, ref Hit best)
    {
        double time, along;
        if (mapping.EdgeMapped)
        {
            double fraction = Fraction(point, mapping.Triangle.A, mapping.Triangle.B);
            time = mapping.TimeA + (mapping.TimeB - mapping.TimeA) * fraction;
            along = mapping.AlongA + (mapping.AlongB - mapping.AlongA) * fraction;
        }
        else
        {
            var triangle = mapping.Triangle;
            var e0 = triangle.B - triangle.A; var e1 = triangle.C - triangle.A; var p = point - triangle.A;
            double aa = CutPoint.Dot(e0, e0), ab = CutPoint.Dot(e0, e1), bb = CutPoint.Dot(e1, e1);
            double denominator = aa * bb - ab * ab;
            if (!double.IsFinite(denominator) || denominator <= 1e-24) return;
            double u = (bb * CutPoint.Dot(p, e0) - ab * CutPoint.Dot(p, e1)) / denominator;
            double v = (aa * CutPoint.Dot(p, e1) - ab * CutPoint.Dot(p, e0)) / denominator;
            double wa = 1 - u - v;
            time = wa * mapping.TimeA + u * mapping.TimeB + v * mapping.TimeC;
            along = wa * mapping.AlongA + u * mapping.AlongB + v * mapping.AlongC;
        }
        AddHit(point, time, along, ref best);
    }

    private static void AddHit(CutPoint point, double time, double along, ref Hit best)
    {
        if (!point.Finite || !double.IsFinite(time) || !double.IsFinite(along)) return;
        time = Math.Clamp(time, 0, 1); along = Math.Clamp(along, 0, 1);
        bool earlier = !best.Found || time < best.Time - TimeTieEpsilon;
        bool sameTime = best.Found && Math.Abs(time - best.Time) <= TimeTieEpsilon;
        bool lowerAlong = sameTime && along < best.Along - TimeTieEpsilon;
        bool sameAlong = sameTime && Math.Abs(along - best.Along) <= TimeTieEpsilon;
        bool stablePoint = sameAlong && Lexicographic(point, best.Point) < 0;
        if (!earlier && !lowerAlong && !stablePoint) return;
        best = new() { Found = true, Point = point, Time = time, Along = along };
    }

    private static int Lexicographic(CutPoint a, CutPoint b)
    {
        int x = a.X.CompareTo(b.X); if (x != 0) return x;
        int y = a.Y.CompareTo(b.Y); return y != 0 ? y : a.Z.CompareTo(b.Z);
    }

    private static double Fraction(CutPoint point, CutPoint a, CutPoint b)
    {
        var direction = b - a;
        return direction.LengthSquared > 1e-20
            ? Math.Clamp(CutPoint.Dot(point - a, direction) / direction.LengthSquared, 0, 1) : 0;
    }

    private static bool BoundsOverlap(CutPoint minA, CutPoint maxA, CutPoint minB, CutPoint maxB)
    {
        const double epsilon = 1e-8;
        return minA.X <= maxB.X + epsilon && maxA.X + epsilon >= minB.X
            && minA.Y <= maxB.Y + epsilon && maxA.Y + epsilon >= minB.Y
            && minA.Z <= maxB.Z + epsilon && maxA.Z + epsilon >= minB.Z;
    }

    // Inclusive two-sided segment/triangle intersection, including coplanar overlaps
    // and edge/vertex grazes. Bounds never count as confirmation.
    internal static bool Segment(CutPoint a, CutPoint b, CutTriangle t, out CutPoint hit)
    {
        hit = default;
        Hit best = default;
        CollectSegmentHits(a, b, t, ParamTriangle.Edge(a, b, 0, 1, 0, 1), ref best);
        if (!best.Found) return false;
        hit = best.Point;
        return true;
    }

    private static bool Inside(CutPoint p, CutTriangle t)
    {
        const double epsilon = -1e-9;
        return CutPoint.Dot(CutPoint.Cross(t.B - t.A, p - t.A), t.Normal) >= epsilon
            && CutPoint.Dot(CutPoint.Cross(t.C - t.B, p - t.B), t.Normal) >= epsilon
            && CutPoint.Dot(CutPoint.Cross(t.A - t.C, p - t.C), t.Normal) >= epsilon;
    }

    private static bool OnSegment(CutPoint point, CutPoint a, CutPoint b)
    {
        var direction = b - a; double lengthSquared = direction.LengthSquared;
        if (lengthSquared <= 1e-20) return (point - a).LengthSquared <= 1e-16;
        double fraction = CutPoint.Dot(point - a, direction) / lengthSquared;
        if (fraction < -1e-9 || fraction > 1 + 1e-9) return false;
        var delta = point - (a + direction * Math.Clamp(fraction, 0, 1));
        return delta.LengthSquared <= 1e-16 * Math.Max(1, lengthSquared);
    }

    private static bool Crosses(CutPoint a, CutPoint b, CutPoint c, CutPoint d, CutPoint normal, out CutPoint hit)
    {
        hit = default;
        var u = b - a; var v = d - c; var delta = c - a;
        double denominator = CutPoint.Dot(CutPoint.Cross(u, v), normal);
        if (Math.Abs(denominator) < 1e-20) return false; // Collinear ends are covered by Inside/OnSegment.
        double s = CutPoint.Dot(CutPoint.Cross(delta, v), normal) / denominator;
        double t = CutPoint.Dot(CutPoint.Cross(delta, u), normal) / denominator;
        if (s < -1e-9 || s > 1 + 1e-9 || t < -1e-9 || t > 1 + 1e-9) return false;
        hit = a + u * Math.Clamp(s, 0, 1);
        return hit.Finite;
    }
}
