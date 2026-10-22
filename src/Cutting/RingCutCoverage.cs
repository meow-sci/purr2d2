namespace Purr2D2.Cutting;

/// <summary>Permanent coverage of one selected plane, independent of heat and visual scorch lifetime.</summary>
internal sealed class RingCutCoverage
{
    private const double Turn = 2 * Math.PI;
    private readonly CutPoint _center, _axis, _radial, _tangent;
    private readonly double _radius;
    private readonly List<(double Start, double End)> _arcs = new();
    private double? _previous;

    internal RingCutCoverage(CutPoint center, CutPoint axis, CutPoint radial, double radius)
    {
        _center = center; _axis = axis.Normalized; _radial = radial.Normalized;
        _tangent = CutPoint.Cross(_axis, _radial).Normalized; _radius = radius;
    }

    internal double Progress => Math.Clamp(_arcs.Sum(a => a.End - a.Start) / Turn, 0, 1);
    internal bool Complete => Progress >= 1 - 1e-9;
    internal void BreakStroke() => _previous = null;

    internal bool Add(CutPoint hit)
    {
        var offset = hit - _center;
        double axial = CutPoint.Dot(offset, _axis);
        double x = CutPoint.Dot(offset, _radial), y = CutPoint.Dot(offset, _tangent);
        double radius = Math.Sqrt(x * x + y * y);
        if (!hit.Finite || !double.IsFinite(_radius) || _radius <= .025 || Math.Abs(axial) > .05
            || Math.Abs(radius - _radius) > Math.Max(.1, _radius * .1))
        { BreakStroke(); return false; }
        double angle = Math.Atan2(y, x);
        if (angle < 0) angle += Turn;
        double halfWidth = Math.Atan2(.025, radius);
        double start = angle - halfWidth, end = angle + halfWidth;
        if (_previous is { } previous)
        {
            double delta = Math.IEEERemainder(angle - previous, Turn);
            // Never bridge missed contact, a teleport, or a large angular jump.
            if (Math.Abs(delta) < .15 && Math.Abs(delta) * radius < .15)
            { start = Math.Min(angle, angle - delta) - halfWidth; end = Math.Max(angle, angle - delta) + halfWidth; }
        }
        _previous = angle;
        if (start < 0) { Merge(start + Turn, Turn); start = 0; }
        if (end > Turn) { Merge(0, end - Turn); end = Turn; }
        Merge(start, end);
        return true;
    }

    private void Merge(double start, double end)
    {
        int index = 0;
        while (index < _arcs.Count && _arcs[index].End < start) index++;
        while (index < _arcs.Count && _arcs[index].Start <= end)
        {
            start = Math.Min(start, _arcs[index].Start); end = Math.Max(end, _arcs[index].End);
            _arcs.RemoveAt(index);
        }
        _arcs.Insert(index, (start, end));
    }
}
