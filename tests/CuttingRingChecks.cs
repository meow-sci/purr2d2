using Brutal.Numerics;
using Purr2D2.Autopilot;
using Purr2D2.Cutting;

internal static class CuttingRingChecks
{
    public static void Run()
    {
        FindsRingWithoutKittenStation();
        BodyOffsetAndAxisSelection();
        SelectedStationAndPicking();
        RefusesMissingAndIrregularSections();
        RefusesBlockedApproachAndOrbit();
        LargeVesselSearchAndPicking();
        BoundedSearchRetainsOnlyValidatedRings();
        InvalidAndBoundedInput();
    }

    private static void FindsRingWithoutKittenStation()
    {
        var triangles = Cylinder(new(0, 0, 0), 0);
        var bounds = Bounds(triangles);
        Check(CuttingRingGeometry.TryFind(triangles, [bounds], new(30, 0, 0), -1, false, .55,
            out var ring, out string reason), "cylinder ring captured while kitten is beyond its tip: " + reason);
        Near(Math.Abs(ring.Axis.X), 1, "auto axis resolves the elongated cylinder");
        Near(ring.Radius, 1, "outer surface radius is independent of distance from kitten");
        Check(ring.Center.X < 2 && ring.Center.X > -2, "search chooses an interior station");
        var start = ring.Center + ring.Radial * (ring.Radius + .55);
        Check(CuttingRingGeometry.IsPathClear(triangles, new(30, 0, 0), start, .25, out _),
            "selected approach clears the end cap with the whole kitten capsule");
        Check(CuttingRingGeometry.TryFind(triangles, [bounds], new(-100, 8, 0), 0, true, .55,
            out var reversed, out _), "selected reverse axis captures from far beyond the opposite end");
        Near(reversed.Axis.X, -1, "axis reversal is retained in the captured ring");
    }

    private static void BodyOffsetAndAxisSelection()
    {
        var center = new double3(7, -5, 3);
        var triangles = Cylinder(center, 1);
        var bounds = Bounds(triangles);
        Check(!CuttingRingGeometry.TryFind(triangles, [bounds], center + new double3(0, 15, 4), 0, false, .55,
            out _, out _), "wrong manually chosen cylinder axis refuses a rectangular section");
        Check(CuttingRingGeometry.TryFind(triangles, [bounds], center + new double3(0, 15, 4), -1, false, .55,
            out var ring, out _), "auto search finds the correct axis on an offset cylinder");
        Near(Math.Abs(ring.Axis.Y), 1, "Y-aligned cylinder axis selected");
        Near(ring.Center.X, center.X, "ring center preserves perpendicular X offset from vehicle COM");
        Near(ring.Center.Z, center.Z, "ring center preserves perpendicular Z offset from vehicle COM");
        Near(ring.Radius, 1, "translated body-space geometry keeps its actual radius");
    }

    private static void SelectedStationAndPicking()
    {
        var triangles = Cylinder(default, 0);
        var bounds = Bounds(triangles);
        Check(CuttingRingGeometry.TryPick(triangles, new(.6, 5, 0), new(0, -8, 0), out var hit),
            "body-space target picking finds the nearest positive triangle hit");
        Near(hit.X, .6, "pick retains the selected axial position");
        Near(hit.Y, 1, "pick chooses the visible near wall before the far wall");
        Check(CuttingRingGeometry.TryFind(triangles, [bounds], new(10, 5, 0), 0, false, .55,
            out var ring, out _, hit), "clicked hull station can be captured");
        Near(ring.Center.X, .6, "capture follows the exact clicked station");
        Check(CuttingRingGeometry.TryFind(triangles, [bounds], new(10, 5, 0), 0, false, .55,
            out var nudged, out _, ring.Center + ring.Axis * .2), "a ring can be nudged along its axis");
        Near(nudged.Center.X, .8, "nudge preserves the requested station");
        Check(!CuttingRingGeometry.TryFind(triangles, [bounds], new(10, 5, 0), 0, false, .55,
            out _, out string reason, new double3(8, 0, 0)), "invalid selected station refuses instead of falling back elsewhere");
        Check(reason.Contains("selected station"), "station refusal explains the selected-point problem");
        Check(!CuttingRingGeometry.TryPick(triangles, new(0, 5, 0), new(0, 1, 0), out _),
            "picking rejects hits behind the ray origin");
    }

