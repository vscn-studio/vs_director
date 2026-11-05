namespace VSDirector.Data;

public sealed class DirectorReplayDocument
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DirectorReplayPages? Pages { get; internal set; }
    public const int CurrentSchemaVersion = 22;

    public const int CurrentParticleCaptureVersion = 1;

    public const string ClientActorCaptureScope = "loaded-client-actors";

    public const string ClientWorldCaptureScope = "loaded-client-world-region";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public double SampleRate { get; set; } = 30d;

    public double DurationSeconds { get; set; }

    public string CaptureScope { get; set; } = ClientWorldCaptureScope;

    public VSDirector.Data.DirectorSessionLink? ServerRecording { get; set; }
    /// <summary>Shared server data required to complete a coordinated replay.</summary>
    public VSDirector.Data.DirectorServerReplayLink? ServerReplay { get; set; }
    public DirectorSessionMergeOrigin? SessionMerge { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasHistoricalBaselines => SessionMerge is not null || ServerReplay?.Materialized == true;

    public bool IncludesWorldSnapshot { get; set; }

    public DirectorReplayWorldSnapshot WorldSnapshot { get; set; } = new();

    public DirectorReplayList<DirectorReplayBlockEvent> BlockEvents { get; set; } = new();

    public DirectorReplayList<DirectorReplayParticleEvent> ParticleEvents { get; set; } = new();

    public int ParticleCaptureVersion { get; set; }

    public DirectorReplayList<DirectorReplaySoundEvent> SoundEvents { get; set; } = new();

    public DirectorReplayList<DirectorReplayVoiceFrame> VoiceFrames { get; set; } = new();

    public DirectorReplayList<DirectorReplayClockSample> ClockSamples { get; set; } = new();

    public List<DirectorReplayMarker> Markers { get; set; } = new();

    public List<DirectorReplaySpeedKeyframe> SpeedKeyframes { get; set; } = new();

    /// <summary>
    /// Replay-local element tracks and camera keyframes.
    /// </summary>
    public DirectorReplayViewportTimeline ViewportTimeline { get; set; } = new();

    public List<DirectorReplayPreloadColumn> IncludedPreloadColumns { get; set; } = new();

    public List<DirectorReplayPreloadColumn> ExcludedPreloadColumns { get; set; } = new();

    public List<DirectorReplayActorTrack> Actors { get; set; } = new();

    public DirectorReplayDocument DeepClone()
    {
        return new DirectorReplayDocument
        {
            SchemaVersion = SchemaVersion,
            Id = Id,
            Name = Name,
            WorldId = WorldId,
            CreatedUtc = CreatedUtc,
            SampleRate = SampleRate,
            DurationSeconds = DurationSeconds,
            CaptureScope = CaptureScope,
            ServerRecording = ServerRecording?.Copy(),
            ServerReplay = ServerReplay?.Copy(),
            SessionMerge = SessionMerge?.Copy(),
            IncludesWorldSnapshot = IncludesWorldSnapshot,
            WorldSnapshot = WorldSnapshot?.DeepClone() ?? new DirectorReplayWorldSnapshot(),
            BlockEvents = BlockEvents is null
                ? new List<DirectorReplayBlockEvent>()
                : BlockEvents.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            ParticleEvents = ParticleEvents is null
                ? new List<DirectorReplayParticleEvent>()
                : ParticleEvents.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            ParticleCaptureVersion = ParticleCaptureVersion,
            SoundEvents = SoundEvents is null
                ? new List<DirectorReplaySoundEvent>()
                : SoundEvents.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            VoiceFrames = VoiceFrames is null
                ? new List<DirectorReplayVoiceFrame>()
                : VoiceFrames.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            ClockSamples = ClockSamples is null
                ? new List<DirectorReplayClockSample>()
                : ClockSamples.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            Markers = Markers is null
                ? new List<DirectorReplayMarker>()
                : Markers.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            SpeedKeyframes = SpeedKeyframes is null
                ? new List<DirectorReplaySpeedKeyframe>()
                : SpeedKeyframes.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            ViewportTimeline = ViewportTimeline?.DeepClone() ?? new DirectorReplayViewportTimeline(),
            IncludedPreloadColumns = IncludedPreloadColumns is null
                ? new List<DirectorReplayPreloadColumn>()
                : new List<DirectorReplayPreloadColumn>(IncludedPreloadColumns),
            ExcludedPreloadColumns = ExcludedPreloadColumns is null
                ? new List<DirectorReplayPreloadColumn>()
                : new List<DirectorReplayPreloadColumn>(ExcludedPreloadColumns),
            Actors = Actors is null
                ? new List<DirectorReplayActorTrack>()
                : Actors.Where(actor => actor is not null).Select(actor => actor.DeepClone()).ToList()
        };
    }
}

