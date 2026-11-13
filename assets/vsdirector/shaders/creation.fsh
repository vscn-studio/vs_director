#version 330 core
in vec2 uv;
in vec3 surfacePosition;
in vec3 eyePosition;
uniform sampler2D tex;
uniform int textured;
uniform vec4 tint;
uniform float glow;
uniform vec3 worldLight;
uniform vec3 lightDirection;
uniform int surfaceShading;
uniform vec4 fogColor;
uniform float fogDensity;
uniform float fogMinimum;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
void main() {
    vec4 color = textured != 0 ? texture(tex, uv) : vec4(1.0);
    color *= tint;
    if (color.a < 0.01) discard;
    vec3 normal = cross(dFdx(surfacePosition), dFdy(surfacePosition));
    normal /= max(length(normal), 0.000001);
    if (!gl_FrontFacing) normal = -normal;
    vec3 direction = lightDirection / max(length(lightDirection), 0.000001);
    float shade = surfaceShading != 0 ? max(0.45, 0.5 + 0.5 * dot(normal, direction)) : 1.0;
    if (surfaceShading != 0) shade = max(shade, max(0.0, normal.y) * 0.95);
    float emission = clamp(glow, 0.0, 1.0);
    vec3 lighting = mix(max(worldLight, vec3(0.0)) * shade, vec3(1.0), emission);
    float fog = clamp(1.0 - exp(-length(eyePosition) * max(0.0, fogDensity)) + fogMinimum, 0.0, 1.0);
    outColor = vec4(mix(color.rgb * lighting, fogColor.rgb, fog), color.a);
    // The world framebuffer has a separate bloom attachment. Leaving it
    // unwritten can turn ordinary actor surfaces into undefined glow.
    // The bloom attachment uses alpha as its coverage mask. Writing zero
    // here lets the underlying world's glow bleed through opaque actors.
    outGlow = vec4(emission * (1.0 - fog), 0.0, 0.0, color.a);
}
