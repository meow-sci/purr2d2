using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Rendering;

internal static class ForceCaptureBubbleChecks
{
    private const int MaxDashCount = 16 * 32;
    private static readonly float2 ViewportSize = new(640, 480);

    internal static void Run()
    {
        Check(ForceCaptureBubble.CircleCount == 16 && ForceCaptureBubble.SegmentsPerCircle == 64,
            "the dashed sphere uses eight meridians and eight latitude rings with fixed tessellation");
        VisibleDashesHaveFixedWidthAndColors();
        FrustumBehindNearPlaneAndOffscreenAreRejected();
        PartialAndCameraInsideSpheresStayFiniteAndBounded();
        WarmProjectionIsBoundedAndAllocationFree();
        Console.WriteLine("Force capture bubble checks passed (bounded two-pixel-wide dashes with warmed zero-allocation draw).");
    }

    private static void VisibleDashesHaveFixedWidthAndColors()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count == MaxDashCount,
            "a fully visible sphere emits exactly 32 alternating dashes on each of its 16 circles");
        Check(ImDrawListPtr.Lines.All(line => line.Thickness == 2),
            "each dash has a two UI pixel stroke width");
        Check(ImDrawListPtr.Lines.All(line => line.Color == new ImColor8(110, 255, 174, 230).Value),
            "light kitten bubbles use the green dash color");
        Check(ImDrawListPtr.Circles.Count == 0,
            "the capture sphere draws dashes without dot markers");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: true);
        Check(ImDrawListPtr.Lines.Count == MaxDashCount
            && ImDrawListPtr.Lines.All(line => line.Thickness == 2
                && line.Color == new ImColor8(255, 72, 78, 230).Value),
            "dark kitten bubbles use the red two pixel wide dashes");
        Check(ImDrawListPtr.Circles.Count == 0,
            "dark bubbles also avoid dot markers");
    }

    private static void FrustumBehindNearPlaneAndOffscreenAreRejected()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        camera.SphereOutsideFrustum = true;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count == 0,
            "a sphere outside the frustum is rejected before dash projection");

        ResetDrawLists();
        camera.SphereOutsideFrustum = false;
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, 10), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count == 0,
            "segments behind the camera are skipped");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -.05), .02,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count == 0,
            "segments before the near plane are skipped");

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(10000, 10000, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count == 0,
            "projected dashes outside the viewport are culled");
    }

    private static void PartialAndCameraInsideSpheresStayFiniteAndBounded()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(-310, 0, -20), 40,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count > 0 && ImDrawListPtr.Lines.Count < MaxDashCount,
            "a large sphere crossing the viewport edge emits only its clipped segments");
        CheckDashesStayInsideViewport();

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, double3.Zero, 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count > 0 && ImDrawListPtr.Lines.Count < MaxDashCount,
            "a camera inside a sphere keeps only segments in front of the near plane");
        CheckDashesStayInsideViewport();

        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -.1), .5,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count > 0 && ImDrawListPtr.Lines.Count < MaxDashCount,
            "a sphere crossing the near plane retains only its safe clipped segments");
        CheckDashesStayInsideViewport();
    }

    private static void WarmProjectionIsBoundedAndAllocationFree()
    {
        var camera = new Camera();
        var draw = default(ImDrawListPtr);
        ResetDrawLists();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        Check(ImDrawListPtr.Lines.Count <= MaxDashCount,
            "projected work is bounded by 512 dashes even for a fully visible sphere");

        int lineCapacity = ImDrawListPtr.Lines.Capacity;
        int eventCapacity = ImDrawListPtr.Events.Capacity;
        ImDrawListPtr.Lines.Clear();
        ImDrawListPtr.Events.Clear();
        ImDrawListPtr.Circles.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ForceCaptureBubble.Draw(draw, camera, new(0, 0, -20), 2,
            float2.Zero, ViewportSize, dark: false);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after == before,
            $"a warmed bubble projection allocates no per-frame geometry (observed {after - before} bytes; dashes {ImDrawListPtr.Lines.Count}/{lineCapacity}->{ImDrawListPtr.Lines.Capacity}, events {eventCapacity}->{ImDrawListPtr.Events.Capacity})");
        Check(ImDrawListPtr.Lines.Count == MaxDashCount && ImDrawListPtr.Circles.Count == 0,
            "the warmed pass emits only the bounded dash set");
    }

    private static void CheckDashesStayInsideViewport()
    {
        foreach (var line in ImDrawListPtr.Lines)
        {
            Check(float.IsFinite(line.Start.X) && float.IsFinite(line.Start.Y)
                && float.IsFinite(line.End.X) && float.IsFinite(line.End.Y)
                && line.Start.X >= 0 && line.Start.X <= ViewportSize.X
                && line.Start.Y >= 0 && line.Start.Y <= ViewportSize.Y
                && line.End.X >= 0 && line.End.X <= ViewportSize.X
                && line.End.Y >= 0 && line.End.Y <= ViewportSize.Y,
                "every projected dash has finite endpoints clipped inside the viewport");
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
