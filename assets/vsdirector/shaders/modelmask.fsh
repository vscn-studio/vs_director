#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;

uniform sampler2D entityTex;
uniform sampler2D sceneDepthTex;
uniform int respectSceneDepth;
uniform float depthEpsilon;

layout(location = 0) out vec4 outMask;

void sortDepthPair(inout float first, inout float second)
{
    float lower = min(first, second);
    float upper = max(first, second);
    first = lower;
    second = upper;
}

float medianSceneDepth(
    ivec2 centerPixel,
    ivec2 depthSize,
    float centerDepth)
{
    ivec2 maximumPixel = max(depthSize - ivec2(1), ivec2(0));
    float depths[5];
    depths[0] = texelFetch(
        sceneDepthTex,
        clamp(centerPixel + ivec2(-1, 0), ivec2(0), maximumPixel),
        0).r;
    depths[1] = texelFetch(
        sceneDepthTex,
        clamp(centerPixel + ivec2(1, 0), ivec2(0), maximumPixel),
        0).r;
    depths[2] = centerDepth;
    depths[3] = texelFetch(
        sceneDepthTex,
        clamp(centerPixel + ivec2(0, -1), ivec2(0), maximumPixel),
        0).r;
    depths[4] = texelFetch(
        sceneDepthTex,
        clamp(centerPixel + ivec2(0, 1), ivec2(0), maximumPixel),
        0).r;

    // Five-value sorting network. The median follows a sloped surface instead
    // of selecting one arbitrarily closer neighbor, while still rejecting a
    // one-pixel scene-depth hole when most adjacent pixels are an obstacle.
    sortDepthPair(depths[0], depths[1]);
    sortDepthPair(depths[3], depths[4]);
    sortDepthPair(depths[2], depths[4]);
    sortDepthPair(depths[2], depths[3]);
    sortDepthPair(depths[1], depths[4]);
    sortDepthPair(depths[0], depths[3]);
    sortDepthPair(depths[0], depths[2]);
    sortDepthPair(depths[1], depths[3]);
    sortDepthPair(depths[1], depths[2]);
    return depths[2];
}

void main(void)
{
    if (texture(entityTex, uv).a < 0.05)
    {
        discard;
    }
    float visibleMask = 1.0;
    if (respectSceneDepth != 0)
    {
        ivec2 depthSize = textureSize(sceneDepthTex, 0);
        ivec2 depthPixel = clamp(
            ivec2(gl_FragCoord.xy),
            ivec2(0),
            max(depthSize - ivec2(1), ivec2(0)));
        float centerSceneDepth = texelFetch(sceneDepthTex, depthPixel, 0).r;
        // Keep the exact center sample for thin obstacles, and combine it with
        // a cross-neighborhood median that suppresses sub-pixel depth holes.
        // Unlike the previous derivative bias, this threshold cannot grow as
        // an animated or angled model triangle changes slope.
        float neighborhoodSceneDepth = medianSceneDepth(
            depthPixel,
            depthSize,
            centerSceneDepth);
        bool matchesRenderedSurface = abs(gl_FragCoord.z - centerSceneDepth)
            <= depthEpsilon * 2.0;
        float sceneDepth = matchesRenderedSurface
            ? centerSceneDepth
            : min(centerSceneDepth, neighborhoodSceneDepth);
        if (gl_FragCoord.z > sceneDepth + depthEpsilon)
        {
            visibleMask = 0.0;
        }
    }
    // R keeps the complete projected model. G keeps only fragments visible
    // against the scene depth. The two masks let the composite close the
    // contour where a wall or block cuts through the model without revealing
    // the rest of the hidden silhouette.
    outMask = vec4(1.0, visibleMask, 0.0, 1.0);
}
