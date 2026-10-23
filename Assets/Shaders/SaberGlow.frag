#version 450

layout(push_constant) uniform GlowPush {
    mat4 mvp;
    vec4 color;
    vec4 shape;
    vec4 animation;
    vec4 camera;
} pc;

layout(location=0) in vec2 positionOnRibbon;
layout(location=1) in float effectBrightness;
layout(location=0) out vec4 outColor;

void main() {
    if (pc.shape.w > 1.5) {
        float distanceToArc = abs(positionOnRibbon.x);
        float halo = exp(-distanceToArc / (pc.shape.z * 1.5));
        halo *= 1.0 - smoothstep(pc.shape.y * 0.65, pc.shape.y, distanceToArc);
        float core = 1.0 - smoothstep(pc.shape.z * 0.2, pc.shape.z, distanceToArc);
        float ends = smoothstep(0.0, 0.12, positionOnRibbon.y)
            * (1.0 - smoothstep(0.88, 1.0, positionOnRibbon.y));
        // KSA's additive state writes attachment alpha. Fade RGB, always retain alpha=1.
        outColor = vec4((pc.color.rgb * halo + vec3(core * 0.85))
            * pc.color.a * effectBrightness * ends, 1.0);
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
        // A small traveling modulation in the emitted light; the white core stays stable.
        // Integer-cycle frequencies make the bounded 256-second clock seamless.
        float shimmer = 0.0;
        if (pc.animation.w > 0.0) {
            float along = positionOnRibbon.y + pc.animation.z;
            float phase = pc.animation.y * 6.28318530718;
            float wave = 0.65 * sin(along * 23.0 - pc.animation.x * 9.42477796077 + phase)
                + 0.35 * sin(along * 41.0 + pc.animation.x * 15.7079632679 + phase * 2.0);
            shimmer = pc.animation.w * wave;
        }
        float halo = exp(-distanceToSegment / (radius * 1.4 * (1.0 + shimmer)));
        halo *= 1.0 - smoothstep(pc.shape.y * 0.75, pc.shape.y, distanceToSegment);
        halo *= 1.0 + shimmer * 0.6;
        float core = 1.0 - smoothstep(radius * 0.3, radius, distanceToSegment);
        outColor = vec4((pc.color.rgb * halo + vec3(core * 1.5)) * pc.color.a, 1.0);
    }
}