public sealed class DirectorReplayMarker
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public double TimeSeconds { get; set; }

    public string Label { get; set; } = string.Empty;

    public DirectorReplayMarker DeepClone() => (DirectorReplayMarker)MemberwiseClone();
}

public enum DirectorReplaySpeedInterpolation
{
    Hold,
    Linear,
    EaseInOut
}

public sealed class DirectorReplaySpeedKeyframe
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public double TimeSeconds { get; set; }

    public double Speed { get; set; } = 1d;

    public DirectorReplaySpeedInterpolation InterpolationToNext { get; set; }
        = DirectorReplaySpeedInterpolation.Linear;

    public DirectorReplaySpeedKeyframe DeepClone() => (DirectorReplaySpeedKeyframe)MemberwiseClone();
}

public sealed class DirectorReplayClockSample
{
    public double TimeSeconds { get; set; }

    public double TotalHours { get; set; }

    public float CalendarSpeedMul { get; set; } = 1f;

    public float? SeasonOverride { get; set; }

    public DirectorReplayClockSample DeepClone() => (DirectorReplayClockSample)MemberwiseClone();
}

public sealed class DirectorReplayActorTrack
{
    public string ActorKey { get; set; } = string.Empty;

    public long EntityId { get; set; }

    public string PlayerUid { get; set; } = string.Empty;

    public bool ForceGhost { get; set; }

    public string EntityCode { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public long ReplayEntityId { get; set; }

    public int SimulationRange { get; set; }
    public int EquipmentSlotCapacity { get; set; }
    public int BackpackSlotCapacity { get; set; }

    private byte[] initialState = [];
    internal Func<byte[]>? InitialStateReader;
    public byte[] InitialState { get => InitialStateReader?.Invoke() ?? initialState; set { initialState = value; InitialStateReader = null; } }

    public List<DirectorReplayActorLifetime> Lifetimes { get; set; } = new();

    public DirectorReplayList<DirectorReplayActorState> States { get; set; } = new();

    public DirectorReplayList<DirectorReplayActorSample> Samples { get; set; } = new();

    public DirectorReplayActorTrack DeepClone()
    {
        return new DirectorReplayActorTrack
        {
            ActorKey = ActorKey,
            EntityId = EntityId,
            PlayerUid = PlayerUid,
            ForceGhost = ForceGhost,
            EntityCode = EntityCode,
            DisplayName = DisplayName,
            ReplayEntityId = ReplayEntityId,
            SimulationRange = SimulationRange,
            EquipmentSlotCapacity = EquipmentSlotCapacity,
            BackpackSlotCapacity = BackpackSlotCapacity,
            InitialState = InitialState is null ? Array.Empty<byte>() : (byte[])InitialState.Clone(),
            Lifetimes = Lifetimes is null
                ? new List<DirectorReplayActorLifetime>()
                : Lifetimes.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            States = States is null
                ? new List<DirectorReplayActorState>()
                : States.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            Samples = Samples is null
                ? new List<DirectorReplayActorSample>()
                : Samples.Where(sample => sample is not null).Select(sample => sample.DeepClone()).ToList()
        };
    }
}

public sealed class DirectorReplayActorLifetime
{
    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public bool EndedByDespawn { get; set; }

