using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VSDirector.Gui;

internal enum DirectorSurfaceKind
{
    Window,
    Card,
    OpaqueWindow,
    DockedWindow,
    OpaqueDockedWindow
}

[Flags]
internal enum DirectorSurfaceEdges
{
    None = 0,
    Top = 1 << 0,
    Right = 1 << 1,
    Bottom = 1 << 2,
    Left = 1 << 3,
    All = Top | Right | Bottom | Left
}

internal sealed class GuiElementDirectorSurface : GuiElement
{
    // Director panels share the opaque dark-gray workspace palette.
    private const double BackgroundAlpha = 1d;

    private readonly DirectorSurfaceKind kind;
    private readonly DirectorSurfaceEdges edges;

    public GuiElementDirectorSurface(
        ICoreClientAPI capi,
        ElementBounds bounds,
        DirectorSurfaceKind kind,
        DirectorSurfaceEdges edges = DirectorSurfaceEdges.All)
        : base(capi, bounds)
    {
        this.kind = kind;
        this.edges = edges;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        Bounds.CalcWorldBounds();
        bool insetPercentualBounds = Bounds.horizontalSizing is
            ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed
            || Bounds.verticalSizing is
                ElementSizing.Percentual or ElementSizing.PercentualSubstractFixed;
        double x = insetPercentualBounds ? Bounds.drawX : Bounds.bgDrawX;
        double y = insetPercentualBounds ? Bounds.drawY : Bounds.bgDrawY;
        double width = insetPercentualBounds
            ? Bounds.InnerWidth - Bounds.absPaddingX * 2d
            : Bounds.OuterWidth;
        double height = insetPercentualBounds
            ? Bounds.InnerHeight - Bounds.absPaddingY * 2d
            : Bounds.OuterHeight;
        if (width <= 1d || height <= 1d)
        {
            return;
        }

        context.Save();
        bool docked = kind is DirectorSurfaceKind.DockedWindow
            or DirectorSurfaceKind.OpaqueDockedWindow;
        if (!docked)
        {
            DirectorGuiTheme.RoundedRectangle(
                context,
                x + 0.5d,
                y + 0.5d,
                width - 1d,
                height - 1d,
                DirectorGuiTheme.ScaledCornerRadius);
        }
        else
        {
            context.Rectangle(x, y, width, height);
        }
        bool window = kind is DirectorSurfaceKind.Window
            or DirectorSurfaceKind.OpaqueWindow
            or DirectorSurfaceKind.DockedWindow
            or DirectorSurfaceKind.OpaqueDockedWindow;
        context.SetSourceRGBA(
            window ? DirectorGuiTheme.SurfaceR : DirectorGuiTheme.RaisedR,
            window ? DirectorGuiTheme.SurfaceG : DirectorGuiTheme.RaisedG,
            window ? DirectorGuiTheme.SurfaceB : DirectorGuiTheme.RaisedB,
            kind is DirectorSurfaceKind.OpaqueWindow or DirectorSurfaceKind.OpaqueDockedWindow
                ? 1d
                : BackgroundAlpha);
        if (docked)
        {
            context.Fill();
        }
        else
        {
            context.FillPreserve();
        }
        context.SetSourceRGBA(
            DirectorGuiTheme.WindowBorderR,
            DirectorGuiTheme.WindowBorderG,
            DirectorGuiTheme.WindowBorderB,
            window ? 0.72d : 0.38d);
        context.LineWidth = 1d;
        if (docked)
        {
            DrawDockedEdges(context, x, y, width, height);
        }
        else
        {
            context.Stroke();
        }
        context.Restore();
    }

    private void DrawDockedEdges(Context context, double x, double y, double width, double height)
    {
        if (edges == DirectorSurfaceEdges.None)
        {
            return;
        }

        // Fill one physical pixel for each edge instead of stroking a path.
        // A stroked 1px line at a fractional scroll offset is antialiased
        // across two pixels, which makes pane dividers look doubled while
        // scrolling. Bounds are already in the render surface's pixel space.
        double leftX = Math.Round(x);
        double rightX = Math.Round(x + width) - 1d;
        double topY = Math.Round(y);
        double bottomY = Math.Round(y + height) - 1d;
        context.NewPath();
        if ((edges & DirectorSurfaceEdges.Top) != 0)
        {
            context.Rectangle(leftX, topY, Math.Max(1d, rightX - leftX + 1d), 1d);
        }
        if ((edges & DirectorSurfaceEdges.Right) != 0)
        {
            context.Rectangle(rightX, topY, 1d, Math.Max(1d, bottomY - topY + 1d));
        }
        if ((edges & DirectorSurfaceEdges.Bottom) != 0)
        {
            context.Rectangle(leftX, bottomY, Math.Max(1d, rightX - leftX + 1d), 1d);
        }
        if ((edges & DirectorSurfaceEdges.Left) != 0)
        {
            context.Rectangle(leftX, topY, 1d, Math.Max(1d, bottomY - topY + 1d));
        }
        context.Fill();
    }
}

