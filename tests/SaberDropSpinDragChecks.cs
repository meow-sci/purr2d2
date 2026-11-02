using Brutal.Numerics;
using Purr2D2.Core;

internal static class SaberDropSpinDragChecks
{
    internal static void Run()
    {
        var drag = new SaberDropSpinDrag();
        float2 center = new(100, 100);
        Check(drag.Begin(center + new float2(0, 12), center, 20, 120) && drag.SpinMultiplier == 0
            && drag.Thumb == center, "a Drop press starts at zero even away from the button center");
        Check(!drag.Begin(center, center, 20, 120), "duplicate presses preserve the active Drop gesture");
        Check(drag.Move(new(500, 172)) && MathF.Abs(drag.SpinMultiplier - 1) < 1e-6f
            && drag.Thumb == center + new float2(0, 60), "half a pill of downward movement selects the previous release spin");
        Check(drag.Move(new(-500, 172)) && drag.SpinMultiplier == 1, "horizontal movement does not change release spin");
        Check(drag.Move(new(100, -500)) && drag.SpinMultiplier == 0, "upward movement clamps release spin at zero");
        Check(drag.Move(new(100, 500)) && drag.SpinMultiplier == 2
            && drag.Thumb == center + new float2(0, 120), "the pill bottom caps release spin at twice the previous spin");
        Check(drag.Finish(new(100, 142), out float selected) && selected == .5f && !drag.Held && drag.SpinMultiplier == 0,
            "release samples its final position and resets the transient control");
        Check(!drag.Finish(new(100, 500), out _), "the same release cannot queue a second drop");
        Check(drag.Begin(center, center, 20, 120) && drag.SpinMultiplier == 0
            && drag.Finish(center, out selected) && selected == 0,
            "a new tap after a spinning drop starts over and releases with zero spin");
        drag.Begin(center, center, 20, 120); drag.Move(new(100, 220)); drag.Cancel();
        Check(!drag.Held && drag.SpinMultiplier == 0 && !drag.Finish(center, out _),
            "cancellation clears spin without authorizing a drop");
        drag.Begin(center, center, 20, 120);
        Check(!drag.Move(new(float.NaN, 130)) && drag.SpinMultiplier == 0
            && !drag.Finish(new(float.PositiveInfinity, 130), out _) && !drag.Held,
            "nonfinite release input cancels instead of dropping with an old value");
        foreach (float invalid in new[] { 0, -1, float.NaN, float.PositiveInfinity })
            Check(!drag.Begin(center, center, invalid, 120) && !drag.Begin(center, center, 20, invalid),
                "invalid radius or travel cannot capture the Drop control");
        Check(!drag.Begin(new(121, 100), center, 20, 120)
            && !drag.Begin(new(float.NaN, 100), center, 20, 120), "Drop capture requires a finite press inside its round button");
        Check(drag.Begin(new(0, -float.MaxValue * .5f), new(0, -float.MaxValue * .5f), 1e33f, 1e34f)
            && drag.Move(new(0, float.MaxValue)) && drag.SpinMultiplier == 2,
            "opposite finite pointer extremes clamp without overflowing the spin value");
        drag.Cancel();

        var layout = SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 3);
        Check(drag.Begin(layout.DropCenter, layout.DropCenter, layout.IgniteRadius, layout.DropPillTravel)
            && drag.Matches(layout) && !drag.Matches(layout with { SwingCenter = layout.SwingCenter + new float2(0, 1) })
            && !drag.Matches(default), "Drop capture rejects moved, resized or missing layout geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Drop spin drag check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
