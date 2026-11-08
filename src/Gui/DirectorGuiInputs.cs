using Cairo;
using HarmonyLib;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Gui;

internal static class DirectorNumberFormatting
{
    private const string InputFormat = "0.###";

    public static string Format(double value)
        => value.ToString(InputFormat, CultureInfo.InvariantCulture);

    public static string Format(float value)
        => value.ToString(InputFormat, CultureInfo.InvariantCulture);
}

/// <summary>
/// Captures the focused element before a composer is rebuilt and restores it
/// after the replacement has been composed. Director panels rebuild their
/// composers when values, pages, or layout state change; disposing the old
/// composer otherwise leaves the newly created text input without focus, so
/// editing keys are silently ignored until the field is clicked again.
/// </summary>
internal readonly record struct DirectorGuiFocusSnapshot(int TabIndex, string? Key);

internal static class DirectorGuiFocus
{
    private sealed class ElementKey(string key)
    {
        internal string Value { get; } = key;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GuiElement, ElementKey> elementKeys = new();

    internal static void Register(GuiElement element, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        elementKeys.Remove(element);
        elementKeys.Add(element, new ElementKey(key));
    }

    internal static DirectorGuiFocusSnapshot? Capture(GuiComposer? composer)
    {
        GuiElement? focused = composer?.CurrentTabIndexElement;
        if (focused is null || !focused.HasFocus || focused.TabIndex < 0)
        {
            return null;
        }

        string? key = elementKeys.TryGetValue(focused, out ElementKey? elementKey)
            ? elementKey.Value
            : null;
        return new DirectorGuiFocusSnapshot(focused.TabIndex, key);
    }

    internal static void Restore(GuiComposer? composer, DirectorGuiFocusSnapshot? snapshot)
    {
        if (composer is null || snapshot is not { } focus)
        {
            return;
        }

        if (focus.Key is { Length: > 0 }
            && composer.GetElement(focus.Key) is GuiElement keyedElement
            && keyedElement.Focusable
            && composer.FocusElement(keyedElement.TabIndex))
        {
            return;
        }

        composer.FocusElement(focus.TabIndex);
    }
}

internal interface IDirectorFixedPopover
{
    bool IsPopupOpen { get; }
    bool IsDisposedForCapture { get; }
    void ClosePopup();
}

internal static class DirectorDropdownInputCapture
{
    private static GuiElement? openPopup;
    private static GuiElement? mouseCapture;

    internal static GuiElement? OpenPopup
    {
        get
        {
            if (openPopup is not IDirectorFixedPopover popup || !popup.IsPopupOpen)
            {
                openPopup = null;
            }
            return openPopup;
        }
    }

    internal static void RegisterOpen(GuiElement popupElement)
    {
        if (popupElement is not IDirectorFixedPopover popup)
        {
            return;
        }
        if (openPopup is { } previous
            && !ReferenceEquals(previous, popupElement)
            && previous is IDirectorFixedPopover previousPopup)
        {
            previousPopup.ClosePopup();
        }
        openPopup = popupElement;
    }

    internal static void CaptureMouse(GuiElement popupElement)
        => mouseCapture = popupElement;

    internal static GuiElement? MouseCapture => mouseCapture;

    internal static void CancelMouseCapture()
        => mouseCapture = null;

    internal static bool IsMouseCaptured(GuiElement popupElement)
        => ReferenceEquals(mouseCapture, popupElement);

    internal static GuiElement? TakeMouseCapture()
    {
        GuiElement? captured = mouseCapture;
        mouseCapture = null;
        return captured;
    }

    internal static void NotifyClosed(GuiElement popupElement)
    {
        if (ReferenceEquals(openPopup, popupElement))
        {
            openPopup = null;
        }
        if (ReferenceEquals(mouseCapture, popupElement))
        {
            mouseCapture = null;
        }
    }
}

/// <summary>
/// Keeps a button's release event attached to the button that received the
/// press. Workspace canvases and floating panes perform their own mouse-up
/// routing, so relying only on the current composer can leave a button painted
/// pressed without ever invoking its command.
/// </summary>
internal static class DirectorButtonInputCapture
{
    private static GuiElementDirectorButton? captured;

    internal static GuiElementDirectorButton? Captured => captured;

    internal static void Capture(GuiElementDirectorButton button)
    {
        if (ReferenceEquals(captured, button))
        {
            return;
        }

        captured?.CancelPointerPress();
        DirectorDropdownInputCapture.CancelMouseCapture();
        DirectorSliderInputCapture.CancelPointerCapture();
        GuiElementDirectorNumberInput.CancelCapture();
        captured = button;
    }

    internal static GuiElementDirectorButton? Take()
    {
        GuiElementDirectorButton? result = captured;
        captured = null;
        return result;
    }

    internal static void Cancel()
    {
        GuiElementDirectorButton? button = Take();
        button?.CancelPointerPress();
    }

    internal static bool TryRelease(MouseEvent mouse)
    {
        if (mouse.Button != EnumMouseButton.Left)
        {
            return false;
        }

        GuiElementDirectorButton? button = Take();
        if (button is null)
        {
            return false;
        }

        button.OnCapturedMouseUp(mouse);
        return true;
    }

    internal static void Release(GuiElementDirectorButton button)
    {
        if (ReferenceEquals(captured, button))
        {
            captured = null;
        }
    }

    internal static void Forget(GuiElementDirectorButton button)
    {
        if (ReferenceEquals(captured, button))
        {
            // A panel can rebuild its composer between the press and release
            // (for example after a hover-driven layout update). Keep the
            // pointer capture alive so the click still reaches its command.
            return;
        }
        button.CancelPointerPress();
    }
}

/// <summary>
/// Keeps an active slider receiving pointer events after the cursor leaves its
/// bounds. GuiComposer otherwise only routes moves to the composer under the
/// cursor.
/// </summary>
internal static class DirectorSliderInputCapture
{
    private static GuiElementDirectorSlider? captured;
    private static GuiElementDirectorSlider? selected;

    internal static GuiElementDirectorSlider? Captured => captured;

    internal static bool IsSelected(GuiElementDirectorSlider slider)
        => ReferenceEquals(selected, slider);

    internal static void Select(GuiElementDirectorSlider slider)
    {
        if (ReferenceEquals(selected, slider))
        {
            return;
        }

        if (selected?.HasFocus == true)
        {
            selected.OnFocusLost();
        }
        selected = slider;
        if (!slider.HasFocus)
        {
            slider.OnFocusGained();
        }
    }

    internal static void DeselectUnlessInside(int mouseX, int mouseY)
    {
        if (selected is null || selected.Bounds.PointInside(mouseX, mouseY))
        {
            return;
        }

        GuiElementDirectorSlider previous = selected;
        selected = null;
        if (previous.HasFocus)
        {
            previous.OnFocusLost();
        }
    }

    internal static void Capture(GuiElementDirectorSlider slider)
        => captured = slider;

    internal static void CancelPointerCapture()
    {
        GuiElementDirectorSlider? previous = captured;
        captured = null;
        previous?.CancelPointerDrag();
    }

    internal static GuiElementDirectorSlider? Take()
    {
        GuiElementDirectorSlider? result = captured;
        captured = null;
        return result;
    }

    internal static void Release(GuiElementDirectorSlider slider)
    {
        if (ReferenceEquals(captured, slider))
        {
            captured = null;
        }
    }

    internal static void Forget(GuiElementDirectorSlider slider)
    {
        Release(slider);
        slider.CancelPointerDrag();
        if (ReferenceEquals(selected, slider))
        {
            selected = null;
        }
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseDown))]
internal static class DirectorGuiComposerDropdownCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        GuiComposer __instance,
        MouseEvent mouseArgs,
        Dictionary<string, GuiElement> ___interactiveElements)
    {
        DirectorSliderInputCapture.DeselectUnlessInside(mouseArgs.X, mouseArgs.Y);
        if (mouseArgs.Handled)
        {
            // The manager has already assigned this press to another layer.
            // Do not let the composer reinterpret the handled event and focus
            // its first interactive element.
            return false;
        }

        GuiElement? openDropdown = DirectorDropdownInputCapture.OpenPopup
            ?? ___interactiveElements.Values
                .Where(element => element is IDirectorFixedPopover)
                .FirstOrDefault(element => element is IDirectorFixedPopover popup && popup.IsPopupOpen);
        if (openDropdown is null)
        {
            return true;
        }

        DirectorDropdownInputCapture.RegisterOpen(openDropdown);
        if (!openDropdown.IsPositionInside(mouseArgs.X, mouseArgs.Y))
        {
            ((IDirectorFixedPopover)openDropdown).ClosePopup();
            return true;
        }

        DirectorDropdownInputCapture.CaptureMouse(openDropdown);
        openDropdown.OnMouseDown(__instance.Api, mouseArgs);
        mouseArgs.Handled = true;
        return false;
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseUp))]
internal static class DirectorGuiComposerDropdownMouseUpCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        // A button pressed in a canvas owns the release even if an older
        // popup capture was left behind by a panel rebuild.
        if (DirectorButtonInputCapture.Captured is not null)
        {
            return true;
        }

        GuiElement? captured = DirectorDropdownInputCapture.TakeMouseCapture();
        if (captured is null)
        {
            // The vanilla composer always forwards mouse-up to every
            // interactive element; Handled only controls propagation to the
            // next GUI layer. Preserve that dispatch when no popup owns the
            // pointer.
            return true;
        }

        if (captured is not IDirectorFixedPopover popup || !popup.IsDisposedForCapture)
        {
            captured.OnMouseUp(__instance.Api, mouse);
        }
        mouse.Handled = true;
        return false;
    }
}

internal static class DirectorGuiTheme
{
    public const double CornerRadius = 0d;
    public const double SurfaceR = 36d / 255d;
    public const double SurfaceG = 36d / 255d;
    public const double SurfaceB = 36d / 255d;
    public const double RaisedR = 0.16d;
    public const double RaisedG = 0.16d;
    public const double RaisedB = 0.16d;
    public const double BorderR = 0.38d;
    public const double BorderG = 0.38d;
    public const double BorderB = 0.38d;
    public const double WindowBorderR = 0.38d;
    public const double WindowBorderG = 0.38d;
    public const double WindowBorderB = 0.38d;
    public const double AccentR = 0.22d;
    public const double AccentG = 0.22d;
    public const double AccentB = 0.22d;
    public const double TextR = 0.9d;
    public const double TextG = 0.95d;
    public const double TextB = 0.96d;

    public static double ScaledCornerRadius => GuiElement.scaled(CornerRadius);

    public static void RoundedRectangle(
        Context context,
        double x,
        double y,
        double width,
        double height,
        double radius)
    {
        _ = radius;
        context.NewPath();
        context.Rectangle(x, y, width, height);
        context.ClosePath();
    }

    public static string Ellipsize(Context context, CairoFont font, string value, double maxWidth)
    {
        value ??= string.Empty;
        if (maxWidth <= 0d)
        {
            return string.Empty;
        }

        font.SetupContext(context);
        if (font.GetTextExtents(value).Width <= maxWidth)
        {
            return value;
        }

        const string suffix = "...";
        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            string candidate = value[..middle].TrimEnd() + suffix;
            if (font.GetTextExtents(candidate).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low <= 0 ? suffix : value[..low].TrimEnd() + suffix;
    }

    public static string Ellipsize(CairoFont font, string value, double maxWidth)
    {
        value ??= string.Empty;
        if (maxWidth <= 0d)
        {
            return string.Empty;
        }
        if (font.GetTextExtents(value).Width <= maxWidth)
        {
            return value;
        }

        const string suffix = "...";
        int low = 0;
        int high = value.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            string candidate = value[..middle].TrimEnd() + suffix;
            if (font.GetTextExtents(candidate).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low <= 0 ? suffix : value[..low].TrimEnd() + suffix;
    }
}

/// <summary>
/// VS Director dropdown field. The inherited API remains intact so existing
/// panels can continue to retrieve it as a GuiElementDropDown.
/// </summary>
internal sealed class GuiElementDirectorDropDown : GuiElementDropDown, IDirectorFixedPopover
{
    internal bool SettingsStyle
    {
        get => ((GuiElementDirectorListMenu)listMenu).SettingsStyle;
        set { ((GuiElementDirectorListMenu)listMenu).SettingsStyle = value; fieldStamp = int.MinValue; }
    }
    private LoadedTexture normalTexture;
    private LoadedTexture hoverFieldTexture;
    private LoadedTexture focusTexture;
    private LoadedTexture openTexture;
    private LoadedTexture disabledTexture;
    private LoadedTexture valueTexture;
    private int fieldStamp = int.MinValue;
    private int valueStamp = int.MinValue;
    private bool disposedForCapture;
    private bool visible = true;

    internal bool Visible
    {
        get => visible;
        set
        {
            if (visible == value)
            {
                return;
            }

            visible = value;
            if (!visible)
            {
                ClosePopup();
            }
        }
    }

    public GuiElementDirectorDropDown(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        bool multiSelect)
        : base(capi, values, names, selectedIndex, onSelectionChanged, bounds, font, multiSelect)
    {
        normalTexture = new LoadedTexture(capi);
        hoverFieldTexture = new LoadedTexture(capi);
        focusTexture = new LoadedTexture(capi);
        openTexture = new LoadedTexture(capi);
        disabledTexture = new LoadedTexture(capi);
        valueTexture = new LoadedTexture(capi);

        listMenu.Dispose();
        ElementBounds menuBounds = bounds
            .ForkChildOffseted(-bounds.fixedX, -bounds.fixedY)
            .WithAlignment(EnumDialogArea.None);
        listMenu = new GuiElementDirectorListMenu(
            capi,
            values,
            names,
            selectedIndex,
            HandleSelectionChanged,
            menuBounds,
            font,
            multiSelect)
        {
            HoveredIndex = selectedIndex
        };
    }

    public override bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            fieldStamp = int.MinValue;
            valueStamp = int.MinValue;
            if (!enabled)
            {
                listMenu.OnFocusLost();
            }
        }
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        _ = ctx;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeFieldTextures();
        ComposeValueTexture();
        ((GuiElementDirectorListMenu)listMenu).ComposeDirectorElements();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!Visible)
        {
            return;
        }

        EnsureTextures();
        bool hovered = Enabled && Bounds.PointInside(api.Input.MouseX, api.Input.MouseY);
        LoadedTexture field = !Enabled
            ? disabledTexture
            : listMenu.IsOpened
                ? openTexture
                : HasFocus
                    ? focusTexture
                    : hovered ? hoverFieldTexture : normalTexture;