    private static void RefusesMissingAndIrregularSections()
    {
        var open = Cylinder(default, 0, missingSector: true);
        Check(!CuttingRingGeometry.TryFind(open, [Bounds(open)], new(8, 4, 0), 0, false, .55,
            out _, out _), "a missing azimuth sector cannot produce a ring");
        var box = Box(new(-2, -1, -1), new(2, 1, 1));
        Check(!CuttingRingGeometry.TryFind(box, [Bounds(box)], new(8, 4, 0), -1, false, .55,
            out _, out _), "a box is refused because its radial surface variation is too large");
        var flattened = Cylinder(default, 0).Select(t => new CutTriangle(
            new(t.A.X, t.A.Y * 2, t.A.Z), new(t.B.X, t.B.Y * 2, t.B.Z), new(t.C.X, t.C.Y * 2, t.C.Z))).ToArray();
        Check(!CuttingRingGeometry.TryFind(flattened, [Bounds(flattened)], new(8, 4, 0), 0, false, .55,
            out _, out _), "a strongly elliptical hull is refused");
    }

    private static void RefusesBlockedApproachAndOrbit()
    {
        var cylinder = Cylinder(default, 0);
        var wall = Box(new(-4, 2.5, -4), new(4, 2.7, 4));
        var blocked = cylinder.Concat(wall).ToArray();
        Check(!CuttingRingGeometry.TryFind(blocked, [Bounds(cylinder)], new(0, 5, 0), 0, false, .55,
            out _, out _), "a wall across the direct approach prevents automatic capture");
        Check(!CuttingRingGeometry.IsPathClear(blocked, new(0, 5, 0), new(0, 1.55, 0), .25, out _),
            "capsule path helper catches a crossed surface");
        // The obstacle lies between the sampled hull section and the kitten's orbit,
        // away from the three axial planes used to estimate the surface radius.
        var obstacle = Box(new(.13, -.1, 1.4), new(.2, .1, 1.7));
        var orbitBlocked = cylinder.Concat(obstacle).ToArray();
        Check(!CuttingRingGeometry.TryFind(orbitBlocked, [Bounds(cylinder)], new(0, 5, 0), 0, false, .55,
            out _, out _, new double3(0, 0, 0)), "whole-orbit capsule catches a protrusion beside the ring plane");
        Check(CuttingRingGeometry.TryFind(orbitBlocked, [Bounds(cylinder)], new(0, 5, 0), 0, false, .55,
            out var alternate, out _), "automatic search tries another clear station after an obstructed orbit");
        Check(Math.Abs(alternate.Center.X) > .3, "alternate station moves beyond the obstruction");
    }

    private static void InvalidAndBoundedInput()
    {
        Check(!CuttingRingGeometry.TryFind([], [], new(0, 5, 0), -1, false, .55, out _, out _),
            "unavailable geometry refuses capture");
        var cylinder = Cylinder(default, 2);
        Check(!CuttingRingGeometry.TryFind(cylinder, [Bounds(cylinder)], new(double.NaN, 5, 0), -1,
            false, .55, out _, out _), "invalid navigation state refuses capture");
        var invalid = new CutTriangle[] { new(default, default, default) };
        Check(!CuttingRingGeometry.TryFind(invalid, [new(default, new(1, 1, 1))], new(0, 5, 0), -1,
            false, .55, out _, out _), "degenerate geometry refuses capture");
        Check(!CuttingRingGeometry.TryFind(cylinder, Enumerable.Repeat(Bounds(cylinder), AutopilotGeometryLimits.Parts + 1).ToArray(),
            new(0, 5, 0), -1, false, .55, out _, out string reason), "part search count is bounded");
        Check(reason.Contains("limit"), "bounded search failure is actionable");
        Check(!CuttingRingGeometry.TryFind(cylinder, [Bounds(cylinder), new(default, new(double.NaN, 0, 0))],
            new(0, 5, 0), -1, false, .55, out _, out _), "invalid remote bounds cannot bypass validation through ranking");
    }

