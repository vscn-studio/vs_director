using static VSDirector.Gui.DirectorCreationText;
namespace VSDirector.Rendering;

/// <summary>Restores the engine's GL state even if a project texture or mesh fails to load.</summary>
internal sealed class DirectorCreationRenderState : IDisposable
{
    private static readonly Lazy<Bindings> Api = new(() => new());
    private readonly Bindings gl = Api.Value;
    private readonly bool depth, cull, blend;
    private readonly int mask, srcRgb, dstRgb, srcAlpha, dstAlpha, equationRgb, equationAlpha, program, active, texture, texture0, vao, buffer;
    internal DirectorCreationRenderState()
    {
        depth = gl.Enabled(0x0B71); cull = gl.Enabled(0x0B44); blend = gl.Enabled(0x0BE2);
        mask = gl.Get(0x0B72); srcRgb = gl.Get(0x80C9); dstRgb = gl.Get(0x80C8);
        srcAlpha = gl.Get(0x80CB); dstAlpha = gl.Get(0x80CA);
        equationRgb = gl.Get(0x8009); equationAlpha = gl.Get(0x883D);
        program = gl.Get(0x8B8D); active = gl.Get(0x84E0); texture = gl.Get(0x8069);
        gl.Active(0x84C0); texture0 = gl.Get(0x8069); gl.Active(active);
        vao = gl.Get(0x85B5); buffer = gl.Get(0x8894);
    }
    public void Dispose()
    {
        void Toggle(int cap, bool on) { if (on) gl.Enable(cap); else gl.Disable(cap); }
        Toggle(0x0B71, depth); Toggle(0x0B44, cull); Toggle(0x0BE2, blend);
        gl.Mask(mask != 0); gl.Blend(srcRgb, dstRgb, srcAlpha, dstAlpha); gl.Equation(equationRgb, equationAlpha);
        gl.Program(program); gl.Active(0x84C0); gl.Texture(0x0DE1, texture0);
        gl.Active(active); gl.Texture(0x0DE1, texture); gl.Vao(vao); gl.Buffer(0x8892, buffer);
    }
    private sealed class Bindings
    {
        internal readonly Func<int, bool> Enabled;
        internal readonly Func<int, int> Get;
        internal readonly Action<int> Enable, Disable, Program, Active, Vao;
        internal readonly Action<bool> Mask;
        internal readonly Action<int, int> Texture, Buffer, Equation;
        internal readonly Action<int, int, int, int> Blend;
        internal Bindings()
        {
            Type api = DirectorOpenGlBinding.ApiType;
            Type T(string name) => DirectorOpenGlBinding.NativeType(api, name);
            Enabled = DirectorOpenGlBinding.Bind<Func<int, bool>>(api, "IsEnabled", T("EnableCap"));
            Get = DirectorOpenGlBinding.Bind<Func<int, int>>(api, "GetInteger", T("GetPName"));
            Enable = DirectorOpenGlBinding.Bind<Action<int>>(api, "Enable", T("EnableCap"));
            Disable = DirectorOpenGlBinding.Bind<Action<int>>(api, "Disable", T("EnableCap"));
            Program = DirectorOpenGlBinding.Bind<Action<int>>(api, "UseProgram", typeof(int));
            Active = DirectorOpenGlBinding.Bind<Action<int>>(api, "ActiveTexture", T("TextureUnit"));
            Mask = DirectorOpenGlBinding.Bind<Action<bool>>(api, "DepthMask", typeof(bool));
            Texture = DirectorOpenGlBinding.Bind<Action<int,int>>(api, "BindTexture", T("TextureTarget"), typeof(int));
            Vao = DirectorOpenGlBinding.Bind<Action<int>>(api, "BindVertexArray", typeof(int));
            Buffer = DirectorOpenGlBinding.Bind<Action<int,int>>(api, "BindBuffer", T("BufferTarget"), typeof(int));
            Blend = DirectorOpenGlBinding.Bind<Action<int,int,int,int>>(api, "BlendFuncSeparate", T("BlendingFactorSrc"), T("BlendingFactorDest"), T("BlendingFactorSrc"), T("BlendingFactorDest"));
            Equation = DirectorOpenGlBinding.Bind<Action<int,int>>(api, "BlendEquationSeparate", T("BlendEquationMode"), T("BlendEquationMode"));
        }
    }
}
