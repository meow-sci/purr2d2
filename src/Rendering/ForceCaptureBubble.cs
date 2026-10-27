using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>Allocation-free projected 3D dot sphere for the transient Force capture radius.</summary>
internal static class ForceCaptureBubble
{
    private const int MeridianCount = 8;
    private const int LatitudeCount = 8;
    private const int CircleSegments = 64;
    private const int SampleStep = 2;
    private const float DotRadius = 1;
    private const int DotSegments = 6;
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
        double forwardLengthSquared = forward.LengthSquared();
        if (framebuffer.X <= 0 || framebuffer.Y <= 0 || !double.IsFinite(near) || near <= 0
            || !ForceMathFinite(forward) || !double.IsFinite(forwardLengthSquared) || forwardLengthSquared < .5
            || camera.IsSphereOutsideFrustum(centerEgo, radius)) return;

        double clipMinX = viewportOrigin.X, clipMinY = viewportOrigin.Y;
        double clipMaxX = clipMinX + viewportSize.X, clipMaxY = clipMinY + viewportSize.Y;
        if (!double.IsFinite(clipMaxX) || !double.IsFinite(clipMaxY)
            || clipMaxX - clipMinX < DotRadius * 2 || clipMaxY - clipMinY < DotRadius * 2) return;

        var color = dark ? new ImColor8(255, 72, 78, 230) : new ImColor8(110, 255, 174, 230);
        foreach (var circle in Circles)
        {
            for (int sample = 0; sample < CircleSegments; sample += SampleStep)
            {
                var point = centerEgo + circle[sample] * radius;
                if (!ForceMathFinite(point)) continue;
                double depth = double3.Dot(point, forward);
                if (!double.IsFinite(depth) || depth < near) continue;

                var pixel = camera.EgoToScreenDouble(point, ignoreBehind: false);
                if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y)) continue;
                double screenX = viewportOrigin.X + pixel.X * viewportSize.X / framebuffer.X;
                double screenY = viewportOrigin.Y + pixel.Y * viewportSize.Y / framebuffer.Y;
                if (!double.IsFinite(screenX) || !double.IsFinite(screenY)
                    || screenX < clipMinX + DotRadius || screenX > clipMaxX - DotRadius
                    || screenY < clipMinY + DotRadius || screenY > clipMaxY - DotRadius) continue;

                float x = (float)screenX, y = (float)screenY;
                if (!float.IsFinite(x) || !float.IsFinite(y)
                    || x < clipMinX + DotRadius || x > clipMaxX - DotRadius
                    || y < clipMinY + DotRadius || y > clipMaxY - DotRadius) continue;
                draw.AddCircleFilled(new(x, y), DotRadius, color, DotSegments);
            }
        }
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
