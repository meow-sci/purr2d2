#version 450
#extension GL_GOOGLE_include_directive : require
#include "Content/Purr2D2/SaberVfxCommon.glsl"

// A coarse piecewise contour travels through the halo; its temporal noise remains coherent.
float bladeShimmer(float along) {
    float key = along * 12.0;
    float cell = floor(key);
    float seed = pc.animation.y * 97.0;
    float coarse = mix(movingNoise(seed + cell * 7.0, pc.animation.x),
        movingNoise(seed + (cell + 1.0) * 7.0, pc.animation.x), fract(key));
    return 0.65 * coarse
        + 0.35 * sin(along * 23.0 - pc.animation.x * TAU * 2.0 + seed);
}

#include "Content/Purr2D2/SaberRibbonFragment.glsl"
