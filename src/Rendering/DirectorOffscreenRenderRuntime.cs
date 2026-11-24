using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using System.Linq.Expressions;
using Vintagestory.API.MathTools;
using VSDirector.Compatibility;

namespace VSDirector.Rendering;

internal static class DirectorOffscreenRenderRuntime
{
    [ThreadStatic]
    private static DirectorOffscreenRenderScope? currentScope;

    internal static DirectorOffscreenMultiViewRenderer? Renderer { get; set; }

    internal static bool IsRendering => currentScope is not null;

    internal static DirectorOffscreenViewRequest? CurrentView => currentScope?.View;

    // These passes do not dispatch the main window's AfterFinalComposition stage.
    internal static void RestoreGradeOnViewExit(Action restore)
    {
        if (currentScope is not null) currentScope.RestoreGrade = restore;
    }

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
        internal Action? RestoreGrade { get; set; }
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
            try { RestoreGrade?.Invoke(); }
            finally { currentScope = null; }
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

// The native visibility buffers belong to the main camera's background chunk
// culler. A second camera can face chunks hidden in those buffers. Evaluate its
// own frustum/range without changing the buffers or FrustumVisible, which are
// still needed by the editor and the next normal frame.
[HarmonyPatch(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull))]
internal static class DirectorOffscreenTerrainVisibilityPatch
{
    private static readonly System.Func<ModelDataPoolLocation, bool> Hidden = DirectorGameCompatibility.Getter<ModelDataPoolLocation, bool>("Hide");
    private static readonly System.Func<ModelDataPoolLocation, int> Lod = DirectorGameCompatibility.Getter<ModelDataPoolLocation, int>("LodLevel");
    private static readonly System.Func<FrustumCulling, Sphere, int, bool> InRange = CreateRangeTest();

    [HarmonyPrefix]
    private static bool Prefix(MeshDataPool __instance, FrustumCulling? __0, EnumFrustumCullMode __1,
        List<ModelDataPoolLocation> ___poolLocations)
    {
        if (DirectorOffscreenRenderRuntime.CurrentView is not { } view) return true;
        // The native pool writes camera-specific visibility flags in both
        // versions. Build only the draw groups for this camera, leaving all
        // native occlusion/FrustumVisible flags untouched.
        __instance.indicesGroupsCount = __instance.RenderedTriangles = __instance.AllocatedTris = 0;
        foreach (var location in ___poolLocations)
        {
            int count = location.IndicesEnd - location.IndicesStart;
            __instance.AllocatedTris += count / 3;
            if (!view.RenderTerrain || !IsVisible(location, __0, __1)) continue;
            int group = __instance.indicesGroupsCount++;
            __instance.indicesStartsByte[group * 2] = location.IndicesStart * sizeof(int);
            __instance.indicesSizes[group] = count;
            __instance.RenderedTriangles += count / 3;
        }
        return false;
    }

    internal static bool IsVisible(ModelDataPoolLocation location, FrustumCulling? culler, EnumFrustumCullMode mode)
    {
        if (Hidden(location)) return false;
        if (culler is null) return true;
        return mode switch
        {
            EnumFrustumCullMode.CullNormal => InRange(culler, location.FrustumCullSphere, Lod(location)),
            EnumFrustumCullMode.CullInstant => culler.InFrustum(location.FrustumCullSphere),
            EnumFrustumCullMode.CullInstantShadowPassNear => culler.InFrustumShadowPass(location.FrustumCullSphere),
            EnumFrustumCullMode.CullInstantShadowPassFar => Lod(location) >= 1
                && culler.InFrustumShadowPass(location.FrustumCullSphere),
            _ => true
        };
    }

    private static System.Func<FrustumCulling, Sphere, int, bool> CreateRangeTest()
    {
        var culler = Expression.Parameter(typeof(FrustumCulling));
        var sphere = Expression.Parameter(typeof(Sphere));
        var lod = Expression.Parameter(typeof(int));
        var method = typeof(FrustumCulling).GetMethod("InFrustumAndRange", [typeof(Sphere), typeof(int)])
            ?? typeof(FrustumCulling).GetMethod("InFrustumAndRange", [typeof(Sphere), typeof(bool), typeof(int)])
            ?? throw new MissingMethodException(typeof(FrustumCulling).FullName, "InFrustumAndRange");
        Expression[] arguments = method.GetParameters().Length == 2 ? [sphere, lod] : [sphere, Expression.Constant(false), lod];
        return Expression.Lambda<System.Func<FrustumCulling, Sphere, int, bool>>(Expression.Call(culler, method, arguments), culler, sphere, lod).Compile();
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
