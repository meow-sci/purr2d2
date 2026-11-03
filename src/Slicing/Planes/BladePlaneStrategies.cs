using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Planes;

internal static class SlicePlaneStrategies
{
    internal static bool TrySelect(SlicePlaneRequest request, IReadOnlyList<CutPoint> axes,
        out SlicePlaneSelection selection, out string reason)
    {
        ISlicePlaneStrategy? strategy = request.StrategyId switch
        {
            "legacy-contact" => new LegacyContactPlaneStrategy(),
            "blade-strict" => new BladePlaneStrategy(BladePlaneMode.Strict),
            "blade-limited" => new BladePlaneStrategy(BladePlaneMode.Limited),
            "sweep-fit" => new BladePlaneStrategy(BladePlaneMode.Fit),
            _ => null
        };
        if (strategy is null) { selection = default; reason = "Unknown contact plane strategy."; return false; }
        return strategy.TrySelect(request, axes, out selection, out reason);
    }
}

internal enum BladePlaneMode { Strict, Limited, Fit }

internal sealed class BladePlaneStrategy(BladePlaneMode mode) : ISlicePlaneStrategy
{
    public bool TrySelect(SlicePlaneRequest request, IReadOnlyList<CutPoint> axes,
        out SlicePlaneSelection selection, out string reason)
    {
        selection = default;
        reason = "DegenerateMotion: contact evidence does not define a cutting plane.";
        if (!request.Anchor.Finite || !TryUnit(request.BladeAtHit, out var blade)) return false;
        bool strictValid = TryUnit(request.MotionAtHit, out var movement);
        var cross = strictValid ? CutPoint.Cross(blade, movement) : default;
        strictValid = strictValid && double.IsFinite(cross.Length) && cross.Length > 1e-4;
        var strict = strictValid ? cross.Normalized : default;
        CutPoint normal;
        double? residual = null;
        if (mode == BladePlaneMode.Fit)
        {
            SliceSweepSamples samples;
            if (request.Samples is { Valid: true } supplied) samples = supplied;
            else if (request.Ribbon is { Valid: true } ribbon) samples = ribbon.ToSamples();
            else return false;
            if (!TryFit(request.Anchor, blade, strictValid, strict, samples, out normal, out residual)) return false;
        }
        else
        {
            if (!strictValid) return false;
            normal = strict;
        }
        var result = new SlicePlaneCandidate[mode == BladePlaneMode.Limited ? 9 : 1];
        result[0] = Make(normal, 0);
        if (mode == BladePlaneMode.Limited)
        {
            AssistedSliceGeometry.Basis(normal, out var u, out var v);
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                var tangent = u * Math.Cos(angle) + v * Math.Sin(angle);
                result[i + 1] = Make((normal * Math.Cos(Math.PI / 36) + tangent * Math.Sin(Math.PI / 36)).Normalized, i + 1);
            }
        }
        selection = new(result, residual);
        reason = "";
        return true;
        SlicePlaneCandidate Make(CutPoint n, int i) => new(new(n, CutPoint.Dot(n, request.Anchor)), i,
            i == 0 ? SlicePlaneFallback.None : SlicePlaneFallback.TiltFallback);
    }

    private static bool TryFit(CutPoint anchor, CutPoint blade, bool strictValid, CutPoint strict,
        SliceSweepSamples samples, out CutPoint normal, out double? residual)
    {
        normal = default; residual = null;
        AssistedSliceGeometry.Basis(blade, out var u, out var v);
        double a = 0, b = 0, c = 0;
        for (int i = 0; i < 9; i++)
        {
            var q = samples.At(i) - anchor;
            double sampleX = CutPoint.Dot(q, u), sampleY = CutPoint.Dot(q, v);
            a += sampleX * sampleX; b += sampleX * sampleY; c += sampleY * sampleY;
        }
        double trace = a + c;
        if (!double.IsFinite(trace) || trace <= 1e-12)
        { if (!strictValid) return false; normal = strict; return true; }
        double gap = Math.Sqrt((a - c) * (a - c) + 4 * b * b);
        if (!double.IsFinite(gap) || gap <= 1e-8 * trace)
        { if (!strictValid) return false; normal = strict; return true; }
        double lambda = Math.Max(0, (trace - gap) * .5);
        // Pick the longer of two equivalent eigenvector forms to avoid cancellation.
        double x1 = b, y1 = lambda - a, x2 = lambda - c, y2 = b;
        double x = x1 * x1 + y1 * y1 >= x2 * x2 + y2 * y2 ? x1 : x2;
        double y = x1 * x1 + y1 * y1 >= x2 * x2 + y2 * y2 ? y1 : y2;
        if (!TryUnit(u * x + v * y, out normal)) return false;
        double sign = strictValid ? CutPoint.Dot(normal, strict) : LexicalSign(normal);
        if (sign < 0) normal *= -1;
        residual = Math.Sqrt(lambda / 9);
        return double.IsFinite(residual.Value);
    }

    private static double LexicalSign(CutPoint p) => Math.Abs(p.X) > 1e-12 ? p.X : Math.Abs(p.Y) > 1e-12 ? p.Y : p.Z;
    private static bool TryUnit(CutPoint value, out CutPoint unit)
    {
        unit = default;
        double length = value.Length;
        if (!value.Finite || !double.IsFinite(length) || length <= 1e-12) return false;
        unit = value / length;
        return unit.Finite;
    }
}
