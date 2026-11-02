// Set0,2,3 are the actual native global/shared/per-ocean sets. Keep GlowPush128B.
layout(set=1,binding=0) uniform sampler2D saberPostOceanDepth;
layout(set=1,binding=1,std140) uniform SaberWaterData { vec4 values; } saberWater;
// values: double-derived camera altitude, native extinction scale, far-above-water flag, reserved.

float saberSurfaceDisplacement(vec3 point, vec3 normal, float filterSize) {
    vec2 target = GetPlanarPosition(point,normal);
    vec2 parcel = target;
    vec3 displacement = vec3(0);
    // Match the native underwater probe's inverse horizontal displacement search.
    for (int i=0;i<8;i++) {
        displacement = GetOceanDisplacement(parcel,filterSize);
        vec2 error = target - (parcel + displacement.xy);
        if (dot(error,error) <= 0.02 * 0.02) break;
        parcel += error;
    }
    return displacement.z;
}

vec3 saberWaterTransmission() {
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 extent = textureSize(saberPostOceanDepth,0);
    if (any(lessThan(pixel,ivec2(0))) || any(greaterThanEqual(pixel,extent))) discard;
    float opaque = texelFetch(sceneDepth,pixel,0).r;
    // Native opaque reverse-Z depth excludes water. Zero is the far/sky value.
    if (isnan(opaque) || isinf(opaque) || gl_FragCoord.z <= 0.0 || gl_FragCoord.z < opaque) discard;
    vec2 uv = (vec2(pixel)+0.5) / vec2(extent);
    vec3 position = getWorldPosition(uv,gl_FragCoord.z,global.camera.inverseProjection,global.camera.inverseView);
    float fragmentDistance = length(position);
    if (isnan(fragmentDistance) || isinf(fragmentDistance)) discard;
    vec3 ray = normalize(position);
    vec3 up = normalize(-global.lighting.planetPosition.xyz);
    float slope = dot(ray,up);
    float altitude = saberWater.values.x;
    float probeDistance = max(2.0*global.camera.nearPlane,0.25);
    float filterSize = probeDistance * 2.0 / (global.camera.projection[1][1] * float(global.camera.screenHeight))
        * uboStaticOceanData.tessellationEdgeLengthPixels;
    float displacement = 0.0;
    float underwaterBlend = 0.0;
    if (saberWater.values.z < 0.5) {
        displacement = saberSurfaceDisplacement(ray*probeDistance,up,filterSize);
        float signedDepth = displacement - (altitude + probeDistance*slope);
        underwaterBlend = smoothstep(-0.001,0.001,signedDepth);
    }
    float postDepth = texelFetch(saberPostOceanDepth,pixel,0).r;
    bool hasSurface = postDepth > opaque;
    float surfaceDistance = hasSurface
        ? length(getWorldPosition(uv,postDepth,global.camera.inverseProjection,global.camera.inverseView)) : 0.0;
    float underwaterLength = hasSurface ? min(fragmentDistance,surfaceDistance) : fragmentDistance;
    if (!hasSurface && postDepth == 0.0 && slope > 1e-4)
        underwaterLength = min(underwaterLength,max(displacement-altitude,0.0)/slope);
    float aboveWaterLength = hasSurface ? max(fragmentDistance-surfaceDistance,0.0) : 0.0;
    float transparencyDepth = max(uboStaticOceanData.transparencyDepth,1.0);
    // Match the native above-water ocean's linear absorption and underwater Beer-Lambert law.
    float surfaceTransparencyDepth = max(uboStaticOceanData.transparencyDepth,0.000001);
    vec3 aboveTransmission = vec3(max(1.0-aboveWaterLength/surfaceTransparencyDepth,0.0));
    vec3 textureNormal = mat3(uboDynamicOceanData.worldToPlanet)*up;
    vec3 waterColor = unpackRGBA(uboStaticOceanData.color).rgb
        * gammaToLinear(textureLod(colorMap,calculateSphericalUV(textureNormal),0.0).rgb);
    vec3 extinction = max(saberWater.values.y,0.0) * -log(clamp(waterColor,vec3(0.005),vec3(0.98))) / transparencyDepth;
    vec3 underTransmission = exp(-extinction*underwaterLength);
    // Native fog already supplied the background inscatter; add only attenuated emitted RGB.
    return mix(aboveTransmission,underTransmission,underwaterBlend);
}
