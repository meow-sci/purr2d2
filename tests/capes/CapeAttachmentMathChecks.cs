using Brutal.Numerics;
using Purr2D2.Capes;

internal static class CapeAttachmentMathChecks
{
    private const float Tolerance = .002f;

    internal static void Run()
    {
        CheckPlacement("main view", float4x4.CreateRotationY(.37f) * float4x4.CreateRotationZ(-.62f)
            * float4x4.CreateTranslation(new float3(123f, -48f, 9f)));
        CheckPlacement("secondary view", float4x4.CreateScale(.8f, 1.1f, .9f)
            * float4x4.CreateRotationX(.41f) * float4x4.CreateRotationZ(1.2f)
            * float4x4.CreateTranslation(new float3(-350f, 72f, 18f)));
        CheckPlacement("somersault", float4x4.CreateRotationX(MathF.PI)
            * float4x4.CreateRotationY(-.28f) * float4x4.CreateRotationZ(.13f)
            * float4x4.CreateTranslation(new float3(17f, 220f, -91f)));
        CheckFallbackSelection();
        Console.WriteLine("Hood meter-to-centimeter placement matches native helmet transforms across views and rotations.");
    }

    private static void CheckPlacement(string label, float4x4 nativeHelmetTransform)
    {
        Check(CapeAttachmentMath.TryGetNativeHoodTransform(true, nativeHelmetTransform, out float4x4 hoodTransform),
            label + " selects the published helmet transform");

        float3[] hoodPointsMeters = [
            float3.Zero,
            float3.UnitX,
            float3.UnitY,
            float3.UnitZ,
            new float3(.31f, .74f, -.22f)
        ];
        float3 hoodOrigin = float3.Transform(float3.Zero, hoodTransform);
        float3 nativeOrigin = float3.Transform(float3.Zero, nativeHelmetTransform);
        CheckNear(hoodOrigin, nativeOrigin, label + " preserves the transformed origin");

        foreach (float3 pointMeters in hoodPointsMeters)
        {
            // Compare real Brutal matrix transforms: the hood point is authored in meters,
            // while the corresponding native helmet point is authored in centimeters.
            float3 hoodPoint = float3.Transform(pointMeters, hoodTransform);
            float3 nativeHelmetPoint = float3.Transform(pointMeters * 100f, nativeHelmetTransform);
            CheckNear(hoodPoint, nativeHelmetPoint, label + " maps hood point " + pointMeters);
            if (pointMeters != float3.Zero)
                CheckNear(hoodPoint - hoodOrigin, nativeHelmetPoint - nativeOrigin,
                    label + " preserves direction " + pointMeters);
        }
    }

    private static void CheckFallbackSelection()
    {
        var publishedTransform = float4x4.CreateTranslation(new float3(2f, 3f, 4f));
        Check(!CapeAttachmentMath.TryGetNativeHoodTransform(false, publishedTransform, out _),
            "disabled or absent helmet attachment requests the existing SocketFrame fallback");
        Check(!CapeAttachmentMath.TryGetNativeHoodTransform(true, null, out _),
            "missing helmet mesh requests the existing SocketFrame fallback");
    }

    private static void CheckNear(float3 actual, float3 expected, string message)
    {
        Check((actual - expected).Length() <= Tolerance,
            $"{message}; actual=({actual.X:G6}, {actual.Y:G6}, {actual.Z:G6}), " +
            $"expected=({expected.X:G6}, {expected.Y:G6}, {expected.Z:G6})");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cape attachment check failed: " + message);
    }
}
