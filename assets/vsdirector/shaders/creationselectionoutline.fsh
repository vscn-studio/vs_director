#version 330 core
uniform sampler2D maskTex;
uniform int radius;
uniform vec4 selectedColor;
uniform vec4 hoveredColor;
out vec4 outColor;

vec2 sampleMask(ivec2 p, ivec2 size) {
    if (any(lessThan(p, ivec2(0))) || any(greaterThanEqual(p, size))) return vec2(0.0);
    return texelFetch(maskTex, p, 0).rg;
}
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    ivec2 size = textureSize(maskTex, 0);
    vec2 center = sampleMask(p, size);
    vec2 expanded = center;
    int r = clamp(radius, 1, 8);
    for (int y = -r; y <= r; y++) {
        for (int x = -r; x <= r; x++) {
            if (x*x + y*y <= r*r) expanded = max(expanded, sampleMask(p + ivec2(x,y), size));
        }
    }
    vec2 edge = max(expanded - center, vec2(0.0));
    if (expanded.r > 0.0) outColor = vec4(selectedColor.rgb, selectedColor.a * max(edge.r, center.r * 0.07));
    else if (expanded.g > 0.0) outColor = vec4(hoveredColor.rgb, hoveredColor.a * max(edge.g, center.g * 0.04));
    else outColor = vec4(0.0);
}