        api.Render.Render2DTexturePremultipliedAlpha(field.TextureId, Bounds);
        api.Render.Render2DTexturePremultipliedAlpha(valueTexture.TextureId, Bounds);
        listMenu.RenderInteractiveElements(deltaTime);
    }

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible || !Enabled)
        {
            return;
        }

        if (listMenu.IsOpened)
        {
            listMenu.OnMouseDown(api, args);
            return;
        }
        if (args.Button != EnumMouseButton.Left)
        {
            return;
        }

        listMenu.OnMouseDown(api, args);
        if (!args.Handled && !listMenu.IsOpened && Bounds.PointInside(args.X, args.Y))
        {
            ((GuiElementDirectorListMenu)listMenu).OpenDirector();
            DirectorDropdownInputCapture.RegisterOpen(this);
            api.Gui.PlaySound("menubutton");
            args.Handled = true;
        }
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (Visible)
        {
            listMenu.OnMouseMove(api, args);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (!Visible)
        {
            return;
        }

        listMenu.OnMouseUp(api, args);
        args.Handled |= Bounds.PointInside(args.X, args.Y);
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Visible || !Enabled)
        {
            return;
        }

        if (listMenu.IsOpened)
        {
            listMenu.OnMouseWheel(api, args);
            return;
        }
        if (!HasFocus)
        {
            return;
        }

        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) || listMenu.Values.Length == 0)
        {
            return;
        }

        int direction = args.delta <= 0 ? 1 : -1;
        int index = GameMath.Mod(listMenu.SelectedIndex + direction, listMenu.Values.Length);
        SetSelectedIndex(index);
        valueStamp = int.MinValue;
        args.SetHandled();
        onSelectionChanged?.Invoke(SelectedValue, true);
    }

    public override bool IsPositionInside(int posX, int posY)
        => Visible
            && (Bounds.PointInside(posX, posY)
                || (listMenu.IsOpened && listMenu.IsPositionInside(posX, posY)));

    internal bool IsPopupOpen => listMenu.IsOpened;

    internal bool IsDisposedForCapture => disposedForCapture;

    bool IDirectorFixedPopover.IsPopupOpen => IsPopupOpen;

    bool IDirectorFixedPopover.IsDisposedForCapture => IsDisposedForCapture;

    void IDirectorFixedPopover.ClosePopup() => ClosePopup();

    internal void ClosePopup()
    {
        listMenu.OnFocusLost();
        DirectorDropdownInputCapture.NotifyClosed(this);
    }

    internal void SetDirectorList(string[] values, string[] names, int selectedIndex)
    {
        ((GuiElementDirectorListMenu)listMenu).SetDirectorList(values, names, selectedIndex);
        valueStamp = int.MinValue;
    }

    public override void Dispose()
    {
        disposedForCapture = true;
        DirectorDropdownInputCapture.NotifyClosed(this);
        normalTexture.Dispose();
        hoverFieldTexture.Dispose();
        focusTexture.Dispose();
        openTexture.Dispose();
        disabledTexture.Dispose();
        valueTexture.Dispose();
        base.Dispose();
    }

    private void HandleSelectionChanged(string value, bool selected)
    {
        valueStamp = int.MinValue;
        onSelectionChanged?.Invoke(value, selected);
    }

    private void EnsureTextures()
    {
        int currentFieldStamp = HashCode.Combine(
            Bounds.OuterWidthInt,
            Bounds.OuterHeightInt,
            Scale,
            Enabled);
        if (currentFieldStamp != fieldStamp)
        {
            ComposeFieldTextures();
        }

        int currentValueStamp = SelectionStamp();
        if (currentValueStamp != valueStamp)
        {
            ComposeValueTexture();
        }
    }

    private void ComposeFieldTextures()
    {
        Bounds.CalcWorldBounds();
        ComposeFieldTexture(ref normalTexture, FieldVisual.Normal);
        ComposeFieldTexture(ref hoverFieldTexture, FieldVisual.Hover);
        ComposeFieldTexture(ref focusTexture, FieldVisual.Focus);
        ComposeFieldTexture(ref openTexture, FieldVisual.Open);
        ComposeFieldTexture(ref disabledTexture, FieldVisual.Disabled);
        fieldStamp = HashCode.Combine(Bounds.OuterWidthInt, Bounds.OuterHeightInt, Scale, Enabled);
    }

    private void ComposeFieldTexture(ref LoadedTexture target, FieldVisual visual)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);

        if (SettingsStyle)
        {
            DirectorSettingsControlStyle.Field(context, 0, 0, width, height, visual != FieldVisual.Disabled,
                visual is FieldVisual.Hover or FieldVisual.Focus or FieldVisual.Open);
            string arrow = visual == FieldVisual.Open ? "chevron-up" : "chevron-down";
            var asset = api.Assets.TryGet(new AssetLocation($"vsdirector:textures/icons/tabler/{arrow}.svg"));
            if (asset is not null)
            {
                int size = (int)GuiElement.scaled(20);
                surface.Flush();
                api.Gui.DrawSvg(asset, surface, width - size - (int)GuiElement.scaled(8), (height - size) / 2,
                    size, size, ColorUtil.ToRgba(255, visual == FieldVisual.Disabled ? 119 : 238, visual == FieldVisual.Disabled ? 119 : 238, visual == FieldVisual.Disabled ? 119 : 238));
            }
            generateTexture(surface, ref target, linearMag: true);
            return;
        }

        double borderAlpha = visual == FieldVisual.Disabled ? 0.22d : 0.9d;
        double backgroundAlpha = visual == FieldVisual.Disabled ? 0.48d : 0.98d;
        double borderR = DirectorGuiTheme.BorderR;
        double borderG = DirectorGuiTheme.BorderG;
        double borderB = DirectorGuiTheme.BorderB;
        if (visual is FieldVisual.Focus or FieldVisual.Open)
        {
            borderR = 1d;
            borderG = 1d;
            borderB = 1d;
        }
        else if (visual == FieldVisual.Hover)
        {
            borderR += 0.12d;
            borderG += 0.14d;
            borderB += 0.14d;
        }

        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            backgroundAlpha);
        context.FillPreserve();
        context.SetSourceRGBA(borderR, borderG, borderB, borderAlpha);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        double arrowWidth = Math.Min(width * 0.32d, GuiElement.scaled(27d));
        double separatorX = width - arrowWidth;
        context.SetSourceRGBA(borderR, borderG, borderB, visual == FieldVisual.Disabled ? 0.16d : 0.48d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.MoveTo(separatorX, GuiElement.scaled(5d));
        context.LineTo(separatorX, height - GuiElement.scaled(5d));
        context.Stroke();

        double cx = separatorX + arrowWidth / 2d;
        double cy = height / 2d;
        DirectorTablerIcons.Draw(api, context, visual == FieldVisual.Open ? "chevron-up" : "chevron-down",
            cx, cy, GuiElement.scaled(20), visual == FieldVisual.Disabled ? .3 : .88);

        generateTexture(surface, ref target, linearMag: true);
    }

    private void ComposeValueTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        CairoFont font = Font.Clone();
        font.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            Enabled ? 0.94d : 0.34d
        };
        font.SetupContext(context);

        IEnumerable<string> selectedNames = listMenu.SelectedIndices
            .Where(index => index >= 0 && index < listMenu.Names.Length)
            .Select(index => listMenu.Names[index]);
        string display = string.Join(", ", selectedNames);
        double left = GuiElement.scaled(9d);
        double arrowWidth = Math.Min(width * 0.32d, GuiElement.scaled(27d));
        double availableWidth = Math.Max(1d, width - arrowWidth - left - GuiElement.scaled(6d));
        display = DirectorGuiTheme.Ellipsize(context, font, display, availableWidth);
        TextExtents extents = font.GetTextExtents(display);
        double y = (height - extents.Height) / 2d - extents.YBearing;
        context.SetSourceRGBA(font.Color);
        context.MoveTo(left, y);
        context.ShowText(display);
        generateTexture(surface, ref valueTexture, linearMag: true);
        valueStamp = SelectionStamp();
    }

    private int SelectionStamp()
    {
        HashCode hash = new();
        hash.Add(Enabled);
        hash.Add(Bounds.OuterWidthInt);
        hash.Add(Bounds.OuterHeightInt);
        foreach (int index in listMenu.SelectedIndices)
        {
            hash.Add(index);
        }
        foreach (string name in listMenu.Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private enum FieldVisual
    {
        Normal,
        Hover,
        Focus,
        Open,
        Disabled
    }
}

/// <summary>
/// Custom option popup used by GuiElementDirectorDropDown. It renders only the
/// visible rows, keeping large camera/entity lists responsive.
/// </summary>
internal sealed class GuiElementDirectorListMenu : GuiElementListMenu
{
    internal bool SettingsStyle { get; set; }
    private LoadedTexture menuTexture;
    private LoadedTexture rowHoverTexture;
    private LoadedTexture scrollbarThumbTexture;
    private int textureStamp = int.MinValue;
    private int rowHeightPx;
    private int searchHeightPx;
    private int listViewportHeightPx;
    private int viewportHeightPx;
    private int menuWidthPx;
    private int scrollbarWidthPx;
    private int[] filteredIndices = Array.Empty<int>();
    private string searchText = string.Empty;
    private int searchCaretIndex;
    private bool searchAllSelected;
    private ElementBounds? optionBounds;
    private bool scrollbarDragging;
    private double scrollbarGrabOffset;
    private bool wasExpanded;
    private bool consumeNextMouseUp;

    public GuiElementDirectorListMenu(
        ICoreClientAPI capi,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        bool multiSelect)
        : base(capi, values, names, selectedIndex, onSelectionChanged, bounds, font, multiSelect)
    {
        menuTexture = new LoadedTexture(capi);
        rowHoverTexture = new LoadedTexture(capi);
        scrollbarThumbTexture = new LoadedTexture(capi);
        unscaledLineHeight = 28d;
        MaxHeight = 308;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        _ = ctx;
        _ = surface;
        ComposeDirectorElements();
    }

    public void OpenDirector()
    {
        ResetSearch();
        expanded = true;
        wasExpanded = false;
        scrollbarDragging = false;
        consumeNextMouseUp = false;
        ComposeDirectorElements();
    }

    internal void SetDirectorList(string[] values, string[] names, int selectedIndex)
    {
        if (values.Length != names.Length)
        {
            throw new ArgumentException("Values and names must have the same length.");
        }

        Values = values;
        Names = names;
        SelectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, values.Length - 1);
        HoveredIndex = SelectedIndex;
        ResetSearch();
        textureStamp = int.MinValue;
    }

    public void ComposeDirectorElements()
    {
        Bounds.CalcWorldBounds();
        double scale = Math.Max(0.1d, Scale * RuntimeEnv.GUIScale);
        rowHeightPx = Math.Max(22, (int)Math.Round((SettingsStyle ? 38d : unscaledLineHeight) * scale));
        searchHeightPx = Math.Max(rowHeightPx, (int)Math.Round(32d * scale));
        scrollbarWidthPx = Math.Max(9, (int)Math.Round(10d * scale));
        RebuildFilter();
        expandedBoxHeight = Math.Max(rowHeightPx, filteredIndices.Length * rowHeightPx);
        int maximumPopupHeight = Math.Max(
            searchHeightPx + rowHeightPx,
            (int)Math.Round(MaxHeight * scale));
        listViewportHeightPx = Math.Max(
            rowHeightPx,
            Math.Min((int)Math.Round(expandedBoxHeight), maximumPopupHeight - searchHeightPx));
        viewportHeightPx = searchHeightPx + listViewportHeightPx;

        menuWidthPx = CalculateMenuWidth(scale);
        expandedBoxWidth = menuWidthPx;
        ClampScroll();
        UpdateVisibleBounds();
        ComposeVisibleMenuTexture();
        ComposeOverlayTextures();
        textureStamp = CurrentTextureStamp();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        if (!expanded)
        {
            wasExpanded = false;
            return;
        }

        if (!wasExpanded)
        {
            ScrollSelectionIntoView();
            wasExpanded = true;
        }
        EnsureComposed();

        double x = visibleBounds.renderX;
        double y = visibleBounds.renderY;
        api.Render.Render2DTexturePremultipliedAlpha(
            menuTexture.TextureId,
            x,
            y,
            menuWidthPx,
            viewportHeightPx,
            310f);

        int hoveredRow = FilteredRowOf(HoveredIndex);
        if (hoveredRow >= 0 && optionBounds is not null)
        {
            double hoverY = optionBounds.renderY + hoveredRow * rowHeightPx - scrollOffY;
            api.Render.PushScissor(optionBounds);
            api.Render.Render2DTexturePremultipliedAlpha(
                rowHoverTexture.TextureId,
                x + 1d,
                hoverY + 1d,
                Math.Max(1, menuWidthPx - (HasScrollbar ? scrollbarWidthPx : 0) - 2),
                Math.Max(1, rowHeightPx - 2),
                312f);
            api.Render.PopScissor();
        }

        if (HasScrollbar)
        {
            (double thumbY, double thumbHeight) = ScrollbarThumbMetrics();
            api.Render.Render2DTexturePremultipliedAlpha(
                scrollbarThumbTexture.TextureId,
                x + menuWidthPx - scrollbarWidthPx + GuiElement.scaled(2d),
                y + searchHeightPx + thumbY,
                Math.Max(3d, scrollbarWidthPx - GuiElement.scaled(4d)),
                thumbHeight,
                314f);
        }
    }

    public override bool IsPositionInside(int posX, int posY)
        => expanded && visibleBounds is not null && visibleBounds.PointInside(posX, posY);

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!expanded)
        {
            return;
        }
        EnsureComposed();

        if (scrollbarDragging)
        {
            MoveScrollbar(args.Y);
            args.Handled = true;
            return;
        }

        if (!visibleBounds.PointInside(args.X, args.Y))
        {
            return;
        }

        if (IsOverSearchRow(args.X, args.Y))
        {
            HoveredIndex = -1;
            args.Handled = true;
            return;
        }

        if (HasScrollbar && IsOverScrollbar(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        if (optionBounds is null || !optionBounds.PointInside(args.X, args.Y))
        {
            return;
        }

        int row = (int)Math.Floor((args.Y - optionBounds.absY + scrollOffY) / rowHeightPx);
        if (row >= 0 && row < filteredIndices.Length)
        {
            HoveredIndex = filteredIndices[row];
            args.Handled = true;
        }
    }

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (!expanded)
        {
            return;
        }
        EnsureComposed();
        consumeNextMouseUp = true;

        if (args.Button != EnumMouseButton.Left)
        {
            if (!visibleBounds.PointInside(args.X, args.Y))
            {
                CloseMenu();
            }
            args.Handled = true;
            return;
        }

        if (!visibleBounds.PointInside(args.X, args.Y))
        {
            CloseMenu();
            if (Bounds.PointInside(args.X, args.Y))
            {
                api.Gui.PlaySound("menubutton");
            }
            args.Handled = true;
            return;
        }

        if (IsOverSearchRow(args.X, args.Y))
        {
            if (IsOverSearchClear(args.X, args.Y))
            {
                SetSearchText(string.Empty, 0);
            }
            else
            {
                searchCaretIndex = searchText.Length;
                searchAllSelected = false;
                textureStamp = int.MinValue;
            }
            args.Handled = true;
            return;
        }

        if (HasScrollbar && IsOverScrollbar(args.X, args.Y))
        {
            BeginScrollbarDrag(args.Y);
            args.Handled = true;
            return;
        }

        if (optionBounds is null || !optionBounds.PointInside(args.X, args.Y))
        {
            args.Handled = true;
            return;
        }

        int row = (int)Math.Floor((args.Y - optionBounds.absY + scrollOffY) / rowHeightPx);
        if (row < 0 || row >= filteredIndices.Length)
        {
            args.Handled = true;
            return;
        }

        int index = filteredIndices[row];
        HoveredIndex = index;
        if (multiSelect)
        {
            ToggleSelection(index);
        }
        else
        {
            SelectedIndex = index;
            onSelectionChanged?.Invoke(Values[index], true);
            CloseMenu();
        }

        textureStamp = int.MinValue;
        api.Gui.PlaySound("toggleswitch");
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        _ = api;
        if (consumeNextMouseUp)
        {
            consumeNextMouseUp = false;
            scrollbarDragging = false;
            args.Handled = true;
            return;
        }
        if (scrollbarDragging && args.Button == EnumMouseButton.Left)
        {
            scrollbarDragging = false;
            args.Handled = true;
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!expanded
            || !visibleBounds.PointInside(api.Input.MouseX, api.Input.MouseY))
        {
            return;
        }

        if (optionBounds is not null
            && optionBounds.PointInside(api.Input.MouseX, api.Input.MouseY)
            && HasScrollbar)
        {
            int direction = args.delta <= 0 ? 1 : -1;
            SetScroll(scrollOffY + direction * rowHeightPx);
        }
        args.SetHandled();
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!hasFocus)
        {
            return;
        }

        if (!expanded)
        {
            if (args.KeyCode is (int)GlKeys.Up or (int)GlKeys.Down)
            {
                OpenDirector();
                args.Handled = true;
            }
            return;
        }

        if (args.KeyCode == (int)GlKeys.Escape)
        {
            if (searchText.Length > 0)
            {
                SetSearchText(string.Empty, 0);
            }
            else
            {
                CloseMenu();
            }
            args.Handled = true;
            return;
        }

        if (args.CtrlPressed || args.CommandPressed)
        {
            switch ((GlKeys)args.KeyCode)
            {
                case GlKeys.A:
                    searchAllSelected = searchText.Length > 0;
                    searchCaretIndex = searchText.Length;
                    textureStamp = int.MinValue;
                    args.Handled = true;
                    return;
                case GlKeys.C:
                    if (searchText.Length > 0)
                    {
                        api.Forms.SetClipboardText(searchText);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.X:
                    if (searchText.Length > 0)
                    {
                        api.Forms.SetClipboardText(searchText);
                        SetSearchText(string.Empty, 0);
                    }
                    args.Handled = true;
                    return;
                case GlKeys.V:
                    string clipboard = api.Forms.GetClipboardText() ?? string.Empty;
                    InsertSearchText(clipboard.Replace("\r", string.Empty).Replace("\n", string.Empty));
                    args.Handled = true;
                    return;
            }
        }

        switch ((GlKeys)args.KeyCode)
        {
            case GlKeys.Back:
                DeleteSearchBeforeCaret();
                args.Handled = true;
                return;
            case GlKeys.Delete:
                DeleteSearchAtCaret();
                args.Handled = true;
                return;
            case GlKeys.Left:
                searchAllSelected = false;
                searchCaretIndex = Math.Max(0, searchCaretIndex - 1);
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Right:
                searchAllSelected = false;
                searchCaretIndex = Math.Min(searchText.Length, searchCaretIndex + 1);
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Home:
                searchAllSelected = false;
                searchCaretIndex = 0;
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.End:
                searchAllSelected = false;
                searchCaretIndex = searchText.Length;
                textureStamp = int.MinValue;
                args.Handled = true;
                return;
            case GlKeys.Up:
                MoveHovered(-1);
                args.Handled = true;
                return;
            case GlKeys.Down:
                MoveHovered(1);
                args.Handled = true;
                return;
            case GlKeys.PageUp:
                MoveHovered(-Math.Max(1, listViewportHeightPx / rowHeightPx));
                args.Handled = true;
                return;
            case GlKeys.PageDown:
                MoveHovered(Math.Max(1, listViewportHeightPx / rowHeightPx));
                args.Handled = true;
                return;
            case GlKeys.Enter:
            case GlKeys.KeypadEnter:
                SelectHovered();
                args.Handled = true;
                return;
        }

        if (args.KeyCode != (int)GlKeys.Tab)
        {
            args.Handled = true;
        }
    }

    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
    {
        _ = api;
        if (!hasFocus
            || !expanded
            || args.CtrlPressed
            || args.CommandPressed
            || args.AltPressed
            || args.KeyChar == '\0'
            || char.IsControl(args.KeyChar))
        {
            return;
        }

        InsertSearchText(args.KeyChar.ToString());
        args.Handled = true;
    }

    public override void OnFocusLost()
    {
        base.OnFocusLost();
        CloseMenu();
    }

    public override void Dispose()
    {
        menuTexture.Dispose();
        rowHoverTexture.Dispose();
        scrollbarThumbTexture.Dispose();
        base.Dispose();
    }

    private bool HasScrollbar => expandedBoxHeight > listViewportHeightPx + 0.5d;

    private double MaxScroll => Math.Max(0d, expandedBoxHeight - listViewportHeightPx);

    private void EnsureComposed()
    {
        int stamp = CurrentTextureStamp();
        if (stamp != textureStamp)
        {
            ComposeDirectorElements();
        }
    }

    private int CalculateMenuWidth(double scale)
    {
        int minimum = Math.Max(1, Bounds.OuterWidthInt);
        double widest = minimum;
        using ImageSurface measureSurface = new(Format.Argb32, 1, 1);
        using Context measureContext = new(measureSurface);
        CairoFont font = Font.Clone();
        font.SetupContext(measureContext);
        foreach (string name in Names)
        {
            widest = Math.Max(widest, font.GetTextExtents(name ?? string.Empty).Width + 42d * scale);
        }
        widest = Math.Max(
            widest,
            font.GetTextExtents(Lang.Get("vsdirector:dropdown-search-placeholder")).Width + 68d * scale);

        int available = Math.Max(minimum,
            api.Render.FrameWidth - (int)Math.Max(0d, Bounds.absX) - (int)Math.Round(8d * scale));
        int preferredMaximum = Math.Max(minimum, (int)Math.Round(480d * scale));
        return Math.Max(minimum, Math.Min((int)Math.Ceiling(widest), Math.Min(available, preferredMaximum)));
    }

    private void UpdateVisibleBounds()
    {
        double below = api.Render.FrameHeight - (Bounds.absY + Bounds.OuterHeight);
        double above = Bounds.absY;
        double popupOffsetPx = below < viewportHeightPx + GuiElement.scaled(4d) && above > below
            ? -viewportHeightPx
            : Bounds.OuterHeight;

        visibleBounds = Bounds.FlatCopy();
        visibleBounds.fixedY += popupOffsetPx / RuntimeEnv.GUIScale;
        visibleBounds.fixedWidth = menuWidthPx / (double)RuntimeEnv.GUIScale;
        visibleBounds.fixedHeight = viewportHeightPx / (double)RuntimeEnv.GUIScale;
        visibleBounds.CalcWorldBounds();

        optionBounds = visibleBounds.FlatCopy();
        optionBounds.fixedY += searchHeightPx / (double)RuntimeEnv.GUIScale;
        optionBounds.fixedHeight = listViewportHeightPx / (double)RuntimeEnv.GUIScale;
        optionBounds.CalcWorldBounds();
    }

    private void ComposeVisibleMenuTexture()
    {
        int width = Math.Max(1, menuWidthPx);
        int height = Math.Max(1, viewportHeightPx);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);

        if (SettingsStyle) DirectorSettingsControlStyle.RoundedRectangle(context, .5, .5, width - 1, height - 1, GuiElement.scaled(4));
        else DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            0.995d);
        context.Fill();
        context.Save();
        DirectorGuiTheme.RoundedRectangle(context, 1d, 1d, width - 2d, height - 2d,
            GuiElement.scaled(3d));
        context.Clip();

        DrawSearchRow(context, width);

        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.52d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.MoveTo(GuiElement.scaled(5d), searchHeightPx - 0.5d);
        context.LineTo(width - GuiElement.scaled(5d), searchHeightPx - 0.5d);
        context.Stroke();

        context.Save();
        context.Rectangle(0d, searchHeightPx, width, listViewportHeightPx);
        context.Clip();

        CairoFont rowFont = Font.Clone();
        rowFont.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            0.96d
        };
        rowFont.SetupContext(context);
        FontExtents fontExtents = rowFont.GetFontExtents();
        double textBaselineOffset = (rowHeightPx - fontExtents.Height) / 2d + fontExtents.Ascent;
        int first = Math.Max(0, (int)Math.Floor(scrollOffY / rowHeightPx));
        int last = Math.Min(filteredIndices.Length - 1,
            (int)Math.Ceiling((scrollOffY + listViewportHeightPx) / rowHeightPx));
        double leftPadding = GuiElement.scaled(SettingsStyle && !multiSelect ? 12d : multiSelect ? 31d : 27d);
        double rightPadding = HasScrollbar ? scrollbarWidthPx + GuiElement.scaled(7d) : GuiElement.scaled(8d);

        HashSet<int> selected = SelectedIndices.ToHashSet();
        for (int row = first; row <= last; row++)
        {
            int index = filteredIndices[row];
            double rowY = searchHeightPx + row * rowHeightPx - scrollOffY;
            if (selected.Contains(index))
            {
                context.Rectangle(1d, rowY + 1d, width - 2d, rowHeightPx - 1d);
                context.SetSourceRGBA(
                    SettingsStyle ? 128d / 255 : DirectorGuiTheme.AccentR,
                    SettingsStyle ? 186d / 255 : DirectorGuiTheme.AccentG,
                    SettingsStyle ? 1d : DirectorGuiTheme.AccentB,
                    SettingsStyle ? .33d : 0.18d);
                context.Fill();
                if (!SettingsStyle)
                {
                    context.Rectangle(1d, rowY + GuiElement.scaled(4d), GuiElement.scaled(3d),
                        rowHeightPx - GuiElement.scaled(8d));
                    context.SetSourceRGBA(DirectorGuiTheme.AccentR, DirectorGuiTheme.AccentG, DirectorGuiTheme.AccentB, 0.95d);
                    context.Fill();
                }
            }

            if (!SettingsStyle)
            {
                context.SetSourceRGBA(DirectorGuiTheme.BorderR, DirectorGuiTheme.BorderG, DirectorGuiTheme.BorderB, 0.28d);
                context.LineWidth = 1d;
                context.MoveTo(GuiElement.scaled(7d), rowY + rowHeightPx - 0.5d);
                context.LineTo(width - rightPadding, rowY + rowHeightPx - 0.5d);
                context.Stroke();
            }

            if (!SettingsStyle || multiSelect) DrawSelectionMarker(context, index, rowY, selected.Contains(index));
            double textWidth = Math.Max(1d, width - leftPadding - rightPadding);
            string display = DirectorGuiTheme.Ellipsize(context, rowFont, Names[index], textWidth);
            context.SetSourceRGBA(rowFont.Color);
            context.MoveTo(leftPadding, rowY + textBaselineOffset);
            context.ShowText(display);
        }

        if (filteredIndices.Length == 0)
        {
            string noMatches = Lang.Get("vsdirector:dropdown-no-matches");
            context.SetSourceRGBA(
                DirectorGuiTheme.TextR,
                DirectorGuiTheme.TextG,
                DirectorGuiTheme.TextB,
                0.48d);
            TextExtents extents = rowFont.GetTextExtents(noMatches);
            double baseline = searchHeightPx
                + (listViewportHeightPx - extents.Height) / 2d
                - extents.YBearing;
            context.MoveTo(Math.Max(GuiElement.scaled(8d), (width - extents.Width) / 2d), baseline);
            context.ShowText(noMatches);
        }

        if (HasScrollbar)
        {
            double trackX = width - scrollbarWidthPx;
            context.Rectangle(
                trackX,
                searchHeightPx + GuiElement.scaled(3d),
                scrollbarWidthPx - GuiElement.scaled(1d),
                listViewportHeightPx - GuiElement.scaled(6d));
            context.SetSourceRGBA(
                DirectorGuiTheme.RaisedR,
                DirectorGuiTheme.RaisedG,
                DirectorGuiTheme.RaisedB,
                0.86d);
            context.Fill();
        }

        context.Restore();
        context.Restore();
        if (SettingsStyle) DirectorSettingsControlStyle.RoundedRectangle(context, .5, .5, width - 1, height - 1, GuiElement.scaled(4));
        else DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.72d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        generateTexture(surface, ref menuTexture, linearMag: true);
    }

    private void ComposeOverlayTextures()
    {
        int hoverWidth = Math.Max(1, menuWidthPx - (HasScrollbar ? scrollbarWidthPx : 0));
        using (ImageSurface surface = new(Format.Argb32, hoverWidth, Math.Max(1, rowHeightPx)))
        using (Context context = new(surface))
        {
            DirectorGuiTheme.RoundedRectangle(context, 0d, 0d, hoverWidth, rowHeightPx,
                GuiElement.scaled(2d));
            context.SetSourceRGBA(
                SettingsStyle ? 1d : DirectorGuiTheme.AccentR,
                SettingsStyle ? 1d : DirectorGuiTheme.AccentG,
                SettingsStyle ? 1d : DirectorGuiTheme.AccentB,
                0.16d);
            context.Fill();
            generateTexture(surface, ref rowHoverTexture, linearMag: true);
        }

        int thumbWidth = Math.Max(3, scrollbarWidthPx - (int)GuiElement.scaled(4d));
        using ImageSurface thumbSurface = new(Format.Argb32, thumbWidth, Math.Max(8, rowHeightPx));
        using Context thumbContext = new(thumbSurface);
        DirectorGuiTheme.RoundedRectangle(thumbContext, 0d, 0d, thumbWidth, rowHeightPx,
            Math.Max(1d, thumbWidth / 2d));
        thumbContext.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.82d);
        thumbContext.Fill();
        generateTexture(thumbSurface, ref scrollbarThumbTexture, linearMag: true);
    }

    private void DrawSelectionMarker(Context context, int index, double rowY, bool selected)
    {
        _ = index;
        double cx = GuiElement.scaled(14d);
        double cy = rowY + rowHeightPx / 2d;
        double radius = GuiElement.scaled(multiSelect ? 6d : 5d);
        if (multiSelect)
        {
            DirectorGuiTheme.RoundedRectangle(context, cx - radius, cy - radius, radius * 2d, radius * 2d,
                GuiElement.scaled(2d));
        }
        else
        {
            context.NewPath();
            context.Arc(cx, cy, radius, 0d, Math.PI * 2d);
            context.ClosePath();
        }
        context.SetSourceRGBA(
            selected ? DirectorGuiTheme.AccentR : DirectorGuiTheme.BorderR,
            selected ? DirectorGuiTheme.AccentG : DirectorGuiTheme.BorderG,
            selected ? DirectorGuiTheme.AccentB : DirectorGuiTheme.BorderB,
            selected ? 0.92d : 0.72d);
        if (selected)
        {
            context.Fill();
        }
        else
        {
            context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
            context.Stroke();
        }

        if (!selected || !multiSelect)
        {
            return;
        }
        DirectorTablerIcons.Draw(api, context, "check", cx, cy, radius * 2, .95, 10, 23, 26);
    }

    private void DrawSearchRow(Context context, int width)
    {
        double inset = GuiElement.scaled(4d);
        double fieldX = inset;
        double fieldY = inset;
        double fieldWidth = Math.Max(1d, width - inset * 2d);
        double fieldHeight = Math.Max(1d, searchHeightPx - inset * 2d);

        if (SettingsStyle) DirectorSettingsControlStyle.RoundedRectangle(context, fieldX, fieldY, fieldWidth, fieldHeight, GuiElement.scaled(4));
        else DirectorGuiTheme.RoundedRectangle(context, fieldX, fieldY, fieldWidth, fieldHeight, GuiElement.scaled(3d));
        context.SetSourceRGBA(
            DirectorGuiTheme.RaisedR,
            DirectorGuiTheme.RaisedG,
            DirectorGuiTheme.RaisedB,
            0.98d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.AccentR,
            DirectorGuiTheme.AccentG,
            DirectorGuiTheme.AccentB,
            0.78d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        double iconX = fieldX + GuiElement.scaled(12d);
        double iconY = fieldY + fieldHeight / 2d;
        DirectorTablerIcons.Draw(api, context, "search", iconX, iconY, GuiElement.scaled(18), .62);

        double clearCenterX = fieldX + fieldWidth - GuiElement.scaled(12d);
        double textLeft = fieldX + GuiElement.scaled(25d);
        double textRight = searchText.Length > 0
            ? clearCenterX - GuiElement.scaled(11d)
            : fieldX + fieldWidth - GuiElement.scaled(8d);
        double availableTextWidth = Math.Max(1d, textRight - textLeft);

        CairoFont searchFont = Font.Clone();
        searchFont.Color = new[]
        {
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            searchText.Length == 0 ? 0.46d : 0.96d
        };
        searchFont.SetupContext(context);
        FontExtents fontExtents = searchFont.GetFontExtents();
        double baseline = fieldY + (fieldHeight - fontExtents.Height) / 2d + fontExtents.Ascent;

        int visibleStart = 0;
        string display = searchText.Length == 0
            ? DirectorGuiTheme.Ellipsize(
                context,
                searchFont,
                Lang.Get("vsdirector:dropdown-search-placeholder"),
                availableTextWidth)
            : SearchDisplayText(context, searchFont, availableTextWidth, out visibleStart);
        TextExtents displayExtents = searchFont.GetTextExtents(display);
        if (searchAllSelected && searchText.Length > 0)
        {
            context.Rectangle(
                textLeft - GuiElement.scaled(2d),
                fieldY + GuiElement.scaled(3d),
                Math.Min(availableTextWidth + GuiElement.scaled(4d), displayExtents.XAdvance + GuiElement.scaled(4d)),
                fieldHeight - GuiElement.scaled(6d));
            context.SetSourceRGBA(
                DirectorGuiTheme.AccentR,
                DirectorGuiTheme.AccentG,
                DirectorGuiTheme.AccentB,
                0.34d);
            context.Fill();
        }

        context.SetSourceRGBA(searchFont.Color);
        context.MoveTo(textLeft, baseline);
        context.ShowText(display);

        if (!searchAllSelected)
        {
            double caretOffset;
            if (searchText.Length == 0)
            {
                caretOffset = 0d;
            }
            else if (searchCaretIndex <= visibleStart)
            {
                caretOffset = searchFont.GetTextExtents("…").XAdvance;
            }
            else
            {
                string prefix = visibleStart > 0 ? "…" : string.Empty;
                string beforeCaret = searchText.Substring(
                    visibleStart,
                    Math.Min(searchCaretIndex, searchText.Length) - visibleStart);
                caretOffset = searchFont.GetTextExtents(prefix + beforeCaret).XAdvance;
            }

            double caretX = Math.Clamp(
                textLeft + caretOffset,
                textLeft,
                textLeft + availableTextWidth);
            context.SetSourceRGBA(
                DirectorGuiTheme.AccentR,
                DirectorGuiTheme.AccentG,
                DirectorGuiTheme.AccentB,
                0.96d);
            context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
            context.MoveTo(caretX, fieldY + GuiElement.scaled(4d));
            context.LineTo(caretX, fieldY + fieldHeight - GuiElement.scaled(4d));
            context.Stroke();
        }

        if (searchText.Length > 0)
        {
            double clearCenterY = fieldY + fieldHeight / 2d;
            DirectorTablerIcons.Draw(api, context, "x", clearCenterX, clearCenterY, GuiElement.scaled(16), .7);
        }
    }

    private string SearchDisplayText(
        Context context,
        CairoFont font,
        double maximumWidth,
        out int visibleStart)
    {
        visibleStart = 0;
        if (font.GetTextExtents(searchText).Width <= maximumWidth)
        {
            return searchText;
        }

        const string prefix = "…";
        for (int start = 1; start < searchText.Length; start++)
        {
            string candidate = prefix + searchText[start..];
            if (font.GetTextExtents(candidate).Width <= maximumWidth)
            {
                visibleStart = start;
                return candidate;
            }
        }

        visibleStart = searchText.Length;
        return prefix;
    }

    private void RebuildFilter()
    {
        int count = Math.Min(Values.Length, Names.Length);
        string[] terms = searchText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<int> matches = new(count);
        for (int index = 0; index < count; index++)
        {
            if (terms.Length == 0 || MatchesSearch(Names[index], Values[index], terms))
            {
                matches.Add(index);
            }
        }

        filteredIndices = matches.ToArray();
        if (FilteredRowOf(HoveredIndex) < 0)
        {
            HoveredIndex = filteredIndices.Length == 0 ? -1 : filteredIndices[0];
        }
    }

    private static bool MatchesSearch(string? name, string? value, IEnumerable<string> terms)
    {
        name ??= string.Empty;
        value ??= string.Empty;
        foreach (string term in terms)
        {
            if (!name.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void SetSearchText(string value, int caretIndex)
    {
        value ??= string.Empty;
        if (value.Length > 128)
        {
            value = value[..128];
        }

        searchText = value;
        searchCaretIndex = Math.Clamp(caretIndex, 0, searchText.Length);
        searchAllSelected = false;
        RebuildFilter();
        scrollOffY = 0d;
        HoveredIndex = searchText.Length == 0 && filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices.FirstOrDefault(-1);
        textureStamp = int.MinValue;
    }

    private void ResetSearch()
    {
        searchText = string.Empty;
        searchCaretIndex = 0;
        searchAllSelected = false;
        RebuildFilter();
        scrollOffY = 0d;
        HoveredIndex = filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices.FirstOrDefault(-1);
        textureStamp = int.MinValue;
    }

    private void InsertSearchText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).ToArray());
        if (sanitized.Length == 0)
        {
            return;
        }

        string current = searchAllSelected ? string.Empty : searchText;
        int caret = searchAllSelected ? 0 : Math.Clamp(searchCaretIndex, 0, current.Length);
        string next = current.Insert(caret, sanitized);
        SetSearchText(next, Math.Min(128, caret + sanitized.Length));
    }

    private void DeleteSearchBeforeCaret()
    {
        if (searchAllSelected)
        {
            SetSearchText(string.Empty, 0);
            return;
        }

        int caret = Math.Clamp(searchCaretIndex, 0, searchText.Length);
        if (caret <= 0)
        {
            return;
        }

        SetSearchText(searchText.Remove(caret - 1, 1), caret - 1);
    }

    private void DeleteSearchAtCaret()
    {
        if (searchAllSelected)
        {
            SetSearchText(string.Empty, 0);
            return;
        }

        int caret = Math.Clamp(searchCaretIndex, 0, searchText.Length);
        if (caret >= searchText.Length)
        {
            return;
        }

        SetSearchText(searchText.Remove(caret, 1), caret);
    }

    private void MoveHovered(int delta)
    {
        if (filteredIndices.Length == 0)
        {
            HoveredIndex = -1;
            return;
        }

        int row = FilteredRowOf(HoveredIndex);
        if (row < 0)
        {
            row = delta >= 0 ? 0 : filteredIndices.Length - 1;
        }
        else
        {
            row = Math.Clamp(row + delta, 0, filteredIndices.Length - 1);
        }

        HoveredIndex = filteredIndices[row];
        ScrollHoveredIntoView();
        textureStamp = int.MinValue;
    }

    private void SelectHovered()
    {
        int index = FilteredRowOf(HoveredIndex) >= 0
            ? HoveredIndex
            : filteredIndices.FirstOrDefault(-1);
        if (index < 0 || index >= Values.Length)
        {
            return;
        }

        if (multiSelect)
        {
            ToggleSelection(index);
        }
        else
        {
            SelectedIndex = index;
            onSelectionChanged?.Invoke(Values[index], true);
            CloseMenu();
        }

        api.Gui.PlaySound("toggleswitch");
        textureStamp = int.MinValue;
    }

    private void ToggleSelection(int index)
    {
        List<int> selected = SelectedIndices.ToList();
        bool wasSelected = selected.Remove(index);
        if (!wasSelected)
        {
            selected.Add(index);
            selected.Sort();
        }
        SelectedIndices = selected.ToArray();
        onSelectionChanged?.Invoke(Values[index], !wasSelected);
        textureStamp = int.MinValue;
    }

    private int FilteredRowOf(int originalIndex)
        => Array.IndexOf(filteredIndices, originalIndex);

    private bool IsOverSearchRow(int mouseX, int mouseY)
        => expanded
            && visibleBounds.PointInside(mouseX, mouseY)
            && (optionBounds is null || mouseY < optionBounds.absY);

    private bool IsOverSearchClear(int mouseX, int mouseY)
    {
        if (searchText.Length == 0 || !IsOverSearchRow(mouseX, mouseY))
        {
            return false;
        }

        double clearWidth = Math.Max(searchHeightPx, GuiElement.scaled(28d));
        return mouseX >= visibleBounds.absX + menuWidthPx - clearWidth;
    }

    private void CloseMenu()
    {
        expanded = false;
        scrollbarDragging = false;
        wasExpanded = false;
        ResetSearch();
    }

    private bool IsOverScrollbar(int mouseX, int mouseY)
        => optionBounds is not null
            && optionBounds.PointInside(mouseX, mouseY)
            && mouseX >= visibleBounds.absX + menuWidthPx - scrollbarWidthPx;

    private void BeginScrollbarDrag(int mouseY)
    {
        (double thumbY, double thumbHeight) = ScrollbarThumbMetrics();
        double localY = mouseY - (optionBounds?.absY ?? visibleBounds.absY);
        if (localY >= thumbY && localY <= thumbY + thumbHeight)
        {
            scrollbarGrabOffset = localY - thumbY;
        }
        else
        {
            scrollbarGrabOffset = thumbHeight / 2d;
            MoveScrollbar(mouseY);
        }
        scrollbarDragging = true;
    }

    private void MoveScrollbar(int mouseY)
    {
        (_, double thumbHeight) = ScrollbarThumbMetrics();
        double trackTravel = Math.Max(1d, listViewportHeightPx - thumbHeight);
        double desiredY = mouseY - (optionBounds?.absY ?? visibleBounds.absY) - scrollbarGrabOffset;
        SetScroll(Math.Clamp(desiredY / trackTravel, 0d, 1d) * MaxScroll);
    }

    private (double Y, double Height) ScrollbarThumbMetrics()
    {
        double height = Math.Max(GuiElement.scaled(22d),
            listViewportHeightPx * listViewportHeightPx / Math.Max(listViewportHeightPx, expandedBoxHeight));
        height = Math.Min(listViewportHeightPx, height);
        double travel = Math.Max(0d, listViewportHeightPx - height);
        double y = MaxScroll <= 0d ? 0d : scrollOffY / MaxScroll * travel;
        return (y, height);
    }

    private void ScrollSelectionIntoView()
    {
        if (filteredIndices.Length == 0)
        {
            HoveredIndex = -1;
            SetScroll(0d);
            return;
        }

        HoveredIndex = filteredIndices.Contains(SelectedIndex)
            ? SelectedIndex
            : filteredIndices[0];
        ScrollHoveredIntoView();
    }

    private void ScrollHoveredIntoView()
    {
        int row = FilteredRowOf(HoveredIndex);
        if (row < 0)
        {
            return;
        }

        double top = row * rowHeightPx;
        double bottom = top + rowHeightPx;
        if (top < scrollOffY)
        {
            SetScroll(top);
        }
        else if (bottom > scrollOffY + listViewportHeightPx)
        {
            SetScroll(bottom - listViewportHeightPx);
        }
    }

    private void SetScroll(double value)
    {
        double clamped = Math.Clamp(value, 0d, MaxScroll);
        if (Math.Abs(clamped - scrollOffY) < 0.5d)
        {
            return;
        }
        scrollOffY = clamped;
        textureStamp = int.MinValue;
    }

    private void ClampScroll()
        => scrollOffY = Math.Clamp(scrollOffY, 0d, MaxScroll);

    private int CurrentTextureStamp()
    {
        HashCode hash = new();
        hash.Add(Bounds.OuterWidthInt);
        hash.Add(Bounds.OuterHeightInt);
        hash.Add(api.Render.FrameWidth);
        hash.Add(api.Render.FrameHeight);
        hash.Add(Scale);
        hash.Add((int)Math.Round(scrollOffY));
        hash.Add(MaxHeight);
        hash.Add(multiSelect);
        hash.Add(searchText, StringComparer.Ordinal);
        hash.Add(searchCaretIndex);
        hash.Add(searchAllSelected);
        foreach (string value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
        foreach (string name in Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }
        foreach (int index in SelectedIndices)
        {
            hash.Add(index);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Compact numeric field with horizontal drag and direct text editing.</summary>
internal sealed class GuiElementDirectorNumberInput : GuiElementNumberInput
{
    internal static GuiElementDirectorNumberInput? Captured { get; private set; }
    private readonly Action? onFocusLost;
    private readonly Action<float> renderText;
    private readonly Action recomposeText;
    private readonly Action<ICoreClientAPI, MouseEvent> textMouseDown;
    private LoadedTexture disabledOverlayTexture;
    private bool pointerDown, dragging;
    private int lastDragX, startDragX;
    private double dragValue;
    private long lastClick;

    public GuiElementDirectorNumberInput(ICoreClientAPI capi, ElementBounds bounds,
        Action<string>? onTextChanged, CairoFont font, Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font)
    {
        this.onFocusLost = onFocusLost;
        Interval = 1;
        disabledOverlayTexture = new LoadedTexture(capi);
        // Keep the game's text editing, bypassing only the numeric stepper UI.
        renderText = AccessTools.MethodDelegate<Action<float>>(
            AccessTools.Method(typeof(GuiElementTextInput), nameof(RenderInteractiveElements)), this, false, null);
        textMouseDown = AccessTools.MethodDelegate<Action<ICoreClientAPI, MouseEvent>>(
            AccessTools.Method(typeof(GuiElementTextInput), nameof(OnMouseDownOnElement)), this, false, null);
        recomposeText = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(GuiElementTextInput), "RecomposeText"), this, false, null);
    }

    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        using (ImageSurface scratch = new(Format.Argb32, Math.Max(1, surface.Width), Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratch))
        {
            bool requestedEnabled = enabled;
            try
            {
                enabled = true;
                base.ComposeTextElements(scratchContext, scratch);
                AccessTools.Field(typeof(GuiElementTextInput), "rightSpacing").SetValue(this, 0d);
                recomposeText();
            }
            finally { enabled = requestedEnabled; }
        }
        Bounds.CalcWorldBounds();
        DirectorGuiTheme.RoundedRectangle(ctx, Bounds.drawX + .5, Bounds.drawY + .5,
            Bounds.OuterWidth - 1, Bounds.OuterHeight - 1, DirectorGuiTheme.ScaledCornerRadius);
        ctx.SetSourceRGBA(DirectorGuiTheme.SurfaceR, DirectorGuiTheme.SurfaceG, DirectorGuiTheme.SurfaceB, 1);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(DirectorGuiTheme.BorderR, DirectorGuiTheme.BorderG, DirectorGuiTheme.BorderB, .88);
        ctx.LineWidth = Math.Max(1, GuiElement.scaled(1));
        ctx.Stroke();
        highlightBounds = Bounds.CopyOffsetedSibling().WithFixedPadding(0, 0);
        highlightBounds.CalcWorldBounds();
        using ImageSurface overlay = new(Format.Argb32, Math.Max(1, Bounds.OuterWidthInt), Math.Max(1, Bounds.OuterHeightInt));
        using Context overlayContext = new(overlay);
        overlayContext.SetSourceRGBA(.02, .028, .032, .58);
        overlayContext.Paint();
        generateTexture(overlay, ref disabledOverlayTexture, linearMag: true);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        renderText(deltaTime);
        if (!Enabled) api.Render.Render2DTexturePremultipliedAlpha(disabledOverlayTexture.TextureId, Bounds);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left) return;
        long now = Environment.TickCount64;
        if (Key(api, GlKeys.LControl, GlKeys.RControl) || now - lastClick < 350)
        {
            textMouseDown(api, args);
            lastClick = 0;
            return;
        }
        lastClick = now;
        CancelCapture();
        Captured = this;
        pointerDown = true;
        dragging = false;
        lastDragX = startDragX = args.X;
        double.TryParse(GetText(), NumberStyles.Float, CultureInfo.InvariantCulture, out dragValue);
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!pointerDown) { base.OnMouseMove(api, args); return; }
        if (!api.Input.MouseButton.Left) { CancelCapture(); return; }
        if (!dragging && Math.Abs(args.X - startDragX) < GuiElement.scaled(3)) return;
        dragging = true;
        double step = Interval * (Key(api, GlKeys.LControl, GlKeys.RControl) ? .1 : 1)
            * (Key(api, GlKeys.LShift, GlKeys.RShift) ? 10 : 1);
        dragValue += (args.X - lastDragX) / Math.Max(.001, GuiElement.scaled(1)) * step;
        lastDragX = args.X;
        if (double.IsFinite(dragValue)) SetValue(DirectorNumberFormatting.Format(IntMode ? Math.Round(dragValue) : dragValue));
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (!pointerDown || args.Button != EnumMouseButton.Left) { base.OnMouseUp(api, args); return; }
        bool edited = dragging;
        CancelCapture();
        if (edited) onFocusLost?.Invoke();
        else textMouseDown(api, args);
        args.Handled = true;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        // Scrolling a workbench panel must not silently change a numeric value.
    }

    public override void OnFocusLost()
    {
        bool focused = HasFocus;
        base.OnFocusLost();
        if (focused) onFocusLost?.Invoke();
    }

    internal static void CancelCapture()
    {
        if (Captured is { } previous) previous.pointerDown = previous.dragging = false;
        Captured = null;
    }

    private static bool Key(ICoreClientAPI api, GlKeys left, GlKeys right)
    {
        var keys = api.Input.KeyboardKeyStateRaw;
        return (uint)left < keys.Length && keys[(int)left] || (uint)right < keys.Length && keys[(int)right];
    }

    public override void Dispose()
    {
        if (ReferenceEquals(Captured, this)) CancelCapture();
        disabledOverlayTexture.Dispose();
        base.Dispose();
    }
}

