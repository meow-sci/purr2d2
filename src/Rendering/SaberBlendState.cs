using Brutal.VulkanApi;

namespace Purr2D2.Rendering;

internal static class SaberBlendState
{
    internal static VkPipelineColorBlendAttachmentState CreateAttachment() => new()
    {
        // KSA's additive preset multiplies the background by destination alpha.
        // Ocean pixels can have zero alpha (sunflare transmittance), so that
        // preset erases water even at the transparent edge of a glow ribbon.
        BlendEnable = true,
        SrcColorBlendFactor = VkBlendFactor.One,
        DstColorBlendFactor = VkBlendFactor.One,
        ColorBlendOp = VkBlendOp.Add,
        SrcAlphaBlendFactor = VkBlendFactor.Zero,
        DstAlphaBlendFactor = VkBlendFactor.One,
        AlphaBlendOp = VkBlendOp.Add,
        ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit,
    };
}
