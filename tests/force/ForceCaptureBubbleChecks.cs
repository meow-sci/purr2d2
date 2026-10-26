using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Rendering;

internal static class ForceCaptureBubbleChecks
{
    internal static void Run()
    {
        Check(ForceCaptureBubble.CircleCount == 16 && ForceCaptureBubble.SegmentsPerCircle == 64,
            "the wire sphere uses eight distinct meridians and eight latitude rings at fixed tessellation");
        NearPlaneClipping();
        ViewportClipping();
        ProjectedDrawIsBoundedAndAllocationFree();
        Console.WriteLine("Force capture bubble checks passed (bounded clipping and warmed no-allocation projection).");
    }

    private static void NearPlaneClipping()
    {
        var forward = -double3.UnitZ;
        var behindA = new double3(0, 0, 1);
        var behindB = new double3(0, 0, 2);
        Check(!ForceCaptureBubble.ClipNearPlane(ref behindA, ref behindB, forward, .1),
            "a segment fully behind the near plane is hidden");

        var crossingA = new double3(1, 0, 1);
        var crossingB = new double3(3, 0, -1);
        Check(ForceCaptureBubble.ClipNearPlane(ref crossingA, ref crossingB, forward, .1),
            "a segment crossing the near plane keeps its visible portion");
        Check(Math.Abs(double3.Dot(crossingA, forward) - .1) < 1e-12
            && double3.Dot(crossingB, forward) >= .1,
            "near-plane clipping produces a finite endpoint on the camera's current near plane");
    }

    private static void ViewportClipping()
    {
        var a = new double2(-1000, 240);
        var b = new double2(1000, 240);
        Check(ForceCaptureBubble.ClipViewport(ref a, ref b, new(0, 0), new(640, 480)),
            "a projected line crossing the viewport is retained");
        Check(a.X == 0 && b.X == 640 && a.Y == 240 && b.Y == 240,
            "viewport clipping bounds both ends before drawing");
        a = new(-40, -50); b = new(-10, -10);
        Check(!ForceCaptureBubble.ClipViewport(ref a, ref b, new(0, 0), new(640, 480)),
            "a projected line fully outside the viewport is discarded");
    }

    private static void ProjectedDrawIsBoundedAndAllocationFree()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);
        ImDrawListPtr.Lines.Clear();
        for (int i = 0; i < 3; i++)
        {
            ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
                float2.Zero, new(640, 480), dark: false);
            ImDrawListPtr.Lines.Clear();
        }
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, new(640, 480), dark: true);
        Check(ImDrawListPtr.Lines.Count > 0, "a visible capture sphere emits wire dashes");
        foreach (var line in ImDrawListPtr.Lines)
            Check(line.Start.X >= 0 && line.Start.X <= 640 && line.End.X >= 0 && line.End.X <= 640
                && line.Start.Y >= 0 && line.Start.Y <= 480 && line.End.Y >= 0 && line.End.Y <= 480
                && line.Thickness == 2,
                "every projected dash stays within the UI viewport");
        Check(ImDrawListPtr.Lines.All(line => line.Color == new ImColor8(255, 72, 78, 230).Value),
            "dark kitten bubbles use the red two pixel wire color");
        ImDrawListPtr.Lines.Clear();
        camera.SphereOutsideFrustum = true;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, new(640, 480), dark: false);
        Check(ImDrawListPtr.Lines.Count == 0, "a sphere outside the camera frustum is rejected before projection");
        camera.SphereOutsideFrustum = false;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, new(640, 480), dark: false);
        Check(ImDrawListPtr.Lines.All(line => line.Color == new ImColor8(110, 255, 174, 230).Value),
            "light kitten bubbles use the green two pixel wire color");
        ImDrawListPtr.Lines.Clear();
        ImDrawListPtr.Events.Clear();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, new(640, 480), dark: false);
        int lineCapacity = ImDrawListPtr.Lines.Capacity, eventCapacity = ImDrawListPtr.Events.Capacity;
        ImDrawListPtr.Lines.Clear();
        ImDrawListPtr.Events.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, new(640, 480), dark: false);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after == before, $"a warmed bubble projection allocates no per-frame geometry (observed {after - before} bytes; lines {ImDrawListPtr.Lines.Count}/{lineCapacity}->{ImDrawListPtr.Lines.Capacity}, events {eventCapacity}->{ImDrawListPtr.Events.Capacity})");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
