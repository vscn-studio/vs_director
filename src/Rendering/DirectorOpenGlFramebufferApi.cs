using Vintagestory.API.Client;

namespace VSDirector.Rendering;

internal sealed class DirectorOpenGlFramebufferApi
{
    private const int ReadFramebuffer = 0x8CA8;
    private const int DrawFramebuffer = 0x8CA9;
    private const int ColorBufferBit = 0x4000;
    private const int Linear = 0x2601;

    private readonly BindFramebufferDelegate bindFramebuffer;
    private readonly BlitFramebufferDelegate blitFramebuffer;

    private DirectorOpenGlFramebufferApi(Type glType)
    {
        Type framebufferTarget = DirectorOpenGlBinding.NativeType(glType, "FramebufferTarget");
        Type clearBufferMask = DirectorOpenGlBinding.NativeType(glType, "ClearBufferMask");
        Type blitFilter = DirectorOpenGlBinding.NativeType(glType, "BlitFramebufferFilter");

        bindFramebuffer = DirectorOpenGlBinding.Bind<BindFramebufferDelegate>(
            glType,
            "BindFramebuffer",
            framebufferTarget,
            typeof(int));
        blitFramebuffer = DirectorOpenGlBinding.Bind<BlitFramebufferDelegate>(
            glType,
            "BlitFramebuffer",
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            clearBufferMask,
            blitFilter);
    }

    internal static bool TryCreate(out DirectorOpenGlFramebufferApi? api, out string error)
    {
        try
        {
            Type glType = DirectorOpenGlBinding.ApiType;
            api = new DirectorOpenGlFramebufferApi(glType);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            api = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal void BlitColor(FrameBufferRef source, FrameBufferRef target)
    {
        bindFramebuffer(ReadFramebuffer, source.FboId);
        bindFramebuffer(DrawFramebuffer, target.FboId);
        blitFramebuffer(
            0,
            0,
            source.Width,
            source.Height,
            0,
            0,
            target.Width,
            target.Height,
            ColorBufferBit,
            Linear);
    }

    internal void BlitColorToDefault(
        FrameBufferRef source,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight)
    {
        bindFramebuffer(ReadFramebuffer, source.FboId);
        bindFramebuffer(DrawFramebuffer, 0);
        blitFramebuffer(
            0,
            0,
            source.Width,
            source.Height,
            destinationX,
            destinationY,
            destinationX + destinationWidth,
            destinationY + destinationHeight,
            ColorBufferBit,
            Linear);
    }

    private delegate void BindFramebufferDelegate(int target, int framebuffer);
    private delegate void BlitFramebufferDelegate(
        int sourceX0,
        int sourceY0,
        int sourceX1,
        int sourceY1,
        int destinationX0,
        int destinationY0,
        int destinationX1,
        int destinationY1,
        int mask,
        int filter);
}
