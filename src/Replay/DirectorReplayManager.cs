using System.IO;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Recording;
using VSDirector.Rendering;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal readonly record struct DirectorReplayActorPathSource(
    string ActorKey,
    string DisplayName,
    string PlayerUid,
    long EntityId,
    string EntityCode,
    int SampleCount,
    bool IsPlayer);

/// <summary>
/// Runtime information for a replay entity that can be selected in the game
/// viewport. The Entity reference is always the replay binding (a ghost when
/// the live world actor has been isolated), never an arbitrary live entity.
/// </summary>
internal readonly record struct DirectorReplayEntityInfo(
    string ActorKey,
    string DisplayName,
    string PlayerUid,
    long EntityId,
    long ReplayEntityId,
    string EntityCode,
    bool IsPlayer,
    bool IsCreature,
    bool IsGhost,
    Entity Entity);

internal readonly record struct DirectorReplayPreloadEditorState(
    int CenterChunkX,
    int CenterChunkZ,
    int Dimension,
    DirectorReplayPreloadPlan Plan,
    IReadOnlyList<DirectorReplayPreloadColumn> IncludedColumns,
    IReadOnlyList<DirectorReplayPreloadColumn> ExcludedColumns);

internal sealed class DirectorReplayManager : IDisposable
{
    private const double TimeEpsilon = 0.000001d;
    private const int PendingBlockChangesPerTick = 64;
    private const int EntityStateCapturesPerTick = 8;
    private const int BlockEntityChunksPerTick = 16;
    private const int PlaybackActorsPerFrame = 8;
    private const int IndependentActorLimitForDenseReplay = 64;
    private const int MaxReplayAnimationUpdatesPerFrame = 16;
    private const int DefaultReplayEquipmentSlots = 15;
    private const int DefaultReplayBackpackSlots = 4;
    private const int MaxParticleEventsPerPlaybackFrame = 128;
    private const int MaxReplayParticles = 2048;
    private const int ReservedPriorityReplayParticles = 512;
    // The dedicated renderer owns a separate pool.  Its larger budget is
    // deliberately scoped to the opt-in path so the proven preview defaults
    // remain unchanged.
    private const int DedicatedMaxParticleEventsPerPlaybackFrame = 512;
    private const int DedicatedMaxReplayParticles = 12288;
    private const int DedicatedReservedPriorityReplayParticles = 2048;
    private const double DedicatedParticlePlaybackWorkBudgetMilliseconds = 4d;
    private const double DedicatedMaxParticlePlaybackAgeSeconds = 0.5d;
    private const float MaxPlaybackFrameDeltaSeconds = 0.1f;
    private const double PlaybackActorWorkBudgetMilliseconds = 4d;
    private const double ParticlePlaybackWorkBudgetMilliseconds = 2d;
    private const double MaxParticlePlaybackAgeSeconds = 0.25d;
    private const int PlaybackReadyRadiusChunks = 0;
    // A replay can describe thousands of route columns.  Startup only needs
    // the two areas the player can actually observe: the active camera and
    // the replay anchor at time zero.  Keeping this small also keeps the
    // integrated-server request and client readiness checks bounded.
    private const int PlaybackPreloadWindowRadiusChunks = 0;
    // Keep integrated-server ticks responsive while a replay baseline is
    // applied.  Large batches can trip the host watchdog even though the
    // work is technically spread across frames.
    private const double DefaultReplayPrerollSeconds = 0.5d;
    private const int DefaultExportWarmupFrames = 1;
    internal const long ActionPreviewGhostEntityId = long.MinValue + 8192L;
    private const string FallingBlockEntityType = "Vintagestory.GameContent.EntityBlockFalling";
    private static readonly FieldInfo? EntityRenderersField = typeof(ClientMain).GetField(
        "EntityRenderers",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ParticleManagerField = typeof(ClientMain).GetField(
        "particleManager",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ParticleSystemField = typeof(ParticleManager).GetField(
        "particleSystem",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? MainThreadParticlePoolsField = typeof(SystemRenderParticles).GetField(
        "mainthreadpools",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ParticlePhysicsField = typeof(ParticlePoolQuads).GetField(
        "partPhysics",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? MountedOnProperty = typeof(EntityAgent).GetProperty(
        nameof(EntityAgent.MountedOn),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? MountedOnSetter = MountedOnProperty?.GetSetMethod(nonPublic: true);

    [ThreadStatic]
    private static DirectorReplayManager? dedicatedAnimationWarmupOwner;

    [ThreadStatic]
    private static Entity? dedicatedAnimationWarmupEntity;

    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private readonly DirectorConfig config;
    private readonly DirectorCameraController controller;
    private readonly Action<bool> inputLockHandler;
    private readonly DirectorReplayMultiTrackExporter exporter;
    private readonly object independentVoiceExportGate = new();
    private ObsReplayExportSession? obsExport;
    private DirectorReplayStore? store;
    private DirectorReplayDocument? recording;
    private Dictionary<string, DirectorReplayActorTrack>? recordingActors;
    private readonly Dictionary<long, RecordingActorBinding> recordingEntityActors = new();
    private readonly Dictionary<string, string> recordingReusableActorTypes = new(StringComparer.Ordinal);
    private HashSet<string> recordingVisibleActors = new(StringComparer.Ordinal);
    private Dictionary<BlockKey, CachedBlockEntity> recordingBlockEntities = new();
    private Dictionary<BlockKey, CachedBlockState> recordingBlockStates = new();
    private readonly ConditionalWeakTable<BlockEntity, ObservedBlockState> observedBlockEntityStates = new();
    private readonly object observedBlockEntitySync = new();
    private readonly ConditionalWeakTable<object, ReplayLoadedSoundMarker> replayLoadedSounds = new();
    private readonly List<WeakReference<ILoadedSound>> replayLoadedSoundReferences = new();
    private readonly object soundCaptureSync = new();
    private readonly DirectorReplayVoiceTimestampTimeline voiceCaptureTimeline = new();
    private readonly HashSet<string> recordedVoiceSpeakers = new(StringComparer.Ordinal);
    private Guid voiceTrackRecordingId;
    private long nextReplayLoadedSoundId;
    private HashSet<BlockKey> pendingBlockChanges = new();
    private HashSet<BlockKey> pausedBlockChanges = new();
    private readonly Queue<PausedBlockResyncBatch> pausedBlockResyncBatches = new();
    private Dictionary<ChunkKey, DirectorReplayChunkSnapshot> recordingSnapshotChunks = new();
    private ChunkKey? decodedSnapshotChunkKey;
    private int[]? decodedSnapshotSolid;
    private int[]? decodedSnapshotFluid;
    private readonly Queue<long> pendingEntityStateCaptures = new();
    private BlockEntityCaptureRound? pendingBlockEntityCapture;
    private readonly HashSet<long> replayGhostIds = new();
    private readonly HashSet<long> replayThirdPersonIds = new();
    private readonly Dictionary<string, Func<IParticlePropertiesProvider?>> replayParticleProviderFactories =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> replayParticleFailureWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<Entity, SavedEntityVisibility> suppressedLiveEntities =
        new(ReferenceEqualityComparer.Instance);
    private readonly DirectorReplayParticleFrameClock<ParticlePoolQuads> replayParticleFrameClock = new();
    private readonly DirectorDedicatedReplayRenderer dedicatedReplayRenderer;
    private readonly DirectorReplayVisualEnvironmentController visualEnvironment;
    private readonly DirectorSingleplayerReplaySuspension singleplayerWorldSuspension;
    private readonly Dictionary<long, ReplayRenderedPose> renderedCapturePoses = new();
    private readonly object particleCaptureSync = new();
    private object? visualCalendar;
    private MethodInfo? visualCalendarUpdateMethod;
    private bool visualCalendarRefreshFailureLogged;
    private readonly Dictionary<double, HashSet<DirectorReplayParticleEvent>> capturedParticles = new();
    private double recordingElapsedSeconds;
    private double recordingAccumulator;
    private double entityStateAccumulator;
    private bool recordingPaused;
    private LiveActionCapture? liveActionCapture;
    private ActorBinding? actionPreviewBinding;
    private readonly Dictionary<string, ActorBinding> actionWorkflowPreviewBindings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Entity, string> actionWorkflowRecordingGhosts =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Entity> actionWorkflowRecordingExcludedEntities =
        new(ReferenceEqualityComparer.Instance);
    private bool actionPreviewWorldVisible = true;
    private double actionPreviewDefaultDistance = 4d;
    private double actionPreviewDistance = 4d;
    private double actionPreviewYawOffset;
    private PendingReplaySave? pendingSave;
    private bool CaptureWorld => config.ReplayCaptureWorld;
    private bool CaptureParticles => config.ReplayCaptureParticles;
    private bool CaptureSounds => config.ReplayCaptureSounds;
    internal bool IsVoiceRecording => recording is not null && CaptureVoice;
    private bool CaptureVoice => config.ReplayCaptureVoiceModAudio;
    private int CaptureMaxActors => config.ReplayMaxActors;
    private volatile Playback? playback;
    private CancellationTokenSource? independentVoiceExportCancellation;
    private bool independentVoiceExportActive;
    private string independentVoiceExportStatus = "idle";
    private string independentVoiceExportError = string.Empty;
    private string independentVoiceExportDirectory = string.Empty;
    private string independentVoiceExportOutputFile = string.Empty;
    private int independentVoiceExportTrackCount;
    private PendingReplayLoad? pendingLoad;
    private PendingPlaybackStart? pendingPlaybackStart;
    private PendingPreloadEditorLoad? pendingPreloadEditorLoad;
    private LoadedPreloadEditor? loadedPreloadEditor;
    private string playbackLoadError = string.Empty;
    private bool playbackChunkBuffering;
    private bool replayLoadOverlayVisible;
    private volatile bool applyingReplayState;
    private bool replayAllowsOffscreenActorRendering;
    private long hiddenTrackedReplayEntityId;
    private bool disposed;
    private long replayAnimationFrame;
    // Reused on the render thread so dense replays do not allocate a request
    // list and a hash set on every frame.
    private readonly List<DirectorReplayActorSchedulingRequest> replayActorSchedulingRequests = new();
    private readonly HashSet<int> scheduledReplayAnimationUpdates = new();

    public DirectorReplayManager(
        ICoreClientAPI capi,
        DirectorConfig config,
        DirectorCameraController controller,
        Action<bool> inputLockHandler,
        System.Func<DirectorRecordingTrack, double, (bool Success, string Error)> applyExportTrack)
    {
        this.capi = capi;
        this.config = config;
        this.controller = controller;
        this.inputLockHandler = inputLockHandler;
        game = capi.World as ClientMain
            ?? throw new InvalidOperationException("VS Director replay requires the desktop client.");
        dedicatedReplayRenderer = new DirectorDedicatedReplayRenderer(capi, game);
        visualEnvironment = new DirectorReplayVisualEnvironmentController(capi, game);
        singleplayerWorldSuspension = new DirectorSingleplayerReplaySuspension(capi, game);
        exporter = new DirectorReplayMultiTrackExporter(
            capi,
            config,
            controller,
            SeekForExport,
            () => pendingLoad is null && !IsSeekPending,
            applyExportTrack);
        capi.Event.BlockChanged += OnBlockChanged;
        capi.Event.OnEntitySpawn += OnEntitySpawn;
        capi.Event.OnEntityLoaded += OnEntityLoaded;
        capi.Event.OnEntityDespawn += OnEntityDespawn;
    }

    public bool IsRecording => recording is not null;

    public bool IsRecordingPaused => recording is not null && recordingPaused;

    internal bool TryGetVoiceCaptureRegion(
        out DirectorVoicePosition center,
        out int radiusChunks)
    {
        DirectorReplayDocument? active = recording;
        if (active is null)
        {
            center = default;
            radiusChunks = 0;
            return false;
        }

        // Voice must follow the area the recording client currently has loaded.
        // The replay world snapshot is a fixed recording-start baseline and may
        // be much smaller than the live chunk window after the player moves.
        if (capi.World.Player?.Entity is EntityPlayer player)
        {
            center = new DirectorVoicePosition(
                player.Pos.X,
                player.Pos.Y,
                player.Pos.Z,
                player.Pos.Dimension);
            radiusChunks = Math.Clamp(config.ReplayVoiceCaptureRadiusChunks, 0, 16);
            return true;
        }

        if (controller.CurrentPose is CameraPreset camera)
        {
            center = new DirectorVoicePosition(camera.X, camera.Y, camera.Z, camera.Dimension);
            radiusChunks = Math.Clamp(config.ReplayVoiceCaptureRadiusChunks, 0, 16);
            return true;
        }

        DirectorReplayWorldSnapshot snapshot = active.WorldSnapshot;
        if (snapshot.Chunks.Count > 0)
        {
            center = new DirectorVoicePosition(
                (snapshot.CenterChunkX + 0.5d) * GlobalConstants.ChunkSize,
                0d,
                (snapshot.CenterChunkZ + 0.5d) * GlobalConstants.ChunkSize,
                snapshot.Dimension);
            radiusChunks = Math.Clamp(config.ReplayVoiceCaptureRadiusChunks, 0, 16);
            return true;
        }

        center = default;
        radiusChunks = 0;
        return false;
    }

    public bool IsLiveActionRecording => liveActionCapture is not null;

    public double LiveActionRecordingSeconds => liveActionCapture?.ElapsedSeconds ?? 0d;

    public bool IsSaving => pendingSave?.Operation.IsSaving == true;

    public bool HasPendingSave => pendingSave is not null;

    public bool HasFailedSave => pendingSave is { Operation.IsSaving: false };

    public string ReplaySaveError => pendingSave?.Error ?? string.Empty;

    public bool IsPlaying => playback is not null;
    internal DirectorReplayPages? PlaybackPages => playback?.Document.Pages;
    internal IReadOnlyList<DirectorReplayVoiceFrame>? OpenTimelineVoiceView()
        => playback?.Document.Pages?.OpenVoiceView() ?? playback?.Document.VoiceFrames;
    internal object? TimelineDocumentIdentity => playback?.Document;
    internal bool IsWorldPlaybackActive => playback?.WorldPlayback is not null;
    internal bool IsServerRecordingWorldPlaybackActive => playback?.Document.HasHistoricalBaselines == true && playback.WorldPlayback is not null;
    internal void ObserveServerRecordingChunkLoad(int x, int y, int z) => playback?.WorldPlayback?.ObserveServerRecordingChunkLoad(x, y, z);

    internal bool IsReplayBlockEntity(BlockEntity blockEntity)
        => playback?.WorldPlayback?.IsReplayBlockEntity(blockEntity) == true;

    internal void SetHiddenTrackedReplayEntity(long entityId)
        => hiddenTrackedReplayEntityId = entityId;

    /// <summary>
    /// True only while the currently loaded replay owns the experimental
    /// renderer.  The setting is sampled when playback starts; changing it
    /// while a replay is running takes effect on the next replay.
    /// </summary>
    public bool IsDedicatedRendererActive => dedicatedReplayRenderer.IsActive;

    internal long DedicatedReplayAnimationFrame => replayAnimationFrame;

    internal int DedicatedReplayAnimationWarmupBudget
        => playback is { } active && dedicatedReplayRenderer.IsActive
            ? DirectorReplayActorSchedulingPolicy.ResolveWarmupBudget(
                active.DedicatedAnimationBudget)
            : 0;

    internal Entity? ActionPreviewEntity => actionPreviewBinding?.Entity;

    internal IReadOnlyList<Entity> ReplayTrackingEntities
        => playback?.Bindings.Select(binding => binding.Entity).ToArray()
            ?? Array.Empty<Entity>();

    internal IReadOnlyDictionary<string, Entity> ActionWorkflowPreviewEntities
        => actionWorkflowPreviewBindings.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Entity,
            StringComparer.Ordinal);

    internal void ConfigureActionWorkflowRecordingCapture(Entity? ghost, Entity? excludedEntity)
    {
        actionWorkflowRecordingGhosts.Clear();
        actionWorkflowRecordingExcludedEntities.Clear();
        if (ghost is not null)
        {
            string actorKey = actionWorkflowPreviewBindings?.Values
                .FirstOrDefault(binding => ReferenceEquals(binding.Entity, ghost))?.Track.ActorKey
                ?? "workflow-ghost:" + ghost.EntityId.ToString(CultureInfo.InvariantCulture);
            actionWorkflowRecordingGhosts[ghost] = actorKey;
        }
        if (excludedEntity is not null)
        {
            actionWorkflowRecordingExcludedEntities.Add(excludedEntity);
        }
    }

    internal void ConfigureActionWorkflowRecordingCapture(
        IReadOnlyDictionary<string, Entity>? ghosts,
        IReadOnlyCollection<Entity>? excludedEntities)
    {
        actionWorkflowRecordingGhosts.Clear();
        actionWorkflowRecordingExcludedEntities.Clear();
        if (ghosts is not null)
        {
            foreach ((string actorKey, Entity entity) in ghosts)
            {
                if (entity is not null && !string.IsNullOrWhiteSpace(actorKey))
                {
                    actionWorkflowRecordingGhosts[entity] = actorKey.Trim();
                }
            }
        }
        if (excludedEntities is not null)
        {
            foreach (Entity entity in excludedEntities)
            {
                if (entity is not null)
                {
                    actionWorkflowRecordingExcludedEntities.Add(entity);
                }
            }
        }
    }

    public bool IsLoading
        => pendingLoad is not null
            || pendingPlaybackStart is not null
            || playbackChunkBuffering
            || playback?.WorldPlayback?.IsSynchronizing == true;

    public bool IsReplayLoadOverlayVisible => replayLoadOverlayVisible;

    public bool IsSeekPending
        => playbackChunkBuffering || playback?.WorldPlayback?.IsSynchronizing == true;

    public double LoadingProgress
    {
        get
        {
            PendingReplayLoad? loading = pendingLoad;
            if (loading is not null)
            {
                return Math.Clamp(loading.Progress * 0.35d, 0d, 0.35d);
            }

            PendingPlaybackStart? starting = pendingPlaybackStart;
            if (starting is not null)
            {
                if (!starting.ChunksReady)
                {
                    double chunkProgress = starting.StartupPreloadColumns.Count == 0
                        ? 1d
                        : starting.PreloadReadyColumns
                            / (double)starting.StartupPreloadColumns.Count;
                    return 0.35d + Math.Clamp(chunkProgress, 0d, 1d) * 0.15d;
                }

                double actorProgress = starting.Document.Actors.Count == 0
                    ? 1d
                    : starting.NextActorIndex / (double)starting.Document.Actors.Count;
                return 0.5d + Math.Clamp(actorProgress, 0d, 1d) * 0.05d;
            }

            DirectorReplayWorldPlayback? world = playback?.WorldPlayback;
            if (playbackChunkBuffering)
            {
                return 0.55d;
            }
            return world?.IsSynchronizing == true
                ? 0.55d + Math.Clamp(world.Progress, 0d, 1d) * 0.45d
                : playback is not null ? 1d : 0d;
        }
    }

    public string LoadingPhase
        => pendingLoad is not null
            ? "document"
            : pendingPlaybackStart is not null
                ? pendingPlaybackStart.ChunksReady ? "actors" : "chunks"
            : playbackChunkBuffering
                ? "chunks"
            : playback?.WorldPlayback?.IsSynchronizing == true
                ? playback.WorldPlayback.Phase
                : "ready";

    public string PlaybackLoadError => playbackLoadError;

    public bool IsIndexing => store?.IsIndexing == true;

    public double IndexProgress => store?.IndexProgress ?? 1d;

    public bool IsPaused => playback?.Paused == true;

    public bool HasReachedEnd
        => playback is { } active
            && DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(
                active.Paused,
                active.TimeSeconds,
                active.Document.DurationSeconds);

    public bool IsQuickMode
        => playback?.QuickMode == true
            || pendingPlaybackStart?.QuickMode == true
            || pendingLoad?.QuickMode == true;

    public bool IsApplyingReplayState => applyingReplayState;

    /// <summary>Optional director layer applied after replay interpolation and before Ghost animation sync.</summary>
    internal System.Func<bool>? ActionDecoratorsActive { get; set; }

    internal System.Func<bool>? RequiresFullActorSampling { get; set; }

    internal System.Func<DirectorReplayViewportTimeline, DirectorReplayActorTrack, double, bool>?
        RequiresActorPoseSampling { get; set; }

    internal Action<double>? ActionFrameDecorator { get; set; }

    internal System.Func<DirectorReplayActorTrack, double, DirectorReplayActorSample, DirectorReplayActorSample>?
        ActionSampleDecorator { get; set; }

    internal Action<DirectorReplayActorTrack, double, Entity, DirectorReplayActorSample>?
        ActionEntityDecorator { get; set; }

    public string? ActiveReplayName
        => playback?.Document.Name
            ?? pendingPlaybackStart?.Document.Name
            ?? pendingLoad?.Info.Name
            ?? recording?.Name
            ?? pendingSave?.Document.Name;

    public Guid ActiveReplayId
        => playback?.Document.Id
            ?? pendingPlaybackStart?.Document.Id
            ?? pendingLoad?.Info.Id
            ?? recording?.Id
            ?? pendingSave?.Document.Id
            ?? Guid.Empty;

    public double PlaybackTimeSeconds
        => playback?.TimeSeconds
            ?? (recording is null ? pendingSave?.Document.DurationSeconds ?? 0d : recordingElapsedSeconds);

    /// <summary>
    /// Content time used by the most recently prepared render frame. During a
    /// Freeze element this differs from the logical playback clock and keeps
    /// camera elements on the same sample as replay actors.
    /// </summary>
    internal double PreparedPlaybackTimeSeconds
        => playback is { PreparedTimeSeconds: >= 0d } active
            ? active.PreparedTimeSeconds
            : PlaybackTimeSeconds;

    public double PlaybackDurationSeconds
        => playback?.Document.DurationSeconds
            ?? pendingPlaybackStart?.Document.DurationSeconds
            ?? pendingLoad?.Info.DurationSeconds
            ?? (recording is null ? pendingSave?.Document.DurationSeconds ?? 0d : recordingElapsedSeconds);

    public double PlaybackSpeed
    {
        get
        {
            if (playback is not Playback active)
            {
                return 1d;
            }
            DirectorReplayViewportTimeline timeline = active.Document.ViewportTimeline;
            double legacySpeed = DirectorReplayTimeline.SampleSpeed(
                active.Document.SpeedKeyframes,
                active.TimeSeconds,
                active.Speed);
            return DirectorReplayElementTimeline.SampleTimingRate(
                timeline,
                active.TimeSeconds,
                legacySpeed);
        }
    }

    public double BasePlaybackSpeed => playback?.Speed ?? 1d;

    public IReadOnlyList<DirectorReplayMarker> Markers
        => playback is not null
            ? playback.Document.Markers
            : recording is not null
                ? recording.Markers
                : Array.Empty<DirectorReplayMarker>();

    public IReadOnlyList<DirectorReplaySpeedKeyframe> SpeedKeyframes
        => playback is not null
            ? playback.Document.SpeedKeyframes
            : recording is not null
                ? recording.SpeedKeyframes
                : Array.Empty<DirectorReplaySpeedKeyframe>();

    public IReadOnlyList<DirectorReplayElementKeyframe> ElementKeyframes
        => playback is not null
            ? playback.Document.ViewportTimeline.ElementKeyframes
            : recording is not null
                ? recording.ViewportTimeline.ElementKeyframes
                : Array.Empty<DirectorReplayElementKeyframe>();

    public IReadOnlyList<DirectorReplayViewportClip> ViewportClips
        => playback is not null
            ? playback.Document.ViewportTimeline.Clips
            : recording is not null
                ? recording.ViewportTimeline.Clips
                : Array.Empty<DirectorReplayViewportClip>();

    public DirectorReplayViewportTimeline ViewportTimeline
        => (playback is not null
                ? playback.Document.ViewportTimeline
                : recording?.ViewportTimeline)
            ?.DeepClone()
            ?? new DirectorReplayViewportTimeline();

    public bool IsExporting => exporter.IsExporting || obsExport?.IsActive == true || IsIndependentVoiceExportActive;

    public string ExportStatus => obsExport?.Status ?? (HasIndependentVoiceExportStatus
        ? IndependentVoiceExportStatus
        : exporter.Status);

    public string ExportError => obsExport?.Error ?? (HasIndependentVoiceExportStatus
        ? IndependentVoiceExportError
        : exporter.LastError);

    public string ExportOutputDirectory => obsExport?.OutputDirectory ?? (HasIndependentVoiceExportStatus
        ? IndependentVoiceExportDirectory
        : exporter.OutputDirectory);

    public double ExportProgress
        => obsExport is ObsReplayExportSession obs
            ? Math.Clamp((PlaybackTimeSeconds - obs.StartSeconds)
                / Math.Max(TimeEpsilon, obs.EndSeconds - obs.StartSeconds), 0d, 1d)
            : HasIndependentVoiceExportStatus
                ? IndependentVoiceExportStatus is "complete" ? 1d : 0d
                : exporter.Progress;

    public string ExportCurrentTrackName => obsExport?.SceneName ?? (HasIndependentVoiceExportStatus
        ? "Voice audio"
        : exporter.CurrentTrackName);

    public string ExportNextTrackName => obsExport is null && !HasIndependentVoiceExportStatus
        ? exporter.NextTrackName
        : string.Empty;

    public string ExportCurrentOutputFile => obsExport?.OutputPath ?? (HasIndependentVoiceExportStatus
        ? IndependentVoiceExportOutputFile
        : exporter.CurrentOutputFile);

    public int ExportCurrentTrackNumber => obsExport is null
        ? HasIndependentVoiceExportStatus && IndependentVoiceExportTrackCount > 0
            ? IndependentVoiceExportTrackCount
            : exporter.CurrentTrackNumber
        : 1;

    public int ExportTrackCount => obsExport is null
        ? HasIndependentVoiceExportStatus
            ? IndependentVoiceExportTrackCount
            : exporter.TrackCount
        : 1;

    public long ExportCurrentTrackFrame => obsExport is null ? exporter.CurrentTrackFrame : 0L;

    public long ExportCurrentTrackFramesTotal => obsExport is null ? exporter.CurrentTrackFramesTotal : 0L;

    public IReadOnlyList<DirectorReplayActorPathSource> ActorPathSources
    {
        get
        {
            Playback? active = playback;
            if (active is null)
            {
                return Array.Empty<DirectorReplayActorPathSource>();
            }

            return active.Document.Actors
                .Where(actor => actor.Samples.Count > 0)
                .Select(actor => new DirectorReplayActorPathSource(
                    actor.ActorKey,
                    ActorDisplayName(actor),
                    actor.PlayerUid,
                    actor.EntityId,
                    actor.EntityCode,
                    actor.Samples.Count,
                    IsPlayerActor(actor)))
                .OrderByDescending(actor => actor.IsPlayer)
                .ThenBy(actor => actor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(actor => actor.ActorKey, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<DirectorReplayActorPathSource> LiveActionActorSources
    {
        get
        {
            IEnumerable<DirectorReplayActorPathSource> replayActors = ActorPathSources;
            IEnumerable<DirectorReplayActorPathSource> liveActors = capi.World.LoadedEntities.Values
                .Where(IsCaptureEntityEligible)
                .Select(entity => LiveActionSource(entity))
                .OrderByDescending(actor => actor.IsPlayer)
                .ThenBy(actor => actor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(actor => actor.ActorKey, StringComparer.Ordinal);
            return replayActors.Concat(liveActors)
                .GroupBy(actor => actor.ActorKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }
    }

    public event Action? PlaybackStateChanged;

    public event Action<DirectorReplayInfo?, string>? RecordingSaveCompleted;

    internal bool IsReplayGhost(long entityId) => replayGhostIds.Contains(entityId);

    internal bool TryGetPlaybackAnchorPose(out CameraPreset pose)
    {
        DirectorReplayDocument? document = playback?.Document ?? pendingPlaybackStart?.Document;
        double timeSeconds = playback?.TimeSeconds ?? 0d;
        string preferredPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty;
        if (!DirectorReplayAnchorSelector.TrySelect(
                document,
                timeSeconds,
                preferredPlayerUid,
                out DirectorReplayAnchorPose anchor))
        {
            pose = null!;
            return false;
        }

        pose = new CameraPreset
        {
            X = anchor.X,
            Y = anchor.Y,
            Z = anchor.Z,
            Dimension = anchor.Dimension
        };
        return true;
    }

    internal bool TryGetChunkPreloadPlan(out Guid replayId, out DirectorReplayPreloadPlan plan)
    {
        if (pendingPlaybackStart is { } starting)
        {
            replayId = starting.Document.Id;
            plan = CreatePlaybackPreloadWindowPlan(starting.StartupPreloadColumns);
            return plan.ExpandedColumns.Count > 0;
        }
        if (playback is { } active)
        {
            replayId = active.Document.Id;
            plan = CreatePlaybackPreloadWindowPlan(BuildPlaybackPreloadWindow(
                active.Document,
                active.TimeSeconds,
                active.PreloadPlan));
            return plan.ExpandedColumns.Count > 0;
        }

        replayId = Guid.Empty;
        plan = null!;
        return false;
    }

    private static DirectorReplayPreloadPlan CreatePlaybackPreloadWindowPlan(
        IReadOnlyList<DirectorReplayPreloadColumn> columns)
    {
        DirectorReplayPreloadColumn[] snapshot = columns.ToArray();
        // The returned columns are already expanded around the observation
        // centers.  Sending RadiusChunks = 0 prevents the server from
        // multiplying the request again.
        return new DirectorReplayPreloadPlan(
            snapshot,
            snapshot,
            snapshot,
            radiusChunks: 0,
            truncated: false);
    }

    private IReadOnlyList<DirectorReplayPreloadColumn> BuildPlaybackPreloadWindow(
        DirectorReplayDocument document,
        double timeSeconds,
        DirectorReplayPreloadPlan fallbackPlan)
    {
        List<DirectorReplayPreloadColumn> centers = new();
        void AddCenter(double x, double z, int dimension)
        {
            if (!double.IsFinite(x) || !double.IsFinite(z) || dimension < 0)
            {
                return;
            }

            DirectorReplayPreloadColumn center = new(
                (int)Math.Floor(x / 32d),
                (int)Math.Floor(z / 32d),
                dimension);
            if (!centers.Contains(center))
            {
                centers.Add(center);
            }
        }

        if (controller.IsActive && controller.CurrentPose is { } cameraPose)
        {
            AddCenter(cameraPose.X, cameraPose.Z, cameraPose.Dimension);
        }

        string preferredPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty;
        if (DirectorReplayAnchorSelector.TrySelect(
                document,
                Math.Clamp(timeSeconds, 0d, document.DurationSeconds),
                preferredPlayerUid,
                out DirectorReplayAnchorPose replayPose))
        {
            AddCenter(replayPose.X, replayPose.Z, replayPose.Dimension);
        }

        if (centers.Count == 0)
        {
            DirectorReplayPreloadColumn fallback = fallbackPlan.SourceColumns.FirstOrDefault();
            if (fallbackPlan.SourceColumns.Count == 0)
            {
                fallback = fallbackPlan.ExpandedColumns.FirstOrDefault();
            }
            if (fallbackPlan.SourceColumns.Count > 0 || fallbackPlan.ExpandedColumns.Count > 0)
            {
                centers.Add(fallback);
            }
        }

        List<DirectorReplayPreloadColumn> window = new();
        HashSet<DirectorReplayPreloadColumn> unique = new();
        int radius = PlaybackPreloadWindowRadiusChunks;
        int radiusSquared = radius * radius;
        foreach (DirectorReplayPreloadColumn center in centers)
        {
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                for (int offsetZ = -radius; offsetZ <= radius; offsetZ++)
                {
                    if (offsetX * offsetX + offsetZ * offsetZ > radiusSquared)
                    {
                        continue;
                    }

                    DirectorReplayPreloadColumn column = new(
                        center.X + offsetX,
                        center.Z + offsetZ,
                        center.Dimension);
                    if (unique.Add(column))
                    {
                        window.Add(column);
                    }
                }
            }
        }

        return window;
    }

    internal void RequestPreloadEditor(Guid replayId)
    {
        if (replayId == Guid.Empty
            || pendingPlaybackStart?.Document.Id == replayId
            || playback?.Document.Id == replayId
            || loadedPreloadEditor?.Document.Id == replayId
            || pendingPreloadEditorLoad?.Info.Id == replayId)
        {
            return;
        }

        CancelPreloadEditorLoad();
        DirectorReplayInfo? info = store?.Resolve(replayId.ToString("D"));
        if (info is null)
        {
            return;
        }

        PendingPreloadEditorLoad loading = new(info);
        pendingPreloadEditorLoad = loading;
        loading.Task = LoadReplayWithServerCompanionAsync(
            info.Path,
            progress: null,
            loading.Cancellation.Token);
    }

    internal bool IsPreloadEditorLoading(Guid replayId)
        => pendingPreloadEditorLoad?.Info.Id == replayId;

    internal bool TryGetPreloadEditorState(
        Guid replayId,
        double timeSeconds,
        out DirectorReplayPreloadEditorState state)
    {
        DirectorReplayDocument? document = null;
        if (pendingPlaybackStart?.Document.Id == replayId)
        {
            document = pendingPlaybackStart.Document;
        }
        else if (playback?.Document.Id == replayId)
        {
            document = playback.Document;
        }
        else if (loadedPreloadEditor?.Document.Id == replayId)
        {
            document = loadedPreloadEditor.Document;
        }

        if (document is null)
        {
            state = default;
            return false;
        }

        string preferredPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty;
        bool quickMode = pendingPlaybackStart?.Document.Id == replayId
            ? pendingPlaybackStart.QuickMode
            : playback?.Document.Id == replayId && playback.QuickMode;
        DirectorReplayPreloadPlan plan = pendingPlaybackStart?.Document.Id == replayId
            ? pendingPlaybackStart.PreloadPlan
            : playback?.Document.Id == replayId
                ? playback.PreloadPlan
                : loadedPreloadEditor?.Document.Id == replayId
                    ? loadedPreloadEditor.PreloadPlan
                    : DirectorReplayPreloadPlanner.Build(
                        document,
                        preferredPlayerUid,
                        DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                            document,
                            quickMode,
                            config.ReplayApplyWorldSnapshot));
        int centerX;
        int centerZ;
        int dimension;
        if (DirectorReplayAnchorSelector.TrySelect(
                document,
                Math.Clamp(timeSeconds, 0d, document.DurationSeconds),
                preferredPlayerUid,
                out DirectorReplayAnchorPose pose))
        {
            centerX = (int)Math.Floor(pose.X / 32d);
            centerZ = (int)Math.Floor(pose.Z / 32d);
            dimension = pose.Dimension;
        }
        else
        {
            DirectorReplayPreloadColumn fallback = plan.SourceColumns.FirstOrDefault();
            centerX = fallback.X;
            centerZ = fallback.Z;
            dimension = fallback.Dimension;
        }

        state = new DirectorReplayPreloadEditorState(
            centerX,
            centerZ,
            dimension,
            plan,
            document.IncludedPreloadColumns.ToArray(),
            document.ExcludedPreloadColumns.ToArray());
        return true;
    }

    internal bool SavePreloadOverrides(
        Guid replayId,
        IEnumerable<DirectorReplayPreloadColumn> includedColumns,
        IEnumerable<DirectorReplayPreloadColumn> excludedColumns,
        out string error)
    {
        error = string.Empty;
        DirectorReplayDocument? document = null;
        string sourcePath = string.Empty;
        if (pendingPlaybackStart?.Document.Id == replayId)
        {
            document = pendingPlaybackStart.Document;
            sourcePath = pendingPlaybackStart.SourcePath;
        }
        else if (playback?.Document.Id == replayId)
        {
            document = playback.Document;
            sourcePath = playback.SourcePath;
        }
        else if (loadedPreloadEditor?.Document.Id == replayId)
        {
            document = loadedPreloadEditor.Document;
            sourcePath = loadedPreloadEditor.SourcePath;
        }

        if (document is null || sourcePath.Length == 0 || store is null)
        {
            error = "replay-not-loaded";
            return false;
        }

        document.IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(includedColumns);
        document.ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(excludedColumns);
        try
        {
            store.SaveTimelineEdits(sourcePath, document);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            capi.Logger.Warning("VS Director: replay preload overrides could not be saved: {0}", exception.Message);
            return false;
        }

        bool quickMode = pendingPlaybackStart?.Document.Id == replayId
            ? pendingPlaybackStart.QuickMode
            : playback?.Document.Id == replayId && playback.QuickMode;
        DirectorReplayPreloadPlan plan = DirectorReplayPreloadPlanner.Build(
            document,
            capi.World.Player?.PlayerUID,
            DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                document,
                quickMode,
                config.ReplayApplyWorldSnapshot));
        if (pendingPlaybackStart?.Document.Id == replayId)
        {
            pendingPlaybackStart.PreloadPlan = plan;
            pendingPlaybackStart.StartupPreloadColumns = BuildPlaybackPreloadWindow(
                pendingPlaybackStart.Document,
                timeSeconds: 0d,
                plan);
            pendingPlaybackStart.ChunksReady = false;
            pendingPlaybackStart.PreloadReadyColumns = 0;
            pendingPlaybackStart.LastPreloadCheckMs = 0;
        }
        if (playback?.Document.Id == replayId)
        {
            playback.PreloadPlan = plan;
        }
        if (loadedPreloadEditor?.Document.Id == replayId)
        {
            loadedPreloadEditor.PreloadPlan = plan;
        }

        PlaybackStateChanged?.Invoke();
        return true;
    }

    internal bool ShouldPrioritizePreloadedChunk(int chunkX, int chunkY, int chunkZ)
    {
        DirectorReplayPreloadPlan? plan = pendingPlaybackStart?.PreloadPlan ?? playback?.PreloadPlan;
        if (plan is null)
        {
            return false;
        }

        int dimension = Math.Max(0, chunkY / (DirectorChunkWindow.DimensionHeight / 32));
        return plan.ExpandedColumnSet.Contains(new DirectorReplayPreloadColumn(chunkX, chunkZ, dimension));
    }

    internal bool ShouldDriveReplayAnimation(long entityId)
    {
        ActorBinding? binding = FindReplayBinding(entityId);
        return binding?.HasAppliedSample == true
            && (binding.InterpolatedSample.AnimationStates.Count > 0
                || binding.InterpolatedSample.Animations.Count > 0);
    }

    internal void DetachReplayEntitiesForTick(List<KeyValuePair<long, Entity>> detached)
    {
        detached.Clear();
        foreach (long entityId in replayGhostIds)
        {
            if (!game.LoadedEntities.TryGetValue(entityId, out Entity? entity))
            {
                continue;
            }

            detached.Add(new KeyValuePair<long, Entity>(entityId, entity));
            game.LoadedEntities.Remove(entityId);
        }

        foreach (Entity entity in suppressedLiveEntities.Keys)
        {
            if (ReferenceEquals(entity, game.EntityPlayer)
                || !game.LoadedEntities.TryGetValue(entity.EntityId, out Entity? loaded)
                || !ReferenceEquals(entity, loaded))
            {
                continue;
            }

            detached.Add(new KeyValuePair<long, Entity>(entity.EntityId, entity));
            game.LoadedEntities.Remove(entity.EntityId);
        }

        IReadOnlyList<ActorBinding>? activeBindings = playback?.Bindings
            ?? pendingPlaybackStart?.Bindings;
        if (activeBindings is not null)
        {
            foreach (ActorBinding binding in activeBindings)
            {
                Entity entity = binding.Entity;
                if (binding.IsGhost
                    || ReferenceEquals(entity, game.EntityPlayer)
                    || !game.LoadedEntities.TryGetValue(entity.EntityId, out Entity? loaded)
                    || !ReferenceEquals(entity, loaded))
                {
                    continue;
                }

                detached.Add(new KeyValuePair<long, Entity>(entity.EntityId, entity));
                game.LoadedEntities.Remove(entity.EntityId);
            }
        }
    }

    internal void RestoreReplayGhostsAfterTick(List<KeyValuePair<long, Entity>> detached)
    {
        foreach ((long entityId, Entity entity) in detached)
        {
            game.LoadedEntities.TryAdd(entityId, entity);
        }
        detached.Clear();
    }

    internal bool IsReplayThirdPerson(long entityId)
        => replayGhostIds.Contains(entityId) || replayThirdPersonIds.Contains(entityId);

    internal bool TryGetReplayHandSlot(long entityId, bool rightHand, out ItemSlot? slot)
    {
        slot = null;
        ActorBinding? binding = FindReplayBinding(entityId);
        if (binding is null
            || !binding.HasAppliedSample
            || !binding.InterpolatedSample.HasPlayerState)
        {
            return false;
        }

        slot = rightHand ? binding.RightHandSlot : binding.LeftHandSlot;
        return true;
    }

    internal void CaptureRenderedPose(Entity entity)
    {
        if (recording is null
            || recordingPaused
            || applyingReplayState
            || entity.EntityId < 0
            || !IsCaptureEntityEligible(entity))
        {
            return;
        }

        renderedCapturePoses[entity.EntityId] = new ReplayRenderedPose(
            entity.Pos.X,
            entity.Pos.Y,
            entity.Pos.Z,
            entity.Pos.Dimension,
            entity.Pos.Yaw,
            entity.Pos.Pitch,
            entity.Pos.Roll,
            entity.Pos.HeadYaw,
            entity.Pos.HeadPitch,
            entity is EntityAgent agent ? agent.BodyYaw : entity.Pos.Yaw);
    }

    internal bool TryGetReplayItemRenderElapsedMilliseconds(long entityId, out long elapsedMilliseconds)
    {
        elapsedMilliseconds = 0L;
        ActorBinding? binding = FindReplayBinding(entityId);
        if (binding is null
            || !binding.HasAppliedSample
            || !binding.InterpolatedSample.HasItemRenderState)
        {
            return false;
        }

        elapsedMilliseconds = binding.InterpolatedSample.ItemRenderElapsedMilliseconds;
        return true;
    }

    internal bool PrepareReplayItemRenderState(Entity entity, object renderer)
    {
        ActorBinding? binding = FindReplayBinding(entity.EntityId);
        if (binding is null
            || !ReferenceEquals(binding.Entity, entity)
            || !binding.HasAppliedSample
            || !binding.InterpolatedSample.HasItemRenderState)
        {
            return false;
        }

        ApplyReplayItemRenderState(renderer, entity, binding.InterpolatedSample);
        return true;
    }

    internal bool TryGetReplayPlayerInventory(
        long entityId,
        string inventoryClassName,
        out IInventory? inventory)
    {
        inventory = null;
        ActorBinding? binding = FindReplayBinding(entityId);
        if (binding is null)
        {
            return false;
        }

        inventory = inventoryClassName switch
        {
            "character" => binding.ReplayEquipmentInventory,
            "backpack" => binding.ReplayBackpackInventory,
            _ => null
        };
        return inventory is not null;
    }

    internal bool TryGetReplayThirdPersonBodyYaw(long entityId, out float bodyYaw)
    {
        bodyYaw = 0f;
        ActorBinding? binding = FindReplayBinding(entityId);
        if (binding is null || !binding.HasAppliedSample)
        {
            return false;
        }

        DirectorReplayActorSample sample = binding.InterpolatedSample;
        bodyYaw = sample.HasThirdPersonPose
            ? sample.ThirdPersonBodyYaw
            : sample.BodyYaw;
        return true;
    }

    private ActorBinding? FindReplayBinding(long entityId)
    {
        if (playback?.BindingsByEntityId.TryGetValue(entityId, out ActorBinding? playbackBinding) == true)
        {
            return playbackBinding;
        }
        if (pendingPlaybackStart is { } pendingStart)
        {
            ActorBinding? pendingBinding = pendingStart.Bindings.FirstOrDefault(
                binding => binding.Entity.EntityId == entityId);
            if (pendingBinding is not null)
            {
                return pendingBinding;
            }
        }
        if (actionPreviewBinding?.Entity.EntityId == entityId)
        {
            return actionPreviewBinding;
        }
        foreach (ActorBinding binding in actionWorkflowPreviewBindings.Values)
        {
            if (binding.Entity.EntityId == entityId)
            {
                return binding;
            }
        }
        return null;
    }

    internal float ReplayParticleDeltaTime(float fallback)
    {
        float delta = dedicatedReplayRenderer.IsActive
            ? fallback
            : replayParticleFrameClock.Read(fallback).DeltaSeconds;
        return delta;
    }

    internal bool ShouldSuppressLiveParticleSpawns
        => playback is { } active
            && UsesDeterministicParticleTimeline(active)
            && !dedicatedReplayRenderer.IsActive;

    internal void BeginReplayParticleRenderFrame()
    {
        Playback? active = playback;
        bool deterministic = active is not null && UsesDeterministicParticleTimeline(active);
        if (deterministic && DirectorOffscreenRenderRuntime.IsRendering)
        {
            // Offscreen views are rendered after the primary frame. They must
            // see the same particle pose, but must not consume the primary
            // frame's replay delta or publish another advancing frame.
            replayParticleFrameClock.Publish(usesReplayClock: true, frameDeltaSeconds: 0f);
            return;
        }

        float deltaSeconds = deterministic
            && DirectorReplayPlaybackPolicy.ShouldAdvanceParticleClock(
                active!.Paused,
                exporter.IsExporting
                    || obsExport?.StartGate.RecordingStarted == true)
            ? Math.Max(0f, active.PendingParticleDeltaSeconds)
            : 0f;
        if (deterministic)
        {
            active!.PendingParticleDeltaSeconds = 0f;
        }
        replayParticleFrameClock.Publish(deterministic, deltaSeconds);
    }

    internal float ReplayParticlePoolDeltaTime(
        ParticlePoolQuads pool,
        bool offthread,
        float fallback)
    {
        if (dedicatedReplayRenderer.IsActive
            && !dedicatedReplayRenderer.OwnsParticlePool(pool))
        {
            return fallback;
        }

        // SystemRenderParticles renders the cube pool in the opaque pass and
        // the quad pool in the OIT pass. The same pool can be reached by more
        // than one renderer in custom clients, so consume the replay clock at
        // most once per pool and frame.
        DirectorReplayParticleFrameDelta frameDelta = replayParticleFrameClock.Consume(pool, fallback);
        if (!frameDelta.UsesReplayClock)
        {
            return frameDelta.DeltaSeconds;
        }

        float deltaSeconds = frameDelta.DeltaSeconds;
        if (offthread)
        {
            return deltaSeconds;
        }

        // ParticlePoolQuads multiplies main-thread dt by the calendar speed.
        // Undo that multiplier so the pool receives the exact replay-time
        // delta even when the recorded world clock is accelerated.
        float calendarSpeed = capi.World.Calendar.SpeedOfTime / 60f;
        return calendarSpeed > 0.0001f ? deltaSeconds / calendarSpeed : 0f;
    }

    internal void CompleteReplayParticleFrame()
    {
        // Async pools can finish after the OIT pass. The immutable frame
        // snapshot remains published until the next opaque particle frame.
    }

    private void ResetReplayParticleFrameState()
        => replayParticleFrameClock.Reset();

    internal bool IsLiveEntitySuppressed(Entity entity)
    {
        if (IsDedicatedReplayAnimationWarmup(entity))
        {
            return false;
        }

        if (suppressedLiveEntities.ContainsKey(entity))
        {
            return true;
        }

        Playback? active = playback;
        if (active is null)
        {
            return false;
        }

        if (replayGhostIds.Contains(entity.EntityId))
        {
            return false;
        }

        if ((entity is EntityItem || IsFallingBlockEntity(entity))
            && entity.EntityId >= 0
            && !active.Bindings.Any(binding => !binding.IsGhost && ReferenceEquals(binding.Entity, entity))
            && WithinCapturedWorld(active.Document, entity.Pos.AsBlockPos))
        {
            return true;
        }

        return false;
    }

    internal bool ShouldRenderReplayEntity(long entityId)
    {
        if (IsDedicatedReplayAnimationWarmup(entityId))
        {
            return true;
        }

        Playback? active = playback;
        if (active is null)
        {
            return true;
        }

        return !active.BindingsByEntityId.TryGetValue(entityId, out ActorBinding? binding)
            || ShouldRenderBinding(active, binding);
    }

    internal void EnforceEntityVisibilityAfterCulling()
    {
        Playback? active = playback;
        if (active is null)
        {
            return;
        }

        // Vanilla culling resets IsRendered before each render pass. Keep
        // every live entity in the suppression ledger hidden after that
        // reset, including custom renderers that do not use our renderer
        // patches.
        foreach (Entity entity in suppressedLiveEntities.Keys)
        {
            if (game.LoadedEntities.TryGetValue(entity.EntityId, out Entity? loaded)
                && ReferenceEquals(loaded, entity))
            {
                entity.IsRendered = false;
                entity.IsShadowRendered = false;
            }
        }

        CameraPreset? camera = controller.IsActive ? controller.CurrentPose : null;
        double cameraX = camera?.X ?? game.MainCamera.CameraEyePos.X;
        double cameraZ = camera?.Z ?? game.MainCamera.CameraEyePos.Z;
        int cameraDimension = camera?.Dimension ?? game.EntityPlayer.Pos.Dimension;
        double maxDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;

        foreach (ActorBinding binding in active.Bindings)
        {
            if (binding.IsGhost)
            {
                Entity? live = binding.HiddenLiveEntity;
                if (!string.IsNullOrWhiteSpace(binding.Track.PlayerUid))
                {
                    live = capi.World.PlayerByUid(binding.Track.PlayerUid)?.Entity
                        ?? capi.World.AllOnlinePlayers.FirstOrDefault(player => string.Equals(
                            player.PlayerUID,
                            binding.Track.PlayerUid,
                            StringComparison.Ordinal))?.Entity
                        ?? live;
                }

                if (live is not null && !ReferenceEquals(live, binding.Entity))
                {
                    SuppressLiveEntity(live);
                }
            }

            bool visible = ShouldPrepareBinding(active, binding)
                && IsInCurrentReplayView(
                    binding.Entity,
                    cameraX,
                    cameraZ,
                    cameraDimension,
                    maxDistanceSquared);
            if (visible && binding.HasAppliedSample && binding.AnimationPoseDirty)
            {
                ApplyPendingReplayAnimationPose(active, binding);
            }
            SetReplayEntityVisibility(binding.Entity, visible);
        }
    }

    internal void PrepareReplayEntityAnimationForOffscreenRender(Entity entity)
    {
        Playback? active = playback;
        if (active is null
            || !active.BindingsByEntityId.TryGetValue(entity.EntityId, out ActorBinding? binding)
            || !ReferenceEquals(binding.Entity, entity)
            || !binding.HasAppliedSample
            || !binding.AnimationPoseDirty)
        {
            return;
        }

        ApplyPendingReplayAnimationPose(active, binding);
    }

    internal bool NeedsDedicatedReplayAnimationWarmup(Entity entity)
    {
        Playback? active = playback;
        return dedicatedReplayRenderer.IsActive
            && active is not null
            && active.WorldPlayback?.IsSynchronizing != true
            && active.BindingsByEntityId.TryGetValue(entity.EntityId, out ActorBinding? binding)
            && ReferenceEquals(binding.Entity, entity)
            && binding.HasAppliedSample
            && binding.DedicatedAnimationWarmupPending
            && !binding.DedicatedAnimationPoseReady
            && binding.DedicatedAnimationViewEligible
            && binding.DedicatedAnimationWarmupLastAttemptFrame != replayAnimationFrame
            && replayAnimationFrame >= binding.DedicatedAnimationWarmupNextFrame;
    }

    internal bool BeginDedicatedReplayAnimationWarmup(Entity entity)
    {
        if (!NeedsDedicatedReplayAnimationWarmup(entity)
            || playback is not { } active
            || !active.BindingsByEntityId.TryGetValue(entity.EntityId, out ActorBinding? binding)
            || !ReferenceEquals(binding.Entity, entity)
            || dedicatedAnimationWarmupOwner is not null)
        {
            return false;
        }

        // Claim the attempt before invoking mod or engine renderer code. An
        // exception must not make another stage retry the same actor again in
        // the same physical replay frame.
        binding.DedicatedAnimationWarmupLastAttemptFrame = replayAnimationFrame;
        dedicatedAnimationWarmupOwner = this;
        dedicatedAnimationWarmupEntity = entity;
        return true;
    }

    internal void EndDedicatedReplayAnimationWarmup(Entity entity)
    {
        if (!ReferenceEquals(dedicatedAnimationWarmupOwner, this)
            || !ReferenceEquals(dedicatedAnimationWarmupEntity, entity))
        {
            return;
        }

        dedicatedAnimationWarmupEntity = null;
        dedicatedAnimationWarmupOwner = null;
    }

    internal bool CompleteDedicatedReplayAnimationWarmup(Entity entity)
    {
        Playback? active = playback;
        if (active is null
            || !dedicatedReplayRenderer.IsActive
            || !active.BindingsByEntityId.TryGetValue(entity.EntityId, out ActorBinding? binding)
            || !ReferenceEquals(binding.Entity, entity)
            || !binding.DedicatedAnimationWarmupPending
            || binding.DedicatedAnimationWarmupLastAttemptFrame != replayAnimationFrame)
        {
            return false;
        }

        if (entity.AnimManager?.Animator is null)
        {
            return RegisterDedicatedReplayAnimationWarmupFailure(active, binding);
        }

        try
        {
            ApplySample(
                entity,
                binding.InterpolatedSample,
                binding,
                forceAnimations: true,
                updateAnimations: true,
                poseAlreadyApplied: true);
            bool poseReady = true;
            if (binding.InterpolatedSample.AnimationStates.Count > 0)
            {
                binding.AnimationPoseDirty = true;
                poseReady = ApplyPendingReplayAnimationPose(active, binding);
            }

            if (!poseReady)
            {
                return RegisterDedicatedReplayAnimationWarmupFailure(active, binding);
            }

            binding.DedicatedAnimationPoseReady = true;
            binding.DedicatedAnimationWarmupPending = false;
            binding.DedicatedAnimationWarmupFallbackActive = false;
            binding.DedicatedAnimationWarmupAttempts = 0;
            binding.DedicatedAnimationWarmupNextFrame = -1L;
            CompleteDedicatedReplayAnimationVisibility(binding);
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: replay actor {0} animation pose warm-up failed: {1}",
                entity.EntityId,
                exception.GetBaseException().Message);
            return RegisterDedicatedReplayAnimationWarmupFailure(active, binding);
        }
    }

    internal bool FailDedicatedReplayAnimationWarmup(Entity entity)
    {
        Playback? active = playback;
        return active is not null
            && active.BindingsByEntityId.TryGetValue(entity.EntityId, out ActorBinding? binding)
            && ReferenceEquals(binding.Entity, entity)
            && binding.DedicatedAnimationWarmupPending
            && binding.DedicatedAnimationWarmupLastAttemptFrame == replayAnimationFrame
            && RegisterDedicatedReplayAnimationWarmupFailure(active, binding);
    }

    internal void RecordDedicatedReplayAnimationWarmupWork(
        long elapsedTicks,
        int attemptedCount)
    {
        Playback? active = playback;
        if (active is null
            || !dedicatedReplayRenderer.IsActive
            || elapsedTicks <= 0L
            || attemptedCount <= 0)
        {
            return;
        }

        double elapsedMilliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        active.DedicatedAnimationBudget =
            DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                active.DedicatedAnimationBudget,
                elapsedMilliseconds,
                attemptedCount,
                targetMilliseconds:
                    DirectorReplayActorSchedulingPolicy.DedicatedWarmupWorkBudgetMilliseconds);
    }

    private bool RegisterDedicatedReplayAnimationWarmupFailure(
        Playback active,
        ActorBinding binding)
    {
        binding.DedicatedAnimationWarmupAttempts++;
        if (DirectorReplayActorSchedulingPolicy.ShouldFallbackAfterWarmupFailure(
                binding.DedicatedAnimationWarmupAttempts))
        {
            // Leave the actor visible so the native renderer can complete lazy
            // model and animator creation. A stale/static pose is preferable
            // to making the replay model disappear indefinitely.
            binding.DedicatedAnimationPoseReady = false;
            binding.DedicatedAnimationWarmupPending = true;
            binding.DedicatedAnimationWarmupFallbackActive = true;
            binding.DedicatedAnimationWarmupNextFrame = replayAnimationFrame
                + DirectorReplayActorSchedulingPolicy.DedicatedMaximumWarmupRetryDelayFrames;
            binding.AnimationPoseDirty = true;
            if (!binding.DedicatedAnimationWarmupFallbackLogged)
            {
                binding.DedicatedAnimationWarmupFallbackLogged = true;
                capi.Logger.Warning(
                    "VS Director: replay actor {0} did not create an animator after {1} attempts; rendering its current pose and retrying at a low rate.",
                    binding.Entity.EntityId,
                    binding.DedicatedAnimationWarmupAttempts);
            }
            return false;
        }

        binding.DedicatedAnimationPoseReady = false;
        binding.DedicatedAnimationWarmupPending = true;
        binding.DedicatedAnimationWarmupNextFrame = replayAnimationFrame
            + DirectorReplayActorSchedulingPolicy.WarmupRetryDelayFrames(
                binding.DedicatedAnimationWarmupAttempts);
        return false;
    }

    private void CompleteDedicatedReplayAnimationVisibility(ActorBinding binding)
    {
        binding.LastAnimationUpdateFrame = replayAnimationFrame;
        // ApplyPlaybackFrame owns the main-view visibility snapshot. Render
        // stages may currently hold a shadow or offscreen frustum, so they
        // must not recalculate this value while completing a warm-up.
        binding.WasInCurrentView = binding.DedicatedAnimationMainViewVisible;
        SetReplayEntityVisibility(binding.Entity, binding.WasInCurrentView);
    }

    private bool IsDedicatedReplayAnimationWarmup(Entity entity)
        => dedicatedReplayRenderer.IsActive
            && playback is not null
            && ReferenceEquals(dedicatedAnimationWarmupOwner, this)
            && ReferenceEquals(dedicatedAnimationWarmupEntity, entity);

    private bool IsDedicatedReplayAnimationWarmup(long entityId)
    {
        if (!dedicatedReplayRenderer.IsActive
            || playback is not { } active
            || !ReferenceEquals(dedicatedAnimationWarmupOwner, this)
            || dedicatedAnimationWarmupEntity?.EntityId != entityId
            || !active.BindingsByEntityId.TryGetValue(entityId, out ActorBinding? binding))
        {
            return false;
        }

        return ReferenceEquals(binding.Entity, dedicatedAnimationWarmupEntity);
    }

    private static bool ApplyPendingReplayAnimationPose(
        Playback active,
        ActorBinding binding,
        float? advanceSeconds = null)
    {
        IAnimationManager? animationManager = binding.Entity.AnimManager;
        if (animationManager?.Animator is null)
        {
            return false;
        }

        float deltaSeconds = advanceSeconds ?? active.PendingParticleDeltaSeconds;
        if (binding.Entity is EntityPlayer)
        {
            AdvanceReplayPlayerAnimationPose(animationManager, deltaSeconds);
        }
        else
        {
            ApplyRecordedAnimationState(
                animationManager,
                binding.InterpolatedSample.AnimationStates,
                recalculatePose: true,
                onlyActive: true,
                advanceSeconds: deltaSeconds,
                applyRecordedRenderMetadata: true);
        }
        binding.AnimationPoseDirty = false;
        return true;
    }

    private static void AdvanceReplayPlayerAnimationPose(
        IAnimationManager animationManager,
        float advanceSeconds)
    {
        float deltaSeconds = float.IsFinite(advanceSeconds) ? Math.Max(0f, advanceSeconds) : 0f;
        animationManager.HeadController?.OnFrame(deltaSeconds);
        animationManager.Animator?.OnFrame(
            animationManager.ActiveAnimationsByAnimCode,
            deltaSeconds);
    }

    private bool IsInCurrentReplayView(
        Entity entity,
        double cameraX,
        double cameraZ,
        int cameraDimension,
        double maxDistanceSquared)
    {
        if (entity.Pos.Dimension != cameraDimension)
        {
            return false;
        }

        double dx = entity.Pos.X - cameraX;
        double dz = entity.Pos.Z - cameraZ;
        return (entity.AllowOutsideLoadedRange
                || (dx * dx + dz * dz <= maxDistanceSquared
                    && game.WorldMap.IsChunkRendered(entity.Pos)))
            && capi.Render.DefaultFrustumCuller.SphereInFrustum(
                entity.Pos.X,
                entity.Pos.InternalY,
                entity.Pos.Z,
                entity.FrustumSphereRadius);
    }

    public void RefreshConfiguration()
    {
        if (!config.ReplayPauseWorld && singleplayerWorldSuspension.IsActive)
        {
            singleplayerWorldSuspension.Release();
        }
        inputLockHandler(
            playback is not null
            && (config.LockInputDuringReplay || singleplayerWorldSuspension.IsActive));
        if (playback is not null
            && config.ReplayUseDedicatedRenderer != dedicatedReplayRenderer.IsActive)
        {
            capi.Logger.Debug(
                "VS Director: dedicated replay renderer setting will apply when the next replay starts.");
        }
    }

    public void InitializeWorld(string worldId)
    {
        ClearPreloadEditor();
        StopActionWorkflowPreviewActors();
        StopActionPreviewActor();
        StopPlayback();
        if (recording is not null && !RequestStopRecording(out string error))
        {
            capi.Logger.Warning("VS Director: replay could not be saved before switching worlds: {0}", error);
        }

        RetireStore(store);
        try { store = new DirectorReplayStore(capi, worldId, config.ReplayOutputDirectory); }
        catch (Exception e) { store = null; capi.Logger.Error("VS Director replay directory: {0}", e.Message); capi.ShowChatMessage(Lang.Get("vsdirector:replay-directory-error", e.Message)); }
    }

    public void LeaveWorld()
    {
        CancelLiveActionRecording();
        ClearPreloadEditor();
        StopActionWorkflowPreviewActors();
        StopActionPreviewActor();
        StopPlayback();
        if (recording is not null && !RequestStopRecording(out string error))
        {
            capi.Logger.Warning("VS Director: replay could not be saved while leaving world: {0}", error);
        }

        RetireStore(store);
        store = null;
        inputLockHandler(false);
    }
    internal void RefreshReplayDirectory(string directory)
    {
        if (string.IsNullOrEmpty(capi.World?.SavegameIdentifier)) return;
        if (IsRecording || HasPendingSave || IsPlaying || IsLoading || IsExporting)
            throw new InvalidOperationException("Stop recording, playback and export, and finish saving before changing the replay directory.");
        var next = new DirectorReplayStore(capi, capi.World.SavegameIdentifier, directory);
        RetireStore(store); store = next;
    }

    public bool StartRecording(string requestedName, out string error)
        => StartRecordingCore(requestedName, out error);

    private bool StartRecordingCore(string requestedName, out string error)
    {
        PollPendingSave();
        error = string.Empty;
        if (store is null)
        {
            error = "world-not-ready";
            return false;
        }

        if (recording is not null)
        {
            error = "replay-recording-active";
            return false;
        }

        if (pendingSave is not null)
        {
            error = pendingSave.Operation.IsSaving
                ? "replay-save-in-progress"
                : "replay-save-retry-required";
            return false;
        }

        StopPlayback();
        string name = string.IsNullOrWhiteSpace(requestedName) ? "Replay" : requestedName.Trim();
        DirectorReplayDocument document = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            WorldId = capi.World.SavegameIdentifier ?? string.Empty,
            CreatedUtc = DateTimeOffset.UtcNow,
            SampleRate = DirectorReplayMath.NormalizeSampleRate(config.ReplaySampleRate),
            ParticleCaptureVersion = config.ReplayCaptureParticles
                ? DirectorReplayDocument.CurrentParticleCaptureVersion
                : 0
        };
        if (config.ReplayCaptureWorld)
        {
            try
            {
                document.WorldSnapshot = DirectorReplayWorldCapture.Capture(
                    capi,
                    game,
                    config.ReplaySnapshotRadiusChunks,
                    controller.CurrentPose);
                document.IncludesWorldSnapshot = document.WorldSnapshot.Chunks.Count > 0;
                document.CaptureScope = document.IncludesWorldSnapshot
                    ? DirectorReplayDocument.ClientWorldCaptureScope
                    : DirectorReplayDocument.ClientActorCaptureScope;
            }
            catch (Exception exception)
            {
                capi.Logger.Error("VS Director: client world snapshot failed; actor capture will continue: {0}", exception);
            }
        }

        recordingActors = new Dictionary<string, DirectorReplayActorTrack>(StringComparer.Ordinal);
        recordingEntityActors.Clear();
        recordingReusableActorTypes.Clear();
        recordingVisibleActors = new HashSet<string>(StringComparer.Ordinal);
        recordingBlockEntities = document.WorldSnapshot.BlockEntities.ToDictionary(
            snapshot => BlockKey.From(snapshot),
            snapshot => new CachedBlockEntity(snapshot.ClassName, CloneBytes(snapshot.Data)));
        recordingBlockStates = new Dictionary<BlockKey, CachedBlockState>();
        pendingBlockChanges = new HashSet<BlockKey>();
        pausedBlockChanges = new HashSet<BlockKey>();
        pausedBlockResyncBatches.Clear();
        recordingSnapshotChunks = document.WorldSnapshot.Chunks.ToDictionary(
            chunk => new ChunkKey(
                chunk.ChunkX,
                chunk.ChunkY,
                chunk.ChunkZ,
                document.WorldSnapshot.Dimension));
        decodedSnapshotChunkKey = null;
        decodedSnapshotSolid = null;
        decodedSnapshotFluid = null;
        recordingElapsedSeconds = 0d;
        recordingAccumulator = 0d;
        entityStateAccumulator = 0d;
        recordingPaused = false;
        lock (particleCaptureSync)
        {
            capturedParticles.Clear();
            recording = document;
        }
        CaptureAlreadyPlayingLoadedSounds();
        pendingEntityStateCaptures.Clear();
        pendingBlockEntityCapture = null;
        CaptureFrame(0d);
        CaptureFullEntityStates(0d);
        return true;
    }

    public bool StopRecording(out DirectorReplayInfo? info, out string error)
    {
        PollPendingSave();
        info = null;
        error = string.Empty;
        if (pendingSave?.Operation.IsSaving == true)
        {
            error = "replay-save-in-progress";
            return false;
        }

        if (pendingSave is null && !TryFreezeRecording(out error))
        {
            return false;
        }

        PendingReplaySave active = pendingSave!;
        try
        {
            string path = active.Store.Save(active.Document, active.Compression);
            info = CompletePendingSave(active, path);
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: replay save failed: {0}", exception);
            error = exception.Message;
            FailPendingSave(active, error);
            return false;
        }
    }

    public bool RequestStopRecording(out string error)
    {
        PollPendingSave();
        error = string.Empty;
        if (pendingSave?.Operation.IsSaving == true)
        {
            error = "replay-save-in-progress";
            return false;
        }

        if (pendingSave is null && !TryFreezeRecording(out error))
        {
            return false;
        }

        PendingReplaySave active = pendingSave!;
        active.Error = string.Empty;
        active.Operation.Start();
        SchedulePendingSaveCompletion(active);
        PlaybackStateChanged?.Invoke();
        return true;
    }

    private void SchedulePendingSaveCompletion(PendingReplaySave active)
    {
        Task? completion = active.Operation.Completion;
        if (completion is null)
        {
            return;
        }

        _ = completion.ContinueWith(
            _ =>
            {
                if (disposed)
                {
                    active.Store.Dispose();
                    return;
                }

                try
                {
                    capi.Event.EnqueueMainThreadTask(
                        () =>
                        {
                            if (disposed)
                            {
                                active.Store.Dispose();
                            }
                            else if (ReferenceEquals(pendingSave, active))
                            {
                                PollPendingSave();
                            }
                        },
                        "vsdirector-replay-save-completed");
                }
                catch (Exception exception)
                {
                    capi.Logger.Warning(
                        "VS Director: replay save completion could not be queued on the main thread: {0}",
                        exception.Message);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryFreezeRecording(out string error)
    {
        error = string.Empty;
        DirectorReplayDocument? active = recording;
        DirectorReplayStore? activeStore = store;
        if (active is null || activeStore is null)
        {
            error = "replay-not-recording";
            return false;
        }

        try
        {
            bool wasPaused = recordingPaused;
            if (!wasPaused)
            {
                ProcessPausedBlockResync(int.MaxValue);
                FlushPendingBlockChanges();
                CaptureFrame(recordingElapsedSeconds);
                CaptureFullEntityStates(recordingElapsedSeconds);
                CaptureBlockEntityChanges(recordingElapsedSeconds);
            }
            lock (soundCaptureSync)
            {
                lock (particleCaptureSync)
                {
                    active.DurationSeconds = recordingElapsedSeconds;
                    pendingSave = new PendingReplaySave(
                        active,
                        activeStore,
                        config.ReplayCompression,
                        wasPaused);
                    ClearRecordingState();
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: replay could not be frozen for saving: {0}", exception);
            error = exception.Message;
            return false;
        }
    }

    public bool ToggleRecordingPause(out bool paused)
    {
        if (recording is null)
        {
            paused = false;
            return false;
        }

        if (!recordingPaused)
        {
            ProcessPausedBlockResync(int.MaxValue);
            FlushPendingBlockChanges();
            CaptureFrame(recordingElapsedSeconds);
            CaptureFullEntityStates(recordingElapsedSeconds);
            CaptureBlockEntityChanges(recordingElapsedSeconds);
            pendingEntityStateCaptures.Clear();
            pendingBlockEntityCapture = null;
            recordingPaused = true;
        }
        else
        {
            recordingPaused = false;
            ResynchronizeLoadedSounds();
            if (pausedBlockChanges.Count > 0)
            {
                pausedBlockResyncBatches.Enqueue(new PausedBlockResyncBatch(
                    pausedBlockChanges.ToArray(),
                    recordingElapsedSeconds));
                pausedBlockChanges.Clear();
            }
            QueueBlockEntityCapture(recordingElapsedSeconds);
            CaptureFrame(recordingElapsedSeconds);
            CaptureFullEntityStates(recordingElapsedSeconds);
        }

        paused = recordingPaused;
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<DirectorReplayInfo> List() => store?.List() ?? Array.Empty<DirectorReplayInfo>();

    public bool UpdateMetadata(
        Guid id,
        string name,
        IEnumerable<string>? tags,
        string? description,
        out DirectorReplayInfo? updated,
        out string error)
    {
        updated = null;
        error = string.Empty;
        if (store is null)
        {
            error = "world-not-ready";
            return false;
        }

        if (!store.UpdateMetadata(id, name, tags, description, out updated, out error)
            || updated is null)
        {
            return false;
        }

        if (playback?.Document.Id == id)
        {
            playback.Document.Name = updated.Name;
        }
        if (pendingLoad?.Info.Id == id)
        {
            pendingLoad.Info = updated;
        }
        if (pendingPlaybackStart?.Document.Id == id)
        {
            pendingPlaybackStart.Document.Name = updated.Name;
        }
        if (pendingPreloadEditorLoad?.Info.Id == id)
        {
            pendingPreloadEditorLoad.Info = updated;
        }
        if (loadedPreloadEditor?.Document.Id == id)
        {
            loadedPreloadEditor.Document.Name = updated.Name;
        }
        return true;
    }

    public bool Delete(Guid id, out string deletedName, out string error)
    {
        deletedName = string.Empty;
        error = string.Empty;
        if (store is null)
        {
            error = "world-not-ready";
            return false;
        }

        if (recording?.Id == id || pendingSave?.Document.Id == id)
        {
            error = "replay-recording-active";
            return false;
        }

        if (playback?.Document.Id == id || pendingLoad?.Info.Id == id)
        {
            StopPlayback();
        }

        bool deleted = store.Delete(id, out deletedName, out error);
        if (deleted)
        {
            if (pendingPreloadEditorLoad?.Info.Id == id)
            {
                CancelPreloadEditorLoad();
            }
            if (loadedPreloadEditor?.Document.Id == id)
            {
                loadedPreloadEditor = null;
            }
        }
        return deleted;
    }

    public void RefreshIndex() => store?.BeginIndexRefresh(force: true);

    public bool StartPlayback(
        string selector,
        out int missingActors,
        out string error,
        bool pauseAfterLoad = false)
    {
        missingActors = 0;
        error = string.Empty;
        DirectorReplayStore? activeStore = store;
        if (activeStore is null)
        {
            error = "world-not-ready";
            return false;
        }

        if (recording is not null || pendingSave is not null)
        {
            error = recording is not null
                ? "replay-recording-active"
                : pendingSave!.Operation.IsSaving
                    ? "replay-save-in-progress"
                    : "replay-save-retry-required";
            return false;
        }

        DirectorReplayInfo? info = activeStore.Resolve(selector);
        if (info is null)
        {
            error = activeStore.IsIndexing ? "replay-indexing" : "replay-not-found";
            return false;
        }

        StopActionWorkflowPreviewActors();
        StopActionPreviewActor();
        StopPlayback();
        playbackLoadError = string.Empty;
        PendingReplayLoad loading = new(info, config.ReplayQuickMode, pauseAfterLoad);
        pendingLoad = loading;
        replayLoadOverlayVisible = pauseAfterLoad;
        loading.Task = LoadReplayWithServerCompanionAsync(
            info.Path,
            new ReplayLoadProgress(loading),
            loading.Cancellation.Token);
        return true;
    }

    private ActorBinding? CreatePlaybackBinding(
        DirectorReplayActorTrack actor,
        bool preferLiveActor,
        HashSet<long> claimedEntityIds,
        out bool missing)
    {
        missing = false;
        Entity? liveEntity = ResolveLiveActor(actor);
        Entity? replayEntity = null;
        bool liveActorAlreadyClaimed = liveEntity is not null
            && claimedEntityIds.Contains(liveEntity.EntityId);
        bool ghost = actor.ForceGhost
            || liveActorAlreadyClaimed
            || (!preferLiveActor || liveEntity is null || liveEntity is EntityPlayer);
        if (ghost)
        {
            replayEntity = TryCreateGhost(
                actor,
                liveActorAlreadyClaimed ? null : liveEntity);
            if (actor.ForceGhost && replayEntity is null)
            {
                missing = true;
                return null;
            }
            ghost = replayEntity is not null;
        }

        replayEntity ??= liveEntity;
        if (replayEntity is null)
        {
            missing = true;
            return null;
        }
        if (!claimedEntityIds.Add(replayEntity.EntityId))
        {
            if (ghost)
            {
                RemoveGhost(replayEntity);
            }
            missing = true;
            return null;
        }

        byte[] savedState = ghost || liveEntity is null
            ? Array.Empty<byte>()
            : TrySerializeEntity(liveEntity, liveEntity.EntityId);
        bool hiddenLiveRendered = liveEntity?.IsRendered == true;
        bool? savedTpUseFpAnimations = null;
        bool? savedSelfUseFpAnimations = null;
        if (replayEntity is EntityPlayer replayPlayer)
        {
            savedTpUseFpAnimations = replayPlayer.TpAnimManager.UseFpAnmations;
            savedSelfUseFpAnimations = replayPlayer.SelfFpAnimManager.UseFpAnmations;
        }

        ActorBinding binding = new(
            actor,
            replayEntity,
            CaptureSample(replayEntity, 0d),
            savedState,
            replayEntity.IsRendered,
            replayEntity.IsShadowRendered,
            replayEntity is EntityItem savedItem ? savedItem.itemSpawnedMilliseconds : null,
            ghost,
            ghost && !liveActorAlreadyClaimed ? liveEntity : null,
            hiddenLiveRendered,
            savedTpUseFpAnimations,
            savedSelfUseFpAnimations);
        return binding;
    }

    private void InitializePlaybackBinding(ActorBinding binding)
    {
        EntityPlayer? replayPlayer = binding.Entity as EntityPlayer;
        if (replayPlayer is not null)
        {
            replayThirdPersonIds.Add(binding.Entity.EntityId);
            ConfigureReplayPlayer(replayPlayer, markShapeModified: true);
        }
        InitializePlaybackBindingState(binding);
        if (replayPlayer is not null)
        {
            // EntityPlayer.FromBytes restores the source actor's active
            // animation manager. A local first-person snapshot can therefore
            // seed TpAnimManager with idle-fp/walk-fp layers before the replay
            // samples run. Clear that imported state once, then let recorded
            // controls and explicit actions rebuild the observer animation set.
            ResetReplayAnimations(binding, replayPlayer.TpAnimManager);
        }
        PrepareReplayHandItems(binding);
        if (replayPlayer is not null)
        {
            // The renderer may have tessellated the ghost while it was being
            // created, before the binding and replay inventory existed. Force
            // one more pass after the recorded equipment slots are ready.
            replayPlayer.MarkShapeModified();
        }
        if (binding.IsGhost && binding.HiddenLiveEntity is not null)
        {
            SuppressLiveEntity(binding.HiddenLiveEntity);
        }
        if (binding.IsGhost)
        {
            ParkReplayEntity(binding.Entity);
        }
        else
        {
            SetReplayEntityVisibility(binding.Entity, false);
        }
    }

    private void InitializePlaybackBindingState(ActorBinding binding)
    {
        if (!DirectorReplayMath.IsActorVisible(binding.Track, 0d))
        {
            return;
        }

        DirectorReplayActorState? state = DirectorReplayMath.FindActorState(binding.Track, 0d);
        if (state is null)
        {
            return;
        }

        if (CanRestoreFullEntityState(binding))
        {
            bool wasApplyingReplayState = applyingReplayState;
            SetApplyingReplayState(true);
            try
            {
                ApplyEntityState(binding.Entity, state.Data);
                if (binding.Entity is EntityPlayer replayPlayer)
                {
                    ConfigureReplayPlayer(replayPlayer, markShapeModified: true);
                }
            }
            finally
            {
                SetApplyingReplayState(wasApplyingReplayState);
            }
        }

        binding.LastAppliedState = state;
        binding.FullStateRestorePending = false;
    }

    private bool FinishLoadedPlayback(
        DirectorReplayDocument document,
        string sourcePath,
        bool quickMode,
        DirectorReplayPreloadPlan preloadPlan,
        List<ActorBinding> bindings,
        bool pauseAfterLoad,
        out int missingActors,
        out string error)
    {
        missingActors = document.Actors.Count - bindings.Count;
        error = string.Empty;
        bool hasWorldContent = DirectorReplayPlaybackPolicy.HasNonActorContent(document, quickMode);
        if (bindings.Count == 0 && !hasWorldContent)
        {
            error = "replay-no-actors";
            return false;
        }

        DirectorReplayWorldPlayback? worldPlayback = null;
        if (DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                document,
                quickMode,
                config.ReplayApplyWorldSnapshot))
        {
            worldPlayback = new DirectorReplayWorldPlayback(
                capi,
                game,
                document,
                SetApplyingReplayState,
                () => applyingReplayState);
            try
            {
                worldPlayback.Start();
            }
            catch (Exception exception)
            {
                capi.Logger.Error("VS Director: replay world baseline could not be applied: {0}", exception);
                worldPlayback.Dispose();
                worldPlayback = null;
            }
        }

        playback = new Playback(
            document,
            sourcePath,
            quickMode,
            preloadPlan,
            bindings,
            worldPlayback,
            game.timelapse,
            game.timelapsedCurrent,
            game.timelapseEnd,
            capi.World.Calendar.Timelapse,
            capi.World.Calendar.CalendarSpeedMul,
            capi.World.Calendar.SeasonOverride);
        // Selecting a replay is a load/inspection action. Leave the first
        // frame paused so a large replay cannot begin advancing while the
        // player is still waiting for the loading overlay to finish.
        playback.Paused = pauseAfterLoad;
        bool shouldSuspendSingleplayer = config.ReplayPauseWorld
            && DirectorSingleplayerReplaySuspension.IsEligible(
                capi.IsSinglePlayer,
                capi.OpenedToLan,
                game.BlocksReceivedAndLoaded);
        if (shouldSuspendSingleplayer && !singleplayerWorldSuspension.TryAcquire())
        {
            error = "replay-world-suspend-failed";
            return false;
        }
        SuppressLoadedRealityEntities(playback);
        SuppressLiveSpatialSounds(playback);
        if (config.ReplayUseDedicatedRenderer)
        {
            try
            {
                dedicatedReplayRenderer.Start(
                    bindings
                        .Select(binding => binding.Entity),
                    document.ParticleEvents.Count);
                foreach (ActorBinding binding in bindings)
                {
                    if (!ReferenceEquals(binding.Entity, game.EntityPlayer))
                    {
                        binding.DedicatedAnimationPoseReady = false;
                        ResetDedicatedReplayAnimationWarmup(binding);
                    }
                }
                capi.Logger.Notification(
                    "VS Director: experimental dedicated replay renderer enabled for '{0}' ({1} actors).",
                    document.Name,
                    dedicatedReplayRenderer.ActorCount);
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "VS Director: dedicated replay renderer could not be enabled; using the preview renderer: {0}",
                    exception.GetBaseException().Message);
                dedicatedReplayRenderer.Stop();
            }
        }
        SetPlaybackChunkBuffering(false);
        ClearReplayParticles();
        replayParticleFailureWarnings.Clear();
        inputLockHandler(config.LockInputDuringReplay || singleplayerWorldSuspension.IsActive);
        ApplyPlaybackFrame(playback);
        return true;
    }

    public bool TogglePause(out bool paused)
    {
        if (playback is null)
        {
            paused = false;
            return false;
        }

        if (DirectorReplayPlaybackPolicy.ShouldRestartFromEnd(
                playback.Paused,
                playback.TimeSeconds,
                playback.Document.DurationSeconds))
        {
            playback.TimeSeconds = 0d;
            playback.TimedEventSeekPending = true;
            ApplyPlaybackFrame(playback);
        }
        playback.Paused = !playback.Paused;
        paused = playback.Paused;
        if (!paused)
        {
            replayLoadOverlayVisible = false;
        }
        UpdateReplaySoundPauseState(playback);
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool SetPaused(bool paused)
    {
        if (playback is null)
        {
            return false;
        }

        playback.Paused = paused;
        if (!paused)
        {
            replayLoadOverlayVisible = false;
        }
        UpdateReplaySoundPauseState(playback);
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool Seek(double timeSeconds, out double applied)
    {
        if (playback is null || !double.IsFinite(timeSeconds))
        {
            applied = 0d;
            return false;
        }

        playback.TimeSeconds = Math.Clamp(timeSeconds, 0d, playback.Document.DurationSeconds);
        playback.TimedEventSeekPending = true;
        applied = playback.TimeSeconds;
        ApplyPlaybackFrame(playback);
        return true;
    }

    public bool SetSpeed(double speed, out double applied)
    {
        if (playback is null
            || !double.IsFinite(speed)
            || speed < DirectorReplayTimeline.MinSpeed
            || speed > DirectorReplayTimeline.MaxSpeed)
        {
            applied = playback?.Speed ?? 1d;
            return false;
        }

        playback.Speed = speed;
        applied = speed;
        return true;
    }

    public bool AddMarker(
        string label,
        double? requestedTime,
        out DirectorReplayMarker marker,
        out string error)
    {
        marker = new DirectorReplayMarker();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        string normalizedLabel = label?.Trim() ?? string.Empty;
        if (normalizedLabel.Length == 0)
        {
            error = "replay-marker-name";
            return false;
        }

        double time = requestedTime ?? PlaybackTimeSeconds;
        double duration = recording is null ? document.DurationSeconds : recordingElapsedSeconds;
        if (!double.IsFinite(time) || time < 0d || time > duration + TimeEpsilon)
        {
            error = "replay-marker-time";
            return false;
        }

        marker = new DirectorReplayMarker
        {
            Id = Guid.NewGuid(),
            TimeSeconds = Math.Clamp(time, 0d, duration),
            Label = normalizedLabel.Length <= 80 ? normalizedLabel : normalizedLabel[..80]
        };
        document.Markers.Add(marker);
        document.Markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        if (!PersistTimelineEdit(document, out error))
        {
            Guid markerId = marker.Id;
            document.Markers.RemoveAll(item => item.Id == markerId);
            marker = new DirectorReplayMarker();
            return false;
        }

        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool DeleteMarker(string selector, out DirectorReplayMarker marker, out string error)
    {
        marker = new DirectorReplayMarker();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        DirectorReplayMarker? found = ResolveMarker(document.Markers, selector);
        if (found is null)
        {
            error = "replay-marker-not-found";
            return false;
        }

        int index = document.Markers.IndexOf(found);
        document.Markers.RemoveAt(index);
        if (!PersistTimelineEdit(document, out error))
        {
            document.Markers.Insert(index, found);
            return false;
        }

        marker = found.DeepClone();
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool UpdateMarker(
        string selector,
        string label,
        double requestedTime,
        out DirectorReplayMarker marker,
        out string error)
    {
        marker = new DirectorReplayMarker();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        DirectorReplayMarker? found = ResolveMarker(document.Markers, selector);
        if (found is null)
        {
            error = "replay-marker-not-found";
            return false;
        }

        string normalizedLabel = label?.Trim() ?? string.Empty;
        double duration = recording is null ? document.DurationSeconds : recordingElapsedSeconds;
        if (normalizedLabel.Length == 0)
        {
            error = "replay-marker-name";
            return false;
        }
        if (!double.IsFinite(requestedTime)
            || requestedTime < 0d
            || requestedTime > duration + TimeEpsilon)
        {
            error = "replay-marker-time";
            return false;
        }

        DirectorReplayMarker previous = found.DeepClone();
        found.Label = normalizedLabel.Length <= 80 ? normalizedLabel : normalizedLabel[..80];
        found.TimeSeconds = Math.Clamp(requestedTime, 0d, duration);
        document.Markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        if (!PersistTimelineEdit(document, out error))
        {
            found.Label = previous.Label;
            found.TimeSeconds = previous.TimeSeconds;
            document.Markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
            return false;
        }

        marker = found.DeepClone();
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool JumpToMarker(string selector, out DirectorReplayMarker marker)
    {
        marker = new DirectorReplayMarker();
        Playback? active = playback;
        DirectorReplayMarker? found = active is null ? null : ResolveMarker(active.Document.Markers, selector);
        if (found is null || !Seek(found.TimeSeconds, out _))
        {
            return false;
        }

        marker = found.DeepClone();
        return true;
    }

    public bool AddSpeedKeyframe(
        double speed,
        double? requestedTime,
        DirectorReplaySpeedInterpolation interpolation,
        out DirectorReplaySpeedKeyframe keyframe,
        out string error)
    {
        keyframe = new DirectorReplaySpeedKeyframe();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        double time = requestedTime ?? PlaybackTimeSeconds;
        double duration = recording is null ? document.DurationSeconds : recordingElapsedSeconds;
        if (!double.IsFinite(time) || time < 0d || time > duration + TimeEpsilon)
        {
            error = "replay-speed-key-time";
            return false;
        }
        if (!double.IsFinite(speed)
            || speed < DirectorReplayTimeline.MinSpeed
            || speed > DirectorReplayTimeline.MaxSpeed
            || !Enum.IsDefined(interpolation))
        {
            error = "replay-invalid-speed";
            return false;
        }

        time = Math.Clamp(time, 0d, duration);
        DirectorReplaySpeedKeyframe? existing = document.SpeedKeyframes
            .FirstOrDefault(item => Math.Abs(item.TimeSeconds - time) <= TimeEpsilon);
        DirectorReplaySpeedKeyframe? previous = existing?.DeepClone();
        if (existing is null)
        {
            existing = new DirectorReplaySpeedKeyframe { Id = Guid.NewGuid(), TimeSeconds = time };
            document.SpeedKeyframes.Add(existing);
        }
        existing.Speed = speed;
        existing.InterpolationToNext = interpolation;
        document.SpeedKeyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        if (!PersistTimelineEdit(document, out error))
        {
            if (previous is null)
            {
                document.SpeedKeyframes.Remove(existing);
            }
            else
            {
                existing.Speed = previous.Speed;
                existing.InterpolationToNext = previous.InterpolationToNext;
            }
            keyframe = new DirectorReplaySpeedKeyframe();
            return false;
        }

        keyframe = existing.DeepClone();
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool DeleteSpeedKeyframe(
        string selector,
        out DirectorReplaySpeedKeyframe keyframe,
        out string error)
    {
        keyframe = new DirectorReplaySpeedKeyframe();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        DirectorReplaySpeedKeyframe? found = ResolveSpeedKeyframe(document.SpeedKeyframes, selector);
        if (found is null)
        {
            error = "replay-speed-key-not-found";
            return false;
        }

        int index = document.SpeedKeyframes.IndexOf(found);
        document.SpeedKeyframes.RemoveAt(index);
        if (!PersistTimelineEdit(document, out error))
        {
            document.SpeedKeyframes.Insert(index, found);
            return false;
        }

        keyframe = found.DeepClone();
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool UpdateSpeedKeyframe(
        string selector,
        double requestedTime,
        double speed,
        DirectorReplaySpeedInterpolation interpolation,
        out DirectorReplaySpeedKeyframe keyframe,
        out string error)
    {
        keyframe = new DirectorReplaySpeedKeyframe();
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        DirectorReplaySpeedKeyframe? found = ResolveSpeedKeyframe(document.SpeedKeyframes, selector);
        if (found is null)
        {
            error = "replay-speed-key-not-found";
            return false;
        }

        double duration = recording is null ? document.DurationSeconds : recordingElapsedSeconds;
        if (!double.IsFinite(requestedTime)
            || requestedTime < 0d
            || requestedTime > duration + TimeEpsilon)
        {
            error = "replay-speed-key-time";
            return false;
        }
        if (!double.IsFinite(speed)
            || speed < DirectorReplayTimeline.MinSpeed
            || speed > DirectorReplayTimeline.MaxSpeed
            || !Enum.IsDefined(interpolation))
        {
            error = "replay-invalid-speed";
            return false;
        }

        double time = Math.Clamp(requestedTime, 0d, duration);
        if (document.SpeedKeyframes.Any(item => item.Id != found.Id
            && Math.Abs(item.TimeSeconds - time) <= TimeEpsilon))
        {
            error = "replay-speed-key-conflict";
            return false;
        }

        DirectorReplaySpeedKeyframe previous = found.DeepClone();
        found.TimeSeconds = time;
        found.Speed = speed;
        found.InterpolationToNext = interpolation;
        document.SpeedKeyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        if (!PersistTimelineEdit(document, out error))
        {
            found.TimeSeconds = previous.TimeSeconds;
            found.Speed = previous.Speed;
            found.InterpolationToNext = previous.InterpolationToNext;
            document.SpeedKeyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
            return false;
        }

        keyframe = found.DeepClone();
        PlaybackStateChanged?.Invoke();
        return true;
    }

    private DirectorReplayDocument? EditableTimelineDocument()
        => recording ?? playback?.Document;

    public bool ReplaceViewportTimeline(
        DirectorReplayViewportTimeline timeline,
        out string error,
        bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        error = string.Empty;
        DirectorReplayDocument? document = EditableTimelineDocument();
        if (document is null)
        {
            error = "replay-not-active";
            return false;
        }

        DirectorReplayViewportTimeline previous = document.ViewportTimeline?.DeepClone()
            ?? new DirectorReplayViewportTimeline();
        List<DirectorReplaySpeedKeyframe> previousLegacySpeedKeyframes = document.SpeedKeyframes
            .Select(keyframe => keyframe.DeepClone())
            .ToList();
        DirectorReplayViewportTimeline normalized = DirectorReplayViewportTimeline.Normalize(
            timeline,
            recording is null ? document.DurationSeconds : recordingElapsedSeconds);
        bool removedLastSpeedTrack = previous.Tracks.Any(track =>
                track.TrackType == DirectorReplayElementTrackType.Speed)
            && !normalized.Tracks.Any(track =>
                track.TrackType == DirectorReplayElementTrackType.Speed);
        document.ViewportTimeline = normalized;
        if (removedLastSpeedTrack)
        {
            // Legacy speed keys are migration input only. Leaving them behind
            // would recreate a Speed row the next time the sidecar is loaded.
            document.SpeedKeyframes.Clear();
        }
        if (!PersistTimelineEdit(document, out error))
        {
            document.ViewportTimeline = previous;
            document.SpeedKeyframes = previousLegacySpeedKeyframes;
            return false;
        }

        if (ReferenceEquals(document, playback?.Document))
        {
            RefreshPlaybackForVisualChanges(playback!, previous.Visuals, normalized.Visuals);
        }

        if (notify)
        {
            PlaybackStateChanged?.Invoke();
        }
        return true;
    }

    private void RefreshPlaybackForVisualChanges(
        Playback active,
        DirectorReplayVisualSettings previous,
        DirectorReplayVisualSettings current)
    {
        if (RequiresWorldVisualRebuild(previous, current))
        {
            RebuildPlaybackWorld(active);
        }

        // Actor binding behavior is fixed; visual changes only affect rendering.
    }

    private static bool RequiresWorldVisualRebuild(
        DirectorReplayVisualSettings previous,
        DirectorReplayVisualSettings current)
    {
        return previous.World.ShowTerrain != current.World.ShowTerrain;
    }

    private void RebuildPlaybackWorld(Playback active)
    {
        active.WorldPlayback?.Dispose();
        active.WorldPlayback = null;
        if (!DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                active.Document,
                active.QuickMode,
                config.ReplayApplyWorldSnapshot)
            || active.Document.WorldSnapshot.Chunks.Count == 0)
        {
            return;
        }

        DirectorReplayWorldPlayback rebuilt = new(
            capi,
            game,
            active.Document,
            SetApplyingReplayState,
            () => applyingReplayState);
        try
        {
            rebuilt.Start();
            rebuilt.ApplyAt(active.TimeSeconds, synchronize: true);
            active.WorldPlayback = rebuilt;
        }
        catch (Exception exception)
        {
            rebuilt.Dispose();
            capi.Logger.Error(
                "VS Director: replay world could not be refreshed after a visual setting changed: {0}",
                exception);
        }
    }

    private void RebuildPlaybackActors(Playback active)
    {
        bool restartDedicatedRenderer = dedicatedReplayRenderer.IsActive;
        if (restartDedicatedRenderer)
        {
            dedicatedReplayRenderer.Stop();
        }

        RestorePendingBindings(active.Bindings);
        active.Bindings.Clear();
        active.BindingsByEntityId.Clear();
        HashSet<long> claimedEntityIds = new();
        bool preferLiveActors = !config.ReplayUseDedicatedRenderer
            && active.Document.Actors.Count > IndependentActorLimitForDenseReplay;
        foreach (DirectorReplayActorTrack actor in active.Document.Actors)
        {
            ActorBinding? binding = CreatePlaybackBinding(
                actor,
                preferLiveActors,
                claimedEntityIds,
                out _);
            if (binding is null)
            {
                continue;
            }

            active.Bindings.Add(binding);
            active.BindingsByEntityId[binding.Entity.EntityId] = binding;
            InitializePlaybackBinding(binding);
        }

        SuppressLoadedRealityEntities(active);
        if (restartDedicatedRenderer)
        {
            try
            {
                dedicatedReplayRenderer.Start(
                    active.Bindings.Select(binding => binding.Entity),
                    active.Document.ParticleEvents.Count);
                foreach (ActorBinding binding in active.Bindings)
                {
                    if (!ReferenceEquals(binding.Entity, game.EntityPlayer))
                    {
                        binding.DedicatedAnimationPoseReady = false;
                        ResetDedicatedReplayAnimationWarmup(binding);
                    }
                }
            }
            catch (Exception exception)
            {
                capi.Logger.Warning(
                    "VS Director: dedicated replay renderer could not be refreshed after actor settings changed: {0}",
                    exception.GetBaseException().Message);
                dedicatedReplayRenderer.Stop();
            }
        }

        active.TimedEventSeekPending = true;
        active.AppliedTimeSeconds = -1d;
        ApplyPlaybackFrame(active);
    }

    private bool PersistTimelineEdit(DirectorReplayDocument document, out string error)
    {
        error = string.Empty;
        if (ReferenceEquals(document, recording))
        {
            return true;
        }

        DirectorReplayStore? activeStore = store;
        if (!ReferenceEquals(document, playback?.Document) || activeStore is null)
        {
            error = "replay-not-active";
            return false;
        }

        try
        {
            activeStore.SaveTimelineEdits(playback!.SourcePath, document);
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("VS Director: replay timeline edit could not be saved: {0}", exception);
            error = exception.Message;
            return false;
        }
    }

    private static DirectorReplayMarker? ResolveMarker(
        IReadOnlyList<DirectorReplayMarker> markers,
        string selector)
    {
        string value = selector?.Trim() ?? string.Empty;
        if (int.TryParse(value, out int index) && index >= 1 && index <= markers.Count)
        {
            return markers[index - 1];
        }
        if (Guid.TryParse(value, out Guid id))
        {
            return markers.FirstOrDefault(item => item.Id == id);
        }
        return markers.FirstOrDefault(
            item => string.Equals(item.Label, value, StringComparison.OrdinalIgnoreCase));
    }

    private static DirectorReplaySpeedKeyframe? ResolveSpeedKeyframe(
        IReadOnlyList<DirectorReplaySpeedKeyframe> keyframes,
        string selector)
    {
        string value = selector?.Trim() ?? string.Empty;
        if (int.TryParse(value, out int index) && index >= 1 && index <= keyframes.Count)
        {
            return keyframes[index - 1];
        }
        return Guid.TryParse(value, out Guid id)
            ? keyframes.FirstOrDefault(item => item.Id == id)
            : null;
    }

    public Entity? ResolveReplayActor(string? playerUid, long entityId)
        => ResolveReplayActor(null, playerUid, entityId);

    public Entity? ResolveReplayActor(
        string? actorKey,
        string? playerUid,
        long entityId)
    {
        Playback? active = playback;
        if (active is null)
        {
            return null;
        }

        ActorBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(actorKey))
        {
            binding = active.Bindings.FirstOrDefault(candidate =>
                string.Equals(candidate.Track.ActorKey, actorKey, StringComparison.Ordinal));
        }
        if (binding is null && !string.IsNullOrWhiteSpace(playerUid))
        {
            binding = active.Bindings.FirstOrDefault(candidate =>
                string.Equals(candidate.Track.PlayerUid, playerUid, StringComparison.Ordinal));
        }
        if (binding is null && entityId != 0L)
        {
            binding = active.Bindings.FirstOrDefault(candidate =>
                candidate.Track.EntityId == entityId
                || candidate.Track.ReplayEntityId == entityId);
        }
        return binding?.Entity;
    }

    public bool TryResolveActor(
        string actorSelector,
        out DirectorReplayActorPathSource source,
        out Entity? entity)
    {
        source = default;
        entity = null;
        Playback? active = playback;
        DirectorReplayActorTrack? actor = active is null
            ? null
            : ResolveActorTrack(active.Document, actorSelector);
        if (actor is null || active is null)
        {
            return false;
        }

        ActorBinding? binding = active.Bindings.FirstOrDefault(
            candidate => string.Equals(candidate.Track.ActorKey, actor.ActorKey, StringComparison.Ordinal));
        if (binding is null)
        {
            return false;
        }

        source = new DirectorReplayActorPathSource(
            actor.ActorKey,
            ActorDisplayName(actor),
            actor.PlayerUid,
            actor.EntityId,
            actor.EntityCode,
            actor.Samples.Count,
            IsPlayerActor(actor));
        entity = binding.Entity;
        return true;
    }

    public bool TryBuildActorPerformance(
        string actorSelector,
        string requestedName,
        out DirectorActorPerformance performance,
        out DirectorReplayActorPathSource source,
        out string error)
    {
        performance = new DirectorActorPerformance();
        source = default;
        error = string.Empty;
        Playback? active = playback;
        if (active is null)
        {
            error = "replay-not-playing";
            return false;
        }

        DirectorReplayActorTrack? actor = ResolveActorTrack(active.Document, actorSelector);
        if (actor is null)
        {
            error = "actor-path-actor-not-found";
            return false;
        }

        source = new DirectorReplayActorPathSource(
            actor.ActorKey,
            ActorDisplayName(actor),
            actor.PlayerUid,
            actor.EntityId,
            actor.EntityCode,
            actor.Samples.Count,
            IsPlayerActor(actor));
        return DirectorActorPerformanceImporter.TryCreate(
            actor,
            requestedName,
            out performance,
            out error,
            active.Document.Id);
    }

    public bool TryBuildAction(
        string actorSelector,
        double startSeconds,
        double endSeconds,
        string requestedName,
        out DirectorActionAsset action,
        out DirectorReplayActorPathSource source,
        out string error)
    {
        action = new DirectorActionAsset();
        source = default;
        error = string.Empty;
        Playback? active = playback;
        if (active is null)
        {
            error = "replay-not-playing";
            return false;
        }
        DirectorReplayActorTrack? actor = ResolveActorTrack(active.Document, actorSelector);
        if (actor is null)
        {
            error = "actor-path-actor-not-found";
            return false;
        }
        source = new DirectorReplayActorPathSource(
            actor.ActorKey,
            ActorDisplayName(actor),
            actor.PlayerUid,
            actor.EntityId,
            actor.EntityCode,
            actor.Samples.Count,
            IsPlayerActor(actor));
        return DirectorActionImporter.TryCreateFromReplay(
            actor,
            startSeconds,
            endSeconds,
            requestedName,
            out action,
            out error,
            active.Document.Id);
    }

    public bool StartLiveActionRecording(
        string actorSelector,
        string requestedName,
        out DirectorReplayActorPathSource source,
        out string error)
    {
        source = default;
        error = string.Empty;
        if (liveActionCapture is not null)
        {
            error = "action-live-recording-active";
            return false;
        }
        Entity? entity = ResolveLiveActionEntity(actorSelector, out source);
        if (entity is null)
        {
            error = "actor-path-actor-not-found";
            return false;
        }
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? source.DisplayName + " Action"
            : requestedName.Trim();
        DirectorReplayActorTrack track = new()
        {
            ActorKey = source.ActorKey,
            EntityId = source.EntityId,
            PlayerUid = source.PlayerUid,
            EntityCode = source.EntityCode,
            DisplayName = source.DisplayName,
            ReplayEntityId = long.MinValue + 1L,
            SimulationRange = entity.SimulationRange,
            InitialState = TrySerializeEntity(
                entity,
                long.MinValue + 1L,
                useThirdPersonPlayerAnimations: true),
            Samples = new List<DirectorReplayActorSample> { CaptureSample(entity, 0d) }
        };
        liveActionCapture = new LiveActionCapture(name, source, entity, track);
        PlaybackStateChanged?.Invoke();
        return true;
    }

    public bool StopLiveActionRecording(
        out DirectorActionAsset action,
        out DirectorReplayActorPathSource source,
        out string error)
    {
        action = new DirectorActionAsset();
        source = default;
        error = string.Empty;
        LiveActionCapture? capture = liveActionCapture;
        if (capture is null)
        {
            error = "action-live-not-recording";
            return false;
        }
        liveActionCapture = null;
        source = capture.Source;
        double duration = Math.Max(capture.ElapsedSeconds, capture.Track.Samples.LastOrDefault()?.TimeSeconds ?? 0d);
        try
        {
            AddOrReplaceSample(capture.Track, CaptureSample(capture.Entity, duration));
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: final live action frame could not be sampled: {0}", exception.Message);
        }
        PlaybackStateChanged?.Invoke();
        if (duration <= TimeEpsilon || capture.Track.Samples.Count < 2)
        {
            error = "action-live-too-short";
            return false;
        }
        return DirectorActionImporter.TryCreateFromReplay(
            capture.Track,
            0d,
            duration,
            capture.Name,
            out action,
            out error);
    }

    public bool CancelLiveActionRecording()
    {
        if (liveActionCapture is null)
        {
            return false;
        }
        liveActionCapture = null;
        PlaybackStateChanged?.Invoke();
        return true;
    }

    private Entity? ResolveLiveActionEntity(
        string? actorSelector,
        out DirectorReplayActorPathSource source)
    {
        source = default;
        string selector = actorSelector?.Trim() ?? string.Empty;
        if (playback is { } active && selector.Length > 0)
        {
            DirectorReplayActorTrack? replayTrack = ResolveActorTrack(active.Document, selector);
            ActorBinding? binding = replayTrack is null
                ? null
                : active.Bindings.FirstOrDefault(candidate => string.Equals(
                    candidate.Track.ActorKey,
                    replayTrack.ActorKey,
                    StringComparison.Ordinal));
            if (binding is not null)
            {
                source = LiveActionSource(binding.Entity, replayTrack);
                return binding.Entity;
            }
        }

        Entity[] entities = capi.World.LoadedEntities.Values.Where(IsCaptureEntityEligible).ToArray();
        Entity? entity = selector.Length == 0
            ? capi.World.Player?.Entity
            : entities.FirstOrDefault(candidate => LiveActionSelectorMatches(candidate, selector));
        if (entity is null)
        {
            return null;
        }
        source = LiveActionSource(entity);
        return entity;
    }

    private static bool LiveActionSelectorMatches(Entity entity, string selector)
    {
        DirectorReplayActorPathSource source = LiveActionSource(entity);
        return string.Equals(source.ActorKey, selector, StringComparison.Ordinal)
            || string.Equals(source.PlayerUid, selector, StringComparison.Ordinal)
            || string.Equals(source.DisplayName, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.EntityCode, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.EntityId.ToString(CultureInfo.InvariantCulture), selector, StringComparison.Ordinal);
    }

    private static DirectorReplayActorPathSource LiveActionSource(
        Entity entity,
        DirectorReplayActorTrack? replayTrack = null)
    {
        string playerUid = replayTrack?.PlayerUid
            ?? (entity as EntityPlayer)?.PlayerUID
            ?? string.Empty;
        string actorKey = replayTrack?.ActorKey
            ?? (playerUid.Length > 0 ? "player:" + playerUid : "entity:" + entity.EntityId);
        string entityCode = replayTrack?.EntityCode ?? entity.Code?.ToShortString() ?? string.Empty;
        string displayName = replayTrack is null
            ? entity is EntityPlayer player
                ? player.Player?.PlayerName ?? player.PlayerUID ?? actorKey
                : entity.Code?.ToShortString() ?? actorKey
            : ActorDisplayName(replayTrack);
        return new DirectorReplayActorPathSource(
            actorKey,
            displayName,
            playerUid,
            replayTrack?.EntityId ?? entity.EntityId,
            entityCode,
            replayTrack?.Samples.Count ?? 0,
            entity is EntityPlayer || replayTrack is not null && IsPlayerActor(replayTrack));
    }

    private static DirectorReplayActorTrack? ResolveActorTrack(
        DirectorReplayDocument document,
        string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        string trimmed = selector.Trim();
        DirectorReplayActorTrack[] actors = document.Actors
            .Where(actor => actor.Samples.Count > 0)
            .OrderByDescending(IsPlayerActor)
            .ThenBy(ActorDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(actor => actor.ActorKey, StringComparer.Ordinal)
            .ToArray();
        if (int.TryParse(trimmed, out int index) && index >= 1 && index <= actors.Length)
        {
            return actors[index - 1];
        }

        return actors.FirstOrDefault(actor => string.Equals(
                actor.ActorKey,
                trimmed,
                StringComparison.Ordinal))
            ?? actors.FirstOrDefault(actor => !string.IsNullOrWhiteSpace(actor.PlayerUid)
                && string.Equals(actor.PlayerUid, trimmed, StringComparison.Ordinal))
            ?? actors.FirstOrDefault(actor => actor.EntityId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) == trimmed)
            ?? actors.FirstOrDefault(actor => string.Equals(
                actor.DisplayName,
                trimmed,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlayerActor(DirectorReplayActorTrack actor)
        => !string.IsNullOrWhiteSpace(actor.PlayerUid)
            || actor.ActorKey.StartsWith("player:", StringComparison.OrdinalIgnoreCase)
            || actor.EntityCode.Contains("player", StringComparison.OrdinalIgnoreCase);

    private static string ActorDisplayName(DirectorReplayActorTrack actor)
    {
        if (!string.IsNullOrWhiteSpace(actor.DisplayName))
        {
            return actor.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(actor.PlayerUid))
        {
            return actor.PlayerUid.Trim();
        }

        if (!string.IsNullOrWhiteSpace(actor.EntityCode))
        {
            return actor.EntityCode.Trim();
        }

        return actor.ActorKey;
    }

    public bool StartExport(
        IReadOnlyList<CameraPath> paths,
        double startSeconds,
        double endSeconds,
        double timeScale,
        out string error)
    {
        DirectorRecordingTrack[] tracks = paths.Select(path => new DirectorRecordingTrack
        {
            Id = path.Id,
            Name = path.Name,
            SourceType = DirectorRecordingSourceType.Path,
            SourceId = path.Id,
            Enabled = true
        }).ToArray();
        return StartTrackExport(tracks, startSeconds, endSeconds, timeScale, out error);
    }

    public bool StartTrackExport(
        IReadOnlyList<DirectorRecordingTrack> tracks,
        double startSeconds,
        double endSeconds,
        double timeScale,
        out string error)
        => StartTrackExport(
            tracks,
            startSeconds,
            endSeconds,
            timeScale,
            new DirectorReplayExportOptions
            {
                FramesPerSecond = config.RecordingFrameRate,
                Width = game.Width,
                Height = game.Height,
                Encoder = config.OffscreenEncoder,
                VideoQuality = config.FfmpegVideoCrf,
                VideoBitrateKbps = config.OffscreenVideoBitrateKbps,
                IncludeAudio = true,
                AudioBitrateKbps = config.FfmpegAudioBitrateKbps,
                SoftwarePreset = config.FfmpegVideoPreset,
                OutputDirectory = config.RecordingOutputDirectory,
                FfmpegPath = config.FfmpegPath,
                SettleFrames = config.ReplayExportWarmupFrames
            },
            out error);

    public bool StartTrackExport(
        IReadOnlyList<DirectorRecordingTrack> tracks,
        double startSeconds,
        double endSeconds,
        double timeScale,
        DirectorReplayExportOptions options,
        out string error)
    {
        Playback? active = playback;
        if (active is null || IsLoading)
        {
            error = "replay-not-playing";
            return false;
        }

        if (endSeconds > active.Document.DurationSeconds + TimeEpsilon)
        {
            error = "replay-export-range";
            return false;
        }

        if (IsExporting)
        {
            error = "replay-export-active";
            return false;
        }

        ClearIndependentVoiceExportStatus();

        if (options.Backend == DirectorReplayExportBackend.IndependentAudio)
        {
            return StartIndependentVoiceExport(active, startSeconds, endSeconds, out error);
        }

        if (options.Backend == DirectorReplayExportBackend.Obs)
        {
            return StartObsExport(
                active,
                tracks.FirstOrDefault()?.Name ?? active.Document.Name,
                startSeconds,
                endSeconds,
                timeScale,
                options,
                out error);
        }

        active.Paused = true;
        return exporter.Start(
            active.Document.Name,
            tracks,
            active.Document.ViewportTimeline,
            startSeconds,
            endSeconds,
            timeScale,
            options,
            active.Document,
            capi.World.Player?.PlayerUID ?? string.Empty,
            out error);
    }

    public bool StopExport()
    {
        if (StopIndependentVoiceExport())
        {
            return true;
        }
        if (obsExport is { IsActive: true } obs)
        {
            BeginObsExportStop(obs, completed: false);
            return true;
        }

        bool active = exporter.IsExporting;
        exporter.Stop();
        return active;
    }

    private bool StartIndependentVoiceExport(
        Playback active,
        double startSeconds,
        double endSeconds,
        out string error)
    {
        error = string.Empty;
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || startSeconds < 0d
            || endSeconds <= startSeconds
            || endSeconds > active.Document.DurationSeconds + TimeEpsilon)
        {
            error = "replay-export-range";
            return false;
        }

        // Recorded lists are immutable during playback. Snapshot only the
        // editable timeline; never expand actors or world payloads for audio.
        DirectorReplayDocument document = DirectorReplayAudioExporter.CreateVoiceSource(active.Document);
        if (document.VoiceFrames.Count == 0)
        {
            error = "replay-voice-export-empty";
            return false;
        }

        string sourceDirectory = Path.GetDirectoryName(active.SourcePath) ?? string.Empty;
        if (sourceDirectory.Length == 0)
        {
            error = "replay-voice-export-path";
            return false;
        }
        string replayFileName = Path.GetFileNameWithoutExtension(
            Path.GetFileNameWithoutExtension(active.SourcePath));
        string folderName = DirectorRecordingFiles.SanitizeFilePart(replayFileName, "Replay")
            + "-voice-" + DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss-fff");
        string outputDirectory = Path.Combine(sourceDirectory, folderName);
        CancellationTokenSource cancellation = new();
        lock (independentVoiceExportGate)
        {
            if (independentVoiceExportActive)
            {
                cancellation.Dispose();
                error = "replay-export-active";
                return false;
            }
            independentVoiceExportCancellation?.Dispose();
            independentVoiceExportCancellation = cancellation;
            independentVoiceExportActive = true;
            independentVoiceExportStatus = "exporting-audio";
            independentVoiceExportError = string.Empty;
            independentVoiceExportDirectory = outputDirectory;
            independentVoiceExportOutputFile = string.Empty;
            independentVoiceExportTrackCount = 0;
        }

        _ = Task.Run(() => RunIndependentVoiceExport(
            document,
            startSeconds,
            endSeconds,
            outputDirectory,
            cancellation));
        return true;
    }

    private void RunIndependentVoiceExport(
        DirectorReplayDocument document,
        double startSeconds,
        double endSeconds,
        string outputDirectory,
        CancellationTokenSource cancellation)
    {
        try
        {
            IReadOnlyList<string> paths = DirectorReplayAudioExporter.ExportIndependentVoiceTracks(
                document,
                startSeconds,
                endSeconds,
                outputDirectory,
                cancellation.Token);
            if (paths.Count == 0) throw new InvalidOperationException("replay-voice-export-empty");
            lock (independentVoiceExportGate)
            {
                if (!ReferenceEquals(independentVoiceExportCancellation, cancellation))
                {
                    return;
                }
                independentVoiceExportActive = false;
                independentVoiceExportStatus = "complete";
                independentVoiceExportTrackCount = paths.Count;
                independentVoiceExportOutputFile = paths.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteIndependentVoiceExport(outputDirectory);
            lock (independentVoiceExportGate)
            {
                if (ReferenceEquals(independentVoiceExportCancellation, cancellation))
                {
                    independentVoiceExportActive = false;
                    independentVoiceExportStatus = "stopped";
                }
            }
        }
        catch (Exception exception)
        {
            lock (independentVoiceExportGate)
            {
                if (ReferenceEquals(independentVoiceExportCancellation, cancellation))
                {
                    independentVoiceExportActive = false;
                    independentVoiceExportStatus = "error";
                    independentVoiceExportError = exception.GetBaseException().Message;
                }
            }
            capi.Logger.Error("VS Director: independent voice export failed: {0}", exception);
        }
    }

    private static void TryDeleteIndependentVoiceExport(string outputDirectory)
    {
        try
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private bool StopIndependentVoiceExport()
    {
        lock (independentVoiceExportGate)
        {
            if (!independentVoiceExportActive || independentVoiceExportCancellation is null)
            {
                return false;
            }
            independentVoiceExportStatus = "stopping";
            independentVoiceExportCancellation.Cancel();
            return true;
        }
    }

    private void ClearIndependentVoiceExportStatus()
    {
        lock (independentVoiceExportGate)
        {
            if (independentVoiceExportActive)
            {
                return;
            }
            independentVoiceExportCancellation?.Dispose();
            independentVoiceExportCancellation = null;
            independentVoiceExportStatus = "idle";
            independentVoiceExportError = string.Empty;
            independentVoiceExportDirectory = string.Empty;
            independentVoiceExportOutputFile = string.Empty;
            independentVoiceExportTrackCount = 0;
        }
    }

    private bool IsIndependentVoiceExportActive
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportActive;
            }
        }
    }

    private bool HasIndependentVoiceExportStatus
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportActive || independentVoiceExportStatus != "idle";
            }
        }
    }

    private string IndependentVoiceExportStatus
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportStatus;
            }
        }
    }

    private string IndependentVoiceExportError
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportError;
            }
        }
    }

    private string IndependentVoiceExportDirectory
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportDirectory;
            }
        }
    }

    private string IndependentVoiceExportOutputFile
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportOutputFile;
            }
        }
    }

    private int IndependentVoiceExportTrackCount
    {
        get
        {
            lock (independentVoiceExportGate)
            {
                return independentVoiceExportTrackCount;
            }
        }
    }

    private bool StartObsExport(
        Playback active,
        string sceneName,
        double startSeconds,
        double endSeconds,
        double timeScale,
        DirectorReplayExportOptions options,
        out string error)
    {
        error = string.Empty;
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || endSeconds <= startSeconds
            || !double.IsFinite(timeScale)
            || timeScale < DirectorReplayTimeline.MinSpeed
            || timeScale > DirectorReplayTimeline.MaxSpeed
            || options.FramesPerSecond is < 1 or > 240)
        {
            error = "replay-export-range";
            return false;
        }

        ObsReplayExportSession session = new(
            sceneName,
            startSeconds,
            endSeconds,
            active.TimeSeconds,
            active.Speed,
            active.Paused,
            timeScale,
            options.FramesPerSecond,
            Math.Max(
                options.SettleFrames,
                DefaultExportWarmupFrames),
            active.Document.ViewportTimeline);
        active.Paused = true;
        active.TimeSeconds = startSeconds;
        active.TimedEventSeekPending = true;
        ApplyPlaybackFrame(active);
        obsExport?.Cancellation.Dispose();
        obsExport = session;
        _ = Task.Run(() => PrepareObsExportAsync(session, options.DeepClone()));
        return true;
    }

    private async Task PrepareObsExportAsync(
        ObsReplayExportSession session,
        DirectorReplayExportOptions options)
    {
        DirectorObsWebSocketClient? client = null;
        try
        {
            client = new DirectorObsWebSocketClient(
                options.ObsWebSocketUrl,
                options.ObsWebSocketPassword);
            await client.PrepareAsync(session.Cancellation.Token).ConfigureAwait(false);
            DirectorObsWebSocketClient connected = client;
            client = null;
            capi.Event.EnqueueMainThreadTask(
                () => CompleteObsExportConnection(session, connected),
                "vsdirector-replay-obs-connected");
        }
        catch (Exception exception)
        {
            client?.Dispose();
            string failure = exception.GetBaseException().Message;
            capi.Event.EnqueueMainThreadTask(
                () => FailObsExport(session, failure),
                "vsdirector-replay-obs-connect-failed");
        }
    }

    private void CompleteObsExportConnection(
        ObsReplayExportSession session,
        DirectorObsWebSocketClient client)
    {
        if (!ReferenceEquals(obsExport, session) || session.Cancellation.IsCancellationRequested)
        {
            _ = DisconnectStaleObsClientAsync(client);
            return;
        }
        if (playback is not Playback active)
        {
            _ = DisconnectStaleObsClientAsync(client);
            FailObsExport(session, "replay-not-playing");
            return;
        }

        session.Client = client;
        session.StartGate.MarkConnected();
        active.Paused = true;
        UpdateReplaySoundPauseState(active);
        PlaybackStateChanged?.Invoke();
    }

    private void UpdateObsExportStart(Playback active, bool replayReady)
    {
        ObsReplayExportSession? session = obsExport;
        if (session is not { IsActive: true, Client: not null }
            || !session.StartGate.TryBeginRecording(replayReady))
        {
            return;
        }

        DirectorObsWebSocketClient client = session.Client;
        _ = Task.Run(() => StartObsRecordingAsync(session, client));
    }

    private async Task StartObsRecordingAsync(
        ObsReplayExportSession session,
        DirectorObsWebSocketClient client)
    {
        try
        {
            await client.StartRecordingAsync(session.Cancellation.Token).ConfigureAwait(false);
            capi.Event.EnqueueMainThreadTask(
                () => CompleteObsRecordingStart(session, client),
                "vsdirector-replay-obs-recording-started");
        }
        catch (Exception exception)
        {
            string failure = exception.GetBaseException().Message;
            capi.Event.EnqueueMainThreadTask(
                () => FailObsRecordingStart(session, client, failure),
                "vsdirector-replay-obs-recording-start-failed");
        }
    }

    private void CompleteObsRecordingStart(
        ObsReplayExportSession session,
        DirectorObsWebSocketClient client)
    {
        if (!ReferenceEquals(obsExport, session)
            || session.Status == "stopping"
            || !ReferenceEquals(session.Client, client))
        {
            if (session.Status != "stopping")
            {
                _ = StopStaleObsExportAsync(client);
            }
            return;
        }
        if (playback is not Playback active)
        {
            session.Client = null;
            _ = StopStaleObsExportAsync(client);
            FailObsExport(session, "replay-not-playing");
            return;
        }

        session.StartGate.CompleteRecordingStart();
        session.Status = "rendering";
        active.Paused = true;
        active.TimeSeconds = session.StartSeconds;
        UpdateReplaySoundPauseState(active);
        PlaybackStateChanged?.Invoke();
    }

    private void FailObsRecordingStart(
        ObsReplayExportSession session,
        DirectorObsWebSocketClient client,
        string failure)
    {
        session.StartGate.FailRecordingStart();
        if (!ReferenceEquals(obsExport, session)
            || session.Status == "stopping"
            || !ReferenceEquals(session.Client, client))
        {
            return;
        }

        session.Client = null;
        _ = DisconnectStaleObsClientAsync(client);
        FailObsExport(session, failure);
    }

    private void UpdateObsExport(Playback active)
    {
        ObsReplayExportSession? session = obsExport;
        if (session is not { IsActive: true, Client: not null }
            || !session.StartGate.RecordingStarted
            || active.TimeSeconds < session.EndSeconds - TimeEpsilon)
        {
            return;
        }
        active.TimeSeconds = session.EndSeconds;
        ApplyPlaybackFrame(active);
        BeginObsExportStop(session, completed: true);
    }

    private void BeginObsExportStop(ObsReplayExportSession session, bool completed)
    {
        if (!ReferenceEquals(obsExport, session) || session.Status == "stopping")
        {
            return;
        }
        session.Status = "stopping";
        bool recordingMayHaveStarted = session.StartGate.RecordingStarted
            || session.StartGate.RecordingStartPending;
        session.Cancellation.Cancel();
        if (playback is Playback active)
        {
            active.Paused = true;
        }
        if (session.Client is not DirectorObsWebSocketClient client)
        {
            session.Status = completed ? "complete" : "stopped";
            RestoreAfterObsExport(session);
            return;
        }
        session.Client = null;
        _ = Task.Run(async () =>
        {
            string outputPath = string.Empty;
            string failure = string.Empty;
            try
            {
                if (recordingMayHaveStarted)
                {
                    try
                    {
                        outputPath = await client.StopRecordingAsync().ConfigureAwait(false);
                    }
                    catch when (!session.StartGate.RecordingStarted)
                    {
                        // A canceled StartRecord request may not have reached OBS.
                    }
                }
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception.GetBaseException().Message;
            }
            finally
            {
                client.Dispose();
            }
            capi.Event.EnqueueMainThreadTask(
                () => CompleteObsExportStop(session, completed, outputPath, failure),
                "vsdirector-replay-obs-stop");
        });
    }

    private void CompleteObsExportStop(
        ObsReplayExportSession session,
        bool completed,
        string outputPath,
        string failure)
    {
        if (!ReferenceEquals(obsExport, session))
        {
            return;
        }
        session.OutputPath = outputPath;
        session.OutputDirectory = string.IsNullOrWhiteSpace(outputPath)
            ? string.Empty
            : Path.GetDirectoryName(outputPath) ?? string.Empty;
        session.Error = failure;
        session.Status = failure.Length > 0 ? "error" : completed ? "complete" : "stopped";
        RestoreAfterObsExport(session);
    }

    private void FailObsExport(ObsReplayExportSession session, string failure)
    {
        if (!ReferenceEquals(obsExport, session))
        {
            return;
        }
        session.Error = failure;
        session.Status = session.Cancellation.IsCancellationRequested ? "stopped" : "error";
        RestoreAfterObsExport(session);
    }

    private void RestoreAfterObsExport(ObsReplayExportSession session)
    {
        if (session.Restored || playback is not Playback active)
        {
            return;
        }
        session.Restored = true;
        active.Speed = session.RestoreSpeed;
        active.TimeSeconds = Math.Clamp(
            session.RestoreTimeSeconds,
            0d,
            active.Document.DurationSeconds);
        active.Paused = session.RestorePaused;
        active.TimedEventSeekPending = true;
        ApplyPlaybackFrame(active);
        UpdateReplaySoundPauseState(active);
        PlaybackStateChanged?.Invoke();
    }

    private static async Task StopStaleObsExportAsync(DirectorObsWebSocketClient client)
    {
        try
        {
            _ = await client.StopRecordingAsync().ConfigureAwait(false);
            await client.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task DisconnectStaleObsClientAsync(DirectorObsWebSocketClient client)
    {
        try
        {
            await client.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    public void StopPlayback()
    {
        replayLoadOverlayVisible = false;
        StopIndependentVoiceExport();
        if (obsExport is { IsActive: true } obs)
        {
            BeginObsExportStop(obs, completed: false);
        }
        exporter.Stop();
        PendingReplayLoad? loading = pendingLoad;
        pendingLoad = null;
        if (loading is not null)
        {
            loading.Cancellation.Cancel();
            loading.Cancellation.Dispose();
        }

        PendingPlaybackStart? starting = pendingPlaybackStart;
        pendingPlaybackStart = null;
        if (starting is not null)
        {
            RestorePendingBindings(starting.Bindings);
        }

        Playback? active = playback;
        bool dedicatedWasActive = dedicatedReplayRenderer.IsActive;
        if (ReferenceEquals(dedicatedAnimationWarmupOwner, this))
        {
            dedicatedAnimationWarmupEntity = null;
            dedicatedAnimationWarmupOwner = null;
        }
        try
        {
            // Restore detached entity renderers before RemoveGhost.  The
            // engine's despawn path owns disposal once the registry is back.
            dedicatedReplayRenderer.Stop();
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: dedicated replay renderer cleanup failed: {0}",
                exception.GetBaseException().Message);
        }
        playback = null;
        visualEnvironment.Restore();
        ResetReplayParticleFrameState();
        SetPlaybackChunkBuffering(false);
        try
        {
            if (active is not null)
            {
                ResetReplaySounds(active, restoreAtCurrentTime: false);
                SetApplyingReplayState(true);
                foreach (ActorBinding binding in active.Bindings)
                {
                    try
                    {
                        if (binding.IsGhost)
                        {
                            RemoveGhost(binding.Entity);
                            continue;
                        }

                        replayThirdPersonIds.Remove(binding.Entity.EntityId);

                        if (binding.SavedFullState.Length > 0)
                        {
                            ApplyEntityState(binding.Entity, binding.SavedFullState);
                        }

                        ApplySample(binding.Entity, binding.SavedState, binding, forceAnimations: true);
                        if (binding.SavedItemSpawnedMilliseconds is long spawned
                            && binding.Entity is EntityItem item)
                        {
                            item.itemSpawnedMilliseconds = spawned;
                        }
                        if (binding.Entity is EntityPlayer restoredPlayer)
                        {
                            if (binding.SavedTpUseFpAnimations is bool tpUseFp)
                            {
                                restoredPlayer.TpAnimManager.UseFpAnmations = tpUseFp;
                            }
                            if (binding.SavedSelfUseFpAnimations is bool selfUseFp)
                            {
                                restoredPlayer.SelfFpAnimManager.UseFpAnmations = selfUseFp;
                            }
                            restoredPlayer.MarkShapeModified();
                        }
                        binding.Entity.IsRendered = binding.SavedRendered;
                        binding.Entity.IsShadowRendered = binding.SavedShadowRendered;
                    }
                    catch (Exception exception)
                    {
                        capi.Logger.Warning(
                            "VS Director: replay actor '{0}' could not be restored cleanly: {1}",
                            binding.Track.DisplayName,
                            exception.Message);
                    }
                }

                active.WorldPlayback?.Dispose();
                DisposeReplayWorldOwnedSounds(active);
                RestoreSuppressedLiveSpatialSounds(active);
                game.timelapse = active.SavedTimelapseStep;
                game.timelapsedCurrent = active.SavedTimelapseCurrent;
                game.timelapseEnd = active.SavedTimelapseEnd;
                capi.World.Calendar.Timelapse = active.SavedCalendarTimelapse;
                capi.World.Calendar.CalendarSpeedMul = active.SavedCalendarSpeedMultiplier;
                capi.World.Calendar.SeasonOverride = active.SavedSeasonOverride;
                RefreshVisualCalendar();
                if (!dedicatedWasActive)
                {
                    ClearReplayParticles();
                }
            }
        }
        finally
        {
            RestoreSuppressedLiveEntities();
            replayThirdPersonIds.Clear();
            SetApplyingReplayState(false);
            singleplayerWorldSuspension.Release();
            inputLockHandler(false);
        }
    }

    private void RestorePendingBindings(IEnumerable<ActorBinding> bindings)
    {
        bool wasApplyingReplayState = applyingReplayState;
        SetApplyingReplayState(true);
        try
        {
            foreach (ActorBinding binding in bindings)
            {
                try
                {
                    if (binding.IsGhost)
                    {
                        RemoveGhost(binding.Entity);
                        continue;
                    }

                    replayThirdPersonIds.Remove(binding.Entity.EntityId);
                    if (binding.SavedFullState.Length > 0)
                    {
                        ApplyEntityState(binding.Entity, binding.SavedFullState);
                    }
                    ApplySample(binding.Entity, binding.SavedState, binding, forceAnimations: true);
                    if (binding.SavedItemSpawnedMilliseconds is long spawned
                        && binding.Entity is EntityItem item)
                    {
                        item.itemSpawnedMilliseconds = spawned;
                    }
                    if (binding.Entity is EntityPlayer restoredPlayer)
                    {
                        if (binding.SavedTpUseFpAnimations is bool tpUseFp)
                        {
                            restoredPlayer.TpAnimManager.UseFpAnmations = tpUseFp;
                        }
                        if (binding.SavedSelfUseFpAnimations is bool selfUseFp)
                        {
                            restoredPlayer.SelfFpAnimManager.UseFpAnmations = selfUseFp;
                        }
                        restoredPlayer.MarkShapeModified();
                    }
                    binding.Entity.IsRendered = binding.SavedRendered;
                    binding.Entity.IsShadowRendered = binding.SavedShadowRendered;
                }
                catch (Exception exception)
                {
                    capi.Logger.Debug(
                        "VS Director: partially initialized replay actor could not be restored: {0}",
                        exception.Message);
                }
            }
        }
        finally
        {
            SetApplyingReplayState(wasApplyingReplayState);
        }

        RestoreSuppressedLiveEntities();
        replayThirdPersonIds.Clear();
    }

    public void OnGameTick(float deltaTime)
    {
        PollPendingSave();
        PollPendingPreloadEditorLoad();
        UpdateLiveActionCapture(deltaTime);
        DirectorReplayDocument? active = recording;
        if (active is null || recordingPaused)
        {
            return;
        }

        ProcessPausedBlockResync(PendingBlockChangesPerTick);
        FlushPendingBlockChanges(PendingBlockChangesPerTick);
        ProcessEntityStateCaptures(EntityStateCapturesPerTick, recordingElapsedSeconds);
        ProcessBlockEntityCapture(BlockEntityChunksPerTick);
        if (game.IsPaused)
        {
            return;
        }

        double dt = float.IsFinite(deltaTime) && deltaTime > 0f ? Math.Min(deltaTime, 1f) : 0d;
        recordingElapsedSeconds += dt;
        recordingAccumulator += dt;
        entityStateAccumulator += dt;

        double sampleInterval = 1d / active.SampleRate;
        if (recordingAccumulator + TimeEpsilon >= sampleInterval)
        {
            recordingAccumulator %= sampleInterval;
            CaptureFrame(recordingElapsedSeconds);
        }

        double stateInterval = 1d / config.ReplayEntityStateRate;
        if (entityStateAccumulator + TimeEpsilon >= stateInterval)
        {
            entityStateAccumulator %= stateInterval;
            CaptureClock(recordingElapsedSeconds);
            QueueEntityStateCapture();
            QueueBlockEntityCapture();
        }
    }

    private void UpdateLiveActionCapture(float deltaTime)
    {
        LiveActionCapture? capture = liveActionCapture;
        if (capture is null || game.IsPaused)
        {
            return;
        }
        double dt = float.IsFinite(deltaTime) && deltaTime > 0f ? Math.Min(deltaTime, 1f) : 0d;
        capture.ElapsedSeconds += dt;
        capture.Accumulator += dt;
        double interval = 1d / DirectorReplayMath.NormalizeSampleRate(config.ReplaySampleRate);
        int maximumCatchUpFrames = 8;
        while (capture.Accumulator + TimeEpsilon >= interval && maximumCatchUpFrames-- > 0)
        {
            capture.Accumulator -= interval;
            double sampleTime = Math.Max(0d, capture.ElapsedSeconds - capture.Accumulator);
            try
            {
                AddOrReplaceSample(capture.Track, CaptureSample(capture.Entity, sampleTime));
            }
            catch (Exception exception)
            {
                capi.Logger.Debug("VS Director: live action frame could not be sampled: {0}", exception.Message);
                liveActionCapture = null;
                PlaybackStateChanged?.Invoke();
                return;
            }
        }
        if (maximumCatchUpFrames < 0)
        {
            capture.Accumulator %= interval;
        }
    }

    public void PrepareFrame(float deltaTime)
    {
        PollPendingSave();
        PollPendingPreloadEditorLoad();
        PollPendingLoad();
        ProcessPendingPlaybackStart();
        UpdateActionPreviewActors(deltaTime);
        replayAllowsOffscreenActorRendering = false;
        Playback? active = playback;
        if (active is null)
        {
            return;
        }

        if (!singleplayerWorldSuspension.Maintain())
        {
            capi.Logger.Warning(
                "VS Director: replay playback stopped because singleplayer suspension is no longer eligible.");
            StopPlayback();
            return;
        }

        if (active.WorldPlayback?.IsSynchronizing == true)
        {
            UpdateObsExportStart(active, replayReady: false);
            ApplyPlaybackFrame(active);
            UpdateReplaySoundPauseState(active);
            return;
        }

        if (!IsPlaybackObservationAreaReady(active.Document, active.TimeSeconds))
        {
            SetPlaybackChunkBuffering(true);
            UpdateObsExportStart(active, replayReady: false);
            ApplyPlaybackFrame(active);
            UpdateReplaySoundPauseState(active);
            return;
        }
        SetPlaybackChunkBuffering(false);

        UpdateObsExportStart(active, IsObsExportStartReady(active));
        if (obsExport is { IsActive: true, Client: not null } obs
            && obs.StartGate.CanAdvanceReplayClock)
        {
            double outputDelta = DirectorReplayPlaybackPolicy.ClampPlaybackDelta(
                deltaTime,
                MaxPlaybackFrameDeltaSeconds);
            active.TimeSeconds = obs.Clock.AdvanceByOutputSeconds(outputDelta);
        }
        bool reachedEnd = false;
        DirectorReplayViewportTimeline elementTimeline = active.Document.ViewportTimeline;
        if (!active.Paused && !game.IsPaused)
        {
            double dt = DirectorReplayPlaybackPolicy.ClampPlaybackDelta(
                deltaTime,
                MaxPlaybackFrameDeltaSeconds);
            double legacySpeed = DirectorReplayTimeline.SampleSpeed(
                active.Document.SpeedKeyframes,
                active.TimeSeconds,
                active.Speed);
            double sampledSpeed = DirectorReplayElementTimeline.SampleTimingRate(
                elementTimeline,
                active.TimeSeconds,
                legacySpeed);
            active.TimeSeconds += dt * sampledSpeed;
            if (active.TimeSeconds >= active.Document.DurationSeconds)
            {
                active.TimeSeconds = active.Document.DurationSeconds;
                active.Paused = true;
                reachedEnd = true;
            }
        }
        ApplyPlaybackFrame(active);
        UpdateReplaySoundPauseState(active);
        UpdateObsExport(active);
        if (reachedEnd)
        {
            PlaybackStateChanged?.Invoke();
        }
    }

    private bool IsObsExportStartReady(Playback active)
        => ReferenceEquals(playback, active)
            && pendingLoad is null
            && pendingPlaybackStart is null
            && !IsSeekPending
            && active.WorldPlayback?.IsSynchronizing != true
            && IsPlaybackObservationAreaReady(active.Document, active.TimeSeconds)
            && IsActiveCameraAreaReady();

    public bool TryStartActionPreviewActor(
        DirectorActionAsset action,
        string actorSelector,
        out string actorKey,
        out string error,
        bool worldVisible = true,
        string entityCode = "",
        double previewDistance = 4d,
        string previewActorKey = "")
    {
        ArgumentNullException.ThrowIfNull(action);
        StopActionPreviewActor();
        actionPreviewDefaultDistance = double.IsFinite(previewDistance)
            ? Math.Clamp(previewDistance, 0.75d, 12d)
            : 4d;
        actionPreviewDistance = actionPreviewDefaultDistance;
        actionPreviewYawOffset = 0d;
        if (!TryCreateActionPreviewBinding(
                action,
                actorSelector,
                ActionPreviewGhostEntityId,
                entityCode,
                previewActorKey,
                out actorKey,
                out error,
                out ActorBinding? binding))
        {
            return false;
        }
        actionPreviewBinding = binding;
        actionPreviewWorldVisible = worldVisible;
        SetReplayEntityVisibility(binding.Entity, worldVisible);
        return true;
    }

    internal bool TryStartActionWorkflowPreviewActor(
        DirectorActionAsset action,
        string sourceActorSelector,
        string workflowActorKey,
        string entityCode,
        out Entity? ghost,
        out string error)
    {
        ghost = null;
        error = string.Empty;
        string actorKey = workflowActorKey?.Trim() ?? string.Empty;
        if (actorKey.Length == 0)
        {
            error = "action-preview-no-model-source";
            return false;
        }
        if (actionWorkflowPreviewBindings.Remove(actorKey, out ActorBinding? existing))
        {
            RemoveActionPreviewBinding(existing);
        }

        long ghostEntityId = ActionPreviewGhostEntityId - 1L - actionWorkflowPreviewBindings.Count;
        while (game.LoadedEntities.ContainsKey(ghostEntityId))
        {
            ghostEntityId--;
        }
        if (!TryCreateActionPreviewBinding(
                action,
                sourceActorSelector,
                ghostEntityId,
                entityCode,
                actorKey,
                out _,
                out error,
                out ActorBinding binding))
        {
            return false;
        }
        actionWorkflowPreviewBindings[actorKey] = binding;
        SetReplayEntityVisibility(binding.Entity, true);
        ghost = binding.Entity;
        return true;
    }

    public bool RotateActionPreview(double deltaYaw)
    {
        if (actionPreviewBinding is null || !double.IsFinite(deltaYaw))
        {
            return false;
        }
        actionPreviewYawOffset = NormalizeRadians(actionPreviewYawOffset + deltaYaw);
        RepositionActionPreview();
        return true;
    }

    public bool ScaleActionPreviewDistance(double scale)
    {
        if (actionPreviewBinding is null || !double.IsFinite(scale) || scale <= 0d)
        {
            return false;
        }
        actionPreviewDistance = Math.Clamp(actionPreviewDistance * scale, 0.75d, 12d);
        RepositionActionPreview();
        return true;
    }

    public bool ResetActionPreviewTransform()
    {
        if (actionPreviewBinding is null)
        {
            return false;
        }
        actionPreviewDistance = actionPreviewDefaultDistance;
        actionPreviewYawOffset = 0d;
        RepositionActionPreview();
        return true;
    }

    private void RepositionActionPreview()
    {
        if (actionPreviewBinding?.Track.Samples.FirstOrDefault() is { } sample)
        {
            PositionActionPreview(sample);
        }
    }

    private static double NormalizeRadians(double value)
    {
        double normalized = value % (Math.PI * 2d);
        return normalized > Math.PI
            ? normalized - Math.PI * 2d
            : normalized < -Math.PI
                ? normalized + Math.PI * 2d
                : normalized;
    }

    private bool TryCreateActionPreviewBinding(
        DirectorActionAsset action,
        string actorSelector,
        long ghostEntityId,
        string entityCodeOverride,
        string actorKeyOverride,
        out string actorKey,
        out string error,
        out ActorBinding binding)
    {
        actorKey = string.Empty;
        error = string.Empty;
        binding = null!;
        if (pendingLoad is not null || pendingPlaybackStart is not null)
        {
            error = "replay-playing";
            return false;
        }
        Entity? sourceEntity = ResolveLiveActionEntity(actorSelector, out _);
        string requestedEntityCode = entityCodeOverride?.Trim() ?? string.Empty;
        Entity? previewSourceEntity = sourceEntity;
        if (requestedEntityCode.Length > 0
            && !string.Equals(
                previewSourceEntity?.Code?.ToShortString(),
                requestedEntityCode,
                StringComparison.OrdinalIgnoreCase))
        {
            previewSourceEntity = capi.World.LoadedEntities.Values.FirstOrDefault(entity =>
                entity.EntityId >= 0L
                && string.Equals(
                    entity.Code?.ToShortString(),
                    requestedEntityCode,
                    StringComparison.OrdinalIgnoreCase));
        }
        string storedEntityCode = action.CompatibleEntityCodes
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? string.Empty;
        bool usePreviewModel = action.PreviewEntityState is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(action.PreviewEntityCode);
        bool useStoredModel = !usePreviewModel
            && action.InitialEntityState is { Length: > 0 }
            && storedEntityCode.Length > 0;
        string entityCode = requestedEntityCode.Length > 0
            ? requestedEntityCode
            : usePreviewModel
                ? action.PreviewEntityCode
                : useStoredModel
                    ? storedEntityCode
                    : sourceEntity?.Code?.ToShortString() ?? string.Empty;
        byte[] initialState = requestedEntityCode.Length > 0
            ? previewSourceEntity is not null
                ? TrySerializeEntity(
                    previewSourceEntity,
                    ghostEntityId,
                    useThirdPersonPlayerAnimations: true)
                : TryCreatePreviewEntityState(requestedEntityCode, ghostEntityId)
            : usePreviewModel
                ? CloneBytes(action.PreviewEntityState)
                : useStoredModel
                    ? CloneBytes(action.InitialEntityState)
                    : sourceEntity is null
                        ? Array.Empty<byte>()
                        : TrySerializeEntity(
                            sourceEntity,
                            ghostEntityId,
                            useThirdPersonPlayerAnimations: true);
        if (initialState.Length == 0 || entityCode.Length == 0)
        {
            error = "action-preview-no-model-source";
            return false;
        }

        actorKey = string.IsNullOrWhiteSpace(actorKeyOverride)
            ? "action-preview:" + action.Id.ToString("N") + ":"
                + ghostEntityId.ToString(CultureInfo.InvariantCulture)
            : actorKeyOverride.Trim();
        DirectorReplayActorTrack track = new()
        {
            ActorKey = actorKey,
            EntityId = ghostEntityId,
            PlayerUid = string.Empty,
            ForceGhost = true,
            EntityCode = entityCode,
            DisplayName = action.Name,
            ReplayEntityId = ghostEntityId,
            SimulationRange = previewSourceEntity?.SimulationRange ?? 9999,
            InitialState = initialState
        };
        Entity? ghost = TryCreateGhost(track, previewSourceEntity);
        if (ghost is null)
        {
            actorKey = string.Empty;
            error = "action-preview-ghost-failed";
            return false;
        }

        StopAllAnimationsImmediately(ghost.AnimManager);
        DirectorReplayActorSample baseSample = CaptureSample(ghost, 0d);
        PositionActionPreview(baseSample);
        NormalizeActionPreviewBaseSample(baseSample);
        track.Samples.Add(baseSample.DeepClone());
        bool? savedTpUseFpAnimations = null;
        bool? savedSelfUseFpAnimations = null;
        if (ghost is EntityPlayer player)
        {
            savedTpUseFpAnimations = player.TpAnimManager.UseFpAnmations;
            savedSelfUseFpAnimations = player.SelfFpAnimManager.UseFpAnmations;
            ConfigureReplayPlayer(player, markShapeModified: true);
        }
        binding = new ActorBinding(
            track,
            ghost,
            baseSample.DeepClone(),
            Array.Empty<byte>(),
            savedRendered: false,
            savedShadowRendered: false,
            savedItemSpawnedMilliseconds: null,
            isGhost: true,
            hiddenLiveEntity: null,
            hiddenLiveRendered: false,
            savedTpUseFpAnimations,
            savedSelfUseFpAnimations);
        PrepareReplayHandItems(binding);
        ApplyReplayPose(ghost, baseSample);
        if (ghost is EntityAgent agent)
        {
            agent.BodyYaw = ReplayBodyYaw(ghost, baseSample);
            agent.BodyYawServer = ghost is EntityPlayer && baseSample.HasThirdPersonPose
                ? NonZeroPlayerBodyYaw(agent.BodyYaw)
                : baseSample.BodyYawServer;
        }
        SetReplayEntityVisibility(ghost, false);
        return true;
    }

    private byte[] TryCreatePreviewEntityState(string entityCode, long entityId)
    {
        Entity? temporary = null;
        try
        {
            EntityProperties? properties = capi.World.GetEntityType(AssetLocation.Create(entityCode));
            if (properties?.Client is null)
            {
                return Array.Empty<byte>();
            }

            temporary = capi.ClassRegistry.CreateEntity(properties);
            temporary.EntityId = entityId;
            temporary.SimulationRange = 9999;
            temporary.Initialize(properties.Clone(), capi, 0L);
            temporary.AfterInitialized(onFirstSpawn: false);
            temporary.State = EnumEntityState.Inactive;
            temporary.IdleSoundChanceModifier = 0f;
            return TrySerializeEntity(temporary, entityId);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: GUI preview entity type {0} could not be initialized: {1}",
                entityCode,
                exception.Message);
            return Array.Empty<byte>();
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    DespawnReplayEntity(temporary);
                }
                catch
                {
                    // A temporary preview source is never added to the world.
                }
            }
        }
    }

    public bool TryCaptureActionPreviewModel(
        string actorSelector,
        out string entityCode,
        out string displayName,
        out byte[] entityState,
        out string error)
    {
        entityCode = string.Empty;
        displayName = string.Empty;
        entityState = Array.Empty<byte>();
        error = string.Empty;
        Entity? entity = ResolveLiveActionEntity(actorSelector, out DirectorReplayActorPathSource source);
        if (entity is null)
        {
            error = "action-preview-no-model-source";
            return false;
        }
        entityCode = entity.Code?.ToShortString() ?? source.EntityCode;
        displayName = source.DisplayName;
        entityState = TrySerializeEntity(
            entity,
            ActionPreviewGhostEntityId,
            useThirdPersonPlayerAnimations: true);
        if (entityCode.Length == 0 || entityState.Length == 0)
        {
            entityCode = string.Empty;
            displayName = string.Empty;
            entityState = Array.Empty<byte>();
            error = "action-preview-model-store-failed";
            return false;
        }
        return true;
    }

    public void StopActionPreviewActor()
    {
        ActorBinding? binding = actionPreviewBinding;
        actionPreviewBinding = null;
        actionPreviewWorldVisible = true;
        actionPreviewDefaultDistance = 4d;
        actionPreviewDistance = 4d;
        actionPreviewYawOffset = 0d;
        if (binding is null)
        {
            return;
        }
        RemoveActionPreviewBinding(binding);
    }

    internal void StopActionWorkflowPreviewActors()
    {
        ActorBinding[] bindings = actionWorkflowPreviewBindings.Values.ToArray();
        actionWorkflowPreviewBindings.Clear();
        foreach (ActorBinding binding in bindings)
        {
            RemoveActionPreviewBinding(binding);
        }
        actionWorkflowRecordingGhosts.Clear();
        actionWorkflowRecordingExcludedEntities.Clear();
    }

    private void RemoveActionPreviewBinding(ActorBinding binding)
    {
        try
        {
            StopAllAnimationsImmediately(binding.Entity.AnimManager);
            RemoveGhost(binding.Entity);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: action preview Ghost could not be removed: {0}", exception.Message);
        }
    }

    private void UpdateActionPreviewActors(float deltaTime)
    {
        if (actionPreviewBinding is { } primary
            && !UpdateActionPreviewActor(primary, actionPreviewWorldVisible, deltaTime))
        {
            actionPreviewBinding = null;
        }
        foreach ((string actorKey, ActorBinding binding) in actionWorkflowPreviewBindings.ToArray())
        {
            if (!UpdateActionPreviewActor(binding, worldVisible: true, deltaTime: deltaTime))
            {
                actionWorkflowPreviewBindings.Remove(actorKey);
            }
        }
    }

    private bool UpdateActionPreviewActor(
        ActorBinding binding,
        bool worldVisible,
        float deltaTime)
    {
        if (!game.LoadedEntities.TryGetValue(binding.Entity.EntityId, out Entity? loaded)
            || !ReferenceEquals(loaded, binding.Entity))
        {
            return false;
        }

        DirectorReplayActorSample sample = binding.Track.Samples[0].DeepClone();
        if (ActionSampleDecorator is not null)
        {
            sample = ActionSampleDecorator(binding.Track, 0d, sample);
        }
        object? animator = binding.Entity.AnimManager?.Animator;
        bool animatorReplaced = !ReferenceEquals(binding.LastAnimator, animator);
        ApplySample(
            binding.Entity,
            sample,
            binding,
            forceAnimations: !binding.HasAppliedSample || animatorReplaced);
        binding.InterpolatedSample = sample;
        binding.HasAppliedSample = true;
        binding.WasVisible = true;
        binding.LastAnimator = binding.Entity.AnimManager?.Animator;
        ActionEntityDecorator?.Invoke(binding.Track, 0d, binding.Entity, sample);
        if (!ReferenceEquals(binding.LastAnimator, binding.Entity.AnimManager?.Animator))
        {
            binding.LastAnimator = null;
        }
        if (binding.Entity is EntityPlayer player)
        {
            ConfigureReplayPlayer(player, markShapeModified: false);
        }
        binding.Entity.State = EnumEntityState.Inactive;
        binding.Entity.IdleSoundChanceModifier = 0f;
        SetReplayEntityVisibility(binding.Entity, worldVisible);
        ApplyRecordedAnimationState(
            binding.Entity.AnimManager,
            sample.AnimationStates,
            recalculatePose: true,
            onlyActive: true,
            advanceSeconds: float.IsFinite(deltaTime) ? Math.Max(0f, deltaTime) : 0f,
            applyRecordedRenderMetadata: ShouldApplyRecordedAnimationRenderMetadata(binding.Entity));
        return true;
    }

    private void PositionActionPreview(DirectorReplayActorSample sample)
    {
        IClientPlayer? clientPlayer = capi.World.Player;
        EntityPlayer? player = clientPlayer?.Entity;
        if (clientPlayer is null || player is null)
        {
            return;
        }
        double cameraYaw = game.MainCamera.Yaw;
        Vec3d eye = game.MainCamera.CameraEyePos;
        Vec3d forward = game.MainCamera.forwardVec;
        if (!double.IsFinite(eye.X)
            || !double.IsFinite(eye.Y)
            || !double.IsFinite(eye.Z)
            || !double.IsFinite(forward.X)
            || !double.IsFinite(forward.Y)
            || !double.IsFinite(forward.Z)
            || forward.LengthSq() < 0.25d)
        {
            cameraYaw = clientPlayer.CameraYaw;
            Vec3f fallback = EntityPos.GetViewVector(clientPlayer.CameraPitch, clientPlayer.CameraYaw);
            eye = new Vec3d(
                player.CameraPos.X + player.LocalEyePos.X,
                player.CameraPos.Y + player.LocalEyePos.Y,
                player.CameraPos.Z + player.LocalEyePos.Z);
            forward = new Vec3d(fallback.X, fallback.Y, fallback.Z);
        }
        double distance = actionPreviewDistance;
        double dimensionOffset = player.Pos.Dimension * 32768d;
        double modelHeightOffset = Math.Clamp(sample.SelectionBoxY2 * 0.55d, 0.45d, 1.25d);
        sample.X = eye.X + forward.X * distance;
        sample.Y = eye.Y - dimensionOffset + forward.Y * distance - modelHeightOffset;
        sample.Z = eye.Z + forward.Z * distance;
        sample.Dimension = player.Pos.Dimension;
        float facingYaw = GameMath.Mod(
            (float)(cameraYaw + Math.PI + actionPreviewYawOffset),
            GameMath.TWOPI);
        sample.Yaw = facingYaw;
        sample.Pitch = 0f;
        sample.Roll = 0f;
        sample.HeadYaw = 0f;
        sample.HeadPitch = 0f;
        sample.BodyYaw = facingYaw;
        sample.HasThirdPersonPose = true;
        sample.ThirdPersonBodyYaw = facingYaw;
        sample.ThirdPersonHeadYaw = 0f;
        sample.ThirdPersonHeadPitch = 0f;
        sample.BodyYawServer = facingYaw;
        sample.WalkYaw = facingYaw;
        sample.WalkPitch = 0f;
        sample.MotionX = 0d;
        sample.MotionY = 0d;
        sample.MotionZ = 0d;
        sample.HasMotion = false;
    }

    private static void NormalizeActionPreviewBaseSample(DirectorReplayActorSample sample)
    {
        sample.Animations.Clear();
        sample.AnimationStates.Clear();
        sample.Controls = 0;
        sample.ServerControls = 0;
        sample.HandUse = (int)EnumHandInteract.None;
        sample.ServerHandUse = (int)EnumHandInteract.None;
        sample.OnGround = true;
        sample.FeetInLiquid = false;
        sample.Swimming = false;
        sample.ClimbingOnFace = string.Empty;
        sample.CanStandUp = true;
    }

    public void CaptureParticle(IParticlePropertiesProvider provider)
    {
        DirectorReplayDocument? active = recording;
        if (active is null || applyingReplayState || !CaptureParticles || !CanCaptureEvent(active))
        {
            return;
        }

        try
        {
            DirectorReplayParticleEvent particle = new()
            {
                TimeSeconds = recordingElapsedSeconds,
                Kind = DirectorReplayParticleKind.Provider,
                IsWeatherParticle = DirectorReplayWeatherVisualPatch.IsWeatherParticleSpawnScope,
                ProviderType = provider.GetType().AssemblyQualifiedName ?? provider.GetType().FullName ?? string.Empty,
                ProviderData = DirectorReplaySerialization.SerializeParticle(provider)
            };
            lock (particleCaptureSync)
            {
                if (ReferenceEquals(recording, active)
                    && !applyingReplayState
                    && CaptureParticles
                    && CanCaptureEvent(active))
                {
                    if (TryAddCapturedParticle(particle))
                    {
                        active.ParticleEvents.Add(particle);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: particle provider could not be captured: {0}", exception.Message);
        }
    }

    public void CaptureParticle(
        float quantity,
        int color,
        Vec3d minPos,
        Vec3d maxPos,
        Vec3f minVelocity,
        Vec3f maxVelocity,
        float lifeLength,
        float gravityEffect,
        float scale,
        EnumParticleModel model)
    {
        DirectorReplayDocument? active = recording;
        if (active is null || applyingReplayState || !CaptureParticles || !CanCaptureEvent(active))
        {
            return;
        }

        DirectorReplayParticleEvent particle = new()
        {
            TimeSeconds = recordingElapsedSeconds,
            Kind = DirectorReplayParticleKind.Simple,
            Quantity = quantity,
            Color = color,
            MinX = minPos.X,
            MinY = minPos.Y,
            MinZ = minPos.Z,
            MaxX = maxPos.X,
            MaxY = maxPos.Y,
            MaxZ = maxPos.Z,
            MinVelocityX = minVelocity.X,
            MinVelocityY = minVelocity.Y,
            MinVelocityZ = minVelocity.Z,
            MaxVelocityX = maxVelocity.X,
            MaxVelocityY = maxVelocity.Y,
            MaxVelocityZ = maxVelocity.Z,
            LifeLength = lifeLength,
            GravityEffect = gravityEffect,
            Scale = scale,
            ParticleModel = (int)model
        };
        lock (particleCaptureSync)
        {
            if (ReferenceEquals(recording, active)
                && !applyingReplayState
                && CaptureParticles
                && CanCaptureEvent(active))
            {
                if (TryAddCapturedParticle(particle))
                {
                    active.ParticleEvents.Add(particle);
                }
            }
        }
    }

    private bool TryAddCapturedParticle(DirectorReplayParticleEvent particle)
    {
        if (!capturedParticles.TryGetValue(
            particle.TimeSeconds,
            out HashSet<DirectorReplayParticleEvent>? capturedAtTime))
        {
            capturedAtTime = new HashSet<DirectorReplayParticleEvent>(
                DirectorReplayMath.ParticlePayloadComparer);
            capturedParticles.Add(particle.TimeSeconds, capturedAtTime);
        }
        return capturedAtTime.Add(particle);
    }

    internal void CaptureClientSoundStart(ILoadedSound? sound)
    {
        if (sound is null)
        {
            return;
        }

        if (replayLoadedSounds.TryGetValue(sound, out ReplayLoadedSoundMarker? marker))
        {
            if (ReferenceEquals(marker.CaptureDocument, recording) && marker.CapturedActive)
            {
                return;
            }
            CaptureSound(
                sound.Params,
                marker,
                DirectorReplaySoundAction.Play,
                ReadSoundPlaybackPosition(sound),
                ReadSoundLength(sound));
            return;
        }

        CaptureSound(
            sound.Params,
            marker: null,
            DirectorReplaySoundAction.Play,
            playbackPosition: 0f,
            ReadSoundLength(sound));
    }

    internal void RegisterReplayLoadedSound(ILoadedSound? sound, SoundParams? parameters)
    {
        if (sound is null || parameters is null)
        {
            return;
        }

        if (replayLoadedSounds.TryGetValue(sound, out _))
        {
            return;
        }

        ReplayLoadedSoundMarker marker = new(Interlocked.Increment(ref nextReplayLoadedSoundId));
        replayLoadedSounds.Add(sound, marker);
        replayLoadedSoundReferences.Add(new WeakReference<ILoadedSound>(sound));
        if (replayLoadedSoundReferences.Count >= 256)
        {
            PruneReplayLoadedSoundReferences();
        }
    }

    internal bool ShouldAllowLoadedSoundStart(ILoadedSound sound)
    {
        Playback? active = playback;
        if (active is null || active.PlaybackOwnedSounds.Contains(sound))
        {
            return true;
        }

        SoundParams parameters = sound.Params;
        bool spatial = !parameters.RelativePosition && parameters.Position is not null;
        if (spatial
            && parameters.Position is { } position
            && active.WorldPlayback?.OwnsReplayBlockEntitySound(position) == true)
        {
            active.ReplayWorldOwnedSounds.Add(sound);
            return false;
        }
        if (spatial && parameters.ShouldLoop)
        {
            active.SuppressedLiveSoundStarts.Add(sound);
        }
        return !spatial;
    }

    internal bool ShouldApplyLoadedSoundVolume(ILoadedSound sound, float volume)
    {
        Playback? active = playback;
        if (active is null || active.PlaybackOwnedSounds.Contains(sound))
        {
            return true;
        }
        if (active.ReplayWorldOwnedSounds.Contains(sound))
        {
            return false;
        }
        if (!active.SuppressedLiveSoundVolumes.ContainsKey(sound))
        {
            return true;
        }

        active.SuppressedLiveSoundVolumes[sound] = volume;
        return false;
    }

    private void SuppressLiveSpatialSounds(Playback active)
    {
        for (int index = replayLoadedSoundReferences.Count - 1; index >= 0; index--)
        {
            if (!replayLoadedSoundReferences[index].TryGetTarget(out ILoadedSound? sound)
                || sound.IsDisposed)
            {
                replayLoadedSoundReferences.RemoveAt(index);
                continue;
            }

            SoundParams parameters = sound.Params;
            if (!parameters.RelativePosition
                && parameters.Position is not null
                && parameters.ShouldLoop
                && (sound.IsPlaying || sound.IsPaused))
            {
                float volume = parameters.Volume;
                sound.SetVolume(0f);
                active.SuppressedLiveSoundVolumes.TryAdd(sound, volume);
            }
        }
    }

    private void RestoreSuppressedLiveSpatialSounds(Playback active)
    {
        foreach ((ILoadedSound sound, float volume) in active.SuppressedLiveSoundVolumes)
        {
            try
            {
                if (CanRestoreLiveSpatialSound(sound))
                {
                    sound.SetVolume(volume);
                }
                else
                {
                    DisposeReplaySound(sound);
                }
            }
            catch (Exception exception)
            {
                DisposeReplaySound(sound);
                capi.Logger.Debug(
                    "VS Director: suppressed live loop could not be restored: {0}",
                    exception.Message);
            }
        }

        foreach (ILoadedSound sound in active.SuppressedLiveSoundStarts)
        {
            try
            {
                if (active.SuppressedLiveSoundVolumes.ContainsKey(sound) || sound.IsDisposed)
                {
                    continue;
                }
                if (CanRestoreLiveSpatialSound(sound))
                {
                    sound.Start();
                }
                else
                {
                    DisposeReplaySound(sound);
                }
            }
            catch (Exception exception)
            {
                DisposeReplaySound(sound);
                capi.Logger.Debug(
                    "VS Director: deferred live loop could not be restored: {0}",
                    exception.Message);
            }
        }

        active.SuppressedLiveSoundVolumes.Clear();
        active.SuppressedLiveSoundStarts.Clear();
    }

    private static void DisposeReplayWorldOwnedSounds(Playback active)
    {
        foreach (ILoadedSound sound in active.ReplayWorldOwnedSounds)
        {
            DisposeReplaySound(sound);
        }
        active.ReplayWorldOwnedSounds.Clear();
    }

    private bool CanRestoreLiveSpatialSound(ILoadedSound sound)
    {
        if (sound.IsDisposed || sound.Params.Position is not { } position)
        {
            return false;
        }

        BlockPos blockPosition = new(
            (int)Math.Floor(position.X),
            (int)Math.Floor(position.Y),
            (int)Math.Floor(position.Z),
            game.EntityPlayer.Pos.Dimension);
        if (capi.World.BlockAccessor.GetBlockEntity(blockPosition) is not null)
        {
            return true;
        }

        return game.LoadedEntities.Values.Any(entity =>
            !replayGhostIds.Contains(entity.EntityId)
            && entity.Pos.Dimension == blockPosition.dimension
            && Math.Abs(entity.Pos.X - position.X) <= 2d
            && Math.Abs(entity.Pos.Y - position.Y) <= 2d
            && Math.Abs(entity.Pos.Z - position.Z) <= 2d);
    }

    internal void CaptureReplayLoadedSoundStart(ILoadedSound? sound)
    {
        if (sound is null || !replayLoadedSounds.TryGetValue(sound, out ReplayLoadedSoundMarker? marker))
        {
            return;
        }

        CaptureSound(
            sound.Params,
            marker,
            DirectorReplaySoundAction.Play,
            ReadSoundPlaybackPosition(sound),
            ReadSoundLength(sound));
    }

    internal void CaptureReplayLoadedSoundState(
        ILoadedSound? sound,
        DirectorReplaySoundAction action,
        float? pitchOffset = null)
    {
        if (sound is null
            || !replayLoadedSounds.TryGetValue(sound, out ReplayLoadedSoundMarker? marker))
        {
            return;
        }

        if (pitchOffset is float offset && float.IsFinite(offset))
        {
            marker.PitchOffset = offset;
        }
        CaptureSound(
            sound.Params,
            marker,
            action,
            ReadSoundPlaybackPosition(sound),
            ReadSoundLength(sound));
    }

    private void CaptureAlreadyPlayingLoadedSounds()
    {
        for (int index = replayLoadedSoundReferences.Count - 1; index >= 0; index--)
        {
            if (!replayLoadedSoundReferences[index].TryGetTarget(out ILoadedSound? sound)
                || sound.IsDisposed)
            {
                replayLoadedSoundReferences.RemoveAt(index);
                continue;
            }

            if (sound.IsPlaying)
            {
                CaptureReplayLoadedSoundStart(sound);
            }
            else if (sound.IsPaused)
            {
                CaptureReplayLoadedSoundStart(sound);
                CaptureReplayLoadedSoundState(sound, DirectorReplaySoundAction.Pause);
            }
        }
    }

    private void PruneReplayLoadedSoundReferences()
    {
        for (int index = replayLoadedSoundReferences.Count - 1; index >= 0; index--)
        {
            if (!replayLoadedSoundReferences[index].TryGetTarget(out ILoadedSound? sound)
                || sound.IsDisposed)
            {
                replayLoadedSoundReferences.RemoveAt(index);
            }
        }
    }

    private void ResynchronizeLoadedSounds()
    {
        for (int index = replayLoadedSoundReferences.Count - 1; index >= 0; index--)
        {
            if (!replayLoadedSoundReferences[index].TryGetTarget(out ILoadedSound? sound)
                || sound.IsDisposed)
            {
                replayLoadedSoundReferences.RemoveAt(index);
                continue;
            }

            if (!replayLoadedSounds.TryGetValue(sound, out ReplayLoadedSoundMarker? marker))
            {
                continue;
            }
            bool capturedActive = ReferenceEquals(marker.CaptureDocument, recording)
                && marker.CapturedActive;
            if (sound.IsPlaying)
            {
                if (capturedActive)
                {
                    CaptureReplayLoadedSoundState(sound, DirectorReplaySoundAction.Update);
                }
                else
                {
                    CaptureReplayLoadedSoundStart(sound);
                }
            }
            else if (sound.IsPaused)
            {
                if (!capturedActive)
                {
                    CaptureReplayLoadedSoundStart(sound);
                }
                CaptureReplayLoadedSoundState(sound, DirectorReplaySoundAction.Pause);
            }
            else if (capturedActive)
            {
                CaptureReplayLoadedSoundState(sound, DirectorReplaySoundAction.Stop);
            }
        }
    }

    private void CaptureSound(
        SoundParams? parameters,
        ReplayLoadedSoundMarker? marker,
        DirectorReplaySoundAction action,
        float playbackPosition,
        float soundLengthSeconds)
    {
        if (parameters?.Location is not { } location)
        {
            return;
        }

        Vec3f? position = parameters.Position;
        if (!parameters.RelativePosition && position is null)
        {
            return;
        }

        double x = position?.X ?? 0d;
        double y = position?.Y ?? 0d;
        double z = position?.Z ?? 0d;
        lock (soundCaptureSync)
        {
            DirectorReplayDocument? active = recording;
            if (active is null
                || applyingReplayState
                || !CaptureSounds
                || !DirectorReplayPlaybackPolicy.IsValidSoundPosition(x, y, z)
                || !CanCaptureEvent(active))
            {
                return;
            }

            if (marker is not null)
            {
                if (!ReferenceEquals(marker.CaptureDocument, active))
                {
                    marker.CaptureDocument = active;
                    marker.CapturedActive = false;
                }
                if (action != DirectorReplaySoundAction.Play && !marker.CapturedActive)
                {
                    return;
                }
                marker.CapturedActive = action is DirectorReplaySoundAction.Play
                    or DirectorReplaySoundAction.Update;
            }

            DirectorReplaySoundEvent captured = new()
            {
                TimeSeconds = recordingElapsedSeconds,
                Location = location.ToString(),
                InstanceId = marker?.InstanceId ?? 0L,
                Action = action,
                X = x,
                Y = y,
                Z = z,
                RelativePosition = parameters.RelativePosition,
                ShouldLoop = parameters.ShouldLoop,
                PlaybackPosition = Math.Max(0f, playbackPosition),
                SoundLengthSeconds = Math.Max(0f, soundLengthSeconds),
                Volume = parameters.Volume,
                Pitch = parameters.Pitch,
                PitchOffset = marker?.PitchOffset ?? 0f,
                Range = parameters.Range,
                ReferenceDistance = parameters.ReferenceDistance,
                LowPassFilter = parameters.LowPassFilter,
                ReverbDecayTime = parameters.ReverbDecayTime,
                SoundType = (int)parameters.SoundType
            };
            if (action == DirectorReplaySoundAction.Update
                && active.SoundEvents.LastOrDefault() is DirectorReplaySoundEvent previous
                && previous.Action == DirectorReplaySoundAction.Update
                && previous.InstanceId == captured.InstanceId
                && captured.TimeSeconds - previous.TimeSeconds < 1d / Math.Max(1d, active.SampleRate))
            {
                active.SoundEvents[^1] = captured;
            }
            else
            {
                active.SoundEvents.Add(captured);
            }
        }
    }

    private static float ReadSoundPlaybackPosition(ILoadedSound sound)
    {
        try
        {
            return Math.Max(0f, sound.PlaybackPosition);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private static float ReadSoundLength(ILoadedSound sound)
    {
        try
        {
            return Math.Max(0f, sound.SoundLengthSeconds);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private void CaptureFrame(double timeSeconds)
    {
        DirectorReplayDocument? active = recording;
        Dictionary<string, DirectorReplayActorTrack>? actors = recordingActors;
        if (active is null || actors is null)
        {
            return;
        }

        Entity[] loadedEntities = LoadedCaptureEntities(applyCapacity: false);
        HashSet<long> loadedEntityIds = loadedEntities
            .Select(entity => entity.EntityId)
            .ToHashSet();
        string[] previouslyVisible = recordingVisibleActors.ToArray();
        HashSet<string> visible = new(StringComparer.Ordinal);
        foreach (Entity entity in ApplyCaptureCapacity(loadedEntities))
        {
            DirectorReplayActorTrack? track = EnsureActor(entity, timeSeconds);
            if (track is null)
            {
                continue;
            }

            visible.Add(track.ActorKey);
            if (!recordingVisibleActors.Contains(track.ActorKey))
            {
                track.Lifetimes.Add(new DirectorReplayActorLifetime
                {
                    StartSeconds = timeSeconds,
                    EndSeconds = timeSeconds
                });
            }

            track.Lifetimes[^1].EndSeconds = timeSeconds;
            AddOrReplaceSample(track, CaptureSample(entity, timeSeconds));
        }

        foreach (string actorKey in previouslyVisible.Where(actorKey => !visible.Contains(actorKey)))
        {
            if (actors.TryGetValue(actorKey, out DirectorReplayActorTrack? disappeared)
                && disappeared.Lifetimes.Count > 0)
            {
                if (IsRecordingActorStillLoaded(disappeared, loadedEntityIds))
                {
                    continue;
                }

                disappeared.Lifetimes[^1].EndedByDespawn = true;
                RemoveRecordingActorBinding(actorKey);
            }
        }

        recordingVisibleActors = visible;
        active.DurationSeconds = timeSeconds;
    }

    private void CaptureFullEntityStates(double timeSeconds)
    {
        CaptureClock(timeSeconds);
        foreach (Entity entity in LoadedCaptureEntities())
        {
            DirectorReplayActorTrack? track = EnsureActor(entity, timeSeconds);
            if (track is null)
            {
                continue;
            }

            CaptureEntityState(entity, track, timeSeconds);
        }
    }

    private void QueueEntityStateCapture()
    {
        if (pendingEntityStateCaptures.Count > 0)
        {
            return;
        }

        foreach (Entity entity in LoadedCaptureEntities())
        {
            pendingEntityStateCaptures.Enqueue(entity.EntityId);
        }
    }

    private void ProcessEntityStateCaptures(int maximum, double timeSeconds)
    {
        if (recording is null || recordingActors is null || maximum <= 0)
        {
            return;
        }

        for (int captured = 0; captured < maximum && pendingEntityStateCaptures.Count > 0; captured++)
        {
            long entityId = pendingEntityStateCaptures.Dequeue();
            Entity? entity = actionWorkflowRecordingGhosts.Keys.FirstOrDefault(candidate =>
                    candidate.EntityId == entityId)
                ?? capi.World.GetEntityById(entityId);
            if (!IsRecordingCaptureEntityEligible(entity))
            {
                continue;
            }

            DirectorReplayActorTrack? track = EnsureActor(entity!, timeSeconds);
            if (track is not null)
            {
                CaptureEntityState(entity!, track, timeSeconds);
            }
        }
    }

    private void CaptureEntityState(
        Entity entity,
        DirectorReplayActorTrack track,
        double timeSeconds)
    {
        byte[] data = TrySerializeEntity(
            entity,
            track.ReplayEntityId,
            useThirdPersonPlayerAnimations: true);
        if (data.Length == 0)
        {
            return;
        }

        if (track.InitialState.Length == 0)
        {
            track.InitialState = CloneBytes(data);
        }

        if (track.States.Count == 0
            || Math.Abs(track.States[^1].TimeSeconds - timeSeconds) > TimeEpsilon
            || !track.States[^1].Data.AsSpan().SequenceEqual(data))
        {
            track.States.Add(new DirectorReplayActorState
            {
                TimeSeconds = timeSeconds,
                Data = data
            });
        }
    }

    private void CaptureClock(double timeSeconds)
    {
        DirectorReplayDocument? active = recording;
        if (active is null)
        {
            return;
        }

        double visualTotalHours = capi.World.Calendar.TotalHours
            + capi.World.Calendar.Timelapse * capi.World.Calendar.HoursPerDay;
        DirectorReplayClockSample sample = new()
        {
            TimeSeconds = timeSeconds,
            TotalHours = visualTotalHours,
            CalendarSpeedMul = capi.World.Calendar.CalendarSpeedMul,
            SeasonOverride = capi.World.Calendar.SeasonOverride
        };
        if (active.ClockSamples.Count > 0
            && Math.Abs(active.ClockSamples[^1].TimeSeconds - timeSeconds) <= TimeEpsilon)
        {
            active.ClockSamples[^1] = sample;
        }
        else
        {
            active.ClockSamples.Add(sample);
        }
    }

    private Entity[] LoadedCaptureEntities(bool applyCapacity = true)
    {
        Entity[] loaded = capi.World.LoadedEntities.Values
            .Where(IsRecordingCaptureEntityEligible)
            .OrderByDescending(entity => actionWorkflowRecordingGhosts.ContainsKey(entity))
            .ThenByDescending(entity => entity is EntityPlayer)
            .ThenByDescending(IsReusableTransientCaptureEntity)
            .ThenByDescending(entity => entity.IsCreature)
            .ThenBy(entity => entity.EntityId)
            .ToArray();
        return applyCapacity ? ApplyCaptureCapacity(loaded) : loaded;
    }

    private Entity[] ApplyCaptureCapacity(IEnumerable<Entity> entities)
        => entities
            .Take(Math.Clamp(CaptureMaxActors, 1, 2048))
            .ToArray();

    private bool IsRecordingActorStillLoaded(
        DirectorReplayActorTrack track,
        HashSet<long> loadedEntityIds)
    {
        if (!string.IsNullOrWhiteSpace(track.PlayerUid))
        {
            return loadedEntityIds.Contains(track.EntityId);
        }

        return recordingEntityActors.Values.Any(binding =>
            string.Equals(binding.ActorKey, track.ActorKey, StringComparison.Ordinal)
            && loadedEntityIds.Contains(binding.Entity.EntityId));
    }

    private void RemoveRecordingActorBinding(string actorKey)
    {
        foreach (long entityId in recordingEntityActors
            .Where(pair => string.Equals(pair.Value.ActorKey, actorKey, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray())
        {
            recordingEntityActors.Remove(entityId);
        }
    }

    private bool IsCaptureEntityEligible(Entity? entity)
    {
        if (entity is null
            || entity.EntityId < 0
            || (!config.ReplayIncludeNonCreatures && !entity.IsCreature && entity is not EntityPlayer))
        {
            return false;
        }

        // BlockPlaceOnDrop removes an EntityItem before the client entity table
        // necessarily drops it. Recording that dead entry extends its lifetime
        // over the newly placed block and renders both states at once.
        if (entity is EntityItem item && !IsItemEntityAlive(item))
        {
            return false;
        }

        // EntityBlockFalling lingers for roughly one second after it has put
        // the block back into the world. Its dedicated renderer has already
        // switched DoRender off at that point, so the lingering entity must not
        // extend the recorded visual lifetime over the placed block.
        return !IsFallingBlockEntity(entity) || IsFallingBlockRenderActive(entity);
    }

    private bool IsRecordingCaptureEntityEligible(Entity? entity)
    {
        if (entity is null || actionWorkflowRecordingExcludedEntities.Contains(entity))
        {
            return false;
        }
        return actionWorkflowRecordingGhosts.ContainsKey(entity) || IsCaptureEntityEligible(entity);
    }

    private static bool IsReusableTransientCaptureEntity(Entity entity)
        => entity is EntityItem || IsFallingBlockEntity(entity);

    private DirectorReplayActorTrack? EnsureActor(Entity entity, double timeSeconds)
    {
        DirectorReplayDocument? active = recording;
        Dictionary<string, DirectorReplayActorTrack>? actors = recordingActors;
        if (active is null || actors is null)
        {
            return null;
        }

        bool workflowGhost = actionWorkflowRecordingGhosts.TryGetValue(entity, out string? workflowActorKey);
        string playerUid = workflowGhost
            ? string.Empty
            : entity is EntityPlayer player ? player.PlayerUID ?? string.Empty : string.Empty;
        string baseActorKey = workflowGhost
            ? workflowActorKey!
            : playerUid.Length > 0 ? "player:" + playerUid : "entity:" + entity.EntityId;
        string actorKey = baseActorKey;
        if (playerUid.Length > 0
            && actors.TryGetValue(actorKey, out DirectorReplayActorTrack? existingPlayer))
        {
            return existingPlayer;
        }
        if (playerUid.Length == 0
            && recordingEntityActors.TryGetValue(entity.EntityId, out RecordingActorBinding mapped))
        {
            if (ReferenceEquals(mapped.Entity, entity)
                && actors.TryGetValue(mapped.ActorKey, out DirectorReplayActorTrack? existingEntity))
            {
                return existingEntity;
            }

            if (actors.TryGetValue(mapped.ActorKey, out DirectorReplayActorTrack? replaced)
                && replaced.Lifetimes.Count > 0)
            {
                replaced.Lifetimes[^1].EndSeconds = Math.Min(
                    replaced.Lifetimes[^1].EndSeconds,
                    timeSeconds);
                replaced.Lifetimes[^1].EndedByDespawn = true;
                recordingVisibleActors.Remove(mapped.ActorKey);
            }

            recordingEntityActors.Remove(entity.EntityId);
        }

        bool reusableTransient = IsReusableTransientCaptureEntity(entity);
        bool canUseTransientReserve = workflowGhost
            || playerUid.Length > 0
            || reusableTransient;
        // The document accumulates history for the whole recording. Counting
        // actors.Count here permanently stopped new creature capture after
        // 192 historical tracks with the default 256-actor budget.
        if (!DirectorReplayActorSchedulingPolicy.CanAllocateRecordingActor(
            recordingVisibleActors,
            recordingReusableActorTypes,
            CaptureMaxActors,
            canUseTransientReserve))
        {
            return null;
        }

        if (reusableTransient
            && TryReuseRecordingActor(entity, actors, out DirectorReplayActorTrack? reusableTrack)
            && reusableTrack is not null)
        {
            recordingEntityActors[entity.EntityId] = new RecordingActorBinding(
                reusableTrack.ActorKey,
                entity);
            CaptureEntityState(entity, reusableTrack, timeSeconds);
            return reusableTrack;
        }

        if (actors.ContainsKey(actorKey))
        {
            for (int generation = 2; ; generation++)
            {
                string candidate = $"{baseActorKey}#{generation}";
                if (!actors.ContainsKey(candidate))
                {
                    actorKey = candidate;
                    break;
                }
            }
        }

        long replayEntityId = long.MinValue + actors.Count + 1L;
        string displayName = entity is EntityPlayer entityPlayer
            ? entityPlayer.Player?.PlayerName ?? entityPlayer.PlayerUID ?? actorKey
            : entity.Code?.ToShortString() ?? actorKey;
        byte[] initialState = TrySerializeEntity(
            entity,
            replayEntityId,
            useThirdPersonPlayerAnimations: true);
        DirectorReplayActorTrack track = new()
        {
            ActorKey = actorKey,
            EntityId = entity.EntityId,
            PlayerUid = playerUid,
            ForceGhost = workflowGhost,
            EntityCode = entity.Code?.ToShortString() ?? string.Empty,
            DisplayName = displayName,
            ReplayEntityId = replayEntityId,
            SimulationRange = entity.SimulationRange,
            InitialState = CloneBytes(initialState),
            Lifetimes = new List<DirectorReplayActorLifetime>
            {
                new() { StartSeconds = timeSeconds, EndSeconds = timeSeconds }
            }
        };
        if (initialState.Length > 0)
        {
            track.States.Add(new DirectorReplayActorState
            {
                TimeSeconds = timeSeconds,
                Data = initialState
            });
        }

        actors.Add(actorKey, track);
        if (reusableTransient)
        {
            recordingReusableActorTypes[actorKey] = entity.GetType().FullName ?? entity.GetType().Name;
        }
        if (playerUid.Length == 0)
        {
            recordingEntityActors[entity.EntityId] = new RecordingActorBinding(actorKey, entity);
        }
        active.Actors.Add(track);
        recordingVisibleActors.Add(actorKey);
        return track;
    }

    private bool TryReuseRecordingActor(
        Entity entity,
        Dictionary<string, DirectorReplayActorTrack> actors,
        out DirectorReplayActorTrack? track)
    {
        string runtimeType = entity.GetType().FullName ?? entity.GetType().Name;
        HashSet<string> boundActorKeys = recordingEntityActors.Values
            .Select(binding => binding.ActorKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string actorKey, string recordedType) in recordingReusableActorTypes)
        {
            if (!string.Equals(recordedType, runtimeType, StringComparison.Ordinal)
                || recordingVisibleActors.Contains(actorKey)
                || boundActorKeys.Contains(actorKey)
                || !actors.TryGetValue(actorKey, out track))
            {
                continue;
            }

            return true;
        }

        track = null;
        return false;
    }

    private void OnEntitySpawn(Entity entity)
    {
        Playback? activePlayback = playback;
        if (!applyingReplayState
            && activePlayback is not null
            && ShouldSuppressLiveReality(activePlayback)
            && !IsReplayControlledEntity(activePlayback, entity))
        {
            SuppressLiveEntity(entity);
        }

        if (recording is null || recordingPaused || applyingReplayState || !IsCaptureEntityEligible(entity))
        {
            return;
        }

        DirectorReplayActorTrack? track = EnsureActor(entity, recordingElapsedSeconds);
        if (track is null)
        {
            return;
        }

        if (!recordingVisibleActors.Contains(track.ActorKey))
        {
            track.Lifetimes.Add(new DirectorReplayActorLifetime
            {
                StartSeconds = recordingElapsedSeconds,
                EndSeconds = recordingElapsedSeconds
            });
            recordingVisibleActors.Add(track.ActorKey);
        }

        AddOrReplaceSample(track, CaptureSample(entity, recordingElapsedSeconds));
        CaptureEntityState(entity, track, recordingElapsedSeconds);
    }

    internal IReadOnlyList<DirectorReplayEntityInfo> ReplayEntityInfos
    {
        get
        {
            Playback? active = playback;
            if (active is null)
            {
                return Array.Empty<DirectorReplayEntityInfo>();
            }

            double time = active.TimeSeconds;
            return active.Bindings
                .Where(binding => DirectorReplayMath.IsActorVisible(binding.Track, time)
                    && IsReplayEntityRenderable(binding.Entity)
                    && binding.Entity.IsRendered)
                .Select(binding => new DirectorReplayEntityInfo(
                    binding.Track.ActorKey,
                    ActorDisplayName(binding.Track),
                    binding.Track.PlayerUid,
                    binding.Track.EntityId,
                    binding.Track.ReplayEntityId,
                    binding.Track.EntityCode.Length > 0
                        ? binding.Track.EntityCode
                        : binding.Entity.Code?.ToShortString() ?? string.Empty,
                    IsPlayerActor(binding.Track),
                    binding.Entity.IsCreature,
                    binding.IsGhost,
                    binding.Entity))
                .OrderBy(info => info.Entity.EntityId)
                .ThenBy(info => info.ActorKey, StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal void CaptureVoiceFrame(DirectorVoicePcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!CaptureVoice
            || frame.Samples is not { Length: > 0 }
            || !DirectorReplayPlaybackPolicy.IsValidSoundPosition(
                frame.Spatialization.Position.X,
                frame.Spatialization.Position.Y,
                frame.Spatialization.Position.Z))
        {
            return;
        }

        lock (soundCaptureSync)
        {
            DirectorReplayDocument? active = recording;
            if (active is null || applyingReplayState || !CanCaptureEvent(active))
            {
                return;
            }

            DirectorVoicePosition voicePosition = frame.Spatialization.Position;
            if (TryGetVoiceCaptureRegion(out DirectorVoicePosition center, out int radiusChunks)
                && !DirectorVoiceCaptureRegion.Contains(voicePosition, center, radiusChunks))
            {
                return;
            }

            long nowMilliseconds = capi.World.ElapsedMilliseconds;
            double ageSeconds = frame.TimestampMilliseconds > 0L && nowMilliseconds > frame.TimestampMilliseconds
                ? Math.Min(2d, (nowMilliseconds - frame.TimestampMilliseconds) / 1000d)
                : 0d;
            double requestedTimeSeconds = Math.Max(0d, recordingElapsedSeconds - ageSeconds);
            DirectorVoiceSpatialization spatialization = frame.Spatialization;
            DirectorVoicePosition position = spatialization.Position;
            active.VoiceFrames.Add(new DirectorReplayVoiceFrame
            {
                TimeSeconds = voiceCaptureTimeline.Resolve(
                    active.Id,
                    frame.SourceId,
                    requestedTimeSeconds,
                    frame.Samples.Length),
                SourceId = frame.SourceId,
                SpeakerId = frame.SpeakerId,
                SpeakerName = frame.SpeakerName,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Dimension = position.Dimension,
                Volume = frame.Volume,
                MaxDistance = spatialization.MaxDistance,
                ReferenceDistance = spatialization.ReferenceDistance,
                RolloffFactor = spatialization.RolloffFactor,
                Spatialized = spatialization.IsSpatialized,
                Pcm16 = DirectorVoicePcm.ToLittleEndianBytes(frame.Samples)
            });
            if (voiceTrackRecordingId != active.Id)
            { voiceTrackRecordingId = active.Id; recordedVoiceSpeakers.Clear(); }
            string speakerKey = string.IsNullOrWhiteSpace(frame.SpeakerId) ? frame.SourceId : frame.SpeakerId;
            if (recordedVoiceSpeakers.Add(speakerKey))
                DirectorReplayVoiceTrackPolicy.EnsureTracks(active.ViewportTimeline, new[] { active.VoiceFrames[^1] });
        }
    }

    internal bool TryPickReplayEntity(
        Vec3d rayStart,
        Vec3d rayEnd,
        out DirectorReplayEntityInfo selected)
    {
        selected = default;
        Vec3d direction = rayEnd - rayStart;
        double lengthSquared = direction.LengthSq();
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0.0000000001d)
        {
            return false;
        }

        double nearest = double.PositiveInfinity;
        foreach (DirectorReplayEntityInfo candidate in ReplayEntityInfos)
        {
            Entity entity = candidate.Entity;
            Cuboidf box = entity.SelectionBox is Cuboidf selectedBox
                ? selectedBox
                : new Cuboidf(-0.3f, 0f, -0.3f, 0.3f, 1.8f, 0.3f);
            double minX = entity.Pos.X + Math.Min(box.X1, box.X2);
            double minY = entity.Pos.Y + Math.Min(box.Y1, box.Y2);
            double minZ = entity.Pos.Z + Math.Min(box.Z1, box.Z2);
            double maxX = entity.Pos.X + Math.Max(box.X1, box.X2);
            double maxY = entity.Pos.Y + Math.Max(box.Y1, box.Y2);
            double maxZ = entity.Pos.Z + Math.Max(box.Z1, box.Z2);
            if (!TryIntersectRayAabb(
                    rayStart,
                    direction,
                    lengthSquared,
                    minX,
                    minY,
                    minZ,
                    maxX,
                    maxY,
                    maxZ,
                    out double distance))
            {
                continue;
            }

            if (distance < nearest)
            {
                nearest = distance;
                selected = candidate;
            }
        }

        if (!double.IsFinite(nearest))
        {
            return false;
        }

        // Match Flashback's selection order: a block in front of an entity
        // wins the ray. The world API is asked for blocks only so live-world
        // entities cannot leak into the replay picker.
        BlockSelection? blockSelection = null;
        EntitySelection? ignoredEntitySelection = null;
        try
        {
            capi.World.RayTraceForSelection(
                rayStart,
                rayEnd,
                ref blockSelection,
                ref ignoredEntitySelection,
                efilter: _ => false);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: replay entity block picking failed: {0}", exception.Message);
            blockSelection = null;
        }

        if (blockSelection?.Position is BlockPos blockPosition
            && TryIntersectRayAabb(
                rayStart,
                direction,
                lengthSquared,
                blockPosition.X,
                blockPosition.Y,
                blockPosition.Z,
                blockPosition.X + 1d,
                blockPosition.Y + 1d,
                blockPosition.Z + 1d,
                out double blockDistance)
            && blockDistance + 0.0001d < nearest)
        {
            selected = default;
            return false;
        }

        return true;
    }

    private static bool TryIntersectRayAabb(
        Vec3d origin,
        Vec3d direction,
        double directionLengthSquared,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ,
        out double distance)
    {
        double tMin = 0d;
        double tMax = 1d;
        if (!ClipRayAxis(origin.X, direction.X, minX, maxX, ref tMin, ref tMax)
            || !ClipRayAxis(origin.Y, direction.Y, minY, maxY, ref tMin, ref tMax)
            || !ClipRayAxis(origin.Z, direction.Z, minZ, maxZ, ref tMin, ref tMax))
        {
            distance = 0d;
            return false;
        }

        distance = tMin * Math.Sqrt(directionLengthSquared);
        return double.IsFinite(distance) && distance >= 0d;
    }

    private static bool ClipRayAxis(
        double origin,
        double direction,
        double minimum,
        double maximum,
        ref double tMin,
        ref double tMax)
    {
        if (Math.Abs(direction) <= 0.0000000001d)
        {
            return origin >= minimum && origin <= maximum;
        }

        double inverse = 1d / direction;
        double first = (minimum - origin) * inverse;
        double second = (maximum - origin) * inverse;
        if (first > second)
        {
            (first, second) = (second, first);
        }
        tMin = Math.Max(tMin, first);
        tMax = Math.Min(tMax, second);
        return tMin <= tMax && tMax >= 0d && tMin <= 1d;
    }

    private void OnEntityLoaded(Entity entity)
    {
        Playback? activePlayback = playback;
        if (!applyingReplayState
            && activePlayback is not null
            && ShouldSuppressLiveReality(activePlayback)
            && !IsReplayControlledEntity(activePlayback, entity))
        {
            // EntityLoaded covers chunk/bulk loads and the client's deferred
            // load queue. Those paths do not emit OnEntitySpawn, but the
            // entities still enter the live renderer registry immediately.
            SuppressLiveEntity(entity);
        }
    }

    private void OnEntityDespawn(Entity entity, EntityDespawnData reason)
    {
        _ = reason;
        suppressedLiveEntities.Remove(entity);
        renderedCapturePoses.Remove(entity.EntityId);
        if (recording is null || recordingPaused || applyingReplayState || entity.EntityId < 0)
        {
            return;
        }

        string uid = entity is EntityPlayer player ? player.PlayerUID ?? string.Empty : string.Empty;
        string key;
        if (uid.Length > 0)
        {
            key = "player:" + uid;
        }
        else if (recordingEntityActors.TryGetValue(entity.EntityId, out RecordingActorBinding mapped)
            && ReferenceEquals(mapped.Entity, entity))
        {
            key = mapped.ActorKey;
        }
        else
        {
            return;
        }

        if (recordingActors?.TryGetValue(key, out DirectorReplayActorTrack? track) != true || track is null)
        {
            return;
        }

        bool renderableAtDespawn = IsCaptureEntityEligible(entity);
        if (renderableAtDespawn)
        {
            AddOrReplaceSample(track, CaptureSample(entity, recordingElapsedSeconds));
        }
        if (track.Lifetimes.Count > 0)
        {
            if (renderableAtDespawn)
            {
                track.Lifetimes[^1].EndSeconds = recordingElapsedSeconds;
            }
            track.Lifetimes[^1].EndedByDespawn = true;
        }

        recordingVisibleActors.Remove(key);
        if (uid.Length == 0)
        {
            recordingEntityActors.Remove(entity.EntityId);
        }
    }

    private void OnBlockChanged(BlockPos position, Block oldBlock)
    {
        if (!applyingReplayState
            && !ReplayBlockEntityStateCapturePatch.IsLiveBlockEntityPacket
            && !ReplayBlockEntityStateCapturePatch.IsReplayAnimationRedraw)
        {
            playback?.WorldPlayback?.ObserveLiveBlockChange(position);
        }

        DirectorReplayDocument? active = recording;
        if (active is null
            || applyingReplayState
            || !CaptureWorld
            || !WithinCapturedWorld(active, position))
        {
            return;
        }

        if (recordingPaused)
        {
            QueueBlockNeighborhood(position, pausedBlockChanges);
            return;
        }

        BlockKey key = BlockKey.From(position);
        if (!recordingBlockStates.ContainsKey(key))
        {
            if (oldBlock is not null)
            {
                int fluidId = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Fluid).BlockId;
                recordingBlockStates[key] = new CachedBlockState(
                    oldBlock.BlockId,
                    fluidId,
                    string.Empty,
                    Array.Empty<byte>(),
                    HasBlockEntityState: false);
            }
            else if (TryGetSnapshotBlockState(key, out CachedBlockState snapshotState))
            {
                recordingBlockStates[key] = snapshotState;
            }
        }

        QueueBlockNeighborhood(position);
    }

    public bool ShouldTrackDirectBlockChanges
        => !applyingReplayState
            && !ReplayBlockEntityStateCapturePatch.IsLiveBlockEntityPacket
            && !ReplayBlockEntityStateCapturePatch.IsReplayAnimationRedraw
            && ((recording is not null && CaptureWorld)
                || playback?.WorldPlayback is not null);

    public void QueueBlockChanged(BlockPos position)
    {
        if (!applyingReplayState
            && !ReplayBlockEntityStateCapturePatch.IsLiveBlockEntityPacket
            && !ReplayBlockEntityStateCapturePatch.IsReplayAnimationRedraw)
        {
            playback?.WorldPlayback?.ObserveLiveBlockChange(position);
        }

        DirectorReplayDocument? active = recording;
        if (active is null
            || applyingReplayState
            || !CaptureWorld
            || !WithinCapturedWorld(active, position))
        {
            return;
        }

        if (recordingPaused)
        {
            QueueBlockPosition(
                position.X,
                position.Y,
                position.Z,
                position.dimension,
                pausedBlockChanges);
            return;
        }

        QueueBlockPosition(
            position.X,
            position.Y,
            position.Z,
            position.dimension,
            pendingBlockChanges);
    }

    public void QueueBlockChanges(IReadOnlyList<BlockPos> positions)
    {
        for (int index = 0; index < positions.Count; index++)
        {
            QueueBlockChanged(positions[index]);
        }
    }

    internal void CaptureObservedBlockEntityState(BlockEntity blockEntity, bool baseline)
    {
        if (DirectorReplaySerialization.IsSerializingBlockEntity(blockEntity))
        {
            return;
        }

        BlockPos? position = blockEntity.Pos;
        if (applyingReplayState || position is null)
        {
            return;
        }

        DirectorReplayWorldPlayback? worldPlayback = playback?.WorldPlayback;
        if (worldPlayback is not null)
        {
            if (!baseline)
            {
                worldPlayback.ObserveLiveBlockEntityState(position);
            }
            return;
        }

        CachedBlockState observed;
        if (!TryGetObservedBlockEntityState(blockEntity, out observed))
        {
            observed = ReadCurrentBlockState(position);
            StoreObservedBlockEntityState(blockEntity, observed);
        }

        DirectorReplayDocument? active = recording;
        if (active is null
            || !CaptureWorld
            || !WithinObservedCaptureWorld(active, position))
        {
            if (!baseline)
            {
                StoreObservedBlockEntityState(blockEntity, ReadCurrentBlockState(position));
            }
            return;
        }

        BlockKey key = BlockKey.From(position);
        bool known = recordingBlockStates.ContainsKey(key)
            || TryGetSnapshotBlockState(key, out _);
        if (baseline)
        {
            if (!known)
            {
                RecordBlockEvent(active, position, 0d, observed);
            }
            return;
        }

        if (!known)
        {
            // A camera can reach loaded block entities outside the fixed
            // snapshot. Seed their replay baseline from the last state seen
            // before this transition, not from the already-mutated object.
            RecordBlockEvent(active, position, 0d, observed);
        }

        CachedBlockState current = ReadCurrentBlockState(position);
        StoreObservedBlockEntityState(blockEntity, current);
        if (recordingPaused)
        {
            QueueBlockPosition(
                position.X,
                position.Y,
                position.Z,
                position.dimension,
                pausedBlockChanges);
            return;
        }

        RecordBlockEvent(active, position, recordingElapsedSeconds, current);
    }

    internal void CaptureObservedBlockEntityPacket(BlockPos position)
    {
        if (applyingReplayState)
        {
            return;
        }

        playback?.WorldPlayback?.ObserveLiveBlockEntityPacket(position);
    }

    private bool TryGetObservedBlockEntityState(
        BlockEntity blockEntity,
        out CachedBlockState state)
    {
        lock (observedBlockEntitySync)
        {
            if (observedBlockEntityStates.TryGetValue(blockEntity, out ObservedBlockState? observed))
            {
                state = observed.State with { BlockEntityData = CloneBytes(observed.State.BlockEntityData) };
                return true;
            }
        }

        state = default;
        return false;
    }

    private void StoreObservedBlockEntityState(BlockEntity blockEntity, CachedBlockState state)
    {
        CachedBlockState snapshot = state with { BlockEntityData = CloneBytes(state.BlockEntityData) };
        lock (observedBlockEntitySync)
        {
            if (observedBlockEntityStates.TryGetValue(blockEntity, out ObservedBlockState? observed))
            {
                observed.State = snapshot;
            }
            else
            {
                observedBlockEntityStates.Add(blockEntity, new ObservedBlockState(snapshot));
            }
        }
    }

    private void QueueBlockNeighborhood(BlockPos position)
        => QueueBlockNeighborhood(position, pendingBlockChanges);

    private void QueueBlockNeighborhood(BlockPos position, HashSet<BlockKey> destination)
    {
        for (int dy = 0; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    QueueBlockPosition(
                        position.X + dx,
                        position.Y + dy,
                        position.Z + dz,
                        position.dimension,
                        destination);
                }
            }
        }

        QueueBlockPosition(position.X, position.Y - 1, position.Z, position.dimension, destination);
        QueueBlockPosition(position.X, position.Y + 2, position.Z, position.dimension, destination);
    }

    private void QueueBlockPosition(
        int x,
        int y,
        int z,
        int dimension,
        HashSet<BlockKey> destination)
    {
        if (y < 0 || y >= capi.World.MapSizeY)
        {
            return;
        }

        BlockPos position = new(x, y, z, dimension);
        if (recording is null || !WithinCapturedWorld(recording, position))
        {
            return;
        }

        destination.Add(BlockKey.From(position));
    }

    private void ProcessPausedBlockResync(int maximum)
    {
        DirectorReplayDocument? active = recording;
        if (active is null || maximum <= 0)
        {
            return;
        }

        int processed = 0;
        while (processed < maximum && pausedBlockResyncBatches.TryPeek(out PausedBlockResyncBatch? batch))
        {
            while (processed < maximum && batch.NextIndex < batch.Changes.Length)
            {
                CaptureBlockEvent(active, batch.Changes[batch.NextIndex++].ToBlockPos(), batch.TimeSeconds);
                processed++;
            }

            if (batch.NextIndex >= batch.Changes.Length)
            {
                pausedBlockResyncBatches.Dequeue();
            }
        }
    }

    private void FlushPendingBlockChanges(int maximum = int.MaxValue)
    {
        DirectorReplayDocument? active = recording;
        if (active is null || pendingBlockChanges.Count == 0 || maximum <= 0)
        {
            return;
        }

        BlockKey[] changes = maximum >= pendingBlockChanges.Count
            ? pendingBlockChanges.ToArray()
            : pendingBlockChanges.Take(maximum).ToArray();
        foreach (BlockKey key in changes)
        {
            pendingBlockChanges.Remove(key);
            CaptureBlockEvent(active, key.ToBlockPos(), recordingElapsedSeconds);
        }
    }

    private void CaptureBlockEntityChanges(double timeSeconds)
    {
        pendingBlockEntityCapture = null;
        QueueBlockEntityCapture(timeSeconds);
        while (pendingBlockEntityCapture is not null)
        {
            ProcessBlockEntityCapture(int.MaxValue);
        }
    }

    private void QueueBlockEntityCapture(double? timeSeconds = null)
    {
        DirectorReplayDocument? active = recording;
        if (pendingBlockEntityCapture is not null
            || active is null
            || !CaptureWorld
            || active.WorldSnapshot.Chunks.Count == 0)
        {
            return;
        }

        pendingBlockEntityCapture = new BlockEntityCaptureRound(
            active.WorldSnapshot.Chunks.ToArray(),
            timeSeconds ?? recordingElapsedSeconds);
    }

    private void ProcessBlockEntityCapture(int maximumChunks)
    {
        DirectorReplayDocument? active = recording;
        BlockEntityCaptureRound? capture = pendingBlockEntityCapture;
        if (active is null || capture is null || maximumChunks <= 0)
        {
            return;
        }

        int end = Math.Min(capture.Chunks.Length, capture.NextChunkIndex + maximumChunks);
        while (capture.NextChunkIndex < end)
        {
            DirectorReplayChunkSnapshot chunkSnapshot = capture.Chunks[capture.NextChunkIndex++];
            int internalChunkY = chunkSnapshot.ChunkY
                + active.WorldSnapshot.Dimension * GlobalConstants.DimensionSizeInChunks;
            IWorldChunk? chunk = game.WorldMap.GetChunk(
                chunkSnapshot.ChunkX,
                internalChunkY,
                chunkSnapshot.ChunkZ);
            if (chunk is null)
            {
                continue;
            }

            capture.ScannedChunks.Add(new ChunkKey(
                chunkSnapshot.ChunkX,
                chunkSnapshot.ChunkY,
                chunkSnapshot.ChunkZ,
                active.WorldSnapshot.Dimension));

            foreach ((BlockPos rawPosition, BlockEntity blockEntity) in chunk.BlockEntities)
            {
                BlockPos position = new(rawPosition.X, rawPosition.Y, rawPosition.Z, active.WorldSnapshot.Dimension);
                BlockKey key = BlockKey.From(position);
                capture.Seen.Add(key);
                try
                {
                    string className = capi.ClassRegistry.GetBlockEntityClass(blockEntity.GetType()) ?? string.Empty;
                    byte[] data = DirectorReplaySerialization.SerializeBlockEntity(blockEntity);
                    if (!recordingBlockEntities.TryGetValue(key, out CachedBlockEntity cached)
                        || !string.Equals(cached.ClassName, className, StringComparison.Ordinal)
                        || !cached.Data.AsSpan().SequenceEqual(data))
                    {
                        CaptureBlockEvent(active, position, capture.TimeSeconds);
                    }
                }
                catch (Exception exception)
                {
                    capi.Logger.Debug("VS Director: block entity update could not be captured: {0}", exception.Message);
                }
            }
        }

        if (capture.NextChunkIndex < capture.Chunks.Length)
        {
            return;
        }

        foreach (BlockKey removed in recordingBlockEntities.Keys.Where(key =>
            capture.ScannedChunks.Contains(ChunkKey.FromBlock(key)) && !capture.Seen.Contains(key)).ToArray())
        {
            CaptureBlockEvent(active, removed.ToBlockPos(), capture.TimeSeconds);
        }

        pendingBlockEntityCapture = null;
    }

    private void CaptureBlockEvent(DirectorReplayDocument active, BlockPos position, double timeSeconds)
    {
        RecordBlockEvent(active, position, timeSeconds, ReadCurrentBlockState(position));
    }

    private CachedBlockState ReadCurrentBlockState(BlockPos position)
    {
        Block solid = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid);
        Block fluid = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Fluid);
        BlockEntity? blockEntity = capi.World.BlockAccessor.GetBlockEntity(position);
        string blockEntityClass = string.Empty;
        byte[] blockEntityData = Array.Empty<byte>();
        if (blockEntity is not null)
        {
            try
            {
                blockEntityClass = capi.ClassRegistry.GetBlockEntityClass(blockEntity.GetType()) ?? string.Empty;
                blockEntityData = DirectorReplaySerialization.SerializeBlockEntity(blockEntity);
            }
            catch (Exception exception)
            {
                capi.Logger.Debug("VS Director: block entity event data could not be captured: {0}", exception.Message);
            }
        }

        return new CachedBlockState(
            solid.BlockId,
            fluid.BlockId,
            blockEntityClass,
            blockEntityData,
            HasBlockEntityState: true);
    }

    private void RecordBlockEvent(
        DirectorReplayDocument active,
        BlockPos position,
        double timeSeconds,
        CachedBlockState current)
    {
        BlockKey key = BlockKey.From(position);
        bool hasPrevious = recordingBlockStates.TryGetValue(key, out CachedBlockState previous)
            || TryGetSnapshotBlockState(key, out previous);
        if (hasPrevious && DirectorReplayBlockStateComparison.IsEquivalent(
            previous.SolidBlockId,
            previous.FluidBlockId,
            previous.HasBlockEntityState,
            previous.BlockEntityClass,
            previous.BlockEntityData,
            current.SolidBlockId,
            current.FluidBlockId,
            current.BlockEntityClass,
            current.BlockEntityData))
        {
            return;
        }

        recordingBlockStates[key] = new CachedBlockState(
            current.SolidBlockId,
            current.FluidBlockId,
            current.BlockEntityClass,
            CloneBytes(current.BlockEntityData),
            HasBlockEntityState: true);
        if (!CanCaptureEvent(active))
        {
            return;
        }

        DirectorReplayBlockEvent change = new()
        {
            TimeSeconds = timeSeconds,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Dimension = position.dimension,
            SolidBlockId = current.SolidBlockId,
            FluidBlockId = current.FluidBlockId,
            BlockEntityCaptured = true
        };
        if (string.IsNullOrWhiteSpace(current.BlockEntityClass))
        {
            recordingBlockEntities.Remove(key);
        }
        else
        {
            change.BlockEntityClass = current.BlockEntityClass;
            change.BlockEntityData = CloneBytes(current.BlockEntityData);
            recordingBlockEntities[key] = new CachedBlockEntity(
                change.BlockEntityClass,
                CloneBytes(change.BlockEntityData));
        }

        active.BlockEvents.Add(change);
    }

    private bool TryGetSnapshotBlockState(BlockKey key, out CachedBlockState state)
    {
        state = default;
        ChunkKey chunkKey = ChunkKey.FromBlock(key);
        if (!recordingSnapshotChunks.TryGetValue(chunkKey, out DirectorReplayChunkSnapshot? chunk))
        {
            return false;
        }

        if (decodedSnapshotChunkKey != chunkKey
            || decodedSnapshotSolid is null
            || decodedSnapshotFluid is null)
        {
            int blockCount = GlobalConstants.ChunkSize
                * GlobalConstants.ChunkSize
                * GlobalConstants.ChunkSize;
            decodedSnapshotSolid = DirectorReplayChunkCodec.Decode(chunk.SolidRuns, blockCount);
            decodedSnapshotFluid = DirectorReplayChunkCodec.Decode(chunk.FluidRuns, blockCount);
            decodedSnapshotChunkKey = chunkKey;
        }

        int localX = key.X - chunkKey.X * GlobalConstants.ChunkSize;
        int localY = key.Y - chunkKey.Y * GlobalConstants.ChunkSize;
        int localZ = key.Z - chunkKey.Z * GlobalConstants.ChunkSize;
        int index = localX
            + localZ * GlobalConstants.ChunkSize
            + localY * GlobalConstants.ChunkSize * GlobalConstants.ChunkSize;
        if ((uint)index >= (uint)decodedSnapshotSolid.Length
            || (uint)index >= (uint)decodedSnapshotFluid.Length)
        {
            return false;
        }

        CachedBlockEntity blockEntity = recordingBlockEntities.TryGetValue(key, out CachedBlockEntity cached)
            ? cached
            : new CachedBlockEntity(string.Empty, Array.Empty<byte>());
        state = new CachedBlockState(
            decodedSnapshotSolid[index],
            decodedSnapshotFluid[index],
            blockEntity.ClassName,
            CloneBytes(blockEntity.Data),
            HasBlockEntityState: true);
        return true;
    }

    private bool WithinCapturedWorld(DirectorReplayDocument document, BlockPos position)
    {
        DirectorReplayWorldSnapshot snapshot = document.WorldSnapshot;
        if (snapshot.Chunks.Count == 0 || position.dimension != snapshot.Dimension)
        {
            return false;
        }

        int chunkX = (int)Math.Floor(position.X / (double)GlobalConstants.ChunkSize);
        int chunkZ = (int)Math.Floor(position.Z / (double)GlobalConstants.ChunkSize);
        return Math.Abs(chunkX - snapshot.CenterChunkX) <= snapshot.RadiusChunks
            && Math.Abs(chunkZ - snapshot.CenterChunkZ) <= snapshot.RadiusChunks;
    }

    private bool WithinObservedCaptureWorld(DirectorReplayDocument document, BlockPos position)
    {
        if (position.dimension != document.WorldSnapshot.Dimension)
        {
            return false;
        }
        if (WithinCapturedWorld(document, position))
        {
            return true;
        }

        int radius = Math.Clamp(config.ReplaySnapshotRadiusChunks, 0, 16);
        if (capi.World.Player?.Entity is { } player
            && WithinChunkRadius(position, player.Pos.X, player.Pos.Z, player.Pos.Dimension, radius))
        {
            return true;
        }

        CameraPreset? camera = controller.CurrentPose;
        return camera is not null
            && WithinChunkRadius(position, camera.X, camera.Z, camera.Dimension, radius);
    }

    private static bool WithinChunkRadius(
        BlockPos position,
        double centerX,
        double centerZ,
        int centerDimension,
        int radius)
    {
        if (position.dimension != centerDimension
            || !double.IsFinite(centerX)
            || !double.IsFinite(centerZ))
        {
            return false;
        }

        int chunkX = (int)Math.Floor(position.X / (double)GlobalConstants.ChunkSize);
        int chunkZ = (int)Math.Floor(position.Z / (double)GlobalConstants.ChunkSize);
        int centerChunkX = (int)Math.Floor(centerX / GlobalConstants.ChunkSize);
        int centerChunkZ = (int)Math.Floor(centerZ / GlobalConstants.ChunkSize);
        return Math.Abs(chunkX - centerChunkX) <= radius
            && Math.Abs(chunkZ - centerChunkZ) <= radius;
    }

    private Entity? ResolveLiveActor(DirectorReplayActorTrack actor)
    {
        if (!string.IsNullOrWhiteSpace(actor.PlayerUid))
        {
            Entity? playerEntity = capi.World.PlayerByUid(actor.PlayerUid)?.Entity
                ?? capi.World.AllOnlinePlayers.FirstOrDefault(player => string.Equals(
                    player.PlayerUID,
                    actor.PlayerUid,
                    StringComparison.Ordinal))?.Entity;
            if (playerEntity is not null)
            {
                return playerEntity;
            }
        }

        Entity? entity = actor.EntityId > 0 ? capi.World.GetEntityById(actor.EntityId) : null;
        if (entity is null || string.IsNullOrWhiteSpace(actor.EntityCode))
        {
            return entity;
        }

        return string.Equals(entity.Code?.ToShortString(), actor.EntityCode, StringComparison.Ordinal)
            || string.Equals(entity.Code?.ToString(), actor.EntityCode, StringComparison.Ordinal)
                ? entity
                : null;
    }

    private Entity? TryCreateGhost(DirectorReplayActorTrack actor, Entity? liveEntity)
    {
        byte[] state = actor.InitialState.Length > 0
            ? actor.InitialState
            : actor.States.FirstOrDefault()?.Data ?? Array.Empty<byte>();
        if (state.Length == 0 && liveEntity is not null)
        {
            state = TrySerializeEntity(
                liveEntity,
                actor.ReplayEntityId,
                useThirdPersonPlayerAnimations: true);
        }

        if (state.Length == 0 || string.IsNullOrWhiteSpace(actor.EntityCode) || actor.ReplayEntityId >= 0)
        {
            return null;
        }

        Entity? ghost = null;
        long replayEntityId = actor.ReplayEntityId;
        try
        {
            SetApplyingReplayState(true);
            // Register before packet construction: entity creation can query
            // AnimManager or tessellate before it returns the new instance.
            replayGhostIds.Add(replayEntityId);
            Packet_Entity packet = new()
            {
                EntityType = actor.EntityCode,
                EntityId = replayEntityId,
                SimulationRange = actor.SimulationRange,
                Data = CloneBytes(state)
            };
            ghost = ClientSystemEntities.createOrUpdateEntityFromPacket(packet, game);
            if (ghost is null)
            {
                replayGhostIds.Remove(replayEntityId);
                return null;
            }

            DetachReplayMount(ghost);
            ClearReplaySeatPassengers(ghost);
            game.LoadedEntities[ghost.EntityId] = ghost;
            replayGhostIds.Add(ghost.EntityId);
            replayThirdPersonIds.Add(ghost.EntityId);
            ghost.State = EnumEntityState.Inactive;
            ghost.IdleSoundChanceModifier = 0f;
            if (ghost is EntityPlayer ghostPlayer)
            {
                ConfigureReplayPlayer(ghostPlayer, markShapeModified: false);
            }
            CreateGhostRenderer(ghost);
            DetachReplayMount(ghost);
            ClearReplaySeatPassengers(ghost);
            if (ghost is EntityPlayer loadedGhostPlayer)
            {
                ConfigureReplayPlayer(loadedGhostPlayer, markShapeModified: true);
            }
            return ghost;
        }
        catch (Exception exception)
        {
            if (ghost is not null)
            {
                RemoveGhost(ghost);
            }
            replayGhostIds.Remove(replayEntityId);
            capi.Logger.Warning("VS Director: could not create replay ghost '{0}': {1}", actor.DisplayName, exception.Message);
            return null;
        }
        finally
        {
            SetApplyingReplayState(false);
        }
    }

    private void CreateGhostRenderer(Entity entity)
    {
        string? rendererName = entity.Properties?.Client?.RendererName;
        IDictionary? renderers = EntityRenderersField?.GetValue(game) as IDictionary;
        if (string.IsNullOrWhiteSpace(rendererName) || renderers?.Contains(entity.EntityId) == true)
        {
            entity.OnEntityLoaded();
            return;
        }

        if (!ClientMain.ClassRegistry.EntityRendererClassNameToTypeMapping.ContainsKey(rendererName))
        {
            throw new InvalidOperationException($"No renderer is registered for replay entity '{rendererName}'.");
        }

        if (entity.Properties?.Client is null || renderers is null)
        {
            throw new InvalidOperationException("The client entity renderer registry is unavailable.");
        }

        entity.Properties.Client.Renderer = ClientMain.ClassRegistry.CreateEntityRenderer(rendererName, entity, capi);
        renderers.Add(entity.EntityId, entity.Properties.Client.Renderer);
        entity.OnEntityLoaded();
    }

    private void RemoveGhost(Entity entity)
    {
        replayGhostIds.Remove(entity.EntityId);
        replayThirdPersonIds.Remove(entity.EntityId);
        try
        {
            DespawnReplayEntity(entity);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: replay ghost despawn callback failed: {0}", exception.Message);
        }

        try
        {
            game.WorldMap.GetChunk(entity.InChunkIndex3d)?.RemoveEntity(entity.EntityId);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: replay ghost chunk removal failed: {0}", exception.Message);
        }

        try
        {
            IDictionary? renderers = EntityRenderersField?.GetValue(game) as IDictionary;
            if (renderers?.Contains(entity.EntityId) == true)
            {
                game.RemoveEntityRenderer(entity);
            }
            else if (entity.Properties?.Client?.Renderer is { } detachedRenderer)
            {
                detachedRenderer.Dispose();
                entity.Properties.Client.Renderer = null;
            }
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: replay ghost renderer removal failed: {0}", exception.Message);
        }

        game.LoadedEntities.Remove(entity.EntityId);
    }

    private static void DespawnReplayEntity(Entity entity)
    {
        using IDisposable textureGuard = ReplayClothingTextureGuard.SuppressUnallocatedTextureRelease(
            entity.Properties?.Client?.Behaviors);
        entity.OnEntityDespawn(new EntityDespawnData { Reason = EnumDespawnReason.Removed });
    }

    private DirectorReplayActorSample CaptureSample(Entity entity, double timeSeconds)
    {
        EntityPlayer? player = entity as EntityPlayer;
        List<DirectorReplayAnimationSample> animationStates = player is not null
            ? CaptureAnimationStatesFromManagers(
                player.AnimManager,
                player.TpAnimManager,
                normalizePlayerCodes: true)
            : CaptureAnimationStates(
                entity.AnimManager,
                normalizePlayerCodes: false);
        List<string> animations = animationStates.Select(state => state.Code).ToList();
        EntityAgent? agent = entity as EntityAgent;
        ReplayRenderedPose? renderedPose = renderedCapturePoses.TryGetValue(entity.EntityId, out ReplayRenderedPose? pose)
            ? pose
            : null;
        bool hasRecordedThirdPersonPose = player is not null
            && ShouldCaptureRecordedThirdPersonPose(player);
        float capturedHeadYaw = renderedPose?.HeadYaw ?? entity.Pos.HeadYaw;
        float capturedHeadPitch = renderedPose?.HeadPitch ?? entity.Pos.HeadPitch;
        float capturedBodyYaw = renderedPose?.BodyYaw ?? agent?.BodyYaw ?? entity.Pos.Yaw;
        DirectorReplayActorSample sample = new()
        {
            TimeSeconds = timeSeconds,
            X = renderedPose?.X ?? entity.Pos.X,
            Y = renderedPose?.Y ?? entity.Pos.Y,
            Z = renderedPose?.Z ?? entity.Pos.Z,
            Dimension = renderedPose?.Dimension ?? entity.Pos.Dimension,
            Yaw = renderedPose?.Yaw ?? entity.Pos.Yaw,
            Pitch = renderedPose?.Pitch ?? entity.Pos.Pitch,
            Roll = renderedPose?.Roll ?? entity.Pos.Roll,
            HeadYaw = capturedHeadYaw,
            HeadPitch = capturedHeadPitch,
            BodyYaw = capturedBodyYaw,
            HasThirdPersonPose = hasRecordedThirdPersonPose,
            ThirdPersonBodyYaw = hasRecordedThirdPersonPose ? capturedBodyYaw : 0f,
            ThirdPersonHeadYaw = hasRecordedThirdPersonPose ? capturedHeadYaw : 0f,
            ThirdPersonHeadPitch = hasRecordedThirdPersonPose ? capturedHeadPitch : 0f,
            HasMotion = true,
            MotionX = entity.Pos.Motion.X,
            MotionY = entity.Pos.Motion.Y,
            MotionZ = entity.Pos.Motion.Z,
            HasRenderState = true,
            OnGround = entity.OnGround,
            FeetInLiquid = entity.FeetInLiquid,
            Swimming = entity.Swimming,
            ClimbingOnFace = entity.ClimbingOnFace?.Code ?? string.Empty,
            LocalEyePosX = entity.LocalEyePos.X,
            LocalEyePosY = entity.LocalEyePos.Y,
            LocalEyePosZ = entity.LocalEyePos.Z,
            SelectionBoxY2 = entity.SelectionBox.Y2,
            CollisionBoxY2 = entity.CollisionBox.Y2,
            HasAgentState = agent is not null,
            BodyYawServer = agent?.BodyYawServer ?? 0f,
            HasPlayerState = player is not null,
            HasPlayerPostureState = player is not null,
            CanStandUp = player?.PrevFrameCanStandUp ?? true,
            Controls = agent?.Controls.ToInt() ?? 0,
            ServerControls = agent?.ServerControls.ToInt() ?? 0,
            HandUse = (int)(player?.Controls.HandUse ?? EnumHandInteract.None),
            ServerHandUse = (int)(player?.ServerControls.HandUse ?? EnumHandInteract.None),
            WalkYaw = player?.WalkYaw ?? 0f,
            WalkPitch = player?.WalkPitch ?? 0f,
            RightHandItem = SerializeItemStack(player?.RightHandItemSlot?.Itemstack),
            LeftHandItem = SerializeItemStack(player?.LeftHandItemSlot?.Itemstack),
            EquipmentItems = SerializeInventory(
                player?.Player?.InventoryManager.GetOwnInventory("character")),
            BackpackItems = SerializeInventory(
                player?.Player?.InventoryManager.GetOwnInventory("backpack")),
            Animations = animations,
            AnimationStates = animationStates
        };
        CaptureItemRenderState(entity, sample);
        return sample;
    }

    private bool ShouldCaptureRecordedThirdPersonPose(EntityPlayer player)
    {
        IClientPlayer? currentPlayer = capi.World.Player;
        EntityPlayer? localPlayer = currentPlayer?.Entity;
        bool isLocalPlayer = ReferenceEquals(player, localPlayer)
            || localPlayer?.EntityId == player.EntityId;
        return !isLocalPlayer
            || currentPlayer?.CameraMode != EnumCameraMode.FirstPerson
            || ReplayPlayerRenderMode.UseThirdPerson(player);
    }

    private static List<DirectorReplayAnimationSample> CaptureAnimationStatesFromManagers(
        IAnimationManager? primaryManager,
        IAnimationManager? thirdPersonManager,
        bool normalizePlayerCodes)
    {
        List<DirectorReplayAnimationSample> primaryStates = CaptureAnimationStates(
            primaryManager,
            normalizePlayerCodes);
        if (thirdPersonManager is null || ReferenceEquals(primaryManager, thirdPersonManager))
        {
            return primaryStates;
        }

        List<DirectorReplayAnimationSample> thirdPersonStates = CaptureAnimationStates(
            thirdPersonManager,
            normalizePlayerCodes);
        return MergeAnimationCaptureStates(primaryStates, thirdPersonStates);
    }

    private static List<DirectorReplayAnimationSample> MergeAnimationCaptureStates(
        List<DirectorReplayAnimationSample> primaryStates,
        List<DirectorReplayAnimationSample> thirdPersonStates)
    {
        if (primaryStates.Count == 0)
        {
            return thirdPersonStates;
        }
        if (thirdPersonStates.Count == 0)
        {
            // A local first-person manager can contain layers that have no
            // third-person counterpart at all. Importing those layers would
            // recreate the exact rigid FP pose this capture path is meant to
            // remove. The replay player can rebuild locomotion from controls;
            // an FP-only action is intentionally omitted from the observer
            // view instead of being applied to TpAnimManager.
            return new List<DirectorReplayAnimationSample>();
        }

        // The local player's active manager follows the live camera mode. In
        // first person it carries the advancing -fp/-ifp layers. The TP
        // manager is the authoritative source for replayable full-body layers
        // and render metadata. Preserve active-manager timing only for an
        // identity that the TP manager also owns; an FP-only layer must never
        // be remapped onto the third-person replay actor.
        Dictionary<string, DirectorReplayAnimationSample> primaryByIdentity = primaryStates
            .ToDictionary(
                AnimationCaptureIdentity,
                state => state,
                StringComparer.OrdinalIgnoreCase);
        foreach (DirectorReplayAnimationSample state in thirdPersonStates)
        {
            string identity = AnimationCaptureIdentity(state);
            if (primaryByIdentity.TryGetValue(identity, out DirectorReplayAnimationSample? primary))
            {
                state.Progress = primary.Progress;
                state.AnimationSpeed = primary.AnimationSpeed;
                state.EasingFactor = primary.EasingFactor;
                state.BlendedWeight = primary.BlendedWeight;
                state.Iterations = primary.Iterations;
                state.IsActive = primary.IsActive;
                state.IsRunning = primary.IsRunning;
                state.NowPlaying = primary.NowPlaying;
                state.ShouldRewind = primary.ShouldRewind;
                state.ShouldPlayTillEnd = primary.ShouldPlayTillEnd;
            }
        }
        return thirdPersonStates;
    }

    private static string AnimationCaptureIdentity(DirectorReplayAnimationSample state)
        => state.Code + "\n" + AnimationStateKey(state);

    private void CaptureItemRenderState(Entity entity, DirectorReplayActorSample sample)
    {
        if (entity is not EntityItem
            || !TryGetEntityRenderer(entity.EntityId, out object? renderer)
            || renderer is null)
        {
            return;
        }

        Type rendererType = renderer.GetType();
        if (!TryGetRendererField(rendererType, renderer, "touchGroundMS", out long touchGroundMs)
            || !TryGetRendererField(rendererType, renderer, "scaleRand", out float scaleRand)
            || !TryGetRendererField(rendererType, renderer, "yRotRand", out float yawRandom)
            || !TryGetRendererField(rendererType, renderer, "xangle", out float xAngle)
            || !TryGetRendererField(rendererType, renderer, "yangle", out float yAngle)
            || !TryGetRendererField(rendererType, renderer, "zangle", out float zAngle)
            || !TryGetRendererField(rendererType, renderer, "accum", out float accumulator))
        {
            return;
        }

        long elapsedMilliseconds = capi.World.ElapsedMilliseconds;
        sample.HasItemRenderState = true;
        sample.ItemRenderElapsedMilliseconds = Math.Max(0L, elapsedMilliseconds);
        sample.ItemTouchGroundAgeMilliseconds = Math.Clamp(
            elapsedMilliseconds - touchGroundMs,
            0L,
            86_400_000L);
        sample.ItemScaleRandom = scaleRand;
        sample.ItemYawRandom = yawRandom;
        sample.ItemRenderXAngle = xAngle;
        sample.ItemRenderYAngle = yAngle;
        sample.ItemRenderZAngle = zAngle;
        sample.ItemRenderAccumulator = accumulator;
    }

    private static byte[] SerializeItemStack(ItemStack? stack)
    {
        try
        {
            return stack?.ToBytes() ?? Array.Empty<byte>();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static List<DirectorReplayAnimationSample> CaptureAnimationStates(
        IAnimationManager? manager,
        bool normalizePlayerCodes)
    {
        if (manager is null)
        {
            return new List<DirectorReplayAnimationSample>();
        }

        KeyValuePair<string, AnimationMetaData>[] activeAnimations;
        try
        {
            activeAnimations = manager.ActiveAnimationsByAnimCode?.ToArray()
                ?? Array.Empty<KeyValuePair<string, AnimationMetaData>>();
        }
        catch (Exception)
        {
            // The active dictionary can be replaced while an entity renderer
            // initializes. Keep the animator fallback below available so a
            // transient dictionary read cannot create an empty replay frame.
            activeAnimations = Array.Empty<KeyValuePair<string, AnimationMetaData>>();
        }

        int runningCapacity = manager.Animator?.Animations?.Length ?? 0;
        List<DirectorReplayAnimationSample> result = new(activeAnimations.Length + runningCapacity);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourceCode, AnimationMetaData metadata) in activeAnimations
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            RunningAnimation? running = TryGetRunningAnimation(manager, sourceCode);
            AddCapturedAnimationState(
                result,
                seen,
                sourceCode,
                metadata,
                running,
                normalizePlayerCodes);
        }

        if (manager.Animator?.Animations is { } runningAnimations)
        {
            foreach (RunningAnimation running in runningAnimations)
            {
                if (running.Animation is null
                    || (!running.Running && !running.Active))
                {
                    continue;
                }

                string stateCode = !string.IsNullOrWhiteSpace(running.meta?.Animation)
                    ? running.meta.Animation
                    : running.Animation.Code;
                AddCapturedAnimationState(
                    result,
                    seen,
                    stateCode,
                    running.meta,
                    running,
                    normalizePlayerCodes);
            }
        }
        return result;
    }

    private static void AddCapturedAnimationState(
        List<DirectorReplayAnimationSample> result,
        HashSet<string> seen,
        string sourceCode,
        AnimationMetaData? metadata,
        RunningAnimation? running,
        bool normalizePlayerCodes)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return;
        }

        string code = string.IsNullOrWhiteSpace(metadata?.Code)
            ? sourceCode
            : metadata.Code;
        string stateCode = sourceCode;
        if (normalizePlayerCodes)
        {
            code = DirectorReplayMath.NormalizePlayerAnimationCode(code);
            stateCode = DirectorReplayMath.NormalizePlayerAnimationCode(stateCode);
        }
        string stateIdentity = code + "\n" + stateCode;
        if (code.Length == 0 || !seen.Add(stateIdentity))
        {
            return;
        }

        float progress = running?.AnimProgress ?? 0f;
        result.Add(new DirectorReplayAnimationSample
        {
            Code = code,
            StateCode = stateCode,
            Progress = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 0f,
            AnimationSpeed = metadata is not null && float.IsFinite(metadata.AnimationSpeed)
                ? metadata.AnimationSpeed
                : 1f,
            EasingFactor = running is not null && float.IsFinite(running.EasingFactor)
                ? Math.Clamp(running.EasingFactor, 0f, 1f)
                : 1f,
            BlendedWeight = running is not null && float.IsFinite(running.BlendedWeight)
                ? Math.Max(0f, running.BlendedWeight)
                : 1f,
            Iterations = Math.Clamp(running?.Iterations ?? 0, -1_000_000, 1_000_000),
            IsActive = running?.Active ?? true,
            IsRunning = running?.Running ?? true,
            NowPlaying = running?.NowPlaying ?? false,
            ShouldRewind = running?.ShouldRewind ?? false,
            ShouldPlayTillEnd = running?.ShouldPlayTillEnd ?? false,
            Weight = metadata is not null && float.IsFinite(metadata.Weight)
                ? Math.Max(0f, metadata.Weight)
                : 1f,
            EaseInSpeed = metadata is not null && float.IsFinite(metadata.EaseInSpeed)
                ? Math.Max(0f, metadata.EaseInSpeed)
                : 10f,
            EaseOutSpeed = metadata is not null && float.IsFinite(metadata.EaseOutSpeed)
                ? Math.Max(0f, metadata.EaseOutSpeed)
                : 10f,
            BlendMode = metadata is null ? 0 : (int)metadata.BlendMode,
            ElementWeights = metadata?.ElementWeight is null
                ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(metadata.ElementWeight, StringComparer.OrdinalIgnoreCase),
            ElementBlendModes = metadata?.ElementBlendMode is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : metadata.ElementBlendMode.ToDictionary(
                    item => item.Key,
                    item => (int)item.Value,
                    StringComparer.OrdinalIgnoreCase)
        });
    }

    private static RunningAnimation? TryGetRunningAnimation(IAnimationManager manager, string animationCode)
    {
        RunningAnimation? direct = null;
        try
        {
            direct = manager.Animator is null ? null : manager.GetAnimationState(animationCode);
        }
        catch (Exception)
        {
        }

        if (direct is not null
            && (direct.Running
                || direct.Active
                || direct.EasingFactor > 0.0001f
                || direct.BlendedWeight > 0.0001f))
        {
            return direct;
        }

        AnimationMetaData? activeMetadata = null;
        try
        {
            manager.ActiveAnimationsByAnimCode?.TryGetValue(animationCode, out activeMetadata);
        }
        catch (Exception)
        {
        }

        RunningAnimation? fallback = direct;
        if (manager.Animator?.Animations is not { } runningAnimations)
        {
            return fallback;
        }
        foreach (RunningAnimation running in runningAnimations)
        {
            if (running.Animation is null
                || (!AnimationIdentityMatches(running, animationCode)
                    && !AnimationIdentityMatches(running, activeMetadata?.Animation)
                    && !AnimationIdentityMatches(running, activeMetadata?.Code)))
            {
                continue;
            }

            fallback ??= running;
            if (running.Running
                || running.Active
                || running.EasingFactor > 0.0001f
                || running.BlendedWeight > 0.0001f)
            {
                return running;
            }
        }
        return fallback;
    }

    private static bool AnimationIdentityMatches(RunningAnimation running, string? identity)
        => !string.IsNullOrWhiteSpace(identity)
            && (string.Equals(running.Animation?.Code, identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(running.meta?.Animation, identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(running.meta?.Code, identity, StringComparison.OrdinalIgnoreCase));

    private void ApplyPlaybackFrame(Playback active)
    {
        long updateStarted = Stopwatch.GetTimestamp();
        double audioWorkMs = 0;
        double timelineTimeSeconds = active.TimeSeconds;
        DirectorReplayElementFreezeState freeze = DirectorReplayElementTimeline.SampleFreeze(
            active.Document.ViewportTimeline,
            timelineTimeSeconds);
        bool resumedFromFreeze = active.ElementFreezeActive && !freeze.Frozen;
        active.ElementFreezeActive = freeze.Frozen;
        active.ElementFullyFrozen = freeze.FullyFrozen;
        if (resumedFromFreeze)
        {
            // The replay content catches up to the logical timeline in one
            // step. Rebuild transient state instead of emitting every event
            // skipped while the content was frozen.
            active.TimedEventSeekPending = true;
        }
        active.TimeSeconds = freeze.ReplayTimeSeconds;
        active.PreparedTimeSeconds = active.TimeSeconds;
        replayAllowsOffscreenActorRendering = false;
        long animationFrame = ++replayAnimationFrame;
        double previousTime = active.AppliedTimeSeconds;
        bool backwards = active.TimeSeconds + TimeEpsilon < previousTime;
        bool resetTimedEvents = backwards || active.TimedEventSeekPending;
        float replayFrameDeltaSeconds = !resetTimedEvents
            && previousTime >= 0d
            && active.TimeSeconds > previousTime + TimeEpsilon
                ? (float)(active.TimeSeconds - previousTime)
                : 0f;
        if (resetTimedEvents)
        {
            ClearReplayParticles();
        }
        SetApplyingReplayState(true);
        try
        {
            if (active.WorldPlayback is not null)
            {
                active.WorldPlayback.ApplyAt(active.TimeSeconds, resetTimedEvents);
                if (active.WorldPlayback.IsBusy)
                {
                    if (active.WorldPlayback.IsSynchronizing)
                    {
                        HideReplayActors(active);
                    }
                    active.WorldPlayback.Advance(config.ReplayWorldOperationsPerFrame);
                    if (active.WorldPlayback.IsSynchronizing)
                    {
                        return;
                    }
                }
            }
            if (resetTimedEvents || Math.Abs(active.TimeSeconds - previousTime) > TimeEpsilon) ApplyClock(active);
            visualEnvironment.Apply(active.Document.ViewportTimeline.Visuals);
            DirectorReplayVisualRuntime.ReplaySpeed = PlaybackSpeed;
            ActionFrameDecorator?.Invoke(active.TimeSeconds);
            CameraPreset? camera = controller.IsActive ? controller.CurrentPose : null;
            double cameraX = camera?.X ?? game.MainCamera.CameraEyePos.X;
            double cameraZ = camera?.Z ?? game.MainCamera.CameraEyePos.Z;
            int cameraDimension = camera?.Dimension ?? game.EntityPlayer.Pos.Dimension;
            double maxDistanceSquared = ClientSettings.ViewDistance * ClientSettings.ViewDistance;
            bool actionDecoratorsActive = ActionDecoratorsActive?.Invoke()
                ?? (ActionSampleDecorator is not null || ActionEntityDecorator is not null);
            bool requiresFullActorSampling = RequiresFullActorSampling?.Invoke()
                ?? actionDecoratorsActive;
            replayAllowsOffscreenActorRendering = requiresFullActorSampling;
            int bindingCount = active.Bindings.Count;
            int animationStart = bindingCount == 0
                ? 0
                : active.AnimationUpdateCursor % bindingCount;
            bool dedicatedActorScheduling = dedicatedReplayRenderer.IsActive;
            if (dedicatedActorScheduling)
            {
                PrepareDedicatedAnimationSchedule(
                    active,
                    animationFrame,
                    animationStart,
                    resetTimedEvents,
                    requiresFullActorSampling,
                    cameraX,
                    cameraZ,
                    cameraDimension,
                    maxDistanceSquared);
            }
            else
            {
                scheduledReplayAnimationUpdates.Clear();
            }
            int animationUpdates = 0;
            long dedicatedAnimationWorkTicks = 0L;
            for (int bindingOffset = 0; bindingOffset < bindingCount; bindingOffset++)
            {
                int bindingIndex = (animationStart + bindingOffset) % bindingCount;
                ActorBinding binding = active.Bindings[bindingIndex];
                bool animatorReplaced = false;
                if (resetTimedEvents)
                {
                    binding.FullStateRestorePending = true;
                    binding.LastAppliedState = null;
                    if (dedicatedActorScheduling
                        && !ReferenceEquals(binding.Entity, game.EntityPlayer))
                    {
                        if (!binding.DedicatedAnimationWarmupFallbackActive
                            || binding.Entity.AnimManager?.Animator is not null)
                        {
                            binding.DedicatedAnimationWarmupFallbackActive = false;
                            binding.DedicatedAnimationPoseReady = false;
                            ResetDedicatedReplayAnimationWarmup(binding);
                        }
                    }
                }
                bool trackVisible = DirectorReplayMath.IsActorVisible(
                    binding.Track,
                    active.TimeSeconds);
                bool visualAllowed = trackVisible
                    && IsEntityAllowedByVisuals(binding.Track, binding.Entity);
                if (!trackVisible)
                {
                    if (binding.WasVisible
                        && binding.Entity.AnimManager is { } hiddenAnimationManager)
                    {
                        ResetReplayAnimations(binding, hiddenAnimationManager);
                    }
                    ParkReplayEntity(binding.Entity);
                    binding.WasVisible = false;
                    binding.WasInCurrentView = false;
                    binding.DedicatedAnimationPoseReady = true;
                    ResetDedicatedReplayAnimationWarmup(binding);
                    binding.LastAppliedState = null;
                    binding.FullStateRestorePending = true;
                    if (binding.LastAnimations.Count > 0)
                    {
                        binding.LastAnimations.Clear();
                    }
                    continue;
                }

                bool visible = visualAllowed;
                bool becameVisible = !binding.WasVisible;
                bool resetRendererState = becameVisible || resetTimedEvents;

                DirectorReplayActorPosition position = default;
                bool requiresActorPoseSampling = requiresFullActorSampling
                    || RequiresActorPoseSampling?.Invoke(
                        active.Document.ViewportTimeline,
                        binding.Track,
                        active.TimeSeconds) == true;
                bool hasSampledPosition = !requiresActorPoseSampling
                    && dedicatedActorScheduling
                    && binding.ScheduledPositionFrame == animationFrame
                    && binding.HasScheduledPosition;
                if (hasSampledPosition)
                {
                    position = binding.ScheduledPosition;
                }
                else if (!requiresActorPoseSampling)
                {
                    hasSampledPosition = DirectorReplayMath.TrySamplePosition(
                        binding.Track,
                        active.TimeSeconds,
                        interpolate: true,
                        out position);
                }

                if (hasSampledPosition)
                {
                    ApplyReplayPosition(binding.Entity, position);
                    if (!IsInCurrentReplayView(
                            binding.Entity,
                            cameraX,
                            cameraZ,
                            cameraDimension,
                            maxDistanceSquared))
                    {
                        SetReplayEntityVisibility(binding.Entity, false);
                        binding.WasInCurrentView = false;
                        binding.DedicatedAnimationMainViewVisible = false;
                        binding.DedicatedAnimationViewEligible = false;
                        binding.LastAppliedState = null;
                        binding.FullStateRestorePending = true;
                        if (becameVisible || backwards)
                        {
                            ResetItemPlaybackAnimation(binding.Entity);
                        }
                        binding.WasVisible = true;
                        continue;
                    }
                }

                DirectorReplayActorState? state = !binding.FullStateRestorePending ? binding.LastAppliedState : dedicatedActorScheduling
                    && binding.ScheduledStateFrame == animationFrame
                        ? binding.ScheduledState
                        : DirectorReplayMath.FindActorState(
                            binding.Track,
                            active.TimeSeconds);
                if (state is not null)
                {
                    // Full state packets also contain interpolation queues and
                    // animator state. Replaying them continuously makes remote
                    // actors snap back at the entity-state capture rate.
                    bool restoreFullState = binding.FullStateRestorePending
                        && !ReferenceEquals(state, binding.LastAppliedState)
                        && CanRestoreFullEntityState(binding);
                    if (restoreFullState)
                    {
                        IAnimationManager? animationManagerBeforeRestore = binding.Entity.AnimManager;
                        ApplyEntityState(binding.Entity, state.Data);
                        if (binding.Entity is EntityPlayer replayPlayer)
                        {
                            // Mod-owned watched attributes live in the full
                            // entity state. Equipment is restored from the
                            // sampled replay inventories below; both may affect
                            // the third-person mesh, so refresh it immediately.
                            ConfigureReplayPlayer(replayPlayer, markShapeModified: true);
                        }
                        // FromBytes can replace an entity's animation manager,
                        // but a normal visibility re-entry keeps the same
                        // manager. Do not stop every active layer on that
                        // re-entry; the sample reconciliation below restores
                        // the recorded state without a visible animation
                        // flash. A genuinely replaced manager still needs its
                        // replay bookkeeping reset before it is synchronized.
                        if (!ReferenceEquals(
                                animationManagerBeforeRestore,
                                binding.Entity.AnimManager))
                        {
                            ResetReplayAnimationTracking(binding);
                            animatorReplaced = true;
                        }
                    }
                    binding.LastAppliedState = state;
                    binding.FullStateRestorePending = false;
                }

                // Visual hiding must not stop sampling the actor. TrackEntity
                // evaluates this same instance after the replay frame, so a
                // hidden target still needs its current position, body yaw,
                // head yaw, and pitch before the camera is evaluated.
                if (!visualAllowed
                    && IsReplayEntityRenderable(binding.Entity)
                    && DirectorReplayMath.TrySample(
                        binding.Track,
                        active.TimeSeconds,
                        binding.InterpolatedSample,
                        interpolate: true))
                {
                    if (actionDecoratorsActive && ActionSampleDecorator is not null)
                    {
                        binding.InterpolatedSample = ActionSampleDecorator(
                            binding.Track,
                            active.TimeSeconds,
                            binding.InterpolatedSample);
                    }
                    ApplySample(
                        binding.Entity,
                        binding.InterpolatedSample,
                        binding,
                        forceAnimations: false,
                        updateAnimations: false);
                    binding.HasAppliedSample = true;
                }

                visible = IsReplayEntityRenderable(binding.Entity)
                    && visualAllowed;
                SetReplayEntityVisibility(binding.Entity, visible);
                if (!visible)
                {
                    binding.WasVisible = false;
                    binding.WasInCurrentView = false;
                    binding.DedicatedAnimationPoseReady = true;
                    ResetDedicatedReplayAnimationWarmup(binding);
                    binding.FullStateRestorePending = visualAllowed;
                    continue;
                }

                if (binding.IsGhost)
                {
                    binding.Entity.State = EnumEntityState.Inactive;
                    binding.Entity.IdleSoundChanceModifier = 0f;
                    if (becameVisible || animatorReplaced)
                    {
                        DetachReplayMount(binding.Entity);
                        ClearReplaySeatPassengers(binding.Entity);
                    }
                }

                if (DirectorReplayMath.TrySample(
                        binding.Track,
                        active.TimeSeconds,
                        binding.InterpolatedSample,
                        interpolate: true))
                {
                    if (actionDecoratorsActive && ActionSampleDecorator is not null)
                    {
                        binding.InterpolatedSample = ActionSampleDecorator(
                            binding.Track,
                            active.TimeSeconds,
                            binding.InterpolatedSample);
                    }
                    ApplyReplayPose(binding.Entity, binding.InterpolatedSample);
                    bool inCurrentView = IsInCurrentReplayView(
                        binding.Entity,
                        cameraX,
                        cameraZ,
                        cameraDimension,
                        maxDistanceSquared);
                    resetRendererState |= animatorReplaced
                        || (inCurrentView && !binding.WasInCurrentView);
                    if (dedicatedActorScheduling)
                    {
                        binding.DedicatedAnimationMainViewVisible = inCurrentView;
                        binding.DedicatedAnimationViewEligible = inCurrentView
                            || requiresFullActorSampling;
                    }
                    bool dedicatedPosePending = dedicatedActorScheduling
                        && !ReferenceEquals(binding.Entity, game.EntityPlayer)
                        && !binding.DedicatedAnimationPoseReady;
                    bool forceAnimations = DirectorReplayPlaybackPolicy.ShouldForceActorAnimations(
                        backwards,
                        becameVisible,
                        animatorReplaced,
                        inCurrentView,
                        binding.WasInCurrentView)
                        || resetTimedEvents
                        || dedicatedPosePending;
                    bool exactExport = exporter.IsExporting || obsExport?.StartGate.RecordingStarted == true;
                    bool wantsAnimationUpdate = forceAnimations || exactExport
                        || replayFrameDeltaSeconds > 0f && ShouldUpdateReplayAnimation(
                            binding,
                            animationFrame,
                            binding.Entity,
                            cameraX,
                            cameraZ);
                    bool updateAnimations = dedicatedActorScheduling
                        ? (inCurrentView || requiresFullActorSampling)
                            && wantsAnimationUpdate
                            && scheduledReplayAnimationUpdates.Contains(bindingIndex)
                        : (inCurrentView || requiresFullActorSampling)
                            && wantsAnimationUpdate
                            && (exactExport || animationUpdates < MaxReplayAnimationUpdatesPerFrame);
                    if (updateAnimations)
                    {
                        animationUpdates++;
                    }
                    long animationWorkStart = dedicatedActorScheduling && updateAnimations
                        ? Stopwatch.GetTimestamp()
                        : 0L;
                    ApplySample(
                        binding.Entity,
                        binding.InterpolatedSample,
                        binding,
                        forceAnimations: forceAnimations,
                        updateAnimations: updateAnimations,
                        poseAlreadyApplied: true);
                    if (animationWorkStart != 0L)
                    {
                        dedicatedAnimationWorkTicks += Stopwatch.GetTimestamp() - animationWorkStart;
                    }
                    if (actionDecoratorsActive)
                    {
                        ActionEntityDecorator?.Invoke(
                            binding.Track,
                            active.TimeSeconds,
                            binding.Entity,
                            binding.InterpolatedSample);
                    }
                    binding.HasAppliedSample = true;
                    bool hasRecordedAnimations = binding.InterpolatedSample.AnimationStates.Count > 0
                        || binding.InterpolatedSample.Animations.Count > 0;
                    if (dedicatedActorScheduling && updateAnimations)
                    {
                        binding.LastAnimationUpdateFrame = animationFrame;
                    }
                    if (dedicatedActorScheduling
                        && !ReferenceEquals(binding.Entity, game.EntityPlayer))
                    {
                        if (binding.DedicatedAnimationWarmupFallbackActive
                            && binding.Entity.AnimManager?.Animator is not null)
                        {
                            binding.DedicatedAnimationWarmupFallbackActive = false;
                            binding.DedicatedAnimationPoseReady = false;
                            ResetDedicatedReplayAnimationWarmup(binding);
                        }

                        if (!hasRecordedAnimations)
                        {
                            binding.DedicatedAnimationPoseReady = true;
                            ResetDedicatedReplayAnimationWarmup(binding);
                            binding.LastAnimationUpdateFrame = animationFrame;
                        }
                        else if (updateAnimations)
                        {
                            bool fallbackWithoutAnimator = binding.DedicatedAnimationWarmupFallbackActive
                                && binding.Entity.AnimManager?.Animator is null;
                            bool poseReady = !fallbackWithoutAnimator
                                && binding.Entity.AnimManager?.Animator is not null;
                            if (!fallbackWithoutAnimator && hasRecordedAnimations)
                            {
                                binding.AnimationPoseDirty = true;
                                // Detached actors are not guaranteed to pass
                                // through SystemRenderEntities' culling hook.
                                // Restore the animator pose before the dedicated
                                // render stages begin.
                                long poseWorkStart = Stopwatch.GetTimestamp();
                                poseReady = ApplyPendingReplayAnimationPose(
                                    active,
                                    binding,
                                    replayFrameDeltaSeconds);
                                dedicatedAnimationWorkTicks += Stopwatch.GetTimestamp() - poseWorkStart;
                            }
                            binding.DedicatedAnimationPoseReady = poseReady;
                            binding.DedicatedAnimationWarmupPending = !poseReady;
                            if (poseReady)
                            {
                                binding.DedicatedAnimationWarmupAttempts = 0;
                                binding.DedicatedAnimationWarmupNextFrame = -1L;
                                binding.DedicatedAnimationWarmupLastAttemptFrame = -1L;
                            }
                            else
                            {
                                if (!binding.DedicatedAnimationWarmupFallbackActive)
                                {
                                    binding.DedicatedAnimationWarmupNextFrame = animationFrame;
                                }
                            }
                        }
                        else if (forceAnimations)
                        {
                            // Do not draw a stale/T-pose actor. It will be
                            // selected again on the next frame unless a
                            // bounded renderer warm-up already owns it.
                            if (!binding.DedicatedAnimationWarmupFallbackActive)
                            {
                                binding.DedicatedAnimationPoseReady = false;
                            }
                        }
                    }
                    binding.WasInCurrentView = inCurrentView;
                    if (!dedicatedActorScheduling
                        && updateAnimations
                        && hasRecordedAnimations)
                    {
                        binding.LastAnimationUpdateFrame = animationFrame;
                        binding.AnimationPoseDirty = true;
                        ApplyPendingReplayAnimationPose(
                            active,
                            binding,
                            replayFrameDeltaSeconds);
                    }
                }

                SetReplayEntityVisibility(
                    binding.Entity,
                    binding.WasInCurrentView);

                if (resetRendererState
                    && !binding.InterpolatedSample.HasItemRenderState)
                {
                    ResetItemPlaybackAnimation(binding.Entity);
                }

                binding.WasVisible = true;
            }

            if (bindingCount > 0 && !dedicatedActorScheduling)
            {
                active.AnimationUpdateCursor =
                    (animationStart + Math.Max(1, MaxReplayAnimationUpdatesPerFrame)) % bindingCount;
            }
            else if (dedicatedActorScheduling && animationUpdates > 0)
            {
                double animationWorkMilliseconds =
                    dedicatedAnimationWorkTicks * 1000d / Stopwatch.Frequency;
                active.DedicatedAnimationBudget =
                    DirectorReplayActorSchedulingPolicy.AdaptAnimationBudget(
                        active.DedicatedAnimationBudget,
                        animationWorkMilliseconds,
                        animationUpdates);
            }

            long audioStarted = Stopwatch.GetTimestamp();
            ApplyTimedEvents(active, resetTimedEvents);
            audioWorkMs = Stopwatch.GetElapsedTime(audioStarted).TotalMilliseconds;
            active.TimedEventSeekPending = false;
            active.PendingParticleDeltaSeconds = UsesDeterministicParticleTimeline(active)
                ? DirectorReplayPlaybackPolicy.ResolvePendingParticleDelta(
                    replayFrameDeltaSeconds,
                    active.PendingParticleDeltaSeconds,
                    resetTimedEvents)
                : replayFrameDeltaSeconds;
            active.AppliedTimeSeconds = active.TimeSeconds;
        }
        finally
        {
            SetApplyingReplayState(false);
            active.TimeSeconds = timelineTimeSeconds;
            double workMs = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
            active.ProfileFrames++;
            active.ProfileTotalMs += workMs;
            active.ProfilePeakMs = Math.Max(active.ProfilePeakMs, workMs);
            active.ProfileEventsMs += audioWorkMs;
            if (Stopwatch.GetElapsedTime(active.ProfileStarted).TotalSeconds >= 5)
            {
                long pages = active.Document.Pages?.PagesRead ?? 0;
                capi.Logger.Debug("VS Director replay update: avg {0:0.00}ms, peak {1:0.00}ms, events/audio {2:0.00}ms, actors {3}, sounds {4}, voice streams {5}, decoded pages {6}.",
                    active.ProfileTotalMs / active.ProfileFrames, active.ProfilePeakMs, active.ProfileEventsMs / active.ProfileFrames,
                    active.Bindings.Count, active.ReplaySounds.Count, active.VoiceStream?.ActiveSources ?? 0, pages - active.ProfilePages);
                active.ProfileStarted = Stopwatch.GetTimestamp(); active.ProfilePages = pages;
                active.ProfileFrames = 0; active.ProfileTotalMs = active.ProfilePeakMs = active.ProfileEventsMs = 0;
            }
        }
    }

    private void HideReplayActors(Playback active)
    {
        foreach (ActorBinding binding in active.Bindings)
        {
            ParkReplayEntity(binding.Entity);
            binding.WasVisible = false;
            binding.WasInCurrentView = false;
            bool keepWarmupBackoff = dedicatedReplayRenderer.IsActive
                && binding.DedicatedAnimationWarmupPending
                && !binding.DedicatedAnimationPoseReady;
            if (keepWarmupBackoff)
            {
                binding.DedicatedAnimationViewEligible = false;
                binding.DedicatedAnimationMainViewVisible = false;
                binding.DedicatedAnimationWarmupLastAttemptFrame = -1L;
            }
            else
            {
                binding.DedicatedAnimationPoseReady = true;
                ResetDedicatedReplayAnimationWarmup(binding);
            }
        }
    }

    private static void ResetDedicatedReplayAnimationWarmup(ActorBinding binding)
    {
        binding.DedicatedAnimationWarmupPending = false;
        binding.DedicatedAnimationWarmupAttempts = 0;
        binding.DedicatedAnimationWarmupNextFrame = -1L;
        binding.DedicatedAnimationWarmupLastAttemptFrame = -1L;
        binding.DedicatedAnimationWarmupFallbackActive = false;
        binding.DedicatedAnimationViewEligible = false;
        binding.DedicatedAnimationMainViewVisible = false;
    }

    private static bool IsReplayEntityRenderable(Entity entity)
        => entity is not EntityItem item || IsItemEntityAlive(item);

    private static bool CanRestoreFullEntityState(ActorBinding binding)
        => binding.Entity is not EntityPlayer
            && binding.Entity.GetBehavior("seatable") is null;

    private static bool IsItemEntityAlive(EntityItem item)
        => item.Itemstack is not null
            && item.WatchedAttributes.GetInt("entityDead") == 0;

    private bool ShouldRenderBinding(Playback active, ActorBinding binding)
        => ShouldPrepareBinding(active, binding);

    private bool ShouldPrepareBinding(Playback active, ActorBinding binding)
        => active.WorldPlayback?.IsSynchronizing != true
            && binding.WasVisible
            && (binding.WasInCurrentView
                || (replayAllowsOffscreenActorRendering
                    && DirectorOffscreenRenderRuntime.IsRendering))
            && IsReplayEntityRenderable(binding.Entity)
            && !IsFallingBlockAlreadyPlaced(binding.Entity);

    private void PrepareDedicatedAnimationSchedule(
        Playback active,
        long frame,
        int cursor,
        bool resetAnimations,
        bool requiresFullActorSampling,
        double cameraX,
        double cameraZ,
        int cameraDimension,
        double maxDistanceSquared)
    {
        replayActorSchedulingRequests.Clear();
        scheduledReplayAnimationUpdates.Clear();
        int currentViewAnimationCount = 0;

        for (int index = 0; index < active.Bindings.Count; index++)
        {
            ActorBinding binding = active.Bindings[index];
            Entity entity = binding.Entity;
            bool fallbackWithoutAnimator = binding.DedicatedAnimationWarmupFallbackActive
                && entity.AnimManager?.Animator is null;
            if (binding.DedicatedAnimationWarmupFallbackActive && !fallbackWithoutAnimator)
            {
                binding.DedicatedAnimationWarmupFallbackActive = false;
                binding.DedicatedAnimationPoseReady = false;
                ResetDedicatedReplayAnimationWarmup(binding);
            }
            bool trackVisible = DirectorReplayMath.IsActorVisible(binding.Track, active.TimeSeconds);
            bool inCurrentView = false;
            double distanceSquared = double.PositiveInfinity;
            if (trackVisible)
            {
                if (DirectorReplayMath.TrySamplePosition(
                        binding.Track,
                        active.TimeSeconds,
                        interpolate: true,
                        out DirectorReplayActorPosition position))
                {
                    binding.ScheduledPosition = position;
                    binding.HasScheduledPosition = true;
                    binding.ScheduledPositionFrame = frame;
                    double dx = position.X - cameraX;
                    double dz = position.Z - cameraZ;
                    distanceSquared = dx * dx + dz * dz;
                    inCurrentView = IsSampleInCurrentReplayView(
                        binding,
                        position,
                        cameraX,
                        cameraZ,
                        cameraDimension,
                        maxDistanceSquared);
                }
                else
                {
                    binding.HasScheduledPosition = false;
                    binding.ScheduledPositionFrame = frame;
                    double dx = entity.Pos.X - cameraX;
                    double dz = entity.Pos.Z - cameraZ;
                    distanceSquared = dx * dx + dz * dz;
                    inCurrentView = IsInCurrentReplayView(
                        entity,
                        cameraX,
                        cameraZ,
                        cameraDimension,
                        maxDistanceSquared);
                }
            }
            else
            {
                binding.HasScheduledPosition = false;
                binding.ScheduledPositionFrame = frame;
            }

            binding.DedicatedAnimationMainViewVisible = inCurrentView;
            binding.DedicatedAnimationViewEligible = trackVisible
                && (inCurrentView || requiresFullActorSampling);

            DirectorReplayActorState? scheduledState = !binding.FullStateRestorePending ? binding.LastAppliedState : trackVisible
                ? DirectorReplayMath.FindActorState(binding.Track, active.TimeSeconds)
                : null;
            binding.ScheduledState = scheduledState;
            binding.ScheduledStateFrame = frame;
            bool stateRestorePending = scheduledState is not null
                && binding.FullStateRestorePending
                && !ReferenceEquals(scheduledState, binding.LastAppliedState)
                && CanRestoreFullEntityState(binding);

            bool awaitingWarmup = binding.DedicatedAnimationWarmupPending
                && !binding.DedicatedAnimationPoseReady;
            bool eligible = trackVisible
                && (inCurrentView || requiresFullActorSampling)
                && !awaitingWarmup
                && !fallbackWithoutAnimator;
            if (eligible && inCurrentView)
            {
                currentViewAnimationCount++;
            }
            bool force = resetAnimations
                || !binding.WasVisible
                || stateRestorePending
                || (!binding.DedicatedAnimationPoseReady
                    && !awaitingWarmup
                    && !fallbackWithoutAnimator)
                || inCurrentView;
            replayActorSchedulingRequests.Add(
                new DirectorReplayActorSchedulingRequest(
                    eligible,
                    force,
                    binding.LastAnimationUpdateFrame,
                    distanceSquared));
        }

        DirectorReplayActorSchedulingResult result =
            DirectorReplayActorSchedulingPolicy.SelectAnimationUpdates(
                replayActorSchedulingRequests,
                frame,
                cursor,
                DirectorReplayActorSchedulingMode.Dedicated,
                budgetOverride: Math.Max(
                    active.DedicatedAnimationBudget,
                    currentViewAnimationCount));
        foreach (int index in result.SelectedIndices)
        {
            scheduledReplayAnimationUpdates.Add(index);
        }
        active.AnimationUpdateCursor = result.NextCursor;
    }

    private bool IsSampleInCurrentReplayView(
        ActorBinding binding,
        DirectorReplayActorPosition position,
        double cameraX,
        double cameraZ,
        int cameraDimension,
        double maxDistanceSquared)
    {
        Entity entity = binding.Entity;
        if (position.Dimension != cameraDimension)
        {
            return false;
        }

        double dx = position.X - cameraX;
        double dz = position.Z - cameraZ;
        if (!entity.AllowOutsideLoadedRange)
        {
            if (dx * dx + dz * dz > maxDistanceSquared)
            {
                return false;
            }

            EntityPos scheduledPosition = binding.ScheduledEntityPosition;
            scheduledPosition.SetPos(position.X, position.Y, position.Z);
            scheduledPosition.Dimension = position.Dimension;
            if (!game.WorldMap.IsChunkRendered(scheduledPosition))
            {
                return false;
            }
        }

        double internalY = position.Y + position.Dimension * 32768d;
        return capi.Render.DefaultFrustumCuller.SphereInFrustum(
            position.X,
            internalY,
            position.Z,
            entity.FrustumSphereRadius);
    }

    private static bool ShouldUpdateReplayAnimation(
        ActorBinding binding,
        long frame,
        Entity entity,
        double cameraX,
        double cameraZ)
    {
        if (binding.LastAnimationUpdateFrame < 0L)
        {
            return true;
        }

        double dx = entity.Pos.X - cameraX;
        double dz = entity.Pos.Z - cameraZ;
        double distanceSquared = dx * dx + dz * dz;
        int interval = distanceSquared <= 16d * 16d
            ? 1
            : distanceSquared <= 32d * 32d
                ? 2
                : 4;
        return frame - binding.LastAnimationUpdateFrame >= interval;
    }

    private void SetReplayEntityVisibility(Entity entity, bool visible)
    {
        entity.IsRendered = visible;
        entity.IsShadowRendered = visible;
        if (replayGhostIds.Contains(entity.EntityId) && IsFallingBlockEntity(entity))
        {
            SetFallingBlockField(entity, "DoRender", visible);
        }
    }

    private void ParkReplayEntity(Entity entity)
    {
        // Visibility is enforced by the replay renderer patches and by
        // IsRendered/IsShadowRendered. Moving a live or replay entity to a
        // synthetic dimension here corrupts camera tracking, chunk culling,
        // and texture lookup when a hidden TrackEntity target is reused.
        SetReplayEntityVisibility(entity, false);
    }

    private void SuppressLiveEntity(Entity entity)
    {
        if (!suppressedLiveEntities.ContainsKey(entity))
        {
            suppressedLiveEntities.Add(
                entity,
                new SavedEntityVisibility(entity.IsRendered, entity.IsShadowRendered));
        }

        entity.IsRendered = false;
        entity.IsShadowRendered = false;
    }

    private void SuppressLoadedRealityEntities(Playback active)
    {
        if (!ShouldSuppressLiveReality(active))
        {
            return;
        }
        foreach (Entity entity in game.LoadedEntities.Values.ToArray())
        {
            if (!IsReplayControlledEntity(active, entity))
            {
                SuppressLiveEntity(entity);
            }
        }
    }

    private bool IsReplayControlledEntity(Playback active, Entity entity)
        => replayGhostIds.Contains(entity.EntityId)
            || active.Bindings.Any(binding => ReferenceEquals(binding.Entity, entity));

    private void RestoreSuppressedLiveEntities()
    {
        foreach ((Entity entity, SavedEntityVisibility visibility) in suppressedLiveEntities)
        {
            try
            {
                if (!game.LoadedEntities.TryGetValue(entity.EntityId, out Entity? loaded)
                    || !ReferenceEquals(loaded, entity))
                {
                    continue;
                }
                entity.IsRendered = visibility.IsRendered;
                entity.IsShadowRendered = visibility.IsShadowRendered;
                if (entity is EntityPlayer)
                {
                    entity.MarkShapeModified();
                }
            }
            catch (Exception exception)
            {
                capi.Logger.Debug(
                    "VS Director: suppressed live entity visibility could not be restored: {0}",
                    exception.Message);
            }
        }

        suppressedLiveEntities.Clear();
    }

    private static bool IsFallingBlockEntity(Entity entity)
        => string.Equals(entity.GetType().FullName, FallingBlockEntityType, StringComparison.Ordinal);

    private static bool IsFallingBlockRenderActive(Entity entity)
        => entity.GetType().GetField("DoRender", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entity)
            is not bool doRender
            || doRender;

    private bool IsFallingBlockAlreadyPlaced(Entity entity)
    {
        if (!IsFallingBlockEntity(entity))
        {
            return false;
        }

        try
        {
            if (entity.GetType().GetProperty("Block", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entity)
                is not Block fallingBlock)
            {
                return false;
            }

            Block worldBlock = capi.World.BlockAccessor.GetBlock(entity.Pos.AsBlockPos, BlockLayersAccess.Solid);
            return worldBlock.BlockId == fallingBlock.BlockId;
        }
        catch
        {
            return false;
        }
    }

    private static void SetFallingBlockField(Entity entity, string name, bool value)
    {
        entity.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.SetValue(entity, value);
    }

    internal bool TryGetDedicatedFallingBlockRenderState(Entity entity, out bool value)
    {
        value = false;
        if (!replayGhostIds.Contains(entity.EntityId) || !IsFallingBlockEntity(entity))
        {
            return false;
        }

        try
        {
            FieldInfo? field = entity.GetType().GetField(
                "DoRender",
                BindingFlags.Instance | BindingFlags.Public);
            if (field?.GetValue(entity) is not bool current)
            {
                return false;
            }

            value = current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void SetDedicatedFallingBlockRenderState(Entity entity, bool value)
    {
        if (!replayGhostIds.Contains(entity.EntityId) || !IsFallingBlockEntity(entity))
        {
            return;
        }

        try
        {
            SetFallingBlockField(entity, "DoRender", value);
        }
        catch
        {
        }
    }

    private void ResetItemPlaybackAnimation(Entity entity)
    {
        if (entity is not EntityItem item)
        {
            return;
        }

        // EntityItemRenderer seeds its falling rotation from the live world's
        // elapsed clock when the ghost is created. A replay actor can remain
        // hidden for minutes before its first lifetime begins, which otherwise
        // makes the item appear with an already advanced spin. Restart the
        // renderer on the exact visibility edge so hidden time is never
        // interpreted as replay animation time.
        item.itemSpawnedMilliseconds = capi.World.ElapsedMilliseconds;
        object? renderer = null;
        if (EntityRenderersField?.GetValue(game) is IDictionary renderers
            && renderers[entity.EntityId] is object registered)
        {
            renderer = registered;
        }
        else if (dedicatedReplayRenderer.TryGetRenderer(entity.EntityId, out EntityRenderer? dedicated))
        {
            renderer = dedicated;
        }

        if (renderer is null)
        {
            return;
        }

        Type type = renderer.GetType();
        SetRendererField(type, renderer, "touchGroundMS", capi.World.ElapsedMilliseconds);
        SetRendererField(type, renderer, "xangle", 0f);
        SetRendererField(type, renderer, "yangle", 0f);
        SetRendererField(type, renderer, "zangle", 0f);
        SetRendererField(type, renderer, "accum", 0f);
        SetRendererField(type, renderer, "lerpedPos", new Vec3d(entity.Pos.X, entity.Pos.Y, entity.Pos.Z));
    }

    private static List<byte[]> SerializeInventory(IInventory? inventory)
    {
        List<byte[]> result = new(inventory?.Count ?? 0);
        if (inventory is null)
        {
            return result;
        }
        for (int index = 0; index < inventory.Count; index++)
        {
            result.Add(SerializeItemStack(inventory[index]?.Itemstack));
        }
        return result;
    }

    private void ApplyReplayItemRenderState(Entity entity, DirectorReplayActorSample sample)
    {
        if (!sample.HasItemRenderState
            || entity is not EntityItem
            || !TryGetEntityRenderer(entity.EntityId, out object? renderer)
            || renderer is null)
        {
            return;
        }

        ApplyReplayItemRenderState(renderer, entity, sample);
    }

    private static void ApplyReplayItemRenderState(
        object renderer,
        Entity entity,
        DirectorReplayActorSample sample)
    {
        Type type = renderer.GetType();
        long elapsedMilliseconds = Math.Max(0L, sample.ItemRenderElapsedMilliseconds);
        SetRendererField(
            type,
            renderer,
            "touchGroundMS",
            elapsedMilliseconds - Math.Max(0L, sample.ItemTouchGroundAgeMilliseconds));
        SetRendererField(type, renderer, "scaleRand", sample.ItemScaleRandom);
        SetRendererField(type, renderer, "yRotRand", sample.ItemYawRandom);
        SetRendererField(type, renderer, "xangle", sample.ItemRenderXAngle);
        SetRendererField(type, renderer, "yangle", sample.ItemRenderYAngle);
        SetRendererField(type, renderer, "zangle", sample.ItemRenderZAngle);
        SetRendererField(type, renderer, "accum", sample.ItemRenderAccumulator);
        SetRendererField(type, renderer, "lerpedPos", new Vec3d(entity.Pos.X, entity.Pos.Y, entity.Pos.Z));
    }

    private bool TryGetEntityRenderer(long entityId, out object? renderer)
    {
        renderer = null;
        if (EntityRenderersField?.GetValue(game) is IDictionary renderers
            && renderers[entityId] is object registered)
        {
            renderer = registered;
            return true;
        }

        if (dedicatedReplayRenderer.TryGetRenderer(entityId, out EntityRenderer? dedicated)
            && dedicated is not null)
        {
            renderer = dedicated;
            return true;
        }

        return false;
    }

    private static bool TryGetRendererField<T>(
        Type rendererType,
        object renderer,
        string name,
        out T value)
    {
        if (FindRendererField(rendererType, name)?.GetValue(renderer)
            is T captured)
        {
            value = captured;
            return true;
        }

        value = default!;
        return false;
    }

    private static void SetRendererField(Type rendererType, object renderer, string name, object value)
    {
        FindRendererField(rendererType, name)?.SetValue(renderer, value);
    }

    private static FieldInfo? FindRendererField(Type rendererType, string name)
    {
        for (Type? type = rendererType; type is not null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(
                name,
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }
        return null;
    }

    private void ApplyClock(Playback active)
    {
        if (!DirectorReplayMath.TrySampleClock(
            active.Document.ClockSamples,
            active.TimeSeconds,
            out DirectorReplayClockSample clock))
        {
            return;
        }

        double hoursPerDay = Math.Max(1d, capi.World.Calendar.HoursPerDay);
        bool sampledTime = DirectorReplayElementTimeline.TrySample(
                active.Document.ViewportTimeline,
                DirectorReplayElementTrackType.TimeOfDay,
                active.TimeSeconds,
                out DirectorReplayElementKeyframe timeOfDay);
        DirectorReplayEnvironmentVisuals environment = active.Document.ViewportTimeline.Visuals.Environment;
        if (sampledTime || environment.OverrideTime)
        {
            double dayStart = Math.Floor(clock.TotalHours / hoursPerDay) * hoursPerDay;
            int tick = sampledTime ? timeOfDay.TimeOfDayTick : environment.TimeOfDayTick;
            clock.TotalHours = dayStart + tick / 24000d * hoursPerDay;
        }
        float visualOffsetDays = (float)((clock.TotalHours - capi.World.Calendar.TotalHours) / hoursPerDay);
        game.timelapse = 0f;
        game.timelapsedCurrent = visualOffsetDays;
        game.timelapseEnd = float.MaxValue;
        capi.World.Calendar.Timelapse = visualOffsetDays;
        capi.World.Calendar.CalendarSpeedMul = Math.Clamp(clock.CalendarSpeedMul, 0f, 1000f);
        capi.World.Calendar.SeasonOverride = environment.OverrideSeason
            ? environment.Season
            : clock.SeasonOverride;
        RefreshVisualCalendar();
    }

    private void RefreshVisualCalendar()
    {
        // Sun/moon position, light strength, and shadow direction are cached
        // by ClientGameCalendar.Update(), unlike the night sky's TotalDays.
        object calendar = capi.World.Calendar;
        if (!ReferenceEquals(visualCalendar, calendar))
        {
            visualCalendar = calendar;
            visualCalendarUpdateMethod = calendar.GetType().GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            visualCalendarRefreshFailureLogged = false;
        }
        if (visualCalendarUpdateMethod is null)
        {
            if (!visualCalendarRefreshFailureLogged)
            {
                visualCalendarRefreshFailureLogged = true;
                capi.Logger.Warning("VS Director: client calendar does not expose Update(); replay daylight may be stale.");
            }
            return;
        }
        try
        {
            visualCalendarUpdateMethod.Invoke(calendar, null);
        }
        catch (Exception exception)
        {
            if (!visualCalendarRefreshFailureLogged)
            {
                visualCalendarRefreshFailureLogged = true;
                capi.Logger.Warning(
                    "VS Director: could not refresh replay daylight: {0}",
                    exception.Message);
            }
        }
    }

    private static IReadOnlyList<DirectorReplayElementKeyframe> EnabledElementKeyframes(
        DirectorReplayViewportTimeline timeline)
    {
        HashSet<Guid> enabledTrackIds = timeline.Tracks
            .Where(track => track.Enabled)
            .Select(track => track.Id)
            .ToHashSet();
        return timeline.ElementKeyframes.Where(keyframe =>
            keyframe.TrackId == Guid.Empty
                ? !timeline.DisabledElementTracks.Contains(keyframe.TrackType)
                : enabledTrackIds.Contains(keyframe.TrackId)).ToArray();
    }

    private void ApplyTimedEvents(Playback active, bool resetCursors)
    {
        if (resetCursors)
        {
            active.NextParticleEvent = FirstEventAfter(active.Document.ParticleEvents, active.TimeSeconds);
            active.NextSoundEvent = FirstEventAfter(active.Document.SoundEvents, active.TimeSeconds);
            active.NextVoiceFrame = Math.Max(
                0,
                FirstVoiceFrameAfter(active.Document.VoiceFrames, active.TimeSeconds) - 1);
            RestoreReplayParticles(active);
            DirectorReplayAudioVisuals audio = active.Document.ViewportTimeline.Visuals.Audio;
            bool previewAudio = !active.Paused
                || exporter.IsExporting
                || obsExport?.IsActive == true;
            ResetReplaySounds(active, restoreAtCurrentTime: previewAudio);
            UpdateReplaySoundSpatialAnchors(active);
            ApplyReplayVoiceFrames(active);
            return;
        }

        bool dedicated = dedicatedReplayRenderer.IsActive;
        int maximumParticleEvents = dedicated
            ? DedicatedMaxParticleEventsPerPlaybackFrame
            : MaxParticleEventsPerPlaybackFrame;
        double maximumParticleAge = dedicated
            ? DedicatedMaxParticlePlaybackAgeSeconds
            : MaxParticlePlaybackAgeSeconds;
        double particleWorkBudget = dedicated
            ? DedicatedParticlePlaybackWorkBudgetMilliseconds
            : ParticlePlaybackWorkBudgetMilliseconds;
        DirectorReplayParticlePlaybackWindow particleWindow =
            DirectorReplayPlaybackPolicy.SelectParticleWindow(
                active.Document.ParticleEvents,
                active.NextParticleEvent,
                active.TimeSeconds,
                maximumParticleAge,
                maximumParticleEvents);
        active.NextParticleEvent = particleWindow.StartIndex;
        Stopwatch particleWork = Stopwatch.StartNew();
        int spawnedParticles = 0;
        while (active.NextParticleEvent < particleWindow.EndIndex
            && spawnedParticles < maximumParticleEvents
            && (spawnedParticles == 0
                || particleWork.Elapsed.TotalMilliseconds < particleWorkBudget))
        {
            DirectorReplayParticleEvent particle = active.Document.ParticleEvents[active.NextParticleEvent++];
            DirectorReplayParticleVisuals particleVisuals = active.Document.ViewportTimeline.Visuals.Particles;
            if (!ShouldRenderParticle(particle, particleVisuals))
            {
                continue;
            }
            SpawnParticle(particle, particleVisuals);
            spawnedParticles++;
        }

        DirectorReplaySoundPlaybackWindow soundWindow = DirectorReplayPlaybackPolicy.SelectSoundWindow(
            active.Document.SoundEvents,
            active.NextSoundEvent,
            active.TimeSeconds);
        active.NextSoundEvent = soundWindow.StartIndex;
        while (active.NextSoundEvent < soundWindow.EndIndex)
        {
            DirectorReplaySoundEvent sound = active.Document.SoundEvents[active.NextSoundEvent++];
            if (DirectorReplayPlaybackPolicy.IsValidSoundPosition(sound.X, sound.Y, sound.Z))
            {
                ApplyReplaySoundEvent(active, sound);
            }
        }
        ApplyReplayVoiceFrames(active);
        UpdateReplaySoundSpatialAnchors(active);
        ApplyReplaySoundVisuals(active);
    }

    private static int FirstVoiceFrameAfter(IReadOnlyList<DirectorReplayVoiceFrame>? frames, double timeSeconds)
        => frames is null ? 0 : DirectorReplayTimeSearch.LowerBound(frames, timeSeconds - TimeEpsilon, static f => f.TimeSeconds);

    private void ApplyReplayVoiceFrames(Playback active)
    {
        if (!CaptureVoice || active.Document.VoiceFrames.Count == 0)
        {
            ResetReplayVoiceSounds(active);
            return;
        }
        try
        {
            active.VoiceAudio ??= new DirectorOpenAlNative();
            var al = active.VoiceAudio;
            active.VoiceStream ??= new DirectorReplayVoiceStream(active.Document.VoiceFrames, () => new DirectorReplayVoiceOutput(al));
            int dimension = controller.CurrentPose?.Dimension ?? game.EntityPlayer.Pos.Dimension;
            active.VoiceStream.Update(active.TimeSeconds, (float)DirectorReplayVisualRuntime.ReplaySpeed,
                ShouldPauseReplaySounds(active), frame =>
                {
                    if (frame.Spatialized && frame.Dimension != dimension) return 0;
                    var audio = active.Document.ViewportTimeline.Visuals.Audio;
                    if (!audio.Enabled || !DirectorReplayVoiceTrackPolicy.TryResolveGain(active.Document.ViewportTimeline, frame, active.TimeSeconds, out float gain)) return 0;
                    return frame.Volume * gain * audio.MasterVolume * Math.Clamp(ClientSettings.EntitySoundLevel / 100f, 0, 1);
                });
        }
        catch (Exception exception)
        {
            ResetReplayVoiceSounds(active);
            if (!active.VoiceFailureReported)
            {
                active.VoiceFailureReported = true;
                capi.Logger.Warning("VS Director: replay voice stream failed: {0}", exception.Message);
            }
        }
    }

    private void ResetReplayVoiceSounds(Playback active)
    {
        active.VoiceStream?.Dispose(); active.VoiceStream = null;
        active.VoiceAudio?.Dispose(); active.VoiceAudio = null;
    }

    private void ApplyReplaySoundEvent(Playback active, DirectorReplaySoundEvent sound)
    {
        if (sound.Action == DirectorReplaySoundAction.Play)
        {
            PlayReplaySound(active, sound, restoreTimeSeconds: null);
            return;
        }

        if (sound.InstanceId <= 0L
            || !active.ReplaySounds.TryGetValue(sound.InstanceId, out ILoadedSound? loadedSound))
        {
            return;
        }

        switch (sound.Action)
        {
            case DirectorReplaySoundAction.Pause:
                loadedSound.Pause();
                active.TimelinePausedReplaySounds.Add(sound.InstanceId);
                break;
            case DirectorReplaySoundAction.Stop:
                StopReplaySound(active, sound.InstanceId, loadedSound);
                break;
            case DirectorReplaySoundAction.Update:
                active.ReplaySoundStates[sound.InstanceId] = sound;
                ApplyReplaySoundParameters(
                    loadedSound,
                    sound,
                    updatePosition: !active.ActorAnchoredReplaySounds.Contains(sound.InstanceId));
                break;
        }
    }

    private void PlayReplaySound(
        Playback active,
        DirectorReplaySoundEvent sound,
        double? restoreTimeSeconds)
    {
        if (!ShouldPlaySound(sound))
        {
            return;
        }
        if (sound.InstanceId > 0L
            && active.ReplaySounds.Remove(sound.InstanceId, out ILoadedSound? previous))
        {
            active.ReplaySoundStates.Remove(sound.InstanceId);
            active.PlaybackOwnedSounds.Remove(previous);
            DisposeReplaySound(previous);
        }
        active.TimelinePausedReplaySounds.Remove(sound.InstanceId);
        active.ActorAnchoredReplaySounds.Remove(sound.InstanceId);

        SoundParams parameters = CreateSpatializedReplaySoundParams(
            active.Document,
            sound,
            capi.World.Player?.PlayerUID,
            out bool actorAnchored);
        ILoadedSound? loadedSound = capi.World.LoadSound(parameters);
        if (loadedSound is null)
        {
            return;
        }
        active.PlaybackOwnedSounds.Add(loadedSound);

        float playbackPosition = sound.PlaybackPosition;
        if (restoreTimeSeconds is double restoreTime && loadedSound.SoundLengthSeconds > 0f)
        {
            double elapsed = Math.Max(0d, restoreTime - sound.TimeSeconds);
            DirectorReplayAudioVisuals audio = DirectorReplayVisualRuntime.Current?.Audio
                ?? new DirectorReplayAudioVisuals();
            double effectivePitch = Math.Max(
                0.01d,
                sound.Pitch * EffectiveMasterPitch(audio) + sound.PitchOffset);
            double resolvedPosition = playbackPosition + elapsed * effectivePitch;
            if (!sound.ShouldLoop && resolvedPosition >= loadedSound.SoundLengthSeconds)
            {
                DisposeReplaySound(loadedSound);
                active.PlaybackOwnedSounds.Remove(loadedSound);
                return;
            }
            playbackPosition = sound.ShouldLoop
                ? (float)(resolvedPosition % loadedSound.SoundLengthSeconds)
                : (float)resolvedPosition;
        }
        loadedSound.PlaybackPosition = Math.Max(0f, playbackPosition);
        loadedSound.Start();
        if (sound.PitchOffset != 0f)
        {
            loadedSound.SetPitchOffset(sound.PitchOffset);
        }
        if (ShouldPauseReplaySounds(active))
        {
            loadedSound.Pause();
        }
        if (sound.InstanceId > 0L)
        {
            active.ReplaySounds[sound.InstanceId] = loadedSound;
            active.ReplaySoundStates[sound.InstanceId] = sound;
            if (actorAnchored)
            {
                active.ActorAnchoredReplaySounds.Add(sound.InstanceId);
            }
        }
    }

    private void ResetReplaySounds(Playback active, bool restoreAtCurrentTime)
    {
        ResetReplayVoiceSounds(active);
        foreach (ILoadedSound sound in active.PlaybackOwnedSounds)
        {
            DisposeReplaySound(sound);
        }
        active.PlaybackOwnedSounds.Clear();
        active.ReplaySounds.Clear();
        active.ReplaySoundStates.Clear();
        active.TimelinePausedReplaySounds.Clear();
        active.ActorAnchoredReplaySounds.Clear();
        if (!restoreAtCurrentTime)
        {
            return;
        }

        double prerollStart = Math.Max(
            0d,
            active.TimeSeconds
                - DefaultReplayPrerollSeconds);
        foreach (DirectorReplaySoundEvent sound in DirectorReplayPlaybackPolicy.SelectActiveSounds(
            active.Document.SoundEvents,
            active.TimeSeconds))
        {
            if (!sound.ShouldLoop && sound.TimeSeconds + TimeEpsilon < prerollStart)
            {
                continue;
            }
            PlayReplaySound(active, sound, active.TimeSeconds);
        }
    }

    private static void ApplyReplaySoundParameters(
        ILoadedSound loadedSound,
        DirectorReplaySoundEvent sound,
        bool updatePosition)
    {
        if (loadedSound.IsDisposed) return;
        DirectorReplayAudioVisuals audio = DirectorReplayVisualRuntime.Current?.Audio
            ?? new DirectorReplayAudioVisuals();
        SoundParams current = loadedSound.Params;
        if (current.ShouldLoop != sound.ShouldLoop) loadedSound.SetLooping(sound.ShouldLoop);
        if (updatePosition && !sound.RelativePosition && (current.Position is null
            || current.Position.X != (float)sound.X || current.Position.Y != (float)sound.Y || current.Position.Z != (float)sound.Z))
        {
            loadedSound.SetPosition((float)sound.X, (float)sound.Y, (float)sound.Z);
        }
        float pitch = Math.Clamp(sound.Pitch * EffectiveMasterPitch(audio), 0.1f, 4f);
        if (current.Pitch != pitch) loadedSound.SetPitch(pitch);
        loadedSound.SetPitchOffset(sound.PitchOffset);
        float volume = Math.Clamp(
            sound.Volume * SoundBusGain(sound, audio) * audio.MasterVolume,
            0f,
            4f);
        if (current.Volume != volume) loadedSound.SetVolume(volume);
        float lowPass = Math.Clamp(sound.LowPassFilter, 0f, 4f), reverb = Math.Clamp(sound.ReverbDecayTime, 0f, 120f);
        if (current.LowPassFilter != lowPass) loadedSound.SetLowPassfiltering(lowPass);
        if (current.ReverbDecayTime != reverb) loadedSound.SetReverb(reverb);
    }

    private static void StopReplaySound(
        Playback active,
        long instanceId,
        ILoadedSound loadedSound)
    {
        active.ReplaySounds.Remove(instanceId);
        active.ReplaySoundStates.Remove(instanceId);
        active.PlaybackOwnedSounds.Remove(loadedSound);
        active.TimelinePausedReplaySounds.Remove(instanceId);
        active.ActorAnchoredReplaySounds.Remove(instanceId);
        DisposeReplaySound(loadedSound);
    }

    private static void DisposeReplaySound(ILoadedSound sound)
    {
        try
        {
            sound.Stop();
            sound.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void UpdateReplaySoundPauseState(Playback active)
    {
        bool shouldPause = ShouldPauseReplaySounds(active);
        foreach ((long instanceId, ILoadedSound sound) in active.ReplaySounds.ToArray())
        {
            if (sound.IsDisposed || !sound.Params.ShouldLoop && sound.HasStopped)
            {
                active.ReplaySounds.Remove(instanceId);
                active.ReplaySoundStates.Remove(instanceId);
                active.PlaybackOwnedSounds.Remove(sound);
                active.TimelinePausedReplaySounds.Remove(instanceId);
                active.ActorAnchoredReplaySounds.Remove(instanceId);
                continue;
            }
            if (shouldPause && sound.IsPlaying)
            {
                sound.Pause();
            }
            else if (!shouldPause
                && sound.IsPaused
                && !active.TimelinePausedReplaySounds.Contains(instanceId))
            {
                sound.Start();
            }
        }
        active.VoiceStream?.SetPaused(shouldPause);
    }

    private static bool ShouldSuppressLiveReality(Playback active)
    {
        _ = active;
        // Replay playback owns the visible scene. Live entities remain in
        // the client world for normal networking and are restored on stop,
        // but they must never leak into a replay frame.
        return true;
    }

    private bool IsEntityAllowedByVisuals(
        DirectorReplayActorTrack track,
        Entity entity)
    {
        DirectorReplayWorldVisuals? visuals = DirectorReplayVisualRuntime.Current?.World;
        if (visuals is null)
        {
            return true;
        }
        if (!visuals.ShowEntities)
        {
            return false;
        }
        if (visuals.HideTrackedEntity
            && hiddenTrackedReplayEntityId != 0L
            && entity.EntityId == hiddenTrackedReplayEntityId)
        {
            return false;
        }
        if (entity is EntityPlayer)
        {
            return visuals.ShowPlayers;
        }
        return true;
    }

    private bool ShouldPauseReplaySounds(Playback active)
        => active.ElementFullyFrozen || playbackChunkBuffering || active.WorldPlayback?.IsSynchronizing == true
            || DirectorReplayPlaybackPolicy.ShouldPauseReplaySounds(
                active.Paused,
                game.IsPaused,
                obsExport is { IsActive: true, Client: not null }
                    && obsExport.StartGate.RecordingStarted);

    private void UpdateReplaySoundSpatialAnchors(Playback active)
    {
        if (active.ActorAnchoredReplaySounds.Count == 0
            || !TryGetReplaySoundAnchor(
                active.Document,
                active.TimeSeconds,
                capi.World.Player?.PlayerUID,
                out Vec3f position))
        {
            return;
        }

        foreach (long instanceId in active.ActorAnchoredReplaySounds.ToArray())
        {
            if (active.ReplaySounds.TryGetValue(instanceId, out ILoadedSound? sound)
                && !sound.IsDisposed)
            {
                sound.SetPosition(position);
            }
            else
            {
                active.ActorAnchoredReplaySounds.Remove(instanceId);
            }
        }
    }

    private static SoundParams CreateSpatializedReplaySoundParams(
        DirectorReplayDocument document,
        DirectorReplaySoundEvent sound,
        string? preferredPlayerUid,
        out bool actorAnchored)
    {
        SoundParams parameters = CreateReplaySoundParams(sound);
        bool shouldAnchor = DirectorSoundSpatialPolicy.AnchorRelativeSoundToWorld(
            sound.RelativePosition,
            parameters.SoundType);
        Vec3f position = new();
        actorAnchored = shouldAnchor && TryGetReplaySoundAnchor(
                document,
                sound.TimeSeconds,
                preferredPlayerUid,
                out position);
        if (actorAnchored)
        {
            parameters.RelativePosition = false;
            parameters.Position = position;
        }
        return parameters;
    }

    private static bool TryGetReplaySoundAnchor(
        DirectorReplayDocument document,
        double timeSeconds,
        string? preferredPlayerUid,
        out Vec3f position)
    {
        if (DirectorReplayAnchorSelector.TrySelect(
            document,
            timeSeconds,
            preferredPlayerUid,
            out DirectorReplayAnchorPose anchor))
        {
            position = new Vec3f(
                (float)anchor.X,
                (float)DirectorChunkWindow.ToAbsoluteY(anchor.Y, anchor.Dimension),
                (float)anchor.Z);
            return true;
        }

        position = new Vec3f();
        return false;
    }

    private static SoundParams CreateReplaySoundParams(DirectorReplaySoundEvent sound)
    {
        DirectorReplayAudioVisuals audio = DirectorReplayVisualRuntime.Current?.Audio
            ?? new DirectorReplayAudioVisuals();
        return new SoundParams
        {
            Location = new AssetLocation(sound.Location),
            Position = new Vec3f((float)sound.X, (float)sound.Y, (float)sound.Z),
            RelativePosition = audio.SpatialAudio ? sound.RelativePosition : true,
            ShouldLoop = sound.ShouldLoop,
            DisposeOnFinish = !sound.ShouldLoop,
            Pitch = Math.Clamp(sound.Pitch * EffectiveMasterPitch(audio), 0.1f, 4f),
            Range = Math.Max(0.1f, sound.Range),
            ReferenceDistance = Math.Max(0.1f, sound.ReferenceDistance),
            LowPassFilter = Math.Clamp(sound.LowPassFilter, 0f, 4f),
            ReverbDecayTime = Math.Clamp(sound.ReverbDecayTime, 0f, 120f),
            SoundType = (EnumSoundType)sound.SoundType,
            Volume = Math.Clamp(
                sound.Volume * SoundBusGain(sound, audio) * audio.MasterVolume,
                0f,
                4f)
        };
    }

    private static bool ShouldPlaySound(DirectorReplaySoundEvent sound)
    {
        DirectorReplayAudioVisuals? audio = DirectorReplayVisualRuntime.Current?.Audio;
        return !DirectorReplayWeatherVisualPatch.IsRecordedWeatherSound(sound)
            && (audio is null
                || audio.Enabled && audio.MasterVolume > 0f && SoundBusGain(sound, audio) > 0f);
    }

    private static float EffectiveMasterPitch(DirectorReplayAudioVisuals audio)
    {
        double replayRate = audio.PreservePitchWithReplaySpeed
            ? 1d
            : DirectorReplayVisualRuntime.ReplaySpeed;
        return (float)Math.Clamp(replayRate, 0.1d, 4d);
    }

    private static float SoundBusGain(
        DirectorReplaySoundEvent sound,
        DirectorReplayAudioVisuals audio)
    {
        return 1f;
    }

    private static void ApplyReplaySoundVisuals(Playback active)
    {
        if (active.ReplaySounds.Count == 0)
        {
            return;
        }
        foreach ((long instanceId, ILoadedSound loaded) in active.ReplaySounds)
        {
            if (loaded.IsDisposed || !active.ReplaySoundStates.TryGetValue(instanceId, out var state))
            {
                continue;
            }
            if (!ShouldPlaySound(state))
            {
                loaded.SetVolume(0f);
                continue;
            }
            ApplyReplaySoundParameters(
                loaded,
                state,
                updatePosition: !active.ActorAnchoredReplaySounds.Contains(instanceId));
        }
    }

    private static bool ShouldRenderParticle(
        DirectorReplayParticleEvent particle,
        DirectorReplayParticleVisuals visuals)
        => visuals.ShowParticles
            && !DirectorReplayWeatherVisualPatch.IsRecordedWeatherParticle(particle);

    private void RestoreReplayParticles(Playback active)
    {
        DirectorReplayVisualSettings visuals = active.Document.ViewportTimeline.Visuals;
        double preroll = DefaultReplayPrerollSeconds;
        if (preroll <= TimeEpsilon || !visuals.Particles.ShowParticles)
        {
            return;
        }
        double start = Math.Max(0d, active.TimeSeconds - preroll);
        int first = DirectorReplayTimeSearch.LowerBound(active.Document.ParticleEvents, start - TimeEpsilon, static e => e.TimeSeconds);
        for (int index = first; index < active.Document.ParticleEvents.Count; index++)
        {
            var particle = active.Document.ParticleEvents[index];
            if (particle.TimeSeconds > active.TimeSeconds + TimeEpsilon)
            {
                break;
            }
            if (ShouldRenderParticle(particle, visuals.Particles))
            {
                SpawnParticle(
                    particle,
                    visuals.Particles,
                    (float)Math.Max(0d, active.TimeSeconds - particle.TimeSeconds));
            }
        }
    }

    private void SpawnParticle(
        DirectorReplayParticleEvent particle,
        DirectorReplayParticleVisuals visuals,
        float ageSeconds = 0f)
    {
        try
        {
            if (particle.Kind == DirectorReplayParticleKind.Simple)
            {
                SimpleParticleProperties properties = new(
                    particle.Quantity,
                    particle.Quantity,
                    particle.Color,
                    new Vec3d(particle.MinX, particle.MinY, particle.MinZ),
                    new Vec3d(particle.MaxX, particle.MaxY, particle.MaxZ),
                    new Vec3f(particle.MinVelocityX, particle.MinVelocityY, particle.MinVelocityZ),
                    new Vec3f(particle.MaxVelocityX, particle.MaxVelocityY, particle.MaxVelocityZ),
                    particle.LifeLength,
                    particle.GravityEffect)
                {
                    ParticleModel = (EnumParticleModel)particle.ParticleModel,
                    MinSize = particle.Scale,
                    MaxSize = particle.Scale
                };
                SpawnReplayParticles(properties, priority: false, ageSeconds);
                return;
            }

            IParticlePropertiesProvider? provider = CreateReplayParticleProvider(particle.ProviderType);
            if (provider is null)
            {
                return;
            }

            using MemoryStream stream = new(particle.ProviderData, writable: false);
            using BinaryReader reader = new(stream);
            provider.FromBytes(reader, capi.World);
            SpawnReplayParticles(
                provider,
                provider is BlockBreakingParticleProps || provider.IgnoreUserConfig,
                ageSeconds);
        }
        catch (Exception exception)
        {
            LogReplayParticleFailureOnce(particle.ProviderType, exception);
        }
    }

    private IParticlePropertiesProvider? CreateReplayParticleProvider(string providerTypeName)
    {
        if (!replayParticleProviderFactories.TryGetValue(providerTypeName, out var factory))
        {
            factory = ResolveReplayParticleProviderFactory(providerTypeName);
            replayParticleProviderFactories[providerTypeName] = factory;
        }

        return factory();
    }

    private Func<IParticlePropertiesProvider?> ResolveReplayParticleProviderFactory(string providerTypeName)
    {
        Type? providerType = Type.GetType(providerTypeName, throwOnError: false);
        if (providerType is null || !typeof(IParticlePropertiesProvider).IsAssignableFrom(providerType))
        {
            LogReplayParticleFailureOnce(
                providerTypeName,
                new TypeLoadException("The recorded particle provider type is unavailable."));
            return static () => null;
        }

        try
        {
            _ = capi.ClassRegistry.CreateParticlePropertyProvider(providerType);
            return () => capi.ClassRegistry.CreateParticlePropertyProvider(providerType);
        }
        catch (Exception registryException)
        {
            // WeatherParticleProps inherits the complete serialized SimpleParticleProperties
            // payload but is not registered in the class registry. Replaying that payload as
            // the base provider preserves the recorded rain/snow volume without its transient
            // rain-height cache.
            if (typeof(SimpleParticleProperties).IsAssignableFrom(providerType))
            {
                return static () => new SimpleParticleProperties();
            }

            ConstructorInfo? constructor = providerType.GetConstructor(Type.EmptyTypes);
            if (constructor is not null)
            {
                return () => constructor.Invoke(null) as IParticlePropertiesProvider;
            }

            LogReplayParticleFailureOnce(providerTypeName, registryException);
            return static () => null;
        }
    }

    private void LogReplayParticleFailureOnce(string providerType, Exception exception)
    {
        string key = providerType + "|" + exception.GetType().FullName + "|" + exception.Message;
        if (!replayParticleFailureWarnings.Add(key))
        {
            return;
        }

        capi.Logger.Warning(
            "VS Director: replay particle provider '{0}' could not be restored; matching events will be skipped: {1}",
            string.IsNullOrWhiteSpace(providerType) ? "(simple)" : providerType,
            exception.Message);
    }

    private void SpawnReplayParticles(
        IParticlePropertiesProvider provider,
        bool priority,
        float ageSeconds = 0f)
    {
        bool dedicated = dedicatedReplayRenderer.IsActive;
        IParticlePool? pool = GetReplayParticlePool(provider.ParticleModel);
        FastParticlePool? particles = GetFastParticlePool(pool);
        if (pool is null || particles is null)
        {
            if (dedicated)
            {
                LogReplayParticleFailureOnce(
                    "dedicated-model:" + provider.ParticleModel,
                    new NotSupportedException(
                        "The dedicated replay particle pool does not support this model."));
                return;
            }

            capi.World.SpawnParticles(provider);
            return;
        }

        int availableSlots = DirectorReplayPlaybackPolicy.AvailableParticleSlots(
            ReplayParticleAliveCount(),
            dedicated ? DedicatedMaxReplayParticles : MaxReplayParticles,
            dedicated
                ? DedicatedReservedPriorityReplayParticles
                : ReservedPriorityReplayParticles,
            priority);
        if (availableSlots <= 0)
        {
            return;
        }

        ParticleBase? previousFirst = particles.FirstAlive;
        provider.Init(capi);
        pool.SpawnParticles(provider);
        int retained = 0;
        for (ParticleBase? current = particles.FirstAlive;
            current is not null && !ReferenceEquals(current, previousFirst);)
        {
            ParticleBase? next = current.Next;
            if (retained >= availableSlots)
            {
                particles.Kill(current);
                current = next;
                continue;
            }

            if (!float.IsFinite(current.LifeLength))
            {
                current.LifeLength = Math.Max(0.001f, provider.LifeLength * 5f);
            }
            float age = float.IsFinite(ageSeconds) ? Math.Max(0f, ageSeconds) : 0f;
            if (age >= current.LifeLength)
            {
                particles.Kill(current);
                current = next;
                continue;
            }
            if (age > 0f)
            {
                current.SecondsAlive = age;
                current.Position.Add(
                    current.Velocity.X * age,
                    current.Velocity.Y * age,
                    current.Velocity.Z * age);
            }
            retained++;
            current = next;
        }
    }

    private int ReplayParticleAliveCount()
    {
        int total = 0;
        foreach (IParticlePool pool in ReplayParticlePools())
        {
            FastParticlePool? particles = GetFastParticlePool(pool);
            if (particles is not null)
            {
                total = total > int.MaxValue - particles.AliveCount
                    ? int.MaxValue
                    : total + Math.Max(0, particles.AliveCount);
            }
        }
        return total;
    }

    private IParticlePool? GetReplayParticlePool(EnumParticleModel model)
    {
        if (dedicatedReplayRenderer.IsActive)
        {
            return dedicatedReplayRenderer.GetParticlePool(model);
        }

        object? manager = ParticleManagerField?.GetValue(game);
        object? system = manager is null ? null : ParticleSystemField?.GetValue(manager);
        IParticlePool[]? pools = system is null
            ? null
            : MainThreadParticlePoolsField?.GetValue(system) as IParticlePool[];
        int index = (int)model;
        return pools is not null && index >= 0 && index < pools.Length ? pools[index] : null;
    }

    private void ClearReplayParticles()
    {
        foreach (IParticlePool pool in ReplayParticlePools())
        {
            FastParticlePool? particles = GetFastParticlePool(pool);
            if (particles is null)
            {
                continue;
            }

            ParticleBase? current = particles.FirstAlive;
            while (current is not null)
            {
                ParticleBase? next = current.Next;
                particles.Kill(current);
                current = next;
            }
            SetParticlePoolAliveCount(pool, particles.AliveCount);
        }
    }

    private IParticlePool[] ReplayParticlePools()
    {
        if (dedicatedReplayRenderer.IsActive)
        {
            return dedicatedReplayRenderer.GetParticlePools();
        }

        object? manager = ParticleManagerField?.GetValue(game);
        object? system = manager is null ? null : ParticleSystemField?.GetValue(manager);
        return system is null
            ? Array.Empty<IParticlePool>()
            : MainThreadParticlePoolsField?.GetValue(system) as IParticlePool[]
                ?? Array.Empty<IParticlePool>();
    }

    private static FastParticlePool? GetFastParticlePool(IParticlePool? pool)
        => pool?.GetType().GetField(
            "ParticlesPool",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pool)
            as FastParticlePool;

    private static void SetParticlePoolAliveCount(IParticlePool pool, int value)
    {
        pool.GetType().GetProperty(
            nameof(IParticlePool.QuantityAlive),
            BindingFlags.Instance | BindingFlags.Public)?.SetValue(pool, value);
    }

    private static int FirstEventAfter<T>(IReadOnlyList<T> events, double timeSeconds) where T : class
        => DirectorReplayTimeSearch.UpperBound(events, timeSeconds + TimeEpsilon, static e => e switch
        {
            DirectorReplayParticleEvent particle => particle.TimeSeconds,
            DirectorReplaySoundEvent sound => sound.TimeSeconds, _ => 0d
        });

    private static void ApplyEntityState(Entity entity, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        long entityId = entity.EntityId;
        using MemoryStream stream = new(data, writable: false);
        using BinaryReader reader = new(stream);
        entity.FromBytes(reader, true);
        entity.EntityId = entityId;
    }

    private static void ConfigureReplayPlayer(EntityPlayer player, bool markShapeModified)
    {
        player.TpAnimManager.UseFpAnmations = false;
        player.SelfFpAnimManager.UseFpAnmations = false;
        player.BodyYawLimits = null;
        player.HeadYawLimits = null;
        if (markShapeModified)
        {
            player.MarkShapeModified();
        }
    }

    private static void DetachReplayMount(Entity entity)
    {
        if (entity is not EntityAgent agent)
        {
            return;
        }

        IMountableSeat? seat = agent.MountedOn;
        try
        {
            MountedOnSetter?.Invoke(agent, new object?[] { null });
        }
        catch (Exception)
        {
        }

        if (seat is not null)
        {
            ClearSeatPassenger(seat, agent);
            seat.PassengerEntityIdForInit = 0L;
            seat.Controls?.StopAllMovement();
        }

        if (agent.WatchedAttributes.HasAttribute("mountedOn"))
        {
            agent.WatchedAttributes.RemoveAttribute("mountedOn");
        }
        if (agent is EntityPlayer player)
        {
            player.BodyYawLimits = null;
            player.HeadYawLimits = null;
        }
    }

    private static void ClearReplaySeatPassengers(Entity entity)
    {
        EntityBehavior? seatable = entity.GetBehavior("seatable");
        PropertyInfo? seatsProperty = seatable?.GetType().GetProperty(
            "Seats",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (seatsProperty?.GetValue(seatable) is not IEnumerable seats)
        {
            return;
        }

        foreach (object? value in seats)
        {
            if (value is not IMountableSeat seat)
            {
                continue;
            }

            Entity? passenger = seat.Passenger;
            if (passenger is EntityAgent agent && ReferenceEquals(agent.MountedOn, seat))
            {
                DetachReplayMount(agent);
            }
            else if (passenger is not null)
            {
                ClearSeatPassenger(seat, passenger);
            }
            seat.PassengerEntityIdForInit = 0L;
            seat.Controls?.StopAllMovement();
        }
    }

    private static void ClearSeatPassenger(IMountableSeat seat, Entity expectedPassenger)
    {
        if (!ReferenceEquals(seat.Passenger, expectedPassenger))
        {
            return;
        }

        try
        {
            seat.GetType().GetProperty(
                nameof(IMountableSeat.Passenger),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(seat, null);
        }
        catch (Exception)
        {
        }
    }

    private void ApplySample(
        Entity entity,
        DirectorReplayActorSample sample,
        ActorBinding binding,
        bool forceAnimations,
        bool updateAnimations = true,
        bool poseAlreadyApplied = false)
    {
        if (!poseAlreadyApplied)
        {
            ApplyReplayPose(entity, sample);
        }
        ApplyReplayItemRenderState(entity, sample);
        if (entity is EntityAgent agent)
        {
            float replayBodyYaw = ReplayBodyYaw(entity, sample);
            agent.BodyYaw = replayBodyYaw;
            if (sample.HasAgentState)
            {
                agent.Controls.FromInt(sample.Controls);
                agent.ServerControls.FromInt(sample.ServerControls);
                agent.BodyYawServer = entity is EntityPlayer && sample.HasThirdPersonPose
                    ? NonZeroPlayerBodyYaw(replayBodyYaw)
                    : sample.BodyYawServer;
            }
        }

        if (entity is EntityPlayer player)
        {
            bool equipmentChanged = ApplyReplayEquipment(
                binding,
                sample,
                showEquipment: true);
            if (binding.IsGhost && !binding.PlayerSkinTexturePrepared)
            {
                // The clothing behavior reloads its overlays before its first
                // texture-source lookup, so allocate the skin region up front.
                binding.PlayerSkinTexturePrepared = TryPrepareReplayPlayerSkinTexture(player);
            }
            player.PrevFrameCanStandUp = sample.HasPlayerPostureState
                ? sample.CanStandUp
                : true;
            player.WalkYaw = sample.HasPlayerState ? sample.WalkYaw : sample.BodyYaw;
            player.WalkPitch = sample.HasPlayerState ? sample.WalkPitch : 0f;
            if (sample.HasPlayerState)
            {
                player.Controls.HandUse = NormalizeHandUse(sample.HandUse);
                player.ServerControls.HandUse = NormalizeHandUse(sample.ServerHandUse);
                ApplyReplayHandItems(binding, sample, showHeldItems: true);
            }
            else if (binding.IsGhost)
            {
                player.Controls.StopAllMovement();
                player.ServerControls.StopAllMovement();
                player.Controls.HandUse = EnumHandInteract.None;
                player.ServerControls.HandUse = EnumHandInteract.None;
            }
            ConfigureReplayPlayer(player, markShapeModified: equipmentChanged);
        }

        IReadOnlyList<string> animations = sample.Animations;
        IAnimationManager? animationManager = entity.AnimManager;
        if (animationManager is null || !updateAnimations)
        {
            return;
        }

        if (forceAnimations)
        {
            if (DirectorReplayPlaybackPolicy.ShouldResetActorAnimationManager(
                    forceAnimations,
                    sample.AnimationStates.Count > 0))
            {
                ResetReplayAnimations(binding, animationManager);
            }
            else
            {
                // Exact samples are reconciled state by state below. Clearing
                // the manager here used to destroy a valid pose whenever an
                // actor re-entered the frustum or the camera changed view.
                ResetReplayAnimationTracking(binding);
            }
        }

        if (entity is EntityPlayer replayPlayer)
        {
            // First-person capture cannot describe all full-body player
            // layers. Let the TP manager rebuild locomotion from the recorded
            // controls and pose instead of forcing its incomplete animation
            // set frame by frame. Explicit non-control actions are still
            // reconciled by SyncReplayPlayerAnimations.
            RunReplayPlayerAnimationTick(replayPlayer, sample);
            SyncReplayPlayerAnimations(binding, replayPlayer, animationManager, sample);
        }
        else
        {
            SyncExactReplayAnimations(binding, entity, animationManager, sample);
        }

        if (entity is not EntityPlayer)
        {
            ApplyRecordedAnimationState(
                animationManager,
                sample.AnimationStates,
                recalculatePose: false,
                onlyActive: true,
                applyRecordedRenderMetadata: true);
        }
    }

    private static void ApplyReplayPose(Entity entity, DirectorReplayActorSample sample)
    {
        ApplyReplayPosition(
            entity,
            new DirectorReplayActorPosition(sample.X, sample.Y, sample.Z, sample.Dimension));
        entity.Pos.Yaw = sample.Yaw;
        entity.Pos.Pitch = sample.Pitch;
        entity.Pos.Roll = sample.Roll;
        entity.Pos.HeadYaw = ReplayHeadYaw(entity, sample);
        entity.Pos.HeadPitch = ReplayHeadPitch(entity, sample);
        entity.Pos.Motion.Set(sample.MotionX, sample.MotionY, sample.MotionZ);
        if (!sample.HasRenderState)
        {
            return;
        }

        entity.OnGround = sample.OnGround;
        entity.FeetInLiquid = sample.FeetInLiquid;
        entity.Swimming = sample.Swimming;
        entity.ClimbingOnFace = string.IsNullOrWhiteSpace(sample.ClimbingOnFace)
            ? null
            : BlockFacing.FromCode(sample.ClimbingOnFace);
        entity.LocalEyePos.Set(sample.LocalEyePosX, sample.LocalEyePosY, sample.LocalEyePosZ);
        entity.SelectionBox.Y2 = sample.SelectionBoxY2;
        entity.OriginSelectionBox.Y2 = sample.SelectionBoxY2;
        entity.CollisionBox.Y2 = sample.CollisionBoxY2;
        entity.OriginCollisionBox.Y2 = sample.CollisionBoxY2;
    }

    private bool TryPrepareReplayPlayerSkinTexture(EntityPlayer player)
    {
        EntityPlayer? previous = ReplayPlayerInventoryContext.Current;
        ReplayPlayerInventoryContext.Current = player;
        try
        {
            _ = player.GetTextureSource();
            return ReplayClothingTextureGuard.AreTextureSpacesAllocated(
                player.Properties?.Client?.Behaviors);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: replay player skin texture could not be prepared: {0}",
                exception.GetBaseException().Message);
            return false;
        }
        finally
        {
            ReplayPlayerInventoryContext.Current = previous;
        }
    }

    private static void ApplyReplayPosition(
        Entity entity,
        DirectorReplayActorPosition position)
    {
        entity.Pos.SetPos(position.X, position.Y, position.Z);
        entity.Pos.Dimension = position.Dimension;
    }

    private static void ResetReplayAnimations(ActorBinding binding, IAnimationManager manager)
    {
        StopAllAnimationsImmediately(manager);

        ResetReplayAnimationTracking(binding);
    }

    private static void ResetReplayAnimationTracking(ActorBinding binding)
    {
        binding.ExplicitReplayAnimations.Clear();
        binding.LastAnimations.Clear();
    }

    private static void StopAllAnimationsImmediately(IAnimationManager? manager)
    {
        if (manager is null)
        {
            return;
        }
        manager.StopAllAnimations();
        manager.ActiveAnimationsByAnimCode.Clear();
        if (manager.Animator?.Animations is not { } runningAnimations)
        {
            return;
        }
        foreach (RunningAnimation running in runningAnimations)
        {
            if (running.Running || running.Active)
            {
                running.Stop();
            }
        }
    }

    private static void StopAnimationImmediately(IAnimationManager manager, string animationCode)
    {
        if (string.IsNullOrWhiteSpace(animationCode))
        {
            return;
        }
        manager.ActiveAnimationsByAnimCode.Remove(animationCode);
        if (manager.Animator?.Animations is not { } runningAnimations)
        {
            return;
        }
        foreach (RunningAnimation running in runningAnimations)
        {
            if (string.Equals(running.Animation?.Code, animationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(running.meta?.Animation, animationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(running.meta?.Code, animationCode, StringComparison.OrdinalIgnoreCase))
            {
                running.Stop();
            }
        }
    }

    private void RunReplayPlayerAnimationTick(EntityPlayer player, DirectorReplayActorSample sample)
    {
        bool savedShadowPass = player.selfNowShadowPass;
        try
        {
            // The local first-person player deliberately keeps idle active
            // alongside locomotion. A replay ghost must be evaluated with the
            // same observer semantics as a remote third-person player.
            player.selfNowShadowPass = true;
            player.OnGameTick(0f);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug(
                "VS Director: replay player animation tick for entity {0} failed: {1}",
                player.EntityId,
                exception.Message);
        }
        finally
        {
            player.selfNowShadowPass = savedShadowPass;
            ApplyReplayPose(player, sample);
            player.BodyYaw = ReplayBodyYaw(player, sample);
            if (sample.HasAgentState)
            {
                player.BodyYawServer = sample.HasThirdPersonPose
                    ? NonZeroPlayerBodyYaw(player.BodyYaw)
                    : sample.BodyYawServer;
            }
            player.WalkYaw = sample.HasPlayerState ? sample.WalkYaw : sample.BodyYaw;
            player.WalkPitch = sample.HasPlayerState ? sample.WalkPitch : 0f;
        }
    }

    private static float ReplayBodyYaw(Entity entity, DirectorReplayActorSample sample)
        => entity is EntityPlayer && sample.HasThirdPersonPose
            ? sample.ThirdPersonBodyYaw
            : sample.BodyYaw;

    private static float ReplayHeadYaw(Entity entity, DirectorReplayActorSample sample)
        => entity is EntityPlayer && sample.HasThirdPersonPose
            ? sample.ThirdPersonHeadYaw
            : sample.HeadYaw;

    private static float ReplayHeadPitch(Entity entity, DirectorReplayActorSample sample)
        => entity is EntityPlayer && sample.HasThirdPersonPose
            ? sample.ThirdPersonHeadPitch
            : sample.HeadPitch;

    private static float NonZeroPlayerBodyYaw(float bodyYaw)
        => Math.Abs(bodyYaw) < 0.000001f ? 0.000001f : bodyYaw;

    private static void SyncReplayPlayerAnimations(
        ActorBinding binding,
        EntityPlayer player,
        IAnimationManager animationManager,
        DirectorReplayActorSample sample)
    {
        HashSet<string> desiredExplicitKeys = binding.DesiredExplicitAnimationKeys;
        desiredExplicitKeys.Clear();
        foreach (DirectorReplayAnimationSample state in sample.AnimationStates)
        {
            if (IsControlDrivenAnimation(player, state))
            {
                continue;
            }

            desiredExplicitKeys.Add(
                FindActiveAnimationKey(animationManager, state)
                ?? AnimationManagerKey(player, state));
        }

        List<string> keysToStop = binding.AnimationKeysToStop;
        keysToStop.Clear();
        foreach (string code in binding.ExplicitReplayAnimations)
        {
            if (desiredExplicitKeys.Contains(code))
            {
                continue;
            }

            keysToStop.Add(code);
        }
        foreach (string code in keysToStop)
        {
            StopAnimationImmediately(animationManager, code);
            binding.ExplicitReplayAnimations.Remove(code);
        }

        foreach (DirectorReplayAnimationSample state in sample.AnimationStates)
        {
            if (IsControlDrivenAnimation(player, state))
            {
                continue;
            }

            string? expectedKey = FindActiveAnimationKey(animationManager, state);
            if (expectedKey is not null)
            {
                binding.ExplicitReplayAnimations.Add(expectedKey);
                continue;
            }

            string? activeKey = StartReplayAnimation(player, animationManager, state);
            if (activeKey is not null)
            {
                binding.ExplicitReplayAnimations.Add(activeKey);
            }
        }

        binding.LastAnimations.Clear();
        binding.LastAnimations.AddRange(sample.Animations);
    }

    private static void SyncExactReplayAnimations(
        ActorBinding binding,
        Entity entity,
        IAnimationManager animationManager,
        DirectorReplayActorSample sample)
    {
        if (sample.AnimationStates.Count == 0)
        {
            SyncLegacyReplayAnimations(binding, animationManager, sample.Animations);
            return;
        }

        List<DirectorReplayAnimationSample> desiredStates = binding.DesiredAnimationStates;
        desiredStates.Clear();
        foreach (DirectorReplayAnimationSample state in sample.AnimationStates)
        {
            if (!ShouldStopSwimmingAnimation(entity, sample, state))
            {
                desiredStates.Add(state);
            }
        }

        List<string> keysToStop = binding.AnimationKeysToStop;
        keysToStop.Clear();
        foreach (string activeKey in animationManager.ActiveAnimationsByAnimCode.Keys)
        {
            bool matched = false;
            foreach (DirectorReplayAnimationSample state in desiredStates)
            {
                if (ActiveAnimationMatches(animationManager, activeKey, state))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                keysToStop.Add(activeKey);
            }
        }
        foreach (string activeKey in keysToStop)
        {
            StopAnimationImmediately(animationManager, activeKey);
        }

        StopExitedSwimmingAnimations(entity, animationManager, sample);
        binding.ExplicitReplayAnimations.Clear();
        foreach (DirectorReplayAnimationSample state in desiredStates)
        {
            string? expectedKey = FindActiveAnimationKey(animationManager, state);
            if (expectedKey is not null)
            {
                binding.ExplicitReplayAnimations.Add(expectedKey);
                continue;
            }

            string? activeKey = StartReplayAnimation(entity, animationManager, state);
            if (activeKey is not null)
            {
                binding.ExplicitReplayAnimations.Add(activeKey);
            }
        }

        binding.LastAnimations.Clear();
        foreach (DirectorReplayAnimationSample state in desiredStates)
        {
            binding.LastAnimations.Add(state.Code);
        }
    }

    private static bool ShouldStopSwimmingAnimation(
        Entity entity,
        DirectorReplayActorSample sample,
        DirectorReplayAnimationSample state)
        => sample.HasRenderState
            && !sample.Swimming
            && entity.Properties?.Habitat != EnumHabitat.Underwater
            && (IsSwimmingAnimationCode(state.Code) || IsSwimmingAnimationCode(state.StateCode));

    private static void StopExitedSwimmingAnimations(
        Entity entity,
        IAnimationManager animationManager,
        DirectorReplayActorSample sample)
    {
        if (!sample.HasRenderState
            || sample.Swimming
            || entity.Properties?.Habitat == EnumHabitat.Underwater
            || animationManager.Animator?.Animations is not { } runningAnimations)
        {
            return;
        }

        foreach (RunningAnimation running in runningAnimations)
        {
            if ((running.Running || running.Active)
                && (IsSwimmingAnimationCode(running.Animation?.Code)
                    || IsSwimmingAnimationCode(running.meta?.Code)
                    || IsSwimmingAnimationCode(running.meta?.Animation)))
            {
                running.Stop();
            }
        }
    }

    private static bool IsSwimmingAnimationCode(string? code)
        => !string.IsNullOrWhiteSpace(code)
            && code.Contains("swim", StringComparison.OrdinalIgnoreCase);

    private static void SyncLegacyReplayAnimations(
        ActorBinding binding,
        IAnimationManager animationManager,
        IReadOnlyList<string> animations)
    {
        bool changed = animations.Count != binding.LastAnimations.Count
            || !animations.SequenceEqual(binding.LastAnimations, StringComparer.OrdinalIgnoreCase);
        if (!changed)
        {
            return;
        }

        StopAllAnimationsImmediately(animationManager);
        binding.LastAnimations.Clear();
        binding.LastAnimations.AddRange(animations);
        foreach (string animation in animations)
        {
            animationManager.StartAnimation(animation);
        }
    }

    private static bool IsControlDrivenAnimation(Entity entity, DirectorReplayAnimationSample state)
        => FindAnimationMetadata(entity, state)?.TriggeredBy is not null;

    private static bool ShouldApplyRecordedAnimationRenderMetadata(Entity entity)
        => entity is not EntityPlayer;

    private static string? StartReplayAnimation(
        Entity entity,
        IAnimationManager animationManager,
        DirectorReplayAnimationSample state)
    {
        bool applyRecordedRenderMetadata = ShouldApplyRecordedAnimationRenderMetadata(entity);
        AnimationMetaData? metadata = FindAnimationMetadata(entity, state);
        if (metadata is not null)
        {
            AnimationMetaData replayMetadata = metadata.Clone();
            replayMetadata.AnimationSpeed = float.IsFinite(state.AnimationSpeed)
                ? state.AnimationSpeed
                : replayMetadata.AnimationSpeed;
            if (applyRecordedRenderMetadata)
            {
                ApplyRecordedAnimationMetadata(replayMetadata, state);
            }
            try
            {
                animationManager.StartAnimation(replayMetadata);
                return replayMetadata.Animation;
            }
            catch (Exception)
            {
                return null;
            }
        }

        string stateKey = AnimationStateKey(state);
        if (stateKey.Length == 0 || animationManager.Animator is null)
        {
            return null;
        }

        AnimationMetaData fallback = new()
        {
            Code = string.IsNullOrWhiteSpace(state.Code) ? stateKey : state.Code,
            Animation = stateKey,
            AnimationSpeed = float.IsFinite(state.AnimationSpeed) ? state.AnimationSpeed : 1f
        };
        if (applyRecordedRenderMetadata)
        {
            ApplyRecordedAnimationMetadata(fallback, state);
        }
        try
        {
            return animationManager.TryStartAnimation(fallback) ? stateKey : null;
        }
        catch (Exception)
        {
            // Some entity renderers create their animator one frame after the
            // replay ghost. The next playback frame will retry once it exists.
            return null;
        }
    }

    private static void ApplyRecordedAnimationMetadata(
        AnimationMetaData metadata,
        DirectorReplayAnimationSample state)
    {
        metadata.Weight = float.IsFinite(state.Weight) ? Math.Max(0f, state.Weight) : metadata.Weight;
        metadata.EaseInSpeed = float.IsFinite(state.EaseInSpeed)
            ? Math.Max(0f, state.EaseInSpeed)
            : metadata.EaseInSpeed;
        metadata.EaseOutSpeed = float.IsFinite(state.EaseOutSpeed)
            ? Math.Max(0f, state.EaseOutSpeed)
            : metadata.EaseOutSpeed;
        if (Enum.IsDefined(typeof(EnumAnimationBlendMode), state.BlendMode))
        {
            metadata.BlendMode = (EnumAnimationBlendMode)state.BlendMode;
        }
        metadata.ElementWeight = state.ElementWeights is null
            ? new Dictionary<string, float>()
            : new Dictionary<string, float>(state.ElementWeights, StringComparer.OrdinalIgnoreCase);
        metadata.ElementBlendMode = state.ElementBlendModes is null
            ? new Dictionary<string, EnumAnimationBlendMode>(StringComparer.OrdinalIgnoreCase)
            : state.ElementBlendModes
                .Where(item => Enum.IsDefined(typeof(EnumAnimationBlendMode), item.Value))
                .ToDictionary(
                    item => item.Key,
                    item => (EnumAnimationBlendMode)item.Value,
                    StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyRecordedAnimationMetadataInPlace(
        AnimationMetaData metadata,
        DirectorReplayAnimationSample state)
    {
        metadata.Weight = float.IsFinite(state.Weight) ? Math.Max(0f, state.Weight) : metadata.Weight;
        metadata.EaseInSpeed = float.IsFinite(state.EaseInSpeed)
            ? Math.Max(0f, state.EaseInSpeed)
            : metadata.EaseInSpeed;
        metadata.EaseOutSpeed = float.IsFinite(state.EaseOutSpeed)
            ? Math.Max(0f, state.EaseOutSpeed)
            : metadata.EaseOutSpeed;
        if (Enum.IsDefined(typeof(EnumAnimationBlendMode), state.BlendMode))
        {
            metadata.BlendMode = (EnumAnimationBlendMode)state.BlendMode;
        }

        metadata.ElementWeight ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        metadata.ElementWeight.Clear();
        if (state.ElementWeights is not null)
        {
            foreach ((string key, float value) in state.ElementWeights)
            {
                metadata.ElementWeight[key] = value;
            }
        }

        metadata.ElementBlendMode ??=
            new Dictionary<string, EnumAnimationBlendMode>(StringComparer.OrdinalIgnoreCase);
        metadata.ElementBlendMode.Clear();
        if (state.ElementBlendModes is null)
        {
            return;
        }
        foreach ((string key, int value) in state.ElementBlendModes)
        {
            if (Enum.IsDefined(typeof(EnumAnimationBlendMode), value))
            {
                metadata.ElementBlendMode[key] = (EnumAnimationBlendMode)value;
            }
        }
    }

    private static AnimationMetaData? FindAnimationMetadata(
        Entity entity,
        DirectorReplayAnimationSample state)
    {
        string code = state.Code?.Trim() ?? string.Empty;
        string stateKey = AnimationStateKey(state);
        AnimationMetaData[]? animations = entity.Properties?.Client?.Animations;
        AnimationMetaData? exact = animations?.FirstOrDefault(metadata =>
            metadata is not null
            && string.Equals(metadata.Animation, stateKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(metadata.Code, code, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (code.Length > 0
            && entity.Properties?.Client?.AnimationsByMetaCode?.TryGetValue(code, out AnimationMetaData? byCode) == true
            && (stateKey.Length == 0
                || string.Equals(byCode.Animation, stateKey, StringComparison.OrdinalIgnoreCase)))
        {
            return byCode;
        }

        AnimationMetaData? byAnimation = animations?.FirstOrDefault(metadata =>
            metadata is not null
            && string.Equals(metadata.Animation, stateKey, StringComparison.OrdinalIgnoreCase));
        if (byAnimation is not null)
        {
            return byAnimation;
        }

        return code.Length > 0
            && entity.Properties?.Client?.AnimationsByMetaCode?.TryGetValue(code, out byCode) == true
                ? byCode
                : animations?.FirstOrDefault(metadata => metadata is not null
                    && string.Equals(metadata.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private static string AnimationStateKey(DirectorReplayAnimationSample state)
        => string.IsNullOrWhiteSpace(state.StateCode)
            ? state.Code?.Trim() ?? string.Empty
            : state.StateCode.Trim();

    private static string AnimationManagerKey(Entity entity, DirectorReplayAnimationSample state)
    {
        AnimationMetaData? metadata = FindAnimationMetadata(entity, state);
        return string.IsNullOrWhiteSpace(metadata?.Animation)
            ? AnimationStateKey(state)
            : metadata.Animation.Trim();
    }

    private static string? FindActiveAnimationKey(
        IAnimationManager manager,
        DirectorReplayAnimationSample state)
    {
        foreach (string activeKey in manager.ActiveAnimationsByAnimCode.Keys)
        {
            if (ActiveAnimationMatches(manager, activeKey, state))
            {
                return activeKey;
            }
        }
        return null;
    }

    private static bool ActiveAnimationMatches(
        IAnimationManager manager,
        string activeKey,
        DirectorReplayAnimationSample state)
    {
        string code = state.Code?.Trim() ?? string.Empty;
        string stateKey = AnimationStateKey(state);
        if (!manager.ActiveAnimationsByAnimCode.TryGetValue(activeKey, out AnimationMetaData? metadata)
            || metadata is null)
        {
            return string.Equals(activeKey, stateKey, StringComparison.OrdinalIgnoreCase)
                || (code.Length > 0 && string.Equals(activeKey, code, StringComparison.OrdinalIgnoreCase));
        }

        bool animationMatches = stateKey.Length == 0
            || string.Equals(activeKey, stateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.Animation, stateKey, StringComparison.OrdinalIgnoreCase)
            || (code.Length > 0
                && string.Equals(stateKey, code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(metadata.Code, code, StringComparison.OrdinalIgnoreCase));
        bool metadataMatches = code.Length == 0
            || string.IsNullOrWhiteSpace(metadata.Code)
            || string.Equals(metadata.Code, code, StringComparison.OrdinalIgnoreCase);
        return animationMatches && metadataMatches;
    }

    private void ApplyReplayHandItems(
        ActorBinding binding,
        DirectorReplayActorSample sample,
        bool showHeldItems)
    {
        byte[] rightHandItem = showHeldItems ? sample.RightHandItem : Array.Empty<byte>();
        byte[] leftHandItem = showHeldItems ? sample.LeftHandItem : Array.Empty<byte>();
        if (!ItemDataEquals(binding.LastRightHandItemData, rightHandItem))
        {
            binding.RightHandSlot.Set(ResolveReplayHandItem(binding, rightHandItem));
            binding.LastRightHandItemData = rightHandItem;
        }
        if (!ItemDataEquals(binding.LastLeftHandItemData, leftHandItem))
        {
            binding.LeftHandSlot.Set(ResolveReplayHandItem(binding, leftHandItem));
            binding.LastLeftHandItemData = leftHandItem;
        }
    }

    private void PrepareReplayHandItems(ActorBinding binding)
    {
        int equipmentSlots = binding.Track.EquipmentSlotCapacity;
        int backpackSlots = binding.Track.BackpackSlotCapacity;
        foreach (DirectorReplayActorSample sample in binding.Track.Samples.IsPaged
            ? binding.Track.Samples.Take(1) : binding.Track.Samples)
        {
            if (!sample.HasPlayerState)
            {
                continue;
            }

            sample.RightHandItem = CacheReplayHandItem(binding, sample.RightHandItem);
            sample.LeftHandItem = CacheReplayHandItem(binding, sample.LeftHandItem);
            equipmentSlots = Math.Max(equipmentSlots, sample.EquipmentItems.Count);
            backpackSlots = Math.Max(backpackSlots, sample.BackpackItems.Count);
            CacheReplayInventory(binding, sample.EquipmentItems);
            CacheReplayInventory(binding, sample.BackpackItems);
        }

        if (binding.Entity is not EntityPlayer)
        {
            return;
        }

        // Older recordings can have no serialized equipment lists at all.
        // The player renderer treats a missing character inventory as a signal
        // to skip its entire clothing tessellation path, so replay players
        // must always expose the vanilla inventory shape even when every slot
        // is empty. Preserve larger modded inventories recorded by newer files.
        equipmentSlots = Math.Max(DefaultReplayEquipmentSlots, equipmentSlots);
        backpackSlots = Math.Max(DefaultReplayBackpackSlots, backpackSlots);
        binding.ReplayEquipmentInventory = new InventoryGeneric(
            equipmentSlots,
            "character",
            "vsdirector-replay-" + binding.Entity.EntityId,
            capi);
        binding.ReplayBackpackInventory = new InventoryGeneric(
            backpackSlots,
            "backpack",
            "vsdirector-replay-" + binding.Entity.EntityId,
            capi);
    }

    private bool ApplyReplayEquipment(
        ActorBinding binding,
        DirectorReplayActorSample sample,
        bool showEquipment)
    {
        IReadOnlyList<byte[]> equipment = showEquipment
            ? sample.EquipmentItems
            : Array.Empty<byte[]>();
        IReadOnlyList<byte[]> backpack = showEquipment
            ? sample.BackpackItems
            : Array.Empty<byte[]>();
        bool changed = ApplyReplayInventory(
            binding,
            binding.ReplayEquipmentInventory,
            equipment,
            binding.LastEquipmentItemData);
        return ApplyReplayInventory(
                binding,
                binding.ReplayBackpackInventory,
                backpack,
                binding.LastBackpackItemData)
            || changed;
    }

    private bool ApplyReplayInventory(
        ActorBinding binding,
        InventoryGeneric? inventory,
        IReadOnlyList<byte[]> items,
        List<byte[]?> lastItems)
    {
        if (inventory is null)
        {
            return false;
        }

        while (lastItems.Count < inventory.Count)
        {
            lastItems.Add(null);
        }

        bool changed = false;
        for (int index = 0; index < inventory.Count; index++)
        {
            byte[] data = index < items.Count ? items[index] : Array.Empty<byte>();
            if (ItemDataEquals(lastItems[index], data))
            {
                continue;
            }

            inventory[index].Itemstack = ResolveReplayHandItem(binding, data)?.Clone();
            lastItems[index] = data;
            changed = true;
        }
        return changed;
    }

    private void CacheReplayInventory(ActorBinding binding, List<byte[]> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            items[index] = CacheReplayHandItem(binding, items[index]);
        }
    }

    private byte[] CacheReplayHandItem(ActorBinding binding, byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return Array.Empty<byte>();
        }

        if (binding.HandItemSnapshots.TryGetValue(data, out ReplayHandItemSnapshot? existing))
        {
            return existing.Data;
        }

        ReplayHandItemSnapshot snapshot = new(data, DeserializeItemStack(data));
        binding.HandItemSnapshots.Add(data, snapshot);
        return snapshot.Data;
    }

    private ItemStack? ResolveReplayHandItem(ActorBinding binding, byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        return binding.HandItemSnapshots.TryGetValue(data, out ReplayHandItemSnapshot? snapshot)
            ? snapshot.Stack
            : DeserializeItemStack(data);
    }

    private static bool ItemDataEquals(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        ReadOnlySpan<byte> leftData = left ?? Array.Empty<byte>();
        ReadOnlySpan<byte> rightData = right ?? Array.Empty<byte>();
        return leftData.SequenceEqual(rightData);
    }

    private ItemStack? DeserializeItemStack(byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using MemoryStream stream = new(data, writable: false);
            using BinaryReader reader = new(stream);
            return new ItemStack(reader, capi.World);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: replay hand item could not be restored: {0}", exception.Message);
            return null;
        }
    }

    private static EnumHandInteract NormalizeHandUse(int value)
        => Enum.IsDefined(typeof(EnumHandInteract), value)
            ? (EnumHandInteract)value
            : EnumHandInteract.None;

    private static void ApplyRecordedAnimationState(
        IAnimationManager? manager,
        IReadOnlyList<DirectorReplayAnimationSample> states,
        bool recalculatePose,
        bool onlyActive = false,
        float advanceSeconds = 0f,
        bool applyRecordedRenderMetadata = true)
    {
        if (manager?.Animator is null)
        {
            return;
        }

        float poseDeltaSeconds = 0f;
        if (recalculatePose)
        {
            float dt = float.IsFinite(advanceSeconds)
                ? Math.Max(0f, advanceSeconds)
                : 0f;
            poseDeltaSeconds = DirectorReplayPlaybackPolicy.ResolveAnimatorPoseDelta(
                states.Count > 0,
                dt);
            manager.HeadController?.OnFrame(dt);
            bool needsRegistration = false;
            foreach (DirectorReplayAnimationSample state in states)
            {
                string? stateCode = FindActiveAnimationKey(manager, state);
                RunningAnimation? running = stateCode is null
                    ? null
                    : TryGetRunningAnimation(manager, stateCode);
                if (stateCode is not null
                    && (running is null || (!running.Active && !running.Running)))
                {
                    needsRegistration = true;
                    break;
                }
            }
            if (needsRegistration)
            {
                // StartAnimation updates the manager dictionary first. Register
                // a newly active RunningAnimation before restoring its frame.
                manager.Animator.OnFrame(manager.ActiveAnimationsByAnimCode, 0f);
            }
        }

        foreach (DirectorReplayAnimationSample state in states)
        {
            string? stateCode = FindActiveAnimationKey(manager, state);
            if (stateCode is null)
            {
                if (onlyActive)
                {
                    continue;
                }
                stateCode = AnimationStateKey(state);
            }
            RunningAnimation? running = TryGetRunningAnimation(manager, stateCode);
            if (running?.Animation is null)
            {
                continue;
            }

            int frameCount = Math.Max(1, running.Animation.QuantityFrames - 1);
            running.CurrentFrame = state.RecordedFrame is float recordedFrame
                ? Math.Clamp(recordedFrame, 0f, frameCount)
                : Math.Clamp(state.Progress, 0f, 1f) * frameCount;
            running.EasingFactor = Math.Clamp(state.EasingFactor, 0f, 1f);
            running.BlendedWeight = Math.Max(0f, state.BlendedWeight);
            // Keep the layer evaluable until matrices are calculated below.
            // Do not mark a newly started layer active during the state-only
            // pass: AnimatorBase uses the false-to-true transition to add it
            // to its private active-or-running list. Skipping that transition
            // leaves ActiveAnimationsByAnimCode populated but produces the
            // default pose for every animation.
            running.Iterations = 0;
            if (recalculatePose)
            {
                running.Active = true;
                running.Running = true;
            }
            if (running.meta is not null)
            {
                AnimationMetaData replayMetadata = onlyActive
                    ? running.meta
                    : running.meta.Clone();
                if (float.IsFinite(state.AnimationSpeed))
                {
                    replayMetadata.AnimationSpeed = state.AnimationSpeed;
                }
                // Player replay always renders through TpAnimManager. Its
                // local metadata owns the full-body bone masks and blend
                // modes; recorded first-person variants may only restore
                // frame timing, never replace that render configuration.
                if (applyRecordedRenderMetadata && onlyActive)
                {
                    ApplyRecordedAnimationMetadataInPlace(replayMetadata, state);
                }
                else if (applyRecordedRenderMetadata)
                {
                    ApplyRecordedAnimationMetadata(replayMetadata, state);
                }
                running.meta = replayMetadata;
                if (manager.ActiveAnimationsByAnimCode.ContainsKey(stateCode))
                {
                    manager.ActiveAnimationsByAnimCode[stateCode] = replayMetadata;
                }
            }
        }

        if (recalculatePose)
        {
            manager.Animator.OnFrame(
                manager.ActiveAnimationsByAnimCode,
                poseDeltaSeconds);

            foreach (DirectorReplayAnimationSample state in states)
            {
                string? stateCode = FindActiveAnimationKey(manager, state);
                if (stateCode is null)
                {
                    continue;
                }
                RunningAnimation? running = TryGetRunningAnimation(manager, stateCode);
                if (running is null)
                {
                    continue;
                }

                running.Iterations = state.Iterations;
                running.Active = state.IsActive;
                running.Running = state.IsRunning;
                running.NowPlaying = state.NowPlaying;
                running.ShouldRewind = state.ShouldRewind;
                running.ShouldPlayTillEnd = state.ShouldPlayTillEnd;
                running.EasingFactor = Math.Clamp(state.EasingFactor, 0f, 1f);
                running.BlendedWeight = Math.Max(0f, state.BlendedWeight);
            }
        }
    }

    private byte[] TrySerializeEntity(
        Entity entity,
        long entityId,
        bool useThirdPersonPlayerAnimations = false)
    {
        try
        {
            return DirectorReplaySerialization.SerializeEntity(
                entity,
                entityId,
                useThirdPersonPlayerAnimations);
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("VS Director: entity {0} state could not be serialized: {1}", entity.EntityId, exception.Message);
            return Array.Empty<byte>();
        }
    }

    private bool CanCaptureEvent(DirectorReplayDocument document)
        => !recordingPaused
            && ReferenceEquals(recording, document)
            && document.BlockEvents.Count + document.ParticleEvents.Count + document.SoundEvents.Count
            < config.ReplayMaxWorldEvents;

    private static bool UsesDeterministicParticleTimeline(Playback active)
        => active.Document.ParticleCaptureVersion >= DirectorReplayDocument.CurrentParticleCaptureVersion;

    private static void AddOrReplaceSample(
        DirectorReplayActorTrack track,
        DirectorReplayActorSample sample)
    {
        if (track.Samples.Count > 0 && sample.HasPlayerState)
        {
            DirectorReplayActorSample previous = track.Samples[^1];
            if (ItemDataEquals(previous.RightHandItem, sample.RightHandItem))
            {
                sample.RightHandItem = previous.RightHandItem;
            }
            if (ItemDataEquals(previous.LeftHandItem, sample.LeftHandItem))
            {
                sample.LeftHandItem = previous.LeftHandItem;
            }
        }

        if (track.Samples.Count > 0
            && Math.Abs(track.Samples[^1].TimeSeconds - sample.TimeSeconds) <= TimeEpsilon)
        {
            track.Samples[^1] = sample;
        }
        else
        {
            track.Samples.Add(sample);
        }
    }

    private void SetApplyingReplayState(bool applying)
    {
        applyingReplayState = applying;
    }

    internal void MaintainSingleplayerWorldSuspension()
    {
        if (singleplayerWorldSuspension.Maintain())
        {
            return;
        }

        capi.Logger.Warning(
            "VS Director: replay playback stopped because the world was opened to multiplayer.");
        StopPlayback();
    }

    private bool SeekForExport(double timeSeconds)
    {
        Playback? active = playback;
        if (active is null)
        {
            return false;
        }

        active.Paused = true;
        double target = Math.Clamp(timeSeconds, 0d, active.Document.DurationSeconds);
        double delta = target - active.TimeSeconds;
        if (delta < -TimeEpsilon || delta > 1d)
        {
            active.TimedEventSeekPending = true;
        }
        active.TimeSeconds = target;
        ApplyPlaybackFrame(active);
        return true;
    }

    private void PollPendingSave()
    {
        PendingReplaySave? active = pendingSave;
        if (active is null
            || !active.Operation.TryTakeCompletion(out string? path, out Exception? exception))
        {
            return;
        }

        if (exception is not null || string.IsNullOrWhiteSpace(path))
        {
            string error = exception?.Message ?? "Replay save returned no output path.";
            capi.Logger.Error("VS Director: asynchronous replay save failed: {0}", exception ?? new IOException(error));
            FailPendingSave(active, error);
            return;
        }

        CompletePendingSave(active, path);
    }

    private DirectorReplayInfo CompletePendingSave(PendingReplaySave active, string path)
    {
        DirectorReplayInfo info = new(
            path,
            active.Document.Id,
            active.Document.Name,
            active.Document.CreatedUtc,
            active.Document.DurationSeconds,
            active.Document.Actors.Count);
        if (ReferenceEquals(pendingSave, active))
        {
            pendingSave = null;
        }
        if (active.DisposeStoreWhenSaved)
        {
            active.Store.Dispose();
        }

        RecordingSaveCompleted?.Invoke(info, string.Empty);
        PlaybackStateChanged?.Invoke();
        return info;
    }

    private void FailPendingSave(PendingReplaySave active, string error)
    {
        active.Error = string.IsNullOrWhiteSpace(error) ? "replay-save-failed" : error;
        RecordingSaveCompleted?.Invoke(null, active.Error);
        PlaybackStateChanged?.Invoke();
    }

    private void RetireStore(DirectorReplayStore? retiring)
    {
        if (retiring is null)
        {
            return;
        }

        if (pendingSave is { } active && ReferenceEquals(active.Store, retiring))
        {
            active.DisposeStoreWhenSaved = true;
            return;
        }

        retiring.Dispose();
    }

    private static async Task<DirectorReplayDocument> LoadReplayWithServerCompanionAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return await DirectorReplayIO.LoadPagedAsync(path, progress, cancellationToken).ConfigureAwait(false);
    }

    private void PollPendingLoad()
    {
        PendingReplayLoad? loading = pendingLoad;
        if (loading is null || loading.Task is not { IsCompleted: true } task)
        {
            return;
        }

        pendingLoad = null;
        try
        {
            DirectorReplayDocument document = task.GetAwaiter().GetResult();
            if (document.WorldId != capi.World.SavegameIdentifier)
                throw new InvalidDataException("Open the world in which this replay was recorded.");
            if (document.ServerReplay is { RequiredForCompletePlayback: true, Available: false })
                throw new InvalidDataException("This coordinated replay requires its server companion data. Copy the matching server replay directory beside the client volume and try again.");
            document.Name = loading.Info.Name;
            store?.ApplyTimelineEdits(loading.Info.Path, document);
            // Do not build the complete route plan here.  Build() rasterizes
            // every actor sample and scans the whole world snapshot on the
            // client thread, which can stall an integrated server while the
            // document is otherwise ready.  Startup only needs the small
            // observation window around the camera and the t=0 anchor.
            DirectorReplayPreloadPlan emptyPlan = CreatePlaybackPreloadWindowPlan(
                Array.Empty<DirectorReplayPreloadColumn>());
            IReadOnlyList<DirectorReplayPreloadColumn> startupColumns =
                BuildPlaybackPreloadWindow(document, 0d, emptyPlan);
            DirectorReplayPreloadPlan preloadPlan = CreatePlaybackPreloadWindowPlan(
                startupColumns);
            PendingPlaybackStart starting = new PendingPlaybackStart(
                document,
                loading.Info.Path,
                loading.QuickMode,
                preloadPlan,
                loading.PauseAfterLoad,
                preferLiveActors: !config.ReplayUseDedicatedRenderer
                    && document.Actors.Count > IndependentActorLimitForDenseReplay);
            starting.StartupPreloadColumns = startupColumns;
            pendingPlaybackStart = starting;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            playbackLoadError = exception.Message;
            replayLoadOverlayVisible = false;
            capi.Logger.Error("VS Director: replay document could not be loaded: {0}", exception);
            CleanupFailedPlaybackStart();
        }
        finally
        {
            loading.Cancellation.Dispose();
            PlaybackStateChanged?.Invoke();
        }
    }

    private void PollPendingPreloadEditorLoad()
    {
        PendingPreloadEditorLoad? loading = pendingPreloadEditorLoad;
        if (loading?.Task is not { IsCompleted: true } task)
        {
            return;
        }

        pendingPreloadEditorLoad = null;
        try
        {
            DirectorReplayDocument document = task.GetAwaiter().GetResult();
            if (document.ServerReplay is { RequiredForCompletePlayback: true, Available: false })
                throw new InvalidDataException("This coordinated replay requires its server companion data. Copy the matching server replay directory beside the client volume and try again.");
            document.Name = loading.Info.Name;
            store?.ApplyTimelineEdits(loading.Info.Path, document);
            loadedPreloadEditor = new LoadedPreloadEditor(
                document,
                loading.Info.Path,
                DirectorReplayPreloadPlanner.Build(
                    document,
                    capi.World.Player?.PlayerUID,
                    DirectorReplayPlaybackPolicy.ShouldRebuildWorld(
                        document,
                        quickMode: false,
                        config.ReplayApplyWorldSnapshot)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: replay preload preview could not be loaded: {0}",
                exception.Message);
        }
        finally
        {
            loading.Cancellation.Dispose();
            PlaybackStateChanged?.Invoke();
        }
    }

    private void CancelPreloadEditorLoad()
    {
        PendingPreloadEditorLoad? loading = pendingPreloadEditorLoad;
        pendingPreloadEditorLoad = null;
        if (loading is null)
        {
            return;
        }

        loading.Cancellation.Cancel();
        loading.Cancellation.Dispose();
    }

    private void ClearPreloadEditor()
    {
        CancelPreloadEditorLoad();
        loadedPreloadEditor = null;
    }

    private void ProcessPendingPlaybackStart()
    {
        PendingPlaybackStart? starting = pendingPlaybackStart;
        if (starting is null)
        {
            return;
        }

        try
        {
            if (!starting.ChunksReady)
            {
                UpdatePreloadReadiness(starting);
                // Only the first replay observation window is required before
                // actors can be initialized. The rest of the route is
                // intentionally streamed by the background preload client;
                // waiting for every expanded column can queue thousands of
                // server chunk loads and trigger the host watchdog.
                if (!IsPlaybackObservationAreaReady(starting.Document, 0d))
                {
                    return;
                }

                starting.ChunksReady = true;
                capi.Logger.Debug(
                    "VS Director: replay observation chunks are ready; initializing {0} actors.",
                    starting.Document.Actors.Count);
            }

            Stopwatch actorWork = Stopwatch.StartNew();
            int createdThisFrame = 0;
            while (createdThisFrame < PlaybackActorsPerFrame
                && starting.NextActorIndex < starting.Document.Actors.Count
                && (createdThisFrame == 0
                    || actorWork.Elapsed.TotalMilliseconds < PlaybackActorWorkBudgetMilliseconds))
            {
                DirectorReplayActorTrack actor = starting.Document.Actors[starting.NextActorIndex++];
                ActorBinding? binding = CreatePlaybackBinding(
                    actor,
                    starting.PreferLiveActors,
                    starting.ClaimedEntityIds,
                    out bool missing);
                createdThisFrame++;
                if (binding is not null)
                {
                    starting.Bindings.Add(binding);
                    InitializePlaybackBinding(binding);
                }
                else if (missing)
                {
                    starting.MissingActors++;
                }
            }

            if (starting.NextActorIndex < starting.Document.Actors.Count)
            {
                return;
            }

            if (starting.PreferLiveActors)
            {
                int reused = starting.Bindings.Count(binding => !binding.IsGhost);
                capi.Logger.Debug(
                    "VS Director: dense replay reused {0} live actors and created {1} independent actors.",
                    reused,
                    starting.Bindings.Count - reused);
            }

            bool startedPlayback = FinishLoadedPlayback(
                starting.Document,
                starting.SourcePath,
                starting.QuickMode,
                starting.PreloadPlan,
                starting.Bindings,
                starting.PauseAfterLoad,
                out _,
                out string error);
            pendingPlaybackStart = null;
            if (!startedPlayback)
            {
                playbackLoadError = error;
                replayLoadOverlayVisible = false;
                inputLockHandler(false);
                CleanupFailedPlaybackStart();
            }
            PlaybackStateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            pendingPlaybackStart = null;
            playbackLoadError = exception.Message;
            replayLoadOverlayVisible = false;
            capi.Logger.Error("VS Director: replay actors could not be initialized: {0}", exception);
            RestorePendingBindings(starting.Bindings);
            CleanupFailedPlaybackStart();
            PlaybackStateChanged?.Invoke();
        }
    }

    private bool IsPlaybackObservationAreaReady(DirectorReplayDocument document, double timeSeconds)
    {
        if (!IsActiveCameraAreaReady())
        {
            return false;
        }

        string preferredPlayerUid = capi.World.Player?.PlayerUID ?? string.Empty;
        return !DirectorReplayAnchorSelector.TrySelect(
                document,
                timeSeconds,
                preferredPlayerUid,
                out DirectorReplayAnchorPose replayPose)
            || IsPoseChunkWindowReady(replayPose.X, replayPose.Z, replayPose.Dimension);
    }

    private bool IsActiveCameraAreaReady()
        => !controller.IsActive
            || controller.CurrentPose is not { } cameraPose
            || IsPoseChunkWindowReady(cameraPose.X, cameraPose.Z, cameraPose.Dimension);

    private void UpdatePreloadReadiness(PendingPlaybackStart starting)
    {
        long now = capi.ElapsedMilliseconds;
        if (starting.LastPreloadCheckMs > 0 && now - starting.LastPreloadCheckMs < 100)
        {
            return;
        }

        starting.LastPreloadCheckMs = now;
        int ready = 0;
        foreach (DirectorReplayPreloadColumn column in starting.StartupPreloadColumns)
        {
            if (IsPreloadColumnRendered(column))
            {
                ready++;
            }
        }

        if (ready != starting.PreloadReadyColumns)
        {
            starting.PreloadReadyColumns = ready;
            capi.Logger.Debug(
                "VS Director: replay preload client progress: {0}/{1} columns rendered.",
                ready,
                starting.StartupPreloadColumns.Count);
        }
    }

    private bool IsPreloadColumnRendered(DirectorReplayPreloadColumn column)
    {
        int chunkSize = game.WorldMap.ClientChunkSize;
        int chunkCountY = (game.WorldMap.MapSizeY + chunkSize - 1) / chunkSize;
        int dimensionOffset = column.Dimension * DirectorChunkWindow.DimensionHeight / chunkSize;
        for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
        {
            int internalChunkY = chunkY + dimensionOffset;
            if (game.WorldMap.GetChunk(column.X, internalChunkY, column.Z) is null
                || !game.WorldMap.IsChunkRendered(column.X, internalChunkY, column.Z))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsPoseChunkWindowReady(double x, double z, int dimension)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
        {
            return false;
        }

        int chunkSize = game.WorldMap.ClientChunkSize;
        int centerChunkX = (int)Math.Floor(x / chunkSize);
        int centerChunkZ = (int)Math.Floor(z / chunkSize);
        return DirectorChunkWindow.IsReady(
            centerChunkX,
            centerChunkZ,
            dimension,
            PlaybackReadyRadiusChunks,
            game.WorldMap.MapSizeY,
            chunkSize,
            (chunkX, chunkY, chunkZ) => game.WorldMap.GetChunk(chunkX, chunkY, chunkZ) is not null);
    }

    private void SetPlaybackChunkBuffering(bool buffering)
    {
        if (playbackChunkBuffering == buffering)
        {
            return;
        }

        playbackChunkBuffering = buffering;
        capi.Logger.Debug(
            "VS Director: replay remote chunk buffering {0} at {1:0.###}s.",
            buffering ? "started" : "finished",
            playback?.TimeSeconds ?? 0d);
        PlaybackStateChanged?.Invoke();
    }

    private void CleanupFailedPlaybackStart()
    {
        if (playback is not null)
        {
            StopPlayback();
            return;
        }

        foreach (long entityId in replayGhostIds.ToArray())
        {
            Entity? ghost = game.LoadedEntities.TryGetValue(entityId, out Entity? loaded)
                ? loaded
                : null;
            if (ghost is null)
            {
                continue;
            }

            try
            {
                RemoveGhost(ghost);
            }
            catch (Exception exception)
            {
                capi.Logger.Debug(
                    "VS Director: partially created replay ghost could not be removed: {0}",
                    exception.Message);
            }
        }

        replayGhostIds.Clear();
        replayThirdPersonIds.Clear();
        RestoreSuppressedLiveEntities();
        SetApplyingReplayState(false);
        singleplayerWorldSuspension.Release();
        inputLockHandler(false);
    }

    private void ClearRecordingState()
    {
        recording = null;
        recordingActors = null;
        recordingEntityActors.Clear();
        recordingReusableActorTypes.Clear();
        recordingVisibleActors.Clear();
        recordingBlockEntities.Clear();
        recordingBlockStates.Clear();
        pendingBlockChanges.Clear();
        pausedBlockChanges.Clear();
        pausedBlockResyncBatches.Clear();
        recordingSnapshotChunks.Clear();
        renderedCapturePoses.Clear();
        decodedSnapshotChunkKey = null;
        decodedSnapshotSolid = null;
        decodedSnapshotFluid = null;
        pendingEntityStateCaptures.Clear();
        pendingBlockEntityCapture = null;
        recordingElapsedSeconds = 0d;
        recordingAccumulator = 0d;
        entityStateAccumulator = 0d;
        recordingPaused = false;
        capturedParticles.Clear();
        voiceCaptureTimeline.Reset();
    }

    private static byte[] CloneBytes(byte[]? data)
        => data is null || data.Length == 0 ? Array.Empty<byte>() : (byte[])data.Clone();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.BlockChanged -= OnBlockChanged;
        capi.Event.OnEntitySpawn -= OnEntitySpawn;
        capi.Event.OnEntityLoaded -= OnEntityLoaded;
        capi.Event.OnEntityDespawn -= OnEntityDespawn;
        LeaveWorld();
        PendingReplaySave? abandonedSave = pendingSave;
        pendingSave = null;
        if (abandonedSave is not null)
        {
            Task? completion = abandonedSave.Operation.Completion;
            if (completion is null || completion.IsCompleted)
            {
                abandonedSave.Store.Dispose();
            }
            else
            {
                _ = completion.ContinueWith(
                    _ => abandonedSave.Store.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        dedicatedReplayRenderer.Dispose();
        singleplayerWorldSuspension.Dispose();
        exporter.Dispose();
    }

    private sealed class PendingReplaySave
    {
        public PendingReplaySave(
            DirectorReplayDocument document,
            DirectorReplayStore store,
            DirectorReplayCompressionProfile compression,
            bool wasPaused)
        {
            Document = document;
            Store = store;
            Compression = compression;
            WasPaused = wasPaused;
            Operation = new DirectorRetryableAsyncSave<string>(
                () => Store.SaveAsync(Document, Compression));
        }

        public DirectorReplayDocument Document { get; }

        public DirectorReplayStore Store { get; }

        public DirectorReplayCompressionProfile Compression { get; }

        public bool WasPaused { get; }

        public DirectorRetryableAsyncSave<string> Operation { get; }

        public string Error { get; set; } = string.Empty;

        public bool DisposeStoreWhenSaved { get; set; }
    }

    private sealed class Playback
    {
        public long ProfileStarted = Stopwatch.GetTimestamp(), ProfilePages;
        public int ProfileFrames;
        public double ProfileTotalMs, ProfilePeakMs, ProfileEventsMs;
        public Playback(
            DirectorReplayDocument document,
            string sourcePath,
            bool quickMode,
            DirectorReplayPreloadPlan preloadPlan,
            List<ActorBinding> bindings,
            DirectorReplayWorldPlayback? worldPlayback,
            float savedTimelapseStep,
            float savedTimelapseCurrent,
            float savedTimelapseEnd,
            float savedCalendarTimelapse,
            float savedCalendarSpeedMultiplier,
            float? savedSeasonOverride)
        {
            Document = document;
            SourcePath = sourcePath;
            QuickMode = quickMode;
            PreloadPlan = preloadPlan;
            Bindings = bindings;
            BindingsByEntityId = bindings.ToDictionary(binding => binding.Entity.EntityId);
            WorldPlayback = worldPlayback;
            SavedTimelapseStep = savedTimelapseStep;
            SavedTimelapseCurrent = savedTimelapseCurrent;
            SavedTimelapseEnd = savedTimelapseEnd;
            SavedCalendarTimelapse = savedCalendarTimelapse;
            SavedCalendarSpeedMultiplier = savedCalendarSpeedMultiplier;
            SavedSeasonOverride = savedSeasonOverride;
        }

        public DirectorReplayDocument Document { get; }

        public string SourcePath { get; }

        public bool QuickMode { get; }

        public DirectorReplayPreloadPlan PreloadPlan { get; set; }

        public List<ActorBinding> Bindings { get; }

        public Dictionary<long, ActorBinding> BindingsByEntityId { get; }

        public DirectorReplayWorldPlayback? WorldPlayback { get; set; }

        public float SavedTimelapseStep { get; }

        public float SavedTimelapseCurrent { get; }

        public float SavedTimelapseEnd { get; }

        public float SavedCalendarTimelapse { get; }

        public float SavedCalendarSpeedMultiplier { get; }

        public float? SavedSeasonOverride { get; }

        public double TimeSeconds { get; set; }

        public double AppliedTimeSeconds { get; set; } = -1d;

        public double PreparedTimeSeconds { get; set; } = -1d;

        public double Speed { get; set; } = 1d;

        public bool Paused { get; set; }

        public bool ElementFreezeActive { get; set; }

        public bool ElementFullyFrozen { get; set; }

        public int NextParticleEvent { get; set; }

        public int NextSoundEvent { get; set; }

        public int NextVoiceFrame { get; set; }

        public Dictionary<long, ILoadedSound> ReplaySounds { get; } = new();
        public Dictionary<long, DirectorReplaySoundEvent> ReplaySoundStates { get; } = new();

        public HashSet<ILoadedSound> PlaybackOwnedSounds { get; } =
            new(ReferenceEqualityComparer.Instance);

        public DirectorOpenAlNative? VoiceAudio { get; set; }
        public DirectorReplayVoiceStream? VoiceStream { get; set; }
        public bool VoiceFailureReported { get; set; }

        public HashSet<ILoadedSound> ReplayWorldOwnedSounds { get; } =
            new(ReferenceEqualityComparer.Instance);

        public Dictionary<ILoadedSound, float> SuppressedLiveSoundVolumes { get; } =
            new(ReferenceEqualityComparer.Instance);

        public HashSet<ILoadedSound> SuppressedLiveSoundStarts { get; } =
            new(ReferenceEqualityComparer.Instance);

        public HashSet<long> TimelinePausedReplaySounds { get; } = new();

        public HashSet<long> ActorAnchoredReplaySounds { get; } = new();

        public bool TimedEventSeekPending { get; set; }

        public float PendingParticleDeltaSeconds { get; set; }

        public int AnimationUpdateCursor { get; set; }

        public int DedicatedAnimationBudget { get; set; }
            = DirectorReplayActorSchedulingPolicy.DedicatedAnimationBudget;
    }

    private sealed class PendingReplayLoad
    {
        public PendingReplayLoad(DirectorReplayInfo info, bool quickMode, bool pauseAfterLoad)
        {
            Info = info;
            QuickMode = quickMode;
            PauseAfterLoad = pauseAfterLoad;
        }

        public DirectorReplayInfo Info { get; set; }

        public bool QuickMode { get; }

        public bool PauseAfterLoad { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<DirectorReplayDocument>? Task { get; set; }

        public double Progress;
    }

    private sealed class PendingPreloadEditorLoad
    {
        public PendingPreloadEditorLoad(DirectorReplayInfo info)
        {
            Info = info;
        }

        public DirectorReplayInfo Info { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<DirectorReplayDocument>? Task { get; set; }
    }

    private sealed class LoadedPreloadEditor
    {
        public LoadedPreloadEditor(
            DirectorReplayDocument document,
            string sourcePath,
            DirectorReplayPreloadPlan preloadPlan)
        {
            Document = document;
            SourcePath = sourcePath;
            PreloadPlan = preloadPlan;
        }

        public DirectorReplayDocument Document { get; }

        public string SourcePath { get; }

        public DirectorReplayPreloadPlan PreloadPlan { get; set; }
    }

    private sealed class PendingPlaybackStart
    {
        public PendingPlaybackStart(
            DirectorReplayDocument document,
            string sourcePath,
            bool quickMode,
            DirectorReplayPreloadPlan preloadPlan,
            bool pauseAfterLoad,
            bool preferLiveActors)
        {
            Document = document;
            SourcePath = sourcePath;
            QuickMode = quickMode;
            PreloadPlan = preloadPlan;
            PauseAfterLoad = pauseAfterLoad;
            PreferLiveActors = preferLiveActors;
        }

        public DirectorReplayDocument Document { get; }

        public string SourcePath { get; }

        public bool QuickMode { get; }

        public bool PauseAfterLoad { get; }

        public DirectorReplayPreloadPlan PreloadPlan { get; set; }

        public IReadOnlyList<DirectorReplayPreloadColumn> StartupPreloadColumns { get; set; }
            = Array.Empty<DirectorReplayPreloadColumn>();

        public bool PreferLiveActors { get; }

        public List<ActorBinding> Bindings { get; } = new();

        public HashSet<long> ClaimedEntityIds { get; } = new();

        public int NextActorIndex { get; set; }

        public int MissingActors { get; set; }

        public bool ChunksReady { get; set; }

        public int PreloadReadyColumns { get; set; }

        public long LastPreloadCheckMs { get; set; }
    }

    private sealed class ReplayLoadProgress : IProgress<double>
    {
        private readonly PendingReplayLoad owner;

        public ReplayLoadProgress(PendingReplayLoad owner)
        {
            this.owner = owner;
        }

        public void Report(double value)
        {
            owner.Progress = Math.Clamp(value, 0d, 1d);
        }
    }

    private sealed class ActorBinding
    {
        public ActorBinding(
            DirectorReplayActorTrack track,
            Entity entity,
            DirectorReplayActorSample savedState,
            byte[] savedFullState,
            bool savedRendered,
            bool savedShadowRendered,
            long? savedItemSpawnedMilliseconds,
            bool isGhost,
            Entity? hiddenLiveEntity,
            bool hiddenLiveRendered,
            bool? savedTpUseFpAnimations,
            bool? savedSelfUseFpAnimations)
        {
            Track = track;
            Entity = entity;
            SavedState = savedState;
            SavedFullState = savedFullState;
            SavedRendered = savedRendered;
            SavedShadowRendered = savedShadowRendered;
            SavedItemSpawnedMilliseconds = savedItemSpawnedMilliseconds;
            IsGhost = isGhost;
            HiddenLiveEntity = hiddenLiveEntity;
            HiddenLiveRendered = hiddenLiveRendered;
            SavedTpUseFpAnimations = savedTpUseFpAnimations;
            SavedSelfUseFpAnimations = savedSelfUseFpAnimations;
            ScheduledEntityPosition = entity.Pos.Copy();
        }

        public DirectorReplayActorTrack Track { get; }

        public Entity Entity { get; }

        public DirectorReplayActorSample SavedState { get; }

        public byte[] SavedFullState { get; }

        public bool SavedRendered { get; }

        public bool SavedShadowRendered { get; }

        public long? SavedItemSpawnedMilliseconds { get; }

        public bool IsGhost { get; }

        public Entity? HiddenLiveEntity { get; }

        public bool HiddenLiveRendered { get; }

        public bool? SavedTpUseFpAnimations { get; }

        public bool? SavedSelfUseFpAnimations { get; }

        public DirectorReplayActorState? LastAppliedState { get; set; }

        public bool FullStateRestorePending { get; set; } = true;

        public List<string> LastAnimations { get; } = new();

        public HashSet<string> ExplicitReplayAnimations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DesiredExplicitAnimationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> AnimationKeysToStop { get; } = new();

        public List<DirectorReplayAnimationSample> DesiredAnimationStates { get; } = new();

        public DummySlot RightHandSlot { get; } = new();

        public DummySlot LeftHandSlot { get; } = new();

        public InventoryGeneric? ReplayEquipmentInventory { get; set; }

        public InventoryGeneric? ReplayBackpackInventory { get; set; }

        public List<byte[]?> LastEquipmentItemData { get; } = new();

        public List<byte[]?> LastBackpackItemData { get; } = new();

        public Dictionary<byte[], ReplayHandItemSnapshot> HandItemSnapshots { get; }
            = new(ByteArrayValueComparer.Instance);

        public byte[]? LastRightHandItemData { get; set; }

        public byte[]? LastLeftHandItemData { get; set; }

        public DirectorReplayActorSample InterpolatedSample { get; set; } = new();

        public bool HasAppliedSample { get; set; }

        public bool PlayerSkinTexturePrepared { get; set; }

        public bool WasVisible { get; set; }

        public bool WasInCurrentView { get; set; }

        public long LastAnimationUpdateFrame { get; set; } = -1L;

        public bool AnimationPoseDirty { get; set; }

        public object? LastAnimator { get; set; }

        // Dedicated playback keeps an actor hidden while a forced first-view
        // animation update is waiting for its bounded frame budget. Preview
        // playback never consults this latch.
        public bool DedicatedAnimationPoseReady { get; set; } = true;

        public bool DedicatedAnimationWarmupPending { get; set; }

        public int DedicatedAnimationWarmupAttempts { get; set; }

        public long DedicatedAnimationWarmupNextFrame { get; set; } = -1L;

        public long DedicatedAnimationWarmupLastAttemptFrame { get; set; } = -1L;

        public bool DedicatedAnimationWarmupFallbackLogged { get; set; }

        public bool DedicatedAnimationWarmupFallbackActive { get; set; }

        // Captured during ApplyPlaybackFrame using the primary camera. It is
        // intentionally independent from pose readiness and from any later
        // offscreen or shadow frustum.
        public bool DedicatedAnimationMainViewVisible { get; set; }

        public bool DedicatedAnimationViewEligible { get; set; }

        public DirectorReplayActorPosition ScheduledPosition { get; set; }

        public long ScheduledPositionFrame { get; set; } = -1L;

        public bool HasScheduledPosition { get; set; }

        public EntityPos ScheduledEntityPosition { get; }

        public DirectorReplayActorState? ScheduledState { get; set; }

        public long ScheduledStateFrame { get; set; } = -1L;
    }

    private sealed class BlockEntityCaptureRound
    {
        public BlockEntityCaptureRound(DirectorReplayChunkSnapshot[] chunks, double timeSeconds)
        {
            Chunks = chunks;
            TimeSeconds = timeSeconds;
        }

        public DirectorReplayChunkSnapshot[] Chunks { get; }

        public double TimeSeconds { get; }

        public HashSet<BlockKey> Seen { get; } = new();

        public HashSet<ChunkKey> ScannedChunks { get; } = new();

        public int NextChunkIndex { get; set; }
    }

    private sealed class ObservedBlockState
    {
        public ObservedBlockState(CachedBlockState state)
        {
            State = state;
        }

        public CachedBlockState State { get; set; }
    }

    private sealed record ReplayRenderedPose(
        double X,
        double Y,
        double Z,
        int Dimension,
        float Yaw,
        float Pitch,
        float Roll,
        float HeadYaw,
        float HeadPitch,
        float BodyYaw);

    private sealed class PausedBlockResyncBatch
    {
        public PausedBlockResyncBatch(BlockKey[] changes, double timeSeconds)
        {
            Changes = changes;
            TimeSeconds = timeSeconds;
        }

        public BlockKey[] Changes { get; }

        public double TimeSeconds { get; }

        public int NextIndex { get; set; }
    }

    private readonly record struct CachedBlockEntity(string ClassName, byte[] Data);

    private readonly record struct SavedEntityVisibility(bool IsRendered, bool IsShadowRendered);

    private readonly record struct RecordingActorBinding(string ActorKey, Entity Entity);

    private sealed class LiveActionCapture
    {
        public LiveActionCapture(
            string name,
            DirectorReplayActorPathSource source,
            Entity entity,
            DirectorReplayActorTrack track)
        {
            Name = name;
            Source = source;
            Entity = entity;
            Track = track;
        }

        public string Name { get; }

        public DirectorReplayActorPathSource Source { get; }

        public Entity Entity { get; }

        public DirectorReplayActorTrack Track { get; }

        public double ElapsedSeconds { get; set; }

        public double Accumulator { get; set; }
    }

    private sealed record ReplayHandItemSnapshot(byte[] Data, ItemStack? Stack);

    private sealed class ReplayLoadedSoundMarker
    {
        public ReplayLoadedSoundMarker(long instanceId)
        {
            InstanceId = instanceId;
        }

        public long InstanceId { get; }

        public DirectorReplayDocument? CaptureDocument { get; set; }

        public bool CapturedActive { get; set; }

        public float PitchOffset { get; set; }
    }

    private sealed class ByteArrayValueComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayValueComparer Instance { get; } = new();

        public bool Equals(byte[]? left, byte[]? right)
            => ReferenceEquals(left, right)
                || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

        public int GetHashCode(byte[] value)
        {
            unchecked
            {
                int hash = 17;
                foreach (byte item in value)
                {
                    hash = hash * 31 + item;
                }
                return hash;
            }
        }
    }

    private readonly record struct CachedBlockState(
        int SolidBlockId,
        int FluidBlockId,
        string BlockEntityClass,
        byte[] BlockEntityData,
        bool HasBlockEntityState);

    private readonly record struct BlockKey(int X, int Y, int Z, int Dimension)
    {
        public static BlockKey From(BlockPos position)
            => new(position.X, position.Y, position.Z, position.dimension);

        public static BlockKey From(DirectorReplayBlockEntitySnapshot snapshot)
            => new(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Dimension);

        public BlockPos ToBlockPos() => new(X, Y, Z, Dimension);
    }

    private readonly record struct ChunkKey(int X, int Y, int Z, int Dimension)
    {
        public static ChunkKey FromBlock(BlockKey block)
            => new(
                (int)Math.Floor(block.X / (double)GlobalConstants.ChunkSize),
                (int)Math.Floor(block.Y / (double)GlobalConstants.ChunkSize),
                (int)Math.Floor(block.Z / (double)GlobalConstants.ChunkSize),
                block.Dimension);
    }

    private sealed class ObsReplayExportSession
    {
        public ObsReplayExportSession(
            string sceneName,
            double startSeconds,
            double endSeconds,
            double restoreTimeSeconds,
            double restoreSpeed,
            bool restorePaused,
            double timeScale,
            int framesPerSecond,
            int settleFrames,
            DirectorReplayViewportTimeline timeline)
        {
            SceneName = sceneName;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            RestoreTimeSeconds = restoreTimeSeconds;
            RestoreSpeed = restoreSpeed;
            RestorePaused = restorePaused;
            StartGate = new DirectorReplayObsExportStartGate(settleFrames);
            Clock = new DirectorReplayElementExportClock(
                timeline,
                startSeconds,
                endSeconds,
                timeScale,
                framesPerSecond);
        }

        public string SceneName { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public double RestoreTimeSeconds { get; }

        public double RestoreSpeed { get; }

        public bool RestorePaused { get; }

        public DirectorReplayObsExportStartGate StartGate { get; }

        public DirectorReplayElementExportClock Clock { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public DirectorObsWebSocketClient? Client { get; set; }

        public string Status { get; set; } = "preparing";

        public string Error { get; set; } = string.Empty;

        public string OutputPath { get; set; } = string.Empty;

        public string OutputDirectory { get; set; } = string.Empty;

        public bool Restored { get; set; }

        public bool IsActive => Status is "preparing" or "rendering" or "stopping";
    }
}
