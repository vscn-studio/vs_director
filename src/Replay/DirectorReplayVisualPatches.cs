using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using VSDirector.Data;
using Vintagestory.API.Common.Entities;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal static class DirectorReplayWorldRenderPatch
{
    private static readonly MethodInfo SkipSolidPrefix = AccessTools.Method(
        typeof(DirectorReplayWorldRenderPatch),
        nameof(PrefixSolid))!;
    private static readonly MethodInfo SkipLiquidPrefix = AccessTools.Method(
        typeof(DirectorReplayWorldRenderPatch),
        nameof(PrefixLiquid))!;

    internal static IReadOnlyList<MethodBase> TryPatch(Harmony harmony, ILogger logger)
    {
        List<MethodBase> patched = new();
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderTerrain",
            "OnRenderOpaque",
            SkipSolidPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderTerrain",
            "OnRenderOIT",
            SkipLiquidPrefix);
        AddPatch(
            harmony,
            logger,
            patched,
            "Vintagestory.Client.NoObf.SystemRenderTerrain",
            "OnRenderAfterOIT",
            SkipLiquidPrefix);
        return patched;
    }

    private static void AddPatch(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        string typeName,
        string methodName,
        MethodInfo prefix)
    {
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? target = type is null
            ? null
            : AccessTools.Method(type, methodName, new[] { typeof(float) })
                ?? AccessTools.Method(type, methodName);
        if (target is null)
        {
            logger.Debug("VS Director: replay visual hook {0}.{1} was not found.", typeName, methodName);
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        patched.Add(target);
    }

    private static bool PrefixSolid()
        => DirectorReplayVisualRuntime.Current?.World is not { } world
            || world.ShowTerrain;

    private static bool PrefixLiquid()
        => DirectorReplayVisualRuntime.Current is not { } visuals
            || visuals.World.ShowTerrain;
}

internal static class DirectorReplayVisualEffectsPatch
{
    private static readonly MethodInfo RenderNameTagsPrefix = AccessTools.Method(
        typeof(DirectorReplayVisualEffectsPatch),
        nameof(PrefixRenderNameTags))!;

    internal static IReadOnlyList<MethodBase> TryPatch(Harmony harmony, ILogger logger)
    {
        List<MethodBase> patched = new();
        Add(harmony, logger, patched, "Vintagestory.Client.NoObf.SystemRenderEntities", "OnRenderFrame2D", RenderNameTagsPrefix);
        Add(harmony, logger, patched, "Vintagestory.GameContent.EntityBehaviorNameTag", "OnRenderFrame", AccessTools.Method(typeof(DirectorReplayVisualEffectsPatch), nameof(PrefixEntityNameTag)));
        return patched;
    }

    private static void Add(
        Harmony harmony,
        ILogger logger,
        ICollection<MethodBase> patched,
        string typeName,
        string methodName,
        MethodInfo? prefix = null,
        MethodInfo? postfix = null)
    {
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? target = type is null ? null : AccessTools.Method(type, methodName);
        if (target is null)
        {
            logger.Debug("VS Director: replay effect hook {0}.{1} was not found.", typeName, methodName);
            return;
        }
        harmony.Patch(
            target,
            prefix is null ? null : new HarmonyMethod(prefix) { priority = Priority.First },
            postfix is null ? null : new HarmonyMethod(postfix) { priority = Priority.Last });
        patched.Add(target);
    }

    private static bool PrefixRenderNameTags()
        => DirectorReplayVisualRuntime.Current?.World.ShowNameTags != false;
    private static bool PrefixEntityNameTag(EntityBehavior __instance) => ShouldRenderNameTag(__instance.entity);
    internal static bool ShouldRenderNameTag(Entity entity)
        => PrefixRenderNameTags() && DirectorCreationRuntime.Current?.ShouldHideEntity(entity) != true
            && DirectorReplayRuntime.Manager?.IsLiveEntitySuppressed(entity) != true
            && DirectorReplayRuntime.Manager?.IsReplacedByCreation(entity.EntityId) != true
            && DirectorReplayRuntime.Manager?.ShouldRenderReplayEntity(entity.EntityId) != false;
}
