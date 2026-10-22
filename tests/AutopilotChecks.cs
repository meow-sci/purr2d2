using Brutal.Numerics;
using Purr2D2.Autopilot;

internal static class AutopilotChecks
{
    public static void Run()
    {
        AxisAndRingGeometry();
        GuidanceAndProgress();
        AttitudeAlignment();
    }

    private static void AxisAndRingGeometry()
    {
        Near(CylinderOrbitGuidance.Axis(0, false), double3.UnitX, "X cylinder axis selected");
        Near(CylinderOrbitGuidance.Axis(2, true), -double3.UnitZ, "axis reversal is exact");
        Check(CylinderOrbitGuidance.TryRadial(new double3(2, 3, 4), double3.UnitX,
            out double axial, out double3 radial), "radial frame resolves away from the axis");
        Near(axial, 2, "axial station preserved");
        Near(double3.Dot(radial, double3.UnitX), 0, "radial is perpendicular to cylinder axis");
        Near(CylinderOrbitGuidance.Point(double3.UnitX, 2, double3.UnitY, 3, 0),
            new double3(2, 3, 0), "ring starts at the captured radial direction");
        Near(CylinderOrbitGuidance.Point(double3.UnitX, 2, double3.UnitY, 3, Math.PI / 2),
            new double3(2, 0, 3), "quarter orbit follows the right-handed cylinder ring");
        Near(CylinderOrbitGuidance.Point(double3.UnitX, 2, double3.UnitY, 3, Math.PI * 2),
            new double3(2, 3, 0), "full orbit closes at the captured position");
        Pass("cylinder frame and closed ring geometry");
    }

    private static void GuidanceAndProgress()
    {
        double3 velocity = CylinderOrbitGuidance.Velocity(double3.UnitZ, double3.UnitX, 2, 0, .25);
        Near(velocity, new double3(0, .5, 0), "tangential velocity follows axis cross radial");
        double3 acceleration = CylinderOrbitGuidance.Acceleration(
            new double3(3, 0, 0), double3.Zero, new double3(2, 0, 0), double3.Zero);
        Check(acceleration.X < 0 && Math.Abs(acceleration.Y) < 1e-12 && acceleration.Length() <= 2.000001,
            "PD guidance commands inward acceleration within its limit");
        double angle = CylinderOrbitGuidance.AdvanceAngle(0, .2, 2, 1, clockwise: false);
        Near(angle, .1, "counterclockwise angle advances by speed over radius");
        Near(CylinderOrbitGuidance.AdvanceAngle(0, .2, 2, 1, clockwise: true), -.1,
            "clockwise angle advances in the opposite direction");
        Near(CylinderOrbitGuidance.Progress(Math.PI), .5, "half turn reports half progress");
        Near(CylinderOrbitGuidance.Progress(10 * Math.PI), 1, "progress clamps after a full turn");
        Pass("bounded relative-position guidance and orbit progress");
    }

    private static void AttitudeAlignment()
    {
        double3 blade = new(.05, .8, .6);
        double3 inward = -double3.UnitX;
        double3 axis = double3.UnitZ;
        doubleQuat attitude = CylinderOrbitGuidance.DesiredBody2Cci(blade, inward, axis, 0);
        Near(double3.Transform(double3.NormalizeOrZero(blade), attitude), inward, "desired attitude points the blade into the hull");

        double3 sourceSecondary = -double3.UnitZ;
        sourceSecondary = double3.NormalizeOrZero(sourceSecondary - double3.NormalizeOrZero(blade)
            * double3.Dot(sourceSecondary, double3.NormalizeOrZero(blade)));
        double3 mapped0 = double3.Transform(sourceSecondary, attitude);
        doubleQuat rolled = CylinderOrbitGuidance.DesiredBody2Cci(blade, inward, axis, 1);
        double3 mapped90 = double3.Transform(sourceSecondary, rolled);
        Near(double3.Dot(mapped0, inward), 0, "secondary kitten axis remains perpendicular to the blade");
        Near(double3.Dot(mapped90, inward), 0, "quarter-turn adjustment preserves blade alignment");
        Near(double3.Dot(mapped0, mapped90), 0, "quarter-turn adjustment rotates the kitten by ninety degrees");
        Pass("blade-inward attitude and human-assisted quarter-turn orientation");
    }

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Autopilot check failed: {message}; expected {expected}, found {actual}");
        Console.WriteLine("PASS: " + message);
    }

    private static void Near(double3 actual, double3 expected, string message)
    {
        if ((actual - expected).Length() > 1e-8)
            throw new InvalidOperationException($"Autopilot check failed: {message}; expected {expected}, found {actual}");
        Console.WriteLine("PASS: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Autopilot check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Pass(string message) => Console.WriteLine("PASS: " + message);
}