internal sealed class GuiElementDirectorTextInput : GuiElementTextInput
{
    internal bool SettingsStyle { get; set; }
    private readonly Action? onFocusLost;

    internal bool Visible { get; set; } = true;

    public GuiElementDirectorTextInput(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont font,
        Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font)
    {
        this.onFocusLost = onFocusLost;
    }

    public override void OnFocusLost()
    {
        bool wasFocused = HasFocus;
        base.OnFocusLost();
        if (wasFocused)
        {
            onFocusLost?.Invoke();
        }
    }

    public override void ComposeTextElements(Context context, ImageSurface surface)
    {
        if (!Visible)
        {
            return;
        }

        using (ImageSurface scratchSurface = new(
            Format.Argb32,
            Math.Max(1, surface.Width),
            Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratchSurface))
        {
            base.ComposeTextElements(scratchContext, scratchSurface);
        }

        Bounds.CalcWorldBounds();
        if (SettingsStyle)
        {
            DirectorSettingsControlStyle.Field(context, Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight, Enabled, false);
            using ImageSurface activeSurface = new(Format.Argb32, Math.Max(1, Bounds.OuterWidthInt), Math.Max(1, Bounds.OuterHeightInt));
            using Context activeContext = new(activeSurface);
            DirectorSettingsControlStyle.RoundedRectangle(activeContext, 1, 1, activeSurface.Width - 2, activeSurface.Height - 2, GuiElement.scaled(4));
            activeContext.SetSourceRGBA(1, 1, 1, .08);
            activeContext.Fill();
            generateTexture(activeSurface, ref highlightTexture, linearMag: true);
            highlightBounds = Bounds.CopyOffsetedSibling().WithFixedPadding(0d, 0d);
            highlightBounds.CalcWorldBounds();
            return;
        }
        DirectorGuiTheme.RoundedRectangle(
            context,
            Bounds.drawX + 0.5d,
            Bounds.drawY + 0.5d,
            Bounds.OuterWidth - 1d,
            Bounds.OuterHeight - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            Enabled ? 0.88d : 0.35d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();

        using ImageSurface focusSurface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context focusContext = new(focusSurface);
        DirectorGuiTheme.RoundedRectangle(focusContext, 1d, 1d,
            focusSurface.Width - 2d, focusSurface.Height - 2d, DirectorGuiTheme.ScaledCornerRadius);
        focusContext.SetSourceRGBA(
            1d,
            1d,
            1d,
            0.95d);
        focusContext.LineWidth = Math.Max(1d, GuiElement.scaled(1.4d));
        focusContext.Stroke();
        generateTexture(focusSurface, ref highlightTexture, linearMag: true);
        highlightBounds = Bounds.CopyOffsetedSibling().WithFixedPadding(0d, 0d);
        highlightBounds.CalcWorldBounds();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (Visible)
        {
            base.RenderInteractiveElements(deltaTime);
        }
    }
}

internal sealed class GuiElementDirectorTextArea : GuiElementTextArea
{
    private readonly Action? onFocusLost;

