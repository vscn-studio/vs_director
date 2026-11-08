using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace VSDirector.Replay;

// EntityShapeRenderer builds meshes on TyronThreadPool and uploads them on the
// client thread later.  The engine does not associate the completion callback
// with the shape revision which produced it, so a late mesh can replace a
// newer mesh (most visibly when replay clothing changes quickly).  Keep the
// latest-wins rule at the completion boundary instead of trying to serialize
// playback around the worker queue.
internal static class ReplayEntityTessellationRevisionPatch
{
    private static readonly ConditionalWeakTable<object, RendererRevision> RendererRevisions = new();
    private static readonly ConditionalWeakTable<Entity, ShapeRevision> ShapeRevisions = new();

    private static FieldInfo? rendererEntityField;
    private static FieldInfo? completionMeshField;
    private static FieldInfo? completionOuterField;
    private static FieldInfo? outerCallbackField;

    internal static MethodBase? TesselationMethod { get; private set; }

    internal static MethodBase? CompletionMethod { get; private set; }

    internal static MethodBase? MarkShapeModifiedMethod { get; private set; }

    internal static MethodBase? DisposeMethod { get; private set; }

    internal static MethodBase? StaleCompletionMethod { get; private set; }

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            Type? rendererType = AccessTools.TypeByName(
                "Vintagestory.GameContent.EntityShapeRenderer");
            if (rendererType is null)
            {
                logger.Warning("VS Director: replay tessellation renderer type was not available.");
                return false;
            }

            rendererEntityField = AccessTools.Field(rendererType, "entity");
            TesselationMethod = AccessTools.Method(
                rendererType,
                "TesselateShape",
                new[] { typeof(Action<MeshData>), typeof(string[]) });
            DisposeMethod = rendererType.GetMethod(
                nameof(IDisposable.Dispose),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            DisposeMethod ??= AccessTools.Method(
                rendererType,
                nameof(IDisposable.Dispose),
                Type.EmptyTypes);
            MethodInfo? tesselationPrefix = AccessTools.Method(
                typeof(ReplayEntityTessellationRevisionPatch),
                nameof(TesselationPrefix));
            MethodInfo? disposePrefix = AccessTools.Method(
                typeof(ReplayEntityTessellationRevisionPatch),
                nameof(DisposePrefix));
            if (rendererEntityField is null
                || TesselationMethod is null
                || DisposeMethod is null
                || tesselationPrefix is null
                || disposePrefix is null)
            {
                logger.Warning("VS Director: replay tessellation renderer target was not available.");
                return false;
            }

            harmony.Patch(
                TesselationMethod,
                prefix: new HarmonyMethod(tesselationPrefix) { priority = Priority.First });
            harmony.Patch(
                DisposeMethod,
                prefix: new HarmonyMethod(disposePrefix) { priority = Priority.First });

            StaleCompletionMethod = AccessTools.Method(
                typeof(EntityPlayer),
                nameof(EntityPlayer.OnTesselated),
                Type.EmptyTypes);
            MethodInfo? staleCompletionPrefix = AccessTools.Method(
                typeof(ReplayEntityTessellationRevisionPatch),
                nameof(StaleCompletionPrefix));
            if (StaleCompletionMethod is null || staleCompletionPrefix is null)
            {
                logger.Warning(
                    "VS Director: replay stale tessellation completion guard target was not available.");
                return false;
            }
            harmony.Patch(
                StaleCompletionMethod,
                prefix: new HarmonyMethod(staleCompletionPrefix) { priority = Priority.First });

            CompletionMethod = FindCompletionMethod(rendererType);
            if (CompletionMethod is not null)
            {
                MethodInfo? completionPrefix = AccessTools.Method(
                    typeof(ReplayEntityTessellationRevisionPatch),
                    nameof(CompletionPrefix));
                if (completionPrefix is not null)
                {
                    harmony.Patch(
                        CompletionMethod,
                        prefix: new HarmonyMethod(completionPrefix) { priority = Priority.First });
                }
            }
            else
            {
                // The callback wrapper remains useful on a future engine build
                // whose compiler-generated completion closure changed shape.
                logger.Warning(
                    "VS Director: replay tessellation completion closure was not found; "
                    + "mesh revision filtering will use the callback fallback.");
            }

            MarkShapeModifiedMethod = AccessTools.Method(
                typeof(Entity),
                nameof(Entity.MarkShapeModified),
                Type.EmptyTypes);
            if (MarkShapeModifiedMethod is not null)
            {
                MethodInfo? markPostfix = AccessTools.Method(
                    typeof(ReplayEntityTessellationRevisionPatch),
                    nameof(MarkShapeModifiedPostfix));
                if (markPostfix is not null)
                {
                    harmony.Patch(
                        MarkShapeModifiedMethod,
                        postfix: new HarmonyMethod(markPostfix) { priority = Priority.Last });
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "VS Director: replay tessellation revision patch failed: {0}",
                exception.GetBaseException().Message);
            return false;
        }
    }

    private static void TesselationPrefix(
        object __instance,
        ref Action<MeshData>? __0)
    {
        RendererRevision rendererRevision = RendererRevisions.GetValue(
            __instance,
            static _ => new RendererRevision());
        bool rendererAlive = rendererRevision.TryBegin(out long rendererToken);

        // Every tessellation on this renderer invalidates its previous result,
        // including calls made while replay ownership is transitioning. Only
        // replay calls need the callback wrapper, but a live call must still
        // prevent an older replay mesh from becoming current after a restart.
        if (!rendererAlive
            || __0 is null
            || rendererEntityField?.GetValue(__instance) is not Entity entity
            || DirectorReplayRuntime.Manager?.IsReplayThirdPerson(entity.EntityId) != true)
        {
            return;
        }

        ShapeRevision shapeRevision = ShapeRevisions.GetValue(
            entity,
            static _ => new ShapeRevision());
        long shapeToken = Volatile.Read(ref shapeRevision.Value);
        Action<MeshData> original = __0;
        RevisionedMeshCallback callback = new(
            __instance,
            entity,
            rendererRevision,
            shapeRevision,
            rendererToken,
            shapeToken,
            original);
        __0 = callback.Invoke;
    }

