#version 330 core
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
out vec2 uv;
out vec3 surfacePosition;
out vec3 eyePosition;
void main() {
    uv = uvIn;
    vec4 world = modelMatrix * vec4(vertexPositionIn, 1.0);
    surfacePosition = world.xyz;
    vec4 eye = viewMatrix * world;
    eyePosition = eye.xyz;
    gl_Position = projectionMatrix * eye;
}
