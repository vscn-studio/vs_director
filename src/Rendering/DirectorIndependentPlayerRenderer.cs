using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

internal static class DirectorIndependentPlayerRendererRegistry
{
    private static readonly object Sync = new();
    private static readonly HashSet<object> Renderers = new(ReferenceEqualityComparer.Instance);

    internal static bool Contains(object renderer)
    {
        lock (Sync)
        {
            return Renderers.Contains(renderer);
        }
    }

    internal static void Add(object renderer)
    {
        lock (Sync)
        {
            Renderers.Add(renderer);
        }
    }

    internal static void Remove(object renderer)
    {
        lock (Sync)
        {
            Renderers.Remove(renderer);
        }
    }
}

internal sealed class DirectorIndependentPlayerRenderer : IDisposable
{
    private const long RetryIntervalFrames = 30;

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private EntityRenderer? sourceRenderer;
    private EntityRenderer? independentRenderer;
    private Entity? playerEntity;
    private FieldInfo? sourceThirdPersonMeshField;
    private FieldInfo? independentThirdPersonMeshField;
    private MethodInfo? tesselateShapeMethod;
    private MethodInfo? onMeshReadyMethod;
    private object? observedSourceMesh;
    private long lastTesselationFrame = long.MinValue;
    private int tesselationRevision;
    private bool meshRefreshPending;
    private bool disposed;

    internal DirectorIndependentPlayerRenderer(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal void Prepare(long frameNumber)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Entity? localPlayer = capi.World?.Player?.Entity;
        if (localPlayer is null
            || !DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                localPlayer.EntityId,
                out EntityRenderer? current)
            || current is null)
        {
            return;
        }

        if (DirectorIndependentPlayerRendererRegistry.Contains(current))
        {
            return;
        }
        if (!ReferenceEquals(sourceRenderer, current)
            || !ReferenceEquals(playerEntity, localPlayer)
            || independentRenderer is null)
        {
            CreateRenderer(current, localPlayer, frameNumber);
        }

        object? currentSourceMesh = sourceThirdPersonMeshField?.GetValue(sourceRenderer);
        bool sourceMeshChanged = currentSourceMesh is not null
            && !ReferenceEquals(currentSourceMesh, observedSourceMesh);
        if (sourceMeshChanged)
        {
            observedSourceMesh = currentSourceMesh;
            meshRefreshPending = true;
        }

