using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Gui;
using VSDirector.Networking;
using VSDirector.Recording;
using VSDirector.Rendering;
using VSDirector.Replay;
using VSDirector.Runtime;
using VSDirector.Storage;
using DirectorKeyframe = VSDirector.Data.CameraKeyframe;

namespace VSDirector;

public sealed partial class VSDirectorModSystem : ModSystem
{
    internal const string VanillaPlayerModelCode = "game:player";
    private const string ConfigFileName = "VSDirector.json";
    private const string HarmonyId = "vscn.vsdirector.camera";
    private const string ServerHarmonyId = "vscn.vsdirector.remotechunks";
    private const string PanelHotkey = "vsdirector-panel";
    private const string WorkspaceModeHotkey = "vsdirector-workspace-mode";
    private const string ExitHotkey = "vsdirector-exit";
    private const string ReplayRecordingStartHotkey = "vsdirector-replay-recording-start";
    private const string ReplayRecordingStopHotkey = "vsdirector-replay-recording-stop";
    private const string QuickSlotHotkeyPrefix = "vsdirector-alt-slot-";
    private const long RemoteFollowLoadTimeoutMs = 30000;
    private const int RemoteFollowReadyRadiusChunks = 1;
    private static readonly string[] CoreVanillaPlayerAnimations =
    {
        "idle", "walk", "sprint", "sneakidle", "sneakwalk", "swim", "swimidle",
        "jump", "climbidle", "climbup", "creativefly"
    };

    private ICoreClientAPI? capi;
    private ICoreServerAPI? sapi;
    private Harmony? harmony;
    private Harmony? serverHarmony;
    private DirectorConfig config = new();
    private DirectorCameraController? controller;
    private DirectorPathPreviewRenderer? pathPreview;
    private DirectorModelOutlineRenderer? modelOutlineRenderer;
    private DirectorGreenScreenSkyRenderer? greenScreenSkyRenderer;
    private DirectorGreenScreenPreviewRenderer? greenScreenPreviewRenderer;
    private DirectorReplayCompositionOverlay? replayCompositionOverlay;
    private DirectorWorkspaceHud? workspace;
    private DirectorSettingsDialog? settingsDialog;
    private WorldCameraStore? store;
    private DirectorPresetStore? presetStore;
    private DirectorActionStore? actionStore;
    private DirectorModelStore? modelStore;
    private DirectorVsmc2FileWatcher? vsmc2FileWatcher;
    private readonly DirectorVsmc2ProcessBridge vsmc2ProcessBridge = new();
    private readonly Dictionary<Guid, DirectorVsmc2ShapeDocument> pendingVsmc2Shapes = new();
    private readonly Dictionary<Guid, string> vsmc2ShapeStatuses = new();
    private DirectorActionModIntegration? actionModIntegration;
    private IReadOnlyList<DirectorExternalModelInfo>? vanillaActionModels;
    private bool ensuringVanillaActionLibrary;
    private bool vanillaActionLibraryReady;
    private long nextVanillaActionLibraryRetryTimestamp;
    private DirectorActionEnvironmentController? actionEnvironment;
    private CameraLibrary? library;
    private DirectorAnchorClient? anchorClient;
    private PendingRemoteFollow? pendingRemoteFollow;
    private PendingRemoteLiveOffscreen? pendingRemoteLiveOffscreen;
    private DirectorChunkPreloadClient? chunkPreloadClient;
    private Guid previewChunkPreloadId;
    private DirectorReplayPreloadPlan? previewChunkPreloadPlan;
    private DirectorAnchorServer? anchorServer;
    private DirectorPermissionServer? permissionServer;
    private DirectorPermissionClient? permissionClient;
    private DirectorSharedCameraClient? sharedCameraClient;
    private DirectorSharedCameraServer? sharedCameraServer;
    private DirectorRecordingCoordinator? recording => null;
    private DirectorRecordingSessionStatus? latestRecordingServerStatus;
    private Guid? assignedRecordingTrackId;
    private DirectorLiveWorkflowRunner? liveWorkflowRunner;
    private DirectorReplayManager? replay;
    private Guid replayElementRuntimeReplayId;
    private double replayElementPreviousTime = -1d;
    private bool replayElementFovWasApplied;
    private readonly Dictionary<string, Guid> replayElementAppliedBlocks = new(StringComparer.Ordinal);
    private DirectorOffscreenMultiViewRenderer? offscreenRenderer;
    private DirectorLiveOffscreenSession? liveOffscreenSession;
    private DirectorObsWebSocketClient? liveOffscreenObs;
    private DirectorOffscreenFfmpegSink? liveOffscreenFileSink;
    private CancellationTokenSource? liveOffscreenCancellation;
    private Task? liveOffscreenFileOperation;
    private LiveOffscreenRecordingState liveOffscreenState;
    private DirectorLiveOffscreenBackend liveOffscreenActiveBackend;
    private long liveOffscreenOperationId;
    private string liveOffscreenSourceName = string.Empty;
    private bool liveOffscreenUsesSavedSource;
    private string liveOffscreenStatus = string.Empty;
    private string liveOffscreenOutputPath = string.Empty;
    private string liveOffscreenVoiceWavePath = string.Empty;
    private CameraFollowRig? liveOffscreenFollowRig;
    private readonly List<DirectorOffscreenFfmpegSink> offscreenSinks = new();
    private readonly List<DirectorOffscreenFfmpegSink> offscreenOperationSinks = new();
    private bool offscreenCaptureActive;
    private bool offscreenCaptureStarting;
    private bool offscreenCaptureStopping;
    private long offscreenOperationId;
    private Task? offscreenOperationTask;
    private CancellationTokenSource? offscreenStartCancellation;
    private string offscreenOperationDirectory = string.Empty;
    private int offscreenOperationViewCount;
    private DateTimeOffset offscreenCaptureStartedUtc;
    private string offscreenLastResult = string.Empty;
    private DirectorWorkflowMultiViewCapture? workflowMultiViewCapture;
    private DirectorWorkflowMultiViewManifest? workflowMultiViewManifest;
    private string workflowMultiViewManifestPath = string.Empty;
    private string workflowMultiViewFinalStatus = string.Empty;
    private readonly Dictionary<Guid, WorkflowMultiViewFollowState> workflowMultiViewFollowStates = new();
    private long tickListenerId = -1;
    private bool accessAttempted;
    private bool recordingInputLock;
    private bool replayInputLock;
    private bool automaticReplayRecording;
    private bool automaticReplayAuthorizationPending;
    private ReplaySavePurpose pendingReplaySavePurpose;
    private Action? pendingAutomaticReplayAction;
    private ActiveTakeCapture? activeTakeCapture;
    private Guid? directWorkflowExpectedId;
    private Guid? directWorkflowAssignedTrackId;
    private Guid? workflowPreviousAssignment;
    private bool restoreWorkflowAssignmentPending;
    private WorkflowReplayRecordingSession? workflowReplayRecording;
    private Guid? actionWorkflowReplayRecordingId;
    private Guid? actionWorkflowVideoRecordingId;
    private DirectorReplayCameraState replayCameraState = DirectorReplayCameraState.Inactive;
    private DirectorReplayCameraAuthority replayViewportCameraAuthority =
        DirectorReplayCameraAuthority.Timeline;
    private string replayAttachedActorKey = string.Empty;
    private string replayAttachedActorName = string.Empty;
    private CameraFollowViewMode? replayAttachedActorView;
    private Guid? replayAttachedRigId;
    private ActiveActionPreview? activeActionPreview;
    private Guid activeActionWorkflowId;
    private LiveActionWorkflowPlayback? liveActionWorkflowPlayback;
    private readonly Dictionary<ActionEvaluatorCacheKey, DirectorActionEvaluator> actionEvaluators = new();
    private readonly Dictionary<Guid, DirectorActorPerformanceEvaluator> actorPerformanceEvaluators = new();
    private readonly Dictionary<long, string> appliedActionModels = new();
    private readonly Dictionary<long, string> appliedActionExpressions = new();
    private readonly Dictionary<string, double> actionEventTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionTriggerPlaybackState> actionTriggerPlaybacks = new(StringComparer.Ordinal);
    private string lastTakeManifestPath = string.Empty;
    private bool disposed;
    private bool leavingWorld;
    internal bool IsShuttingDown => disposed || leavingWorld;
    private readonly DirectorVoiceBridge voiceBridge = new();
    private readonly DirectorVoiceApi voiceApi;

    public VSDirectorModSystem()
    {
        voiceApi = new DirectorVoiceApi(voiceBridge);
    }

    internal IReadOnlyList<CameraPreset> Cameras => library?.Ordered ?? Array.Empty<CameraPreset>();

    internal bool HasClearView => capi?.ModLoader.IsModEnabled("clearview") == true;

    internal void NotifyClearViewUnavailable()
        => Show(Lang.Get("vsdirector:follow-clear-view-unavailable"));

    internal IReadOnlyList<CameraPath> Paths => library?.OrderedPaths ?? Array.Empty<CameraPath>();

    internal IReadOnlyList<CameraSequence> Sequences => library?.OrderedSequences ?? Array.Empty<CameraSequence>();

    internal IReadOnlyList<DirectorWorkflow> Workflows
        => library?.OrderedWorkflows ?? Array.Empty<DirectorWorkflow>();

    internal IReadOnlyList<DirectorActionAsset> Actions
        => library?.OrderedActions ?? Array.Empty<DirectorActionAsset>();

    internal IReadOnlyList<DirectorModelAsset> Models
        => library?.OrderedModels ?? Array.Empty<DirectorModelAsset>();

    internal IReadOnlyList<DirectorActorPerformance> ActorPerformances
        => library?.OrderedActorPerformances ?? Array.Empty<DirectorActorPerformance>();

    internal bool IsDirectorAdmin => permissionClient?.IsAdmin == true;

    internal IReadOnlyList<DirectorPermissionMemberDto> PermissionMembers
        => permissionClient?.Members ?? Array.Empty<DirectorPermissionMemberDto>();

    internal bool HasPermission(DirectorPermission permission)
        => permissionClient?.Has(permission)
            ?? permission == DirectorPermission.OffscreenCurrentView;

    internal bool HasWorkspacePermission => new[]
    {
        DirectorPermission.CameraLibrary,
        DirectorPermission.PathLibrary,
        DirectorPermission.FollowCameras,
        DirectorPermission.ReplayCapture,
        DirectorPermission.ReplayPlayback,
        DirectorPermission.OffscreenSavedSources,
    }.Any(HasPermission);

    internal IReadOnlyList<DirectorExternalModelInfo> ActionModels
        => DiscoverVanillaActionModels()
            .Concat(actionModIntegration?.DiscoverModels() ?? Array.Empty<DirectorExternalModelInfo>())
            .Concat(DiscoverGreenScreenModels())
            .GroupBy(model => model.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private IReadOnlyList<DirectorExternalModelInfo> DiscoverGreenScreenModels()
    {
        if (!config.EnableGreenScreenSupport || !config.EnableGreenScreenModels)
        {
            return Array.Empty<DirectorExternalModelInfo>();
        }
        string group = Lang.Get("vsdirector:model-green-screen-group");
        return new[]
        {
            new DirectorExternalModelInfo(
                DirectorGreenScreenPreviewRenderer.PlaneModelCode,
                string.Empty,
                Lang.Get("vsdirector:model-green-screen-plane"),
                group,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                PreviewOnly: true),
            new DirectorExternalModelInfo(
                DirectorGreenScreenPreviewRenderer.RingModelCode,
                string.Empty,
                Lang.Get("vsdirector:model-green-screen-ring"),
                group,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                PreviewOnly: true),
            new DirectorExternalModelInfo(
                DirectorGreenScreenPreviewRenderer.FloorModelCode,
                string.Empty,
                Lang.Get("vsdirector:model-green-screen-floor"),
                group,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                PreviewOnly: true)
        };
    }

    private IReadOnlyList<DirectorExternalModelInfo> DiscoverVanillaActionModels()
    {
        if (vanillaActionModels is not null
            && vanillaActionModels.FirstOrDefault(model => string.Equals(
                model.Code,
                VanillaPlayerModelCode,
                StringComparison.OrdinalIgnoreCase)) is { } cachedPlayer
            && HasCoreVanillaPlayerAnimations(cachedPlayer.Animations))
        {
            return vanillaActionModels;
        }

        List<DirectorExternalModelInfo> models = new();
        EntityPlayer? currentPlayer = capi?.World.Player?.Entity;
        foreach (EntityProperties properties in capi?.World?.EntityTypes ?? new List<EntityProperties>())
        {
            AssetLocation? location = properties.Code;
            string entityCode = location?.ToShortString() ?? string.Empty;
            EntityClientProperties? client = properties.Client;
            bool player = location is not null
                && string.Equals(location.Domain, "game", StringComparison.OrdinalIgnoreCase)
                && string.Equals(location.Path, "player", StringComparison.OrdinalIgnoreCase);
            EntityClientProperties? runtimeClient = player ? currentPlayer?.Properties?.Client : null;
            IReadOnlyList<string> animations = CollectVanillaAnimationCodes(
                player,
                runtimeClient,
                client);
            if (location is null
                || entityCode.Length == 0
                || client is null
                || string.IsNullOrWhiteSpace(client.RendererName)
                || (!player && client.LoadedShape is null && client.Shape is null)
                || (!player && animations.Count == 0)
                || IsUnsafeGuiPreviewEntityClass(properties.Class))
            {
                continue;
            }

            string modelCode = player ? VanillaPlayerModelCode : "entity/" + entityCode;
            IReadOnlyList<string> expressions = player
                ? actionModIntegration?.DiscoverVanillaExpressions(currentPlayer) ?? Array.Empty<string>()
                : Array.Empty<string>();
            string playerName = capi?.World.Player?.PlayerName?.Trim() ?? string.Empty;
            models.Add(new DirectorExternalModelInfo(
                modelCode,
                entityCode,
                player
                    ? playerName.Length > 0
                        ? Lang.Get("vsdirector:model-current-player", playerName)
                        : Lang.Get("vsdirector:model-vanilla-player")
                    : VanillaEntityName(location),
                Lang.Get("vsdirector:model-vanilla-group"),
                string.Empty,
                animations,
                expressions));
        }

        if (models.All(model => !string.Equals(
                model.Code,
                VanillaPlayerModelCode,
                StringComparison.OrdinalIgnoreCase)))
        {
            EntityClientProperties? currentClient = currentPlayer?.Properties?.Client;
            EntityClientProperties? registeredClient = capi?.World.EntityTypes.FirstOrDefault(properties =>
                string.Equals(properties.Code?.Domain, "game", StringComparison.OrdinalIgnoreCase)
                && string.Equals(properties.Code?.Path, "player", StringComparison.OrdinalIgnoreCase))?.Client;
            IReadOnlyList<string> currentAnimations = CollectVanillaAnimationCodes(
                player: true,
                currentClient,
                registeredClient);
            IReadOnlyList<string> currentExpressions = actionModIntegration
                ?.DiscoverVanillaExpressions(currentPlayer) ?? Array.Empty<string>();
            string currentName = capi?.World.Player?.PlayerName?.Trim() ?? string.Empty;
            models.Add(new DirectorExternalModelInfo(
                VanillaPlayerModelCode,
                currentPlayer?.Code?.ToShortString() ?? VanillaPlayerModelCode,
                currentName.Length > 0
                    ? Lang.Get("vsdirector:model-current-player", currentName)
                    : Lang.Get("vsdirector:model-vanilla-player"),
                Lang.Get("vsdirector:model-vanilla-group"),
                string.Empty,
                currentAnimations,
                currentExpressions));
        }

        vanillaActionModels = models
            .OrderByDescending(model => string.Equals(
                model.Code,
                VanillaPlayerModelCode,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.EntityCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return vanillaActionModels;
    }

    private static IReadOnlyList<string> CollectVanillaAnimationCodes(
        bool player,
        params EntityClientProperties?[] clients)
    {
        IEnumerable<string> metadataCodes = clients
            .Where(client => client is not null)
            .SelectMany(client => (client!.AnimationsByMetaCode?.Values
                    ?? Enumerable.Empty<AnimationMetaData>())
                .Concat(client.Animations ?? Array.Empty<AnimationMetaData>()))
            .Where(animation => animation is not null)
            .SelectMany(animation => new[] { animation.Code, animation.Animation });
        IEnumerable<string> shapeCodes = clients
            .Where(client => client is not null)
            .SelectMany(client => new[] { client!.LoadedShapeForEntity, client.LoadedShape })
            .Where(shape => shape?.Animations is not null)
            .SelectMany(shape => shape!.Animations)
            .Where(animation => animation is not null)
            .SelectMany(animation => new[] { animation.Code, animation.Name });
        return metadataCodes
            .Concat(shapeCodes)
            .Select(code => code?.Trim() ?? string.Empty)
            .Select(code => player ? DirectorReplayMath.NormalizePlayerAnimationCode(code) : code)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasCoreVanillaPlayerAnimations(IEnumerable<string> animations)
    {
        HashSet<string> codes = animations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "walk", "sprint", "swim" }.All(codes.Contains);
    }

    private static bool IsVanillaPlayerAnimationCode(string animationCode)
    {
        string code = animationCode?.Trim() ?? string.Empty;
        int separator = code.IndexOf(':');
        return separator < 0 || code.StartsWith("game:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMisclassifiedEmoteAction(DirectorActionAsset action)
        => string.Equals(action.SourceModId, "game", StringComparison.OrdinalIgnoreCase)
            && action.SourceCode.StartsWith("vanilla-animation:emotes:", StringComparison.OrdinalIgnoreCase)
            && action.Tags.Contains("vanilla", StringComparer.OrdinalIgnoreCase)
            && action.Tags.Contains("player-animation", StringComparer.OrdinalIgnoreCase);

    private bool RepairMisclassifiedEmoteActions()
    {
        if (library is null || actionStore is null)
        {
            return false;
        }
        DirectorActionAsset[] misplaced = library.OrderedActions
            .Where(IsMisclassifiedEmoteAction)
            .ToArray();
        if (misplaced.Length == 0)
        {
            return false;
        }
        IReadOnlyList<DirectorExternalActionInfo> emotes = actionModIntegration?.DiscoverEmotes()
            ?? Array.Empty<DirectorExternalActionInfo>();
        bool changed = false;
        foreach (DirectorActionAsset action in misplaced)
        {
            string runtimeCode = action.SourceCode["vanilla-animation:".Length..];
            string shortCode = runtimeCode.StartsWith("emotes:", StringComparison.OrdinalIgnoreCase)
                ? runtimeCode["emotes:".Length..]
                : runtimeCode;
            DirectorExternalActionInfo? external = emotes.FirstOrDefault(candidate =>
                string.Equals(candidate.Code, runtimeCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Code, shortCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Animation, runtimeCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Animation, shortCode, StringComparison.OrdinalIgnoreCase));
            string sourceCode = external?.Code ?? runtimeCode;
            DirectorActionAsset? canonical = library.OrderedActions.FirstOrDefault(candidate =>
                candidate.Id != action.Id
                && string.Equals(candidate.SourceModId, "emotes", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.SourceCode, sourceCode, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
            {
                if (library.RemoveAction(action.Id, canonical.Id))
                {
                    actionStore.Delete(action.Id);
                    changed = true;
                }
                continue;
            }

            DirectorActionAsset repaired = action.DeepClone();
            repaired.SourceModId = "emotes";
            repaired.SourceCode = sourceCode;
            repaired.Name = external?.Name ?? Lang.Get("vsdirector:emote-action-name", shortCode);
            repaired.Tags = repaired.Tags
                .Where(tag => !tag.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                    && !tag.Equals("player-animation", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { "emote", shortCode })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (library.UpdateAction(repaired))
            {
                actionStore.Delete(action.Id);
                actionStore.Save(library.FindAction(action.Id)!);
                changed = true;
            }
        }
        if (changed)
        {
            store?.Save(library);
            actionEvaluators.Clear();
        }
        return changed;
    }

    private void EnsureVanillaExpressionLibrary()
    {
        IReadOnlyList<string> expressions = actionModIntegration
            ?.DiscoverVanillaExpressions(capi?.World.Player?.Entity) ?? Array.Empty<string>();
        if (library is null || actionStore is null || expressions.Count == 0)
        {
            return;
        }
        bool changed = false;
        foreach (string expression in expressions)
        {
            string sourceCode = "facialexpression:" + expression;
            bool exists = library.OrderedActions.Any(action =>
                string.Equals(action.SourceModId, "game", StringComparison.OrdinalIgnoreCase)
                && string.Equals(action.SourceCode, sourceCode, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                continue;
            }
            DirectorActionAsset action = DirectorActionImporter.CreateExpressionAction(
                Lang.Get("vsdirector:vanilla-expression-name", expression),
                expression,
                VanillaPlayerModelCode,
                sourceModId: "game");
            action.SourceCode = sourceCode;
            DirectorActionAsset added = library.AddAction(action);
            actionStore.Save(added);
            changed = true;
        }
        if (changed)
        {
            store?.Save(library);
            actionEvaluators.Clear();
        }
    }

    private void EnsureVanillaActionLibrary()
    {
        if (library is null || actionStore is null)
        {
            return;
        }
        DirectorExternalModelInfo? playerModel = DiscoverVanillaActionModels().FirstOrDefault(model =>
            string.Equals(model.Code, VanillaPlayerModelCode, StringComparison.OrdinalIgnoreCase));
        if (playerModel is null)
        {
            return;
        }
        HashSet<string> existing = library.OrderedActions
            .Where(action => string.Equals(action.SourceModId, "game", StringComparison.OrdinalIgnoreCase))
            .Select(action => action.SourceCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (string animationCode in playerModel.Animations
            .Concat(CoreVanillaPlayerAnimations)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Where(IsVanillaPlayerAnimationCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            string sourceCode = "vanilla-animation:" + animationCode;
            if (!existing.Add(sourceCode))
            {
                continue;
            }
            DirectorExternalAnimationInfo animationInfo = ResolveModelAnimationInfo(
                playerModel.Code,
                playerModel.EntityCode,
                animationCode);
            AnimationMetaData metadata = animationInfo.Metadata;
            float animationSpeed = float.IsFinite(metadata.AnimationSpeed)
                ? metadata.AnimationSpeed
                : 1f;
            double frameSpan = animationInfo.EndHandling == EnumEntityAnimationEndHandling.Repeat
                ? animationInfo.QuantityFrames
                : animationInfo.QuantityFrames - 1d;
            double durationSeconds = Math.Clamp(
                frameSpan / (30d * Math.Max(0.01f, Math.Abs(animationSpeed))),
                1d / 60d,
                600d);
            string configuredCode = string.IsNullOrWhiteSpace(metadata.Code)
                ? animationCode
                : metadata.Code.Trim();
            string stateCode = string.IsNullOrWhiteSpace(metadata.Animation)
                ? animationCode
                : metadata.Animation.Trim();
            DirectorActionAsset action = DirectorActionImporter.CreateAnimationAction(
                Lang.Get("vsdirector:vanilla-action-name", animationCode),
                configuredCode,
                sourceModId: "game",
                sourceCode: sourceCode,
                durationSeconds: durationSeconds,
                animationSpeed: animationSpeed,
                weight: float.IsFinite(metadata.Weight) ? metadata.Weight : 1f,
                easeInSpeed: float.IsFinite(metadata.EaseInSpeed) ? metadata.EaseInSpeed : 10f,
                easeOutSpeed: float.IsFinite(metadata.EaseOutSpeed) ? metadata.EaseOutSpeed : 10f,
                blendMode: (int)metadata.BlendMode);
            action.Category = DirectorActionCategory.Player;
            action.CompatibleEntityCodes = new List<string> { playerModel.EntityCode };
            action.CompatibleModelCodes = new List<string> { playerModel.Code };
            action.PreviewModelCode = playerModel.Code;
            action.LoopMode = animationInfo.EndHandling == EnumEntityAnimationEndHandling.Repeat
                ? DirectorActionLoopMode.Loop
                : DirectorActionLoopMode.Once;
            action.Tags = action.Tags
                .Concat(new[] { "vanilla", "player-animation", animationCode })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (DirectorReplayAnimationSample state in action.Layers
                .SelectMany(layer => layer.Frames)
                .SelectMany(frame => frame.State.AnimationStates))
            {
                state.Code = configuredCode;
                state.StateCode = stateCode;
                state.ElementWeights = metadata.ElementWeight is null
                    ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, float>(metadata.ElementWeight, StringComparer.OrdinalIgnoreCase);
                state.ElementBlendModes = metadata.ElementBlendMode is null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : metadata.ElementBlendMode.ToDictionary(
                        item => item.Key,
                        item => (int)item.Value,
                        StringComparer.OrdinalIgnoreCase);
            }
            DirectorActionAsset added = library.AddAction(DirectorActionNormalizer.Normalize(action));
            actionStore.Save(added);
            changed = true;
        }
        if (changed)
        {
            store?.Save(library);
            actionEvaluators.Clear();
        }
    }

    internal bool EnsureVanillaActionsAvailable(bool forceRefresh = false)
    {
        if (ensuringVanillaActionLibrary || library is null || actionStore is null)
        {
            return false;
        }
        ensuringVanillaActionLibrary = true;
        try
        {
            if (forceRefresh || !vanillaActionLibraryReady)
            {
                vanillaActionModels = null;
            }
            bool repaired = RepairMisclassifiedEmoteActions();
            int before = library.OrderedActions.Count;
            EnsureVanillaActionLibrary();
            EnsureVanillaExpressionLibrary();
            DirectorExternalModelInfo? playerModel = DiscoverVanillaActionModels().FirstOrDefault(model =>
                string.Equals(model.Code, VanillaPlayerModelCode, StringComparison.OrdinalIgnoreCase));
            vanillaActionLibraryReady = playerModel is not null
                && HasCoreVanillaPlayerAnimations(playerModel.Animations)
                && CoreVanillaPlayerAnimations.Where(code => code is "walk" or "sprint" or "swim")
                    .All(code => library.OrderedActions.Any(action =>
                        string.Equals(action.SourceModId, "game", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            action.SourceCode,
                            "vanilla-animation:" + code,
                            StringComparison.OrdinalIgnoreCase)));
            return repaired || library.OrderedActions.Count > before;
        }
        finally
        {
            ensuringVanillaActionLibrary = false;
        }
    }

    private void UpdateVanillaActionLibrary()
    {
        if (vanillaActionLibraryReady || library is null || actionStore is null)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (now < nextVanillaActionLibraryRetryTimestamp)
        {
            return;
        }
        nextVanillaActionLibraryRetryTimestamp = now + Stopwatch.Frequency * 2L;
        if (EnsureVanillaActionsAvailable(forceRefresh: true))
        {
            workspace?.Refresh();
        }
    }

    private static bool IsUnsafeGuiPreviewEntityClass(string? className)
    {
        string normalized = className?.Trim() ?? string.Empty;
        return normalized.Contains("DressedHumanoid", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Trader", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Villager", StringComparison.OrdinalIgnoreCase);
    }

    private static string VanillaEntityName(AssetLocation code)
    {
        string? translated = Lang.GetMatchingIfExists($"{code.Domain}:item-creature-{code.Path}")
            ?? Lang.GetMatchingIfExists($"{code.Domain}:item-creature-{code.Path.Replace("-", string.Empty)}");
        return string.IsNullOrWhiteSpace(translated) ? code.Path.Replace('-', ' ') : translated;
    }

    internal bool HasPlayerModelLibrary => actionModIntegration?.HasPlayerModelLibrary == true;

    internal bool HasEmotesLibrary => actionModIntegration?.HasEmotes == true;

    internal Entity? ActionPreviewEntity => replay?.ActionPreviewEntity;

    internal bool HasActionPreviewVisual
        => greenScreenPreviewRenderer?.IsActive == true || replay?.ActionPreviewEntity is not null;

    internal bool RotateActionPreview(double deltaYaw)
        => greenScreenPreviewRenderer?.IsActive == true
            ? greenScreenPreviewRenderer.Rotate(deltaYaw)
            : replay?.RotateActionPreview(deltaYaw) == true;

    internal bool ScaleActionPreviewDistance(double scale)
        => greenScreenPreviewRenderer?.IsActive == true
            ? greenScreenPreviewRenderer.ScaleDistance(scale)
            : replay?.ScaleActionPreviewDistance(scale) == true;

    internal bool ResetActionPreviewTransform()
        => greenScreenPreviewRenderer?.IsActive == true
            ? greenScreenPreviewRenderer.ResetTransform()
            : replay?.ResetActionPreviewTransform() == true;

    internal string ActionDirectory => actionStore?.DirectoryPath ?? string.Empty;

    internal string ModelDirectory => modelStore?.DirectoryPath ?? string.Empty;

    internal Guid PreviewActionId => activeActionPreview?.Action.Id ?? Guid.Empty;

    internal string PreviewActionActorKey => activeActionPreview?.ActorKey ?? string.Empty;

    internal Guid ActiveActionWorkflowId => activeActionWorkflowId;

    internal IReadOnlyList<CameraFollowRig> FollowRigs
        => library?.OrderedFollowRigs ?? Array.Empty<CameraFollowRig>();

    internal IReadOnlyList<CameraPreset> SharedCameras
        => sharedCameraClient?.Cameras ?? Array.Empty<CameraPreset>();

    internal IReadOnlyList<DirectorRecordingTrack> RecordingTracks
        => library?.OrderedRecordingTracks ?? Array.Empty<DirectorRecordingTrack>();

    internal IReadOnlyList<DirectorRecordingTrack> ReplayExportTracks
        => Array.Empty<DirectorRecordingTrack>();

    private static bool ObsOnlyRecording => true;

    internal IReadOnlyList<DirectorReplayInfo> Replays
        => replay?.List() ?? Array.Empty<DirectorReplayInfo>();

    internal IReadOnlyList<DirectorReplayActorPathSource> ReplayActorPathSources
        => replay?.ActorPathSources ?? Array.Empty<DirectorReplayActorPathSource>();

    internal bool TryPickReplayEntity(
        Vec3d rayStart,
        Vec3d rayEnd,
        out DirectorReplayEntityInfo entity)
    {
        if (replay?.TryPickReplayEntity(rayStart, rayEnd, out entity) == true)
        {
            return true;
        }

        entity = default;
        return false;
    }

    internal bool TryResolveReplayActor(
        string actorSelector,
        out DirectorReplayActorPathSource source,
        out Entity? entity)
    {
        if (replay?.TryResolveActor(actorSelector, out source, out entity) == true)
        {
            return true;
        }

        source = default;
        entity = null;
        return false;
    }

    internal bool TryGetReplayTrackingTargetPose(
        Entity target,
        string? bodyPart,
        out DirectorReplayTrackedTargetPose pose)
        => TryCreateReplayTrackingTargetPose(target, bodyPart, out pose);

    private static bool TryCreateReplayTrackingTargetPose(
        Entity target,
        string? bodyPart,
        out DirectorReplayTrackedTargetPose pose)
    {
        ArgumentNullException.ThrowIfNull(target);
        pose = default;
        if (!double.IsFinite(target.Pos.X)
            || !double.IsFinite(target.Pos.Y)
            || !double.IsFinite(target.Pos.Z))
        {
            return false;
        }

        string part = bodyPart?.Trim().ToLowerInvariant() ?? "head";
        if (part.Length == 0)
        {
            part = "head";
        }
        double x = target.Pos.X;
        double y = target.Pos.Y;
        double z = target.Pos.Z;
        double yawRadians;
        double pitchRadians;
        switch (part)
        {
            case "head":
                x += target.LocalEyePos.X;
                y += target.LocalEyePos.Y;
                z += target.LocalEyePos.Z;
                yawRadians = DirectorEntityViewResolver.ResolveCameraYaw(target);
                pitchRadians = DirectorEntityViewResolver.ResolveCameraPitch(target);
                break;
            case "body":
                if (target.SelectionBox is Cuboidf box)
                {
                    x += (box.X1 + box.X2) * 0.5d;
                    y += (box.Y1 + box.Y2) * 0.5d;
                    z += (box.Z1 + box.Z2) * 0.5d;
                }
                else
                {
                    y += 0.9d;
                }
                yawRadians = DirectorEntityViewResolver.ResolveBodyYaw(target);
                pitchRadians = 0d;
                break;
            case "root":
                yawRadians = 0d;
                pitchRadians = 0d;
                break;
            default:
                return false;
        }

        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(z)
            || !double.IsFinite(yawRadians)
            || !double.IsFinite(pitchRadians))
        {
            return false;
        }

        const double degreesPerRadian = 180d / Math.PI;
        pose = new DirectorReplayTrackedTargetPose(
            x,
            y,
            z,
            target.Pos.Dimension,
            yawRadians * degreesPerRadian,
            pitchRadians * degreesPerRadian);
        return true;
    }

    internal bool TryLookAtReplayEntity(Entity target, out string message)
    {
        ArgumentNullException.ThrowIfNull(target);
        message = string.Empty;
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (!EnsurePermission(DirectorPermission.FreeCamera, out message))
        {
            Show(message);
            return false;
        }
        if (!controller.TryCaptureCurrentView("Replay entity look-at", out CameraPreset pose))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        double targetX = target.Pos.X;
        double targetY = target.Pos.Y;
        double targetZ = target.Pos.Z;
        if (target.SelectionBox is Cuboidf box)
        {
            targetX += (box.X1 + box.X2) * 0.5d;
            targetY += (box.Y1 + box.Y2) * 0.5d;
            targetZ += (box.Z1 + box.Z2) * 0.5d;
        }
        double dx = targetX - pose.X;
        double dy = targetY - pose.Y;
        double dz = targetZ - pose.Z;
        double horizontal = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(horizontal) || horizontal < 0.0001d)
        {
            message = Lang.Get("vsdirector:replay-entity-look-at-invalid");
            Show(message);
            return false;
        }

        pose.Yaw = (float)(Math.Atan2(-dx, -dz) * 180d / Math.PI);
        pose.Pitch = (float)(Math.Atan2(dy, horizontal) * 180d / Math.PI);
        pose.Name = "Replay entity look-at";
        pose.Id = Guid.Empty;
        if (!controller.TrySetReplayPose(pose, out string error))
        {
            message = Lang.Get("vsdirector:camera-error", error);
            Show(message);
            return false;
        }

        replayViewportCameraAuthority = DirectorReplayCameraAuthority.ManualFree;
        replayCameraState = DirectorReplayCameraState.Free;
        anchorClient?.Update(controller.CurrentPose, active: true);
        message = Lang.Get("vsdirector:replay-entity-look-at-active");
        Show(message);
        return true;
    }

    internal IReadOnlyList<DirectorReplayActorPathSource> ActionActorSources
        => replay?.LiveActionActorSources ?? Array.Empty<DirectorReplayActorPathSource>();

    internal bool IsLiveActionRecording => replay?.IsLiveActionRecording == true;

    internal double LiveActionRecordingSeconds => replay?.LiveActionRecordingSeconds ?? 0d;

    internal IReadOnlyList<string> PresetFiles
        => presetStore?.List() ?? Array.Empty<string>();

    internal string PresetDirectory
        => presetStore?.DirectoryPath ?? string.Empty;

    internal bool IsReplayRecording => replay?.IsRecording == true;

    internal bool IsReplayPlaying => replay?.IsPlaying == true;

    internal bool IsReplayPaused => replay?.IsPaused == true || replay?.IsRecordingPaused == true;

    internal bool HasReplayReachedEnd => replay?.HasReachedEnd == true;

    internal bool IsReplayRecordingPaused => replay?.IsRecordingPaused == true;

    internal bool IsReplaySaving => replay?.IsSaving == true;

    internal bool HasPendingReplaySave => replay?.HasPendingSave == true;

    internal bool HasFailedReplaySave => replay?.HasFailedSave == true;

    internal IReadOnlyList<DirectorReplayMarker> ReplayMarkers
        => replay?.Markers ?? Array.Empty<DirectorReplayMarker>();

    internal IReadOnlyList<DirectorReplaySpeedKeyframe> ReplaySpeedKeyframes
        => replay?.SpeedKeyframes ?? Array.Empty<DirectorReplaySpeedKeyframe>();

    internal IReadOnlyList<DirectorReplayElementKeyframe> ReplayElementKeyframes
        => replay?.ElementKeyframes ?? Array.Empty<DirectorReplayElementKeyframe>();

    internal IReadOnlyList<DirectorReplayViewportClip> ReplayViewportClips
        => replay?.ViewportClips ?? Array.Empty<DirectorReplayViewportClip>();

    internal DirectorReplayViewportTimeline ReplayViewportTimeline
        => replay?.ViewportTimeline ?? new DirectorReplayViewportTimeline();
    internal IReadOnlyList<DirectorReplayVoiceFrame>? OpenTimelineVoiceView() => replay?.OpenTimelineVoiceView();
    internal object? TimelineDocumentIdentity => replay?.TimelineDocumentIdentity;

    internal bool IsReplayExporting => replay?.IsExporting == true;
    private DirectorWorkspaceHud? replayExportWorkspace;

    private void SuspendWorkspaceForReplayExport()
    {
        if (workspace?.IsOpened() != true) return;
        replayExportWorkspace = workspace;
        workspace.TryClose();
    }

    private void RestoreWorkspaceAfterReplayExport()
    {
        if (DirectorOffscreenRenderRuntime.IsRendering || IsReplayExporting || replayExportWorkspace is not { } previous) return;
        replayExportWorkspace = null;
        if (ReferenceEquals(previous, workspace) && !IsShuttingDown && IsWorldReady && HasWorkspacePermission && !previous.IsOpened())
            previous.ToggleOpen();
    }

    internal bool IsReplayLoading => replay?.IsLoading == true;

    internal bool IsReplayLoadOverlayVisible => replay?.IsReplayLoadOverlayVisible == true;

    internal bool IsReplayQuickMode => replay?.IsQuickMode == true;

    internal bool IsReplayActorAttached => replayAttachedActorView is not null;

    internal bool IsReplayFreeCamera => replay?.IsPlaying == true
        && replayCameraState.Mode == DirectorReplayCameraMode.Free
        && controller?.IsFreeCamera == true;

    internal DirectorReplayCameraMode ReplayActiveCameraMode
        => replay?.IsPlaying == true && controller?.IsActive == true
            ? replayCameraState.Mode
            : DirectorReplayCameraMode.None;

    internal string ReplayActiveCameraStatusText()
    {
        DirectorReplayCameraMode mode = ReplayActiveCameraMode;
        string modeText = mode switch
        {
            DirectorReplayCameraMode.Free => Lang.Get("vsdirector:replay-view-free"),
            DirectorReplayCameraMode.Fixed => Lang.Get("vsdirector:replay-view-camera"),
            DirectorReplayCameraMode.Path => Lang.Get("vsdirector:replay-view-path"),
            DirectorReplayCameraMode.SyncedPath => Lang.Get("vsdirector:replay-view-path"),
            DirectorReplayCameraMode.Sequence => Lang.Get("vsdirector:replay-view-sequence"),
            DirectorReplayCameraMode.Follow => Lang.Get("vsdirector:replay-view-follow"),
            _ => Lang.Get("vsdirector:inactive")
        };
        if (mode == DirectorReplayCameraMode.Free)
        {
            return Lang.Get("vsdirector:replay-active-view-status", modeText);
        }

        string name = controller?.ActiveCameraName ?? string.Empty;
        if (replayCameraState.SourceId != Guid.Empty)
        {
            name = mode switch
            {
                DirectorReplayCameraMode.Fixed => library?.Find(replayCameraState.SourceId)?.Name ?? name,
                DirectorReplayCameraMode.Path or DirectorReplayCameraMode.SyncedPath
                    => library?.FindPath(replayCameraState.SourceId)?.Name ?? name,
                DirectorReplayCameraMode.Sequence => library?.FindSequence(replayCameraState.SourceId)?.Name ?? name,
                DirectorReplayCameraMode.Follow => library?.FindFollowRig(replayCameraState.SourceId)?.Name ?? name,
                _ => name
            };
        }

        string detail = name.Length == 0 ? modeText : modeText + ": " + CompactStatusPart(name, 28);
        return Lang.Get("vsdirector:replay-active-view-status", detail);
    }

    internal string ReplayAttachedActorKey => replayAttachedActorKey;

    internal bool IsReplayIndexing => replay?.IsIndexing == true;

    internal double ReplayIndexProgress => replay?.IndexProgress ?? 1d;

    internal double ReplayLoadingProgress => replay?.LoadingProgress ?? 0d;

    internal string LoadingPhase => replay?.LoadingPhase ?? "ready";

    internal string PlaybackLoadError
        => replay?.PlaybackLoadError is "replay-world-suspend-failed"
            ? Lang.Get("vsdirector:replay-world-suspend-failed")
            : replay?.PlaybackLoadError ?? string.Empty;

    internal void RequestReplayPreloadEditor(Guid replayId)
    {
        if (HasPermission(DirectorPermission.RemoteChunkAccess))
        {
            replay?.RequestPreloadEditor(replayId);
        }
    }

    internal bool IsReplayPreloadEditorLoading(Guid replayId)
        => HasPermission(DirectorPermission.RemoteChunkAccess)
            && replay?.IsPreloadEditorLoading(replayId) == true;

    internal bool TryGetReplayPreloadEditorState(
        Guid replayId,
        double timeSeconds,
        out DirectorReplayPreloadEditorState state)
    {
        if (HasPermission(DirectorPermission.RemoteChunkAccess) && replay is not null)
        {
            return replay.TryGetPreloadEditorState(replayId, timeSeconds, out state);
        }

        state = default;
        return false;
    }

    internal bool TrySaveReplayPreloadOverrides(
        Guid replayId,
        IEnumerable<DirectorReplayPreloadColumn> includedColumns,
        IEnumerable<DirectorReplayPreloadColumn> excludedColumns,
        out string error)
    {
        if (!EnsurePermission(DirectorPermission.RemoteChunkAccess, out error))
        {
            return false;
        }
        if (replay is null)
        {
            error = "world-not-ready";
            return false;
        }

        return replay.SavePreloadOverrides(replayId, includedColumns, excludedColumns, out error);
    }

    internal Guid ActiveReplayId => replay?.ActiveReplayId ?? Guid.Empty;

    internal string? ActiveReplayName => replay?.ActiveReplayName;

    internal double ReplayTimeSeconds => replay?.PlaybackTimeSeconds ?? 0d;

    internal double ReplayDurationSeconds => replay?.PlaybackDurationSeconds ?? 0d;

    internal double ReplayPlaybackSpeed => replay?.PlaybackSpeed ?? 1d;

    internal double ReplayBasePlaybackSpeed => replay?.BasePlaybackSpeed ?? 1d;

    internal double ReplayAdjustedDurationSeconds => DirectorReplayTimeline.AdjustedDurationSeconds(
        ReplayDurationSeconds,
        ReplaySpeedKeyframes,
        ReplayBasePlaybackSpeed);

    internal double ReplayAdjustedElapsedSeconds(double replayTimeSeconds)
        => DirectorReplayTimeline.AdjustedElapsedSeconds(
            replayTimeSeconds,
            ReplayDurationSeconds,
            ReplaySpeedKeyframes,
            ReplayBasePlaybackSpeed);

    internal double ActivePathTimeSeconds => controller?.ActivePathTimeSeconds ?? 0d;

    internal bool IsPathPlaying => controller?.IsPlayingPath == true;

    internal double ActiveFollowOrbitAngleDegrees => controller?.ActiveFollowOrbitAngleDegrees ?? 0d;

    internal double ActiveFollowElevationDegrees => controller?.ActiveFollowElevationDegrees ?? 0d;

    internal DirectorConfig Config => config;
    internal string ReplayDirectory
    {
        get
        {
            // Keep settings accessible when a manually edited path is invalid.
            try { return capi is null ? config.ReplayOutputDirectory : DirectorReplayStore.ResolveRoot(capi, config.ReplayOutputDirectory); }
            catch (ArgumentException) { return config.ReplayOutputDirectory; }
        }
    }

    /// <summary>Client-side bridge for decoded PCM submitted by voice mods.</summary>
    public DirectorVoiceApi VoiceApi => voiceApi;

    /// <summary>Whether replay recording currently accepts voice frames.</summary>
    public bool IsReplayVoiceCaptureActive
        => voiceApi.IsCaptureEnabled
            && replay?.IsVoiceRecording == true;

    /// <summary>Gets the active live offscreen recording camera used for proximity-audio routing.</summary>
    public bool TryGetActiveVoiceListener(out DirectorVoicePosition position)
    {
        if (voiceApi.IsCaptureEnabled
            && liveOffscreenState == LiveOffscreenRecordingState.Recording
            && liveOffscreenSession?.CurrentPose is DirectorOffscreenCameraPose pose)
        {
            position = new DirectorVoicePosition(pose.X, pose.Y, pose.Z, pose.Dimension);
            return true;
        }

        if (IsReplayVoiceCaptureActive
            && controller?.CurrentPose is CameraPreset replayPose)
        {
            position = new DirectorVoicePosition(replayPose.X, replayPose.Y, replayPose.Z, replayPose.Dimension);
            return true;
        }

        // A replay without an active director camera still needs a meaningful
        // proximity listener. Use the recording player's pose so voice remains
        // spatialized and preserves the source mod's attenuation parameters.
        if (IsReplayVoiceCaptureActive
            && capi?.World.Player?.Entity is EntityPlayer player)
        {
            position = new DirectorVoicePosition(
                player.Pos.X,
                player.Pos.Y,
                player.Pos.Z,
                player.Pos.Dimension);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>
    /// Gets the fixed world region captured by the active replay. Voice
    /// integrations use this to request every speaker in the replay snapshot,
    /// instead of applying the normal proximity-chat radius.
    /// </summary>
    public bool TryGetReplayVoiceCaptureRegion(
        out DirectorVoicePosition center,
        out int radiusChunks)
    {
        if (IsReplayVoiceCaptureActive
            && replay?.TryGetVoiceCaptureRegion(out center, out radiusChunks) == true)
        {
            return true;
        }

        center = default;
        radiusChunks = 0;
        return false;
    }

    internal Guid? AssignedRecordingTrackId => assignedRecordingTrackId;

    internal bool IsRecordingActive => recording?.IsActive == true;

    internal bool HasEscapeMenuRecording
        => IsReplayRecording
            || IsRecordingActive
            || IsLiveActionRecording
            || OffscreenCaptureBusy
            || liveOffscreenState != LiveOffscreenRecordingState.Idle;

    internal bool HasEscapeMenuPrimaryRecording
        => IsReplayRecording
            || IsRecordingActive
            || IsLiveActionRecording
            || OffscreenCaptureBusy;

    internal bool CanStartEscapeMenuReplayRecording
        => HasPermission(DirectorPermission.ReplayCapture)
            && IsWorldReady
            && !HasEscapeMenuRecording
            && replay is
            {
                IsPlaying: false,
                IsLoading: false,
                IsExporting: false,
                IsSaving: false,
                HasPendingSave: false
            };

    internal bool IsOffscreenCaptureActive => OffscreenCaptureBusy;

    internal bool IsLiveOffscreenBusy => liveOffscreenState != LiveOffscreenRecordingState.Idle;

    internal bool IsLiveOffscreenRecording
        => liveOffscreenState == LiveOffscreenRecordingState.Recording;

    internal bool CanStartLiveOffscreen => liveOffscreenState == LiveOffscreenRecordingState.Idle;

    internal bool CanStartEscapeMenuLiveOffscreen
        => HasPermission(DirectorPermission.OffscreenCurrentView)
            && IsWorldReady
            && liveOffscreenState == LiveOffscreenRecordingState.Idle
            && !OffscreenCaptureBusy
            && !IsRecordingActive
            && !IsLiveActionRecording
            && !IsReplayPlaying
            && !IsReplayRecording
            && !IsReplayLoading
            && !IsReplayExporting;

    internal bool CanStopLiveOffscreen => liveOffscreenState is
        LiveOffscreenRecordingState.LoadingTarget
        or LiveOffscreenRecordingState.Starting
        or LiveOffscreenRecordingState.Recording
        or LiveOffscreenRecordingState.Stopping;

    internal string LiveOffscreenStatusText()
    {
        if (liveOffscreenStatus.Length > 0)
        {
            if (liveOffscreenState == LiveOffscreenRecordingState.Recording
                && liveOffscreenSession is DirectorLiveOffscreenSession session
                && session.AudioEnabled)
            {
                DirectorOpenAlDiagnostics diagnostics = session.AudioDiagnostics;
                return liveOffscreenStatus + "\n" + Lang.Get(
                    "vsdirector:live-offscreen-audio-status",
                    diagnostics.MirroredSources,
                    diagnostics.PlayingPositionalSources,
                    diagnostics.LeftPeak * 100f,
                    diagnostics.RightPeak * 100f,
                    diagnostics.ListenerX,
                    diagnostics.ListenerY,
                    diagnostics.ListenerZ);
            }
            if (liveOffscreenState == LiveOffscreenRecordingState.Recording
                && liveOffscreenSession is DirectorLiveOffscreenSession fileSession
                && liveOffscreenActiveBackend != DirectorLiveOffscreenBackend.Obs)
            {
                DirectorOffscreenFfmpegStatistics statistics = fileSession.OutputStatistics;
                return liveOffscreenStatus + "\n" + Lang.Get(
                    "vsdirector:live-offscreen-file-status",
                    statistics.EncodedFrames,
                    statistics.DroppedFrames,
                    statistics.GpuBackpressureEvents,
                    statistics.WriterBackpressureEvents);
            }
            return liveOffscreenStatus;
        }
        if (liveOffscreenOutputPath.Length > 0)
        {
            return Lang.Get("vsdirector:live-offscreen-last-output", liveOffscreenOutputPath);
        }
        return Lang.Get("vsdirector:live-offscreen-idle");
    }

    internal int OffscreenViewCount => offscreenRenderer?.ActiveViewCount ?? 0;

    internal bool RecordReplayWithVideo => config.RecordReplayWithVideo;

    internal bool IsWorldReady => library != null && controller != null;

    internal string ExitHotkeyLabel => GetHotkeyLabel(ExitHotkey, "F10");

    internal string RecordingHotkeyLabel
        => string.Empty;

    internal string ReplayRecordingStartHotkeyLabel
        => GetHotkeyLabel(ReplayRecordingStartHotkey, "Ctrl+R");

    internal string ReplayRecordingStopHotkeyLabel
        => GetHotkeyLabel(ReplayRecordingStopHotkey, "Alt+S");

    internal string QuickSlotHotkeyLabel(int slot)
    {
        if (!QuickSlots.IsValid(slot))
        {
            return string.Empty;
        }

        string fallback = "Alt+" + QuickSlots.KeyboardDigit(slot).ToString(CultureInfo.InvariantCulture);
        if (capi is null)
        {
            return fallback;
        }

        try
        {
            return capi.Input.GetHotKeyByCode(QuickSlotHotkeyPrefix + slot)?.CurrentMapping?.ToString()
                ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    internal (double X, double Y, double Z) DisplayPosition(double x, double y, double z)
    {
        int mapSizeX = capi?.World.BlockAccessor.MapSizeX ?? 0;
        int mapSizeZ = capi?.World.BlockAccessor.MapSizeZ ?? 0;
        return DirectorCoordinateSpace.ToDisplay(x, y, z, mapSizeX, mapSizeZ);
    }

    internal (double X, double Y, double Z) AbsolutePosition(double x, double y, double z)
    {
        int mapSizeX = capi?.World.BlockAccessor.MapSizeX ?? 0;
        int mapSizeZ = capi?.World.BlockAccessor.MapSizeZ ?? 0;
        return DirectorCoordinateSpace.ToAbsolute(x, y, z, mapSizeX, mapSizeZ);
    }

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return true;
    }

    public override void Start(ICoreAPI api)
    {
        DirectorNetwork.Register(api);
        base.Start(api);
    }

    // VSImGui creates its renderer in StartPre at order 0.
    public override double ExecuteOrder() => -0.1;

    public override void StartPre(ICoreAPI api)
    {
        if (api is ICoreClientAPI client) DirectorImGuiRuntime.Initialize(client, Mod);
        base.StartPre(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        string ffmpegDirectory = Path.Combine(
            api.GetOrCreateDataPath("ModData"),
            "VSDirector",
            "Tools",
            "FFmpeg");
        DirectorFfmpegLocator.ConfigureManagedDirectory(ffmpegDirectory);
        try
        {
            Directory.CreateDirectory(ffmpegDirectory);
        }
        catch (Exception exception)
        {
            api.Logger.Warning(
                "VS Director: could not create the managed FFmpeg directory '{0}': {1}",
                ffmpegDirectory,
                exception.Message);
        }
        config = LoadConfig(api);
        vsmc2FileWatcher = new DirectorVsmc2FileWatcher(OnVsmc2FileChanged);
        presetStore = new DirectorPresetStore(api);
        actionStore = new DirectorActionStore(api);
        modelStore = new DirectorModelStore(api);
        actionModIntegration = new DirectorActionModIntegration(api);
        actionEnvironment = new DirectorActionEnvironmentController(api);
        controller = new DirectorCameraController(api, config);
        controller.PathCompleted += OnPathCompleted;
        controller.SequenceCompleted += OnSequenceCompleted;
        controller.FollowTargetLost += OnFollowTargetLost;
        DirectorCameraRuntime.Controller = controller;
        DirectorEscapeMenuRuntime.Attach(this);
        replay = new DirectorReplayManager(api, config, controller, SetReplayInputLock, ApplyTimedRecordingTrack);
        voiceBridge.Configure(
            () => config,
            () => capi is not null && !disposed,
            ConsumeVoiceFrame,
            () => replay?.IsVoiceRecording == true);
        replay.ActionDecoratorsActive = HasActiveReplayActionDecoration;
        replay.RequiresFullActorSampling = RequiresFullReplayActorSampling;
        replay.RequiresActorPoseSampling = RequiresReplayCameraActorPoseSampling;
        replay.ActionFrameDecorator = UpdateActionWorkflowEnvironment;
        replay.ActionSampleDecorator = DecorateReplayActionSample;
        replay.ActionEntityDecorator = DecorateReplayActionEntity;
        replay.PlaybackStateChanged += OnReplayPlaybackStateChanged;
        replay.RecordingSaveCompleted += OnReplayRecordingSaveCompleted;
        DirectorReplayRuntime.Manager = replay;
        DirectorReplayRuntime.UpdateCameraForRenderFrame = PrepareReplayWorkspaceForRenderFrame;
        liveWorkflowRunner = new DirectorLiveWorkflowRunner(ApplyTimedRecordingTrack);
        DirectorLiveWorkflowRuntime.Runner = liveWorkflowRunner;
        permissionClient = new DirectorPermissionClient(api, OnPermissionSnapshot);
        anchorClient = new DirectorAnchorClient(api, OnAnchorStatus);
        DirectorBlockSoundRelay.Attach(api);
        chunkPreloadClient = new DirectorChunkPreloadClient(api);
        sharedCameraClient = new DirectorSharedCameraClient(api, OnSharedCameraError);
        sharedCameraClient.CatalogChanged += OnSharedCameraCatalogChanged;
        pathPreview = new DirectorPathPreviewRenderer(
            api,
            () => Cameras,
            () => Paths,
            ReplayCameraElementPreviewPaths,
            ReplayOrbitElementPreviewGuides,
            () => workspace?.IsOpened() == true,
            () => HasEscapeMenuRecording);
        greenScreenSkyRenderer = new DirectorGreenScreenSkyRenderer(api);
        DirectorGreenScreenRuntime.Attach(config, greenScreenSkyRenderer);
        greenScreenPreviewRenderer = new DirectorGreenScreenPreviewRenderer(api);
        modelOutlineRenderer = new DirectorModelOutlineRenderer(api, config);
        replayCompositionOverlay = new DirectorReplayCompositionOverlay(api);

        harmony = new Harmony(HarmonyId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        ReplayPlayerInventoryPatch.TryPatchPlayerBehaviorInventory(harmony, api.Logger);
        ReplayPlayerShapeRendererPatch.TryPatch(harmony, api.Logger);
        ReplayEntityTessellationRevisionPatch.TryPatch(harmony, api.Logger);
        ReplayEntityShapeRendererPatch.TryPatch(harmony, api.Logger);
        ReplayPositionInterpolationIsolationPatch.TryPatch(harmony, api.Logger);
        ReplayItemRendererVisibilityPatch.TryPatch(harmony, api.Logger);
        ReplayHideWaterSurfaceVisibilityPatch.TryPatch(harmony, api.Logger);
        ReplayFallingBlockIsolationPatch.TryPatch(harmony, api.Logger);
        ReplayRemoteParticleViewPatch.TryPatch(harmony, api.Logger);
        DirectorReplayWeatherVisualPatch.TryPatch(harmony, api.Logger);
        ReplayChunkTessellationRevisionPatch.TryPatch(harmony, api.Logger);
        ReplayRemoteChunkPriorityPatch.TryPatch(harmony, api.Logger);
        ReplayBlockEntityStateCapturePatch.TryPatch(harmony, api.Logger);
        DirectorGreenScreenRenderPatch.TryPatch(harmony, api.Logger);
        DirectorReplayWorldRenderPatch.TryPatch(harmony, api.Logger);
        DirectorReplayVisualEffectsPatch.TryPatch(harmony, api.Logger);

        RegisterHotkeys(api);
        DirectorCommands.Register(api, this);
        api.Event.LevelFinalize += OnLevelFinalize;
        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.LeftWorld += OnLeftWorld;
        tickListenerId = api.Event.RegisterGameTickListener(OnClientTick, 25);

        if (api.PlayerReadyFired)
        {
            OnLevelFinalize();
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverHarmony = new Harmony(ServerHarmonyId);
        DirectorAnchorChunkUnloadPatch.TryPatch(serverHarmony, api.Logger);
        DirectorAnchorEntityTrackingPatch.TryPatch(serverHarmony, api.Logger);
        DirectorAnchorSoundBroadcastPatch.TryPatch(serverHarmony, api.Logger);
        permissionServer = new DirectorPermissionServer(api);
        anchorServer = new DirectorAnchorServer(api, permissionServer);
        sharedCameraServer = new DirectorSharedCameraServer(api, permissionServer);
    }

    public override void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DirectorImGuiRuntime.Dispose();
        if (capi != null)
        {
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LeaveWorld -= OnLeaveWorld;
            capi.Event.LeftWorld -= OnLeftWorld;
        }

        OnLeaveWorld();

        if (capi != null && tickListenerId >= 0)
        {
            capi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = -1;
        }
        workspace?.Dispose();
        workspace = null;
        vsmc2FileWatcher?.Dispose();
        vsmc2FileWatcher = null;
        pendingVsmc2Shapes.Clear();
        vsmc2ShapeStatuses.Clear();
        settingsDialog?.Dispose();
        settingsDialog = null;
        pathPreview?.Dispose();
        pathPreview = null;
        modelOutlineRenderer?.Dispose();
        modelOutlineRenderer = null;
        greenScreenPreviewRenderer?.Dispose();
        greenScreenPreviewRenderer = null;
        DirectorGreenScreenRuntime.Detach(config, greenScreenSkyRenderer);
        greenScreenSkyRenderer?.Dispose();
        greenScreenSkyRenderer = null;
        replayCompositionOverlay?.Dispose();
        replayCompositionOverlay = null;
        anchorClient?.Dispose();
        anchorClient = null;
        chunkPreloadClient?.Dispose();
        chunkPreloadClient = null;
        if (sharedCameraClient is not null)
        {
            sharedCameraClient.CatalogChanged -= OnSharedCameraCatalogChanged;
        }
        sharedCameraClient = null;
        ForceStopLiveOffscreenForShutdown();
        ForceStopOffscreenCaptureForShutdown();
        offscreenRenderer?.Dispose();
        offscreenRenderer = null;
        liveWorkflowRunner?.Reset();
        liveWorkflowRunner = null;
        DirectorLiveWorkflowRuntime.Runner = null;
        if (replay is not null)
        {
            replay.PlaybackStateChanged -= OnReplayPlaybackStateChanged;
            replay.RecordingSaveCompleted -= OnReplayRecordingSaveCompleted;
        }
        replay?.Dispose();
        replay = null;
        DirectorReplayRuntime.Manager = null;
        DirectorReplayRuntime.UpdateCameraForRenderFrame = null;
        sharedCameraServer = null;
        anchorServer?.Dispose();
        anchorServer = null;
        permissionServer?.Dispose();
        permissionServer = null;
        if (serverHarmony is not null)
        {
            try
            {
                serverHarmony.UnpatchAll(ServerHarmonyId);
            }
            catch (Exception ex)
            {
                sapi?.Logger.Warning("VS Director: failed to unpatch remote chunk hooks: {0}", ex.Message);
            }

            serverHarmony = null;
        }
        if (controller != null)
        {
            controller.PathCompleted -= OnPathCompleted;
            controller.SequenceCompleted -= OnSequenceCompleted;
            controller.FollowTargetLost -= OnFollowTargetLost;
        }
        controller?.Dispose();
        controller = null;
        DirectorCameraRuntime.Controller = null;
        DirectorCameraRuntime.PendingRemoteCameraPose = null;
        DirectorCameraRuntime.PendingRemoteTargetPose = null;
        DirectorEscapeMenuRuntime.Detach(this);
        DirectorBlockSoundRelay.Detach();
        DirectorReplayWeatherVisualPatch.ClearOverrides();

        if (harmony != null)
        {
            try
            {
                harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                capi?.Logger.Warning("VS Director: failed to unpatch camera hooks: {0}", ex.Message);
            }

            harmony = null;
        }

        capi = null;
        sapi = null;
        permissionClient = null;
        library = null;
        store = null;
        presetStore = null;
        actionStore = null;
        modelStore = null;
        base.Dispose();
    }

    internal void TogglePanel()
    {
        if (!HasWorkspacePermission)
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ToggleOpen();
    }

    internal void OpenWorkspaceFromEscapeMenu()
    {
        if (!HasWorkspacePermission)
        {
            ShowPermissionDenied();
            return;
        }
        if (TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace)
            && !readyWorkspace.IsOpened())
        {
            readyWorkspace.ToggleOpen();
        }
    }

    internal bool TryToggleWorkspaceInteractionMode()
    {
        if (workspace?.ToggleInteractionMode() == true)
        {
            return true;
        }

        TogglePanel();
        return true;
    }

    internal void ToggleSettings()
    {
        if (capi is null)
        {
            return;
        }

        settingsDialog ??= new DirectorSettingsDialog(capi, this,
            workspace is { } activeWorkspace && activeWorkspace.IsOpened()
                ? activeWorkspace.ImGuiWorkspace
                : null);
        settingsDialog.ToggleOpen();
    }
    internal void OpenReplayStorageSettings()
    {
        if (capi is null) return;
        settingsDialog ??= new DirectorSettingsDialog(capi, this,
            workspace is { } activeWorkspace && activeWorkspace.IsOpened()
                ? activeWorkspace.ImGuiWorkspace
                : null);
        settingsDialog.OpenReplayStorage();
    }

    internal void OpenSettingsFromEscapeMenu()
    {
        if (capi is null)
        {
            return;
        }

        settingsDialog ??= new DirectorSettingsDialog(capi, this,
            workspace is { } activeWorkspace && activeWorkspace.IsOpened()
                ? activeWorkspace.ImGuiWorkspace
                : null);
        if (!settingsDialog.IsOpened())
        {
            settingsDialog.ToggleOpen();
        }
    }

    internal void TogglePathPanel()
    {
        if (!HasPermission(DirectorPermission.PathLibrary))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.Paths);
    }

    internal void OpenPathEditor(Guid pathId)
    {
        if (!HasPermission(DirectorPermission.PathLibrary))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }
        readyWorkspace.ShowPath(pathId);
    }

    internal void ToggleSequencePanel()
    {
        if (!HasPermission(DirectorPermission.CameraSequences))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.SequenceEditor);
    }

    internal void ToggleWorkflowPanel()
    {
        if (!HasPermission(DirectorPermission.Workflows))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.Workflows);
    }

    internal void ToggleFollowPanel()
    {
        if (!HasPermission(DirectorPermission.FollowCameras))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.Follows);
    }

    internal void ToggleReplayPanel()
    {
        if (!HasPermission(DirectorPermission.ReplayCapture)
            && !HasPermission(DirectorPermission.ReplayPlayback))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.Replay);
    }

    internal void OpenReplayPanelFromEscapeMenu()
    {
        if (!HasPermission(DirectorPermission.ReplayCapture)
            && !HasPermission(DirectorPermission.ReplayPlayback))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }
        if (!readyWorkspace.IsOpened()
            || !readyWorkspace.IsShowing(DirectorWorkspaceSection.Replay))
        {
            readyWorkspace.ShowSection(DirectorWorkspaceSection.Replay);
        }
    }

    internal void TogglePresetPanel()
    {
        if (!HasPermission(DirectorPermission.PresetPackages))
        {
            ShowPermissionDenied();
            return;
        }
        if (!TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace))
        {
            return;
        }

        readyWorkspace.ShowSection(DirectorWorkspaceSection.Presets);
    }

    internal bool TryPreviewWorkflow(Guid workflowId, out string message)
    {
        if (!EnsurePermission(DirectorPermission.Workflows, out message))
        {
            Show(message);
            return false;
        }
        DirectorWorkflow? workflow = library?.FindWorkflow(workflowId);
        if (workflow is null)
        {
            message = Lang.Get("vsdirector:workflow-not-found", workflowId);
            Show(message);
            return false;
        }
        if (!StartLiveWorkflow(workflow.Id, out string error))
        {
            message = LiveWorkflowErrorText(error, workflow.Name);
            Show(message);
            return false;
        }
        message = Lang.Get("vsdirector:workflow-preview-started", workflow.Name);
        Show(message);
        return true;
    }

    private bool TryEnsureWorkspace(out DirectorWorkspaceHud readyWorkspace)
    {
        readyWorkspace = null!;
        if (!HasWorkspacePermission)
        {
            ShowPermissionDenied();
            return false;
        }
        if (!IsWorldReady
            || capi?.World.Player == null
            || string.IsNullOrWhiteSpace(capi.World.SavegameIdentifier))
        {
            Show(Lang.Get("vsdirector:world-not-ready"));
            return false;
        }

        if (workspace is not null)
        {
            readyWorkspace = workspace;
            return true;
        }

        try
        {
            workspace = new DirectorWorkspaceHud(capi, this, capi.World.SavegameIdentifier);
            readyWorkspace = workspace;
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: failed to compose director workspace: {0}", exception);
            workspace?.Dispose();
            workspace = null;
            Show(Lang.Get("vsdirector:workspace-load-failed"));
            return false;
        }
    }

    internal void SetCameraGuiOverride(bool enabled)
    {
        controller?.SetGuiOverride(enabled);
    }

    internal void SetWorkspaceInputOverride(bool enabled)
        => controller?.SetWorkspaceInputOverride(enabled);

    internal void SetReplayViewportFreeCameraPointerActive(bool active)
        => controller?.SetGuiPointerCameraInput(active);

    internal bool TryAddCamera(string requestedName, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:camera-name-required");
            Show(message);
            return false;
        }

        if (library!.Find(name) != null)
        {
            message = Lang.Get("vsdirector:camera-name-exists", name);
            Show(message);
            return false;
        }

        if (!controller!.TryCaptureCurrentView(name, out CameraPreset preset))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.Add(preset);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:camera-added", name);
        Show(message);
        return true;
    }

    internal bool TryRecaptureCamera(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        CameraPreset? existing = library!.Find(id);
        if (existing == null)
        {
            message = Lang.Get("vsdirector:camera-not-found", id.ToString());
            Show(message);
            return false;
        }

        if (!controller!.TryCaptureCurrentView(existing.Name, out CameraPreset pose))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.UpdatePose(id, pose);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:camera-updated", existing.Name);
        Show(message);
        return true;
    }

    internal bool TrySetCameraPreviewVisible(Guid id, bool visible, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        CameraPreset? existing = library!.Find(id);
        if (existing is null)
        {
            message = Lang.Get("vsdirector:camera-not-found", id);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        if (!library.SetCameraPreviewVisible(id, visible)
            || !PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get(visible
            ? "vsdirector:camera-model-shown"
            : "vsdirector:camera-model-hidden", existing.Name);
        Show(message);
        return true;
    }

    internal bool TrySetPathPreviewVisible(Guid id, bool visible, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        CameraPath? existing = library!.FindPath(id);
        if (existing is null)
        {
            message = Lang.Get("vsdirector:path-not-found", id);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        if (!library.SetPathPreviewVisible(id, visible)
            || !PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get(visible
            ? "vsdirector:path-preview-shown"
            : "vsdirector:path-preview-hidden", existing.Name);
        Show(message);
        return true;
    }

    internal bool TryDeleteCamera(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        CameraPreset? existing = library!.Find(id);
        if (existing == null)
        {
            message = Lang.Get("vsdirector:camera-not-found", id.ToString());
            return false;
        }

        Guid? activeSequenceId = controller?.ActiveSequenceId;
        bool affectsActiveSequence = activeSequenceId is Guid sequenceId
            && library.FindSequence(sequenceId)?.Shots.Any(shot => shot.CameraId == id) == true;
        CameraLibraryDocument before = library.Document;
        library.Remove(id);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        if (controller!.ActiveCameraId == id || affectsActiveSequence)
        {
            controller?.Deactivate();
            replayCameraState = DirectorReplayCameraState.Inactive;
            anchorClient?.StopAnchor();
        }

        message = Lang.Get("vsdirector:camera-deleted", existing.Name);
        Show(message);
        return true;
    }

    internal bool TryActivateCamera(Guid id, bool showNotification = true)
    {
        if (!EnsurePermission(DirectorPermission.CameraLibrary, out string permissionError))
        {
            Show(permissionError);
            return false;
        }
        if (!EnsureReady(out string error))
        {
            Show(error);
            return false;
        }

        if (!EnsureDirectorAccess(out error))
        {
            Show(error);
            return false;
        }

        CameraPreset? camera = library!.Find(id);
        if (camera == null)
        {
            Show(Lang.Get("vsdirector:camera-not-found", id.ToString()));
            return false;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller!.TryActivate(camera, out error))
        {
            Show(Lang.Get("vsdirector:camera-error", error));
            return false;
        }

        SetReplayCameraState(DirectorReplayCameraMode.Fixed, camera.Id);

        if (showNotification && config.ShowSwitchNotifications)
        {
            Show(Lang.Get("vsdirector:camera-active", camera.Name, ExitHotkeyLabel));
        }

        return true;
    }

    internal bool TryActivateSlot(int slot)
    {
        CameraPreset? camera = Cameras.FirstOrDefault(candidate => candidate.QuickSlot == slot);
        if (camera != null)
        {
            return TryActivateCamera(camera.Id);
        }

        CameraFollowRig? follow = FollowRigs.FirstOrDefault(candidate => candidate.QuickSlot == slot);
        if (follow is not null)
        {
            return TryActivateFollowRig(follow.Id);
        }

        Show(Lang.Get("vsdirector:slot-empty", slot));
        return false;
    }

    internal void ExitCamera()
    {
        bool remoteFollowPending = pendingRemoteFollow is not null;
        CancelPendingRemoteFollow(stopAnchor: true);
        replayCameraState = DirectorReplayCameraState.Inactive;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        ClearReplayActorAttachment();
        if (controller?.IsActive != true)
        {
            if (remoteFollowPending)
            {
                Show(Lang.Get("vsdirector:camera-exited"));
            }
            return;
        }

        if (recording?.ActiveTrack?.SourceType == DirectorRecordingSourceType.Workflow
            && liveWorkflowRunner?.IsPrepared == true)
        {
            liveWorkflowRunner.Hold();
            if (!recording.RequestStop(out _))
            {
                recording.FailActiveSource("workflow-camera-exited");
            }
        }

        controller?.Deactivate();
        anchorClient?.StopAnchor();
        Show(Lang.Get("vsdirector:camera-exited"));
    }

    internal CameraPreset? FindCamera(Guid id) => library?.Find(id);

    internal CameraPreset? FindSharedCamera(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        string trimmed = selector.Trim();
        IReadOnlyList<CameraPreset> cameras = SharedCameras;
        if (Guid.TryParse(trimmed, out Guid id))
        {
            return cameras.FirstOrDefault(camera => camera.Id == id)?.DeepClone();
        }
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return index >= 1 && index <= cameras.Count ? cameras[index - 1].DeepClone() : null;
        }
        return cameras.FirstOrDefault(camera => string.Equals(
            camera.Name,
            trimmed,
            StringComparison.OrdinalIgnoreCase))?.DeepClone();
    }

    internal string SharedCameraListText()
    {
        IReadOnlyList<CameraPreset> cameras = SharedCameras;
        if (cameras.Count == 0)
        {
            return Lang.Get("vsdirector:shared-camera-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:shared-camera-list-header", cameras.Count) };
        for (int index = 0; index < cameras.Count; index++)
        {
            CameraPreset camera = cameras[index];
            (double displayX, double displayY, double displayZ) = DisplayPosition(
                camera.X,
                camera.Y,
                camera.Z);
            lines.Add(Lang.Get(
                "vsdirector:shared-camera-list-entry",
                index + 1,
                camera.Name,
                camera.Dimension,
                displayX.ToString("0.##", CultureInfo.InvariantCulture),
                displayY.ToString("0.##", CultureInfo.InvariantCulture),
                displayZ.ToString("0.##", CultureInfo.InvariantCulture)));
        }
        return string.Join("\n", lines);
    }

    internal void RequestSharedCameraList()
    {
        if (!HasPermission(DirectorPermission.SharedCameras))
        {
            ShowPermissionDenied();
            return;
        }
        sharedCameraClient?.RequestList();
    }

    internal bool TryPublishSharedCamera(string selector, out string message)
    {
        if (!EnsurePermission(DirectorPermission.SharedCameras, out message))
        {
            Show(message);
            return false;
        }
        CameraPreset? camera = FindCamera(selector);
        if (camera is null)
        {
            message = Lang.Get("vsdirector:camera-not-found", selector);
            Show(message);
            return false;
        }
        if (sharedCameraClient is null)
        {
            message = Lang.Get("vsdirector:server-required");
            return false;
        }

        sharedCameraClient.Publish(camera);
        message = Lang.Get("vsdirector:shared-camera-publish-requested", camera.Name);
        Show(message);
        return true;
    }

    internal bool TryDeleteSharedCamera(string selector, out string message)
    {
        if (!EnsurePermission(DirectorPermission.SharedCameras, out message))
        {
            Show(message);
            return false;
        }
        CameraPreset? camera = FindSharedCamera(selector);
        if (camera is null)
        {
            message = Lang.Get("vsdirector:shared-camera-not-found", selector);
            Show(message);
            return false;
        }
        if (sharedCameraClient is null)
        {
            message = Lang.Get("vsdirector:server-required");
            return false;
        }

        sharedCameraClient.Delete(camera.Id);
        message = Lang.Get("vsdirector:shared-camera-delete-requested", camera.Name);
        Show(message);
        return true;
    }

    internal bool TryActivateSharedCamera(string selector, out string message)
    {
        if (!EnsurePermission(DirectorPermission.SharedCameras, out message))
        {
            Show(message);
            return false;
        }
        CameraPreset? camera = FindSharedCamera(selector);
        if (camera is null)
        {
            message = Lang.Get("vsdirector:shared-camera-not-found", selector);
            Show(message);
            return false;
        }
        if (!EnsureReady(out message) || !EnsureDirectorAccess(out message))
        {
            Show(message);
            return false;
        }
        if (!controller!.TryActivate(camera, out string error))
        {
            message = Lang.Get("vsdirector:camera-error", error);
            Show(message);
            return false;
        }

        message = Lang.Get("vsdirector:shared-camera-active", camera.Name, ExitHotkeyLabel);
        Show(message);
        return true;
    }

    internal bool TryImportSharedCamera(string selector, out string message)
    {
        if (!EnsurePermission(DirectorPermission.SharedCameras, out message)
            || !EnsurePermission(DirectorPermission.CameraLibrary, out message))
        {
            Show(message);
            return false;
        }
        CameraPreset? camera = FindSharedCamera(selector);
        if (camera is null)
        {
            message = Lang.Get("vsdirector:shared-camera-not-found", selector);
            Show(message);
            return false;
        }
        if (!EnsureEditAccess(out message))
        {
            return false;
        }

        CameraLibraryDocument before = library!.Document;
        camera.Id = Guid.Empty;
        camera.QuickSlot = null;
        CameraPreset added = library.Add(camera);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:shared-camera-imported", added.Name);
        Show(message);
        return true;
    }

    internal CameraPath? FindPath(Guid id) => library?.FindPath(id);

    internal CameraSequence? FindSequence(Guid id) => library?.FindSequence(id);

    internal DirectorWorkflow? FindWorkflow(Guid id) => library?.FindWorkflow(id);

    internal DirectorWorkflow? FindWorkflow(string selector)
    {
        if (library is null || string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        string value = selector.Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= Workflows.Count)
        {
            return Workflows[index - 1].DeepClone();
        }

        return Guid.TryParse(value, out Guid id)
            ? library.FindWorkflow(id)
            : library.FindWorkflow(value);
    }

    internal string WorkflowListText()
        => Workflows.Count == 0
            ? Lang.Get("vsdirector:workflow-list-empty")
            : Lang.Get("vsdirector:workflow-list-header", Workflows.Count)
                + "\n"
                + string.Join("\n", Workflows.Select((workflow, index) => Lang.Get(
                    "vsdirector:workflow-list-entry",
                    index + 1,
                    workflow.Name,
                    workflow.Cards.Count,
                    workflow.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    PlanWorkflow(workflow.Id).Count)));

    internal CameraFollowRig? FindFollowRig(Guid id) => library?.FindFollowRig(id);

    internal bool TryExportPresetPackage(string requestedName, out string message)
    {
        message = string.Empty;
        if (!EnsurePermission(DirectorPermission.PresetPackages, out message))
        {
            Show(message);
            return false;
        }
        if (!EnsureReady(out message) || presetStore is null)
        {
            return false;
        }

        try
        {
            string path = presetStore.Export(requestedName, library!.Document);
            message = Lang.Get("vsdirector:preset-exported", path);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning("VS Director: preset export failed: {0}", exception.Message);
            message = Lang.Get("vsdirector:preset-export-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TryImportPresetPackage(string selector, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PresetPackages, out message)
            || presetStore is null)
        {
            return false;
        }

        CameraLibraryDocument before = library!.Document;
        try
        {
            (DirectorPresetPackage package, string path) = presetStore.Import(selector);
            DirectorPresetImportResult result = DirectorPresetPackageIO.Merge(before, package);
            library = new CameraLibrary(result.Document);
            if (!PersistOrRollback(before, out message))
            {
                return false;
            }

            RefreshWorkspaceDialogs();
            message = Lang.Get(
                "vsdirector:preset-imported",
                result.Cameras,
                result.Paths,
                result.Sequences,
                result.FollowRigs,
                result.Workflows,
                path);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            library = new CameraLibrary(before);
            capi?.Logger.Warning("VS Director: preset import failed: {0}", exception.Message);
            message = Lang.Get("vsdirector:preset-import-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal string PresetListText()
    {
        IReadOnlyList<string> files = PresetFiles;
        return files.Count == 0
            ? Lang.Get("vsdirector:preset-list-empty", PresetDirectory)
            : Lang.Get("vsdirector:preset-list-header", files.Count, PresetDirectory)
                + "\n"
                + string.Join("\n", files.Select((file, index) => $"{index + 1}. {file}"));
    }

    internal void SetPathEditPreview(
        CameraPath? path,
        int selectedKeyframe = -1,
        double? previewTime = null,
        IReadOnlyCollection<int>? selectedKeyframes = null)
    {
        pathPreview?.SetTransientPath(path, selectedKeyframe, previewTime, selectedKeyframes);
        previewChunkPreloadId = path?.Id ?? Guid.Empty;
        previewChunkPreloadPlan = path is null ? null : DirectorReplayPreloadPlanner.Build(path);
    }

    internal bool TryPreviewPathPose(CameraPath path, double timeSeconds, out string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        message = string.Empty;
        if (controller is null || replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:world-not-ready");
            return false;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        double time = Math.Clamp(timeSeconds, 0d, Math.Max(0d, path.DurationSeconds));
        if (!controller.TryEvaluatePathPose(path, time, out CameraPreset pose))
        {
            message = Lang.Get("vsdirector:path-preview-no-samples");
            return false;
        }
        if (!controller.TrySetReplayPose(pose, out string error))
        {
            message = Lang.Get("vsdirector:camera-error", error);
            return false;
        }

        anchorClient?.Update(controller.CurrentPose, active: true);
        return true;
    }

    internal bool TryStartPathFreeCamera(CameraPath path, double timeSeconds, out string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        message = string.Empty;
        if (!EnsurePermission(DirectorPermission.FreeCamera, out message)
            || !EnsurePermission(DirectorPermission.PathLibrary, out message))
        {
            Show(message);
            return false;
        }
        if (controller is null || replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:world-not-ready");
            Show(message);
            return false;
        }
        if (path.Keyframes.Count > 0 && !TryPreviewPathPose(path, timeSeconds, out message))
        {
            Show(message);
            return false;
        }
        if (!controller.TryStartFreeCamera(out string error))
        {
            message = Lang.Get("vsdirector:camera-error", error);
            Show(message);
            return false;
        }

        anchorClient?.Update(controller.CurrentPose, active: true);
        message = Lang.Get("vsdirector:path-free-camera-enabled");
        Show(message);
        return true;
    }

    internal bool TryStartEscapeMenuFreeCamera(out string message)
    {
        message = string.Empty;
        if (!EnsurePermission(DirectorPermission.FreeCamera, out message))
        {
            Show(message);
            return false;
        }
        if (controller is null || replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:world-not-ready");
            Show(message);
            return false;
        }
        if (replay?.IsPlaying == true)
        {
            return EnterReplayFreeCamera(out message);
        }
        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller.TryStartFreeCamera(out string error))
        {
            message = Lang.Get("vsdirector:camera-error", error);
            Show(message);
            return false;
        }

        anchorClient?.Update(controller.CurrentPose, active: true);
        _ = workspace?.EnterReplayNavigationMode();
        message = Lang.Get("vsdirector:escape-free-camera-enabled");
        Show(message);
        return true;
    }

    internal CameraPreset? FindCamera(string selector)
    {
        if (library == null)
        {
            return null;
        }

        string value = selector.Trim();
        CameraPreset? named = library.Find(value);
        if (named != null)
        {
            return named;
        }

        if (Guid.TryParse(value, out Guid id))
        {
            return library.Find(id);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= Cameras.Count)
        {
            return Cameras[index - 1];
        }

        return null;
    }

    internal CameraPath? FindPath(string selector)
    {
        if (library == null)
        {
            return null;
        }

        string value = selector.Trim();
        CameraPath? named = library.FindPath(value);
        if (named != null)
        {
            return named;
        }

        if (Guid.TryParse(value, out Guid id))
        {
            return library.FindPath(id);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= Paths.Count)
        {
            return Paths[index - 1];
        }

        return null;
    }

    internal CameraSequence? FindSequence(string selector)
    {
        if (library == null)
        {
            return null;
        }

        string value = selector.Trim();
        CameraSequence? named = library.FindSequence(value);
        if (named != null)
        {
            return named;
        }

        if (Guid.TryParse(value, out Guid id))
        {
            return library.FindSequence(id);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= Sequences.Count)
        {
            return Sequences[index - 1];
        }

        return null;
    }

    internal CameraFollowRig? FindFollowRig(string selector)
    {
        if (library == null)
        {
            return null;
        }

        string value = selector.Trim();
        CameraFollowRig? named = library.FindFollowRig(value);
        if (named != null)
        {
            return named;
        }

        if (Guid.TryParse(value, out Guid id))
        {
            return library.FindFollowRig(id);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= FollowRigs.Count)
        {
            return FollowRigs[index - 1];
        }

        return null;
    }

    internal IReadOnlyList<DirectorFollowTarget> GetFollowTargets()
    {
        if (capi == null)
        {
            return Array.Empty<DirectorFollowTarget>();
        }

        List<DirectorFollowTarget> targets = new();
        HashSet<long> playerEntityIds = new();
        foreach (IPlayer player in capi.World.AllOnlinePlayers)
        {
            if (player is null || string.IsNullOrWhiteSpace(player.PlayerUID))
            {
                continue;
            }

            long entityId = player.Entity?.EntityId ?? 0;
            targets.Add(new DirectorFollowTarget(
                CameraFollowTargetType.Player,
                player.PlayerUID,
                entityId,
                string.IsNullOrWhiteSpace(player.PlayerName) ? player.PlayerUID : player.PlayerName));
            if (entityId > 0)
            {
                playerEntityIds.Add(entityId);
            }
        }

        foreach (Entity entity in capi.World.LoadedEntities.Values)
        {
            if (entity is null || entity.EntityId <= 0 || playerEntityIds.Contains(entity.EntityId))
            {
                continue;
            }

            string displayName;
            try
            {
                displayName = entity.GetName();
            }
            catch
            {
                displayName = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = entity.Code?.ToShortString() ?? "Entity";
            }

            targets.Add(new DirectorFollowTarget(
                CameraFollowTargetType.Entity,
                string.Empty,
                entity.EntityId,
                displayName));
        }

        return targets
            .OrderBy(target => target.TargetType)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.EntityId)
            .ToArray();
    }

    internal DirectorFollowTarget? FindFollowTarget(string selector)
    {
        string value = selector.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        IReadOnlyList<DirectorFollowTarget> targets = GetFollowTargets();
        string playerValue = value.StartsWith("player:", StringComparison.OrdinalIgnoreCase)
            ? value[7..]
            : value;
        DirectorFollowTarget? player = targets.FirstOrDefault(target =>
            target.TargetType == CameraFollowTargetType.Player
            && (string.Equals(target.PlayerUid, playerValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.DisplayName, playerValue, StringComparison.OrdinalIgnoreCase)));
        if (player is { PlayerUid.Length: > 0 })
        {
            return player;
        }

        string entityValue = value.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)
            ? value[7..]
            : value;
        if (long.TryParse(entityValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long entityId))
        {
            DirectorFollowTarget? entity = targets.FirstOrDefault(target =>
                target.TargetType == CameraFollowTargetType.Entity && target.EntityId == entityId);
            if (entity is { EntityId: > 0 })
            {
                return entity;
            }
        }

        return null;
    }

    internal bool TryAddFollowRig(string requestedName, DirectorFollowTarget target, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.FollowCameras, out message))
        {
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:follow-name-required");
            Show(message);
            return false;
        }

        if (library!.FindFollowRig(name) != null)
        {
            message = Lang.Get("vsdirector:follow-name-exists", name);
            Show(message);
            return false;
        }

        CameraFollowRig rig = new()
        {
            Name = name,
            TargetType = target.TargetType,
            TargetPlayerUid = target.PlayerUid,
            TargetEntityId = target.EntityId,
            TargetDisplayName = target.DisplayName,
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        CameraLibraryDocument before = library.Document;
        library.AddFollowRig(rig);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:follow-added", name);
        Show(message);
        return true;
    }

    internal bool TrySaveFollowRig(
        CameraFollowRig edited,
        out string message,
        bool showNotification = true)
    {
        ArgumentNullException.ThrowIfNull(edited);
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.FollowCameras, out message))
        {
            return false;
        }

        string name = edited.Name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:follow-name-required");
            Show(message);
            return false;
        }

        CameraFollowRig? sameName = library!.FindFollowRig(name);
        if (sameName is not null && sameName.Id != edited.Id)
        {
            message = Lang.Get("vsdirector:follow-name-exists", name);
            Show(message);
            return false;
        }

        if (!ValidateFollowRig(edited, out message))
        {
            Show(message);
            return false;
        }

        edited.Name = name;
        CameraLibraryDocument before = library.Document;
        if (!library.UpdateFollowRig(edited))
        {
            message = Lang.Get("vsdirector:follow-not-found", edited.Id);
            Show(message);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:follow-saved", name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryDeleteFollowRig(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.FollowCameras, out message))
        {
            return false;
        }

        CameraFollowRig? rig = library!.FindFollowRig(id);
        if (rig is null)
        {
            message = Lang.Get("vsdirector:follow-not-found", id);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.RemoveFollowRig(id);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        if (controller?.ActiveFollowRigId == id
            || pendingRemoteFollow?.Rig.Id == id
            || replayCameraState.Is(DirectorReplayCameraMode.Follow, id))
        {
            CancelPendingRemoteFollow(stopAnchor: false);
            controller?.Deactivate();
            replayCameraState = DirectorReplayCameraState.Inactive;
            anchorClient?.StopAnchor();
        }

        message = Lang.Get("vsdirector:follow-deleted", rig.Name);
        Show(message);
        return true;
    }

    internal bool TryActivateFollowRig(Guid id, bool showNotification = true)
    {
        if (!EnsurePermission(DirectorPermission.FollowCameras, out string permissionError))
        {
            Show(permissionError);
            return false;
        }
        if (!EnsureReady(out string error))
        {
            Show(error);
            return false;
        }

        if (!EnsureDirectorAccess(out error))
        {
            Show(error);
            return false;
        }

        CameraFollowRig? rig = library!.FindFollowRig(id);
        if (rig is null)
        {
            Show(Lang.Get("vsdirector:follow-not-found", id));
            return false;
        }

        if (rig.OpenClearView && !HasClearView)
        {
            Show(Lang.Get("vsdirector:follow-clear-view-unavailable"));
            return false;
        }

        Entity? target = ResolveFollowTarget(rig);
        if (target is null)
        {
            if (rig.TargetType == CameraFollowTargetType.Player
                && BeginRemoteFollow(rig, showNotification, preview: false))
            {
                return true;
            }

            Show(Lang.Get(rig.TargetType == CameraFollowTargetType.Player
                ? "vsdirector:follow-player-unavailable"
                : "vsdirector:follow-entity-unloaded", rig.TargetDisplayName));
            return false;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller!.TryFollow(rig, target, out error))
        {
            Show(Lang.Get("vsdirector:camera-error", error));
            return false;
        }

        SetReplayCameraState(DirectorReplayCameraMode.Follow, rig.Id);

        anchorClient?.Update(
            controller.CurrentPose,
            active: true,
            controller.ActiveFollowTargetPlayerUid,
            controller.ActiveFollowTargetEntityId);
        if (showNotification && config.ShowSwitchNotifications)
        {
            Show(Lang.Get("vsdirector:follow-active", rig.Name, rig.TargetDisplayName, ExitHotkeyLabel));
        }

        return true;
    }

    internal bool TryPreviewFollowRig(CameraFollowRig rig)
    {
        if (controller is null || replay?.IsExporting == true)
        {
            return false;
        }
        if (rig.OpenClearView && !HasClearView)
        {
            return false;
        }
        Entity? target = ResolveFollowTarget(rig);
        if (target is null)
        {
            if (rig.TargetType != CameraFollowTargetType.Player
                || !BeginRemoteFollow(rig, showNotification: false, preview: true))
            {
                return false;
            }

            KeepFollowPreviewUiVisible();
            return true;
        }

        if (controller.ActiveFollowRigId == rig.Id
            && controller.TryUpdateFollow(rig, target, out _))
        {
            anchorClient?.Update(
                controller.CurrentPose,
                active: true,
                controller.ActiveFollowTargetPlayerUid,
                controller.ActiveFollowTargetEntityId);
            KeepFollowPreviewUiVisible();
            return true;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller.TryFollow(rig, target, out _))
        {
            return false;
        }
        anchorClient?.Update(
            controller.CurrentPose,
            active: true,
            controller.ActiveFollowTargetPlayerUid,
            controller.ActiveFollowTargetEntityId);
        KeepFollowPreviewUiVisible();
        return true;
    }

    private void KeepFollowPreviewUiVisible()
    {
        if (workspace?.IsOpened() == true)
        {
            workspace.KeepReplayWorkspaceVisible();
            return;
        }

        SetCameraGuiOverride(true);
    }

    private bool BeginRemoteFollow(CameraFollowRig rig, bool showNotification, bool preview)
    {
        if (capi is null
            || controller is null
            || anchorClient?.AccessAllowed != true
            || rig.TargetType != CameraFollowTargetType.Player
            || string.IsNullOrWhiteSpace(rig.TargetPlayerUid))
        {
            return false;
        }

        CameraPreset? anchorPose = controller.CurrentPose;
        if (anchorPose is null
            && !controller.TryCaptureCurrentView("Remote follow preload", out anchorPose))
        {
            return false;
        }

        pendingRemoteFollow = new PendingRemoteFollow(
            rig.DeepClone(),
            anchorPose,
            capi.ElapsedMilliseconds,
            showNotification,
            preview);
        RefreshPendingRemoteChunkPriorities();
        anchorClient.Update(
            anchorPose,
            active: true,
            rig.TargetPlayerUid,
            rig.TargetEntityId);
        if (showNotification && config.ShowSwitchNotifications)
        {
            Show(Lang.Get("vsdirector:follow-remote-loading", rig.TargetDisplayName));
        }

        return true;
    }

    private void UpdatePendingRemoteFollow()
    {
        PendingRemoteFollow? pending = pendingRemoteFollow;
        if (pending is null || capi is null || controller is null)
        {
            return;
        }

        Entity? target = ResolveFollowTarget(pending.Rig);
        if (target is null)
        {
            if (capi.ElapsedMilliseconds - pending.StartedMilliseconds >= RemoteFollowLoadTimeoutMs)
            {
                FailPendingRemoteFollow("vsdirector:follow-player-unavailable");
            }
            return;
        }

        if (!UpdateRemoteFollowChunkWarmup(pending.Rig, target, pending.ChunkWarmup))
        {
            if (capi.ElapsedMilliseconds - pending.StartedMilliseconds >= RemoteFollowLoadTimeoutMs)
            {
                FailPendingRemoteFollow("vsdirector:follow-remote-timeout");
            }
            return;
        }

        pendingRemoteFollow = null;
        RefreshPendingRemoteChunkPriorities();
        if (!controller.TryFollow(pending.Rig, target, out string error))
        {
            anchorClient?.StopAnchor();
            if (!pending.Preview)
            {
                Show(Lang.Get("vsdirector:camera-error", error));
            }
            return;
        }

        if (!pending.Preview)
        {
            SetReplayCameraState(DirectorReplayCameraMode.Follow, pending.Rig.Id);
        }
        anchorClient?.Update(
            controller.CurrentPose,
            active: true,
            controller.ActiveFollowTargetPlayerUid,
            controller.ActiveFollowTargetEntityId);
        if (pending.Preview)
        {
            KeepFollowPreviewUiVisible();
        }
        if (!pending.Preview && pending.ShowNotification && config.ShowSwitchNotifications)
        {
            Show(Lang.Get(
                "vsdirector:follow-active",
                pending.Rig.Name,
                pending.Rig.TargetDisplayName,
                ExitHotkeyLabel));
        }
        RefreshWorkspaceDialogs();
    }

    private void UpdatePendingRemoteLiveOffscreen()
    {
        PendingRemoteLiveOffscreen? pending = pendingRemoteLiveOffscreen;
        if (pending is null || capi is null)
        {
            return;
        }

        Entity? target = ResolveFollowTarget(pending.Rig);
        if (target is null)
        {
            if (capi.ElapsedMilliseconds - pending.StartedMilliseconds >= RemoteFollowLoadTimeoutMs)
            {
                FailPendingRemoteLiveOffscreen("vsdirector:follow-player-unavailable");
            }
            return;
        }

        if (!UpdateRemoteFollowChunkWarmup(pending.Rig, target, pending.ChunkWarmup))
        {
            if (capi.ElapsedMilliseconds - pending.StartedMilliseconds >= RemoteFollowLoadTimeoutMs)
            {
                FailPendingRemoteLiveOffscreen("vsdirector:follow-remote-timeout");
            }
            return;
        }

        pendingRemoteLiveOffscreen = null;
        RefreshPendingRemoteChunkPriorities();
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        string sourceValue = pending.Source.Serialize();
        if (!TryStartLiveOffscreenRecording(sourceValue, out _))
        {
            anchorClient?.StopAnchor();
        }
        RefreshWorkspaceDialogs();
    }

    private void FailPendingRemoteLiveOffscreen(string messageKey)
    {
        PendingRemoteLiveOffscreen? pending = pendingRemoteLiveOffscreen;
        pendingRemoteLiveOffscreen = null;
        liveOffscreenFollowRig = null;
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        liveOffscreenSourceName = string.Empty;
        anchorClient?.StopAnchor();
        RefreshPendingRemoteChunkPriorities();
        if (pending is not null)
        {
            liveOffscreenStatus = Lang.Get(messageKey, pending.Rig.TargetDisplayName);
            Show(liveOffscreenStatus);
        }
        RefreshWorkspaceDialogs();
    }

    private void FailPendingRemoteFollow(string messageKey)
    {
        PendingRemoteFollow? pending = pendingRemoteFollow;
        pendingRemoteFollow = null;
        anchorClient?.StopAnchor();
        RefreshPendingRemoteChunkPriorities();
        if (pending is { Preview: false })
        {
            Show(Lang.Get(messageKey, pending.Rig.TargetDisplayName));
        }
        RefreshWorkspaceDialogs();
    }

    private void CancelPendingRemoteFollow(bool stopAnchor)
    {
        if (pendingRemoteFollow is null)
        {
            return;
        }

        pendingRemoteFollow = null;
        RefreshPendingRemoteChunkPriorities();
        if (stopAnchor)
        {
            anchorClient?.StopAnchor();
        }
    }

    private bool UpdateRemoteFollowChunkWarmup(
        CameraFollowRig rig,
        Entity target,
        RemoteFollowChunkWarmup warmup)
    {
        if (capi?.World is not ClientMain game
            || controller?.TryEvaluateFollowPose(rig, target, out CameraPreset cameraPose) != true)
        {
            return false;
        }

        CameraPreset targetPose = new()
        {
            X = target.Pos.X,
            Y = target.Pos.Y,
            Z = target.Pos.Z,
            Dimension = target.Pos.Dimension
        };
        warmup.CameraPose = cameraPose;
        warmup.TargetPose = targetPose;
        RefreshPendingRemoteChunkPriorities();

        int chunkSize = game.WorldMap.ClientChunkSize;
        RemoteChunkCenter cameraCenter = RemoteChunkCenter.From(cameraPose, chunkSize);
        if (warmup.PrioritizedCameraCenter != cameraCenter)
        {
            warmup.PrioritizedCameraCenter = cameraCenter;
            ReplayRemoteChunkPriorityPatch.PrioritizeExistingWindow(
                game.WorldMap,
                cameraPose,
                RemoteFollowReadyRadiusChunks);
        }

        RemoteChunkCenter targetCenter = RemoteChunkCenter.From(targetPose, chunkSize);
        if (warmup.PrioritizedTargetCenter != targetCenter)
        {
            warmup.PrioritizedTargetCenter = targetCenter;
            ReplayRemoteChunkPriorityPatch.PrioritizeExistingWindow(
                game.WorldMap,
                targetPose,
                RemoteFollowReadyRadiusChunks);
        }

        return ReplayRemoteChunkPriorityPatch.IsWindowRendered(
                game.WorldMap,
                cameraPose,
                RemoteFollowReadyRadiusChunks)
            && ReplayRemoteChunkPriorityPatch.IsWindowRendered(
                game.WorldMap,
                targetPose,
                RemoteFollowReadyRadiusChunks);
    }

    private void RefreshPendingRemoteChunkPriorities()
    {
        RemoteFollowChunkWarmup? warmup = pendingRemoteFollow?.ChunkWarmup
            ?? pendingRemoteLiveOffscreen?.ChunkWarmup;
        DirectorCameraRuntime.PendingRemoteCameraPose = warmup?.CameraPose;
        DirectorCameraRuntime.PendingRemoteTargetPose = warmup?.TargetPose;
    }

    internal bool TryAssignFollowSlot(Guid id, int slot, out string message)
    {
        CameraFollowRig? rig = library?.FindFollowRig(id);
        if (rig is null)
        {
            message = Lang.Get("vsdirector:follow-not-found", id);
            return false;
        }

        rig.QuickSlot = slot;
        return TrySaveFollowRig(rig, out message);
    }

    internal bool TrySetFollowOrbit(double angleDegrees, out string message)
    {
        if (controller?.TrySetFollowOrbit(angleDegrees, out double applied) != true)
        {
            message = Lang.Get("vsdirector:follow-third-person-required");
            Show(message);
            return false;
        }

        message = Lang.Get("vsdirector:follow-orbit-set", applied.ToString("0.#", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TrySetFollowDistance(double distance, out string message)
    {
        if (!double.IsFinite(distance) || distance < 0d || distance > CameraFollowMath.MaxDistance)
        {
            message = Lang.Get("vsdirector:follow-invalid-distance", CameraFollowMath.MaxDistance);
            Show(message);
            return false;
        }

        if (controller?.TrySetFollowDistance(distance, out double applied) != true)
        {
            message = Lang.Get("vsdirector:follow-third-person-required");
            Show(message);
            return false;
        }

        message = Lang.Get("vsdirector:follow-distance-set", applied.ToString("0.##", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal string FollowListText()
    {
        if (FollowRigs.Count == 0)
        {
            return Lang.Get("vsdirector:follow-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:follow-list-header", FollowRigs.Count) };
        for (int index = 0; index < FollowRigs.Count; index++)
        {
            CameraFollowRig rig = FollowRigs[index];
            string slot = rig.QuickSlot is int quickSlot && QuickSlots.IsValid(quickSlot)
                ? QuickSlotHotkeyLabel(quickSlot)
                : Lang.Get("vsdirector:no-slot");
            lines.Add(Lang.Get(
                "vsdirector:follow-list-entry",
                index + 1,
                rig.Name,
                rig.TargetDisplayName,
                FollowViewLabel(rig.ViewMode),
                FollowMotionLabel(rig.MotionMode),
                slot));
        }

        return string.Join("\n", lines);
    }

    internal bool TryAddPath(string requestedName, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:path-name-required");
            Show(message);
            return false;
        }

        if (library!.FindPath(name) != null)
        {
            message = Lang.Get("vsdirector:path-name-exists", name);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.AddPath(new CameraPath { Name = name });
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:path-added", name);
        Show(message);
        return true;
    }

    internal bool TryDeletePath(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        CameraPath? path = library!.FindPath(id);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", id);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.RemovePath(id);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        if (controller?.ActivePathId == id
            || replayCameraState.Is(DirectorReplayCameraMode.Path, id)
            || replayCameraState.Is(DirectorReplayCameraMode.SyncedPath, id))
        {
            controller?.Deactivate();
            replayCameraState = DirectorReplayCameraState.Inactive;
            anchorClient?.StopAnchor();
        }

        message = Lang.Get("vsdirector:path-deleted", path.Name);
        Show(message);
        return true;
    }

    internal bool TryCapturePathKeyframe(Guid pathId, double timeSeconds, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        CameraPath? path = library!.FindPath(pathId);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathId);
            return false;
        }

        if (!TryCapturePathKeyframePose(path, timeSeconds,
                out DirectorKeyframe frame, out message, preferBasePathPreview: true))
        {
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.UpsertPathKeyframe(pathId, frame);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:keyframe-saved", path.Name, timeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TryCapturePathKeyframePose(
        CameraPath path,
        double timeSeconds,
        out DirectorKeyframe keyframe,
        out string message,
        bool preferBasePathPreview = false)
    {
        keyframe = new DirectorKeyframe();
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        if (!double.IsFinite(timeSeconds) || timeSeconds < 0d)
        {
            message = Lang.Get("vsdirector:invalid-keyframe-time");
            Show(message);
            return false;
        }

        if (!controller!.TryCaptureCurrentView(path.Name, out CameraPreset pose))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        keyframe = DirectorKeyframe.FromPreset(pose, timeSeconds);
        // A path preview already includes element overrides. Recover the base
        // curve only while the user has left that preview pose unchanged.
        if (preferBasePathPreview
            && CameraPathSampler.TryEvaluate(path, timeSeconds, out DirectorKeyframe baseFrame)
            && controller.TryEvaluatePathPose(path, timeSeconds, out CameraPreset previewPose)
            && PathPreviewPoseMatches(pose, previewPose))
        {
            keyframe = DirectorKeyframe.FromPreset(
                baseFrame.ToPreset(path.Id, path.Name),
                timeSeconds);
        }
        return true;
    }

    private static bool PathPreviewPoseMatches(CameraPreset captured, CameraPreset preview)
    {
        const double positionEpsilon = 0.001d;
        const double angleEpsilon = 0.05d;
        return captured.Dimension == preview.Dimension
            && Math.Abs(captured.X - preview.X) <= positionEpsilon
            && Math.Abs(captured.Y - preview.Y) <= positionEpsilon
            && Math.Abs(captured.Z - preview.Z) <= positionEpsilon
            && AngleDistance(captured.Yaw, preview.Yaw) <= angleEpsilon
            && AngleDistance(captured.Pitch, preview.Pitch) <= angleEpsilon
            && AngleDistance(captured.Roll, preview.Roll) <= angleEpsilon
            && Math.Abs(captured.Fov - preview.Fov) <= angleEpsilon;
    }

    private static double AngleDistance(double left, double right)
    {
        double difference = (left - right) % 360d;
        if (difference > 180d)
        {
            difference -= 360d;
        }
        else if (difference < -180d)
        {
            difference += 360d;
        }
        return Math.Abs(difference);
    }

    internal bool TryRemovePathKeyframe(Guid pathId, int keyframeIndex, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        CameraPath? path = library!.FindPath(pathId);
        if (path is null || keyframeIndex < 0 || keyframeIndex >= path.Keyframes.Count)
        {
            message = Lang.Get("vsdirector:keyframe-not-found");
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.RemovePathKeyframe(pathId, keyframeIndex);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:keyframe-deleted", keyframeIndex + 1, path.Name);
        Show(message);
        return true;
    }

    internal bool TryUpdatePathSettings(
        Guid pathId,
        string name,
        CameraPathInterpolation interpolation,
        CameraPathLoopMode loopMode,
        bool previewVisible,
        out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:path-name-required");
            Show(message);
            return false;
        }

        CameraPath? sameName = library!.FindPath(name);
        if (sameName != null && sameName.Id != pathId)
        {
            message = Lang.Get("vsdirector:path-name-exists", name);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        if (!library.UpdatePathSettings(pathId, name, interpolation, loopMode, previewVisible))
        {
            message = Lang.Get("vsdirector:path-not-found", pathId);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:path-settings-saved", name);
        Show(message);
        return true;
    }

    internal bool TrySavePath(
        CameraPath edited,
        out string message,
        bool showNotification = true)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        string name = edited.Name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:path-name-required");
            Show(message);
            return false;
        }

        CameraPath? existing = library!.FindPath(edited.Id);
        if (existing is null)
        {
            message = Lang.Get("vsdirector:path-not-found", edited.Id);
            Show(message);
            return false;
        }

        CameraPath? sameName = library.FindPath(name);
        if (sameName is not null && sameName.Id != edited.Id)
        {
            message = Lang.Get("vsdirector:path-name-exists", name);
            Show(message);
            return false;
        }

        if (!double.IsFinite(edited.PlaybackSpeed)
            || edited.PlaybackSpeed < CameraPathTiming.MinPlaybackSpeed
            || edited.PlaybackSpeed > CameraPathTiming.MaxPlaybackSpeed)
        {
            message = Lang.Get(
                "vsdirector:invalid-path-speed",
                CameraPathTiming.MinPlaybackSpeed,
                CameraPathTiming.MaxPlaybackSpeed);
            Show(message);
            return false;
        }

        CameraPath copy = edited.DeepClone();
        copy.Name = name;
        CameraLibraryDocument before = library.Document;
        if (!library.UpdatePath(copy))
        {
            message = Lang.Get("vsdirector:path-not-found", copy.Id);
            Show(message);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:path-settings-saved", name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryExportPathJson(CameraPath path, out string json, out string message)
    {
        json = string.Empty;
        message = string.Empty;
        try
        {
            json = CameraPathJsonIO.Export(path);
            message = Lang.Get("vsdirector:path-json-exported");
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:path-json-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TryImportPathJson(string json, out Guid pathId, out string message)
    {
        pathId = Guid.Empty;
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.PathLibrary, out message))
        {
            return false;
        }

        try
        {
            CameraPath imported = CameraPathJsonIO.Import(json);
            imported.Id = Guid.Empty;
            imported.CreatedUtc = DateTimeOffset.UtcNow;
            imported.ModifiedUtc = imported.CreatedUtc;
            CameraLibraryDocument before = library!.Document;
            CameraPath added = library.AddPath(imported);
            if (!PersistOrRollback(before, out message))
            {
                return false;
            }
            pathId = added.Id;
            message = Lang.Get("vsdirector:path-json-imported", added.Name);
            Show(message);
            RefreshWorkspaceDialogs();
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:path-json-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TrySetPathKeyframeDuration(
        Guid pathId,
        int keyframeIndex,
        double durationSeconds,
        out string message)
    {
        CameraPath? path = library?.FindPath(pathId);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathId);
            Show(message);
            return false;
        }

        if (!CameraPathTiming.TrySetKeyframeDuration(
            path,
            keyframeIndex,
            durationSeconds,
            out double appliedDuration))
        {
            message = Lang.Get("vsdirector:invalid-keyframe-duration");
            Show(message);
            return false;
        }

        if (!TrySavePath(path, out message))
        {
            return false;
        }

        message = Lang.Get(
            "vsdirector:keyframe-duration-saved",
            keyframeIndex + 1,
            appliedDuration.ToString("0.###", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TrySetPathSpeed(Guid pathId, double playbackSpeed, out string message)
    {
        CameraPath? path = library?.FindPath(pathId);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathId);
            Show(message);
            return false;
        }

        path.PlaybackSpeed = playbackSpeed;
        return TrySavePath(path, out message);
    }

    internal bool TrySetPathSegmentMetadata(
        Guid pathId,
        int keyframeIndex,
        double startSpeed,
        double endSpeed,
        string marker,
        out string message)
    {
        CameraPath? path = library?.FindPath(pathId);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathId);
            Show(message);
            return false;
        }
        if (keyframeIndex < 0 || keyframeIndex >= path.Keyframes.Count)
        {
            message = Lang.Get("vsdirector:keyframe-not-found");
            Show(message);
            return false;
        }
        if (keyframeIndex < path.Keyframes.Count - 1
            && !CameraPathTiming.TrySetSegmentSpeeds(
                path,
                keyframeIndex,
                startSpeed,
                endSpeed,
                out _,
                out _))
        {
            message = Lang.Get("vsdirector:invalid-segment-speed");
            Show(message);
            return false;
        }

        path.Keyframes[keyframeIndex].Marker = marker?.Trim() ?? string.Empty;
        if (!TrySavePath(path, out message))
        {
            return false;
        }
        message = Lang.Get("vsdirector:path-segment-metadata-saved", keyframeIndex + 1);
        Show(message);
        return true;
    }

    internal bool TryPlayPath(
        Guid pathId,
        bool reverse,
        bool showNotification = true,
        CameraPathLoopMode? loopOverride = null)
    {
        if (!EnsurePermission(DirectorPermission.PathLibrary, out string permissionError))
        {
            Show(permissionError);
            return false;
        }
        if (!EnsureReady(out string error))
        {
            Show(error);
            return false;
        }

        if (!EnsureDirectorAccess(out error))
        {
            Show(error);
            return false;
        }

        CameraPath? path = library!.FindPath(pathId);
        if (path is null)
        {
            Show(Lang.Get("vsdirector:path-not-found", pathId));
            return false;
        }

        if (loopOverride is CameraPathLoopMode overrideMode)
        {
            path.LoopMode = overrideMode;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller!.TryPlayPath(path, reverse, out error))
        {
            Show(Lang.Get("vsdirector:camera-error", error));
            return false;
        }

        SetReplayCameraState(DirectorReplayCameraMode.Path, path.Id);

        if (showNotification)
        {
            Show(Lang.Get(reverse ? "vsdirector:path-playing-reverse" : "vsdirector:path-playing", path.Name, ExitHotkeyLabel));
        }

        return true;
    }

    internal string PathListText()
    {
        if (Paths.Count == 0)
        {
            return Lang.Get("vsdirector:path-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:path-list-header", Paths.Count) };
        for (int i = 0; i < Paths.Count; i++)
        {
            CameraPath path = Paths[i];
            lines.Add(Lang.Get(
                "vsdirector:path-list-entry",
                i + 1,
                path.Name,
                path.Keyframes.Count,
                path.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                path.Interpolation,
                path.LoopMode,
                path.PlaybackSpeed.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        return string.Join("\n", lines);
    }

    internal bool TryAddSequence(string requestedName, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraSequences, out message))
        {
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:sequence-name-required");
            Show(message);
            return false;
        }

        if (library!.FindSequence(name) is not null)
        {
            message = Lang.Get("vsdirector:sequence-name-exists", name);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.AddSequence(new CameraSequence { Name = name });
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:sequence-added", name);
        Show(message);
        return true;
    }

    internal bool TryDeleteSequence(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraSequences, out message))
        {
            return false;
        }

        CameraSequence? sequence = library!.FindSequence(id);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", id);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.RemoveSequence(id);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        if (controller?.ActiveSequenceId == id
            || replayCameraState.Is(DirectorReplayCameraMode.Sequence, id))
        {
            controller?.Deactivate();
            replayCameraState = DirectorReplayCameraState.Inactive;
            anchorClient?.StopAnchor();
        }

        message = Lang.Get("vsdirector:sequence-deleted", sequence.Name);
        Show(message);
        return true;
    }

    internal bool TrySaveSequence(
        CameraSequence edited,
        out string message,
        bool showNotification = true)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraSequences, out message))
        {
            return false;
        }

        string name = edited.Name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:sequence-name-required");
            Show(message);
            return false;
        }

        CameraSequence? existing = library!.FindSequence(edited.Id);
        if (existing is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", edited.Id);
            Show(message);
            return false;
        }

        CameraSequence? sameName = library.FindSequence(name);
        if (sameName is not null && sameName.Id != edited.Id)
        {
            message = Lang.Get("vsdirector:sequence-name-exists", name);
            Show(message);
            return false;
        }

        foreach (CameraSequenceShot? shot in edited.Shots ?? new List<CameraSequenceShot>())
        {
            if (shot is null)
            {
                message = Lang.Get("vsdirector:sequence-shot-not-found");
                Show(message);
                return false;
            }

            if (library.Find(shot.CameraId) is null)
            {
                message = Lang.Get("vsdirector:sequence-camera-missing");
                Show(message);
                return false;
            }

            if (!double.IsFinite(shot.DurationSeconds)
                || shot.DurationSeconds < CameraSequenceSampler.MinShotDurationSeconds
                || shot.DurationSeconds > CameraSequenceSampler.MaxShotDurationSeconds)
            {
                message = Lang.Get(
                    "vsdirector:invalid-shot-duration",
                    CameraSequenceSampler.MinShotDurationSeconds,
                    CameraSequenceSampler.MaxShotDurationSeconds);
                Show(message);
                return false;
            }
        }

        CameraSequence copy = edited.DeepClone();
        copy.Name = name;
        CameraLibraryDocument before = library.Document;
        if (!library.UpdateSequence(copy))
        {
            message = Lang.Get("vsdirector:sequence-not-found", copy.Id);
            Show(message);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:sequence-saved", name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryAddSequenceShot(Guid sequenceId, Guid cameraId, double durationSeconds, out string message)
    {
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", sequenceId);
            Show(message);
            return false;
        }

        if (library?.Find(cameraId) is null)
        {
            message = Lang.Get("vsdirector:camera-not-found", cameraId);
            Show(message);
            return false;
        }

        if (!IsValidShotDuration(durationSeconds, out message))
        {
            Show(message);
            return false;
        }

        sequence.Shots.Add(new CameraSequenceShot
        {
            CameraId = cameraId,
            DurationSeconds = durationSeconds
        });
        return TrySaveSequence(sequence, out message);
    }

    internal bool TryRemoveSequenceShot(Guid sequenceId, int shotIndex, out string message)
    {
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", sequenceId);
            Show(message);
            return false;
        }

        if (shotIndex < 0 || shotIndex >= sequence.Shots.Count)
        {
            message = Lang.Get("vsdirector:sequence-shot-not-found");
            Show(message);
            return false;
        }

        sequence.Shots.RemoveAt(shotIndex);
        return TrySaveSequence(sequence, out message);
    }

    internal bool TrySetSequenceShotDuration(
        Guid sequenceId,
        int shotIndex,
        double durationSeconds,
        out string message)
    {
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", sequenceId);
            Show(message);
            return false;
        }

        if (shotIndex < 0 || shotIndex >= sequence.Shots.Count)
        {
            message = Lang.Get("vsdirector:sequence-shot-not-found");
            Show(message);
            return false;
        }

        if (!IsValidShotDuration(durationSeconds, out message))
        {
            Show(message);
            return false;
        }

        sequence.Shots[shotIndex].DurationSeconds = durationSeconds;
        return TrySaveSequence(sequence, out message);
    }

    internal bool TryMoveSequenceShot(Guid sequenceId, int fromIndex, int toIndex, out string message)
    {
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", sequenceId);
            Show(message);
            return false;
        }

        if (fromIndex < 0 || fromIndex >= sequence.Shots.Count || toIndex < 0 || toIndex >= sequence.Shots.Count)
        {
            message = Lang.Get("vsdirector:sequence-shot-not-found");
            Show(message);
            return false;
        }

        CameraSequenceShot shot = sequence.Shots[fromIndex];
        sequence.Shots.RemoveAt(fromIndex);
        sequence.Shots.Insert(toIndex, shot);
        return TrySaveSequence(sequence, out message);
    }

    internal bool TrySetSequenceLoop(Guid sequenceId, CameraSequenceLoopMode loopMode, out string message)
    {
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        if (sequence is null)
        {
            message = Lang.Get("vsdirector:sequence-not-found", sequenceId);
            Show(message);
            return false;
        }

        sequence.LoopMode = loopMode;
        return TrySaveSequence(sequence, out message);
    }

    internal bool TryPlaySequence(
        Guid sequenceId,
        bool showNotification = true,
        CameraSequenceLoopMode? loopOverride = null)
    {
        if (!EnsurePermission(DirectorPermission.CameraSequences, out string permissionError))
        {
            Show(permissionError);
            return false;
        }
        if (!EnsureReady(out string error))
        {
            Show(error);
            return false;
        }

        if (!EnsureDirectorAccess(out error))
        {
            Show(error);
            return false;
        }

        CameraSequence? sequence = library!.FindSequence(sequenceId);
        if (sequence is null)
        {
            Show(Lang.Get("vsdirector:sequence-not-found", sequenceId));
            return false;
        }

        if (sequence.Shots.Count == 0)
        {
            Show(Lang.Get("vsdirector:sequence-empty-error"));
            return false;
        }

        Dictionary<Guid, CameraPreset> cameras = new();
        foreach (CameraSequenceShot shot in sequence.Shots)
        {
            CameraPreset? camera = library.Find(shot.CameraId);
            if (camera is null)
            {
                Show(Lang.Get("vsdirector:sequence-camera-missing"));
                return false;
            }

            cameras[camera.Id] = camera;
        }

        if (loopOverride is CameraSequenceLoopMode overrideMode)
        {
            sequence.LoopMode = overrideMode;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller!.TryPlaySequence(sequence, cameras, out error))
        {
            Show(Lang.Get("vsdirector:camera-error", error));
            return false;
        }

        SetReplayCameraState(DirectorReplayCameraMode.Sequence, sequence.Id);

        if (showNotification)
        {
            Show(Lang.Get("vsdirector:sequence-playing", sequence.Name, ExitHotkeyLabel));
        }

        return true;
    }

    internal string SequenceListText()
    {
        if (Sequences.Count == 0)
        {
            return Lang.Get("vsdirector:sequence-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:sequence-list-header", Sequences.Count) };
        for (int index = 0; index < Sequences.Count; index++)
        {
            CameraSequence sequence = Sequences[index];
            lines.Add(Lang.Get(
                "vsdirector:sequence-list-entry",
                index + 1,
                sequence.Name,
                sequence.Shots.Count,
                sequence.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                sequence.LoopMode == CameraSequenceLoopMode.Loop
                    ? Lang.Get("vsdirector:sequence-loop-loop")
                    : Lang.Get("vsdirector:sequence-loop-once")));
        }

        return string.Join("\n", lines);
    }

    internal bool TryAddWorkflow(string requestedName, out DirectorWorkflow? created, out string message)
    {
        created = null;
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        string name = requestedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:workflow-name-required");
            Show(message);
            return false;
        }

        if (library!.FindWorkflow(name) is not null)
        {
            message = Lang.Get("vsdirector:workflow-name-exists", name);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        created = library.AddWorkflow(new DirectorWorkflow { Name = name });
        if (!PersistOrRollback(before, out message))
        {
            created = null;
            return false;
        }

        message = Lang.Get("vsdirector:workflow-added", created.Name);
        Show(message);
        return true;
    }

    internal bool TrySaveWorkflow(
        DirectorWorkflow edited,
        out string message,
        bool showNotification = true)
    {
        ArgumentNullException.ThrowIfNull(edited);
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        string name = edited.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:workflow-name-required");
            Show(message);
            return false;
        }

        DirectorWorkflow? existing = library!.FindWorkflow(edited.Id);
        DirectorWorkflow? sameName = library.FindWorkflow(name);
        if (existing is null)
        {
            message = Lang.Get("vsdirector:workflow-not-found", edited.Id);
            Show(message);
            return false;
        }
        if (sameName is not null && sameName.Id != edited.Id)
        {
            message = Lang.Get("vsdirector:workflow-name-exists", name);
            Show(message);
            return false;
        }

        foreach (DirectorWorkflowCard card in edited.Cards ?? new List<DirectorWorkflowCard>())
        {
            if (card.Enabled && (!DirectorWorkflowPlanner.IsCardKindAllowed(edited.Type, card.Kind)
                || !double.IsFinite(card.StartSeconds)
                || !double.IsFinite(card.EndSeconds)
                || card.StartSeconds < 0d
                || card.EndSeconds - card.StartSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds
                || (card.Kind == DirectorWorkflowCardKind.Shot
                    && !WorkflowSourceExists(card.SourceType, card.SourceId))))
            {
                message = Lang.Get("vsdirector:workflow-card-invalid", card.Name);
                Show(message);
                return false;
            }
        }

        if (DirectorTimelineFfmpegPlanner.FindUnsupportedCrossDissolve(edited)
            is DirectorWorkflowTransitionIssue transitionIssue)
        {
            message = Lang.Get(
                "vsdirector:workflow-transition-overlap-invalid",
                transitionIssue.CardName,
                transitionIssue.RequestedDurationSeconds,
                transitionIssue.AvailableOverlapSeconds);
            Show(message);
            return false;
        }

        DirectorWorkflow copy = edited.DeepClone();
        copy.Name = name;
        CameraLibraryDocument before = library.Document;
        if (!library.UpdateWorkflow(copy) || !PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:workflow-saved", name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryDeleteWorkflow(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        DirectorWorkflow? workflow = library!.FindWorkflow(id);
        if (workflow is null)
        {
            message = Lang.Get("vsdirector:workflow-not-found", id);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        if (!library.RemoveWorkflow(id) || !PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:workflow-deleted", workflow.Name);
        Show(message);
        return true;
    }

    internal bool TryDeleteWorkflow(string selector, out string message)
    {
        DirectorWorkflow? workflow = FindWorkflow(selector);
        return workflow is not null
            ? TryDeleteWorkflow(workflow.Id, out message)
            : FailWorkflow(selector, out message);
    }

    internal bool TryListWorkflowAudio(string workflowSelector, out string message)
    {
        DirectorWorkflow? workflow = FindWorkflow(workflowSelector);
        if (workflow is null)
        {
            message = Lang.Get("vsdirector:workflow-not-found", workflowSelector);
            return false;
        }

        DirectorTimelineAudioClip[] clips = (workflow.AudioClips ?? new List<DirectorTimelineAudioClip>())
            .ToArray();
        if (clips.Length == 0)
        {
            message = Lang.Get("vsdirector:workflow-audio-list-empty", workflow.Name);
            return true;
        }

        message = Lang.Get("vsdirector:workflow-audio-list-header", workflow.Name, clips.Length)
            + "\n"
            + string.Join("\n", clips.Select((clip, index) => Lang.Get(
                "vsdirector:workflow-audio-list-entry",
                index + 1,
                clip.Name,
                clip.Track,
                clip.TimelineStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                clip.SourceStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                clip.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                clip.GainDecibels.ToString("0.##", CultureInfo.InvariantCulture),
                clip.PlaybackRate.ToString("0.###", CultureInfo.InvariantCulture),
                clip.InputPath)));
        return true;
    }

    internal bool TryAddWorkflowAudio(
        string workflowSelector,
        string inputPath,
        double timelineStartSeconds,
        double sourceStartSeconds,
        double durationSeconds,
        int track,
        double gainDecibels,
        double fadeInSeconds,
        double fadeOutSeconds,
        double playbackRate,
        out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        DirectorWorkflow? workflow = FindWorkflow(workflowSelector);
        if (workflow is null)
        {
            return FailWorkflow(workflowSelector, out message);
        }
        if (!DirectorWorkflowAudioEditor.TryCreate(
            inputPath,
            timelineStartSeconds,
            sourceStartSeconds,
            durationSeconds,
            track,
            gainDecibels,
            fadeInSeconds,
            fadeOutSeconds,
            playbackRate,
            out DirectorTimelineAudioClip clip,
            out string error))
        {
            message = WorkflowAudioErrorText(error);
            Show(message);
            return false;
        }

        workflow.AudioClips.Add(clip);
        if (!TrySaveWorkflow(workflow, out message, showNotification: false))
        {
            return false;
        }
        message = Lang.Get("vsdirector:workflow-audio-added", clip.Name, workflow.Name);
        Show(message);
        return true;
    }

    internal bool TryUpdateWorkflowAudio(
        string workflowSelector,
        string audioSelector,
        string inputPath,
        double timelineStartSeconds,
        double sourceStartSeconds,
        double durationSeconds,
        int track,
        double gainDecibels,
        double fadeInSeconds,
        double fadeOutSeconds,
        double playbackRate,
        out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        DirectorWorkflow? workflow = FindWorkflow(workflowSelector);
        if (workflow is null)
        {
            return FailWorkflow(workflowSelector, out message);
        }
        DirectorTimelineAudioClip? clip = DirectorWorkflowAudioEditor.Resolve(workflow, audioSelector);
        if (clip is null)
        {
            message = Lang.Get("vsdirector:workflow-audio-not-found", audioSelector);
            Show(message);
            return false;
        }
        if (!DirectorWorkflowAudioEditor.TryUpdate(
            clip,
            inputPath,
            timelineStartSeconds,
            sourceStartSeconds,
            durationSeconds,
            track,
            gainDecibels,
            fadeInSeconds,
            fadeOutSeconds,
            playbackRate,
            out string error))
        {
            message = WorkflowAudioErrorText(error);
            Show(message);
            return false;
        }

        if (!TrySaveWorkflow(workflow, out message, showNotification: false))
        {
            return false;
        }
        message = Lang.Get("vsdirector:workflow-audio-updated", clip.Name, workflow.Name);
        Show(message);
        return true;
    }

    internal bool TryDeleteWorkflowAudio(
        string workflowSelector,
        string audioSelector,
        out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }

        DirectorWorkflow? workflow = FindWorkflow(workflowSelector);
        if (workflow is null)
        {
            return FailWorkflow(workflowSelector, out message);
        }
        DirectorTimelineAudioClip? clip = DirectorWorkflowAudioEditor.Resolve(workflow, audioSelector);
        if (clip is null)
        {
            message = Lang.Get("vsdirector:workflow-audio-not-found", audioSelector);
            Show(message);
            return false;
        }

        workflow.AudioClips.Remove(clip);
        if (!TrySaveWorkflow(workflow, out message, showNotification: false))
        {
            return false;
        }
        message = Lang.Get("vsdirector:workflow-audio-deleted", clip.Name, workflow.Name);
        Show(message);
        return true;
    }

    private static string WorkflowAudioErrorText(string error)
        => Lang.Get(error == "workflow-audio-path"
            ? "vsdirector:workflow-audio-path-invalid"
            : "vsdirector:workflow-audio-range-invalid");

    private bool FailWorkflow(string selector, out string message)
    {
        message = Lang.Get("vsdirector:workflow-not-found", selector);
        Show(message);
        return false;
    }

    internal bool WorkflowSourceExists(DirectorRecordingSourceType type, Guid id)
    {
        if (library is null || id == Guid.Empty)
        {
            return false;
        }

        return type switch
        {
            DirectorRecordingSourceType.StaticCamera => library.Find(id) is not null,
            DirectorRecordingSourceType.Path => library.FindPath(id) is { Keyframes.Count: >= 2 },
            DirectorRecordingSourceType.Sequence => false,
            DirectorRecordingSourceType.Follow => library.FindFollowRig(id) is not null,
            _ => false
        };
    }

    internal string WorkflowSourceName(DirectorRecordingSourceType type, Guid id)
    {
        if (library is null)
        {
            return string.Empty;
        }

        return type switch
        {
            DirectorRecordingSourceType.StaticCamera => library.Find(id)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Path => library.FindPath(id)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Sequence => library.FindSequence(id)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Follow => library.FindFollowRig(id)?.Name ?? string.Empty,
            _ => string.Empty
        };
    }

    internal IReadOnlyList<DirectorWorkflowExportPass> PlanWorkflow(Guid workflowId)
    {
        DirectorWorkflow? workflow = library?.FindWorkflow(workflowId);
        return workflow is null
            ? Array.Empty<DirectorWorkflowExportPass>()
            : DirectorWorkflowPlanner.Create(
                workflow,
                card => WorkflowSourceExists(card.SourceType, card.SourceId));
    }

    internal bool TryStartWorkflowReplayRecording(
        Guid workflowId,
        Guid replayId,
        string recordingDevice,
        out string message)
    {
        message = string.Empty;
        WorkflowReplayRecordingBackend backend;
        if (string.Equals(recordingDevice, "obs", StringComparison.OrdinalIgnoreCase))
        {
            backend = WorkflowReplayRecordingBackend.Obs;
        }
        else if (string.Equals(recordingDevice, "offline", StringComparison.OrdinalIgnoreCase))
        {
            backend = WorkflowReplayRecordingBackend.Ffmpeg;
        }
        else if (string.Equals(recordingDevice, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            backend = WorkflowReplayRecordingBackend.Ffmpeg;
        }
        else
        {
            message = Lang.Get("vsdirector:workflow-record-device-invalid");
            Show(message);
            return false;
        }
        if (recording?.IsActive == true
            || workflowReplayRecording is not null
            || OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }
        if ((replay?.IsRecording == true && !automaticReplayRecording)
            || replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:replay-playback-active");
            Show(message);
            return false;
        }

        DirectorReplayInfo? replayInfo = Replays.FirstOrDefault(candidate => candidate.Id == replayId);
        if (replayInfo is null)
        {
            message = Lang.Get("vsdirector:replay-not-found", replayId);
            Show(message);
            return false;
        }
        DirectorWorkflow? workflow;
        IReadOnlyList<DirectorWorkflowExportPass> passes;
        string error;
        if (backend == WorkflowReplayRecordingBackend.Obs)
        {
            if (!TryPlanLiveWorkflow(workflowId, out workflow,
                    out DirectorWorkflowExportPass? pass, out error)
                || workflow is null
                || pass is null)
            {
                message = LiveWorkflowErrorText(error, workflow?.Name ?? string.Empty);
                Show(message);
                return false;
            }
            passes = new[] { pass };
        }
        else
        {
            workflow = library?.FindWorkflow(workflowId);
            if (workflow is null)
            {
                message = Lang.Get("vsdirector:workflow-not-found", workflowId);
                Show(message);
                return false;
            }
            if (!ValidateWorkflowMultiViewSources(workflow, out message))
            {
                Show(message);
                return false;
            }
            passes = DirectorWorkflowPlanner.Create(
                workflow,
                card => WorkflowSourceExists(card.SourceType, card.SourceId));
            if (passes.Count == 0)
            {
                message = Lang.Get("vsdirector:workflow-empty");
                Show(message);
                return false;
            }
            if (passes.Count > DirectorOffscreenMultiViewRenderer.MaximumViews)
            {
                message = Lang.Get(
                    "vsdirector:workflow-multiview-too-many-passes",
                    passes.Count,
                    DirectorOffscreenMultiViewRenderer.MaximumViews);
                Show(message);
                return false;
            }
        }
        double startSeconds = passes.Min(pass => pass.StartSeconds);
        double endSeconds = passes.Max(pass => pass.EndSeconds);
        if (endSeconds > replayInfo.DurationSeconds + DirectorWorkflowPlanner.TimeEpsilon)
        {
            message = Lang.Get("vsdirector:workflow-record-replay-range");
            Show(message);
            return false;
        }

        workflowReplayRecording = new WorkflowReplayRecordingSession(
            workflowId,
            replayInfo.Id,
            replayInfo.Name,
            startSeconds,
            endSeconds,
            backend,
            DirectorWorkflowPlanner.RecordingCountdownSeconds(workflow));
        if (!TryPlayReplay(replayInfo.Id.ToString("D"), out _))
        {
            workflowReplayRecording = null;
            message = Lang.Get("vsdirector:workflow-record-replay-load-failed", replayInfo.Name);
            Show(message);
            return false;
        }

        message = Lang.Get(
            backend != WorkflowReplayRecordingBackend.Obs
                ? "vsdirector:workflow-record-replay-loading-offline"
                : "vsdirector:workflow-record-replay-loading",
            workflow.Name,
            replayInfo.Name);
        Show(message);
        workspace?.TryClose();
        return true;
    }

    internal bool TryStartWorkflowReplayRecording(
        string workflowSelector,
        string replaySelector,
        out string message)
    {
        DirectorWorkflow? workflow = FindWorkflow(workflowSelector);
        if (workflow is null)
        {
            return FailWorkflow(workflowSelector, out message);
        }

        string selector = replaySelector?.Trim() ?? string.Empty;
        DirectorReplayInfo? replayInfo = int.TryParse(selector, out int index)
            && index >= 1
            && index <= Replays.Count
                ? Replays[index - 1]
                : Guid.TryParse(selector, out Guid replayId)
                    ? Replays.FirstOrDefault(info => info.Id == replayId)
                    : Replays.FirstOrDefault(info => string.Equals(
                        info.Name,
                        selector,
                        StringComparison.OrdinalIgnoreCase));
        if (replayInfo is null)
        {
            message = Lang.Get("vsdirector:replay-not-found", replaySelector);
            Show(message);
            return false;
        }

        return TryStartWorkflowReplayRecording(workflow.Id, replayInfo.Id, "obs", out message);
    }

    private bool TryGetOrCreateWorkflowRecordingTrack(
        DirectorWorkflow workflow,
        out DirectorRecordingTrack? track,
        out string message)
    {
        track = RecordingTracks.FirstOrDefault(candidate =>
            candidate.Enabled
            && candidate.SourceType == DirectorRecordingSourceType.Workflow
            && candidate.SourceId == workflow.Id);
        if (track is not null)
        {
            message = string.Empty;
            return true;
        }

        string baseName = string.IsNullOrWhiteSpace(workflow.Name)
            ? Lang.Get("vsdirector:record-source-workflow")
            : workflow.Name.Trim();
        string trackName = baseName;
        int suffix = 2;
        while (library?.FindRecordingTrack(trackName) is not null)
        {
            trackName = $"{baseName} ({suffix++})";
        }

        if (!TryAddRecordingTrack(
                DirectorRecordingSourceType.Workflow,
                workflow.Id.ToString("D"),
                trackName,
                out message))
        {
            return false;
        }

        track = RecordingTracks.FirstOrDefault(candidate =>
            candidate.Enabled
            && candidate.SourceType == DirectorRecordingSourceType.Workflow
            && candidate.SourceId == workflow.Id);
        return track is not null;
    }

    internal bool TryUpdateCameraMetadata(Guid id, string name, string tags, string description, float fov, float roll, int? slot, out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:camera-name-required");
            Show(message);
            return false;
        }

        CameraPreset? current = library!.Find(id);
        if (current == null)
        {
            message = Lang.Get("vsdirector:camera-not-found", id.ToString());
            Show(message);
            return false;
        }

        CameraPreset? sameName = library.Find(name);
        if (sameName != null && sameName.Id != id)
        {
            message = Lang.Get("vsdirector:camera-name-exists", name);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        bool updated = library.UpdateMetadata(
            id,
            name,
            tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            description,
            fov,
            roll,
            slot);
        if (!updated)
        {
            message = Lang.Get("vsdirector:camera-not-found", id.ToString());
            Show(message);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:camera-metadata-saved", name);
        Show(message);
        return true;
    }

    internal void PreviewCameraEdit(CameraPreset camera) => controller?.UpdateActiveCamera(camera);

    internal bool TryUpdateCamera(
        CameraPreset edited,
        out string message,
        bool showNotification = true)
    {
        ArgumentNullException.ThrowIfNull(edited);

        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.CameraLibrary, out message))
        {
            return false;
        }

        string name = edited.Name.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:camera-name-required");
            Show(message);
            return false;
        }

        CameraPreset? current = library!.Find(edited.Id);
        if (current is null)
        {
            message = Lang.Get("vsdirector:camera-not-found", edited.Id.ToString());
            Show(message);
            return false;
        }

        CameraPreset? sameName = library.Find(name);
        if (sameName is not null && sameName.Id != edited.Id)
        {
            message = Lang.Get("vsdirector:camera-name-exists", name);
            Show(message);
            return false;
        }

        if (!double.IsFinite(edited.X)
            || !double.IsFinite(edited.Y)
            || !double.IsFinite(edited.Z)
            || !float.IsFinite(edited.Yaw)
            || !float.IsFinite(edited.Pitch)
            || !float.IsFinite(edited.Roll)
            || !float.IsFinite(edited.Fov)
            || edited.Fov < 10f
            || edited.Fov > 170f)
        {
            message = Lang.Get("vsdirector:invalid-camera-pose-values");
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        bool updated = library.UpdateMetadata(
            edited.Id,
            name,
            edited.Tags ?? new List<string>(),
            edited.Description,
            edited.Fov,
            edited.Roll,
            edited.QuickSlot)
            && library.UpdatePose(edited.Id, edited);
        if (!updated)
        {
            message = Lang.Get("vsdirector:camera-not-found", edited.Id.ToString());
            Show(message);
            return false;
        }

        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:camera-metadata-saved", name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal void SetHudHidden(bool hide)
    {
        config.HideHudOnCamera = hide;
        controller?.SetHideHud(hide);
        SaveConfig();
        Show(Lang.Get("vsdirector:hud-setting", hide ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off")));
    }

    internal void SetInputLock(bool locked)
    {
        config.LockPlayerInput = locked;
        controller?.SetInputLock(locked);
        SaveConfig();
        Show(Lang.Get("vsdirector:input-setting", locked ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off")));
    }

    internal bool TryReloadConfig(out string message)
    {
        if (capi is null)
        {
            message = Lang.Get("vsdirector:config-unavailable");
            return false;
        }

        try
        {
            DirectorConfig loaded = capi.LoadModConfig<DirectorConfig>(ConfigFileName) ?? new DirectorConfig();
            loaded.Normalize();
            return TryApplySettings(loaded, out message);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("VS Director: failed to reload config: {0}", ex.Message);
            message = Lang.Get("vsdirector:config-reload-failed", ex.Message);
            Show(message);
            return false;
        }
    }

    internal bool TrySaveConfig(out string message)
    {
        if (SaveConfig())
        {
            message = Lang.Get("vsdirector:config-saved", ConfigFileName);
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:config-save-failed");
        Show(message);
        return false;
    }

    internal bool TryApplySettings(DirectorConfig edited, out string message)
    {
        ArgumentNullException.ThrowIfNull(edited);
        DirectorConfig normalized = new();
        normalized.CopyFrom(edited);
        normalized.Normalize();
        bool directoryChanged = !string.Equals(config.ReplayOutputDirectory, normalized.ReplayOutputDirectory, StringComparison.Ordinal);
        if (directoryChanged)
        {
            try
            {
                if (replay is not null && (replay.IsRecording || replay.HasPendingSave || replay.IsPlaying || replay.IsLoading || replay.IsExporting))
                    throw new InvalidOperationException(Lang.Get("vsdirector:replay-directory-busy"));
                string directory = DirectorReplayStore.ResolveRoot(capi!, normalized.ReplayOutputDirectory);
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".vsdirector-write-" + Guid.NewGuid().ToString("N"));
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                replay?.RefreshReplayDirectory(normalized.ReplayOutputDirectory);
            }
            catch (Exception e) { message = Lang.Get("vsdirector:replay-directory-error", e.Message); return false; }
        }
        DirectorConfig before = new();
        before.CopyFrom(config);
        config.CopyFrom(normalized);
        ApplyRuntimeConfig();
        if (!SaveConfig())
        {
            config.CopyFrom(before);
            if (directoryChanged) replay?.RefreshReplayDirectory(before.ReplayOutputDirectory);
            ApplyRuntimeConfig();
            message = Lang.Get("vsdirector:config-save-failed");
            Show(message);
            return false;
        }
        workspace?.Refresh();
        message = Lang.Get("vsdirector:config-saved", ConfigFileName);
        Show(message);
        return true;
    }

    internal string ConfigStatusText()
    {
        string outputDirectory = string.IsNullOrWhiteSpace(config.RecordingOutputDirectory)
            ? Lang.Get("vsdirector:config-default")
            : config.RecordingOutputDirectory;
        string baseStatus = Lang.Get(
            "vsdirector:config-status",
            ConfigFileName,
            config.HideHudOnCamera ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ShowSwitchNotifications ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.LockPlayerInput ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.LockInputWhileRecording ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.RecordingBackend,
            config.RecordingContainer,
            config.RecordingFrameRate,
            outputDirectory,
            config.ObsWebSocketUrl,
            string.IsNullOrEmpty(config.ObsWebSocketPassword) ? Lang.Get("vsdirector:off") : Lang.Get("vsdirector:on"),
            config.ReplaySampleRate.ToString("0.##", CultureInfo.InvariantCulture),
            config.ReplayMaxActors,
            config.LockInputDuringReplay ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplayCaptureWorld ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplaySnapshotRadiusChunks,
            config.ReplayEntityStateRate.ToString("0.##", CultureInfo.InvariantCulture),
            config.ReplayCaptureParticles ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplayCaptureSounds ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplayMaxWorldEvents,
            config.ReplayApplyWorldSnapshot ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplaySpawnGhostActors ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.ReplayFreeCameraSpeed.ToString("0.##", CultureInfo.InvariantCulture),
            config.ReplayExportWarmupFrames,
            config.ReplayWorldOperationsPerFrame,
            config.RecordReplayWithVideo ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));
        return baseStatus + "\n" + Lang.Get(
                 "vsdirector:config-status-replay-storage",
                Lang.Get("vsdirector:replay-compression-"
                    + config.ReplayCompression.ToString().ToLowerInvariant()),
                config.ReplayQuickMode ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
                config.AutoRecordReplay ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
                config.ReplayUseDedicatedRenderer ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));
    }

    internal void SetRecordingBackend(string backend)
    {
        config.RecordingBackend = DirectorRecordingBackend.ObsWebSocket;
        SaveConfig();
        Show(Lang.Get("vsdirector:record-backend-set", config.RecordingBackend));
    }

    internal void SetRecordingContainer(string container)
    {
        Show(Lang.Get("vsdirector:obs-only"));
    }

    internal void SetRecordingFrameRate(int framesPerSecond)
    {
        config.RecordingFrameRate = DirectorTimecode.NormalizeFrameRate(framesPerSecond);
        SaveConfig();
        Show(Lang.Get("vsdirector:record-fps-set", config.RecordingFrameRate));
    }

    internal void SetRecordReplayWithVideo(bool enabled)
    {
        config.RecordReplayWithVideo = enabled;
        SaveConfig();
        Show(Lang.Get(
            "vsdirector:record-replay-with-video-set",
            enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off")));
    }

    internal bool TryStartLiveOffscreenRecording(string sourceValue, out string message)
    {
        message = string.Empty;
        if (capi is null || library is null)
        {
            message = Lang.Get("vsdirector:world-not-ready");
            Show(message);
            return false;
        }
        if (!DirectorLiveOffscreenSource.TryParse(sourceValue, out DirectorLiveOffscreenSource source))
        {
            message = Lang.Get("vsdirector:live-offscreen-source-required");
            Show(message);
            return false;
        }
        DirectorPermission requiredPermission = source.Type == DirectorRecordingSourceType.Program
            ? DirectorPermission.OffscreenCurrentView
            : DirectorPermission.OffscreenSavedSources;
        if (!EnsurePermission(requiredPermission, out message))
        {
            Show(message);
            return false;
        }
        if (liveOffscreenState != LiveOffscreenRecordingState.Idle
            || OffscreenCaptureBusy
            || recording?.IsActive == true
            || replay?.IsPlaying == true
            || replay?.IsRecording == true
            || replay?.IsLoading == true
            || replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:live-offscreen-conflict");
            Show(message);
            return false;
        }

        liveOffscreenUsesSavedSource = source.Type != DirectorRecordingSourceType.Program;
        CameraFollowRig? requestedFollow = null;
        if (source.Type == DirectorRecordingSourceType.Follow)
        {
            requestedFollow = library.FindFollowRig(source.Id)?.DeepClone();
            if (requestedFollow is null)
            {
                message = Lang.Get("vsdirector:live-offscreen-source-missing");
                Show(message);
                return false;
            }

            if (ResolveFollowTarget(requestedFollow) is null)
            {
                if (requestedFollow.TargetType == CameraFollowTargetType.Player)
                {
                    if (BeginRemoteLiveOffscreen(source, requestedFollow, out message))
                    {
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return false;
                    }
                }

                message = Lang.Get(requestedFollow.TargetType == CameraFollowTargetType.Player
                    ? "vsdirector:follow-player-unavailable"
                    : "vsdirector:follow-entity-unloaded", requestedFollow.TargetDisplayName);
                Show(message);
                return false;
            }
        }

        if (!EnsureOffscreenRenderer(out string rendererError) || offscreenRenderer is null)
        {
            message = Lang.Get("vsdirector:offscreen-unavailable-detail", rendererError);
            Show(message);
            return false;
        }
        if (!TryCreateLiveOffscreenPoseEvaluator(
                source,
                out string sourceName,
                out System.Func<double, DirectorOffscreenCameraPose?>? evaluator,
                out string sourceError)
            || evaluator is null)
        {
            message = sourceError;
            Show(message);
            return false;
        }

        if (config.LiveOffscreenBackend != DirectorLiveOffscreenBackend.Obs)
        {
            liveOffscreenFollowRig = requestedFollow;
            bool started = TryStartLiveOffscreenFileRecording(
                sourceName,
                evaluator,
                config.LiveOffscreenBackend,
                out message);
            if (!started)
            {
                liveOffscreenFollowRig = null;
            }
            return started;
        }

        DirectorLiveOffscreenSession? session = null;
        try
        {
            session = new DirectorLiveOffscreenSession(
                offscreenRenderer,
                sourceName,
                config.OffscreenWidth,
                config.OffscreenHeight,
                config.OffscreenFrameRate,
                evaluator,
                () => capi?.World?.Player?.Entity?.Pos?.XYZFloat,
                config.OffscreenCaptureDirectorAudio,
                config.OffscreenCaptureVoiceModAudio);
            if (!session.Start(out string audioError))
            {
                throw new InvalidOperationException(audioError);
            }
        }
        catch (Exception exception)
        {
            session?.Dispose();
            message = Lang.Get(
                "vsdirector:live-offscreen-start-failed",
                exception.GetBaseException().Message);
            Show(message);
            return false;
        }

        long operationId = ++liveOffscreenOperationId;
        liveOffscreenFollowRig = requestedFollow;
        liveOffscreenSession = session;
        liveOffscreenSourceName = sourceName;
        liveOffscreenActiveBackend = DirectorLiveOffscreenBackend.Obs;
        liveOffscreenState = LiveOffscreenRecordingState.Starting;
        liveOffscreenStatus = Lang.Get("vsdirector:live-offscreen-connecting", sourceName);
        liveOffscreenOutputPath = string.Empty;
        liveOffscreenCancellation?.Dispose();
        liveOffscreenCancellation = new CancellationTokenSource();
        _ = RunLiveOffscreenObsStartAsync(
            operationId,
            session,
            liveOffscreenCancellation.Token);
        message = liveOffscreenStatus;
        Show(message);
        return true;
    }

    private bool BeginRemoteLiveOffscreen(
        DirectorLiveOffscreenSource source,
        CameraFollowRig rig,
        out string message)
    {
        message = string.Empty;
        if (capi is null || controller is null || !EnsureDirectorAccess(out message))
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Show(message);
            }
            return false;
        }

        CameraPreset? anchorPose = controller.CurrentPose;
        if (anchorPose is null
            && !controller.TryCaptureCurrentView("Remote offscreen follow preload", out anchorPose))
        {
            message = Lang.Get("vsdirector:live-offscreen-source-missing");
            return false;
        }

        pendingRemoteLiveOffscreen = new PendingRemoteLiveOffscreen(
            source,
            rig.DeepClone(),
            anchorPose,
            capi.ElapsedMilliseconds);
        RefreshPendingRemoteChunkPriorities();
        liveOffscreenState = LiveOffscreenRecordingState.LoadingTarget;
        liveOffscreenSourceName = rig.Name;
        liveOffscreenStatus = Lang.Get(
            "vsdirector:live-offscreen-follow-loading",
            rig.TargetDisplayName);
        liveOffscreenOutputPath = string.Empty;
        anchorClient!.Update(
            anchorPose,
            active: true,
            rig.TargetPlayerUid,
            rig.TargetEntityId);
        message = liveOffscreenStatus;
        Show(message);
        return true;
    }

    private bool TryStartLiveOffscreenFileRecording(
        string sourceName,
        System.Func<double, DirectorOffscreenCameraPose?> evaluator,
        DirectorLiveOffscreenBackend backend,
        out string message)
    {
        if (capi is null || offscreenRenderer is null)
        {
            message = Lang.Get("vsdirector:offscreen-unavailable");
            Show(message);
            return false;
        }
        if (backend != DirectorLiveOffscreenBackend.Ffmpeg)
        {
            message = Lang.Get("vsdirector:live-offscreen-backend-invalid");
            Show(message);
            return false;
        }

        Guid viewId = Guid.NewGuid();
        const string extension = ".mp4";
        string outputPath;
        DirectorOffscreenFfmpegSink sink;
        DirectorLiveOffscreenSession session;
        try
        {
            string fileName = string.Format(
                CultureInfo.InvariantCulture,
                "{0}_LiveOffscreen_{1}_{2}{3}",
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture),
                DirectorRecordingFiles.SanitizeFilePart(sourceName, "director"),
                viewId.ToString("N")[..8],
                extension);
            outputPath = Path.Combine(ResolveOffscreenOutputDirectory(), fileName);
            sink = CreateOffscreenSink(viewId, outputPath);
            liveOffscreenVoiceWavePath = outputPath + ".voice.wav";
            session = new DirectorLiveOffscreenSession(
                offscreenRenderer,
                viewId,
                sourceName,
                config.OffscreenWidth,
                config.OffscreenHeight,
                evaluator,
                sink,
                config.OffscreenCaptureVoiceModAudio,
                liveOffscreenVoiceWavePath);
        }
        catch (Exception exception)
        {
            message = Lang.Get(
                "vsdirector:live-offscreen-start-failed",
                exception.GetBaseException().Message);
            Show(message);
            return false;
        }

        long operationId = ++liveOffscreenOperationId;
        liveOffscreenSession = session;
        liveOffscreenFileSink = sink;
        liveOffscreenSourceName = sourceName;
        liveOffscreenActiveBackend = backend;
        liveOffscreenState = LiveOffscreenRecordingState.Starting;
        liveOffscreenStatus = Lang.Get(
            "vsdirector:live-offscreen-file-starting",
            LiveOffscreenBackendLabel(backend),
            sourceName,
            outputPath);
        liveOffscreenOutputPath = string.Empty;
        liveOffscreenCancellation?.Dispose();
        liveOffscreenCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = liveOffscreenCancellation.Token;
        ICoreClientAPI api = capi;
        liveOffscreenFileOperation = Task.Run(() =>
        {
            Exception? failure = null;
            try
            {
                sink.Start(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            bool cancelled = cancellationToken.IsCancellationRequested;
            api.Event.EnqueueMainThreadTask(
                () => CompleteLiveOffscreenFileStart(
                    operationId,
                    session,
                    sink,
                    outputPath,
                    backend,
                    failure,
                    cancelled),
                "vsdirector-live-offscreen-file-start-complete");
        });
        message = liveOffscreenStatus;
        Show(message);
        return true;
    }

    private void CompleteLiveOffscreenFileStart(
        long operationId,
        DirectorLiveOffscreenSession session,
        DirectorOffscreenFfmpegSink sink,
        string outputPath,
        DirectorLiveOffscreenBackend backend,
        Exception? failure,
        bool cancelled)
    {
        if (operationId != liveOffscreenOperationId
            || cancelled
            || !ReferenceEquals(session, liveOffscreenSession)
            || !ReferenceEquals(sink, liveOffscreenFileSink)
            || liveOffscreenState != LiveOffscreenRecordingState.Starting)
        {
            BeginDetachedOffscreenSinkStop(new[] { sink });
            return;
        }
        liveOffscreenFileOperation = null;

        if (failure is not null)
        {
            liveOffscreenFileSink = null;
            CleanupLiveOffscreenSession();
            try { File.Delete(liveOffscreenVoiceWavePath); } catch { }
            liveOffscreenVoiceWavePath = string.Empty;
            liveOffscreenState = LiveOffscreenRecordingState.Idle;
            liveOffscreenStatus = Lang.Get(
                "vsdirector:live-offscreen-start-failed",
                failure.GetBaseException().Message);
            BeginDetachedOffscreenSinkStop(new[] { sink });
            Show(liveOffscreenStatus);
            return;
        }

        try
        {
            if (!session.Start(out string sessionError))
            {
                throw new InvalidOperationException(sessionError);
            }
        }
        catch (Exception exception)
        {
            liveOffscreenFileSink = null;
            CleanupLiveOffscreenSession();
            liveOffscreenState = LiveOffscreenRecordingState.Idle;
            liveOffscreenStatus = Lang.Get(
                "vsdirector:live-offscreen-start-failed",
                exception.GetBaseException().Message);
            BeginDetachedOffscreenSinkStop(new[] { sink });
            Show(liveOffscreenStatus);
            return;
        }

        liveOffscreenState = LiveOffscreenRecordingState.Recording;
        liveOffscreenStatus = Lang.Get(
            "vsdirector:live-offscreen-file-recording",
            LiveOffscreenBackendLabel(backend),
            liveOffscreenSourceName,
            config.OffscreenWidth,
            config.OffscreenHeight,
            config.OffscreenFrameRate,
            session.OutputCodec,
            outputPath);
        Show(liveOffscreenStatus);
    }

    internal bool TryStartEscapeMenuLiveOffscreenRecording(
        string? selectedSource,
        out string resolvedSource,
        out string message)
    {
        resolvedSource = selectedSource?.Trim() ?? string.Empty;
        message = string.Empty;
        if (!CanStartEscapeMenuLiveOffscreen)
        {
            message = Lang.Get("vsdirector:live-offscreen-conflict");
            Show(message);
            return false;
        }
        if (resolvedSource.Length == 0)
        {
            resolvedSource = new DirectorLiveOffscreenSource(
                DirectorRecordingSourceType.Program,
                Guid.Empty).Serialize();
        }
        else if (!EnsurePermission(DirectorPermission.OffscreenSavedSources, out message))
        {
            Show(message);
            return false;
        }

        return TryStartLiveOffscreenRecording(resolvedSource, out message);
    }

    internal bool TryStopLiveOffscreenRecording(out string message)
    {
        if (liveOffscreenState == LiveOffscreenRecordingState.Idle)
        {
            message = Lang.Get("vsdirector:live-offscreen-not-active");
            Show(message);
            return false;
        }
        if (liveOffscreenState == LiveOffscreenRecordingState.Stopping)
        {
            message = liveOffscreenStatus;
            return true;
        }
        if (liveOffscreenState == LiveOffscreenRecordingState.LoadingTarget)
        {
            ++liveOffscreenOperationId;
            pendingRemoteLiveOffscreen = null;
            RefreshPendingRemoteChunkPriorities();
            liveOffscreenFollowRig = null;
            liveOffscreenSourceName = string.Empty;
            liveOffscreenState = LiveOffscreenRecordingState.Idle;
            liveOffscreenStatus = Lang.Get("vsdirector:live-offscreen-cancelled");
            anchorClient?.StopAnchor();
            message = liveOffscreenStatus;
            Show(message);
            return true;
        }

        long operationId = liveOffscreenOperationId;
        if (liveOffscreenState == LiveOffscreenRecordingState.Starting)
        {
            ++liveOffscreenOperationId;
            liveOffscreenCancellation?.Cancel();
            liveOffscreenFileSink = null;
            CleanupLiveOffscreenSession();
            liveOffscreenState = LiveOffscreenRecordingState.Idle;
            liveOffscreenStatus = Lang.Get("vsdirector:live-offscreen-cancelled");
            message = liveOffscreenStatus;
            Show(message);
            return true;
        }

        liveOffscreenState = LiveOffscreenRecordingState.Stopping;
        liveOffscreenStatus = liveOffscreenActiveBackend == DirectorLiveOffscreenBackend.Obs
            ? Lang.Get("vsdirector:live-offscreen-stopping", liveOffscreenSourceName)
            : Lang.Get(
                "vsdirector:live-offscreen-file-stopping",
                LiveOffscreenBackendLabel(liveOffscreenActiveBackend),
                liveOffscreenSourceName);
        message = liveOffscreenStatus;
        Show(message);
        if (liveOffscreenActiveBackend != DirectorLiveOffscreenBackend.Obs)
        {
            DirectorOffscreenFfmpegSink? sink = liveOffscreenFileSink;
            DirectorLiveOffscreenBackend backend = liveOffscreenActiveBackend;
            liveOffscreenVoiceWavePath = liveOffscreenSession?.VoiceWavePath ?? string.Empty;
            liveOffscreenFileSink = null;
            CleanupLiveOffscreenSession();
            if (sink is null)
            {
                CompleteLiveOffscreenFileStop(
                    operationId,
                    null,
                    new InvalidOperationException("The live offscreen output sink is unavailable."),
                    backend);
                return true;
            }
            BeginLiveOffscreenFileStop(operationId, sink, backend);
            return true;
        }
        DirectorObsWebSocketClient? obs = liveOffscreenObs;
        if (obs is null)
        {
            CompleteLiveOffscreenStop(operationId, string.Empty, null);
            return true;
        }

        _ = RunLiveOffscreenObsStopAsync(operationId, obs);
        return true;
    }

    private void BeginLiveOffscreenFileStop(
        long operationId,
        DirectorOffscreenFfmpegSink sink,
        DirectorLiveOffscreenBackend backend)
    {
        ICoreClientAPI? api = capi;
        if (api is null)
        {
            return;
        }
        liveOffscreenFileOperation = Task.Run(() =>
        {
            OffscreenStopOutcome outcome = StopOffscreenSink(sink);
            api.Event.EnqueueMainThreadTask(
                () =>
                {
                    try
                    {
                        sink.CompleteGpuCleanupOnRenderThread();
                    }
                    catch (Exception exception)
                    {
                        api.Logger.Warning(
                            "VS Director: live offscreen PBO cleanup failed: {0}",
                            exception.Message);
                    }
                    finally
                    {
                        sink.Dispose();
                    }
                    CompleteLiveOffscreenFileStop(
                        operationId,
                        outcome.Result,
                        outcome.Error,
                        backend);
                },
                "vsdirector-live-offscreen-file-stop-complete");
        });
    }

    private void CompleteLiveOffscreenFileStop(
        long operationId,
        DirectorOffscreenFfmpegResult? result,
        Exception? failure,
        DirectorLiveOffscreenBackend backend)
    {
        if (operationId != liveOffscreenOperationId)
        {
            return;
        }

        liveOffscreenFileOperation = null;
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        string error = failure?.GetBaseException().Message
            ?? (result is { Success: false } failed ? failed.Error : string.Empty);
        if (result is DirectorOffscreenFfmpegResult completed)
        {
            liveOffscreenOutputPath = completed.OutputPath?.Trim() ?? string.Empty;
            if (error.Length == 0
                && config.OffscreenCaptureVoiceModAudio
                && liveOffscreenVoiceWavePath.Length > 0
                && File.Exists(liveOffscreenVoiceWavePath)
                && File.Exists(liveOffscreenOutputPath))
            {
                string temporaryVideoPath = liveOffscreenOutputPath + ".voice-remux.mp4";
                try
                {
                    DirectorFfmpegResult mux = DirectorFfmpegExporter.RemuxMp4Async(
                            config.FfmpegPath,
                            liveOffscreenOutputPath,
                            liveOffscreenVoiceWavePath,
                            temporaryVideoPath,
                            new DirectorTimecode(0L, config.OffscreenFrameRate),
                            config.FfmpegAudioBitrateKbps,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    if (!mux.Success)
                    {
                        error = mux.Message;
                    }
                    else
                    {
                        File.Move(temporaryVideoPath, liveOffscreenOutputPath, overwrite: true);
                    }
                }
                catch (Exception exception)
                {
                    error = exception.GetBaseException().Message;
                }
                finally
                {
                    try { File.Delete(temporaryVideoPath); } catch { }
                    try { File.Delete(liveOffscreenVoiceWavePath); } catch { }
                }
            }
        }
        liveOffscreenVoiceWavePath = string.Empty;
        liveOffscreenStatus = error.Length == 0
            ? Lang.Get(
                "vsdirector:live-offscreen-file-stopped",
                LiveOffscreenBackendLabel(backend),
                liveOffscreenOutputPath,
                result?.Statistics.EncodedFrames ?? 0L,
                result?.Statistics.DroppedFrames ?? 0L)
            : Lang.Get(
                "vsdirector:live-offscreen-file-stop-failed",
                LiveOffscreenBackendLabel(backend),
                error);
        Show(liveOffscreenStatus);
    }

    private async Task RunLiveOffscreenObsStartAsync(
        long operationId,
        DirectorLiveOffscreenSession session,
        CancellationToken cancellationToken)
    {
        DirectorObsWebSocketClient? obs = null;
        bool recordingStarted = false;
        try
        {
            obs = new DirectorObsWebSocketClient(config.ObsWebSocketUrl, config.ObsWebSocketPassword);
            await obs.PrepareAsync(cancellationToken).ConfigureAwait(false);
            if (!await obs.IsInputKindAvailableAsync(
                    DirectorObsSharedTransport.InputKind,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    Lang.Get("vsdirector:live-offscreen-obs-source-missing"));
            }
            string inputName = await obs.EnsureInputInProgramSceneAsync(
                DirectorObsSharedTransport.InputKind,
                "VS Director Offscreen",
                cancellationToken).ConfigureAwait(false);

            long deadline = Environment.TickCount64 + 6000L;
            while (!session.ConsumerConnected && Environment.TickCount64 < deadline)
            {
                if (session.AudioFailure.Length > 0)
                {
                    throw new InvalidOperationException(session.AudioFailure);
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (!session.ConsumerConnected)
            {
                throw new InvalidOperationException(
                    Lang.Get("vsdirector:live-offscreen-obs-source-inactive", inputName));
            }

            if (config.OffscreenMuteOtherObsAudio)
            {
                await obs.SuspendOtherAudioInputsAsync(inputName, cancellationToken).ConfigureAwait(false);
            }
            await obs.StartRecordingAsync(cancellationToken).ConfigureAwait(false);
            recordingStarted = true;
            cancellationToken.ThrowIfCancellationRequested();
            DirectorObsWebSocketClient activeObs = obs;
            obs = null;
            EnqueueClientTask(
                () => CompleteLiveOffscreenStart(operationId, session, activeObs),
                "vsdirector-live-offscreen-started");
        }
        catch (Exception exception)
        {
            if (recordingStarted && obs is not null)
            {
                try
                {
                    _ = await obs.StopRecordingAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            if (obs is not null)
            {
                try
                {
                    await obs.RestoreDesktopAudioAsync(CancellationToken.None).ConfigureAwait(false);
                    await obs.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            obs?.Dispose();
            if (!cancellationToken.IsCancellationRequested)
            {
                string error = exception.GetBaseException().Message;
                EnqueueClientTask(
                    () => FailLiveOffscreenStart(operationId, error),
                    "vsdirector-live-offscreen-start-failed");
            }
        }
    }

    private void CompleteLiveOffscreenStart(
        long operationId,
        DirectorLiveOffscreenSession session,
        DirectorObsWebSocketClient obs)
    {
        if (operationId != liveOffscreenOperationId
            || !ReferenceEquals(session, liveOffscreenSession)
            || liveOffscreenState != LiveOffscreenRecordingState.Starting)
        {
            _ = StopStaleLiveOffscreenObsAsync(obs);
            return;
        }

        liveOffscreenObs = obs;
        liveOffscreenState = LiveOffscreenRecordingState.Recording;
        session.SetRecording(true);
        liveOffscreenStatus = Lang.Get(
            session.AudioEnabled
                ? "vsdirector:live-offscreen-recording"
                : "vsdirector:live-offscreen-recording-video-only",
            liveOffscreenSourceName,
            config.OffscreenWidth,
            config.OffscreenHeight,
            config.OffscreenFrameRate);
        Show(liveOffscreenStatus);
    }

    private static async Task StopStaleLiveOffscreenObsAsync(DirectorObsWebSocketClient obs)
    {
        try
        {
            _ = await obs.StopRecordingAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try
            {
                await obs.RestoreDesktopAudioAsync().ConfigureAwait(false);
                await obs.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            obs.Dispose();
        }
    }

    private void FailLiveOffscreenStart(long operationId, string error)
    {
        if (operationId != liveOffscreenOperationId
            || liveOffscreenState != LiveOffscreenRecordingState.Starting)
        {
            return;
        }

        CleanupLiveOffscreenSession();
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        liveOffscreenStatus = Lang.Get("vsdirector:live-offscreen-start-failed", error);
        Show(liveOffscreenStatus);
    }

    private async Task RunLiveOffscreenObsStopAsync(
        long operationId,
        DirectorObsWebSocketClient obs)
    {
        string outputPath = string.Empty;
        Exception? failure = null;
        try
        {
            outputPath = await obs.StopRecordingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await obs.RestoreDesktopAudioAsync().ConfigureAwait(false);
            await obs.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        EnqueueClientTask(
            () => CompleteLiveOffscreenStop(operationId, outputPath, failure),
            "vsdirector-live-offscreen-stopped");
    }

    private void CompleteLiveOffscreenStop(
        long operationId,
        string outputPath,
        Exception? failure)
    {
        if (operationId != liveOffscreenOperationId)
        {
            return;
        }

        liveOffscreenOutputPath = outputPath?.Trim() ?? string.Empty;
        CleanupLiveOffscreenSession();
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        liveOffscreenStatus = failure is null
            ? Lang.Get("vsdirector:live-offscreen-stopped", liveOffscreenOutputPath)
            : Lang.Get(
                "vsdirector:live-offscreen-stop-failed",
                failure.GetBaseException().Message);
        Show(liveOffscreenStatus);
    }

    private void CleanupLiveOffscreenSession()
    {
        liveOffscreenCancellation?.Cancel();
        liveOffscreenCancellation?.Dispose();
        liveOffscreenCancellation = null;
        liveOffscreenObs?.Dispose();
        liveOffscreenObs = null;
        try
        {
            liveOffscreenSession?.SetRecording(false);
            liveOffscreenSession?.Dispose();
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning(
                "VS Director: live offscreen cleanup failed: {0}",
                exception.Message);
        }
        liveOffscreenSession = null;
        liveOffscreenSourceName = string.Empty;
        liveOffscreenUsesSavedSource = false;
        liveOffscreenFollowRig = null;
        pendingRemoteLiveOffscreen = null;
        RefreshPendingRemoteChunkPriorities();
    }

    private void ForceStopLiveOffscreenForShutdown()
    {
        ++liveOffscreenOperationId;
        liveOffscreenCancellation?.Cancel();
        DirectorOffscreenFfmpegSink? fileSink = liveOffscreenFileSink;
        Task? fileOperation = liveOffscreenFileOperation;
        liveOffscreenFileSink = null;
        liveOffscreenFileOperation = null;
        if (liveOffscreenState == LiveOffscreenRecordingState.Recording
            && liveOffscreenObs is DirectorObsWebSocketClient obs)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                _ = obs.StopRecordingAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning(
                    "VS Director: OBS did not confirm live offscreen stop during shutdown: {0}",
                    exception.Message);
            }
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                obs.RestoreDesktopAudioAsync(timeout.Token).GetAwaiter().GetResult();
                obs.DisconnectAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning(
                    "VS Director: OBS desktop audio state was not restored during shutdown: {0}",
                    exception.Message);
            }
        }
        CleanupLiveOffscreenSession();
        if (fileOperation is { IsCompleted: false })
        {
            try
            {
                _ = fileOperation.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning(
                    "VS Director: live offscreen output startup did not settle during shutdown: {0}",
                    exception.GetBaseException().Message);
            }
        }
        if (fileSink is not null)
        {
            try
            {
                DirectorOffscreenFfmpegResult result = fileSink.Stop();
                if (!result.Success)
                {
                    capi?.Logger.Warning(
                        "VS Director: live offscreen output did not finalize cleanly during shutdown: {0}",
                        result.Error);
                }
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning(
                    "VS Director: live offscreen output could not stop during shutdown: {0}",
                    exception.GetBaseException().Message);
            }
            try
            {
                fileSink.CompleteGpuCleanupOnRenderThread();
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning(
                    "VS Director: live offscreen PBO cleanup failed during shutdown: {0}",
                    exception.Message);
            }
            finally
            {
                fileSink.Dispose();
            }
        }
        liveOffscreenState = LiveOffscreenRecordingState.Idle;
        liveOffscreenStatus = string.Empty;
    }

    private static string LiveOffscreenBackendLabel(DirectorLiveOffscreenBackend backend)
        => Lang.Get(backend switch
        {
            DirectorLiveOffscreenBackend.Ffmpeg => "vsdirector:offscreen-backend-ffmpeg",
            _ => "vsdirector:offscreen-backend-obs"
        });

    private bool TryCreateLiveOffscreenPoseEvaluator(
        DirectorLiveOffscreenSource source,
        out string sourceName,
        out System.Func<double, DirectorOffscreenCameraPose?>? evaluator,
        out string error)
    {
        sourceName = string.Empty;
        evaluator = null;
        error = Lang.Get("vsdirector:live-offscreen-source-missing");
        if (library is null)
        {
            return false;
        }

        switch (source.Type)
        {
            case DirectorRecordingSourceType.Program:
                if (controller is null
                    || !controller.TryCaptureCurrentView(
                        Lang.Get("vsdirector:escape-offscreen-current-view"),
                        out CameraPreset currentView))
                {
                    return false;
                }
                sourceName = Lang.Get("vsdirector:escape-offscreen-current-view");
                DirectorOffscreenCameraPose currentPose = OffscreenPose(currentView);
                evaluator = _ => currentPose;
                return true;

            case DirectorRecordingSourceType.StaticCamera:
                if (library.Find(source.Id)?.DeepClone() is not CameraPreset camera)
                {
                    return false;
                }
                sourceName = camera.Name;
                DirectorOffscreenCameraPose staticPose = OffscreenPose(camera);
                evaluator = _ => staticPose;
                return true;

            case DirectorRecordingSourceType.Path:
                if (library.FindPath(source.Id)?.DeepClone() is not CameraPath path
                    || path.Keyframes.Count == 0)
                {
                    return false;
                }
                sourceName = path.Name;
                evaluator = elapsed =>
                {
                    CameraPathTime time = CameraPathSampler.ResolvePlaybackTime(
                        path.DurationSeconds,
                        elapsed * CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed),
                        path.LoopMode,
                        reverse: false);
                    return CameraPathSampler.TryEvaluate(path, time.TimeSeconds, out DirectorKeyframe frame)
                        ? OffscreenPose(frame)
                        : null;
                };
                return true;

            case DirectorRecordingSourceType.Sequence:
                if (library.FindSequence(source.Id)?.DeepClone() is not CameraSequence sequence
                    || sequence.Shots.Count == 0)
                {
                    return false;
                }
                Dictionary<Guid, CameraPreset> sequenceCameras = library.Ordered
                    .Select(item => item.DeepClone())
                    .ToDictionary(item => item.Id);
                sourceName = sequence.Name;
                evaluator = elapsed =>
                {
                    if (!CameraSequenceSampler.TryResolve(sequence, elapsed, out CameraSequenceTime time)
                        || time.ShotIndex < 0
                        || time.ShotIndex >= sequence.Shots.Count
                        || !sequenceCameras.TryGetValue(
                            sequence.Shots[time.ShotIndex].CameraId,
                            out CameraPreset? selected))
                    {
                        return null;
                    }
                    return OffscreenPose(selected);
                };
                return true;

            case DirectorRecordingSourceType.Follow:
                if (library.FindFollowRig(source.Id)?.DeepClone() is not CameraFollowRig follow)
                {
                    return false;
                }
                sourceName = follow.Name;
                WorkflowMultiViewFollowState followState = new(follow.Id);
                evaluator = elapsed => ResolveLiveOffscreenFollowPose(
                    follow,
                    followState,
                    elapsed);
                return true;

            case DirectorRecordingSourceType.Workflow:
                if (library.FindWorkflow(source.Id)?.DeepClone() is not DirectorWorkflow workflow)
                {
                    return false;
                }
                return TryCreateLiveWorkflowPoseEvaluator(
                    workflow,
                    out sourceName,
                    out evaluator,
                    out error);

            default:
                return false;
        }
    }

    private bool TryCreateLiveWorkflowPoseEvaluator(
        DirectorWorkflow workflow,
        out string sourceName,
        out System.Func<double, DirectorOffscreenCameraPose?>? evaluator,
        out string error)
    {
        sourceName = workflow.Name;
        evaluator = null;
        error = Lang.Get("vsdirector:live-offscreen-workflow-empty", workflow.Name);
        if (library is null)
        {
            return false;
        }

        Dictionary<Guid, CameraPreset> cameras = library.Ordered
            .Select(item => item.DeepClone())
            .ToDictionary(item => item.Id);
        Dictionary<Guid, CameraPath> paths = library.OrderedPaths
            .Select(item => item.DeepClone())
            .ToDictionary(item => item.Id);
        Dictionary<Guid, CameraSequence> sequences = library.OrderedSequences
            .Select(item => item.DeepClone())
            .ToDictionary(item => item.Id);
        Dictionary<Guid, CameraFollowRig> follows = library.OrderedFollowRigs
            .Select(item => item.DeepClone())
            .ToDictionary(item => item.Id);
        bool SourceExists(DirectorRecordingSourceType type, Guid id)
            => type switch
            {
                DirectorRecordingSourceType.StaticCamera => cameras.ContainsKey(id),
                DirectorRecordingSourceType.Path => paths.TryGetValue(id, out CameraPath? path)
                    && path.Keyframes.Count >= 2,
                DirectorRecordingSourceType.Sequence => sequences.TryGetValue(id, out CameraSequence? sequence)
                    && sequence.Shots.Count > 0,
                DirectorRecordingSourceType.Follow => follows.ContainsKey(id),
                _ => false
            };
        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(
            workflow,
            card => SourceExists(card.SourceType, card.SourceId));
        if (passes.Count == 0)
        {
            return false;
        }

        workflowMultiViewFollowStates.Clear();
        DirectorWorkflowMultiViewScheduler scheduler = new(
            id => cameras.TryGetValue(id, out CameraPreset? camera) ? camera : null,
            id => paths.TryGetValue(id, out CameraPath? path) ? path : null,
            id => sequences.TryGetValue(id, out CameraSequence? sequence) ? sequence : null,
            id => follows.TryGetValue(id, out CameraFollowRig? follow) ? follow : null,
            ResolveWorkflowMultiViewFollowPose);
        if (!scheduler.Prepare(
                workflow,
                passes,
                new DirectorWorkflowMultiViewSettings
                {
                    Width = config.OffscreenWidth,
                    Height = config.OffscreenHeight,
                    RenderTerrain = true,
                    RenderEntities = true,
                    RenderLocalPlayer = true,
                    RenderOpaqueParticles = true
                },
                out IReadOnlyList<DirectorWorkflowMultiViewError> failures))
        {
            error = failures.Count == 0
                ? error
                : Lang.Get("vsdirector:live-offscreen-workflow-invalid", failures[0].Detail);
            return false;
        }

        DirectorOffscreenCameraPose? lastPose = null;
        evaluator = elapsed =>
        {
            double timeline = Math.Clamp(
                scheduler.StartSeconds + Math.Max(0d, elapsed),
                scheduler.StartSeconds,
                scheduler.EndSeconds);
            DirectorWorkflowMultiViewFrame frame = scheduler.EvaluateAt(timeline);
            DirectorWorkflowViewPlan? view = frame.Views
                .OrderBy(candidate => candidate.PassStartSeconds)
                .ThenBy(candidate => candidate.PassName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (view is not null)
            {
                DirectorWorkflowCameraPose pose = view.Pose;
                lastPose = new DirectorOffscreenCameraPose(
                    pose.X,
                    pose.Y,
                    pose.Z,
                    pose.Dimension,
                    pose.Yaw,
                    pose.Pitch,
                    pose.Roll,
                    pose.Fov);
            }
            return lastPose;
        };
        return true;
    }

    private DirectorOffscreenCameraPose? ResolveLiveOffscreenFollowPose(
        CameraFollowRig rig,
        WorkflowMultiViewFollowState state,
        double elapsedSeconds)
    {
        Entity? target = ResolveFollowTarget(rig);
        if (target is null || !TryCreateWorkflowFollowTargetPose(
                rig,
                target,
                out CameraFollowTargetPose targetPose))
        {
            return null;
        }

        double elapsed = Math.Max(0d, elapsedSeconds);
        CameraFollowPose desired = CameraFollowMath.Evaluate(
            rig,
            targetPose,
            elapsed * rig.OrbitSpeedDegreesPerSecond);
        bool reset = !state.HasPose
            || elapsed < state.TimelineSeconds - DirectorWorkflowPlanner.TimeEpsilon
            || state.Pose.Dimension != desired.Dimension;
        if (reset)
        {
            state.History.Clear();
            state.HasPose = false;
        }

        CameraFollowPose pose = desired;
        if (state.HasPose)
        {
            double deltaTime = Math.Max(0d, elapsed - state.TimelineSeconds);
            pose = rig.MotionMode switch
            {
                CameraFollowMotionMode.Smooth => CameraFollowMath.Smooth(
                    state.Pose,
                    desired,
                    deltaTime,
                    rig.SmoothTimeSeconds),
                CameraFollowMotionMode.Delayed => ResolveWorkflowMultiViewDelayedPose(
                    state,
                    rig,
                    elapsed,
                    desired),
                _ => desired
            };
        }
        else if (rig.MotionMode == CameraFollowMotionMode.Delayed)
        {
            state.History.Add(elapsed, desired, rig.DelaySeconds);
        }

        state.TimelineSeconds = elapsed;
        state.SourceTimeSeconds = elapsed;
        state.Pose = pose;
        state.HasPose = true;
        return new DirectorOffscreenCameraPose(
            pose.X,
            pose.Y,
            pose.Z,
            pose.Dimension,
            (float)pose.YawDegrees,
            (float)pose.PitchDegrees,
            (float)pose.RollDegrees,
            (float)pose.Fov);
    }

    private static DirectorOffscreenCameraPose OffscreenPose(CameraPreset camera)
        => new(
            camera.X,
            camera.Y,
            camera.Z,
            camera.Dimension,
            camera.Yaw,
            camera.Pitch,
            camera.Roll,
            camera.Fov);

    private static DirectorOffscreenCameraPose OffscreenPose(DirectorKeyframe frame)
        => new(
            frame.X,
            frame.Y,
            frame.Z,
            frame.Dimension,
            frame.Yaw,
            frame.Pitch,
            frame.Roll,
            frame.Fov);

    private void EnqueueClientTask(Action action, string code)
        => capi?.Event.EnqueueMainThreadTask(action, code);

    internal bool TrySetOffscreenEnabled(bool enabled, out string message)
    {
        if (ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        if (!enabled && OffscreenCaptureBusy)
        {
            _ = StopOffscreenCaptureInternal(showMessage: false, out _);
        }
        config.EnableExperimentalOffscreenMultiView = enabled;
        if (!SaveConfig())
        {
            message = Lang.Get("vsdirector:config-save-failed");
            Show(message);
            return false;
        }

        message = Lang.Get(
            "vsdirector:offscreen-enabled-set",
            enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));
        Show(message);
        return true;
    }

    internal bool TrySetOffscreenSize(int width, int height, out string message)
    {
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }
        if (width is < 16 or > DirectorOffscreenMultiViewRenderer.MaximumDimension
            || height is < 16 or > DirectorOffscreenMultiViewRenderer.MaximumDimension
            || width % 2 != 0
            || height % 2 != 0)
        {
            message = Lang.Get("vsdirector:offscreen-invalid-size");
            Show(message);
            return false;
        }

        config.OffscreenWidth = width;
        config.OffscreenHeight = height;
        SaveConfig();
        message = Lang.Get("vsdirector:offscreen-size-set", width, height);
        Show(message);
        return true;
    }

    internal bool TrySetOffscreenFrameRate(int framesPerSecond, out string message)
    {
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }

        config.OffscreenFrameRate = Math.Clamp(framesPerSecond, 1, 240);
        SaveConfig();
        message = Lang.Get("vsdirector:offscreen-fps-set", config.OffscreenFrameRate);
        Show(message);
        return true;
    }

    internal bool TrySetOffscreenEncoder(string value, out string message)
    {
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }

        config.OffscreenEncoder = value.Trim().ToLowerInvariant() switch
        {
            "nvenc" => DirectorOffscreenEncoder.NvidiaNvenc,
            "qsv" => DirectorOffscreenEncoder.IntelQuickSync,
            "amf" => DirectorOffscreenEncoder.AmdAmf,
            "x264" => DirectorOffscreenEncoder.Libx264,
            _ => DirectorOffscreenEncoder.Auto
        };
        SaveConfig();
        message = Lang.Get("vsdirector:offscreen-encoder-set", config.OffscreenEncoder);
        Show(message);
        return true;
    }

    internal bool TrySetOffscreenOutputDirectory(string path, out string message)
    {
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }

        try
        {
            string normalized = path.Trim();
            config.OffscreenOutputDirectory = normalized.Length == 0
                || string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : Path.GetFullPath(normalized);
            SaveConfig();
            message = Lang.Get(
                "vsdirector:offscreen-output-set",
                config.OffscreenOutputDirectory.Length == 0
                    ? Lang.Get("vsdirector:config-default")
                    : config.OffscreenOutputDirectory);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:offscreen-output-invalid", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TryStartWorkflowMultiViewRecording(string selector, out string message)
    {
        DirectorWorkflow? workflow = FindWorkflow(selector);
        return workflow is not null
            ? TryStartWorkflowMultiViewRecording(workflow.Id, out message)
            : FailWorkflow(selector, out message);
    }

    internal bool TryStartWorkflowMultiViewRecording(Guid workflowId, out string message)
        => TryStartWorkflowMultiViewRecording(
            workflowId,
            allowWorkflowReplay: false,
            out message);

    private bool TryStartWorkflowMultiViewRecording(
        Guid workflowId,
        bool allowWorkflowReplay,
        out string message)
    {
        if (!EnsurePermission(DirectorPermission.OffscreenSavedSources, out message)
            || !EnsurePermission(DirectorPermission.Workflows, out message))
        {
            Show(message);
            return false;
        }
        WorkflowReplayRecordingSession? replaySession = allowWorkflowReplay
            ? workflowReplayRecording
            : null;
        bool replayDriven = replaySession is
            {
                Backend: not WorkflowReplayRecordingBackend.Obs,
                StartupRequested: true
            }
            && replaySession.WorkflowId == workflowId
            && replaySession.ReplayId == replay?.ActiveReplayId
            && replay?.IsPlaying == true
            && replay?.IsLoading == false;
        bool actionDriven = false;
        if (allowWorkflowReplay && !replayDriven)
        {
            message = Lang.Get("vsdirector:offscreen-conflict");
            Show(message);
            return false;
        }
        if (!replayDriven && !actionDriven && ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        if (!replayDriven && !actionDriven && !config.EnableExperimentalOffscreenMultiView)
        {
            message = Lang.Get("vsdirector:offscreen-disabled");
            Show(message);
            return false;
        }
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }
        if (capi is null || library is null || controller is null)
        {
            message = Lang.Get("vsdirector:offscreen-unavailable");
            Show(message);
            return false;
        }
        if (offscreenRenderer is null
            && !EnsureOffscreenRenderer(out string rendererError))
        {
            message = Lang.Get("vsdirector:offscreen-unavailable-detail", rendererError);
            Show(message);
            return false;
        }
        if (replay?.IsRecording == true && !automaticReplayRecording)
        {
            message = Lang.Get("vsdirector:replay-playback-active");
            Show(message);
            return false;
        }
        bool queued = false;
        string handoffError = string.Empty;
        if (!replayDriven && !QueueAfterAutomaticReplaySave(
                () => TryStartWorkflowMultiViewRecording(
                    workflowId,
                    allowWorkflowReplay,
                    out _),
                out queued,
                out handoffError))
        {
            message = ReplayErrorText(handoffError, workflowId.ToString("D"));
            Show(message);
            return false;
        }
        if (!replayDriven && queued)
        {
            message = Lang.Get("vsdirector:auto-replay-handoff-saving");
            Show(message);
            return true;
        }
        if (recording?.IsActive == true
            || replay?.IsExporting == true
            || (!replayDriven && (replay?.IsPlaying == true || replay?.IsLoading == true)))
        {
            message = Lang.Get("vsdirector:offscreen-conflict");
            Show(message);
            return false;
        }

        DirectorWorkflow? workflow = library.FindWorkflow(workflowId);
        if (workflow is null)
        {
            message = Lang.Get("vsdirector:workflow-not-found", workflowId);
            Show(message);
            return false;
        }
        if (!ValidateWorkflowMultiViewSources(workflow, out string sourceError))
        {
            message = sourceError;
            Show(message);
            return false;
        }

        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(workflow);
        if (passes.Count == 0)
        {
            message = Lang.Get("vsdirector:workflow-empty");
            Show(message);
            return false;
        }
        if (passes.Count > DirectorOffscreenMultiViewRenderer.MaximumViews)
        {
            message = Lang.Get(
                "vsdirector:workflow-multiview-too-many-passes",
                passes.Count,
                DirectorOffscreenMultiViewRenderer.MaximumViews);
            Show(message);
            return false;
        }
        if (!DirectorOffscreenResourceBudget.FitsDefaultBudget(
                passes.Count,
                config.OffscreenWidth,
                config.OffscreenHeight,
                DirectorOffscreenFfmpegOptions.DefaultPboRingSize,
                out long estimatedGpuBytes))
        {
            message = Lang.Get(
                "vsdirector:offscreen-memory-budget",
                estimatedGpuBytes / (1024d * 1024d * 1024d),
                DirectorOffscreenResourceBudget.MaximumEstimatedGpuBytes / (1024d * 1024d * 1024d));
            Show(message);
            return false;
        }

        Dictionary<Guid, CameraPreset> cameraSnapshot = Cameras.ToDictionary(camera => camera.Id);
        Dictionary<Guid, CameraPath> pathSnapshot = Paths.ToDictionary(path => path.Id);
        Dictionary<Guid, CameraSequence> sequenceSnapshot = Sequences.ToDictionary(sequence => sequence.Id);
        Dictionary<Guid, CameraFollowRig> followSnapshot = FollowRigs.ToDictionary(rig => rig.Id);
        DirectorWorkflowMultiViewScheduler scheduler = new(
            id => cameraSnapshot.TryGetValue(id, out CameraPreset? camera) ? camera.DeepClone() : null,
            id => pathSnapshot.TryGetValue(id, out CameraPath? path) ? path.DeepClone() : null,
            id => sequenceSnapshot.TryGetValue(id, out CameraSequence? sequence) ? sequence.DeepClone() : null,
            id => followSnapshot.TryGetValue(id, out CameraFollowRig? rig) ? rig.DeepClone() : null,
            ResolveWorkflowMultiViewFollowPose);
        if (!scheduler.Prepare(
                workflow,
                passes,
                new DirectorWorkflowMultiViewSettings
                {
                    Width = config.OffscreenWidth,
                    Height = config.OffscreenHeight,
                    RenderTerrain = true,
                    RenderEntities = true,
                    RenderLocalPlayer = true,
                    RenderOpaqueParticles = true
                },
                out IReadOnlyList<DirectorWorkflowMultiViewError> prepareErrors))
        {
            string detail = string.Join(", ", prepareErrors.Select(error => $"{error.Code}: {error.Detail}"));
            message = Lang.Get("vsdirector:workflow-multiview-plan-failed", detail);
            Show(message);
            return false;
        }

        string runDirectory = Path.Combine(
            ResolveOffscreenOutputDirectory(),
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff_WorkflowMultiView", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N")[..8]);
        List<DirectorOffscreenFfmpegSink> createdSinks = new(passes.Count);
        DirectorWorkflowMultiViewManifest manifest = CreateWorkflowMultiViewManifest(
            workflow,
            passes,
            scheduler,
            runDirectory);
        try
        {
            foreach (DirectorWorkflowMultiViewManifestPass pass in manifest.Passes)
            {
                createdSinks.Add(CreateOffscreenSink(
                    pass.ViewId,
                    Path.Combine(runDirectory, pass.FileName)));
            }
        }
        catch (Exception exception)
        {
            foreach (DirectorOffscreenFfmpegSink sink in createdSinks)
            {
                sink.Dispose();
            }
            message = Lang.Get("vsdirector:offscreen-start-failed", exception.GetBaseException().Message);
            Show(message);
            return false;
        }

        DirectorWorkflowMultiViewCapture capture = new(
            scheduler,
            offscreenRenderer!,
            PrepareWorkflowMultiViewLocalActor,
            OnWorkflowMultiViewTerminal);
        long operationId = ++offscreenOperationId;
        if (replayDriven && replaySession is not null)
        {
            replaySession.OffscreenOperationId = operationId;
        }
        offscreenCaptureStarting = true;
        offscreenCaptureStopping = false;
        offscreenOperationDirectory = runDirectory;
        offscreenOperationViewCount = createdSinks.Count;
        offscreenLastResult = string.Empty;
        offscreenOperationSinks.Clear();
        offscreenOperationSinks.AddRange(createdSinks);
        workflowMultiViewCapture = capture;
        workflowMultiViewManifest = manifest;
        workflowMultiViewManifestPath = Path.Combine(runDirectory, "workflow-multiview.manifest.json");
        workflowMultiViewFinalStatus = string.Empty;
        offscreenStartCancellation?.Dispose();
        offscreenStartCancellation = new CancellationTokenSource();
        CancellationToken token = offscreenStartCancellation.Token;
        ICoreClientAPI api = capi;
        offscreenOperationTask = Task.Run(() =>
        {
            Exception? failure = null;
            try
            {
                Directory.CreateDirectory(runDirectory);
                StartOffscreenSinks(createdSinks, token);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            api.Event.EnqueueMainThreadTask(
                () => CompleteOffscreenStart(
                    operationId,
                    Array.Empty<DirectorOffscreenViewRequest>(),
                    createdSinks,
                    runDirectory,
                    failure),
                "vsdirector-workflow-multiview-start-complete");
        });

        message = Lang.Get(
            "vsdirector:workflow-multiview-starting",
            workflow.Name,
            createdSinks.Count,
            runDirectory);
        Show(message);
        return true;
    }

    private bool ValidateWorkflowMultiViewSources(
        DirectorWorkflow workflow,
        out string message)
    {
        foreach (DirectorWorkflowCard card in (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null && card.Enabled))
        {
            bool validTime = double.IsFinite(card.StartSeconds)
                && double.IsFinite(card.EndSeconds)
                && card.StartSeconds >= 0d
                && card.EndSeconds <= DirectorWorkflowPlanner.MaxTimelineSeconds
                && card.EndSeconds - card.StartSeconds
                    >= DirectorWorkflowPlanner.MinCardDurationSeconds - DirectorWorkflowPlanner.TimeEpsilon;
            if (!validTime)
            {
                message = Lang.Get("vsdirector:workflow-card-invalid", card.Name);
                return false;
            }
            if (card.Kind == DirectorWorkflowCardKind.Countdown)
            {
                continue;
            }
            if (!DirectorWorkflowPlanner.IsExportSource(card.SourceType))
            {
                message = Lang.Get("vsdirector:workflow-card-invalid", card.Name);
                return false;
            }

            bool exists = WorkflowSourceExists(card.SourceType, card.SourceId);
            if (exists && card.SourceType == DirectorRecordingSourceType.Sequence)
            {
                CameraSequence? sequence = library?.FindSequence(card.SourceId);
                exists = sequence is not null
                    && sequence.Shots.All(shot => library?.Find(shot.CameraId) is not null);
            }
            if (!exists)
            {
                message = Lang.Get(
                    "vsdirector:workflow-multiview-source-missing",
                    card.Name,
                    card.SourceType,
                    card.SourceId);
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    internal bool TryStartOffscreenCapture(string cameraSelectors, out string message)
    {
        if (!EnsurePermission(DirectorPermission.OffscreenSavedSources, out message))
        {
            Show(message);
            return false;
        }
        if (ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        if (!config.EnableExperimentalOffscreenMultiView)
        {
            message = Lang.Get("vsdirector:offscreen-disabled");
            Show(message);
            return false;
        }
        if (OffscreenCaptureBusy)
        {
            message = Lang.Get("vsdirector:offscreen-busy");
            Show(message);
            return false;
        }
        if (capi is null || library is null)
        {
            message = Lang.Get("vsdirector:offscreen-unavailable");
            Show(message);
            return false;
        }
        if (offscreenRenderer is null
            && !EnsureOffscreenRenderer(out string rendererError))
        {
            message = Lang.Get("vsdirector:offscreen-unavailable-detail", rendererError);
            Show(message);
            return false;
        }
        if (recording?.IsActive == true || replay?.IsExporting == true || replay?.IsPlaying == true)
        {
            message = Lang.Get("vsdirector:offscreen-conflict");
            Show(message);
            return false;
        }

        List<CameraPreset> selected = new();
        if (string.Equals(cameraSelectors.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            selected.AddRange(Cameras.Select(camera => camera.DeepClone()));
        }
        else
        {
            foreach (string selector in cameraSelectors.Split(
                new[] { ',', ';' },
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                CameraPreset? camera = FindCamera(selector);
                if (camera is null)
                {
                    message = Lang.Get("vsdirector:camera-not-found", selector);
                    Show(message);
                    return false;
                }
                if (selected.All(existing => existing.Id != camera.Id))
                {
                    selected.Add(camera.DeepClone());
                }
            }
        }
        if (selected.Count == 0)
        {
            message = Lang.Get("vsdirector:offscreen-no-cameras");
            Show(message);
            return false;
        }
        if (selected.Count > DirectorOffscreenMultiViewRenderer.MaximumViews)
        {
            message = Lang.Get(
                "vsdirector:offscreen-too-many-views",
                DirectorOffscreenMultiViewRenderer.MaximumViews);
            Show(message);
            return false;
        }

        if (!DirectorOffscreenResourceBudget.FitsDefaultBudget(
                selected.Count,
                config.OffscreenWidth,
                config.OffscreenHeight,
                DirectorOffscreenFfmpegOptions.DefaultPboRingSize,
                out long estimatedGpuBytes))
        {
            message = Lang.Get(
                "vsdirector:offscreen-memory-budget",
                estimatedGpuBytes / (1024d * 1024d * 1024d),
                DirectorOffscreenResourceBudget.MaximumEstimatedGpuBytes / (1024d * 1024d * 1024d));
            Show(message);
            return false;
        }

        string runDirectory = Path.Combine(
            ResolveOffscreenOutputDirectory(),
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff_MultiView", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N")[..8]);
        List<DirectorOffscreenViewRequest> requests = new(selected.Count);
        List<DirectorOffscreenFfmpegSink> createdSinks = new(selected.Count);
        try
        {
            for (int index = 0; index < selected.Count; index++)
            {
                CameraPreset camera = selected[index];
                string fileName = $"{index + 1:00}_{DirectorRecordingFiles.SanitizeFilePart(camera.Name, "camera")}_{camera.Id:N}.mp4";
                string outputPath = Path.Combine(runDirectory, fileName);
                DirectorOffscreenFfmpegSink sink = new(capi, new DirectorOffscreenFfmpegOptions
                {
                    ViewId = camera.Id,
                    OutputPath = outputPath,
                    Width = config.OffscreenWidth,
                    Height = config.OffscreenHeight,
                    FramesPerSecond = config.OffscreenFrameRate,
                    FfmpegPath = config.FfmpegPath,
                    Encoder = config.OffscreenEncoder switch
                    {
                        DirectorOffscreenEncoder.NvidiaNvenc => DirectorOffscreenVideoEncoder.NvidiaNvenc,
                        DirectorOffscreenEncoder.IntelQuickSync => DirectorOffscreenVideoEncoder.IntelQuickSync,
                        DirectorOffscreenEncoder.AmdAmf => DirectorOffscreenVideoEncoder.AmdAmf,
                        DirectorOffscreenEncoder.Libx264 => DirectorOffscreenVideoEncoder.Libx264,
                        _ => DirectorOffscreenVideoEncoder.Auto
                    },
                    AllowSoftwareFallback = true,
                    VideoQuality = config.OffscreenVideoQuality,
                    VideoBitrateKbps = config.OffscreenVideoBitrateKbps,
                    SoftwarePreset = config.FfmpegVideoPreset
                });
                createdSinks.Add(sink);
                requests.Add(new DirectorOffscreenViewRequest
                {
                    ViewId = camera.Id,
                    Name = camera.Name,
                    Width = config.OffscreenWidth,
                    Height = config.OffscreenHeight,
                    Pose = new DirectorOffscreenCameraPose(
                        camera.X,
                        camera.Y,
                        camera.Z,
                        camera.Dimension,
                        camera.Yaw,
                        camera.Pitch,
                        camera.Roll,
                        camera.Fov),
                    RenderTerrain = true,
                    RenderEntities = true,
                    RenderLocalPlayer = false,
                    RenderOpaqueParticles = true
                });
            }
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:offscreen-start-failed", exception.GetBaseException().Message);
            Show(message);
            return false;
        }

        long operationId = ++offscreenOperationId;
        offscreenCaptureStarting = true;
        offscreenCaptureStopping = false;
        offscreenOperationDirectory = runDirectory;
        offscreenOperationViewCount = createdSinks.Count;
        offscreenLastResult = string.Empty;
        offscreenOperationSinks.Clear();
        offscreenOperationSinks.AddRange(createdSinks);
        offscreenStartCancellation?.Dispose();
        offscreenStartCancellation = new CancellationTokenSource();
        CancellationToken token = offscreenStartCancellation.Token;
        ICoreClientAPI api = capi;
        offscreenOperationTask = Task.Run(() =>
        {
            Exception? failure = null;
            try
            {
                Directory.CreateDirectory(runDirectory);
                StartOffscreenSinks(createdSinks, token);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            api.Event.EnqueueMainThreadTask(
                () => CompleteOffscreenStart(
                    operationId,
                    requests,
                    createdSinks,
                    runDirectory,
                    failure),
                "vsdirector-offscreen-start-complete");
        });

        message = Lang.Get("vsdirector:offscreen-starting", createdSinks.Count, runDirectory);
        Show(message);
        return true;
    }

    internal bool TryStopOffscreenCapture(out string message)
        => StopOffscreenCaptureInternal(showMessage: true, out message);

    private bool StopOffscreenCaptureInternal(
        bool showMessage,
        out string message,
        string terminalMessage = "")
    {
        Guid? actionWorkflowId = actionWorkflowVideoRecordingId;
        actionWorkflowVideoRecordingId = null;
        if (actionWorkflowId is Guid actionId && liveActionWorkflowPlayback?.Workflow.Id == actionId)
        {
            StopLiveActionWorkflow();
        }
        WorkflowReplayRecordingSession? offlineSession = workflowReplayRecording is
            {
                Backend: not WorkflowReplayRecordingBackend.Obs
            } session
            && session.OffscreenOperationId == offscreenOperationId
                ? session
                : null;
        if (offlineSession is not null)
        {
            replay?.SetPaused(true);
        }
        PrepareWorkflowMultiViewStop();
        if (offscreenCaptureStarting)
        {
            offscreenCaptureStarting = false;
            offscreenCaptureStopping = true;
            offscreenStartCancellation?.Cancel();
            message = Lang.Get("vsdirector:offscreen-stopping", offscreenOperationViewCount);
            if (showMessage)
            {
                Show(message);
            }
            return true;
        }
        if (offscreenCaptureStopping)
        {
            message = Lang.Get("vsdirector:offscreen-stopping", offscreenOperationViewCount);
            if (showMessage)
            {
                Show(message);
            }
            return true;
        }
        if (!offscreenCaptureActive && offscreenSinks.Count == 0)
        {
            message = Lang.Get("vsdirector:offscreen-not-active");
            if (showMessage)
            {
                Show(message);
            }
            return false;
        }

        long operationId = ++offscreenOperationId;
        if (offlineSession is not null)
        {
            offlineSession.OffscreenOperationId = operationId;
        }
        offscreenCaptureActive = false;
        offscreenCaptureStopping = true;
        offscreenOperationViewCount = offscreenSinks.Count;
        if (offscreenRenderer is not null)
        {
            offscreenRenderer.Enabled = false;
            foreach (DirectorOffscreenFfmpegSink sink in offscreenSinks)
            {
                offscreenRenderer.RemoveSink(sink);
            }
        }

        List<DirectorOffscreenFfmpegSink> stoppingSinks = new(offscreenSinks);
        offscreenSinks.Clear();
        offscreenOperationSinks.Clear();
        offscreenOperationSinks.AddRange(stoppingSinks);
        BeginOffscreenSinkStop(operationId, stoppingSinks, terminalMessage, showMessage);
        message = Lang.Get("vsdirector:offscreen-stopping", stoppingSinks.Count);
        if (showMessage)
        {
            Show(message);
        }
        return true;
    }

    private void CompleteOfflineWorkflowReplayRecording(long operationId)
    {
        if (workflowReplayRecording is not
            {
                Backend: not WorkflowReplayRecordingBackend.Obs
            } session
            || session.OffscreenOperationId != operationId)
        {
            return;
        }

        replay?.SetPaused(true);
        workflowReplayRecording = null;
    }

    private static void StartOffscreenSinks(
        IReadOnlyList<DirectorOffscreenFfmpegSink> sinks,
        CancellationToken cancellationToken)
    {
        if (sinks.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        sinks[0].Start(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Parallel.ForEach(
            sinks.Skip(1),
            new ParallelOptions { CancellationToken = cancellationToken },
            sink => sink.Start(cancellationToken));
    }

    private DirectorOffscreenFfmpegSink CreateOffscreenSink(
        Guid viewId,
        string outputPath)
    {
        ICoreClientAPI api = capi
            ?? throw new InvalidOperationException("The client API is unavailable.");
        return new DirectorOffscreenFfmpegSink(api, new DirectorOffscreenFfmpegOptions
        {
            ViewId = viewId,
            OutputPath = outputPath,
            Width = config.OffscreenWidth,
            Height = config.OffscreenHeight,
            FramesPerSecond = config.OffscreenFrameRate,
            FfmpegPath = config.FfmpegPath,
            Encoder = config.OffscreenEncoder switch
            {
                DirectorOffscreenEncoder.NvidiaNvenc => DirectorOffscreenVideoEncoder.NvidiaNvenc,
                DirectorOffscreenEncoder.IntelQuickSync => DirectorOffscreenVideoEncoder.IntelQuickSync,
                DirectorOffscreenEncoder.AmdAmf => DirectorOffscreenVideoEncoder.AmdAmf,
                DirectorOffscreenEncoder.Libx264 => DirectorOffscreenVideoEncoder.Libx264,
                _ => DirectorOffscreenVideoEncoder.Auto
            },
            AllowSoftwareFallback = true,
            VideoQuality = config.OffscreenVideoQuality,
            VideoBitrateKbps = config.OffscreenVideoBitrateKbps,
            SoftwarePreset = config.FfmpegVideoPreset
        });
    }

    private DirectorWorkflowMultiViewManifest CreateWorkflowMultiViewManifest(
        DirectorWorkflow workflow,
        IReadOnlyList<DirectorWorkflowExportPass> passes,
        DirectorWorkflowMultiViewScheduler scheduler,
        string runDirectory)
    {
        _ = runDirectory;
        DirectorOffscreenCapabilities implemented = offscreenRenderer?.Capabilities
            ?? DirectorOffscreenCapabilities.None;
        DirectorWorkflowMultiViewManifest manifest = new()
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            CreatedUtc = DateTimeOffset.UtcNow,
            TimelineStartSeconds = scheduler.StartSeconds,
            TimelineEndSeconds = scheduler.EndSeconds,
            LastTimelineSeconds = scheduler.StartSeconds,
            Width = config.OffscreenWidth,
            Height = config.OffscreenHeight,
            FramesPerSecond = config.OffscreenFrameRate,
            ReplayStartedByCapture = false,
            AdvancesWorldTicks = false,
            RenderLocalPlayer = true,
            LocalPlayerRenderMode = "detached-third-person-controller-context",
            Capabilities = Enum.GetValues<DirectorOffscreenCapabilities>()
                .Where(capability => capability != DirectorOffscreenCapabilities.None
                    && implemented.HasFlag(capability))
                .Select(capability => capability.ToString())
                .Concat(new[]
                {
                    "WorkflowStaticPathSequenceFollow",
                    "MonotonicFirstRenderedFrameClock",
                    "StablePerPassFfmpegSink",
                    "DetachedThirdPersonLocalPlayer"
                })
                .ToList(),
            Limitations = new List<string>
            {
                "ConcurrentViewsMustUseOneDimensionBecauseTheClientHasOneObservationAnchor",
                "LocalPlayerOnlyAppearsInItsPhysicalDimension",
                "ExternalWorkflowAudioIsNotMuxedIntoPerPassFiles"
            }
        };

        Dictionary<Guid, DirectorWorkflowExportPass> exports = passes.ToDictionary(pass => pass.Id);
        int index = 0;
        foreach (DirectorWorkflowMultiViewPass descriptor in scheduler.Passes)
        {
            DirectorWorkflowExportPass pass = exports[descriptor.PassId];
            const string extension = ".mp4";
            string fileName = $"{++index:00}_{DirectorRecordingFiles.SanitizeFilePart(descriptor.Name, "workflow")}_{descriptor.PassId:N}{extension}";
            manifest.Passes.Add(new DirectorWorkflowMultiViewManifestPass
            {
                PassId = descriptor.PassId,
                ViewId = descriptor.ViewId,
                Name = descriptor.Name,
                StartSeconds = descriptor.StartSeconds,
                EndSeconds = descriptor.EndSeconds,
                SplitForOverlap = descriptor.SplitForOverlap,
                FileName = fileName,
                Segments = pass.Segments.Select(segment => new DirectorWorkflowMultiViewManifestSegment
                {
                    CardId = segment.CardId,
                    CardName = segment.CardName,
                    SourceType = segment.SourceType,
                    SourceId = segment.SourceId,
                    StartSeconds = segment.StartSeconds,
                    EndSeconds = segment.EndSeconds
                }).ToList()
            });
        }
        return manifest;
    }

    private DirectorWorkflowCameraPose? ResolveWorkflowMultiViewFollowPose(
        DirectorWorkflowFollowPoseContext context)
    {
        Entity? target = ResolveFollowTarget(context.Rig);
        if (target is null || !TryCreateWorkflowFollowTargetPose(
                context.Rig,
                target,
                out CameraFollowTargetPose targetPose))
        {
            return null;
        }

        CameraFollowPose desired = CameraFollowMath.Evaluate(
            context.Rig,
            targetPose,
            context.SourceTimeSeconds * context.Rig.OrbitSpeedDegreesPerSecond);
        bool reset = !workflowMultiViewFollowStates.TryGetValue(
                context.ViewId,
                out WorkflowMultiViewFollowState? state)
            || state.RigId != context.Rig.Id
            || context.TimelineSeconds < state.TimelineSeconds - DirectorWorkflowPlanner.TimeEpsilon
            || context.SourceTimeSeconds < state.SourceTimeSeconds - DirectorWorkflowPlanner.TimeEpsilon
            || state.Pose.Dimension != desired.Dimension;
        if (reset || state is null)
        {
            state = new WorkflowMultiViewFollowState(context.Rig.Id);
            workflowMultiViewFollowStates[context.ViewId] = state;
        }

        WorkflowMultiViewFollowState activeState = state;
        CameraFollowPose pose = desired;
        if (activeState.HasPose)
        {
            double deltaTime = Math.Max(0d, context.TimelineSeconds - activeState.TimelineSeconds);
            pose = context.Rig.MotionMode switch
            {
                CameraFollowMotionMode.Smooth => CameraFollowMath.Smooth(
                    activeState.Pose,
                    desired,
                    deltaTime,
                    context.Rig.SmoothTimeSeconds),
                CameraFollowMotionMode.Delayed => ResolveWorkflowMultiViewDelayedPose(
                    activeState,
                    context.Rig,
                    context.TimelineSeconds,
                    desired),
                _ => desired
            };
        }
        else if (context.Rig.MotionMode == CameraFollowMotionMode.Delayed)
        {
            activeState.History.Add(context.TimelineSeconds, desired, context.Rig.DelaySeconds);
        }
        activeState.RigId = context.Rig.Id;
        activeState.TimelineSeconds = context.TimelineSeconds;
        activeState.SourceTimeSeconds = context.SourceTimeSeconds;
        activeState.Pose = pose;
        activeState.HasPose = true;
        return new DirectorWorkflowCameraPose(
            pose.X,
            pose.Y,
            pose.Z,
            pose.Dimension,
            (float)pose.YawDegrees,
            (float)pose.PitchDegrees,
            (float)pose.RollDegrees,
            (float)pose.Fov);
    }

    private static CameraFollowPose ResolveWorkflowMultiViewDelayedPose(
        WorkflowMultiViewFollowState state,
        CameraFollowRig rig,
        double timelineSeconds,
        CameraFollowPose desired)
    {
        state.History.Add(timelineSeconds, desired, rig.DelaySeconds);
        return state.History.TrySample(
            timelineSeconds - CameraFollowMath.NormalizeDelay(rig.DelaySeconds),
            out CameraFollowPose delayed)
                ? delayed
                : desired;
    }

    private bool TryCreateWorkflowFollowTargetPose(
        CameraFollowRig rig,
        Entity target,
        out CameraFollowTargetPose pose)
    {
        pose = default;
        if (!double.IsFinite(target.Pos.X)
            || !double.IsFinite(target.Pos.Y)
            || !double.IsFinite(target.Pos.Z))
        {
            return false;
        }

        double x;
        double y;
        double z;
        double yawRadians;
        double pitchRadians;
        if (rig.Anchor == CameraFollowAnchor.Head)
        {
            if (!DirectorEntityViewResolver.TryResolveHeadPosition(target, out x, out y, out z))
            {
                return false;
            }
            yawRadians = DirectorEntityViewResolver.ResolveCameraYaw(target);
            pitchRadians = DirectorEntityViewResolver.ResolveCameraPitch(target);
        }
        else
        {
            float centerX = target.SelectionBox is null
                ? 0f
                : (target.SelectionBox.X1 + target.SelectionBox.X2) / 2f;
            float centerY = target.SelectionBox is null
                ? 0.9f
                : (target.SelectionBox.Y1 + target.SelectionBox.Y2) / 2f;
            float centerZ = target.SelectionBox is null
                ? 0f
                : (target.SelectionBox.Z1 + target.SelectionBox.Z2) / 2f;
            x = target.Pos.X + centerX;
            y = target.Pos.Y + centerY;
            z = target.Pos.Z + centerZ;
            yawRadians = DirectorEntityViewResolver.ResolveBodyYaw(target);
            pitchRadians = 0d;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
            || !double.IsFinite(yawRadians) || !double.IsFinite(pitchRadians))
        {
            return false;
        }

        const double degreesPerRadian = 180d / Math.PI;
        pose = new CameraFollowTargetPose(
            x,
            y,
            z,
            target.Pos.Dimension,
            yawRadians * degreesPerRadian,
            pitchRadians * degreesPerRadian);
        return true;
    }

    private string? PrepareWorkflowMultiViewLocalActor(DirectorWorkflowViewPlan primary)
    {
        if (controller is null)
        {
            return "The director camera controller is unavailable.";
        }

        DirectorWorkflowCameraPose pose = primary.Pose;
        CameraPreset camera = new()
        {
            Id = primary.ViewId,
            Name = primary.PassName,
            X = pose.X,
            Y = pose.Y,
            Z = pose.Z,
            Dimension = pose.Dimension,
            Yaw = pose.Yaw,
            Pitch = pose.Pitch,
            Roll = pose.Roll,
            Fov = pose.Fov
        };
        return controller.TrySetReplayPose(camera, out string error) ? null : error;
    }

    private void OnWorkflowMultiViewTerminal(DirectorWorkflowMultiViewTerminal terminal)
    {
        if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
        {
            manifest.LastTimelineSeconds = terminal.TimelineSeconds;
            manifest.Errors.AddRange(terminal.Errors);
            workflowMultiViewFinalStatus = terminal.Completed ? "completed" : "failed";
            manifest.Status = "finalizing";
            SaveWorkflowMultiViewManifest();
        }

        string terminalMessage = terminal.Completed
            ? Lang.Get("vsdirector:workflow-multiview-completed")
            : Lang.Get(
                "vsdirector:workflow-multiview-runtime-failed",
                string.Join("; ", terminal.Errors.Select(error => $"{error.Code}: {error.Detail}")));
        Guid? actionWorkflowId = actionWorkflowVideoRecordingId;
        actionWorkflowVideoRecordingId = null;
        if (actionWorkflowId is Guid actionId && liveActionWorkflowPlayback?.Workflow.Id == actionId)
        {
            StopLiveActionWorkflow();
        }
        CompleteOfflineWorkflowReplayRecording(offscreenOperationId);
        _ = StopOffscreenCaptureInternal(
            showMessage: false,
            out _,
            terminalMessage);
    }

    private void SaveWorkflowMultiViewManifest()
    {
        if (workflowMultiViewManifest is null || workflowMultiViewManifestPath.Length == 0)
        {
            return;
        }

        try
        {
            DirectorWorkflowMultiViewManifestIO.SaveAtomic(
                workflowMultiViewManifestPath,
                workflowMultiViewManifest);
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning(
                "VS Director: failed to write workflow multi-view manifest: {0}",
                exception.Message);
        }
    }

    private void PrepareWorkflowMultiViewStop()
    {
        if (workflowMultiViewCapture is null && workflowMultiViewManifest is null)
        {
            return;
        }

        if (workflowMultiViewCapture is DirectorWorkflowMultiViewCapture capture)
        {
            if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
            {
                manifest.LastTimelineSeconds = capture.TimelineSeconds;
            }
            capture.Deactivate();
        }
        DirectorWorkflowMultiViewRenderRuntime.Driver = null;
        controller?.Deactivate();
        if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest activeManifest)
        {
            if (workflowMultiViewFinalStatus.Length == 0)
            {
                workflowMultiViewFinalStatus = "stopped";
            }
            activeManifest.Status = "finalizing";
            SaveWorkflowMultiViewManifest();
        }
    }

    private void CompleteOffscreenStart(
        long operationId,
        IReadOnlyList<DirectorOffscreenViewRequest> requests,
        IReadOnlyList<DirectorOffscreenFfmpegSink> sinks,
        string runDirectory,
        Exception? failure)
    {
        if (operationId != offscreenOperationId)
        {
            BeginDetachedOffscreenSinkStop(sinks);
            return;
        }

        offscreenOperationTask = null;
        bool canceled = offscreenCaptureStopping || offscreenStartCancellation?.IsCancellationRequested == true;
        if (failure is not null || canceled || disposed)
        {
            Guid? actionWorkflowId = actionWorkflowVideoRecordingId;
            actionWorkflowVideoRecordingId = null;
            if (actionWorkflowId is Guid actionId && liveActionWorkflowPlayback?.Workflow.Id == actionId)
            {
                StopLiveActionWorkflow();
            }
            offscreenCaptureStarting = false;
            offscreenCaptureStopping = true;
            if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
            {
                workflowMultiViewFinalStatus = failure is null || failure is OperationCanceledException
                    ? "stopped"
                    : "failed";
                manifest.Status = "finalizing";
                if (failure is not null && failure is not OperationCanceledException)
                {
                    manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                    {
                        Code = "OutputStartFailed",
                        Detail = failure.GetBaseException().Message
                    });
                }
                SaveWorkflowMultiViewManifest();
            }
            string terminalMessage = failure is null || failure is OperationCanceledException
                ? string.Empty
                : Lang.Get("vsdirector:offscreen-start-failed", failure.GetBaseException().Message);
            BeginOffscreenSinkStop(
                operationId,
                sinks,
                terminalMessage,
                showCompletion: terminalMessage.Length > 0);
            return;
        }

        try
        {
            DirectorOffscreenMultiViewRenderer renderer = offscreenRenderer
                ?? throw new InvalidOperationException("The offscreen renderer is unavailable.");
            renderer.SetViews(requests);
            foreach (DirectorOffscreenFfmpegSink sink in sinks)
            {
                renderer.AddSink(sink);
            }
            if (workflowMultiViewCapture is DirectorWorkflowMultiViewCapture workflowCapture)
            {
                if (actionWorkflowVideoRecordingId is Guid actionWorkflowId
                    && liveActionWorkflowPlayback?.Workflow.Id != actionWorkflowId
                    && !TryStartLiveActionWorkflow(actionWorkflowId, out string actionError))
                {
                    throw new InvalidOperationException(actionError);
                }
                if (!workflowCapture.Start(out DirectorWorkflowMultiViewError startError))
                {
                    throw new InvalidOperationException(
                        $"{startError.Code}: {startError.Detail}");
                }
                DirectorWorkflowMultiViewRenderRuntime.Driver = workflowCapture;
            }
            offscreenSinks.Clear();
            offscreenSinks.AddRange(sinks);
            offscreenOperationSinks.Clear();
            renderer.Enabled = true;
            offscreenCaptureStarting = false;
            offscreenCaptureActive = true;
            offscreenCaptureStartedUtc = DateTimeOffset.UtcNow;
            offscreenOperationDirectory = runDirectory;
            offscreenStartCancellation?.Dispose();
            offscreenStartCancellation = null;
            if (workflowReplayRecording is
                {
                    Backend: not WorkflowReplayRecordingBackend.Obs
                } replaySession
                && replaySession.OffscreenOperationId == operationId)
            {
                replaySession.RecordingStarted = true;
                replay?.SetPaused(false);
            }
            string message;
            if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
            {
                manifest.Status = "recording";
                manifest.StartedUtc = offscreenCaptureStartedUtc;
                for (int index = 0; index < Math.Min(manifest.Passes.Count, sinks.Count); index++)
                {
                    manifest.Passes[index].Encoder = sinks[index].SelectedEncoder.ToString();
                    manifest.Passes[index].Codec = sinks[index].SelectedCodec;
                }
                SaveWorkflowMultiViewManifest();
                message = Lang.Get(
                    "vsdirector:workflow-multiview-started",
                    manifest.WorkflowName,
                    sinks.Count,
                    config.OffscreenWidth,
                    config.OffscreenHeight,
                    config.OffscreenFrameRate,
                    string.Join(", ", sinks.Select(sink => sink.SelectedCodec)),
                    runDirectory);
            }
            else
            {
                message = Lang.Get(
                    "vsdirector:offscreen-started",
                    sinks.Count,
                    config.OffscreenWidth,
                    config.OffscreenHeight,
                    config.OffscreenFrameRate,
                    string.Join(", ", sinks.Select(sink => sink.SelectedCodec)),
                    runDirectory);
            }
            Show(message);
        }
        catch (Exception exception)
        {
            Guid? actionWorkflowId = actionWorkflowVideoRecordingId;
            actionWorkflowVideoRecordingId = null;
            if (actionWorkflowId is Guid actionId && liveActionWorkflowPlayback?.Workflow.Id == actionId)
            {
                StopLiveActionWorkflow();
            }
            workflowMultiViewCapture?.Deactivate();
            controller?.Deactivate();
            offscreenRenderer?.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
            foreach (DirectorOffscreenFfmpegSink sink in sinks)
            {
                offscreenRenderer?.RemoveSink(sink);
            }
            offscreenCaptureStarting = false;
            offscreenCaptureStopping = true;
            if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
            {
                workflowMultiViewFinalStatus = "failed";
                manifest.Status = "finalizing";
                manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                {
                    Code = "CaptureActivationFailed",
                    Detail = exception.GetBaseException().Message
                });
                SaveWorkflowMultiViewManifest();
            }
            BeginOffscreenSinkStop(
                operationId,
                sinks,
                Lang.Get("vsdirector:offscreen-start-failed", exception.GetBaseException().Message),
                showCompletion: true);
        }
    }

    private void BeginOffscreenSinkStop(
        long operationId,
        IReadOnlyList<DirectorOffscreenFfmpegSink> sinks,
        string terminalMessage,
        bool showCompletion)
    {
        ICoreClientAPI? api = capi;
        if (api is null)
        {
            return;
        }

        offscreenOperationSinks.Clear();
        offscreenOperationSinks.AddRange(sinks);
        offscreenOperationViewCount = sinks.Count;
        offscreenOperationTask = Task.Run(async () =>
        {
            OffscreenStopOutcome[] outcomes = await Task.WhenAll(sinks.Select(sink => Task.Run(
                () => StopOffscreenSink(sink)))).ConfigureAwait(false);
            api.Event.EnqueueMainThreadTask(
                () => CompleteOffscreenSinkStop(
                    operationId,
                    sinks,
                    outcomes,
                    terminalMessage,
                    showCompletion),
                "vsdirector-offscreen-stop-complete");
        });
    }

    private void BeginDetachedOffscreenSinkStop(IReadOnlyList<DirectorOffscreenFfmpegSink> sinks)
    {
        ICoreClientAPI? api = capi;
        _ = Task.Run(async () =>
        {
            _ = await Task.WhenAll(sinks.Select(sink => Task.Run(
                () => StopOffscreenSink(sink)))).ConfigureAwait(false);
            api?.Event.EnqueueMainThreadTask(
                () => CleanupOffscreenSinkGpuResources(sinks),
                "vsdirector-offscreen-stale-cleanup");
        });
    }

    private static OffscreenStopOutcome StopOffscreenSink(DirectorOffscreenFfmpegSink sink)
    {
        try
        {
            return new OffscreenStopOutcome(sink.Stop(), null);
        }
        catch (Exception exception)
        {
            return new OffscreenStopOutcome(null, exception);
        }
    }

    private void CompleteOffscreenSinkStop(
        long operationId,
        IReadOnlyList<DirectorOffscreenFfmpegSink> sinks,
        IReadOnlyList<OffscreenStopOutcome> outcomes,
        string terminalMessage,
        bool showCompletion)
    {
        CleanupOffscreenSinkGpuResources(sinks);
        if (operationId != offscreenOperationId)
        {
            return;
        }

        offscreenOperationTask = null;
        offscreenOperationSinks.Clear();
        offscreenOperationViewCount = 0;
        offscreenCaptureStarting = false;
        offscreenCaptureStopping = false;
        offscreenCaptureActive = false;
        offscreenStartCancellation?.Dispose();
        offscreenStartCancellation = null;
        try
        {
            offscreenRenderer?.ReleaseViews();
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning("VS Director: offscreen target cleanup failed: {0}", exception.Message);
        }

        DirectorOffscreenFfmpegResult[] results = outcomes
            .Where(outcome => outcome.Result is not null)
            .Select(outcome => outcome.Result!.Value)
            .ToArray();
        foreach (Exception exception in outcomes
            .Where(outcome => outcome.Error is not null)
            .Select(outcome => outcome.Error!))
        {
            capi?.Logger.Warning("VS Director: offscreen video output stop failed: {0}", exception.Message);
        }
        long frames = results.Sum(result => result.Statistics.EncodedFrames);
        long dropped = results.Sum(result => result.Statistics.DroppedFrames);
        int failed = results.Count(result => !result.Success)
            + outcomes.Count(outcome => outcome.Result is null);
        FinalizeWorkflowMultiViewManifest(outcomes);
        CompleteOfflineWorkflowReplayRecording(operationId);
        string summary = Lang.Get(
            "vsdirector:offscreen-stop-summary",
            results.Length,
            frames,
            dropped,
            failed);
        offscreenLastResult = terminalMessage.Length == 0
            ? summary
            : terminalMessage + " " + summary;
        if (showCompletion || terminalMessage.Length > 0 || failed > 0)
        {
            Show(offscreenLastResult);
        }
    }

    private void FinalizeWorkflowMultiViewManifest(
        IReadOnlyList<OffscreenStopOutcome> outcomes)
    {
        DirectorWorkflowMultiViewManifest? manifest = workflowMultiViewManifest;
        if (manifest is null)
        {
            return;
        }

        bool outputFailed = false;
        for (int index = 0; index < manifest.Passes.Count; index++)
        {
            DirectorWorkflowMultiViewManifestPass pass = manifest.Passes[index];
            if (index >= outcomes.Count)
            {
                pass.Success = false;
                pass.Error = "Output stop result is unavailable.";
                outputFailed = true;
                continue;
            }

            OffscreenStopOutcome outcome = outcomes[index];
            if (outcome.Result is DirectorOffscreenFfmpegResult result)
            {
                pass.Encoder = result.Encoder.ToString();
                pass.Codec = result.Codec;
                pass.EncodedFrames = result.Statistics.EncodedFrames;
                pass.DroppedFrames = result.Statistics.DroppedFrames;
                pass.Success = result.Success;
                pass.Error = result.Error;
                outputFailed |= !result.Success;
                if (!result.Success)
                {
                    manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                    {
                        Code = "VideoOutputFailed",
                        PassId = pass.PassId,
                        PassName = pass.Name,
                        Detail = result.Error
                    });
                }
            }
            else if (outcome.Error is Exception error)
            {
                pass.Success = false;
                pass.Error = error.GetBaseException().Message;
                outputFailed = true;
                manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                {
                    Code = "VideoOutputStopFailed",
                    PassId = pass.PassId,
                    PassName = pass.Name,
                    Detail = pass.Error
                });
            }
            else
            {
                pass.Success = false;
                pass.Error = "Output stop result is unavailable.";
                outputFailed = true;
                manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                {
                    Code = "VideoOutputStopResultMissing",
                    PassId = pass.PassId,
                    PassName = pass.Name,
                    Detail = pass.Error
                });
            }
        }

        manifest.CompletedUtc = DateTimeOffset.UtcNow;
        manifest.Status = workflowMultiViewFinalStatus == "failed"
            || outputFailed
            || manifest.Errors.Count > 0
                ? "failed"
                : workflowMultiViewFinalStatus.Length == 0
                    ? "stopped"
                    : workflowMultiViewFinalStatus;
        SaveWorkflowMultiViewManifest();
        workflowMultiViewCapture?.Deactivate();
        DirectorWorkflowMultiViewRenderRuntime.Driver = null;
        controller?.Deactivate();
        workflowMultiViewCapture = null;
        workflowMultiViewManifest = null;
        workflowMultiViewManifestPath = string.Empty;
        workflowMultiViewFinalStatus = string.Empty;
    }

    private void CleanupOffscreenSinkGpuResources(IEnumerable<DirectorOffscreenFfmpegSink> sinks)
    {
        foreach (DirectorOffscreenFfmpegSink sink in sinks)
        {
            try
            {
                sink.CompleteGpuCleanupOnRenderThread();
            }
            catch (Exception exception)
            {
                capi?.Logger.Warning("VS Director: offscreen PBO cleanup failed: {0}", exception.Message);
            }
            finally
            {
                sink.Dispose();
            }
        }
    }

    private bool EnsureOffscreenRenderer(out string error)
    {
        if (offscreenRenderer is not null)
        {
            error = string.Empty;
            return true;
        }
        if (capi is null || disposed)
        {
            error = Lang.Get("vsdirector:world-not-ready");
            return false;
        }

        try
        {
            offscreenRenderer = new DirectorOffscreenMultiViewRenderer(capi)
            {
                UseIndependentPlayerRenderer = config.OffscreenUseIndependentPlayerRenderer
            };
            error = string.Empty;
            capi.Logger.Notification("VS Director: offscreen multi-view renderer initialized.");
            return true;
        }
        catch (Exception exception)
        {
            offscreenRenderer?.Dispose();
            offscreenRenderer = null;
            error = exception.GetBaseException().Message;
            capi.Logger.Error(
                "VS Director: failed to initialize offscreen multi-view renderer: {0}",
                exception);
            return false;
        }
    }

    private void DisposeOffscreenRenderer()
    {
        try
        {
            offscreenRenderer?.Dispose();
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning(
                "VS Director: failed to dispose offscreen multi-view renderer: {0}",
                exception.Message);
        }
        finally
        {
            offscreenRenderer = null;
        }
    }

    private void ForceStopOffscreenCaptureForShutdown()
    {
        if (!OffscreenCaptureBusy
            && offscreenSinks.Count == 0
            && offscreenOperationSinks.Count == 0)
        {
            return;
        }

        PrepareWorkflowMultiViewStop();
        ++offscreenOperationId;
        offscreenStartCancellation?.Cancel();
        Task? activeOperation = offscreenOperationTask;
        try
        {
            _ = activeOperation?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning(
                "VS Director: offscreen background operation did not settle during shutdown: {0}",
                exception.GetBaseException().Message);
        }

        List<DirectorOffscreenFfmpegSink> sinks = offscreenSinks
            .Concat(offscreenOperationSinks)
            .Distinct()
            .ToList();
        offscreenRenderer?.Enabled = false;
        foreach (DirectorOffscreenFfmpegSink sink in sinks)
        {
            offscreenRenderer?.RemoveSink(sink);
        }

        Task<OffscreenStopOutcome[]> stopTask = Task.WhenAll(sinks.Select(sink => Task.Run(
            () => StopOffscreenSink(sink))));
        bool stopped = false;
        try
        {
            stopped = stopTask.Wait(TimeSpan.FromSeconds(20));
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning(
                "VS Director: offscreen outputs did not stop cleanly during shutdown: {0}",
                exception.GetBaseException().Message);
        }
        if (stopped)
        {
            CleanupOffscreenSinkGpuResources(sinks);
            FinalizeWorkflowMultiViewManifest(stopTask.Result);
        }
        else if (sinks.Count > 0)
        {
            capi?.Logger.Warning(
                "VS Director: timed out stopping {0} offscreen output(s); GL cleanup is deferred to context teardown.",
                sinks.Count);
            if (workflowMultiViewManifest is DirectorWorkflowMultiViewManifest manifest)
            {
                workflowMultiViewFinalStatus = "failed";
                manifest.Status = "failed";
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Errors.Add(new DirectorWorkflowMultiViewManifestError
                {
                    Code = "ShutdownTimeout",
                    Detail = "Offscreen outputs did not stop before the shutdown timeout."
                });
                SaveWorkflowMultiViewManifest();
            }
        }

        try
        {
            offscreenRenderer?.ReleaseViews();
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning("VS Director: offscreen target cleanup failed: {0}", exception.Message);
        }

        offscreenSinks.Clear();
        offscreenOperationSinks.Clear();
        offscreenCaptureActive = false;
        offscreenCaptureStarting = false;
        offscreenCaptureStopping = false;
        offscreenOperationTask = null;
        offscreenOperationViewCount = 0;
        offscreenStartCancellation?.Dispose();
        offscreenStartCancellation = null;
    }

    internal string OffscreenStatusText()
    {
        if (offscreenCaptureStarting)
        {
            return Lang.Get(
                "vsdirector:offscreen-status-starting",
                offscreenOperationViewCount,
                offscreenOperationDirectory);
        }
        if (offscreenCaptureStopping)
        {
            return Lang.Get("vsdirector:offscreen-status-stopping", offscreenOperationViewCount);
        }
        if (!offscreenCaptureActive)
        {
            return offscreenLastResult.Length == 0
                ? Lang.Get(
                    "vsdirector:offscreen-status-idle",
                    config.EnableExperimentalOffscreenMultiView
                        ? Lang.Get("vsdirector:on")
                        : Lang.Get("vsdirector:off"),
                    config.OffscreenWidth,
                    config.OffscreenHeight,
                    config.OffscreenFrameRate,
                    config.OffscreenEncoder,
                    ResolveOffscreenOutputDirectory())
                : offscreenLastResult;
        }

        long frames = offscreenSinks.Sum(sink => sink.Statistics.EncodedFrames);
        long dropped = offscreenSinks.Sum(sink => sink.Statistics.DroppedFrames);
        double elapsed = Math.Max(0d, (DateTimeOffset.UtcNow - offscreenCaptureStartedUtc).TotalSeconds);
        return Lang.Get(
            "vsdirector:offscreen-status-active",
            offscreenSinks.Count,
            elapsed.ToString("0.0", CultureInfo.InvariantCulture),
            frames,
            dropped,
            string.Join(", ", offscreenSinks.Select(sink => sink.SelectedCodec)));
    }

    private bool OffscreenCaptureBusy
        => offscreenCaptureActive || offscreenCaptureStarting || offscreenCaptureStopping;

    private string ResolveOffscreenOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(config.OffscreenOutputDirectory))
        {
            return Path.GetFullPath(config.OffscreenOutputDirectory);
        }
        if (!string.IsNullOrWhiteSpace(config.RecordingOutputDirectory))
        {
            return Path.GetFullPath(Path.Combine(config.RecordingOutputDirectory, "OffscreenMultiView"));
        }
        return Path.GetFullPath(Path.Combine(GamePaths.Videos, "VSDirector", "OffscreenMultiView"));
    }

    internal DirectorRecordingTrack? FindRecordingTrack(Guid id) => library?.FindRecordingTrack(id);

    internal DirectorRecordingTrack? FindRecordingTrack(string selector)
    {
        if (library == null || string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        string trimmed = selector.Trim();
        if (Guid.TryParse(trimmed, out Guid id))
        {
            return library.FindRecordingTrack(id);
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            IReadOnlyList<DirectorRecordingTrack> tracks = RecordingTracks;
            return index >= 1 && index <= tracks.Count ? tracks[index - 1] : null;
        }

        return library.FindRecordingTrack(trimmed);
    }

    internal bool TryAddRecordingTrack(
        DirectorRecordingSourceType sourceType,
        string sourceSelector,
        string requestedName,
        out string message)
    {
        message = string.Empty;
        if (recording is null)
        {
            message = Lang.Get("vsdirector:record-unavailable");
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:record-track-name-required");
            Show(message);
            return false;
        }

        if (library!.FindRecordingTrack(name) != null)
        {
            message = Lang.Get("vsdirector:record-track-name-exists", name);
            Show(message);
            return false;
        }

        if (!TryResolveRecordingSource(sourceType, sourceSelector, out Guid sourceId, out string sourceName))
        {
            message = Lang.Get("vsdirector:record-source-not-found", sourceSelector);
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        DirectorRecordingTrack added = library.AddRecordingTrack(new DirectorRecordingTrack
        {
            Name = name,
            SourceType = sourceType,
            SourceId = sourceId,
            Enabled = true,
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow
        });
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get("vsdirector:record-track-added", added.Name, sourceName);
        Show(message);
        return true;
    }

    internal bool TryDeleteRecordingTrack(Guid id, out string message)
    {
        message = string.Empty;
        if (recording is null)
        {
            message = Lang.Get("vsdirector:record-unavailable");
            return false;
        }

        DirectorRecordingTrack? track = library!.FindRecordingTrack(id);
        if (track is null)
        {
            message = Lang.Get("vsdirector:record-track-not-found", id);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        library.RemoveRecordingTrack(id);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        if (assignedRecordingTrackId == id)
        {
            assignedRecordingTrackId = null;
            recording?.UnregisterWorker();
            SaveConfig();
        }

        message = Lang.Get("vsdirector:record-track-deleted", track.Name);
        Show(message);
        return true;
    }

    internal bool TryAssignRecordingWorker(Guid id, out string message)
    {
        message = string.Empty;
        if (recording is null)
        {
            message = Lang.Get("vsdirector:record-unavailable");
            return false;
        }

        DirectorRecordingTrack? track = library!.FindRecordingTrack(id);
        if (track is null)
        {
            message = Lang.Get("vsdirector:record-track-not-found", id);
            return false;
        }

        if (!track.Enabled)
        {
            message = Lang.Get("vsdirector:record-track-disabled", track.Name);
            Show(message);
            return false;
        }

        assignedRecordingTrackId = id;
        SaveConfig();
        recording?.RegisterWorker(track);
        message = Lang.Get("vsdirector:record-worker-assigned", track.Name);
        Show(message);
        return true;
    }

    internal void UnassignRecordingWorker()
    {
        assignedRecordingTrackId = null;
        SaveConfig();
        recording?.UnregisterWorker();
        Show(Lang.Get("vsdirector:record-worker-unassigned"));
    }

    internal bool TryStartRecording(out string message, Guid? expectedTrackId = null)
    {
        if (recording is null)
        {
            message = Lang.Get("vsdirector:record-unavailable");
            Show(message);
            return false;
        }
        expectedTrackId ??= assignedRecordingTrackId;
        if (replay?.IsExporting == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        bool isLiveWorkflow = assignedRecordingTrackId is Guid assignedId
            && library?.FindRecordingTrack(assignedId)?.SourceType == DirectorRecordingSourceType.Workflow;
        bool isReplayDrivenWorkflow = isLiveWorkflow
            && workflowReplayRecording is { } replaySession
            && expectedTrackId == replaySession.TrackId
            && replaySession.WorkflowId == library?.FindRecordingTrack(replaySession.TrackId)?.SourceId
            && replaySession.ReplayId == replay?.ActiveReplayId;
        bool resumeAutomaticRecording = automaticReplayRecording;
        if (resumeAutomaticRecording)
        {
            bool waitForReplaySource = DirectorReplayHandoffPolicy.MustWaitForReplaySave(
                config.RecordReplayWithVideo,
                isLiveWorkflow);
            Guid? queuedTrackId = expectedTrackId;
            Action? continuation = waitForReplaySource
                ? () => TryStartRecording(out _, queuedTrackId)
                : null;
            if (!StopAutomaticReplay(continuation, out string stopError))
            {
                message = ReplayErrorText(stopError, string.Empty);
                Show(message);
                return false;
            }
            if (waitForReplaySource)
            {
                message = Lang.Get("vsdirector:auto-replay-handoff-saving");
                Show(message);
                return true;
            }
        }

        if (isLiveWorkflow
            && !isReplayDrivenWorkflow
            && (replay?.IsRecording == true
                || replay?.IsPlaying == true
                || replay?.IsLoading == true))
        {
            message = Lang.Get("vsdirector:replay-playback-active");
            Show(message);
            if (resumeAutomaticRecording)
            {
                QueueAutomaticReplayRestart();
            }
            return false;
        }
        if (config.RecordReplayWithVideo
            && !isLiveWorkflow
            && !CanStartTakeReplay(out string replayError))
        {
            message = ReplayErrorText(replayError, string.Empty);
            Show(message);
            if (resumeAutomaticRecording)
            {
                QueueAutomaticReplayRestart();
            }
            return false;
        }

        string error = "recording-unavailable";
        if (recording?.RequestStart(out error, expectedTrackId) == true)
        {
            workspace?.TryClose();
            message = Lang.Get("vsdirector:record-start-requested", RecordingHotkeyLabel);
            Show(message);
            return true;
        }

        message = RecordingErrorText(error);
        Show(message);
        if (resumeAutomaticRecording)
        {
            QueueAutomaticReplayRestart();
        }
        return false;
    }

    internal bool TryStopRecording(out string message)
    {
        string error = "recording-unavailable";
        if (recording?.RequestStop(out error) == true)
        {
            message = Lang.Get("vsdirector:record-stop-requested");
            Show(message);
            return true;
        }

        message = RecordingErrorText(error);
        Show(message);
        return false;
    }

    internal void ToggleRecording()
    {
        if (recording is null)
        {
            Show(Lang.Get("vsdirector:record-unavailable"));
            return;
        }
        if (!IsWorldReady)
        {
            Show(Lang.Get("vsdirector:world-not-ready"));
            return;
        }

        if (replay?.IsExporting == true)
        {
            Show(Lang.Get("vsdirector:record-busy"));
            return;
        }

        if (recording?.State is DirectorRecordingState.Idle or DirectorRecordingState.Error)
        {
            TryStartRecording(out _);
            return;
        }

        string error = "recording-unavailable";
        if (recording?.RequestStop(out error) != true)
        {
            Show(RecordingErrorText(error));
        }
    }

    internal string RecordingListText()
    {
        IReadOnlyList<DirectorRecordingTrack> tracks = RecordingTracks;
        if (tracks.Count == 0)
        {
            return Lang.Get("vsdirector:record-track-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:record-track-list-header", tracks.Count) };
        for (int i = 0; i < tracks.Count; i++)
        {
            DirectorRecordingTrack track = tracks[i];
            bool assigned = assignedRecordingTrackId == track.Id;
            lines.Add(Lang.Get(
                "vsdirector:record-track-list-entry",
                i + 1,
                track.Name,
                RecordingSourceLabel(track),
                track.Enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
                assigned ? Lang.Get("vsdirector:record-worker-marker") : string.Empty));
        }

        return string.Join("\n", lines);
    }

    internal string RecordingStatusText(bool includeBoundary = true)
    {
        DirectorRecordingState state = recording?.State ?? DirectorRecordingState.Idle;
        string? track = recording?.ActiveTrack?.Name;
        if (track is null && assignedRecordingTrackId is Guid assignedId)
        {
            track = library?.FindRecordingTrack(assignedId)?.Name;
        }

        track ??= Lang.Get("vsdirector:inactive");
        string status = Lang.Get(
            "vsdirector:record-status",
            RecordingStateLabel(state),
            track,
            config.RecordingBackend,
            config.RecordingContainer,
            DirectorTimecode.NormalizeFrameRate(config.RecordingFrameRate),
            recording?.LastOutputPath ?? string.Empty,
            recording?.LastError ?? string.Empty);
        status += "\n" + RecordingAudioStatusText();
        string takeStatus = TakeOutputStatusText();
        if (takeStatus.Length > 0)
        {
            status += "\n" + takeStatus;
        }

        return includeBoundary
            ? status + "\n" + RecordingBoundaryText()
            : status;
    }

    internal string RecordingMonitorStatusText()
    {
        DirectorRecordingSessionStatus? status = latestRecordingServerStatus;
        if (status is null)
        {
            return Lang.Get("vsdirector:record-monitor-waiting");
        }

        DirectorRecordingWorkerPhase phase = Enum.IsDefined(
            typeof(DirectorRecordingWorkerPhase),
            status.Phase)
                ? (DirectorRecordingWorkerPhase)status.Phase
                : DirectorRecordingWorkerPhase.Error;
        string session = string.IsNullOrWhiteSpace(status.SessionId)
            ? Lang.Get("vsdirector:inactive")
            : status.SessionId;
        string player = !string.IsNullOrWhiteSpace(status.PlayerName)
            ? status.PlayerName
            : !string.IsNullOrWhiteSpace(status.PlayerUid)
                ? status.PlayerUid
                : Lang.Get("vsdirector:inactive");
        string track = string.IsNullOrWhiteSpace(status.TrackName)
            ? Lang.Get("vsdirector:inactive")
            : status.TrackName;
        string message = string.IsNullOrWhiteSpace(status.Message)
            ? Lang.Get("vsdirector:inactive")
            : status.Message;
        return Lang.Get(
            "vsdirector:record-monitor-status",
            session,
            Lang.Get("vsdirector:record-monitor-phase-" + phase.ToString().ToLowerInvariant()),
            player,
            track,
            status.WorkerCount,
            message);
    }

    private string RecordingAudioStatusText()
        => Lang.Get("vsdirector:record-audio-obs");

    internal static string RecordingBoundaryText()
        => Lang.Get("vsdirector:record-output-boundary");

    internal bool TryStartReplayRecording(string name, out string message)
    {
        if (!EnsurePermission(DirectorPermission.ReplayCapture, out message))
        {
            Show(message);
            return false;
        }
        if (recording?.IsActive == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        bool resumeAutomaticRecording = automaticReplayRecording;
        if (resumeAutomaticRecording)
        {
            if (!QueueAfterAutomaticReplaySave(
                () => TryStartReplayRecording(name, out _),
                out bool queued,
                out string stopError))
            {
                message = ReplayErrorText(stopError, name);
                Show(message);
                return false;
            }

            if (queued)
            {
                message = Lang.Get("vsdirector:auto-replay-handoff-saving");
                Show(message);
                return true;
            }
        }

        string error = "replay-unavailable";
        if (replay?.StartRecording(name, out error) == true)
        {
            automaticReplayRecording = false;
            message = Lang.Get(
                "vsdirector:replay-recording-started",
                replay.ActiveReplayName ?? name,
                ReplayRecordingStopHotkeyLabel);
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, name);
        Show(message);
        if (resumeAutomaticRecording)
        {
            QueueAutomaticReplayRestart();
        }
        return false;
    }

    internal bool TryStopReplayRecording(out string message)
    {
        string error = "replay-unavailable";
        if (replay?.RequestStopRecording(out error) == true)
        {
            automaticReplayRecording = false;
            if (activeTakeCapture is not null)
            {
                activeTakeCapture.StopAttempted = true;
                pendingReplaySavePurpose = ReplaySavePurpose.Take;
            }
            else if (pendingReplaySavePurpose == ReplaySavePurpose.None)
            {
                pendingReplaySavePurpose = ReplaySavePurpose.Manual;
            }

            message = Lang.Get("vsdirector:replay-recording-saving", replay.ActiveReplayName ?? string.Empty);
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, string.Empty);
        Show(message);
        return false;
    }

    internal bool TryToggleEscapeMenuReplayRecording(out string message)
        => TryToggleEscapeMenuReplayRecording(null, out message);

    internal bool TryToggleEscapeMenuReplayRecording(string? requestedName, out string message)
    {
        if (liveOffscreenState != LiveOffscreenRecordingState.Idle)
        {
            return TryStopLiveOffscreenRecording(out message);
        }
        if (OffscreenCaptureBusy)
        {
            return TryStopOffscreenCapture(out message);
        }
        if (recording?.IsActive == true)
        {
            return TryStopRecording(out message);
        }
        if (replay?.IsRecording == true)
        {
            return TryStopReplayRecording(out message);
        }
        if (replay?.IsLiveActionRecording == true)
        {
            return TryStopLiveActionRecording(out _, out message);
        }

        string name = string.IsNullOrWhiteSpace(requestedName)
            ? "Replay " + DateTime.Now.ToString(
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture)
            : requestedName.Trim();
        return TryStartReplayRecording(name, out message);
    }

    internal string ReplayListText()
    {
        IReadOnlyList<DirectorReplayInfo> replays = replay?.List() ?? Array.Empty<DirectorReplayInfo>();
        if (replays.Count == 0)
        {
            return Lang.Get("vsdirector:replay-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:replay-list-header", replays.Count) };
        for (int i = 0; i < replays.Count; i++)
        {
            DirectorReplayInfo item = replays[i];
            lines.Add(Lang.Get(
                "vsdirector:replay-list-entry",
                i + 1,
                item.Name,
                item.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                item.ActorCount,
                item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
        }

        return string.Join("\n", lines);
    }

    internal string ReplayActorListText()
    {
        IReadOnlyList<DirectorReplayActorPathSource> actors = ReplayActorPathSources;
        if (replay?.IsPlaying != true)
        {
            return Lang.Get("vsdirector:replay-not-playing");
        }

        if (actors.Count == 0)
        {
            return Lang.Get("vsdirector:actor-path-list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:actor-path-list-header", actors.Count) };
        for (int index = 0; index < actors.Count; index++)
        {
            DirectorReplayActorPathSource actor = actors[index];
            lines.Add(Lang.Get(
                "vsdirector:actor-path-list-entry",
                index + 1,
                actor.DisplayName,
                actor.ActorKey,
                actor.SampleCount));
        }

        return string.Join("\n", lines);
    }

    internal bool TryExportReplayActorPerformance(
        string actorSelector,
        string requestedName,
        out string message)
    {
        message = string.Empty;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || !EnsurePermission(DirectorPermission.ReplayPlayback, out message))
        {
            return false;
        }

        string performanceName = requestedName.Trim();
        if (performanceName.Length == 0)
        {
            message = Lang.Get("vsdirector:actor-performance-name-required");
            Show(message);
            return false;
        }

        if (library!.FindActorPerformance(performanceName) is not null)
        {
            message = Lang.Get("vsdirector:actor-performance-name-exists", performanceName);
            Show(message);
            return false;
        }

        string error = "replay-unavailable";
        if (replay?.TryBuildActorPerformance(
                actorSelector,
                performanceName,
                out DirectorActorPerformance performance,
                out DirectorReplayActorPathSource source,
                out error) != true)
        {
            message = error switch
            {
                "replay-not-playing" => Lang.Get("vsdirector:replay-not-playing"),
                "actor-path-actor-not-found" => Lang.Get(
                    "vsdirector:actor-path-actor-not-found",
                    actorSelector),
                "actor-performance-no-samples" => Lang.Get("vsdirector:actor-path-no-samples"),
                _ => Lang.Get("vsdirector:replay-error", error)
            };
            Show(message);
            return false;
        }

        CameraLibraryDocument before = library.Document;
        DirectorActorPerformance added = library.AddActorPerformance(performance);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        message = Lang.Get(
            "vsdirector:actor-performance-exported",
            source.DisplayName,
            added.Name,
            added.TransformKeyframes.Count,
            added.Cues.Count,
            added.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TryCreateAction(string requestedName, out DirectorActionAsset? created, out string message)
    {
        created = null;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        string name = requestedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:action-name-required");
            return false;
        }
        if (library!.FindAction(name) is not null)
        {
            message = Lang.Get("vsdirector:action-name-exists", name);
            return false;
        }
        CameraLibraryDocument before = library.Document;
        created = library.AddAction(DirectorActionImporter.CreateAnimationAction(name, "idle"));
        if (!PersistOrRollback(before, out message))
        {
            created = null;
            return false;
        }
        actionStore?.Save(created);
        actionEvaluators.Clear();
        actorPerformanceEvaluators.Clear();
        message = Lang.Get("vsdirector:action-created", created.Name);
        Show(message);
        return true;
    }

    internal bool TrySaveAction(
        DirectorActionAsset edited,
        out string message,
        bool showNotification = true)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        CameraLibraryDocument before = library!.Document;
        if (!library.UpdateAction(edited))
        {
            message = Lang.Get("vsdirector:action-not-found", edited.Name);
            return false;
        }
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }
        DirectorActionAsset saved = library.FindAction(edited.Id)!;
        actionStore?.Save(saved);
        actionEvaluators.Clear();
        message = Lang.Get("vsdirector:action-saved", saved.Name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryDeleteAction(Guid id, out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        DirectorActionAsset? action = library!.FindAction(id);
        if (action is null)
        {
            message = Lang.Get("vsdirector:action-not-found", id);
            return false;
        }
        CameraLibraryDocument before = library.Document;
        if (!library.RemoveAction(id) || !PersistOrRollback(before, out message))
        {
            return false;
        }
        actionStore?.Delete(id);
        actionEvaluators.Clear();
        if (activeActionPreview?.Action.Id == id)
        {
            activeActionPreview = null;
            StopStandaloneActionPreviewActor();
        }
        message = Lang.Get("vsdirector:action-deleted", action.Name);
        Show(message);
        return true;
    }

    internal bool TrySetActionFavorite(Guid id, bool favorite, out string message)
    {
        DirectorActionAsset? action = library?.FindAction(id);
        if (action is null)
        {
            message = Lang.Get("vsdirector:action-not-found", id);
            return false;
        }
        action.Favorite = favorite;
        return TrySaveAction(action, out message);
    }

    internal bool TryStoreActionPreviewModel(Guid id, string actorSelector, out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        DirectorActionAsset? action = library!.FindAction(id);
        if (action is null)
        {
            message = Lang.Get("vsdirector:action-not-found", id);
            return false;
        }
        string error = "action-preview-model-store-failed";
        if (replay?.TryCaptureActionPreviewModel(
                actorSelector,
                out string entityCode,
                out string displayName,
                out byte[] entityState,
                out error) != true)
        {
            message = Lang.Get("vsdirector:" + error);
            Show(message);
            return false;
        }
        CameraLibraryDocument before = library.Document;
        DirectorActionAsset edited = action.DeepClone();
        edited.PreviewEntityCode = entityCode;
        edited.PreviewEntityName = displayName;
        edited.PreviewEntityState = entityState;
        edited.ModifiedUtc = DateTimeOffset.UtcNow;
        if (!library.UpdateAction(edited) || !PersistOrRollback(before, out message))
        {
            return false;
        }
        DirectorActionAsset saved = library.FindAction(id)!;
        actionStore?.Save(saved);
        message = Lang.Get("vsdirector:action-preview-model-stored", displayName);
        Show(message);
        return true;
    }

    internal bool TryClearActionPreviewModel(Guid id, out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        DirectorActionAsset? action = library!.FindAction(id);
        if (action is null)
        {
            message = Lang.Get("vsdirector:action-not-found", id);
            return false;
        }
        CameraLibraryDocument before = library.Document;
        DirectorActionAsset edited = action.DeepClone();
        edited.PreviewEntityCode = string.Empty;
        edited.PreviewEntityName = string.Empty;
        edited.PreviewEntityState = Array.Empty<byte>();
        edited.ModifiedUtc = DateTimeOffset.UtcNow;
        if (!library.UpdateAction(edited) || !PersistOrRollback(before, out message))
        {
            return false;
        }
        DirectorActionAsset saved = library.FindAction(id)!;
        actionStore?.Save(saved);
        message = Lang.Get("vsdirector:action-preview-model-cleared");
        Show(message);
        return true;
    }

    internal bool TrySaveModel(
        DirectorModelAsset model,
        out DirectorModelAsset? saved,
        out string message,
        bool showNotification = true)
    {
        saved = null;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        Guid requestedId = model.Id;
        DirectorModelAsset normalized = DirectorModelNormalizer.Normalize(model);
        if (normalized.ModelCode.Length == 0)
        {
            message = Lang.Get("vsdirector:model-code-required");
            Show(message);
            return false;
        }
        CameraLibraryDocument before = library!.Document;
        DirectorModelAsset? existing = requestedId != Guid.Empty
            ? library.FindModel(requestedId)
            : library.FindModel(normalized.ModelCode);
        bool changed;
        if (existing is null)
        {
            saved = library.AddModel(normalized);
            changed = true;
        }
        else
        {
            normalized.Id = existing.Id;
            changed = library.UpdateModel(normalized);
            saved = changed ? library.FindModel(existing.Id) : null;
        }
        if (!changed || !PersistOrRollback(before, out message))
        {
            saved = null;
            return false;
        }
        modelStore?.Save(saved!);
        message = Lang.Get("vsdirector:model-saved", saved!.Name);
        if (showNotification)
        {
            Show(message);
        }
        return true;
    }

    internal bool TryDeleteModel(Guid id, out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message))
        {
            return false;
        }
        DirectorModelAsset? model = library!.FindModel(id);
        if (model is null)
        {
            message = Lang.Get("vsdirector:model-not-found");
            return false;
        }
        CameraLibraryDocument before = library.Document;
        if (!library.RemoveModel(id) || !PersistOrRollback(before, out message))
        {
            return false;
        }
        modelStore?.Delete(id);
        message = Lang.Get("vsdirector:model-deleted", model.Name);
        Show(message);
        return true;
    }

    internal bool TryImportModelFile(string path, out int importedCount, out string message)
    {
        importedCount = 0;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || modelStore is null)
        {
            return false;
        }
        try
        {
            IReadOnlyList<DirectorModelAsset> imported = modelStore.Import(path);
            CameraLibraryDocument before = library!.Document;
            foreach (DirectorModelAsset source in imported)
            {
                DirectorModelAsset? existing = source.Id != Guid.Empty ? library.FindModel(source.Id) : null;
                existing ??= library.FindModel(source.ModelCode);
                if (existing is null)
                {
                    library.AddModel(source);
                }
                else
                {
                    source.Id = existing.Id;
                    library.UpdateModel(source);
                }
                importedCount++;
                if (source.ShapeSourcePath.Length > 0)
                {
                    try
                    {
                        DirectorVsmc2ShapeDocument shape = modelStore.InspectShape(source.ShapeSourcePath);
                        EnsureGameShapeReadable(shape);
                        SetVsmc2ShapeStatus(source.Id, shape);
                    }
                    catch (Exception exception)
                    {
                        vsmc2ShapeStatuses[source.Id] = Lang.Get(
                            "vsdirector:model-shape-validation-failed",
                            exception.GetBaseException().Message);
                    }
                    WatchVsmc2Shape(source);
                }
            }
            if (!PersistOrRollback(before, out message))
            {
                return false;
            }
            message = Lang.Get("vsdirector:model-file-imported", importedCount);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:model-file-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TryExportModel(Guid id, out string path, out string message)
    {
        path = string.Empty;
        DirectorModelAsset? model = library?.FindModel(id);
        if (model is null || modelStore is null)
        {
            message = Lang.Get("vsdirector:model-not-found");
            return false;
        }
        try
        {
            path = modelStore.Save(model);
            message = Lang.Get("vsdirector:model-exported", model.Name, path);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:model-file-failed", exception.Message);
            return false;
        }
    }

    internal bool TryRecordReplayAction(
        string actorSelector,
        double startSeconds,
        double endSeconds,
        string requestedName,
        out DirectorActionAsset? created,
        out string message)
    {
        created = null;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || !EnsurePermission(DirectorPermission.ReplayPlayback, out message))
        {
            return false;
        }
        string name = requestedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:action-name-required");
            return false;
        }
        if (library!.FindAction(name) is not null)
        {
            message = Lang.Get("vsdirector:action-name-exists", name);
            return false;
        }
        string error = "replay-unavailable";
        if (replay?.TryBuildAction(
                actorSelector,
                startSeconds,
                endSeconds,
                name,
                out DirectorActionAsset action,
                out DirectorReplayActorPathSource source,
                out error) != true)
        {
            message = Lang.Get("vsdirector:action-record-failed", error);
            return false;
        }
        CameraLibraryDocument before = library.Document;
        created = library.AddAction(action);
        if (!PersistOrRollback(before, out message))
        {
            created = null;
            return false;
        }
        actionStore?.Save(created);
        actionEvaluators.Clear();
        message = Lang.Get(
            "vsdirector:action-recorded",
            source.DisplayName,
            created.Name,
            created.FrameCount,
            created.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TryStartLiveActionRecording(
        string actorSelector,
        string requestedName,
        out string message)
    {
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || !EnsurePermission(DirectorPermission.StandardRecording, out message))
        {
            return false;
        }
        string name = requestedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:action-name-required");
            return false;
        }
        if (library!.FindAction(name) is not null)
        {
            message = Lang.Get("vsdirector:action-name-exists", name);
            return false;
        }
        string error = "replay-unavailable";
        if (replay?.StartLiveActionRecording(actorSelector, name, out DirectorReplayActorPathSource source, out error) != true)
        {
            message = Lang.Get("vsdirector:action-record-failed", error);
            Show(message);
            return false;
        }
        message = Lang.Get("vsdirector:action-live-started", source.DisplayName, name);
        Show(message);
        return true;
    }

    internal bool TryStopLiveActionRecording(out DirectorActionAsset? created, out string message)
    {
        created = null;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || !EnsurePermission(DirectorPermission.StandardRecording, out message))
        {
            return false;
        }
        string error = "action-live-not-recording";
        if (replay?.StopLiveActionRecording(
                out DirectorActionAsset action,
                out DirectorReplayActorPathSource source,
                out error) != true)
        {
            message = Lang.Get("vsdirector:action-record-failed", error);
            Show(message);
            return false;
        }
        CameraLibraryDocument before = library!.Document;
        created = library.AddAction(action);
        if (!PersistOrRollback(before, out message))
        {
            created = null;
            return false;
        }
        actionStore?.Save(created);
        actionEvaluators.Clear();
        message = Lang.Get(
            "vsdirector:action-recorded",
            source.DisplayName,
            created.Name,
            created.FrameCount,
            created.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Show(message);
        return true;
    }

    internal bool TryImportExternalActionLibraries(out int importedCount, out string message)
    {
        importedCount = 0;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || actionModIntegration is null)
        {
            return false;
        }
        CameraLibraryDocument before = library!.Document;
        HashSet<string> existing = library.OrderedActions
            .Where(action => action.SourceModId.Length > 0 && action.SourceCode.Length > 0)
            .Select(action => action.SourceModId + ":" + action.SourceCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (DirectorExternalActionInfo external in actionModIntegration.DiscoverEmotes())
        {
            if (!existing.Add(external.SourceModId + ":" + external.Code))
            {
                continue;
            }
            DirectorActionAsset action = DirectorActionImporter.CreateAnimationAction(
                external.Name,
                external.Animation,
                external.SourceModId,
                external.Code,
                external.RequiresTarget,
                external.DurationSeconds,
                external.AnimationSpeed,
                external.Weight,
                external.EaseInSpeed,
                external.EaseOutSpeed,
                external.BlendMode);
            action.Description = external.RequiresTarget
                ? $"Paired action, distance {external.PairDistance:0.##}"
                : string.Empty;
            DirectorActionAsset added = library.AddAction(action);
            actionStore?.Save(added);
            importedCount++;
        }
        foreach (DirectorExternalModelInfo model in actionModIntegration.DiscoverModels())
        {
            foreach (string animation in model.Animations)
            {
                string sourceCode = model.Code + ":animation:" + animation;
                if (!existing.Add("playermodellib:" + sourceCode))
                {
                    continue;
                }
                DirectorActionAsset action = DirectorActionImporter.CreateAnimationAction(
                    model.Name + " - " + animation,
                    animation,
                    "playermodellib",
                    sourceCode);
                action.CompatibleModelCodes = new List<string> { model.Code };
                action.PreviewModelCode = model.Code;
                action.Tags = action.Tags
                    .Concat(new[] { "model-animation", model.Code })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                DirectorActionAsset added = library.AddAction(action);
                actionStore?.Save(added);
                importedCount++;
            }
            foreach (string expression in model.Expressions)
            {
                string sourceCode = model.Code + ":expression:" + expression;
                if (!existing.Add("playermodellib:" + sourceCode))
                {
                    continue;
                }
                DirectorActionAsset action = DirectorActionImporter.CreateExpressionAction(
                    model.Name + " - " + expression,
                    expression,
                    model.Code);
                action.SourceCode = sourceCode;
                DirectorActionAsset added = library.AddAction(action);
                actionStore?.Save(added);
                importedCount++;
            }
        }
        if (importedCount > 0 && !PersistOrRollback(before, out message))
        {
            return false;
        }
        actionEvaluators.Clear();
        message = Lang.Get("vsdirector:action-external-imported", importedCount);
        Show(message);
        return true;
    }

    internal bool TryImportActionFile(string path, out int importedCount, out string message)
    {
        importedCount = 0;
        if (!EnsureFeatureEditAccess(DirectorPermission.Workflows, out message)
            || actionStore is null)
        {
            return false;
        }
        try
        {
            IReadOnlyList<DirectorActionAsset> imported = actionStore.Import(path);
            CameraLibraryDocument before = library!.Document;
            foreach (DirectorActionAsset action in imported)
            {
                DirectorActionAsset? existing = library.FindAction(action.Id);
                if (existing is not null)
                {
                    library.UpdateAction(action);
                }
                else
                {
                    library.AddAction(action);
                }
                importedCount++;
            }
            if (!PersistOrRollback(before, out message))
            {
                return false;
            }
            actionEvaluators.Clear();
            message = Lang.Get("vsdirector:action-file-imported", importedCount);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:action-file-failed", exception.Message);
            Show(message);
            return false;
        }
    }

    internal bool TryExportAction(Guid id, bool binary, out string path, out string message)
    {
        path = string.Empty;
        DirectorActionAsset? action = library?.FindAction(id);
        if (action is null || actionStore is null)
        {
            message = Lang.Get("vsdirector:action-not-found", id);
            return false;
        }
        try
        {
            path = actionStore.Save(action, binary);
            message = Lang.Get("vsdirector:action-exported", action.Name, path);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:action-file-failed", exception.Message);
            return false;
        }
    }

    internal bool TryExportActionPack(IEnumerable<Guid> ids, string name, out string path, out string message)
    {
        path = string.Empty;
        DirectorActionAsset[] actions = ids.Select(id => library?.FindAction(id))
            .Where(action => action is not null)
            .Cast<DirectorActionAsset>()
            .ToArray();
        if (actions.Length == 0 || actionStore is null)
        {
            message = Lang.Get("vsdirector:action-pack-empty");
            return false;
        }
        try
        {
            path = actionStore.ExportPackage(actions, name);
            message = Lang.Get("vsdirector:action-pack-exported", actions.Length, path);
            Show(message);
            return true;
        }
        catch (Exception exception)
        {
            message = Lang.Get("vsdirector:action-file-failed", exception.Message);
            return false;
        }
    }

    internal bool TryPreviewAction(
        Guid actionId,
        string actorKey,
        string modelCode,
        double speed,
        DirectorActionLoopMode loopMode,
        int loopCount,
        out string message)
        => TryPreviewActionCore(
            actionId,
            actorKey,
            modelCode,
            speed,
            loopMode,
            loopCount,
            modelSize: 1f,
            libraryPreview: false,
            out message);

    internal bool TryPreviewLibraryAction(
        Guid actionId,
        string actorKey,
        string modelCode,
        double speed,
        DirectorActionLoopMode loopMode,
        int loopCount,
        out string message)
        => TryPreviewActionCore(
            actionId,
            actorKey,
            modelCode,
            speed,
            loopMode,
            loopCount,
            modelSize: 1f,
            libraryPreview: true,
            out message);

    internal bool TryPreviewLibraryModel(
        string actorKey,
        string modelCode,
        string entityCode,
        float modelSize,
        out string message)
    {
        string normalizedModelCode = modelCode?.Trim() ?? string.Empty;
        string normalizedEntityCode = entityCode?.Trim() ?? string.Empty;
        if (normalizedModelCode.Length == 0)
        {
            message = Lang.Get("vsdirector:model-code-required");
            return false;
        }
        if (DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(normalizedModelCode))
        {
            StopLiveActionWorkflow();
            StopStandaloneActionPreviewActor();
            if (!config.EnableGreenScreenSupport
                || !config.EnableGreenScreenModels
                || greenScreenPreviewRenderer?.Start(normalizedModelCode, modelSize) != true)
            {
                message = Lang.Get("vsdirector:model-green-screen-disabled");
                Show(message);
                return false;
            }
            activeActionPreview = null;
            activeActionWorkflowId = Guid.Empty;
            actionEnvironment?.Restore();
            actionEventTimes.Clear();
            actionTriggerPlaybacks.Clear();
            message = Lang.Get("vsdirector:model-preview-started", normalizedModelCode);
            Show(message);
            return true;
        }
        DirectorActionAsset idle = DirectorActionNormalizer.Normalize(new DirectorActionAsset
        {
            Id = Guid.NewGuid(),
            Name = normalizedModelCode,
            LoopMode = DirectorActionLoopMode.Loop
        });
        StopLiveActionWorkflow();
        StopStandaloneActionPreviewActor();
        string target = string.IsNullOrWhiteSpace(actorKey)
            ? ActionActorSources.FirstOrDefault().ActorKey ?? string.Empty
            : actorKey.Trim();
        string error = "action-preview-no-model-source";
        if (replay?.TryStartActionPreviewActor(
                idle,
                target,
                out target,
                out error,
                worldVisible: true,
                entityCode: normalizedEntityCode.Length > 0
                    ? normalizedEntityCode
                    : ResolveActionModelEntityCode(normalizedModelCode)) != true)
        {
            message = Lang.Get("vsdirector:" + error);
            Show(message);
            return false;
        }
        activeActionPreview = new ActiveActionPreview(
            idle,
            target,
            normalizedModelCode,
            float.IsFinite(modelSize) ? Math.Clamp(modelSize, 0.1f, 10f) : 1f,
            new DirectorActionEvaluator(idle),
            Stopwatch.GetTimestamp(),
            KeepFinalPose: false);
        activeActionWorkflowId = Guid.Empty;
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
        message = Lang.Get("vsdirector:model-preview-started", normalizedModelCode);
        Show(message);
        return true;
    }

    internal bool TryPreviewLibraryModelAnimation(
        string actorKey,
        string modelCode,
        string entityCode,
        string animationCode,
        float modelSize,
        out string message)
    {
        string normalizedModelCode = modelCode?.Trim() ?? string.Empty;
        string normalizedEntityCode = entityCode?.Trim() ?? string.Empty;
        string normalizedAnimationCode = animationCode?.Trim() ?? string.Empty;
        if (normalizedModelCode.Length == 0)
        {
            message = Lang.Get("vsdirector:model-code-required");
            return false;
        }
        if (DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(normalizedModelCode))
        {
            message = Lang.Get("vsdirector:model-green-screen-preview-only-error");
            Show(message);
            return false;
        }
        if (normalizedAnimationCode.Length == 0)
        {
            message = Lang.Get("vsdirector:action-not-found", animationCode);
            return false;
        }

        DirectorExternalAnimationInfo animationInfo = ResolveModelAnimationInfo(
            normalizedModelCode,
            normalizedEntityCode,
            normalizedAnimationCode);
        AnimationMetaData metadata = animationInfo.Metadata;
        float animationSpeed = float.IsFinite(metadata.AnimationSpeed)
            ? metadata.AnimationSpeed
            : 1f;
        double frameSpan = animationInfo.EndHandling == EnumEntityAnimationEndHandling.Repeat
            ? animationInfo.QuantityFrames
            : animationInfo.QuantityFrames - 1d;
        double durationSeconds = Math.Clamp(
            frameSpan / (30d * Math.Max(0.01f, Math.Abs(animationSpeed))),
            1d / 60d,
            600d);
        DirectorActionAsset preview = DirectorActionImporter.CreateAnimationAction(
            normalizedAnimationCode,
            string.IsNullOrWhiteSpace(metadata.Code) ? normalizedAnimationCode : metadata.Code,
            sourceCode: normalizedModelCode,
            durationSeconds: durationSeconds,
            animationSpeed: animationSpeed,
            weight: float.IsFinite(metadata.Weight) ? metadata.Weight : 1f,
            easeInSpeed: float.IsFinite(metadata.EaseInSpeed) ? metadata.EaseInSpeed : 10f,
            easeOutSpeed: float.IsFinite(metadata.EaseOutSpeed) ? metadata.EaseOutSpeed : 10f,
            blendMode: (int)metadata.BlendMode);
        preview.CompatibleModelCodes = new List<string> { normalizedModelCode };
        preview.CompatibleEntityCodes = normalizedEntityCode.Length == 0
            ? new List<string>()
            : new List<string> { normalizedEntityCode };
        preview.PreviewModelCode = normalizedModelCode;
        preview.LoopMode = animationInfo.EndHandling == EnumEntityAnimationEndHandling.Repeat
            ? DirectorActionLoopMode.Loop
            : DirectorActionLoopMode.Once;
        preview.LoopCount = 0;
        string activeAnimationCode = string.IsNullOrWhiteSpace(metadata.Animation)
            ? normalizedAnimationCode
            : metadata.Animation.Trim();
        string configuredAnimationCode = string.IsNullOrWhiteSpace(metadata.Code)
            ? normalizedAnimationCode
            : metadata.Code.Trim();
        foreach (DirectorActionFrame frame in preview.Layers.SelectMany(layer => layer.Frames))
        {
            frame.State.Animations = new List<string> { configuredAnimationCode };
            foreach (DirectorReplayAnimationSample state in frame.State.AnimationStates)
            {
                state.Code = configuredAnimationCode;
                state.StateCode = activeAnimationCode;
                state.ElementWeights = metadata.ElementWeight is null
                    ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, float>(metadata.ElementWeight, StringComparer.OrdinalIgnoreCase);
                state.ElementBlendModes = metadata.ElementBlendMode is null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : metadata.ElementBlendMode.ToDictionary(
                        item => item.Key,
                        item => (int)item.Value,
                        StringComparer.OrdinalIgnoreCase);
            }
        }
        if (animationInfo.EndHandling == EnumEntityAnimationEndHandling.Repeat
            && preview.Layers.FirstOrDefault() is { } repeatLayer
            && repeatLayer.Frames.LastOrDefault() is { } repeatEndFrame)
        {
            double lastPoseSeconds = Math.Clamp(
                (animationInfo.QuantityFrames - 1d)
                    / (30d * Math.Max(0.01f, Math.Abs(animationSpeed))),
                0d,
                durationSeconds);
            if (lastPoseSeconds < durationSeconds - DirectorActionNormalizer.TimeEpsilon)
            {
                DirectorActionFrame lastPoseFrame = repeatEndFrame.DeepClone();
                lastPoseFrame.TimeSeconds = lastPoseSeconds;
                lastPoseFrame.State.TimeSeconds = lastPoseSeconds;
                repeatLayer.Frames.Insert(repeatLayer.Frames.Count - 1, lastPoseFrame);
            }
        }
        if (animationInfo.EndHandling == EnumEntityAnimationEndHandling.EaseOut
            && preview.Layers.FirstOrDefault() is { } fadeLayer
            && fadeLayer.Frames.LastOrDefault() is { } lastFrame)
        {
            double fadeSeconds = Math.Clamp(
                1d / Math.Max(1f, metadata.EaseOutSpeed),
                1d / 30d,
                0.5d);
            DirectorActionFrame fadedFrame = lastFrame.DeepClone();
            fadedFrame.TimeSeconds += fadeSeconds;
            fadedFrame.State.TimeSeconds = fadedFrame.TimeSeconds;
            fadeLayer.Frames.Add(fadedFrame);
            fadeLayer.FadeOutSeconds = fadeSeconds;
        }
        preview = DirectorActionNormalizer.Normalize(preview);

        StopLiveActionWorkflow();
        StopStandaloneActionPreviewActor();
        string target = string.IsNullOrWhiteSpace(actorKey)
            ? ActionActorSources.FirstOrDefault().ActorKey ?? string.Empty
            : actorKey.Trim();
        string error = "action-preview-no-model-source";
        if (replay?.TryStartActionPreviewActor(
                preview,
                target,
                out target,
                out error,
                worldVisible: true,
                entityCode: normalizedEntityCode.Length > 0
                    ? normalizedEntityCode
                    : ResolveActionModelEntityCode(normalizedModelCode)) != true)
        {
            message = Lang.Get("vsdirector:" + error);
            Show(message);
            return false;
        }

        activeActionPreview = new ActiveActionPreview(
            preview,
            target,
            normalizedModelCode,
            float.IsFinite(modelSize) ? Math.Clamp(modelSize, 0.1f, 10f) : 1f,
            new DirectorActionEvaluator(preview),
            Stopwatch.GetTimestamp(),
            KeepFinalPose: animationInfo.EndHandling == EnumEntityAnimationEndHandling.Hold);
        activeActionWorkflowId = Guid.Empty;
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
        message = Lang.Get("vsdirector:action-preview-direct-started", normalizedAnimationCode);
        Show(message);
        return true;
    }

    private DirectorExternalAnimationInfo ResolveModelAnimationInfo(
        string modelCode,
        string entityCode,
        string animationCode)
    {
        DirectorExternalAnimationInfo? external = actionModIntegration?.ResolveModelAnimation(
            modelCode,
            animationCode);
        if (external is not null)
        {
            return external;
        }

        EntityClientProperties? client = capi?.World.LoadedEntities.Values
            .FirstOrDefault(entity => string.Equals(
                entity.Code?.ToShortString(),
                entityCode,
                StringComparison.OrdinalIgnoreCase))?.Properties?.Client
            ?? capi?.World.EntityTypes.FirstOrDefault(properties => string.Equals(
                properties.Code?.ToShortString(),
                entityCode,
                StringComparison.OrdinalIgnoreCase))?.Client;
        AnimationMetaData? metadata = null;
        if (client?.AnimationsByMetaCode?.TryGetValue(animationCode, out AnimationMetaData? byCode) == true)
        {
            metadata = byCode.Clone();
        }
        metadata ??= client?.Animations?.FirstOrDefault(candidate => candidate is not null
            && (string.Equals(candidate.Code, animationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Animation, animationCode, StringComparison.OrdinalIgnoreCase)))?.Clone();
        Shape? shape = client?.LoadedShapeForEntity ?? client?.LoadedShape;
        string shapeAnimationCode = string.IsNullOrWhiteSpace(metadata?.Animation)
            ? animationCode
            : metadata.Animation;
        Animation? animation = shape?.Animations?.FirstOrDefault(candidate => candidate is not null
            && (string.Equals(candidate.Code, shapeAnimationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, shapeAnimationCode, StringComparison.OrdinalIgnoreCase)));
        metadata ??= new AnimationMetaData
        {
            Code = animationCode,
            Animation = animation?.Code ?? animationCode
        };
        metadata.Code = string.IsNullOrWhiteSpace(metadata.Code) ? animationCode : metadata.Code;
        metadata.Animation = string.IsNullOrWhiteSpace(metadata.Animation)
            ? animation?.Code ?? animationCode
            : metadata.Animation;
        return new DirectorExternalAnimationInfo(
            metadata,
            Math.Max(2, animation?.QuantityFrames ?? 31),
            animation?.OnAnimationEnd ?? EnumEntityAnimationEndHandling.Repeat);
    }

    internal bool TryPreviewLibraryAction(
        Guid actionId,
        string actorKey,
        string modelCode,
        double speed,
        DirectorActionLoopMode loopMode,
        int loopCount,
        float modelSize,
        out string message)
        => TryPreviewActionCore(
            actionId,
            actorKey,
            modelCode,
            speed,
            loopMode,
            loopCount,
            modelSize,
            libraryPreview: true,
            out message);

    private bool TryPreviewActionCore(
        Guid actionId,
        string actorKey,
        string modelCode,
        double speed,
        DirectorActionLoopMode loopMode,
        int loopCount,
        float modelSize,
        bool libraryPreview,
        out string message)
    {
        if (!EnsurePermission(DirectorPermission.Workflows, out message))
        {
            Show(message);
            return false;
        }
        DirectorActionAsset? action = library?.FindAction(actionId);
        if (action is null)
        {
            message = Lang.Get("vsdirector:action-not-found", actionId);
            return false;
        }
        DirectorActionAsset preview = action.DeepClone();
        preview.PlaybackSpeed = double.IsFinite(speed) ? Math.Clamp(speed, 0.01d, 64d) : 1d;
        preview.LoopMode = loopMode;
        preview.LoopCount = Math.Clamp(loopCount, 0, 100_000);
        string resolvedModelCode = modelCode?.Trim() ?? string.Empty;
        if (DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(resolvedModelCode))
        {
            message = Lang.Get("vsdirector:model-green-screen-preview-only-error");
            Show(message);
            return false;
        }
        bool standalone = libraryPreview || replay?.IsPlaying != true;
        string target = string.IsNullOrWhiteSpace(actorKey)
            ? (standalone ? ActionActorSources : ReplayActorPathSources).FirstOrDefault().ActorKey ?? string.Empty
            : actorKey.Trim();
        if (standalone)
        {
            StopLiveActionWorkflow();
            StopStandaloneActionPreviewActor();
            string error = "action-preview-no-model-source";
            if (replay?.TryStartActionPreviewActor(
                    preview,
                    target,
                    out target,
                    out error,
                    worldVisible: true,
                    entityCode: resolvedModelCode.Length == 0
                        ? string.Empty
                        : ResolveActionModelEntityCode(resolvedModelCode),
                    previewDistance: action.Category == DirectorActionCategory.Expression ? 2d : 4d) != true)
            {
                message = Lang.Get("vsdirector:" + error);
                Show(message);
                return false;
            }
        }
        else
        {
            StopStandaloneActionPreviewActor();
            if (target.Length == 0)
            {
                message = Lang.Get("vsdirector:action-preview-no-actor");
                return false;
            }
        }
        activeActionPreview = new ActiveActionPreview(
            preview,
            target,
            resolvedModelCode,
            float.IsFinite(modelSize) ? Math.Clamp(modelSize, 0.1f, 10f) : 1f,
            new DirectorActionEvaluator(preview),
            Stopwatch.GetTimestamp(),
            KeepFinalPose: false);
        activeActionWorkflowId = Guid.Empty;
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
        message = Lang.Get(
            standalone ? "vsdirector:action-preview-direct-started" : "vsdirector:action-preview-started",
            action.Name);
        Show(message);
        return true;
    }

    private string ResolveActionModelEntityCode(string modelCode)
    {
        string normalized = modelCode?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return string.Empty;
        }
        if (DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(normalized))
        {
            return string.Empty;
        }
        string saved = library?.FindModel(normalized)?.EntityCode ?? string.Empty;
        if (saved.Length > 0)
        {
            return saved;
        }
        string discovered = ActionModels.FirstOrDefault(model => string.Equals(
            model.Code,
            normalized,
            StringComparison.OrdinalIgnoreCase))?.EntityCode ?? string.Empty;
        if (discovered.Length > 0)
        {
            return discovered;
        }
        return normalized.StartsWith("entity/", StringComparison.OrdinalIgnoreCase)
            ? normalized["entity/".Length..]
            : VanillaPlayerModelCode;
    }

    internal void StopActionPreview()
    {
        if (liveActionWorkflowPlayback?.GhostActors.Count > 0)
        {
            StopLiveActionWorkflow();
            return;
        }
        activeActionPreview = null;
        StopStandaloneActionPreviewActor();
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
    }

    internal bool TryPreviewActionWorkflow(Guid workflowId, out string message)
        => TryStartLiveActionWorkflow(workflowId, out message);

    internal void StopActionWorkflowPreview()
    {
        if (liveActionWorkflowPlayback is not null)
        {
            StopLiveActionWorkflow();
            return;
        }
        activeActionWorkflowId = Guid.Empty;
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
    }

    internal bool TryStartLiveActionWorkflow(Guid workflowId, out string message)
    {
        DirectorWorkflow? workflow = library?.FindWorkflow(workflowId);
        if (workflow is null || workflow.Type != DirectorWorkflowType.Action)
        {
            message = Lang.Get("vsdirector:action-workflow-not-found");
            Show(message);
            return false;
        }
        if (capi?.World.Player?.Entity is null)
        {
            message = Lang.Get("vsdirector:world-not-ready");
            return false;
        }
        StopLiveActionWorkflow();
        DirectorWorkflow configured = workflow.DeepClone();
        DirectorWorkflowActorBinding.NormalizeCardBindings(configured);
        if (configured.Cards.Any(card =>
                DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(card.ModelCode)))
        {
            message = Lang.Get("vsdirector:model-green-screen-preview-only-error");
            Show(message);
            return false;
        }
        LiveActionWorkflowPlayback playback = new(configured, Stopwatch.GetTimestamp());
        IReadOnlyList<DirectorWorkflowGhostActor> ghostActors = DirectorWorkflowActorBinding
            .CollectGhostActors(configured);
        if (ghostActors.Count == 0)
        {
            message = Lang.Get("vsdirector:action-workflow-ghost-required");
            Show(message);
            return false;
        }
        activeActionPreview = null;
        StopStandaloneActionPreviewActor();
        replay?.StopActionWorkflowPreviewActors();
        foreach (DirectorWorkflowGhostActor actor in ghostActors)
        {
            DirectorWorkflowCard? entityCard = actor.EntityCard;
            Entity? sourceEntity = ResolveLiveActionWorkflowActor(actor.SourceActorKey);
            DirectorActionAsset idle = DirectorActionNormalizer.Normalize(new DirectorActionAsset
            {
                Id = Guid.NewGuid(),
                Name = entityCard?.Name ?? actor.SourceActorKey,
                LoopMode = DirectorActionLoopMode.Loop
            });
            string entityCode = !string.IsNullOrWhiteSpace(entityCard?.EntityCode)
                ? entityCard!.EntityCode.Trim()
                : !string.IsNullOrWhiteSpace(entityCard?.ModelCode)
                    ? ResolveActionModelEntityCode(entityCard!.ModelCode)
                    : string.Empty;
            string error = "action-preview-no-model-source";
            if (replay?.TryStartActionWorkflowPreviewActor(
                    idle,
                    actor.SourceActorKey,
                    actor.GhostActorKey,
                    entityCode,
                    out Entity? ghost,
                    out error) != true)
            {
                replay?.StopActionWorkflowPreviewActors();
                RestoreLiveActionWorkflowEntities(playback);
                message = Lang.Get("vsdirector:" + error);
                Show(message);
                return false;
            }
            playback.GhostActors[actor.GhostActorKey] = ghost!;
            if (entityCard?.HideLiveActor != false && sourceEntity is not null
                && !playback.HiddenLiveEntities.ContainsKey(sourceEntity))
            {
                playback.HiddenLiveEntities[sourceEntity] = new LiveActorVisibility(
                    sourceEntity.IsRendered,
                    sourceEntity.IsShadowRendered);
                sourceEntity.IsRendered = false;
                sourceEntity.IsShadowRendered = false;
            }
        }
        liveActionWorkflowPlayback = playback;
        activeActionWorkflowId = configured.Id;
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
        UpdateLiveActionWorkflow();
        message = Lang.Get("vsdirector:action-workflow-live-started", workflow.Name);
        Show(message);
        return true;
    }

    internal void StopLiveActionWorkflow()
    {
        LiveActionWorkflowPlayback? playback = liveActionWorkflowPlayback;
        liveActionWorkflowPlayback = null;
        if (playback is null)
        {
            return;
        }
        if (actionWorkflowReplayRecordingId == playback.Workflow.Id)
        {
            actionWorkflowReplayRecordingId = null;
            if (replay?.IsRecording == true)
            {
                pendingReplaySavePurpose = ReplaySavePurpose.Manual;
                if (replay.RequestStopRecording(out string error))
                {
                    Show(Lang.Get("vsdirector:replay-recording-saving", replay.ActiveReplayName ?? playback.Workflow.Name));
                }
                else
                {
                    Show(ReplayErrorText(error, playback.Workflow.Name));
                }
            }
            replay?.ConfigureActionWorkflowRecordingCapture(
                (IReadOnlyDictionary<string, Entity>?)null,
                (IReadOnlyCollection<Entity>?)null);
            controller?.Deactivate();
        }
        if (actionWorkflowVideoRecordingId == playback.Workflow.Id)
        {
            actionWorkflowVideoRecordingId = null;
            if (recording?.IsActive == true)
            {
                recording.RequestStop(out _);
            }
            if (OffscreenCaptureBusy)
            {
                _ = StopOffscreenCaptureInternal(showMessage: false, out _);
            }
        }
        replay?.StopActionWorkflowPreviewActors();
        RestoreLiveActionWorkflowEntities(playback);
        foreach (Entity ghost in playback.GhostActors.Values)
        {
            appliedActionModels.Remove(ghost.EntityId);
            appliedActionExpressions.Remove(ghost.EntityId);
        }
        if (activeActionWorkflowId == playback.Workflow.Id)
        {
            activeActionWorkflowId = Guid.Empty;
        }
        actionEnvironment?.Restore();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
    }

    private void UpdateLiveActionWorkflow()
    {
        LiveActionWorkflowPlayback? playback = liveActionWorkflowPlayback;
        if (playback is null || capi?.World.Player?.Entity is null)
        {
            return;
        }
        double time = Stopwatch.GetElapsedTime(playback.StartTimestamp).TotalSeconds;
        foreach (Entity hidden in playback.HiddenLiveEntities.Keys)
        {
            hidden.IsRendered = false;
            hidden.IsShadowRendered = false;
        }
        foreach (DirectorWorkflowCard card in playback.Workflow.Cards.Where(card => card.Enabled))
        {
            bool active = time >= card.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                && time < card.EndSeconds - DirectorWorkflowPlanner.TimeEpsilon;
            Guid pathId = card.Kind == DirectorWorkflowCardKind.Shot
                    && card.SourceType == DirectorRecordingSourceType.Path
                        ? card.SourceId
                        : Guid.Empty;
            if (active && pathId != Guid.Empty && playback.StartedPathCards.Add(card.Id))
            {
                TryPlayPath(pathId, reverse: false, showNotification: false, loopOverride: CameraPathLoopMode.Once);
            }
        }

        if (time >= playback.Workflow.DurationSeconds - DirectorWorkflowPlanner.TimeEpsilon)
        {
            string name = playback.Workflow.Name;
            StopLiveActionWorkflow();
            Show(Lang.Get("vsdirector:action-workflow-live-finished", name));
        }
    }

    private Entity? ResolveLiveActionWorkflowActor(string actorKey)
    {
        DirectorReplayActorPathSource source = ActionActorSources.FirstOrDefault(candidate =>
            string.Equals(candidate.ActorKey, actorKey, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(source.ActorKey))
        {
            return null;
        }
        return capi?.World.LoadedEntities.TryGetValue(source.EntityId, out Entity? entity) == true
            ? entity
            : capi?.World.Player?.Entity is { } player
                && player.EntityId == source.EntityId
                    ? player
                    : null;
    }

    private static void RestoreLiveActionWorkflowEntities(LiveActionWorkflowPlayback playback)
    {
        foreach ((Entity entity, LiveActorVisibility visibility) in playback.HiddenLiveEntities)
        {
            entity.IsRendered = visibility.Rendered;
            entity.IsShadowRendered = visibility.ShadowRendered;
        }
        playback.HiddenLiveEntities.Clear();
    }

    private IReadOnlyList<string> StartLivePlayerAction(
        EntityPlayer player,
        DirectorActionAsset action,
        double cardPlaybackSpeed)
    {
        HashSet<string> started = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);
        float playbackSpeed = (float)Math.Clamp(
            action.PlaybackSpeed * (double.IsFinite(cardPlaybackSpeed) ? cardPlaybackSpeed : 1d),
            0.01d,
            64d);
        IEnumerable<DirectorReplayAnimationSample> states = action.Layers
            .Where(layer => layer.Enabled)
            .SelectMany(layer => layer.Frames.FirstOrDefault()?.State.AnimationStates
                ?? new List<DirectorReplayAnimationSample>());
        foreach (DirectorReplayAnimationSample state in states)
        {
            string configCode = string.IsNullOrWhiteSpace(state.Code) ? state.StateCode : state.Code;
            string animationCode = string.IsNullOrWhiteSpace(state.StateCode) ? state.Code : state.StateCode;
            if (configCode.Length == 0 || !requested.Add(configCode))
            {
                continue;
            }
            AnimationMetaData? metadata = null;
            if (player.Properties?.Client?.AnimationsByMetaCode is { } animations)
            {
                animations.TryGetValue(configCode, out metadata);
                metadata ??= animations.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Code, configCode, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Animation, animationCode, StringComparison.OrdinalIgnoreCase));
            }
            try
            {
                if (metadata is not null)
                {
                    AnimationMetaData liveMetadata = metadata.Clone();
                    float sourceSpeed = float.IsFinite(state.AnimationSpeed)
                        ? state.AnimationSpeed
                        : liveMetadata.AnimationSpeed;
                    liveMetadata.AnimationSpeed = sourceSpeed * playbackSpeed;
                    player.AnimManager?.StartAnimation(liveMetadata);
                    started.Add(liveMetadata.Code);
                    started.Add(liveMetadata.Animation);
                }
                else
                {
                    player.AnimManager?.StartAnimation(configCode);
                    started.Add(configCode);
                }
            }
            catch (Exception exception)
            {
                capi?.Logger.Debug(
                    "VS Director: live action animation {0} could not start: {1}",
                    configCode,
                    exception.Message);
            }
        }
        return started.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray();
    }

    private void StopUnreferencedLiveAnimations(
        EntityPlayer player,
        LiveActionWorkflowPlayback playback,
        IEnumerable<string> completedAnimations)
    {
        HashSet<string> stillActive = playback.ActiveAnimations.Values
            .SelectMany(value => value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string animation in completedAnimations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!stillActive.Contains(animation))
            {
                TryStopLiveAnimation(player, animation);
            }
        }
    }

    private void TryStopLiveAnimation(EntityPlayer player, string animation)
    {
        try
        {
            player.AnimManager?.StopAnimation(animation);
        }
        catch (Exception exception)
        {
            capi?.Logger.Debug(
                "VS Director: live action animation {0} could not stop: {1}",
                animation,
                exception.Message);
        }
    }

    private void StopStandaloneActionPreviewActor()
    {
        greenScreenPreviewRenderer?.Stop();
        replay?.StopActionPreviewActor();
        appliedActionModels.Remove(DirectorReplayManager.ActionPreviewGhostEntityId);
        appliedActionExpressions.Remove(DirectorReplayManager.ActionPreviewGhostEntityId);
    }

    internal bool TryFollowReplayActorPath(string pathSelector, out string message)
    {
        CameraPath? path = FindPath(pathSelector);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathSelector);
            Show(message);
            return false;
        }

        if (!TryActivateReplayActorPath(path, out string error))
        {
            message = error == "actor-path-replay-mismatch"
                ? Lang.Get("vsdirector:actor-path-replay-mismatch")
                : error == "replay-not-playing"
                    ? Lang.Get("vsdirector:replay-not-playing")
                    : Lang.Get("vsdirector:replay-error", error);
            Show(message);
            return false;
        }

        message = Lang.Get("vsdirector:actor-path-following", path.Name);
        Show(message);
        return true;
    }

    private bool TryActivateReplayActorPath(CameraPath path, out string error)
    {
        error = string.Empty;
        if (replay?.IsPlaying != true || controller is null)
        {
            error = "replay-not-playing";
            return false;
        }

        if (path.SourceReplayId == Guid.Empty || path.SourceReplayId != replay.ActiveReplayId)
        {
            error = "actor-path-replay-mismatch";
            return false;
        }

        double pathTime = DirectorActorPathPlayback.ResolvePlaybackTime(
            path,
            replay.ActiveReplayId,
            replay.PlaybackTimeSeconds,
            localTimeSeconds: 0d);
        if (!CameraPathSampler.TryEvaluate(path, pathTime, out DirectorKeyframe frame)
            || !controller.TrySetReplayPose(frame.ToPreset(path.Id, path.Name), out error))
        {
            if (error.Length == 0)
            {
                error = "actor-path-no-samples";
            }
            return false;
        }

        replayCameraState = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.SyncedPath,
            path.Id);
        return true;
    }

    private bool TryActivateReplayTimelinePath(CameraPath path, out string error)
    {
        error = string.Empty;
        if (replay?.IsPlaying != true || controller is null)
        {
            error = "replay-not-playing";
            return false;
        }

        double pathTime = DirectorReplayPathTimeline.ResolvePathTime(
            path,
            replay.ActiveReplayId,
            replay.PlaybackTimeSeconds);
        if (!CameraPathSampler.TryEvaluate(path, pathTime, out DirectorKeyframe frame)
            || !controller.TrySetReplayPose(frame.ToPreset(path.Id, path.Name), out error))
        {
            if (error.Length == 0)
            {
                error = "path-no-samples";
            }
            return false;
        }

        replayCameraState = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.SyncedPath,
            path.Id);
        return true;
    }

    internal string Vsmc2ShapeStatus(DirectorModelAsset? model)
    {
        if (model is null || model.ShapeSourcePath.Length == 0 && model.ShapeAssetLocation.Length == 0)
        {
            return Lang.Get("vsdirector:model-shape-unlinked");
        }
        if (!config.EnableVsmc2Integration)
        {
            return Lang.Get("vsdirector:vsmc2-disabled");
        }
        if (string.IsNullOrWhiteSpace(config.Vsmc2ExecutablePath))
        {
            return Lang.Get("vsdirector:vsmc2-not-configured");
        }
        if (!File.Exists(config.Vsmc2ExecutablePath))
        {
            return Lang.Get("vsdirector:vsmc2-executable-missing");
        }
        if (model.ShapeSourcePath.Length == 0)
        {
            return Lang.Get("vsdirector:model-shape-asset-only", model.ShapeAssetLocation);
        }
        if (!File.Exists(model.ShapeSourcePath))
        {
            return Lang.Get("vsdirector:model-shape-missing");
        }
        if (vsmc2ShapeStatuses.TryGetValue(model.Id, out string? status))
        {
            return status;
        }
        try
        {
            DateTimeOffset modified = File.GetLastWriteTimeUtc(model.ShapeSourcePath);
            if (model.ShapeSourceModifiedUtc is not null
                && modified > model.ShapeSourceModifiedUtc.Value.AddMilliseconds(50))
            {
                return Lang.Get("vsdirector:model-shape-modified");
            }
        }
        catch
        {
            return Lang.Get("vsdirector:model-shape-missing");
        }
        return model.ShapeSourceSha256.Length == 64
            ? Lang.Get("vsdirector:model-shape-valid", model.ShapeAnimationCodes.Count)
            : Lang.Get("vsdirector:model-shape-not-scanned");
    }

    internal bool HasPendingVsmc2ShapeChange(Guid modelId)
        => pendingVsmc2Shapes.ContainsKey(modelId);

    internal void WatchVsmc2Shape(DirectorModelAsset? model)
    {
        if (!config.EnableVsmc2Integration
            || model is null
            || model.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(model.ShapeSourcePath))
        {
            vsmc2FileWatcher?.Stop();
            return;
        }
        try
        {
            vsmc2FileWatcher?.Watch(
                model.Id,
                model.ShapeSourcePath,
                config.Vsmc2FileWatchDebounceMs);
        }
        catch (Exception exception)
        {
            vsmc2ShapeStatuses[model.Id] = Lang.Get(
                "vsdirector:model-shape-watch-failed",
                exception.GetBaseException().Message);
        }
    }

    internal bool TryRescanVsmc2Shape(
        DirectorModelAsset model,
        out DirectorModelAsset? saved,
        out string message)
    {
        saved = null;
        if (!config.EnableVsmc2Integration)
        {
            message = Lang.Get("vsdirector:vsmc2-disabled");
            Show(message);
            return false;
        }
        string shapePath = model.ShapeSourcePath?.Trim() ?? string.Empty;
        if (shapePath.Length == 0)
        {
            message = Lang.Get("vsdirector:model-shape-path-required");
            Show(message);
            return false;
        }

        DirectorModelAsset edited = model.DeepClone();
        edited.ShapeSourcePath = shapePath;
        edited.ShapeFormat = DirectorVsmc2ShapeDocument.Format;
        DirectorVsmc2ShapeDocument? shape = null;
        try
        {
            shape = modelStore?.InspectShape(shapePath)
                ?? DirectorVsmc2ShapeDocument.Read(shapePath);
            EnsureGameShapeReadable(shape);
            if (shape.IsValid)
            {
                ApplyShapeMetadata(edited, shape);
            }
        }
        catch (Exception exception)
        {
            message = Lang.Get(
                "vsdirector:model-shape-validation-failed",
                exception.GetBaseException().Message);
            if (edited.Id != Guid.Empty)
            {
                vsmc2ShapeStatuses[edited.Id] = message;
            }
            TrySaveModel(edited, out saved, out _, showNotification: false);
            if (saved is not null)
            {
                WatchVsmc2Shape(saved);
                workspace?.RefreshVsmc2Model(saved);
            }
            Show(message);
            return false;
        }

        if (!TrySaveModel(edited, out saved, out message, showNotification: false) || saved is null)
        {
            return false;
        }
        pendingVsmc2Shapes.Remove(saved.Id);
        SetVsmc2ShapeStatus(saved.Id, shape);
        WatchVsmc2Shape(saved);
        workspace?.RefreshVsmc2Model(saved);
        message = shape.IsValid
            ? Lang.Get(
                "vsdirector:model-shape-scanned",
                shape.ElementCount,
                shape.TextureCount,
                shape.AnimationCodes.Count)
            : Lang.Get(
                "vsdirector:model-shape-validation-failed",
                FirstShapeError(shape));
        Show(message);
        return true;
    }

    internal bool TryOpenVsmc2Shape(DirectorModelAsset model, out string message)
    {
        if (!config.EnableVsmc2Integration)
        {
            message = Lang.Get("vsdirector:vsmc2-disabled");
            Show(message);
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.Vsmc2ExecutablePath))
        {
            message = Lang.Get("vsdirector:vsmc2-not-configured");
            Show(message);
            return false;
        }
        if (!vsmc2ProcessBridge.TryOpenShape(
                config.Vsmc2ExecutablePath,
                model.ShapeSourcePath,
                out string error))
        {
            message = Lang.Get("vsdirector:vsmc2-open-failed", error);
            Show(message);
            return false;
        }
        WatchVsmc2Shape(model);
        message = Lang.Get("vsdirector:vsmc2-opened");
        Show(message);
        return true;
    }

    internal bool TryOpenVsmc2ShapeDirectory(DirectorModelAsset model, out string message)
    {
        if (!vsmc2ProcessBridge.TryOpenDirectory(model.ShapeSourcePath, out string error))
        {
            message = Lang.Get("vsdirector:model-shape-folder-failed", error);
            Show(message);
            return false;
        }
        message = Lang.Get("vsdirector:model-shape-folder-opened");
        return true;
    }

    internal bool TryIgnoreVsmc2ShapeChange(Guid modelId, out string message)
    {
        if (!pendingVsmc2Shapes.Remove(modelId))
        {
            message = Lang.Get("vsdirector:model-shape-no-pending-change");
            return false;
        }
        vsmc2ShapeStatuses[modelId] = Lang.Get("vsdirector:model-shape-change-ignored");
        message = vsmc2ShapeStatuses[modelId];
        workspace?.Refresh();
        return true;
    }

    internal bool TryEnableVsmc2AutoReload(Guid modelId, out string message)
    {
        config.Vsmc2AutoReload = true;
        if (!SaveConfig())
        {
            message = Lang.Get("vsdirector:config-save-failed", ConfigFileName);
            return false;
        }
        if (pendingVsmc2Shapes.TryGetValue(modelId, out DirectorVsmc2ShapeDocument? shape)
            && library?.FindModel(modelId) is DirectorModelAsset model
            && shape.IsValid)
        {
            return TryApplyVsmc2ShapeDocument(model, shape, out message);
        }
        message = Lang.Get("vsdirector:model-shape-auto-reload-enabled");
        workspace?.Refresh();
        return true;
    }

    private void OnVsmc2FileChanged(DirectorVsmc2FileChange change)
    {
        ICoreClientAPI? api = capi;
        if (api is null || disposed)
        {
            return;
        }
        api.Event.EnqueueMainThreadTask(
            () => HandleVsmc2FileChanged(change),
            "vsdirector-vsmc2-shape-changed");
    }

    private void HandleVsmc2FileChanged(DirectorVsmc2FileChange change)
    {
        DirectorModelAsset? model = library?.FindModel(change.ModelId);
        if (model is null
            || !string.Equals(
                Path.GetFullPath(model.ShapeSourcePath),
                Path.GetFullPath(change.Path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }
        if (!change.Exists)
        {
            vsmc2ShapeStatuses[model.Id] = Lang.Get("vsdirector:model-shape-missing");
            pendingVsmc2Shapes.Remove(model.Id);
            workspace?.RefreshVsmc2Model(model);
            return;
        }

        DirectorVsmc2ShapeDocument shape;
        try
        {
            shape = modelStore?.InspectShape(change.Path)
                ?? DirectorVsmc2ShapeDocument.Read(change.Path);
            EnsureGameShapeReadable(shape);
        }
        catch (Exception exception)
        {
            vsmc2ShapeStatuses[model.Id] = Lang.Get(
                "vsdirector:model-shape-validation-failed",
                exception.GetBaseException().Message);
            workspace?.RefreshVsmc2Model(model);
            return;
        }
        if (string.Equals(shape.Sha256, model.ShapeSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            SetVsmc2ShapeStatus(model.Id, shape);
            workspace?.RefreshVsmc2Model(model);
            return;
        }

        pendingVsmc2Shapes[model.Id] = shape;
        if (!shape.IsValid)
        {
            SetVsmc2ShapeStatus(model.Id, shape);
            Show(Lang.Get("vsdirector:model-shape-validation-failed", FirstShapeError(shape)));
            workspace?.RefreshVsmc2Model(model);
            return;
        }
        if (config.Vsmc2AutoReload)
        {
            TryApplyVsmc2ShapeDocument(model, shape, out _);
            return;
        }
        vsmc2ShapeStatuses[model.Id] = Lang.Get(
            "vsdirector:model-shape-change-pending",
            shape.ElementCount,
            shape.AnimationCodes.Count);
        Show(vsmc2ShapeStatuses[model.Id]);
        workspace?.RefreshVsmc2Model(model);
    }

    private bool TryApplyVsmc2ShapeDocument(
        DirectorModelAsset model,
        DirectorVsmc2ShapeDocument shape,
        out string message)
    {
        DirectorModelAsset edited = model.DeepClone();
        ApplyShapeMetadata(edited, shape);
        if (!TrySaveModel(edited, out DirectorModelAsset? saved, out message, showNotification: false)
            || saved is null)
        {
            return false;
        }
        pendingVsmc2Shapes.Remove(saved.Id);
        SetVsmc2ShapeStatus(saved.Id, shape);
        message = Lang.Get(
            "vsdirector:model-shape-reloaded",
            shape.ElementCount,
            shape.AnimationCodes.Count);
        Show(message);
        workspace?.RefreshVsmc2Model(saved);
        return true;
    }

    private void SetVsmc2ShapeStatus(Guid modelId, DirectorVsmc2ShapeDocument shape)
    {
        vsmc2ShapeStatuses[modelId] = shape.IsValid
            ? Lang.Get("vsdirector:model-shape-valid", shape.AnimationCodes.Count)
            : Lang.Get("vsdirector:model-shape-validation-failed", FirstShapeError(shape));
    }

    private static void ApplyShapeMetadata(DirectorModelAsset model, DirectorVsmc2ShapeDocument shape)
    {
        model.ShapeSourcePath = shape.FilePath;
        model.ShapeSourceSha256 = shape.Sha256;
        model.ShapeSourceModifiedUtc = shape.ModifiedUtc;
        model.ShapeFormat = DirectorVsmc2ShapeDocument.Format;
        model.ShapeAnimationCodes = shape.AnimationCodes.ToList();
    }

    private static string FirstShapeError(DirectorVsmc2ShapeDocument shape)
        => shape.Diagnostics.FirstOrDefault(diagnostic =>
                diagnostic.Severity == DirectorShapeDiagnosticSeverity.Error)?.Message
            ?? "Shape validation failed.";

    private static void EnsureGameShapeReadable(DirectorVsmc2ShapeDocument shape)
    {
        if (shape.IsValid
            && !DirectorVsmc2ShapeLoader.TryRead(shape.FilePath, out _, out string error))
        {
            throw new DirectorShapeFileException(
                "Vintage Story Shape deserialization failed: " + error);
        }
    }

    internal bool TryUpdateReplayMetadata(
        Guid id,
        string name,
        string tags,
        string description,
        out string message,
        bool showNotification = true,
        bool refreshWorkspace = true)
    {
        message = string.Empty;
        if (!EnsurePermission(DirectorPermission.ReplayCapture, out message))
        {
            Show(message);
            return false;
        }
        if (replay is null)
        {
            message = Lang.Get("vsdirector:replay-unavailable");
            Show(message);
            return false;
        }

        string normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            message = Lang.Get("vsdirector:replay-name-required");
            Show(message);
            return false;
        }

        if (replay.UpdateMetadata(
                id,
                normalizedName,
                tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                description,
                out DirectorReplayInfo? updated,
                out string error)
            && updated is not null)
        {
            message = Lang.Get("vsdirector:replay-metadata-saved", updated.Name);
            if (showNotification)
            {
                Show(message);
            }
            if (refreshWorkspace)
            {
                RefreshWorkspaceDialogs();
            }
            return true;
        }

        message = error switch
        {
            "replay-name-required" => Lang.Get("vsdirector:replay-name-required"),
            "replay-not-found" => Lang.Get("vsdirector:replay-not-found", id),
            _ => Lang.Get("vsdirector:replay-metadata-save-failed", error)
        };
        Show(message);
        return false;
    }

    internal bool TryDeleteReplay(Guid id, out string message)
    {
        message = string.Empty;
        if (!EnsurePermission(DirectorPermission.ReplayCapture, out message))
        {
            Show(message);
            return false;
        }
        if (replay is null)
        {
            message = Lang.Get("vsdirector:replay-unavailable");
            Show(message);
            return false;
        }

        if (replay.Delete(id, out string deletedName, out string error))
        {
            message = Lang.Get("vsdirector:replay-deleted", deletedName);
            Show(message);
            RefreshWorkspaceDialogs();
            return true;
        }

        message = error == "replay-not-found"
            ? Lang.Get("vsdirector:replay-not-found", id)
            : error == "session-source-readonly" ? Lang.Get("vsdirector:session-source-readonly")
            : Lang.Get("vsdirector:replay-delete-failed", error);
        Show(message);
        return false;
    }

    internal bool TryDeleteReplay(string selector, out string message)
    {
        DirectorReplayInfo? info = string.IsNullOrWhiteSpace(selector)
            ? null
            : (int.TryParse(selector.Trim(), out int index) && index >= 1 && index <= Replays.Count
                ? Replays[index - 1]
                : Guid.TryParse(selector.Trim(), out Guid id)
                    ? Replays.FirstOrDefault(replayInfo => replayInfo.Id == id)
                    : Replays.FirstOrDefault(replayInfo => string.Equals(
                        replayInfo.Name, selector.Trim(), StringComparison.OrdinalIgnoreCase)));
        return info is not null
            ? TryDeleteReplay(info.Id, out message)
            : FailReplayDelete(selector, out message);
    }

    private bool FailReplayDelete(string selector, out string message)
    {
        message = Lang.Get("vsdirector:replay-not-found", selector);
        Show(message);
        return false;
    }

    internal bool TryPlayReplay(
        string selector,
        out string message,
        bool pauseAfterLoad = false)
    {
        if (!EnsurePermission(DirectorPermission.ReplayPlayback, out message))
        {
            Show(message);
            return false;
        }
        if (recording?.IsActive == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        bool resumeAutomaticRecording = automaticReplayRecording;
        if (resumeAutomaticRecording)
        {
            if (!QueueAfterAutomaticReplaySave(
                () => TryPlayReplay(selector, out _, pauseAfterLoad),
                out bool queued,
                out string stopError))
            {
                message = ReplayErrorText(stopError, selector);
                Show(message);
                return false;
            }

            if (queued)
            {
                message = Lang.Get("vsdirector:auto-replay-handoff-saving");
                Show(message);
                return true;
            }
        }

        ClearReplayActorAttachment(deactivate: true);
        StopReplayActorPathFollow(deactivate: true);
        replayCameraState = DirectorReplayCameraState.Inactive;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;

        int missingActors = 0;
        string error = "replay-unavailable";
        if (replay?.StartPlayback(selector, out missingActors, out error, pauseAfterLoad) == true)
        {
            message = replay.IsLoading
                ? Lang.Get("vsdirector:replay-loading-started", replay.ActiveReplayName ?? selector)
                : Lang.Get(
                    "vsdirector:replay-playing",
                    replay.ActiveReplayName ?? selector,
                    missingActors);
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, selector);
        Show(message);
        if (resumeAutomaticRecording)
        {
            QueueAutomaticReplayRestart();
        }
        return false;
    }

    internal bool TryToggleReplayPause(out string message)
    {
        bool restartFromEnd = replay?.HasReachedEnd == true;
        if (replay?.IsPlaying == true && replay.TogglePause(out bool pausedPlayback))
        {
            message = Lang.Get(restartFromEnd
                ? "vsdirector:replay-restarted"
                : pausedPlayback
                    ? "vsdirector:replay-paused"
                    : "vsdirector:replay-resumed");
            Show(message);
            return true;
        }

        if (replay?.ToggleRecordingPause(out bool pausedRecording) == true)
        {
            message = Lang.Get(pausedRecording
                ? "vsdirector:replay-recording-paused"
                : "vsdirector:replay-recording-resumed");
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:replay-not-playing");
        Show(message);
        return false;
    }

    internal bool TrySeekReplay(double seconds, out string message)
    {
        if (replay?.Seek(seconds, out double applied) == true)
        {
            message = Lang.Get("vsdirector:replay-seeked", applied.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:replay-invalid-seek");
        Show(message);
        return false;
    }

    internal bool SeekReplayFromPanel(double seconds)
        => replay?.Seek(seconds, out _) == true;

    internal void PauseReplayForTimelineScrub()
    {
        if (replay?.IsPlaying == true)
        {
            replay.SetPaused(true);
        }
    }

    internal void RefreshReplayIndex() => replay?.RefreshIndex();

    internal string ReplayExportOutputDirectory()
    {
        string configured = config.RecordingOutputDirectory?.Trim() ?? string.Empty;
        return configured.Length == 0
            ? Path.GetFullPath(Path.Combine(GamePaths.Videos, "VSDirector", "ReplayExports"))
            : Path.GetFullPath(Path.Combine(configured, "ReplayExports"));
    }

    internal bool TrySetReplaySpeed(double speed, out string message)
    {
        if (replay?.SetSpeed(speed, out double applied) == true)
        {
            message = Lang.Get("vsdirector:replay-speed-set", applied.ToString("0.##", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:replay-invalid-speed");
        Show(message);
        return false;
    }

    internal bool TryAddReplayMarker(string label, double? seconds, out string message)
    {
        string requested = label?.Trim() ?? string.Empty;
        if (requested.Length == 0)
        {
            requested = Lang.Get("vsdirector:replay-marker-default", ReplayMarkers.Count + 1);
        }

        DirectorReplayMarker marker = new();
        string error = "replay-not-active";
        if (replay?.AddMarker(requested, seconds, out marker, out error) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-marker-added",
                marker.Label,
                marker.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryDeleteReplayMarker(string selector, out string message)
    {
        DirectorReplayMarker marker = new();
        string error = "replay-not-active";
        if (replay?.DeleteMarker(selector, out marker, out error) == true)
        {
            message = Lang.Get("vsdirector:replay-marker-deleted", marker.Label);
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryUpdateReplayMarker(
        Guid markerId,
        string label,
        double seconds,
        out string message)
        => TryUpdateReplayMarker(markerId.ToString("D"), label, seconds, out message);

    internal bool TryUpdateReplayMarker(
        string selector,
        string label,
        double seconds,
        out string message)
    {
        DirectorReplayMarker marker = new();
        string error = "replay-not-active";
        if (replay?.UpdateMarker(
            selector,
            label,
            seconds,
            out marker,
            out error) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-marker-updated",
                marker.Label,
                marker.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryJumpReplayMarker(string selector, out string message)
    {
        if (replay?.JumpToMarker(selector, out DirectorReplayMarker marker) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-marker-jumped",
                marker.Label,
                marker.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:replay-marker-not-found", selector);
        Show(message);
        return false;
    }

    internal string ReplayMarkerListText()
    {
        IReadOnlyList<DirectorReplayMarker> markers = ReplayMarkers;
        if (markers.Count == 0)
        {
            return Lang.Get("vsdirector:replay-marker-list-empty");
        }

        return string.Join("\n", new[] { Lang.Get("vsdirector:replay-marker-list-header", markers.Count) }
            .Concat(markers.Select((marker, index) => Lang.Get(
                "vsdirector:replay-marker-list-entry",
                index + 1,
                marker.Label,
                marker.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                marker.Id.ToString("D")))));
    }

    internal bool TryAddReplaySpeedKeyframe(
        double speed,
        double? seconds,
        DirectorReplaySpeedInterpolation interpolation,
        out string message)
    {
        DirectorReplaySpeedKeyframe keyframe = new();
        string error = "replay-not-active";
        if (replay?.AddSpeedKeyframe(
            speed,
            seconds,
            interpolation,
            out keyframe,
            out error) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-speed-key-added",
                keyframe.Speed.ToString("0.##", CultureInfo.InvariantCulture),
                keyframe.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryDeleteReplaySpeedKeyframe(string selector, out string message)
    {
        DirectorReplaySpeedKeyframe keyframe = new();
        string error = "replay-not-active";
        if (replay?.DeleteSpeedKeyframe(
            selector,
            out keyframe,
            out error) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-speed-key-deleted",
                keyframe.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryUpdateReplaySpeedKeyframe(
        Guid keyframeId,
        double seconds,
        double speed,
        DirectorReplaySpeedInterpolation interpolation,
        out string message)
        => TryUpdateReplaySpeedKeyframe(
            keyframeId.ToString("D"), seconds, speed, interpolation, out message);

    internal bool TryUpdateReplaySpeedKeyframe(
        string selector,
        double seconds,
        double speed,
        DirectorReplaySpeedInterpolation interpolation,
        out string message)
    {
        DirectorReplaySpeedKeyframe keyframe = new();
        string error = "replay-not-active";
        if (replay?.UpdateSpeedKeyframe(
            selector,
            seconds,
            speed,
            interpolation,
            out keyframe,
            out error) == true)
        {
            message = Lang.Get(
                "vsdirector:replay-speed-key-updated",
                keyframe.Speed.ToString("0.##", CultureInfo.InvariantCulture),
                keyframe.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal string ReplaySpeedKeyframeListText()
    {
        IReadOnlyList<DirectorReplaySpeedKeyframe> keyframes = ReplaySpeedKeyframes;
        if (keyframes.Count == 0)
        {
            return Lang.Get("vsdirector:replay-speed-key-list-empty");
        }

        return string.Join("\n", new[] { Lang.Get("vsdirector:replay-speed-key-list-header", keyframes.Count) }
            .Concat(keyframes.Select((keyframe, index) => Lang.Get(
                "vsdirector:replay-speed-key-list-entry",
                index + 1,
                keyframe.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                keyframe.Speed.ToString("0.##", CultureInfo.InvariantCulture),
                keyframe.InterpolationToNext,
                keyframe.Id.ToString("D")))));
    }

    private static DirectorReplaySpeedInterpolation ParseReplaySpeedInterpolation(string value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "hold" => DirectorReplaySpeedInterpolation.Hold,
            "ease" => DirectorReplaySpeedInterpolation.EaseInOut,
            _ => DirectorReplaySpeedInterpolation.Linear
        };

    internal bool TryAddReplaySpeedKeyframe(
        double speed,
        double? seconds,
        string interpolation,
        out string message)
        => TryAddReplaySpeedKeyframe(speed, seconds, ParseReplaySpeedInterpolation(interpolation), out message);

    private static string ReplayTimelineEditError(string error)
        => error switch
        {
            "replay-not-active" => Lang.Get("vsdirector:replay-not-active"),
            "replay-marker-name" => Lang.Get("vsdirector:replay-marker-name-required"),
            "replay-marker-time" => Lang.Get("vsdirector:replay-marker-invalid-time"),
            "replay-marker-not-found" => Lang.Get("vsdirector:replay-marker-not-found", string.Empty),
            "replay-speed-key-time" => Lang.Get("vsdirector:replay-speed-key-invalid-time"),
            "replay-speed-key-not-found" => Lang.Get("vsdirector:replay-speed-key-not-found", string.Empty),
            "replay-speed-key-conflict" => Lang.Get("vsdirector:replay-speed-key-conflict"),
            "replay-invalid-speed" => Lang.Get("vsdirector:replay-invalid-speed"),
            _ => Lang.Get("vsdirector:replay-error", error)
        };

    internal bool TryToggleReplayFreeCamera(out string message)
    {
        if (!EnsurePermission(DirectorPermission.FreeCamera, out message)
            || !EnsurePermission(DirectorPermission.ReplayPlayback, out message))
        {
            Show(message);
            return false;
        }
        return EnterReplayFreeCamera(out message);
    }

    internal bool TryEnableReplayFreeCamera(out string message)
        => TryToggleReplayFreeCamera(out message);

    private bool EnterReplayFreeCamera(out string message, bool showNotification = true)
    {
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            if (showNotification) Show(message);
            return false;
        }
        bool alreadyFree = controller.IsFreeCamera
            && replayCameraState.Mode == DirectorReplayCameraMode.Free;
        if (!alreadyFree)
        {
            CancelPendingRemoteFollow(stopAnchor: false);
            if (!controller.TryStartFreeCamera(out string error))
            {
                message = Lang.Get("vsdirector:replay-error", error);
                if (showNotification) Show(message);
                return false;
            }
        }

        ClearReplayActorAttachment();
        replayCameraState = DirectorReplayCameraState.Free;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.ManualFree;
        workspace?.Refresh();
        _ = workspace?.KeepReplayWorkspaceVisible();
        message = Lang.Get("vsdirector:replay-free-enabled");
        if (showNotification) Show(message);
        return true;
    }

    internal bool TryActivateReplayCamera(Guid cameraId)
    {
        if (replay?.IsPlaying != true)
        {
            Show(Lang.Get("vsdirector:replay-not-playing"));
            return false;
        }
        ClearReplayActorAttachment();
        StopReplayActorPathFollow(deactivate: false);
        bool activated = TryActivateCamera(cameraId);
        if (activated)
        {
            replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        }
        return activated;
    }

    internal bool TryActivateReplayPath(Guid pathId)
    {
        if (replay?.IsPlaying != true)
        {
            Show(Lang.Get("vsdirector:replay-not-playing"));
            return false;
        }

        CameraPath? path = library?.FindPath(pathId);
        if (path is null)
        {
            Show(Lang.Get("vsdirector:path-not-found", pathId));
            return false;
        }
        ClearReplayActorAttachment();
        if (path.SourceReplayId == replay.ActiveReplayId)
        {
            if (TryActivateReplayActorPath(path, out string error))
            {
                replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
                Show(Lang.Get("vsdirector:replay-view-path-active", path.Name));
                return true;
            }
            Show(Lang.Get("vsdirector:replay-error", error));
            return false;
        }

        StopReplayActorPathFollow(deactivate: false);
        if (!TryActivateReplayTimelinePath(path, out string timelineError))
        {
            Show(Lang.Get("vsdirector:replay-error", timelineError));
            return false;
        }
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        Show(Lang.Get("vsdirector:replay-view-path-active", path.Name));
        return true;
    }

    internal bool TryActivateReplayFollow(Guid followId)
    {
        if (replay?.IsPlaying != true)
        {
            Show(Lang.Get("vsdirector:replay-not-playing"));
            return false;
        }
        ClearReplayActorAttachment();
        StopReplayActorPathFollow(deactivate: false);
        bool activated = TryActivateFollowRig(followId);
        if (activated)
        {
            replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        }
        return activated;
    }

    internal bool TryActivateReplaySequence(Guid sequenceId)
    {
        if (replay?.IsPlaying != true)
        {
            Show(Lang.Get("vsdirector:replay-not-playing"));
            return false;
        }
        ClearReplayActorAttachment();
        StopReplayActorPathFollow(deactivate: false);
        if (!TryPlaySequence(sequenceId, showNotification: false))
        {
            return false;
        }

        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        CameraSequence? sequence = library?.FindSequence(sequenceId);
        Show(Lang.Get("vsdirector:replay-view-sequence-active", sequence?.Name ?? string.Empty));
        return true;
    }

    internal bool TryCreateReplayPath(string requestedName, out Guid pathId, out string message)
    {
        pathId = Guid.Empty;
        message = string.Empty;
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (!EnsureEditAccess(out message))
        {
            return false;
        }

        string name = requestedName.Trim();
        if (name.Length == 0)
        {
            message = Lang.Get("vsdirector:path-name-required");
            Show(message);
            return false;
        }
        if (library!.FindPath(name) is not null)
        {
            message = Lang.Get("vsdirector:path-name-exists", name);
            Show(message);
            return false;
        }
        if (!controller.TryCaptureCurrentView(name, out CameraPreset pose))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CameraPath created = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SourceReplayId = replay.ActiveReplayId,
            ReplayTimeOffsetSeconds = replay.PlaybackTimeSeconds,
            PreviewVisible = true,
            CreatedUtc = now,
            ModifiedUtc = now,
            Keyframes = new List<DirectorKeyframe>
            {
                DirectorKeyframe.FromPreset(pose, 0d, 0d)
            }
        };
        CameraLibraryDocument before = library.Document;
        CameraPath added = library.AddPath(created);
        if (!PersistOrRollback(before, out message))
        {
            return false;
        }

        pathId = added.Id;
        message = Lang.Get("vsdirector:replay-path-created", added.Name);
        Show(message);
        workspace?.Refresh();
        return true;
    }

    internal bool TryAttachReplayActor(
        string actorSelector,
        CameraFollowViewMode viewMode,
        out string message)
    {
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (!Enum.IsDefined(viewMode)
            || !replay.TryResolveActor(
                actorSelector,
                out DirectorReplayActorPathSource source,
                out Entity? target)
            || target is null)
        {
            message = Lang.Get("vsdirector:replay-attach-actor-not-found", actorSelector);
            Show(message);
            return false;
        }

        string viewName = Lang.Get(viewMode == CameraFollowViewMode.FirstPerson
            ? "vsdirector:replay-attach-first-short"
            : "vsdirector:replay-attach-third-short");
        CameraFollowRig rig = new()
        {
            Id = Guid.NewGuid(),
            Name = Lang.Get("vsdirector:replay-attach-camera-name", source.DisplayName, viewName),
            TargetType = source.IsPlayer && source.PlayerUid.Length > 0
                ? CameraFollowTargetType.Player
                : CameraFollowTargetType.Entity,
            TargetPlayerUid = source.PlayerUid,
            TargetEntityId = target.EntityId,
            TargetDisplayName = source.DisplayName,
            ViewMode = viewMode,
            MotionMode = CameraFollowMotionMode.Immediate,
            Anchor = CameraFollowAnchor.Head,
            Distance = 4d,
            Height = 0.35d,
            Fov = CameraFollowMath.DefaultFov
        };
        CancelPendingRemoteFollow(stopAnchor: false);
        if (!controller.TryFollow(rig, target, out string error))
        {
            message = Lang.Get("vsdirector:replay-error", error);
            Show(message);
            return false;
        }

        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        replayCameraState = DirectorReplayCameraState.FromSource(
            DirectorReplayCameraMode.Follow,
            rig.Id);
        replayAttachedActorKey = source.ActorKey;
        replayAttachedActorName = source.DisplayName;
        replayAttachedActorView = viewMode;
        replayAttachedRigId = rig.Id;
        message = Lang.Get("vsdirector:replay-attach-active", source.DisplayName, viewName);
        Show(message);
        workspace?.Refresh();
        return true;
    }

    internal bool TryDetachReplayActor(out string message)
    {
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (replayAttachedActorView is null)
        {
            message = Lang.Get("vsdirector:replay-attach-not-active");
            Show(message);
            return false;
        }
        string detachedName = replayAttachedActorName;
        if (!EnterReplayFreeCamera(out message, showNotification: false))
        {
            Show(message);
            return false;
        }

        message = Lang.Get("vsdirector:replay-attach-detached", detachedName);
        Show(message);
        return true;
    }

    internal bool TrySetReplayQuickMode(bool enabled, out string message)
    {
        bool previous = config.ReplayQuickMode;
        config.ReplayQuickMode = enabled;
        if (!SaveConfig())
        {
            config.ReplayQuickMode = previous;
            message = Lang.Get("vsdirector:config-save-failed");
            Show(message);
            return false;
        }
        settingsDialog?.RefreshFromConfig();
        workspace?.Refresh();
        message = replay?.IsPlaying == true || replay?.IsLoading == true
            ? Lang.Get(
                "vsdirector:replay-quick-set-next",
                enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
                replay.IsQuickMode ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"))
            : Lang.Get(
                "vsdirector:replay-quick-set",
                enabled ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"));
        Show(message);
        return true;
    }

    private void ClearReplayActorAttachment(bool deactivate = false)
    {
        replayAttachedActorKey = string.Empty;
        replayAttachedActorName = string.Empty;
        replayAttachedActorView = null;
        replayAttachedRigId = null;
        if (deactivate && controller?.IsFollowing == true)
        {
            controller?.Deactivate();
        }
    }

    private void SetReplayCameraState(DirectorReplayCameraMode mode, Guid sourceId)
    {
        if (replay?.IsPlaying == true)
        {
            replayCameraState = DirectorReplayCameraState.FromSource(mode, sourceId);
        }
    }

    internal bool TryCaptureReplayKeyframe(string pathSelector, out string message)
    {
        if (replay?.IsPlaying != true)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }

        CameraPath? path = FindPath(pathSelector);
        if (path is null)
        {
            message = Lang.Get("vsdirector:path-not-found", pathSelector);
            Show(message);
            return false;
        }

        double time = path.SourceReplayId == replay.ActiveReplayId
            ? Math.Max(0d, replay.PlaybackTimeSeconds - path.ReplayTimeOffsetSeconds)
            : replay.PlaybackTimeSeconds;
        return TryCapturePathKeyframe(path.Id, time, out message);
    }

    internal bool TryStartReplayExport(
        string pathSelectors,
        double startSeconds,
        double endSeconds,
        double timeScale,
        out string message)
    {
        if (ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        if (replay?.IsPlaying != true)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }

        if (recording?.IsActive == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        List<CameraPath> paths = new();
        if (string.Equals(pathSelectors.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            paths.AddRange(Paths.Select(path => path.DeepClone()));
        }
        else
        {
            foreach (string selector in pathSelectors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                CameraPath? path = FindPath(selector);
                if (path is null)
                {
                    message = Lang.Get("vsdirector:path-not-found", selector);
                    Show(message);
                    return false;
                }

                if (paths.All(candidate => candidate.Id != path.Id))
                {
                    paths.Add(path);
                }
            }
        }

        string error = string.Empty;
        if (replay.StartExport(paths, startSeconds, endSeconds, timeScale, out error))
        {
            replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
            SuspendWorkspaceForReplayExport();
            message = Lang.Get(
                "vsdirector:replay-export-started",
                paths.Count,
                DirectorTimecode.NormalizeFrameRate(config.RecordingFrameRate),
                timeScale.ToString("0.##", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, pathSelectors);
        Show(message);
        return false;
    }

    internal bool TryStartReplayTrackExport(
        string selectors,
        double startSeconds,
        double endSeconds,
        double timeScale,
        out string message)
    {
        if (ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        List<Guid> trackIds = new();
        if (string.Equals(selectors?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            trackIds.AddRange(ReplayExportTracks.Select(track => track.Id));
        }
        else
        {
            foreach (string selector in (selectors ?? string.Empty).Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                DirectorRecordingTrack? track = FindRecordingTrack(selector);
                if (track is null)
                {
                    message = Lang.Get("vsdirector:replay-export-tracks");
                    Show(message);
                    return false;
                }

                trackIds.Add(track.Id);
            }
        }

        return TryStartReplayTrackExport(
            trackIds,
            startSeconds,
            endSeconds,
            timeScale,
            out message);
    }

    internal bool TryStartReplayTrackExport(
        IEnumerable<Guid> trackIds,
        double startSeconds,
        double endSeconds,
        double timeScale,
        out string message)
    {
        if (ObsOnlyRecording)
        {
            return FailObsOnly(out message);
        }

        if (replay?.IsPlaying != true)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }

        if (recording?.IsActive == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        List<DirectorRecordingTrack> tracks = new();
        foreach (Guid id in trackIds.Distinct())
        {
            DirectorRecordingTrack? track = library?.FindRecordingTrack(id);
            if (track is null
                || !track.Enabled
                || !DirectorWorkflowPlanner.IsExportSource(track.SourceType)
                || !RecordingTrackSourceExists(track))
            {
                message = Lang.Get("vsdirector:record-invalid-track");
                Show(message);
                return false;
            }

            tracks.Add(track);
        }

        string error = string.Empty;
        if (replay.StartTrackExport(tracks, startSeconds, endSeconds, timeScale, out error))
        {
            replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
            SuspendWorkspaceForReplayExport();
            message = Lang.Get(
                "vsdirector:replay-export-started",
                tracks.Count,
                DirectorTimecode.NormalizeFrameRate(config.RecordingFrameRate),
                timeScale.ToString("0.##", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, string.Join(',', tracks.Select(track => track.Name)));
        Show(message);
        return false;
    }

    internal bool TryStartReplaySceneExport(
        Guid sceneId,
        string sceneName,
        double startSeconds,
        double endSeconds,
        double timeScale,
        DirectorReplayExportOptions options,
        out string message)
    {
        if (replay?.IsPlaying != true)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (recording?.IsActive == true)
        {
            message = Lang.Get("vsdirector:record-busy");
            Show(message);
            return false;
        }

        DirectorReplayViewportTimeline timeline = replay.ViewportTimeline;
        DirectorReplayElementScene? scene = timeline.Scenes.FirstOrDefault(candidate =>
            candidate.Id == sceneId);
        if ((options.Backend != DirectorReplayExportBackend.IndependentAudio
                && scene is null)
            || sceneId != timeline.ActiveSceneId)
        {
            message = Lang.Get("vsdirector:replay-export-scene-missing");
            Show(message);
            return false;
        }

        if (options.Backend != DirectorReplayExportBackend.IndependentAudio)
        {
            config.RecordingFrameRate = DirectorTimecode.NormalizeFrameRate(options.FramesPerSecond);
            config.RecordingContainer = DirectorRecordingContainer.Mp4;
            config.FfmpegPath = options.FfmpegPath;
            config.FfmpegVideoCrf = Math.Clamp(options.VideoQuality, 0, 51);
            config.FfmpegVideoPreset = options.SoftwarePreset;
            config.OffscreenEncoder = options.Encoder;
            config.OffscreenVideoBitrateKbps = Math.Clamp(options.VideoBitrateKbps, 0, 1000000);
            config.RecordingOutputDirectory = options.OutputDirectory;
            config.ObsWebSocketUrl = options.ObsWebSocketUrl;
            config.ObsWebSocketPassword = options.ObsWebSocketPassword;
            config.ReplayExportWarmupFrames = Math.Clamp(options.SettleFrames, 0, 8);
            config.Normalize();
            SaveConfig();
        }
        DirectorRecordingTrack track = new()
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(sceneName) ? scene?.Name ?? "Voice audio" : sceneName.Trim(),
            SourceType = DirectorRecordingSourceType.ReplayScene,
            SourceId = sceneId,
            Enabled = true
        };
        if (replay.StartTrackExport(new[] { track }, startSeconds, endSeconds, timeScale, options,
                out string error))
        {
            replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
            SuspendWorkspaceForReplayExport();
            message = options.Backend == DirectorReplayExportBackend.IndependentAudio
                ? Lang.Get("vsdirector:replay-voice-export-started")
                : Lang.Get(
                    "vsdirector:replay-export-started",
                    1,
                    config.RecordingFrameRate,
                    timeScale.ToString("0.##", CultureInfo.InvariantCulture));
            Show(message);
            return true;
        }

        message = ReplayErrorText(error, scene?.Name ?? sceneName);
        Show(message);
        return false;
    }

    internal bool TryStopReplayExport(out string message)
    {
        if (replay?.StopExport() == true)
        {
            message = Lang.Get("vsdirector:replay-export-stopped");
            Show(message);
            return true;
        }

        message = Lang.Get("vsdirector:replay-export-not-active");
        Show(message);
        return false;
    }

    internal string ReplayExportStatusText()
    {
        DirectorReplayManager? manager = replay;
        string rawStatus = manager?.ExportStatus ?? "idle";
        string localizedStatus = Lang.Get("vsdirector:replay-export-state-" + rawStatus);
        string currentName = string.IsNullOrWhiteSpace(manager?.ExportCurrentTrackName)
            ? Lang.Get("vsdirector:inactive")
            : manager.ExportCurrentTrackName;
        string nextName = string.IsNullOrWhiteSpace(manager?.ExportNextTrackName)
            ? Lang.Get("vsdirector:inactive")
            : manager.ExportNextTrackName;
        if ((manager?.ExportTrackCount ?? 0) == 0)
        {
            return Lang.Get("vsdirector:replay-export-status-empty", localizedStatus,
                ((manager?.ExportProgress ?? 0d) * 100d).ToString("0.0", CultureInfo.InvariantCulture),
                manager?.ExportOutputDirectory ?? string.Empty,
                manager?.ExportError ?? string.Empty);
        }

        return Lang.Get(
            "vsdirector:replay-export-status",
            localizedStatus,
            ((manager?.ExportProgress ?? 0d) * 100d).ToString("0.0", CultureInfo.InvariantCulture),
            manager?.ExportCurrentTrackNumber ?? 0,
            manager?.ExportTrackCount ?? 0,
            currentName,
            manager?.ExportCurrentTrackFrame ?? 0L,
            manager?.ExportCurrentTrackFramesTotal ?? 0L,
            nextName,
            manager?.ExportCurrentOutputFile ?? string.Empty,
            manager?.ExportOutputDirectory ?? string.Empty,
            manager?.ExportError ?? string.Empty);
    }

    internal string ReplayExportStatusShortText()
    {
        DirectorReplayManager? manager = replay;
        string rawStatus = manager?.ExportStatus ?? "idle";
        string localizedStatus = Lang.Get("vsdirector:replay-export-state-" + rawStatus);
        string currentName = string.IsNullOrWhiteSpace(manager?.ExportCurrentTrackName)
            ? Lang.Get("vsdirector:inactive")
            : manager.ExportCurrentTrackName;
        string nextName = string.IsNullOrWhiteSpace(manager?.ExportNextTrackName)
            ? Lang.Get("vsdirector:inactive")
            : manager.ExportNextTrackName;
        string fileName = string.IsNullOrWhiteSpace(manager?.ExportCurrentOutputFile)
            ? Lang.Get("vsdirector:inactive")
            : Path.GetFileName(manager.ExportCurrentOutputFile);
        if ((manager?.ExportTrackCount ?? 0) == 0)
        {
            return Lang.Get("vsdirector:replay-export-status-short-empty", localizedStatus,
                ((manager?.ExportProgress ?? 0d) * 100d).ToString("0.0", CultureInfo.InvariantCulture),
                CompactStatusPart(fileName, 24));
        }

        return Lang.Get(
            "vsdirector:replay-export-status-short",
            localizedStatus,
            ((manager?.ExportProgress ?? 0d) * 100d).ToString("0.0", CultureInfo.InvariantCulture),
            manager?.ExportCurrentTrackNumber ?? 0,
            manager?.ExportTrackCount ?? 0,
            CompactStatusPart(currentName, 18),
            manager?.ExportCurrentTrackFrame ?? 0L,
            manager?.ExportCurrentTrackFramesTotal ?? 0L,
            CompactStatusPart(nextName, 14),
            CompactStatusPart(fileName, 24));
    }

    internal void StopReplay()
    {
        bool wasPlaying = replay?.IsPlaying == true;
        if (workflowReplayRecording is
            {
                Backend: not WorkflowReplayRecordingBackend.Obs
            })
        {
            if (OffscreenCaptureBusy)
            {
                _ = StopOffscreenCaptureInternal(showMessage: false, out _);
            }
            workflowReplayRecording = null;
        }
        else if (workflowReplayRecording is not null)
        {
            liveWorkflowRunner?.Hold();
            if (recording?.IsActive == true)
            {
                recording.RequestStop(out _);
            }
            else
            {
                workflowReplayRecording = null;
                RestoreWorkflowRecordingAssignment();
            }
        }
        ClearReplayActorAttachment(deactivate: true);
        StopReplayActorPathFollow(deactivate: true);
        replayCameraState = DirectorReplayCameraState.Inactive;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        replay?.StopPlayback();
        controller?.Deactivate();
        anchorClient?.StopAnchor();
        if (wasPlaying)
        {
            Show(Lang.Get("vsdirector:replay-stopped"));
        }
        QueueAutomaticReplayRestart();
    }

    internal string ReplayStatusText()
    {
        string state = CurrentReplayStateLabel();
        string name = replay?.ActiveReplayName ?? Lang.Get("vsdirector:inactive");
        return Lang.Get(
            "vsdirector:replay-status",
            state,
            name,
            (replay?.PlaybackTimeSeconds ?? 0d).ToString("0.##", CultureInfo.InvariantCulture),
            (replay?.PlaybackSpeed ?? 1d).ToString("0.##", CultureInfo.InvariantCulture))
            + "\n"
            + Lang.Get(
                "vsdirector:replay-mode-status",
                replay?.IsQuickMode == true
                    ? Lang.Get("vsdirector:replay-quick-short")
                    : Lang.Get("vsdirector:replay-full-short"),
                ReplayAttachmentStatusText())
            + "\n"
            + ReplayExportStatusText()
            + "\n"
            + Lang.Get("vsdirector:replay-capture-boundary");
    }

    internal string StatusText()
    {
        string active = controller?.IsFollowing == true
            ? Lang.Get(
                "vsdirector:follow-status-active",
                controller.ActiveCameraName ?? string.Empty,
                controller.ActiveFollowTargetName ?? string.Empty)
            : controller?.ActiveSequenceName is string sequenceName
            ? Lang.Get(
                "vsdirector:sequence-status-active",
                sequenceName,
                controller.ActiveSequenceShotIndex + 1,
                controller.ActiveCameraName ?? string.Empty)
            : controller?.ActiveCameraName ?? Lang.Get("vsdirector:inactive");
        string replayState = CurrentReplayStateLabel();
        return Lang.Get("vsdirector:status", Cameras.Count, Paths.Count, FollowRigs.Count, active,
            config.HideHudOnCamera ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            config.LockPlayerInput ? Lang.Get("vsdirector:on") : Lang.Get("vsdirector:off"),
            replayState);
    }

    internal string ReplayTimelineStateShortText()
    {
        string state = replay?.IsRecordingPaused == true
            ? Lang.Get("vsdirector:replay-recording-paused-short")
            : replay?.IsRecording == true
                ? Lang.Get("vsdirector:replay-recording-short")
                : replay?.IsSaving == true
                    ? Lang.Get("vsdirector:replay-saving-short")
                    : replay?.HasFailedSave == true
                        ? Lang.Get("vsdirector:replay-save-failed-short")
                : replay?.HasReachedEnd == true
                    ? Lang.Get("vsdirector:replay-ended-short")
                    : replay?.IsPaused == true
                        ? Lang.Get("vsdirector:replay-paused-short")
                        : replay?.IsPlaying == true
                            ? Lang.Get("vsdirector:replay-playing-short")
                            : Lang.Get("vsdirector:inactive");
        return replay?.IsQuickMode == true
            ? state + " | " + Lang.Get("vsdirector:replay-quick-short")
            : state;
    }

    private string CurrentReplayStateLabel()
        => replay?.IsRecordingPaused == true
            ? Lang.Get("vsdirector:replay-state-recording-paused")
            : replay?.IsRecording == true
                ? Lang.Get("vsdirector:replay-state-recording")
                : replay?.IsSaving == true
                    ? Lang.Get("vsdirector:replay-state-saving")
                    : replay?.HasFailedSave == true
                        ? Lang.Get("vsdirector:replay-state-save-failed")
                        : replay?.HasReachedEnd == true
                            ? Lang.Get("vsdirector:replay-state-ended")
                            : replay?.IsPlaying == true
                                ? Lang.Get("vsdirector:replay-state-playing")
                                : Lang.Get("vsdirector:inactive");

    internal string ReplayAttachmentStatusText()
        => replayAttachedActorView is CameraFollowViewMode view
            ? Lang.Get(
                "vsdirector:replay-attach-status",
                replayAttachedActorName,
                Lang.Get(view == CameraFollowViewMode.FirstPerson
                    ? "vsdirector:replay-attach-first-short"
                    : "vsdirector:replay-attach-third-short"))
            : Lang.Get("vsdirector:replay-attach-none");

    internal string FollowVisualStatusText()
        => controller?.IsFollowing == true
            ? Lang.Get("vsdirector:follow-visual-status",
                controller.ActiveFollowOrbitAngleDegrees.ToString("0.0", CultureInfo.InvariantCulture),
                controller.ActiveFollowElevationDegrees.ToString("0.0", CultureInfo.InvariantCulture),
                controller.ActiveFollowDistance.ToString("0.00", CultureInfo.InvariantCulture))
            : Lang.Get("vsdirector:follow-visual-inactive");

    internal string ListText()
    {
        IReadOnlyList<CameraPreset> cameras = Cameras;
        if (cameras.Count == 0)
        {
            return Lang.Get("vsdirector:list-empty");
        }

        List<string> lines = new() { Lang.Get("vsdirector:list-header", cameras.Count) };
        for (int i = 0; i < cameras.Count; i++)
        {
            CameraPreset camera = cameras[i];
            (double displayX, double displayY, double displayZ) = DisplayPosition(
                camera.X,
                camera.Y,
                camera.Z);
            string slot = camera.QuickSlot is int quickSlot && QuickSlots.IsValid(quickSlot)
                ? QuickSlotHotkeyLabel(quickSlot)
                : Lang.Get("vsdirector:no-slot");
            lines.Add(Lang.Get("vsdirector:list-entry", i + 1, camera.Name, slot,
                displayX.ToString("0.##", CultureInfo.InvariantCulture),
                displayY.ToString("0.##", CultureInfo.InvariantCulture),
                displayZ.ToString("0.##", CultureInfo.InvariantCulture),
                camera.Fov.ToString("0.#", CultureInfo.InvariantCulture)));
        }

        return string.Join("\n", lines);
    }

    private void RegisterHotkeys(ICoreClientAPI api)
    {
        api.Input.RegisterHotKey(PanelHotkey, Lang.Get("vsdirector:hotkey-panel"), GlKeys.F9, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(PanelHotkey, _ => { TogglePanel(); return true; });
        api.Input.RegisterHotKey(WorkspaceModeHotkey, Lang.Get("vsdirector:hotkey-workspace-mode"),
            GlKeys.F8, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(WorkspaceModeHotkey, _ =>
        {
            TryToggleWorkspaceInteractionMode();
            return true;
        });
        api.Input.RegisterHotKey(ExitHotkey, Lang.Get("vsdirector:hotkey-exit"), GlKeys.F10, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(ExitHotkey, hotkey =>
        {
            _ = hotkey;
            if (replay?.IsRecording == true)
            {
                TryStopReplayRecording(out _);
            }
            else if (replay?.IsPlaying == true || replay?.IsLoading == true)
            {
                StopReplay();
            }
            else
            {
                ExitCamera();
            }
            return true;
        });
        api.Input.RegisterHotKey(
            ReplayRecordingStartHotkey,
            Lang.Get("vsdirector:hotkey-replay-recording-start"),
            GlKeys.R,
            HotkeyType.GUIOrOtherControls,
            ctrlPressed: true);
        api.Input.SetHotKeyHandler(ReplayRecordingStartHotkey, hotkey =>
        {
            _ = hotkey;
            if (replay?.IsRecording != true)
            {
                string name = "Replay " + DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture);
                TryStartReplayRecording(name, out _);
            }
            return true;
        });
        api.Input.RegisterHotKey(
            ReplayRecordingStopHotkey,
            Lang.Get("vsdirector:hotkey-replay-recording-stop"),
            GlKeys.S,
            HotkeyType.GUIOrOtherControls,
            altPressed: true);
        api.Input.SetHotKeyHandler(ReplayRecordingStopHotkey, hotkey =>
        {
            _ = hotkey;
            if (replay?.IsRecording == true)
            {
                TryStopReplayRecording(out _);
            }
            return true;
        });

        for (int slot = QuickSlots.First; slot <= QuickSlots.Last; slot++)
        {
            int capturedSlot = slot;
            string code = QuickSlotHotkeyPrefix + capturedSlot;
            GlKeys key = QuickSlotKey(capturedSlot);
            api.Input.RegisterHotKey(code, Lang.Get("vsdirector:hotkey-quick-slot", capturedSlot), key,
                HotkeyType.GUIOrOtherControls, altPressed: true);
            api.Input.SetHotKeyHandler(code, _ =>
            {
                // Numeric keys must remain text input while a native ImGui
                // field owns focus. ImGui.GetIO() is safe when no native
                // window is active and simply reports false in that case.
                try { if (ImGui.GetIO().WantTextInput) return false; }
                catch (InvalidOperationException) { }
                TryActivateSlot(capturedSlot);
                return true;
            });
        }
    }

    private static GlKeys QuickSlotKey(int slot)
    {
        return QuickSlots.KeyboardDigit(slot) switch
        {
            0 => GlKeys.Number0,
            1 => GlKeys.Number1,
            2 => GlKeys.Number2,
            3 => GlKeys.Number3,
            4 => GlKeys.Number4,
            5 => GlKeys.Number5,
            6 => GlKeys.Number6,
            7 => GlKeys.Number7,
            8 => GlKeys.Number8,
            9 => GlKeys.Number9,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private void MergeGlobalActionLibrary()
    {
        if (library is null || actionStore is null)
        {
            return;
        }
        foreach (DirectorActionAsset action in actionStore.LoadAll())
        {
            DirectorActionAsset? existing = library.FindAction(action.Id);
            if (existing is null)
            {
                library.AddAction(action);
            }
            else if (action.ModifiedUtc > existing.ModifiedUtc)
            {
                library.UpdateAction(action);
            }
        }
        bool repairedAny = false;
        foreach (DirectorActionAsset action in library.OrderedActions.ToArray())
        {
            if (!DirectorActionImporter.TryRepairGeneratedAnimationProgress(action, out DirectorActionAsset repaired))
            {
                continue;
            }
            library.UpdateAction(repaired);
            actionStore.Save(repaired);
            repairedAny = true;
        }
        if (repairedAny)
        {
            store?.Save(library);
        }
        actionEvaluators.Clear();
    }

    private void MergeGlobalModelLibrary()
    {
        if (library is null || modelStore is null)
        {
            return;
        }
        bool changed = false;
        foreach (DirectorModelAsset model in modelStore.LoadAll())
        {
            DirectorModelAsset? existing = library.FindModel(model.Id) ?? library.FindModel(model.ModelCode);
            if (existing is null)
            {
                library.AddModel(model);
                changed = true;
            }
            else if (model.ModifiedUtc > existing.ModifiedUtc)
            {
                model.Id = existing.Id;
                library.UpdateModel(model);
                changed = true;
            }
        }
        if (changed)
        {
            store?.Save(library);
        }
    }

    private DirectorReplayActorSample DecorateReplayActionSample(
        DirectorReplayActorTrack track,
        double replayTimeSeconds,
        DirectorReplayActorSample replayState)
    {
        if (!HasActiveReplayActionDecoration())
        {
            return replayState;
        }

        double actionTime = ResolveActionWorkflowTime(track, replayTimeSeconds);
        DirectorReplayActorSample result = ApplyActionWorkflowActorPath(track, actionTime, replayState);
        foreach (ResolvedActionPlayback resolved in ResolveActionPlaybacks(track, actionTime))
        {
            result = DirectorActionComposer.Apply(
                result,
                resolved.Evaluation,
                resolved.Action.IncludeRootMotion);
        }
        return result;
    }

    private DirectorReplayActorSample ApplyActionWorkflowActorPath(
        DirectorReplayActorTrack track,
        double replayTimeSeconds,
        DirectorReplayActorSample replayState)
    {
        DirectorWorkflow? workflow = activeActionWorkflowId == Guid.Empty
            ? null
            : library?.FindWorkflow(activeActionWorkflowId);
        if (workflow is null)
        {
            return replayState;
        }
        DirectorWorkflowCard? card = workflow.Cards
            .Where(card => card.Enabled
                && card.Kind == DirectorWorkflowCardKind.ActorPath
                && replayTimeSeconds >= card.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                && replayTimeSeconds <= card.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon
                && ActorMatches(track, ResolveActorKey(workflow, card)))
            .OrderBy(card => card.Lane)
            .ThenBy(card => card.Order)
            .LastOrDefault();
        if (card is null)
        {
            return replayState;
        }
        double local = card.SourceOffsetSeconds
            + Math.Max(0d, replayTimeSeconds - card.StartSeconds) * card.PlaybackSpeed;
        DirectorReplayActorSample result = replayState.DeepClone();
        DirectorActorPerformance? performance = library?.FindActorPerformance(card.ActorPerformanceId);
        if (performance is not null)
        {
            if (!actorPerformanceEvaluators.TryGetValue(performance.Id, out DirectorActorPerformanceEvaluator? evaluator))
            {
                evaluator = new DirectorActorPerformanceEvaluator(performance);
                actorPerformanceEvaluators[performance.Id] = evaluator;
            }
            DirectorActorPerformanceState state = evaluator.Sample(local);
            if (state.Transform is { } transform)
            {
                result.X = transform.X;
                result.Y = transform.Y;
                result.Z = transform.Z;
                result.Dimension = transform.Dimension;
                result.Yaw = transform.Yaw;
                result.Pitch = transform.Pitch;
                result.Roll = transform.Roll;
                result.HeadYaw = transform.HeadYaw;
                result.HeadPitch = transform.HeadPitch;
                result.BodyYaw = transform.BodyYaw;
            }
            if (state.Animations.Count > 0)
            {
                result.Animations = new List<string>(state.Animations);
                result.AnimationStates = state.Animations.Select(code => new DirectorReplayAnimationSample
                {
                    Code = code,
                    StateCode = code,
                    Weight = 1f,
                    BlendedWeight = 1f,
                    EasingFactor = 1f
                }).ToList();
            }
            return result;
        }

        CameraPath? path = library?.FindPath(card.PathId);
        if (path is not null && CameraPathSampler.TryEvaluate(path, local, out DirectorKeyframe frame))
        {
            const double motionSampleSeconds = 1d / 30d;
            double neighborTime = local + motionSampleSeconds <= path.DurationSeconds
                ? local + motionSampleSeconds
                : Math.Max(0d, local - motionSampleSeconds);
            DirectorKeyframe? motionFrame = Math.Abs(neighborTime - local) > DirectorWorkflowPlanner.TimeEpsilon
                && CameraPathSampler.TryEvaluate(path, neighborTime, out DirectorKeyframe sampledMotion)
                    ? sampledMotion
                    : null;
            bool playerActor = track.EntityCode.Contains("player", StringComparison.OrdinalIgnoreCase);
            result = DirectorActorPathPlayback.Apply(
                result,
                path,
                frame,
                motionFrame,
                neighborTime - local,
                playerActor);
        }
        return result;
    }

    private void DecorateReplayActionEntity(
        DirectorReplayActorTrack track,
        double replayTimeSeconds,
        Entity entity,
        DirectorReplayActorSample replayState)
    {
        if (!HasActiveReplayActionDecoration())
        {
            return;
        }

        double actionTime = ResolveActionWorkflowTime(track, replayTimeSeconds);
        if (actionModIntegration is null)
        {
            return;
        }

        ActiveActionPreview? entityPreview = activeActionPreview is { } primaryPreview
                && ActorMatches(track, primaryPreview.ActorKey)
            ? primaryPreview
            : null;
        string modelCode = entityPreview is { } preview
            ? preview.ModelCode
            : string.Empty;
        float modelSize = entityPreview is { } sizedPreview
            ? sizedPreview.ModelSize
            : 1f;
        DirectorWorkflow? workflow = activeActionWorkflowId == Guid.Empty
            ? null
            : library?.FindWorkflow(activeActionWorkflowId);
        if (workflow is not null)
        {
            DirectorWorkflowCard? entityCard = workflow.Cards
                .Where(card => card.Enabled
                    && card.Kind == DirectorWorkflowCardKind.Entity
                    && actionTime >= card.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                    && actionTime <= card.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon
                    && ActorMatches(track, ResolveActorKey(workflow, card)))
                .OrderBy(card => card.Lane)
                .ThenBy(card => card.Order)
                .LastOrDefault();
            if (entityCard is not null && entityCard.ModelCode.Length > 0)
            {
                modelCode = entityCard.ModelCode;
                modelSize = library?.FindModel(modelCode)?.Size ?? 1f;
            }
        }
        if (DirectorGreenScreenPreviewRenderer.IsGreenScreenModelCode(modelCode))
        {
            modelCode = string.Empty;
        }
        string appliedModelKey = modelCode + "@" + modelSize.ToString("R", CultureInfo.InvariantCulture);
        if (modelCode.Length > 0
            && (!appliedActionModels.TryGetValue(entity.EntityId, out string? appliedModel)
                || !string.Equals(appliedModel, appliedModelKey, StringComparison.OrdinalIgnoreCase))
            && actionModIntegration.TryApplyModel(entity, modelCode, modelSize))
        {
            appliedActionModels[entity.EntityId] = appliedModelKey;
        }

        IReadOnlyList<ResolvedActionPlayback> playbacks = ResolveActionPlaybacks(track, actionTime);
        string expression = playbacks
            .SelectMany(playback => playback.Evaluation.Layers)
            .SelectMany(layer => layer.StringProperties)
            .Where(item => item.Key.Equals("facialexpression", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .LastOrDefault() ?? string.Empty;
        if (expression.Length > 0
            && (!appliedActionExpressions.TryGetValue(entity.EntityId, out string? appliedExpression)
                || !string.Equals(appliedExpression, expression, StringComparison.OrdinalIgnoreCase))
            && actionModIntegration.TryApplyExpression(entity, expression))
        {
            appliedActionExpressions[entity.EntityId] = expression;
        }

        ApplyActionBoneOverrides(entity, playbacks);
        foreach (ResolvedActionPlayback playback in playbacks)
        {
            DispatchActionEvents(entity, playback);
        }
    }

    private double ResolveActionWorkflowTime(DirectorReplayActorTrack track, double replayTimeSeconds)
    {
        LiveActionWorkflowPlayback? live = liveActionWorkflowPlayback;
        return live is not null
            && live.GhostActors.ContainsKey(track.ActorKey)
                ? Stopwatch.GetElapsedTime(live.StartTimestamp).TotalSeconds
                : replayTimeSeconds;
    }

    private static void ApplyActionBoneOverrides(
        Entity entity,
        IReadOnlyList<ResolvedActionPlayback> playbacks)
    {
        if (playbacks.Count == 0)
        {
            return;
        }

        ClientAnimator? animator = entity.AnimManager?.Animator as ClientAnimator
            ?? (entity as EntityPlayer)?.TpAnimManager?.Animator as ClientAnimator;
        float[]? matrices = animator?.Matrices;
        if (animator is null || matrices is null || matrices.Length < 16)
        {
            return;
        }
        Dictionary<string, int> joints = animator.jointsById.Values
            .Where(joint => joint?.Element?.Name is not null)
            .Select(joint => (Name: joint.Element.Name!, joint.JointId))
            .GroupBy(joint => joint.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().JointId, StringComparer.OrdinalIgnoreCase);
        foreach (DirectorActionLayerEvaluation layer in playbacks
            .SelectMany(playback => playback.Evaluation.Layers))
        {
            float weight = Math.Clamp(layer.EffectiveWeight, 0f, 1f);
            foreach ((string boneName, DirectorActionBoneTransform transform) in layer.BoneOverrides)
            {
                if (!joints.TryGetValue(boneName, out int jointId))
                {
                    continue;
                }
                int offset = jointId * 16;
                if (offset < 0 || offset + 16 > matrices.Length)
                {
                    continue;
                }
                float[] current = new float[16];
                Array.Copy(matrices, offset, current, 0, 16);
                float[] adjustment = Mat4f.Create();
                Mat4f.Translate(adjustment, adjustment,
                    transform.TranslationX * weight,
                    transform.TranslationY * weight,
                    transform.TranslationZ * weight);
                const float degreesToRadians = MathF.PI / 180f;
                Mat4f.RotateX(adjustment, adjustment, transform.RotationX * weight * degreesToRadians);
                Mat4f.RotateY(adjustment, adjustment, transform.RotationY * weight * degreesToRadians);
                Mat4f.RotateZ(adjustment, adjustment, transform.RotationZ * weight * degreesToRadians);
                Mat4f.Scale(adjustment, adjustment,
                    1f + (transform.ScaleX - 1f) * weight,
                    1f + (transform.ScaleY - 1f) * weight,
                    1f + (transform.ScaleZ - 1f) * weight);
                float[] result = new float[16];
                Mat4f.Mul(result, current, adjustment);
                Array.Copy(result, 0, matrices, offset, 16);
            }
        }
    }

    private bool HasActiveReplayActionDecoration()
        => activeActionPreview is not null
            || activeActionWorkflowId != Guid.Empty
            || liveActionWorkflowPlayback is not null;

    private bool RequiresFullReplayActorSampling()
        => HasActiveReplayActionDecoration()
            // Workflow views are resolved only after the primary frame has
            // rendered. Prime every replay actor while capture is starting or
            // active so an offscreen-only actor is ready for the first view.
            || OffscreenCaptureBusy
            || (offscreenRenderer?.ActiveViewCount ?? 0) > 0;

    private bool RequiresReplayCameraActorPoseSampling(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayActorTrack actor,
        double replayTimeSeconds)
    {
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track => track.Enabled))
        {
            DirectorReplayElementKeyframe target;
            if (track.TrackType == DirectorReplayElementTrackType.TrackEntity)
            {
                if (!DirectorReplayElementTimeline.TrySample(
                        timeline.ElementKeyframes,
                        track.TrackType,
                        track.Id,
                        replayTimeSeconds,
                        out target))
                {
                    continue;
                }
            }
            else if (track.TrackType == DirectorReplayElementTrackType.FollowEntity)
            {
                if (!DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
                        timeline.ElementKeyframes,
                        track.Id,
                        replayTimeSeconds,
                        out target,
                        out _))
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (string.Equals(target.EntityActorKey, actor.ActorKey, StringComparison.Ordinal)
                || target.EntityId != 0L
                    && (target.EntityId == actor.EntityId
                        || target.EntityId == actor.ReplayEntityId))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateActionWorkflowEnvironment(double replayTimeSeconds)
    {
        if (actionEnvironment is null)
        {
            return;
        }
        DirectorWorkflow? workflow = activeActionWorkflowId == Guid.Empty
            ? null
            : library?.FindWorkflow(activeActionWorkflowId);
        DirectorWorkflowCard? environment = workflow?.Cards
            .Where(card => card.Enabled
                && card.Kind == DirectorWorkflowCardKind.Environment
                && replayTimeSeconds >= card.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                && replayTimeSeconds <= card.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon)
            .OrderBy(card => card.Lane)
            .ThenBy(card => card.Order)
            .LastOrDefault();
        if (environment is null)
        {
            actionEnvironment.Restore();
            return;
        }
        actionEnvironment.Apply(environment.EnvironmentCode, environment.EnvironmentData, environment.Id);
    }

    private IReadOnlyList<ResolvedActionPlayback> ResolveActionPlaybacks(
        DirectorReplayActorTrack track,
        double replayTimeSeconds)
    {
        List<ResolvedActionPlayback> result = new();
        if (activeActionPreview is { } preview && ActorMatches(track, preview.ActorKey))
        {
            double elapsed = Stopwatch.GetElapsedTime(preview.StartTimestamp).TotalSeconds;
            DirectorActionEvaluation evaluation = preview.Evaluator.Sample(elapsed);
            if (!evaluation.Completed || preview.KeepFinalPose)
            {
                result.Add(new ResolvedActionPlayback(
                    "preview:" + preview.Action.Id.ToString("N") + ":" + track.ActorKey,
                    preview.Action,
                    evaluation));
            }
        }
        DirectorWorkflow? workflow = activeActionWorkflowId == Guid.Empty
            ? null
            : library?.FindWorkflow(activeActionWorkflowId);
        if (workflow is null)
        {
            return result;
        }
        foreach (DirectorWorkflowCard card in workflow.Cards
            .Where(card => card.Enabled
                && card.Kind == DirectorWorkflowCardKind.Action
                && card.ActionId != Guid.Empty
                && replayTimeSeconds >= card.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                && replayTimeSeconds <= card.EndSeconds + DirectorWorkflowPlanner.TimeEpsilon)
            .OrderBy(card => card.Lane)
            .ThenBy(card => card.Order))
        {
            string actorKey = ResolveActorKey(workflow, card);
            if (!ActorMatches(track, actorKey))
            {
                continue;
            }
            DirectorActionAsset? action = library?.FindAction(card.ActionId);
            if (action is null)
            {
                continue;
            }
            string runtimeKey = "workflow:" + workflow.Id.ToString("N") + ":"
                + card.Id.ToString("N") + ":" + track.ActorKey;
            double local;
            if (card.ActionTriggerType == DirectorActionTriggerType.Manual)
            {
                local = card.SourceOffsetSeconds
                    + Math.Max(0d, replayTimeSeconds - card.StartSeconds) * card.PlaybackSpeed;
            }
            else if (!TryResolveTriggeredActionLocal(
                         runtimeKey,
                         card,
                         action,
                         track,
                         replayTimeSeconds,
                         out local))
            {
                continue;
            }
            result.Add(new ResolvedActionPlayback(
                runtimeKey,
                action,
                EvaluatorFor(action, card.Id, card.ActionLoopMode).Sample(local)));
        }
        return result;
    }

    private bool TryResolveTriggeredActionLocal(
        string runtimeKey,
        DirectorWorkflowCard card,
        DirectorActionAsset action,
        DirectorReplayActorTrack track,
        double replayTimeSeconds,
        out double local)
    {
        local = 0d;
        if (!DirectorReplayMath.TrySample(track, replayTimeSeconds, out DirectorReplayActorSample sample))
        {
            return false;
        }
        if (!actionTriggerPlaybacks.TryGetValue(runtimeKey, out ActionTriggerPlaybackState? state))
        {
            state = new ActionTriggerPlaybackState();
            actionTriggerPlaybacks[runtimeKey] = state;
        }
        bool discontinuity = double.IsFinite(state.LastReplayTime)
            && (replayTimeSeconds < state.LastReplayTime - DirectorWorkflowPlanner.TimeEpsilon
                || replayTimeSeconds - state.LastReplayTime > 0.5d);
        if (discontinuity)
        {
            state.Active = false;
            state.WasMatching = false;
            actionEventTimes.Remove(runtimeKey);
        }
        bool sameFrame = double.IsFinite(state.LastReplayTime)
            && Math.Abs(replayTimeSeconds - state.LastReplayTime) <= DirectorWorkflowPlanner.TimeEpsilon;
        if (!sameFrame)
        {
            DirectorActionTrigger? trigger = action.Triggers
                .FirstOrDefault(item => item.Type == card.ActionTriggerType);
            bool matching = DirectorActionTriggerDetector.Matches(
                sample,
                card.ActionTriggerType,
                trigger?.EventCode);
            if (matching && !state.WasMatching && (!state.Active || trigger?.RestartIfPlaying != false))
            {
                state.Active = true;
                state.StartReplayTime = replayTimeSeconds + Math.Max(0d, trigger?.DelaySeconds ?? 0d);
                actionEventTimes.Remove(runtimeKey);
            }
            state.WasMatching = matching;
            state.LastReplayTime = replayTimeSeconds;
        }
        if (!state.Active || replayTimeSeconds < state.StartReplayTime - DirectorWorkflowPlanner.TimeEpsilon)
        {
            return false;
        }
        local = card.SourceOffsetSeconds
            + Math.Max(0d, replayTimeSeconds - state.StartReplayTime) * card.PlaybackSpeed;
        DirectorActionEvaluation evaluation = EvaluatorFor(action, card.Id, card.ActionLoopMode).Sample(local);
        if (evaluation.Completed && card.ActionLoopMode == DirectorActionLoopMode.Once)
        {
            state.Active = false;
        }
        return true;
    }

    private DirectorActionEvaluator EvaluatorFor(
        DirectorActionAsset action,
        Guid cacheId,
        DirectorActionLoopMode loopMode)
    {
        ActionEvaluatorCacheKey key = new(
            cacheId,
            action.Id,
            loopMode,
            action.ModifiedUtc.UtcDateTime.Ticks);
        if (!actionEvaluators.TryGetValue(key, out DirectorActionEvaluator? evaluator))
        {
            foreach (ActionEvaluatorCacheKey stale in actionEvaluators.Keys
                .Where(candidate => candidate.CacheId == cacheId && candidate != key)
                .ToArray())
            {
                actionEvaluators.Remove(stale);
            }
            evaluator = new DirectorActionEvaluator(DirectorActionCardPlayback.Configure(action, loopMode));
            actionEvaluators[key] = evaluator;
        }
        return evaluator;
    }

    private static string ResolveActorKey(DirectorWorkflow workflow, DirectorWorkflowCard card)
        => DirectorWorkflowActorBinding.ResolveRuntimeActorKey(workflow, card);

    private static bool ActorMatches(DirectorReplayActorTrack track, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }
        string value = selector.Trim();
        return string.Equals(track.ActorKey, value, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(track.PlayerUid)
                && string.Equals(track.PlayerUid, value, StringComparison.Ordinal))
            || string.Equals(track.EntityId.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    private void DispatchActionEvents(Entity entity, ResolvedActionPlayback playback)
    {
        double current = playback.Evaluation.SourceSeconds;
        bool initialized = actionEventTimes.TryGetValue(playback.RuntimeKey, out double previous);
        IEnumerable<DirectorActionEvent> crossed = !initialized
            ? playback.Action.Events.Where(item => item.TimeSeconds <= current + DirectorActionNormalizer.TimeEpsilon)
            : current >= previous
                ? playback.Action.Events.Where(item => item.TimeSeconds > previous + DirectorActionNormalizer.TimeEpsilon
                    && item.TimeSeconds <= current + DirectorActionNormalizer.TimeEpsilon)
                : playback.Action.Events.Where(item => item.TimeSeconds > previous + DirectorActionNormalizer.TimeEpsilon
                    || item.TimeSeconds <= current + DirectorActionNormalizer.TimeEpsilon);
        actionEventTimes[playback.RuntimeKey] = current;
        foreach (DirectorActionEvent actionEvent in crossed)
        {
            double x = entity.Pos.X + actionEvent.OffsetX;
            double y = entity.Pos.Y + actionEvent.OffsetY;
            double z = entity.Pos.Z + actionEvent.OffsetZ;
            if (actionEvent.Type == DirectorActionEventType.Sound && actionEvent.Code.Length > 0)
            {
                capi?.World.PlaySoundAt(
                    new AssetLocation(actionEvent.Code),
                    x,
                    y,
                    z,
                    null,
                    EnumSoundType.Sound,
                    actionEvent.Pitch,
                    32f,
                    actionEvent.Volume);
            }
            else if (actionEvent.Type == DirectorActionEventType.Particle && capi is not null)
            {
                int color = ParseActionParticleColor(actionEvent.Data);
                SimpleParticleProperties properties = new(
                    1f,
                    2f,
                    color,
                    new Vec3d(x - 0.05d, y, z - 0.05d),
                    new Vec3d(x + 0.05d, y + 0.1d, z + 0.05d),
                    new Vec3f(-0.05f, 0.05f, -0.05f),
                    new Vec3f(0.05f, 0.2f, 0.05f),
                    0.6f,
                    0.2f)
                {
                    MinSize = 0.05f,
                    MaxSize = 0.15f
                };
                capi.World.SpawnParticles(properties);
            }
        }
    }

    private static int ParseActionParticleColor(string? value)
    {
        string text = value?.Trim().TrimStart('#') ?? string.Empty;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
            ? unchecked((int)(text.Length <= 6 ? parsed | 0xff000000u : parsed))
            : unchecked((int)0xffffb347u);
    }

    private void OnLevelFinalize()
    {
        if (disposed || capi == null || capi.World.Player == null || string.IsNullOrWhiteSpace(capi.World.SavegameIdentifier))
        {
            return;
        }

        leavingWorld = false;
        if (sharedCameraClient is not null)
        {
            sharedCameraClient.CatalogChanged -= OnSharedCameraCatalogChanged;
            sharedCameraClient.CatalogChanged += OnSharedCameraCatalogChanged;
        }
        if (replay is not null)
        {
            replay.PlaybackStateChanged -= OnReplayPlaybackStateChanged;
            replay.PlaybackStateChanged += OnReplayPlaybackStateChanged;
        }
        string worldId = capi.World.SavegameIdentifier;
        try
        {
            vanillaActionModels = null;
            vanillaActionLibraryReady = false;
            nextVanillaActionLibraryRetryTimestamp = 0L;
            store = new WorldCameraStore(capi, worldId);
            library = new CameraLibrary(store.Load());
            MergeGlobalActionLibrary();
            MergeGlobalModelLibrary();
            EnsureVanillaActionsAvailable(forceRefresh: true);
            _ = EnsureOffscreenRenderer(out _);
            workspace?.Dispose();
            workspace = null;
            recording?.ResetWorld();
            latestRecordingServerStatus = null;
            replay?.InitializeWorld(worldId);
            activeTakeCapture = null;
            lastTakeManifestPath = string.Empty;
            accessAttempted = false;
            automaticReplayAuthorizationPending = false;
            permissionClient?.Reset();
            permissionClient?.RequestSnapshot();
            anchorClient?.ResetSession();
            chunkPreloadClient?.ResetSession();
            sharedCameraClient?.Reset();
            pathPreview?.Refresh();
            // LevelFinalize overlaps other mods' GUI initialization. The
            // Cairo-backed workspace is created lazily from a user action so
            // its text composition cannot race that startup work.
        }
        catch (Exception ex)
        {
            capi.Logger.Error("VS Director: failed to initialize world camera library: {0}", ex.Message);
            store = null;
            library = null;
            Show(Lang.Get("vsdirector:library-load-failed"));
        }
    }

    private void TryStartAutomaticReplay(string worldId)
    {
        if (IsShuttingDown
            || !config.AutoRecordReplay
            || !HasPermission(DirectorPermission.ReplayCapture)
            || !HasPermission(DirectorPermission.RemoteChunkAccess)
            || anchorClient?.AccessAllowed != true
            || replay?.IsRecording == true
            || replay?.HasPendingSave == true
            || replay?.IsPlaying == true
            || replay?.IsLoading == true
            || replay?.IsExporting == true
            || recording?.IsActive == true
            || capi?.World.SavegameIdentifier != worldId)
        {
            return;
        }

        string name = $"Auto {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string error = "replay-unavailable";
        if (replay is null || !replay.StartRecording(name, out error))
        {
            capi?.Logger.Warning("VS Director: automatic replay recording did not start: {0}", error);
            return;
        }

        automaticReplayRecording = true;
        workspace?.Refresh();
        if (config.ShowSwitchNotifications)
        {
            Show(Lang.Get("vsdirector:auto-replay-started", name));
        }
    }

    private void QueueAutomaticReplayRestart()
    {
        if (!config.AutoRecordReplay || capi is null || IsShuttingDown)
        {
            return;
        }

        string worldId = capi.World.SavegameIdentifier ?? string.Empty;
        capi.Event.EnqueueMainThreadTask(
            () => TryStartAutomaticReplay(worldId),
            "vsdirector-resume-auto-replay");
    }

    internal bool QueueAfterAutomaticReplaySave(
        Action continuation,
        out bool queued,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        queued = automaticReplayRecording
            || (pendingReplaySavePurpose == ReplaySavePurpose.Automatic
                && replay?.HasPendingSave == true);
        if (!queued)
        {
            error = string.Empty;
            return true;
        }

        if (!automaticReplayRecording)
        {
            pendingAutomaticReplayAction ??= continuation;
            error = string.Empty;
            return true;
        }

        if (StopAutomaticReplay(continuation, out error))
        {
            return true;
        }

        queued = false;
        return false;
    }

    private bool StopAutomaticReplay(Action? continuation, out string error)
    {
        error = string.Empty;
        if (!automaticReplayRecording)
        {
            return true;
        }

        automaticReplayRecording = false;
        pendingReplaySavePurpose = ReplaySavePurpose.Automatic;
        pendingAutomaticReplayAction = continuation;
        error = "replay-unavailable";
        if (replay?.RequestStopRecording(out error) != true)
        {
            automaticReplayRecording = true;
            pendingReplaySavePurpose = ReplaySavePurpose.None;
            pendingAutomaticReplayAction = null;
            capi?.Logger.Warning("VS Director: automatic replay recording did not stop cleanly: {0}", error);
            return false;
        }

        return true;
    }

    private void BeginWorldShutdown()
    {
        // Resets and pending GUI commits can synchronously raise change notifications.
        // UI services may already have shut down in an earlier LeaveWorld handler.
        leavingWorld = true;
        replayExportWorkspace = null;
        if (sharedCameraClient is not null)
        {
            sharedCameraClient.CatalogChanged -= OnSharedCameraCatalogChanged;
        }
        if (replay is not null)
        {
            replay.PlaybackStateChanged -= OnReplayPlaybackStateChanged;
        }
    }

    private void OnLeaveWorld()
    {
        BeginWorldShutdown();
        vsmc2FileWatcher?.Stop();
        pendingVsmc2Shapes.Clear();
        vsmc2ShapeStatuses.Clear();
        StopLiveActionWorkflow();
        greenScreenPreviewRenderer?.Stop();
        actionEnvironment?.Restore();
        activeActionPreview = null;
        activeActionWorkflowId = Guid.Empty;
        actionEvaluators.Clear();
        actorPerformanceEvaluators.Clear();
        appliedActionModels.Clear();
        appliedActionExpressions.Clear();
        actionEventTimes.Clear();
        actionTriggerPlaybacks.Clear();
        ForceStopLiveOffscreenForShutdown();
        ForceStopOffscreenCaptureForShutdown();
        DisposeOffscreenRenderer();
        recording?.ResetWorld();
        latestRecordingServerStatus = null;
        replay?.LeaveWorld();
        automaticReplayRecording = false;
        automaticReplayAuthorizationPending = false;
        pendingAutomaticReplayAction = null;
        replayCameraState = DirectorReplayCameraState.Inactive;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        ClearReplayActorAttachment();
        activeTakeCapture = null;
        lastTakeManifestPath = string.Empty;
        pendingRemoteFollow = null;
        RefreshPendingRemoteChunkPriorities();
        permissionClient?.Reset();
        anchorClient?.ResetSession();
        chunkPreloadClient?.ResetSession();
        sharedCameraClient?.Reset();
        controller?.Deactivate();
        workspace?.Dispose();
        workspace = null;
        settingsDialog?.TryClose();
        workflowReplayRecording = null;
        library = null;
        store = null;
        pathPreview?.Clear();
    }

    private void OnLeftWorld()
    {
        BeginWorldShutdown();
        vsmc2FileWatcher?.Stop();
        pendingVsmc2Shapes.Clear();
        vsmc2ShapeStatuses.Clear();
        greenScreenPreviewRenderer?.Stop();
        ForceStopLiveOffscreenForShutdown();
        ForceStopOffscreenCaptureForShutdown();
        DisposeOffscreenRenderer();
        recording?.ResetWorld();
        latestRecordingServerStatus = null;
        replay?.LeaveWorld();
        automaticReplayRecording = false;
        automaticReplayAuthorizationPending = false;
        pendingAutomaticReplayAction = null;
        replayCameraState = DirectorReplayCameraState.Inactive;
        replayViewportCameraAuthority = DirectorReplayCameraAuthority.Timeline;
        ClearReplayActorAttachment();
        activeTakeCapture = null;
        lastTakeManifestPath = string.Empty;
        pendingRemoteFollow = null;
        RefreshPendingRemoteChunkPriorities();
        permissionClient?.Reset();
        anchorClient?.ResetSession();
        chunkPreloadClient?.ResetSession();
        sharedCameraClient?.Reset();
        controller?.Deactivate();
        workspace?.Dispose();
        workspace = null;
        settingsDialog?.TryClose();
        workflowReplayRecording = null;
        library = null;
        store = null;
        pathPreview?.Clear();
    }

    private Entity? ResolveFollowTarget(CameraFollowRig rig)
    {
        if (capi == null)
        {
            return null;
        }

        Entity? replayTarget = rig.TargetType == CameraFollowTargetType.Player
            ? replay?.ResolveReplayActor(rig.TargetPlayerUid, rig.TargetEntityId)
            : replay?.ResolveReplayActor(null, rig.TargetEntityId);
        if (replayTarget is not null)
        {
            return replayTarget;
        }

        if (rig.TargetType == CameraFollowTargetType.Player)
        {
            if (string.IsNullOrWhiteSpace(rig.TargetPlayerUid))
            {
                return null;
            }

            Entity? playerEntity = capi.World.AllOnlinePlayers.FirstOrDefault(player => string.Equals(
                    player.PlayerUID,
                    rig.TargetPlayerUid,
                    StringComparison.Ordinal))?.Entity
                ?? capi.World.PlayerByUid(rig.TargetPlayerUid)?.Entity;
            if (playerEntity is not null)
            {
                return playerEntity;
            }

            Entity? entityById = rig.TargetEntityId > 0
                ? capi.World.GetEntityById(rig.TargetEntityId)
                : null;
            return entityById is EntityPlayer remotePlayer
                && string.Equals(remotePlayer.PlayerUID, rig.TargetPlayerUid, StringComparison.Ordinal)
                    ? remotePlayer
                    : null;
        }

        return rig.TargetEntityId > 0 ? capi.World.GetEntityById(rig.TargetEntityId) : null;
    }

    private static bool ValidateFollowRig(CameraFollowRig rig, out string message)
    {
        bool targetValid = rig.TargetType switch
        {
            CameraFollowTargetType.Player => !string.IsNullOrWhiteSpace(rig.TargetPlayerUid),
            CameraFollowTargetType.Entity => rig.TargetEntityId > 0,
            _ => false
        };
        bool valuesValid = targetValid
            && Enum.IsDefined(rig.ViewMode)
            && Enum.IsDefined(rig.MotionMode)
            && Enum.IsDefined(rig.Anchor)
            && double.IsFinite(rig.OffsetX)
            && Math.Abs(rig.OffsetX) <= CameraFollowMath.MaxOffset
            && double.IsFinite(rig.OffsetY)
            && Math.Abs(rig.OffsetY) <= CameraFollowMath.MaxOffset
            && double.IsFinite(rig.OffsetZ)
            && Math.Abs(rig.OffsetZ) <= CameraFollowMath.MaxOffset
            && double.IsFinite(rig.Distance)
            && rig.Distance >= 0d
            && rig.Distance <= CameraFollowMath.MaxDistance
            && double.IsFinite(rig.Height)
            && Math.Abs(rig.Height) <= CameraFollowMath.MaxHeight
            && double.IsFinite(rig.OrbitAngleDegrees)
            && double.IsFinite(rig.OrbitSpeedDegreesPerSecond)
            && Math.Abs(rig.OrbitSpeedDegreesPerSecond) <= CameraFollowMath.MaxOrbitSpeed
            && double.IsFinite(rig.SmoothTimeSeconds)
            && rig.SmoothTimeSeconds >= CameraFollowMath.MinSmoothTimeSeconds
            && rig.SmoothTimeSeconds <= CameraFollowMath.MaxSmoothTimeSeconds
            && double.IsFinite(rig.DelaySeconds)
            && rig.DelaySeconds >= 0d
            && rig.DelaySeconds <= CameraFollowMath.MaxDelaySeconds
            && float.IsFinite(rig.Fov)
            && rig.Fov is >= 10f and <= 170f
            && (rig.QuickSlot is null || QuickSlots.IsValid(rig.QuickSlot.Value));
        message = valuesValid
            ? string.Empty
            : Lang.Get("vsdirector:invalid-follow-values");
        return valuesValid;
    }

    private static string FollowViewLabel(CameraFollowViewMode mode)
        => Lang.Get(mode == CameraFollowViewMode.FirstPerson
            ? "vsdirector:follow-view-first"
            : "vsdirector:follow-view-third");

    private static string FollowMotionLabel(CameraFollowMotionMode mode)
    {
        return Lang.Get(mode switch
        {
            CameraFollowMotionMode.Immediate => "vsdirector:follow-motion-immediate",
            CameraFollowMotionMode.Delayed => "vsdirector:follow-motion-delayed",
            _ => "vsdirector:follow-motion-smooth"
        });
    }

    private bool TryResolveRecordingSource(
        DirectorRecordingSourceType sourceType,
        string selector,
        out Guid sourceId,
        out string sourceName)
    {
        sourceId = Guid.Empty;
        sourceName = Lang.Get("vsdirector:record-source-program");
        switch (sourceType)
        {
            case DirectorRecordingSourceType.Program:
                return true;
            case DirectorRecordingSourceType.StaticCamera:
                {
                    CameraPreset? camera = FindCamera(selector);
                    if (camera is null)
                    {
                        return false;
                    }

                    sourceId = camera.Id;
                    sourceName = camera.Name;
                    return true;
                }
            case DirectorRecordingSourceType.Path:
                {
                    CameraPath? path = FindPath(selector);
                    if (path is null)
                    {
                        return false;
                    }

                    sourceId = path.Id;
                    sourceName = path.Name;
                    return true;
                }
            case DirectorRecordingSourceType.Sequence:
                {
                    CameraSequence? sequence = FindSequence(selector);
                    if (sequence is null)
                    {
                        return false;
                    }

                    sourceId = sequence.Id;
                    sourceName = sequence.Name;
                    return true;
                }
            case DirectorRecordingSourceType.Follow:
                {
                    CameraFollowRig? rig = FindFollowRig(selector);
                    if (rig is null)
                    {
                        return false;
                    }

                    sourceId = rig.Id;
                    sourceName = rig.Name;
                    return true;
                }
            case DirectorRecordingSourceType.Workflow:
                {
                    DirectorWorkflow? workflow = FindWorkflow(selector);
                    if (workflow is null)
                    {
                        return false;
                    }

                    sourceId = workflow.Id;
                    sourceName = workflow.Name;
                    return true;
                }
            default:
                return false;
        }
    }

    private string RecordingSourceLabel(DirectorRecordingTrack track)
    {
        string name = track.SourceType switch
        {
            DirectorRecordingSourceType.Program => Lang.Get("vsdirector:record-source-program"),
            DirectorRecordingSourceType.StaticCamera => library?.Find(track.SourceId)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Path => library?.FindPath(track.SourceId)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Sequence => library?.FindSequence(track.SourceId)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Follow => library?.FindFollowRig(track.SourceId)?.Name ?? string.Empty,
            DirectorRecordingSourceType.Workflow => library?.FindWorkflow(track.SourceId)?.Name ?? string.Empty,
            _ => string.Empty
        };
        if (name.Length == 0)
        {
            name = Lang.Get("vsdirector:record-source-missing");
        }

        return Lang.Get(
            "vsdirector:record-source-label",
            RecordingSourceTypeLabel(track.SourceType),
            name);
    }

    internal string ReplayExportSourceLabel(DirectorRecordingTrack track)
        => RecordingSourceLabel(track);

    private bool RecordingTrackSourceExists(DirectorRecordingTrack track)
    {
        if (library is null)
        {
            return false;
        }

        return track.SourceType switch
        {
            DirectorRecordingSourceType.Program => track.SourceId == Guid.Empty,
            DirectorRecordingSourceType.StaticCamera => library.Find(track.SourceId) is not null,
            DirectorRecordingSourceType.Path => library.FindPath(track.SourceId) is { Keyframes.Count: >= 2 },
            DirectorRecordingSourceType.Sequence => false,
            DirectorRecordingSourceType.Follow => library.FindFollowRig(track.SourceId) is not null,
            DirectorRecordingSourceType.Workflow => false,
            DirectorRecordingSourceType.ReplayScene => replay?.ViewportTimeline.Scenes.Any(scene =>
                scene.Id == track.SourceId) == true,
            _ => false
        };
    }

    private (bool Success, string Error) ApplyTimedRecordingTrack(
        DirectorRecordingTrack track,
        double replayTimeSeconds)
        => ApplyTimedRecordingTrackCore(track, replayTimeSeconds, synchronizeReplayActorPath: true);

    private (bool Success, string Error) ApplyTimedRecordingTrackCore(
        DirectorRecordingTrack track,
        double replayTimeSeconds,
        bool synchronizeReplayActorPath,
        bool? loopOverride = null)
    {
        if (library is null || controller is null)
        {
            return (false, "world-not-ready");
        }

        if (track.WorkflowSegments.Count > 0)
        {
            DirectorWorkflowExportSegment[] segments = track.WorkflowSegments
                .OrderBy(segment => segment.StartSeconds)
                .ThenBy(segment => segment.EndSeconds)
                .ToArray();
            DirectorWorkflowExportSegment segment = segments.LastOrDefault(candidate =>
                replayTimeSeconds >= candidate.StartSeconds - DirectorWorkflowPlanner.TimeEpsilon
                && replayTimeSeconds < candidate.EndSeconds - DirectorWorkflowPlanner.TimeEpsilon)
                ?? segments.LastOrDefault(candidate => candidate.StartSeconds <= replayTimeSeconds)
                ?? segments[0];
            DirectorRecordingTrack shot = new()
            {
                Id = segment.CardId,
                Name = segment.CardName,
                SourceType = segment.SourceType,
                SourceId = segment.SourceId,
                Enabled = true
            };
            return ApplyTimedRecordingTrackCore(
                shot,
                DirectorWorkflowPlanner.SegmentLocalTime(segment, replayTimeSeconds),
                synchronizeReplayActorPath: false,
                loopOverride: loopOverride);
        }

        double sourceTimeSeconds = Math.Max(
            0d,
            replayTimeSeconds - track.ExportStartSeconds.GetValueOrDefault());
        string error;
        switch (track.SourceType)
        {
            case DirectorRecordingSourceType.StaticCamera:
                {
                    CameraPreset? camera = library.Find(track.SourceId);
                    return camera is null
                        ? (false, "record-source-missing")
                        : (controller.TrySetReplayPose(camera, out error), error);
                }
            case DirectorRecordingSourceType.Path:
                {
                    CameraPath? path = library.FindPath(track.SourceId);
                    if (path is null)
                    {
                        return (false, "replay-export-paths");
                    }

                    double pathTimeSeconds;
                    if (synchronizeReplayActorPath)
                    {
                        pathTimeSeconds = DirectorActorPathPlayback.ResolvePlaybackTime(
                            path,
                            replay?.ActiveReplayId ?? Guid.Empty,
                            replayTimeSeconds,
                            sourceTimeSeconds);
                    }
                    else
                    {
                        CameraPathTime resolved = CameraPathSampler.ResolvePlaybackTime(
                            path.DurationSeconds,
                            sourceTimeSeconds * CameraPathTiming.NormalizePlaybackSpeed(path.PlaybackSpeed),
                            loopOverride is null
                                ? path.LoopMode
                                : loopOverride.Value
                                    ? CameraPathLoopMode.Loop
                                    : CameraPathLoopMode.Once,
                            reverse: false);
                        pathTimeSeconds = resolved.TimeSeconds;
                    }

                    if (!CameraPathSampler.TryEvaluate(path, pathTimeSeconds, out DirectorKeyframe frame))
                    {
                        return (false, "replay-export-paths");
                    }

                    return (controller.TrySetReplayPose(frame.ToPreset(path.Id, path.Name), out error), error);
                }
            case DirectorRecordingSourceType.Sequence:
                {
                    CameraSequence? sequence = library.FindSequence(track.SourceId);
                    if (sequence is null
                    || !CameraSequenceSampler.TryResolve(
                        sequence,
                        sourceTimeSeconds,
                        loopOverride is null
                            ? null
                            : loopOverride.Value
                                ? CameraSequenceLoopMode.Loop
                                : CameraSequenceLoopMode.Once,
                        out CameraSequenceTime time)
                        || time.ShotIndex < 0
                        || time.ShotIndex >= sequence.Shots.Count)
                    {
                        return (false, "record-source-missing");
                    }

                    CameraPreset? camera = library.Find(sequence.Shots[time.ShotIndex].CameraId);
                    return camera is null
                        ? (false, "record-source-missing")
                        : (controller.TrySetReplayPose(camera, out error), error);
                }
            case DirectorRecordingSourceType.Follow:
                {
                    CameraFollowRig? rig = library.FindFollowRig(track.SourceId);
                    Entity? target = rig is null ? null : ResolveFollowTarget(rig);
                    if (rig is null || target is null)
                    {
                        return (false, "record-source-missing");
                    }

                    return (controller.TrySetReplayFollowPose(rig, target, sourceTimeSeconds, out error), error);
                }
            case DirectorRecordingSourceType.ReplayScene:
                {
                    DirectorReplayViewportTimeline timeline = replay?.ViewportTimeline
                        ?? new DirectorReplayViewportTimeline();
                    if (timeline.ActiveSceneId != track.SourceId
                        || timeline.Scenes.All(scene => scene.Id != track.SourceId))
                    {
                        return (false, "replay-export-scene-missing");
                    }
                    UpdateReplayViewportTimeline();
                    return (true, string.Empty);
                }
            default:
                return (false, "replay-export-tracks");
        }
    }

    private static string RecordingSourceTypeLabel(DirectorRecordingSourceType sourceType)
    {
        return Lang.Get(sourceType switch
        {
            DirectorRecordingSourceType.StaticCamera => "vsdirector:record-source-static",
            DirectorRecordingSourceType.Path => "vsdirector:record-source-path",
            DirectorRecordingSourceType.Sequence => "vsdirector:record-source-sequence",
            DirectorRecordingSourceType.Follow => "vsdirector:record-source-follow",
            DirectorRecordingSourceType.Workflow => "vsdirector:record-source-workflow",
            DirectorRecordingSourceType.ReplayScene => "vsdirector:replay-export-scene",
            _ => "vsdirector:record-source-program"
        });
    }

    private (bool Success, string Error) ActivateRecordingTrack(
        DirectorRecordingTrack track,
        bool prepare)
    {
        if (!track.Enabled)
        {
            return (false, Lang.Get("vsdirector:record-track-disabled", track.Name));
        }

        if (prepare
            && directWorkflowExpectedId is Guid expectedWorkflowId
            && (track.SourceType != DirectorRecordingSourceType.Workflow
                || track.SourceId != expectedWorkflowId))
        {
            return (false, Lang.Get("vsdirector:workflow-assignment-mismatch"));
        }

        if (track.SourceType == DirectorRecordingSourceType.Workflow)
        {
            bool replayDriven = workflowReplayRecording is { } workflowReplay
                && workflowReplay.WorkflowId == track.SourceId
                && workflowReplay.ReplayId == replay?.ActiveReplayId;
            bool actionDriven = actionWorkflowVideoRecordingId == track.SourceId;
            if (!replayDriven && !actionDriven)
            {
                return (false, Lang.Get("vsdirector:workflow-record-select-replay"));
            }

            string workflowError;
            if (!prepare
                && actionDriven
                && liveActionWorkflowPlayback?.Workflow.Id != track.SourceId
                && !TryStartLiveActionWorkflow(track.SourceId, out workflowError))
            {
                return (false, workflowError);
            }
            bool workflowSuccess = prepare
                ? PrepareLiveWorkflow(track.SourceId, out workflowError)
                : StartLiveWorkflow(track.SourceId, replayDriven, out workflowError);
            return workflowSuccess
                ? (true, string.Empty)
                : (false, LiveWorkflowErrorText(workflowError, track.Name));
        }

        bool success = track.SourceType switch
        {
            DirectorRecordingSourceType.Program => true,
            DirectorRecordingSourceType.StaticCamera => TryActivateCamera(track.SourceId, showNotification: !prepare),
            DirectorRecordingSourceType.Path => prepare
                ? PrepareRecordingPath(track.SourceId)
                : TryPlayPath(
                    track.SourceId,
                    reverse: false,
                    showNotification: true,
                    loopOverride: CameraPathLoopMode.Once),
            DirectorRecordingSourceType.Sequence => prepare
                ? PrepareRecordingSequence(track.SourceId)
                : TryPlaySequence(
                    track.SourceId,
                    showNotification: true,
                    loopOverride: CameraSequenceLoopMode.Once),
            DirectorRecordingSourceType.Follow => prepare
                ? PrepareRecordingFollow(track.SourceId)
                : TryActivateFollowRig(track.SourceId, showNotification: true),
            _ => false
        };
        return success
            ? (true, string.Empty)
            : (false, Lang.Get("vsdirector:record-track-activation-failed", track.Name));
    }

    private static string LiveWorkflowErrorText(string error, string workflowName)
    {
        return error switch
        {
            "workflow-live-overlap" => Lang.Get("vsdirector:workflow-live-overlap"),
            "workflow-empty" => Lang.Get("vsdirector:workflow-empty"),
            "workflow-not-prepared" => Lang.Get("vsdirector:workflow-not-prepared"),
            "workflow-camera-failed" => Lang.Get("vsdirector:workflow-camera-failed"),
            "workflow-dimension" => Lang.Get("vsdirector:workflow-dimension"),
            _ => Lang.Get("vsdirector:record-track-activation-failed", workflowName)
        };
    }

    private bool PrepareRecordingPath(Guid pathId)
    {
        if (!TryPlayPath(
            pathId,
            reverse: false,
            showNotification: false,
            loopOverride: CameraPathLoopMode.Once))
        {
            return false;
        }

        CameraPreset? firstPose = controller?.CurrentPose;
        return firstPose is not null
            && controller!.TrySetReplayPose(firstPose, out _);
    }

    private bool PrepareRecordingSequence(Guid sequenceId)
    {
        if (!TryPlaySequence(
            sequenceId,
            showNotification: false,
            loopOverride: CameraSequenceLoopMode.Once))
        {
            return false;
        }

        CameraPreset? firstPose = controller?.CurrentPose;
        return firstPose is not null
            && controller!.TrySetReplayPose(firstPose, out _);
    }

    private bool PrepareRecordingFollow(Guid followId)
    {
        CameraFollowRig? rig = library?.FindFollowRig(followId);
        Entity? target = rig is null ? null : ResolveFollowTarget(rig);
        return rig is not null
            && target is not null
            && controller?.TrySetReplayFollowPose(rig, target, 0d, out _) == true;
    }

    private bool IsLiveWorkflowValid(Guid workflowId)
        => TryPlanLiveWorkflow(workflowId, out _, out _, out _);

    private bool PrepareLiveWorkflow(Guid workflowId, out string error)
    {
        if (liveWorkflowRunner is null)
        {
            error = "world-not-ready";
            return false;
        }

        if (!TryPlanLiveWorkflow(workflowId, out DirectorWorkflow? workflow,
                out DirectorWorkflowExportPass? pass, out error))
        {
            return false;
        }

        return liveWorkflowRunner.Prepare(workflow!, new[] { pass! }, out error);
    }

    private bool StartLiveWorkflow(Guid workflowId, out string error)
        => StartLiveWorkflow(workflowId, replayDriven: false, out error);

    private bool StartLiveWorkflow(Guid workflowId, bool replayDriven, out string error)
    {
        if (liveWorkflowRunner is null)
        {
            error = "world-not-ready";
            return false;
        }

        if ((!liveWorkflowRunner.IsPrepared || liveWorkflowRunner.WorkflowId != workflowId)
            && !PrepareLiveWorkflow(workflowId, out error))
        {
            return false;
        }

        bool started = replayDriven
            ? liveWorkflowRunner.Start(workflowId, () => replay?.PlaybackTimeSeconds ?? 0d, out error)
            : liveWorkflowRunner.Start(workflowId, out error);
        if (started && replayDriven)
        {
            replay?.SetPaused(false);
        }
        return started;
    }

    private bool TryPlanLiveWorkflow(
        Guid workflowId,
        out DirectorWorkflow? workflow,
        out DirectorWorkflowExportPass? pass,
        out string error)
    {
        workflow = library?.FindWorkflow(workflowId);
        pass = null;
        error = string.Empty;
        if (workflow is null)
        {
            error = "record-source-missing";
            return false;
        }

        IReadOnlyList<DirectorWorkflowExportPass> passes = DirectorWorkflowPlanner.Create(
            workflow,
            card => WorkflowSourceExists(card.SourceType, card.SourceId));
        if (passes.Count == 0)
        {
            error = "workflow-empty";
            return false;
        }
        if (passes.Count != 1 || passes[0].SplitForOverlap)
        {
            error = "workflow-live-overlap";
            return false;
        }
        pass = passes[0];
        return true;
    }

    private void RegisterConfiguredRecordingWorker()
    {
        if (recording is null || library is null)
        {
            return;
        }
        if (assignedRecordingTrackId is Guid trackId
            && library.FindRecordingTrack(trackId) is { Enabled: true } track)
        {
            recording.RegisterWorker(track);
            return;
        }

        recording.UnregisterWorker();
        if (assignedRecordingTrackId is not null)
        {
            assignedRecordingTrackId = null;
            SaveConfig();
        }
    }

    private void ApplyRuntimeConfig()
    {
        if (!config.EnableVsmc2Integration)
        {
            vsmc2FileWatcher?.Stop();
        }
        if (!config.EnableGreenScreenSupport || !config.EnableGreenScreenModels)
        {
            greenScreenPreviewRenderer?.Stop();
        }
        if (!config.EnableExperimentalOffscreenMultiView && OffscreenCaptureBusy)
        {
            _ = StopOffscreenCaptureInternal(showMessage: false, out _);
        }
        controller?.SetHideHud(config.HideHudOnCamera);
        controller?.SetInputLock(config.LockPlayerInput);
        if (offscreenRenderer is not null)
        {
            offscreenRenderer.UseIndependentPlayerRenderer =
                config.OffscreenUseIndependentPlayerRenderer;
        }
        recording?.RefreshConfiguration();
        replay?.RefreshConfiguration();
        RegisterConfiguredRecordingWorker();

        if (!config.AutoRecordReplay)
        {
            automaticReplayAuthorizationPending = false;
            if (automaticReplayRecording
                && !StopAutomaticReplay(continuation: null, out string stopError))
            {
                capi?.Logger.Warning(
                    "VS Director: automatic replay recording did not stop after being disabled: {0}",
                    stopError);
            }

            return;
        }

        if (!IsWorldReady
            || capi is null
            || anchorClient is null
            || !HasPermission(DirectorPermission.ReplayCapture)
            || !HasPermission(DirectorPermission.RemoteChunkAccess))
        {
            return;
        }

        if (!anchorClient.AccessKnown)
        {
            automaticReplayAuthorizationPending = true;
            anchorClient.RequestAccess();
            return;
        }

        if (anchorClient.AccessAllowed)
        {
            string worldId = capi.World.SavegameIdentifier ?? string.Empty;
            capi.Event.EnqueueMainThreadTask(
                () => TryStartAutomaticReplay(worldId),
                "vsdirector-enable-auto-replay");
        }
    }

    private void ReconcileRecordingAssignment()
    {
        if (library is null || assignedRecordingTrackId is not Guid trackId)
        {
            return;
        }

        if (library.FindRecordingTrack(trackId) is { Enabled: true })
        {
            return;
        }

        assignedRecordingTrackId = null;
        recording?.UnregisterWorker();
        SaveConfig();
    }

    private void SetRecordingInputLock(bool locked)
    {
        recordingInputLock = locked;
        controller?.SetExternalInputLock(recordingInputLock || replayInputLock);
    }

    private void SetReplayInputLock(bool locked)
    {
        replayInputLock = locked;
        controller?.SetExternalInputLock(recordingInputLock || replayInputLock);
    }

    private void OnRecordingStateChanged(DirectorRecordingState state, string detail)
    {
        if (state == DirectorRecordingState.Stopping)
        {
            liveWorkflowRunner?.Hold();
            if (workflowReplayRecording is not null)
            {
                replay?.SetPaused(true);
            }
        }
        else if ((state is DirectorRecordingState.Idle or DirectorRecordingState.Error)
            && liveWorkflowRunner?.IsPrepared == true)
        {
            liveWorkflowRunner.Reset();
            controller?.Deactivate();
            anchorClient?.StopAnchor();
        }
        if (state is DirectorRecordingState.Idle or DirectorRecordingState.Error)
        {
            Guid? actionWorkflowId = actionWorkflowVideoRecordingId;
            actionWorkflowVideoRecordingId = null;
            if (actionWorkflowId is Guid actionId && liveActionWorkflowPlayback?.Workflow.Id == actionId)
            {
                StopLiveActionWorkflow();
            }
            RestoreWorkflowRecordingAssignment();
            if (workflowReplayRecording is not null)
            {
                replay?.SetPaused(true);
                workflowReplayRecording = null;
            }
        }
        else if (state == DirectorRecordingState.Recording && workflowReplayRecording is { } session)
        {
            session.RecordingStarted = true;
        }

        HandleTakeRecordingState(state, detail);
        if ((state is DirectorRecordingState.Idle or DirectorRecordingState.Error)
            && config.AutoRecordReplay
            && activeTakeCapture is null
            && capi is not null)
        {
            string worldId = capi.World.SavegameIdentifier ?? string.Empty;
            capi.Event.EnqueueMainThreadTask(
                () => TryStartAutomaticReplay(worldId),
                "vsdirector-resume-auto-replay");
        }
        if (!config.ShowSwitchNotifications)
        {
            return;
        }

        switch (state)
        {
            case DirectorRecordingState.Armed:
                Show(Lang.Get("vsdirector:record-armed", detail));
                break;
            case DirectorRecordingState.Recording:
                Show(Lang.Get("vsdirector:record-started", detail, RecordingHotkeyLabel));
                break;
            case DirectorRecordingState.Idle when !string.IsNullOrWhiteSpace(detail):
                Show(Lang.Get("vsdirector:record-stopped", detail));
                if (!string.IsNullOrWhiteSpace(recording?.LastError))
                {
                    Show(Lang.Get("vsdirector:record-postprocess-warning", recording.LastError));
                }

                break;
            case DirectorRecordingState.Error:
                Show(RecordingErrorText(detail));
                break;
        }
    }

    private void OnRecordingSourceCaptureStopped(DirectorRecordingTrack track)
    {
        if (track.SourceType != DirectorRecordingSourceType.Workflow)
        {
            return;
        }

        liveWorkflowRunner?.Reset();
        controller?.Deactivate();
        anchorClient?.StopAnchor();
    }

    private void RestoreWorkflowRecordingAssignment()
    {
        if (!restoreWorkflowAssignmentPending)
        {
            directWorkflowExpectedId = null;
            directWorkflowAssignedTrackId = null;
            return;
        }

        bool assignmentUnchanged = directWorkflowAssignedTrackId is Guid directTrackId
            && assignedRecordingTrackId == directTrackId;
        if (assignmentUnchanged)
        {
            Guid? restored = workflowPreviousAssignment is Guid previousId
                && library?.FindRecordingTrack(previousId) is { Enabled: true }
                    ? previousId
                    : null;
            assignedRecordingTrackId = restored;
            SaveConfig();
            RegisterConfiguredRecordingWorker();
        }

        restoreWorkflowAssignmentPending = false;
        workflowPreviousAssignment = null;
        directWorkflowExpectedId = null;
        directWorkflowAssignedTrackId = null;
    }

    private bool CanStartTakeReplay(out string error)
    {
        error = string.Empty;
        if (replay is null)
        {
            error = "replay-unavailable";
            return false;
        }

        if (replay.IsRecording)
        {
            error = "replay-recording-active";
            return false;
        }

        if (replay.HasPendingSave)
        {
            error = replay.IsSaving ? "replay-save-in-progress" : "replay-save-retry-required";
            return false;
        }

        if (replay.IsPlaying || replay.IsExporting || replay.IsLoading)
        {
            error = "replay-playback-active";
            return false;
        }

        return true;
    }

    private void HandleTakeRecordingState(DirectorRecordingState state, string detail)
    {
        switch (state)
        {
            case DirectorRecordingState.Armed:
                if (config.RecordReplayWithVideo
                    && recording?.ActiveTrack?.SourceType != DirectorRecordingSourceType.Workflow
                    && activeTakeCapture is null)
                {
                    BeginTakeReplayCapture(detail);
                }

                break;
            case DirectorRecordingState.Stopping:
                StopTakeReplayCapture();
                break;
            case DirectorRecordingState.Error:
                StopTakeReplayCapture();
                FinalizeTakeManifest();
                break;
            case DirectorRecordingState.Idle when activeTakeCapture is not null:
                StopTakeReplayCapture();
                FinalizeTakeManifest();
                break;
        }
    }

    private void BeginTakeReplayCapture(string trackDetail)
    {
        if (replay is null || recording is null)
        {
            return;
        }

        if (!CanStartTakeReplay(out string availabilityError))
        {
            Show(ReplayErrorText(availabilityError, string.Empty));
            recording.RequestStop(out _);
            return;
        }

        DirectorRecordingTrack? track = recording.ActiveTrack;
        if (track is null)
        {
            Show(Lang.Get("vsdirector:take-replay-start-failed", "missing-track"));
            recording.RequestStop(out _);
            return;
        }

        string suffix = string.IsNullOrWhiteSpace(trackDetail) ? track.Name : trackDetail;
        string replayName = $"Take {DateTime.Now:yyyy-MM-dd HH-mm-ss} - {suffix}";
        if (!replay.StartRecording(replayName, out string error))
        {
            Show(Lang.Get("vsdirector:take-replay-start-failed", error));
            recording.RequestStop(out _);
            return;
        }

        activeTakeCapture = new ActiveTakeCapture(
            recording.SessionId ?? string.Empty,
            track,
            replayName,
            DateTimeOffset.UtcNow);
        lastTakeManifestPath = string.Empty;
        Show(Lang.Get("vsdirector:take-replay-started", replayName));
    }

    private void StopTakeReplayCapture()
    {
        ActiveTakeCapture? take = activeTakeCapture;
        if (take is null || take.StopAttempted)
        {
            return;
        }

        take.StopAttempted = true;
        if (take.ReplayInfo is not null)
        {
            return;
        }

        string error = "replay-unavailable";
        if (replay?.RequestStopRecording(out error) == true)
        {
            pendingReplaySavePurpose = ReplaySavePurpose.Take;
            Show(Lang.Get("vsdirector:replay-recording-saving", take.ReplayName));
        }
        else
        {
            take.Error = string.IsNullOrWhiteSpace(error) ? "replay-not-recording" : error;
            Show(ReplayErrorText(take.Error, take.ReplayName));
        }
    }

    private void FinalizeTakeManifest()
    {
        ActiveTakeCapture? take = activeTakeCapture;
        if (take is null)
        {
            return;
        }

        if (take.ReplayInfo is null)
        {
            if (replay?.HasPendingSave == true)
            {
                return;
            }
            activeTakeCapture = null;
            return;
        }

        try
        {
            string outputPath = recording?.LastOutputPath ?? string.Empty;
            lastTakeManifestPath = DirectorTakeFiles.WriteManifest(
                take.SessionId,
                take.Track,
                outputPath,
                take.ReplayInfo,
                take.StartedUtc,
                recording?.LastActualStartUtc ?? default,
                recording?.LastError ?? take.Error);
            Show(Lang.Get(
                "vsdirector:take-complete",
                outputPath,
                take.ReplayInfo.Path,
                lastTakeManifestPath));
        }
        catch (Exception exception)
        {
            capi?.Logger.Warning("VS Director: failed to write take manifest: {0}", exception.Message);
        }
        finally
        {
            activeTakeCapture = null;
        }
    }

    internal string TakeOutputStatusText()
    {
        if (activeTakeCapture is not null)
        {
            return activeTakeCapture.ReplayInfo is null
                ? Lang.Get("vsdirector:take-status-capturing", activeTakeCapture.ReplayName)
                : Lang.Get("vsdirector:take-status-replay-saved", activeTakeCapture.ReplayInfo.Path);
        }

        return lastTakeManifestPath.Length > 0
            ? Lang.Get("vsdirector:take-status-complete", lastTakeManifestPath)
            : string.Empty;
    }

    private void OnRecordingServerStatus(DirectorRecordingSessionStatus status)
    {
        latestRecordingServerStatus = status;
        workspace?.Refresh();
        if (status.Phase == (int)DirectorRecordingWorkerPhase.Error
            && recording?.State != DirectorRecordingState.Requesting)
        {
            Show(RecordingErrorText(status.Message));
        }
    }

    private static string RecordingStateLabel(DirectorRecordingState state)
    {
        return Lang.Get(state switch
        {
            DirectorRecordingState.Requesting => "vsdirector:record-state-requesting",
            DirectorRecordingState.Armed => "vsdirector:record-state-armed",
            DirectorRecordingState.Starting => "vsdirector:record-state-starting",
            DirectorRecordingState.Recording => "vsdirector:record-state-recording",
            DirectorRecordingState.Stopping => "vsdirector:record-state-stopping",
            DirectorRecordingState.Error => "vsdirector:record-state-error",
            _ => "vsdirector:record-state-idle"
        });
    }

    private static string CompactStatusPart(string value, int maxLength)
        => value.Length <= maxLength
            ? value
            : value[..Math.Max(1, maxLength - 3)] + "...";

    private static string RecordingErrorText(string code)
    {
        string key = code switch
        {
            "permission" => "vsdirector:permission-denied",
            "worker-limit" => "vsdirector:record-worker-limit",
            "record-track-not-registered" => "vsdirector:workflow-assignment-mismatch",
            "invalid-track" or "invalid-track-type" or "invalid-track-source" => "vsdirector:record-invalid-track",
            "session-active" => "vsdirector:record-session-active",
            "recording-busy" or "client-busy" => "vsdirector:record-busy",
            "recording-idle" or "no-session" => "vsdirector:record-not-active",
            "recording-unavailable" => "vsdirector:record-unavailable",
            "obs-not-ready" => "vsdirector:record-obs-not-ready",
            _ => "vsdirector:record-error"
        };
        return key == "vsdirector:record-error" ? Lang.Get(key, code) : Lang.Get(key);
    }

    private static string ReplayErrorText(string code, string selector)
    {
        return code switch
        {
            "world-not-ready" => Lang.Get("vsdirector:world-not-ready"),
            "replay-recording-active" => Lang.Get("vsdirector:replay-recording-active"),
            "replay-not-recording" => Lang.Get("vsdirector:replay-not-recording"),
            "replay-save-in-progress" => Lang.Get("vsdirector:replay-save-in-progress"),
            "replay-save-retry-required" => Lang.Get("vsdirector:replay-save-retry-required"),
            "replay-indexing" => Lang.Get("vsdirector:replay-indexing"),
            "replay-not-found" => Lang.Get("vsdirector:replay-not-found", selector),
            "replay-no-actors" => Lang.Get("vsdirector:replay-no-actors"),
            "replay-world-suspend-failed" => Lang.Get("vsdirector:replay-world-suspend-failed"),
            "replay-not-playing" => Lang.Get("vsdirector:replay-not-playing"),
            "replay-export-active" => Lang.Get("vsdirector:replay-export-active"),
            "replay-export-paths" => Lang.Get("vsdirector:replay-export-paths"),
            "replay-export-tracks" => Lang.Get("vsdirector:replay-export-tracks"),
            "replay-export-range" => Lang.Get("vsdirector:replay-export-range"),
            "replay-voice-export-empty" => Lang.Get("vsdirector:replay-voice-export-empty"),
            "replay-voice-export-path" => Lang.Get("vsdirector:replay-voice-export-path"),
            "replay-playback-active" => Lang.Get("vsdirector:take-replay-playback-active"),
            "replay-unavailable" => Lang.Get("vsdirector:replay-unavailable"),
            _ => Lang.Get("vsdirector:replay-error", code)
        };
    }

    private bool EnsureReady(out string message)
    {
        if (IsWorldReady)
        {
            message = string.Empty;
            return true;
        }

        message = Lang.Get("vsdirector:world-not-ready");
        return false;
    }

    private bool EnsureEditAccess(out string message)
    {
        if (!EnsureReady(out message))
        {
            Show(message);
            return false;
        }

        return true;
    }

    private bool EnsureFeatureEditAccess(
        DirectorPermission permission,
        out string message)
    {
        if (!EnsureEditAccess(out message))
        {
            return false;
        }

        if (!EnsurePermission(permission, out message))
        {
            Show(message);
            return false;
        }

        return true;
    }

    private void RefreshWorkspaceDialogs()
    {
        if (IsShuttingDown) return;
        workspace?.Refresh();
        pathPreview?.Refresh();
    }

    private void OnReplayPlaybackStateChanged()
    {
        if (IsShuttingDown) return;
        RefreshWorkspaceDialogs();
        if (replay?.IsPlaying != true
            && replay?.IsLoading != true
            && replay?.IsRecording != true
            && replay?.HasPendingSave != true
            && activeTakeCapture is null)
        {
            QueueAutomaticReplayRestart();
        }
    }

    private void OnReplayRecordingSaveCompleted(DirectorReplayInfo? info, string error)
    {
        ReplaySavePurpose purpose = pendingReplaySavePurpose;
        if (info is null)
        {
            if (activeTakeCapture is not null)
            {
                activeTakeCapture.StopAttempted = false;
                activeTakeCapture.Error = error;
            }
            Show(ReplayErrorText(error, activeTakeCapture?.ReplayName ?? string.Empty));
            RefreshWorkspaceDialogs();
            return;
        }

        pendingReplaySavePurpose = ReplaySavePurpose.None;
        Action? continuation = pendingAutomaticReplayAction;
        pendingAutomaticReplayAction = null;
        if (purpose == ReplaySavePurpose.Take || activeTakeCapture is not null)
        {
            ActiveTakeCapture? take = activeTakeCapture;
            if (take is not null)
            {
                take.ReplayInfo = info;
                take.Error = string.Empty;
                Show(Lang.Get("vsdirector:take-replay-saved", info.Name, info.Path));
                if (recording?.State is DirectorRecordingState.Idle or DirectorRecordingState.Error)
                {
                    FinalizeTakeManifest();
                    QueueAutomaticReplayRestart();
                }
            }
        }
        else if (purpose == ReplaySavePurpose.Automatic)
        {
            if (config.ShowSwitchNotifications)
            {
                Show(Lang.Get("vsdirector:auto-replay-saved", info.Name));
            }
        }
        else
        {
            Show(Lang.Get(
                "vsdirector:replay-recording-saved",
                info.Name,
                info.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                info.ActorCount,
                info.Path));
        }

        if (continuation is not null && !IsShuttingDown)
        {
            continuation();
        }

        RefreshWorkspaceDialogs();
    }

    private static bool IsValidShotDuration(double durationSeconds, out string message)
    {
        if (double.IsFinite(durationSeconds)
            && durationSeconds >= CameraSequenceSampler.MinShotDurationSeconds
            && durationSeconds <= CameraSequenceSampler.MaxShotDurationSeconds)
        {
            message = string.Empty;
            return true;
        }

        message = Lang.Get(
            "vsdirector:invalid-shot-duration",
            CameraSequenceSampler.MinShotDurationSeconds,
            CameraSequenceSampler.MaxShotDurationSeconds);
        return false;
    }

    private bool EnsureDirectorAccess(out string message)
    {
        if (anchorClient is null)
        {
            message = Lang.Get("vsdirector:server-required");
            return false;
        }

        accessAttempted = true;
        if (!anchorClient.AccessKnown)
        {
            anchorClient.RequestAccess();
            message = Lang.Get("vsdirector:authorization-pending");
            return false;
        }

        if (!anchorClient.AccessAllowed)
        {
            anchorClient.RequestAccess();
            message = Lang.Get("vsdirector:authorization-pending");
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void OnClientTick(float deltaTime)
    {
        if (IsShuttingDown) return;
        voiceBridge.Drain();
        MonitorLiveOffscreenRecording();
        MonitorOffscreenCapture();
        UpdateVanillaActionLibrary();
        UpdateLiveActionWorkflow();
        replay?.OnGameTick(deltaTime);
        RestoreWorkspaceAfterReplayExport();
        UpdateReplayTimelinePathFollow();
        UpdatePendingWorkflowReplayRecording();
        recording?.OnGameTick();
        UpdateLiveWorkflowRecording();
        UpdatePendingRemoteFollow();
        UpdatePendingRemoteLiveOffscreen();
        controller?.OnGameTick(deltaTime);
        if (replayAttachedActorView is not null
            && controller?.ActiveFollowRigId != replayAttachedRigId)
        {
            ClearReplayActorAttachment();
        }
        PendingRemoteFollow? remoteFollow = pendingRemoteFollow;
        PendingRemoteLiveOffscreen? remoteOffscreen = pendingRemoteLiveOffscreen;
        bool replayIsActive = replay?.IsLoading == true || replay?.IsPlaying == true;
        bool replayOwnsRemoteChunkWindow = replayIsActive
            && remoteFollow is null
            && remoteOffscreen is null;
        int? replayAnchorRadiusOverride = replayOwnsRemoteChunkWindow ? 0 : null;
        CameraPreset? cameraAnchor = controller?.IsActive == true ? controller.CurrentPose : null;
        CameraPreset? replayAnchor = replay?.TryGetPlaybackAnchorPose(out CameraPreset replayPose) == true
            ? replayPose
            : null;
        CameraPreset? liveOffscreenAnchor = liveOffscreenSession?.CurrentPose is
            DirectorOffscreenCameraPose offscreenPose
                ? new CameraPreset
                {
                    X = offscreenPose.X,
                    Y = offscreenPose.Y,
                    Z = offscreenPose.Z,
                    Dimension = offscreenPose.Dimension,
                    Yaw = offscreenPose.Yaw,
                    Pitch = offscreenPose.Pitch,
                    Roll = offscreenPose.Roll,
                    Fov = offscreenPose.Fov
                }
                : null;
        anchorClient?.Update(
            remoteFollow?.EffectiveAnchorPose
                ?? remoteOffscreen?.EffectiveAnchorPose
                ?? cameraAnchor
                ?? replayAnchor
                ?? liveOffscreenAnchor,
            remoteFollow is not null
                || remoteOffscreen is not null
                || cameraAnchor is not null
                || replayAnchor is not null
                || liveOffscreenAnchor is not null,
            remoteFollow?.Rig.TargetPlayerUid
                ?? remoteOffscreen?.Rig.TargetPlayerUid
                ?? liveOffscreenFollowRig?.TargetPlayerUid
                ?? (cameraAnchor is not null ? controller?.ActiveFollowTargetPlayerUid : null),
            remoteFollow?.Rig.TargetEntityId
                ?? remoteOffscreen?.Rig.TargetEntityId
                ?? liveOffscreenFollowRig?.TargetEntityId
                ?? (cameraAnchor is not null ? controller?.ActiveFollowTargetEntityId : null),
            cameraAnchor is not null ? replayAnchor : null,
            replayAnchorRadiusOverride,
            replayOwnsRemoteChunkWindow);
        if (!HasPermission(DirectorPermission.RemoteChunkAccess) || replayIsActive)
        {
            chunkPreloadClient?.Stop();
        }
        else if (replay?.TryGetChunkPreloadPlan(out Guid replayId, out DirectorReplayPreloadPlan preloadPlan) == true)
        {
            chunkPreloadClient?.Update(replayId, preloadPlan);
        }
        else if (controller?.ActivePathId is Guid activePathId
                 && library?.FindPath(activePathId) is CameraPath activePath)
        {
            chunkPreloadClient?.Update(activePathId, DirectorReplayPreloadPlanner.Build(activePath));
        }
        else if (previewChunkPreloadId != Guid.Empty && previewChunkPreloadPlan is not null)
        {
            chunkPreloadClient?.Update(previewChunkPreloadId, previewChunkPreloadPlan);
        }
        else
        {
            chunkPreloadClient?.Stop();
        }
    }

    private string GetHotkeyLabel(string code, string fallback)
    {
        if (capi is null)
        {
            return fallback;
        }

        try
        {
            return capi.Input.GetHotKeyByCode(code)?.CurrentMapping?.ToString()
                ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void ConsumeVoiceFrame(DirectorVoicePcmFrame frame, bool replayEnabled, bool offscreenEnabled)
    {
        if (replayEnabled)
        {
            replay?.CaptureVoiceFrame(frame);
        }
        if (offscreenEnabled)
        {
            liveOffscreenSession?.CaptureVoiceFrame(frame, capi?.World.ElapsedMilliseconds ?? frame.TimestampMilliseconds);
        }
    }

    private void MonitorLiveOffscreenRecording()
    {
        if (liveOffscreenState != LiveOffscreenRecordingState.Recording
            || liveOffscreenSession is not DirectorLiveOffscreenSession session)
        {
            return;
        }

        if (session.OutputFailure.Length > 0)
        {
            liveOffscreenStatus = Lang.Get(
                "vsdirector:live-offscreen-output-failed",
                session.OutputFailure);
            Show(liveOffscreenStatus);
            _ = TryStopLiveOffscreenRecording(out _);
            return;
        }
        if (session.AudioFailure.Length == 0)
        {
            return;
        }

        liveOffscreenStatus = Lang.Get(
            "vsdirector:live-offscreen-audio-failed",
            session.AudioFailure);
        Show(liveOffscreenStatus);
        _ = TryStopLiveOffscreenRecording(out _);
    }

    private void MonitorOffscreenCapture()
    {
        if (!offscreenCaptureActive)
        {
            return;
        }

        string failure = offscreenSinks
            .Select(sink => sink.FailureMessage)
            .FirstOrDefault(message => message.Length > 0)
            ?? string.Empty;
        if (failure.Length == 0)
        {
            return;
        }

        capi?.Logger.Error("VS Director: offscreen output failed: {0}", failure);
        string terminalMessage = Lang.Get(
            "vsdirector:offscreen-runtime-failed",
            failure,
            string.Empty).Trim();
        _ = StopOffscreenCaptureInternal(
            showMessage: false,
            out _,
            terminalMessage);
    }

    private void PrepareReplayWorkspaceForRenderFrame()
    {
        // Export completion can happen in a render callback or an asynchronous
        // finalizer while game ticks are paused. Recover before the next main
        // camera/UI frame, never while an offscreen export pass is rendering.
        RestoreWorkspaceAfterReplayExport();
        UpdateReplayViewportTimeline();
    }

    private void UpdateReplayViewportTimeline()
    {
        if (replay?.IsPlaying != true
            || replay.IsLoading
            || capi is null
            || library is null
            || controller is null)
        {
            ResetReplayElementRuntime();
            return;
        }

        double replayTime = replay.PreparedPlaybackTimeSeconds;
        DirectorReplayViewportTimeline viewportTimeline = replay.ViewportTimeline;
        DirectorReplayCameraVisuals visualCamera = viewportTimeline.Visuals.Camera;
        controller.SetReplayElementClearView(false);
        replay.SetHiddenTrackedReplayEntity(0L);
        HashSet<Guid> enabledTrackIds = viewportTimeline.Tracks
            .Where(track => track.Enabled)
            .Select(track => track.Id)
            .ToHashSet();
        IReadOnlyList<DirectorReplayElementKeyframe> elementKeyframes = replay.ElementKeyframes
            .Where(keyframe => keyframe.TrackId == Guid.Empty
                ? !viewportTimeline.DisabledElementTracks.Contains(keyframe.TrackType)
                : enabledTrackIds.Contains(keyframe.TrackId))
            .ToArray();
        bool editingFree = replayViewportCameraAuthority == DirectorReplayCameraAuthority.ManualFree;
        if (replayElementRuntimeReplayId != replay.ActiveReplayId) controller.ResetReplayPreviewFollowState();
        CameraPreset? cameraPose = null;
        bool preserveReplayFollowState = false;
        Entity? clearViewElementTarget = null;
        DirectorReplayCameraGuideTarget? cameraGuideTarget = null;
        Guid activeCameraId = Guid.Empty;
        HashSet<DirectorReplayElementTrackType> appliedCameraTypes = new();
        float preservedFov = editingFree ? ReplayCameraGuideFov(viewportTimeline, replayTime)
            : controller.CurrentPose?.Fov ?? 70f;
        foreach (DirectorReplayElementTrack track in viewportTimeline.Tracks)
        {
            if (!track.Enabled
                || appliedCameraTypes.Contains(track.TrackType))
            {
                continue;
            }

            switch (track.TrackType)
            {
                case DirectorReplayElementTrackType.Camera:
                    if (!TryBuildReplayCameraElementPath(
                            viewportTimeline,
                            track,
                            out CameraPath elementPath,
                            out List<DirectorReplayViewportClip> cameraElements)
                        || replayTime < cameraElements[0].TimelineStartSeconds
                        || !CameraPathSampler.TryEvaluate(
                            elementPath,
                            replayTime,
                            out VSDirector.Data.CameraKeyframe evaluated))
                    {
                        continue;
                    }

                    DirectorReplayViewportClip active = cameraElements
                        .Last(item => item.TimelineStartSeconds <= replayTime);
                    cameraPose = evaluated.ToPreset(Guid.Empty, active.Name);
                    cameraPose.Fov = preservedFov;
                    cameraGuideTarget = null;
                    activeCameraId = active.Id;
                    preserveReplayFollowState = false;
                    clearViewElementTarget = null;
                    appliedCameraTypes.Add(track.TrackType);
                    break;

                case DirectorReplayElementTrackType.CameraOrbit:
                    if (!DirectorReplayElementTimeline.TrySample(
                            viewportTimeline.ElementKeyframes,
                            track.TrackType,
                            track.Id,
                            replayTime,
                            out DirectorReplayElementKeyframe orbit))
                    {
                        continue;
                    }

                    cameraPose = ReplayOrbitPose(orbit, cameraPose?.Fov ?? preservedFov);
                    cameraGuideTarget = new DirectorReplayCameraGuideTarget(
                        orbit.X,
                        orbit.Y,
                        orbit.Z,
                        orbit.Dimension);
                    activeCameraId = orbit.Id;
                    preserveReplayFollowState = false;
                    clearViewElementTarget = null;
                    appliedCameraTypes.Add(track.TrackType);
                    break;

                case DirectorReplayElementTrackType.TrackEntity:
                    if (!DirectorReplayElementTimeline.TrySample(
                            viewportTimeline.ElementKeyframes,
                            track.TrackType,
                            track.Id,
                            replayTime,
                            out DirectorReplayElementKeyframe tracking))
                    {
                        continue;
                    }

                    // Preserve the camera while its tracked actor is temporarily
                    // unavailable to avoid jumping to another camera track.
                    appliedCameraTypes.Add(track.TrackType);
                    Entity? target = replay.ResolveReplayActor(
                        tracking.EntityActorKey,
                        null,
                        tracking.EntityId);
                    if (target is null
                        || !TryCreateReplayTrackingTargetPose(
                            target,
                            tracking.BodyPart,
                            out DirectorReplayTrackedTargetPose targetPose))
                    {
                        continue;
                    }

                    if (!editingFree) replay.SetHiddenTrackedReplayEntity(target.EntityId);
                    cameraPose = ReplayTrackedEntityPose(
                        tracking,
                        targetPose,
                        cameraPose?.Fov ?? preservedFov);
                    cameraGuideTarget = new DirectorReplayCameraGuideTarget(
                        targetPose.X,
                        targetPose.Y,
                        targetPose.Z,
                        targetPose.Dimension,
                        target.EntityId);
                    activeCameraId = tracking.Id;
                    preserveReplayFollowState = false;
                    clearViewElementTarget = tracking.OpenClearView ? target : null;
                    break;

                case DirectorReplayElementTrackType.FollowEntity:
                    if (!DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
                            viewportTimeline.ElementKeyframes,
                            track.Id,
                            replayTime,
                            out DirectorReplayElementKeyframe followStart,
                            out _))
                    {
                        continue;
                    }

                    appliedCameraTypes.Add(track.TrackType);
                    Entity? followTarget = replay.ResolveReplayActor(
                        followStart.EntityActorKey,
                        null,
                        followStart.EntityId);
                    if (followTarget is null)
                    {
                        continue;
                    }

                    CameraFollowRig followRig = ReplayFollowEntityRig(followStart, followTarget);
                    if (!controller.TrySetReplayFollowElementPose(
                            followRig,
                            followTarget,
                            Math.Max(0d, replayTime - followStart.TimeSeconds),
                            out CameraPreset followedPose,
                            out _, applyToMainCamera: !editingFree))
                    {
                        continue;
                    }

                    cameraPose = followedPose;
                    cameraGuideTarget = new DirectorReplayCameraGuideTarget(
                        followTarget.Pos.X,
                        followTarget.Pos.Y,
                        followTarget.Pos.Z,
                        followTarget.Pos.Dimension,
                        followTarget.EntityId);
                    activeCameraId = followStart.Id;
                    preserveReplayFollowState = true;
                    clearViewElementTarget = followStart.FollowOpenClearView ? followTarget : null;
                    break;
            }
        }

        bool hasFov = DirectorReplayElementTimeline.TrySample(
            viewportTimeline,
            DirectorReplayElementTrackType.Fov,
            replayTime,
            out DirectorReplayElementKeyframe fov);
        bool hasShake = DirectorReplayElementTimeline.TrySample(
            viewportTimeline,
            DirectorReplayElementTrackType.CameraShake,
            replayTime,
            out _);
        replayShootingBasePose = cameraPose?.DeepClone() ?? replayShootingBasePose;
        cameraPose ??= hasShake
            || visualCamera.CameraShake
            || visualCamera.OverrideRoll
                ? (editingFree ? replayShootingBasePose?.DeepClone() : controller.CurrentPose)
                : null;
        if (cameraPose is not null)
        {
            cameraPose = ApplyReplayShootingCameraVisuals(cameraPose, viewportTimeline, replayTime);
            replayShootingPose = cameraPose.DeepClone();
            replayShootingSourceId = activeCameraId;
            if (editingFree)
            {
                pathPreview?.SetCurrentReplayCameraGuide(
                    new DirectorReplayCurrentCameraGuide(cameraPose, cameraGuideTarget));
                UpdateReplayElementEvents(replayTime, elementKeyframes);
                return;
            }
            bool poseApplied = preserveReplayFollowState
                ? controller.TrySetReplayFollowElementAdjustedPose(cameraPose, out _)
                : controller.TrySetReplayPose(cameraPose, out _);
            if (poseApplied)
            {
                controller.SetReplayElementClearView(clearViewElementTarget is not null, clearViewElementTarget);
                replayCameraState = DirectorReplayCameraState.FromSource(
                    DirectorReplayCameraMode.Fixed,
                    activeCameraId);
            }
        }
        else if (!editingFree && appliedCameraTypes.Count == 0
            && replayCameraState.Mode == DirectorReplayCameraMode.Fixed
            && controller.TryStartFreeCamera(out _))
        {
            replayCameraState = DirectorReplayCameraState.Free;
        }
        if (editingFree)
        {
            replayShootingPose = null;
            replayShootingSourceId = Guid.Empty;
            pathPreview?.SetCurrentReplayCameraGuide(null);
            UpdateReplayElementEvents(replayTime, elementKeyframes);
            return;
        }
        if (cameraPose is null)
        {
            replayShootingPose = null;
            replayShootingSourceId = Guid.Empty;
        }
        if (hasFov || visualCamera.OverrideFov)
        {
            controller.TrySetReplayFov(hasFov ? fov.Fov : visualCamera.Fov, out _);
            replayElementFovWasApplied = true;
        }
        else if (replayElementFovWasApplied)
        {
            controller.TrySetReplayFov(
                Math.Clamp(
                    capi.Settings.Int["fieldOfView"],
                    (int)DirectorReplayElementTimeline.MinFovDegrees,
                    (int)DirectorReplayElementTimeline.MaxFovDegrees),
                out _);
            replayElementFovWasApplied = false;
        }

        pathPreview?.SetCurrentReplayCameraGuide(
            replay.IsPlaying && cameraPose is not null
                ? new DirectorReplayCurrentCameraGuide(cameraPose, cameraGuideTarget)
                : null);

        UpdateReplayElementEvents(replayTime, elementKeyframes);
    }

    private IReadOnlyList<CameraPath> ReplayCameraElementPreviewPaths()
    {
        if (replay is null || library is null)
        {
            return Array.Empty<CameraPath>();
        }

        DirectorReplayViewportTimeline timeline = replay.ViewportTimeline;
        List<CameraPath> paths = new();
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == DirectorReplayElementTrackType.Camera))
        {
            if (TryBuildReplayCameraElementPath(timeline, track, out CameraPath path, out _))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private static CameraFollowRig ReplayFollowEntityRig(
        DirectorReplayElementKeyframe keyframe,
        Entity target)
        => new()
        {
            Id = keyframe.Id,
            Name = "Replay entity follow",
            TargetType = target is EntityPlayer
                ? CameraFollowTargetType.Player
                : CameraFollowTargetType.Entity,
            TargetEntityId = target.EntityId,
            TargetDisplayName = keyframe.EntityActorKey,
            ViewMode = keyframe.FollowViewMode,
            MotionMode = keyframe.FollowMotionMode,
            Anchor = keyframe.FollowAnchor,
            OpenClearView = keyframe.FollowOpenClearView,
            OffsetX = keyframe.FollowOffsetX,
            OffsetY = keyframe.FollowOffsetY,
            OffsetZ = keyframe.FollowOffsetZ,
            Distance = keyframe.FollowDistance,
            Height = keyframe.FollowHeight,
            OrbitAngleDegrees = keyframe.FollowOrbitAngleDegrees,
            OrbitSpeedDegreesPerSecond = keyframe.FollowOrbitSpeedDegreesPerSecond,
            SmoothTimeSeconds = keyframe.FollowSmoothTimeSeconds,
            DelaySeconds = keyframe.FollowDelaySeconds,
            Fov = keyframe.FollowFov
        };

    private IReadOnlyList<DirectorReplayOrbitGuide> ReplayOrbitElementPreviewGuides()
    {
        if (replay is null || capi is null)
        {
            return Array.Empty<DirectorReplayOrbitGuide>();
        }

        DirectorReplayViewportTimeline timeline = replay.ViewportTimeline;
        List<DirectorReplayOrbitGuide> guides = new();
        foreach (DirectorReplayElementTrack track in timeline.Tracks.Where(track =>
            track.Enabled && track.TrackType == DirectorReplayElementTrackType.CameraOrbit))
        {
            DirectorReplayOrbitGuide guide = DirectorReplayCameraGuide.BuildOrbit(
                timeline,
                track.Id,
                timeSeconds => ReplayCameraGuideFov(timeline, timeSeconds));
            if (guide.Samples.Count > 0 || guide.Keyframes.Count > 0)
            {
                guides.Add(guide);
            }
        }
        return guides;
    }

    private float ReplayCameraGuideFov(
        DirectorReplayViewportTimeline timeline,
        double timeSeconds)
    {
        if (DirectorReplayElementTimeline.TrySample(
                timeline,
                DirectorReplayElementTrackType.Fov,
                timeSeconds,
                out DirectorReplayElementKeyframe fov))
        {
            return fov.Fov;
        }
        if (timeline.Visuals.Camera.OverrideFov)
        {
            return timeline.Visuals.Camera.Fov;
        }

        return capi is null
            ? 70f
            : Math.Clamp(
                capi.Settings.Int["fieldOfView"],
                (int)DirectorReplayElementTimeline.MinFovDegrees,
                (int)DirectorReplayElementTimeline.MaxFovDegrees);
    }

    private bool TryBuildReplayCameraElementPath(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrack track,
        out CameraPath path,
        out List<DirectorReplayViewportClip> clips)
    {
        path = new CameraPath();
        clips = new List<DirectorReplayViewportClip>();
        if (library is null)
        {
            return false;
        }

        List<(DirectorReplayViewportClip Clip, CameraPreset Pose)> elements = timeline.Clips
            .Where(clip => clip.Enabled
                && clip.TrackId == track.Id
                && clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)
            .Select(clip => (
                Clip: clip,
                Pose: clip.CameraPose?.DeepClone() ?? library.Find(clip.SourceId)!))
            .Where(item => item.Pose is not null)
            .OrderBy(item => item.Clip.TimelineStartSeconds)
            .ThenBy(item => item.Clip.Id)
            .ToList();
        if (elements.Count == 0)
        {
            return false;
        }

        clips = elements.Select(item => item.Clip).ToList();
        path = new CameraPath
        {
            Id = track.Id,
            Name = string.IsNullOrWhiteSpace(track.Name) ? "Replay camera elements" : track.Name,
            Interpolation = CameraPathInterpolation.EaseInOut,
            PreviewVisible = true,
            Keyframes = elements
                .Select(item => VSDirector.Data.CameraKeyframe.FromPreset(
                    item.Pose,
                    item.Clip.TimelineStartSeconds,
                    CameraPathTiming.DefaultKeyframeDurationSeconds))
                .Select((frame, index) =>
                {
                    frame.InterpolationToNext = elements[index].Clip.CameraInterpolationToNext
                        ?? CameraPathInterpolation.EaseInOut;
                    return frame;
                })
                .ToList()
        };
        return true;
    }

    private static CameraPreset ReplayOrbitPose(
        DirectorReplayElementKeyframe orbit,
        float fov)
        => DirectorReplayCameraElementMath.EvaluateOrbit(orbit, fov);

    private static CameraPreset ReplayTrackedEntityPose(
        DirectorReplayElementKeyframe tracking,
        DirectorReplayTrackedTargetPose target,
        float fov)
        => DirectorReplayCameraElementMath.EvaluateTrack(tracking, target, fov);

    private void UpdateReplayElementEvents(
        double replayTime,
        IReadOnlyList<DirectorReplayElementKeyframe> elementKeyframes)
    {
        if (capi is null || replay is null)
        {
            return;
        }
        if (replayElementRuntimeReplayId != replay.ActiveReplayId
            || replayTime + 0.000001d < replayElementPreviousTime)
        {
            replayElementRuntimeReplayId = replay.ActiveReplayId;
            replayElementPreviousTime = -1d;
            replayElementAppliedBlocks.Clear();
        }

        foreach (IGrouping<string, DirectorReplayElementKeyframe> group in elementKeyframes
            .Where(keyframe => keyframe.TrackType == DirectorReplayElementTrackType.BlockOverride
                && keyframe.TimeSeconds <= replayTime + 0.000001d
                && keyframe.BlockCode.Length > 0)
            .GroupBy(keyframe => string.Create(CultureInfo.InvariantCulture,
                $"{keyframe.Dimension}:{keyframe.BlockX}:{keyframe.BlockY}:{keyframe.BlockZ}")))
        {
            DirectorReplayElementKeyframe blockKeyframe = group
                .OrderBy(keyframe => keyframe.TimeSeconds)
                .ThenBy(keyframe => keyframe.Id)
                .Last();
            if (replayElementAppliedBlocks.TryGetValue(group.Key, out Guid appliedId)
                && appliedId == blockKeyframe.Id)
            {
                continue;
            }
            Block? block = capi.World.GetBlock(new AssetLocation(blockKeyframe.BlockCode));
            if (block is null)
            {
                continue;
            }
            capi.World.BlockAccessor.SetBlock(
                block.BlockId,
                new BlockPos(
                    blockKeyframe.BlockX,
                    blockKeyframe.BlockY,
                    blockKeyframe.BlockZ,
                    blockKeyframe.Dimension));
            replayElementAppliedBlocks[group.Key] = blockKeyframe.Id;
        }
        replayElementPreviousTime = replayTime;
    }

    private void ResetReplayElementRuntime()
    {
        replayShootingPose = null;
        replayShootingBasePose = null;
        replayShootingSourceId = Guid.Empty;
        controller?.ResetReplayPreviewFollowState();
        controller?.SetReplayElementClearView(false);
        replayElementRuntimeReplayId = Guid.Empty;
        replayElementPreviousTime = -1d;
        replayElementFovWasApplied = false;
        replayElementAppliedBlocks.Clear();
        pathPreview?.SetCurrentReplayCameraGuide(null);
    }

    private void UpdateReplayTimelinePathFollow()
    {
        if (replayCameraState.Mode != DirectorReplayCameraMode.SyncedPath)
        {
            return;
        }

        Guid pathId = replayCameraState.SourceId;

        if (replay?.IsPlaying != true)
        {
            StopReplayActorPathFollow(deactivate: true);
            return;
        }

        if (replay.IsExporting)
        {
            replayCameraState = DirectorReplayCameraState.Inactive;
            return;
        }

        CameraPath? path = library?.FindPath(pathId);
        bool active = path is not null
            && (path.SourceReplayId == replay.ActiveReplayId
                ? TryActivateReplayActorPath(path, out _)
                : TryActivateReplayTimelinePath(path, out _));
        if (!active)
        {
            StopReplayActorPathFollow(deactivate: true);
        }
    }

    private void StopReplayActorPathFollow(bool deactivate)
    {
        if (replayCameraState.Mode != DirectorReplayCameraMode.SyncedPath)
        {
            return;
        }

        replayCameraState = DirectorReplayCameraState.Inactive;
        if (deactivate)
        {
            controller?.Deactivate();
            anchorClient?.StopAnchor();
        }
    }

    private void UpdateLiveWorkflowRecording()
    {
        if (liveWorkflowRunner?.TryTakeFailure(out string error) == true)
        {
            Show(LiveWorkflowErrorText(error, recording?.ActiveTrack?.Name ?? string.Empty));
            recording?.FailActiveSource(error);
            return;
        }

        if (liveWorkflowRunner?.TryTakeCompletion() == true)
        {
            if (workflowReplayRecording is not null)
            {
                replay?.SetPaused(true);
            }
            recording?.StopIfSourceCompleted(DirectorRecordingSourceType.Workflow);
        }
    }

    private void UpdatePendingWorkflowReplayRecording()
    {
        WorkflowReplayRecordingSession? session = workflowReplayRecording;
        if (session is null || session.StartupRequested || replay is null)
        {
            return;
        }
        if (replay.PlaybackLoadError.Length > 0)
        {
            FailWorkflowReplayRecording(Lang.Get(
                "vsdirector:workflow-record-replay-load-failed",
                session.ReplayName));
            return;
        }
        if (!replay.IsPlaying)
        {
            return;
        }
        if (replay.ActiveReplayId != session.ReplayId)
        {
            FailWorkflowReplayRecording(Lang.Get(
                "vsdirector:workflow-record-replay-load-failed",
                session.ReplayName));
            return;
        }
        if (replay.IsLoading)
        {
            return;
        }
        if (!session.SeekApplied)
        {
            replay.SetPaused(true);
            replay.SetSpeed(1d, out _);
            if (!replay.Seek(session.StartSeconds, out _))
            {
                FailWorkflowReplayRecording(Lang.Get("vsdirector:replay-invalid-seek"));
                return;
            }
            session.SeekApplied = true;
            return;
        }
        if (replay.IsLoading)
        {
            return;
        }

        if (WaitForWorkflowRecordingCountdown(session))
        {
            return;
        }

        if (session.Backend != WorkflowReplayRecordingBackend.Obs)
        {
            session.StartupRequested = true;
            if (!TryStartWorkflowMultiViewRecording(
                    session.WorkflowId,
                    allowWorkflowReplay: true,
                    out string offlineMessage))
            {
                FailWorkflowReplayRecording(offlineMessage);
            }
            return;
        }

        DirectorWorkflow? workflow = library?.FindWorkflow(session.WorkflowId);
        DirectorRecordingTrack? track = null;
        string message = string.Empty;
        if (workflow is null
            || !TryGetOrCreateWorkflowRecordingTrack(workflow, out track, out message)
            || track is null)
        {
            FailWorkflowReplayRecording(string.IsNullOrWhiteSpace(message)
                ? Lang.Get("vsdirector:workflow-not-found", session.WorkflowId)
                : message);
            return;
        }

        Guid? previousAssignment = assignedRecordingTrackId;
        if (!TryAssignRecordingWorker(track.Id, out message))
        {
            FailWorkflowReplayRecording(message);
            return;
        }

        workflowPreviousAssignment = previousAssignment;
        restoreWorkflowAssignmentPending = true;
        directWorkflowExpectedId = session.WorkflowId;
        directWorkflowAssignedTrackId = track.Id;
        session.TrackId = track.Id;
        session.StartupRequested = true;
        if (!TryStartRecording(out message, track.Id))
        {
            RestoreWorkflowRecordingAssignment();
            FailWorkflowReplayRecording(message);
        }
    }

    private bool WaitForWorkflowRecordingCountdown(WorkflowReplayRecordingSession session)
    {
        if (session.CountdownSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds
            || session.CountdownCompleted)
        {
            return false;
        }

        if (!session.CountdownStarted)
        {
            session.CountdownStarted = true;
            session.CountdownStartedTimestamp = Stopwatch.GetTimestamp();
        }

        double elapsedSeconds = Math.Max(
            0d,
            Stopwatch.GetElapsedTime(session.CountdownStartedTimestamp).TotalSeconds);
        double remainingSeconds = Math.Max(0d, session.CountdownSeconds - elapsedSeconds);
        if (remainingSeconds > DirectorWorkflowPlanner.TimeEpsilon)
        {
            int wholeSeconds = Math.Max(1, (int)Math.Ceiling(remainingSeconds));
            if (wholeSeconds != session.LastCountdownWholeSeconds)
            {
                session.LastCountdownWholeSeconds = wholeSeconds;
                Show(Lang.Get("vsdirector:workflow-record-countdown", wholeSeconds));
            }
            return true;
        }

        session.CountdownCompleted = true;
        Show(Lang.Get("vsdirector:workflow-record-countdown-complete"));
        return false;
    }

    private void FailWorkflowReplayRecording(string message)
    {
        replay?.SetPaused(true);
        workflowReplayRecording = null;
        liveWorkflowRunner?.Reset();
        directWorkflowExpectedId = null;
        directWorkflowAssignedTrackId = null;
        if (!string.IsNullOrWhiteSpace(message))
        {
            Show(message);
        }
    }

    internal void RequestPermissionMembers()
        => permissionClient?.RequestSnapshot();

    internal bool TryUpdateMemberPermissions(
        string playerUid,
        DirectorPermission permissions,
        out string message)
    {
        if (!IsDirectorAdmin || permissionClient is null)
        {
            message = Lang.Get("vsdirector:permission-denied");
            Show(message);
            return false;
        }

        permissionClient.Update(playerUid, permissions);
        message = Lang.Get("vsdirector:permissions-update-pending");
        return true;
    }

    internal bool EnsurePermission(DirectorPermission permission, out string message)
    {
        if (HasPermission(permission))
        {
            message = string.Empty;
            return true;
        }

        if (permissionClient?.IsKnown != true)
        {
            permissionClient?.RequestSnapshot();
            message = Lang.Get("vsdirector:authorization-pending");
            return false;
        }

        message = Lang.Get("vsdirector:permission-feature-denied");
        return false;
    }

    internal bool SetReplaySpeedFromPanel(double speed)
        => replay?.SetSpeed(speed, out _) == true;

    internal bool TryReplaceReplayViewportTimeline(
        DirectorReplayViewportTimeline timeline,
        out string message,
        bool notify = true)
    {
        string error = "replay-not-active";
        if (replay?.ReplaceViewportTimeline(timeline, out error, notify) == true)
        {
            if (!notify)
            {
                pathPreview?.Refresh();
            }
            message = string.Empty;
            return true;
        }

        message = ReplayTimelineEditError(error);
        Show(message);
        return false;
    }

    internal bool TryCaptureReplayCameraKeyframe(
        string requestedName,
        out CameraPreset pose,
        out string message)
    {
        pose = new CameraPreset();
        message = string.Empty;
        if (replay?.IsPlaying != true || controller is null)
        {
            message = Lang.Get("vsdirector:replay-not-playing");
            Show(message);
            return false;
        }
        if (!EnsureEditAccess(out message))
        {
            return false;
        }

        string name = string.IsNullOrWhiteSpace(requestedName)
            ? Lang.Get("vsdirector:replay-camera-default-name")
            : requestedName.Trim();
        if (!controller.TryCaptureCurrentView(name, out pose))
        {
            message = Lang.Get("vsdirector:capture-failed");
            Show(message);
            return false;
        }

        pose.Id = Guid.Empty;
        pose.Name = name;
        pose.Tags.Clear();
        pose.Description = string.Empty;
        pose.PreviewVisible = false;
        pose.QuickSlot = null;
        return true;
    }

    private void ShowPermissionDenied()
        => Show(Lang.Get(permissionClient?.IsKnown == true
            ? "vsdirector:permission-feature-denied"
            : "vsdirector:authorization-pending"));

    private void OnPermissionSnapshot(DirectorPermissionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            string key = snapshot.Error switch
            {
                "permission" => "vsdirector:permission-denied",
                "admin-managed-by-role" => "vsdirector:permissions-admin-role-managed",
                "invalid-player" => "vsdirector:permissions-invalid-player",
                "save-failed" => "vsdirector:permissions-save-failed",
                _ => "vsdirector:permissions-update-failed"
            };
            Show(Lang.Get(key));
        }

        if (!HasPermission(DirectorPermission.FreeCamera)
            && controller?.IsFreeCamera == true)
        {
            ExitCamera();
        }
        if (!HasPermission(DirectorPermission.ReplayCapture)
            && automaticReplayRecording)
        {
            _ = StopAutomaticReplay(continuation: null, out _);
        }
        if (!HasPermission(DirectorPermission.OffscreenSavedSources)
            && liveOffscreenUsesSavedSource
            && liveOffscreenState != LiveOffscreenRecordingState.Idle)
        {
            _ = TryStopLiveOffscreenRecording(out _);
        }
        if (IsWorldReady)
        {
            if (HasPermission(DirectorPermission.RemoteChunkAccess))
            {
                automaticReplayAuthorizationPending = config.AutoRecordReplay
                    && HasPermission(DirectorPermission.ReplayCapture);
                anchorClient?.RequestAccess();
            }
            else
            {
                automaticReplayAuthorizationPending = false;
                anchorClient?.ResetSession();
            }

            if (HasPermission(DirectorPermission.SharedCameras))
            {
                sharedCameraClient?.RequestList();
            }
            else
            {
                sharedCameraClient?.Reset();
            }

        }

        if (!HasWorkspacePermission && workspace is not null)
        {
            workspace.Dispose();
            workspace = null;
        }
        else
        {
            workspace?.Refresh();
        }
        settingsDialog?.RefreshPermissions();
    }

    private void OnAnchorStatus(DirectorAnchorStatus status)
    {
        if (status.Allowed)
        {
            if (pendingRemoteFollow is PendingRemoteFollow pending && status.TargetRequested)
            {
                if (!status.TargetAvailable)
                {
                    FailPendingRemoteFollow("vsdirector:follow-player-unavailable");
                    return;
                }

                if (status.TargetEntityId > 0)
                {
                    pending.Rig.TargetEntityId = status.TargetEntityId;
                }
            }

            if (pendingRemoteLiveOffscreen is PendingRemoteLiveOffscreen offscreen
                && status.TargetRequested)
            {
                if (!status.TargetAvailable)
                {
                    FailPendingRemoteLiveOffscreen("vsdirector:follow-player-unavailable");
                    return;
                }

                if (status.TargetEntityId > 0)
                {
                    offscreen.Rig.TargetEntityId = status.TargetEntityId;
                }
            }

            if (automaticReplayAuthorizationPending)
            {
                automaticReplayAuthorizationPending = false;
                string worldId = capi?.World.SavegameIdentifier ?? string.Empty;
                capi?.Event.EnqueueMainThreadTask(
                    () => TryStartAutomaticReplay(worldId),
                    "vsdirector-authorized-auto-replay");
            }

            return;
        }

        CancelPendingRemoteFollow(stopAnchor: false);
        if (pendingRemoteLiveOffscreen is not null)
        {
            FailPendingRemoteLiveOffscreen("vsdirector:permission-denied");
        }

        if (status.MessageCode == "permission")
        {
            automaticReplayAuthorizationPending = false;
            if (automaticReplayRecording
                && !StopAutomaticReplay(continuation: null, out string stopError))
            {
                capi?.Logger.Warning(
                    "VS Director: automatic replay recording did not stop after permission was denied: {0}",
                    stopError);
            }
        }

        if (recording?.ActiveTrack?.SourceType == DirectorRecordingSourceType.Workflow
            && liveWorkflowRunner?.IsPrepared == true)
        {
            liveWorkflowRunner.Hold();
            recording.FailActiveSource("workflow-anchor-denied");
        }

        bool wasActive = controller?.IsActive == true;
        controller?.Deactivate();
        replayCameraState = DirectorReplayCameraState.Inactive;
        if (!wasActive && !accessAttempted)
        {
            return;
        }

        string key = status.MessageCode == "invalid-anchor"
            ? "vsdirector:invalid-anchor"
            : "vsdirector:permission-denied";
        Show(Lang.Get(key));
    }

    private void OnSharedCameraCatalogChanged()
    {
        if (library is not null)
        {
            RefreshWorkspaceDialogs();
        }
    }

    private void OnSharedCameraError(string error)
    {
        string key = error switch
        {
            "permission" => "vsdirector:permission-denied",
            "invalid-camera" => "vsdirector:shared-camera-invalid",
            "camera-not-found" => "vsdirector:shared-camera-not-found-generic",
            "camera-save-failed" => "vsdirector:camera-save-failed",
            _ => "vsdirector:shared-camera-error"
        };
        Show(key == "vsdirector:shared-camera-error" ? Lang.Get(key, error) : Lang.Get(key));
    }

    private void OnPathCompleted(string pathName)
    {
        if (replayCameraState.Mode == DirectorReplayCameraMode.Path)
        {
            replayCameraState = DirectorReplayCameraState.Inactive;
        }
        recording?.StopIfSourceCompleted(DirectorRecordingSourceType.Path);
        anchorClient?.StopAnchor();
        Show(Lang.Get("vsdirector:path-completed", pathName));
    }

    private void OnSequenceCompleted(string sequenceName)
    {
        if (replayCameraState.Mode == DirectorReplayCameraMode.Sequence)
        {
            replayCameraState = DirectorReplayCameraState.Inactive;
        }
        recording?.StopIfSourceCompleted(DirectorRecordingSourceType.Sequence);
        anchorClient?.StopAnchor();
        Show(Lang.Get("vsdirector:sequence-completed", sequenceName));
    }

    private void OnFollowTargetLost(string rigName)
    {
        if (replayCameraState.Mode == DirectorReplayCameraMode.Follow)
        {
            replayCameraState = DirectorReplayCameraState.Inactive;
        }
        ClearReplayActorAttachment();
        recording?.StopIfSourceCompleted(DirectorRecordingSourceType.Follow);
        anchorClient?.StopAnchor();
        Show(Lang.Get("vsdirector:follow-target-lost", rigName));
    }

    private bool PersistOrRollback(CameraLibraryDocument before, out string message)
    {
        if (library != null && store != null && store.Save(library))
        {
            pathPreview?.Refresh();
            ReconcileRecordingAssignment();
            message = string.Empty;
            return true;
        }

        library = new CameraLibrary(before);
        pathPreview?.Refresh();
        message = Lang.Get("vsdirector:camera-save-failed");
        Show(message);
        return false;
    }

    private DirectorConfig LoadConfig(ICoreClientAPI api)
    {
        DirectorConfig loaded;
        try
        {
            loaded = api.LoadModConfig<DirectorConfig>(ConfigFileName) ?? new DirectorConfig();
            loaded.Normalize();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("VS Director: failed to load config: {0}", ex.Message);
            loaded = new DirectorConfig();
            loaded.Normalize();
            return loaded;
        }

        try
        {
            api.StoreModConfig(loaded, ConfigFileName);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("VS Director: failed to create or normalize config: {0}", ex.Message);
        }

        return loaded;
    }

    private bool SaveConfig()
    {
        try
        {
            if (capi is null)
            {
                return false;
            }

            config.Normalize();
            capi.StoreModConfig(config, ConfigFileName);
            return true;
        }
        catch (Exception ex)
        {
            capi?.Logger.Warning("VS Director: failed to save config: {0}", ex.Message);
            return false;
        }
    }

    private bool FailObsOnly(out string message)
    {
        message = Lang.Get("vsdirector:obs-only");
        Show(message);
        return false;
    }

    private void Show(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            capi?.ShowChatMessage(message);
        }
    }

    private sealed record ActiveActionPreview(
        DirectorActionAsset Action,
        string ActorKey,
        string ModelCode,
        float ModelSize,
        DirectorActionEvaluator Evaluator,
        long StartTimestamp,
        bool KeepFinalPose);

    private sealed class LiveActionWorkflowPlayback
    {
        public LiveActionWorkflowPlayback(DirectorWorkflow workflow, long startTimestamp)
        {
            Workflow = workflow;
            StartTimestamp = startTimestamp;
        }

        public DirectorWorkflow Workflow { get; }

        public long StartTimestamp { get; }

        public Dictionary<string, Entity> GhostActors { get; } = new(StringComparer.Ordinal);

        public Dictionary<Entity, LiveActorVisibility> HiddenLiveEntities { get; } =
            new(ReferenceEqualityComparer.Instance);

        public Dictionary<Guid, IReadOnlyList<string>> ActiveAnimations { get; } = new();

        public HashSet<Guid> StartedPathCards { get; } = new();
    }

    private readonly record struct LiveActorVisibility(bool Rendered, bool ShadowRendered);

    private sealed record ResolvedActionPlayback(
        string RuntimeKey,
        DirectorActionAsset Action,
        DirectorActionEvaluation Evaluation);

    private readonly record struct ActionEvaluatorCacheKey(
        Guid CacheId,
        Guid ActionId,
        DirectorActionLoopMode LoopMode,
        long ModifiedTicks);

    private sealed class ActionTriggerPlaybackState
    {
        public double LastReplayTime { get; set; } = double.NaN;

        public double StartReplayTime { get; set; }

        public bool WasMatching { get; set; }

        public bool Active { get; set; }
    }

    private readonly record struct OffscreenStopOutcome(
        DirectorOffscreenFfmpegResult? Result,
        Exception? Error);

    private enum ReplaySavePurpose
    {
        None,
        Manual,
        Automatic,
        Take
    }

    private sealed class ActiveTakeCapture
    {
        public ActiveTakeCapture(
            string sessionId,
            DirectorRecordingTrack track,
            string replayName,
            DateTimeOffset startedUtc)
        {
            SessionId = sessionId;
            Track = track.DeepClone();
            ReplayName = replayName;
            StartedUtc = startedUtc;
        }

        public string SessionId { get; }

        public DirectorRecordingTrack Track { get; }

        public string ReplayName { get; }

        public DateTimeOffset StartedUtc { get; }

        public bool StopAttempted { get; set; }

        public DirectorReplayInfo? ReplayInfo { get; set; }

        public string Error { get; set; } = string.Empty;
    }

    private sealed class WorkflowReplayRecordingSession
    {
        public WorkflowReplayRecordingSession(
            Guid workflowId,
            Guid replayId,
            string replayName,
            double startSeconds,
            double endSeconds,
            WorkflowReplayRecordingBackend backend,
            double countdownSeconds)
        {
            WorkflowId = workflowId;
            ReplayId = replayId;
            ReplayName = replayName;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            Backend = backend;
            CountdownSeconds = double.IsFinite(countdownSeconds)
                ? Math.Clamp(
                    countdownSeconds,
                    0d,
                    DirectorWorkflowPlanner.MaxTimelineSeconds)
                : 0d;
        }

        public Guid WorkflowId { get; }

        public Guid ReplayId { get; }

        public string ReplayName { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public WorkflowReplayRecordingBackend Backend { get; }

        public double CountdownSeconds { get; }

        public bool SeekApplied { get; set; }

        public bool StartupRequested { get; set; }

        public bool RecordingStarted { get; set; }

        public Guid TrackId { get; set; }

        public long OffscreenOperationId { get; set; }

        public bool CountdownStarted { get; set; }

        public long CountdownStartedTimestamp { get; set; }

        public int LastCountdownWholeSeconds { get; set; } = -1;

        public bool CountdownCompleted { get; set; }
    }

    private enum WorkflowReplayRecordingBackend
    {
        Obs,
        Ffmpeg
    }

    private enum LiveOffscreenRecordingState
    {
        Idle,
        LoadingTarget,
        Starting,
        Recording,
        Stopping
    }

    private sealed class WorkflowMultiViewFollowState
    {
        internal WorkflowMultiViewFollowState(Guid rigId)
        {
            RigId = rigId;
        }

        internal Guid RigId { get; set; }

        internal double TimelineSeconds { get; set; }

        internal double SourceTimeSeconds { get; set; }

        internal CameraFollowPose Pose { get; set; }

        internal CameraFollowPoseHistory History { get; } = new();

        internal bool HasPose { get; set; }
    }

    private sealed class PendingRemoteFollow
    {
        internal PendingRemoteFollow(
            CameraFollowRig rig,
            CameraPreset anchorPose,
            long startedMilliseconds,
            bool showNotification,
            bool preview)
        {
            Rig = rig;
            AnchorPose = anchorPose;
            StartedMilliseconds = startedMilliseconds;
            ShowNotification = showNotification;
            Preview = preview;
        }

        internal CameraFollowRig Rig { get; }

        internal CameraPreset AnchorPose { get; }

        internal RemoteFollowChunkWarmup ChunkWarmup { get; } = new();

        internal CameraPreset EffectiveAnchorPose => ChunkWarmup.CameraPose ?? AnchorPose;

        internal long StartedMilliseconds { get; }

        internal bool ShowNotification { get; }

        internal bool Preview { get; }
    }

    private sealed class PendingRemoteLiveOffscreen
    {
        internal PendingRemoteLiveOffscreen(
            DirectorLiveOffscreenSource source,
            CameraFollowRig rig,
            CameraPreset anchorPose,
            long startedMilliseconds)
        {
            Source = source;
            Rig = rig;
            AnchorPose = anchorPose;
            StartedMilliseconds = startedMilliseconds;
        }

        internal DirectorLiveOffscreenSource Source { get; }

        internal CameraFollowRig Rig { get; }

        internal CameraPreset AnchorPose { get; }

        internal RemoteFollowChunkWarmup ChunkWarmup { get; } = new();

        internal CameraPreset EffectiveAnchorPose => ChunkWarmup.CameraPose ?? AnchorPose;

        internal long StartedMilliseconds { get; }
    }

    private sealed class RemoteFollowChunkWarmup
    {
        internal CameraPreset? CameraPose { get; set; }

        internal CameraPreset? TargetPose { get; set; }

        internal RemoteChunkCenter? PrioritizedCameraCenter { get; set; }

        internal RemoteChunkCenter? PrioritizedTargetCenter { get; set; }
    }

    private readonly record struct RemoteChunkCenter(int X, int Z, int Dimension)
    {
        internal static RemoteChunkCenter From(CameraPreset pose, int chunkSize)
            => new(
                (int)Math.Floor(pose.X / chunkSize),
                (int)Math.Floor(pose.Z / chunkSize),
                pose.Dimension);
    }
}
