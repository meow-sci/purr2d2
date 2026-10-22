#version 450

layout(push_constant) uniform GlowPush {
    mat4 mvp;
    vec4 color;
    vec4 shape; // half segment length, outside radius, core radius, 0=hot / 1=char
} pc;

layout(location=0) out vec2 positionOnRibbon;

void main() {
    const vec2 corners[4] = vec2[4](vec2(-1,-1), vec2(1,-1), vec2(-1,1), vec2(1,1));
    vec2 corner = corners[gl_VertexIndex];
    positionOnRibbon = vec2(corner.x * pc.shape.y, corner.y * (pc.shape.x + pc.shape.y));
    gl_Position = pc.mvp * vec4(corner, 0, 1);
}
