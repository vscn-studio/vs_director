using Vintagestory.API.Client;

namespace VSDirector.Rendering;

/// <summary>Additional state touched by rendering an editor-only selection texture.</summary>
internal sealed class DirectorCreationSelectionState : IDisposable
{
    private static readonly Lazy<Bindings> Api = new(() => new());
    private readonly Bindings gl = Api.Value;
    private readonly int drawBuffer, readBuffer;
    private readonly int[] viewport = new int[4];
    private readonly bool[] colorMask = new bool[4];
    private readonly float[] clearColor = new float[4];
    private readonly bool scissor, stencil, srgb;

    internal DirectorCreationSelectionState()
    {
        drawBuffer = gl.Get(0x8CA6); readBuffer = gl.Get(0x8CAA);
        gl.GetInts(0x0BA2, viewport); gl.GetBools(0x0C23, colorMask); gl.GetFloats(0x0C22, clearColor);
        scissor = gl.Enabled(0x0C11); stencil = gl.Enabled(0x0B90); srgb = gl.Enabled(0x8DB9);
    }

    internal void Begin(FrameBufferRef target, bool union = true)
    {
        gl.Framebuffer(0x8D40, target.FboId); gl.Viewport(0, 0, target.Width, target.Height);
        gl.Disable(0x0C11); gl.Disable(0x0B90); gl.Disable(0x8DB9);
        gl.Disable(0x0B71); gl.Disable(0x0B44); gl.Mask(false);
        gl.ColorMask(true, true, true, true);
        gl.ClearColor(0, 0, 0, 0); gl.Clear(0x4000);
        if (union) { gl.Enable(0x0BE2); gl.Equation(0x8008); gl.Blend(1, 1, 1, 1); }
        else gl.Disable(0x0BE2);
    }

    public void Dispose()
    {
        gl.Framebuffer(0x8CA9, drawBuffer); gl.Framebuffer(0x8CA8, readBuffer);
        gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        gl.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
        gl.ClearColor(clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
        void Toggle(int cap, bool on) { if (on) gl.Enable(cap); else gl.Disable(cap); }
        Toggle(0x0C11, scissor); Toggle(0x0B90, stencil); Toggle(0x8DB9, srgb);
    }

    private sealed class Bindings
    {
        internal readonly Func<int, int> Get;
        internal readonly Func<int, bool> Enabled;
        internal readonly Action<int, int[]> GetInts;
        internal readonly Action<int, bool[]> GetBools;
        internal readonly Action<int, float[]> GetFloats;
        internal readonly Action<int, int> Framebuffer;
        internal readonly Action<int, int, int, int> Blend;
        internal readonly Action<int, int, int, int> Viewport;
        internal readonly Action<float, float, float, float> ClearColor;
        internal readonly Action<bool, bool, bool, bool> ColorMask;
        internal readonly Action<bool> Mask;
        internal readonly Action<int> Enable, Disable, Clear, Equation;

        internal Bindings()
        {
            Type api = DirectorOpenGlBinding.ApiType;
            Type T(string name) => DirectorOpenGlBinding.NativeType(api, name);
            Get = DirectorOpenGlBinding.Bind<Func<int, int>>(api, "GetInteger", T("GetPName"));
            Enabled = DirectorOpenGlBinding.Bind<Func<int, bool>>(api, "IsEnabled", T("EnableCap"));
            GetInts = DirectorOpenGlBinding.Bind<Action<int, int[]>>(api, "GetInteger", T("GetPName"), typeof(int[]));
            GetBools = DirectorOpenGlBinding.Bind<Action<int, bool[]>>(api, "GetBoolean", T("GetPName"), typeof(bool[]));
            GetFloats = DirectorOpenGlBinding.Bind<Action<int, float[]>>(api, "GetFloat", T("GetPName"), typeof(float[]));
            Framebuffer = DirectorOpenGlBinding.Bind<Action<int, int>>(api, "BindFramebuffer", T("FramebufferTarget"), typeof(int));
            Viewport = DirectorOpenGlBinding.Bind<Action<int, int, int, int>>(api, "Viewport", typeof(int), typeof(int), typeof(int), typeof(int));
            ClearColor = DirectorOpenGlBinding.Bind<Action<float, float, float, float>>(api, "ClearColor", typeof(float), typeof(float), typeof(float), typeof(float));
            ColorMask = DirectorOpenGlBinding.Bind<Action<bool, bool, bool, bool>>(api, "ColorMask", typeof(bool), typeof(bool), typeof(bool), typeof(bool));
            Mask = DirectorOpenGlBinding.Bind<Action<bool>>(api, "DepthMask", typeof(bool));
            Enable = DirectorOpenGlBinding.Bind<Action<int>>(api, "Enable", T("EnableCap"));
            Disable = DirectorOpenGlBinding.Bind<Action<int>>(api, "Disable", T("EnableCap"));
            Clear = DirectorOpenGlBinding.Bind<Action<int>>(api, "Clear", T("ClearBufferMask"));
            Equation = DirectorOpenGlBinding.Bind<Action<int>>(api, "BlendEquation", T("BlendEquationMode"));
            Blend = DirectorOpenGlBinding.Bind<Action<int, int, int, int>>(api, "BlendFuncSeparate", T("BlendingFactorSrc"), T("BlendingFactorDest"), T("BlendingFactorSrc"), T("BlendingFactorDest"));
        }
    }
}
