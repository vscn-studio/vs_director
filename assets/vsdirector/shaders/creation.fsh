#version 330 core
in vec2 uv;
uniform sampler2D tex;
uniform int textured;
uniform vec4 tint;
uniform float glow;
out vec4 outColor;
void main() {
    vec4 color = textured != 0 ? texture(tex, uv) : vec4(1.0);
    color *= tint;
    if (color.a < 0.01) discard;
    outColor = vec4(color.rgb * (1.0 + max(0.0, glow)), color.a);
}
