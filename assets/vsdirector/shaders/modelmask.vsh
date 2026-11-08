#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 5) in int jointId;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

// UBO:Animation,0,4800
layout (std140) uniform Animation
{
    mat4 values[MAXANIMATEDELEMENTS];
} ElementTransforms;

out vec2 uv;

void main(void)
{
    mat4 animatedModel = modelMatrix * ElementTransforms.values[jointId];
    uv = uvIn;
    gl_Position = projectionMatrix
        * viewMatrix
        * animatedModel
        * vec4(vertexPositionIn, 1.0);
}
