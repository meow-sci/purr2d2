// Shared 128-byte ABI. Each saber type owns its entry shaders and Vulkan pipeline.
layout(push_constant) uniform GlowPush {
    mat4 mvp;
    vec4 color; // tint, brightness (hot/char) or radial amplitude (arc)
    vec4 shape; // hot/char: half length,radius,core,mode0/1; arc: length,width,burstClock,2+count
    vec4 animation; // phase seconds, owner seed, along offset / segments, shimmer / arc level
    vec4 camera; // local camera position, arc visibility * HDR brightness
} pc;

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

// Returns the start and span in metres, always inside the deployed blade.
vec2 arcExtent(float arc, float seed) {
    float span = min(pc.shape.x * 0.32, 0.30);
    float configuredArcs = clamp(pc.shape.w - 2.0, 1.0, 8.0);
    float center = clamp(0.16 + arc * (0.64 / max(configuredArcs - 1.0, 1.0))
        + 0.04 * sin(TAU * pc.animation.x * 0.125 + seed), 0.05, 0.95);
    return vec2((pc.shape.x - span) * center, span);
}
