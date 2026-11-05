using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VSDirector.Gui;

/// <summary>
/// Draws filled GUI rectangles through a Cairo-backed texture quad. This is
/// intentionally separate from IRenderAPI.RenderRectangle, which renders a
/// rectangle outline in Vintage Story.
/// </summary>
internal sealed class DirectorSolidTextureRenderer : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly Dictionary<int, Vec4f> premultipliedTints = new();
    private LoadedTexture texture;
    private bool disposed;

    public DirectorSolidTextureRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        texture = new LoadedTexture(capi);

        using ImageSurface surface = new(Format.Argb32, 2, 2);
        using Context context = new(surface);
        context.Rectangle(0d, 0d, surface.Width, surface.Height);
        context.SetSourceRGBA(1d, 1d, 1d, 1d);
        context.Fill();
        // This texture is also used for one-physical-pixel dividers. Linear
        // filtering would blend the edge into the adjacent pixel and make a
        // single line appear doubled while a pane is scrolled.
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: false, ref texture);
    }

    public void Render(
        double x,
        double y,
        double width,
        double height,
        float z,
        int color)
    {
        if (disposed || width <= 0d || height <= 0d)
        {
            return;
        }

        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            x,
            y,
            width,
            height,
            z,
            PremultipliedTint(color));
    }

    private Vec4f PremultipliedTint(int color)
    {
        if (premultipliedTints.TryGetValue(color, out Vec4f? tint))
        {
            return tint;
        }

        Vec4f straight = new();
        ColorUtil.ToRGBAVec4f(color, ref straight);
        tint = new Vec4f(
            straight.X * straight.W,
            straight.Y * straight.W,
            straight.Z * straight.W,
            straight.W);
        premultipliedTints[color] = tint;
        return tint;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        premultipliedTints.Clear();
        texture.Dispose();
    }
}
