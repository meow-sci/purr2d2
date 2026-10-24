using System.Numerics;
using Brutal.VulkanApi;
using Purr2D2.Rendering;

internal static class SaberBlendChecks
{
    public static void Main()
    {
        var state = SaberBlendState.CreateAttachment();
        Check(state.BlendEnable, "glow blending is enabled");
        Check(state.ColorBlendOp == VkBlendOp.Add && state.AlphaBlendOp == VkBlendOp.Add,
            "blend equations add their weighted inputs");
        Check(state.ColorWriteMask == (VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit),
            "all RGB channels are written and sunflare/transmittance alpha is preserved");

        foreach (float alpha in new[] { 0f, .25f, .5f, 1f })
        foreach (float sourceAlpha in new[] { 0f, .3f, 1f })
        {
            var background = new Vector4(.08f, .3f, .7f, alpha);
            var edge = new Vector4(0, 0, 0, sourceAlpha);
            Near(Blend(state, edge, background), background, "a faded ribbon edge leaves ocean/land unchanged");
            var glow = new Vector4(.2f, 1.8f, .1f, sourceAlpha);
            var expected = background + new Vector4(glow.X, glow.Y, glow.Z, 0);
            Near(Blend(state, glow, background), expected, "HDR glow adds light independently of background alpha");
            Near(Blend(state, edge, Blend(state, glow, background)), expected,
                "an overlapping faded ribbon cannot erase existing glow or change transmittance");
            Near(Blend(state, glow, Blend(state, glow, background)),
                expected + new Vector4(glow.X, glow.Y, glow.Z, 0), "overlapping arcs add without changing transmittance");
        }
        // Reproduce the former ocean-only failure with the host preset's factors.
        var former = state;
        former.DstColorBlendFactor = VkBlendFactor.DstAlpha;
        Check(Blend(former, new Vector4(0, 0, 0, 1), new Vector4(.08f, .3f, .7f, 0)).Z == 0,
            "regression setup reproduces a black ribbon over zero-alpha water");
        Console.WriteLine("Saber blend checks passed (water/land alpha, faded edges, HDR light, overlapping arcs, preserved transmittance).");
    }

    private static Vector4 Blend(VkPipelineColorBlendAttachmentState state, Vector4 source, Vector4 destination)
    {
        var rgb = new Vector3(source.X, source.Y, source.Z) * Factor(state.SrcColorBlendFactor, destination.W)
            + new Vector3(destination.X, destination.Y, destination.Z) * Factor(state.DstColorBlendFactor, destination.W);
        float alpha = (state.ColorWriteMask & VkColorComponentFlags.ABit) == 0 ? destination.W
            : source.W * Factor(state.SrcAlphaBlendFactor, destination.W)
                + destination.W * Factor(state.DstAlphaBlendFactor, destination.W);
        return new Vector4(rgb, alpha);
    }

    private static float Factor(VkBlendFactor factor, float destinationAlpha) => factor switch
    {
        VkBlendFactor.Zero => 0,
        VkBlendFactor.One => 1,
        VkBlendFactor.DstAlpha => destinationAlpha,
        _ => throw new InvalidOperationException($"Unexpected glow blend factor: {factor}"),
    };

    private static void Near(Vector4 actual, Vector4 expected, string message) =>
        Check(Vector4.Distance(actual, expected) < .00001f, message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
