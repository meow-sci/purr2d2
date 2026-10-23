#version 450
#extension GL_GOOGLE_include_directive : require
#include "SaberVfxCommon.glsl"

// Light sabers carry smooth, gently curving energy loops with no spatial noise knots.
vec3 arcPoint(float u, float seed, float arc) {
    float time = pc.animation.x;
    vec2 extent = arcExtent(arc, seed);
    float envelope = sin(3.14159265359 * u);
    float angle = seed * TAU + arc * 2.399963
        + 0.22 * sin(TAU * time * 0.125 + seed)
        + 0.18 * envelope * sin(u * TAU + time * TAU * 0.25 + seed);
    float radius = 0.010 + envelope
        * (0.032 + 0.006 * sin(u * TAU + time * TAU * 0.5 + seed)) * pc.color.a;
    return vec3(vec2(cos(angle), sin(angle)) * radius, extent.x + u * extent.y);
}

#include "SaberRibbonVertex.glsl"