    private static void LargeVesselSearchAndPicking()
    {
        var cylinder = Cylinder(default, 0);
        // More geometry than both former snapshot/search limits, spatially spread
        // so this also exercises the real BVH rather than one duplicated triangle.
        var remote = Enumerable.Range(0, 400_000).Select(i => new CutTriangle(
            new(100 + i, 100, 100), new(100.5 + i, 100, 100), new(100 + i, 101, 100))).ToArray();
        var triangles = remote.Concat(cylinder).ToArray();
        var bounds = Enumerable.Range(0, 128).Select(i => new CutBounds(new(100 + i, 100, 100), new(101 + i, 101, 100)))
            .Append(Bounds(cylinder)).ToArray();
        Check(CuttingRingGeometry.TryPick(triangles, new(.6, 5, 0), new(0, -1, 0), out var hit),
            "picking supports a target with more than 400,000 triangles");
        Near(hit.X, .6, "large-target picking preserves the requested station");
        Check(CuttingRingGeometry.TryFind(triangles, bounds, new(0, 5, 0), 0, false, .55,
            out var ring, out _, out var surface, hit), "ring search accepts more than 64 part bounds and 400,000 triangles");
        Near(ring.Center.X, .6, "large-target search retains the exact clicked station");
        Check(surface != null && ReferenceEquals(surface.Triangles, triangles), "accepted search reuses its complete collision mesh for flight");
        Check(!CuttingRingGeometry.IsPathClear(surface!, new(400_099.1, 100.1, 99), new(400_099.1, 100.1, 101), .25, out _),
            "remote geometry outside the candidate bounds remains a flight obstacle");
    }

    private static void BoundedSearchRetainsOnlyValidatedRings()
    {
        var cylinder = Cylinder(default, 0);
        var farBoxes = Enumerable.Range(1, 1_700).Select(i => Box(new(i * 10 - 2, -1, -1), new(i * 10 + 2, 1, 1))).ToArray();
        var bounds = farBoxes.Select(Bounds).Append(Bounds(cylinder)).ToArray();
        var triangles = farBoxes.SelectMany(t => t).Concat(cylinder).ToArray();
        Check(CuttingRingGeometry.TryFind(triangles, bounds, new(0, 5, 0), 0, false, .55,
            out var ring, out var reason), "nearby ring wins even when listed after 1,700 other parts");
        Near(ring.Center.X, 0, "nearest bounds are searched before distant parts");
        Check(reason.Contains("work limit"), "later search exhaustion retains a fully checked ring");

        var box = Box(new(-2, -1, -1), new(2, 1, 1));
        Check(!CuttingRingGeometry.TryFind(farBoxes.SelectMany(t => t).Concat(box).ToArray(), bounds,
            new(0, 5, 0), 0, false, .55, out _, out reason), "exhaustion without a validated ring refuses capture");
        Check(reason.Contains("Select a hull point"), "bounded search provides a targeted retry rather than rejecting vessel size");

        var wall = Box(new(-4, 2.5, -4), new(4, 2.7, 4));
        Check(!CuttingRingGeometry.TryFind(triangles.Concat(wall).ToArray(), bounds,
            new(0, 5, 0), 0, false, .55, out _, out _), "large-target candidate ranking never drops obstructing geometry");
    }

    private static CutTriangle[] Cylinder(double3 center, int axis, bool missingSector = false)
    {
        const int segments = 48;
        var result = new List<CutTriangle>();
        for (int i = 0; i < segments; i++)
        {
            if (missingSector && i is >= 6 and < 12) continue;
            double a = i * Math.PI * 2 / segments, b = (i + 1) * Math.PI * 2 / segments;
            CutPoint p = Position(-2, Math.Cos(a), Math.Sin(a)), q = Position(2, Math.Cos(a), Math.Sin(a));
            CutPoint r = Position(2, Math.Cos(b), Math.Sin(b)), s = Position(-2, Math.Cos(b), Math.Sin(b));
            result.Add(new(p, q, r)); result.Add(new(p, r, s));
            result.Add(new(Position(-2, 0, 0), p, s));
            result.Add(new(Position(2, 0, 0), r, q));
        }
        return result.ToArray();

        CutPoint Position(double axial, double u, double v) => axis switch
        {
            0 => new(center.X + axial, center.Y + u, center.Z + v),
            1 => new(center.X + u, center.Y + axial, center.Z + v),
            _ => new(center.X + u, center.Y + v, center.Z + axial),
        };
    }

    private static CutTriangle[] Box(CutPoint min, CutPoint max)
    {
        CutPoint[] p = [min, new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), max, new(min.X, max.Y, max.Z)];
        int[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        return Enumerable.Range(0, indices.Length / 3)
            .Select(i => new CutTriangle(p[indices[i * 3]], p[indices[i * 3 + 1]], p[indices[i * 3 + 2]])).ToArray();
    }

    private static CutBounds Bounds(CutTriangle[] triangles)
    {
        var min = triangles[0].Min; var max = triangles[0].Max;
        foreach (var triangle in triangles) { min = CutPoint.Min(min, triangle.Min); max = CutPoint.Max(max, triangle.Max); }
        return new(min, max);
    }

    private static void Near(double actual, double expected, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) < 1e-7, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cutting ring check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
