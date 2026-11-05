using System.Globalization;
using System.Xml.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Gui;

/// <summary>
/// Renders solid circular keyframes from a square Cairo texture so their
/// geometry stays stable at every GUI scale.
/// </summary>
internal sealed class DirectorTimelineMarkerRenderer : IDisposable
{
    private const int TextureSize = 64;

    private readonly ICoreClientAPI capi;
    private readonly LoadedTexture circleTexture;
    private readonly LoadedTexture playheadTexture;
    private readonly LoadedTexture resizeTexture;
    private readonly LoadedTexture timelapseStartTexture;
    private readonly LoadedTexture timelapseEndTexture;
    private readonly Dictionary<DirectorReplayElementTrackType, LoadedTexture> trackTextures = new();
    private readonly Dictionary<DirectorPathElementTrackType, LoadedTexture> pathTrackTextures = new();
    private readonly Dictionary<string, LoadedTexture> timelapseLabelTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Vec4f> premultipliedTints = new();
    private bool disposed;

    public DirectorTimelineMarkerRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        circleTexture = CreateTablerTexture(
            "circle-filled",
            DrawCircle);
        playheadTexture = CreateTablerTexture(
            "caret-down-filled",
            DrawPlayhead);
        resizeTexture = CreateTablerTexture("arrows-maximize", DrawResizeHandle);
        timelapseStartTexture = CreateTablerTexture(
            "walk",
            DrawCircle);
        timelapseEndTexture = CreateTablerTexture(
            "shoe",
            DrawCircle);
        foreach ((DirectorReplayElementTrackType type, string asset) in TrackIconAssets())
        {
            trackTextures[type] = CreateTablerTexture(asset, DrawCircle);
        }
        foreach ((DirectorPathElementTrackType type, string asset) in PathTrackIconAssets())
        {
            pathTrackTextures[type] = CreateTablerTexture(asset, DrawCircle);
        }
    }

    public void Render(
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d)
        {
            return;
        }

        capi.Render.Render2DTexturePremultipliedAlpha(
            circleTexture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderResizeHandle(double right, double bottom, double size, float z, int color)
    {
        if (disposed || size <= 0d) return;
        capi.Render.Render2DTexturePremultipliedAlpha(
            resizeTexture.TextureId, right - size, bottom - size, size, size, z,
            PremultipliedTint(color));
    }

    public void RenderTrackIcon(
        DirectorReplayElementTrackType type,
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d || !trackTextures.TryGetValue(type, out LoadedTexture? texture))
        {
            return;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderTrackIcon(
        DirectorPathElementTrackType type,
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d
            || !pathTrackTextures.TryGetValue(type, out LoadedTexture? texture))
        {
            return;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderPlayhead(
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d)
        {
            return;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(
            playheadTexture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderTimelapseKeyframe(
        DirectorReplayTimelapseKeyframeRole role,
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d)
        {
            return;
        }

        LoadedTexture texture = role == DirectorReplayTimelapseKeyframeRole.End
            ? timelapseEndTexture
            : timelapseStartTexture;
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderFollowEntityKeyframe(
        DirectorReplayFollowEntityKeyframeRole role,
        double centerX,
        double centerY,
        double size,
        float z,
        int color)
    {
        if (disposed || size <= 0d)
        {
            return;
        }

        LoadedTexture texture = role == DirectorReplayFollowEntityKeyframeRole.End
            ? timelapseEndTexture
            : timelapseStartTexture;
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            centerX - size * 0.5d,
            centerY - size * 0.5d,
            size,
            size,
            z,
            PremultipliedTint(color));
    }

    public void RenderTimelapseLabel(
        string text,
        double centerX,
        double centerY,
        double width,
        double height,
        float z,
        int color)
    {
        if (disposed || string.IsNullOrWhiteSpace(text) || width <= 0d || height <= 0d)
        {
            return;
        }

        if (!timelapseLabelTextures.TryGetValue(text, out LoadedTexture? texture))
        {
            texture = CreateTimelapseLabelTexture(text);
            timelapseLabelTextures[text] = texture;
        }
        capi.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            centerX - width * 0.5d,
            centerY - height * 0.5d,
            width,
            height,
            z,
            PremultipliedTint(color));
    }

    private static IEnumerable<(DirectorReplayElementTrackType Type, string Asset)> TrackIconAssets()
    {
        yield return (DirectorReplayElementTrackType.Camera, "camera");
        yield return (DirectorReplayElementTrackType.Marker, "bookmark");
        yield return (DirectorReplayElementTrackType.Speed, "gauge");
        yield return (DirectorReplayElementTrackType.CameraOrbit, "rotate-360");
        yield return (DirectorReplayElementTrackType.TrackEntity, "focus-2");
        yield return (DirectorReplayElementTrackType.FollowEntity, "walk");
        yield return (DirectorReplayElementTrackType.CameraShake, "camera-rotate");
        yield return (DirectorReplayElementTrackType.Fov, "eye");
        yield return (DirectorReplayElementTrackType.Timelapse, "hourglass");
        yield return (DirectorReplayElementTrackType.TimeOfDay, "clock");
        yield return (DirectorReplayElementTrackType.Freeze, "snowflake");
        yield return (DirectorReplayElementTrackType.BlockOverride, "cube");
        yield return (DirectorReplayElementTrackType.VoiceAudio, "volume");
    }

    private static IEnumerable<(DirectorPathElementTrackType Type, string Asset)>
        PathTrackIconAssets()
    {
        yield return (DirectorPathElementTrackType.CameraPath, "camera");
        yield return (DirectorPathElementTrackType.Fov, "eye");
        yield return (DirectorPathElementTrackType.Roll, "rotate");
        yield return (DirectorPathElementTrackType.CameraShake, "camera-rotate");
        yield return (DirectorPathElementTrackType.LookAt, "focus-2");
        yield return (DirectorPathElementTrackType.Speed, "gauge");
        yield return (DirectorPathElementTrackType.Marker, "bookmark");
    }

    private LoadedTexture CreateTexture(Action<Context> draw)
    {
        LoadedTexture texture = new(capi);
        using ImageSurface surface = new(Format.Argb32, TextureSize, TextureSize);
        using Context context = new(surface);
        context.SetSourceRGBA(1d, 1d, 1d, 1d);
        draw(context);
        context.Fill();
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: true, ref texture);
        return texture;
    }

    private LoadedTexture CreateTimelapseLabelTexture(string text)
    {
        const int width = 320;
        const int height = 48;
        LoadedTexture texture = new(capi);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        CairoFont font = DirectorFont.WhiteSmallBoldText();
        font.UnscaledFontsize = 32d;
        font.SetupContext(context);
        TextExtents extents = context.TextExtents(text);
        FontExtents fontExtents = font.GetFontExtents();
        double x = Math.Max(4d, (width - extents.XAdvance) * 0.5d);
        double y = Math.Max(fontExtents.Ascent,
            (height - fontExtents.Height) * 0.5d + fontExtents.Ascent);
        // A dark one-pixel outline keeps the white annotation legible over
        // the dashed connector and any adjacent timeline content.
        context.SetSourceRGBA(0.015d, 0.02d, 0.025d, 0.92d);
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                if (offsetX == 0 && offsetY == 0)
                {
                    continue;
                }
                context.MoveTo(x + offsetX, y + offsetY);
                context.ShowText(text);
            }
        }
        context.SetSourceRGBA(1d, 1d, 1d, 1d);
        context.MoveTo(x, y);
        context.ShowText(text);
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: true, ref texture);
        return texture;
    }

    private LoadedTexture CreateTablerTexture(
        string iconName,
        Action<Context> fallback)
    {
        IAsset? icon = capi.Assets.TryGet(new AssetLocation(
            $"vsdirector:textures/icons/tabler/{iconName}.svg"));
        if (icon is null)
        {
            return CreateTexture(fallback);
        }

        LoadedTexture texture = new(capi);
        using ImageSurface surface = new(Format.Argb32, TextureSize, TextureSize);
        (double viewWidth, double viewHeight) = ReadViewBoxSize(icon);
        const double padding = 6d;
        double available = TextureSize - padding * 2d;
        double scale = Math.Min(
            available / Math.Max(1d, viewWidth),
            available / Math.Max(1d, viewHeight));
        int width = Math.Max(1, (int)Math.Round(viewWidth * scale));
        int height = Math.Max(1, (int)Math.Round(viewHeight * scale));
        int x = (TextureSize - width) / 2;
        int y = (TextureSize - height) / 2;
        surface.Flush();
        capi.Gui.DrawSvg(
            icon,
            surface,
            x,
            y,
            width,
            height,
            ColorUtil.ToRgba(255, 255, 255, 255));
        capi.Gui.LoadOrUpdateCairoTexture(surface, linearMag: true, ref texture);
        return texture;
    }

    private static (double Width, double Height) ReadViewBoxSize(IAsset icon)
    {
        try
        {
            string? viewBox = XDocument.Parse(icon.ToText()).Root?
                .Attribute("viewBox")?.Value;
            string[] values = (viewBox ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 4
                && double.TryParse(values[2], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double width)
                && double.TryParse(values[3], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double height)
                && width > 0d
                && height > 0d)
            {
                return (width, height);
            }
        }
        catch
        {
            // Invalid custom assets fall back to a square without blocking the editor.
        }
        return (1d, 1d);
    }

    private static void DrawResizeHandle(Context context)
    {
        context.LineWidth = 4d;
        context.LineCap = LineCap.Round;
        context.LineJoin = LineJoin.Round;
        foreach (double angle in new[] { 0d, Math.PI / 2, Math.PI, Math.PI * 1.5 })
        {
            context.Save();
            context.Translate(32d, 32d);
            context.Rotate(angle);
            context.MoveTo(4d, -4d);
            context.LineTo(18d, -18d);
            context.MoveTo(9d, -18d);
            context.LineTo(18d, -18d);
            context.LineTo(18d, -9d);
            context.Stroke();
            context.Restore();
        }
    }

    private static void DrawCircle(Context context)
    {
        context.Arc(32d, 32d, 31d, 0d, Math.PI * 2d);
    }

    private static void DrawPlayhead(Context context)
    {
        context.MoveTo(5d, 18d);
        context.LineTo(59d, 18d);
        context.LineTo(32d, 52d);
        context.ClosePath();
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
        foreach (LoadedTexture texture in trackTextures.Values)
        {
            texture.Dispose();
        }
        trackTextures.Clear();
        foreach (LoadedTexture texture in pathTrackTextures.Values)
        {
            texture.Dispose();
        }
        pathTrackTextures.Clear();
        foreach (LoadedTexture texture in timelapseLabelTextures.Values)
        {
            texture.Dispose();
        }
        timelapseLabelTextures.Clear();
        playheadTexture.Dispose();
        resizeTexture.Dispose();
        timelapseStartTexture.Dispose();
        timelapseEndTexture.Dispose();
        circleTexture.Dispose();
    }
}
