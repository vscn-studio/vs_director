using ProtoBuf;
using Vintagestory.API.Common;

namespace VSDirector.Networking;

public static class DirectorNetwork
{
    public const string ChannelName = "vsdirector";

    public static void Register(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<DirectorPermissionRequest>()
            .RegisterMessageType<DirectorPermissionSnapshot>()
            .RegisterMessageType<DirectorPermissionMemberDto>()
            .RegisterMessageType<DirectorAnchorRequest>()
            .RegisterMessageType<DirectorAnchorStatus>()
            .RegisterMessageType<DirectorBlockSoundEvent>()
            .RegisterMessageType<DirectorChunkPreloadColumn>()
            .RegisterMessageType<DirectorChunkPreloadRequest>()
            .RegisterMessageType<DirectorChunkPreloadStatus>()
            .RegisterMessageType<DirectorSharedCameraRequest>()
            .RegisterMessageType<DirectorSharedCameraCatalog>();
    }
}

public enum DirectorPermissionRequestAction
{
    Refresh,
    Update
}

[ProtoContract]
public sealed class DirectorPermissionRequest
{
    [ProtoMember(1)]
    public int Action;

    [ProtoMember(2)]
    public string PlayerUid = string.Empty;

    [ProtoMember(3)]
    public long PermissionMask;
}

[ProtoContract]
public sealed class DirectorPermissionSnapshot
{
    [ProtoMember(1)]
    public bool IsAdmin;

    [ProtoMember(2)]
    public long PermissionMask;

    [ProtoMember(3)]
    public List<DirectorPermissionMemberDto> Members = new();

    [ProtoMember(4)]
    public string Error = string.Empty;
}

[ProtoContract]
public sealed class DirectorPermissionMemberDto
{
    [ProtoMember(8)] public long AssignedMask;
    [ProtoMember(9)] public long InheritedMask;
    [ProtoMember(1)]
    public string PlayerUid = string.Empty;

    [ProtoMember(2)]
    public string PlayerName = string.Empty;

    [ProtoMember(3)]
    public string RoleCode = string.Empty;

    [ProtoMember(4)]
    public bool Online;

    [ProtoMember(5)]
    public bool IsAdmin;

    [ProtoMember(6)]
    public long PermissionMask;
}

public enum DirectorSharedCameraAction
{
    List,
    Upsert,
    Delete
}

[ProtoContract]
public sealed class DirectorSharedCameraRequest
{
    [ProtoMember(1)]
    public int Action;

    [ProtoMember(2)]
    public DirectorSharedCameraDto Camera = new();

    [ProtoMember(3)]
    public string CameraId = string.Empty;
}

[ProtoContract]
public sealed class DirectorSharedCameraCatalog
{
    [ProtoMember(1)]
    public List<DirectorSharedCameraDto> Cameras = new();

    [ProtoMember(2)]
    public string Error = string.Empty;
}

[ProtoContract]
public sealed class DirectorSharedCameraDto
{
    [ProtoMember(1)]
    public string Id = string.Empty;

    [ProtoMember(2)]
    public string Name = string.Empty;

    [ProtoMember(3)]
    public double X;

    [ProtoMember(4)]
    public double Y;

    [ProtoMember(5)]
    public double Z;

    [ProtoMember(6)]
    public float Yaw;

    [ProtoMember(7)]
    public float Pitch;

    [ProtoMember(8)]
    public float Roll;

    [ProtoMember(9)]
    public float Fov = 70f;

    [ProtoMember(10)]
    public int Dimension;

    [ProtoMember(11)]
    public long ModifiedUtcTicks;
}

[ProtoContract]
public sealed class DirectorAnchorRequest
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public double X;

    [ProtoMember(3)]
    public double Z;

    [ProtoMember(4)]
    public int Dimension;

    [ProtoMember(5)]
    public int RadiusChunks;

    [ProtoMember(6)]
    public string TargetPlayerUid = string.Empty;

    [ProtoMember(7)]
    public long TargetEntityId;

    [ProtoMember(8)]
    public bool HasSecondaryAnchor;

    [ProtoMember(9)]
    public double SecondaryX;

    [ProtoMember(10)]
    public double SecondaryZ;

    [ProtoMember(11)]
    public int SecondaryDimension;

    // A replay only needs the currently observed chunk column while it is
    // loading.  Keep this explicit so normal remote-follow requests can
    // continue to honor the target player's view distance.
    [ProtoMember(12)]
    public bool CompactWindow;
}

[ProtoContract]
public sealed class DirectorAnchorStatus
{
    [ProtoMember(1)]
    public bool Allowed;

    [ProtoMember(2)]
    public string MessageCode = string.Empty;

