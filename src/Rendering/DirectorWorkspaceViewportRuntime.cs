using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

/// <summary>
/// Replaces the physical full-window world blit while the Director workspace
/// is open. The game still renders once at its normal resolution; only the
/// final image is fitted into the movable workspace viewport.
/// </summary>
internal static class DirectorWorkspaceViewportRuntime
{
    private readonly record struct PhysicalViewport(
        int X,
        int Top,
        int Bottom,
        int Width,
        int Height);

    private sealed record ViewportState(
        object Owner,
        ICoreClientAPI ClientApi,
        int X,
        int Y,
        int Width,
        int Height,
        bool MapGameInput);

    private static ViewportState? state;
    private static DirectorOpenGlFramebufferApi? framebufferApi;
    private static bool framebufferApiUnavailable;
    private static bool failureLogged;
    private static bool interfaceHudHidden;

    [ThreadStatic]
    private static bool gameHudViewportActive;

    internal static bool IsActive => state is not null;

    internal static bool IsGameHudViewportActive => gameHudViewportActive;

    // A camera can hide the game's HUD before or after the workspace opens.
    // Keep the GUI pass running without changing that saved HUD preference.
    internal static bool ShouldRenderInterface(ClientMain game)
    {
        bool offscreen = DirectorOffscreenRenderRuntime.IsRendering;
        interfaceHudHidden = !offscreen && !game.ShouldRender2DOverlays;
        // After export closes the workspace, the restored camera can still hide
        // overlays. Keep dialog rendering alive so VSImGui can advance frames
        // and reopened tools/settings/escape menus remain visible.
        return game.ShouldRender2DOverlays || (!offscreen
            && (ReferenceEquals(state?.ClientApi.World, game)
                || (game.Api as ICoreClientAPI)?.Gui?.OpenedGuis.Any(dialog =>
                    dialog.DialogType == EnumDialogType.Dialog
                    || dialog.GetType().FullName == "VSImGui.VSImGuiDialog") == true));
    }

    internal static bool HideGameHud
        => !DirectorOffscreenRenderRuntime.IsRendering
            && (interfaceHudHidden || state?.ClientApi.World is ClientMain { ShouldRender2DOverlays: false });

    internal static bool ShouldRenderDialog(GuiDialog dialog)
        => (!HideGameHud
            || dialog.DialogType == EnumDialogType.Dialog
            || dialog.GetType().FullName == "VSImGui.VSImGuiDialog"
            || dialog.GetType().Assembly == typeof(DirectorWorkspaceViewportRuntime).Assembly)
            && dialog.ShouldReceiveRenderEvents();

