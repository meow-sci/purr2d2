using Brutal.Numerics;

namespace Purr2D2.Capes;

/// <summary>Converts meter-authored hood coordinates into the native helmet's centimeter frame.</summary>
internal static class CapeAttachmentMath
{
    private static readonly float4x4 HoodMetersToNativeCentimeters = float4x4.CreateScale(100f);

    /// <summary>
    /// Uses the helmet matrix already published by KittenRenderable for this viewport.
    /// False means the caller should retain its authored head-socket fallback.
    /// </summary>
    internal static bool TryGetNativeHoodTransform(bool hasHelmetAttachment, float4x4? helmetMeshTransform,
        out float4x4 hoodTransform)
    {
        if (!hasHelmetAttachment || helmetMeshTransform is not { } nativeTransform)
        {
            hoodTransform = default;
            return false;
        }

        hoodTransform = HoodMetersToNativeCentimeters * nativeTransform;
        return true;
    }
}