internal sealed class GuiElementDirectorStaticClip : GuiElement
{
    private readonly bool begin;

    public GuiElementDirectorStaticClip(
        ICoreClientAPI capi,
        ElementBounds bounds,
        bool begin)
        : base(capi, bounds)
    {
        this.begin = begin;
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = surface;
        if (!begin)
        {
            context.Restore();
            return;
        }

        Bounds.CalcWorldBounds();
        context.Save();
        context.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight);
        context.Clip();
    }
}

internal static class DirectorGuiSurfaceExtensions
{
    public static GuiComposer AddDirectorWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Window, key);

    public static GuiComposer AddDirectorCard(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.Card, key);

    public static GuiComposer AddDirectorOpaqueWindowSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.OpaqueWindow, key);

    public static GuiComposer AddDirectorDockedSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.DockedWindow, key);

    public static GuiComposer AddDirectorOpaqueDockedSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
        => AddDirectorSurface(composer, bounds, DirectorSurfaceKind.OpaqueDockedWindow, key);

    public static GuiComposer AddDirectorOpaqueDockedSurface(
        this GuiComposer composer,
        ElementBounds bounds,
        DirectorSurfaceEdges edges,
        string? key = null)
        => AddDirectorSurface(
            composer,
            bounds,
            DirectorSurfaceKind.OpaqueDockedWindow,
            key,
            edges);

    public static GuiComposer BeginDirectorStaticClip(
        this GuiComposer composer,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(composer.Api, bounds, begin: true),
                key);
        }
        return composer;
    }

    public static GuiComposer EndDirectorStaticClip(
        this GuiComposer composer,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorStaticClip(
                    composer.Api,
                    ElementBounds.Fixed(0, 0, 1, 1),
                    begin: false),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorDialogHeader(
        this GuiComposer composer,
        string title,
        Action close,
        double width,
        string key = "directorHeader",
        Action<double, double>? onMoved = null)
    {
        if (composer.Composed)
        {
            return composer;
        }

        composer.AddDirectorDialogDragArea(width, 32d, 48d, key + "-drag", onMoved);
        composer
            .AddStaticText(title, DirectorFont.WhiteSmallishText(),
                ElementBounds.Fixed(14, 8, Math.Max(40d, width - 70d), 28), key + "-title")
            .AddDirectorIconButton(
                DirectorIcon.Close,
                Lang.Get("vsdirector:close"),
                () => { close(); return true; },
                ElementBounds.Fixed(width - 38d, 6, 30, 28),
                EnumButtonStyle.Small,
                key + "-close");
        return composer;
    }

    public static GuiComposer AddDirectorDialogDragArea(
        this GuiComposer composer,
        double width,
        double height = 32d,
        double rightReserved = 48d,
        string key = "directorHeader-drag",
        Action<double, double>? onMoved = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorDragHandle(
                    composer.Api,
                    composer,
                    ElementBounds.Fixed(
                        8d,
                        4d,
                        Math.Max(40d, width - rightReserved - 14d),
                        Math.Max(20d, height)),
                    onMoved),
                key);
        }
        return composer;
    }

    private static GuiComposer AddDirectorSurface(
        GuiComposer composer,
        ElementBounds bounds,
        DirectorSurfaceKind kind,
        string? key,
        DirectorSurfaceEdges edges = DirectorSurfaceEdges.All)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(
                new GuiElementDirectorSurface(composer.Api, bounds, kind, edges),
                key);
        }
        return composer;
    }
}

internal sealed class GuiElementDirectorDragHandle : GuiElementControl
{
    private readonly GuiComposer composer;
    private readonly Action<double, double>? onMoved;
    private bool dragging;
    private int dragStartX;
    private int dragStartY;
    private double startOffsetX;
    private double startOffsetY;

    public GuiElementDirectorDragHandle(
        ICoreClientAPI capi,
        GuiComposer composer,
        ElementBounds bounds,
        Action<double, double>? onMoved = null)
        : base(capi, bounds)
    {
        this.composer = composer;
        this.onMoved = onMoved;
        MouseOverCursor = "move";
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        dragging = true;
        dragStartX = args.X;
        dragStartY = args.Y;
        startOffsetX = composer.Bounds.fixedOffsetX;
        startOffsetY = composer.Bounds.fixedOffsetY;
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!dragging)
        {
            return;
        }

        double scale = Math.Max(0.001d, GuiElement.scaled(1d));
        composer.Bounds.WithFixedAlignmentOffset(
            startOffsetX + (args.X - dragStartX) / scale,
            startOffsetY + (args.Y - dragStartY) / scale);
        composer.Bounds.MarkDirtyRecursive();
        composer.Bounds.CalcWorldBounds();
        onMoved?.Invoke(composer.Bounds.fixedOffsetX, composer.Bounds.fixedOffsetY);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && dragging)
        {
            dragging = false;
            args.Handled = true;
        }
    }
}
