namespace VSDirector.Data;

public sealed partial class DirectorReplayViewportTimeline
{
    public bool IsTrackEnabled(DirectorReplayElementTrack track)
        => track.Enabled && (track.ParentGroupId == Guid.Empty
            || Tracks.Any(group => group.Id == track.ParentGroupId
                && group.TrackType == DirectorReplayElementTrackType.PresetGroup && group.Enabled));

    public HashSet<Guid> TrackFamily(Guid id)
        => Tracks.Where(track => track.Id == id || track.ParentGroupId == id)
            .Select(track => track.Id).ToHashSet();

    // New preset instances override earlier instances. Preserve the original
    // first-row priority of ungrouped timelines for existing recordings.
    public IEnumerable<DirectorReplayElementTrack> EffectTracksInPriorityOrder()
        => Enumerable.Reverse(Tracks).Where(track => track.ParentGroupId != Guid.Empty)
            .Concat(Tracks.Where(track => track.ParentGroupId == Guid.Empty));

    public IEnumerable<DirectorReplayElementTrack> VisibleTracks(IReadOnlySet<Guid> expanded)
    {
        foreach (var track in Tracks.Where(track => track.ParentGroupId == Guid.Empty))
        {
            if (track.TrackType == DirectorReplayElementTrackType.Marker) continue;
            yield return track;
            if (track.TrackType == DirectorReplayElementTrackType.PresetGroup && expanded.Contains(track.Id))
                foreach (var child in Tracks.Where(child => child.ParentGroupId == track.Id)) yield return child;
        }
    }

    private void NormalizeGroups()
    {
        // Older head recordings were single rows. Wrap without baking a camera
        // from today's view: their original samples and playback baseline survive.
        foreach (var motion in Tracks.Where(t => t.TrackType == DirectorReplayElementTrackType.HeadMotion
            && t.ParentGroupId == Guid.Empty).ToArray())
            DirectorHeadMotion.EnsureGroup(this, motion);
        var groups = Tracks.Where(track => track.TrackType == DirectorReplayElementTrackType.PresetGroup)
            .Select(track => track.Id).ToHashSet();
        foreach (var track in Tracks)
            if (groups.Contains(track.Id) || !groups.Contains(track.ParentGroupId)) track.ParentGroupId = Guid.Empty;
        // Keep each family contiguous for both rendering and runtime priority.
        Tracks = Tracks.Where(track => track.ParentGroupId == Guid.Empty)
            .SelectMany(track => new[] { track }.Concat(Tracks.Where(child => child.ParentGroupId == track.Id))).ToList();
    }
}

/// <summary>Samples visual elements over the scene baseline without modifying the saved view.</summary>
public static class DirectorReplayVisualElements
{
    public static bool IsVisual(DirectorReplayElementTrackType type)
        => type is DirectorReplayElementTrackType.Weather or DirectorReplayElementTrackType.Sky
            or DirectorReplayElementTrackType.Composition;

    public static DirectorReplayVisualSettings Evaluate(DirectorReplayViewportTimeline timeline, double time)
    {
        var result = timeline.Visuals.DeepClone();
        foreach (var track in timeline.Tracks)
        {
            if (!IsVisual(track.TrackType) || !timeline.IsTrackEnabled(track)
                || !DirectorReplayElementTimeline.TrySample(timeline.ElementKeyframes, track.TrackType,
                    track.Id, time, out var frame) || frame.VisualSettings is not { } value) continue;
            switch (track.TrackType)
            {
                case DirectorReplayElementTrackType.Weather: result.Environment = value.Environment.DeepClone(); break;
                case DirectorReplayElementTrackType.Sky: result.Sky = value.Sky.DeepClone(); break;
                case DirectorReplayElementTrackType.Composition: result.Composition = value.Composition.DeepClone(); break;
            }
        }
        return result;
    }
}
