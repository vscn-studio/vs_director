using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSDirector.Gui;

/// <summary>
/// Owns the workspace's large floating editor so it has an independent render
/// and input layer above the docked workspace panes.
/// </summary>
internal sealed class DirectorWorkspaceFloatingEditorDialog : GuiDialog
{
    private const string ComposerKey = "vsdirector-workspace-floating-editor";
    private readonly DirectorWorkspaceHud owner;

    public DirectorWorkspaceFloatingEditorDialog(
        ICoreClientAPI capi,
        DirectorWorkspaceHud owner)
        : base(capi)
    {
        this.owner = owner;
    }

    public override string? ToggleKeyCombinationCode => null;

    public override EnumDialogType DialogType => EnumDialogType.HUD;

    // Stay above Director tool windows while leaving the escape menu and true
    // modal prompts unobscured.
    public override double DrawOrder => 0.885d;

    // Keep the vanilla input bucket so DrawOrder also determines hit priority.
    public override double InputOrder => 0.5d;

    public override bool UnregisterOnClose => true;

    public override bool PrefersUngrabbedMouse => owner.PrefersUngrabbedMouse;

    public override bool DisableMouseGrab => owner.DisableMouseGrab;

    internal GuiComposer? Composer => Composers[ComposerKey];

    internal bool HasComposer => Composer is not null;

    internal bool PointInside(int x, int y)
        => Composer?.Bounds.PointInside(x, y) == true;

    internal void SetComposer(GuiComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        if (!HasComposer) DirectorToolWindows.Claim(this, owner.CloseFloatingTool);
        GuiComposer? previous = Composer;
        DirectorGuiFocusSnapshot? focus = DirectorGuiFocus.Capture(previous);
        Composers[ComposerKey] = composer;
        if (!ReferenceEquals(previous, composer))
        {
            previous?.Dispose();
        }
        DirectorGuiFocus.Restore(composer, focus);
    }

    internal void OpenForWorkspace()
    {
        if (!IsOpened())
        {
            TryOpen(withFocus: false);
        }
    }

    internal void RemoveComposer()
    {
        DirectorToolWindows.Release(this);
        Composer?.Dispose();
        Composers.Remove(ComposerKey);
    }

    public override bool ShouldReceiveRenderEvents()
        => HasComposer && base.ShouldReceiveRenderEvents();

    public override bool ShouldReceiveKeyboardEvents()
        => HasComposer && base.ShouldReceiveKeyboardEvents();

    public override bool ShouldReceiveMouseEvents()
        => HasComposer && owner.ShouldRouteFloatingEditorInput;

    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Handled)
        {
            return;
        }

        Composer?.OnMouseDown(args);
        if (!args.Handled)
        {
            owner.RouteFloatingEditorMouseDown(args);
        }

        if (!args.Handled && PointInside(args.X, args.Y))
        {
            args.Handled = true;
        }
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (args.Handled)
        {
            return;
        }

        if (owner.HasActiveFloatingPointerInteraction())
        {
            owner.RouteFloatingEditorMouseMove(args);
            return;
        }

        Composer?.OnMouseMove(args);
        if (!args.Handled)
        {
            owner.RouteFloatingEditorMouseMove(args);
        }

        if (!args.Handled && PointInside(args.X, args.Y))
        {
            args.Handled = true;
        }
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (args.Handled)
        {
            return;
        }

        if (owner.HasActiveFloatingPointerInteraction())
        {
            owner.RouteFloatingEditorMouseUp(args);
            return;
        }

        Composer?.OnMouseUp(args);
        if (!args.Handled)
        {
            owner.RouteFloatingEditorMouseUp(args);
        }

        if (!args.Handled && PointInside(args.X, args.Y))
        {
            args.Handled = true;
        }
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (args.IsHandled)
        {
            return;
        }

        Composer?.OnMouseWheel(args);
        if (!args.IsHandled)
        {
            owner.RouteFloatingEditorMouseWheel(args);
        }

        if (!args.IsHandled && PointInside(capi.Input.MouseX, capi.Input.MouseY))
        {
            args.SetHandled();
        }
    }

    public override void Dispose()
    {
        if (IsOpened())
        {
            TryClose();
        }

        RemoveComposer();
        base.Dispose();
    }
}
