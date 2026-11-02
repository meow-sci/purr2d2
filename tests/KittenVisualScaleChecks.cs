using System;
using Brutal.Numerics;
using Purr2D2.Capes;
using Purr2D2.Pose;

internal static class KittenVisualScaleChecks
{
    private static readonly float4x4 ModelRotation = float4x4.CreateRotationX(-MathF.PI / 2)
        * float4x4.CreateRotationZ(-MathF.PI / 2);

    internal static void Run()
    {
        CheckFrame(float3.One, float3.One, float3.One, "normal");
        CheckFrame(new(8), float3.One, new(8), "uniform physical scale already present on first equip");
        CheckFrame(new(2, 3, 4), float3.One, new(2, 3, 4), "nonuniform physical scale");
        CheckFrame(float3.One, new(5, 7, 9), new(7, 9, 5), "draw-only body-axis scale");
        CheckFrame(new(2, 3, 4), new(5, 7, 9), new(14, 27, 20), "combined avatar and draw-only scale");
        CheckFrame(new(1f / 128f, 32, 128), float3.One, new(1f / 128f, 32, 128), "small and large axes");
        CheckFrame(float3.One, float3.One, float3.One, "return to normal");
        InvalidFrames(); CapeResetOwnership();
    }

    private static void CheckFrame(float3 avatarAxes, float3 bodyAxes, float3 expectedAxes, string context)
    {
        var bodyRotation = doubleQuat.CreateFromAxisAngle(double3.Normalize(new(1, 2, 3)), .731);
        var nativeRotation = float4x4.CreateFromQuaternion(floatQuat.Pack(bodyRotation));
        float3 bodyPosition = new(1024.5f, -3072.125f, 6144.25f);
        float3 centerOfMass = new(.31f, -.47f, .23f);
        var expectedBody = float4x4.CreateScale(avatarAxes * .01f) * ModelRotation
            * float4x4.CreateTranslation(-centerOfMass) * float4x4.CreateScale(bodyAxes);
        var observed = expectedBody * nativeRotation * float4x4.CreateTranslation(bodyPosition);
        Check(KittenVisualScale.TryModelToBody(observed, bodyRotation, double3.Unpack(bodyPosition), out var actual),
            context + " resolves from the final game-owned matrix");
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
            Check(Math.Abs(actual[row, column] - expectedBody[row, column]) <= Math.Max(1e-7, Math.Abs(expectedBody[row, column]) * 2e-5),
                context + " preserves transformed basis " + row + "/" + column);
        Check((actual.Translation - double3.Unpack(expectedBody.Translation)).Length() < .002,
            context + " removes the camera origin and rigid body rotation while retaining the COM offset");
        Check(KittenVisualScale.TryRelativeScale(actual, out var axes) && Near(axes, expectedAxes, .0001f),
            context + " reports the effective model axes without a first-equip baseline");

        var socketRotation = float4x4.CreateRotationZ(.42f) * float4x4.CreateRotationY(-.25f);
        float3 socket = new(2, 3, 4);
        Check(KittenVisualScale.TrySocketFrame(socketRotation, observed, socket, out var cape),
            context + " resolves a metre-authored cape socket");
        var expectedCape = float4x4.CreateScale(100f) * socketRotation * observed;
        foreach (float3 axis in new[] { float3.UnitX, float3.UnitY, float3.UnitZ })
            Check(Near(float3.TransformNormal(axis, cape), float3.TransformNormal(axis, expectedCape), .0001f),
                context + " cape retains complete axis scale and shear");
        Check(cape.Translation == socket, context + " cape remains at the observed chest socket");

        // A different camera origin/orientation changes neither the recovered size
        // nor its ownership, including when a secondary viewport draws next.
        float3 nextPosition = new(-21, 8, 13);
        var nextRotation = doubleQuat.CreateFromAxisAngle(double3.UnitY, -1.2);
        observed = expectedBody * float4x4.CreateFromQuaternion(floatQuat.Pack(nextRotation))
            * float4x4.CreateTranslation(nextPosition);
        Check(KittenVisualScale.TryModelToBody(observed, nextRotation, double3.Unpack(nextPosition), out var next)
            && KittenVisualScale.TryRelativeScale(next, out var nextAxes) && !KittenVisualScale.Changed(axes, nextAxes),
            context + " scale is independent of viewport and body rotation");
    }

    private static void InvalidFrames()
    {
        var frame = float4x4.Identity;
        frame[0, 0] = float.NaN;
        Check(!KittenVisualScale.TryModelToBody(frame, doubleQuat.Identity, default, out _), "nonfinite observed frames refuse safely");
        frame = float4x4.CreateScale(new float3(1, 0, 1));
        Check(!KittenVisualScale.TryModelToBody(frame, doubleQuat.Identity, default, out _), "singular observed scale refuses safely");
        frame = float4x4.Identity; frame[0, 3] = .1f;
        Check(!KittenVisualScale.TryModelToBody(frame, doubleQuat.Identity, default, out _), "nonaffine observed frames refuse safely");
        var shear = double4x4.Identity; shear[1, 0] = 1; shear[1, 1] = 0;
        Check(!KittenVisualScale.TryRelativeScale(shear, out _), "collinear axes do not masquerade as a valid nonuniform scale");
        Check(!KittenVisualScale.TryModelToBody(float4x4.Identity, doubleQuat.Identity,
            new(double.PositiveInfinity, 0, 0), out _), "invalid physical position cannot publish an attachment frame");
    }

    private static void CapeResetOwnership()
    {
        var a = new CapeScaleState(); var b = new CapeScaleState();
        Check(!a.Observe(new(4)) && !a.ConsumeReset(), "first cape observation accepts an already enlarged kitten");
        Check(!b.Observe(float3.One) && !b.ConsumeReset(), "another kitten starts with its own normal scale");
        Check(!a.Observe(new(4.00001f)) && !a.ConsumeReset(), "camera rounding does not repeatedly reset cloth");
        Check(a.Observe(new(4, 2, 3)) && !b.ConsumeReset(), "one kitten's nonuniform change leaves other cloth untouched");
        Check(a.ConsumeReset() && !a.ConsumeReset(), "the settled manager consumes a scale reset exactly once");
        Check(a.Observe(float3.One) && a.ConsumeReset(), "returning to normal size resets prior cloth motion");
        Check(b.Observe(new(.1f)) && b.Observe(new(.2f)) && b.ConsumeReset() && !b.ConsumeReset(),
            "multiple render observations coalesce into one deferred cloth reset");
    }

    private static bool Near(float3 actual, float3 expected, float relative) =>
        (actual - expected).Length() <= MathF.Max(1e-6f, expected.Length() * relative);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Visual scale check failed: " + message);
        Console.WriteLine("PASS " + message);
    }
}
