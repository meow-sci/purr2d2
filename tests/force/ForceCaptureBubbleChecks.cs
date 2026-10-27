using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Rendering;

internal static class ForceCaptureBubbleChecks
{
    private const int MaxDotCount = 16 * 32;
    private static readonly float2 ViewportSize = new(640, 480);

    internal static void Run()
    {
        Check(ForceCaptureBubble.CircleCount == 16 && ForceCaptureBubble.SegmentsPerCircle == 64,
            "the dotted sphere uses eight meridians and eight latitude rings with fixed tessellation");
        VisibleDotsHaveFixedSizeAndColors();
        FrustumBehindNearPlaneAndOffscreenAreRejected();
        PartialAndCameraInsideSpheresStayFiniteAndBounded();
        WarmProjectionIsBoundedAndAllocationFree();
        Console.WriteLine("Force capture bubble checks passed (bounded two-pixel dot projection with warmed zero-allocation draw).");
    }

    private static void VisibleDotsHaveFixedSizeAndColors()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count == MaxDotCount,
            "a fully visible sphere emits exactly 32 samples on each of its 16 circles");
        Check(ImDrawListPtr.Circles.All(circle => circle.Filled && circle.Radius == 1),
            "each sample is a filled dot with a two UI pixel diameter");
        Check(ImDrawListPtr.Circles.All(circle => circle.Color == new ImColor8(110, 255, 174, 230).Value),
            "light kitten bubbles use the green dot color");
        Check(ImDrawListPtr.Lines.Count == 0,
            "the capture sphere has no connecting wire or dash lines");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: true);
        Check(ImDrawListPtr.Circles.Count == MaxDotCount
            && ImDrawListPtr.Circles.All(circle => circle.Filled && circle.Radius == 1
                && circle.Color == new ImColor8(255, 72, 78, 230).Value),
            "dark kitten bubbles use the red two pixel filled dots");
        Check(ImDrawListPtr.Lines.Count == 0,
            "dark bubbles also avoid wire strokes");
    }

    private static void FrustumBehindNearPlaneAndOffscreenAreRejected()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        camera.SphereOutsideFrustum = true;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count == 0,
            "a sphere outside the frustum is rejected before dot projection");

        ResetDrawLists();
        camera.SphereOutsideFrustum = false;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, 10), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count == 0,
            "surface samples behind the camera are skipped");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -.05), .02,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count == 0,
            "surface samples before the near plane are skipped");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(10000, 10000, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count == 0,
            "projected dots outside the viewport are culled");
    }

    private static void PartialAndCameraInsideSpheresStayFiniteAndBounded()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(-310, 0, -20), 40,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count > 0 && ImDrawListPtr.Circles.Count < MaxDotCount,
            "a large sphere crossing the viewport edge emits only its visible samples");
        CheckDotsStayInsideViewport();

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, double3.Zero, 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count > 0 && ImDrawListPtr.Circles.Count < MaxDotCount,
            "a camera inside a sphere keeps only samples in front of the near plane");
        CheckDotsStayInsideViewport();

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -.1), .5,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count > 0 && ImDrawListPtr.Circles.Count < MaxDotCount,
            "a sphere crossing the near plane retains only its safe visible samples");
        CheckDotsStayInsideViewport();
    }

    private static void WarmProjectionIsBoundedAndAllocationFree()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);
        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Circles.Count <= MaxDotCount,
            "projected work is bounded by 512 dots even for a fully visible sphere");

        int circleCapacity = ImDrawListPtr.Circles.Capacity;
        int eventCapacity = ImDrawListPtr.Events.Capacity;
        ImDrawListPtr.Circles.Clear();
        ImDrawListPtr.Events.Clear();
        ImDrawListPtr.Lines.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after == before,
            $"a warmed bubble projection allocates no per-frame geometry (observed {after - before} bytes; dots {ImDrawListPtr.Circles.Count}/{circleCapacity}->{ImDrawListPtr.Circles.Capacity}, events {eventCapacity}->{ImDrawListPtr.Events.Capacity})");
        Check(ImDrawListPtr.Circles.Count == MaxDotCount && ImDrawListPtr.Lines.Count == 0,
            "the warmed pass emits only the bounded filled dot set");
    }

    private static void CheckDotsStayInsideViewport()
    {
        foreach (var circle in ImDrawListPtr.Circles)
        {
            Check(float.IsFinite(circle.Center.X) && float.IsFinite(circle.Center.Y)
                && circle.Center.X >= circle.Radius && circle.Center.X <= ViewportSize.X - circle.Radius
                && circle.Center.Y >= circle.Radius && circle.Center.Y <= ViewportSize.Y - circle.Radius,
                "every projected dot has finite coordinates and stays fully inside the viewport");
        }
    }

    private static void ResetDrawLists()
    {
        ImDrawListPtr.Circles.Clear();
        ImDrawListPtr.Lines.Clear();
        ImDrawListPtr.Events.Clear();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
