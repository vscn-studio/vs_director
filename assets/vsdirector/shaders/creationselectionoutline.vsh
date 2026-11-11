#version 330 core
layout(location = 0) in vec3 vertexPositionIn;
void main() {
    gl_Position = vec4(vertexPositionIn, 1.0);
}
