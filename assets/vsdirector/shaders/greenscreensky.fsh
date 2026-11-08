#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform vec4 greenScreenColor;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

void main(void)
{
    outColor = greenScreenColor;
    outGlow = vec4(0.0, 0.0, 0.0, greenScreenColor.a);
#if SSAOLEVEL > 0
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
#endif
}
