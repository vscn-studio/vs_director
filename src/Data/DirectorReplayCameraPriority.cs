namespace VSDirector.Data;

/// <summary>
/// Decides which replay camera row drives the view. Camera, CameraOrbit, TrackEntity and
/// FollowEntity rows compete for the view; the last enabled row (lowest in the timeline)
/// that is active at a time wins. A row is active from its first keyframe until it ends:
/// an "Element ends here" keyframe, an "ends shot" camera keyframe, or the end keyframe of
/// a follow interval. When it ends, the next active row above it takes the view again.
/// </summary>
public static class DirectorReplayCameraPriority
{
    public static bool IsCameraTrackType(DirectorReplayElementTrackType type)
        => type is DirectorReplayElementTrackType.Camera
            or DirectorReplayElementTrackType.CameraOrbit
            or DirectorReplayElementTrackType.TrackEntity
            or DirectorReplayElementTrackType.FollowEntity;

    /// <summary>Enabled camera rows, the row with the highest priority first.</summary>
    public static IEnumerable<DirectorReplayElementTrack> InPriorityOrder(DirectorReplayViewportTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        for (int index = timeline.Tracks.Count - 1; index >= 0; index--)
        {
            DirectorReplayElementTrack track = timeline.Tracks[index];
            if (track.Enabled && IsCameraTrackType(track.TrackType))
            {
                yield return track;
            }
        }
    }

    /// <summary>Whether <paramref name="track"/> has a camera value at <paramref name="timeSeconds"/>.</summary>
    public static bool IsActive(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(track);
        switch (track.TrackType)
        {
            case DirectorReplayElementTrackType.Camera:
                DirectorReplayViewportClip? current = null;
                foreach (DirectorReplayViewportClip clip in timeline.Clips)
                {
                    if (!clip.Enabled
                        || clip.TrackId != track.Id
                        || clip.SourceType != DirectorReplayViewportSourceType.StaticCamera
                        || clip.TimelineStartSeconds > timeSeconds)
                    {
                        continue;
                    }
                    if (current is null
                        || clip.TimelineStartSeconds > current.TimelineStartSeconds
                        || clip.TimelineStartSeconds == current.TimelineStartSeconds
                            && clip.Id.CompareTo(current.Id) > 0)
                    {
                        current = clip;
                    }
                }
                return current is { EndsShot: false };
            case DirectorReplayElementTrackType.CameraOrbit:
            case DirectorReplayElementTrackType.TrackEntity:
                return DirectorReplayElementTimeline.TrySample(
                    timeline.ElementKeyframes, track.TrackType, track.Id, timeSeconds, out _);
            case DirectorReplayElementTrackType.FollowEntity:
                return DirectorReplayElementTimeline.TrySampleFollowEntityInterval(
                    timeline.ElementKeyframes, track.Id, timeSeconds, out _, out _);
            default:
                return false;
        }
    }

    /// <summary>The camera row that drives the view at <paramref name="timeSeconds"/>, if any.</summary>
    public static DirectorReplayElementTrack? ResolveActive(DirectorReplayViewportTimeline timeline, double timeSeconds)
        => InPriorityOrder(timeline).FirstOrDefault(track => IsActive(timeline, track, timeSeconds));
}
