#version 450
#extension GL_GOOGLE_include_directive : require
// Native includes are resolved beside the installed UnderwaterPostProcessCompute shader.
#include "../Common/Shared.glsl"
#include "../Common/Camera.glsl"
#define OCEAN_SHARED_DATA_BINDING_SET 2
#include "OceanFunctions.glsl"
// The native math header already owns TAU; keep our existing effects self-contained.
#define TAU SABER_TAU
#include "Content/Purr2D2/SaberVfxCommon.glsl"
#define SABER_WATER_PASS 1
#include "Content/Purr2D2/SaberWaterFragment.glsl"

// Broad, smooth traveling light; all temporal frequencies repeat on the 256s clock.
float bladeShimmer(float along) {
    float phase = pc.animation.y * TAU;
    return 0.7 * sin(along * 11.0 - pc.animation.x * TAU * 0.5 + phase)
        + 0.3 * sin(along * 17.0 + pc.animation.x * TAU * 0.75 + phase * 2.0);
}

#include "Content/Purr2D2/SaberRibbonFragment.glsl"
