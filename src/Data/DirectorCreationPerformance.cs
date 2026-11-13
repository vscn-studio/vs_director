namespace VSDirector.Data;

public sealed class CreationPerformance
{
    public CreationRecordingChannels Channels { get; set; } = CreationRecordingChannels.All;
    // Project-relative recording owned by this take. Legacy performances keep
    // resolving their explicitly linked replay through BackgroundReplayId.
    public string WorldSource { get; set; } = "";
    public Guid BackgroundReplayId { get; set; }
    public double BackgroundStart { get; set; }
    public CreationVector Origin { get; set; }
    public DirectorActionAsset Action { get; set; } = new();
    public List<CreationTrack> Tracks { get; set; } = new();
    private DirectorActionFrame[]? frames;
    public void Validate()
    {
        if (WorldSource is null || !Origin.Finite() || !double.IsFinite(BackgroundStart) || BackgroundStart < 0 || (Channels & ~CreationRecordingChannels.All) != 0
            || Action is null || Action.Layers is null || Tracks is null) throw new InvalidDataException("Invalid recorded performance.");
        foreach (var track in Tracks)
        {
            if (track is null || track.Keys is null || !Enum.IsDefined(track.Channel)) throw new InvalidDataException("Invalid recorded track.");
            foreach (var key in track.Keys)
                if (key is null || !double.IsFinite(key.Time) || key.Time < 0 || !key.Value.Finite() || !Enum.IsDefined(key.Interpolation))
                    throw new InvalidDataException("Invalid recorded keyframe.");
        }
        foreach (var layer in Action.Layers)
            if (layer is null || layer.Frames is null || layer.Frames.Any(f => f is null || f.State is null || !double.IsFinite(f.TimeSeconds) || f.TimeSeconds < 0))
                throw new InvalidDataException("Invalid recorded actor state.");
    }
    public DirectorReplayActorSample? SampleState(double time)
    {
        frames ??= Action.Layers.SelectMany(l => l.Frames).OrderBy(f => f.TimeSeconds).ToArray();
        if (frames.Length == 0) return null;
        int lo = 0, hi = frames.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (frames[mid].TimeSeconds <= time) lo = mid + 1; else hi = mid; }
        return frames[Math.Max(0, lo - 1)].State;
    }
}
