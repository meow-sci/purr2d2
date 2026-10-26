using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>Allocation-free projected 3D wire sphere for the transient Force capture radius.</summary>
internal static class ForceCaptureBubble
{
    private const int MeridianCount = 8;
    private const int LatitudeCount = 8;
    private const int CircleSegments = 64;
    private static readonly double3[][] Circles = BuildCircles();

    internal static int CircleCount => Circles.Length;
    internal static int SegmentsPerCircle => CircleSegments;

    internal static void Draw(ImDrawListPtr draw, Camera camera, double3 centerEgo, double radius,
        float2 viewportOrigin, float2 viewportSize, bool dark)
    {
        if (camera == null || !ForceMathFinite(centerEgo) || !double.IsFinite(radius) || radius <= 0
            || !Finite(viewportOrigin) || !Finite(viewportSize) || viewportSize.X <= 0 || viewportSize.Y <= 0) return;
        var framebuffer = camera.FramebufferSize;
        double near = camera.NearPlane;
        var forward = camera.GetForwardEcl();
        if (framebuffer.X <= 0 || framebuffer.Y <= 0 || !double.IsFinite(near) || near <= 0
            || !ForceMathFinite(forward) || forward.LengthSquared() < .5
            || camera.IsSphereOutsideFrustum(centerEgo, radius)) return;

        var color = dark ? new ImColor8(255, 72, 78, 230) : new ImColor8(110, 255, 174, 230);
        var clipMin = viewportOrigin;
        var clipMax = viewportOrigin + viewportSize;
        foreach (var circle in Circles)
        {
            for (int segment = 0; segment < CircleSegments; segment += 2)
            {
                var a = centerEgo + circle[segment] * radius;
                var b = centerEgo + circle[segment + 1] * radius;
                if (!ClipNearPlane(ref a, ref b, forward, near)) continue;
                var pixelA = camera.EgoToScreenDouble(a, ignoreBehind: false);
                var pixelB = camera.EgoToScreenDouble(b, ignoreBehind: false);
                if (!double.IsFinite(pixelA.X) || !double.IsFinite(pixelA.Y)
                    || !double.IsFinite(pixelB.X) || !double.IsFinite(pixelB.Y)) continue;
                var screenA = new double2(viewportOrigin.X + pixelA.X * viewportSize.X / framebuffer.X,
                    viewportOrigin.Y + pixelA.Y * viewportSize.Y / framebuffer.Y);
                var screenB = new double2(viewportOrigin.X + pixelB.X * viewportSize.X / framebuffer.X,
                    viewportOrigin.Y + pixelB.Y * viewportSize.Y / framebuffer.Y);
                if (!ClipViewport(ref screenA, ref screenB, clipMin, clipMax)) continue;
                draw.AddLine(new((float)screenA.X, (float)screenA.Y), new((float)screenB.X, (float)screenB.Y), color, 2);
            }
        }
    }

    internal static bool ClipNearPlane(ref double3 a, ref double3 b, double3 forward, double near)
    {
        if (!ForceMathFinite(a) || !ForceMathFinite(b) || !ForceMathFinite(forward)
            || !double.IsFinite(near) || near <= 0) return false;
        double depthA = double3.Dot(a, forward), depthB = double3.Dot(b, forward);
        if (!double.IsFinite(depthA) || !double.IsFinite(depthB) || depthA < near && depthB < near) return false;
        if (depthA < near || depthB < near)
        {
            double denominator = depthB - depthA;
            if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-15) return false;
            double t = Math.Clamp((near - depthA) / denominator, 0, 1);
            var clipped = a + (b - a) * t;
            if (!ForceMathFinite(clipped)) return false;
            if (depthA < near) a = clipped; else b = clipped;
        }
        return true;
    }

    internal static bool ClipViewport(ref double2 a, ref double2 b, float2 minimum, float2 maximum)
    {
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y)
            || !Finite(minimum) || !Finite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y) return false;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t0 = 0, t1 = 1;
        if (!Clip(-dx, a.X - minimum.X, ref t0, ref t1)
            || !Clip(dx, maximum.X - a.X, ref t0, ref t1)
            || !Clip(-dy, a.Y - minimum.Y, ref t0, ref t1)
            || !Clip(dy, maximum.Y - a.Y, ref t0, ref t1)) return false;
        var origin = a;
        a = new(origin.X + t0 * dx, origin.Y + t0 * dy);
        b = new(origin.X + t1 * dx, origin.Y + t1 * dy);
        return double.IsFinite(a.X) && double.IsFinite(a.Y) && double.IsFinite(b.X) && double.IsFinite(b.Y);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-15) return q >= 0;
        double t = q / p;
        if (!double.IsFinite(t)) return false;
        if (p < 0)
        {
            if (t > t1) return false;
            if (t > t0) t0 = t;
        }
        else
        {
            if (t < t0) return false;
            if (t < t1) t1 = t;
        }
        return true;
    }

    private static double3[][] BuildCircles()
    {
        var circles = new double3[MeridianCount + LatitudeCount][];
        int circle = 0;
        for (int meridian = 0; meridian < MeridianCount; meridian++)
        {
            double longitude = Math.PI * meridian / MeridianCount;
            var points = new double3[CircleSegments + 1];
            for (int step = 0; step <= CircleSegments; step++)
            {
                double angle = Math.PI * 2 * step / CircleSegments;
                double sin = Math.Sin(angle);
                points[step] = new(sin * Math.Cos(longitude), Math.Cos(angle), sin * Math.Sin(longitude));
            }
            circles[circle++] = points;
        }
        for (int latitude = 0; latitude < LatitudeCount; latitude++)
        {
            double angle = -Math.PI * .5 + Math.PI * (latitude + 1) / (LatitudeCount + 1);
            double horizontal = Math.Cos(angle), vertical = Math.Sin(angle);
            var points = new double3[CircleSegments + 1];
            for (int step = 0; step <= CircleSegments; step++)
            {
                double longitude = Math.PI * 2 * step / CircleSegments;
                points[step] = new(horizontal * Math.Cos(longitude), vertical, horizontal * Math.Sin(longitude));
            }
            circles[circle++] = points;
        }
        return circles;
    }

    private static bool ForceMathFinite(double3 value) => double.IsFinite(value.X)
        && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