    private static void DisposePrefix(object __instance)
    {
        if (RendererRevisions.TryGetValue(__instance, out RendererRevision? revision))
        {
            revision.Dispose();
        }
    }

    private static void MarkShapeModifiedPostfix(Entity __instance)
    {
        if (ShapeRevisions.TryGetValue(__instance, out ShapeRevision? revision))
        {
            Interlocked.Increment(ref revision.Value);
        }
    }

    private static bool CompletionPrefix(object __instance)
    {
        if (completionMeshField?.GetValue(__instance) is not MeshData meshData
            || completionOuterField?.GetValue(__instance) is not object outer
            || outerCallbackField?.GetValue(outer) is not Delegate callback
            || callback.Target is not RevisionedMeshCallback revisioned)
        {
            return true;
        }

        revisioned.Complete(meshData);
        return false;
    }

    private static MethodInfo? FindCompletionMethod(Type rendererType)
    {
        foreach (Type nested in GetNestedTypes(rendererType))
        {
            FieldInfo? meshField = nested.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(field => field.FieldType == typeof(MeshData));
            if (meshField is null)
            {
                continue;
            }

            MethodInfo? method = nested.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate =>
                    candidate.ReturnType == typeof(void)
                    && candidate.GetParameters().Length == 0
                    && candidate.Name.Contains("TesselateShape", StringComparison.Ordinal));
            if (method is null)
            {
                continue;
            }

            FieldInfo? outerField = nested.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(field => HasMeshCallbackField(field.FieldType));
            if (outerField is null)
            {
                continue;
            }

            FieldInfo? callbackField = GetMeshCallbackField(outerField.FieldType);
            if (callbackField is null)
            {
                continue;
            }

            completionMeshField = meshField;
            completionOuterField = outerField;
            outerCallbackField = callbackField;
            return method;
        }

        return null;
    }

    private static IEnumerable<Type> GetNestedTypes(Type type)
    {
        foreach (Type nested in type.GetNestedTypes(
                     BindingFlags.Instance | BindingFlags.Static
                     | BindingFlags.Public | BindingFlags.NonPublic))
        {
            yield return nested;
            foreach (Type descendant in GetNestedTypes(nested))
            {
                yield return descendant;
            }
        }
    }

    private static bool HasMeshCallbackField(Type type)
        => GetMeshCallbackField(type) is not null;

    private static FieldInfo? GetMeshCallbackField(Type type)
        => type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType == typeof(Action<MeshData>));

    private sealed class RendererRevision
    {
        private long generation;
        private int disposed;

        internal bool TryBegin(out long token)
        {
            token = Interlocked.Increment(ref generation);
            return Volatile.Read(ref disposed) == 0;
        }

        // Kept as a small reflection/test seam for older probes. The
        // lifecycle guard lives in TryBegin; this wrapper must not revive a
        // renderer after Dispose either.
        internal long Begin()
        {
            TryBegin(out long token);
            return token;
        }

        internal bool IsCurrent(long token)
            => Volatile.Read(ref disposed) == 0
                && Volatile.Read(ref generation) == token;

        internal void Dispose()
        {
            Volatile.Write(ref disposed, 1);
            Interlocked.Increment(ref generation);
        }
    }

    private sealed class ShapeRevision
    {
        internal long Value;
    }

    private sealed class RevisionedMeshCallback
    {
        private readonly object renderer;
        private readonly Entity entity;
        private readonly RendererRevision rendererRevision;
        private readonly ShapeRevision shapeRevision;
        private readonly long rendererToken;
        private readonly long shapeToken;
        private readonly Action<MeshData> original;

        internal RevisionedMeshCallback(
            object renderer,
            Entity entity,
            RendererRevision rendererRevision,
            ShapeRevision shapeRevision,
            long rendererToken,
            long shapeToken,
            Action<MeshData> original)
        {
            this.renderer = renderer;
            this.entity = entity;
            this.rendererRevision = rendererRevision;
            this.shapeRevision = shapeRevision;
            this.rendererToken = rendererToken;
            this.shapeToken = shapeToken;
            this.original = original;
        }

        internal void Invoke(MeshData meshData)
        {
            if (!IsCurrent())
            {
                meshData.Dispose();
                MarkStaleCompletion(entity);
                return;
            }

            original(meshData);
        }

        internal void Complete(MeshData meshData)
        {
            if (!IsCurrent())
            {
                meshData.Dispose();
                return;
            }

            original(meshData);
            entity.OnTesselated();
        }

        private bool IsCurrent()
            => rendererRevision.IsCurrent(rendererToken)
                && Volatile.Read(ref shapeRevision.Value) == shapeToken
                && DirectorReplayRuntime.Manager?.IsReplayThirdPerson(entity.EntityId) == true
                && rendererEntityField?.GetValue(renderer) is Entity current
                && ReferenceEquals(current, entity);
    }

    [ThreadStatic]
    private static EntityPlayer? staleCompletionEntity;

    private static void MarkStaleCompletion(Entity entity)
    {
        if (entity is EntityPlayer player)
        {
            staleCompletionEntity = player;
        }
    }

    private static bool StaleCompletionPrefix(EntityPlayer __instance)
    {
        if (!ReferenceEquals(staleCompletionEntity, __instance))
        {
            return true;
        }

        staleCompletionEntity = null;
        return false;
    }
}
