using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using VSDirector.Rendering;
using VSDirector.Replay;

namespace VSDirector.Gui;

internal sealed class DirectorReplayCompositionOverlay : IRenderer, IDisposable
{
    private readonly ICoreClientAPI capi;
    private LoadedTexture texture;
    private int renderedHash;
    private bool hasTexture;
    private bool disposed;

    internal DirectorReplayCompositionOverlay(ICoreClientAPI capi)
    {
        this.capi = capi;
        texture = new LoadedTexture(capi);
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "vsdirector-replay-composition");
    }

    public double RenderOrder => 0.991d;

    public int RenderRange => int.MaxValue;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        _ = stage;
        if (DirectorOffscreenRenderRuntime.IsRendering
            || DirectorReplayVisualRuntime.Current?.Composition is not { } composition
            || !HasVisibleOverlay(composition))
        {
            return;
        }

        int width = Math.Max(1, capi.Render.FrameWidth);
        int height = Math.Max(1, capi.Render.FrameHeight);
        int hash = VisualHash(composition, width, height);
        if (!hasTexture || hash != renderedHash)
        {
            Compose(composition, width, height);
            renderedHash = hash;
            hasTexture = true;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            0d,
            0d,
            width,
            height,
            9998f,
            new Vec4f(1f, 1f, 1f, 1f));
    }

    private void Compose(DirectorReplayCompositionVisuals composition, int width, int height)
    {
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        Frame frame = new(0d, 0d, width, height);

        context.SetSourceRGBA(0.9d, 0.96d, 0.98d, 0.72d);
        context.LineWidth = Math.Max(1d, width / 1920d);
        if (composition.RuleOfThirds)
        {
            Vertical(context, frame, 1d / 3d);
            Vertical(context, frame, 2d / 3d);
            Horizontal(context, frame, 1d / 3d);
            Horizontal(context, frame, 2d / 3d);
        }
        if (composition.CenterGuide)
        {
            Vertical(context, frame, 0.5d);
            Horizontal(context, frame, 0.5d);
        }
        context.Stroke();
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: true, ref texture);
    }

    private static bool HasVisibleOverlay(DirectorReplayCompositionVisuals value)
        => value.RuleOfThirds || value.CenterGuide;

    private static int VisualHash(
        DirectorReplayCompositionVisuals value,
        int width,
        int height)
    {
        HashCode hash = new();
        hash.Add(width);
        hash.Add(height);
        hash.Add(value.RuleOfThirds);
        hash.Add(value.CenterGuide);
        return hash.ToHashCode();
    }

    private static void Vertical(Context context, Frame frame, double amount)
    {
        double x = frame.X + frame.Width * amount;
        context.MoveTo(x, frame.Y);
        context.LineTo(x, frame.Bottom);
    }

    private static void Horizontal(Context context, Frame frame, double amount)
    {
        double y = frame.Y + frame.Height * amount;
        context.MoveTo(frame.X, y);
        context.LineTo(frame.Right, y);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        texture.Dispose();
    }

    private readonly record struct Frame(double X, double Y, double Width, double Height)
    {
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
    }
}
