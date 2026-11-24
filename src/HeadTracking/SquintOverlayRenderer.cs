// Adapted from VS-6DOF (DF; bjfishman / TGU-HansJack), CC0-1.0.
// See licenses/VS-6DOF-CC0.txt and licenses/THIRD-PARTY.txt.
#nullable disable
using Vintagestory.API.Client;

namespace VSDirector.HeadTracking
{
    public sealed class SquintOverlayRenderer : IRenderer
    {
        private readonly MeshRef quadRef;
        private readonly ICoreClientAPI capi;
        private bool disposed;
        public IShaderProgram OverlayShaderProg;

        public float PercentZoomed = 0f;

        public SquintOverlayRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            var quadMesh = QuadMeshUtil.GetCustomQuadModelData(-1, -1, 0, 2, 2);
            quadMesh.Rgba = null;
            quadRef = capi.Render.UploadMesh(quadMesh);

            LoadShader();
            capi.Event.ReloadShader += LoadShader;
            capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho);
        }

        public double RenderOrder => 1.1;

        public int RenderRange => 1;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            capi.Event.ReloadShader -= LoadShader;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
            OverlayShaderProg?.Dispose();
            capi.Render.DeleteMesh(quadRef);
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (disposed || PercentZoomed <= 0f || OverlayShaderProg == null || !VsdofModSystem.AllowLive())
            {
                return;
            }

            var curShader = capi.Render.CurrentActiveShader;
            curShader?.Stop();
            OverlayShaderProg.Use();
            capi.Render.GlToggleBlend(true);
            OverlayShaderProg.Uniform("percentZoomed", PercentZoomed);
            capi.Render.RenderMesh(quadRef);
            OverlayShaderProg.Stop();
            curShader?.Use();
        }

        public bool LoadShader()
        {
            OverlayShaderProg?.Dispose();
            OverlayShaderProg = capi.Shader.NewShaderProgram();
            OverlayShaderProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            OverlayShaderProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

            OverlayShaderProg.VertexShader.Code = GetVertexShaderCode();
            OverlayShaderProg.FragmentShader.Code = GetFragmentShaderCode();

            capi.Shader.RegisterMemoryShaderProgram("vsdirector-headtracking-squintoverlay", OverlayShaderProg);
            if (OverlayShaderProg.Compile()) return true;
            OverlayShaderProg.Dispose(); OverlayShaderProg = null;
            return false;
        }

        private static string GetVertexShaderCode()
        {
            return @"
        #version 330 core
        #extension GL_ARB_explicit_attrib_location: enable

        #ifdef GL_ES
        precision mediump float;
        #endif

        #extension GL_OES_standard_derivatives : enable

        layout(location = 0) in vec3 vertex;

        out vec2 uv;

        void main(void) {
          gl_Position = vec4(vertex.xy, 0, 1);
          uv = (vertex.xy + 1.0) / 2.0;
        }
      ";
        }

        private static string GetFragmentShaderCode()
        {
            return @"
        #version 330 core

        in vec2 uv;
        out vec4 outColor;

        uniform float percentZoomed;
        uniform vec2 resolution;

        void main () {
          float dist = distance(uv.xy, vec2(0.5,0.5));
          float viewStrength = smoothstep(0.45, 0.38, dist * smoothstep(-1, 1, percentZoomed));
          outColor = vec4(0, 0, 0, min(0.8, 1 - viewStrength));
        }
      ";
        }
    }
}