    internal static void Configure(
        object owner,
        ICoreClientAPI capi,
        int x,
        int y,
        int width,
        int height,
        bool mapGameInput)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(capi);
        state = new ViewportState(
            owner,
            capi,
            Math.Max(0, x),
            Math.Max(0, y),
            Math.Max(1, width),
            Math.Max(1, height),
            mapGameInput);
    }

    internal static void Deactivate(object owner)
    {
        if (ReferenceEquals(state?.Owner, owner))
        {
            state = null;
            gameHudViewportActive = false;
        }
    }

    internal static void UseFullScreenUi(ICoreClientAPI capi)
    {
        gameHudViewportActive = false;
        capi.Render.GlViewport(
            0,
            0,
            Math.Max(1, capi.Render.FrameWidth),
            Math.Max(1, capi.Render.FrameHeight));
    }

    internal static void RestoreFullScreenViewport()
    {
        if (DirectorOffscreenRenderRuntime.IsRendering)
        {
            return;
        }
        ViewportState? current = state;
        gameHudViewportActive = false;
        if (current is not null)
        {
            UseFullScreenUi(current.ClientApi);
        }
    }

    internal static bool UseGameHudViewport()
    {
        if (DirectorOffscreenRenderRuntime.IsRendering)
        {
            gameHudViewportActive = false;
            return false;
        }
        ViewportState? current = state;
        if (current is null
            || !TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport))
        {
            gameHudViewportActive = false;
            return false;
        }

        current.ClientApi.Render.GlViewport(
            viewport.X,
            viewport.Bottom,
            viewport.Width,
            viewport.Height);
        gameHudViewportActive = true;
        return true;
    }

    internal static bool TryTransformGameHudScissor(
        ref int x,
        ref int y,
        ref int width,
        ref int height)
    {
        ViewportState? current = state;
        if (!gameHudViewportActive
            || current is null
            || !TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport))
        {
            return false;
        }

        int frameWidth = Math.Max(1, current.ClientApi.Render.FrameWidth);
        int frameHeight = Math.Max(1, current.ClientApi.Render.FrameHeight);
        double scaleX = viewport.Width / (double)frameWidth;
        double scaleY = viewport.Height / (double)frameHeight;
        x = viewport.X + (int)Math.Round(x * scaleX);
        y = viewport.Bottom + (int)Math.Round(y * scaleY);
        width = Math.Max(0, (int)Math.Round(width * scaleX));
        height = Math.Max(0, (int)Math.Round(height * scaleY));
        return true;
    }

    internal static bool TryTransformGameInput(MouseEvent args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ViewportState? current = state;
        if (DirectorOffscreenRenderRuntime.IsRendering
            || current is null
            || current.ClientApi.Input.MouseGrabbed
            || HasUnscaledInputDialog(current)
            || !ShouldMapGameInput(current)
            || !TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport))
        {
            return false;
        }

        int frameWidth = Math.Max(1, current.ClientApi.Render.FrameWidth);
        int frameHeight = Math.Max(1, current.ClientApi.Render.FrameHeight);
        args.X = (int)Math.Round(
            (args.X - viewport.X) * frameWidth / (double)Math.Max(1, viewport.Width));
        args.Y = (int)Math.Round(
            (args.Y - viewport.Top) * frameHeight / (double)Math.Max(1, viewport.Height));
        return true;
    }

    internal static bool ContainsPhysicalGamePoint(double x, double y)
    {
        ViewportState? current = state;
        return current is not null
            && TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport)
            && x >= viewport.X
            && x < viewport.X + viewport.Width
            && y >= viewport.Top
            && y < viewport.Top + viewport.Height;
    }

    internal static bool TryMapPhysicalGamePoint(
        double physicalX,
        double physicalY,
        out double gameX,
        out double gameY)
    {
        ViewportState? current = state;
        if (current is null
            || !TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport)
            || physicalX < viewport.X
            || physicalX >= viewport.X + viewport.Width
            || physicalY < viewport.Top
            || physicalY >= viewport.Top + viewport.Height)
        {
            gameX = 0d;
            gameY = 0d;
            return false;
        }

        gameX = (physicalX - viewport.X)
            * Math.Max(1, current.ClientApi.Render.FrameWidth)
            / Math.Max(1, viewport.Width);
        gameY = (physicalY - viewport.Top)
            * Math.Max(1, current.ClientApi.Render.FrameHeight)
            / Math.Max(1, viewport.Height);
        return true;
    }

    internal static bool TryMapGamePointToPhysical(
        double gameX,
        double gameY,
        out double physicalX,
        out double physicalY)
    {
        ViewportState? current = state;
        if (current is null
            || !TryGetFittedViewport(
                current,
                current.ClientApi.Render.FrameWidth / (double)Math.Max(
                    1,
                    current.ClientApi.Render.FrameHeight),
                out PhysicalViewport viewport))
        {
            physicalX = 0d;
            physicalY = 0d;
            return false;
        }

        physicalX = viewport.X
            + gameX * viewport.Width / Math.Max(1, current.ClientApi.Render.FrameWidth);
        physicalY = viewport.Top
            + gameY * viewport.Height / Math.Max(1, current.ClientApi.Render.FrameHeight);
        return true;
    }

    internal static bool TryMapUntransformedPickingPoint(
        int physicalX,
        int physicalY,
        out int gameX,
        out int gameY)
    {
        ViewportState? current = state;
        if (DirectorOffscreenRenderRuntime.IsRendering
            || current is null
            || current.ClientApi.Input.MouseGrabbed
            || HasUnscaledInputDialog(current)
            || ShouldMapGameInput(current)
            || !TryMapPhysicalGamePoint(
                physicalX,
                physicalY,
                out double mappedX,
                out double mappedY))
        {
            gameX = physicalX;
            gameY = physicalY;
            return false;
        }

        gameX = (int)Math.Round(mappedX);
        gameY = (int)Math.Round(mappedY);
        return true;
    }

    internal static bool BeginDirectorDialogRender(GuiDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ViewportState? current = state;
        if (current is null
            || (dialog.GetType().Assembly != typeof(DirectorWorkspaceViewportRuntime).Assembly
                && dialog.GetType().FullName != "VSImGui.VSImGuiDialog"))
        {
            return false;
        }

        UseFullScreenUi(current.ClientApi);
        return !ReferenceEquals(dialog, current.Owner);
    }

    internal static void EndDirectorDialogRender(bool restoreGameViewport)
    {
        if (restoreGameViewport)
        {
            UseGameHudViewport();
        }
    }

    private static bool HasUnscaledInputDialog(ViewportState current)
        => current.ClientApi.Gui.OpenedGuis.Any(dialog =>
            !ReferenceEquals(dialog, current.Owner)
            && dialog.GetType().Assembly == typeof(DirectorWorkspaceViewportRuntime).Assembly
            && dialog.ShouldReceiveMouseEvents());

    private static bool ShouldMapGameInput(ViewportState current)
        => current.MapGameInput
            || current.ClientApi.Gui.OpenedGuis.Any(dialog =>
                !ReferenceEquals(dialog, current.Owner)
                && dialog.GetType().Assembly != typeof(DirectorWorkspaceViewportRuntime).Assembly
                && dialog.DialogType == EnumDialogType.Dialog
                && dialog.ShouldReceiveMouseEvents());

    internal static bool TryBlitPrimaryToViewport()
    {
        ViewportState? current = state;
        if (current is null || framebufferApiUnavailable)
        {
            return false;
        }

        try
        {
            IRenderAPI render = current.ClientApi.Render;
            if (render.FrameBuffers.Count <= (int)EnumFrameBuffer.Primary)
            {
                return false;
            }

            FrameBufferRef source = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            if (source.FboId == 0 || source.Width <= 0 || source.Height <= 0)
            {
                return false;
            }

            if (framebufferApi is null)
            {
                if (!DirectorOpenGlFramebufferApi.TryCreate(out framebufferApi, out string error))
                {
                    framebufferApiUnavailable = true;
                    current.ClientApi.Logger.Warning(
                        "VS Director: game viewport OpenGL bridge is unavailable: {0}",
                        error);
                    return false;
                }
            }

            if (!TryGetFittedViewport(
                    current,
                    source.Width / (double)source.Height,
                    out PhysicalViewport viewport))
            {
                return false;
            }

            DirectorOpenGlFramebufferApi activeFramebufferApi = framebufferApi
                ?? throw new InvalidOperationException("The viewport framebuffer bridge was not initialized.");
            activeFramebufferApi.BlitColorToDefault(
                source,
                viewport.X,
                viewport.Bottom,
                viewport.Width,
                viewport.Height);
            render.GlViewport(0, 0, Math.Max(1, render.FrameWidth), Math.Max(1, render.FrameHeight));
            failureLogged = false;
            return true;
        }
        catch (Exception exception)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                current.ClientApi.Logger.Warning(
                    "VS Director: game viewport blit failed; restoring the normal game view: {0}",
                    exception.GetBaseException().Message);
            }
            return false;
        }
    }

    private static bool TryGetFittedViewport(
        ViewportState current,
        double sourceAspect,
        out PhysicalViewport viewport)
    {
        IRenderAPI render = current.ClientApi.Render;
        int frameWidth = Math.Max(1, render.FrameWidth);
        int frameHeight = Math.Max(1, render.FrameHeight);
        int containerX = Math.Clamp(current.X, 0, frameWidth - 1);
        int containerY = Math.Clamp(current.Y, 0, frameHeight - 1);
        int containerWidth = Math.Clamp(current.Width, 1, frameWidth - containerX);
        int containerHeight = Math.Clamp(current.Height, 1, frameHeight - containerY);
        if (!double.IsFinite(sourceAspect) || sourceAspect <= 0d)
        {
            viewport = default;
            return false;
        }

        int fittedWidth = containerWidth;
        int fittedHeight = Math.Max(1, (int)Math.Round(fittedWidth / sourceAspect));
        if (fittedHeight > containerHeight)
        {
            fittedHeight = containerHeight;
            fittedWidth = Math.Max(1, (int)Math.Round(fittedHeight * sourceAspect));
        }

        int fittedX = containerX + (containerWidth - fittedWidth) / 2;
        int fittedTop = containerY + (containerHeight - fittedHeight) / 2;
        viewport = new PhysicalViewport(
            fittedX,
            fittedTop,
            frameHeight - fittedTop - fittedHeight,
            fittedWidth,
            fittedHeight);
        return true;
    }
}

