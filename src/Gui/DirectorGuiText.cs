using Cairo;
using Vintagestory.API.Client;

namespace VSDirector.Gui;

internal sealed class GuiElementDirectorSingleLineText : GuiElement
{
    private readonly string text;
    private readonly CairoFont font;

    public GuiElementDirectorSingleLineText(
        ICoreClientAPI capi,
        string text,
        CairoFont font,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.text = text ?? string.Empty;
        this.font = font;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double width = Math.Max(1d, Bounds.InnerWidth);
        double height = Math.Max(1d, Bounds.InnerHeight);

        context.Save();
        context.Rectangle(x, y, width, height);
        context.Clip();
        font.SetupContext(context);
        string display = DirectorGuiTheme.Ellipsize(context, font, text, width);
        FontExtents extents = font.GetFontExtents();
        double baseline = y + Math.Max(extents.Ascent, (height - extents.Height) / 2d + extents.Ascent);
        context.MoveTo(x, baseline);
        context.ShowText(display);
        context.Restore();
    }
}

internal static class DirectorGuiTextExtensions
{
    public static GuiComposer AddDirectorSingleLineText(
        this GuiComposer composer,
        string text,
        CairoFont font,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorSingleLineText(composer.Api, text, font, bounds),
                key);
        }
        return composer;
    }
}
