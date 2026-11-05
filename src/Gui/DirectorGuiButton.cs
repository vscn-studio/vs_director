using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VSDirector.Gui;

internal enum DirectorIcon
{
    Lock,
    Unlock,
    Eye,
    EyeOff,
    GripVertical,
    Drag,
    Bars,
    CirclePlus,
    Navigate,
    Edit,
    Add,
    Delete,
    Duplicate,
    Export,
    Import,
    Paste,
    Record,
    ZoomIn,
    ZoomOut,
    Fit,
    Select,
    Save,
    Play,
    Pause,
    Stop,
    Reverse,
    FastForward,
    SkipBack,
    SkipForward,
    Previous,
    Next,
    Capture,
    Scissors,
    Marker,
    Settings,
    Refresh,
    Speed,
    Search,
    Undo,
    Redo,
    Close
}

/// <summary>
/// Flat workspace button using the vendored Tabler SVG set for command icons.
/// Navigation and category buttons continue to use text labels.
/// </summary>
internal sealed class GuiElementDirectorButton : GuiElementControl
{
    private string text;
    private DirectorIcon? icon;
    private IAsset? iconAsset;
    private readonly ActionConsumable onClick;
    private readonly EnumButtonStyle intent;
    private readonly bool danger;
    private readonly bool rectangular;
    private readonly bool chromeless;
    private readonly double iconPadding;
    private int iconRed = 235;
    private int iconGreen = 247;
    private int iconBlue = 250;
    private LoadedTexture normalTexture;
    private LoadedTexture hoverTexture;
    private LoadedTexture pressedTexture;
    private LoadedTexture disabledTexture;
    private bool isOver;
    private bool pressed;

    internal bool Visible { get; set; } = true;
    internal bool SettingsStyle { get; set; }
    internal string SettingsTone { get; set; } = "normal";

    public GuiElementDirectorButton(
        ICoreClientAPI capi,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent)
        : this(capi, text, onClick, bounds, intent, danger: false)
    {
    }

    internal GuiElementDirectorButton(
        ICoreClientAPI capi,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent,
        bool danger,
        bool rectangular = false,
        bool chromeless = false,
        double iconPadding = 4d)
        : base(capi, bounds)
    {
        this.text = text ?? string.Empty;
        this.onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
        this.intent = intent;
        this.danger = danger;
        this.rectangular = rectangular;
        this.chromeless = chromeless;
        this.iconPadding = Math.Max(0d, iconPadding);
        normalTexture = new LoadedTexture(capi);
        hoverTexture = new LoadedTexture(capi);
        pressedTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
    }

    public GuiElementDirectorButton(
        ICoreClientAPI capi,
        DirectorIcon icon,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent)
        : this(capi, icon, onClick, bounds, intent, chromeless: false)
    {
    }

    internal GuiElementDirectorButton(
        ICoreClientAPI capi,
        DirectorIcon icon,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle intent,
        bool chromeless,
        double iconPadding = 4d)
        : this(capi, string.Empty, onClick, bounds, intent, danger: false,
            chromeless: chromeless || icon == DirectorIcon.Close, iconPadding: iconPadding)
    {
        this.icon = icon;
        iconAsset = capi.Assets.TryGet(IconLocation(icon));
    }

    public override bool Focusable => Enabled;

    internal string Text
    {
        get => text;
        set => text = value ?? string.Empty;
    }

    public void SetIcon(DirectorIcon nextIcon)
    {
        if (icon == nextIcon)
        {
            return;
        }

        icon = nextIcon;
        iconAsset = api.Assets.TryGet(IconLocation(nextIcon));
        RecomposeTextures();
    }

    public void SetIconTint(int red, int green, int blue)
    {
        red = Math.Clamp(red, 0, 255);
        green = Math.Clamp(green, 0, 255);
        blue = Math.Clamp(blue, 0, 255);
        if (iconRed == red && iconGreen == green && iconBlue == blue)
        {
            return;
        }

        iconRed = red;
        iconGreen = green;
        iconBlue = blue;
        RecomposeTextures();
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        RecomposeTextures();
    }

