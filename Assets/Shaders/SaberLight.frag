#version 450
#extension GL_GOOGLE_include_directive : require
#include "SaberVfxCommon.glsl"

// Broad, smooth traveling light; all temporal frequencies repeat on the 256s clock.
float bladeShimmer(float along) {
    float phase = pc.animation.y * TAU;
    return 0.7 * sin(along * 11.0 - pc.animation.x * TAU * 0.5 + phase)
        + 0.3 * sin(along * 17.0 + pc.animation.x * TAU * 0.75 + phase * 2.0);
}

#include "SaberRibbonFragment.glsl"