    public GuiElementDirectorTextArea(
        ICoreClientAPI capi,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont font,
        Action? onFocusLost = null)
        : base(capi, bounds, onTextChanged, font.WithLineHeightMultiplier(1.18d))
    {
        this.onFocusLost = onFocusLost;
        Autoheight = false;
    }

    public override void OnFocusLost()
    {
        bool wasFocused = HasFocus;
        base.OnFocusLost();
        if (wasFocused)
        {
            onFocusLost?.Invoke();
        }
    }

    public override void ComposeTextElements(Context context, ImageSurface surface)
    {
        using (ImageSurface scratchSurface = new(
            Format.Argb32,
            Math.Max(1, surface.Width),
            Math.Max(1, surface.Height)))
        using (Context scratchContext = new(scratchSurface))
        {
            base.ComposeTextElements(scratchContext, scratchSurface);
        }

        Bounds.CalcWorldBounds();
        DirectorGuiTheme.RoundedRectangle(
            context,
            Bounds.drawX + 0.5d,
            Bounds.drawY + 0.5d,
            Bounds.OuterWidth - 1d,
            Bounds.OuterHeight - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        context.FillPreserve();
        context.SetSourceRGBA(
            HasFocus ? 1d : DirectorGuiTheme.BorderR,
            HasFocus ? 1d : DirectorGuiTheme.BorderG,
            HasFocus ? 1d : DirectorGuiTheme.BorderB,
            Enabled ? 0.9d : 0.35d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(HasFocus ? 1.4d : 1d));
        context.Stroke();
    }
}

internal sealed class GuiElementDirectorSwitch : GuiElementSwitch
{
    private LoadedTexture offTexture;
    private LoadedTexture enabledTexture;

