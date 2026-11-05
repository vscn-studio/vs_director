using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal static class ReplayRemoteParticleViewPatch
{
    private static readonly FieldInfo? ClientSystemGameField = AccessTools.Field(
        typeof(ClientSystemEntities).BaseType,
        "game");

    internal static IReadOnlyList<MethodBase> Targets { get; private set; } = Array.Empty<MethodBase>();

    internal static bool TryPatch(Harmony harmony, ILogger logger)
    {
        try
        {
            List<MethodBase> targets = new();
            Type? weatherType = AccessTools.TypeByName("Vintagestory.GameContent.WeatherSimulationParticles");
            MethodInfo? weatherSpawn = weatherType is null
                ? null
                : AccessTools.Method(
                    weatherType,
                    "asyncParticleSpawn",
                    new[] { typeof(float), typeof(IAsyncParticleManager) });
            AddPatch(harmony, weatherSpawn, targets);
            AddPatch(harmony, AccessTools.Method(
                weatherType,
                "desertStormSim",
                new[] { typeof(float) }), targets);

            Type? weatherClientType = AccessTools.TypeByName("Vintagestory.GameContent.WeatherSystemClient");
            AddPatch(harmony, AccessTools.Method(
                weatherClientType,
                "OnRenderFrame",
                new[] { typeof(float), typeof(EnumRenderStage) }), targets);

            Type? weatherSoundType = AccessTools.TypeByName("Vintagestory.GameContent.WeatherSimulationSound");
            AddPatch(harmony, AccessTools.Method(
                weatherSoundType,
                "Update",
                new[] { typeof(float) }), targets);

            Type? weatherLightningType = AccessTools.TypeByName("Vintagestory.GameContent.WeatherSimulationLightning");
            AddPatch(harmony, AccessTools.Method(
                weatherLightningType,
                "ClientTick",
                new[] { typeof(float) }), targets);
            AddPatch(harmony, AccessTools.Method(
                weatherLightningType,
                "OnRenderFrame",
                new[] { typeof(float), typeof(EnumRenderStage) }), targets);

            Type? cloudType = AccessTools.TypeByName("Vintagestory.GameContent.CloudRenderer");
            AddPatch(harmony, AccessTools.Method(
                cloudType,
                "CloudTick",
                new[] { typeof(float) }), targets);
            AddPatch(harmony, AccessTools.Method(
                cloudType,
                "OnRenderFrame",
                new[] { typeof(float), typeof(EnumRenderStage) }), targets);

            Type? cloudMapType = AccessTools.TypeByName("FluffyClouds.CloudRendererMap");
            AddPatch(harmony, AccessTools.Method(
                cloudMapType,
                "CloudTick",
                new[] { typeof(float) }), targets);
            AddPatch(harmony, AccessTools.Method(
                cloudMapType,
                "OnRenderFrame",
                new[] { typeof(float), typeof(EnumRenderStage) }), targets);

            Type? ambientType = AccessTools.TypeByName("Vintagestory.GameContent.ModSystemAmbientParticles");
            MethodInfo? ambientSpawn = ambientType is null
                ? null
                : AccessTools.Method(
                    ambientType,
                    "AsyncParticleSpawnTick",
                    new[] { typeof(float), typeof(IAsyncParticleManager) });
            AddPatch(harmony, ambientSpawn, targets);
            AddPatch(harmony, AccessTools.Method(
                ambientType,
                "OnSlowTick",
                new[] { typeof(float) }), targets);

            Type? blockParticleType = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityParticleEmitter");
            AddPatch(harmony, AccessTools.Method(
                blockParticleType,
                "OnGameTick",
                new[] { typeof(float) }), targets);

            Type? entityParticleSpawnerType = AccessTools.TypeByName("Vintagestory.GameContent.EntityParticleSpawner");
            AddPatch(harmony, AccessTools.Method(
                entityParticleSpawnerType,
                "Sys_OnSimTick",
                new[] { typeof(float) }), targets);

            Type? entityRendererType = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderEntities");
            AddPatch(harmony, AccessTools.Method(
                entityRendererType,
                "OnBeforeRender",
                new[] { typeof(float) }), targets);
            AddPatch(harmony, AccessTools.Method(
                entityRendererType,
                "OnRenderFrameShadows",
                new[] { typeof(float) }), targets);

            Type? clientEntitiesType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSystemEntities");
            AddPostfixPatch(harmony, AccessTools.Method(
                clientEntitiesType,
                "UpdateEvery1000ms",
                new[] { typeof(float) }), targets);

            if (targets.Count == 0)
            {
                logger.Warning("VS Director: remote particle view patch targets were not available.");
                return false;
            }

            Targets = targets;
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning("VS Director: remote particle view patch failed: {0}", exception.Message);
            return false;
        }
    }

    private static void AddPatch(Harmony harmony, MethodInfo? target, ICollection<MethodBase> targets)
    {
        if (target is null || targets.Contains(target))
        {
            return;
        }

        MethodInfo? transpiler = AccessTools.Method(
            typeof(ReplayRemoteParticleViewPatch),
            nameof(Transpiler));
        if (transpiler is null)
        {
            return;
        }

        harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
        targets.Add(target);
    }

    private static void AddPostfixPatch(
        Harmony harmony,
        MethodInfo? target,
        ICollection<MethodBase> targets)
    {
        if (target is null || targets.Contains(target))
        {
            return;
        }

        MethodInfo? postfix = AccessTools.Method(
            typeof(ReplayRemoteParticleViewPatch),
            nameof(UpdateEntityDistancesPostfix));
        if (postfix is null)
        {
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        targets.Add(target);
    }

    private static void UpdateEntityDistancesPostfix(object __instance)
    {
        if (ClientSystemGameField?.GetValue(__instance) is not ClientMain game
            || game.EntityPlayer is null)
        {
            return;
        }

        EntityPos observation = GetParticleViewPosition(game.EntityPlayer);
        foreach (Entity entity in game.LoadedEntities.Values)
        {
            entity.NearestPlayerDistance = entity.Pos.Dimension == observation.Dimension
                ? (float)entity.Pos.DistanceTo(observation)
                : float.MaxValue;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? positionGetter = AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.Pos));
        MethodInfo? positionResolver = AccessTools.Method(
            typeof(ReplayRemoteParticleViewPatch),
            nameof(GetParticleViewPosition));
        if (positionGetter is null || positionResolver is null)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
            }
            yield break;
        }

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(positionGetter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = positionResolver;
            }

            yield return instruction;
        }
    }

    private static EntityPos GetParticleViewPosition(Entity entity)
    {
        DirectorCameraController? controller = DirectorCameraRuntime.Controller;
        if (entity is not EntityPlayer || controller?.IsLocalPlayerEntity(entity) != true)
        {
            return entity.Pos;
        }

        CameraPreset? pose = null;
        if (controller.IsActive)
        {
            pose = controller.CurrentPose;
        }
        else if (DirectorReplayRuntime.Manager?.TryGetPlaybackAnchorPose(out CameraPreset replayPose) == true)
        {
            pose = replayPose;
        }

        if (pose is null
            || !double.IsFinite(pose.X)
            || !double.IsFinite(pose.Y)
            || !double.IsFinite(pose.Z))
        {
            return entity.Pos;
        }

        EntityPos result = entity.Pos.Copy();
        result.X = pose.X;
        result.Y = pose.Y;
        result.Z = pose.Z;
        result.Dimension = pose.Dimension;
        return result;
    }
}
