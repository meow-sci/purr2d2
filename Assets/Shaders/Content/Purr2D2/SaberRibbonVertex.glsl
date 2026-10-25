// Shared submission/strip construction. The including entry shader defines arcPoint().
layout(location=0) out vec2 positionOnRibbon;
layout(location=1) out float effectBrightness;

float arcBurst(float seed) {
    // One 0.24-0.38s burst in each staggered 2s window, with a true quiet gap.
    // Wrapping the window key at 128 repeats seamlessly with the 256s CPU clock.
    float time = pc.shape.z + hash(seed + 11.0) * 2.0;
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
    if (pc.shape.w < -0.5) {
        // Four vertices per ember. Analytic motion avoids a simulation/update buffer.
        float seed = pc.animation.y + float(gl_InstanceIndex) * 23.0;
        float age = pc.animation.x;
        float life = pc.animation.z * mix(0.65, 1.0, hash(seed + 7.0));
        float fade = max(1.0 - age / life, 0.0);
        effectBrightness = fade * fade;
        if (age >= life) {
            positionOnRibbon = vec2(0);
            gl_Position = vec4(0, 0, 0, 1);
            return;
        }
        vec3 velocity = vec3(hash(seed + 11.0) * 2.0 - 1.0,
            mix(0.1, 0.9, hash(seed + 31.0)), hash(seed + 53.0) * 2.0 - 1.0)
            * (1.6 * pc.shape.z);
        vec3 gravity = vec3(0, -pc.animation.w, 0);
        vec3 point = velocity * age + gravity * (0.5 * age * age);
        vec3 tangent = velocity + gravity * age;
        // A short velocity-aligned streak, with a camera-facing width.
        float speed = length(tangent);
        tangent /= max(speed, 0.0001);
        vec3 side = cross(tangent, pc.camera.xyz - point);
        if (dot(side, side) < 1e-12) side = cross(tangent, vec3(1, 0, 0));
        if (dot(side, side) < 1e-12) side = cross(tangent, vec3(0, 0, 1));
        side *= inversesqrt(max(dot(side, side), 1e-12));
        vec2 corner = vec2((gl_VertexIndex & 1) == 0 ? -1.0 : 1.0,
            gl_VertexIndex < 2 ? -1.0 : 1.0);
        float width = pc.shape.y * mix(0.65, 1.0, hash(seed + 71.0));
        float halfLength = width + min(speed * 0.012, 0.045 * pc.shape.z);
        positionOnRibbon = corner;
        gl_Position = pc.mvp * vec4(point + side * (corner.x * width)
            + tangent * (corner.y * halfLength), 1);
        return;
    }
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
