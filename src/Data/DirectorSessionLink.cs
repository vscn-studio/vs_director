namespace VSDirector.Data;

/// <summary>Read-only metadata retained for replays recorded by older coordinated-recording builds.</summary>
public sealed class DirectorSessionLink
{
    public Guid SessionId { get; set; }
    public string PlayerUid { get; set; } = "";
    public Guid RecordingId { get; set; }
    public int VolumeIndex { get; set; }
    public DirectorServerRecordingConfig Capture { get; set; } = new();
    /// <summary>Shared server-side authoritative stream for this session.</summary>
    public DirectorServerReplayLink? ServerReplay { get; set; }
    public List<DirectorClockAnchor> ClockAnchors { get; set; } = [];
    public DirectorSessionLink Copy() => new()
    {
        SessionId = SessionId, PlayerUid = PlayerUid ?? "",
        RecordingId = RecordingId, VolumeIndex = VolumeIndex, Capture = Capture?.Copy() ?? new(),
        ServerReplay = ServerReplay?.Copy(),
        ClockAnchors = ClockAnchors?.Where(a => a is not null && double.IsFinite(a.ReplaySeconds)
            && a.ReplaySeconds >= 0 && a.UncertaintyUs >= 0).ToList() ?? []
    };
}

public sealed record DirectorClockAnchor(double ReplaySeconds, long ServerTimeUs, long UncertaintyUs);