    public GuiElementDirectorSwitch(
        ICoreClientAPI capi,
        Action<bool> onToggled,
        ElementBounds bounds,
        double size,
        double padding)
        : base(capi, onToggled, bounds, size, padding)
    {
        offTexture = new LoadedTexture(capi);
        enabledTexture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeState(ref offTexture, false);
        ComposeState(ref enabledTexture, true);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(
            On ? enabledTexture.TextureId : offTexture.TextureId,
            Bounds);
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        // Switches are mouse controls in the Director UI. Consuming Space
        // prevents a focused switch from toggling when the key is intended
        // for replay/free-camera input or ordinary text editing.
        _ = api;
        if (args.KeyCode == (int)GlKeys.Space)
        {
            args.Handled = true;
            return;
        }
        base.OnKeyDown(api, args);
    }

    public override void Dispose()
    {
        offTexture.Dispose();
        enabledTexture.Dispose();
        base.Dispose();
    }

    private void ComposeState(ref LoadedTexture texture, bool value)
    {
        using ImageSurface surface = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context context = new(surface);
        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d,
            surface.Width - 1d, surface.Height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(
            value ? DirectorGuiTheme.AccentR : DirectorGuiTheme.SurfaceR,
            value ? DirectorGuiTheme.AccentG : DirectorGuiTheme.SurfaceG,
            value ? DirectorGuiTheme.AccentB : DirectorGuiTheme.SurfaceB,
            Enabled ? 0.96d : 0.35d);
        context.FillPreserve();
        context.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            Enabled ? 0.9d : 0.3d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
        if (value)
        {
            DirectorTablerIcons.Draw(api, context, "check", surface.Width / 2d, surface.Height / 2d,
                Math.Min(surface.Width, surface.Height) * .85, Enabled ? 1 : .4);
        }
        generateTexture(surface, ref texture, linearMag: true);
    }
}

internal sealed class GuiElementDirectorSlider : GuiElementControl
{
    private readonly ActionConsumable<int> onChanged;
    private LoadedTexture backgroundTexture;
    private LoadedTexture valueTexture;
    private LoadedTexture trackTexture;
    private LoadedTexture progressTexture;
    private LoadedTexture thumbTexture;
    private int minimum;
    private int maximum = 100;
    private int step = 1;
    private int current;
    private bool dragging;

    public GuiElementDirectorSlider(
        ICoreClientAPI capi,
        ActionConsumable<int> onChanged,
        ElementBounds bounds)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        backgroundTexture = new LoadedTexture(capi);
        valueTexture = new LoadedTexture(capi);
        trackTexture = new LoadedTexture(capi);
        progressTexture = new LoadedTexture(capi);
        thumbTexture = new LoadedTexture(capi);
    }

    public SliderTooltipDelegate? OnSliderTooltip { get; set; }

    public int Value => current;

    public override bool Focusable => Enabled;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        using ImageSurface field = new(
            Format.Argb32,
            Math.Max(1, Bounds.OuterWidthInt),
            Math.Max(1, Bounds.OuterHeightInt));
        using Context fieldContext = new(field);
        DirectorGuiTheme.RoundedRectangle(fieldContext, 0.5d, 0.5d,
            field.Width - 1d, field.Height - 1d, DirectorGuiTheme.ScaledCornerRadius);
        fieldContext.SetSourceRGBA(
            DirectorGuiTheme.SurfaceR,
            DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB,
            1d);
        fieldContext.FillPreserve();
        fieldContext.SetSourceRGBA(
            DirectorGuiTheme.BorderR,
            DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB,
            0.88d);
        fieldContext.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        fieldContext.Stroke();
        generateTexture(field, ref backgroundTexture, linearMag: true);
        ComposeSolidTexture(ref trackTexture, 55d / 255d, 76d / 255d, 82d / 255d);
        ComposeSolidTexture(ref progressTexture, 46d / 255d, 174d / 255d, 179d / 255d);
        ComposeSolidTexture(ref thumbTexture, 224d / 255d, 242d / 255d, 244d / 255d);
        ComposeValueTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(backgroundTexture.TextureId, Bounds);
        double left = Bounds.renderX + GuiElement.scaled(9d);
        double labelWidth = GuiElement.scaled(58d);
        double usable = Math.Max(GuiElement.scaled(24d), Bounds.OuterWidth - labelWidth - GuiElement.scaled(18d));
        double fraction = (double)(current - minimum) / Math.Max(1, maximum - minimum);
        double centerY = Bounds.renderY + Bounds.OuterHeight / 2d;
        RenderSolidTexture(trackTexture, left, centerY - 2d, usable, 4d, 300f);
        RenderSolidTexture(progressTexture, left, centerY - 2d,
            Math.Max(2d, usable * fraction), 4d, 300.1f);
        double handleX = left + usable * fraction;
        RenderSolidTexture(thumbTexture, handleX - 4d, centerY - 8d, 8d, 16d, 300.2f);
        api.Render.Render2DTexturePremultipliedAlpha(
            valueTexture.TextureId,
            Bounds.renderX + Bounds.OuterWidth - labelWidth,
            Bounds.renderY,
            labelWidth,
            Bounds.OuterHeight);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }
        DirectorSliderInputCapture.Select(this);
        dragging = true;
        DirectorSliderInputCapture.Capture(this);
        args.Handled = SetFromMouse(args.X);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging)
        {
            args.Handled = SetFromMouse(args.X);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left)
        {
            dragging = false;
            DirectorSliderInputCapture.Release(this);
            args.Handled = true;
        }
    }

    internal void CancelPointerDrag()
    {
        dragging = false;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (Enabled && DirectorSliderInputCapture.IsSelected(this))
        {
            double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
            if (direction == 0d)
            {
                return;
            }

            args.SetHandled(true);
            SetValue(current + Math.Sign(direction) * step, notify: true);
        }
    }

    public void SetValues(int value, int minValue, int maxValue, int stepValue)
    {
        minimum = Math.Min(minValue, maxValue);
        maximum = Math.Max(minValue + 1, maxValue);
        step = Math.Max(1, stepValue);
        if (!SetValue(value, notify: false))
        {
            return;
        }
        ComposeValueTexture();
    }

    public bool SetValue(int value)
        => SetValue(value, notify: false);

    public override void Dispose()
    {
        DirectorSliderInputCapture.Forget(this);
        backgroundTexture.Dispose();
        valueTexture.Dispose();
        trackTexture.Dispose();
        progressTexture.Dispose();
        thumbTexture.Dispose();
        base.Dispose();
    }

    private void ComposeSolidTexture(
        ref LoadedTexture texture,
        double red,
        double green,
        double blue)
    {
        using ImageSurface surface = new(Format.Argb32, 2, 2);
        using Context context = new(surface);
        context.Rectangle(0d, 0d, surface.Width, surface.Height);
        context.SetSourceRGBA(red, green, blue, 1d);
        context.Fill();
        generateTexture(surface, ref texture, linearMag: true);
    }

    private void RenderSolidTexture(
        LoadedTexture texture,
        double x,
        double y,
        double width,
        double height,
        float z)
    {
        api.Render.Render2DTexturePremultipliedAlpha(
            texture.TextureId,
            x,
            y,
            width,
            height,
            z,
            new Vec4f(1f, 1f, 1f, 1f));
    }

    private bool SetFromMouse(int mouseX)
    {
        double left = Bounds.absX + GuiElement.scaled(9d);
        double usable = Math.Max(GuiElement.scaled(24d),
            Bounds.OuterWidth - GuiElement.scaled(76d));
        double fraction = Math.Clamp((mouseX - left) / usable, 0d, 1d);
        int requested = minimum + (int)Math.Round((maximum - minimum) * fraction);
        return SetValue(requested, notify: true);
    }

    private bool SetValue(int requested, bool notify)
    {
        int snapped = minimum + (int)Math.Round((double)(requested - minimum) / step) * step;
        int next = Math.Clamp(snapped, minimum, maximum);
        if (next == current)
        {
            return true;
        }
        current = next;
        ComposeValueTexture();
        return !notify || onChanged(current);
    }

    private void ComposeValueTexture()
    {
        int width = Math.Max(1, (int)Math.Ceiling(GuiElement.scaled(58d)));
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        string text = OnSliderTooltip?.Invoke(current) ?? current.ToString();
        TextExtents extents = font.GetTextExtents(text);
        context.SetSourceRGBA(
            DirectorGuiTheme.TextR,
            DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB,
            Enabled ? 0.92d : 0.35d);
        context.MoveTo(Math.Max(2d, (width - extents.Width) / 2d - extents.XBearing),
            (height - extents.Height) / 2d - extents.YBearing);
        context.ShowText(text);
        generateTexture(surface, ref valueTexture, linearMag: true);
    }
}

