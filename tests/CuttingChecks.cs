using Purr2D2.Cutting;

internal static class CuttingChecks
{
    public static void Run()
    {
        var triangle = new CutTriangle(new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1));
        Check(CutGeometry.SegmentTriangleDistanceSquared(new(0, -1, 0), new(0, 1, 0), triangle, out _) < 1e-18, "finite blade crosses hull face");
        Check(Math.Abs(CutGeometry.SegmentTriangleDistanceSquared(new(0, .2, 0), new(0, .5, 0), triangle, out _) - .04) < 1e-12, "blade above face has physical clearance");
        Check(CutGeometry.SegmentTriangleDistanceSquared(new(-2, 0, 0), new(2, 0, 0), triangle, out _) < 1e-18, "coplanar blade crossing is detected");
        Check(CutGeometry.SegmentTriangleDistanceSquared(new(0, 0, 0), new(0, 0, 0), triangle, out _) < 1e-18, "stationary point on surface is detected");
        Check(!CutGeometry.SweepContact(new(0, .5, 0), new(0, 1, 0), new(.1, .5, 0), new(.1, 1, 0), triangle, .025, out _), "parallel movement clear of hull misses");
        Check(CutGeometry.SweepContact(new(0, -.2, 0), new(0, -.1, 0), new(0, .2, 0), new(0, .3, 0), triangle, .025, out _), "fast sweep cannot tunnel between endpoint samples");
        Check(CutGeometry.MotionWithinLimit(new(0, 0, 0), new(0, 1, 0), new(.1, 0, 0), new(.1, 1, 0), .75), "ordinary blade movement retains sweep");
        Check(!CutGeometry.MotionWithinLimit(new(0, 0, 0), new(0, 1, 0), new(10, 0, 0), new(10, 1, 0), .75), "teleport rejects entire contact step");
        Check(!CutGeometry.MotionWithinLimit(new(double.NaN, 0, 0), new(0, 1, 0), new(0, 0, 0), new(0, 1, 0), .75), "invalid transform never authorizes a contact");
        var triangles = Enumerable.Range(0, 50).Select(i => new CutTriangle(new(i * 3, 0, 0), new(i * 3 + 1, 0, 0), new(i * 3, 1, 0))).ToArray();
        var mesh = new CutMesh(triangles);
        var found = new List<int>();
        mesh.Query(new(new(29.9, -.1, -.1), new(31.1, 1.1, .1)), found);
        Check(found.Count == 1 && found[0] == 10, "BVH retains exact nearby candidate");
        var heat = new CutHeat();
        Check(!heat.Advance(true, 0, .3), "pause produces no damage");
        Check(!heat.Advance(true, 10, .3), "warp step produces no damage");
        Check(!heat.Advance(true, .1, .3), "single contact below threshold");
        Check(!heat.Advance(false, .1, .3) && Math.Abs(heat.Seconds - .05) < 1e-12, "interrupted contact cools");
        Check(!heat.Advance(true, .1, .3), "heat accumulation uses elapsed time");
        Check(!heat.Advance(true, .1, .3), "heat remains below threshold");
        Check(heat.Advance(true, .1, .3), "sustained contact crosses threshold");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cutting check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