    public DirectorReplayActorLifetime DeepClone()
        => new()
        {
            StartSeconds = StartSeconds,
            EndSeconds = EndSeconds,
            EndedByDespawn = EndedByDespawn
        };
}

public sealed class DirectorReplayActorState
{
    public double TimeSeconds { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();

    public DirectorReplayActorState DeepClone()
        => new()
        {
            TimeSeconds = TimeSeconds,
            Data = Data is null ? Array.Empty<byte>() : (byte[])Data.Clone()
        };
}

public sealed class DirectorReplayActorSample
{
    public double TimeSeconds { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float Roll { get; set; }

    public float HeadYaw { get; set; }

    public float HeadPitch { get; set; }

    public float BodyYaw { get; set; }

    public bool HasThirdPersonPose { get; set; }

    public float ThirdPersonBodyYaw { get; set; }

    public float ThirdPersonHeadYaw { get; set; }

    public float ThirdPersonHeadPitch { get; set; }

    public bool HasMotion { get; set; }

    public double MotionX { get; set; }

    public double MotionY { get; set; }

    public double MotionZ { get; set; }

    public bool HasRenderState { get; set; }

    public bool OnGround { get; set; }

    public bool FeetInLiquid { get; set; }

    public bool Swimming { get; set; }

    public string ClimbingOnFace { get; set; } = string.Empty;

    public double LocalEyePosX { get; set; }

    public double LocalEyePosY { get; set; }

    public double LocalEyePosZ { get; set; }

    public float SelectionBoxY2 { get; set; }

    public float CollisionBoxY2 { get; set; }

    public bool HasItemRenderState { get; set; }

    public long ItemRenderElapsedMilliseconds { get; set; }

    public long ItemTouchGroundAgeMilliseconds { get; set; }

    public float ItemScaleRandom { get; set; }

    public float ItemYawRandom { get; set; }

    public float ItemRenderXAngle { get; set; }

    public float ItemRenderYAngle { get; set; }

    public float ItemRenderZAngle { get; set; }

    public float ItemRenderAccumulator { get; set; }

    public bool HasAgentState { get; set; }

    public float BodyYawServer { get; set; }

    public bool HasPlayerState { get; set; }

    public bool HasPlayerPostureState { get; set; }

    public bool CanStandUp { get; set; } = true;

    public int Controls { get; set; }

    public int ServerControls { get; set; }

    public int HandUse { get; set; }

    public int ServerHandUse { get; set; }

    public float WalkYaw { get; set; }

    public float WalkPitch { get; set; }

    public byte[] RightHandItem { get; set; } = Array.Empty<byte>();

    public byte[] LeftHandItem { get; set; } = Array.Empty<byte>();

    public List<byte[]> EquipmentItems { get; set; } = new();

    public List<byte[]> BackpackItems { get; set; } = new();

    public List<string> Animations { get; set; } = new();

    public List<DirectorReplayAnimationSample> AnimationStates { get; set; } = new();

    public DirectorReplayActorSample DeepClone()
    {
        return new DirectorReplayActorSample
        {
            TimeSeconds = TimeSeconds,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            Yaw = Yaw,
            Pitch = Pitch,
            Roll = Roll,
            HeadYaw = HeadYaw,
            HeadPitch = HeadPitch,
            BodyYaw = BodyYaw,
            HasThirdPersonPose = HasThirdPersonPose,
            ThirdPersonBodyYaw = ThirdPersonBodyYaw,
            ThirdPersonHeadYaw = ThirdPersonHeadYaw,
            ThirdPersonHeadPitch = ThirdPersonHeadPitch,
            HasMotion = HasMotion,
            MotionX = MotionX,
            MotionY = MotionY,
            MotionZ = MotionZ,
            HasRenderState = HasRenderState,
            OnGround = OnGround,
            FeetInLiquid = FeetInLiquid,
            Swimming = Swimming,
            ClimbingOnFace = ClimbingOnFace,
            LocalEyePosX = LocalEyePosX,
            LocalEyePosY = LocalEyePosY,
            LocalEyePosZ = LocalEyePosZ,
            SelectionBoxY2 = SelectionBoxY2,
            CollisionBoxY2 = CollisionBoxY2,
            HasItemRenderState = HasItemRenderState,
            ItemRenderElapsedMilliseconds = ItemRenderElapsedMilliseconds,
            ItemTouchGroundAgeMilliseconds = ItemTouchGroundAgeMilliseconds,
            ItemScaleRandom = ItemScaleRandom,
            ItemYawRandom = ItemYawRandom,
            ItemRenderXAngle = ItemRenderXAngle,
            ItemRenderYAngle = ItemRenderYAngle,
            ItemRenderZAngle = ItemRenderZAngle,
            ItemRenderAccumulator = ItemRenderAccumulator,
            HasAgentState = HasAgentState,
            BodyYawServer = BodyYawServer,
            HasPlayerState = HasPlayerState,
            HasPlayerPostureState = HasPlayerPostureState,
            CanStandUp = CanStandUp,
            Controls = Controls,
            ServerControls = ServerControls,
            HandUse = HandUse,
            ServerHandUse = ServerHandUse,
            WalkYaw = WalkYaw,
            WalkPitch = WalkPitch,
            RightHandItem = RightHandItem is null ? Array.Empty<byte>() : (byte[])RightHandItem.Clone(),
            LeftHandItem = LeftHandItem is null ? Array.Empty<byte>() : (byte[])LeftHandItem.Clone(),
            EquipmentItems = CloneItemList(EquipmentItems),
            BackpackItems = CloneItemList(BackpackItems),
            Animations = Animations is null ? new List<string>() : new List<string>(Animations),
            AnimationStates = AnimationStates is null
                ? new List<DirectorReplayAnimationSample>()
                : AnimationStates.Where(state => state is not null).Select(state => state.DeepClone()).ToList()
        };
    }

    private static List<byte[]> CloneItemList(IReadOnlyList<byte[]>? items)
        => items is null
            ? new List<byte[]>()
            : items.Select(item => item is null ? Array.Empty<byte>() : (byte[])item.Clone()).ToList();
}

public sealed class DirectorReplayAnimationSample
{
    public string Code { get; set; } = string.Empty;

