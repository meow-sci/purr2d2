using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Force;

internal static class ForceCaptureRadiusDragChecks
{
    private static int _checks;

    internal static void Run()
    {
        Check(ForceMath.DefaultCaptureRadius == 2, "a new Force capture starts with a two metre radius");
        ClickAndThreshold();
        HighWaterAndRegrab();
        InvalidInputAndCancellation();
        LayoutMatches();
        Console.WriteLine($"Force capture radius drag checks passed ({_checks} assertions).");
    }

    private static void ClickAndThreshold()
    {
        var drag = new ForceCaptureRadiusDrag();
        var layout = Layout();
        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel, ForceMath.DefaultCaptureRadius), "a normal Angry press starts a transient radius gesture");
        Check(!drag.Dragged && drag.Radius == 2, "press alone keeps the two metre radius and remains a click");
        Check(drag.Move(layout.AngryCenter + new float2(3, 0)) && !drag.Dragged,
            "movement below four logical pixels remains a click");
        Check(drag.Finish(out bool dragged, out double radius) && !dragged && radius == 2,
            "click release completes without raising its radius");
        Check(!drag.Finish(out _, out _), "one pointer release cannot finish the gesture twice");

        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel, 2), "a fresh gesture can begin after a click");
        Check(drag.Move(layout.AngryCenter + new float2(0, -4)) && drag.Dragged,
            "the four pixel threshold starts the drag at its exact boundary");
        Check(drag.Radius > 2, "upward movement past the threshold begins growing the radius");
    }

    private static void HighWaterAndRegrab()
    {
        var layout = Layout();
        var drag = new ForceCaptureRadiusDrag();
        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel, 2), "high-water gesture begins from the current hold radius");
        drag.Move(layout.AngryCenter + new float2(0, -layout.SliderTravel * .5f));
        double halfway = drag.Radius;
        Check(Math.Abs(halfway - 26) < 1e-9, "half the upward travel adds half the available radius");
        drag.Move(layout.AngryCenter + new float2(0, layout.SliderTravel));
        Check(drag.Radius == halfway, "downward motion cannot reduce a grown radius");
        drag.Move(layout.AngryCenter + new float2(0, -layout.SliderTravel * .25f));
        Check(drag.Radius == halfway, "returning upward below the previous high-water point does not shrink");
        drag.Move(layout.AngryCenter + new float2(0, -layout.SliderTravel * 4));
        Check(drag.Radius == ForceCaptureRadiusDrag.MaximumRadius, "large upward movement clamps at the existing 50 metre maximum");
        Check(drag.Finish(out bool dragged, out double maximum) && dragged && maximum == 50,
            "release after any drag is distinguished from a click and returns its maximum radius");

        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius,
            layout.SliderTravel, halfway), "a re-grab starts from the current radius");
        drag.Move(layout.AngryCenter + new float2(0, -layout.SliderTravel * .25f));
        Check(Math.Abs(drag.Radius - 38) < 1e-9, "a re-grab scales upward from the existing radius rather than resetting to two metres");
    }

    private static void InvalidInputAndCancellation()
    {
        var drag = new ForceCaptureRadiusDrag();
        var layout = Layout();
        Check(!drag.Begin(new(float.NaN, 0), layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel, 2),
            "nonfinite press coordinates are rejected");
        Check(!drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel, 1),
            "a new session cannot begin below its two metre minimum");
        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel, 2),
            "valid geometry starts after rejected inputs");
        Check(!drag.Move(new(0, float.NaN)) && drag.Radius == 2,
            "nonfinite motion is ignored without altering a radius gesture");
        Check(drag.Move(new(float.MaxValue, -float.MaxValue)) && drag.Radius == 50,
            "extreme finite pointer values stay clamped and finite");
        drag.Cancel();
        Check(!drag.Held && !drag.Dragged && drag.Radius == 2,
            "cancel clears the transient gesture without completing a throw");
    }

    private static void LayoutMatches()
    {
        var layout = Layout();
        var drag = new ForceCaptureRadiusDrag();
        Check(drag.Begin(layout.AngryCenter, layout.AngryCenter, layout.ButtonRadius, layout.SliderTravel, 2),
            "layout geometry starts a radius gesture");
        Check(drag.Matches(layout), "the pressed radius geometry matches its overlay layout");
        Check(!drag.Matches(layout with { AngryCenter = layout.AngryCenter + new float2(1, 0) }),
            "moving the Angry button invalidates a held gesture");
        Check(!drag.Matches(layout with { ButtonRadius = layout.ButtonRadius + 1 }),
            "changing its radius invalidates the held gesture");
        Check(!drag.Matches(layout with { Scale = layout.Scale + .1f }),
            "a DPI/layout scale change invalidates the old travel geometry");
        drag.Cancel();
        Check(!drag.Matches(layout), "a cancelled radius gesture cannot match a later layout");
    }

    private static ForceOverlayLayout Layout() => new(
        new float2(10, 10), new float2(20, 20), new float2(120, 240), new float2(40, 40),
        12, new float2(100, 100), 60, new float2(0, 0), new float2(50, 50), 1, 16);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