    private void RecomposeTextures()
    {
        ComposeTexture(ref normalTexture, 0, 0.92d, 1d);
        ComposeTexture(ref hoverTexture, 1, 0.98d, 1d);
        ComposeTexture(ref pressedTexture, 2, 0.98d, 0.94d);
        ComposeTexture(ref disabledTexture, 3, 0.42d, 0.42d);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        if (!Visible)
        {
            return;
        }

        LoadedTexture texture = !Enabled
            ? disabledTexture
            : pressed
                ? pressedTexture
                : isOver ? hoverTexture : normalTexture;
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, Bounds);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        isOver = Visible && Enabled && Bounds.PointInside(args.X, args.Y);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible || !Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        base.OnMouseDownOnElement(api, args);
        pressed = true;
        isOver = true;
        DirectorButtonInputCapture.Capture(this);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (Visible && args.Button == EnumMouseButton.Left && pressed)
        {
            ReleasePointerPress(args);
        }
    }

    internal void OnCapturedMouseUp(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && pressed)
        {
            ReleasePointerPress(args);
        }
    }

    private void ReleasePointerPress(MouseEvent args)
    {
        bool activate = Visible && Enabled && Bounds.PointInside(args.X, args.Y);
        pressed = false;
        DirectorButtonInputCapture.Release(this);
        if (activate)
        {
            args.Handled = onClick();
        }
    }

    internal void CancelPointerPress()
    {
        pressed = false;
    }

    public override void Dispose()
    {
        DirectorButtonInputCapture.Forget(this);
        normalTexture.Dispose();
        hoverTexture.Dispose();
        pressedTexture.Dispose();
        disabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeTexture(ref LoadedTexture target, int state, double alpha, double contentAlpha)
    {
        if (SettingsStyle) { ComposeSettingsTexture(ref target, state); return; }
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context context = new(surface);
        double radius = DirectorGuiTheme.ScaledCornerRadius;
        double width = Bounds.OuterWidth;
        double height = Bounds.OuterHeight;
        if (chromeless)
        {
            if (state is 1 or 2)
            {
                DrawRoundedRect(context, 0.5d, 0.5d, width - 1d, height - 1d, radius);
                context.SetSourceRGBA(0.92d, 0.97d, 0.98d, state == 1 ? 0.1d : 0.18d);
                context.Fill();
            }

            if (icon is DirectorIcon transportIcon)
            {
                DrawTablerIcon(surface, transportIcon, width, height, contentAlpha);
            }
            generateTexture(surface, ref target, linearMag: true);
            return;
        }

        bool primary = intent == EnumButtonStyle.Normal;
        (double red, double green, double blue) = danger
            ? (0.46d, 0.105d, 0.12d)
            : primary
                ? (DirectorGuiTheme.AccentR, DirectorGuiTheme.AccentG, DirectorGuiTheme.AccentB)
                : (0.19d, 0.19d, 0.19d);
        if (state == 1)
        {
            red += 0.04d;
            green += 0.04d;
            blue += 0.04d;
        }
        else if (state == 2)
        {
            red *= 0.76d;
            green *= 0.76d;
            blue *= 0.76d;
        }

        if (rectangular)
        {
            context.Rectangle(0.5d, 0.5d, width - 1d, height - 1d);
        }
        else
        {
            DrawRoundedRect(context, 0.5d, 0.5d, width - 1d, height - 1d, radius);
        }
        context.SetSourceRGBA(red, green, blue, alpha);
        context.FillPreserve();
        (double borderRed, double borderGreen, double borderBlue) = danger
            ? (0.78d, 0.29d, 0.31d)
            : primary
                ? (1d, 1d, 1d)
                : (DirectorGuiTheme.BorderR, DirectorGuiTheme.BorderG, DirectorGuiTheme.BorderB);
        context.SetSourceRGBA(
            borderRed,
            borderGreen,
            borderBlue,
            state == 3 ? 0.16d : danger ? 0.62d : primary ? 1d : 0.75d);
        context.LineWidth = 1d;
        context.Stroke();

        if (icon is DirectorIcon iconValue)
        {
            DrawTablerIcon(surface, iconValue, width, height, contentAlpha);
            generateTexture(surface, ref target, linearMag: true);
            return;
        }

        CairoFont font = rectangular
            ? DirectorFont.ToolbarButtonText()
            : DirectorFont.ButtonText();
        font.SetupContext(context);
        double horizontalPadding = GuiElement.scaled(8d);
        double availableWidth = Math.Max(1d, width - horizontalPadding * 2d);
        TextExtents fullExtents = font.GetTextExtents(text);
        if (fullExtents.Width > availableWidth)
        {
            double fit = availableWidth / Math.Max(1d, fullExtents.Width);
            font.UnscaledFontsize = Math.Max(9d, font.UnscaledFontsize * fit);
            font.SetupContext(context);
        }
        string displayText = DirectorGuiTheme.Ellipsize(context, font, text, availableWidth);
        TextExtents extents = font.GetTextExtents(displayText);
        FontExtents fontExtents = font.GetFontExtents();
        double x = Math.Max(horizontalPadding, (width - extents.Width) / 2d - extents.XBearing);
        double y = Math.Max(fontExtents.Ascent, (height - fontExtents.Height) / 2d + fontExtents.Ascent);
        context.SetSourceRGBA(0.92d, 0.97d, 0.98d, contentAlpha);
        context.MoveTo(x, y);
        context.ShowText(displayText);
        generateTexture(surface, ref target, linearMag: true);
    }

    private void ComposeSettingsTexture(ref LoadedTexture target, int state)
    {
        using ImageSurface surface = new(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using Context context = new(surface);
        DirectorSettingsControlStyle.Button(context, surface.Width, surface.Height, state, SettingsTone);
        CairoFont font = DirectorFont.WhiteSmallText().WithFontSize(16f);
        double padding = GuiElement.scaled(12), available = Math.Max(1, surface.Width - padding * 2);
        font.SetupContext(context);
        double measured = font.GetTextExtents(text).Width;
        if (measured > available) { font.UnscaledFontsize = Math.Max(12, 16 * available / measured); font.SetupContext(context); }
        string display = DirectorGuiTheme.Ellipsize(context, font, text, available);
        TextExtents extents = font.GetTextExtents(display);
        FontExtents metrics = font.GetFontExtents();
        double foreground = state == 3 ? 119d / 255 : 238d / 255;
        context.SetSourceRGB(foreground, foreground, foreground);
        context.MoveTo((surface.Width - extents.Width) / 2 - extents.XBearing,
            (surface.Height - metrics.Height) / 2 + metrics.Ascent);
        context.ShowText(display);
        generateTexture(surface, ref target, linearMag: true);
    }

    private void DrawTablerIcon(
        ImageSurface surface,
        DirectorIcon iconValue,
        double width,
        double height,
        double alpha)
    {
        if (iconAsset is null || icon != iconValue)
        {
            return;
        }

        double padding = GuiElement.scaled(iconPadding);
        int size = Math.Max(1, (int)Math.Floor(Math.Min(width, height) - padding * 2d));
        int x = Math.Max(0, (int)Math.Round((width - size) / 2d));
        int y = Math.Max(0, (int)Math.Round((height - size) / 2d));
        int color = ColorUtil.ToRgba(
            Math.Clamp((int)Math.Round(alpha * 255d), 0, 255),
            iconRed,
            iconGreen,
            iconBlue);
        surface.Flush();
        api.Gui.DrawSvg(iconAsset, surface, x, y, size, size, color);
    }

    private static AssetLocation IconLocation(DirectorIcon icon)
        => new("vsdirector:textures/icons/tabler/" + IconFileName(icon) + ".svg");

    private static string IconFileName(DirectorIcon icon)
        => icon switch
        {
            DirectorIcon.Lock => "lock",
            DirectorIcon.Unlock => "lock-open",
            DirectorIcon.Eye => "eye",
            DirectorIcon.EyeOff => "eye-off",
            DirectorIcon.GripVertical => "grip-vertical",
            DirectorIcon.Drag => "grip-horizontal",
            DirectorIcon.Bars => "menu-2",
            DirectorIcon.CirclePlus => "circle-plus",
            DirectorIcon.Navigate => "pointer",
            DirectorIcon.Edit => "pencil",
            DirectorIcon.Add => "plus",
            DirectorIcon.Delete => "trash",
            DirectorIcon.Duplicate => "copy",
            DirectorIcon.Export => "upload",
            DirectorIcon.Import => "download",
            DirectorIcon.Paste => "clipboard",
            DirectorIcon.Record => "player-record",
            DirectorIcon.ZoomIn => "zoom-in",
            DirectorIcon.ZoomOut => "zoom-out",
            DirectorIcon.Fit => "maximize",
            DirectorIcon.Select => "check",
            DirectorIcon.Save => "device-floppy",
            DirectorIcon.Play => "player-play",
            DirectorIcon.Pause => "player-pause",
            DirectorIcon.Stop => "player-stop",
            DirectorIcon.Reverse => "player-track-prev",
            DirectorIcon.FastForward => "player-track-next",
            DirectorIcon.SkipBack => "player-skip-back",
            DirectorIcon.SkipForward => "player-skip-forward",
            DirectorIcon.Previous => "chevron-left",
            DirectorIcon.Next => "chevron-right",
            DirectorIcon.Capture => "camera",
            DirectorIcon.Scissors => "scissors",
            DirectorIcon.Marker => "bookmark",
            DirectorIcon.Settings => "settings",
            DirectorIcon.Refresh => "refresh",
            DirectorIcon.Speed => "gauge",
            DirectorIcon.Search => "search",
            DirectorIcon.Undo => "arrow-back-up",
            DirectorIcon.Redo => "arrow-forward-up",
            DirectorIcon.Close => "x",
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null)
        };

    private static void DrawRoundedRect(Context context, double x, double y, double width, double height, double radius)
    {
        _ = radius;
        context.NewPath();
        context.Rectangle(x, y, width, height);
        context.ClosePath();
    }
}