/// <summary>
/// GuiManager is the single release boundary for every loaded dialog. Handle
/// a captured Director button here before dialog order, stale popup state, or
/// a custom dialog OnMouseUp can mark the event handled and stop propagation.
/// </summary>
[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnMouseUp))]
internal static class DirectorGuiManagerButtonMouseUpCapturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(MouseEvent args)
    {
        if (!DirectorButtonInputCapture.TryRelease(args))
        {
            return true;
        }

        args.Handled = true;
        return false;
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseUp))]
internal static class DirectorGuiComposerButtonMouseUpCapturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        if (!DirectorButtonInputCapture.TryRelease(mouse))
        {
            return true;
        }

        mouse.Handled = true;
        return false;
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseMove))]
internal static class DirectorGuiComposerDropdownMouseMoveCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        if (mouse.Handled)
        {
            return false;
        }

        GuiElement? captured = DirectorDropdownInputCapture.MouseCapture;
        if (captured is null)
        {
            return true;
        }

        if (captured is not IDirectorFixedPopover popup || !popup.IsDisposedForCapture)
        {
            captured.OnMouseMove(__instance.Api, mouse);
        }
        mouse.Handled = true;
        return false;
    }
}

internal static class DirectorTimeText
{
    internal static bool TryParseTicks(string? text, out int ticks)
    {
        ticks = 0;
        string[] parts = (text ?? string.Empty).Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || hours is < 0 or > 23
            || minutes is < 0 or > 59)
        {
            return false;
        }

        ticks = (int)Math.Round((hours * 60d + minutes) / (24d * 60d) * 24000d);
        return true;
    }

    internal static string FormatTicks(int ticks)
    {
        double totalMinutes = Math.Clamp(ticks, 0, 24000) / 24000d * 24d * 60d;
        int hours = (int)Math.Floor(totalMinutes / 60d) % 24;
        int minutes = (int)Math.Round(totalMinutes - hours * 60d);
        if (minutes >= 60)
        {
            hours = (hours + 1) % 24;
            minutes = 0;
        }
        return $"{hours:00}:{minutes:00}";
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseMove))]
internal static class DirectorGuiComposerSliderMouseMoveCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        if (GuiElementDirectorNumberInput.Captured is { } number)
        {
            number.OnMouseMove(__instance.Api, mouse);
            mouse.Handled = true;
            return false;
        }
        GuiElementDirectorSlider? captured = DirectorSliderInputCapture.Captured;
        if (captured is null)
        {
            return true;
        }

        captured.OnMouseMove(__instance.Api, mouse);
        mouse.Handled = true;
        return false;
    }
}

[HarmonyPatch(typeof(GuiComposer), nameof(GuiComposer.OnMouseUp))]
internal static class DirectorGuiComposerSliderMouseUpCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GuiComposer __instance, MouseEvent mouse)
    {
        if (GuiElementDirectorNumberInput.Captured is { } number)
        {
            number.OnMouseUp(__instance.Api, mouse);
            mouse.Handled = true;
            return false;
        }
        if (DirectorButtonInputCapture.Captured is not null)
        {
            return true;
        }

        GuiElementDirectorSlider? captured = DirectorSliderInputCapture.Take();
        if (captured is null)
        {
            return true;
        }

        captured.OnMouseUp(__instance.Api, mouse);
        mouse.Handled = true;
        return false;
    }
}

/// <summary>
/// Base for the compact, non-draggable editors used by replay visual settings.
/// The popup is always anchored to the field's lower edge; it never changes
/// position to fit the screen or becomes a floating window.
/// </summary>
internal abstract class GuiElementDirectorFixedPopover : GuiElementControl, IDirectorFixedPopover
{
    protected bool popupOpen;
    protected bool disposedForCapture;

    protected GuiElementDirectorFixedPopover(ICoreClientAPI capi, ElementBounds bounds)
        : base(capi, bounds)
    {
    }

    public bool IsPopupOpen => popupOpen;

    public bool IsDisposedForCapture => disposedForCapture;

    public override bool Focusable => Enabled;

    public override bool IsPositionInside(int posX, int posY)
        => Bounds.PointInside(posX, posY) || popupOpen && PopupBounds().PointInside(posX, posY);

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (!Enabled || args.Button != EnumMouseButton.Left)
        {
            return;
        }

        if (popupOpen)
        {
            if (PopupBounds().PointInside(args.X, args.Y))
            {
                HandlePopupMouseDown(api, args);
            }
            else if (Bounds.PointInside(args.X, args.Y))
            {
                ClosePopup();
                args.Handled = true;
            }
            else
            {
                ClosePopup();
                args.Handled = true;
            }
            return;
        }

        if (Bounds.PointInside(args.X, args.Y))
        {
            TogglePopup(api);
            args.Handled = true;
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        => OnMouseDown(api, args);

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (popupOpen
            && (PopupBounds().PointInside(args.X, args.Y)
                || DirectorDropdownInputCapture.IsMouseCaptured(this)))
        {
            HandlePopupMouseMove(api, args);
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        _ = api;
        _ = args;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (popupOpen && PopupBounds().PointInside(api.Input.MouseX, api.Input.MouseY))
        {
            HandlePopupMouseWheel(api, args);
        }
    }

    public override void Dispose()
    {
        disposedForCapture = true;
        popupOpen = false;
        DirectorDropdownInputCapture.NotifyClosed(this);
        DisposeTextures();
        base.Dispose();
    }

    public void ClosePopup()
    {
        if (!popupOpen)
        {
            DirectorDropdownInputCapture.NotifyClosed(this);
            return;
        }
        popupOpen = false;
        OnPopupClosed();
        DirectorDropdownInputCapture.NotifyClosed(this);
        ComposePopupTexture();
    }

    protected void TogglePopup(ICoreClientAPI api)
    {
        if (popupOpen)
        {
            ClosePopup();
            return;
        }

        popupOpen = true;
        DirectorDropdownInputCapture.RegisterOpen(this);
        api.Gui.PlaySound("menubutton");
        ComposePopupTexture();
    }

    protected abstract ElementBounds PopupBounds();
    protected abstract void HandlePopupMouseDown(ICoreClientAPI api, MouseEvent args);
    protected virtual void HandlePopupMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        _ = api;
        _ = args;
    }
    protected virtual void HandlePopupMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        _ = api;
        _ = args;
    }
    protected virtual void OnPopupClosed()
    {
    }
    protected abstract void ComposePopupTexture();
    protected abstract void DisposeTextures();
}

internal sealed class GuiElementDirectorLegacyColorPopover : GuiElementDirectorFixedPopover
{
    private static readonly string[] Palette =
    {
        "#000000", "#FFFFFF", "#C8D8E8", "#FF0000", "#FF8000", "#FFFF00",
        "#00FF00", "#00FFFF", "#0080FF", "#8000FF", "#FF00FF", "#804000",
        "#808080", "#404040", "#800000", "#008000", "#000080", "#800080"
    };

    private readonly Action<string> onChanged;
    private LoadedTexture fieldTexture;
    private LoadedTexture popupTexture;
    private string value;

    public GuiElementDirectorLegacyColorPopover(
        ICoreClientAPI capi,
        ElementBounds bounds,
        string initialValue,
        Action<string> onChanged)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        value = NormalizeColor(initialValue);
        fieldTexture = new LoadedTexture(capi);
        popupTexture = new LoadedTexture(capi);
    }

    public string Value => value;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(fieldTexture.TextureId, Bounds);
        if (popupOpen)
        {
            ElementBounds popup = PopupBounds();
            api.Render.Render2DTexturePremultipliedAlpha(
                popupTexture.TextureId,
                popup.renderX,
                popup.renderY,
                popup.OuterWidth,
                popup.OuterHeight,
                315f);
        }
    }

    public void SetValue(string nextValue)
    {
        string normalized = NormalizeColor(nextValue);
        if (normalized == value)
        {
            return;
        }
        value = normalized;
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    protected override ElementBounds PopupBounds()
    {
        ElementBounds popup = ElementBounds.Fixed(
                Bounds.fixedX,
                Bounds.fixedY + Bounds.fixedHeight + 6d,
                280d,
                164d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        popup.CalcWorldBounds();
        return popup;
    }

    protected override void HandlePopupMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        ElementBounds popup = PopupBounds();
        double scale = Math.Max(0.1d, GuiElement.scaled(1d));
        double swatch = GuiElement.scaled(24d);
        double gap = GuiElement.scaled(5d);
        double left = popup.absX + GuiElement.scaled(12d);
        double top = popup.absY + GuiElement.scaled(34d);
        for (int index = 0; index < Palette.Length; index++)
        {
            int column = index % 6;
            int row = index / 6;
            double x = left + column * (swatch + gap);
            double y = top + row * (swatch + gap);
            if (args.X < x || args.X > x + swatch || args.Y < y || args.Y > y + swatch)
            {
                continue;
            }

            SetValue(Palette[index]);
            onChanged(value);
            api.Gui.PlaySound("toggleswitch");
            args.Handled = true;
            _ = scale;
            return;
        }

        if (popup.PointInside(args.X, args.Y))
        {
            // Clicking the code copies it, keeping the popup useful without a
            // second movable text editor layered over the inspector.
            api.Forms.SetClipboardText(value);
            args.Handled = true;
        }
    }

    protected override void ComposePopupTexture()
    {
        int width = Math.Max(1, (int)Math.Round(GuiElement.scaled(280d)));
        int height = Math.Max(1, (int)Math.Round(GuiElement.scaled(164d)));
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(22d));
        context.ShowText(Lang.Get("vsdirector:replay-visual-color-picker"));
        double swatch = GuiElement.scaled(24d);
        double gap = GuiElement.scaled(5d);
        double left = GuiElement.scaled(12d);
        double top = GuiElement.scaled(34d);
        for (int index = 0; index < Palette.Length; index++)
        {
            int column = index % 6;
            int row = index / 6;
            DrawColor(context, Palette[index], left + column * (swatch + gap),
                top + row * (swatch + gap), swatch);
        }
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.9d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(151d));
        context.ShowText(value);
        generateTexture(surface, ref popupTexture, linearMag: true);
    }

    protected override void DisposeTextures()
    {
        fieldTexture.Dispose();
        popupTexture.Dispose();
    }

    private void ComposeFieldTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        DrawPanelForPopover(context, width, height);
        double swatch = Math.Min(height - GuiElement.scaled(8d), GuiElement.scaled(18d));
        DrawColor(context, value, GuiElement.scaled(8d), (height - swatch) / 2d, swatch);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(34d), (height + font.GetTextExtents(value).Height) / 2d);
        context.ShowText(value);
        generateTexture(surface, ref fieldTexture, linearMag: true);
    }

    internal static void DrawPanelForPopover(Context context, int width, int height)
    {
        DirectorGuiTheme.RoundedRectangle(context, 0.5d, 0.5d, width - 1d, height - 1d,
            DirectorGuiTheme.ScaledCornerRadius);
        context.SetSourceRGBA(DirectorGuiTheme.SurfaceR, DirectorGuiTheme.SurfaceG,
            DirectorGuiTheme.SurfaceB, 0.99d);
        context.FillPreserve();
        context.SetSourceRGBA(DirectorGuiTheme.BorderR, DirectorGuiTheme.BorderG,
            DirectorGuiTheme.BorderB, 0.95d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
    }

    private static void DrawColor(Context context, string color, double x, double y, double size)
    {
        (double red, double green, double blue) = ParseRgb(color);
        context.Rectangle(x, y, size, size);
        context.SetSourceRGB(red, green, blue);
        context.FillPreserve();
        context.SetSourceRGBA(1d, 1d, 1d, 0.45d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
    }

    private static string NormalizeColor(string? candidate)
    {
        string text = candidate?.Trim() ?? string.Empty;
        return text.Length is 7 or 9
            && text[0] == '#'
            && text[1..].All(Uri.IsHexDigit)
            ? text.ToUpperInvariant()
            : "#000000";
    }

    private static (double Red, double Green, double Blue) ParseRgb(string color)
    {
        int red = Convert.ToInt32(color.Substring(1, 2), 16);
        int green = Convert.ToInt32(color.Substring(3, 2), 16);
        int blue = Convert.ToInt32(color.Substring(5, 2), 16);
        return (red / 255d, green / 255d, blue / 255d);
    }
}

internal sealed class GuiElementDirectorLegacyTimePopover : GuiElementDirectorFixedPopover
{
    private readonly Action<int> onChanged;
    private LoadedTexture fieldTexture;
    private LoadedTexture popupTexture;
    private int value;

    public GuiElementDirectorLegacyTimePopover(
        ICoreClientAPI capi,
        ElementBounds bounds,
        int initialValue,
        Action<int> onChanged)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        value = Math.Clamp(initialValue, 0, 24000);
        fieldTexture = new LoadedTexture(capi);
        popupTexture = new LoadedTexture(capi);
    }

    public int Value => value;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(fieldTexture.TextureId, Bounds);
        if (popupOpen)
        {
            ElementBounds popup = PopupBounds();
            api.Render.Render2DTexturePremultipliedAlpha(
                popupTexture.TextureId,
                popup.renderX,
                popup.renderY,
                popup.OuterWidth,
                popup.OuterHeight,
                315f);
        }
    }

    public void SetValue(int nextValue)
    {
        int normalized = Math.Clamp(nextValue, 0, 24000);
        if (normalized == value)
        {
            return;
        }
        value = normalized;
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    protected override ElementBounds PopupBounds()
    {
        ElementBounds popup = ElementBounds.Fixed(
                Bounds.fixedX,
                Bounds.fixedY + Bounds.fixedHeight + 6d,
                280d,
                112d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        popup.CalcWorldBounds();
        return popup;
    }

    protected override void HandlePopupMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        ElementBounds popup = PopupBounds();
        double left = popup.absX + GuiElement.scaled(14d);
        double usable = popup.OuterWidth - GuiElement.scaled(28d);
        double trackY = popup.absY + GuiElement.scaled(67d);
        if (args.Y >= trackY - GuiElement.scaled(14d)
            && args.Y <= trackY + GuiElement.scaled(14d))
        {
            int next = (int)Math.Round(Math.Clamp((args.X - left) / Math.Max(1d, usable), 0d, 1d) * 24000d);
            SetValue(next);
            onChanged(value);
            args.Handled = true;
            return;
        }
        args.Handled = popup.PointInside(args.X, args.Y);
    }

    protected override void HandlePopupMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        double direction = args.deltaPrecise != 0f ? args.deltaPrecise : args.delta;
        if (direction == 0d)
        {
            return;
        }
        SetValue(value + Math.Sign(direction) * 100);
        onChanged(value);
        args.SetHandled(true);
    }

    protected override void ComposePopupTexture()
    {
        int width = Math.Max(1, (int)Math.Round(GuiElement.scaled(280d)));
        int height = Math.Max(1, (int)Math.Round(GuiElement.scaled(112d)));
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(22d));
        context.ShowText(FormatTime(value));
        context.MoveTo(GuiElement.scaled(118d), GuiElement.scaled(22d));
        context.ShowText(value.ToString(CultureInfo.InvariantCulture) + " tick");
        double left = GuiElement.scaled(14d);
        double top = GuiElement.scaled(66d);
        double usable = width - GuiElement.scaled(28d);
        context.SetSourceRGBA(55d / 255d, 76d / 255d, 82d / 255d, 1d);
        context.Rectangle(left, top - GuiElement.scaled(2d), usable, GuiElement.scaled(4d));
        context.Fill();
        context.SetSourceRGBA(46d / 255d, 174d / 255d, 179d / 255d, 1d);
        context.Rectangle(left, top - GuiElement.scaled(2d), usable * value / 24000d, GuiElement.scaled(4d));
        context.Fill();
        context.SetSourceRGBA(224d / 255d, 242d / 255d, 244d / 255d, 1d);
        context.Arc(left + usable * value / 24000d, top, GuiElement.scaled(7d), 0d, Math.PI * 2d);
        context.Fill();
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.75d);
        context.MoveTo(GuiElement.scaled(14d), GuiElement.scaled(96d));
        context.ShowText("0:00");
        context.MoveTo(width - GuiElement.scaled(44d), GuiElement.scaled(96d));
        context.ShowText("24:00");
        generateTexture(surface, ref popupTexture, linearMag: true);
    }

    protected override void DisposeTextures()
    {
        fieldTexture.Dispose();
        popupTexture.Dispose();
    }

    private void ComposeFieldTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG, DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(10d), (height + font.GetTextExtents(FormatTime(value)).Height) / 2d);
        context.ShowText(FormatTime(value));
        generateTexture(surface, ref fieldTexture, linearMag: true);
    }

    private static string FormatTime(int tick)
    {
        double totalMinutes = tick / 24000d * 24d * 60d;
        int hours = (int)Math.Floor(totalMinutes / 60d) % 24;
        int minutes = (int)Math.Round(totalMinutes - hours * 60d);
        if (minutes >= 60)
        {
            hours = (hours + 1) % 24;
            minutes = 0;
        }
        return $"{hours:00}:{minutes:00}";
    }
}


