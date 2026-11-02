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
