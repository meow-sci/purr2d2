namespace Purr2D2.Cutting;

/// <summary>A cut plane request fixed to a confirmed surface contact in assembly coordinates.</summary>
internal readonly record struct ContactSlice(CutPoint Point, CutPoint Normal);

/// <summary>Deterministic geometry for manual cuts. This file has no game or native dependencies.</summary>
internal static class ContactSliceGeometry
{
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
    /// Returns a preferred plane, four small cardinal tilts and four larger diagonal tilts.
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

        var result = new SlicePlane[9];
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