internal static class DirectorGuiButtonExtensions
{
    public static GuiComposer AddDirectorToolbarButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Small,
        string? key = null,
        string? tooltip = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(
                    composer.Api,
                    text,
                    onClick,
                    bounds,
                    style,
                    danger: false,
                    rectangular: true),
                key);
            AddButtonTooltip(composer, tooltip ?? text, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Normal,
        string? key = null,
        string? tooltip = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(composer.Api, text, onClick, bounds, style),
                key);
            AddButtonTooltip(composer, tooltip ?? text, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorIconButton(
        this GuiComposer composer,
        DirectorIcon icon,
        string tooltip,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Small,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(composer.Api, icon, onClick, bounds, style),
                key);
            AddButtonTooltip(composer, tooltip, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorTransportButton(
        this GuiComposer composer,
        DirectorIcon icon,
        string tooltip,
        ActionConsumable onClick,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(
                    composer.Api,
                    icon,
                    onClick,
                    bounds,
                    EnumButtonStyle.Small,
                    chromeless: true),
                key);
            AddButtonTooltip(composer, tooltip, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorChromelessIconButton(
        this GuiComposer composer,
        DirectorIcon icon,
        string tooltip,
        ActionConsumable onClick,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(
                    composer.Api,
                    icon,
                    onClick,
                    bounds,
                    EnumButtonStyle.Small,
                    chromeless: true,
                    iconPadding: 2d),
                key);
            AddButtonTooltip(composer, tooltip, bounds, key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorDangerButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        string? key = null,
        string? tooltip = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorButton(
                    composer.Api,
                    text,
                    onClick,
                    bounds,
                    EnumButtonStyle.Normal,
                    danger: true),
                key);
            AddButtonTooltip(composer, tooltip ?? text, bounds, key);
        }
        return composer;
    }

    // Kept under the existing helper name for the workspace source until all
    // panels migrate. Calls in this namespace resolve this overload first.
    public static GuiComposer AddSmallButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        EnumButtonStyle style = EnumButtonStyle.Normal,
        string? key = null,
        string? tooltip = null)
        => AddDirectorButton(composer, text, onClick, bounds, style, key, tooltip);

    private static void AddButtonTooltip(
        GuiComposer composer,
        string tooltip,
        ElementBounds bounds,
        string? key)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return;
        }

        string tooltipKey = string.IsNullOrWhiteSpace(key) ? null! : key + "-tooltip";
        composer.AddHoverText(tooltip, DirectorFont.WhiteSmallText(), 320, bounds.FlatCopy(), tooltipKey);
        if (!string.IsNullOrWhiteSpace(tooltipKey))
        {
            composer.GetHoverText(tooltipKey).SetAutoWidth(true);
        }
    }

    public static GuiElementDirectorButton? TryGetButton(this GuiComposer composer, string key)
        => composer.GetElement(key) as GuiElementDirectorButton;

    public static GuiElementDirectorButton GetButton(this GuiComposer composer, string key)
    {
        object? element = composer.GetElement(key);
        if (element is GuiElementDirectorButton button)
        {
            return button;
        }

        throw element is null
            ? new KeyNotFoundException($"GUI button '{key}' was not found.")
            : new InvalidOperationException(
                $"GUI element '{key}' is {element.GetType().Name}, not a director button.");
    }
}
