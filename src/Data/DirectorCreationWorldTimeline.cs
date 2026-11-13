namespace VSDirector.Data;

public sealed record CreationRecordedWorld(CreationClip Clip, CreationPerformance Performance, double SourceTime);

// Select the recorded world independently of an explicitly imported replay.
// Before the first take, show its baseline; gaps retain the preceding take's
// ending state. The same mapping drives seeking, preview and video export.
public static class DirectorCreationWorldTimeline
{
    public static CreationRecordedWorld? Resolve(DirectorCreationProject project, double time)
    {
        if (!project.UsePerformanceWorld || project.BackgroundReplayId != Guid.Empty) return null;
        var candidates = project.Clips.Where(c => c.Enabled && c.PerformanceAssetId != Guid.Empty
            && project.Duration > c.Start
            && project.Objects.Any(o => o.Id == c.ObjectId)
            && project.Performances.TryGetValue(c.PerformanceAssetId, out var take)
            && take.WorldSource.Length > 0).ToArray();
        if (candidates.Length == 0) return null;

        var clip = candidates.Where(c => DirectorCreationEvaluator.ClipActive(c, time, project.Duration))
            .OrderBy(c => c.Lane).ThenBy(c => c.Start).ThenBy(c => c.Id).LastOrDefault()
            ?? candidates.Where(c => time >= c.Start + c.Duration)
                .OrderBy(c => c.Start + c.Duration).ThenBy(c => c.Lane).ThenBy(c => c.Id).LastOrDefault()
            ?? candidates.OrderBy(c => c.Start).ThenBy(c => c.Lane).ThenBy(c => c.Id).First();
        var performance = project.Performances[clip.PerformanceAssetId];
        double sourceTime = performance.BackgroundStart
            + DirectorCreationTiming.ClipSourceTime(clip, time, performance.Action.DurationSeconds);
        return new(clip, performance, sourceTime);
    }
}
