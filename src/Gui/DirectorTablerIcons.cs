using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSDirector.Gui;

internal static class DirectorTablerIcons
{
    internal static void Draw(ICoreClientAPI api, Context context, string name,
        double centerX, double centerY, double size, double alpha = 1, int red = 238, int green = 238, int blue = 238)
    {
        var asset = api.Assets.TryGet(new AssetLocation($"vsdirector:textures/icons/tabler/{name}.svg"));
        if (asset is null) return;
        int pixels = Math.Max(1, (int)Math.Round(size));
        using var surface = new ImageSurface(Format.Argb32, pixels, pixels);
        api.Gui.DrawSvg(asset, surface, 0, 0, pixels, pixels,
            ColorUtil.ToRgba(Math.Clamp((int)Math.Round(alpha * 255), 0, 255), red, green, blue));
        // Composite through the existing Cairo context to preserve menu clipping.
        context.Save();
        context.SetSourceSurface(surface, (int)Math.Round(centerX - pixels / 2d), (int)Math.Round(centerY - pixels / 2d));
        context.Paint();
        context.Restore();
    }
}
