// Shared additive HDR profile. The entry shader defines its own bladeShimmer().
layout(location=0) in vec2 positionOnRibbon;
layout(location=1) in float effectBrightness;
layout(location=0) out vec4 outColor;

void main() {
    if (pc.shape.w > 1.5) {
        float distanceToArc = abs(positionOnRibbon.x);
        float radius = pc.shape.y * 0.1875;
        float halo = exp(-distanceToArc / (radius * 1.5));
        halo *= 1.0 - smoothstep(pc.shape.y * 0.65, pc.shape.y, distanceToArc);
        float core = 1.0 - smoothstep(radius * 0.2, radius, distanceToArc);
        float ends = smoothstep(0.0, 0.12, positionOnRibbon.y)
            * (1.0 - smoothstep(0.88, 1.0, positionOnRibbon.y));
        // KSA's additive state writes attachment alpha. Fade RGB, always retain alpha=1.
        outColor = vec4((pc.color.rgb * halo + vec3(core * 0.85))
            * effectBrightness * ends, 1.0);
        return;
    }
    vec2 radial = vec2(positionOnRibbon.x, max(abs(positionOnRibbon.y) - pc.shape.x, 0.0));
    float distanceToSegment = length(radial);
    if (distanceToSegment >= pc.shape.y) discard;
    if (pc.shape.w > 0.5) {
        float feather = 1.0 - smoothstep(pc.shape.y * 0.55, pc.shape.y, distanceToSegment);
        outColor = vec4(pc.color.rgb, pc.color.a * feather);
    } else {
        float radius = max(pc.shape.z, 0.0001);
        // The white core stays steady. Animation=0 preserves the original cut-spark path.
        float shimmer = 0.0;
        if (pc.animation.w > 0.0) {
            float along = positionOnRibbon.y + pc.animation.z;
            shimmer = pc.animation.w * bladeShimmer(along);
        }
        float halo = exp(-distanceToSegment / (radius * 1.4 * (1.0 + shimmer)));
        halo *= 1.0 - smoothstep(pc.shape.y * 0.75, pc.shape.y, distanceToSegment);
        halo *= 1.0 + shimmer * 0.6;
        float core = 1.0 - smoothstep(radius * 0.3, radius, distanceToSegment);
        outColor = vec4((pc.color.rgb * halo + vec3(core * 1.5)) * pc.color.a, 1.0);
    }
}
