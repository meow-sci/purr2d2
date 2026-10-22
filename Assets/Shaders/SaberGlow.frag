#version 450

layout(push_constant) uniform GlowPush {
    mat4 mvp;
    vec4 color;
    vec4 shape;
} pc;

layout(location=0) in vec2 positionOnRibbon;
layout(location=0) out vec4 outColor;

void main() {
    vec2 radial = vec2(positionOnRibbon.x, max(abs(positionOnRibbon.y) - pc.shape.x, 0.0));
    float distanceToSegment = length(radial);
    if (distanceToSegment >= pc.shape.y) discard;
    if (pc.shape.w > 0.5) {
        float feather = 1.0 - smoothstep(pc.shape.y * 0.55, pc.shape.y, distanceToSegment);
        outColor = vec4(pc.color.rgb, pc.color.a * feather);
    } else {
        float radius = max(pc.shape.z, 0.0001);
        float halo = exp(-distanceToSegment / (radius * 1.4));
        halo *= 1.0 - smoothstep(pc.shape.y * 0.75, pc.shape.y, distanceToSegment);
        float core = 1.0 - smoothstep(radius * 0.3, radius, distanceToSegment);
        outColor = vec4((pc.color.rgb * halo + vec3(core * 1.5)) * pc.color.a, 1.0);
    }
}
