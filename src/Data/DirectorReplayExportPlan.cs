using System.Globalization;

namespace VSDirector.Data;

public sealed class DirectorReplayExportTrackPlan
{
    public Guid TrackId { get; set; }

    public string TrackName { get; set; } = string.Empty;

    public DirectorRecordingSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public string SourceName { get; set; } = string.Empty;

    public int Order { get; set; }

    public bool Enabled { get; set; } = true;

    public string FileStem { get; set; } = string.Empty;

    public DirectorReplayExportTrackPlan DeepClone() => (DirectorReplayExportTrackPlan)MemberwiseClone();
}

public sealed class DirectorReplayExportPlan
{
    public Guid ReplayId { get; set; }

    public string ReplayName { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public double TimeScale { get; set; } = 1d;

    public int FramesPerSecond { get; set; } = 30;

    public DirectorRecordingContainer Container { get; set; } = DirectorRecordingContainer.Mp4;

    public string OutputDirectory { get; set; } = string.Empty;

    public List<DirectorReplayExportTrackPlan> Tracks { get; set; } = new();

    public IReadOnlyList<DirectorReplayExportTrackPlan> EnabledTracks
        => Tracks
            .Where(track => track.Enabled)
            .OrderBy(track => track.Order)
            .ThenBy(track => track.TrackName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public long FramesPerTrack
    {
        get
        {
            if (!IsRangeValid)
            {
                return 0L;
            }

            return DirectorFrameSchedule.TargetFrameCount(
                TimeSpan.FromSeconds((EndSeconds - StartSeconds) / TimeScale),
                FramesPerSecond);
        }
    }

    public long TotalFrames => checked(FramesPerTrack * EnabledTracks.Count);

    public bool IsRangeValid
        => double.IsFinite(StartSeconds)
            && double.IsFinite(EndSeconds)
            && EndSeconds > StartSeconds
            && StartSeconds >= 0d
            && double.IsFinite(TimeScale)
            && TimeScale >= 0.05d
            && TimeScale <= 1024d
            && FramesPerSecond is >= 1 and <= 240;

    public bool IsReady => IsRangeValid && EnabledTracks.Count > 0;

    public string Summary
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{EnabledTracks.Count} track(s), {StartSeconds:0.###}-{EndSeconds:0.###}s, {FramesPerSecond} fps, {TimeScale:0.##}x, {TotalFrames} frames");

    public void RefreshFileStems()
    {
        int enabledIndex = 0;
        foreach (DirectorReplayExportTrackPlan track in Tracks
            .OrderBy(track => track.Order)
            .ThenBy(track => track.TrackName, StringComparer.OrdinalIgnoreCase))
        {
            string prefix = track.Enabled
                ? (++enabledIndex).ToString("00", CultureInfo.InvariantCulture)
                : "--";
            track.FileStem = $"{prefix}_{SanitizeFileStem(track.TrackName)}";
        }
    }

    public DirectorReplayExportPlan DeepClone()
    {
        DirectorReplayExportPlan plan = new()
        {
            ReplayId = ReplayId,
            ReplayName = ReplayName,
            StartSeconds = StartSeconds,
            EndSeconds = EndSeconds,
            TimeScale = TimeScale,
            FramesPerSecond = FramesPerSecond,
            Container = Container,
            OutputDirectory = OutputDirectory,
            Tracks = Tracks.Select(track => track.DeepClone()).ToList()
        };
        return plan;
    }

    public static DirectorReplayExportPlan Create(
        Guid replayId,
        string replayName,
        double durationSeconds,
        IEnumerable<CameraPath> paths,
        int framesPerSecond,
        DirectorRecordingContainer container,
        string outputDirectory)
    {
        CameraPath[] pathArray = paths
            .Where(path => path is not null)
            .ToArray();
        DirectorRecordingTrack[] recordingTracks = pathArray
            .Select(path => new DirectorRecordingTrack
            {
                Id = path.Id,
                Name = path.Name,
                SourceType = DirectorRecordingSourceType.Path,
                SourceId = path.Id,
                Enabled = true
            })
            .ToArray();

        return Create(
            replayId,
            replayName,
            durationSeconds,
            recordingTracks,
            track => pathArray.First(path => path.Id == track.SourceId).Name,
            framesPerSecond,
            container,
            outputDirectory);
    }

    public static DirectorReplayExportPlan Create(
        Guid replayId,
        string replayName,
        double durationSeconds,
        IEnumerable<DirectorRecordingTrack> recordingTracks,
        Func<DirectorRecordingTrack, string> resolveSourceName,
        int framesPerSecond,
        DirectorRecordingContainer container,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(recordingTracks);
        ArgumentNullException.ThrowIfNull(resolveSourceName);
        List<DirectorReplayExportTrackPlan> tracks = recordingTracks
            .Where(track => track is not null
                && track.Enabled
                && DirectorWorkflowPlanner.IsExportSource(track.SourceType))
            .Select((track, index) => new DirectorReplayExportTrackPlan
            {
                TrackId = track.Id,
                TrackName = track.Name,
                SourceType = track.SourceType,
                SourceId = track.SourceId,
                SourceName = resolveSourceName(track)?.Trim() ?? string.Empty,
                Order = index,
                Enabled = true,
                FileStem = $"{index + 1:00}_{SanitizeFileStem(track.Name)}"
            })
            .ToList();

        DirectorReplayExportPlan plan = new()
        {
            ReplayId = replayId,
            ReplayName = replayName?.Trim() ?? string.Empty,
            StartSeconds = 0d,
            EndSeconds = Math.Max(0d, durationSeconds),
            TimeScale = 1d,
            FramesPerSecond = DirectorTimecode.NormalizeFrameRate(framesPerSecond),
            Container = container,
            OutputDirectory = outputDirectory?.Trim() ?? string.Empty,
            Tracks = tracks
        };
        plan.RefreshFileStems();
        return plan;
    }

    private static string SanitizeFileStem(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string normalized = new((value ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray());
        normalized = normalized.Trim('.', ' ');
        if (normalized.Length > 80)
        {
            normalized = normalized[..80];
        }
        return normalized.Length == 0 ? "CameraPath" : normalized;
    }
}