/// <summary>
/// Fixed HSB color editor used by replay visuals and settings. The square is
/// saturation/value, while the narrow strip below it selects hue. A HEX input
/// is kept in the same anchored popup so exact colors remain editable.
/// </summary>
internal sealed class GuiElementDirectorColorPopover : GuiElementDirectorFixedPopover
{
    private readonly Action<string> onChanged;
    private LoadedTexture fieldTexture;
    private LoadedTexture popupTexture;
    private string value;
    private double hue;
    private double saturation;
    private double brightness;
    private GuiElementDirectorTextInput? hexInput;
    private bool updatingInput;
    private bool hexInputFocused;
    private bool draggingPlane;
    private bool draggingHue;

    public GuiElementDirectorColorPopover(
        ICoreClientAPI capi,
        ElementBounds bounds,
        string initialValue,
        Action<string> onChanged)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        value = NormalizeColor(initialValue);
        (hue, saturation, brightness) = RgbToHsb(ParseRgb(value));
        fieldTexture = new LoadedTexture(capi);
        popupTexture = new LoadedTexture(capi);
    }

    public string Value => value;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        EnsureHexInput();
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(fieldTexture.TextureId, Bounds);
        if (!popupOpen)
        {
            return;
        }

        ElementBounds popup = PopupBounds();
        api.Render.Render2DTexturePremultipliedAlpha(
            popupTexture.TextureId,
            popup.renderX,
            popup.renderY,
            popup.OuterWidth,
            popup.OuterHeight,
            315f);
        hexInput?.RenderInteractiveElements(deltaTime);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && popupOpen)
        {
            draggingPlane = false;
            draggingHue = false;
            if (hexInputFocused && hexInput is not null)
            {
                hexInput.OnMouseUp(api, args);
            }
            args.Handled = true;
        }
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!popupOpen || !hexInputFocused || hexInput is null)
        {
            return;
        }

        hexInput.OnKeyDown(api, args);
        if (args.KeyCode is (int)GlKeys.Enter or (int)GlKeys.KeypadEnter)
        {
            CommitHexInput();
        }
    }

    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
    {
        if (popupOpen && hexInputFocused && hexInput is not null)
        {
            hexInput.OnKeyPress(api, args);
        }
    }

    public void SetValue(string nextValue)
    {
        string normalized = NormalizeColor(nextValue);
        if (normalized == value)
        {
            if (hexInput is not null && hexInput.GetText() != normalized)
            {
                SetHexInputValue(normalized);
            }
            return;
        }

        value = normalized;
        (hue, saturation, brightness) = RgbToHsb(ParseRgb(value));
        if (hexInput is not null && hexInput.GetText() != value)
        {
            SetHexInputValue(value);
        }
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    protected override ElementBounds PopupBounds()
    {
        ElementBounds popup = ElementBounds.Fixed(
                Bounds.fixedX,
                Bounds.fixedY + Bounds.fixedHeight + 6d,
                300d,
                258d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        popup.CalcWorldBounds();
        return popup;
    }

    protected override void HandlePopupMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        EnsureHexInput();
        ElementBounds popup = PopupBounds();
        if (hexInput is not null && hexInput.Bounds.PointInside(args.X, args.Y))
        {
            hexInputFocused = true;
            if (!hexInput.HasFocus)
            {
                hexInput.OnFocusGained();
            }
            hexInput.OnMouseDown(api, args);
            args.Handled = true;
            return;
        }

        (double planeX, double planeY, double planeWidth, double planeHeight) = PlaneMetrics(popup);
        if (args.X >= planeX && args.X <= planeX + planeWidth
            && args.Y >= planeY && args.Y <= planeY + planeHeight)
        {
            BlurHexInput();
            draggingPlane = true;
            SetPlaneFromMouse(args.X, args.Y, planeX, planeY, planeWidth, planeHeight);
            args.Handled = true;
            return;
        }

        (double hueX, double hueY, double hueWidth, double hueHeight) = HueMetrics(popup);
        if (args.X >= hueX && args.X <= hueX + hueWidth
            && args.Y >= hueY && args.Y <= hueY + hueHeight)
        {
            BlurHexInput();
            draggingHue = true;
            SetHueFromMouse(args.X, hueX, hueWidth);
            args.Handled = true;
            return;
        }

        BlurHexInput();
        args.Handled = popup.PointInside(args.X, args.Y);
    }

    protected override void HandlePopupMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        _ = api;
        ElementBounds popup = PopupBounds();
        (double planeX, double planeY, double planeWidth, double planeHeight) = PlaneMetrics(popup);
        (double hueX, double hueY, double hueWidth, double hueHeight) = HueMetrics(popup);
        if (draggingPlane)
        {
            SetPlaneFromMouse(args.X, args.Y, planeX, planeY, planeWidth, planeHeight);
            args.Handled = true;
        }
        else if (draggingHue)
        {
            SetHueFromMouse(args.X, hueX, hueWidth);
            args.Handled = true;
        }
    }

    protected override void OnPopupClosed()
    {
        draggingPlane = false;
        draggingHue = false;
        BlurHexInput();
    }

    protected override void ComposePopupTexture()
    {
        int width = Math.Max(1, (int)Math.Round(GuiElement.scaled(300d)));
        int height = Math.Max(1, (int)Math.Round(GuiElement.scaled(258d)));
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(21d));
        context.ShowText(Lang.Get("vsdirector:replay-visual-color-picker"));

        double planeLeft = GuiElement.scaled(12d);
        double planeTop = GuiElement.scaled(30d);
        double planeWidth = width - GuiElement.scaled(24d);
        double planeHeight = GuiElement.scaled(158d);
        DrawHsbPlane(context, planeLeft, planeTop, planeWidth, planeHeight, hue);
        double markerX = planeLeft + saturation * planeWidth;
        double markerY = planeTop + (1d - brightness) * planeHeight;
        context.SetSourceRGBA(0d, 0d, 0d, 0.85d);
        context.Arc(markerX, markerY, GuiElement.scaled(6d), 0d, Math.PI * 2d);
        context.Stroke();
        context.SetSourceRGBA(1d, 1d, 1d, 0.9d);
        context.Arc(markerX, markerY, GuiElement.scaled(4d), 0d, Math.PI * 2d);
        context.Stroke();

        double hueTop = planeTop + planeHeight + GuiElement.scaled(12d);
        double hueHeight = GuiElement.scaled(16d);
        DrawHueStrip(context, planeLeft, hueTop, planeWidth, hueHeight);
        double hueMarkerX = planeLeft + hue * planeWidth;
        context.SetSourceRGBA(1d, 1d, 1d, 0.9d);
        context.Rectangle(hueMarkerX - GuiElement.scaled(2d), hueTop - GuiElement.scaled(2d),
            GuiElement.scaled(4d), hueHeight + GuiElement.scaled(4d));
        context.Stroke();

        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.82d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(235d));
        context.ShowText("HEX");
        context.MoveTo(GuiElement.scaled(170d), GuiElement.scaled(235d));
        context.ShowText($"H {hue * 360d:0}  S {saturation * 100d:0}%  B {brightness * 100d:0}%");
        ComposeHexInput(context, surface);
        generateTexture(surface, ref popupTexture, linearMag: true);
    }

    protected override void DisposeTextures()
    {
        hexInput?.Dispose();
        fieldTexture.Dispose();
        popupTexture.Dispose();
    }

    private void EnsureHexInput()
    {
        if (hexInput is not null || Bounds.ParentBounds is null)
        {
            return;
        }

        ElementBounds popup = PopupBounds();
        ElementBounds inputBounds = ElementBounds.Fixed(
                popup.fixedX + 42d,
                popup.fixedY + 218d,
                112d,
                28d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        hexInput = new GuiElementDirectorTextInput(
            api,
            inputBounds,
            OnHexInputChanged,
            DirectorFont.TextInput());
        SetHexInputValue(value);
    }

    private void ComposeHexInput(Context context, ImageSurface surface)
    {
        if (hexInput is null)
        {
            return;
        }
        ElementBounds popup = PopupBounds();
        context.Save();
        context.Translate(-popup.renderX, -popup.renderY);
        hexInput.ComposeElements(context, surface);
        context.Restore();
    }

    private void BlurHexInput()
    {
        hexInputFocused = false;
        if (hexInput?.HasFocus == true)
        {
            hexInput.OnFocusLost();
        }
    }

    private void OnHexInputChanged(string text)
    {
        if (updatingInput)
        {
            return;
        }
        string normalized = NormalizeColor(text);
        if (!string.Equals(normalized, text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        ApplyColor(normalized, notify: true);
    }

    private void CommitHexInput()
    {
        if (hexInput is null)
        {
            return;
        }
        string normalized = NormalizeColor(hexInput.GetText());
        SetHexInputValue(normalized);
        ApplyColor(normalized, notify: true);
    }

    private void SetHexInputValue(string text)
    {
        if (hexInput is null)
        {
            return;
        }
        updatingInput = true;
        try
        {
            hexInput.SetValue(text);
        }
        finally
        {
            updatingInput = false;
        }
    }

    private void SetPlaneFromMouse(
        int mouseX,
        int mouseY,
        double left,
        double top,
        double width,
        double height)
    {
        saturation = Math.Clamp((mouseX - left) / Math.Max(1d, width), 0d, 1d);
        brightness = Math.Clamp(1d - (mouseY - top) / Math.Max(1d, height), 0d, 1d);
        ApplyHsb(notify: true);
    }

    private void SetHueFromMouse(int mouseX, double left, double width)
    {
        hue = Math.Clamp((mouseX - left) / Math.Max(1d, width), 0d, 1d);
        ApplyHsb(notify: true);
    }

    private void ApplyHsb(bool notify)
    {
        ApplyColor(RgbToHex(HsbToRgb(hue, saturation, brightness)), notify);
    }

    private void ApplyColor(string nextValue, bool notify)
    {
        string normalized = NormalizeColor(nextValue);
        value = normalized;
        (hue, saturation, brightness) = RgbToHsb(ParseRgb(value));
        if (hexInput is not null && hexInput.GetText() != value)
        {
            SetHexInputValue(value);
        }
        ComposeFieldTexture();
        ComposePopupTexture();
        if (notify)
        {
            onChanged(value);
        }
    }

    private void ComposeFieldTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        double swatch = Math.Min(height - GuiElement.scaled(8d), GuiElement.scaled(18d));
        DrawColor(context, value, GuiElement.scaled(8d), (height - swatch) / 2d, swatch);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(34d), (height + font.GetTextExtents(value).Height) / 2d);
        context.ShowText(value);
        generateTexture(surface, ref fieldTexture, linearMag: true);
    }

    private static (double X, double Y, double Width, double Height) PlaneMetrics(ElementBounds popup)
        => (popup.absX + GuiElement.scaled(12d),
            popup.absY + GuiElement.scaled(30d),
            popup.OuterWidth - GuiElement.scaled(24d),
            GuiElement.scaled(158d));

    private static (double X, double Y, double Width, double Height) HueMetrics(ElementBounds popup)
        => (popup.absX + GuiElement.scaled(12d),
            popup.absY + GuiElement.scaled(200d),
            popup.OuterWidth - GuiElement.scaled(24d),
            GuiElement.scaled(16d));

    private static void DrawHsbPlane(
        Context context,
        double left,
        double top,
        double width,
        double height,
        double hue)
    {
        int columns = Math.Max(24, (int)Math.Round(width / GuiElement.scaled(3d)));
        int rows = Math.Max(24, (int)Math.Round(height / GuiElement.scaled(3d)));
        double cellWidth = width / columns;
        double cellHeight = height / rows;
        for (int row = 0; row < rows; row++)
        {
            double brightness = 1d - (double)row / Math.Max(1, rows - 1);
            for (int column = 0; column < columns; column++)
            {
                double saturation = (double)column / Math.Max(1, columns - 1);
                (double red, double green, double blue) = HsbToRgb(hue, saturation, brightness);
                context.SetSourceRGB(red, green, blue);
                context.Rectangle(left + column * cellWidth, top + row * cellHeight,
                    cellWidth + 0.5d, cellHeight + 0.5d);
                context.Fill();
            }
        }
        context.SetSourceRGBA(1d, 1d, 1d, 0.35d);
        context.Rectangle(left, top, width, height);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
    }

    private static void DrawHueStrip(Context context, double left, double top, double width, double height)
    {
        int columns = Math.Max(36, (int)Math.Round(width / GuiElement.scaled(3d)));
        double cellWidth = width / columns;
        for (int column = 0; column < columns; column++)
        {
            (double red, double green, double blue) = HsbToRgb(
                (double)column / Math.Max(1, columns - 1), 1d, 1d);
            context.SetSourceRGB(red, green, blue);
            context.Rectangle(left + column * cellWidth, top, cellWidth + 0.5d, height);
            context.Fill();
        }
    }

    private static void DrawColor(Context context, string color, double x, double y, double size)
    {
        (double red, double green, double blue) = ParseRgb(color);
        context.Rectangle(x, y, size, size);
        context.SetSourceRGB(red, green, blue);
        context.FillPreserve();
        context.SetSourceRGBA(1d, 1d, 1d, 0.45d);
        context.LineWidth = Math.Max(1d, GuiElement.scaled(1d));
        context.Stroke();
    }

    private static string NormalizeColor(string? candidate)
    {
        string text = candidate?.Trim() ?? string.Empty;
        return text.Length == 7
            && text[0] == '#'
            && text[1..].All(Uri.IsHexDigit)
            ? text.ToUpperInvariant()
            : "#000000";
    }

    private static (double Red, double Green, double Blue) ParseRgb(string color)
        => (Convert.ToInt32(color.Substring(1, 2), 16) / 255d,
            Convert.ToInt32(color.Substring(3, 2), 16) / 255d,
            Convert.ToInt32(color.Substring(5, 2), 16) / 255d);

    private static string RgbToHex((double Red, double Green, double Blue) rgb)
        => $"#{Math.Clamp((int)Math.Round(rgb.Red * 255d), 0, 255):X2}"
            + $"{Math.Clamp((int)Math.Round(rgb.Green * 255d), 0, 255):X2}"
            + $"{Math.Clamp((int)Math.Round(rgb.Blue * 255d), 0, 255):X2}";

    private static (double Hue, double Saturation, double Brightness) RgbToHsb(
        (double Red, double Green, double Blue) rgb)
    {
        double max = Math.Max(rgb.Red, Math.Max(rgb.Green, rgb.Blue));
        double min = Math.Min(rgb.Red, Math.Min(rgb.Green, rgb.Blue));
        double delta = max - min;
        double hue = 0d;
        if (delta > 0d)
        {
            hue = max == rgb.Red
                ? (rgb.Green - rgb.Blue) / delta % 6d
                : max == rgb.Green
                    ? (rgb.Blue - rgb.Red) / delta + 2d
                    : (rgb.Red - rgb.Green) / delta + 4d;
            hue /= 6d;
            if (hue < 0d)
            {
                hue += 1d;
            }
        }
        return (hue, max <= 0d ? 0d : delta / max, max);
    }

    private static (double Red, double Green, double Blue) HsbToRgb(
        double hue,
        double saturation,
        double brightness)
    {
        hue = (hue % 1d + 1d) % 1d;
        double h = hue * 6d;
        int sector = (int)Math.Floor(h);
        double fraction = h - sector;
        double p = brightness * (1d - saturation);
        double q = brightness * (1d - saturation * fraction);
        double t = brightness * (1d - saturation * (1d - fraction));
        return sector switch
        {
            0 => (brightness, t, p),
            1 => (q, brightness, p),
            2 => (p, brightness, t),
            3 => (p, q, brightness),
            4 => (t, p, brightness),
            _ => (brightness, p, q)
        };
    }
}

