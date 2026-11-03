using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Planes;

/// <summary>Nine immutable samples at times 0/hit/1 and blade positions 0/hit/1.</summary>
internal readonly record struct SliceSweepSamples(CutPoint P00, CutPoint P01, CutPoint P02,
    CutPoint P10, CutPoint P11, CutPoint P12, CutPoint P20, CutPoint P21, CutPoint P22)
{
    internal CutPoint At(int index) => index switch
    {
        0 => P00, 1 => P01, 2 => P02, 3 => P10, 4 => P11,
        5 => P12, 6 => P20, 7 => P21, 8 => P22,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    internal bool Valid => P00.Finite && P01.Finite && P02.Finite && P10.Finite && P11.Finite
        && P12.Finite && P20.Finite && P21.Finite && P22.Finite;
    internal SliceSweepSamples Map(Func<CutPoint, CutPoint> transform) => new(
        transform(P00), transform(P01), transform(P02), transform(P10), transform(P11),
        transform(P12), transform(P20), transform(P21), transform(P22));
}