    [ProtoMember(3)]
    public int MaxRadiusChunks;

    [ProtoMember(4)]
    public bool TargetRequested;

    [ProtoMember(5)]
    public bool TargetAvailable;

    [ProtoMember(6)]
    public long TargetEntityId;
}

[ProtoContract]
public sealed class DirectorBlockSoundEvent
{
    [ProtoMember(1)]
    public string Location = string.Empty;

    [ProtoMember(2)]
    public double X;

    [ProtoMember(3)]
    public double Y;

    [ProtoMember(4)]
    public double Z;

    [ProtoMember(5)]
    public int Dimension;

    [ProtoMember(6)]
    public float Pitch = 1f;

    [ProtoMember(7)]
    public float Range = 16f;

    [ProtoMember(8)]
    public float Volume = 1f;

    [ProtoMember(9)]
    public int SoundType;

    [ProtoMember(10)]
    public int BlockX;

    [ProtoMember(11)]
    public int BlockY;

    [ProtoMember(12)]
    public int BlockZ;

    [ProtoMember(13)]
    public bool HasBlockPosition;
}

[ProtoContract]
public sealed class DirectorChunkPreloadColumn
{
    [ProtoMember(1)]
    public int X;

    [ProtoMember(2)]
    public int Z;

    [ProtoMember(3)]
    public int Dimension;
}

[ProtoContract]
public sealed class DirectorChunkPreloadRequest
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public string ReplayId = string.Empty;

    [ProtoMember(3)]
    public int RadiusChunks;

    [ProtoMember(4)]
    public List<DirectorChunkPreloadColumn> Columns = new();
}

[ProtoContract]
public sealed class DirectorChunkPreloadStatus
{
    [ProtoMember(1)]
    public bool Allowed;

    [ProtoMember(2)]
    public string ReplayId = string.Empty;

    [ProtoMember(3)]
    public string MessageCode = string.Empty;

    [ProtoMember(4)]
    public int TotalColumns;

    [ProtoMember(5)]
    public int SentColumns;
}

public enum DirectorRecordingSessionAction
{
    Start = 1,
    Stop = 2,
    Status = 3
}

public enum DirectorRecordingWorkerPhase
{
    Unassigned,
    Registered,
    Armed,
    Recording,
    Stopped,
    Error
}

[ProtoContract]
public sealed class DirectorRecordingWorkerRegistration
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public string TrackId = string.Empty;

    [ProtoMember(3)]
    public string TrackName = string.Empty;

    [ProtoMember(4)]
    public int SourceType;

    [ProtoMember(5)]
    public string SourceId = string.Empty;

    [ProtoMember(6)]
    public bool Enabled;
}

[ProtoContract]
public sealed class DirectorRecordingSessionRequest
{
    [ProtoMember(1)]
    public int Action;

    [ProtoMember(2)]
    public int FramesPerSecond;

    [ProtoMember(3)]
    public int Container;

    [ProtoMember(4)]
    public string ExpectedTrackId = string.Empty;
}

[ProtoContract]
public sealed class DirectorRecordingControl
{
    [ProtoMember(1)]
    public int Action;

    [ProtoMember(2)]
    public string SessionId = string.Empty;

    [ProtoMember(3)]
    public long StartWorldMilliseconds;

    [ProtoMember(4)]
    public long StartUtcTicks;

    [ProtoMember(5)]
    public int FramesPerSecond;

    [ProtoMember(6)]
    public int Container;

    [ProtoMember(7)]
    public string TrackId = string.Empty;

    [ProtoMember(8)]
    public string TrackName = string.Empty;

    [ProtoMember(9)]
    public int SourceType;

    [ProtoMember(10)]
    public string SourceId = string.Empty;

    [ProtoMember(11)]
    public int StartDelayMilliseconds;
}

[ProtoContract]
public sealed class DirectorRecordingWorkerReport
{
    [ProtoMember(1)]
    public string SessionId = string.Empty;

    [ProtoMember(2)]
    public int Phase;

    [ProtoMember(3)]
    public string Message = string.Empty;

    [ProtoMember(4)]
    public string OutputPath = string.Empty;
}

[ProtoContract]
public sealed class DirectorRecordingSessionStatus
{
    [ProtoMember(1)]
    public string SessionId = string.Empty;

    [ProtoMember(2)]
    public string PlayerUid = string.Empty;

    [ProtoMember(3)]
    public string PlayerName = string.Empty;

    [ProtoMember(4)]
    public string TrackName = string.Empty;

    [ProtoMember(5)]
    public int Phase;

    [ProtoMember(6)]
    public string Message = string.Empty;

    [ProtoMember(7)]
    public int WorkerCount;
}
