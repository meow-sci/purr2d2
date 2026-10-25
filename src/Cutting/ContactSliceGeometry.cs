namespace Purr2D2.Cutting;

/// <summary>A cut plane request fixed to a confirmed surface contact in assembly coordinates.</summary>
internal readonly record struct ContactSlice(CutPoint Point, CutPoint Normal);

/// <summary>Deterministic geometry for manual cuts. This file has no game or native dependencies.</summary>
internal static class ContactSliceGeometry
{
    internal const int CandidateCount = 25; // Preferred plane and bounded sweep-angle adjustments.
    internal const int MaximumCandidateCount = CandidateCount + 3;
    internal const double MaximumTilt = .6;
    private const double MinimumDirectionLength = 1e-12;
    private const double CollinearSine = 1e-4;

    /// <summary>Builds a finite preferred plane normal from the saber direction and its motion.</summary>
    internal static bool TryCreate(CutPoint point, CutPoint bladeDirection, CutPoint motion,
        out ContactSlice contact, out string reason)
    {
        contact = default;
        reason = "The contact cut direction is invalid.";
        if (!point.Finite || !bladeDirection.Finite || !motion.Finite) return false;

        double bladeLength = bladeDirection.Length;
        double motionLength = motion.Length;
        if (!double.IsFinite(bladeLength) || bladeLength <= MinimumDirectionLength
            || !double.IsFinite(motionLength) || motionLength <= MinimumDirectionLength) return false;

        var blade = bladeDirection / bladeLength;
        var movement = motion / motionLength;
        var crossed = CutPoint.Cross(blade, movement);
        var normal = crossed.Length <= CollinearSine ? Perpendicular(blade) : crossed.Normalized;
        if (!normal.Finite || normal.Length <= MinimumDirectionLength) return false;

        contact = new(point, normal);
        reason = "";
        return true;
    }

    /// <summary>
    /// Returns the preferred plane, eight small adjustments and two wider rings of eight tilts.
    /// Every plane passes through the confirmed contact and uses a fixed sequence.
    /// </summary>
    internal static bool TryCandidates(ContactSlice contact, out SlicePlane[] candidates, out string reason)
    {
        candidates = [];
        reason = "The contact point or cut plane is invalid.";
        if (!contact.Point.Finite || !contact.Normal.Finite) return false;
        double length = contact.Normal.Length;
        if (!double.IsFinite(length) || length <= MinimumDirectionLength) return false;

        var normal = contact.Normal / length;
        AssistedSliceGeometry.Basis(normal, out var u, out var v);
        if (!u.Finite || !v.Finite || u.Length <= MinimumDirectionLength || v.Length <= MinimumDirectionLength)
            return false;

        var result = new SlicePlane[CandidateCount];
        double offset = CutPoint.Dot(normal, contact.Point);
        if (!double.IsFinite(offset)) return false;
        result[0] = new(normal, offset);
        CutPoint[] cardinal = [u, u * -1, v, v * -1];
        for (int i = 0; i < cardinal.Length; i++)
        {
            if (!TryTilt(cardinal[i], .06, out result[i + 1])) return false;
        }
        CutPoint[] diagonal = [(u + v).Normalized, (u - v).Normalized,
            (u * -1 + v).Normalized, (u * -1 - v).Normalized];
        for (int i = 0; i < diagonal.Length; i++)
            if (!TryTilt(diagonal[i], .18, out result[i + 5])) return false;

        // Some backed stock panels intersect along an entire range of nearby
        // sections. Search farther only after the original nine planes fail,
        // keeping the contact anchor and a strict 34.4-degree deviation bound.
        double[] widerAngles = [.35, MaximumTilt];
        for (int ring = 0; ring < widerAngles.Length; ring++)
            for (int i = 0; i < 8; i++)
            {
                double azimuth = i * Math.PI / 4;
                var tangent = u * Math.Cos(azimuth) + v * Math.Sin(azimuth);
                if (!TryTilt(tangent, widerAngles[ring], out result[9 + ring * 8 + i])) return false;
            }

        candidates = result;
        reason = "";
        return true;

        bool TryTilt(CutPoint tangent, double angle, out SlicePlane plane)
        {
            var tilted = (normal * Math.Cos(angle) + tangent * Math.Sin(angle)).Normalized;
            if (!tilted.Finite || tilted.Length <= MinimumDirectionLength)
            { plane = default; return false; }
            offset = CutPoint.Dot(tilted, contact.Point);
            if (!double.IsFinite(offset)) { plane = default; return false; }
            plane = new(tilted, offset);
            return true;
        }
    }

    /// <summary>Last resort: actual part-axis sections through the hit, closest to the sweep first.</summary>
    internal static SlicePlane[] AxisCandidates(ContactSlice contact, IReadOnlyList<CutPoint> axes)
    {
        var result = new List<(SlicePlane Plane, double Alignment, int Index)>();
        double preferredLength = contact.Normal.Length;
        if (!contact.Point.Finite || !contact.Normal.Finite || !double.IsFinite(preferredLength)
            || preferredLength <= MinimumDirectionLength) return [];
        var preferred = contact.Normal / preferredLength;
        for (int i = 0; i < Math.Min(3, axes.Count); i++)
        {
            double length = axes[i].Length;
            if (!axes[i].Finite || !double.IsFinite(length) || length <= MinimumDirectionLength) continue;
            var normal = axes[i] / length;
            double alignment = CutPoint.Dot(normal, preferred);
            if (alignment < 0) { normal *= -1; alignment *= -1; }
            double offset = CutPoint.Dot(normal, contact.Point);
            if (double.IsFinite(offset)) result.Add((new(normal, offset), alignment, i));
        }
        return result.OrderByDescending(p => p.Alignment).ThenBy(p => p.Index).Select(p => p.Plane).ToArray();
    }

    private static CutPoint Perpendicular(CutPoint direction)
    {
        // Choose the coordinate axis least aligned to the blade so the cross product
        // remains well-conditioned and produces the same result for identical input.
        var helper = Math.Abs(direction.X) <= Math.Abs(direction.Y) && Math.Abs(direction.X) <= Math.Abs(direction.Z)
            ? new CutPoint(1, 0, 0)
            : Math.Abs(direction.Y) <= Math.Abs(direction.Z) ? new CutPoint(0, 1, 0) : new CutPoint(0, 0, 1);
        return CutPoint.Cross(direction, helper).Normalized;
    }
}