[HarmonyPatch(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.BlitPrimaryToDefault))]
internal static class DirectorWorkspaceViewportBlitPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
        => !DirectorWorkspaceViewportRuntime.TryBlitPrimaryToViewport();
}

[HarmonyPatch]
internal static class DirectorWorkspaceGameHudViewportPatch
{
    private static System.Reflection.MethodBase TargetMethod()
        => AccessTools.Method(
            typeof(ClientMain),
            nameof(ClientMain.OrthoMode),
            new[] { typeof(int), typeof(int), typeof(bool) })
            ?? throw new MissingMethodException(typeof(ClientMain).FullName, nameof(ClientMain.OrthoMode));

    [HarmonyPostfix]
    private static void Postfix(bool inverseY)
    {
        if (!inverseY)
        {
            DirectorWorkspaceViewportRuntime.UseGameHudViewport();
        }
    }
}

[HarmonyPatch(typeof(SystemRenderAim), nameof(SystemRenderAim.OnRenderFrame2DOverlay))]
internal static class DirectorWorkspaceAimViewportPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref bool __state)
    {
        if (DirectorWorkspaceViewportRuntime.HideGameHud)
        {
            __state = false;
            return false;
        }
        __state = DirectorWorkspaceViewportRuntime.UseGameHudViewport();
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(bool __state)
    {
        if (__state && DirectorWorkspaceViewportRuntime.IsActive)
        {
            ViewportStateRestoreFull();
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__exception is not null
            && __state
            && DirectorWorkspaceViewportRuntime.IsActive)
        {
            ViewportStateRestoreFull();
        }
        return __exception;
    }

    private static void ViewportStateRestoreFull()
    {
        // The active state owns the client API; toggling to the full viewport
        // is also done by PerspectiveMode as a final safety net.
        DirectorWorkspaceViewportRuntime.RestoreFullScreenViewport();
    }
}

