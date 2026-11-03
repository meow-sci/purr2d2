using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Planes;

// Values are captured in the same source-metre coordinates as the copied geometry.
internal readonly record struct SliceRibbonMotion(CutPoint Base0, CutPoint Tip0, CutPoint Base1, CutPoint Tip1,
    double HitTime01, double AlongBlade01)
{
    internal CutPoint Evaluate(double t, double s) =>
        (Base0 * (1 - s) + Tip0 * s) * (1 - t) + (Base1 * (1 - s) + Tip1 * s) * t;
    internal bool Valid => Base0.Finite && Tip0.Finite && Base1.Finite && Tip1.Finite
        && double.IsFinite(HitTime01) && HitTime01 is >= 0 and <= 1
        && double.IsFinite(AlongBlade01) && AlongBlade01 is >= 0 and <= 1;
    internal SliceSweepSamples ToSamples() => new(
        Evaluate(0, 0), Evaluate(0, AlongBlade01), Evaluate(0, 1),
        Evaluate(HitTime01, 0), Evaluate(HitTime01, AlongBlade01), Evaluate(HitTime01, 1),
        Evaluate(1, 0), Evaluate(1, AlongBlade01), Evaluate(1, 1));
}

internal sealed record SlicePlaneRequest(string StrategyId, string Trigger, string TargetKind,
    CutPoint Anchor, CutPoint BladeAtHit, CutPoint MotionAtHit, SliceRibbonMotion? Ribbon = null,
    SliceSweepSamples? Samples = null)
{
    internal bool AllowsLegacyFallbacks => StrategyId == "legacy-contact";
}

internal enum SlicePlaneFallback { None, TiltFallback, AxisFallback }
internal readonly record struct SlicePlaneCandidate(SlicePlane Plane, int Index, SlicePlaneFallback Fallback);
internal readonly record struct SlicePlaneSelection(SlicePlaneCandidate[] Candidates, double? FitResidualMetres = null);

internal interface ISlicePlaneStrategy
{
    bool TrySelect(SlicePlaneRequest request, IReadOnlyList<CutPoint> axes,
        out SlicePlaneSelection selection, out string reason);
}
