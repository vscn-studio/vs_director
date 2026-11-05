using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

internal static class DirectorOffscreenRenderRuntime
{
    [ThreadStatic]
    private static DirectorOffscreenRenderScope? currentScope;

    internal static DirectorOffscreenMultiViewRenderer? Renderer { get; set; }

    internal static bool IsRendering => currentScope is not null;

    internal static DirectorOffscreenViewRequest? CurrentView => currentScope?.View;

    /// <summary>
    /// Monotonic frame id assigned by the offscreen multi-view renderer.  A
    /// dedicated replay renderer can use this to keep per-view caches scoped
    /// to one physical render frame instead of leaking visibility from an
    /// earlier capture.
    /// </summary>
    internal static long? CurrentPhysicalFrameNumber => currentScope?.PhysicalFrameNumber;

    internal static IDisposable Enter(DirectorOffscreenViewRequest view, long physicalFrameNumber)
    {
        if (currentScope is not null)
        {
            throw new InvalidOperationException("Nested VS Director offscreen rendering is not supported.");
        }

        currentScope = new DirectorOffscreenRenderScope(view, physicalFrameNumber);
        return currentScope;
    }

    private sealed class DirectorOffscreenRenderScope : IDisposable
    {
        private bool disposed;
        internal DirectorOffscreenViewRequest View { get; }

        internal long PhysicalFrameNumber { get; }

        internal DirectorOffscreenRenderScope(
            DirectorOffscreenViewRequest view,
            long physicalFrameNumber)
        {
            View = view;
            PhysicalFrameNumber = physicalFrameNumber;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            currentScope = null;
        }
    }
}

// EntityPlayer selects its first-person animation manager from the primary
// render API camera type. Offscreen camera matrices do not change that global
// selector, so force every offscreen player renderer onto the third-person
// manager while its view scope is active.
[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.AnimManager), MethodType.Getter)]
internal static class DirectorOffscreenPlayerAnimationManagerPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityPlayer __instance, ref IAnimationManager __result)
    {
        DirectorOffscreenViewRequest? view = DirectorOffscreenRenderRuntime.CurrentView;
        if (view is null || !view.RenderEntities)
        {
            return;
        }

        bool isLocalPlayer = ReferenceEquals(
            __instance,
            (__instance.Api as ICoreClientAPI)?.World.Player.Entity);
        if (!isLocalPlayer || view.RenderLocalPlayer)
        {
            __result = __instance.TpAnimManager;
        }
    }
}

/// <summary>
/// RenderToDefaultFramebuffer is the first client boundary after the primary
/// world, post-processing, GUI, and Done stages have completed. Re-entering at
/// this point cannot trigger ClientMain.MainRenderLoop or a second game tick.
/// </summary>
[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.RenderToDefaultFramebuffer))]
internal static class DirectorOffscreenRenderBoundaryPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(float dt)
    {
        IDirectorWorkflowMultiViewRenderDriver? workflow = DirectorWorkflowMultiViewRenderRuntime.Driver;
        workflow?.BeforeOffscreenRender();
        DirectorLiveOffscreenRuntime.Session?.BeforeOffscreenRender();
        try
        {
            DirectorOffscreenRenderRuntime.Renderer?.RenderAtFrameBoundary(dt);
        }
        finally
        {
            workflow?.AfterOffscreenRender();
        }
    }
}