    public string StateCode { get; set; } = string.Empty;

    public float Progress { get; set; }

    // ServerRecording records an absolute animation frame; resolve against the loaded shape.
    public float? RecordedFrame { get; set; }

    public float AnimationSpeed { get; set; } = 1f;

    public float EasingFactor { get; set; } = 1f;

    public float BlendedWeight { get; set; } = 1f;

    public int Iterations { get; set; }

    /// <summary>
    /// Runtime flags are distinct from iteration progress: a one-shot can
    /// still contribute a held or easing-out pose after it leaves the active
    /// animation dictionary.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public bool IsRunning { get; set; } = true;

    public bool NowPlaying { get; set; }

    public bool ShouldRewind { get; set; }

    public bool ShouldPlayTillEnd { get; set; }

    public float Weight { get; set; } = 1f;

    public float EaseInSpeed { get; set; } = 10f;

    public float EaseOutSpeed { get; set; } = 10f;

    /// <summary>Serialized EnumAnimationBlendMode value; kept API-neutral for core tools.</summary>
    public int BlendMode { get; set; }

    public Dictionary<string, float> ElementWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serialized EnumAnimationBlendMode values keyed by shape element.</summary>
    public Dictionary<string, int> ElementBlendModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DirectorReplayAnimationSample DeepClone()
    {
        DirectorReplayAnimationSample clone = (DirectorReplayAnimationSample)MemberwiseClone();
        clone.ElementWeights = ElementWeights is null
            ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(ElementWeights, StringComparer.OrdinalIgnoreCase);
        clone.ElementBlendModes = ElementBlendModes is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(ElementBlendModes, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}

public sealed class DirectorReplayWorldSnapshot
{
    public int Dimension { get; set; }

    public int CenterChunkX { get; set; }

    public int CenterChunkZ { get; set; }

    public int RadiusChunks { get; set; }

    public int MapSizeY { get; set; }

    public List<DirectorReplayChunkSnapshot> Chunks { get; set; } = new();

    public List<DirectorReplayBlockEntitySnapshot> BlockEntities { get; set; } = new();

    public DirectorReplayWorldSnapshot DeepClone()
        => new()
        {
            Dimension = Dimension,
            CenterChunkX = CenterChunkX,
            CenterChunkZ = CenterChunkZ,
            RadiusChunks = RadiusChunks,
            MapSizeY = MapSizeY,
            Chunks = Chunks is null
                ? new List<DirectorReplayChunkSnapshot>()
                : Chunks.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            BlockEntities = BlockEntities is null
                ? new List<DirectorReplayBlockEntitySnapshot>()
                : BlockEntities.Where(item => item is not null).Select(item => item.DeepClone()).ToList()
        };
}

public sealed class DirectorReplayChunkSnapshot
{
    public string ChunkHash { get; set; } = string.Empty;
    public long ChunkVersion { get; set; }
    /// <summary>Zero for legacy baselines; ServerRecording baselines become visible at their capture time.</summary>
    public double CapturedAtSeconds { get; set; }

    public int ChunkX { get; set; }

    public int ChunkY { get; set; }

    public int ChunkZ { get; set; }

    public DirectorReplayList<DirectorReplayBlockRun> SolidRuns { get; set; } = new();

    public DirectorReplayList<DirectorReplayBlockRun> FluidRuns { get; set; } = new();

    public DirectorReplayChunkSnapshot DeepClone()
        => new()
        {
            ChunkHash = ChunkHash,
            ChunkVersion = ChunkVersion,
            CapturedAtSeconds = CapturedAtSeconds,
            ChunkX = ChunkX,
            ChunkY = ChunkY,
            ChunkZ = ChunkZ,
            SolidRuns = SolidRuns is null
                ? new List<DirectorReplayBlockRun>()
                : SolidRuns.Where(item => item is not null).Select(item => item.DeepClone()).ToList(),
            FluidRuns = FluidRuns is null
                ? new List<DirectorReplayBlockRun>()
                : FluidRuns.Where(item => item is not null).Select(item => item.DeepClone()).ToList()
        };
}

public sealed class DirectorReplayBlockRun
{
    public int BlockId { get; set; }

    public int Length { get; set; }

    public DirectorReplayBlockRun DeepClone() => new() { BlockId = BlockId, Length = Length };
}

public sealed class DirectorReplayBlockEntitySnapshot
{
    private byte[] data = [];
    internal Func<byte[]>? DataReader;
    public int X { get; set; }

    public int Y { get; set; }

    public int Z { get; set; }

    public int Dimension { get; set; }

    public string ClassName { get; set; } = string.Empty;

    public byte[] Data { get => DataReader?.Invoke() ?? data; set { data = value; DataReader = null; } }

    public DirectorReplayBlockEntitySnapshot DeepClone()
        => new()
        {
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            ClassName = ClassName,
            Data = Data is null ? Array.Empty<byte>() : (byte[])Data.Clone()
        };
}

public sealed class DirectorReplayBlockEvent
{
    public double TimeSeconds { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public int Z { get; set; }

    public int Dimension { get; set; }

    public int SolidBlockId { get; set; }

    public int FluidBlockId { get; set; }

    public bool BlockEntityCaptured { get; set; }

    public string BlockEntityClass { get; set; } = string.Empty;

    public byte[] BlockEntityData { get; set; } = Array.Empty<byte>();

    public DirectorReplayBlockEvent DeepClone()
        => new()
        {
            TimeSeconds = TimeSeconds,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            SolidBlockId = SolidBlockId,
            FluidBlockId = FluidBlockId,
            BlockEntityCaptured = BlockEntityCaptured,
            BlockEntityClass = BlockEntityClass,
            BlockEntityData = BlockEntityData is null ? Array.Empty<byte>() : (byte[])BlockEntityData.Clone()
        };
}

public enum DirectorReplayParticleKind
{
    Provider,
    Simple
}

public sealed class DirectorReplayParticleEvent
{
    public double TimeSeconds { get; set; }

    public DirectorReplayParticleKind Kind { get; set; }

    // Set while recording from WeatherSimulationParticles. Older replay files
    // leave this false and are covered by the provider-type fallback.
    public bool IsWeatherParticle { get; set; }

    public string ProviderType { get; set; } = string.Empty;

    public byte[] ProviderData { get; set; } = Array.Empty<byte>();

    public float Quantity { get; set; }

    public int Color { get; set; }

    public double MinX { get; set; }

    public double MinY { get; set; }

    public double MinZ { get; set; }

    public double MaxX { get; set; }

    public double MaxY { get; set; }

    public double MaxZ { get; set; }

    public float MinVelocityX { get; set; }

    public float MinVelocityY { get; set; }

    public float MinVelocityZ { get; set; }

    public float MaxVelocityX { get; set; }

    public float MaxVelocityY { get; set; }

    public float MaxVelocityZ { get; set; }

    public float LifeLength { get; set; }

    public float GravityEffect { get; set; }

    public float Scale { get; set; } = 1f;

    public int ParticleModel { get; set; }

    public DirectorReplayParticleEvent DeepClone()
        => (DirectorReplayParticleEvent)MemberwiseCloneWithData();

    private object MemberwiseCloneWithData()
    {
        DirectorReplayParticleEvent clone = (DirectorReplayParticleEvent)MemberwiseClone();
        clone.ProviderData = ProviderData is null ? Array.Empty<byte>() : (byte[])ProviderData.Clone();
        return clone;
    }
}

public enum DirectorReplaySoundAction
{
    Play,
    Pause,
    Stop,
    Update
}

public sealed class DirectorReplaySoundEvent
{
    public double TimeSeconds { get; set; }

    public string Location { get; set; } = string.Empty;

    public long InstanceId { get; set; }

    public DirectorReplaySoundAction Action { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public bool RelativePosition { get; set; }

    public bool ShouldLoop { get; set; }

    public float PlaybackPosition { get; set; }

    public float SoundLengthSeconds { get; set; }

    public float Volume { get; set; } = 1f;

    public float Pitch { get; set; } = 1f;

    public float PitchOffset { get; set; }

    public float Range { get; set; } = 32f;

    public float ReferenceDistance { get; set; } = 3f;

    public float LowPassFilter { get; set; } = 1f;

    public float ReverbDecayTime { get; set; }

    public int SoundType { get; set; }

    public DirectorReplaySoundEvent DeepClone() => (DirectorReplaySoundEvent)MemberwiseClone();
}

public sealed class DirectorReplayVoiceFrame
{
    public const int SampleRate = 48000;

    public double TimeSeconds { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SpeakerId { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public float Volume { get; set; } = 1f;
    public float MaxDistance { get; set; } = DirectorVoiceSpatialization.DefaultMaxDistance;
    public float ReferenceDistance { get; set; } = DirectorVoiceSpatialization.DefaultReferenceDistance;
    public float RolloffFactor { get; set; } = DirectorVoiceSpatialization.DefaultRolloffFactor;
    public bool Spatialized { get; set; } = true;

    /// <summary>Little-endian signed 16-bit mono PCM at 48 kHz.</summary>
    public byte[] Pcm16 { get; set; } = Array.Empty<byte>();

    public DirectorReplayVoiceFrame DeepClone()
        => new()
        {
            TimeSeconds = TimeSeconds,
            SourceId = SourceId,
            SpeakerId = SpeakerId,
            SpeakerName = SpeakerName,
            X = X,
            Y = Y,
            Z = Z,
            Dimension = Dimension,
            Volume = Volume,
            MaxDistance = MaxDistance,
            ReferenceDistance = ReferenceDistance,
            RolloffFactor = RolloffFactor,
            Spatialized = Spatialized,
            Pcm16 = Pcm16 is null ? Array.Empty<byte>() : (byte[])Pcm16.Clone()
        };
}
