namespace VSDirector.Data;

public sealed class DirectorSessionTimeMap
{
    private readonly DirectorClockAnchor[] anchors;
    public long UncertaintyUs => anchors.Max(a => a.UncertaintyUs);
    public DirectorSessionTimeMap(DirectorSessionLink link)
    {
        anchors = link.Copy().ClockAnchors.GroupBy(a => a.ReplaySeconds).Select(g => g.Last())
            .OrderBy(a => a.ReplaySeconds).ToArray();
        if (anchors.Length == 0) throw new InvalidDataException("Replay has no ServerRecording clock anchors; automatic alignment is unavailable.");
        for (int i = 1; i < anchors.Length; i++)
            if (anchors[i].ServerTimeUs <= anchors[i - 1].ServerTimeUs)
                throw new InvalidDataException("Replay clock is not monotonic; automatic alignment would reverse recorded history.");
    }
    public double ServerSeconds(double local)
    {
        if (anchors.Length == 1) return anchors[0].ServerTimeUs / 1_000_000d + local - anchors[0].ReplaySeconds;
        int index = Array.FindIndex(anchors, a => a.ReplaySeconds >= local);
        index = index <= 0 ? (index < 0 ? anchors.Length - 1 : 1) : index;
        var a = anchors[index - 1]; var b = anchors[index];
        return a.ServerTimeUs / 1_000_000d + (local - a.ReplaySeconds)
            * ((b.ServerTimeUs - a.ServerTimeUs) / 1_000_000d) / (b.ReplaySeconds - a.ReplaySeconds);
    }

    public double ReplaySeconds(double server)
    {
        if (anchors.Length == 1) return server - anchors[0].ServerTimeUs / 1_000_000d + anchors[0].ReplaySeconds;
        int index = Array.FindIndex(anchors, a => a.ServerTimeUs / 1_000_000d >= server);
        index = index <= 0 ? (index < 0 ? anchors.Length - 1 : 1) : index;
        var a = anchors[index - 1]; var b = anchors[index];
        return a.ReplaySeconds + (server - a.ServerTimeUs / 1_000_000d) * (b.ReplaySeconds - a.ReplaySeconds)
            / ((b.ServerTimeUs - a.ServerTimeUs) / 1_000_000d);
    }
}

public sealed class DirectorSessionMergeOrigin
{
    public Guid SessionId { get; set; }
    public double FromSeconds { get; set; }
    public double ToSeconds { get; set; }
    public long MaxClockUncertaintyUs { get; set; }
    public List<Guid> Replays { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public DirectorSessionMergeOrigin Copy() => new() { SessionId = SessionId, FromSeconds = FromSeconds,
        ToSeconds = ToSeconds, MaxClockUncertaintyUs = MaxClockUncertaintyUs, Replays = [.. Replays], Warnings = [.. Warnings] };
}
