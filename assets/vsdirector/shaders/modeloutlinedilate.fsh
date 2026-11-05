#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D maskTex;
uniform vec2 texelSize;
uniform int outlineRadius;

layout(location = 0) out vec4 outMask;

void main(void)
{
    vec2 uv = gl_FragCoord.xy * texelSize;
    vec2 expandedMasks = vec2(0.0);
    for (int offset = -outlineRadius; offset <= outlineRadius; offset++)
    {
        expandedMasks = max(
            expandedMasks,
            texture(maskTex, uv + vec2(float(offset) * texelSize.x, 0.0)).rg);
    }
    outMask = vec4(expandedMasks, 0.0, 1.0);
}
