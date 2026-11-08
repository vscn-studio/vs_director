using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VSDirector.Gui;

/// <summary>
/// Readable, locale-aware typography for Director UI. Vintage Story already
/// selects fonts that cover the active language; East Asian locales use the
/// corresponding regular family instead of the light-weight body family.
/// </summary>
internal static class DirectorFont
{
    private const double DetailSize = 15d;
    private const double BodySize = 17d;
    private const double ToolbarSize = 14d;
    private const double TitleSize = 21d;
    private const double InputSize = 18d;
    private const double MediumSize = 30d;

    public static CairoFont WhiteDetailText()
        => Create(DetailSize, FontWeight.Normal);

    public static CairoFont WhiteSmallText()
        => Create(BodySize, FontWeight.Normal);

    public static CairoFont WhiteSmallBoldText()
        => Create(BodySize, FontWeight.Bold);

    public static CairoFont WhiteSmallishText()
        => Create(TitleSize, FontWeight.Bold);

    public static CairoFont WhiteMediumText()
        => Create(MediumSize, FontWeight.Bold);

    public static CairoFont ButtonText()
        => Create(BodySize, FontWeight.Bold);

    public static CairoFont ToolbarButtonText()
        => Create(ToolbarSize, FontWeight.Normal);

    public static CairoFont TextInput()
        => Create(InputSize, FontWeight.Normal, new[] { 1d, 1d, 1d, 0.96d });

    private static CairoFont Create(
        double size,
        FontWeight weight,
        double[]? color = null)
        => new()
        {
            Color = color ?? (double[])GuiStyle.DialogDefaultTextColor.Clone(),
            Fontname = ResolveFontFamily(),
            FontWeight = weight,
            UnscaledFontsize = size
        };

    private static string ResolveFontFamily()
    {
        string standard = GuiStyle.StandardFontName;
        string regular = GuiStyle.DecorativeFontName;
        string locale = Lang.CurrentLocale?.ToLowerInvariant() ?? string.Empty;

        // The game deliberately maps these scripts to platform fonts with full
        // glyph coverage. Its decorative choice is the regular family, while
        // the standard choice can be a thin/light face that is hard to read.
        bool useLocalizedRegular = locale.StartsWith("zh-", StringComparison.Ordinal)
            || locale is "ja" or "ko" or "th" or "ar";
        if (useLocalizedRegular && !string.IsNullOrWhiteSpace(regular))
        {
            return regular;
        }

        return string.IsNullOrWhiteSpace(standard) ? "sans-serif" : standard;
    }
}
