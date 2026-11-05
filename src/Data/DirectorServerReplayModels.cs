using System.Text.Json.Serialization;

namespace VSDirector.Data;

/// <summary>
/// Identifies the server side companion stream belonging to a coordinated
/// client replay. The stream contains only authoritative server observations;
/// camera, animation, audio and particle data remain in client volumes.
/// </summary>
public sealed class DirectorServerReplayLink
{
    public Guid SessionId { get; set; }
    public string Format { get; set; } = "vsdserver-segment-v2";
    public string ManifestFile { get; set; } = string.Empty;
    public bool RequiredForCompletePlayback { get; set; } = true;
    public bool Available { get; set; }
    public bool Materialized { get; set; }

    public DirectorServerReplayLink Copy() => new()
    {
        SessionId = SessionId,
        Format = Format ?? string.Empty,
        ManifestFile = ManifestFile ?? string.Empty,
        RequiredForCompletePlayback = RequiredForCompletePlayback,
        Available = Available,
        Materialized = Materialized
    };
}

public enum DirectorServerReplayRecordKind : byte
{
    PlayerSample = 1,
    PlayerJoin = 2,
    PlayerLeave = 3,
    PlayerDeath = 4,
    EntitySpawn = 5,
    EntityDespawn = 6,
    BlockPlace = 7,
    BlockBreak = 8,
    ChunkBaseline = 9,
    EntitySample = 10
}

/// <summary>One compact, serializable server observation.</summary>
public sealed class DirectorServerReplayRecord
{
    public DirectorServerReplayRecordKind Kind { get; set; }
    public double TimeSeconds { get; set; }
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string EntityCode { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public int SolidBlockId { get; set; }
    public int FluidBlockId { get; set; }
    public int OldBlockId { get; set; }
    public int NewBlockId { get; set; }
    public int ChunkX { get; set; }
    public int ChunkY { get; set; }
    public int ChunkZ { get; set; }
    public List<DirectorReplayBlockRun> SolidRuns { get; set; } = [];
    public List<DirectorReplayBlockRun> FluidRuns { get; set; } = [];
    public string BlockEntityClass { get; set; } = string.Empty;
    public byte[] BlockEntityData { get; set; } = [];
    public List<DirectorReplayBlockEntitySnapshot> BlockEntities { get; set; } = [];
    /// <summary>Monotonic content version of the captured chunk.</summary>
    public long ChunkVersion { get; set; }
    /// <summary>Content hash used to share identical chunk baselines.</summary>
    public string ChunkHash { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsPlayer => Kind is DirectorServerReplayRecordKind.PlayerSample
        or DirectorServerReplayRecordKind.PlayerJoin
        or DirectorServerReplayRecordKind.PlayerLeave
        or DirectorServerReplayRecordKind.PlayerDeath;
}

public sealed class DirectorServerReplaySegmentInfo
{
    public int Index { get; set; }
    public string File { get; set; } = string.Empty;
    public double FromSeconds { get; set; }
    public double ToSeconds { get; set; }
    public long Bytes { get; set; }
    public long Records { get; set; }
    public string BinaryFile { get; set; } = string.Empty;
}

public sealed class DirectorServerReplayManifest
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public Guid SessionId { get; set; }
    public string WorldId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? StoppedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "recording";
    public long Records { get; set; }
    public long DroppedRecords { get; set; }
    public List<DirectorServerReplaySegmentInfo> Segments { get; set; } = [];
    public string StorageFormat { get; set; } = "binary-delta-v2";
    public string IndexFile { get; set; } = string.Empty;
    public List<DirectorServerReplayChunkIndex> Chunks { get; set; } = [];
    public List<DirectorServerReplayCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class DirectorServerReplayCheckpoint
{
    public int Segment { get; set; }
    public long RecordOffset { get; set; }
    public double TimeSeconds { get; set; }
    public long TimeUs { get; set; }
    public long Xmm { get; set; }
    public long Ymm { get; set; }
    public long Zmm { get; set; }
    public int Dimension { get; set; }
    public long EntityId { get; set; }
    public string PlayerUid { get; set; } = string.Empty;
}

public sealed class DirectorServerReplayChunkIndex
{
    public bool HasPayload { get; set; }
    public int Dimension { get; set; }
    public int ChunkX { get; set; }
    public int ChunkY { get; set; }
    public int ChunkZ { get; set; }
    public long Version { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int Segment { get; set; }
    public long RecordOffset { get; set; }
    public double TimeSeconds { get; set; }
}
