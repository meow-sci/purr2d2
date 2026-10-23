#version 450

layout(push_constant) uniform GlowPush {
    mat4 mvp;
    vec4 color;
    vec4 shape; // half segment/blade length, outside radius, core radius, 0=hot / 1=char / 2=arc
    vec4 animation; // seconds, owner seed, along-blade offset / arc segment count, shimmer / arc level
    vec4 camera; // camera position in the normalized saber frame, arc visibility
} pc;

layout(location=0) out vec2 positionOnRibbon;
layout(location=1) out float effectBrightness;

const float TAU = 6.28318530718;

float hash(float value) {
    vec3 p = fract(vec3(value) * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

// Periodic value noise: 4 keys/second x 256 seconds. The boundary blends to key 0.
float movingNoise(float key, float time) {
    float tick = time * 4.0;
    float epoch = floor(tick);
    float blend = fract(tick);
    blend = blend * blend * (3.0 - 2.0 * blend);
    return mix(hash(key + mod(epoch, 1024.0) * 17.0),
        hash(key + mod(epoch + 1.0, 1024.0) * 17.0), blend) * 2.0 - 1.0;
}

vec3 arcPoint(float u, float seed, float arc) {
    float time = pc.animation.x;
    float span = min(pc.shape.x * 0.32, 0.30);
    float center = clamp(0.16 + arc * 0.32
        + 0.04 * sin(TAU * time * 0.125 + seed), 0.05, 0.95);
    float start = (pc.shape.x - span) * center;
    float envelope = sin(3.14159265359 * u);
    // Fixed spatial knots keep the same curve when the strip changes segment detail.
    float knot = u * 11.0;
    float cell = floor(knot);
    float along = fract(knot);
    float radialNoise = mix(movingNoise(seed + cell * 7.0, time),
        movingNoise(seed + (cell + 1.0) * 7.0, time), along);
    float angleNoise = mix(movingNoise(seed + 93.0 + cell * 7.0, time),
        movingNoise(seed + 93.0 + (cell + 1.0) * 7.0, time), along);
    float angle = seed * TAU + arc * 2.399963
        + 0.4 * sin(TAU * time * 0.25 + seed) + angleNoise * envelope * 0.45;
    float radius = 0.010 + envelope * (0.045 + radialNoise * 0.014);
    return vec3(vec2(cos(angle), sin(angle)) * radius, start + u * span);
}

float arcBurst(float seed) {
    // One 0.24-0.38s burst in each staggered 2s window, with a true quiet gap.
    // Wrapping the window key at 128 repeats seamlessly with the 256s CPU clock.
    float time = pc.animation.x + hash(seed + 11.0) * 2.0;
    float window = floor(time * 0.5);
    float local = time - window * 2.0;
    float key = seed + mod(window, 128.0) * 29.0;
    float start = 0.18 + hash(key + 7.0) * 1.22;
    float duration = 0.24 + hash(key + 53.0) * 0.14;
    return smoothstep(start, start + 0.06, local)
        * (1.0 - smoothstep(start + duration - 0.10, start + duration, local));
}

void drawArc() {
    float segments = clamp(pc.animation.z, 6.0, 10.0);
    float u = float(gl_VertexIndex / 2) / segments;
    float arc = float(gl_InstanceIndex);
    float seed = pc.animation.y * 97.0 + arc * 23.0;
    float burst = arcBurst(seed);
    effectBrightness = pc.camera.w * smoothstep(0.0, 1.0, pc.animation.w - arc) * burst;
    if (effectBrightness <= 0.0) {
        // Uniform across this instance: collapse the strip before doing path/noise work.
        positionOnRibbon = vec2(0);
        gl_Position = vec4(0, 0, 0, 1);
        return;
    }
    vec3 point = arcPoint(u, seed, arc);
    vec3 tangent = arcPoint(min(u + 1.0 / segments, 1.0), seed, arc)
        - arcPoint(max(u - 1.0 / segments, 0.0), seed, arc);
    vec3 side = cross(tangent, pc.camera.xyz - point);
    if (dot(side, side) < 1e-12) side = cross(tangent, vec3(1, 0, 0));
    if (dot(side, side) < 1e-12) side = cross(tangent, vec3(0, 1, 0));
    side *= inversesqrt(max(dot(side, side), 1e-12));
    float across = (gl_VertexIndex & 1) == 0 ? -1.0 : 1.0;
    positionOnRibbon = vec2(across * pc.shape.y, u);
    gl_Position = pc.mvp * vec4(point + side * (across * pc.shape.y), 1);
}

void main() {
    if (pc.shape.w > 1.5) {
        drawArc();
        return;
    }
    const vec2 corners[4] = vec2[4](vec2(-1,-1), vec2(1,-1), vec2(-1,1), vec2(1,1));
    vec2 corner = corners[gl_VertexIndex];
    positionOnRibbon = vec2(corner.x * pc.shape.y, corner.y * (pc.shape.x + pc.shape.y));
    effectBrightness = 1.0;
    gl_Position = pc.mvp * vec4(corner, 0, 1);
}
