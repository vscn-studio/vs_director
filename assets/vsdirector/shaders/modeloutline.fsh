#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D maskTex;
uniform sampler2D horizontalMaskTex;
uniform vec2 screenTexelSize;
uniform vec2 maskTexelSize;
uniform int outlineRadius;
uniform vec4 outlineColor;
uniform int throughObstacles;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

void main(void)
{
    vec2 uv = gl_FragCoord.xy * screenTexelSize;
    vec2 masks = texture(maskTex, uv).rg;

    vec2 expandedMasks = vec2(0.0);
    for (int offset = -outlineRadius; offset <= outlineRadius; offset++)
    {
        expandedMasks = max(
            expandedMasks,
            texture(horizontalMaskTex, uv + vec2(0.0, float(offset) * maskTexelSize.y)).rg);
    }

    bool fullOuterContour = masks.r <= 0.5 && expandedMasks.r > 0.5;
    bool drawContour;
    if (throughObstacles != 0)
    {
        drawContour = fullOuterContour;
    }
    else
    {
        bool visibleOuterContour = fullOuterContour
            && expandedMasks.g > 0.5;
        bool occlusionClosure = masks.r > 0.5
            && masks.g <= 0.5
            && expandedMasks.g > 0.5;

        // Keep the same single outer ring as the through-obstacles mode, but
        // only where the model is scene-visible. At an obstacle intersection,
        // add the short visible/hidden boundary segment inside the complete
        // model mask so the ring closes instead of ending against the block.
        drawContour = visibleOuterContour || occlusionClosure;
    }
    if (!drawContour)
    {
        discard;
    }

    outColor = outlineColor;
    outGlow = vec4(0.0, 0.0, 0.0, outlineColor.a);
#if SSAOLEVEL > 0
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
#endif
}