[HarmonyPatch(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.GlScissor))]
internal static class DirectorWorkspaceGameHudScissorPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref int x, ref int y, ref int width, ref int height)
        => DirectorWorkspaceViewportRuntime.TryTransformGameHudScissor(
            ref x,
            ref y,
            ref width,
            ref height);
}

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.PerspectiveMode))]
internal static class DirectorWorkspaceViewportRestorePatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => DirectorWorkspaceViewportRuntime.RestoreFullScreenViewport();
}

/// <summary>
/// Item icons are lazily rendered into a private atlas in the middle of the
/// game's outer Ortho pass. Disposing that atlas calls PerspectiveMode even
/// though the outer GUI pass is still active, so restore the game HUD viewport
/// after the atlas framebuffer has been released.
/// </summary>
[HarmonyPatch(typeof(InventoryItemRenderer), nameof(InventoryItemRenderer.DisposeFrameBuffer))]
internal static class DirectorWorkspaceInventoryAtlasViewportPatch
{
    [HarmonyPrefix]
    private static void Prefix(out bool __state)
        => __state = DirectorWorkspaceViewportRuntime.IsGameHudViewportActive;

    [HarmonyPostfix]
    private static void Postfix(bool __state)
    {
        if (__state)
        {
            DirectorWorkspaceViewportRuntime.UseGameHudViewport();
        }
    }
}

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.OnMouseMove))]
internal static class DirectorWorkspaceMouseMovePatch
{
    [HarmonyPrefix]
    private static void Prefix(MouseEvent args)
        => DirectorWorkspaceViewportRuntime.TryTransformGameInput(args);
}

[HarmonyPatch(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.CreateMouseEvent))]
internal static class DirectorWorkspaceMouseButtonPatch
{
    [HarmonyPostfix]
    private static void Postfix(MouseEvent __result)
        => DirectorWorkspaceViewportRuntime.TryTransformGameInput(__result);
}

[HarmonyPatch(
    typeof(PickingRayUtil),
    nameof(PickingRayUtil.GetPickingRayByMouseCoordinates))]
internal static class DirectorWorkspacePickingRayPatch
{
    private readonly record struct MousePositionState(int X, int Y, bool Mapped);

    [HarmonyPrefix]
    private static void Prefix(ClientMain game, out MousePositionState __state)
    {
        int originalX = game.MouseCurrentX;
        int originalY = game.MouseCurrentY;
        bool mapped = DirectorWorkspaceViewportRuntime.TryMapUntransformedPickingPoint(
            originalX,
            originalY,
            out int gameX,
            out int gameY);
        __state = new MousePositionState(originalX, originalY, mapped);
        if (mapped)
        {
            game.MouseCurrentX = gameX;
            game.MouseCurrentY = gameY;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(ClientMain game, MousePositionState __state)
        => Restore(game, __state);

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        ClientMain game,
        MousePositionState __state)
    {
        Restore(game, __state);
        return __exception;
    }

    private static void Restore(ClientMain game, MousePositionState state)
    {
        if (state.Mapped)
        {
            game.MouseCurrentX = state.X;
            game.MouseCurrentY = state.Y;
        }
    }
}

[HarmonyPatch]
internal static class DirectorWorkspaceDirectorDialogViewportPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GuiDialog), nameof(GuiDialog.OnRenderGUI));
        // VSImGui overrides the render method without calling the base method.
        yield return AccessTools.Method(AccessTools.TypeByName("VSImGui.VSImGuiDialog"), nameof(GuiDialog.OnRenderGUI));
    }

    [HarmonyPrefix]
    private static void Prefix(GuiDialog __instance, out bool __state)
        => __state = DirectorWorkspaceViewportRuntime.BeginDirectorDialogRender(__instance);

    [HarmonyPostfix]
    private static void Postfix(bool __state)
        => DirectorWorkspaceViewportRuntime.EndDirectorDialogRender(__state);

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        DirectorWorkspaceViewportRuntime.EndDirectorDialogRender(__state);
        return __exception;
    }
}
