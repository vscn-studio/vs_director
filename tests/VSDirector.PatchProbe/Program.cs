using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;
using VSDirector;
using VSDirector.Data;
using VSDirector.Networking;
using ProtoBuf;

const string harmonyId = "vscn.vsdirector.patch-probe";
DirectorPerformanceChecks.Run();
DirectorCreationRecordingChecks.Run();
Harmony harmony = new(harmonyId);

Type indexedWorldType = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayWorldPlayback", true)!;
object indexedWorld = RuntimeHelpers.GetUninitializedObject(indexedWorldType);
FieldInfo indexedField = AccessTools.Field(indexedWorldType, "blockEventsByPosition");
var indexedPositions = (IDictionary)Activator.CreateInstance(indexedField.FieldType)!;
Type indexedKeyType = indexedField.FieldType.GetGenericArguments()[0];
object indexedKey = Activator.CreateInstance(indexedKeyType, 1, 2, 3, 0)!;
indexedPositions.Add(indexedKey, new DirectorReplayBlockEvent[] {
    new() { TimeSeconds = 1, X = 1, Y = 2, Z = 3, SolidBlockId = 5, BlockEntityCaptured = true, BlockEntityClass = "test", BlockEntityData = [7] },
    new() { TimeSeconds = 2, X = 1, Y = 2, Z = 3, SolidBlockId = 6 },
    new() { TimeSeconds = 4, X = 1, Y = 2, Z = 3, SolidBlockId = 0, BlockEntityCaptured = true }
});
indexedField.SetValue(indexedWorld, indexedPositions);
var indexedAt = AccessTools.Method(indexedWorldType, "IndexedStateAt");
var atThree = ((IEnumerable<DirectorReplayBlockEvent>)indexedAt.Invoke(indexedWorld, [3d])!).Single();
var atFive = ((IEnumerable<DirectorReplayBlockEvent>)indexedAt.Invoke(indexedWorld, [5d])!).Single();
if (atThree.SolidBlockId != 6 || !atThree.BlockEntityData.SequenceEqual(new byte[] { 7 })
    || atFive.SolidBlockId != 0 || atFive.BlockEntityClass.Length != 0)
    throw new InvalidOperationException("Indexed world seek did not preserve last block/entity state.");
Console.WriteLine("PASS indexed world seek keeps only the final state and preserves block-entity tombstones");

Type soundManagerProbe = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
Type soundPlaybackProbe = soundManagerProbe.GetNestedType("Playback", BindingFlags.NonPublic)!;
object soundPlayback = RuntimeHelpers.GetUninitializedObject(soundPlaybackProbe);
var poisonSoundHistory = (DirectorReplayList<DirectorReplaySoundEvent>)typeof(DirectorReplayList<DirectorReplaySoundEvent>)
    .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
        10000, new System.Func<int, DirectorReplaySoundEvent>(_ => throw new Exception("Per-frame sound update scanned replay history")), null]);
var cachedSound = new DirectorReplaySoundEvent { InstanceId = 1, Location = "game:sounds/probe", RelativePosition = true, Pitch = 1, Volume = 1, LowPassFilter = 1 };
var soundParameters = new SoundParams { Pitch = 1, Volume = 1, LowPassFilter = 1 };
var cachedSoundHandle = ProbeDispatchProxy.Create<ILoadedSound>((method, _) => method.Name == "get_Params" ? soundParameters : ProbeDispatchProxy.DefaultValue(method.ReturnType));
AccessTools.Field(soundPlaybackProbe, "<Document>k__BackingField").SetValue(soundPlayback, new DirectorReplayDocument { SoundEvents = poisonSoundHistory });
AccessTools.Field(soundPlaybackProbe, "<ReplaySounds>k__BackingField").SetValue(soundPlayback, new Dictionary<long, ILoadedSound> { [1] = cachedSoundHandle });
AccessTools.Field(soundPlaybackProbe, "<ReplaySoundStates>k__BackingField").SetValue(soundPlayback, new Dictionary<long, DirectorReplaySoundEvent> { [1] = cachedSound });
AccessTools.Field(soundPlaybackProbe, "<ActorAnchoredReplaySounds>k__BackingField").SetValue(soundPlayback, new HashSet<long>());
AccessTools.Method(soundManagerProbe, "ApplyReplaySoundVisuals").Invoke(null, [soundPlayback]);
Console.WriteLine("PASS per-frame replay sound updates never enumerate historical sound events");

Type voiceNativeProbe = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Recording.DirectorOpenAlNative", true)!;
Type voiceOutputProbe = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Replay.DirectorReplayVoiceOutput", true)!;
using (var voiceAl = (IDisposable)Activator.CreateInstance(voiceNativeProbe, true)!)
{
    AccessTools.Method(voiceNativeProbe, "InitializeLoopback").Invoke(voiceAl, [48000]);
    byte[] voiceTone = new byte[1920];
    for (int i = 0; i < 960; i++) { voiceTone[i * 2] = 0xe0; voiceTone[i * 2 + 1] = 0x2e; }
    var voicePackets = Enumerable.Range(0, 100).Select(i => new DirectorReplayVoiceFrame { SourceId = "probe", TimeSeconds = i * .02, Spatialized = false, Pcm16 = voiceTone }).ToArray();
    using var queuedVoice = new DirectorReplayVoiceStream(voicePackets,
        () => (IDirectorReplayVoiceOutput)Activator.CreateInstance(voiceOutputProbe, [voiceAl])!);
    short[] renderedVoice = new short[9600];
    for (int frame = 0; frame < 18; frame++)
    {
        queuedVoice.Update(frame * .1, 1, false, _ => 1);
        AccessTools.Method(voiceNativeProbe, "RenderSamples").Invoke(voiceAl, [renderedVoice, 4800]);
        if (renderedVoice.Count(s => Math.Abs((int)s) > 100) < 9000)
            throw new InvalidOperationException("OpenAL voice queue had an audible gap at 10fps.");
    }
    queuedVoice.SetPaused(true);
    AccessTools.Method(voiceNativeProbe, "RenderSamples").Invoke(voiceAl, [renderedVoice, 4800]);
    AccessTools.Method(voiceNativeProbe, "RenderSamples").Invoke(voiceAl, [renderedVoice, 4800]);
    if (renderedVoice.Any(s => Math.Abs((int)s) > 100)) throw new InvalidOperationException("Paused queued voice kept playing.");
}
Console.WriteLine("PASS real OpenAL streaming voice stays continuous at 10fps and pauses cleanly");

_ = AccessTools.Field(typeof(PlayerHeadController), "entityPlayer")
    ?? throw new InvalidOperationException(
        "PlayerHeadController.entityPlayer was not found; replay ghosts cannot override first-person self detection.");

MethodBase[] targets =
{
    AccessTools.Method(typeof(EntityPlayer), "updateEyeHeight"),
    AccessTools.Method(typeof(EntityPlayer), "canStandUp"),
    AccessTools.Method(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.IsVisible)),
    AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.AnimManager)),
    AccessTools.Method(typeof(ClientSystemEntities), "OnGameTick", new[] { typeof(float) }),
    AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame), new[] { typeof(float) }),
    AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.OtherAnimManager)),
    AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.RightHandItemSlot)),
    AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.LeftHandItemSlot)),
    AccessTools.PropertyGetter(typeof(EntityPlayer), "IsSelf"),
    AccessTools.Method(typeof(PlayerHeadController), "IsSelf", Type.EmptyTypes),
    AccessTools.Method(
        typeof(EntityPlayer),
        nameof(EntityPlayer.OnTesselation),
        new[] { typeof(Shape).MakeByRefType(), typeof(string) }),
    AccessTools.Method(
        typeof(PlayerInventoryManager),
        nameof(PlayerInventoryManager.GetOwnInventory),
        new[] { typeof(string) }),
    AccessTools.PropertyGetter(typeof(EntityPlayerShapeRenderer), "IsSelf"),
    AccessTools.Method(
        typeof(EntityPlayerShapeRenderer),
        nameof(EntityPlayerShapeRenderer.DoRender3DOpaque),
        new[] { typeof(float), typeof(bool) }),
    AccessTools.Method(
        typeof(EntityBehaviorInterpolatePosition),
        nameof(EntityBehaviorInterpolatePosition.OnRenderFrame),
        new[] { typeof(float), typeof(EnumRenderStage) }),
    AccessTools.Method(
        typeof(EntityPlayerShapeRenderer),
        nameof(EntityPlayerShapeRenderer.DoRender3DOpaqueBatched),
        new[] { typeof(float), typeof(bool) }),
    AccessTools.Method(
        typeof(EntityPlayerShapeRenderer),
        nameof(EntityPlayerShapeRenderer.BeforeRender),
        new[] { typeof(float) }),
    AccessTools.Method(
        typeof(EntityShapeRenderer),
        nameof(EntityShapeRenderer.BeforeRender),
        new[] { typeof(float) }),
    AccessTools.Method(
        typeof(EntityShapeRenderer),
        nameof(EntityShapeRenderer.DoRender3DOpaque),
        new[] { typeof(float), typeof(bool) }),
    AccessTools.Method(
        typeof(EntityShapeRenderer),
        nameof(EntityShapeRenderer.DoRender3DOpaqueBatched),
        new[] { typeof(float), typeof(bool) }),
    AccessTools.Method(
        typeof(EntityItemRenderer),
        nameof(EntityItemRenderer.DoRender3DOpaque),
        new[] { typeof(float), typeof(bool) }),
    AccessTools.Method(
        typeof(EntityBehaviorHideWaterSurface),
        nameof(EntityBehaviorHideWaterSurface.OnRenderFrame),
        new[] { typeof(float), typeof(EnumRenderStage) }),
    AccessTools.Method(typeof(EntityItem), nameof(EntityItem.OnGameTick), new[] { typeof(float) }),
    AccessTools.Method(typeof(ParticleManager), "copyLoadedEntitiesList2sec", new[] { typeof(float) }),
    AccessTools.Method(typeof(SystemRenderParticles), nameof(SystemRenderParticles.OnRenderFrame3D), new[] { typeof(float) }),
    AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame), new[] { typeof(float), typeof(Vec3d) }),
    AccessTools.Method(typeof(SystemRenderParticles), nameof(SystemRenderParticles.OnRenderFrame3DOIT), new[] { typeof(float) }),
    AccessTools.Method(
        typeof(SystemRenderParticles),
        nameof(SystemRenderParticles.Spawn),
        new[] { typeof(IParticlePropertiesProvider) }),
    AccessTools.Method(typeof(EntityBlockFalling), nameof(EntityBlockFalling.Initialize)),
    AccessTools.Method(typeof(EntityBlockFalling), nameof(EntityBlockFalling.OnGameTick), new[] { typeof(float) }),
    AccessTools.Method(
        typeof(ModSystemRenderFallingBlocksFast),
        "IsRendered",
        new[] { typeof(EntityBlockFalling) }),
    AccessTools.Method(
        typeof(FallingBlockParticlesModSystem),
        nameof(FallingBlockParticlesModSystem.Register),
        new[] { typeof(EntityBlockFalling) }),
    AccessTools.Method(typeof(PlayerCamera), nameof(PlayerCamera.OnBeforeRenderFrame3D), new[] { typeof(float) }),
    AccessTools.Method(
        typeof(SystemSoundEngine),
        nameof(SystemSoundEngine.OnRenderFrame),
        new[] { typeof(float), typeof(EnumRenderStage) }),
    AccessTools.Method(
        typeof(Camera),
        nameof(Camera.GetCameraMatrix),
        new[] { typeof(Vec3d), typeof(Vec3d), typeof(double), typeof(double), typeof(AABBIntersectionTest) }),
    AccessTools.Method(
        typeof(FrustumCulling),
        nameof(FrustumCulling.CalcFrustumEquations),
        new[] { typeof(BlockPos), typeof(double[]), typeof(double[]) }),
    AccessTools.Method(typeof(SystemPlayerControl), nameof(SystemPlayerControl.OnGameTick), new[] { typeof(float) }),
    AccessTools.Method(
        typeof(SystemMouseInWorldInteractions),
        "ContinueBreakSurvival",
        new[] { typeof(BlockSelection), typeof(Block), typeof(float) }),
    AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", new[] { typeof(float) }),
    AccessTools.Method(typeof(ClientMain), nameof(ClientMain.RenderToDefaultFramebuffer), new[] { typeof(float) }),
    AccessTools.Method(typeof(GuiManager), nameof(GuiManager.OnRenderFrameGUI), new[] { typeof(float) }),
    AccessTools.Method(typeof(SystemRenderPlayerAimAcc), "OnRenderFrame2DOverlay", new[] { typeof(float) }),
    AccessTools.Method(typeof(ClientMain), nameof(ClientMain.PauseGame), new[] { typeof(bool) }),
    AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu"), "EscapeMenuHome"),
    AccessTools.Method(
        typeof(ClientMain),
        nameof(ClientMain.SpawnParticles),
        new[]
        {
            typeof(float), typeof(int), typeof(Vec3d), typeof(Vec3d), typeof(Vec3f), typeof(Vec3f),
            typeof(float), typeof(float), typeof(float), typeof(EnumParticleModel), typeof(IPlayer)
        }),
    AccessTools.Method(
        typeof(ClientMain),
        nameof(ClientMain.SpawnParticles),
        new[] { typeof(IParticlePropertiesProvider), typeof(IPlayer) }),
    AccessTools.Method(
        typeof(ClientMain),
        "PlaySoundAtInternal",
        new[]
        {
            typeof(AssetLocation), typeof(double), typeof(double), typeof(double), typeof(float), typeof(float),
            typeof(float), typeof(EnumSoundType)
        }),
    AccessTools.Method(
        typeof(ClientMain),
        nameof(ClientMain.StartPlaying),
        new[] { typeof(ILoadedSound), typeof(AssetLocation) }),
    AccessTools.Method(typeof(ClientMain), nameof(ClientMain.LoadSound), new[] { typeof(SoundParams) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Start), Type.EmptyTypes),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Pause), Type.EmptyTypes),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Stop), Type.EmptyTypes),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Dispose), Type.EmptyTypes),
    AccessTools.Method(
        typeof(LoadedSoundNative),
        nameof(LoadedSoundNative.SetPosition),
        new[] { typeof(float), typeof(float), typeof(float) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetPosition), new[] { typeof(Vec3f) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetPitch), new[] { typeof(float) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetPitchOffset), new[] { typeof(float) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetVolume), new[] { typeof(float) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetLooping), new[] { typeof(bool) }),
    AccessTools.Method(
        typeof(LoadedSoundNative),
        nameof(LoadedSoundNative.SetLowPassfiltering),
        new[] { typeof(float) }),
    AccessTools.Method(typeof(LoadedSoundNative), nameof(LoadedSoundNative.SetReverb), new[] { typeof(float) }),
    AccessTools.Method(
        typeof(BlockAccessorRelaxed),
        nameof(BlockAccessorRelaxed.SetBlock),
        new[] { typeof(int), typeof(BlockPos), typeof(ItemStack) }),
    AccessTools.Method(
        typeof(BlockAccessorRelaxed),
        nameof(BlockAccessorRelaxed.SetBlock),
        new[] { typeof(int), typeof(BlockPos), typeof(int) }),
    AccessTools.Method(
        typeof(BlockAccessorBulkMinimalUpdate),
        nameof(BlockAccessorBulkMinimalUpdate.FastCommit),
        Type.EmptyTypes),
    AccessTools.Method(
        typeof(BEBehaviorDoor),
        nameof(BEBehaviorDoor.ToggleDoorState),
        new[] { typeof(IPlayer), typeof(bool) })
};

static async Task<object?> InvokeTaskAsync(Type type, object instance, string methodName)
{
    MethodInfo method = type.GetMethod(
        methodName,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, methodName);
    object? invoked = method.Invoke(instance, new object[] { CancellationToken.None });
    if (invoked is not Task task)
    {
        throw new InvalidOperationException(methodName + " did not return a Task.");
    }
    await task.ConfigureAwait(false);
    return task.GetType().GetProperty("Result")?.GetValue(task);
}

static bool CallsMethod(MethodInfo caller, MethodInfo callee)
{
    byte[]? il = caller.GetMethodBody()?.GetILAsByteArray();
    if (il is null)
    {
        return false;
    }

    int token = callee.MetadataToken;
    for (int index = 0; index <= il.Length - 5; index++)
    {
        if ((il[index] == 0x28 || il[index] == 0x6f)
            && BitConverter.ToInt32(il, index + 1) == token)
        {
            return true;
        }
    }
    return false;
}

static bool CallsMethodReference(MethodInfo caller, MethodInfo callee)
{
    byte[]? il = caller.GetMethodBody()?.GetILAsByteArray();
    if (il is null)
    {
        return false;
    }

    for (int index = 0; index <= il.Length - 5; index++)
    {
        if (il[index] != 0x28 && il[index] != 0x6f)
        {
            continue;
        }

        try
        {
            MethodBase? called = caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1));
            if (called is MethodBase resolved
                && resolved.DeclaringType == callee.DeclaringType
                && string.Equals(resolved.Name, callee.Name, StringComparison.Ordinal)
                && resolved.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(callee.GetParameters().Select(parameter => parameter.ParameterType)))
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
        }
    }
    return false;
}

static bool AllComposeCallsDisableFirstFocus(MethodInfo caller, MethodInfo composeMethod)
{
    IReadOnlyList<CodeInstruction> instructions = PatchProcessor.GetCurrentInstructions(caller);
    List<int> composeCalls = [];
    for (int index = 0; index < instructions.Count; index++)
    {
        if (instructions[index].Calls(composeMethod))
        {
            composeCalls.Add(index);
        }
    }

    if (composeCalls.Count == 0)
    {
        return false;
    }

    foreach (int composeCall in composeCalls)
    {
        int argument = composeCall - 1;
        while (argument >= 0
            && instructions[argument].opcode == System.Reflection.Emit.OpCodes.Nop)
        {
            argument--;
        }
        if (argument < 0
            || instructions[argument].opcode != System.Reflection.Emit.OpCodes.Ldc_I4_0)
        {
            return false;
        }
    }
    return true;
}

static void AssertWorkspaceViewportPatch(
    MethodBase target,
    string patchTypeName,
    bool prefix)
{
    Patches? patches = Harmony.GetPatchInfo(target);
    IEnumerable<Patch> candidates = prefix ? patches?.Prefixes ?? [] : patches?.Postfixes ?? [];
    if (!candidates.Any(patch =>
            string.Equals(
                patch.PatchMethod.DeclaringType?.Name,
                patchTypeName,
                StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            $"The workspace viewport patch {patchTypeName} is not installed on "
            + $"{target.DeclaringType?.FullName}.{target.Name}.");
    }
}

try
{
    harmony.PatchAll(typeof(VSDirectorModSystem).Assembly);
    Type replayManagerCaptureProbe = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayManager",
        throwOnError: true)!;
    Type replayWorldPlaybackCaptureProbe = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayWorldPlayback",
        throwOnError: true)!;
    MethodInfo queueDirectBlockChangeProbe = replayManagerCaptureProbe.GetMethod(
        "QueueBlockChanged",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay direct block-change queue was not found.");
    MethodInfo shouldTrackDirectBlockChangesProbe = replayManagerCaptureProbe.GetProperty(
        "ShouldTrackDirectBlockChanges",
        BindingFlags.Instance | BindingFlags.Public)?.GetMethod
        ?? throw new InvalidOperationException("Replay direct block-change gate was not found.");
    MethodInfo queueBulkBlockChangesProbe = replayManagerCaptureProbe.GetMethod(
        "QueueBlockChanges",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay bulk block-change queue was not found.");
    MethodInfo queueExactBlockPositionProbe = replayManagerCaptureProbe.GetMethod(
        "QueueBlockPosition",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay exact block-position queue was not found.");
    MethodInfo queueBlockNeighborhoodProbe = replayManagerCaptureProbe.GetMethod(
        "QueueBlockNeighborhood",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(BlockPos) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay block-neighborhood queue was not found.");
    MethodInfo observeLiveBlockChangeProbe = replayWorldPlaybackCaptureProbe.GetMethod(
        "ObserveLiveBlockChange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay latest live-block ledger was not found.");
    Type solidBlockCapturePatchProbe = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplaySolidBlockCapturePatch",
        throwOnError: true)!;
    Type minimalBulkBlockCapturePatchProbe = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayMinimalBulkBlockCapturePatch",
        throwOnError: true)!;
    MethodInfo solidBlockCapturePostfixProbe = solidBlockCapturePatchProbe.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay solid block capture postfix was not found.");
    MethodInfo minimalBulkCapturePrefixProbe = minimalBulkBlockCapturePatchProbe.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay minimal bulk capture prefix was not found.");
    MethodInfo minimalBulkCapturePostfixProbe = minimalBulkBlockCapturePatchProbe.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay minimal bulk capture postfix was not found.");
    MethodInfo copyBlockPositionProbe = typeof(BlockPos).GetMethod(
        nameof(BlockPos.Copy),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null)
        ?? throw new InvalidOperationException("Block position copy method was not found.");
    MethodBase solidBlockSetTargetProbe = AccessTools.Method(
        typeof(BlockAccessorRelaxed),
        nameof(BlockAccessorRelaxed.SetBlock),
        new[] { typeof(int), typeof(BlockPos), typeof(ItemStack) })
        ?? throw new InvalidOperationException("Relaxed solid block setter was not found.");
    MethodBase minimalBulkCommitTargetProbe = AccessTools.Method(
        typeof(BlockAccessorBulkMinimalUpdate),
        nameof(BlockAccessorBulkMinimalUpdate.FastCommit),
        Type.EmptyTypes)
        ?? throw new InvalidOperationException("Minimal bulk block commit was not found.");
    List<string> blockCaptureFailures = [];
    if (Harmony.GetPatchInfo(solidBlockSetTargetProbe)?.Owners.Contains(harmonyId) != true)
    {
        blockCaptureFailures.Add("solid SetBlock patch");
    }
    if (Harmony.GetPatchInfo(minimalBulkCommitTargetProbe)?.Owners.Contains(harmonyId) != true)
    {
        blockCaptureFailures.Add("minimal bulk FastCommit patch");
    }
    if (!CallsMethod(queueDirectBlockChangeProbe, observeLiveBlockChangeProbe))
    {
        blockCaptureFailures.Add("live-world observation");
    }
    if (!CallsMethod(queueDirectBlockChangeProbe, queueExactBlockPositionProbe)
        || CallsMethod(queueDirectBlockChangeProbe, queueBlockNeighborhoodProbe))
    {
        blockCaptureFailures.Add("exact direct-write queueing");
    }
    if (!CallsMethod(queueBulkBlockChangesProbe, queueDirectBlockChangeProbe))
    {
        blockCaptureFailures.Add("bulk-to-direct queueing");
    }
    if (!CallsMethod(solidBlockCapturePostfixProbe, queueDirectBlockChangeProbe))
    {
        blockCaptureFailures.Add("solid postfix handoff");
    }
    if (!CallsMethodReference(minimalBulkCapturePrefixProbe, copyBlockPositionProbe))
    {
        blockCaptureFailures.Add("minimal bulk position copy");
    }
    if (!CallsMethod(minimalBulkCapturePrefixProbe, shouldTrackDirectBlockChangesProbe))
    {
        blockCaptureFailures.Add("minimal bulk idle-state gate");
    }
    if (!CallsMethod(minimalBulkCapturePostfixProbe, queueBulkBlockChangesProbe))
    {
        blockCaptureFailures.Add("minimal bulk postfix handoff");
    }
    if (blockCaptureFailures.Count > 0)
    {
        throw new InvalidOperationException(
            "Direct and minimal-bulk block capture failures: "
            + string.Join(", ", blockCaptureFailures));
    }
    Console.WriteLine("PASS direct and minimal-bulk replay block capture coverage");

    MethodInfo primaryFramebufferBlit = typeof(ClientPlatformWindows).GetMethod(
        nameof(ClientPlatformWindows.BlitPrimaryToDefault),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(
            typeof(ClientPlatformWindows).FullName,
            nameof(ClientPlatformWindows.BlitPrimaryToDefault));
    if (Harmony.GetPatchInfo(primaryFramebufferBlit)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name == "DirectorWorkspaceViewportBlitPatch") != true)
    {
        throw new InvalidOperationException(
            "The workspace game-view final-framebuffer patch is not installed.");
    }
    Console.WriteLine("PASS workspace game-view final-framebuffer patch");

    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(ClientMain),
            nameof(ClientMain.OrthoMode),
            new[] { typeof(int), typeof(int), typeof(bool) })
            ?? throw new MissingMethodException(typeof(ClientMain).FullName, nameof(ClientMain.OrthoMode)),
        "DirectorWorkspaceGameHudViewportPatch",
        prefix: false);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(typeof(SystemRenderAim), nameof(SystemRenderAim.OnRenderFrame2DOverlay))
            ?? throw new MissingMethodException(
                typeof(SystemRenderAim).FullName,
                nameof(SystemRenderAim.OnRenderFrame2DOverlay)),
        "DirectorWorkspaceAimViewportPatch",
        prefix: true);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(ClientPlatformWindows),
            nameof(ClientPlatformWindows.GlScissor),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) })
            ?? throw new MissingMethodException(
                typeof(ClientPlatformWindows).FullName,
                nameof(ClientPlatformWindows.GlScissor)),
        "DirectorWorkspaceGameHudScissorPatch",
        prefix: true);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(typeof(ClientMain), nameof(ClientMain.PerspectiveMode))
            ?? throw new MissingMethodException(typeof(ClientMain).FullName, nameof(ClientMain.PerspectiveMode)),
        "DirectorWorkspaceViewportRestorePatch",
        prefix: false);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(InventoryItemRenderer),
            nameof(InventoryItemRenderer.DisposeFrameBuffer),
            new[] { typeof(FrameBufferRef) })
            ?? throw new MissingMethodException(
                typeof(InventoryItemRenderer).FullName,
                nameof(InventoryItemRenderer.DisposeFrameBuffer)),
        "DirectorWorkspaceInventoryAtlasViewportPatch",
        prefix: false);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(InventoryItemRenderer),
            nameof(InventoryItemRenderer.DisposeFrameBuffer),
            new[] { typeof(FrameBufferRef) })
            ?? throw new MissingMethodException(
                typeof(InventoryItemRenderer).FullName,
                nameof(InventoryItemRenderer.DisposeFrameBuffer)),
        "DirectorWorkspaceInventoryAtlasViewportPatch",
        prefix: true);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(ClientMain),
            nameof(ClientMain.OnMouseMove),
            new[] { typeof(MouseEvent) })
            ?? throw new MissingMethodException(typeof(ClientMain).FullName, nameof(ClientMain.OnMouseMove)),
        "DirectorWorkspaceMouseMovePatch",
        prefix: true);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(ClientPlatformWindows),
            nameof(ClientPlatformWindows.CreateMouseEvent),
            new[] { typeof(EnumMouseButton) })
            ?? throw new MissingMethodException(
                typeof(ClientPlatformWindows).FullName,
                nameof(ClientPlatformWindows.CreateMouseEvent)),
        "DirectorWorkspaceMouseButtonPatch",
        prefix: false);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(PickingRayUtil),
            nameof(PickingRayUtil.GetPickingRayByMouseCoordinates),
            new[] { typeof(ClientMain) })
            ?? throw new MissingMethodException(
                typeof(PickingRayUtil).FullName,
                nameof(PickingRayUtil.GetPickingRayByMouseCoordinates)),
        "DirectorWorkspacePickingRayPatch",
        prefix: true);
    AssertWorkspaceViewportPatch(
        AccessTools.Method(
            typeof(GuiDialog),
            nameof(GuiDialog.OnRenderGUI),
            new[] { typeof(float) })
            ?? throw new MissingMethodException(typeof(GuiDialog).FullName, nameof(GuiDialog.OnRenderGUI)),
        "DirectorWorkspaceDirectorDialogViewportPatch",
        prefix: true);
    Console.WriteLine(
        "PASS workspace HUD, scissor, aim, viewport-restore, mouse-coordinate, picking-ray, and dialog-viewport patches");

    MethodInfo ensureEditAccess = typeof(VSDirectorModSystem).GetMethod(
        "EnsureEditAccess",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "EnsureEditAccess");
    MethodInfo ensureFeatureEditAccess = typeof(VSDirectorModSystem).GetMethod(
        "EnsureFeatureEditAccess",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "EnsureFeatureEditAccess");
    MethodInfo ensurePermission = typeof(VSDirectorModSystem).GetMethod(
        "EnsurePermission",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "EnsurePermission");
    if (CallsMethod(ensureFeatureEditAccess, ensureFeatureEditAccess)
        || !CallsMethod(ensureFeatureEditAccess, ensureEditAccess)
        || !CallsMethod(ensureFeatureEditAccess, ensurePermission))
    {
        throw new InvalidOperationException(
            "Feature edit authorization must check readiness and the requested permission without recursion.");
    }
    Console.WriteLine("PASS feature edit authorization is non-recursive");

    Type[] clientTypes;
    try
    {
        clientTypes = typeof(LoadedSoundNative).Assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException exception)
    {
        clientTypes = exception.Types.OfType<Type>().ToArray();
    }
    Type[] loadedSoundImplementations = clientTypes
        .Where(type => typeof(ILoadedSound).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
        .ToArray();
    if (loadedSoundImplementations.Length != 1
        || loadedSoundImplementations[0] != typeof(LoadedSoundNative))
    {
        throw new InvalidOperationException(
            "Vintage Story added an unpatched ILoadedSound implementation: "
            + string.Join(", ", loadedSoundImplementations.Select(type => type.FullName)));
    }
    MethodInfo clientStartPlaying = AccessTools.Method(
        typeof(ClientMain),
        nameof(ClientMain.StartPlaying),
        new[] { typeof(ILoadedSound), typeof(AssetLocation) })
        ?? throw new InvalidOperationException("ClientMain.StartPlaying sound boundary was not found.");
    MethodInfo loadedSoundStart = typeof(ILoadedSound).GetMethod(nameof(ILoadedSound.Start))
        ?? throw new InvalidOperationException("ILoadedSound.Start was not found.");
    if (!CallsMethodReference(clientStartPlaying, loadedSoundStart))
    {
        throw new InvalidOperationException(
            "ClientMain.StartPlaying no longer reaches ILoadedSound.Start; replay sound coverage must be reviewed.");
    }
    Console.WriteLine("PASS complete Vintage Story loaded-sound implementation and start boundary audit");

    MethodInfo playSoundInternal = AccessTools.Method(
        typeof(ClientMain),
        "PlaySoundAtInternal",
        new[]
        {
            typeof(AssetLocation), typeof(double), typeof(double), typeof(double), typeof(float), typeof(float),
            typeof(float), typeof(EnumSoundType)
        }) ?? throw new InvalidOperationException("ClientMain sound creation boundary was not found.");
    Type followSoundIsolation = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorFollowSoundIsolation",
        throwOnError: true)!;
    MethodInfo directorSoundDistance = followSoundIsolation.GetMethod(
        "DistanceSquaredFromActiveListener",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            followSoundIsolation.FullName,
            "DistanceSquaredFromActiveListener");
    MethodInfo suppressObserverSound = followSoundIsolation.GetMethod(
        "ShouldSuppressObserverRelativeSound",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(double), typeof(double), typeof(double), typeof(EnumSoundType) },
        modifiers: null)
        ?? throw new MissingMethodException(
            followSoundIsolation.FullName,
            "ShouldSuppressObserverRelativeSound");
    Type soundCapturePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplaySoundCapturePatch",
        throwOnError: true)!;
    MethodInfo soundCapturePrefix = soundCapturePatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(soundCapturePatch.FullName, "Prefix");
    Type blockSoundRelay = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorBlockSoundRelay",
        throwOnError: true)!;
    MethodInfo captureBlockSound = blockSoundRelay.GetMethod(
        "Capture",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(blockSoundRelay.FullName, "Capture");
    MethodInfo beginBlockSoundScope = blockSoundRelay.GetMethod(
        "Begin",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(blockSoundRelay.FullName, "Begin");
    MethodInfo endBlockSoundScope = blockSoundRelay.GetMethod(
        "End",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(blockSoundRelay.FullName, "End");
    Type blockSoundScopePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorBlockBreakingSoundScopePatch",
        throwOnError: true)!;
    MethodInfo blockSoundScopePrefix = blockSoundScopePatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(blockSoundScopePatch.FullName, "Prefix");
    MethodInfo blockSoundScopeFinalizer = blockSoundScopePatch.GetMethod(
        "Finalizer",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(blockSoundScopePatch.FullName, "Finalizer");
    Type loadedSoundStartPatchType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayLoadedSoundStartCapturePatch",
        throwOnError: true)!;
    MethodInfo observerLoadedSoundPrefix = loadedSoundStartPatchType.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(loadedSoundStartPatchType.FullName, "Prefix");
    IReadOnlyList<CodeInstruction> soundInstructions =
        PatchProcessor.GetCurrentInstructions(playSoundInternal).ToArray();
    if (!soundInstructions.Any(instruction => instruction.Calls(directorSoundDistance))
        || soundInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && method.DeclaringType == typeof(EntityPos)
            && method.Name == nameof(EntityPos.SquareDistanceTo)
            && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                new[] { typeof(double), typeof(double), typeof(double) }))
        || !CallsMethod(soundCapturePrefix, captureBlockSound)
        || !CallsMethod(blockSoundScopePrefix, beginBlockSoundScope)
        || !CallsMethod(blockSoundScopeFinalizer, endBlockSoundScope)
        || !CallsMethod(soundCapturePrefix, suppressObserverSound)
        || !CallsMethod(observerLoadedSoundPrefix, followSoundIsolation.GetMethod(
            "ShouldSuppressObserverRelativeSound",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(SoundParams) },
            modifiers: null)!))
    {
        throw new InvalidOperationException(
            "Remote follow sounds are not culled, block sounds are not relayed, or observer-relative sounds are not isolated.");
    }
    Type soundSpatialPolicy = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorSoundSpatialPolicy",
        throwOnError: true)!;
    MethodInfo suppressRelativePolicy = soundSpatialPolicy.GetMethod(
        "SuppressObserverRelativeSound",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(soundSpatialPolicy.FullName, "SuppressObserverRelativeSound");
    bool SuppressRelative(bool relative, EnumSoundType type, bool remoteFollow)
        => (bool)suppressRelativePolicy.Invoke(null, new object[] { relative, type, remoteFollow })!;
    if (!SuppressRelative(true, EnumSoundType.Sound, true)
        || !SuppressRelative(true, EnumSoundType.Entity, true)
        || SuppressRelative(true, EnumSoundType.Weather, true)
        || SuppressRelative(true, EnumSoundType.Ambient, true)
        || SuppressRelative(true, EnumSoundType.Music, true)
        || SuppressRelative(false, EnumSoundType.Sound, true)
        || SuppressRelative(true, EnumSoundType.Sound, false))
    {
        throw new InvalidOperationException(
            "Observer sound isolation does not preserve global music, ambience, and weather semantics.");
    }
    Console.WriteLine("PASS remote follow 3D sound culling and observer-audio isolation");

    Type replayManagerType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayManager",
        throwOnError: true)!;
    Type replayPlayerRenderModeType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerRenderMode",
        throwOnError: true)!;
    Type replayHeadControllerPatchType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerHeadControllerIsSelfPatch",
        throwOnError: true)!;
    MethodInfo replayPlayerUseThirdPerson = replayPlayerRenderModeType.GetMethod(
        "UseThirdPerson",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay player render-mode policy was not found.");
    MethodInfo replayHeadControllerPostfix = replayHeadControllerPatchType.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay player head-controller patch was not found.");
    MethodInfo isReplayThirdPerson = replayManagerType.GetMethod(
        "IsReplayThirdPerson",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay third-person actor lookup was not found.");
    if (CallsMethod(replayHeadControllerPostfix, replayPlayerUseThirdPerson)
        || !CallsMethod(replayHeadControllerPostfix, isReplayThirdPerson))
    {
        throw new InvalidOperationException(
            "Live offscreen players can be misclassified as replay ghosts by the head controller.");
    }
    Console.WriteLine("PASS replay-only player head-controller self override");
    Type replaySerializationType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplaySerialization",
        throwOnError: true)!;
    Type replayStoreType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayStore",
        throwOnError: true)!;
    Type replayInfoType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayInfo",
        throwOnError: true)!;
    Type cameraControllerType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorCameraController",
        throwOnError: true)!;
    MethodInfo automatedPlaybackPaused = cameraControllerType.GetProperty(
        "IsAutomatedPlaybackPaused",
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod
        ?? throw new MissingMemberException(cameraControllerType.FullName, "IsAutomatedPlaybackPaused");
    MethodInfo replayPausedGetter = replayManagerType.GetProperty(
        "IsPaused",
        BindingFlags.Instance | BindingFlags.Public)?.GetMethod
        ?? throw new MissingMemberException(replayManagerType.FullName, "IsPaused");
    if (!CallsMethodReference(automatedPlaybackPaused, replayPausedGetter))
    {
        throw new InvalidOperationException("Automated camera playback does not observe replay pause state.");
    }
    foreach (string methodName in new[] { "AdvancePath", "AdvanceSequence", "AdvanceFollow" })
    {
        MethodInfo advance = cameraControllerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(cameraControllerType.FullName, methodName);
        if (!CallsMethodReference(advance, automatedPlaybackPaused))
        {
            throw new InvalidOperationException($"{methodName} does not stop while replay playback is paused.");
        }
    }
    Console.WriteLine("PASS replay pause freezes automated camera playback");

    Type entityViewResolverType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorEntityViewResolver",
        throwOnError: true)!;
    MethodInfo resolveStableHeadPosition = entityViewResolverType.GetMethod(
        "TryResolveHeadPosition",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(entityViewResolverType.FullName, "TryResolveHeadPosition");
    MethodInfo createFollowTargetPose = cameraControllerType.GetMethod(
        "TryCreateFollowTargetPoseWithStableView",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            cameraControllerType.FullName,
            "TryCreateFollowTargetPoseWithStableView");
    MethodInfo createWorkflowFollowTargetPose = typeof(VSDirectorModSystem).GetMethod(
        "TryCreateWorkflowFollowTargetPose",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryCreateWorkflowFollowTargetPose");
    MethodInfo cameraYawFromModel = typeof(DirectorEntityViewMath).GetMethod(
        nameof(DirectorEntityViewMath.ToCameraYaw),
        BindingFlags.Static | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(double), typeof(double), typeof(double) },
        modifiers: null)
        ?? throw new MissingMethodException(typeof(DirectorEntityViewMath).FullName, "ToCameraYaw");
    MethodInfo playerRelativePitch = typeof(DirectorEntityViewMath).GetMethod(
        nameof(DirectorEntityViewMath.ToPlayerRelativePitch),
        BindingFlags.Static | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(double) },
        modifiers: null)
        ?? throw new MissingMethodException(
            typeof(DirectorEntityViewMath).FullName,
            "ToPlayerRelativePitch");
    if (!CallsMethod(createFollowTargetPose, resolveStableHeadPosition)
        || !CallsMethod(createWorkflowFollowTargetPose, resolveStableHeadPosition)
        || CallsMethod(createFollowTargetPose, cameraYawFromModel)
        || CallsMethod(createFollowTargetPose, playerRelativePitch)
        || CallsMethod(createWorkflowFollowTargetPose, cameraYawFromModel)
        || CallsMethod(createWorkflowFollowTargetPose, playerRelativePitch))
    {
        throw new InvalidOperationException(
            "Follow target sampling can feed the director camera pose back into the target pose.");
    }
    Console.WriteLine("PASS follow target sampling uses stable entity pose without camera feedback");

    MethodInfo serializeReplayEntity = replaySerializationType.GetMethod(
        "SerializeEntity",
        BindingFlags.Static | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(Entity), typeof(long), typeof(bool) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay entity serialization policy was not found.");
    ProbeEntityPlayer serializationProbe = new()
    {
        EntityId = 731L,
        selfNowShadowPass = false
    };
    byte[] serializedProbe = (byte[])serializeReplayEntity.Invoke(
        null,
        new object[] { serializationProbe, -731L, true })!;
    if (serializedProbe.Length == 0
        || serializationProbe.ShadowPassDuringSerialization != true
        || serializationProbe.selfNowShadowPass
        || serializationProbe.EntityId != 731L)
    {
        throw new InvalidOperationException(
            "Replay player snapshots do not serialize through the third-person animation manager.");
    }
    Console.WriteLine("PASS replay player snapshot third-person animation context");
    Type listMenuType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorListMenu",
        throwOnError: true)!;
    if (listMenuType.GetField("consumeNextMouseUp", BindingFlags.Instance | BindingFlags.NonPublic) is null
        || listMenuType.GetMethod(
            nameof(GuiElement.OnMouseDown),
            BindingFlags.Instance | BindingFlags.Public)?.DeclaringType != listMenuType
        || listMenuType.GetMethod(
            nameof(GuiElement.OnMouseUp),
            BindingFlags.Instance | BindingFlags.Public)?.DeclaringType != listMenuType)
    {
        throw new InvalidOperationException("Director dropdown popup does not guard its complete mouse click lifecycle.");
    }
    MethodInfo guiComposerMouseDown = AccessTools.Method(
        typeof(GuiComposer),
        nameof(GuiComposer.OnMouseDown),
        new[] { typeof(MouseEvent) })
        ?? throw new InvalidOperationException("GuiComposer mouse-down method was not found.");
    if (Harmony.GetPatchInfo(guiComposerMouseDown)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name == "DirectorGuiComposerDropdownCapturePatch") != true)
    {
        throw new InvalidOperationException("GuiComposer does not have the popup-first dropdown capture patch.");
    }
    MethodInfo guiComposerMouseUp = AccessTools.Method(
        typeof(GuiComposer),
        nameof(GuiComposer.OnMouseUp),
        new[] { typeof(MouseEvent) })
        ?? throw new InvalidOperationException("GuiComposer mouse-up method was not found.");
    if (Harmony.GetPatchInfo(guiComposerMouseUp)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name == "DirectorGuiComposerDropdownMouseUpCapturePatch") != true)
    {
        throw new InvalidOperationException("GuiComposer does not capture dropdown mouse-up across panel rebuilds.");
    }
    MethodInfo guiManagerMouseUp = AccessTools.Method(
        typeof(GuiManager),
        nameof(GuiManager.OnMouseUp),
        new[] { typeof(MouseEvent) })
        ?? throw new InvalidOperationException("GuiManager mouse-up method was not found.");
    if (Harmony.GetPatchInfo(guiManagerMouseUp)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name == "DirectorGuiManagerButtonMouseUpCapturePatch") != true)
    {
        throw new InvalidOperationException(
            "GuiManager does not route captured Director button releases before dialog propagation.");
    }
    Type dropdownCaptureType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorDropdownInputCapture",
        throwOnError: true)!;
    if (dropdownCaptureType.GetField("openPopup", BindingFlags.Static | BindingFlags.NonPublic) is null
        || dropdownCaptureType.GetField("mouseCapture", BindingFlags.Static | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException("Director dropdown capture is not shared across GUI composers.");
    }
    Type buttonCaptureType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorButtonInputCapture",
        throwOnError: true)!;
    if (buttonCaptureType.GetMethod(
            "TryRelease",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(MouseEvent) },
            modifiers: null) is null
        || typeof(VSDirectorModSystem).Assembly.GetType(
            "VSDirector.Gui.GuiElementDirectorButton",
            throwOnError: true)!.GetMethod(
                "OnCapturedMouseUp",
                BindingFlags.Instance | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Director button pointer capture is missing the manager-level release lifecycle.");
    }
    Console.WriteLine("PASS director dropdown click-through guard");
    Type directorSliderType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorSlider",
        throwOnError: true)!;
    MethodInfo composeDirectorSlider = directorSliderType.GetMethod(
        nameof(GuiElement.ComposeElements),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(directorSliderType.FullName, nameof(GuiElement.ComposeElements));
    MethodInfo renderDirectorSlider = directorSliderType.GetMethod(
        nameof(GuiElement.RenderInteractiveElements),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(
            directorSliderType.FullName,
            nameof(GuiElement.RenderInteractiveElements));
    MethodInfo composeSliderSolidTexture = directorSliderType.GetMethod(
        "ComposeSolidTexture",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(directorSliderType.FullName, "ComposeSolidTexture");
    MethodInfo renderSliderSolidTexture = directorSliderType.GetMethod(
        "RenderSolidTexture",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(directorSliderType.FullName, "RenderSolidTexture");
    bool sliderUsesRectangleOutline = typeof(IRenderAPI).GetMethods()
        .Where(method => method.Name == nameof(IRenderAPI.RenderRectangle))
        .Any(method => CallsMethodReference(renderDirectorSlider, method)
            || CallsMethodReference(renderSliderSolidTexture, method));
    if (!CallsMethod(composeDirectorSlider, composeSliderSolidTexture)
        || !CallsMethod(renderDirectorSlider, renderSliderSolidTexture)
        || sliderUsesRectangleOutline
        || directorSliderType.GetField(
            "trackTexture",
            BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(LoadedTexture)
        || directorSliderType.GetField(
            "progressTexture",
            BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(LoadedTexture)
        || directorSliderType.GetField(
            "thumbTexture",
            BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(LoadedTexture))
    {
        throw new InvalidOperationException(
            "Director slider track and thumb are not rendered from Cairo-backed solid textures.");
    }
    Console.WriteLine("PASS director slider solid texture track and thumb");
    Type sliderCaptureType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorSliderInputCapture",
        throwOnError: true)!;
    MethodInfo sliderIsSelected = sliderCaptureType.GetMethod(
        "IsSelected",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(sliderCaptureType.FullName, "IsSelected");
    MethodInfo sliderMouseWheel = directorSliderType.GetMethod(
        nameof(GuiElement.OnMouseWheel),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(directorSliderType.FullName, nameof(GuiElement.OnMouseWheel));
    if (sliderCaptureType.GetField("selected", BindingFlags.Static | BindingFlags.NonPublic) is null
        || !CallsMethod(sliderMouseWheel, sliderIsSelected))
    {
        throw new InvalidOperationException(
            "Director sliders still accept wheel input without an explicit pointer selection.");
    }

    Type fixedPopoverType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorFixedPopover",
        throwOnError: true)!;
    if (fixedPopoverType.GetProperty(
            nameof(GuiElement.Focusable),
            BindingFlags.Instance | BindingFlags.Public)?.GetMethod?.DeclaringType != fixedPopoverType)
    {
        throw new InvalidOperationException(
            "Director fixed popovers cannot retain keyboard focus for their embedded inputs.");
    }

    MethodInfo guiComposerMouseMove = AccessTools.Method(
        typeof(GuiComposer),
        nameof(GuiComposer.OnMouseMove),
        new[] { typeof(MouseEvent) })
        ?? throw new InvalidOperationException("GuiComposer mouse-move method was not found.");
    if (Harmony.GetPatchInfo(guiComposerMouseMove)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name
                == "DirectorGuiComposerDropdownMouseMoveCapturePatch") != true)
    {
        throw new InvalidOperationException(
            "Director fixed popovers do not retain pointer capture while editing.");
    }

    Type timeTextType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorTimeText",
        throwOnError: true)!;
    MethodInfo tryParseTime = timeTextType.GetMethod(
        "TryParseTicks",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(timeTextType.FullName, "TryParseTicks");
    MethodInfo formatTime = timeTextType.GetMethod(
        "FormatTicks",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(timeTextType.FullName, "FormatTicks");
    object?[] validTimeArguments = { "18:30", 0 };
    object?[] invalidTimeArguments = { "24:00", 0 };
    if (tryParseTime.Invoke(null, validTimeArguments) is not true
        || (int)validTimeArguments[1]! != 18500
        || tryParseTime.Invoke(null, invalidTimeArguments) is not false
        || !string.Equals(formatTime.Invoke(null, new object[] { 18500 }) as string, "18:30",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Director HH:MM time input conversion is not stable.");
    }
    Console.WriteLine("PASS focused slider wheel, persistent popover input, and direct HH:MM editor");
    Type escapeMenuPatchType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorEscapeMenuComposePatch",
        throwOnError: true)!;
    double escapeMenuControlHeight = (double)escapeMenuPatchType.GetField(
        "MenuControlHeight",
        BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
    double escapeMenuRowPitch = (double)escapeMenuPatchType.GetField(
        "MenuRowPitch",
        BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
    MethodInfo toggleEscapeRecording = escapeMenuPatchType.GetMethod(
        "ToggleRecording",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(escapeMenuPatchType.FullName, "ToggleRecording");
    MethodInfo toggleEscapeOffscreen = escapeMenuPatchType.GetMethod(
        "ToggleOffscreenRendering",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(escapeMenuPatchType.FullName, "ToggleOffscreenRendering");
    MethodInfo createEscapeCamera = escapeMenuPatchType.GetMethod(
        "CreateCamera",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(escapeMenuPatchType.FullName, "CreateCamera");
    MethodInfo startEscapeFreeCamera = escapeMenuPatchType.GetMethod(
        "StartFreeCamera",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(escapeMenuPatchType.FullName, "StartFreeCamera");
    MethodInfo startEscapeOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "TryStartEscapeMenuLiveOffscreenRecording",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryStartEscapeMenuLiveOffscreenRecording");
    MethodInfo startLiveOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "TryStartLiveOffscreenRecording",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryStartLiveOffscreenRecording");
    MethodInfo addCamera = typeof(VSDirectorModSystem).GetMethod(
        "TryAddCamera",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "TryAddCamera");
    MethodInfo ownerStartFreeCamera = typeof(VSDirectorModSystem).GetMethod(
        "TryStartEscapeMenuFreeCamera",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryStartEscapeMenuFreeCamera");
    MethodInfo ownerToggleEscapeRecording = typeof(VSDirectorModSystem).GetMethod(
        "TryToggleEscapeMenuReplayRecording",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(string), typeof(string).MakeByRefType() },
        modifiers: null)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryToggleEscapeMenuReplayRecording(string, out string)");
    MethodInfo legacyToggleEscapeRecording = typeof(VSDirectorModSystem).GetMethod(
        "TryToggleEscapeMenuReplayRecording",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(string).MakeByRefType() },
        modifiers: null)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryToggleEscapeMenuReplayRecording(out string)");
    MethodInfo startReplayRecording = typeof(VSDirectorModSystem).GetMethod(
        "TryStartReplayRecording",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(string), typeof(string).MakeByRefType() },
        modifiers: null)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryStartReplayRecording");
    FieldInfo startReplayHotkey = typeof(VSDirectorModSystem).GetField(
        "ReplayRecordingStartHotkey",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(VSDirectorModSystem).FullName, "ReplayRecordingStartHotkey");
    FieldInfo stopReplayHotkey = typeof(VSDirectorModSystem).GetField(
        "ReplayRecordingStopHotkey",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(VSDirectorModSystem).FullName, "ReplayRecordingStopHotkey");
    FieldInfo exitHotkey = typeof(VSDirectorModSystem).GetField(
        "ExitHotkey",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(VSDirectorModSystem).FullName, "ExitHotkey");
    if (startReplayHotkey.GetValue(null) is not string startHotkeyCode
        || stopReplayHotkey.GetValue(null) is not string stopHotkeyCode
        || startHotkeyCode == stopHotkeyCode
        || !startHotkeyCode.Contains("replay-recording-start", StringComparison.Ordinal)
        || !stopHotkeyCode.Contains("replay-recording-stop", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Replay recording start/stop hotkey codes are not distinct.");
    }
    string source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "src",
        "VSDirectorModSystem.cs")).ReplaceLineEndings("\n");
    if (exitHotkey.GetValue(null) is not string exitHotkeyCode
        || !source.Contains(
            "RegisterHotKey(ExitHotkey, Lang.Get(\"vsdirector:hotkey-exit\"), GlKeys.F10",
            StringComparison.Ordinal)
        || !source.Contains(
            "if (replay?.IsRecording == true)\n            {\n                TryStopReplayRecording(out _);\n            }\n            else if (replay?.IsPlaying == true || replay?.IsLoading == true)",
            StringComparison.Ordinal)
        || !source.Contains(
            "GlKeys.R,\n            HotkeyType.GUIOrOtherControls,\n            ctrlPressed: true",
            StringComparison.Ordinal)
        || !source.Contains(
            "ReplayRecordingStopHotkey,\n            Lang.Get(\"vsdirector:hotkey-replay-recording-stop\"),\n            GlKeys.S,\n            HotkeyType.GUIOrOtherControls,\n            altPressed: true",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("F10 must stop replay recordings before using the general exit behavior.");
    }
    Console.WriteLine("PASS F10 exits generally and stops replay recordings first; Ctrl+R/Alt+S remain bound");
    Type pauseControlsType = typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Gui.DirectorPauseControls", throwOnError: true)!;
    MethodInfo getText = pauseControlsType.GetProperty("ReplayName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!;
    Type directorDropdownType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorDropDown",
        throwOnError: true)!;
    if (escapeMenuPatchType.GetField(
            "OffscreenStopButtonKey",
            BindingFlags.Static | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException(
            "Escape-menu offscreen controls still contain an overlapping stop button.");
    }
    if (escapeMenuControlHeight != 30d
        || escapeMenuRowPitch != 36d
        || pauseControlsType.GetMethod("RowCount", BindingFlags.Static | BindingFlags.NonPublic) is null
        || pauseControlsType.GetField(
            "ReplayNameKey",
            BindingFlags.Static | BindingFlags.NonPublic) is null
        || pauseControlsType.GetField(
            "OffscreenSourceKey",
            BindingFlags.Static | BindingFlags.NonPublic) is null
        || pauseControlsType.GetField(
            "CameraNameKey",
            BindingFlags.Static | BindingFlags.NonPublic) is null
        || pauseControlsType.GetField(
            "FreeCameraButtonKey",
            BindingFlags.Static | BindingFlags.NonPublic) is null
        || pauseControlsType.GetMethod("RefreshSources", BindingFlags.Instance | BindingFlags.NonPublic) is null
        || !CallsMethod(toggleEscapeOffscreen, startEscapeOffscreen)
        || !CallsMethod(createEscapeCamera, addCamera)
        || CallsMethod(startEscapeOffscreen, addCamera)
        || !CallsMethod(startEscapeOffscreen, startLiveOffscreen))
    {
        throw new InvalidOperationException(
            "Escape-menu current-view offscreen rendering must not save an implicit camera.");
    }
    Console.WriteLine("PASS escape-menu transient current-view offscreen control flow");
    bool escapeRecordingReadsName = PatchProcessor.GetCurrentInstructions(toggleEscapeRecording)
        .Any(instruction => instruction.operand is MethodInfo called
            && string.Equals(called.Name, getText.Name, StringComparison.Ordinal)
            && called.GetParameters().Length == 0
            && (called.DeclaringType == getText.DeclaringType
                || called.DeclaringType?.IsAssignableFrom(getText.DeclaringType) == true
                || getText.DeclaringType?.IsAssignableFrom(called.DeclaringType) == true));
    bool escapeRecordingPassesName = CallsMethod(toggleEscapeRecording, ownerToggleEscapeRecording);
    bool legacyRecordingDelegates = CallsMethod(
        legacyToggleEscapeRecording,
        ownerToggleEscapeRecording);
    bool namedRecordingStartsReplay = CallsMethod(ownerToggleEscapeRecording, startReplayRecording);
    if (!escapeRecordingReadsName
        || !escapeRecordingPassesName
        || !legacyRecordingDelegates
        || !namedRecordingStartsReplay)
    {
        throw new InvalidOperationException(
            "Escape-menu replay recording does not pass the inline custom name to capture: "
            + $"reads={escapeRecordingReadsName}, passes={escapeRecordingPassesName}, "
            + $"legacy={legacyRecordingDelegates}, starts={namedRecordingStartsReplay}.");
    }
    Console.WriteLine("PASS escape-menu inline replay-name recording flow");
    var nativePauseMethods = pauseControlsType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Concat(pauseControlsType.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
        .Where(method => method.GetMethodBody() is not null).ToArray();
    var nativePauseCalls = nativePauseMethods.SelectMany(method => PatchProcessor.GetCurrentInstructions(method))
        .Select(instruction => instruction.operand).OfType<MethodBase>().ToArray();
    foreach (string helper in new[] { "AddTextInput", "AddDropDown", "AddButton" })
    {
        if (!nativePauseCalls.Any(called => called.DeclaringType == typeof(Vintagestory.API.Client.GuiComposerHelpers) && called.Name == helper))
            throw new InvalidOperationException("Pause menu does not use the vanilla " + helper + " helper.");
    }
    if (!typeof(GuiElement).IsAssignableFrom(pauseControlsType)
        || nativePauseCalls.Any(called => called.DeclaringType?.Namespace == "VSDirector.Gui" && called.DeclaringType?.Name.Contains("Rml", StringComparison.Ordinal) == true
            || called.DeclaringType?.Name is "DirectorGuiInputExtensions" or "DirectorGuiButtonExtensions")
        || typeof(VSDirectorModSystem).Assembly.GetType("VSDirector.Gui.DirectorPauseRmlControls") is not null)
        throw new InvalidOperationException("Pause controls must use the vanilla composer without RmlUi or custom control styling.");
    Console.WriteLine("PASS escape-menu controls use vanilla buttons, text inputs and dropdowns without RmlUi");
    if (!CallsMethod(startEscapeFreeCamera, ownerStartFreeCamera)
        || escapeMenuPatchType.GetField(
            "WorkspaceButtonKey",
            BindingFlags.Static | BindingFlags.NonPublic) is not null
        || escapeMenuPatchType.GetField(
            "ReplaysButtonKey",
            BindingFlags.Static | BindingFlags.NonPublic) is not null
        || escapeMenuPatchType.GetField(
            "SettingsButtonKey",
            BindingFlags.Static | BindingFlags.NonPublic) is not null
        || escapeMenuPatchType.GetMethod("BuildWindow", BindingFlags.Static | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Escape-menu companion window or free-camera flow is missing.");
    }
    Console.WriteLine("PASS escape-menu companion window and free-camera control flow");
    Type escapeMenuVisibilityPatchType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorEscapeMenuVisibilityPatch",
        throwOnError: true)!;
    Type nativeEscapeMenuType = AccessTools.TypeByName(
        "Vintagestory.Client.NoObf.GuiDialogEscapeMenu")
        ?? throw new TypeLoadException("Vintagestory.Client.NoObf.GuiDialogEscapeMenu");
    MethodInfo escapeMenuOpened = AccessTools.Method(
        nativeEscapeMenuType,
        nameof(GuiDialog.OnGuiOpened),
        Type.EmptyTypes)
        ?? throw new MissingMethodException(nativeEscapeMenuType.FullName, nameof(GuiDialog.OnGuiOpened));
    MethodInfo escapeMenuClosed = AccessTools.Method(
        nativeEscapeMenuType,
        nameof(GuiDialog.OnGuiClosed),
        Type.EmptyTypes)
        ?? throw new MissingMethodException(nativeEscapeMenuType.FullName, nameof(GuiDialog.OnGuiClosed));
    if (Harmony.GetPatchInfo(escapeMenuOpened)?.Prefixes.Any(patch =>
            patch.PatchMethod.DeclaringType == escapeMenuVisibilityPatchType) != true
        || Harmony.GetPatchInfo(escapeMenuClosed)?.Postfixes.Any(patch =>
            patch.PatchMethod.DeclaringType == escapeMenuVisibilityPatchType) != true)
    {
        throw new InvalidOperationException(
            "Escape menu does not preserve and restore the director camera GUI override.");
    }
    Console.WriteLine("PASS free-camera escape-menu visibility override");
    Type workspaceType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorWorkspaceHud",
        throwOnError: true)!;
    foreach (string removedType in new[]
    {
        "VSDirector.Gui.DirectorPathEditDialog",
        "VSDirector.Gui.DirectorRecordingPanelDialog",
        "VSDirector.Networking.DirectorRecordingServer"
    })
    {
        if (typeof(VSDirectorModSystem).Assembly.GetType(removedType, throwOnError: false) is not null)
        {
            throw new InvalidOperationException($"Removed feature type is still present: {removedType}.");
        }
    }
    foreach (string removedSetting in new[]
    {
        "ShowAdvancedPathEditor",
        "ShowMultiplayerSynchronizedRecording",
        "ServerRecordingEnabled",
        "ServerRecordingMaxStorageGiB",
        "ServerRecordingMinimumFreeDiskGiB",
        "ShowOffscreenBufferingSettings",
        "OffscreenPboRingSize",
        "OffscreenWriterQueueCapacity",
        "AssignedRecordingTrackId",
        "ShowAdvancedReplaySettings",
        "ShowActorExtractionTools"
    })
    {
        if (typeof(DirectorConfig).GetProperty(removedSetting) is not null)
        {
            throw new InvalidOperationException($"Removed setting is still present: {removedSetting}.");
        }
    }
    Assembly actorPathAssembly = typeof(VSDirectorModSystem).Assembly;
    if (actorPathAssembly.GetType("VSDirector.Data.DirectorActorPathExporter", throwOnError: false) is not null
        || typeof(VSDirectorModSystem).GetMethod(
            "TryExportReplayActorPath",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null
        || workspaceType.GetMethod(
            "ComposeActorPathExport",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException("Replay actor path extraction is still exposed.");
    }
    Type actorReplayManagerType = actorPathAssembly.GetType(
        "VSDirector.Replay.DirectorReplayManager",
        throwOnError: true)!;
    if (actorReplayManagerType.GetMethod(
            "TryBuildActorPath",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException("Replay actor path generation is still exposed.");
    }
    if (DirectorPermissions.Definitions.Any(definition =>
            definition.Code is "vsdirector.recording-sync" or "vsdirector.monitor" or "vsdirector.server-recording"))
    {
        throw new InvalidOperationException("Synchronized-recording permissions are still registered.");
    }
    foreach (string removedType in new[]
    {
        "Networking.DirectorServerCoordinator", "Networking.DirectorServerRecordingClient",
        "Networking.DirectorServerReplayCapture", "Networking.DirectorServerPacket",
        "Networking.DirectorSessionControl", "Gui.DirectorServerWindow", "Gui.DirectorRecordingConsentWindow",
        "Data.IDirectorSessionRecorder", "Data.DirectorClockModel", "Data.DirectorSessionMerger"
    })
        if (actorPathAssembly.GetType("VSDirector." + removedType) is not null)
            throw new InvalidOperationException("Removed coordinated-recording runtime remains: " + removedType);
    Console.WriteLine("PASS removed server coordinated-recording runtime, settings, permissions, buffering controls and advanced path editor");
    if (actorPathAssembly.GetTypes().Any(type =>
            type.Name.StartsWith("DirectorStudio", StringComparison.Ordinal)
            || type.Name.StartsWith("IDirectorStudio", StringComparison.Ordinal)
            || type.Name is "StudioInterpolation" or "StudioTransformKey" or "StudioActionClip" or "StudioCameraKey")
        || typeof(VSDirectorModSystem).GetMethod("OpenStudioWorkspace",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || typeof(VSDirectorModSystem).GetField("StudioEntryEnabled",
            BindingFlags.Static | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException("Removed Film Studio runtime or entry point is still present.");
    }
    Console.WriteLine("PASS Film Studio project, resource, timeline, renderer, exporter and workspace entry removed");
    MethodInfo workspaceDropDownOpen = workspaceType.GetMethod(
        "IsWorkspaceDropDownOpen",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "IsWorkspaceDropDownOpen");
    foreach ((string methodName, Type eventType) in new[]
    {
        (nameof(GuiDialog.OnMouseDown), typeof(MouseEvent)),
        (nameof(GuiDialog.OnMouseMove), typeof(MouseEvent)),
        (nameof(GuiDialog.OnMouseUp), typeof(MouseEvent)),
        (nameof(GuiDialog.OnMouseWheel), typeof(MouseWheelEventArgs))
    })
    {
        MethodInfo inputMethod = workspaceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { eventType },
            modifiers: null)
            ?? throw new MissingMethodException(workspaceType.FullName, methodName);
        if (!CallsMethod(inputMethod, workspaceDropDownOpen))
        {
            throw new InvalidOperationException(
                $"Workspace {methodName} does not route an open dropdown before canvas input.");
        }
    }
    Console.WriteLine("PASS workspace dropdowns take priority over canvas input");

    MethodInfo workspaceMouseDown = workspaceType.GetMethod(
        nameof(GuiDialog.OnMouseDown),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(MouseEvent) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, nameof(GuiDialog.OnMouseDown));
    MethodInfo workspaceMouseUp = workspaceType.GetMethod(
        nameof(GuiDialog.OnMouseUp),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(MouseEvent) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, nameof(GuiDialog.OnMouseUp));
    MethodInfo enableReplayFreeCamera = typeof(VSDirectorModSystem).GetMethod(
        "TryEnableReplayFreeCamera",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "TryEnableReplayFreeCamera");
    MethodInfo setReplayViewportPointer = typeof(VSDirectorModSystem).GetMethod(
        "SetReplayViewportFreeCameraPointerActive",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "SetReplayViewportFreeCameraPointerActive");
    MethodInfo endReplayViewportPointer = workspaceType.GetMethod(
        "EndReplayViewportFreeCameraPointerInput",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "EndReplayViewportFreeCameraPointerInput");
    if (!CallsMethod(workspaceMouseDown, enableReplayFreeCamera)
        || !CallsMethod(workspaceMouseDown, setReplayViewportPointer)
        || !CallsMethod(workspaceMouseUp, endReplayViewportPointer)
        || !CallsMethod(endReplayViewportPointer, setReplayViewportPointer))
    {
        throw new InvalidOperationException(
            "Replay viewport left-button input does not acquire and release free-camera pointer control.");
    }

    MethodInfo workspaceKeyDown = workspaceType.GetMethod(
        nameof(GuiDialog.OnKeyDown),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(KeyEvent) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, nameof(GuiDialog.OnKeyDown));
    FieldInfo freeCameraPointerActive = workspaceType.GetField(
        "replayViewportFreeCameraPointerActive",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            workspaceType.FullName,
            "replayViewportFreeCameraPointerActive");
    if (!PatchProcessor.GetCurrentInstructions(workspaceKeyDown).Any(instruction =>
            Equals(instruction.operand, freeCameraPointerActive)))
    {
        throw new InvalidOperationException(
            "Replay free-camera keyboard input is not isolated from focused workspace controls.");
    }
    Console.WriteLine("PASS replay free-camera keyboard input does not toggle focused controls");

    MethodInfo enterReplayFreeCamera = typeof(VSDirectorModSystem).GetMethod(
        "EnterReplayFreeCamera",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "EnterReplayFreeCamera");
    MethodInfo keepReplayWorkspaceVisible = workspaceType.GetMethod(
        "KeepReplayWorkspaceVisible",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "KeepReplayWorkspaceVisible");
    MethodInfo enterReplayNavigationMode = workspaceType.GetMethod(
        "EnterReplayNavigationMode",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "EnterReplayNavigationMode");
    if (!CallsMethod(enterReplayFreeCamera, keepReplayWorkspaceVisible)
        || CallsMethod(enterReplayFreeCamera, enterReplayNavigationMode))
    {
        throw new InvalidOperationException(
            "Replay free camera can hide the workspace by entering navigation mode.");
    }

    FieldInfo guiPointerCameraInput = cameraControllerType.GetField(
        "guiPointerCameraInput",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(cameraControllerType.FullName, "guiPointerCameraInput");
    MethodInfo controllerMouseMove = cameraControllerType.GetMethod(
        "OnMouseMove",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(cameraControllerType.FullName, "OnMouseMove");
    if (!PatchProcessor.GetCurrentInstructions(controllerMouseMove).Any(instruction =>
            Equals(instruction.operand, guiPointerCameraInput)))
    {
        throw new InvalidOperationException(
            "GUI-visible free camera does not gate mouse look on the held viewport pointer state.");
    }
    Console.WriteLine("PASS replay viewport hold-to-free-camera keeps workspace visible");

    FieldInfo replayCameraAuthority = typeof(VSDirectorModSystem).GetField(
        "replayViewportCameraAuthority",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            typeof(VSDirectorModSystem).FullName,
            "replayViewportCameraAuthority");
    if (replayCameraAuthority.FieldType != typeof(DirectorReplayCameraAuthority))
    {
        throw new InvalidOperationException(
            "Replay camera ownership is not represented by an explicit authority state.");
    }

    MethodInfo pauseReplayForTimelineScrub = typeof(VSDirectorModSystem).GetMethod(
        "PauseReplayForTimelineScrub",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "PauseReplayForTimelineScrub");
    MethodInfo setReplayPaused = replayManagerType.GetMethod(
        "SetPaused",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(replayManagerType.FullName, "SetPaused");
    if (!CallsMethod(pauseReplayForTimelineScrub, setReplayPaused)
        || !CallsMethod(workspaceMouseDown, pauseReplayForTimelineScrub))
    {
        throw new InvalidOperationException(
            "Workspace replay timeline scrubbing does not pause before changing playback time.");
    }

    MethodInfo toggleReplayPause = typeof(VSDirectorModSystem).GetMethod(
        "TryToggleReplayPause",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "TryToggleReplayPause");
    MethodInfo updateReplayViewportTimelineForHandoff = typeof(VSDirectorModSystem).GetMethod(
        "UpdateReplayViewportTimeline",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "UpdateReplayViewportTimeline");
    if (PatchProcessor.GetCurrentInstructions(toggleReplayPause).Any(instruction =>
            instruction.opcode == System.Reflection.Emit.OpCodes.Stfld && Equals(instruction.operand, replayCameraAuthority)))
    {
        throw new InvalidOperationException(
            "Resuming replay playback replaces the independent editor camera authority.");
    }

    MethodInfo startControllerFreeCamera = cameraControllerType.GetMethod(
        "TryStartFreeCamera",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(cameraControllerType.FullName, "TryStartFreeCamera");
    MethodInfo currentCameraPose = cameraControllerType.GetProperty(
        "CurrentPose",
        BindingFlags.Instance | BindingFlags.Public)?.GetMethod
        ?? throw new MissingMemberException(cameraControllerType.FullName, "CurrentPose");
    if (!CallsMethodReference(startControllerFreeCamera, currentCameraPose))
    {
        throw new InvalidOperationException(
            "Entering replay free camera does not preserve the currently rendered camera pose.");
    }
    Console.WriteLine("PASS replay free-camera and timeline playback authority boundary");

    Type replaySuspensionType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorSingleplayerReplaySuspension",
        throwOnError: true)!;
    MethodInfo replaySuspensionEligibility = replaySuspensionType.GetMethod(
        "IsEligible",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replaySuspensionType.FullName, "IsEligible");
    bool Eligible(bool singleplayer, bool openedToLan, bool worldReady)
        => replaySuspensionEligibility.Invoke(
            null,
            new object[] { singleplayer, openedToLan, worldReady }) is true;
    if (!Eligible(singleplayer: true, openedToLan: false, worldReady: true)
        || Eligible(singleplayer: false, openedToLan: false, worldReady: true)
        || Eligible(singleplayer: true, openedToLan: true, worldReady: true)
        || Eligible(singleplayer: true, openedToLan: false, worldReady: false))
    {
        throw new InvalidOperationException(
            "Replay world suspension eligibility is not restricted to ready, private singleplayer worlds.");
    }

    MethodInfo acquireReplaySuspension = replaySuspensionType.GetMethod(
        "TryAcquire",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replaySuspensionType.FullName, "TryAcquire");
    MethodInfo maintainReplaySuspension = replaySuspensionType.GetMethod(
        "Maintain",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replaySuspensionType.FullName, "Maintain");
    MethodInfo releaseReplaySuspension = replaySuspensionType.GetMethod(
        "Release",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replaySuspensionType.FullName, "Release");
    MethodInfo setPlatformPaused = typeof(ClientPlatformAbstract).GetMethod(
        nameof(ClientPlatformAbstract.SetGamePausedState),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(
            typeof(ClientPlatformAbstract).FullName,
            nameof(ClientPlatformAbstract.SetGamePausedState));
    MethodInfo stopCalendar = typeof(GameCalendar).GetMethod(
        nameof(GameCalendar.Stop),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(typeof(GameCalendar).FullName, nameof(GameCalendar.Stop));
    MethodInfo startCalendar = typeof(GameCalendar).GetMethod(
        nameof(GameCalendar.Start),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(typeof(GameCalendar).FullName, nameof(GameCalendar.Start));
    MethodInfo pauseWholeGame = typeof(ClientMain).GetMethod(
        nameof(ClientMain.PauseGame),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(bool) },
        modifiers: null)
        ?? throw new MissingMethodException(typeof(ClientMain).FullName, nameof(ClientMain.PauseGame));
    if (!CallsMethodReference(acquireReplaySuspension, setPlatformPaused)
        || !CallsMethodReference(acquireReplaySuspension, stopCalendar)
        || !CallsMethodReference(maintainReplaySuspension, setPlatformPaused)
        || !CallsMethodReference(releaseReplaySuspension, setPlatformPaused)
        || !CallsMethodReference(releaseReplaySuspension, startCalendar)
        || CallsMethodReference(acquireReplaySuspension, pauseWholeGame))
    {
        throw new InvalidOperationException(
            "Replay suspension does not isolate the integrated server and calendar from full client pause.");
    }

    MethodInfo integratedServerLoop = typeof(Vintagestory.Client.ClientProgram).GetMethod(
        "ServerThreadStart",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(Vintagestory.Client.ClientProgram).FullName,
            "ServerThreadStart");
    MethodInfo platformPausedGetter = typeof(ClientPlatformAbstract).GetProperty(
        nameof(ClientPlatformAbstract.IsGamePaused),
        BindingFlags.Instance | BindingFlags.Public)?.GetMethod
        ?? throw new MissingMemberException(
            typeof(ClientPlatformAbstract).FullName,
            nameof(ClientPlatformAbstract.IsGamePaused));
    MethodInfo suspendIntegratedServer = typeof(Vintagestory.Server.ServerMain).GetMethod(
        nameof(Vintagestory.Server.ServerMain.Suspend),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(bool), typeof(int) },
        modifiers: null)
        ?? throw new MissingMethodException(
            typeof(Vintagestory.Server.ServerMain).FullName,
            nameof(Vintagestory.Server.ServerMain.Suspend));
    if (!CallsMethod(integratedServerLoop, platformPausedGetter)
        || !CallsMethod(integratedServerLoop, suspendIntegratedServer))
    {
        throw new InvalidOperationException(
            "Vintage Story no longer routes the platform pause flag to integrated-server suspension.");
    }

    FieldInfo replaySuspensionField = replayManagerType.GetField(
        "singleplayerWorldSuspension",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            replayManagerType.FullName,
            "singleplayerWorldSuspension");
    MethodInfo finishReplayPlayback = replayManagerType.GetMethod(
        "FinishLoadedPlayback",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replayManagerType.FullName, "FinishLoadedPlayback");
    MethodInfo stopReplayForSuspension = replayManagerType.GetMethod(
        "StopPlayback",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(replayManagerType.FullName, "StopPlayback");
    MethodInfo prepareReplayFrame = replayManagerType.GetMethod(
        "PrepareFrame",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(replayManagerType.FullName, "PrepareFrame");
    if (replaySuspensionField.FieldType != replaySuspensionType
        || !CallsMethod(finishReplayPlayback, acquireReplaySuspension)
        || !CallsMethod(stopReplayForSuspension, releaseReplaySuspension)
        || !CallsMethod(prepareReplayFrame, maintainReplaySuspension))
    {
        throw new InvalidOperationException(
            "Replay playback does not own world suspension across activation, frames, and cleanup.");
    }
    Console.WriteLine("PASS private singleplayer replay suspension lifecycle");

    MethodInfo composeResources = workspaceType.GetMethod(
        "ComposeResources",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeResources");
    MethodInfo scrollResourceList = workspaceType.GetMethod(
        "TryScrollResourceList",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "TryScrollResourceList");
    Type surfaceExtensionsType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorGuiSurfaceExtensions",
        throwOnError: true)!;
    MethodInfo addRoundedOpaqueWindow = surfaceExtensionsType.GetMethod(
        "AddDirectorOpaqueWindowSurface",
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMethodException(
            surfaceExtensionsType.FullName,
            "AddDirectorOpaqueWindowSurface");
    MethodInfo composeRmlResources = workspaceType.GetMethod("ComposeRmlResources", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Type workspaceMenusType = workspaceType.Assembly.GetType("VSDirector.Gui.DirectorWorkspaceMenus", throwOnError: true)!;
    if (!CallsMethod(composeResources, composeRmlResources)
        || !CallsMethod(composeRmlResources, workspaceMenusType.GetMethod("Browser", BindingFlags.Static | BindingFlags.NonPublic)!)
        || workspaceType.GetMethod("ComposeLiveOffscreenResources", BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetField(
            "resourceMenuSection",
            BindingFlags.Instance | BindingFlags.NonPublic) is null
        || workspaceType.GetField(
            "resourceScrollOffset",
            BindingFlags.Instance | BindingFlags.NonPublic) is null
        || workspaceType.GetMethod(
            "AddPager",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetMethod(
            "ToggleResourcesDrawer",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException(
            "Workspace resources must use the native VSImGui browser without a workspace offscreen-render window.");
    }
    MethodInfo workspaceMouseMove = workspaceType.GetMethod(
        nameof(GuiDialog.OnMouseMove),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(MouseEvent) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, nameof(GuiDialog.OnMouseMove));
    MethodInfo routeResourceWheel = workspaceType.GetMethod(
        "RouteResourceMouseWheel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RouteResourceMouseWheel");
    MethodInfo composeScene = workspaceType.GetMethod("ComposeSceneWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Type sceneBrowser = workspaceType.Assembly.GetType("VSDirector.Gui.DirectorSceneBrowser", throwOnError: true)!;
    MethodInfo sceneShow = sceneBrowser.GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(sceneBrowser.FullName, "Show");
    if (workspaceType.GetMethod("UpdateResourceMenuHover", BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || !CallsMethod(composeScene, sceneShow)
        || !CallsMethod(routeResourceWheel, scrollResourceList))
    {
        throw new InvalidOperationException(
            "Workspace resources must open on click and scene resources must use the native searchable ImGui window.");
    }
    foreach ((string eventMethodName, Type eventType, string routeMethodName) in new[]
    {
        (nameof(GuiDialog.OnMouseDown), typeof(MouseEvent), "RouteResourceMouseDown"),
        (nameof(GuiDialog.OnMouseMove), typeof(MouseEvent), "RouteResourceMouseMove"),
        (nameof(GuiDialog.OnMouseUp), typeof(MouseEvent), "RouteResourceMouseUp"),
        (nameof(GuiDialog.OnMouseWheel), typeof(MouseWheelEventArgs), "RouteResourceMouseWheel")
    })
    {
        MethodInfo eventMethod = workspaceType.GetMethod(
            eventMethodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { eventType },
            modifiers: null)
            ?? throw new MissingMethodException(workspaceType.FullName, eventMethodName);
        MethodInfo routeMethod = workspaceType.GetMethod(
            routeMethodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(workspaceType.FullName, routeMethodName);
        if (!CallsMethod(eventMethod, routeMethod))
        {
            throw new InvalidOperationException(
                $"Workspace {eventMethodName} does not prioritize the resource hover menu.");
        }
    }
    Console.WriteLine("PASS rounded scrollable resource hover menus and input priority");
    MethodInfo replayVisualSettingsContain = workspaceType.GetMethod(
        "ReplayVisualSettingsContain",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "ReplayVisualSettingsContain");
    MethodInfo replayVisualMouseWheel = workspaceType.GetMethod(
        nameof(GuiDialog.OnMouseWheel),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(MouseWheelEventArgs) },
        modifiers: null)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            nameof(GuiDialog.OnMouseWheel));
    if (!CallsMethod(replayVisualMouseWheel, replayVisualSettingsContain)
        || PatchProcessor.GetCurrentInstructions(replayVisualSettingsContain).Any(instruction =>
            instruction.operand is MethodInfo method && method.Name == nameof(ElementBounds.PointInside)))
    {
        throw new InvalidOperationException(
            "Replay visual-settings wheel routing can call PointInside on an uncomposed bounds instance.");
    }
    Console.WriteLine("PASS replay visual-settings wheel routing survives uncomposed bounds");
    MethodInfo replayVisualValueConverter = workspaceType.GetMethod(
        "TryConvertReplayVisualValue",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Type), typeof(object), typeof(object).MakeByRefType() },
        modifiers: null)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "TryConvertReplayVisualValue");
    MethodInfo setReplayVisualValue = workspaceType.GetMethod(
        "SetReplayVisualValue",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "SetReplayVisualValue");
    object?[] floatConversion = { typeof(float), 70d, null };
    if (replayVisualValueConverter.Invoke(null, floatConversion) is not true
        || floatConversion[2] is not float convertedFov
        || convertedFov != 70f
        || !CallsMethod(setReplayVisualValue, replayVisualValueConverter))
    {
        throw new InvalidOperationException(
            "Replay visual FOV submission does not convert a Double input to the Single property type.");
    }
    object?[] invalidFloatConversion = { typeof(float), double.MaxValue, null };
    if (replayVisualValueConverter.Invoke(null, invalidFloatConversion) is not false)
    {
        throw new InvalidOperationException(
            "Replay visual numeric conversion accepts values that overflow the target property type.");
    }
    Console.WriteLine("PASS replay visual FOV converts Double input to Single safely");
    FieldInfo replayVisualDescriptorsField = workspaceType.GetField(
        "ReplayVisualDescriptors",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(workspaceType.FullName, "ReplayVisualDescriptors");
    if (replayVisualDescriptorsField.GetValue(null) is not System.Collections.IEnumerable
        replayVisualDescriptors)
    {
        throw new InvalidOperationException("Replay visual descriptor whitelist is unavailable.");
    }
    HashSet<string> visibleReplayVisuals = new(StringComparer.Ordinal);
    foreach (object descriptor in replayVisualDescriptors)
    {
        Type descriptorType = descriptor.GetType();
        string groupCode = descriptorType.GetProperty("GroupCode")?.GetValue(descriptor) as string
            ?? throw new InvalidOperationException("Replay visual descriptor has no group code.");
        PropertyInfo valueProperty = descriptorType.GetProperty("ValueProperty")?.GetValue(descriptor)
            as PropertyInfo
            ?? throw new InvalidOperationException("Replay visual descriptor has no value property.");
        visibleReplayVisuals.Add(groupCode + "." + valueProperty.Name);
    }
    string[] requiredVisibleReplayVisuals =
    {
        "world.ShowEntities",
        "world.HideTrackedEntity",
        "world.ShowEntityBoxes",
        "world.ShowEntityLookDirections",
        "sky.Mode",
        "environment.Weather",
        "environment.OverrideFog",
        "camera.OverrideFov",
        "camera.Fov",
        "camera.OverrideNearClip",
        "particles.ShowParticles",
        "audio.MasterVolume",
        "composition.RuleOfThirds"
    };
    if (requiredVisibleReplayVisuals.Any(item => !visibleReplayVisuals.Contains(item))
        || visibleReplayVisuals.Any(item =>
            item.StartsWith("post-processing.", StringComparison.Ordinal)
            || item.StartsWith("liquids.", StringComparison.Ordinal)
            || item.StartsWith("animations.", StringComparison.Ordinal)
            || item.StartsWith("advanced.", StringComparison.Ordinal))
        || visibleReplayVisuals.Count != 44)
    {
        throw new InvalidOperationException(
            "Replay visual sidebar is missing a practical setting or still exposes low-level settings.");
    }
    Console.WriteLine(
        $"PASS replay visual sidebar uses a practical {visibleReplayVisuals.Count}-setting whitelist");
    FieldInfo replayRightTab = workspaceType.GetField(
        "replayRightTab",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(workspaceType.FullName, "replayRightTab");
    MethodInfo composeReplayRightPanel = workspaceType.GetMethod(
        "ComposeReplayRightPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayRightPanel");
    MethodInfo composeReplayVisualsPanel = workspaceType.GetMethod(
        "ComposeReplayVisualsPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayVisualsPanel");
    MethodInfo composeReplayPropertiesPanel = workspaceType.GetMethod(
        "ComposeReplayPropertiesPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "ComposeReplayPropertiesPanel");
    MethodInfo composeReplayMetadataFields = workspaceType.GetMethod(
        "ComposeReplayMetadataFields",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayMetadataFields");
    MethodInfo addLabeledReplayMetadataInput = workspaceType.GetMethod(
        "AddLabeledInput",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "AddLabeledInput");
    MethodInfo autoSaveReplayMetadataOnChanged = workspaceType.GetMethod(
        "AutoSaveOnChanged",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "AutoSaveOnChanged");
    MethodInfo composeCategorizedReplayInspector = workspaceType.GetMethod(
        "ComposeCategorizedReplayInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "ComposeCategorizedReplayInspector");
    MethodInfo composeReplayTimelineEditor = workspaceType.GetMethod(
        "ComposeReplayTimelineEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayTimelineEditor");
    MethodInfo composeReplayPreloadEditor = workspaceType.GetMethod(
        "ComposePreloadEditorEntry",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePreloadEditorEntry");
    MethodInfo composeReplayActorControls = workspaceType.GetMethod(
        "ComposeReplayActorControls",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayActorControls");
    MethodInfo setReplayRightTab = workspaceType.GetMethod(
        "SetReplayRightTab",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "SetReplayRightTab");
    MethodInfo preserveWorkspaceInputs = workspaceType.GetMethod(
        "PreserveInputs",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "PreserveInputs");
    MethodInfo composeWorkspaceInspector = workspaceType.GetMethod(
        "ComposeInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeInspector");
    MethodInfo tryAutoSaveReplayMetadata = workspaceType.GetMethod(
        "TryAutoSaveReplayMetadata",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "TryAutoSaveReplayMetadata");
    Type replayMenuButtonExtensions = workspaceType.Assembly.GetType(
        "VSDirector.Gui.DirectorGuiButtonExtensions",
        throwOnError: true)!;
    MethodInfo addDirectorToolbarButton = replayMenuButtonExtensions.GetMethod(
        "AddDirectorToolbarButton",
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMethodException(
            replayMenuButtonExtensions.FullName,
            "AddDirectorToolbarButton");
    if (replayRightTab.FieldType != typeof(string)
        || !CallsMethodReference(composeReplayRightPanel, addDirectorToolbarButton)
        || !CallsMethod(composeReplayRightPanel, composeReplayVisualsPanel)
        || !CallsMethod(composeReplayRightPanel, composeReplayPropertiesPanel)
        || !CallsMethod(composeReplayPropertiesPanel, composeReplayMetadataFields)
        || CallsMethod(composeReplayPropertiesPanel, composeCategorizedReplayInspector)
        || CallsMethod(composeReplayPropertiesPanel, composeReplayTimelineEditor)
        || CallsMethod(composeReplayPropertiesPanel, composeReplayPreloadEditor)
        || CallsMethod(composeReplayPropertiesPanel, composeReplayActorControls)
        || !CallsMethod(setReplayRightTab, preserveWorkspaceInputs)
        || !CallsMethod(setReplayRightTab, composeWorkspaceInspector)
        || !CallsMethod(composeReplayMetadataFields, addLabeledReplayMetadataInput)
        || !CallsMethod(addLabeledReplayMetadataInput, autoSaveReplayMetadataOnChanged)
        || !PatchProcessor.GetCurrentInstructions(tryAutoSaveReplayMetadata).Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name == "TryUpdateReplayMetadata"))
    {
        throw new InvalidOperationException(
            "Replay right panel menu does not expose the input-only auto-saved properties view.");
    }
    Console.WriteLine("PASS replay right panel uses menu tabs and input-only auto-saved properties");
    MethodInfo addInspectorHeader = workspaceType.GetMethod(
        "AddInspectorHeader",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "AddInspectorHeader");
    MethodInfo composeInspectorSections = workspaceType.GetMethod(
        "ComposeInspectorSections",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeInspectorSections");
    FieldInfo cameraRightTab = workspaceType.GetField(
        "cameraRightTab",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(workspaceType.FullName, "cameraRightTab");
    MethodInfo composeCameraRightPanel = workspaceType.GetMethod(
        "ComposeCameraInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeCameraInspector");
    string[] cameraInspectorKeys = PatchProcessor
        .GetCurrentInstructions(composeCameraRightPanel)
        .Select(instruction => instruction.operand)
        .OfType<string>()
        .ToArray();
    if (cameraRightTab.FieldType != typeof(string)
        || !CallsMethodReference(composeCameraRightPanel, addDirectorToolbarButton)
        || CallsMethod(composeCameraRightPanel, addInspectorHeader)
        || CallsMethod(composeCameraRightPanel, composeInspectorSections)
        || new[] { "cameraName", "cameraTags", "cameraDescription" }
            .Any(key => !cameraInspectorKeys.Contains(key, StringComparer.Ordinal))
        || new[] { "cameraPosX", "cameraYaw", "cameraFov", "cameraSlot", "deleteCamera" }
            .Any(key => !cameraInspectorKeys.Contains(key, StringComparer.Ordinal)))
    {
        throw new InvalidOperationException(
            "Camera sidebar does not separate view controls from metadata properties.");
    }
    Console.WriteLine("PASS camera sidebar uses view and properties tabs without summary text");

    FieldInfo followRightTab = workspaceType.GetField(
        "followRightTab",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(workspaceType.FullName, "followRightTab");
    MethodInfo composeFollowRightPanel = workspaceType.GetMethod(
        "ComposeCategorizedFollowInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "ComposeCategorizedFollowInspector");
    MethodInfo addFollowDropDowns = workspaceType.GetMethod(
        "AddFollowDropDowns",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "AddFollowDropDowns");
    string[] followInspectorKeys = PatchProcessor
        .GetCurrentInstructions(composeFollowRightPanel)
        .Select(instruction => instruction.operand)
        .OfType<string>()
        .ToArray();
    if (followRightTab.FieldType != typeof(string)
        || !CallsMethodReference(composeFollowRightPanel, addDirectorToolbarButton)
        || CallsMethod(composeFollowRightPanel, addInspectorHeader)
        || CallsMethod(composeFollowRightPanel, composeInspectorSections)
        || !CallsMethod(composeFollowRightPanel, addFollowDropDowns)
        || new[] { "followName", "followTags", "followDescription" }
            .Any(key => !followInspectorKeys.Contains(key, StringComparer.Ordinal))
        || new[] { "followTarget", "followOffsetX", "activateFollow" }
            .Any(key => !followInspectorKeys.Contains(key, StringComparer.Ordinal))
        || followInspectorKeys.Contains("followVisualStatus", StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            "Follow sidebar does not separate view controls from metadata properties.");
    }
    Console.WriteLine("PASS follow sidebar uses view and metadata properties without status text");

    MethodInfo replayMetadataSearch = workspaceType.GetMethod(
        "SearchMatch",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(string), typeof(string), typeof(IReadOnlyList<string>) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, "SearchMatch");
    MethodInfo composeReplayResources = workspaceType.GetMethod(
        "ComposeReplayResources",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayResources");
    MethodInfo composeReplayResourceBlock = workspaceType.GetMethod(
        "ComposeReplayResourceBlock",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayResourceBlock");
    bool HasReplayMetadataSearchLambda(string composerName)
        => workspaceType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(method => method.Name.StartsWith(
                    $"<{composerName}>b__",
                    StringComparison.Ordinal)
                && CallsMethod(method, replayMetadataSearch));
    if (!HasReplayMetadataSearchLambda(composeReplayResources.Name)
        || !HasReplayMetadataSearchLambda(composeReplayResourceBlock.Name))
    {
        throw new InvalidOperationException(
            "Replay resource search is not applied in both resource layouts.");
    }
    Console.WriteLine("PASS replay resource search filters both resource layouts");

    FieldInfo pathRightTab = workspaceType.GetField(
        "pathRightTab",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(workspaceType.FullName, "pathRightTab");
    MethodInfo composePathRightPanel = workspaceType.GetMethod(
        "ComposeCategorizedPathInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeCategorizedPathInspector");
    MethodInfo composePathVisualsPanel = workspaceType.GetMethod(
        "ComposePathVisualsPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePathVisualsPanel");
    MethodInfo composePathPropertiesPanel = workspaceType.GetMethod(
        "ComposePathPropertiesPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePathPropertiesPanel");
    MethodInfo composePathResources = workspaceType.GetMethod(
        "ComposePathResources",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePathResources");
    MethodInfo selectPathKeyframeFromList = workspaceType.GetMethod(
        "SelectPathKeyframeFromList",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "SelectPathKeyframeFromList");
    MethodInfo addAutoSavePathNumberRow = workspaceType.GetMethod(
        "AddAutoSavePathInspectorNumberRow",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "AddAutoSavePathInspectorNumberRow");
    MethodInfo savePathEditor = workspaceType.GetMethod(
        "SavePathEditor",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, "SavePathEditor");
    MethodInfo playEditedPath = workspaceType.GetMethod(
        "PlayEditedPath",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "PlayEditedPath");
    MethodInfo deletePathEditor = workspaceType.GetMethod(
        "DeletePathEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "DeletePathEditor");
    MethodInfo tryAutoSavePath = workspaceType.GetMethod(
        "TryAutoSavePath",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "TryAutoSavePath");
    if (pathRightTab.FieldType != typeof(string)
        || !CallsMethodReference(composePathRightPanel, addDirectorToolbarButton)
        || !CallsMethod(composePathRightPanel, composePathVisualsPanel)
        || !CallsMethod(composePathRightPanel, composePathPropertiesPanel)
        || CallsMethod(composePathResources, selectPathKeyframeFromList)
        || !CallsMethod(composePathVisualsPanel, addAutoSavePathNumberRow)
        || !CallsMethod(composePathPropertiesPanel, addLabeledReplayMetadataInput)
        || CallsMethod(composePathPropertiesPanel, savePathEditor)
        || CallsMethod(composePathPropertiesPanel, playEditedPath)
        || CallsMethod(composePathPropertiesPanel, deletePathEditor)
        || !PatchProcessor.GetCurrentInstructions(composePathVisualsPanel).Any(instruction =>
            instruction.operand is MethodInfo method
            && method == deletePathEditor)
        || !PatchProcessor.GetCurrentInstructions(tryAutoSavePath).Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name == "TrySavePath"))
    {
        throw new InvalidOperationException(
            "Path sidebar still exposes the keyframe resource list or command-heavy inspector.");
    }
    Console.WriteLine("PASS path sidebar uses menu tabs, auto-save properties, and no keyframe resource list");
    MethodInfo composeReplayVisualSettings = workspaceType.GetMethod(
        "ComposeReplayVisualSettings",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayVisualSettings");
    MethodInfo buildReplayVisualRows = workspaceType.GetMethod(
        "BuildReplayVisualRows",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "BuildReplayVisualRows");
    MethodInfo addReplayPreloadEditorRow = workspaceType.GetMethod(
        "AddReplayPreloadEditorRow",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "AddReplayPreloadEditorRow");
    MethodInfo openReplayPreloadEditor = workspaceType.GetMethod(
        "OpenReplayPreloadEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "OpenReplayPreloadEditor");
    MethodInfo canManageChunkPreload = workspaceType.GetProperty(
        "CanManageChunkPreload",
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod
        ?? throw new MissingMethodException(workspaceType.FullName, "get_CanManageChunkPreload");
    MethodInfo composeWorkflowCanvasForPreload = workspaceType.GetMethod(
        "ComposeWorkflowCanvas",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeWorkflowCanvas");
    MethodInfo requestReplayPreloadEditor = typeof(VSDirectorModSystem).GetMethod(
        "RequestReplayPreloadEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "RequestReplayPreloadEditor");
    MethodInfo hasDirectorPermission = typeof(VSDirectorModSystem).GetMethod(
        "HasPermission",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "HasPermission");
    if (!CallsMethod(composeReplayVisualSettings, buildReplayVisualRows)
        || !CallsMethod(composeReplayVisualSettings, addReplayPreloadEditorRow)
        || !CallsMethod(buildReplayVisualRows, canManageChunkPreload)
        || !CallsMethod(canManageChunkPreload, hasDirectorPermission)
        || !CallsMethod(openReplayPreloadEditor, requestReplayPreloadEditor)
        || !CallsMethod(openReplayPreloadEditor, composeWorkflowCanvasForPreload)
        || !CallsMethod(requestReplayPreloadEditor, hasDirectorPermission))
    {
        throw new InvalidOperationException(
            "Replay world/entity settings do not expose the permission-gated preload editor.");
    }
    Type settingsDialogWithoutAdvanced = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorSettingsDialog",
        throwOnError: true)!;
    if (settingsDialogWithoutAdvanced.GetMethod(
            "ComposeAdvancedFeatures",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException(
            "Archived preload and multi-view switches remain exposed through advanced settings.");
    }
    Console.WriteLine("PASS replay preload editor entry and archived advanced switches");
    string[] removedReplayVisualTypes =
    {
        "VSDirector.Data.DirectorReplayPostProcessingVisuals",
        "VSDirector.Data.DirectorReplayLiquidVisuals",
        "VSDirector.Data.DirectorReplayAnimationVisuals",
        "VSDirector.Data.DirectorReplayAdvancedVisuals",
        "VSDirector.Data.DirectorReplayAspectMode",
        "VSDirector.Data.DirectorReplayAspectRatio"
    };
    Assembly directorAssembly = typeof(DirectorReplayVisualSettings).Assembly;
    if (removedReplayVisualTypes.Any(typeName => directorAssembly.GetType(typeName) is not null)
        || workspaceType.GetField(
            "replayVisualPreset",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetMethod(
            "ApplyReplayVisualPreset",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetField(
            "expandedReplayVisualGroups",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetMethod(
            "ToggleReplayVisualGroup",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException(
            "Replay visual presets, collapse controls, or removed low-level visual types are still present.");
    }
    Console.WriteLine("PASS replay visual presets, collapse controls, and low-level groups are removed");
    Type replayVisualEnvironmentType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayVisualEnvironmentController",
        throwOnError: true)!;
    MethodInfo applyReplayVisualEnvironment = replayVisualEnvironmentType.GetMethod(
        "Apply",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replayVisualEnvironmentType.FullName, "Apply");
    MethodInfo restoreReplayVisualEnvironment = replayVisualEnvironmentType.GetMethod(
        "Restore",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(replayVisualEnvironmentType.FullName, "Restore");
    if (typeof(DirectorReplayVisualSettings).GetProperty("UseRecordedVisuals") is not null)
    {
        throw new InvalidOperationException(
            "Replay visual settings still expose the removed recorded-original master switch.");
    }
    Type playerCameraPatchType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.PlayerCameraBeforeRenderPatch",
        throwOnError: true)!;
    MethodInfo playerCameraPatch = playerCameraPatchType.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(playerCameraPatchType.FullName, "Prefix");
    MethodInfo overrideNearClipGetter = typeof(DirectorReplayCameraVisuals).GetProperty(
        nameof(DirectorReplayCameraVisuals.OverrideNearClip))?.GetMethod
        ?? throw new MissingMethodException(
            typeof(DirectorReplayCameraVisuals).FullName,
            "get_OverrideNearClip");
    if (CallsMethod(applyReplayVisualEnvironment, restoreReplayVisualEnvironment)
        || !CallsMethod(playerCameraPatch, overrideNearClipGetter))
    {
        throw new InvalidOperationException(
            "Replay visual environment still has a master bypass or near-clip mutation is not guarded.");
    }
    Console.WriteLine("PASS replay visuals use per-setting overrides and near-clip mutation remains guarded");
    if (workspaceType.GetField(
            "consumeWorkflowContextMouseUp",
            BindingFlags.Instance | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException("Workflow context menu does not guard clicks outside its overlay.");
    }
    Console.WriteLine("PASS workflow context-menu click-through guard");
    MethodInfo workflowCardBackground = workspaceType.GetMethod(
        "WorkflowCardBackground",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "WorkflowCardBackground");
    DirectorWorkflowCard pathCard = new()
    {
        Kind = DirectorWorkflowCardKind.Shot,
        SourceType = DirectorRecordingSourceType.Path
    };
    DirectorWorkflowCard countdownCard = new() { Kind = DirectorWorkflowCardKind.Countdown };
    int pathFill = (int)workflowCardBackground.Invoke(null, new object[] { pathCard, false })!;
    int selectedPathFill = (int)workflowCardBackground.Invoke(null, new object[] { pathCard, true })!;
    int countdownFill = (int)workflowCardBackground.Invoke(null, new object[] { countdownCard, false })!;
    if (pathFill != ColorUtil.ToRgba(255, 132, 101, 35)
        || selectedPathFill != ColorUtil.ToRgba(255, 171, 132, 46)
        || countdownFill != ColorUtil.ToRgba(255, 132, 82, 34))
    {
        throw new InvalidOperationException("Workflow timeline cards are not using the solid category fills.");
    }
    Type solidTextureRendererType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorSolidTextureRenderer",
        throwOnError: true)!;
    MethodInfo renderSolidTexture = solidTextureRendererType.GetMethod(
        "Render",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(solidTextureRendererType.FullName, "Render");
    bool solidRendererUsesTextureQuad = typeof(IRenderAPI).GetMethods()
        .Where(method => method.Name == nameof(IRenderAPI.Render2DTexturePremultipliedAlpha))
        .Any(method => CallsMethodReference(renderSolidTexture, method));
    bool solidRendererUsesRectangleOutline = typeof(IRenderAPI).GetMethods()
        .Where(method => method.Name == nameof(IRenderAPI.RenderRectangle))
        .Any(method => CallsMethodReference(renderSolidTexture, method));
    MethodInfo renderSolidWorkflowRect = workspaceType.GetMethod(
        "RenderSolidWorkflowRect",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RenderSolidWorkflowRect");
    foreach (string methodName in new[]
    {
        "RenderWorkflowBoard",
        "RenderReplayTimeline",
        "RenderReplaySequenceTimeline",
        "RenderReplayPathViewTimeline",
        "RenderActionTimeline",
        "RenderPathTimeline",
        "RenderPathGlobalTimeline",
        "RenderTimelineBase"
    })
    {
        MethodInfo timelineRenderer = workspaceType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(workspaceType.FullName, methodName);
        if (!CallsMethod(timelineRenderer, renderSolidWorkflowRect)
            && !CallsMethodReference(timelineRenderer, renderSolidTexture))
        {
            throw new InvalidOperationException(
                $"{methodName} does not render its cards or timeline items as solid texture quads.");
        }
    }
    if (!solidRendererUsesTextureQuad
        || solidRendererUsesRectangleOutline
        || !CallsMethodReference(renderSolidWorkflowRect, renderSolidTexture))
    {
        throw new InvalidOperationException(
            "Workflow cards and timeline items are not using the Cairo-backed solid texture renderer.");
    }
    Console.WriteLine("PASS workflow cards and timeline items use solid texture fills");
    Type floatingEditorType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorWorkspaceFloatingEditorDialog",
        throwOnError: true)!;
    MethodInfo setFloatingEditorComposer = floatingEditorType.GetMethod(
        "SetComposer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(floatingEditorType.FullName, "SetComposer");
    MethodInfo removeFloatingEditorComposer = floatingEditorType.GetMethod(
        "RemoveComposer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(floatingEditorType.FullName, "RemoveComposer");
    MethodInfo openFloatingEditorForWorkspace = floatingEditorType.GetMethod(
        "OpenForWorkspace",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(floatingEditorType.FullName, "OpenForWorkspace");
    MethodInfo floatingMouseDown = floatingEditorType.GetMethod(
        nameof(GuiDialog.OnMouseDown),
        BindingFlags.Instance | BindingFlags.Public,
        null,
        new[] { typeof(MouseEvent) },
        null)!;
    MethodInfo floatingMouseMove = floatingEditorType.GetMethod(
        nameof(GuiDialog.OnMouseMove),
        BindingFlags.Instance | BindingFlags.Public,
        null,
        new[] { typeof(MouseEvent) },
        null)!;
    MethodInfo floatingMouseUp = floatingEditorType.GetMethod(
        nameof(GuiDialog.OnMouseUp),
        BindingFlags.Instance | BindingFlags.Public,
        null,
        new[] { typeof(MouseEvent) },
        null)!;
    MethodInfo floatingMouseWheel = floatingEditorType.GetMethod(
        nameof(GuiDialog.OnMouseWheel),
        BindingFlags.Instance | BindingFlags.Public,
        null,
        new[] { typeof(MouseWheelEventArgs) },
        null)!;
    MethodInfo routeFloatingMouseDown = workspaceType.GetMethod(
        "RouteFloatingEditorMouseDown",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RouteFloatingEditorMouseDown");
    MethodInfo routeFloatingMouseMove = workspaceType.GetMethod(
        "RouteFloatingEditorMouseMove",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RouteFloatingEditorMouseMove");
    MethodInfo routeFloatingMouseUp = workspaceType.GetMethod(
        "RouteFloatingEditorMouseUp",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RouteFloatingEditorMouseUp");
    MethodInfo routeFloatingMouseWheel = workspaceType.GetMethod(
        "RouteFloatingEditorMouseWheel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RouteFloatingEditorMouseWheel");
    MethodInfo activeFloatingPointer = workspaceType.GetMethod(
        "HasActiveFloatingPointerInteraction",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            workspaceType.FullName,
            "HasActiveFloatingPointerInteraction");
    MethodInfo resolvePreloadReplay = workspaceType.GetMethod(
        "ResolvePreloadEditorReplayInfo",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ResolvePreloadEditorReplayInfo");
    MethodInfo preloadTargetAvailable = workspaceType.GetMethod(
        "HasPreloadEditorTarget",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "HasPreloadEditorTarget");
    MethodInfo preloadGridState = workspaceType.GetMethod(
        "TryGetPreloadGridState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "TryGetPreloadGridState");
    MethodInfo preloadReplayInfo = workspaceType.GetMethod(
        "PreloadEditorReplayInfo",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "PreloadEditorReplayInfo");
    MethodInfo composeWorkflowCanvas = workspaceType.GetMethod(
        "ComposeWorkflowCanvas",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeWorkflowCanvas");
    MethodInfo removeFloatingEditor = workspaceType.GetMethod(
        "RemoveFloatingEditorComposer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "RemoveFloatingEditorComposer");
    MethodInfo composePreloadCanvas = workspaceType.GetMethod(
        "ComposePreloadCanvas",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePreloadCanvas");
    MethodInfo composeToolbar = workspaceType.GetMethod(
        "ComposeToolbar",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeToolbar");
    MethodInfo composePreview = workspaceType.GetMethod(
        "ComposePreview",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePreview");
    MethodInfo composeInspector = workspaceType.GetMethod(
        "ComposeInspector",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeInspector");
    MethodInfo composeTimeline = workspaceType.GetMethod(
        "ComposeTimeline",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeTimeline");
    MethodInfo composeReplayFloatingEditor = workspaceType.GetMethod(
        "ComposeReplayFloatingEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeReplayFloatingEditor");
    MethodInfo composeActionTimelinePanel = workspaceType.GetMethod(
        "ComposeActionTimelinePanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeActionTimelinePanel");
    MethodInfo composeActionLibraryCanvas = workspaceType.GetMethod(
        "ComposeActionLibraryCanvas",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposeActionLibraryCanvas");
    MethodInfo composePathFloatingEditor = workspaceType.GetMethod(
        "ComposePathFloatingEditor",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ComposePathFloatingEditor");
    MethodInfo composeMethod = AccessTools.Method(
        typeof(GuiComposer),
        nameof(GuiComposer.Compose),
        new[] { typeof(bool) })
        ?? throw new MissingMethodException(typeof(GuiComposer).FullName, nameof(GuiComposer.Compose));
    MethodInfo[] workspaceComposerMethods =
    {
        workspaceType.GetMethod("EmptyMenuComposer", BindingFlags.Instance | BindingFlags.NonPublic)!,
        composePreview,
        composeInspector,
        composeTimeline,
        composeReplayFloatingEditor,
        composeActionTimelinePanel,
        composeActionLibraryCanvas,
        composePathFloatingEditor
    };
    if (workspaceComposerMethods.Any(method =>
            !AllComposeCallsDisableFirstFocus(method, composeMethod)))
    {
        throw new InvalidOperationException(
            "Workspace composers still auto-focus their first control during composition.");
    }
    Console.WriteLine("PASS workspace composers do not auto-focus their first control");
    MethodInfo workspaceOpened = workspaceType.GetMethod(
        nameof(GuiDialog.OnGuiOpened),
        BindingFlags.Instance | BindingFlags.Public)!;
    MethodInfo composerMouseMove = typeof(GuiComposer).GetMethod(
        nameof(GuiComposer.OnMouseMove),
        new[] { typeof(MouseEvent) })!;
    MethodInfo composerMouseUp = typeof(GuiComposer).GetMethod(
        nameof(GuiComposer.OnMouseUp),
        new[] { typeof(MouseEvent) })!;
    MethodInfo composerMouseWheel = typeof(GuiComposer).GetMethod(
        nameof(GuiComposer.OnMouseWheel),
        new[] { typeof(MouseWheelEventArgs) })!;
    MethodInfo dialogTryClose = typeof(GuiDialog).GetMethod(nameof(GuiDialog.TryClose), Type.EmptyTypes)!;
    MethodInfo dialogTryOpenFocused = typeof(GuiDialog).GetMethod(
        nameof(GuiDialog.TryOpen),
        new[] { typeof(bool) })!;
    MethodInfo requestDialogFocus = typeof(IGuiAPI).GetMethod(
        nameof(IGuiAPI.RequestFocus),
        new[] { typeof(GuiDialog) })!;
    MethodInfo workspaceDrawOrderGetter = workspaceType.GetProperty(
        nameof(GuiDialog.DrawOrder),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingDrawOrderGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.DrawOrder),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingInputOrderGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.InputOrder),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingDialogTypeGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.DialogType),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingUnregisterGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.UnregisterOnClose),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo dialogPrefersUngrabbedGetter = typeof(GuiDialog).GetProperty(
        nameof(GuiDialog.PrefersUngrabbedMouse),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo dialogDisableMouseGrabGetter = typeof(GuiDialog).GetProperty(
        nameof(GuiDialog.DisableMouseGrab),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingPrefersUngrabbedGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.PrefersUngrabbedMouse),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    MethodInfo floatingDisableMouseGrabGetter = floatingEditorType.GetProperty(
        nameof(GuiDialog.DisableMouseGrab),
        BindingFlags.Instance | BindingFlags.Public)!.GetMethod!;
    Type preloadReplayInfoType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayInfo",
        throwOnError: true)!;
    Guid activePreloadReplayId = Guid.NewGuid();
    object selectedPreloadReplay = Activator.CreateInstance(
        preloadReplayInfoType,
        "selected.vsreplay",
        Guid.NewGuid(),
        "Selected",
        DateTimeOffset.UtcNow,
        10d,
        1)!;
    object activePreloadReplay = Activator.CreateInstance(
        preloadReplayInfoType,
        "active.vsreplay",
        activePreloadReplayId,
        "Active",
        DateTimeOffset.UtcNow,
        10d,
        1)!;
    Array preloadReplays = Array.CreateInstance(preloadReplayInfoType, 2);
    preloadReplays.SetValue(selectedPreloadReplay, 0);
    preloadReplays.SetValue(activePreloadReplay, 1);
    object? selectedResolution = resolvePreloadReplay.Invoke(
        null,
        new object?[] { selectedPreloadReplay, activePreloadReplayId, preloadReplays });
    object? activeResolution = resolvePreloadReplay.Invoke(
        null,
        new object?[] { null, activePreloadReplayId, preloadReplays });
    object? emptyResolution = resolvePreloadReplay.Invoke(
        null,
        new object?[] { null, Guid.Empty, preloadReplays });
    Type preloadWindowType = directorAssembly.GetType("VSDirector.Gui.DirectorPreloadRmlWindow", throwOnError: true)!;
    MethodInfo preloadOpen = preloadWindowType.GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!;
    MethodInfo preloadRefresh = preloadWindowType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
    if (!CallsMethodReference(composePreloadCanvas, preloadOpen)
        || !CallsMethodReference(composePreloadCanvas, preloadRefresh)
        || CallsMethodReference(composePreloadCanvas, setFloatingEditorComposer)
        || directorAssembly.GetType("VSDirector.Runtime.DirectorChunkPreloadVisualization") is not null)
        throw new InvalidOperationException("Preload editing must use VSImGui without world chunk-volume rendering.");
    Console.WriteLine("PASS preload editor opens and refreshes VSImGui without legacy canvas or world volumes");
    object workspaceStub = RuntimeHelpers.GetUninitializedObject(workspaceType);
    object floatingEditorStub = RuntimeHelpers.GetUninitializedObject(floatingEditorType);
    List<CodeInstruction> openFloatingInstructions =
        PatchProcessor.GetCurrentInstructions(openFloatingEditorForWorkspace).ToList();
    int openFloatingCall = openFloatingInstructions.FindIndex(instruction =>
        instruction.Calls(dialogTryOpenFocused));
    int openFloatingArgument = openFloatingCall - 1;
    while (openFloatingArgument >= 0
        && openFloatingInstructions[openFloatingArgument].opcode
            == System.Reflection.Emit.OpCodes.Nop)
    {
        openFloatingArgument--;
    }
    bool opensWithoutFocus = openFloatingArgument >= 0
        && openFloatingInstructions[openFloatingArgument].opcode
            == System.Reflection.Emit.OpCodes.Ldc_I4_0;
    if ((double)workspaceDrawOrderGetter.Invoke(workspaceStub, null)! != 0.34d
        || (double)floatingDrawOrderGetter.Invoke(floatingEditorStub, null)! != 0.885d
        || (double)floatingInputOrderGetter.Invoke(floatingEditorStub, null)! != 0.5d
        || floatingDialogTypeGetter.Invoke(floatingEditorStub, null) is not EnumDialogType.HUD
        || floatingUnregisterGetter.Invoke(floatingEditorStub, null) is not true
        || !CallsMethodReference(
            floatingPrefersUngrabbedGetter,
            dialogPrefersUngrabbedGetter)
        || !CallsMethodReference(
            floatingDisableMouseGrabGetter,
            dialogDisableMouseGrabGetter)
        || !opensWithoutFocus
        || !CallsMethodReference(workspaceOpened, openFloatingEditorForWorkspace)
        || !CallsMethodReference(composeWorkflowCanvas, setFloatingEditorComposer)
        || !CallsMethodReference(removeFloatingEditor, removeFloatingEditorComposer)
        || !CallsMethodReference(floatingMouseDown, guiComposerMouseDown)
        || !CallsMethodReference(floatingMouseDown, routeFloatingMouseDown)
        || !CallsMethodReference(floatingMouseMove, composerMouseMove)
        || !CallsMethodReference(floatingMouseMove, routeFloatingMouseMove)
        || !CallsMethodReference(floatingMouseMove, activeFloatingPointer)
        || !CallsMethodReference(floatingMouseUp, composerMouseUp)
        || !CallsMethodReference(floatingMouseUp, routeFloatingMouseUp)
        || !CallsMethodReference(floatingMouseUp, activeFloatingPointer)
        || !CallsMethodReference(floatingMouseWheel, composerMouseWheel)
        || !CallsMethodReference(floatingMouseWheel, routeFloatingMouseWheel)
        || CallsMethodReference(setFloatingEditorComposer, dialogTryOpenFocused)
        || CallsMethodReference(setFloatingEditorComposer, dialogTryClose)
        || CallsMethodReference(setFloatingEditorComposer, requestDialogFocus)
        || CallsMethodReference(removeFloatingEditorComposer, dialogTryOpenFocused)
        || CallsMethodReference(removeFloatingEditorComposer, dialogTryClose)
        || CallsMethodReference(removeFloatingEditorComposer, requestDialogFocus)
        || CallsMethodReference(openFloatingEditorForWorkspace, dialogTryClose)
        || CallsMethodReference(openFloatingEditorForWorkspace, requestDialogFocus)
        || workspaceType.GetMethod(
            "RefreshDrawOrderRegistration",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || workspaceType.GetMethod(
            "SetFloatingEditorVisible",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || !ReferenceEquals(selectedResolution, selectedPreloadReplay)
        || !ReferenceEquals(activeResolution, activePreloadReplay)
        || emptyResolution is not null
        || !CallsMethod(preloadTargetAvailable, preloadReplayInfo)
        || !CallsMethod(preloadGridState, preloadReplayInfo))
    {
        throw new InvalidOperationException(
            "Floating editors are not isolated at the top input layer, can recenter the mouse, or replay preload editing does not resolve the active replay.");
    }
    Console.WriteLine(
        "PASS floating editors use an independent top layer without reopen/focus and active replay preload editing resolves");
    MethodInfo workspacePointerInteraction = workspaceType.GetProperty(
        "ShouldHandleWorkspacePointerInteraction",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!;
    MethodInfo workspacePointerMouseUp = workspaceType.GetMethod(
        nameof(GuiDialog.OnMouseUp),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: new[] { typeof(MouseEvent) },
        modifiers: null)
        ?? throw new MissingMethodException(workspaceType.FullName, nameof(GuiDialog.OnMouseUp));
    MethodInfo resetWorkspacePointer = workspaceType.GetMethod(
        "ResetWorkspacePointerInteraction",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(workspaceType.FullName, "ResetWorkspacePointerInteraction");
    string[] pointerFlags =
    {
        "pathTimelinePanning",
        "pathTimelineScrubbing",
        "pathElementTrackReordering",
        "replayElementTrackReordering",
        "replayTimelinePanning",
        "scrubbingReplay",
        "rotatingActionPreview",
        "paneSplitDragging"
    };
    string[] pointerModes =
    {
        "pathTimelineDragKind",
        "replayExportRangeDragMode",
        "replayAuxiliaryKeyframeDragKind",
        "replayViewportDragMode",
        "replaySplitDragTarget"
    };
    List<FieldInfo> pointerFields = [];
    foreach (string fieldName in pointerFlags)
    {
        FieldInfo field = workspaceType.GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(workspaceType.FullName, fieldName);
        pointerFields.Add(field);
        field.SetValue(workspaceStub, true);
        if ((bool)activeFloatingPointer.Invoke(workspaceStub, null)!)
        {
            field.SetValue(workspaceStub, false);
        }
        else
        {
            throw new InvalidOperationException(
                $"Floating pointer capture ignores workspace flag {fieldName}.");
        }
    }
    foreach (string fieldName in pointerModes)
    {
        FieldInfo field = workspaceType.GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(workspaceType.FullName, fieldName);
        Array values = Enum.GetValues(field.FieldType);
        if (values.Length < 2)
        {
            throw new InvalidOperationException($"Pointer mode {fieldName} has no active value.");
        }
        pointerFields.Add(field);
        field.SetValue(workspaceStub, values.GetValue(1));
        if ((bool)activeFloatingPointer.Invoke(workspaceStub, null)!)
        {
            field.SetValue(workspaceStub, values.GetValue(0));
        }
        else
        {
            throw new InvalidOperationException(
                $"Floating pointer capture ignores workspace mode {fieldName}.");
        }
    }
    if (!CallsMethodReference(workspaceMouseMove, workspacePointerInteraction)
        || !CallsMethodReference(workspacePointerMouseUp, workspacePointerInteraction))
    {
        throw new InvalidOperationException(
            "Floating-editor routed pointer events do not reach active workspace interaction handling.");
    }
    foreach (FieldInfo field in pointerFields)
    {
        if (field.FieldType == typeof(bool))
        {
            field.SetValue(workspaceStub, true);
        }
        else
        {
            Array values = Enum.GetValues(field.FieldType);
            field.SetValue(workspaceStub, values.GetValue(1));
        }
    }
    resetWorkspacePointer.Invoke(workspaceStub, null);
    if ((bool)activeFloatingPointer.Invoke(workspaceStub, null)!)
    {
        string activeFields = string.Join(
            ", ",
            pointerFields
                .Where(field => field.FieldType == typeof(bool)
                    ? (bool)field.GetValue(workspaceStub)!
                    : Convert.ToInt32(field.GetValue(workspaceStub)) != 0)
                .Select(field => field.Name));
        throw new InvalidOperationException(
            "Workspace pointer interaction reset leaves a floating pointer capture active: "
            + activeFields);
    }
    Console.WriteLine("PASS floating editor routing covers and resets timeline pointer interactions");
    MethodInfo activateReplayPath = typeof(VSDirectorModSystem).GetMethod(
        "TryActivateReplayPath",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "TryActivateReplayPath");
    MethodInfo activateReplayTimelinePath = typeof(VSDirectorModSystem).GetMethod(
        "TryActivateReplayTimelinePath",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryActivateReplayTimelinePath");
    MethodInfo updateReplayTimelinePath = typeof(VSDirectorModSystem).GetMethod(
        "UpdateReplayTimelinePathFollow",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "UpdateReplayTimelinePathFollow");
    MethodInfo onClientTick = typeof(VSDirectorModSystem).GetMethod(
        "OnClientTick",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "OnClientTick");
    MethodInfo resolveReplayPathTime = typeof(DirectorReplayPathTimeline).GetMethod(
        nameof(DirectorReplayPathTimeline.ResolvePathTime),
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMethodException(
            typeof(DirectorReplayPathTimeline).FullName,
            nameof(DirectorReplayPathTimeline.ResolvePathTime));
    if (!CallsMethod(activateReplayPath, activateReplayTimelinePath)
        || !CallsMethod(onClientTick, updateReplayTimelinePath)
        || !CallsMethod(activateReplayTimelinePath, resolveReplayPathTime))
    {
        throw new InvalidOperationException(
            "Replay path views do not stay synchronized to absolute replay time.");
    }
    Console.WriteLine("PASS replay path views stay synchronized after reconstruction");
    Type settingsDialogType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorSettingsDialog",
        throwOnError: true)!;
    MethodInfo settingsDraw = settingsDialogType.GetMethod(
        "Draw",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(settingsDialogType.FullName, "Draw");
    MethodInfo settingsOnDraw = settingsDialogType.GetMethod(
        "OnDraw",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(settingsDialogType.FullName, "OnDraw");
    MethodInfo settingsFooter = settingsDialogType.GetMethod(
        "DrawFooter",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(settingsDialogType.FullName, "DrawFooter");
    MethodInfo settingsDrawSetting = settingsDialogType.GetMethod(
        "DrawSetting",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(settingsDialogType.FullName, "DrawSetting");
    MethodInfo settingsPage = settingsDialogType.GetMethod(
        "DrawCategories",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(settingsDialogType.FullName, "DrawCategories");
    MethodInfo? legacyCompose = settingsDialogType.GetMethod(
        "Compose",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    MethodInfo? legacyAddRmlSetting = settingsDialogType.GetMethod(
        "AddRmlSetting",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    MethodInfo? legacyShowRmlSetting = settingsDialogType.GetMethod(
        "ShowRmlSetting",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    string? baseTypeName = settingsDialogType.BaseType?.FullName;
    MethodBase[] nativeSettingsMethods = settingsDialogType.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(method => method.GetMethodBody() is not null)
        .Cast<MethodBase>()
        .ToArray();
    bool usesImGui = nativeSettingsMethods
        .SelectMany(method => PatchProcessor.GetCurrentInstructions((MethodInfo)method))
        .Select(instruction => instruction.operand)
        .OfType<MethodBase>()
        .Any(called => called.DeclaringType?.FullName == "ImGuiNET.ImGui");
    if (!string.Equals(baseTypeName, "VSDirector.Gui.DirectorImGuiDialog", StringComparison.Ordinal)
        || !settingsDraw.IsVirtual
        || legacyCompose is not null
        || legacyAddRmlSetting is not null
        || legacyShowRmlSetting is not null
        || !usesImGui
        || !CallsMethodReference(settingsOnDraw, settingsFooter)
        || !CallsMethodReference(settingsOnDraw, settingsPage)
        || !CallsMethodReference(settingsPage, settingsDrawSetting))
    {
        throw new InvalidOperationException(
            "Settings must use the native VSImGui window, controls, and persistent footer actions.");
    }
    Console.WriteLine("PASS settings uses searchable native collapsing groups, controls, and footer actions");

    if (typeof(VSDirectorModSystem).GetMethod("TopActivityStatusText", BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || typeof(DirectorConfig).GetProperty("ShowCameraStatusBar") is not null
        || typeof(DirectorConfig).GetProperty("ShowReplayCaptureStatusBar") is not null
        || typeof(DirectorConfig).GetProperty("ShowReplayPlaybackStatusBar") is not null
        || typeof(DirectorConfig).GetProperty("ShowRecordingStatusBar") is not null
        || new DirectorConfig().ReplayPauseWorld)
    {
        throw new InvalidOperationException("The activity status bar must be removed and replay must not pause the world by default.");
    }
    Console.WriteLine("PASS activity status bar removal and replay pause default");

    string replayStoreRoot = Path.Combine(Path.GetTempPath(), "vsdirector-replay-metadata-" + Guid.NewGuid().ToString("N"));
    object? replayStore = null;
    object? reloadedReplayStore = null;
    try
    {
        Directory.CreateDirectory(replayStoreRoot);
        ILogger replayStoreLogger = ProbeDispatchProxy.Create<ILogger>((method, arguments) =>
            ProbeDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI replayStoreApi = ProbeDispatchProxy.Create<ICoreClientAPI>((method, arguments) =>
            method.Name switch
            {
                "get_Logger" => replayStoreLogger,
                "get_DataBasePath" => replayStoreRoot,
                "GetOrCreateDataPath" => Path.Combine(
                    replayStoreRoot,
                    arguments?.FirstOrDefault()?.ToString() ?? string.Empty),
                _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
            });
        const string replayWorldId = "patch-probe-world";
        replayStore = Activator.CreateInstance(
            replayStoreType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { replayStoreApi, replayWorldId, "" },
            culture: null)
            ?? throw new InvalidOperationException("Replay store could not be created.");
        Guid metadataReplayId = Guid.NewGuid();
        DirectorReplayDocument metadataReplay = new()
        {
            Id = metadataReplayId,
            Name = "Original replay",
            WorldId = replayWorldId,
            CreatedUtc = DateTimeOffset.UtcNow,
            DurationSeconds = 1.25d
        };
        MethodInfo saveReplay = replayStoreType.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Replay store save method was not found.");
        string savedReplayPath = saveReplay.Invoke(
            replayStore,
            new object[] { metadataReplay, DirectorReplayCompressionProfile.Balanced }) as string
            ?? throw new InvalidOperationException("Replay store did not return a saved path.");
        MethodInfo updateReplayMetadata = replayStoreType.GetMethod(
            "UpdateMetadata",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Replay metadata update method was not found.");
        object?[] metadataArguments =
        {
            metadataReplayId,
            "  Edited replay  ",
            new[] { "scene", " SCENE ", "night" },
            "  Exterior take  ",
            null,
            string.Empty
        };
        if (updateReplayMetadata.Invoke(replayStore, metadataArguments) is not true
            || metadataArguments[4] is not object updatedReplayInfo
            || !string.Equals(
                replayInfoType.GetProperty("Name")?.GetValue(updatedReplayInfo) as string,
                "Edited replay",
                StringComparison.Ordinal)
            || !string.Equals(
                replayInfoType.GetProperty("Description")?.GetValue(updatedReplayInfo) as string,
                "Exterior take",
                StringComparison.Ordinal)
            || replayInfoType.GetProperty("Tags")?.GetValue(updatedReplayInfo) is not IEnumerable<string> updatedTags
            || !updatedTags.SequenceEqual(new[] { "scene", "night" }, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Replay metadata was not normalized and updated in memory.");
        }

        using (System.Text.Json.JsonDocument metadataJson = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(savedReplayPath + ".meta.json")))
        {
            System.Text.Json.JsonElement root = metadataJson.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2
                || root.GetProperty("name").GetString() != "Edited replay"
                || root.GetProperty("description").GetString() != "Exterior take"
                || root.GetProperty("tags").GetArrayLength() != 2)
            {
                throw new InvalidOperationException("Replay metadata sidecar did not persist the edited fields.");
            }
        }

        reloadedReplayStore = Activator.CreateInstance(
            replayStoreType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { replayStoreApi, replayWorldId, "" },
            culture: null)
            ?? throw new InvalidOperationException("Reloaded replay store could not be created.");
        PropertyInfo isIndexing = replayStoreType.GetProperty("IsIndexing", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Replay store indexing state was not found.");
        for (int attempt = 0; attempt < 500 && isIndexing.GetValue(reloadedReplayStore) is true; attempt++)
        {
            Thread.Sleep(10);
        }
        if (isIndexing.GetValue(reloadedReplayStore) is true)
        {
            throw new InvalidOperationException("Replay metadata reload did not finish indexing.");
        }
        object reloadedList = replayStoreType.GetMethod("List", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(reloadedReplayStore, null)!;
        object? reloadedInfo = ((System.Collections.IEnumerable)reloadedList).Cast<object>().SingleOrDefault();
        if (reloadedInfo is null
            || replayInfoType.GetProperty("Name")?.GetValue(reloadedInfo) as string != "Edited replay"
            || replayInfoType.GetProperty("Description")?.GetValue(reloadedInfo) as string != "Exterior take")
        {
            throw new InvalidOperationException("Replay metadata did not survive a fresh index load.");
        }
        Console.WriteLine("PASS replay metadata sidecar update and reload");
    }
    finally
    {
        (reloadedReplayStore as IDisposable)?.Dispose();
        (replayStore as IDisposable)?.Dispose();
        if (Directory.Exists(replayStoreRoot))
        {
            Directory.Delete(replayStoreRoot, recursive: true);
        }
    }

    MethodInfo replaySoundFactory = replayManagerType.GetMethod(
        "CreateReplaySoundParams",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay 3D sound parameter factory was not found.");
    MethodInfo spatializedReplaySoundFactory = replayManagerType.GetMethod(
        "CreateSpatializedReplaySoundParams",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay camera-relative sound spatializer was not found.");
    DirectorReplaySoundEvent replaySoundEvent = new()
    {
        Location = "game:sounds/creature/pig/hurt",
        X = 10d,
        Y = 20d,
        Z = 30d,
        Volume = 0.4f,
        Pitch = 1.2f,
        Range = 9f,
        ReferenceDistance = 2.5f,
        LowPassFilter = 0.75f,
        ReverbDecayTime = 0.5f,
        SoundType = (int)EnumSoundType.Entity
    };
    SoundParams replaySoundParams = replaySoundFactory.Invoke(
        null,
        new object[] { replaySoundEvent }) as SoundParams
        ?? throw new InvalidOperationException("Replay 3D sound parameters were not created.");
    if (replaySoundParams.Position is null
        || replaySoundParams.Position.X != 10f
        || replaySoundParams.Position.Y != 20f
        || replaySoundParams.Position.Z != 30f
        || replaySoundParams.RelativePosition
        || replaySoundParams.ShouldLoop
        || !replaySoundParams.DisposeOnFinish
        || replaySoundParams.Volume != 0.4f
        || replaySoundParams.Pitch != 1.2f
        || replaySoundParams.Range != 9f
        || replaySoundParams.ReferenceDistance != 2.5f
        || replaySoundParams.LowPassFilter != 0.75f
        || replaySoundParams.ReverbDecayTime != 0.5f
        || replaySoundParams.SoundType != EnumSoundType.Entity)
    {
        throw new InvalidOperationException("Replay sound did not preserve its absolute 3D playback parameters.");
    }
    Console.WriteLine("PASS replay absolute 3D sound parameters");

    DirectorReplayDocument soundAnchorDocument = new()
    {
        Actors = new List<DirectorReplayActorTrack>
        {
            new()
            {
                ActorKey = "player:camera-source",
                PlayerUid = "camera-source",
                Samples = new List<DirectorReplayActorSample>
                {
                    new() { TimeSeconds = 0d, X = 10d, Y = 20d, Z = 30d, Dimension = 1 }
                }
            }
        }
    };
    DirectorReplaySoundEvent relativeWorldSound = new()
    {
        TimeSeconds = 1d,
        Location = "game:sounds/block/rock-break.ogg",
        RelativePosition = true,
        SoundType = (int)EnumSoundType.Sound
    };
    object?[] spatializedArguments =
        { soundAnchorDocument, relativeWorldSound, "camera-source", false };
    SoundParams spatializedWorldParams = spatializedReplaySoundFactory.Invoke(
        null,
        spatializedArguments) as SoundParams
        ?? throw new InvalidOperationException("Replay relative world sound was not spatialized.");
    if (spatializedArguments[3] is not true
        || spatializedWorldParams.RelativePosition
        || spatializedWorldParams.Position is null
        || spatializedWorldParams.Position.X != 10f
        || spatializedWorldParams.Position.Y != 32788f
        || spatializedWorldParams.Position.Z != 30f)
    {
        throw new InvalidOperationException(
            "Replay world feedback did not anchor to the recorded player in 3D space.");
    }

    DirectorReplaySoundEvent relativeWeatherSound = new()
    {
        TimeSeconds = 1d,
        Location = "game:sounds/weather/wind-leafy.ogg",
        RelativePosition = true,
        SoundType = (int)EnumSoundType.Weather
    };
    object?[] weatherArguments =
        { soundAnchorDocument, relativeWeatherSound, "camera-source", false };
    SoundParams spatializedWeatherParams = spatializedReplaySoundFactory.Invoke(
        null,
        weatherArguments) as SoundParams
        ?? throw new InvalidOperationException("Replay weather sound parameters were not created.");
    if (weatherArguments[3] is not false || !spatializedWeatherParams.RelativePosition)
    {
        throw new InvalidOperationException(
            "Replay weather sound must remain listener-relative instead of becoming a point source.");
    }
    Console.WriteLine("PASS replay camera audio spatializes world feedback and preserves global ambience");

    MethodInfo registerReplayLoadedSound = replayManagerType.GetMethod(
        "RegisterReplayLoadedSound",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound registration was not found.");
    MethodInfo captureReplayLoadedSoundStart = replayManagerType.GetMethod(
        "CaptureReplayLoadedSoundStart",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound start capture was not found.");
    MethodInfo captureClientSoundStart = replayManagerType.GetMethod(
        "CaptureClientSoundStart",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay client sound start capture was not found.");
    MethodInfo captureReplayLoadedSoundState = replayManagerType.GetMethod(
        "CaptureReplayLoadedSoundState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound state capture was not found.");
    object soundCaptureManager = RuntimeHelpers.GetUninitializedObject(replayManagerType);
    replayManagerType.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(soundCaptureManager, new DirectorConfig { ReplayCaptureSounds = true });
    FieldInfo loadedSoundMarkers = replayManagerType.GetField(
        "replayLoadedSounds",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound markers were not found.");
    loadedSoundMarkers.SetValue(soundCaptureManager, Activator.CreateInstance(loadedSoundMarkers.FieldType));
    FieldInfo loadedSoundReferences = replayManagerType.GetField(
        "replayLoadedSoundReferences",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound weak-reference list was not found.");
    loadedSoundReferences.SetValue(soundCaptureManager, Activator.CreateInstance(loadedSoundReferences.FieldType));
    replayManagerType.GetField("soundCaptureSync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(soundCaptureManager, new object());
    SoundParams reusableLoadedSoundParams = new()
    {
        Location = new AssetLocation("game:sounds/creature/pig/hurt"),
        Position = new Vec3f(4f, 5f, 6f),
        RelativePosition = false,
        ShouldLoop = false,
        Volume = 0.6f,
        Pitch = 0.9f,
        Range = 12f,
        ReferenceDistance = 2f,
        LowPassFilter = 0.8f,
        ReverbDecayTime = 0.25f,
        SoundType = EnumSoundType.Entity
    };
    ILoadedSound reusableLoadedSound = ProbeDispatchProxy.Create<ILoadedSound>((method, _) =>
        method.Name == "get_Params"
            ? reusableLoadedSoundParams
            : ProbeDispatchProxy.DefaultValue(method.ReturnType));
    registerReplayLoadedSound.Invoke(
        soundCaptureManager,
        new object?[] { reusableLoadedSound, reusableLoadedSoundParams });
    DirectorReplayDocument loadedSoundDocument = new();
    replayManagerType.GetField("recording", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(soundCaptureManager, loadedSoundDocument);
    FieldInfo recordingElapsedSeconds = replayManagerType.GetField(
        "recordingElapsedSeconds",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay recording clock was not found.");
    recordingElapsedSeconds.SetValue(soundCaptureManager, 1d);
    captureReplayLoadedSoundStart.Invoke(soundCaptureManager, new object?[] { reusableLoadedSound });
    recordingElapsedSeconds.SetValue(soundCaptureManager, 2d);
    captureReplayLoadedSoundStart.Invoke(soundCaptureManager, new object?[] { reusableLoadedSound });

    SoundParams relativeBreakParams = new()
    {
        Location = new AssetLocation("game:sounds/block/rock-break.ogg"),
        Position = null,
        RelativePosition = true,
        ShouldLoop = false,
        Volume = 0.7f,
        Pitch = 1.1f,
        Range = 32f,
        ReferenceDistance = 3f,
        LowPassFilter = 1f,
        ReverbDecayTime = 0f,
        SoundType = EnumSoundType.Sound
    };
    ILoadedSound relativeBreakSound = ProbeDispatchProxy.Create<ILoadedSound>((method, _) =>
        method.Name == "get_Params"
            ? relativeBreakParams
            : ProbeDispatchProxy.DefaultValue(method.ReturnType));
    recordingElapsedSeconds.SetValue(soundCaptureManager, 3d);
    captureClientSoundStart.Invoke(soundCaptureManager, new object?[] { relativeBreakSound });

    float loopPlaybackPosition = 0.5f;
    const float loopSoundLength = 4f;
    SoundParams loopParams = new()
    {
        Location = new AssetLocation("game:sounds/environment/fireplace.ogg"),
        Position = new Vec3f(8f, 9f, 10f),
        RelativePosition = false,
        ShouldLoop = true,
        DisposeOnFinish = false,
        Volume = 0.35f,
        Pitch = 0.95f,
        Range = 24f,
        SoundType = EnumSoundType.Ambient
    };
    ILoadedSound loopSound = ProbeDispatchProxy.Create<ILoadedSound>((method, _) => method.Name switch
    {
        "get_Params" => loopParams,
        "get_PlaybackPosition" => loopPlaybackPosition,
        "get_SoundLengthSeconds" => loopSoundLength,
        _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
    });
    registerReplayLoadedSound.Invoke(soundCaptureManager, new object?[] { loopSound, loopParams });
    recordingElapsedSeconds.SetValue(soundCaptureManager, 4d);
    captureReplayLoadedSoundStart.Invoke(soundCaptureManager, new object?[] { loopSound });
    loopParams.Volume = 0.55f;
    loopPlaybackPosition = 0.75f;
    recordingElapsedSeconds.SetValue(soundCaptureManager, 5d);
    captureReplayLoadedSoundState.Invoke(
        soundCaptureManager,
        new object?[] { loopSound, DirectorReplaySoundAction.Update, 0.1f });
    recordingElapsedSeconds.SetValue(soundCaptureManager, 6d);
    captureReplayLoadedSoundState.Invoke(
        soundCaptureManager,
        new object?[] { loopSound, DirectorReplaySoundAction.Pause, null });
    loopPlaybackPosition = 1.25f;
    recordingElapsedSeconds.SetValue(soundCaptureManager, 7d);
    captureReplayLoadedSoundStart.Invoke(soundCaptureManager, new object?[] { loopSound });
    recordingElapsedSeconds.SetValue(soundCaptureManager, 8d);
    captureReplayLoadedSoundState.Invoke(
        soundCaptureManager,
        new object?[] { loopSound, DirectorReplaySoundAction.Stop, null });

    if (loadedSoundDocument.SoundEvents.Count != 8
        || loadedSoundDocument.SoundEvents[0].TimeSeconds != 1d
        || loadedSoundDocument.SoundEvents[1].TimeSeconds != 2d
        || loadedSoundDocument.SoundEvents.Take(2).Any(sound =>
            sound.Location != "game:sounds/creature/pig/hurt"
            || sound.X != 4d
            || sound.Y != 5d
            || sound.Z != 6d
            || sound.RelativePosition
            || sound.ReferenceDistance != 2f
            || sound.LowPassFilter != 0.8f
            || sound.ReverbDecayTime != 0.25f
            || sound.SoundType != (int)EnumSoundType.Entity))
    {
        throw new InvalidOperationException(
            "Replay did not capture a preloaded positional sound on every one-shot start.");
    }
    DirectorReplaySoundEvent capturedBreak = loadedSoundDocument.SoundEvents[2];
    if (capturedBreak.TimeSeconds != 3d
        || capturedBreak.Location != "game:sounds/block/rock-break.ogg"
        || !capturedBreak.RelativePosition
        || capturedBreak.X != 0d
        || capturedBreak.Y != 0d
        || capturedBreak.Z != 0d
        || capturedBreak.Volume != 0.7f
        || capturedBreak.Pitch != 1.1f)
    {
        throw new InvalidOperationException(
            "Replay did not preserve listener-relative block-break sound semantics.");
    }
    Console.WriteLine("PASS replay captures absolute and listener-relative one-shot sounds exactly once");
    SoundParams relativeReplayParams = replaySoundFactory.Invoke(
        null,
        new object[] { capturedBreak }) as SoundParams
        ?? throw new InvalidOperationException("Replay relative sound parameters were not created.");
    if (!relativeReplayParams.RelativePosition
        || relativeReplayParams.Position is null
        || relativeReplayParams.Position.X != 0f
        || relativeReplayParams.Position.Y != 0f
        || relativeReplayParams.Position.Z != 0f
        || relativeReplayParams.Volume != 0.7f
        || relativeReplayParams.Pitch != 1.1f)
    {
        throw new InvalidOperationException(
            "Replay did not recreate listener-relative block-break sound parameters.");
    }
    Console.WriteLine("PASS replay preserves captured listener-relative sound parameters before spatialization");

    DirectorReplaySoundEvent[] capturedLoop = loadedSoundDocument.SoundEvents.Skip(3).ToArray();
    if (capturedLoop.Length != 5
        || capturedLoop.Any(sound => sound.InstanceId <= 0L)
        || capturedLoop.Select(sound => sound.InstanceId).Distinct().Count() != 1
        || !capturedLoop.Select(sound => sound.Action).SequenceEqual(new[]
        {
            DirectorReplaySoundAction.Play,
            DirectorReplaySoundAction.Update,
            DirectorReplaySoundAction.Pause,
            DirectorReplaySoundAction.Play,
            DirectorReplaySoundAction.Stop
        })
        || capturedLoop[0].PlaybackPosition != 0.5f
        || capturedLoop.Any(sound => sound.SoundLengthSeconds != loopSoundLength)
        || capturedLoop[1].PlaybackPosition != 0.75f
        || capturedLoop[1].PitchOffset != 0.1f
        || capturedLoop[1].Volume != 0.55f
        || capturedLoop[3].PlaybackPosition != 1.25f
        || capturedLoop.Any(sound => !sound.ShouldLoop))
    {
        throw new InvalidOperationException(
            "Replay did not preserve the complete looping-sound lifecycle and parameter updates.");
    }
    SoundParams loopReplayParams = replaySoundFactory.Invoke(
        null,
        new object[] { capturedLoop[0] }) as SoundParams
        ?? throw new InvalidOperationException("Replay loop sound parameters were not created.");
    if (!loopReplayParams.ShouldLoop || loopReplayParams.DisposeOnFinish)
    {
        throw new InvalidOperationException("Replay loop sound did not retain looping lifetime parameters.");
    }
    Console.WriteLine("PASS replay looping-sound lifecycle and dynamic parameter capture");

    Type loadedSoundStartPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayLoadedSoundStartCapturePatch",
        throwOnError: true)!;
    MethodInfo loadedSoundStartPrefix = loadedSoundStartPatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound isolation prefix was not found.");
    MethodInfo shouldAllowLoadedSoundStart = replayManagerType.GetMethod(
        "ShouldAllowLoadedSoundStart",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-sound ownership policy was not found.");
    Type loadedSoundVolumePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayLoadedSoundVolumeIsolationPatch",
        throwOnError: true)!;
    MethodInfo loadedSoundVolumePrefix = loadedSoundVolumePatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-loop volume isolation prefix was not found.");
    MethodInfo shouldApplyLoadedSoundVolume = replayManagerType.GetMethod(
        "ShouldApplyLoadedSoundVolume",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-loop volume policy was not found.");
    MethodInfo playReplaySound = replayManagerType.GetMethod(
        "PlayReplaySound",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay timeline sound player was not found.");
    Type replayPlaybackState = replayManagerType.GetNestedType("Playback", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay playback state was not found.");
    if (!CallsMethod(loadedSoundStartPrefix, shouldAllowLoadedSoundStart)
        || !CallsMethod(loadedSoundVolumePrefix, shouldApplyLoadedSoundVolume)
        || replayPlaybackState.GetProperty("PlaybackOwnedSounds") is null
        || replayPlaybackState.GetProperty("ReplayWorldOwnedSounds") is null
        || !PatchProcessor.GetCurrentInstructions(playReplaySound).Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name == nameof(HashSet<ILoadedSound>.Add)
            && method.DeclaringType?.IsGenericType == true
            && method.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>)))
    {
        throw new InvalidOperationException(
            "Replay timeline sounds are not owned before live spatial-sound isolation runs.");
    }
    Console.WriteLine("PASS replay/live spatial-sound ownership isolation");

    Type rendererPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerShapeRendererPatch",
        throwOnError: true)!;
    MethodInfo installRendererPatch = rendererPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay renderer patch installer was not found.");
    ILogger logger = ProbeDispatchProxy.Create<ILogger>((method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
    Type playerInventoryPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerInventoryPatch",
        throwOnError: true)!;
    MethodInfo installPlayerInventoryPatch = playerInventoryPatch.GetMethod(
        "TryPatchPlayerBehaviorInventory",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "Replay player behavior inventory patch installer was not found.");
    if (installPlayerInventoryPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException(
            "Replay player behavior inventory getter patch was not installed.");
    }
    MethodInfo playerBehaviorInventoryGetter = AccessTools.PropertyGetter(
        typeof(EntityBehaviorPlayerInventory),
        "Inventory")
        ?? throw new InvalidOperationException(
            "EntityBehaviorPlayerInventory.Inventory getter was not found.");
    if (Harmony.GetPatchInfo(playerBehaviorInventoryGetter)?.Postfixes.Any(patch =>
            patch.PatchMethod.Name == "PlayerBehaviorInventoryPostfix") != true)
    {
        throw new InvalidOperationException(
            "Replay player behavior inventory getter is not redirected to replay equipment.");
    }
    if (installRendererPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay renderer patch was not installed.");
    }
    Type tessellationRevisionPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayEntityTessellationRevisionPatch",
        throwOnError: true)!;
    MethodInfo installTessellationRevisionPatch = tessellationRevisionPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "Replay tessellation revision patch installer was not found.");
    if (installTessellationRevisionPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException(
            "Replay tessellation revision patch was not installed.");
    }
    MethodInfo replayTessellationMethod = tessellationRevisionPatch.GetProperty(
        "TesselationMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
        as MethodInfo
        ?? throw new InvalidOperationException(
            "Replay tessellation revision target method was not discovered.");
    MethodInfo replayCompletionMethod = tessellationRevisionPatch.GetProperty(
        "CompletionMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
        as MethodInfo
        ?? throw new InvalidOperationException(
            "Replay tessellation main-thread completion closure was not discovered.");
    MethodBase markShapeModifiedTarget = (MethodBase)(tessellationRevisionPatch.GetProperty(
            "MarkShapeModifiedMethod",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
        ?? throw new InvalidOperationException(
            "Replay shape revision invalidation target was not discovered."));
    MethodBase disposeShapeRendererTarget = (MethodBase)(tessellationRevisionPatch.GetProperty(
            "DisposeMethod",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
        ?? throw new InvalidOperationException(
            "Replay shape renderer disposal target was not discovered."));
    MethodBase staleCompletionTarget = (MethodBase)(tessellationRevisionPatch.GetProperty(
            "StaleCompletionMethod",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
        ?? throw new InvalidOperationException(
            "Replay stale tessellation completion target was not discovered."));
    if (Harmony.GetPatchInfo(replayTessellationMethod)?.Prefixes.Any(patch =>
            patch.PatchMethod.Name == "TesselationPrefix") != true
        || Harmony.GetPatchInfo(replayCompletionMethod)?.Prefixes.Any(patch =>
            patch.PatchMethod.Name == "CompletionPrefix") != true
        || Harmony.GetPatchInfo(markShapeModifiedTarget)?.Postfixes.Any(patch =>
            patch.PatchMethod.Name == "MarkShapeModifiedPostfix") != true
        || Harmony.GetPatchInfo(disposeShapeRendererTarget)?.Prefixes.Any(patch =>
            patch.PatchMethod.Name == "DisposePrefix") != true
        || Harmony.GetPatchInfo(staleCompletionTarget)?.Prefixes.Any(patch =>
            patch.PatchMethod.Name == "StaleCompletionPrefix") != true)
    {
        throw new InvalidOperationException(
            "Replay tessellation callbacks are not guarded across generation, "
            + "shape revision, completion, and disposal boundaries.");
    }
    Type rendererRevisionType = tessellationRevisionPatch.GetNestedType(
        "RendererRevision",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay renderer revision state was not found.");
    object rendererRevision = Activator.CreateInstance(rendererRevisionType, nonPublic: true)
        ?? throw new InvalidOperationException("Replay renderer revision state could not be created.");
    MethodInfo beginRendererRevision = rendererRevisionType.GetMethod(
        "Begin",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay renderer revision begin method was not found.");
    MethodInfo isCurrentRendererRevision = rendererRevisionType.GetMethod(
        "IsCurrent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay renderer revision comparison was not found.");
    MethodInfo disposeRendererRevision = rendererRevisionType.GetMethod(
        "Dispose",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay renderer revision disposal was not found.");
    long firstRendererRevision = (long)beginRendererRevision.Invoke(rendererRevision, null)!;
    long secondRendererRevision = (long)beginRendererRevision.Invoke(rendererRevision, null)!;
    bool firstRevisionCurrent = (bool)isCurrentRendererRevision.Invoke(
        rendererRevision,
        new object[] { firstRendererRevision })!;
    bool secondRevisionCurrent = (bool)isCurrentRendererRevision.Invoke(
        rendererRevision,
        new object[] { secondRendererRevision })!;
    disposeRendererRevision.Invoke(rendererRevision, null);
    bool disposedRevisionCurrent = (bool)isCurrentRendererRevision.Invoke(
        rendererRevision,
        new object[] { secondRendererRevision })!;
    long postDisposeRendererRevision = (long)beginRendererRevision.Invoke(
        rendererRevision,
        null)!;
    bool postDisposeRevisionCurrent = (bool)isCurrentRendererRevision.Invoke(
        rendererRevision,
        new object[] { postDisposeRendererRevision })!;
    if (firstRevisionCurrent
        || !secondRevisionCurrent
        || disposedRevisionCurrent
        || postDisposeRevisionCurrent)
    {
        throw new InvalidOperationException(
            "Replay renderer revision state did not reject stale, disposed, or revived mesh completions.");
    }
    Console.WriteLine("PASS replay asynchronous entity tessellation revision guard");
    MethodInfo replayPlayerTesselateShape = typeof(EntityPlayerShapeRenderer).GetMethod(
        nameof(EntityPlayerShapeRenderer.TesselateShape),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        Type.EmptyTypes,
        modifiers: null)
        ?? throw new InvalidOperationException("Replay player tessellation entry point was not found.");
    Patches? replayPlayerTesselationPatches = Harmony.GetPatchInfo(replayPlayerTesselateShape);
    if (replayPlayerTesselationPatches?.Prefixes.Any(patch =>
            patch.PatchMethod.Name == "TesselationPrefix") != true
        || replayPlayerTesselationPatches.Postfixes.Any(patch =>
            patch.PatchMethod.Name == "TesselationPostfix") != true
        || replayPlayerTesselationPatches.Finalizers.Any(patch =>
            patch.PatchMethod.Name == "TesselationFinalizer") != true)
    {
        throw new InvalidOperationException(
            "Replay player tessellation does not scope clothing inventory resolution across its early guard.");
    }
    MethodInfo replayPlayerIdentity = AccessTools.PropertyGetter(
        typeof(EntityPlayer),
        nameof(EntityPlayer.Player))
        ?? throw new InvalidOperationException("EntityPlayer.Player getter was not found.");
    if (Harmony.GetPatchInfo(replayPlayerIdentity)?.Postfixes.Any(patch =>
            patch.PatchMethod.DeclaringType?.Name == "ReplayPlayerIdentityPatch") != true)
    {
        throw new InvalidOperationException(
            "Replay player identity does not provide a tessellation-only inventory dispatch target.");
    }
    Type shapeRendererPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayEntityShapeRendererPatch",
        throwOnError: true)!;
    MethodInfo installShapeRendererPatch = shapeRendererPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay entity shape renderer patch installer was not found.");
    if (installShapeRendererPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay entity shape renderer patch was not installed.");
    }
    Type greenScreenPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorGreenScreenRenderPatch",
        throwOnError: true)!;
    MethodInfo installGreenScreenPatch = greenScreenPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Green-screen render patch installer was not found.");
    if (installGreenScreenPatch.Invoke(null, new object[] { harmony, logger })
            is not IReadOnlyList<MethodBase> greenScreenTargets
        || greenScreenTargets.Count < 4
        || greenScreenTargets.Any(target => Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException("Green-screen sky render patches were not installed.");
    }
    string[] requiredGreenScreenTargets =
    {
        "Vintagestory.Client.NoObf.SystemRenderSkyColor.OnRenderFrame3D",
        "Vintagestory.Client.NoObf.SystemRenderNightSky.OnRenderFrame3D",
        "Vintagestory.Client.NoObf.SystemRenderSunMoon.OnRenderFrame3D",
        "Vintagestory.GameContent.CloudRenderer.OnRenderFrame"
    };
    HashSet<string> patchedGreenScreenTargets = greenScreenTargets
        .Select(target => target.DeclaringType?.FullName + "." + target.Name)
        .ToHashSet(StringComparer.Ordinal);
    foreach (string requiredTarget in requiredGreenScreenTargets)
    {
        if (!patchedGreenScreenTargets.Contains(requiredTarget))
        {
            throw new InvalidOperationException($"Green-screen render target was not patched: {requiredTarget}");
        }
    }
    Console.WriteLine($"PASS green-screen sky and cloud hooks ({greenScreenTargets.Count})");
    Type replayWorldRenderPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayWorldRenderPatch",
        throwOnError: true)!;
    MethodInfo installReplayWorldRenderPatch = replayWorldRenderPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay world visual patch installer was not found.");
    if (installReplayWorldRenderPatch.Invoke(null, new object[] { harmony, logger })
            is not IReadOnlyList<MethodBase> replayWorldRenderTargets
        || replayWorldRenderTargets.Count != 3
        || replayWorldRenderTargets.Any(target =>
            Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException("Replay terrain and liquid visual hooks were not installed.");
    }

    Type replayVisualEffectsPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayVisualEffectsPatch",
        throwOnError: true)!;
    MethodInfo installReplayVisualEffectsPatch = replayVisualEffectsPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay visual-effects patch installer was not found.");
    if (installReplayVisualEffectsPatch.Invoke(null, new object[] { harmony, logger })
            is not IReadOnlyList<MethodBase> replayVisualEffectTargets
        || replayVisualEffectTargets.Count != 2
        || replayVisualEffectTargets.Any(target =>
            Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException("Replay visual-effect hooks were not installed.");
    }
    string[] requiredReplayVisualEffectTargets =
    {
        "Vintagestory.Client.NoObf.SystemRenderEntities.OnRenderFrame2D",
        "Vintagestory.GameContent.EntityBehaviorNameTag.OnRenderFrame"
    };
    HashSet<string> patchedReplayVisualEffectTargets = replayVisualEffectTargets
        .Select(target => target.DeclaringType?.FullName + "." + target.Name)
        .ToHashSet(StringComparer.Ordinal);
    foreach (string requiredTarget in requiredReplayVisualEffectTargets)
    {
        if (!patchedReplayVisualEffectTargets.Contains(requiredTarget))
        {
            throw new InvalidOperationException(
                $"Replay visual-effect target was not patched: {requiredTarget}");
        }
    }
    Console.WriteLine(
        $"PASS replay terrain and name-tag hooks "
        + $"({replayWorldRenderTargets.Count + replayVisualEffectTargets.Count})");
    Type optimizedSkyRenderer = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorGreenScreenSkyRenderer",
        throwOnError: true)!;
    if (typeof(IRenderer).IsAssignableFrom(optimizedSkyRenderer)
        || optimizedSkyRenderer.GetMethod(
            "Render",
            BindingFlags.Instance | BindingFlags.NonPublic) is not { ReturnType: { } renderReturn }
        || renderReturn != typeof(bool))
    {
        throw new InvalidOperationException(
            "Green-screen sky must replace the vanilla sky pass without registering a second renderer.");
    }
    Type outlineRenderer = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorModelOutlineRenderer",
        throwOnError: true)!;
    string[] outlinePasses =
    {
        "RenderEntityMask",
        "RenderHorizontalDilation",
        "CompositeOuterContour",
        "CalculateMaskRadius"
    };
    if (outlinePasses.Any(name => outlineRenderer.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) is null))
    {
        throw new InvalidOperationException("The single-contour outline pipeline is incomplete.");
    }
    MethodInfo collectOutlineTargets = outlineRenderer.GetMethod(
        "CollectTargets",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The model outline target collector is missing.");
    MethodInfo resolveActiveOutlineRenderer = outlineRenderer.GetMethod(
        "ResolveActiveRenderer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The active model outline renderer resolver is missing.");
    Type offscreenEngineBindings = outlineRenderer.Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenEngineBindings",
        throwOnError: true)!;
    MethodInfo getActiveEntityRenderer = offscreenEngineBindings.GetMethod(
        "TryGetEntityRenderer",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen entity renderer binding is missing.");
    if (!CallsMethod(collectOutlineTargets, resolveActiveOutlineRenderer)
        || !CallsMethod(resolveActiveOutlineRenderer, getActiveEntityRenderer))
    {
        throw new InvalidOperationException(
            "Offscreen model outlines can use a stale primary-view renderer and shift away from the recorded entity.");
    }
    MethodInfo ensureOutlineFrameBuffers = outlineRenderer.GetMethod(
        "EnsureFrameBuffers",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The model outline framebuffer scaler is missing.");
    if (ensureOutlineFrameBuffers.GetParameters().Length != 3
        || outlineRenderer.GetField(
                "DepthAwareMaskBufferScale",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue() is not float depthScale
        || Math.Abs(depthScale - 1f) > 0.0001f)
    {
        throw new InvalidOperationException(
            "Depth-aware model outlines must use a full-resolution mask.");
    }
    MethodInfo createOutlineFrameBuffer = outlineRenderer.GetMethod(
        "CreateMaskFrameBuffer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The model outline framebuffer factory is missing.");
    ParameterInfo[] outlineFrameBufferParameters = createOutlineFrameBuffer.GetParameters();
    if (outlineFrameBufferParameters.Length != 4
        || outlineFrameBufferParameters[^1].ParameterType != typeof(bool))
    {
        throw new InvalidOperationException(
            "The entity-mask framebuffer must support a private depth attachment.");
    }
    MethodInfo renderOutlineMask = outlineRenderer.GetMethod(
        "RenderEntityMask",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The entity-mask render pass is missing.");
    IReadOnlyList<CodeInstruction> outlineMaskInstructions =
        PatchProcessor.GetCurrentInstructions(renderOutlineMask);
    if (!outlineMaskInstructions.Any(instruction =>
            instruction.operand is MethodInfo method && method.Name == "GlEnableDepthTest")
        || !outlineMaskInstructions.Any(instruction =>
            instruction.operand is MethodInfo method && method.Name == "GlDepthMask")
        || !outlineMaskInstructions.Any(instruction =>
            instruction.operand is MethodInfo method && method.Name == "ClearFrameBuffer"))
    {
        throw new InvalidOperationException(
            "The entity mask must depth-test and clear its private depth buffer.");
    }
    MethodInfo matchesOutlinedEntity = outlineRenderer.GetMethod(
        "MatchesSelectedEntity",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The model outline entity selector is missing.");
    EntityPlayer outlinePlayer = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
    HashSet<string> playerOutlineSelection = new(StringComparer.OrdinalIgnoreCase)
    {
        "GAME:PLAYER"
    };
    if (matchesOutlinedEntity.Invoke(
            null,
            new object[] { outlinePlayer, playerOutlineSelection }) is not true)
    {
        throw new InvalidOperationException(
            "Selecting the canonical player model must include every player entity.");
    }
    MethodInfo configureOutlineUbo = outlineRenderer.GetMethod(
        "ConfigureAnimationUbo",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The model outline Animation UBO initializer is missing.");
    if (!PatchProcessor.GetCurrentInstructions(configureOutlineUbo).Any(instruction =>
            instruction.operand is MethodInfo method && method.Name == "CreateUBO"))
    {
        throw new InvalidOperationException(
            "The model outline shader must explicitly create its Animation UBO.");
    }
    string shaderDirectory = Path.Combine(
        Path.GetDirectoryName(typeof(VSDirectorModSystem).Assembly.Location)!,
        "assets",
        "vsdirector",
        "shaders");
    string outlineFragment = File.ReadAllText(Path.Combine(shaderDirectory, "modeloutline.fsh"));
    string maskFragment = File.ReadAllText(Path.Combine(shaderDirectory, "modelmask.fsh"));
    string dilationFragment = File.ReadAllText(
        Path.Combine(shaderDirectory, "modeloutlinedilate.fsh"));
    if (!outlineFragment.Contains("bool fullOuterContour", StringComparison.Ordinal)
        || !outlineFragment.Contains("bool visibleOuterContour", StringComparison.Ordinal)
        || !outlineFragment.Contains("bool occlusionClosure", StringComparison.Ordinal)
        || !outlineFragment.Contains("throughObstacles", StringComparison.Ordinal)
        || !outlineFragment.Contains("expandedMasks.g", StringComparison.Ordinal)
        || !outlineFragment.Contains("maskTexelSize", StringComparison.Ordinal)
        || !maskFragment.Contains("texelFetch(sceneDepthTex", StringComparison.Ordinal)
        || !maskFragment.Contains("float medianSceneDepth", StringComparison.Ordinal)
        || !maskFragment.Contains("sortDepthPair", StringComparison.Ordinal)
        || !maskFragment.Contains("centerSceneDepth", StringComparison.Ordinal)
        || !maskFragment.Contains("matchesRenderedSurface", StringComparison.Ordinal)
        || maskFragment.Contains("dFdx(gl_FragCoord.z)", StringComparison.Ordinal)
        || !maskFragment.Contains(
            "outMask = vec4(1.0, visibleMask",
            StringComparison.Ordinal)
        || !dilationFragment.Contains("expandedMasks", StringComparison.Ordinal)
        || !dilationFragment.Contains(").rg", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The outline shaders must use stable conservative depth, separate full/visible masks, and close obstacle intersections.");
    }
    Console.WriteLine("PASS optimized green-screen sky, player selection, and single-contour outline architecture");
    Type interpolationPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPositionInterpolationIsolationPatch",
        throwOnError: true)!;
    MethodInfo installInterpolationPatch = interpolationPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay position-interpolation patch installer was not found.");
    if (installInterpolationPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay position-interpolation patch was not installed.");
    }
    Type itemRendererPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayItemRendererVisibilityPatch",
        throwOnError: true)!;
    MethodInfo installItemRendererPatch = itemRendererPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay item renderer patch installer was not found.");
    if (installItemRendererPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay item visibility patch was not installed.");
    }
    Type waterMaskPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayHideWaterSurfaceVisibilityPatch",
        throwOnError: true)!;
    MethodInfo installWaterMaskPatch = waterMaskPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay water-mask patch installer was not found.");
    if (installWaterMaskPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay water-mask visibility patch was not installed.");
    }
    Type fallingBlockPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayFallingBlockIsolationPatch",
        throwOnError: true)!;
    MethodInfo installFallingBlockPatch = fallingBlockPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay falling-block patch installer was not found.");
    if (installFallingBlockPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Replay falling-block isolation patches were not installed.");
    }
    FieldInfo initialBlockRemovedBinding = fallingBlockPatch.GetField(
        "initialBlockRemovedField",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay falling-block source-removal binding was not found.");
    if (initialBlockRemovedBinding.GetValue(null) is not FieldInfo initialBlockRemovedField
        || initialBlockRemovedField.Name != "InitialBlockRemoved")
    {
        throw new InvalidOperationException("Replay falling-block source-removal state did not resolve.");
    }
    Console.WriteLine("PASS replay falling-block source-removal state");

    Type offscreenAnimationScheduler = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenEntityAnimationScheduler",
        throwOnError: true)!;
    MethodInfo enterOffscreenAnimationView = offscreenAnimationScheduler.GetMethod(
        "EnterView",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The unified offscreen entity animation scheduler was not found.");
    MethodInfo prepareDetachedAnimator = offscreenAnimationScheduler.GetMethod(
        "PrepareDetachedAnimator",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The detached offscreen animator frame gate was not found.");
    MethodInfo prepareSharedAnimatorFallback = offscreenAnimationScheduler.GetMethod(
        "PrepareSharedAnimatorFallback",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen shared-animator fallback was not found.");
    MethodInfo advanceOffscreenModelMatrix = offscreenAnimationScheduler.GetMethod(
        "AdvanceModelMatrix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen renderer-time model update was not found.");
    MethodInfo renderOffscreenView = outlineRenderer.Assembly.GetType(
            "VSDirector.Rendering.DirectorOffscreenMultiViewRenderer",
            throwOnError: true)!
        .GetMethod("RenderView", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The offscreen view renderer was not found.");
    Type independentPlayerRenderer = outlineRenderer.Assembly.GetType(
        "VSDirector.Rendering.DirectorIndependentPlayerRenderer",
        throwOnError: true)!;
    MethodInfo restoreIndependentPlayerRenderer = independentPlayerRenderer.GetMethod(
        "TryRestoreSourceRenderer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The independent player renderer restore guard was not found.");
    MethodInfo disposeIndependentPlayerRenderer = independentPlayerRenderer.GetMethod(
        "DisposeRenderer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The independent player renderer cleanup method was not found.");
    MethodInfo completeIndependentPlayerTesselation = independentPlayerRenderer.GetMethod(
        "CompleteTesselation",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The independent player renderer tessellation completion guard was not found.");
    MethodInfo clearDuplicatePlayerMeshes = independentPlayerRenderer.GetMethod(
        "ClearDuplicateMeshReferences",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The independent player renderer mesh ownership guard was not found.");
    Type independentRestoreScope = independentPlayerRenderer.GetNestedType(
        "RestoreScope",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The independent player renderer restore scope was not found.");
    MethodInfo disposeIndependentRestoreScope = independentRestoreScope.GetMethod(
        nameof(IDisposable.Dispose),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "The independent player renderer restore scope cleanup was not found.");
    MethodInfo disposeMeshData = typeof(MeshData).GetMethod(
        nameof(MeshData.Dispose),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("MeshData.Dispose was not found.");
    if (!CallsMethodReference(disposeIndependentRestoreScope, restoreIndependentPlayerRenderer)
        || !CallsMethod(disposeIndependentPlayerRenderer, restoreIndependentPlayerRenderer)
        || !CallsMethod(disposeIndependentPlayerRenderer, clearDuplicatePlayerMeshes)
        || !CallsMethodReference(completeIndependentPlayerTesselation, disposeMeshData)
        || independentPlayerRenderer.GetField(
            "tesselationRevision",
            BindingFlags.Instance | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Independent offscreen player renderers can outlive their mesh or remain installed after cleanup.");
    }
    Console.WriteLine("PASS independent offscreen player renderer lifetime guard");
    if (!CallsMethod(renderOffscreenView, enterOffscreenAnimationView))
    {
        throw new InvalidOperationException(
            "Offscreen rendering does not enter the unified entity animation scheduler.");
    }
    IReadOnlyList<CodeInstruction> schedulerInstructions =
        PatchProcessor.GetCurrentInstructions(enterOffscreenAnimationView);
    IReadOnlyList<CodeInstruction> animatorFrameInstructions =
        PatchProcessor.GetCurrentInstructions(prepareDetachedAnimator);
    if (!animatorFrameInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && (method.DeclaringType == typeof(ClientAnimator)
                || method.DeclaringType == typeof(AnimatorBase)
                || method.DeclaringType == typeof(IAnimator))
            && method.Name == nameof(ClientAnimator.OnFrame))
        || !animatorFrameInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name.Contains("LastAnimationAdvancedFrame", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "Detached entity animations are not gated to one update per physical frame.");
    }
    if (!schedulerInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name == "set_Animator")
        || !schedulerInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && method.Name == "set_HeadController")
        || !schedulerInstructions.Any(instruction =>
            instruction.operand is FieldInfo field
            && field.Name == "OverrideCameraMode")
        || !CallsMethod(enterOffscreenAnimationView, advanceOffscreenModelMatrix))
    {
        throw new InvalidOperationException(
            "Offscreen animator, head-controller, remote-camera, or renderer-time isolation is incomplete.");
    }
    if (!CallsMethod(enterOffscreenAnimationView, prepareSharedAnimatorFallback))
    {
        throw new InvalidOperationException(
            "An incompatible detached animator still leaves offscreen-only actors frozen.");
    }
    foreach (string bindingName in new[]
    {
        "ActiveOrRunningField",
        "LoadEntityModelMatrixMethod",
        "LoadPlayerModelMatrixMethod"
    })
    {
        if (offscreenAnimationScheduler.GetField(
                bindingName,
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not MemberInfo)
        {
            throw new InvalidOperationException(
                $"Offscreen animation engine binding '{bindingName}' did not resolve.");
        }
    }
    Type offscreenEntityState = offscreenAnimationScheduler.GetNestedType(
        "EntityState",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen entity animation state was not found.");
    foreach (string stateName in new[]
    {
        "OffscreenActiveAnimations",
        "LastAnimationAdvancedFrame",
        "LastRendererAdvancedFrame"
    })
    {
        if (offscreenEntityState.GetProperty(
                stateName,
                BindingFlags.Instance | BindingFlags.NonPublic) is null)
        {
            throw new InvalidOperationException(
                $"Offscreen per-entity state '{stateName}' was not found.");
        }
    }
    if (offscreenAnimationScheduler.GetField(
            "RendererStateFieldNames",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            is not IEnumerable<string> isolatedRendererFields
        || new[] { "intoxIntensity" }
            .Except(isolatedRendererFields, StringComparer.Ordinal)
            .Any())
    {
        throw new InvalidOperationException(
            "Offscreen renderer snapshots omit delta-time-dependent model state.");
    }
    if (offscreenAnimationScheduler.GetField(
            "RendererResourceFieldNames",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            is not IEnumerable<string> isolatedRendererResources
        || !isolatedRendererResources.Contains("meshRefOpaque", StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            "Offscreen player mesh selection is not restored after each view.");
    }
    Type rendererStateSnapshot = offscreenAnimationScheduler.GetNestedType(
        "RendererStateSnapshot",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen renderer state snapshot was not found.");
    MethodInfo applyRendererStateSnapshot = rendererStateSnapshot.GetMethod(
        "Apply",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen renderer state restore method was not found.");
    MethodInfo isUsableRendererResource = rendererStateSnapshot.GetMethod(
        "IsUsableResource",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen renderer resource guard was not found.");
    MultiTextureMeshRef disposedRendererResource = new(Array.Empty<MeshRef>(), Array.Empty<int>());
    disposedRendererResource.Dispose();
    if (isUsableRendererResource.Invoke(null, new object?[] { disposedRendererResource }) is not false
        || !CallsMethod(applyRendererStateSnapshot, isUsableRendererResource))
    {
        throw new InvalidOperationException(
            "Offscreen renderer state can restore an invalid or disposed player mesh.");
    }
    Console.WriteLine("PASS offscreen renderer resource validity guard");
    Type offscreenEntityTemporalSnapshot = offscreenAnimationScheduler.GetNestedType(
        "EntityTemporalSnapshot",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen entity temporal snapshot was not found.");
    if (offscreenEntityTemporalSnapshot.GetProperty(
            "SelfNowShadowPass",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Offscreen player shadow-pass state is not isolated from the primary view.");
    }
    Type offscreenPlayerManagerPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenPlayerAnimationManagerPatch",
        throwOnError: true)!;
    MethodInfo offscreenPlayerManagerPostfix = offscreenPlayerManagerPatch.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen third-person player animation-manager patch was not found.");
    if (!PatchProcessor.GetCurrentInstructions(offscreenPlayerManagerPostfix).Any(instruction =>
        instruction.operand is MethodInfo method
        && method.Name == "get_TpAnimManager"))
    {
        throw new InvalidOperationException(
            "Offscreen players are not forced onto their third-person animation manager.");
    }
    Type playerTessellationPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerThirdPersonShapePatch",
        throwOnError: true)!;
    Type playerTessellationState = playerTessellationPatch.GetNestedType(
        "TesselationState",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen local-player tessellation state was not found.");
    MethodInfo restoreLocalPlayerState = playerTessellationState.GetMethod(
        "RestoreLocalState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The offscreen local-player animation restoration method was not found.");
    MethodInfo playerTessellationPostfix = playerTessellationPatch.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The third-person player tessellation postfix was not found.");
    MethodInfo playerTessellationFinalizer = playerTessellationPatch.GetMethod(
        "Finalizer",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The third-person player tessellation finalizer was not found.");
    if (!CallsMethod(playerTessellationPostfix, restoreLocalPlayerState)
        || !CallsMethod(playerTessellationFinalizer, restoreLocalPlayerState)
        || playerTessellationState.GetField(
            "selfUseFpAnimations",
            BindingFlags.Instance | BindingFlags.NonPublic) is null
        || playerTessellationState.GetField(
            "tpHeadController",
            BindingFlags.Instance | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Offscreen player tessellation can leak third-person animation state into the primary first-person view.");
    }
    EntityPlayer firstPersonAnimationProbe = new();
    firstPersonAnimationProbe.TpAnimManager.UseFpAnmations = false;
    firstPersonAnimationProbe.SelfFpAnimManager.UseFpAnmations = true;
    object tessellationStateProbe = Activator.CreateInstance(
        playerTessellationState,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: new object?[] { null, firstPersonAnimationProbe, true },
        culture: null)
        ?? throw new InvalidOperationException(
            "The offscreen local-player tessellation state could not be constructed.");
    firstPersonAnimationProbe.TpAnimManager.UseFpAnmations = true;
    firstPersonAnimationProbe.SelfFpAnimManager.UseFpAnmations = false;
    restoreLocalPlayerState.Invoke(
        tessellationStateProbe,
        new object[] { firstPersonAnimationProbe });
    if (firstPersonAnimationProbe.TpAnimManager.UseFpAnmations
        || !firstPersonAnimationProbe.SelfFpAnimManager.UseFpAnmations)
    {
        throw new InvalidOperationException(
            "Offscreen player tessellation did not restore the primary first-person animation flags.");
    }

    Animation detachedProbeAnimation = new()
    {
        Code = "offscreenprobe",
        Name = "offscreenprobe",
        QuantityFrames = 30,
        KeyFrames = new[]
        {
            new AnimationKeyFrame
            {
                Frame = 0,
                Elements = new Dictionary<string, AnimationKeyFrameElement>()
            }
        }
    };
    ClientAnimator primaryAnimatorProbe = new(
        () => 1d,
        new[] { detachedProbeAnimation },
        Array.Empty<ShapeElement>(),
        new Dictionary<int, AnimationJoint>())
    {
        CalculateMatrices = false
    };
    ClientAnimator detachedAnimatorProbe = new(
        () => 1d,
        new[] { detachedProbeAnimation },
        Array.Empty<ShapeElement>(),
        new Dictionary<int, AnimationJoint>())
    {
        CalculateMatrices = false
    };
    Dictionary<string, AnimationMetaData> detachedProbeActive = new()
    {
        ["offscreenprobe"] = new AnimationMetaData
        {
            Code = "offscreenprobe",
            Animation = "offscreenprobe",
            AnimationSpeed = 1f
        }
    };
    primaryAnimatorProbe.OnFrame(detachedProbeActive, 0f);
    primaryAnimatorProbe.OnFrame(detachedProbeActive, 0.05f);
    MethodInfo synchronizeDetachedAnimator = offscreenAnimationScheduler.GetMethod(
        "SynchronizeAnimatorFromSource",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The detached animator state synchronizer was not found.");
    synchronizeDetachedAnimator.Invoke(
        null,
        new object[] { primaryAnimatorProbe, detachedAnimatorProbe, detachedProbeActive });
    float primaryFrameBeforeDetachedAdvance = primaryAnimatorProbe
        .GetAnimationState("offscreenprobe")
        .CurrentFrame;
    detachedAnimatorProbe.OnFrame(
        detachedProbeActive.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase),
        0.05f);
    if (Math.Abs(
            primaryAnimatorProbe.GetAnimationState("offscreenprobe").CurrentFrame
            - primaryFrameBeforeDetachedAdvance) > 0.0001f
        || detachedAnimatorProbe.GetAnimationState("offscreenprobe").CurrentFrame
            <= primaryFrameBeforeDetachedAdvance)
    {
        throw new InvalidOperationException(
            "Advancing the detached offscreen animator modified or failed to outpace the primary animator.");
    }

    ClientAnimator scheduledAnimatorProbe = new(
        () => 1d,
        new[] { detachedProbeAnimation },
        Array.Empty<ShapeElement>(),
        new Dictionary<int, AnimationJoint>())
    {
        CalculateMatrices = false
    };
    AnimationManager scheduledAnimationManager = new()
    {
        Animator = primaryAnimatorProbe
    };
    scheduledAnimationManager.ActiveAnimationsByAnimCode["offscreenprobe"] =
        detachedProbeActive["offscreenprobe"].Clone();
    ConstructorInfo offscreenEntityStateConstructor = offscreenEntityState
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single();
    object scheduledEntityState = offscreenEntityStateConstructor.Invoke(new object?[]
    {
        null,
        scheduledAnimationManager,
        primaryAnimatorProbe,
        scheduledAnimatorProbe,
        null,
        Activator.CreateInstance(offscreenEntityTemporalSnapshot)
    });
    offscreenEntityState.GetMethod(
            "ResetActiveAnimations",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(
            scheduledEntityState,
            new object[] { scheduledAnimationManager.ActiveAnimationsByAnimCode });
    object[] scheduledFrameArguments =
    {
        scheduledEntityState,
        scheduledAnimationManager,
        primaryAnimatorProbe,
        true,
        100L,
        0.05f
    };
    prepareDetachedAnimator.Invoke(null, scheduledFrameArguments);
    float scheduledFrame100 = scheduledAnimatorProbe
        .GetAnimationState("offscreenprobe")
        .CurrentFrame;
    prepareDetachedAnimator.Invoke(null, scheduledFrameArguments);
    float scheduledFrame100SecondView = scheduledAnimatorProbe
        .GetAnimationState("offscreenprobe")
        .CurrentFrame;
    scheduledFrameArguments[4] = 101L;
    prepareDetachedAnimator.Invoke(null, scheduledFrameArguments);
    float scheduledFrame101 = scheduledAnimatorProbe
        .GetAnimationState("offscreenprobe")
        .CurrentFrame;
    prepareDetachedAnimator.Invoke(null, scheduledFrameArguments);
    float scheduledFrame101SecondView = scheduledAnimatorProbe
        .GetAnimationState("offscreenprobe")
        .CurrentFrame;
    if (Math.Abs(scheduledFrame100SecondView - scheduledFrame100) > 0.0001f
        || scheduledFrame101 <= scheduledFrame100
        || Math.Abs(scheduledFrame101SecondView - scheduledFrame101) > 0.0001f
        || Math.Abs(
            primaryAnimatorProbe.GetAnimationState("offscreenprobe").CurrentFrame
            - primaryFrameBeforeDetachedAdvance) > 0.0001f
        || ReferenceEquals(primaryAnimatorProbe.Matrices, scheduledAnimatorProbe.Matrices)
        || ReferenceEquals(primaryAnimatorProbe.RootPoses, scheduledAnimatorProbe.RootPoses))
    {
        throw new InvalidOperationException(
            "Multiple offscreen views advanced shared animation time or reused primary bone storage.");
    }
    Console.WriteLine("PASS unified isolated offscreen entity animation scheduler");

    Type offscreenLocalPlayerAnimation = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenLocalPlayerAnimationContext",
        throwOnError: true)!;
    MethodInfo enterOffscreenThirdPerson = offscreenLocalPlayerAnimation.GetMethod(
        "Enter",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The detached offscreen third-person animation context was not found.");
    MethodInfo rebuildOffscreenPlayerMatrices = offscreenLocalPlayerAnimation.GetMethod(
        "RebuildAnimatorMatrices",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The zero-time offscreen bone-matrix rebuild was not found.");
    IReadOnlyList<CodeInstruction> offscreenThirdPersonInstructions =
        PatchProcessor.GetCurrentInstructions(enterOffscreenThirdPerson);
    if (!CallsMethod(renderOffscreenView, enterOffscreenThirdPerson))
    {
        throw new InvalidOperationException(
            "Offscreen rendering does not enter the isolated local-player animation context.");
    }
    if (!offscreenThirdPersonInstructions.Any(instruction =>
        instruction.operand is FieldInfo field
        && field.Name == "OverrideCameraMode"))
    {
        throw new InvalidOperationException(
            "The isolated local-player animation context does not override camera mode.");
    }
    if (!offscreenThirdPersonInstructions.Any(instruction =>
        instruction.operand is MethodInfo method
        && method.DeclaringType == typeof(EntityHeadController)
        && method.Name == nameof(PlayerHeadController.OnFrame)))
    {
        throw new InvalidOperationException(
            "The isolated local-player animation context does not update head/body coordination.");
    }
    if (!CallsMethod(enterOffscreenThirdPerson, rebuildOffscreenPlayerMatrices)
        || offscreenLocalPlayerAnimation.GetField(
                "CalculateClientMatricesMethod",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not MethodInfo)
    {
        throw new InvalidOperationException(
            "Local-player offscreen poses still pass through a second animation lifecycle update.");
    }
    foreach (string yawBindingName in new[] { "BodyYawLerpedField", "SmoothedBodyYawField" })
    {
        if (offscreenLocalPlayerAnimation.GetField(
                yawBindingName,
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not FieldInfo)
        {
            throw new InvalidOperationException(
                $"Offscreen third-person renderer yaw binding '{yawBindingName}' did not resolve.");
        }
    }
    Console.WriteLine("PASS isolated offscreen third-person player animation context");

    Type replayPlayerRendererPatch = outlineRenderer.Assembly.GetType(
        "VSDirector.Replay.ReplayPlayerShapeRendererPatch",
        throwOnError: true)!;
    foreach (string yawBindingName in new[] { "bodyYawLerpedField", "smoothedBodyYawField" })
    {
        if (replayPlayerRendererPatch.GetField(
                yawBindingName,
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not FieldInfo)
        {
            throw new InvalidOperationException(
                $"Replay third-person renderer yaw binding '{yawBindingName}' did not resolve.");
        }
    }
    MethodInfo replayPlayerRenderPrefix = replayPlayerRendererPatch.GetMethod(
        "RenderPrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay player render prefix was not found.");
    MethodInfo ensurePlayerMeshesUsable = replayPlayerRendererPatch.GetMethod(
        "EnsurePlayerMeshesUsable",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Player mesh render guard was not found.");
    if (!CallsMethod(replayPlayerRenderPrefix, ensurePlayerMeshesUsable)
        || new[] { "meshRefOpaqueField", "firstPersonMeshRefField", "thirdPersonMeshRefField" }
            .Any(name => replayPlayerRendererPatch.GetField(
                name,
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not FieldInfo))
    {
        throw new InvalidOperationException(
            "Player rendering can enter the engine with a disposed shape mesh.");
    }
    Console.WriteLine("PASS disposed player mesh render guard");
    MethodInfo replayThirdPersonBodyYaw = replayManagerType.GetMethod(
        "TryGetReplayThirdPersonBodyYaw",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay third-person body pose lookup was not found.");
    if (!PatchProcessor.GetCurrentInstructions(replayPlayerRenderPrefix).Any(instruction =>
            instruction.operand is FieldInfo field
            && (field.Name == "bodyYawLerpedField" || field.Name == "smoothedBodyYawField")))
    {
        throw new InvalidOperationException(
            "Replay player rendering does not commit the recorded body yaw before drawing.");
    }
    if (!CallsMethod(replayPlayerRenderPrefix, replayThirdPersonBodyYaw))
    {
        throw new InvalidOperationException(
            "Replay player rendering still uses live renderer yaw instead of the detached third-person pose.");
    }
    if (!PatchProcessor.GetCurrentInstructions(replayPlayerRenderPrefix).Any(instruction =>
            instruction.operand is FieldInfo field
                && field.Name == nameof(EntityPos.Yaw)
            || instruction.operand is MethodInfo method
                && method.Name == $"set_{nameof(EntityPos.Yaw)}"))
    {
        throw new InvalidOperationException(
            "Replay player rendering does not bind the recorded body yaw to the model-matrix input.");
    }

    Type replayEntityRendererPatch = outlineRenderer.Assembly.GetType(
        "VSDirector.Replay.ReplayEntityShapeRendererPatch",
        throwOnError: true)!;
    MethodInfo replayEntityRenderPrefix = replayEntityRendererPatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay creature render prefix was not found.");
    IReadOnlyList<CodeInstruction> replayEntityRenderInstructions =
        PatchProcessor.GetCurrentInstructions(replayEntityRenderPrefix).ToArray();
    if (replayManagerType.GetMethod(
            "TryGetReplayBodyYaw",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null
        || replayEntityRenderInstructions.Any(instruction =>
            instruction.operand is FieldInfo field
                && field.DeclaringType == typeof(EntityPos)
                && field.Name == nameof(EntityPos.Yaw)
            || instruction.operand is MethodInfo method
                && method.DeclaringType == typeof(EntityPos)
                && method.Name == $"set_{nameof(EntityPos.Yaw)}"))
    {
        throw new InvalidOperationException(
            "Replay creature rendering still replaces the recorded model yaw with agent body yaw.");
    }

    MethodInfo shouldRenderReplayBinding = replayManagerType.GetMethod(
        "ShouldRenderBinding",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay actor render policy was not found.");
    if (PatchProcessor.GetCurrentInstructions(shouldRenderReplayBinding).Any(instruction =>
            instruction.operand is FieldInfo field
            && field.Name.Contains("AnimationPoseDirty", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "Replay actor visibility is still gated by animator pose readiness.");
    }

    MethodInfo applyRecordedAnimationState = replayManagerType.GetMethod(
        "ApplyRecordedAnimationState",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay recorded-animation pose updater was not found.");
    MethodInfo resolveAnimatorPoseDelta = typeof(DirectorReplayPlaybackPolicy).GetMethod(
        nameof(DirectorReplayPlaybackPolicy.ResolveAnimatorPoseDelta),
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay legacy-animation delta policy was not found.");
    if (!CallsMethod(applyRecordedAnimationState, resolveAnimatorPoseDelta))
    {
        throw new InvalidOperationException(
            "Replay legacy animations are not advanced by replay time.");
    }
    IReadOnlyList<CodeInstruction> recordedAnimationInstructions =
        PatchProcessor.GetCurrentInstructions(applyRecordedAnimationState).ToArray();
    if (!recordedAnimationInstructions.Any(instruction =>
            instruction.operand is MethodInfo method
            && method.DeclaringType == typeof(DirectorReplayAnimationSample)
            && method.Name == "get_IsActive")
        || !recordedAnimationInstructions.Any(instruction =>
            instruction.operand is FieldInfo field
            && field.DeclaringType == typeof(RunningAnimation)
            && field.Name == nameof(RunningAnimation.Active)))
    {
        throw new InvalidOperationException(
            "Replay one-shot animation runtime flags are not restored after pose evaluation.");
    }

    ClientAnimator replayRegistrationAnimator = new(
        () => 1d,
        new[] { detachedProbeAnimation },
        Array.Empty<ShapeElement>(),
        new Dictionary<int, AnimationJoint>())
    {
        CalculateMatrices = false
    };
    AnimationManager replayRegistrationManager = new()
    {
        Animator = replayRegistrationAnimator
    };
    replayRegistrationManager.ActiveAnimationsByAnimCode["offscreenprobe"] =
        detachedProbeActive["offscreenprobe"].Clone();
    DirectorReplayAnimationSample replayRegistrationSample = new()
    {
        Code = "offscreenprobe",
        StateCode = "offscreenprobe",
        Progress = 0.5f,
        EasingFactor = 1f,
        BlendedWeight = 1f,
        IsActive = true,
        IsRunning = true
    };
    object[] stateOnlyArguments =
    {
        replayRegistrationManager,
        new[] { replayRegistrationSample },
        false,
        true,
        0f,
        true
    };
    applyRecordedAnimationState.Invoke(null, stateOnlyArguments);
    RunningAnimation replayRegistrationState =
        replayRegistrationAnimator.GetAnimationState("offscreenprobe");
    if (replayRegistrationState.Active
        || replayRegistrationState.Running
        || replayRegistrationAnimator.ActiveAnimationCount != 0)
    {
        throw new InvalidOperationException(
            "Replay state staging bypasses the animator's active-layer registration transition.");
    }
    object[] poseArguments =
    {
        replayRegistrationManager,
        new[] { replayRegistrationSample },
        true,
        true,
        0f,
        true
    };
    applyRecordedAnimationState.Invoke(null, poseArguments);
    if (replayRegistrationAnimator.ActiveAnimationCount != 1
        || !replayRegistrationState.Active
        || !replayRegistrationState.Running
        || Math.Abs(replayRegistrationState.AnimProgress - replayRegistrationSample.Progress) > 0.0001f)
    {
        throw new InvalidOperationException(
            "Replay pose evaluation did not register and restore the recorded animation layer.");
    }

    Type replayItemRendererPatch = outlineRenderer.Assembly.GetType(
        "VSDirector.Replay.ReplayItemRendererVisibilityPatch",
        throwOnError: true)!;
    MethodInfo replayItemClock = replayItemRendererPatch.GetMethod(
        "ReplayElapsedMilliseconds",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay item render clock was not found.");
    MethodInfo itemLoadModelMatrix = AccessTools.Method(
        typeof(EntityItemRenderer),
        "LoadModelMatrix",
        new[] { typeof(ItemRenderInfo), typeof(bool), typeof(float) })
        ?? throw new InvalidOperationException("Entity item model-matrix method was not found.");
    if (!PatchProcessor.GetCurrentInstructions(itemLoadModelMatrix).Any(instruction =>
            instruction.operand is MethodInfo method
            && method == replayItemClock))
    {
        throw new InvalidOperationException(
            "Replay dropped-item rotation still reads the live-world clock.");
    }
    Console.WriteLine("PASS replay player yaw, visible pose fallback, and dropped-item clocks");

    object uninitializedModSystem = RuntimeHelpers.GetUninitializedObject(typeof(VSDirectorModSystem));
    DirectorReplayActorTrack actionProbeTrack = new()
    {
        ActorKey = "action-probe",
        Samples = new List<DirectorReplayActorSample> { new() }
    };
    DirectorReplayActorSample actionProbeSample = new();
    MethodInfo decorateActionSample = typeof(VSDirectorModSystem).GetMethod(
        "DecorateReplayActionSample",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay action sample decorator was not found.");
    if (!ReferenceEquals(
            actionProbeSample,
            decorateActionSample.Invoke(
                uninitializedModSystem,
                new object[] { actionProbeTrack, 0d, actionProbeSample })))
    {
        throw new InvalidOperationException(
            "Replay action sample decorator did work when no action preview or workflow was active.");
    }
    MethodInfo decorateActionEntity = typeof(VSDirectorModSystem).GetMethod(
        "DecorateReplayActionEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay action entity decorator was not found.");
    decorateActionEntity.Invoke(
        uninitializedModSystem,
        new object?[] { actionProbeTrack, 0d, null, actionProbeSample });
    Type resolvedPlaybackType = typeof(VSDirectorModSystem).GetNestedType(
        "ResolvedActionPlayback",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Resolved replay action type was not found.");
    MethodInfo applyBoneOverrides = typeof(VSDirectorModSystem).GetMethod(
        "ApplyActionBoneOverrides",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay action bone override method was not found.");
    applyBoneOverrides.Invoke(
        null,
        new object?[] { null, Array.CreateInstance(resolvedPlaybackType, 0) });
    Console.WriteLine("PASS replay action decorators no-op without active workflow");

    Type anchorUnloadPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorChunkUnloadPatch",
        throwOnError: true)!;
    MethodInfo installAnchorUnloadPatch = anchorUnloadPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote chunk unload patch installer was not found.");
    if (installAnchorUnloadPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Remote chunk unload patch was not installed.");
    }
    MethodBase anchorUnloadTarget = anchorUnloadPatch.GetProperty(
        "TargetMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetValue(null) as MethodBase
        ?? throw new InvalidOperationException("Remote chunk unload patch target was not resolved.");
    if (Harmony.GetPatchInfo(anchorUnloadTarget)?.Owners.Contains(harmonyId) != true)
    {
        throw new InvalidOperationException("Remote chunk unload method was not patched.");
    }
    Console.WriteLine("PASS server remote chunk unload protection");

    Type anchorEntityPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorEntityTrackingPatch",
        throwOnError: true)!;
    MethodInfo installAnchorEntityPatch = anchorEntityPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote entity tracking patch installer was not found.");
    if (installAnchorEntityPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Remote entity tracking patch was not installed.");
    }
    MethodBase anchorEntityTarget = anchorEntityPatch.GetProperty(
        "TargetMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetValue(null) as MethodBase
        ?? throw new InvalidOperationException("Remote entity tracking patch target was not resolved.");
    if (Harmony.GetPatchInfo(anchorEntityTarget)?.Owners.Contains(harmonyId) != true)
    {
        throw new InvalidOperationException("Remote entity tracking method was not patched.");
    }
    Type anchorSoundPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorSoundBroadcastPatch",
        throwOnError: true)!;
    MethodInfo installAnchorSoundPatch = anchorSoundPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote sound broadcast patch installer was not found.");
    MethodInfo anchorSoundPostfix = anchorSoundPatch.GetMethod(
        "Postfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Remote sound broadcast postfix was not found.");
    MethodInfo forwardClientSound = anchorSoundPatch.GetMethod(
        "ForwardClientSound",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Client block sound forwarding method was not found.");
    MethodInfo forwardSound = anchorSoundPatch.GetMethod(
        "Forward",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Remote sound forwarding method was not found.");
    Type anchorServerType = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorServer",
        throwOnError: true)!;
    MethodInfo resolveAnchorRadius = anchorServerType.GetMethod(
        "ResolveAnchorRadius",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote follow radius resolver was not found.");
    int Radius(int requested, int targetViewDistance, int maxRadius)
        => (int)(resolveAnchorRadius.Invoke(
            null,
            new object[] { requested, targetViewDistance, 32, maxRadius })
            ?? throw new InvalidOperationException("Remote follow radius resolver returned no value."));
    if (Radius(4, 256, 12) != 8
        || Radius(10, 256, 12) != 10
        || Radius(4, 512, 12) != 12
        || Radius(6, 0, 12) != 6
        || Radius(4, 256, 4) != 4)
    {
        throw new InvalidOperationException(
            "Remote follow radius did not combine director and target-player view distances correctly.");
    }
    MethodInfo blockSoundHandler = anchorServerType.GetMethod(
        "OnBlockSound",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Client block sound server handler was not found.");
    if (installAnchorSoundPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Remote sound broadcast patch was not installed.");
    }
    MethodBase anchorSoundTarget = anchorSoundPatch.GetProperty(
        "TargetMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetValue(null) as MethodBase
        ?? throw new InvalidOperationException("Remote sound broadcast patch target was not resolved.");
    if (Harmony.GetPatchInfo(anchorSoundTarget)?.Owners.Contains(harmonyId) != true)
    {
        throw new InvalidOperationException("Remote sound broadcast method was not patched.");
    }
    Type anchorProtection = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorChunkProtection",
        throwOnError: true)!;
    BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    MethodInfo setAnchorProtection = anchorProtection.GetMethod(
        "Set",
        staticFlags,
        binder: null,
        new[] { typeof(string), typeof(IEnumerable<long>), typeof(IEnumerable<long>) },
        modifiers: null)
        ?? throw new InvalidOperationException("Remote chunk protection setter was not found.");
    MethodInfo queryChunkProtection = anchorProtection.GetMethod(
        "IsProtected",
        staticFlags,
        binder: null,
        new[] { typeof(string), typeof(long) },
        modifiers: null)
        ?? throw new InvalidOperationException("Remote chunk protection query was not found.");
    MethodInfo queryEntityTracking = anchorProtection.GetMethod(
        "ShouldTrackEntity",
        staticFlags,
        binder: null,
        new[] { typeof(string), typeof(long) },
        modifiers: null)
        ?? throw new InvalidOperationException("Remote entity tracking query was not found.");
    Type anchorSoundColumn = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Networking.DirectorAnchorSoundColumn",
        throwOnError: true)!;
    MethodInfo setAnchorSoundProtection = anchorProtection.GetMethods(staticFlags)
        .Single(method => method.Name == "Set" && method.GetParameters().Length == 4);
    MethodInfo queryRemoteSound = anchorProtection.GetMethod(
        "ShouldReceiveRemoteSound",
        staticFlags,
        binder: null,
        new[] { typeof(string), typeof(double), typeof(double), typeof(double) },
        modifiers: null)
        ?? throw new InvalidOperationException("Remote sound coverage query was not found.");
    MethodInfo queryRemoteClientSound = anchorProtection.GetMethod(
        "ShouldReceiveRemoteSound",
        staticFlags,
        binder: null,
        new[]
        {
            AccessTools.TypeByName("Vintagestory.Server.ConnectedClient")!,
            typeof(double),
            typeof(double),
            typeof(double)
        },
        modifiers: null)
        ?? throw new InvalidOperationException("Remote client sound coverage query was not found.");
    MethodInfo startServerSide = typeof(VSDirectorModSystem).GetMethod(
        nameof(VSDirectorModSystem.StartServerSide),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("VS Director server startup method was not found.");
    if (!CallsMethodReference(anchorSoundPostfix, forwardSound)
        || !CallsMethodReference(forwardClientSound, forwardSound)
        || !CallsMethodReference(forwardSound, queryRemoteClientSound)
        || !CallsMethodReference(blockSoundHandler, forwardClientSound)
        || !CallsMethod(startServerSide, installAnchorSoundPatch))
    {
        throw new InvalidOperationException(
            "Remote sound forwarding was not connected to anchor coverage, client block sounds, and server startup.");
    }
    MethodInfo clearAnchorProtection = anchorProtection.GetMethod("Remove", staticFlags)
        ?? throw new InvalidOperationException("Remote chunk protection cleanup was not found.");
    const string probePlayerUid = "patch-probe-player";
    setAnchorProtection.Invoke(null, new object[]
    {
        probePlayerUid,
        new long[] { 101, 202 },
        new long[] { 202 }
    });
    try
    {
        if (queryChunkProtection.Invoke(null, new object[] { probePlayerUid, 101L }) is not true
            || queryChunkProtection.Invoke(null, new object[] { probePlayerUid, 202L }) is not true
            || queryEntityTracking.Invoke(null, new object[] { probePlayerUid, 101L }) is not false
            || queryEntityTracking.Invoke(null, new object[] { probePlayerUid, 202L }) is not true)
        {
            throw new InvalidOperationException("Preloaded chunks were not separated from active entity tracking chunks.");
        }

        Array soundColumns = Array.CreateInstance(anchorSoundColumn, 2);
        soundColumns.SetValue(Activator.CreateInstance(anchorSoundColumn, 10, 20, 0), 0);
        soundColumns.SetValue(Activator.CreateInstance(anchorSoundColumn, 30, 40, 1), 1);
        setAnchorSoundProtection.Invoke(null, new object[]
        {
            probePlayerUid,
            new long[] { 101, 202 },
            new long[] { 202 },
            soundColumns
        });
        if (queryRemoteSound.Invoke(
                null,
                new object[] { probePlayerUid, 10d * 32d + 1d, 100d, 20d * 32d + 1d }) is not true
            || queryRemoteSound.Invoke(
                null,
                new object[] { probePlayerUid, 30d * 32d + 1d, 32768d + 100d, 40d * 32d + 1d }) is not true
            || queryRemoteSound.Invoke(
                null,
                new object[] { probePlayerUid, 30d * 32d + 1d, 100d, 40d * 32d + 1d }) is not false
            || queryRemoteSound.Invoke(
                null,
                new object[] { probePlayerUid, 11d * 32d + 1d, 100d, 20d * 32d + 1d }) is not false
            || queryRemoteSound.Invoke(
                null,
                new object[] { probePlayerUid, double.NaN, 100d, 20d * 32d + 1d }) is not false)
        {
            throw new InvalidOperationException(
                "Remote sound coverage did not preserve anchor columns and dimensions.");
        }
    }
    finally
    {
        clearAnchorProtection.Invoke(null, new object[] { probePlayerUid });
    }
    Console.WriteLine("PASS server remote entity tracking and sound broadcast protection");

    Type remoteParticlePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayRemoteParticleViewPatch",
        throwOnError: true)!;
    MethodInfo installRemoteParticlePatch = remoteParticlePatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote particle view patch installer was not found.");
    if (installRemoteParticlePatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Remote particle view patch was not installed.");
    }
    PropertyInfo remoteParticleTargets = remoteParticlePatch.GetProperty(
        "Targets",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote particle view patch targets were not exposed.");
    if (remoteParticleTargets.GetValue(null) is not IReadOnlyList<MethodBase> particleTargets
        || particleTargets.Count == 0
        || particleTargets.Any(target => Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException("Remote particle view methods were not patched.");
    }
    string[] requiredRemoteViewTargets =
    {
        "Vintagestory.GameContent.WeatherSystemClient.OnRenderFrame",
        "Vintagestory.GameContent.WeatherSimulationParticles.asyncParticleSpawn",
        "Vintagestory.GameContent.ModSystemAmbientParticles.OnSlowTick",
        "Vintagestory.GameContent.BlockEntityParticleEmitter.OnGameTick",
        "Vintagestory.GameContent.EntityParticleSpawner.Sys_OnSimTick",
        "Vintagestory.Client.NoObf.SystemRenderEntities.OnBeforeRender",
        "Vintagestory.Client.NoObf.SystemRenderEntities.OnRenderFrameShadows",
        "Vintagestory.Client.NoObf.ClientSystemEntities.UpdateEvery1000ms"
    };
    HashSet<string> patchedRemoteViewTargets = particleTargets
        .Select(target => target.DeclaringType?.FullName + "." + target.Name)
        .ToHashSet(StringComparer.Ordinal);
    foreach (string requiredTarget in requiredRemoteViewTargets)
    {
        if (!patchedRemoteViewTargets.Contains(requiredTarget))
        {
            throw new InvalidOperationException($"Remote observation target was not patched: {requiredTarget}");
        }
    }
    Console.WriteLine("PASS remote weather, particles, and entity render observation");

    Type replayWeatherVisualPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayWeatherVisualPatch",
        throwOnError: true)!;
    MethodInfo installReplayWeatherVisualPatch = replayWeatherVisualPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "Replay weather visual patch installer was not found.");
    if (installReplayWeatherVisualPatch.Invoke(null, new object[] { harmony, logger })
        is not IReadOnlyList<MethodBase> replayWeatherTargets
        || replayWeatherTargets.Count == 0
        || replayWeatherTargets.Any(target =>
            Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException(
            "Replay weather visual methods were not patched.");
    }
    string[] requiredReplayWeatherTargets =
    {
        "Vintagestory.GameContent.WeatherSystemClient.OnRenderFrame",
        "Vintagestory.GameContent.WeatherSimulationParticles.asyncParticleSpawn",
        "Vintagestory.GameContent.WeatherSimulationSound.updateSounds",
        "Vintagestory.GameContent.WeatherSimulationLightning.ClientTick",
        "Vintagestory.Client.NoObf.AmbientManager.UpdateAmbient"
    };
    HashSet<string> patchedReplayWeatherTargets = replayWeatherTargets
        .Select(target => target.DeclaringType?.FullName + "." + target.Name)
        .ToHashSet(StringComparer.Ordinal);
    foreach (string requiredTarget in requiredReplayWeatherTargets)
    {
        if (!patchedReplayWeatherTargets.Contains(requiredTarget))
        {
            throw new InvalidOperationException(
                $"Replay weather visual target was not patched: {requiredTarget}");
        }
    }

    MethodInfo replaySnapshotResolver = replayWeatherVisualPatch.GetMethod(
        "GetBlendedWeatherDataForReplay",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather snapshot resolver was not found.");
    MethodInfo replayClimateResolver = replayWeatherVisualPatch.GetMethod(
        "GetClimateForReplay",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather climate resolver was not found.");
    MethodInfo advanceReplayPrecipitation = replayWeatherVisualPatch.GetMethod(
        "AdvancePrecipitation",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather precipitation ramp was not found.");
    MethodInfo clearReplayWeatherOverrides = replayWeatherVisualPatch.GetMethod(
        "ClearOverrides",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay weather override reset was not found.");
    MethodInfo isRecordedWeatherParticle = replayWeatherVisualPatch.GetMethod(
        "IsRecordedWeatherParticle",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Recorded weather particle filter was not found.");
    MethodInfo isRecordedWeatherSound = replayWeatherVisualPatch.GetMethod(
        "IsRecordedWeatherSound",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Recorded weather sound filter was not found.");
    PropertyInfo isWeatherParticleSpawnScope = replayWeatherVisualPatch.GetProperty(
        "IsWeatherParticleSpawnScope",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Weather particle capture scope was not exposed.");
    MethodInfo replayWeatherParticlePrefix = replayWeatherVisualPatch.GetMethod(
        "WeatherParticlePrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather particle scope prefix was not found.");
    MethodInfo replayWeatherParticleFinalizer = replayWeatherVisualPatch.GetMethod(
        "WeatherParticleFinalizer",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather particle scope finalizer was not found.");
    PropertyInfo allowsLiveWeatherParticles = replayWeatherVisualPatch.GetProperty(
        "AllowsLiveParticleSpawns",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay weather particle scope was not exposed.");
    Type replayAsyncParticleCapturePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayAsyncParticleCapturePatch",
        throwOnError: true)!;
    MethodInfo shouldAllowReplayParticleSpawn = replayAsyncParticleCapturePatch.GetMethod(
        "ShouldAllowParticleSpawn",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay particle suppression decision was not found.");
    MethodInfo replayAsyncParticlePrefix = replayAsyncParticleCapturePatch.GetMethod(
        "Prefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay async particle prefix was not found.");
    if (!CallsMethod(replayAsyncParticlePrefix, shouldAllowReplayParticleSpawn))
    {
        throw new InvalidOperationException(
            "Replay async particle suppression does not use the weather-aware decision.");
    }
    MethodInfo replayParticleRenderDecision = typeof(VSDirectorModSystem).Assembly
        .GetType("VSDirector.Replay.DirectorReplayManager", throwOnError: true)!
        .GetMethod(
            "ShouldRenderParticle",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay particle render decision was not found.");
    if (!CallsMethod(replayParticleRenderDecision, isRecordedWeatherParticle))
    {
        throw new InvalidOperationException(
            "Replay particle rendering does not consult the recorded weather filter.");
    }
    MethodInfo replaySoundDecision = typeof(VSDirectorModSystem).Assembly
        .GetType("VSDirector.Replay.DirectorReplayManager", throwOnError: true)!
        .GetMethod(
            "ShouldPlaySound",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay sound decision was not found.");
    if (!CallsMethod(replaySoundDecision, isRecordedWeatherSound))
    {
        throw new InvalidOperationException(
            "Replay sound rendering does not consult the recorded weather filter.");
    }
    foreach (string targetName in new[] { "asyncParticleSpawn", "updateSounds", "ClientTick" })
    {
        MethodBase target = replayWeatherTargets.Single(target => target.Name == targetName);
        IReadOnlyList<CodeInstruction> instructions = PatchProcessor.GetCurrentInstructions(target);
        if (!instructions.Any(instruction =>
                instruction.operand is MethodInfo method
                && method == replaySnapshotResolver)
            || !instructions.Any(instruction =>
                instruction.operand is MethodInfo method
                && method == replayClimateResolver))
        {
            throw new InvalidOperationException(
                $"Replay weather input resolver was not emitted into "
                + $"{target.DeclaringType?.FullName}.{targetName}.");
        }
    }
    MethodBase replayWeatherParticleTarget = replayWeatherTargets.Single(
        target => target.Name == "asyncParticleSpawn");
    Patches? replayWeatherParticlePatchInfo = Harmony.GetPatchInfo(replayWeatherParticleTarget);
    if (replayWeatherParticlePatchInfo?.Prefixes.Any(
            patch => patch.PatchMethod == replayWeatherParticlePrefix) != true
        || replayWeatherParticlePatchInfo.Finalizers.Any(
            patch => patch.PatchMethod == replayWeatherParticleFinalizer) != true)
    {
        throw new InvalidOperationException(
            "Replay weather particle scope was not installed around the async callback.");
    }

    Type replayWeatherRuntime = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayVisualRuntime",
        throwOnError: true)!;
    PropertyInfo replayWeatherCurrent = replayWeatherRuntime.GetProperty(
        "Current",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay visual runtime current state was not found.");
    MethodInfo getWeatherSnapshot = replayWeatherVisualPatch.GetMethod(
        "GetBlendedWeatherDataForReplay",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather snapshot resolver was not found.");
    MethodInfo getWeatherClimate = replayWeatherVisualPatch.GetMethod(
        "GetClimateForReplay",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay weather climate resolver was not found.");
    WeatherSystemClient weatherProbe =
        (WeatherSystemClient)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSystemClient));
    WeatherDataSnapshot sourceWeatherSnapshot = new();
    sourceWeatherSnapshot.BlendedPrecType = EnumPrecipitationType.Auto;
    sourceWeatherSnapshot.PrecIntensity = 0.12f;
    FieldInfo weatherCachedField = typeof(WeatherSystemClient).GetField(
        "blendedWeatherDataCached",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Weather cached snapshot field was not found.");
    weatherCachedField.SetValue(weatherProbe, sourceWeatherSnapshot);
    ClimateCondition sourceWeatherClimate = new() { Temperature = 8f, Rainfall = 0.1f };
    typeof(WeatherSystemClient).GetField(
        "clientClimateCond",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
            weatherProbe,
            sourceWeatherClimate);
    clearReplayWeatherOverrides.Invoke(null, null);
    replayWeatherCurrent.SetValue(
        null,
        new DirectorReplayVisualSettings
        {
            Environment = new DirectorReplayEnvironmentVisuals
            {
                Weather = DirectorReplayWeatherMode.Rain,
                Precipitation = 0.8f
            }
        });
    DirectorReplayEnvironmentVisuals replayRainEnvironment =
        ((DirectorReplayVisualSettings)replayWeatherCurrent.GetValue(null)!).Environment;
    try
    {
        advanceReplayPrecipitation.Invoke(null, new object[] { replayRainEnvironment, 0.1f });
        object? replaySnapshotValue = getWeatherSnapshot.Invoke(null, new object[] { weatherProbe });
        object? replayClimateValue = getWeatherClimate.Invoke(null, new object[] { weatherProbe });
        if (replaySnapshotValue is not WeatherDataSnapshot replaySnapshot
            || ReferenceEquals(replaySnapshot, sourceWeatherSnapshot)
            || replaySnapshot.BlendedPrecType != EnumPrecipitationType.Rain
            || replaySnapshot.PrecIntensity < 0.14f
            || replaySnapshot.PrecIntensity > 0.16f
            || sourceWeatherSnapshot.BlendedPrecType != EnumPrecipitationType.Auto
            || Math.Abs(sourceWeatherSnapshot.PrecIntensity - 0.12f) > 0.0001f
            || replayClimateValue is not ClimateCondition replayClimate
            || ReferenceEquals(replayClimate, sourceWeatherClimate)
            || replayClimate.Rainfall < 0.14f
            || replayClimate.Rainfall > 0.16f
            || replayClimate.RainCloudOverlay < 0.14f
            || replayClimate.RainCloudOverlay > 0.16f
            || Math.Abs(sourceWeatherClimate.Rainfall - 0.1f) > 0.0001f)
        {
            throw new InvalidOperationException(
                "Replay weather resolvers did not return isolated ramped rain and climate copies.");
        }

        for (int index = 0; index < 8; index++)
        {
            advanceReplayPrecipitation.Invoke(null, new object[] { replayRainEnvironment, 0.1f });
        }
        replaySnapshotValue = getWeatherSnapshot.Invoke(null, new object[] { weatherProbe });
        if (replaySnapshotValue is not WeatherDataSnapshot settledRain
            || Math.Abs(settledRain.PrecIntensity - 0.8f) > 0.0001f)
        {
            throw new InvalidOperationException(
                "Replay weather precipitation ramp did not settle at its target.");
        }

        replayWeatherCurrent.SetValue(
            null,
            new DirectorReplayVisualSettings
            {
                Environment = new DirectorReplayEnvironmentVisuals
                {
                    Weather = DirectorReplayWeatherMode.Storm,
                    Precipitation = 1f
                }
            });
        clearReplayWeatherOverrides.Invoke(null, null);
        replayWeatherCurrent.SetValue(
            null,
            new DirectorReplayVisualSettings
            {
                Environment = new DirectorReplayEnvironmentVisuals
                {
                    Weather = DirectorReplayWeatherMode.Storm,
                    Precipitation = 1f
                }
            });
        DirectorReplayEnvironmentVisuals replayStormEnvironment =
            ((DirectorReplayVisualSettings)replayWeatherCurrent.GetValue(null)!).Environment;
        advanceReplayPrecipitation.Invoke(null, new object[] { replayStormEnvironment, 0.1f });
        object? stormSnapshotValue = getWeatherSnapshot.Invoke(null, new object[] { weatherProbe });
        if (stormSnapshotValue is not WeatherDataSnapshot stormSnapshot
            || stormSnapshot.BlendedPrecType != EnumPrecipitationType.Rain
            || stormSnapshot.nearLightningRate <= 0f
            || stormSnapshot.nearLightningRate > 0.0035f
            || stormSnapshot.distantLightningRate <= 0f
            || stormSnapshot.distantLightningRate > 0.0075f)
        {
            throw new InvalidOperationException(
                "Replay storm did not use bounded vanilla-scale lightning rates.");
        }

        DirectorReplayParticleEvent weatherParticle = new()
        {
            Kind = DirectorReplayParticleKind.Provider,
            ProviderType = "Vintagestory.GameContent.WeatherParticleProps, VSEssentials"
        };
        DirectorReplayParticleEvent taggedWeatherParticle = new()
        {
            Kind = DirectorReplayParticleKind.Provider,
            ProviderType = "Vintagestory.API.Common.SimpleParticleProperties, VintagestoryAPI",
            IsWeatherParticle = true
        };
        DirectorReplayParticleEvent ordinaryParticle = new()
        {
            Kind = DirectorReplayParticleKind.Provider,
            ProviderType = "Vintagestory.API.Common.AdvancedParticleProperties, VintagestoryAPI"
        };
        DirectorReplaySoundEvent weatherSound = new()
        {
            SoundType = (int)EnumSoundType.Weather,
            Location = "sounds/weather/tracks/rain-leafy.ogg"
        };
        DirectorReplaySoundEvent ordinarySound = new()
        {
            SoundType = (int)EnumSoundType.Ambient,
            Location = "sounds/ambient/forest.ogg"
        };
        if (isRecordedWeatherParticle.Invoke(null, new object[] { weatherParticle }) is not true
            || isRecordedWeatherParticle.Invoke(null, new object[] { taggedWeatherParticle }) is not true
            || isRecordedWeatherParticle.Invoke(null, new object[] { ordinaryParticle }) is not false
            || isRecordedWeatherSound.Invoke(null, new object[] { weatherSound }) is not true
            || isRecordedWeatherSound.Invoke(null, new object[] { ordinarySound }) is not false)
        {
            throw new InvalidOperationException(
                "Custom weather did not filter only recorded weather particle providers.");
        }

        if (allowsLiveWeatherParticles.GetValue(null) is not false
            || shouldAllowReplayParticleSpawn.Invoke(null, new object[] { true }) is not false
            || shouldAllowReplayParticleSpawn.Invoke(null, new object[] { false }) is not true)
        {
            throw new InvalidOperationException(
                "Replay weather particles bypassed suppression outside their async callback.");
        }

        bool outerScopeEntered = false;
        bool innerScopeEntered = false;
        try
        {
            object?[] outerPrefixArguments = { false };
            replayWeatherParticlePrefix.Invoke(null, outerPrefixArguments);
            outerScopeEntered = outerPrefixArguments[0] is true;
            object?[] innerPrefixArguments = { false };
            replayWeatherParticlePrefix.Invoke(null, innerPrefixArguments);
            innerScopeEntered = innerPrefixArguments[0] is true;
            bool leakedToAnotherThread = Task.Run(
                () => allowsLiveWeatherParticles.GetValue(null) is true)
                .GetAwaiter()
                .GetResult();
            if (!outerScopeEntered
                || !innerScopeEntered
                || allowsLiveWeatherParticles.GetValue(null) is not true
                || isWeatherParticleSpawnScope.GetValue(null) is not true
                || shouldAllowReplayParticleSpawn.Invoke(null, new object[] { true }) is not true
                || leakedToAnotherThread)
            {
                throw new InvalidOperationException(
                    "Replay weather particle scope did not selectively bypass suppression.");
            }

            replayWeatherParticleFinalizer.Invoke(null, new object?[] { true, null });
            innerScopeEntered = false;
            if (allowsLiveWeatherParticles.GetValue(null) is not true)
            {
                throw new InvalidOperationException(
                    "Nested replay weather particle scope was released too early.");
            }

            InvalidOperationException marker = new("weather-particle-finalizer-probe");
            object? finalizerResult = replayWeatherParticleFinalizer.Invoke(
                null,
                new object?[] { true, marker });
            outerScopeEntered = false;
            if (!ReferenceEquals(finalizerResult, marker)
                || isWeatherParticleSpawnScope.GetValue(null) is not false
                || allowsLiveWeatherParticles.GetValue(null) is not false)
            {
                throw new InvalidOperationException(
                    "Replay weather particle scope did not clean up after the callback.");
            }
        }
        finally
        {
            if (innerScopeEntered)
            {
                replayWeatherParticleFinalizer.Invoke(null, new object?[] { true, null });
            }
            if (outerScopeEntered)
            {
                replayWeatherParticleFinalizer.Invoke(null, new object?[] { true, null });
            }
        }

        replayWeatherCurrent.SetValue(
            null,
            new DirectorReplayVisualSettings
            {
                Environment = new DirectorReplayEnvironmentVisuals
                {
                    Weather = DirectorReplayWeatherMode.Recorded,
                    Precipitation = 0.8f
                }
            });
        object?[] recordedPrefixArguments = { false };
        replayWeatherParticlePrefix.Invoke(null, recordedPrefixArguments);
        try
        {
            if (recordedPrefixArguments[0] is not true
                || allowsLiveWeatherParticles.GetValue(null) is not false
                || isWeatherParticleSpawnScope.GetValue(null) is not true
                || shouldAllowReplayParticleSpawn.Invoke(null, new object[] { true }) is not false)
            {
                throw new InvalidOperationException(
                    "Recorded weather incorrectly bypassed deterministic particle suppression.");
            }
        }
        finally
        {
            replayWeatherParticleFinalizer.Invoke(
                null,
                new object?[] { recordedPrefixArguments[0], null });
        }

        if (isWeatherParticleSpawnScope.GetValue(null) is not false
            || isRecordedWeatherParticle.Invoke(null, new object[] { weatherParticle }) is not false
            || isRecordedWeatherSound.Invoke(null, new object[] { weatherSound }) is not false)
        {
            throw new InvalidOperationException(
                "Recorded weather mode incorrectly filtered its recorded weather particles.");
        }
    }
    finally
    {
        replayWeatherCurrent.SetValue(null, null);
    }
    Console.WriteLine("PASS replay weather visual snapshot and ambient hooks");

    Type chunkTessellationRevisionPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayChunkTessellationRevisionPatch",
        throwOnError: true)!;
    MethodInfo installChunkTessellationRevisionPatch = chunkTessellationRevisionPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "Replay chunk tessellation revision patch installer was not found.");
    if (installChunkTessellationRevisionPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException(
            "Replay chunk tessellation revision patch was not installed.");
    }
    if (chunkTessellationRevisionPatch.GetProperty(
            "Targets",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
            is not IReadOnlyList<MethodBase> chunkTessellationRevisionTargets
        || chunkTessellationRevisionTargets.Count != 5
        || chunkTessellationRevisionTargets.Any(target =>
            Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException(
            "Replay chunk tessellation generation, upload, or completion targets were not patched.");
    }

    MethodInfo beginChunkRedraw = chunkTessellationRevisionPatch.GetMethod(
        "BeginRedraw",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk redraw revision starter was not found.");
    MethodInfo isCurrentChunkRevision = chunkTessellationRevisionPatch.GetMethod(
        "IsCurrentRevision",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk redraw revision query was not found.");
    MethodInfo tesselateChunkPrefix = chunkTessellationRevisionPatch.GetMethod(
        "TesselateChunkPrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk tessellation ticket creator was not found.");
    MethodInfo tesselateChunkFinalizer = chunkTessellationRevisionPatch.GetMethod(
        "TesselateChunkFinalizer",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk tessellation ticket cleanup was not found.");
    MethodInfo tesselatedChunkConstructorPostfix = chunkTessellationRevisionPatch.GetMethod(
        "TesselatedChunkConstructorPostfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay generated chunk mesh ticket handoff was not found.");
    MethodInfo addTesselatedChunkPrefix = chunkTessellationRevisionPatch.GetMethod(
        "AddTesselatedChunkPrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk mesh upload revision guard was not found.");
    MethodInfo mergeTesselatedChunkPrefix = chunkTessellationRevisionPatch.GetMethod(
        "MergeTesselatedChunkPrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay merged chunk mesh ticket handoff was not found.");
    MethodInfo triggerChunkRetesselatedPrefix = chunkTessellationRevisionPatch.GetMethod(
        "TriggerChunkRetesselatedPrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay stale chunk completion guard was not found.");

    ClientWorldMap revisionProbeWorldMap = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(
        typeof(ClientWorldMap));
    ClientMain revisionProbeGame = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
    revisionProbeGame.WorldMap = revisionProbeWorldMap;
    ChunkTesselatorManager revisionProbeTesselator =
        (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
    AccessTools.Field(typeof(ChunkTesselatorManager), "game")?.SetValue(
        revisionProbeTesselator,
        revisionProbeGame);

    long staleChunkRevision = (long)(beginChunkRedraw.Invoke(
        null,
        new object[] { revisionProbeWorldMap, 0, 0, 0 }) ?? -1L);
    object?[] staleTicketArguments = { revisionProbeTesselator, 0, 0, 0, null };
    tesselateChunkPrefix.Invoke(null, staleTicketArguments);
    object staleTicket = staleTicketArguments[4]
        ?? throw new InvalidOperationException("Replay chunk tessellation did not create a revision ticket.");
    TesselatedChunk staleTessellation = (TesselatedChunk)RuntimeHelpers.GetUninitializedObject(
        typeof(TesselatedChunk));
    tesselatedChunkConstructorPostfix.Invoke(null, new object[] { staleTessellation });
    tesselateChunkFinalizer.Invoke(null, new object?[] { null, staleTicket });
    long currentChunkRevision = (long)(beginChunkRedraw.Invoke(
        null,
        new object[] { revisionProbeWorldMap, 0, 0, 0 }) ?? -1L);
    bool staleChunkUploadAllowed = (bool)(addTesselatedChunkPrefix.Invoke(
        null,
        new object[] { staleTessellation }) ?? true);
    bool staleChunkCompletionAllowed = (bool)(triggerChunkRetesselatedPrefix.Invoke(
        null,
        new object[] { new Vec3i(0, 0, 0) }) ?? true);
    if (staleChunkRevision >= currentChunkRevision
        || isCurrentChunkRevision.Invoke(
            null,
            new object[] { revisionProbeWorldMap, 0, 0, 0, staleChunkRevision }) is not false
        || isCurrentChunkRevision.Invoke(
            null,
            new object[] { revisionProbeWorldMap, 0, 0, 0, currentChunkRevision }) is not true
        || staleChunkUploadAllowed
        || staleChunkCompletionAllowed)
    {
        throw new InvalidOperationException(
            "An obsolete replay chunk mesh can still upload or release callbacks for a newer cabinet state.");
    }

    object?[] queuedTicketArguments = { revisionProbeTesselator, 0, 0, 0, null };
    tesselateChunkPrefix.Invoke(null, queuedTicketArguments);
    object queuedTicket = queuedTicketArguments[4]
        ?? throw new InvalidOperationException("Queued replay mesh did not receive a revision ticket.");
    TesselatedChunk mergedQueueEntry = (TesselatedChunk)RuntimeHelpers.GetUninitializedObject(
        typeof(TesselatedChunk));
    tesselatedChunkConstructorPostfix.Invoke(null, new object[] { mergedQueueEntry });
    tesselateChunkFinalizer.Invoke(null, new object?[] { null, queuedTicket });
    beginChunkRedraw.Invoke(null, new object[] { revisionProbeWorldMap, 0, 0, 0 });
    object?[] incomingTicketArguments = { revisionProbeTesselator, 0, 0, 0, null };
    tesselateChunkPrefix.Invoke(null, incomingTicketArguments);
    object incomingTicket = incomingTicketArguments[4]
        ?? throw new InvalidOperationException("Incoming replay mesh did not receive a revision ticket.");
    TesselatedChunk incomingTessellation = (TesselatedChunk)RuntimeHelpers.GetUninitializedObject(
        typeof(TesselatedChunk));
    tesselatedChunkConstructorPostfix.Invoke(null, new object[] { incomingTessellation });
    tesselateChunkFinalizer.Invoke(null, new object?[] { null, incomingTicket });
    object?[] mergeArguments = { mergedQueueEntry, incomingTessellation, false };
    bool runNormalMerge = (bool)(mergeTesselatedChunkPrefix.Invoke(null, mergeArguments) ?? false);
    bool mergedLatestUploadAllowed = (bool)(addTesselatedChunkPrefix.Invoke(
        null,
        new object[] { mergedQueueEntry }) ?? false);
    if (!runNormalMerge || !mergedLatestUploadAllowed)
    {
        throw new InvalidOperationException(
            "The normal chunk queue lost the latest replay revision when merging tessellated output.");
    }
    Console.WriteLine("PASS replay chunk mesh upload revision ordering");

    Type remoteChunkPriorityPatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayRemoteChunkPriorityPatch",
        throwOnError: true)!;
    MethodInfo installRemoteChunkPriorityPatch = remoteChunkPriorityPatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Remote chunk mesh-priority patch installer was not found.");
    if (installRemoteChunkPriorityPatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Remote chunk mesh-priority patch was not installed.");
    }
    MethodBase remoteChunkPriorityTarget = remoteChunkPriorityPatch.GetProperty(
        "TargetMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetValue(null) as MethodBase
        ?? throw new InvalidOperationException("Remote chunk mesh-priority target was not resolved.");
    if (Harmony.GetPatchInfo(remoteChunkPriorityTarget)?.Owners.Contains(harmonyId) != true)
    {
        throw new InvalidOperationException("Remote chunk mesh-priority method was not patched.");
    }
    MethodBase remoteMapChunkTarget = remoteChunkPriorityPatch.GetProperty(
        "MapChunkTargetMethod",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetValue(null) as MethodBase
        ?? throw new InvalidOperationException("Remote map-chunk backfill target was not resolved.");
    if (Harmony.GetPatchInfo(remoteMapChunkTarget)?.Owners.Contains(harmonyId) != true)
    {
        throw new InvalidOperationException("Remote map-chunk backfill method was not patched.");
    }
    MethodInfo removeQueuedChunkIfPresent = remoteChunkPriorityPatch.GetMethod(
        "RemoveQueuedChunkIfPresent",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Remote chunk priority queue guard was not found.");
    MethodInfo retessellateExistingChunkInOrder = remoteChunkPriorityPatch.GetMethod(
        "RetessellateExistingChunkInOrder",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Ordered replay chunk retessellation helper was not found.");
    MethodInfo markChunkDirty = AccessTools.Method(
        typeof(ClientWorldMap),
        nameof(ClientWorldMap.MarkChunkDirty),
        new[]
        {
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(bool),
            typeof(bool),
            typeof(Action),
            typeof(bool),
            typeof(bool)
        })
        ?? throw new InvalidOperationException("Client chunk dirty method was not found.");
    MethodInfo setChunkDirty = AccessTools.Method(
        typeof(ClientWorldMap),
        "SetChunkDirty",
        new[] { typeof(long), typeof(bool), typeof(bool), typeof(bool) })
        ?? throw new InvalidOperationException("Client chunk priority method was not found.");
    UniqueQueue<long> emptyDirtyChunks = new();
    removeQueuedChunkIfPresent.Invoke(null, new object[] { emptyDirtyChunks, 123L });
    UniqueQueue<long> queuedDirtyChunks = new();
    queuedDirtyChunks.Enqueue(123L);
    queuedDirtyChunks.Enqueue(456L);
    removeQueuedChunkIfPresent.Invoke(null, new object[] { queuedDirtyChunks, 123L });
    removeQueuedChunkIfPresent.Invoke(null, new object[] { queuedDirtyChunks, 789L });
    if (emptyDirtyChunks.Count != 0
        || queuedDirtyChunks.Count != 1
        || queuedDirtyChunks.Contains(123L)
        || !queuedDirtyChunks.Contains(456L))
    {
        throw new InvalidOperationException(
            "Remote chunk priority queue guard did not safely skip absent entries.");
    }
    // The virtual call token can resolve to WorldMap.MarkChunkDirty rather
    // than ClientWorldMap.MarkChunkDirty. The important invariant here is
    // that this ordered path never performs the explicit priority promotion.
    if (CallsMethodReference(retessellateExistingChunkInOrder, setChunkDirty))
    {
        throw new InvalidOperationException(
            "Ordered replay retessellation can still overtake an older normal chunk upload.");
    }
    MethodInfo shouldPrioritizeRemoteChunk = remoteChunkPriorityPatch.GetMethod(
        "ShouldPrioritize",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Remote chunk priority predicate was not found.");
    Type cameraRuntime = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorCameraRuntime",
        throwOnError: true)!;
    PropertyInfo pendingRemoteCameraPose = cameraRuntime.GetProperty(
        "PendingRemoteCameraPose",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Pending remote camera pose was not exposed.");
    PropertyInfo pendingRemoteTargetPose = cameraRuntime.GetProperty(
        "PendingRemoteTargetPose",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Pending remote target pose was not exposed.");
    try
    {
        pendingRemoteCameraPose.SetValue(null, new CameraPreset
        {
            X = 320d,
            Z = 640d,
            Dimension = 0
        });
        pendingRemoteTargetPose.SetValue(null, new CameraPreset
        {
            X = 960d,
            Z = 1280d,
            Dimension = 0
        });
        bool cameraChunkPrioritized = (bool)(shouldPrioritizeRemoteChunk.Invoke(
            null,
            new object[] { 10, 0, 20 }) ?? false);
        bool targetChunkPrioritized = (bool)(shouldPrioritizeRemoteChunk.Invoke(
            null,
            new object[] { 30, 0, 40 }) ?? false);
        if (!cameraChunkPrioritized || !targetChunkPrioritized)
        {
            throw new InvalidOperationException(
                "Pending remote follow positions did not prioritize their incoming chunks.");
        }
    }
    finally
    {
        pendingRemoteCameraPose.SetValue(null, null);
        pendingRemoteTargetPose.SetValue(null, null);
    }
    Console.WriteLine("PASS remote chunk mesh-priority protection");

    Type blockEntityStateCapturePatch = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayBlockEntityStateCapturePatch",
        throwOnError: true)!;
    MethodInfo installBlockEntityStateCapturePatch = blockEntityStateCapturePatch.GetMethod(
        "TryPatch",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Block-entity state capture patch installer was not found.");
    if (installBlockEntityStateCapturePatch.Invoke(null, new object[] { harmony, logger }) is not true)
    {
        throw new InvalidOperationException("Block-entity state capture patch was not installed.");
    }
    if (blockEntityStateCapturePatch.GetProperty(
            "Targets",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
            is not IReadOnlyList<MethodBase> blockEntityStateTargets
        || blockEntityStateTargets.Count == 0
        || blockEntityStateTargets.Any(target =>
            Harmony.GetPatchInfo(target)?.Owners.Contains(harmonyId) != true))
    {
        throw new InvalidOperationException("One or more block-entity state transitions were not patched.");
    }
    MethodBase[] requiredBlockEntityStateTargets =
    {
        AccessTools.Method(
            typeof(BlockEntity),
            nameof(BlockEntity.MarkDirty),
            new[] { typeof(bool), typeof(IPlayer) }),
        AccessTools.Method(
            typeof(BlockEntityGenericTypedContainer),
            nameof(BlockEntityGenericTypedContainer.FromTreeAttributes),
            new[] { typeof(ITreeAttribute), typeof(IWorldAccessor) }),
        AccessTools.Method(
            typeof(BlockEntityFirepit),
            nameof(BlockEntityFirepit.FromTreeAttributes),
            new[] { typeof(ITreeAttribute), typeof(IWorldAccessor) }),
        AccessTools.Method(
            typeof(BlockEntityOpenableContainer),
            nameof(BlockEntityOpenableContainer.OnReceivedServerPacket),
            new[] { typeof(int), typeof(byte[]) }),
        AccessTools.Method(
            typeof(AnimationUtil),
            nameof(AnimationUtil.StartAnimation),
            new[] { typeof(AnimationMetaData) }),
        AccessTools.Method(
            typeof(AnimationUtil),
            nameof(AnimationUtil.StopAnimation),
            new[] { typeof(string) }),
        AccessTools.Method(
            typeof(BlockEntityAnimationUtil),
            "OnAnimationsStateChange",
            new[] { typeof(bool) }),
        AccessTools.Constructor(
            typeof(BlockEntityAnimationUtil),
            new[] { typeof(ICoreAPI), typeof(BlockEntity) }),
        AccessTools.Method(
            typeof(BEBehaviorDoor),
            nameof(BEBehaviorDoor.ToggleDoorState),
            new[] { typeof(IPlayer), typeof(bool) }),
        AccessTools.Method(
            typeof(GeneralPacketHandler),
            "HandleBlockEntities",
            new[] { typeof(Packet_Server) })
    };
    if (requiredBlockEntityStateTargets.Any(target =>
            !blockEntityStateTargets.Contains(target)))
    {
        throw new InvalidOperationException(
            "Block-entity dirty, synchronization, packet, or animation coverage is incomplete.");
    }
    MethodInfo captureDoorSet = blockEntityStateCapturePatch.GetMethod(
        "CaptureDoorSet",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Animated door state capture handoff was not found.");
    MethodInfo captureObservedBlockEntityState = replayManagerType.GetMethod(
        "CaptureObservedBlockEntityState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Observed block-entity state capture method was not found.");
    MethodInfo doorReplayOwnershipCheck = replayManagerType.GetMethod(
        "IsReplayBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay door ownership query was not found.");
    if (!CallsMethod(captureDoorSet, captureObservedBlockEntityState)
        || !CallsMethod(captureDoorSet, doorReplayOwnershipCheck))
    {
        throw new InvalidOperationException(
            "Animated door transitions are not captured or isolated by replay ownership.");
    }
    MethodInfo recordObservedBlockEvent = replayManagerType.GetMethod(
        "RecordBlockEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Observed block-entity event writer was not found.");
    MethodInfo getObservedBlockEntityState = replayManagerType.GetMethod(
        "TryGetObservedBlockEntityState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Observed block-entity baseline cache was not found.");
    if (!CallsMethod(captureObservedBlockEntityState, recordObservedBlockEvent)
        || !CallsMethod(captureObservedBlockEntityState, getObservedBlockEntityState))
    {
        throw new InvalidOperationException(
            "Dynamic block-entity capture does not seed events from the pre-transition state.");
    }
    if (blockEntityStateCapturePatch.GetField(
            "stateBoundaryDepths",
            BindingFlags.Static | BindingFlags.NonPublic) is null
        || blockEntityStateCapturePatch.GetField(
            "livePacketDepth",
            BindingFlags.Static | BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Nested cooking-container synchronization can be captured before its outer state is complete.");
    }
    MethodInfo suppressNestedLivePacketCapture = blockEntityStateCapturePatch.GetMethod(
        "SuppressNestedLivePacketCapture",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Live block-entity packet scope guard was not found.");
    MethodInfo isWorldPlaybackActive = replayManagerType.GetProperty(
        "IsWorldPlaybackActive",
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod
        ?? throw new InvalidOperationException("Replay world-playback state was not found.");
    if (!CallsMethod(suppressNestedLivePacketCapture, isWorldPlaybackActive))
    {
        throw new InvalidOperationException(
            "Quick replay can suppress live block-entity packets without rebuilding the world.");
    }
    Console.WriteLine(
        "PASS generic block-entity and cooking-container synchronization capture");

    Type workspaceHud = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorWorkspaceHud",
        throwOnError: true)!;
    MethodInfo composePathProperties = workspaceHud.GetMethod(
        "ComposePathPropertiesPanel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Path properties composer was not found.");
    string[] pathInspectorElementKeys = PatchProcessor
        .GetCurrentInstructions(composePathProperties)
        .Select(instruction => instruction.operand)
        .OfType<string>()
        .Where(value => value is "pathName"
            or "pathTags"
            or "pathDescription"
            or "pathSegmentMarker"
            or "pathKeyframeName"
            or "pathFrameStart"
            or "pathPosX")
        .ToArray();
    if (pathInspectorElementKeys.Count(value => value == "pathName") != 1
        || pathInspectorElementKeys.Count(value => value == "pathTags") != 1
        || pathInspectorElementKeys.Count(value => value == "pathDescription") != 1
        || pathInspectorElementKeys.Any(value => value is "pathSegmentMarker"
            or "pathKeyframeName"
            or "pathFrameStart"
            or "pathPosX"))
    {
        throw new InvalidOperationException(
            "Path properties must expose only name, tags, and description inputs.");
    }
    Console.WriteLine("PASS path properties expose metadata inputs only");

    MethodInfo setButtonEnabledIfPresent = workspaceHud.GetMethod(
        "SetButtonEnabledIfPresent",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Workspace button state helper was not found.");
    ConstructorInfo composerConstructor = typeof(GuiComposer).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(ICoreClientAPI), typeof(ElementBounds), typeof(string) },
        modifiers: null)
        ?? throw new InvalidOperationException("GuiComposer probe constructor was not found.");

    GuiComposer staleComposer = (GuiComposer)composerConstructor.Invoke(
        new object?[] { null, ElementBounds.Empty, "vsdirector-stale-timeline-probe" });
    setButtonEnabledIfPresent.Invoke(null, new object[] { staleComposer, "pause", false });

    GuiComposer wrongTypeComposer = (GuiComposer)composerConstructor.Invoke(
        new object?[] { null, ElementBounds.Empty, "vsdirector-wrong-control-probe" });
    wrongTypeComposer.AddInteractiveElement(
        new GuiElementCustomRender(null!, ElementBounds.Fixed(0, 0, 10, 10), (_, _) => { }),
        "pause");
    setButtonEnabledIfPresent.Invoke(null, new object[] { wrongTypeComposer, "pause", false });

    Type directorButton = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorButton",
        throwOnError: true)!;
    ConstructorInfo directorButtonConstructor = directorButton.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        new[]
        {
            typeof(ICoreClientAPI), typeof(string), typeof(ActionConsumable),
            typeof(ElementBounds), typeof(EnumButtonStyle)
        },
        modifiers: null)
        ?? throw new InvalidOperationException("Director button probe constructor was not found.");
    GuiElementControl pauseButton = (GuiElementControl)directorButtonConstructor.Invoke(
        new object?[]
        {
            null,
            "Pause",
            (ActionConsumable)(() => true),
            ElementBounds.Fixed(0, 0, 10, 10),
            EnumButtonStyle.Normal
        });
    GuiComposer replayComposer = (GuiComposer)composerConstructor.Invoke(
        new object?[] { null, ElementBounds.Empty, "vsdirector-replay-timeline-probe" });
    replayComposer.AddInteractiveElement(pauseButton, "pause");
    setButtonEnabledIfPresent.Invoke(null, new object[] { replayComposer, "pause", false });
    if (pauseButton.Enabled)
    {
        throw new InvalidOperationException("Workspace replay pause button was not disabled.");
    }
    setButtonEnabledIfPresent.Invoke(null, new object[] { replayComposer, "pause", true });
    if (!pauseButton.Enabled)
    {
        throw new InvalidOperationException("Workspace replay pause button was not enabled.");
    }
    int capturedButtonClicks = 0;
    // PatchProbe does not create a client window, so the API default GUI
    // scale is zero. Production bounds are scaled by RuntimeEnv.GUIScale;
    // use the neutral scale here so the synthetic click has real dimensions.
    float previousGuiScale = Vintagestory.API.Config.RuntimeEnv.GUIScale;
    Vintagestory.API.Config.RuntimeEnv.GUIScale = 1f;
    ElementBounds capturedButtonParent = ElementBounds.FixedSize(100d, 100d)
        .WithEmptyParent();
    capturedButtonParent.CalcWorldBounds();
    ElementBounds capturedButtonBounds = ElementBounds.Fixed(0d, 0d, 10d, 10d)
        .WithParent(capturedButtonParent);
    capturedButtonBounds.CalcWorldBounds();
    GuiElementControl capturedButton = (GuiElementControl)directorButtonConstructor.Invoke(
        new object?[]
        {
            null,
            "Captured",
            (ActionConsumable)(() =>
            {
                capturedButtonClicks++;
                return true;
            }),
            capturedButtonBounds,
            EnumButtonStyle.Normal
        });
    GuiComposer captureComposer = (GuiComposer)composerConstructor.Invoke(
        new object?[] { null, ElementBounds.Empty, "vsdirector-button-capture-probe" });
    captureComposer.AddInteractiveElement(capturedButton, "captured");
    MouseEvent capturedButtonPress = new(5, 5, EnumMouseButton.Left, 0);
    captureComposer.OnMouseDown(capturedButtonPress);
    if (!capturedButtonPress.Handled)
    {
        throw new InvalidOperationException(
            "GuiComposer did not route the in-bounds Director button press.");
    }
    capturedButton.Bounds.CalcWorldBounds();
    MethodInfo managerButtonPrefix = typeof(VSDirectorModSystem).Assembly.GetType(
            "VSDirector.Gui.DirectorGuiManagerButtonMouseUpCapturePatch",
            throwOnError: true)!.GetMethod(
            "Prefix",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            "VSDirector.Gui.DirectorGuiManagerButtonMouseUpCapturePatch",
            "Prefix");
    MouseEvent capturedButtonRelease = new(5, 5, EnumMouseButton.Left, 0);
    bool managerRanOriginal = (bool)managerButtonPrefix.Invoke(
        null,
        new object?[] { capturedButtonRelease })!;
    if (managerRanOriginal
        || capturedButtonClicks != 1
        || !capturedButtonRelease.Handled)
    {
        throw new InvalidOperationException(
            $"GuiManager button capture did not deliver exactly one in-bounds click "
            + $"(ranOriginal={managerRanOriginal}, clicks={capturedButtonClicks}, "
            + $"handled={capturedButtonRelease.Handled}, enabled={capturedButton.Enabled}, "
            + $"bounds={capturedButton.Bounds}).");
    }
    Vintagestory.API.Config.RuntimeEnv.GUIScale = previousGuiScale;
    Console.WriteLine("PASS workspace button release capture lifecycle");
    Console.WriteLine("PASS workspace replay button lifecycle");

    Type directorListMenu = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.GuiElementDirectorListMenu",
        throwOnError: true)!;
    _ = directorListMenu.GetMethod(
        "OpenDirector",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Searchable dropdown open method was not found.");
    MethodInfo matchesDropDownSearch = directorListMenu.GetMethod(
        "MatchesSearch",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Searchable dropdown matcher was not found.");
    bool MatchesDropDownSearch(string name, string value, params string[] terms)
        => matchesDropDownSearch.Invoke(null, new object?[] { name, value, terms }) is true;
    if (!MatchesDropDownSearch("Main Camera", "camera-main", "main")
        || !MatchesDropDownSearch("Main Camera", "camera-main", "CAMERA", "main")
        || !MatchesDropDownSearch("主机位", "camera-main", "主")
        || MatchesDropDownSearch("Main Camera", "camera-main", "actor"))
    {
        throw new InvalidOperationException("Dropdown search did not match display names and values case-insensitively.");
    }
    Console.WriteLine("PASS searchable dropdown option matching");

    Type pathPreviewRenderer = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorPathPreviewRenderer", throwOnError: true)!;
    MethodInfo shouldRenderPathPreview = pathPreviewRenderer.GetMethod(
        "ShouldRenderPreview",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "ShouldRenderPreview");
    bool ShouldRenderPathPreview(bool overlays, bool editor, bool capture, bool offscreen)
        => shouldRenderPathPreview.Invoke(null, new object[] { overlays, editor, capture, offscreen }) is true;
    if (!ShouldRenderPathPreview(overlays: false, editor: true, capture: false, offscreen: false)
        || ShouldRenderPathPreview(overlays: false, editor: false, capture: false, offscreen: false)
        || ShouldRenderPathPreview(overlays: true, editor: true, capture: true, offscreen: false)
        || ShouldRenderPathPreview(overlays: true, editor: true, capture: false, offscreen: true)
        || !ShouldRenderPathPreview(overlays: true, editor: false, capture: false, offscreen: false))
    {
        throw new InvalidOperationException("World preview visibility did not separate editor navigation from capture suppression.");
    }
    Console.WriteLine("PASS free-camera world preview visibility and capture suppression");

    MethodInfo refreshPathPreview = pathPreviewRenderer.GetMethod(
        "Refresh",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "Refresh");
    MethodInfo addReplayOrbitGuide = pathPreviewRenderer.GetMethod(
        "AddReplayOrbitGuide",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "AddReplayOrbitGuide");
    MethodInfo renderPathPreview = pathPreviewRenderer.GetMethod(
        "OnRenderFrame",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "OnRenderFrame");
    MethodInfo renderTrackingBounds = pathPreviewRenderer.GetMethod(
        "RenderTrackingBounds",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "RenderTrackingBounds");
    MethodInfo updateReplayViewportTimeline = typeof(VSDirectorModSystem).GetMethod(
        "UpdateReplayViewportTimeline",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(VSDirectorModSystem), "UpdateReplayViewportTimeline");
    MethodInfo setCurrentReplayCameraGuide = pathPreviewRenderer.GetMethod(
        "SetCurrentReplayCameraGuide",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "SetCurrentReplayCameraGuide");
    MethodInfo buildCameraPreviewMesh = pathPreviewRenderer.GetMethod(
        "BuildCameraMesh",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(pathPreviewRenderer.FullName, "BuildCameraMesh");
    MethodInfo cameraRollGetter = typeof(CameraPreset).GetProperty(nameof(CameraPreset.Roll))?.GetMethod
        ?? throw new MissingMethodException(nameof(CameraPreset), "get_Roll");
    if (!CallsMethodReference(refreshPathPreview, addReplayOrbitGuide)
        || !CallsMethodReference(renderPathPreview, renderTrackingBounds)
        || !CallsMethodReference(updateReplayViewportTimeline, setCurrentReplayCameraGuide)
        || !CallsMethodReference(buildCameraPreviewMesh, cameraRollGetter))
    {
        throw new InvalidOperationException(
            "Replay camera world guides were not connected to orbit refresh, current-camera updates, tracking bounds, and roll-aware frustums.");
    }
    Console.WriteLine("PASS replay camera orbit and tracking world-guide wiring");

    Type replayManager = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayManager",
        throwOnError: true)!;
    MethodInfo isSwimmingAnimationCode = replayManager.GetMethod(
        "IsSwimmingAnimationCode",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay swimming-animation classifier was not found.");
    if (isSwimmingAnimationCode.Invoke(null, new object?[] { "Swim" }) is not true
        || isSwimmingAnimationCode.Invoke(null, new object?[] { "walk" }) is not false)
    {
        throw new InvalidOperationException("Replay swimming-animation classifier returned an invalid result.");
    }
    Console.WriteLine("PASS replay swimming-animation classification");

    MethodInfo captureAnimationStates = replayManager.GetMethod(
        "CaptureAnimationStates",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation capture helper was not found.");
    if (!PatchProcessor.GetCurrentInstructions(captureAnimationStates).Any(instruction =>
            instruction.operand is MethodInfo method
            && string.Equals(method.Name, "get_Animations", StringComparison.Ordinal)
            && typeof(IAnimator).IsAssignableFrom(method.DeclaringType)))
    {
        throw new InvalidOperationException(
            "Replay capture does not preserve running and easing-out animator layers.");
    }
    AnimationManager unreadyAnimationManager = (AnimationManager)RuntimeHelpers.GetUninitializedObject(
        typeof(AnimationManager));
    FieldInfo activeAnimationsField = typeof(AnimationManager).GetField(
        nameof(AnimationManager.ActiveAnimationsByAnimCode),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Animation manager active-animation field was not found.");
    activeAnimationsField.SetValue(
        unreadyAnimationManager,
        new Dictionary<string, AnimationMetaData>
        {
            ["idle"] = new AnimationMetaData
            {
                Code = "idle",
                AnimationSpeed = 1f
            }
        });
    object? capturedAnimationResult = captureAnimationStates.Invoke(
        null,
        new object[] { unreadyAnimationManager, false });
    DirectorReplayAnimationSample[] capturedAnimations =
        ((IEnumerable<DirectorReplayAnimationSample>?)capturedAnimationResult)?.ToArray()
        ?? throw new InvalidOperationException("Replay animation capture returned no collection.");
    if (capturedAnimations.Length != 1
        || !string.Equals(capturedAnimations[0].Code, "idle", StringComparison.Ordinal)
        || Math.Abs(capturedAnimations[0].Progress) > 0.000001f)
    {
        throw new InvalidOperationException("Replay animation capture did not tolerate an unready animator.");
    }
    Console.WriteLine("PASS replay animation capture with unready animator");

    activeAnimationsField.SetValue(
        unreadyAnimationManager,
        new Dictionary<string, AnimationMetaData>
        {
            ["upper-body"] = new AnimationMetaData { Code = "modpose", Animation = "upper-body" },
            ["facial-layer"] = new AnimationMetaData { Code = "modpose", Animation = "facial-layer" }
        });
    DirectorReplayAnimationSample[] layeredCapturedAnimations =
        ((IEnumerable<DirectorReplayAnimationSample>?)captureAnimationStates.Invoke(
            null,
            new object[] { unreadyAnimationManager, false }))?.ToArray()
        ?? Array.Empty<DirectorReplayAnimationSample>();
    if (layeredCapturedAnimations.Length != 2)
    {
        throw new InvalidOperationException(
            "Replay animation capture collapsed mod layers that share one metadata code.");
    }
    Console.WriteLine("PASS replay animation capture preserves mod layers");

    MethodInfo captureAnimationStatesFromManagers = replayManager.GetMethod(
        "CaptureAnimationStatesFromManagers",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay player animation merge helper was not found.");
    MethodInfo captureReplaySample = replayManager.GetMethod(
        "CaptureSample",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay actor capture method was not found.");
    MethodInfo shouldCaptureRecordedThirdPersonPose = replayManager.GetMethod(
        "ShouldCaptureRecordedThirdPersonPose",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Replay camera-mode pose provenance helper was not found.");
    if (!CallsMethod(captureReplaySample, shouldCaptureRecordedThirdPersonPose))
    {
        throw new InvalidOperationException(
            "Replay player capture does not record whether the source was first person.");
    }
    Console.WriteLine("PASS replay player camera-mode pose provenance");
    HashSet<string> playerAnimationGetters = PatchProcessor.GetCurrentInstructions(captureReplaySample)
        .Select(instruction => instruction.operand as MethodInfo)
        .Where(method => method is not null)
        .Select(method => method!.Name)
        .ToHashSet(StringComparer.Ordinal);
    if (!playerAnimationGetters.Contains("get_AnimManager")
        || !playerAnimationGetters.Contains("get_TpAnimManager"))
    {
        throw new InvalidOperationException(
            "Replay player capture does not combine active timing with the third-person manager.");
    }
    AnimationManager firstPersonAnimationManager = (AnimationManager)RuntimeHelpers.GetUninitializedObject(
        typeof(AnimationManager));
    AnimationManager thirdPersonAnimationManager = (AnimationManager)RuntimeHelpers.GetUninitializedObject(
        typeof(AnimationManager));
    activeAnimationsField.SetValue(
        firstPersonAnimationManager,
        new Dictionary<string, AnimationMetaData>
        {
            ["walk-fp"] = new AnimationMetaData
            {
                Code = "walk-fp",
                Animation = "walk-fp",
                AnimationSpeed = 0.5f,
                Weight = 0f,
                EaseInSpeed = 5f,
                ElementWeight = new Dictionary<string, float>
                {
                    ["UpperArmR"] = 80f,
                    ["ItemAnchor"] = 80f
                }
            },
            ["upper-fp"] = new AnimationMetaData
            {
                Code = "modpose-fp",
                Animation = "upper-fp"
            }
        });
    activeAnimationsField.SetValue(
        thirdPersonAnimationManager,
        new Dictionary<string, AnimationMetaData>
        {
            ["walk"] = new AnimationMetaData
            {
                Code = "walk",
                Animation = "walk",
                AnimationSpeed = 1f,
                Weight = 0.001f,
                EaseInSpeed = 10f,
                ElementWeight = new Dictionary<string, float>
                {
                    ["UpperArmR"] = 20f
                }
            },
            ["facial"] = new AnimationMetaData
            {
                Code = "modpose",
                Animation = "facial"
            }
        });
    DirectorReplayAnimationSample[] mergedPlayerAnimations =
        ((IEnumerable<DirectorReplayAnimationSample>?)captureAnimationStatesFromManagers.Invoke(
            null,
            new object[]
            {
                firstPersonAnimationManager,
                thirdPersonAnimationManager,
                true
            }))?.ToArray()
        ?? Array.Empty<DirectorReplayAnimationSample>();
    DirectorReplayAnimationSample? mergedWalk = mergedPlayerAnimations.SingleOrDefault(state =>
        string.Equals(state.Code, "walk", StringComparison.OrdinalIgnoreCase)
        && string.Equals(state.StateCode, "walk", StringComparison.OrdinalIgnoreCase));
    if (mergedPlayerAnimations.Length != 2
        || mergedWalk is null
        || Math.Abs(mergedWalk.AnimationSpeed - 0.5f) > 0.000001f
        || Math.Abs(mergedWalk.Weight - 0.001f) > 0.000001f
        || Math.Abs(mergedWalk.EaseInSpeed - 10f) > 0.000001f
        || mergedWalk.ElementWeights.Count != 1
        || Math.Abs(mergedWalk.ElementWeights["UpperArmR"] - 20f) > 0.000001f
        || mergedPlayerAnimations.Any(state =>
            string.Equals(state.Code, "modpose", StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.StateCode, "upper", StringComparison.OrdinalIgnoreCase))
        || !mergedPlayerAnimations.Any(state =>
            string.Equals(state.Code, "modpose", StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.StateCode, "facial", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException(
            "Replay player animation merge did not reject first-person-only layers.");
    }
    Console.WriteLine("PASS replay player third-person animation merge");

    MethodInfo mergeAnimationCaptureStates = replayManager.GetMethod(
        "MergeAnimationCaptureStates",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation state merge policy was not found.");
    DirectorReplayAnimationSample[] fpOnlyFallbackAnimations =
        ((IEnumerable<DirectorReplayAnimationSample>?)mergeAnimationCaptureStates.Invoke(
            null,
            new object[]
            {
                new List<DirectorReplayAnimationSample>
                {
                    new() { Code = "idle", StateCode = "idle" }
                },
                new List<DirectorReplayAnimationSample>()
            }))?.ToArray()
        ?? Array.Empty<DirectorReplayAnimationSample>();
    if (fpOnlyFallbackAnimations.Length != 0)
    {
        throw new InvalidOperationException(
            "Replay player animation capture still falls back to first-person-only layers when the TP manager is empty.");
    }
    Console.WriteLine("PASS replay player rejects FP-only animation fallback");

    DirectorReplayAnimationSample primaryTiming = new()
    {
        Code = "helditemready",
        StateCode = "helditemready",
        Progress = 0.75f,
        AnimationSpeed = 0.5f,
        EasingFactor = 0.6f,
        Weight = 0f,
        EaseInSpeed = 5f,
        BlendMode = 1,
        ElementWeights = new Dictionary<string, float>
        {
            ["UpperArmR"] = 80f,
            ["ItemAnchor"] = 80f
        }
    };
    DirectorReplayAnimationSample thirdPersonMetadata = new()
    {
        Code = "helditemready",
        StateCode = "helditemready",
        Progress = 0.1f,
        AnimationSpeed = 1f,
        EasingFactor = 0.2f,
        Weight = 0.001f,
        EaseInSpeed = 10f,
        BlendMode = 2,
        ElementWeights = new Dictionary<string, float> { ["UpperArmR"] = 20f }
    };
    DirectorReplayAnimationSample mergedMetadataState =
        ((IEnumerable<DirectorReplayAnimationSample>?)mergeAnimationCaptureStates.Invoke(
            null,
            new object[]
            {
                new List<DirectorReplayAnimationSample> { primaryTiming },
                new List<DirectorReplayAnimationSample> { thirdPersonMetadata }
            }))?.Single()
        ?? throw new InvalidOperationException("Replay animation state merge returned no state.");
    if (Math.Abs(mergedMetadataState.Progress - 0.75f) > 0.000001f
        || Math.Abs(mergedMetadataState.AnimationSpeed - 0.5f) > 0.000001f
        || Math.Abs(mergedMetadataState.EasingFactor - 0.6f) > 0.000001f
        || Math.Abs(mergedMetadataState.Weight - 0.001f) > 0.000001f
        || Math.Abs(mergedMetadataState.EaseInSpeed - 10f) > 0.000001f
        || mergedMetadataState.BlendMode != 2
        || mergedMetadataState.ElementWeights.Count != 1
        || Math.Abs(mergedMetadataState.ElementWeights["UpperArmR"] - 20f) > 0.000001f)
    {
        throw new InvalidOperationException(
            "Replay animation merge did not combine first-person timing with third-person render metadata.");
    }
    Console.WriteLine("PASS replay player timing and third-person metadata merge");

    MethodInfo shouldApplyRecordedAnimationRenderMetadata = replayManager.GetMethod(
        "ShouldApplyRecordedAnimationRenderMetadata",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation render-metadata policy was not found.");
    Entity nonPlayerAnimationEntity = (Entity)RuntimeHelpers.GetUninitializedObject(typeof(EntityItem));
    EntityPlayer playerAnimationEntity = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
    if (shouldApplyRecordedAnimationRenderMetadata.Invoke(
            null,
            new object[] { nonPlayerAnimationEntity }) is not true
        || shouldApplyRecordedAnimationRenderMetadata.Invoke(
            null,
            new object[] { playerAnimationEntity }) is not false)
    {
        throw new InvalidOperationException(
            "Replay players can still apply recorded first-person render metadata.");
    }
    Console.WriteLine("PASS replay players retain local third-person render metadata");

    MethodInfo animationStateKey = replayManager.GetMethod(
        "AnimationStateKey",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation state-key resolver was not found.");
    string resolvedStateKey = animationStateKey.Invoke(
        null,
        new object[]
        {
            new DirectorReplayAnimationSample { Code = "sprint", StateCode = "sprint-shape" }
        }) as string ?? string.Empty;
    string fallbackStateKey = animationStateKey.Invoke(
        null,
        new object[]
        {
            new DirectorReplayAnimationSample { Code = "swim", StateCode = string.Empty }
        }) as string ?? string.Empty;
    if (resolvedStateKey != "sprint-shape" || fallbackStateKey != "swim")
    {
        throw new InvalidOperationException("Replay animation keys do not preserve the shape animation identity.");
    }
    AnimationManager aliasedAnimationManager = new();
    aliasedAnimationManager.ActiveAnimationsByAnimCode["death"] = new AnimationMetaData
    {
        Code = "die",
        Animation = "death"
    };
    DirectorReplayAnimationSample aliasedState = new()
    {
        Code = "die",
        StateCode = "die"
    };
    MethodInfo findActiveAnimationKey = replayManager.GetMethod(
        "FindActiveAnimationKey",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay active animation-key resolver was not found.");
    string aliasedKey = findActiveAnimationKey.Invoke(
        null,
        new object[] { aliasedAnimationManager, aliasedState }) as string ?? string.Empty;
    if (aliasedKey != "death")
    {
        throw new InvalidOperationException("Replay animation aliases would restart an already-active animation.");
    }
    Console.WriteLine("PASS replay animation metadata-code aliases stay active");
    AnimationManager sharedShapeAnimationManager = new();
    sharedShapeAnimationManager.ActiveAnimationsByAnimCode["creativefly"] = new AnimationMetaData
    {
        Code = "creativefly",
        Animation = "creativefly"
    };
    DirectorReplayAnimationSample landingState = new()
    {
        Code = "land",
        StateCode = "creativefly"
    };
    if (findActiveAnimationKey.Invoke(
            null,
            new object[] { sharedShapeAnimationManager, landingState }) is not null)
    {
        throw new InvalidOperationException(
            "Replay animation matching confused landing metadata with creative-flight metadata.");
    }
    sharedShapeAnimationManager.ActiveAnimationsByAnimCode["creativefly"].Code = "land";
    if (!string.Equals(
            findActiveAnimationKey.Invoke(
                null,
                new object[] { sharedShapeAnimationManager, landingState }) as string,
            "creativefly",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Replay animation matching lost the exact metadata/shape animation identity.");
    }
    Console.WriteLine("PASS replay animations distinguish metadata sharing one shape animation");
    MethodInfo vanillaExpressionSelector = AccessTools.Method(
        typeof(EntityBehaviorExtraSkinnable),
        "selectSkinPart",
        new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) })
        ?? throw new InvalidOperationException("Vanilla facial-expression selector was not found.");
    if (vanillaExpressionSelector.ReturnType != typeof(void))
    {
        throw new InvalidOperationException("Vanilla facial-expression selector has an incompatible signature.");
    }
    Console.WriteLine("PASS vanilla facial-expression selector");

    string[] coreVanillaAnimations = (string[])(typeof(VSDirectorModSystem).GetField(
        "CoreVanillaPlayerAnimations",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException("Core vanilla animation catalog was not found."));
    MethodInfo hasCoreVanillaAnimations = AccessTools.Method(
        typeof(VSDirectorModSystem),
        "HasCoreVanillaPlayerAnimations")
        ?? throw new InvalidOperationException("Core vanilla animation validation was not found.");
    MethodInfo isVanillaAnimationCode = AccessTools.Method(
        typeof(VSDirectorModSystem),
        "IsVanillaPlayerAnimationCode")
        ?? throw new InvalidOperationException("Vanilla animation ownership validation was not found.");
    if (!new[] { "walk", "sprint", "swim" }.All(code => coreVanillaAnimations.Contains(
            code,
            StringComparer.OrdinalIgnoreCase))
        || hasCoreVanillaAnimations.Invoke(null, new object?[] { coreVanillaAnimations }) is not true
        || hasCoreVanillaAnimations.Invoke(null, new object?[] { new[] { "walk", "swim" } }) is not false
        || isVanillaAnimationCode.Invoke(null, new object?[] { "walk" }) is not true
        || isVanillaAnimationCode.Invoke(null, new object?[] { "emotes:wave" }) is not false)
    {
        throw new InvalidOperationException("Vanilla player locomotion fallback is incomplete.");
    }
    Console.WriteLine("PASS vanilla player locomotion fallback and mod-animation exclusion");

    MethodInfo isMisclassifiedEmoteAction = AccessTools.Method(
        typeof(VSDirectorModSystem),
        "IsMisclassifiedEmoteAction")
        ?? throw new InvalidOperationException("Legacy emote action migration classifier was not found.");
    DirectorActionAsset legacyEmote = new()
    {
        SourceModId = "game",
        SourceCode = "vanilla-animation:emotes:wave",
        Tags = new List<string> { "vanilla", "player-animation" }
    };
    if (isMisclassifiedEmoteAction.Invoke(null, new object?[] { legacyEmote }) is not true
        || isMisclassifiedEmoteAction.Invoke(null, new object?[]
        {
            new DirectorActionAsset
            {
                SourceModId = "game",
                SourceCode = "vanilla-animation:walk",
                Tags = new List<string> { "vanilla", "player-animation" }
            }
        }) is not false)
    {
        throw new InvalidOperationException("Legacy emote actions cannot be separated from base-game actions.");
    }
    Console.WriteLine("PASS legacy emote actions are separated from base-game actions");
    foreach (string methodName in new[]
    {
        "DetachReplayEntitiesForTick",
        "RunReplayPlayerAnimationTick",
        "SyncReplayPlayerAnimations",
        "SyncExactReplayAnimations",
        "ResetReplayAnimations"
    })
    {
        if (replayManager.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic) is null)
        {
            throw new InvalidOperationException($"Replay observer animation method '{methodName}' was not found.");
        }
    }
    MethodInfo applySample = replayManager.GetMethod(
        "ApplySample",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay sample applier was not found.");
    MethodInfo applyPendingReplayAnimationPose = replayManager.GetMethod(
        "ApplyPendingReplayAnimationPose",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay pending-animation pose updater was not found.");
    MethodInfo advanceReplayPlayerAnimationPose = replayManager.GetMethod(
        "AdvanceReplayPlayerAnimationPose",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay third-person player animation updater was not found.");
    MethodInfo initializePlaybackBinding = replayManager.GetMethod(
        "InitializePlaybackBinding",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay binding initializer was not found.");
    MethodInfo resetReplayAnimations = replayManager.GetMethod(
        "ResetReplayAnimations",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation reset helper was not found.");
    MethodInfo shouldResetActorAnimationManager = typeof(DirectorReplayPlaybackPolicy).GetMethod(
        nameof(DirectorReplayPlaybackPolicy.ShouldResetActorAnimationManager),
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay animation reset policy was not found.");
    if (!CallsMethod(applySample, shouldResetActorAnimationManager)
        || !CallsMethod(applySample, replayManager.GetMethod(
            "RunReplayPlayerAnimationTick",
            BindingFlags.Instance | BindingFlags.NonPublic)!)
        || !CallsMethod(applySample, replayManager.GetMethod(
            "SyncReplayPlayerAnimations",
            BindingFlags.Static | BindingFlags.NonPublic)!)
        || !CallsMethod(applyPendingReplayAnimationPose, advanceReplayPlayerAnimationPose)
        || !CallsMethod(initializePlaybackBinding, resetReplayAnimations))
    {
        throw new InvalidOperationException(
            "Replay player animations are not sanitized and rebuilt through the third-person state machine.");
    }
    MethodInfo shouldDriveReplayAnimation = replayManager.GetMethod(
        "ShouldDriveReplayAnimation",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation clock ownership policy was not found.");
    HashSet<string> replayAnimationMembers = PatchProcessor
        .GetCurrentInstructions(shouldDriveReplayAnimation)
        .Select(instruction => instruction.operand switch
        {
            FieldInfo field => field.Name,
            MethodInfo method => method.Name,
            _ => string.Empty
        })
        .Where(name => name.Length > 0)
        .ToHashSet(StringComparer.Ordinal);
    if (!replayAnimationMembers.Any(name => name.Contains("AnimationStates", StringComparison.Ordinal))
        || !replayAnimationMembers.Any(name => name.Contains("Animations", StringComparison.Ordinal)
            && !name.Contains("AnimationStates", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "Legacy replay animation lists are still advanced by the live physical clock.");
    }
    if (replayManager.GetMethod(
        "RunReplayEntityAnimationTick",
        BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException("Replay animals are still being driven through the live entity tick.");
    }
    MethodInfo startReplayAnimation = replayManager.GetMethod(
        "StartReplayAnimation",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation starter was not found.");
    EntityItem unreadyReplayEntity = (EntityItem)RuntimeHelpers.GetUninitializedObject(typeof(EntityItem));
    object? unreadyStartResult = startReplayAnimation.Invoke(
        null,
        new object[]
        {
            unreadyReplayEntity,
            unreadyAnimationManager,
            new DirectorReplayAnimationSample
            {
                Code = "shape-only",
                StateCode = "shape-only",
                AnimationSpeed = 1f
            }
        });
    if (unreadyStartResult is not null)
    {
        throw new InvalidOperationException("Replay animation started before the entity animator was ready.");
    }
    Console.WriteLine("PASS replay observer animation policy, exact shape keys, and deferred animator startup");

    object replayCapturePolicy = RuntimeHelpers.GetUninitializedObject(replayManager);
    MethodInfo configureActionWorkflowCapture = replayManager.GetMethod(
        "ConfigureActionWorkflowRecordingCapture",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Entity), typeof(Entity) },
        modifiers: null)
        ?? throw new InvalidOperationException("Action-workflow replay capture configuration was not found.");
    MethodInfo isRecordingCaptureEntityEligible = replayManager.GetMethod(
        "IsRecordingCaptureEntityEligible",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Action-workflow replay capture policy was not found.");
    EntityItem actionWorkflowGhost = (EntityItem)RuntimeHelpers.GetUninitializedObject(typeof(EntityItem));
    EntityItem replacedLiveActor = (EntityItem)RuntimeHelpers.GetUninitializedObject(typeof(EntityItem));
    replayManager.GetField(
        "actionWorkflowRecordingGhosts",
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            replayCapturePolicy,
            new Dictionary<Entity, string>(ReferenceEqualityComparer.Instance));
    replayManager.GetField(
        "actionWorkflowRecordingExcludedEntities",
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            replayCapturePolicy,
            new HashSet<Entity>(ReferenceEqualityComparer.Instance));
    configureActionWorkflowCapture.Invoke(
        replayCapturePolicy,
        new object?[] { actionWorkflowGhost, replacedLiveActor });
    if (isRecordingCaptureEntityEligible.Invoke(
            replayCapturePolicy,
            new object?[] { actionWorkflowGhost }) is not true
        || isRecordingCaptureEntityEligible.Invoke(
            replayCapturePolicy,
            new object?[] { replacedLiveActor }) is not false)
    {
        throw new InvalidOperationException("Action-workflow replay capture did not isolate the Ghost actor.");
    }
    Console.WriteLine("PASS action workflow replay captures Ghost actors and excludes replaced live actors");

    Type actionEnvironmentController = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Runtime.DirectorActionEnvironmentController",
        throwOnError: true)!;
    foreach (string clientTimeField in new[] { "timelapsedCurrent", "timelapse" })
    {
        if (typeof(ClientMain).GetField(
                clientTimeField,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType != typeof(float))
        {
            throw new InvalidOperationException($"Client time field '{clientTimeField}' is unavailable.");
        }
    }
    ConstructorInfo actionEnvironmentConstructor = actionEnvironmentController.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(ICoreClientAPI) },
        modifiers: null)
        ?? throw new InvalidOperationException("Action environment controller constructor was not found.");
    MethodInfo applyActionEnvironment = actionEnvironmentController.GetMethod(
        "Apply",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Action environment apply method was not found.");
    MethodInfo restoreActionEnvironment = actionEnvironmentController.GetMethod(
        "Restore",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Action environment restore method was not found.");

    float? environmentSeason = 0.6f;
    float environmentTimelapse = 0.2f;
    IClientGameCalendar environmentCalendar = ProbeDispatchProxy.Create<IClientGameCalendar>((method, arguments) =>
    {
        switch (method.Name)
        {
            case "get_SeasonOverride":
                return environmentSeason;
            case "set_SeasonOverride":
                environmentSeason = (float?)arguments![0];
                return null;
            case "get_Timelapse":
                return environmentTimelapse;
            case "set_Timelapse":
                environmentTimelapse = (float)arguments![0]!;
                return null;
            case "get_HoursPerDay":
                return 24f;
            case "get_TotalHours":
                return 100d;
            default:
                return ProbeDispatchProxy.DefaultValue(method.ReturnType);
        }
    });
    IClientWorldAccessor environmentWorld = ProbeDispatchProxy.Create<IClientWorldAccessor>(
        (method, _) => method.Name == "get_Calendar"
            ? environmentCalendar
            : ProbeDispatchProxy.DefaultValue(method.ReturnType));
    WeatherSystemClient environmentWeather =
        (WeatherSystemClient)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSystemClient));
    environmentWeather.OverridePrecipitation = 0.25f;
    string requestedWeatherSystem = string.Empty;
    IModLoader environmentModLoader = ProbeDispatchProxy.Create<IModLoader>((method, arguments) =>
    {
        if (method.Name == "GetModSystem" && arguments is [string fullName])
        {
            requestedWeatherSystem = fullName;
            return environmentWeather;
        }
        return ProbeDispatchProxy.DefaultValue(method.ReturnType);
    });
    ICoreClientAPI environmentApi = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
    {
        "get_ModLoader" => environmentModLoader,
        "get_World" => environmentWorld,
        "get_Logger" => logger,
        _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
    });
    object environmentController = actionEnvironmentConstructor.Invoke(new object[] { environmentApi });
    applyActionEnvironment.Invoke(environmentController, new object[] { "rain", "0.8", Guid.NewGuid() });
    applyActionEnvironment.Invoke(environmentController, new object[] { "season", "autumn", Guid.NewGuid() });
    if (Math.Abs((environmentSeason ?? -1f) - 0.77f) > 0.0001f)
    {
        throw new InvalidOperationException("Action environment did not use Vintage Story's autumn season phase.");
    }
    applyActionEnvironment.Invoke(environmentController, new object[] { "season", "winter", Guid.NewGuid() });
    applyActionEnvironment.Invoke(environmentController, new object[] { "time", "18", Guid.NewGuid() });
    float expectedTimelapse = 18f / 24f - 100f / 24f + 4f;
    if (!string.Equals(
            requestedWeatherSystem,
            "Vintagestory.GameContent.WeatherSystemClient",
            StringComparison.Ordinal)
        || Math.Abs((environmentWeather.OverridePrecipitation ?? -1f) - 0.8f) > 0.0001f
        || Math.Abs((environmentSeason ?? -1f) - 0.05f) > 0.0001f
        || Math.Abs(environmentTimelapse - expectedTimelapse) > 0.0001f)
    {
        throw new InvalidOperationException(
            $"Action environment did not resolve and apply the client weather system "
            + $"(requested='{requestedWeatherSystem}', precipitation={environmentWeather.OverridePrecipitation}, "
            + $"season={environmentSeason}).");
    }
    restoreActionEnvironment.Invoke(environmentController, null);
    if (Math.Abs((environmentWeather.OverridePrecipitation ?? -1f) - 0.25f) > 0.0001f
        || Math.Abs((environmentSeason ?? -1f) - 0.6f) > 0.0001f
        || Math.Abs(environmentTimelapse - 0.2f) > 0.0001f)
    {
        throw new InvalidOperationException("Action environment did not restore its weather and calendar snapshot.");
    }

    IModLoader unavailableWeatherModLoader = ProbeDispatchProxy.Create<IModLoader>(
        (method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
    ICoreClientAPI unavailableWeatherApi = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
    {
        "get_ModLoader" => unavailableWeatherModLoader,
        "get_World" => environmentWorld,
        "get_Logger" => logger,
        _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
    });
    object unavailableWeatherController = actionEnvironmentConstructor.Invoke(new object[] { unavailableWeatherApi });
    applyActionEnvironment.Invoke(unavailableWeatherController, new object[] { "clear", string.Empty, Guid.NewGuid() });
    restoreActionEnvironment.Invoke(unavailableWeatherController, null);
    Console.WriteLine("PASS action environment weather resolution, snapshot safety, and restore");

    foreach (string particleBindingName in new[]
    {
        "ParticleManagerField",
        "ParticleSystemField",
        "MainThreadParticlePoolsField",
        "ParticlePhysicsField"
    })
    {
        FieldInfo bindingField = replayManager.GetField(
            particleBindingName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Replay particle binding '{particleBindingName}' was not found.");
        if (bindingField.GetValue(null) is not FieldInfo)
        {
            throw new InvalidOperationException($"Replay particle binding '{particleBindingName}' did not resolve.");
        }
    }
    if (typeof(ParticlePoolQuads).GetField(
            "ParticlesPool",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType
            != typeof(FastParticlePool)
        || typeof(FastParticlePool).GetField(
            nameof(FastParticlePool.AliveCount),
            BindingFlags.Instance | BindingFlags.Public) is null)
    {
        throw new InvalidOperationException(
            "Replay particle live-count bindings required by the playback cap are unavailable.");
    }
    Console.WriteLine("PASS replay particle engine bindings");

    Type dedicatedReplayRenderer = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorDedicatedReplayRenderer",
        throwOnError: true)!;
    Type dedicatedEngineBindings = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenEngineBindings",
        throwOnError: true)!;
    FieldInfo dedicatedParticleGameSpeed = dedicatedReplayRenderer.GetField(
        "ParticleGameSpeedField",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Dedicated replay particle game-speed binding was not found.");
    if (dedicatedParticleGameSpeed.GetValue(null) is not FieldInfo)
    {
        throw new InvalidOperationException(
            "Dedicated replay particle game-speed binding did not resolve.");
    }
    if (dedicatedEngineBindings.GetField(
            "ShadowMvpMatrixField",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not FieldInfo)
    {
        throw new InvalidOperationException(
            "Dedicated replay shadow-matrix binding did not resolve.");
    }
    foreach (string methodName in new[] { "Start", "Stop", "TryGetRenderer", "GetParticlePool" })
    {
        if (dedicatedReplayRenderer.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic) is null)
        {
            throw new InvalidOperationException(
                $"Dedicated replay renderer method '{methodName}' was not found.");
        }
    }
    Console.WriteLine("PASS dedicated replay renderer engine bindings");

    if (replayManager.GetMethod(
            "ReplayParticlePoolDeltaTime",
            BindingFlags.Instance | BindingFlags.NonPublic) is null
        || replayManager.GetMethod(
            "AdvanceReplayParticles",
            BindingFlags.Instance | BindingFlags.NonPublic) is not null)
    {
        throw new InvalidOperationException(
            "Replay particles are not using the deterministic whole-pool frame clock.");
    }
    Console.WriteLine("PASS replay particle whole-pool frame clock");

    Type offscreenBindings = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenEngineBindings",
        throwOnError: true)!;
    PropertyInfo offscreenBindingsValid = offscreenBindings.GetProperty(
        "IsValid",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen binding validation property was not found.");
    MethodInfo validateOffscreenBindings = offscreenBindings.GetMethod(
        "Validate",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Offscreen binding validation method was not found.");
    if (offscreenBindingsValid.GetValue(null) is not true)
    {
        string missing = validateOffscreenBindings.Invoke(null, null) as string ?? "unknown bindings";
        throw new InvalidOperationException($"Offscreen renderer bindings are unavailable: {missing}");
    }
    Console.WriteLine("PASS offscreen renderer engine bindings");

    Type liveOffscreenSession = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorLiveOffscreenSession",
        throwOnError: true)!;
    Type offscreenFileSink = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOffscreenFfmpegSink",
        throwOnError: true)!;
    if (!liveOffscreenSession.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == offscreenFileSink)))
    {
        throw new InvalidOperationException(
            "Live offscreen recording cannot attach the direct FFmpeg sink.");
    }
    foreach (string methodName in new[]
    {
        "TryStartLiveOffscreenFileRecording",
        "CompleteLiveOffscreenFileStart",
        "BeginLiveOffscreenFileStop",
        "CompleteLiveOffscreenFileStop",
        "BeginRemoteLiveOffscreen",
        "UpdatePendingRemoteLiveOffscreen",
        "ResolveLiveOffscreenFollowPose"
    })
    {
        if (typeof(VSDirectorModSystem).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic) is null)
        {
            throw new InvalidOperationException(
                $"Live offscreen direct-output method '{methodName}' was not found.");
        }
    }
    MethodInfo createLiveOffscreenPoseEvaluator = typeof(VSDirectorModSystem).GetMethod(
        "TryCreateLiveOffscreenPoseEvaluator",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "TryCreateLiveOffscreenPoseEvaluator");
    MethodInfo resolveLiveOffscreenFollowPose = typeof(VSDirectorModSystem).GetMethod(
        "ResolveLiveOffscreenFollowPose",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "ResolveLiveOffscreenFollowPose");
    MethodInfo onClientTickForOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "OnClientTick",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "OnClientTick");
    MethodInfo updatePendingRemoteOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "UpdatePendingRemoteLiveOffscreen",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "UpdatePendingRemoteLiveOffscreen");
    MethodInfo onAnchorStatusForOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "OnAnchorStatus",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(VSDirectorModSystem).FullName, "OnAnchorStatus");
    MethodInfo failPendingRemoteOffscreen = typeof(VSDirectorModSystem).GetMethod(
        "FailPendingRemoteLiveOffscreen",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(VSDirectorModSystem).FullName,
            "FailPendingRemoteLiveOffscreen");
    bool liveOffscreenFollowEvaluatorWired = CallsMethod(
            createLiveOffscreenPoseEvaluator,
            resolveLiveOffscreenFollowPose)
        || typeof(VSDirectorModSystem).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Any(method => CallsMethod(method, resolveLiveOffscreenFollowPose));
    if (!liveOffscreenFollowEvaluatorWired
        || !CallsMethod(onClientTickForOffscreen, updatePendingRemoteOffscreen)
        || !CallsMethod(onAnchorStatusForOffscreen, failPendingRemoteOffscreen)
        || typeof(VSDirectorModSystem).GetNestedType(
            "PendingRemoteLiveOffscreen",
            BindingFlags.NonPublic) is null)
    {
        throw new InvalidOperationException(
            "Direct follow offscreen recording does not evaluate follow motion or wait for remote targets.");
    }
    Type settingsDialog = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Gui.DirectorSettingsDialog",
        throwOnError: true)!;
    MethodInfo showSetting = settingsDialog.GetMethod(
        "ShowSetting",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(settingsDialog.FullName, "ShowSetting");
    if (settingsDialog.GetMethod("DrawSetting", BindingFlags.Instance | BindingFlags.NonPublic) is null
        || settingsDialog.GetMethod("OpenFfmpegDownload", BindingFlags.Instance | BindingFlags.NonPublic) is null
        || !Enum.GetValues<DirectorLiveOffscreenBackend>().SequenceEqual(new[] { DirectorLiveOffscreenBackend.Obs, DirectorLiveOffscreenBackend.Ffmpeg }))
        throw new InvalidOperationException("The native offscreen settings backend selector is incomplete.");
    object settingsProbe = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(settingsDialog);
    var backendConfig = new DirectorConfig { LiveOffscreenBackend = DirectorLiveOffscreenBackend.Ffmpeg };
    settingsDialog.GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settingsProbe, backendConfig);
    object offscreenPage = Enum.Parse(settingsDialog.GetNestedType("SettingsPage", BindingFlags.NonPublic)!, "Offscreen");
    if (showSetting.Invoke(settingsProbe, new object[] { offscreenPage, "OffscreenEncoder" }) is not true
        || showSetting.Invoke(settingsProbe, new object[] { offscreenPage, "OffscreenCaptureDirectorAudio" }) is not false)
        throw new InvalidOperationException("FFmpeg settings expose the wrong backend controls.");
    backendConfig.LiveOffscreenBackend = DirectorLiveOffscreenBackend.Obs;
    if (showSetting.Invoke(settingsProbe, new object[] { offscreenPage, "OffscreenEncoder" }) is not false
        || showSetting.Invoke(settingsProbe, new object[] { offscreenPage, "OffscreenCaptureDirectorAudio" }) is not true)
        throw new InvalidOperationException("OBS settings expose the wrong backend controls.");
    Console.WriteLine("PASS live offscreen follow, remote loading, OBS, and FFmpeg backends");

    Type framebufferApi = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Rendering.DirectorOpenGlFramebufferApi",
        throwOnError: true)!;
    MethodInfo createFramebufferApi = framebufferApi.GetMethod(
        "TryCreate",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("OpenGL framebuffer binding probe was not found.");
    object?[] framebufferArguments = { null, null };
    if (createFramebufferApi.Invoke(null, framebufferArguments) is not true)
    {
        throw new InvalidOperationException(
            "OpenGL framebuffer bindings are unavailable: " + framebufferArguments[1]);
    }
    Console.WriteLine("PASS OpenGL native framebuffer blit bindings");

    if (string.Equals(
            Environment.GetEnvironmentVariable("VSDIRECTOR_OBS_PROBE"),
            "1",
            StringComparison.Ordinal))
    {
        Type obsClientType = typeof(VSDirectorModSystem).Assembly.GetType(
            "VSDirector.Recording.DirectorObsWebSocketClient",
            throwOnError: true)!;
        IDisposable obsClient = (IDisposable)(Activator.CreateInstance(
            obsClientType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { "ws://127.0.0.1:4455", string.Empty },
            culture: null)
            ?? throw new InvalidOperationException("OBS WebSocket probe client could not be created."));
        try
        {
            await InvokeTaskAsync(obsClientType, obsClient, "PrepareAsync");
            await InvokeTaskAsync(obsClientType, obsClient, "SuspendDesktopAudioAsync");
            await InvokeTaskAsync(obsClientType, obsClient, "StartRecordingAsync");
            await InvokeTaskAsync(obsClientType, obsClient, "DisconnectAsync");
            await Task.Delay(1500);
            object? outputPath = await InvokeTaskAsync(obsClientType, obsClient, "StopRecordingAsync");
            await InvokeTaskAsync(obsClientType, obsClient, "RestoreDesktopAudioAsync");
            await InvokeTaskAsync(obsClientType, obsClient, "DisconnectAsync");
            Console.WriteLine("PASS OBS command reconnect, clean close, and desktop audio restore: " + outputPath);
        }
        finally
        {
            obsClient.Dispose();
        }
    }

    Type loopbackMixer = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Recording.DirectorOpenAlLoopbackMixer",
        throwOnError: true)!;
    foreach (string bindingName in new[]
    {
        "LoadedSoundsField",
        "LoadedSoundsLockField",
        "SampleField",
        "SourceStateField"
    })
    {
        FieldInfo binding = loopbackMixer.GetField(
            bindingName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Loopback sound binding '{bindingName}' was not found.");
        if (binding.GetValue(null) is not FieldInfo)
        {
            throw new InvalidOperationException($"Loopback sound binding '{bindingName}' did not resolve.");
        }
    }
    MethodInfo swapStereoChannels = loopbackMixer.GetMethod(
        "SwapStereoChannels",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Loopback stereo channel correction was not found.");
    short[] stereoProbe = { 1, 2, 3, 4 };
    swapStereoChannels.Invoke(null, new object[] { stereoProbe, 2 });
    if (!stereoProbe.SequenceEqual(new short[] { 2, 1, 4, 3 }))
    {
        throw new InvalidOperationException("Loopback stereo channel correction is reversed.");
    }
    Console.WriteLine("PASS OpenAL loopback stereo channel order");

    Type openAlNative = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Recording.DirectorOpenAlNative",
        throwOnError: true)!;
    object openAl = Activator.CreateInstance(openAlNative, nonPublic: true)
        ?? throw new InvalidOperationException("OpenAL loopback probe could not be created.");
    try
    {
        openAlNative.GetMethod(
            "InitializeLoopback",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(openAl, new object[] { 48000 });
    }
    finally
    {
        ((IDisposable)openAl).Dispose();
    }
    Console.WriteLine("PASS OpenAL Soft loopback and sound mirror bindings");

    Type replayAudioExporter = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioExporter",
        throwOnError: true)!;
    _ = replayAudioExporter.GetMethod(
        "ExportIndependentVoiceTracks",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Independent replay voice WAV export could not be found.");
    Type replayAudioAsset = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioAsset",
        throwOnError: true)!;
    Type replayAudioEvent = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioEvent",
        throwOnError: true)!;
    Type replayAudioPlan = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioExportPlan",
        throwOnError: true)!;
    Type replayAudioFrame = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioFrame",
        throwOnError: true)!;
    Type replayVoiceFrame = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Data.DirectorReplayVoiceFrame",
        throwOnError: true)!;
    Type replayAudioBus = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayAudioBus",
        throwOnError: true)!;
    float[] tone = Enumerable.Range(0, DirectorReplayAudioExportPolicy.SampleRate)
        .Select(index => (float)(Math.Sin(index * Math.PI * 2d * 440d
            / DirectorReplayAudioExportPolicy.SampleRate) * 0.25d))
        .ToArray();
    object toneAsset = replayAudioAsset.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single()
        .Invoke(new object[] { tone, 1, DirectorReplayAudioExportPolicy.SampleRate });
    Array replayEvents = Array.CreateInstance(replayAudioEvent, 1);
    object toneEvent = replayAudioEvent.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single()
        .Invoke(new object[]
        {
            0d,
            "probe:tone",
            1L,
            DirectorReplaySoundAction.Play,
            0d,
            0d,
            0d,
            true,
            false,
            0f,
            1f,
            1f,
            0f,
            32f,
            3f,
            (int)EnumSoundType.Sound,
            1f,
            1f,
            0f,
            Enum.Parse(replayAudioBus, "General")
        });
    replayEvents.SetValue(toneEvent, 0);
    Type assetDictionaryType = typeof(Dictionary<,>).MakeGenericType(
        typeof(string),
        replayAudioAsset);
    IDictionary replayAssets = (IDictionary)(Activator.CreateInstance(assetDictionaryType)
        ?? throw new InvalidOperationException("Replay audio asset dictionary could not be created."));
    replayAssets.Add("probe:tone", toneAsset);
    DirectorReplayAudioVisuals replayAudioVisuals = new();
    object audioPlan = replayAudioPlan.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single()
        .Invoke(new object[]
        {
            replayEvents,
            replayAssets,
            0,
            replayAudioVisuals,
            Array.CreateInstance(replayVoiceFrame, 0),
            new DirectorReplayViewportTimeline()
        });
    Array replayFrames = Array.CreateInstance(replayAudioFrame, 30);
    ConstructorInfo audioFrameConstructor = replayAudioFrame.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single();
    for (int index = 0; index < replayFrames.Length; index++)
    {
        replayFrames.SetValue(
            audioFrameConstructor.Invoke(new object[]
            {
                index / 30d,
                new CameraPreset { Fov = 70f }
            }),
            index);
    }
    MethodInfo renderReplayWave = replayAudioExporter.GetMethod(
        "RenderWave",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay audio WAV renderer was not found.");
    string replayWavePath = Path.Combine(
        Path.GetTempPath(),
        "vsdirector-replay-audio-probe-" + Guid.NewGuid().ToString("N") + ".wav");
    try
    {
        long renderedSamples = (long)(renderReplayWave.Invoke(
            null,
            new object[]
            {
                audioPlan,
                replayFrames,
                30,
                replayWavePath,
                CancellationToken.None,
                null!
            })
            ?? 0L);
        byte[] wave = File.ReadAllBytes(replayWavePath);
        if (renderedSamples != DirectorReplayAudioExportPolicy.SampleRate
            || wave.Length != 44 + DirectorReplayAudioExportPolicy.SampleRate * 4
            || Encoding.ASCII.GetString(wave, 0, 4) != "RIFF"
            || !wave.Skip(44).Any(value => value != 0))
        {
            throw new InvalidOperationException(
                "Replay audio renderer did not produce one second of non-silent stereo PCM.");
        }
    }
    finally
    {
        File.Delete(replayWavePath);
    }
    Console.WriteLine("PASS deterministic replay audio WAV rendering");

    Array creationFrames = Array.CreateInstance(replayAudioFrame, 30);
    foreach (int index in Enumerable.Range(0, 30))
    {
        object frame = audioFrameConstructor.Invoke(new object[] { index / 30d, new CameraPreset { Fov = 70f } });
        replayAudioFrame.GetProperty("Muted")!.SetValue(frame, 10 > index || index >= 20);
        replayAudioFrame.GetProperty("Discontinuity")!.SetValue(frame, index == 10);
        replayAudioFrame.GetProperty("NextReplaySeconds")!.SetValue(frame, (index + 1) / 30d);
        creationFrames.SetValue(frame, index);
    }
    try
    {
        renderReplayWave.Invoke(null, new object[] { audioPlan, creationFrames, 30, replayWavePath, CancellationToken.None, null! });
        byte[] wave = File.ReadAllBytes(replayWavePath);
        int sectionBytes = DirectorReplayAudioExportPolicy.SampleRate / 3 * 4;
        if (wave.Length != 44 + sectionBytes * 3
            || wave.Skip(44).Take(sectionBytes).Any(value => value != 0)
            || !wave.Skip(44 + sectionBytes).Take(sectionBytes).Any(value => value != 0)
            || wave.Skip(44 + sectionBytes * 2).Any(value => value != 0))
            throw new InvalidOperationException("Creation recording audio lost its project offset, silence gaps, or active source samples.");
    }
    finally { File.Delete(replayWavePath); }
    Console.WriteLine("PASS creation recorded audio retains project-frame timing and silent gaps around active performance clips");

    var freezeAudioTrack = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Freeze };
    var freezeAudioTimeline = new DirectorReplayViewportTimeline
    {
        Tracks = [freezeAudioTrack],
        ElementKeyframes = [
            new() { TrackId = freezeAudioTrack.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = .2, Frozen = true, FreezeDelayTicks = 0 },
            new() { TrackId = freezeAudioTrack.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = .6, Frozen = false }
        ]
    };
    object frozenAudioPlan = replayAudioPlan.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
        .Invoke([replayEvents, replayAssets, 0, replayAudioVisuals, Array.CreateInstance(replayVoiceFrame, 0), freezeAudioTimeline]);
    try
    {
        renderReplayWave.Invoke(null, [frozenAudioPlan, replayFrames, 30, replayWavePath, CancellationToken.None, null]);
        byte[] wav = File.ReadAllBytes(replayWavePath);
        bool Audible(int firstFrame, int countFrames) => wav.Skip(44 + firstFrame * 1600 * 4).Take(countFrames * 1600 * 4).Any(b => b != 0);
        if (!Audible(1, 3) || Audible(7, 10) || !Audible(20, 5))
            throw new InvalidOperationException("Frozen export must silence held frames and resume at the unfreeze source time.");
    }
    finally { File.Delete(replayWavePath); }
    Console.WriteLine("PASS frozen export audio follows presentation time and resumes without compressing skipped time");

    var poisonSamples = (DirectorReplayList<DirectorReplayActorSample>)typeof(DirectorReplayList<DirectorReplayActorSample>)
        .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke([
            1, new System.Func<int, DirectorReplayActorSample>(_ => throw new Exception("Audio export read actor payload")), null]);
    var voiceSourceDocument = new DirectorReplayDocument { Actors = [new() { Samples = poisonSamples }], VoiceFrames = [new() { Pcm16 = [1, 2] }] };
    var voiceOnly = (DirectorReplayDocument)AccessTools.Method(replayAudioExporter, "CreateVoiceSource").Invoke(null, [voiceSourceDocument])!;
    if (voiceOnly.Actors.Count != 0 || !ReferenceEquals(voiceOnly.VoiceFrames, voiceSourceDocument.VoiceFrames)
        || ReferenceEquals(voiceOnly.ViewportTimeline, voiceSourceDocument.ViewportTimeline))
        throw new InvalidOperationException("Voice export did not isolate editable metadata without cloning replay payloads.");
    Console.WriteLine("PASS independent voice export snapshots no actor or world payloads");

    ICoreClientAPI exportApi = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
    using (var strictSink = new VSDirector.Rendering.DirectorOffscreenFfmpegSink(exportApi,
        new VSDirector.Rendering.DirectorOffscreenFfmpegOptions { ViewId = Guid.NewGuid(), OutputPath = Path.Combine(Path.GetTempPath(), "unused-export-probe.mp4"), Width = 16, Height = 16, RequireExactFrames = true }))
    {
        AccessTools.Field(strictSink.GetType(), "finalFrameTarget").SetValue(strictSink, 1L);
        using var output = new MemoryStream();
        Task padding = (Task)AccessTools.Method(strictSink.GetType(), "PadFinalFramesAsync").Invoke(strictSink, [output, new byte[1024]])!;
        bool rejected = false;
        try { padding.GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || output.Length != 0) throw new InvalidOperationException("Exact export padded a missing frame instead of failing.");
    }
    Console.WriteLine("PASS exact replay export refuses to hide dropped frames with tail padding");

    MethodInfo exportIndependentVoiceTracks = replayAudioExporter.GetMethod(
        "ExportIndependentVoiceTracks",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Independent replay voice WAV export could not be found.");
    string voiceDirectory = Path.Combine(
        Path.GetTempPath(),
        "vsdirector-independent-voice-probe-" + Guid.NewGuid().ToString("N"));
    try
    {
        short[] voiceSamples = Enumerable.Repeat((short)12000, 4800).ToArray();
        byte[] voicePcm = new byte[voiceSamples.Length * sizeof(short)];
        Buffer.BlockCopy(voiceSamples, 0, voicePcm, 0, voicePcm.Length);
        DirectorReplayDocument voiceDocument = new()
        {
            DurationSeconds = 1d,
            VoiceFrames = new List<DirectorReplayVoiceFrame>
            {
                new()
                {
                    TimeSeconds = 0.25d,
                    SpeakerId = "speaker-a",
                    SpeakerName = "Speaker A",
                    Spatialized = false,
                    Pcm16 = voicePcm
                },
                new()
                {
                    TimeSeconds = 0.6d,
                    SpeakerId = "speaker-b",
                    SpeakerName = "Speaker B",
                    Spatialized = false,
                    Pcm16 = voicePcm
                }
            }
        };
        object exported = exportIndependentVoiceTracks.Invoke(
                null,
                new object[] { voiceDocument, 0d, 1d, voiceDirectory, CancellationToken.None })
            ?? throw new InvalidOperationException("Independent voice export returned no result.");
        string[] voicePaths = ((IEnumerable)exported).Cast<string>().ToArray();
        if (voicePaths.Length != 2)
        {
            throw new InvalidOperationException("Independent voice export did not create one file per speaker.");
        }
        foreach (string voicePath in voicePaths)
        {
            byte[] voiceWave = File.ReadAllBytes(voicePath);
            if (voiceWave.Length != 44 + DirectorReplayAudioExportPolicy.SampleRate * sizeof(short)
                || BitConverter.ToUInt16(voiceWave, 22) != 1
                || Encoding.ASCII.GetString(voiceWave, 0, 4) != "RIFF"
                || voiceWave.Skip(44).Take(12000 * sizeof(short)).Any(value => value != 0)
                || !voiceWave.Skip(44).Any(value => value != 0))
            {
                throw new InvalidOperationException(
                    "Independent voice export did not preserve a synchronized mono timeline.");
            }
        }
    }
    finally
    {
        if (Directory.Exists(voiceDirectory))
        {
            Directory.Delete(voiceDirectory, recursive: true);
        }
    }
    Console.WriteLine("PASS independent synchronized replay voice WAV export");

    string ffmpegProbe = Environment.GetEnvironmentVariable("VSDIRECTOR_TEST_FFMPEG") ?? string.Empty;
    if (File.Exists(ffmpegProbe))
    {
        string probeDirectory = Path.Combine(
            Path.GetTempPath(),
            "vsdirector-replay-audio-mux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        string probeVideo = Path.Combine(probeDirectory, "probe.mp4");
        try
        {
            ProcessStartInfo videoStart = new()
            {
                FileName = ffmpegProbe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:r=30:d=1",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", probeVideo
            })
            {
                videoStart.ArgumentList.Add(argument);
            }
            using (Process videoProcess = new() { StartInfo = videoStart })
            {
                videoProcess.Start();
                string videoError = videoProcess.StandardError.ReadToEnd();
                videoProcess.WaitForExit();
                if (videoProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException("FFmpeg video probe failed: " + videoError);
                }
            }

            MethodInfo renderAndMux = replayAudioExporter.GetMethod(
                "RenderAndMux",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Replay audio muxer was not found.");
            object muxResult = renderAndMux.Invoke(
                    null,
                    new object[]
                    {
                        audioPlan,
                        replayFrames,
                        30,
                        probeVideo,
                        ffmpegProbe,
                        192,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None
                    })
                ?? throw new InvalidOperationException("Replay audio muxer returned no result.");
            if (muxResult.GetType().GetProperty("Success")?.GetValue(muxResult) is not true)
            {
                string muxError = muxResult.GetType().GetProperty("Error")?.GetValue(muxResult)?.ToString()
                    ?? string.Empty;
                throw new InvalidOperationException("Replay audio mux probe failed: " + muxError);
            }

            ProcessStartInfo probeStart = new()
            {
                FileName = ffmpegProbe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-i", probeVideo,
                "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-"
            })
            {
                probeStart.ArgumentList.Add(argument);
            }
            using Process probeProcess = new() { StartInfo = probeStart };
            probeProcess.Start();
            string audioStream = probeProcess.StandardError.ReadToEnd();
            probeProcess.WaitForExit();
            if (probeProcess.ExitCode != 0
                || !audioStream.Contains("Audio: aac", StringComparison.Ordinal)
                || !audioStream.Contains("48000 Hz", StringComparison.Ordinal)
                || !audioStream.Contains("stereo", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "FFmpeg did not decode the expected AAC 48 kHz stereo stream: "
                    + audioStream);
            }

            string[] expectedStemNames =
            {
                "music", "ambient", "weather", "entity", "block", "ui", "general"
            };
            foreach (string stemName in expectedStemNames)
            {
                string stemPath = Path.Combine(probeDirectory, "probe." + stemName + ".wav");
                if (!File.Exists(stemPath))
                {
                    throw new InvalidOperationException(
                        "Replay audio bus stem was not retained: " + stemName);
                }
                byte[] stem = File.ReadAllBytes(stemPath);
                if (stem.Length != 44 + DirectorReplayAudioExportPolicy.SampleRate * 4
                    || Encoding.ASCII.GetString(stem, 0, 4) != "RIFF"
                    || stemName == "general" && !stem.Skip(44).Any(value => value != 0))
                {
                    throw new InvalidOperationException(
                        "Replay audio bus stem is invalid or unexpectedly silent: " + stemName);
                }
            }
        }
        finally
        {
            foreach (string file in Directory.GetFiles(probeDirectory))
            {
                File.Delete(file);
            }
            Directory.Delete(probeDirectory);
        }
        Console.WriteLine("PASS replay audio AAC muxing and seven retained bus stems");
    }

    FieldInfo suppressedLiveEntities = replayManager.GetField(
        "suppressedLiveEntities",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-entity visibility ledger was not found.");
    if (!suppressedLiveEntities.FieldType.IsGenericType
        || suppressedLiveEntities.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
    {
        throw new InvalidOperationException("Replay live-entity visibility ledger has an unexpected type.");
    }
    MethodInfo suppressLiveEntity = replayManager.GetMethod(
        "SuppressLiveEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-entity suppression method was not found.");
    MethodInfo suppressLoadedRealityEntities = replayManager.GetMethod(
        "SuppressLoadedRealityEntities",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay loaded-entity isolation method was not found.");
    MethodInfo onReplayEntitySpawn = replayManager.GetMethod(
        "OnEntitySpawn",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay entity-spawn observer was not found.");
    MethodInfo onReplayEntityLoaded = replayManager.GetMethod(
        "OnEntityLoaded",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay entity-loaded observer was not found.");
    MethodInfo shouldSuppressLiveReality = replayManager.GetMethod(
        "ShouldSuppressLiveReality",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-reality isolation policy was not found.");
    MethodInfo finishLoadedPlayback = replayManager.GetMethod(
        "FinishLoadedPlayback",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay activation method was not found.");
    MethodInfo suppressLiveSpatialSounds = replayManager.GetMethod(
        "SuppressLiveSpatialSounds",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live spatial-sound isolation method was not found.");
    if (!CallsMethod(suppressLoadedRealityEntities, suppressLiveEntity)
        || !CallsMethod(onReplayEntitySpawn, suppressLiveEntity)
        || !CallsMethod(onReplayEntityLoaded, suppressLiveEntity)
        || !CallsMethod(finishLoadedPlayback, suppressLoadedRealityEntities)
        || !CallsMethod(finishLoadedPlayback, suppressLiveSpatialSounds)
        || shouldSuppressLiveReality.Invoke(null, new object?[] { null }) is not true)
    {
        throw new InvalidOperationException(
            "Replay activation does not isolate existing, spawned, and loaded reality entities or sounds.");
    }
    Console.WriteLine("PASS replay reality-entity and live-loop isolation lifecycle");

    Type clothingTextureGuard = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayClothingTextureGuard",
        throwOnError: true)!;
    PropertyInfo clothingTextureGuardAvailable = clothingTextureGuard.GetProperty(
        "IsAvailable",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay clothing texture guard availability was not exposed.");
    MethodInfo suppressUnallocatedTextureRelease = clothingTextureGuard.GetMethod(
        "SuppressUnallocatedTextureRelease",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay clothing texture release guard was not found.");
    MethodInfo areClothingTextureSpacesAllocated = clothingTextureGuard.GetMethod(
        "AreTextureSpacesAllocated",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay clothing texture allocation check was not found.");
    FieldInfo clothingTextureSpaceAllocated = AccessTools.Field(
        typeof(EntityBehaviorTexturedClothing),
        "textureSpaceAllocated")
        ?? throw new InvalidOperationException("Clothing texture allocation state was not found.");
    FieldInfo clothingClientApi = AccessTools.Field(typeof(EntityBehaviorTexturedClothing), "capi")
        ?? throw new InvalidOperationException("Clothing texture client API field was not found.");
    if (clothingTextureGuardAvailable.GetValue(null) is not true)
    {
        throw new InvalidOperationException("Replay clothing texture release guard is unavailable.");
    }

    EntityBehavior unallocatedClothing = (EntityBehavior)RuntimeHelpers.GetUninitializedObject(
        typeof(EntityBehaviorPlayerInventory));
    EntityBehavior allocatedClothing = (EntityBehavior)RuntimeHelpers.GetUninitializedObject(
        typeof(EntityBehaviorPlayerInventory));
    ICoreClientAPI clothingApiMarker = ProbeDispatchProxy.Create<ICoreClientAPI>(
        (method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
    clothingClientApi.SetValue(unallocatedClothing, clothingApiMarker);
    clothingClientApi.SetValue(allocatedClothing, clothingApiMarker);
    clothingTextureSpaceAllocated.SetValue(allocatedClothing, true);
    if (areClothingTextureSpacesAllocated.Invoke(
            null,
            new object[] { new EntityBehavior[] { unallocatedClothing, allocatedClothing } }) is not false)
    {
        throw new InvalidOperationException("Replay clothing preparation ignored an unallocated skin region.");
    }
    clothingTextureSpaceAllocated.SetValue(unallocatedClothing, true);
    if (areClothingTextureSpacesAllocated.Invoke(
            null,
            new object[] { new EntityBehavior[] { unallocatedClothing, allocatedClothing } }) is not true)
    {
        throw new InvalidOperationException("Replay clothing preparation rejected allocated skin regions.");
    }
    clothingTextureSpaceAllocated.SetValue(unallocatedClothing, false);
    IDisposable clothingGuardScope = (IDisposable)(suppressUnallocatedTextureRelease.Invoke(
        null,
        new object[] { new EntityBehavior[] { unallocatedClothing, allocatedClothing } })
        ?? throw new InvalidOperationException("Replay clothing texture release guard returned no scope."));
    try
    {
        if (clothingClientApi.GetValue(unallocatedClothing) is not null
            || !ReferenceEquals(clothingClientApi.GetValue(allocatedClothing), clothingApiMarker))
        {
            throw new InvalidOperationException(
                "Replay clothing guard did not isolate only the unallocated atlas release.");
        }
    }
    finally
    {
        clothingGuardScope.Dispose();
    }
    if (!ReferenceEquals(clothingClientApi.GetValue(unallocatedClothing), clothingApiMarker))
    {
        throw new InvalidOperationException("Replay clothing guard did not restore its scoped client API state.");
    }

    MethodInfo prepareReplayPlayerSkinTexture = replayManager.GetMethod(
        "TryPrepareReplayPlayerSkinTexture",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay player skin preparation method was not found.");
    MethodInfo removeReplayGhost = replayManager.GetMethod(
        "RemoveGhost",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay ghost removal method was not found.");
    MethodInfo despawnReplayEntity = replayManager.GetMethod(
        "DespawnReplayEntity",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay guarded entity despawn method was not found.");
    MethodInfo createPreviewEntityState = replayManager.GetMethod(
        "TryCreatePreviewEntityState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay preview entity serializer was not found.");
    MethodInfo restoreSuppressedLiveEntities = replayManager.GetMethod(
        "RestoreSuppressedLiveEntities",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay reality-entity restoration method was not found.");
    MethodInfo getEntityTextureSource = typeof(Entity).GetMethod(
        nameof(Entity.GetTextureSource),
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        Type.EmptyTypes,
        modifiers: null)
        ?? throw new InvalidOperationException("Entity texture source method was not found.");
    MethodInfo markEntityShapeModified = typeof(Entity).GetMethod(
        nameof(Entity.MarkShapeModified),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Entity shape invalidation method was not found.");
    if (!CallsMethod(applySample, prepareReplayPlayerSkinTexture)
        || !CallsMethodReference(prepareReplayPlayerSkinTexture, getEntityTextureSource)
        || !CallsMethod(prepareReplayPlayerSkinTexture, areClothingTextureSpacesAllocated)
        || !CallsMethod(removeReplayGhost, despawnReplayEntity)
        || !CallsMethod(createPreviewEntityState, despawnReplayEntity)
        || !CallsMethod(despawnReplayEntity, suppressUnallocatedTextureRelease)
        || !CallsMethodReference(restoreSuppressedLiveEntities, markEntityShapeModified))
    {
        throw new InvalidOperationException(
            "Replay clothing preparation, cleanup, or reality refresh is not connected to playback.");
    }
    Console.WriteLine("PASS replay clothing skin allocation and atlas cleanup lifecycle");

    Type blockEntityRound = replayManager.GetNestedType(
        "BlockEntityCaptureRound",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity capture round was not found.");
    ConstructorInfo blockEntityRoundConstructor = blockEntityRound.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(DirectorReplayChunkSnapshot[]), typeof(double) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay block-entity capture timestamp constructor was not found.");
    object blockEntityRoundInstance = blockEntityRoundConstructor.Invoke(
        new object[] { Array.Empty<DirectorReplayChunkSnapshot>(), 12.5d });
    double captureTime = (double)(blockEntityRound.GetProperty("TimeSeconds")?.GetValue(blockEntityRoundInstance)
        ?? throw new InvalidOperationException("Replay block-entity capture timestamp was not found."));
    if (Math.Abs(captureTime - 12.5d) > 0.000001d)
    {
        throw new InvalidOperationException("Replay block-entity capture did not retain its resume timestamp.");
    }
    Console.WriteLine("PASS replay pause visibility ledger and block-entity timestamp");

    Type replaySerialization = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplaySerialization",
        throwOnError: true)!;
    MethodInfo createAnimationRetessellationCallback = replaySerialization.GetMethod(
        "CreateActiveAnimationRetessellationCallback",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Replay block-entity animation retessellation helper was not found.");
    MethodInfo serializeReplayBlockEntity = replaySerialization.GetMethod(
        "SerializeBlockEntity",
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay block-entity serializer was not found.");
    MethodInfo isSerializingReplayBlockEntity = replaySerialization.GetMethod(
        "IsSerializingBlockEntity",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Replay block-entity serialization reentrancy guard was not found.");
    MethodInfo applyReplayBlockEntityData = replaySerialization.GetMethod(
        "ApplyBlockEntityData",
        BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay block-entity state applier was not found.");
    bool IsSerializingReplayBlockEntity(BlockEntity blockEntity)
        => (bool)(isSerializingReplayBlockEntity.Invoke(
            null,
            new object[] { blockEntity }) ?? false);

    BlockEntityGeneric unrelatedSerializationEntity = new();
    ProbeBlockEntitySerializationScope serializationScopeProbe = new()
    {
        IsSerializing = IsSerializingReplayBlockEntity,
        Unrelated = unrelatedSerializationEntity
    };
    _ = serializeReplayBlockEntity.Invoke(
        null,
        new object[] { serializationScopeProbe });
    if (!serializationScopeProbe.WasSerializing
        || serializationScopeProbe.UnrelatedWasSerializing
        || IsSerializingReplayBlockEntity(serializationScopeProbe)
        || !CallsMethod(captureObservedBlockEntityState, isSerializingReplayBlockEntity))
    {
        throw new InvalidOperationException(
            "Block-entity serialization does not suppress only same-entity nested capture.");
    }

    ProbeBlockEntitySerializationScope throwingSerializationScopeProbe = new()
    {
        IsSerializing = IsSerializingReplayBlockEntity,
        ThrowDuringSerialization = true
    };
    bool serializationFailureObserved = false;
    try
    {
        _ = serializeReplayBlockEntity.Invoke(
            null,
            new object[] { throwingSerializationScopeProbe });
    }
    catch (TargetInvocationException exception)
        when (exception.InnerException is InvalidOperationException)
    {
        serializationFailureObserved = true;
    }
    if (!serializationFailureObserved
        || !throwingSerializationScopeProbe.WasSerializing
        || IsSerializingReplayBlockEntity(throwingSerializationScopeProbe))
    {
        throw new InvalidOperationException(
            "Block-entity serialization reentrancy state survived a failed snapshot.");
    }
    Console.WriteLine("PASS same-entity block-entity serialization reentrancy guard");

    Type blockEntityVisualStateRefresh = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayBlockEntityVisualStateRefresh",
        throwOnError: true)!;
    MethodInfo refreshBlockEntityVisualState = blockEntityVisualStateRefresh.GetMethod(
        "RefreshAfterDeserialization",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Replay block-entity visual-state refresh entry point was not found.");
    IWorldAccessor visualRefreshWorld = ProbeDispatchProxy.Create<IWorldAccessor>(
        static (method, _) => ProbeDispatchProxy.DefaultValue(method.ReturnType));
    MethodInfo refreshMannequinBehavior = blockEntityVisualStateRefresh.GetMethod(
        "TryRefreshMannequinBehavior",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(object) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay mannequin mesh refresh method was not found.");
    if (blockEntityVisualStateRefresh.GetProperty(
            "IsAvailable",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not true)
    {
        throw new InvalidOperationException("Vintage Story mannequin mesh refresh target was not available.");
    }
    ProbeMannequinBehavior mannequinBehavior = (ProbeMannequinBehavior)
        RuntimeHelpers.GetUninitializedObject(typeof(ProbeMannequinBehavior));
    BlockEntityGeneric mannequinOwner = new()
    {
        Behaviors = new List<BlockEntityBehavior> { mannequinBehavior }
    };
    if (refreshBlockEntityVisualState.Invoke(
            null,
            new object[] { mannequinOwner, visualRefreshWorld }) is not 1
        || !mannequinBehavior.Refreshed
        || refreshMannequinBehavior.Invoke(null, new object[] { new object() }) is not false
        || !CallsMethod(applyReplayBlockEntityData, refreshBlockEntityVisualState))
    {
        throw new InvalidOperationException(
            "Replay mannequin inventory restoration does not rebuild its cached clothing mesh.");
    }
    Console.WriteLine("PASS replay mannequin clothing mesh rebuild after state restore");
    MethodInfo refreshConstructableBehavior = blockEntityVisualStateRefresh.GetMethod(
        "TryRefreshConstructableBehavior",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(object) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay constructable-shape refresh method was not found.");
    MethodInfo refreshMechanicalPowerBehavior = blockEntityVisualStateRefresh.GetMethod(
        "TryRefreshMechanicalPowerBehavior",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(object), typeof(IWorldAccessor) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay mechanical renderer refresh method was not found.");
    bool constructableRefreshAvailable = blockEntityVisualStateRefresh.GetProperty(
        "IsConstructableRefreshAvailable",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true;
    bool mechanicalRefreshAvailable = blockEntityVisualStateRefresh.GetProperty(
        "IsMechanicalPowerRefreshAvailable",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true;
    if (AccessTools.Method(
            typeof(BEBehaviorMPWaterWheel),
            "updateShape",
            new[] { typeof(IWorldAccessor) }) is null)
    {
        throw new InvalidOperationException(
            "Vintage Story water-wheel mechanical shape refresh target was not available.");
    }
    ProbeConstructableBehavior constructableBehavior =
        (ProbeConstructableBehavior)RuntimeHelpers.GetUninitializedObject(
            typeof(ProbeConstructableBehavior));
    ProbeMechanicalPowerBehavior mechanicalBehavior = new(constructableBehavior);
    BlockEntityGeneric waterWheelOwner = new()
    {
        Behaviors = new List<BlockEntityBehavior> { mechanicalBehavior, constructableBehavior }
    };
    if (!constructableRefreshAvailable
        || !mechanicalRefreshAvailable
        || refreshBlockEntityVisualState.Invoke(
            null,
            new object[] { waterWheelOwner, visualRefreshWorld }) is not 2
        || !constructableBehavior.Refreshed
        || !mechanicalBehavior.Refreshed
        || !mechanicalBehavior.ConstructableWasRefreshed
        || refreshConstructableBehavior.Invoke(null, new object[] { new object() }) is not false
        || refreshMechanicalPowerBehavior.Invoke(
            null,
            new object[] { new object(), visualRefreshWorld }) is not false)
    {
        throw new InvalidOperationException(
            "Replay water-wheel restoration does not rebuild its assembled shape and renderer registration.");
    }
    Console.WriteLine("PASS replay water-wheel assembled shape and mechanical renderer restoration");
    Type replayMechanicalPowerState = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.ReplayMechanicalPowerState",
        throwOnError: true)!;
    MethodInfo captureReplayMechanicalPowerState = replayMechanicalPowerState.GetMethod(
        "Capture",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(BlockEntity), typeof(TreeAttribute) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay mechanical-power capture method was not found.");
    MethodInfo restoreReplayMechanicalPowerState = replayMechanicalPowerState.GetMethod(
        "Restore",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(BlockEntity), typeof(ITreeAttribute) },
        modifiers: null)
        ?? throw new InvalidOperationException("Replay mechanical-power restore method was not found.");
    bool mechanicalStateAvailable = replayMechanicalPowerState.GetProperty(
        "IsAvailable",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true;
    const long capturedNetworkId = 417L;
    MechanicalNetwork capturedNetwork = new(new MechanicalPowerMod(), capturedNetworkId)
    {
        Speed = 0.275f,
        clientSpeed = 0.19f,
        TurnDir = EnumRotDirection.Clockwise,
        DirectionHasReversed = true
    };
    mechanicalBehavior.NetworkId = capturedNetworkId;
    AccessTools.Field(typeof(BEBehaviorMPBase), "network")!.SetValue(
        mechanicalBehavior,
        capturedNetwork);
    TreeAttribute capturedMechanicalTree = new();
    captureReplayMechanicalPowerState.Invoke(
        null,
        new object[] { waterWheelOwner, capturedMechanicalTree });
    ITreeAttribute? capturedMechanicalState = capturedMechanicalTree.GetTreeAttribute(
        "vsdirectorReplayMechanicalPower");
    capturedNetwork.Speed = 0f;
    capturedNetwork.clientSpeed = 0f;
    capturedNetwork.TurnDir = EnumRotDirection.Counterclockwise;
    mechanicalBehavior.NetworkId = capturedNetworkId + 1L;
    object? mismatchedRestore = restoreReplayMechanicalPowerState.Invoke(
        null,
        new object[] { waterWheelOwner, capturedMechanicalTree });
    mechanicalBehavior.NetworkId = capturedNetworkId;
    object? restoredMechanicalState = restoreReplayMechanicalPowerState.Invoke(
        null,
        new object[] { waterWheelOwner, capturedMechanicalTree });
    float restoredAngle = capturedNetwork.AngleRad;
    AccessTools.Field(typeof(MechanicalNetwork), "firstTick")!.SetValue(capturedNetwork, false);
    capturedNetwork.ClientTick(0.02f);
    bool restoredNetworkAdvances = Math.Abs(capturedNetwork.AngleRad - restoredAngle) > 0.00001f;
    if (!mechanicalStateAvailable
        || capturedMechanicalState?.GetInt("count", 0) != 1
        || mismatchedRestore is not 0
        || restoredMechanicalState is not 1
        || Math.Abs(capturedNetwork.Speed - 0.275f) > 0.00001f
        || Math.Abs(capturedNetwork.clientSpeed - 0.275f) > 0.00001f
        || capturedNetwork.TurnDir != EnumRotDirection.Clockwise
        || capturedNetwork.DirectionHasReversed
        || !restoredNetworkAdvances
        || restoreReplayMechanicalPowerState.Invoke(
            null,
            new object[] { waterWheelOwner, new TreeAttribute() }) is not 0
        || !CallsMethod(serializeReplayBlockEntity, captureReplayMechanicalPowerState)
        || !CallsMethod(applyReplayBlockEntityData, restoreReplayMechanicalPowerState))
    {
        throw new InvalidOperationException(
            "Replay mechanical networks do not retain their recorded speed across topology changes.");
    }
    Console.WriteLine("PASS replay mechanical-network speed across recorded topology changes");
    MethodInfo beginReplayAnimationStateChange = replaySerialization.GetMethod(
        "BeginAnimationStateChange",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation revision starter was not found.");
    MethodInfo isReplayAnimationRevisionCurrent = replaySerialization.GetMethod(
        "IsAnimationRevisionCurrent",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation revision query was not found.");
    MethodInfo resolveReplayAnimationVisibility = replaySerialization.GetMethod(
        "ResolveAnimationRendererVisibility",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation revision resolver was not found.");
    MethodInfo invalidateReplayAnimationOwners = replaySerialization.GetMethod(
        "InvalidateAnimationOwners",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation owner invalidator was not found.");

    static BlockEntityGeneric CreateAnimatedContainer(
        out BlockEntityAnimationUtil animation,
        bool lidOpen)
    {
        BlockEntityGeneric blockEntity = new()
        {
            Pos = new BlockPos(4, 5, 6),
            Block = new Block { Code = new AssetLocation("game", "replay-container-probe") }
        };
        BEBehaviorAnimatable behavior = new(blockEntity);
        animation = (BlockEntityAnimationUtil)RuntimeHelpers.GetUninitializedObject(
            typeof(BlockEntityAnimationUtil));
        animation.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>();
        if (lidOpen)
        {
            animation.activeAnimationsByAnimCode["lidopen"] = new AnimationMetaData
            {
                Code = "lidopen",
                Animation = "lidopen",
                AnimationSpeed = 1.8f,
                EaseInSpeed = 15f,
                EaseOutSpeed = 6f
            };
        }
        behavior.animUtil = animation;
        blockEntity.Behaviors = new List<BlockEntityBehavior> { behavior };
        return blockEntity;
    }

    BlockEntityGeneric openContainer = CreateAnimatedContainer(
        out BlockEntityAnimationUtil openContainerAnimation,
        lidOpen: true);
    byte[] openContainerData = (byte[])(serializeReplayBlockEntity.Invoke(
        null,
        new object[] { openContainer })
        ?? throw new InvalidOperationException("Open container state was not serialized."));
    openContainerAnimation.activeAnimationsByAnimCode.Clear();
    byte[] closedContainerData = (byte[])(serializeReplayBlockEntity.Invoke(
        null,
        new object[] { openContainer })
        ?? throw new InvalidOperationException("Closed container state was not serialized."));
    if (openContainerData.AsSpan().SequenceEqual(closedContainerData))
    {
        throw new InvalidOperationException("Container lid animation was absent from replay data.");
    }
    TreeAttribute openContainerTree = new();
    using (MemoryStream animationTreeStream = new(openContainerData, writable: false))
    using (BinaryReader animationTreeReader = new(animationTreeStream))
    {
        openContainerTree.FromBytes(animationTreeReader);
    }
    ITreeAttribute serializedAnimations = openContainerTree.GetTreeAttribute(
        "vsdirectorReplayAnimations")
        ?? throw new InvalidOperationException("Replay animation tree was absent from container data.");
    ITreeAttribute serializedBinding = serializedAnimations.GetTreeAttribute("0")
        ?? throw new InvalidOperationException("Replay animation binding was absent from container data.");
    byte[] serializedLid = serializedBinding.GetBytes("0")
        ?? throw new InvalidOperationException("Replay lid metadata was absent from its animation binding.");
    using (MemoryStream lidStream = new(serializedLid, writable: false))
    using (BinaryReader lidReader = new(lidStream))
    {
        AnimationMetaData lidMetadata = AnimationMetaData.FromBytes(
            lidReader,
            Vintagestory.API.Config.GameVersion.ShortGameVersion);
        if (!string.Equals(lidMetadata.Code, "lidopen", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Replay lid metadata code did not round trip.");
        }
    }

    BlockEntityGeneric restoredContainer = CreateAnimatedContainer(
        out BlockEntityAnimationUtil restoredContainerAnimation,
        lidOpen: false);
    IWorldAccessor replayWorld = ProbeDispatchProxy.Create<IWorldAccessor>((method, _) =>
        method.Name == "get_Side"
            ? EnumAppSide.Client
            : ProbeDispatchProxy.DefaultValue(method.ReturnType));
    applyReplayBlockEntityData.Invoke(
        null,
        new object[] { restoredContainer, openContainerData, replayWorld, false });
    if (!restoredContainerAnimation.activeAnimationsByAnimCode.TryGetValue(
            "lidopen",
            out AnimationMetaData? restoredLid)
        || Math.Abs(restoredLid.AnimationSpeed - 1.8f) > 0.0001f)
    {
        throw new InvalidOperationException("Replay did not restore the open container lid animation.");
    }
    applyReplayBlockEntityData.Invoke(
        null,
        new object[] { restoredContainer, closedContainerData, replayWorld, false });
    if (restoredContainerAnimation.activeAnimationsByAnimCode.Count != 0)
    {
        throw new InvalidOperationException("Replay did not restore the closed container lid state.");
    }
    Console.WriteLine("PASS replay block-entity active animation capture and restore");

    BlockEntityGeneric replayDoorEntity = (BlockEntityGeneric)RuntimeHelpers.GetUninitializedObject(
        typeof(BlockEntityGeneric));
    BEBehaviorDoor replayDoorBehavior = (BEBehaviorDoor)RuntimeHelpers.GetUninitializedObject(
        typeof(BEBehaviorDoor));
    BlockEntityAnimationUtil replayDoorAnimation =
        (BlockEntityAnimationUtil)RuntimeHelpers.GetUninitializedObject(typeof(BlockEntityAnimationUtil));
    AnimatableRenderer replayDoorRenderer =
        (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
    replayDoorAnimation.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>
    {
        ["opened"] = new AnimationMetaData { Code = "opened", Animation = "opened" }
    };
    replayDoorAnimation.renderer = replayDoorRenderer;
    replayDoorBehavior.animUtil = replayDoorAnimation;
    replayDoorEntity.Behaviors = new List<BlockEntityBehavior> { replayDoorBehavior };
    Action? replayDoorRetessellated = createAnimationRetessellationCallback.Invoke(
        null,
        new object[] { replayDoorEntity }) as Action;
    if (replayDoorRetessellated is null || replayDoorRenderer.ShouldRender)
    {
        throw new InvalidOperationException(
            "Replay block-entity animation bypassed the static-mesh retessellation handoff.");
    }
    replayDoorRetessellated();
    if (!replayDoorRenderer.ShouldRender)
    {
        throw new InvalidOperationException(
            "Replay block-entity animation did not become renderable after chunk retessellation.");
    }
    if (createAnimationRetessellationCallback.Invoke(
        null,
        new object[] { replayDoorEntity }) is not null)
    {
        throw new InvalidOperationException(
            "Replay block-entity animation rescheduled a visible renderer handoff.");
    }
    replayDoorAnimation.activeAnimationsByAnimCode.Clear();
    replayDoorRenderer.ShouldRender = false;
    object? replayIdleDoorRetessellated = createAnimationRetessellationCallback.Invoke(
        null,
        new object[] { replayDoorEntity });
    if (replayIdleDoorRetessellated is not null || replayDoorRenderer.ShouldRender)
    {
        throw new InvalidOperationException(
            "Replay block-entity animation scheduled a renderer handoff without an active animation.");
    }
    replayDoorAnimation.activeAnimationsByAnimCode["opened"] =
        new AnimationMetaData { Code = "opened", Animation = "opened" };
    Action staleDoorRetessellated = createAnimationRetessellationCallback.Invoke(
        null,
        new object[] { replayDoorEntity }) as Action
        ?? throw new InvalidOperationException("Replay active door did not schedule a renderer handoff.");
    replayDoorAnimation.activeAnimationsByAnimCode.Clear();
    staleDoorRetessellated();
    if (!replayDoorRenderer.ShouldRender)
    {
        throw new InvalidOperationException(
            "Replay cabinet-style animation left both the static and independent door meshes hidden when its transition finished during retessellation.");
    }
    long staleDoorRevision = (long)(beginReplayAnimationStateChange.Invoke(
        null,
        new object[] { replayDoorAnimation }) ?? -1L);
    long currentDoorRevision = (long)(beginReplayAnimationStateChange.Invoke(
        null,
        new object[] { replayDoorAnimation }) ?? -1L);
    bool staleRevisionCurrent = (bool)(isReplayAnimationRevisionCurrent.Invoke(
        null,
        new object[] { replayDoorAnimation, staleDoorRevision }) ?? true);
    bool currentRevisionCurrent = (bool)(isReplayAnimationRevisionCurrent.Invoke(
        null,
        new object[] { replayDoorAnimation, currentDoorRevision }) ?? false);
    bool staleStartVisible = (bool)(resolveReplayAnimationVisibility.Invoke(
        null,
        new object[] { replayDoorAnimation, staleDoorRevision, true }) ?? true);
    bool currentStartVisible = (bool)(resolveReplayAnimationVisibility.Invoke(
        null,
        new object[] { replayDoorAnimation, currentDoorRevision, true }) ?? false);
    bool currentStopVisible = (bool)(resolveReplayAnimationVisibility.Invoke(
        null,
        new object[] { replayDoorAnimation, currentDoorRevision, false }) ?? true);
    invalidateReplayAnimationOwners.Invoke(null, new object[] { replayDoorEntity });
    bool invalidatedRevisionCurrent = (bool)(isReplayAnimationRevisionCurrent.Invoke(
        null,
        new object[] { replayDoorAnimation, currentDoorRevision }) ?? true);
    long staleBeginAfterInvalidation = (long)(beginReplayAnimationStateChange.Invoke(
        null,
        new object[] { replayDoorAnimation }) ?? -1L);
    bool staleBeginRevivedOwner = (bool)(isReplayAnimationRevisionCurrent.Invoke(
        null,
        new object[] { replayDoorAnimation, staleBeginAfterInvalidation }) ?? true);
    bool removedDoorVisible = (bool)(resolveReplayAnimationVisibility.Invoke(
        null,
        new object[] { replayDoorAnimation, currentDoorRevision, true }) ?? true);
    if (staleRevisionCurrent
        || !currentRevisionCurrent
        || invalidatedRevisionCurrent
        || staleBeginRevivedOwner
        || staleStartVisible
        || !currentStartVisible
        || currentStopVisible
        || removedDoorVisible)
    {
        throw new InvalidOperationException(
            "Replay cabinet animation revisions allow an old or removed renderer callback to override the current door state.");
    }
    Console.WriteLine("PASS replay cabinet animation callback revision ordering");

    BlockEntityGeneric replayCabinetEntity = (BlockEntityGeneric)RuntimeHelpers.GetUninitializedObject(
        typeof(BlockEntityGeneric));
    BEBehaviorCabinetDoors replayCabinetBehavior =
        (BEBehaviorCabinetDoors)RuntimeHelpers.GetUninitializedObject(typeof(BEBehaviorCabinetDoors));
    BlockEntityAnimationUtil replayCabinetAnimation =
        (BlockEntityAnimationUtil)RuntimeHelpers.GetUninitializedObject(typeof(BlockEntityAnimationUtil));
    replayCabinetAnimation.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>
    {
        ["opened"] = new AnimationMetaData { Code = "opened", Animation = "opened" }
    };
    replayCabinetBehavior.animUtil = replayCabinetAnimation;
    replayCabinetEntity.Behaviors = new List<BlockEntityBehavior> { replayCabinetBehavior };
    Action? coldCabinetRetessellated = createAnimationRetessellationCallback.Invoke(
        null,
        new object[] { replayCabinetEntity }) as Action;
    if (coldCabinetRetessellated is null)
    {
        throw new InvalidOperationException(
            "Replay cabinet animation did not schedule renderer creation from a cold state.");
    }
    AnimatableRenderer replayCabinetRenderer =
        (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
    replayCabinetAnimation.renderer = replayCabinetRenderer;
    coldCabinetRetessellated();
    if (!replayCabinetRenderer.ShouldRender)
    {
        throw new InvalidOperationException(
            "Replay cabinet door renderer was not enabled after cold-state retessellation.");
    }

    Type replayWorldPlayback = typeof(VSDirectorModSystem).Assembly.GetType(
        "VSDirector.Replay.DirectorReplayWorldPlayback",
        throwOnError: true)!;
    MethodInfo applyReplayBlockEntity = replayWorldPlayback.GetMethod(
        "ApplyBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity application method was not found.");
    MethodInfo recreateReplayBlockEntity = replayWorldPlayback.GetMethod(
        "RecreateBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity runtime reconciliation method was not found.");
    MethodInfo advanceReplayBaseline = replayWorldPlayback.GetMethod(
        "AdvanceBaselineBlockEntities",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay world baseline processor was not found.");
    MethodInfo disposeReplayWorld = replayWorldPlayback.GetMethod(
        nameof(IDisposable.Dispose),
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay world restoration method was not found.");
    MethodInfo prioritizeExistingChunk = remoteChunkPriorityPatch.GetMethod(
        "PrioritizeExistingChunk",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay animated chunk promotion helper was not found.");
    MethodInfo orderedReplayChunkRedraw = remoteChunkPriorityPatch.GetMethod(
        "RetessellateExistingChunkInOrder",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay ordered chunk redraw helper was not found.");
    MethodInfo markReplayBlockEntity = replayWorldPlayback.GetMethod(
        "MarkReplayBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity ownership marker was not found.");
    MethodInfo unmarkReplayBlockEntity = replayWorldPlayback.GetMethod(
        "UnmarkReplayBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity ownership invalidator was not found.");
    MethodInfo isReplayBlockEntity = replayWorldPlayback.GetMethod(
        "IsReplayBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity ownership query was not found.");
    MethodInfo invalidateAnimationOwners = replaySerialization.GetMethod(
        "InvalidateAnimationOwners",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation owner invalidator was not found.");
    MethodInfo managerIsReplayBlockEntity = replayManager.GetMethod(
        "IsReplayBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay manager block-entity ownership query was not found.");
    MethodInfo captureAnimationOwner = blockEntityStateCapturePatch.GetMethod(
        "Capture",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation capture filter was not found.");
    MethodInfo registerAnimationOwner = blockEntityStateCapturePatch.GetMethod(
        "RegisterAnimationOwner",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation owner registration was not found.");
    MethodInfo unregisterAnimationOwner = blockEntityStateCapturePatch.GetMethod(
        "UnregisterAnimationOwner",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation owner removal was not found.");
    MethodInfo isCurrentAnimationOwner = blockEntityStateCapturePatch.GetMethod(
        "IsCurrentAnimationOwner",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation owner identity guard was not found.");
    MethodInfo animationStartCapturePostfix = blockEntityStateCapturePatch.GetMethod(
        "AnimationStartPostfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation start capture postfix was not found.");
    MethodInfo animationStopCapturePostfix = blockEntityStateCapturePatch.GetMethod(
        "AnimationStopPostfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation stop capture postfix was not found.");
    MethodInfo animationStateChangePrefix = blockEntityStateCapturePatch.GetMethod(
        "AnimationStateChangePrefix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation state-change handoff was not found.");
    MethodInfo completeAnimationStateChange = blockEntityStateCapturePatch.GetMethod(
        "CompleteAnimationStateChange",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation callback completion guard was not found.");
    MethodInfo orderedAnimationRedraw = blockEntityStateCapturePatch.GetMethod(
        "RetessellateReplayAnimationInOrder",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay animation ordered redraw helper was not found.");
    MethodInfo markLiveBlockDirty = AccessTools.Method(
        typeof(IBlockAccessor),
        nameof(IBlockAccessor.MarkBlockDirty),
        new[] { typeof(BlockPos), typeof(Action) })
        ?? throw new InvalidOperationException("Live block dirty callback API was not found.");
    if (!CallsMethod(applyReplayBlockEntity, createAnimationRetessellationCallback)
        || !CallsMethod(applyReplayBlockEntity, orderedReplayChunkRedraw))
    {
        throw new InvalidOperationException(
            "Replay block-entity animations do not retain an ordered retessellation handoff.");
    }
    Console.WriteLine("PASS replay block-entity animation retessellation handoff");

    bool recreateCallsApply = CallsMethod(recreateReplayBlockEntity, applyReplayBlockEntity);
    bool baselineRecreates = CallsMethod(advanceReplayBaseline, recreateReplayBlockEntity);
    bool restoreRecreates = CallsMethod(disposeReplayWorld, recreateReplayBlockEntity);
    bool preservesNestedState = replayWorldPlayback.GetField(
        "isApplyingState",
        BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(Func<bool>);
    bool replayEntityOwnershipGuarded = CallsMethod(applyReplayBlockEntity, markReplayBlockEntity)
        && CallsMethod(applyReplayBlockEntity, unmarkReplayBlockEntity)
        && CallsMethod(unmarkReplayBlockEntity, invalidateAnimationOwners)
        && CallsMethod(managerIsReplayBlockEntity, isReplayBlockEntity)
        && CallsMethod(captureAnimationOwner, managerIsReplayBlockEntity)
        && CallsMethod(animationStartCapturePostfix, captureAnimationOwner)
        && CallsMethod(animationStopCapturePostfix, captureAnimationOwner)
        && CallsMethod(animationStartCapturePostfix, isCurrentAnimationOwner)
        && CallsMethod(animationStopCapturePostfix, isCurrentAnimationOwner)
        && CallsMethodReference(animationStateChangePrefix, markLiveBlockDirty)
        && CallsMethod(animationStateChangePrefix, isWorldPlaybackActive)
        && CallsMethod(animationStateChangePrefix, orderedAnimationRedraw)
        && CallsMethod(completeAnimationStateChange, isReplayAnimationRevisionCurrent)
        && CallsMethod(completeAnimationStateChange, orderedAnimationRedraw)
        && CallsMethod(orderedAnimationRedraw, orderedReplayChunkRedraw);
    if (!recreateCallsApply
        || !baselineRecreates
        || !restoreRecreates
        || !preservesNestedState
        || !replayEntityOwnershipGuarded)
    {
        throw new InvalidOperationException(
            $"Replay block-entity runtime reconciliation is incomplete " +
            $"(apply={recreateCallsApply}, baseline={baselineRecreates}, " +
            $"restore={restoreRecreates}, nested={preservesNestedState}, " +
            $"ownership={replayEntityOwnershipGuarded}).");
    }
    Console.WriteLine("PASS repeat-playback block-entity runtime reconciliation");

    BlockEntityAnimationUtil ownerIdentityAnimation =
        (BlockEntityAnimationUtil)RuntimeHelpers.GetUninitializedObject(
            typeof(BlockEntityAnimationUtil));
    BlockEntityGeneric staleAnimationOwner = new();
    BlockEntityGeneric currentAnimationOwner = new();
    registerAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, staleAnimationOwner });
    bool initialOwnerMatches = (bool)(isCurrentAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, staleAnimationOwner }) ?? false);
    unregisterAnimationOwner.Invoke(null, new object[] { ownerIdentityAnimation });
    bool invalidatedOwnerMatches = (bool)(isCurrentAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, staleAnimationOwner }) ?? true);
    registerAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, currentAnimationOwner });
    bool staleOwnerMatches = (bool)(isCurrentAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, staleAnimationOwner }) ?? true);
    bool currentOwnerMatches = (bool)(isCurrentAnimationOwner.Invoke(
        null,
        new object[] { ownerIdentityAnimation, currentAnimationOwner }) ?? false);
    unregisterAnimationOwner.Invoke(null, new object[] { ownerIdentityAnimation });
    if (!initialOwnerMatches
        || invalidatedOwnerMatches
        || staleOwnerMatches
        || !currentOwnerMatches)
    {
        throw new InvalidOperationException(
            "Replay animation postfix can capture an invalidated or replaced block-entity owner.");
    }
    Console.WriteLine("PASS replay animation owner identity guard");

    MethodInfo stageReplayBlockPair = replayWorldPlayback.GetMethod(
        "StageBlockPair",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-pair staging method was not found.");
    MethodInfo queueReplayChunkRedraw = replayWorldPlayback.GetMethod(
        "QueueChunkRedraw",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk redraw queue was not found.");
    MethodInfo advanceReplayChunkRedraws = replayWorldPlayback.GetMethod(
        "AdvancePendingChunkRedraws",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay chunk redraw processor was not found.");
    if (!CallsMethod(stageReplayBlockPair, queueReplayChunkRedraw)
        || !CallsMethod(advanceReplayChunkRedraws, orderedReplayChunkRedraw))
    {
        throw new InvalidOperationException(
            "Replay block-id changes are not handed off to ordered detached-camera chunk retessellation.");
    }
    Console.WriteLine("PASS replay block-id change chunk retessellation handoff");

    MethodInfo observeLiveBlockChange = replayWorldPlayback.GetMethod(
        "ObserveLiveBlockChange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay latest live-block ledger was not found.");
    MethodInfo observeLiveBlockEntityState = replayWorldPlayback.GetMethod(
        "ObserveLiveBlockEntityState",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay latest live block-entity ledger was not found.");
    MethodInfo observeLiveBlockEntityPacket = replayWorldPlayback.GetMethod(
        "ObserveLiveBlockEntityPacket",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live block-entity packet ledger was not found.");
    MethodInfo queueLiveRealityObservation = replayWorldPlayback.GetMethod(
        "QueueLiveRealityObservation",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-reality observation queue was not found.");
    MethodInfo processPendingLiveRealityObservations = replayWorldPlayback.GetMethod(
        "ProcessPendingLiveRealityObservations",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-reality observation drain was not found.");
    MethodInfo processLiveRealityObservation = replayWorldPlayback.GetMethod(
        "ProcessLiveRealityObservation",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live-reality observation processor was not found.");
    MethodInfo beginApplyingReplayWorldState = replayWorldPlayback.GetMethod(
        "BeginApplyingState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay world nested-application guard was not found.");
    MethodInfo enqueueMainThreadTask = typeof(IEventAPI).GetMethod(
        "EnqueueMainThreadTask",
        new[] { typeof(Action), typeof(string) })
        ?? throw new InvalidOperationException("Main-thread task enqueue API was not found.");
    FieldInfo? pendingLiveRealityObservations = replayWorldPlayback.GetField(
        "pendingLiveRealityObservations",
        BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo? liveRealityObservationTaskQueued = replayWorldPlayback.GetField(
        "liveRealityObservationTaskQueued",
        BindingFlags.Instance | BindingFlags.NonPublic);
    Type? liveRealityObservationKind = replayWorldPlayback.GetNestedType(
        "LiveRealityObservationKind",
        BindingFlags.NonPublic);
    MethodInfo preserveLatestRealityBlockEntity = replayWorldPlayback.GetMethod(
        "PreserveLatestRealityBlockEntity",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay latest reality block-entity snapshot method was not found.");
    MethodInfo reapplyReplayPosition = replayWorldPlayback.GetMethod(
        "ReapplyReplayPosition",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay position isolation method was not found.");
    MethodInfo onReplayBlockChanged = replayManager.GetMethod(
        "OnBlockChanged",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay live block-change observer was not found.");
    MethodInfo captureObservedBlockEntityPacket = replayManager.GetMethod(
        "CaptureObservedBlockEntityPacket",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Observed block-entity packet capture method was not found.");
    MethodInfo livePacketPostfix = blockEntityStateCapturePatch.GetMethod(
        "LiveBlockEntityPacketPostfix",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Live block-entity packet observer postfix was not found.");
    MethodInfo isLiveBlockEntityPacket = blockEntityStateCapturePatch.GetProperty(
        "IsLiveBlockEntityPacket",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetMethod
        ?? throw new InvalidOperationException("Live block-entity packet boundary was not found.");
    MethodInfo isReplayAnimationRedraw = blockEntityStateCapturePatch.GetProperty(
        "IsReplayAnimationRedraw",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetMethod
        ?? throw new InvalidOperationException("Replay animation redraw boundary was not found.");
    bool liveObservationCoordinatesOnly = observeLiveBlockEntityState.GetParameters().Length == 1
        && observeLiveBlockEntityState.GetParameters()[0].ParameterType == typeof(BlockPos);
    bool liveObservationUsesMainQueue = CallsMethod(queueLiveRealityObservation, processPendingLiveRealityObservations)
        && CallsMethodReference(queueLiveRealityObservation, enqueueMainThreadTask);
    bool liveObservationDeduplicatesByPosition = pendingLiveRealityObservations?.FieldType.IsGenericType == true
        && pendingLiveRealityObservations.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && liveRealityObservationTaskQueued?.FieldType == typeof(bool)
        && liveRealityObservationKind?.IsDefined(typeof(FlagsAttribute), inherit: false) == true;
    if (!liveObservationCoordinatesOnly
        || !liveObservationDeduplicatesByPosition
        || !CallsMethod(observeLiveBlockChange, queueLiveRealityObservation)
        || !CallsMethod(observeLiveBlockEntityState, queueLiveRealityObservation)
        || !CallsMethod(observeLiveBlockEntityPacket, queueLiveRealityObservation)
        || !liveObservationUsesMainQueue
        || !CallsMethod(processPendingLiveRealityObservations, processLiveRealityObservation)
        || !CallsMethod(processLiveRealityObservation, reapplyReplayPosition)
        || !CallsMethod(processLiveRealityObservation, preserveLatestRealityBlockEntity)
        || !CallsMethod(processLiveRealityObservation, beginApplyingReplayWorldState)
        || CallsMethod(observeLiveBlockChange, preserveLatestRealityBlockEntity)
        || !CallsMethod(onReplayBlockChanged, observeLiveBlockChange)
        || !CallsMethod(onReplayBlockChanged, isLiveBlockEntityPacket)
        || !CallsMethod(onReplayBlockChanged, isReplayAnimationRedraw)
        || !CallsMethod(captureObservedBlockEntityState, observeLiveBlockEntityState)
        || !CallsMethod(captureObservedBlockEntityPacket, observeLiveBlockEntityPacket)
        || !CallsMethod(livePacketPostfix, captureObservedBlockEntityPacket))
    {
        throw new InvalidOperationException(
            "Reality block updates are not queued on the main thread and hidden behind the active replay frame.");
    }
    Console.WriteLine("PASS queued live block and block-entity reality isolation");
    MethodInfo ownsReplayBlockEntitySound = replayWorldPlayback.GetMethod(
        "OwnsReplayBlockEntitySound",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity sound ownership method was not found.");
    MethodInfo disposeReplayWorldOwnedSounds = replayManager.GetMethod(
        "DisposeReplayWorldOwnedSounds",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay block-entity sound cleanup method was not found.");
    MethodInfo stopReplayPlayback = replayManager.GetMethod(
        "StopPlayback",
        BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("Replay stop method was not found.");
    if (!CallsMethod(shouldAllowLoadedSoundStart, ownsReplayBlockEntitySound)
        || !CallsMethod(stopReplayPlayback, disposeReplayWorldOwnedSounds))
    {
        throw new InvalidOperationException(
            "Replay-created block-entity sounds can still enter the live-sound restoration ledger.");
    }
    MethodInfo readReplayBlockRun = replayWorldPlayback.GetMethod(
        "ReadBlockRun",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Replay point-state decoder was not found.");
    int decodedReplayBlock = (int)(readReplayBlockRun.Invoke(
        null,
        new object[]
        {
            new List<DirectorReplayBlockRun>
            {
                new() { BlockId = 4, Length = 2 },
                new() { BlockId = 9, Length = 3 }
            },
            3
        }) ?? -1);
    if (decodedReplayBlock != 9)
    {
        throw new InvalidOperationException("Replay point-state decoder selected the wrong block run.");
    }
    Console.WriteLine("PASS latest reality block state ledger and replay-frame reapplication");

    foreach (MethodBase target in targets)
    {
        Patches? patches = Harmony.GetPatchInfo(target);
        if (patches?.Owners.Contains(harmonyId) != true)
        {
            throw new InvalidOperationException($"VS Director did not patch {target.DeclaringType?.FullName}.{target.Name}.");
        }

        Console.WriteLine($"PASS {target.DeclaringType?.Name}.{target.Name}");
    }

    MethodInfo? cubeParticleFrameMethod = AccessTools.Method(
        typeof(ParticlePoolCubes), nameof(ParticlePoolCubes.OnNewFrame), new[] { typeof(float), typeof(Vec3d) });
    if (cubeParticleFrameMethod?.DeclaringType != typeof(ParticlePoolQuads))
    {
        throw new InvalidOperationException("ParticlePoolCubes no longer inherits the patched particle frame method.");
    }
    Console.WriteLine("PASS ParticlePoolCubes inherits the patched particle frame method");

    object[] networkMessages =
    {
        new DirectorPerformanceRequest { Session = Guid.NewGuid().ToString(), Action = 3, X = 12, Y = 34, Z = 56, Yaw = 1.25f },
        new DirectorPerformanceStatus { Session = Guid.NewGuid().ToString(), Phase = "sample", X = 12, Y = 34, Z = 56, Seconds = 3.5, Sequence = 17 },
        new DirectorPermissionRequest
        {
            Action = (int)DirectorPermissionRequestAction.Update,
            PlayerUid = "permission-player",
            PermissionMask = (long)(DirectorPermission.CameraLibrary | DirectorPermission.ReplayPlayback)
        },
        new DirectorPermissionMemberDto
        {
            PlayerUid = "permission-player",
            PlayerName = "Permission Player",
            RoleCode = "suplayer",
            Online = true,
            PermissionMask = (long)DirectorPermission.CameraLibrary
        },
        new DirectorPermissionSnapshot
        {
            PermissionMask = (long)DirectorPermission.OffscreenCurrentView,
            Members = new List<DirectorPermissionMemberDto>
            {
                new()
                {
                    PlayerUid = "permission-player",
                    PlayerName = "Permission Player",
                    RoleCode = "suplayer"
                }
            }
        },
        new DirectorAnchorRequest
        {
            Active = true,
            X = 100d,
            Z = 200d,
            Dimension = 0,
            RadiusChunks = 4,
            HasSecondaryAnchor = true,
            SecondaryX = 300d,
            SecondaryZ = 400d,
            SecondaryDimension = 0
        },
        new DirectorAnchorStatus
        {
            Allowed = true,
            TargetRequested = true,
            TargetAvailable = true,
            TargetEntityId = 42
        },
        new DirectorBlockSoundEvent
        {
            Location = "game:sounds/block/rock-break.ogg",
            X = 10.5d,
            Y = 20.5d,
            Z = 30.5d,
            Dimension = 0,
            Pitch = 1.1f,
            Range = 24f,
            Volume = 0.8f,
            SoundType = (int)EnumSoundType.Sound,
            BlockX = 10,
            BlockY = 20,
            BlockZ = 30,
            HasBlockPosition = true
        },
        new DirectorChunkPreloadColumn { X = 10, Z = 20, Dimension = 0 },
        new DirectorChunkPreloadRequest
        {
            Active = true,
            ReplayId = Guid.NewGuid().ToString("D"),
            RadiusChunks = 1,
            Columns = new List<DirectorChunkPreloadColumn>
            {
                new() { X = 10, Z = 20, Dimension = 0 },
                new() { X = 11, Z = 20, Dimension = 0 }
            }
        },
        new DirectorChunkPreloadStatus
        {
            Allowed = true,
            ReplayId = Guid.NewGuid().ToString("D"),
            TotalColumns = 7,
            SentColumns = 5
        },
        new DirectorSharedCameraDto
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Shared probe",
            X = 10d,
            Y = 20d,
            Z = 30d,
            Fov = 70f,
            Dimension = 2
        },
        new DirectorSharedCameraRequest
        {
            Action = (int)DirectorSharedCameraAction.Upsert,
            Camera = new DirectorSharedCameraDto
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Nested request camera",
                X = 1d,
                Y = 2d,
                Z = 3d,
                Fov = 60f,
                Dimension = 1
            }
        },
        new DirectorSharedCameraCatalog
        {
            Cameras = new List<DirectorSharedCameraDto>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = "Nested catalog camera",
                    X = 4d,
                    Y = 5d,
                    Z = 6d,
                    Fov = 80f,
                    Dimension = 3
                }
            }
        }
    };
    foreach (object message in networkMessages)
    {
        using MemoryStream stream = new();
        Serializer.NonGeneric.Serialize(stream, message);
        stream.Position = 0;
        object clone = Serializer.NonGeneric.Deserialize(message.GetType(), stream);
        if (clone.GetType() != message.GetType())
        {
            throw new InvalidOperationException($"Protobuf round trip failed for {message.GetType().Name}.");
        }
        if (message is DirectorPermissionRequest permissionRequest
            && clone is DirectorPermissionRequest clonedPermissionRequest
            && (permissionRequest.PlayerUid != clonedPermissionRequest.PlayerUid
                || permissionRequest.PermissionMask != clonedPermissionRequest.PermissionMask))
        {
            throw new InvalidOperationException("Permission update request did not round trip.");
        }
        if (message is DirectorPermissionSnapshot permissionSnapshot
            && clone is DirectorPermissionSnapshot clonedPermissionSnapshot
            && (clonedPermissionSnapshot.Members.Count != permissionSnapshot.Members.Count
                || clonedPermissionSnapshot.Members[0].PlayerUid
                    != permissionSnapshot.Members[0].PlayerUid))
        {
            throw new InvalidOperationException("Permission snapshot roster did not round trip.");
        }
        if (message is DirectorAnchorRequest anchorRequest
            && clone is DirectorAnchorRequest clonedAnchorRequest
            && (!clonedAnchorRequest.HasSecondaryAnchor
                || clonedAnchorRequest.SecondaryX != anchorRequest.SecondaryX
                || clonedAnchorRequest.SecondaryZ != anchorRequest.SecondaryZ
                || clonedAnchorRequest.SecondaryDimension != anchorRequest.SecondaryDimension))
        {
            throw new InvalidOperationException("Remote chunk secondary anchor did not round trip.");
        }
        if (message is DirectorAnchorStatus anchorStatus
            && clone is DirectorAnchorStatus clonedAnchorStatus
            && (!clonedAnchorStatus.TargetRequested
                || !clonedAnchorStatus.TargetAvailable
                || clonedAnchorStatus.TargetEntityId != anchorStatus.TargetEntityId))
        {
            throw new InvalidOperationException("Remote follow target status did not round trip.");
        }
        if (message is DirectorBlockSoundEvent blockSound
            && clone is DirectorBlockSoundEvent clonedBlockSound
            && (!clonedBlockSound.HasBlockPosition
                || clonedBlockSound.Location != blockSound.Location
                || clonedBlockSound.BlockX != blockSound.BlockX
                || clonedBlockSound.BlockY != blockSound.BlockY
                || clonedBlockSound.BlockZ != blockSound.BlockZ))
        {
            throw new InvalidOperationException("Client block sound event did not round trip.");
        }
        if (message is DirectorChunkPreloadRequest preloadRequest
            && clone is DirectorChunkPreloadRequest clonedPreloadRequest
            && (clonedPreloadRequest.Columns.Count != preloadRequest.Columns.Count
                || clonedPreloadRequest.Columns[1].X != preloadRequest.Columns[1].X))
        {
            throw new InvalidOperationException("Replay chunk preload corridor did not round trip.");
        }
        if (message is DirectorSharedCameraRequest cameraRequest
            && clone is DirectorSharedCameraRequest clonedCameraRequest
            && (!string.Equals(cameraRequest.Camera.Id, clonedCameraRequest.Camera.Id, StringComparison.Ordinal)
                || !string.Equals(cameraRequest.Camera.Name, clonedCameraRequest.Camera.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Shared-camera request payload did not round trip.");
        }
        if (message is DirectorSharedCameraCatalog catalog
            && clone is DirectorSharedCameraCatalog clonedCatalog
            && (clonedCatalog.Cameras.Count != catalog.Cameras.Count
                || !string.Equals(
                    catalog.Cameras[0].Name,
                    clonedCatalog.Cameras[0].Name,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Shared-camera catalog payload did not round trip.");
        }

        Console.WriteLine($"PASS protobuf {message.GetType().Name}");
    }

    DirectorImGuiProbe.Run();
    Console.WriteLine($"{targets.Length}/{targets.Length} Harmony targets and {networkMessages.Length} network messages verified on Vintage Story {typeof(ClientMain).Assembly.GetName().Version}.");
    return 0;
}
finally
{
    harmony.UnpatchAll(harmonyId);
}

internal class ProbeDispatchProxy : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?> handler = null!;

    public static T Create<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = Create<T, ProbeDispatchProxy>();
        ((ProbeDispatchProxy)(object)proxy).handler = handler;
        return proxy;
    }

    public static object? DefaultValue(Type type)
        => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => handler(targetMethod ?? throw new InvalidOperationException("Proxy method was not supplied."), args);
}

internal sealed class ProbeEntityPlayer : EntityPlayer
{
    public bool? ShadowPassDuringSerialization { get; private set; }

    public override void ToBytes(BinaryWriter writer, bool forClient)
    {
        ShadowPassDuringSerialization = selfNowShadowPass;
        writer.Write((byte)(forClient ? 1 : 0));
    }
}

internal sealed class ProbeBlockEntitySerializationScope : BlockEntity
{
    public System.Func<BlockEntity, bool> IsSerializing { get; init; } = null!;

    public BlockEntity? Unrelated { get; init; }

    public bool ThrowDuringSerialization { get; init; }

    public bool WasSerializing { get; private set; }

    public bool UnrelatedWasSerializing { get; private set; }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        WasSerializing = IsSerializing(this);
        UnrelatedWasSerializing = Unrelated is not null && IsSerializing(Unrelated);
        if (ThrowDuringSerialization)
        {
            throw new InvalidOperationException("Probe serialization failure.");
        }

        tree.SetInt("probe", 1);
    }
}

internal sealed class ProbeMannequinBehavior : BEBehaviorMannequin
{
    public bool Refreshed { get; private set; }

    public ProbeMannequinBehavior()
        : base(null!)
    {
    }

    protected override void regenMesh()
        => Refreshed = true;
}

internal class ProbeConstructableBehavior : BEBehaviorRightClickConstructable
{
    public bool Refreshed { get; private set; }

    public ProbeConstructableBehavior()
        : base(null!)
    {
    }

    protected new void updateShape()
        => Refreshed = true;
}

internal sealed class ProbeMechanicalPowerBehavior : BEBehaviorMPBase
{
    private readonly ProbeConstructableBehavior constructableBehavior;

    public bool Refreshed { get; private set; }

    public bool ConstructableWasRefreshed { get; private set; }

    public ProbeMechanicalPowerBehavior(ProbeConstructableBehavior constructableBehavior)
        : base(null!)
    {
        this.constructableBehavior = constructableBehavior;
    }

    public override float GetResistance()
        => 0f;

    protected override void updateShape(IWorldAccessor worldForResolve)
    {
        Refreshed = true;
        ConstructableWasRefreshed = constructableBehavior.Refreshed;
    }
}