/// <summary>Anchored HH:MM editor for time-of-day values.</summary>
internal sealed class GuiElementDirectorTimePopover : GuiElementDirectorFixedPopover
{
    private readonly Action<int> onChanged;
    private LoadedTexture fieldTexture;
    private LoadedTexture popupTexture;
    private int value;
    private GuiElementDirectorTextInput? timeInput;
    private bool updatingInput;
    private bool timeInputFocused;

    public GuiElementDirectorTimePopover(
        ICoreClientAPI capi,
        ElementBounds bounds,
        int initialValue,
        Action<int> onChanged)
        : base(capi, bounds)
    {
        this.onChanged = onChanged;
        value = Math.Clamp(initialValue, 0, 24000);
        fieldTexture = new LoadedTexture(capi);
        popupTexture = new LoadedTexture(capi);
    }

    public int Value => value;

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        _ = context;
        _ = surface;
        Bounds.CalcWorldBounds();
        EnsureTimeInput();
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        _ = deltaTime;
        api.Render.Render2DTexturePremultipliedAlpha(fieldTexture.TextureId, Bounds);
        if (!popupOpen)
        {
            return;
        }
        ElementBounds popup = PopupBounds();
        api.Render.Render2DTexturePremultipliedAlpha(
            popupTexture.TextureId,
            popup.renderX,
            popup.renderY,
            popup.OuterWidth,
            popup.OuterHeight,
            315f);
        timeInput?.RenderInteractiveElements(deltaTime);
    }

    public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
    {
        if (!popupOpen || !timeInputFocused || timeInput is null)
        {
            return;
        }
        timeInput.OnKeyDown(api, args);
        if (args.KeyCode is (int)GlKeys.Enter or (int)GlKeys.KeypadEnter)
        {
            CommitTimeInput();
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Left && popupOpen)
        {
            if (timeInputFocused && timeInput is not null)
            {
                timeInput.OnMouseUp(api, args);
            }
            args.Handled = true;
        }
    }

    public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
    {
        if (popupOpen && timeInputFocused && timeInput is not null)
        {
            timeInput.OnKeyPress(api, args);
        }
    }

    public void SetValue(int nextValue)
    {
        int normalized = Math.Clamp(nextValue, 0, 24000);
        value = normalized;
        if (timeInput is not null && timeInput.GetText() != FormatTime(value))
        {
            SetTimeInputValue(FormatTime(value));
        }
        ComposeFieldTexture();
        ComposePopupTexture();
    }

    protected override ElementBounds PopupBounds()
    {
        ElementBounds popup = ElementBounds.Fixed(
                Bounds.fixedX,
                Bounds.fixedY + Bounds.fixedHeight + 6d,
                280d,
                112d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        popup.CalcWorldBounds();
        return popup;
    }

    protected override void HandlePopupMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        EnsureTimeInput();
        ElementBounds popup = PopupBounds();
        if (timeInput is not null && timeInput.Bounds.PointInside(args.X, args.Y))
        {
            timeInputFocused = true;
            if (!timeInput.HasFocus)
            {
                timeInput.OnFocusGained();
            }
            timeInput.OnMouseDown(api, args);
            args.Handled = true;
            return;
        }
        timeInputFocused = false;
        if (timeInput?.HasFocus == true)
        {
            timeInput.OnFocusLost();
        }
        args.Handled = popup.PointInside(args.X, args.Y);
    }

    protected override void ComposePopupTexture()
    {
        int width = Math.Max(1, (int)Math.Round(GuiElement.scaled(280d)));
        int height = Math.Max(1, (int)Math.Round(GuiElement.scaled(112d)));
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.92d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(22d));
        context.ShowText(Lang.Get("vsdirector:replay-visual-time-picker"));
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.72d);
        context.MoveTo(GuiElement.scaled(12d), GuiElement.scaled(96d));
        context.ShowText("HH:MM");
        ComposeTimeInput(context, surface);
        generateTexture(surface, ref popupTexture, linearMag: true);
    }

    protected override void DisposeTextures()
    {
        timeInput?.Dispose();
        fieldTexture.Dispose();
        popupTexture.Dispose();
    }

    private void EnsureTimeInput()
    {
        if (timeInput is not null || Bounds.ParentBounds is null)
        {
            return;
        }
        ElementBounds popup = PopupBounds();
        ElementBounds inputBounds = ElementBounds.Fixed(
                popup.fixedX + 12d,
                popup.fixedY + 34d,
                112d,
                28d)
            .WithAlignment(EnumDialogArea.None)
            .WithParent(Bounds.ParentBounds);
        timeInput = new GuiElementDirectorTextInput(
            api,
            inputBounds,
            OnTimeInputChanged,
            DirectorFont.TextInput());
        SetTimeInputValue(FormatTime(value));
    }

    private void ComposeTimeInput(Context context, ImageSurface surface)
    {
        if (timeInput is null)
        {
            return;
        }
        ElementBounds popup = PopupBounds();
        context.Save();
        context.Translate(-popup.renderX, -popup.renderY);
        timeInput.ComposeElements(context, surface);
        context.Restore();
    }

    private void OnTimeInputChanged(string text)
    {
        if (updatingInput)
        {
            return;
        }
        if (TryParseTime(text, out int next))
        {
            value = next;
            ComposeFieldTexture();
            ComposePopupTexture();
            onChanged(value);
        }
    }

    private void CommitTimeInput()
    {
        if (timeInput is null)
        {
            return;
        }
        if (!TryParseTime(timeInput.GetText(), out int next))
        {
            SetTimeInputValue(FormatTime(value));
            return;
        }
        SetValue(next);
        onChanged(value);
    }

    private void SetTimeInputValue(string text)
    {
        if (timeInput is null)
        {
            return;
        }
        updatingInput = true;
        try
        {
            timeInput.SetValue(text);
        }
        finally
        {
            updatingInput = false;
        }
    }

    private void ComposeFieldTexture()
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        GuiElementDirectorLegacyColorPopover.DrawPanelForPopover(context, width, height);
        CairoFont font = DirectorFont.WhiteSmallText();
        font.SetupContext(context);
        context.SetSourceRGBA(DirectorGuiTheme.TextR, DirectorGuiTheme.TextG,
            DirectorGuiTheme.TextB, 0.92d);
        string text = FormatTime(value);
        context.MoveTo(GuiElement.scaled(10d),
            (height + font.GetTextExtents(text).Height) / 2d);
        context.ShowText(text);
        generateTexture(surface, ref fieldTexture, linearMag: true);
    }

    private static bool TryParseTime(string? text, out int tick)
    {
        tick = 0;
        string[] parts = (text ?? string.Empty).Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || hours is < 0 or > 23
            || minutes is < 0 or > 59)
        {
            return false;
        }
        tick = (int)Math.Round((hours * 60d + minutes) / (24d * 60d) * 24000d);
        return true;
    }

    private static string FormatTime(int tick)
    {
        double totalMinutes = Math.Clamp(tick, 0, 24000) / 24000d * 24d * 60d;
        int hours = (int)Math.Floor(totalMinutes / 60d) % 24;
        int minutes = (int)Math.Round(totalMinutes - hours * 60d);
        if (minutes >= 60)
        {
            hours = (hours + 1) % 24;
            minutes = 0;
        }
        return $"{hours:00}:{minutes:00}";
    }
}

internal static class DirectorGuiInputExtensions
{
    public static GuiComposer AddTextInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            GuiElementDirectorTextInput input = new(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? DirectorFont.TextInput(),
                    onFocusLost);
            DirectorGuiFocus.Register(input, key);
            composer.AddInteractiveElement(input, key);
        }
        return composer;
    }

    public static GuiComposer AddTextArea(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            GuiElementDirectorTextArea input = new(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? DirectorFont.TextInput(),
                    onFocusLost);
            DirectorGuiFocus.Register(input, key);
            composer.AddInteractiveElement(input, key);
        }
        return composer;
    }

    public static GuiComposer AddSwitch(
        this GuiComposer composer,
        Action<bool> onToggled,
        ElementBounds bounds,
        string? key = null,
        double size = 28d,
        double padding = 3d)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorSwitch(composer.Api, onToggled, bounds, size, padding),
                key);
        }
        return composer;
    }

    public static GuiComposer AddDirectorSlider(
        this GuiComposer composer,
        ActionConsumable<int> onChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorSlider(composer.Api, onChanged, bounds),
                key);
        }
        return composer;
    }

    public static GuiElementDirectorSlider? GetDirectorSlider(this GuiComposer composer, string key)
        => composer.GetElement(key) as GuiElementDirectorSlider;

    public static GuiComposer AddDirectorColorPopover(
        this GuiComposer composer,
        string value,
        Action<string> onChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorColorPopover(composer.Api, bounds, value, onChanged),
                key);
        }
        return composer;
    }

    public static GuiElementDirectorColorPopover? GetDirectorColorPopover(
        this GuiComposer composer,
        string key)
        => composer.GetElement(key) as GuiElementDirectorColorPopover;

    public static GuiComposer AddDirectorTimePopover(
        this GuiComposer composer,
        int value,
        Action<int> onChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddInteractiveElement(
                new GuiElementDirectorTimePopover(composer.Api, bounds, value, onChanged),
                key);
        }
        return composer;
    }

    public static GuiElementDirectorTimePopover? GetDirectorTimePopover(
        this GuiComposer composer,
        string key)
        => composer.GetElement(key) as GuiElementDirectorTimePopover;

    public static GuiComposer AddDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        string? key = null)
        => AddDropDown(
            composer,
            values,
            names,
            selectedIndex,
            onSelectionChanged,
            bounds,
            DirectorFont.WhiteSmallText(),
            key);

    public static GuiComposer AddDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        CairoFont font,
        string? key = null)
    {
        if (!composer.Composed)
        {
            selectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, values.Length - 1);
            composer.AddInteractiveElement(
                new GuiElementDirectorDropDown(
                    composer.Api,
                    values,
                    names,
                    selectedIndex,
                    onSelectionChanged,
                    bounds,
                    font,
                    multiSelect: false),
                key);
        }
        return composer;
    }

    public static GuiComposer AddMultiSelectDropDown(
        this GuiComposer composer,
        string[] values,
        string[] names,
        int selectedIndex,
        SelectionChangedDelegate onSelectionChanged,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            selectedIndex = values.Length == 0 ? -1 : Math.Clamp(selectedIndex, -1, values.Length - 1);
            composer.AddInteractiveElement(
                new GuiElementDirectorDropDown(
                    composer.Api,
                    values,
                    names,
                    selectedIndex,
                    onSelectionChanged,
                    bounds,
                    DirectorFont.WhiteSmallText(),
                    multiSelect: true),
                key);
        }
        return composer;
    }

    public static GuiComposer AddNumberInput(
        this GuiComposer composer,
        ElementBounds bounds,
        Action<string>? onTextChanged,
        CairoFont? font = null,
        string? key = null,
        Action? onFocusLost = null)
    {
        if (!composer.Composed)
        {
            GuiElementDirectorNumberInput input = new(
                    composer.Api,
                    bounds,
                    onTextChanged,
                    font ?? DirectorFont.TextInput(),
                    onFocusLost);
            DirectorGuiFocus.Register(input, key);
            composer.AddInteractiveElement(input, key);
        }
        return composer;
    }
}
