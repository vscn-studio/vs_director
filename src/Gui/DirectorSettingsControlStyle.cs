using Cairo;
using Vintagestory.API.Client;

namespace VSDirector.Gui;

/// <summary>Optional Cairo styling for custom Director workspace controls.</summary>
internal static class DirectorSettingsControlStyle
{
    internal static void RoundedRectangle(Context context, double x, double y, double width, double height, double radius)
    {
        double r = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        context.NewPath();
        context.Arc(x + width - r, y + r, r, -Math.PI / 2, 0);
        context.Arc(x + width - r, y + height - r, r, 0, Math.PI / 2);
        context.Arc(x + r, y + height - r, r, Math.PI / 2, Math.PI);
        context.Arc(x + r, y + r, r, Math.PI, Math.PI * 1.5);
        context.ClosePath();
    }

    internal static void Button(Context context, double width, double height, int state, string tone)
    {
        (int r, int g, int b) = state == 3 ? (48, 48, 48) : (tone, state) switch
        {
            ("primary", 1) => (69, 117, 185), ("primary", 2) => (89, 142, 212), ("primary", _) => (54, 95, 155),
            ("success", 1) => (64, 128, 90), ("success", 2) => (82, 158, 112), ("success", _) => (50, 96, 68),
            ("danger", 1) => (166, 80, 91), ("danger", 2) => (199, 102, 114), ("danger", _) => (133, 61, 70),
            (_, 1) => (65, 65, 65), (_, 2) => (88, 88, 88), _ => (48, 48, 48)
        };
        RoundedRectangle(context, 0, 0, width, height, GuiElement.scaled(4));
        context.SetSourceRGB(r / 255d, g / 255d, b / 255d);
        context.Fill();
    }

    internal static void Field(Context context, double x, double y, double width, double height, bool enabled, bool active)
    {
        RoundedRectangle(context, x + .5, y + .5, width - 1, height - 1, GuiElement.scaled(4));
        double fill = enabled && active ? 65d / 255 : 48d / 255;
        context.SetSourceRGB(fill, fill, fill);
        context.FillPreserve();
        context.SetSourceRGB(82d / 255, 84d / 255, 90d / 255);
        context.LineWidth = 1;
        context.Stroke();
    }
}
