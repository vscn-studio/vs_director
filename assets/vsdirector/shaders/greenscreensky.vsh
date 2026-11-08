#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;

void main(void)
{
    gl_Position = vec4(vertexPositionIn, 1.0);
}