        bool independentMeshUnavailable = independentThirdPersonMeshField
            ?.GetValue(independentRenderer) is not MultiTextureMeshRef mesh
            || mesh.Disposed
            || !mesh.Initialized;
        meshRefreshPending |= independentMeshUnavailable;
        if (meshRefreshPending
            && frameNumber - lastTesselationFrame >= RetryIntervalFrames)
        {
            RequestTesselation(frameNumber);
        }
    }

    internal IDisposable Enter()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourceRenderer is null || independentRenderer is null || playerEntity is null)
        {
            throw new InvalidOperationException(
                "The independent player renderer is not ready.");
        }
        if (!DirectorOffscreenEngineBindings.TryReplaceEntityRenderer(
                game,
                playerEntity.EntityId,
                sourceRenderer,
                independentRenderer))
        {
            throw new InvalidOperationException(
                "The live player renderer changed before the offscreen pass could be isolated.");
        }

        return new RestoreScope(
            this,
            playerEntity.EntityId,
            independentRenderer,
            sourceRenderer);
    }

    private void CreateRenderer(EntityRenderer source, Entity entity, long frameNumber)
    {
        if (!DisposeRenderer())
        {
            throw new InvalidOperationException(
                "The previous independent player renderer is still installed in the live renderer table.");
        }
        Type sourceType = source.GetType();
        Type? vanillaType = AccessTools.TypeByName("Vintagestory.GameContent.EntityPlayerShapeRenderer");
        Exception? failure = null;
        foreach (Type candidate in new[] { sourceType, vanillaType }
            .Where(type => type is not null)
            .Cast<Type>()
            .Distinct())
        {
            object? created = null;
            try
            {
                created = Activator.CreateInstance(
                    candidate,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { entity, capi },
                    culture: null);
                if (created is not EntityRenderer renderer)
                {
                    throw new InvalidCastException(
                        $"{candidate.FullName} is not an EntityRenderer.");
                }

                sourceRenderer = source;
                independentRenderer = renderer;
                playerEntity = entity;
                sourceThirdPersonMeshField = FindMeshField(sourceType);
                independentThirdPersonMeshField = FindMeshField(candidate);
                tesselateShapeMethod = AccessTools.Method(
                    candidate,
                    "TesselateShape",
                    new[] { typeof(Action<MeshData>), typeof(string[]) })
                    ?? throw new MissingMethodException(
                        candidate.FullName,
                        "TesselateShape(Action<MeshData>, string[])");
                onMeshReadyMethod = AccessTools.Method(
                    candidate,
                    "onMeshReady",
                    new[] { typeof(MeshData) })
                    ?? throw new MissingMethodException(candidate.FullName, "onMeshReady(MeshData)");
                observedSourceMesh = sourceThirdPersonMeshField?.GetValue(source);
                DirectorIndependentPlayerRendererRegistry.Add(renderer);
                renderer.OnEntityLoaded();
                RequestTesselation(frameNumber);
                capi.Logger.Notification(
                    "VS Director: independent offscreen player renderer initialized ({0}).",
                    candidate.FullName);
                return;
            }
            catch (Exception exception)
            {
                failure = exception.GetBaseException();
                if (created is EntityRenderer renderer)
                {
                    DirectorIndependentPlayerRendererRegistry.Remove(renderer);
                    try
                    {
                        renderer.Dispose();
                    }
                    catch
                    {
                    }
                }
                ClearRendererReferences();
            }
        }

        throw new InvalidOperationException(
            "Unable to create an independent player renderer.",
            failure);
    }

    private void RequestTesselation(long frameNumber)
    {
        if (independentRenderer is not EntityRenderer renderer
            || tesselateShapeMethod is not MethodInfo tesselate
            || onMeshReadyMethod is not MethodInfo onMeshReady)
        {
            return;
        }

        lastTesselationFrame = frameNumber;
        int revision = ++tesselationRevision;
        meshRefreshPending = true;
        try
        {
            Action<MeshData> completion = meshData => CompleteTesselation(
                renderer,
                onMeshReady,
                revision,
                meshData);
            tesselate.Invoke(renderer, new object?[] { completion, null });
        }
        catch (Exception exception)
        {
            meshRefreshPending = true;
            capi.Logger.Warning(
                "VS Director: independent player mesh generation failed: {0}",
                exception.GetBaseException().Message);
        }
    }

    private void CompleteTesselation(
        EntityRenderer renderer,
        MethodInfo onMeshReady,
        int revision,
        MeshData meshData)
    {
        if (disposed
            || revision != tesselationRevision
            || !ReferenceEquals(independentRenderer, renderer))
        {
            meshData.Dispose();
            return;
        }

        try
        {
            onMeshReady.Invoke(renderer, new object[] { meshData });
            meshRefreshPending = false;
        }
        catch (Exception exception)
        {
            meshRefreshPending = true;
            capi.Logger.Warning(
                "VS Director: independent player mesh upload failed: {0}",
                exception.GetBaseException().Message);
        }
    }

    private static FieldInfo? FindMeshField(Type rendererType)
        => AccessTools.Field(rendererType, "thirdPersonMeshRef")
            ?? AccessTools.Field(rendererType, "meshRefOpaque");

    private bool DisposeRenderer()
    {
        EntityRenderer? renderer = independentRenderer;
        if (renderer is null)
        {
            ClearRendererReferences();
            return true;
        }
        if (sourceRenderer is not EntityRenderer source
            || playerEntity is not Entity entity
            || !TryRestoreSourceRenderer(entity.EntityId, renderer, source))
        {
            capi.Logger.Error(
                "VS Director: refusing to dispose an independent player renderer that may still be installed in the live renderer table.");
            return false;
        }

        ClearRendererReferences();
        DirectorIndependentPlayerRendererRegistry.Remove(renderer);
        try
        {
            ClearDuplicateMeshReferences(renderer);
            renderer.Dispose();
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: independent player renderer cleanup failed: {0}",
                exception.GetBaseException().Message);
        }
        return true;
    }

    private bool TryRestoreSourceRenderer(
        long entityId,
        EntityRenderer independent,
        EntityRenderer source)
    {
        if (!DirectorOffscreenEngineBindings.TryGetEntityRenderer(
                game,
                entityId,
                out EntityRenderer? current))
        {
            return !DirectorOffscreenEngineBindings
                .GetEntityRenderers(game)
                .Any(renderer => ReferenceEquals(renderer, independent));
        }
        if (ReferenceEquals(current, source))
        {
            return true;
        }
        if (!ReferenceEquals(current, independent))
        {
            return true;
        }

        return DirectorOffscreenEngineBindings.TryReplaceEntityRenderer(
            game,
            entityId,
            independent,
            source);
    }

    private static void ClearDuplicateMeshReferences(EntityRenderer renderer)
    {
        Type type = renderer.GetType();
        FieldInfo? opaqueField = AccessTools.Field(type, "meshRefOpaque");
        FieldInfo? firstPersonField = AccessTools.Field(type, "firstPersonMeshRef");
        FieldInfo? thirdPersonField = AccessTools.Field(type, "thirdPersonMeshRef");
        object? opaque = opaqueField?.GetValue(renderer);
        object? firstPerson = firstPersonField?.GetValue(renderer);
        object? thirdPerson = thirdPersonField?.GetValue(renderer);
        if (opaque is not null && ReferenceEquals(firstPerson, opaque))
        {
            firstPersonField?.SetValue(renderer, null);
            firstPerson = null;
        }
        if (opaque is not null && ReferenceEquals(thirdPerson, opaque))
        {
            thirdPersonField?.SetValue(renderer, null);
            thirdPerson = null;
        }
        if (firstPerson is not null && ReferenceEquals(thirdPerson, firstPerson))
        {
            thirdPersonField?.SetValue(renderer, null);
        }
    }

    private void ClearRendererReferences()
    {
        sourceRenderer = null;
        independentRenderer = null;
        playerEntity = null;
        sourceThirdPersonMeshField = null;
        independentThirdPersonMeshField = null;
        tesselateShapeMethod = null;
        onMeshReadyMethod = null;
        observedSourceMesh = null;
        lastTesselationFrame = long.MinValue;
        tesselationRevision++;
        meshRefreshPending = false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = DisposeRenderer();
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly DirectorIndependentPlayerRenderer owner;
        private readonly long entityId;
        private readonly EntityRenderer independent;
        private readonly EntityRenderer source;
        private bool disposed;

        internal RestoreScope(
            DirectorIndependentPlayerRenderer owner,
            long entityId,
            EntityRenderer independent,
            EntityRenderer source)
        {
            this.owner = owner;
            this.entityId = entityId;
            this.independent = independent;
            this.source = source;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!owner.TryRestoreSourceRenderer(entityId, independent, source))
            {
                owner.capi.Logger.Error(
                    "VS Director: the independent player renderer could not be removed from the live renderer table after an offscreen pass.");
            }
        }
    }

}
