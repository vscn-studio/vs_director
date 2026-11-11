#version 330 core
in vec2 uv;
uniform sampler2D tex;
uniform int textured;
uniform vec2 channels;
out vec4 outColor;
void main() {
    if (textured != 0 && texture(tex, uv).a < 0.01) discard;
    // MAX blending merges the complete branch; overlapping cubes create no seams.
    outColor = vec4(channels, 0.0, 1.0);
}
