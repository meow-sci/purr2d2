#version 450
#extension GL_GOOGLE_include_directive : require
#include "SaberVfxCommon.glsl"

// Dark sabers throw coarse jagged arcs. Fixed knots keep the same path across detail levels.
vec3 arcPoint(float u, float seed, float arc) {
    float time = pc.animation.x;
    vec2 extent = arcExtent(arc, seed);
    float envelope = sin(3.14159265359 * u);
    float knot = u * 5.0;
    float cell = floor(knot);
    float along = fract(knot);
    float radialNoise = mix(movingNoise(seed + cell * 7.0, time),
        movingNoise(seed + (cell + 1.0) * 7.0, time), along);
    float angleNoise = mix(movingNoise(seed + 93.0 + cell * 7.0, time),
        movingNoise(seed + 93.0 + (cell + 1.0) * 7.0, time), along);
    float angle = seed * TAU + arc * 2.399963
        + 0.4 * sin(TAU * time * 0.25 + seed) + angleNoise * envelope * 0.45;
    float radius = 0.010 + envelope * (0.045 + radialNoise * 0.014) * pc.color.a;
    return vec3(vec2(cos(angle), sin(angle)) * radius, extent.x + u * extent.y);
}

#include "SaberRibbonVertex.glsl"
