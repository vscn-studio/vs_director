namespace VSDirector.Data;

/// <summary>
/// Resolves timeline policy for each recorded voice speaker. The shared data
/// path keeps live playback and offline export consistent.
/// </summary>
public static class DirectorReplayVoiceTrackPolicy
{
    public const float MaximumVolume = 2f;

    public static void EnsureTracks(
        DirectorReplayViewportTimeline timeline,
        IEnumerable<DirectorReplayVoiceFrame>? voiceFrames)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        Dictionary<string, string> speakers = CollectSpeakers(voiceFrames);
        if (speakers.Count == 0)
        {
            return;
        }

        if (timeline.Scenes.Count == 0)
        {
            EnsureTracks(timeline.Tracks, speakers);
            SynchronizeLegacyTrackLists(timeline);
            return;
        }

        // The active scene is mirrored in the top-level collections. Update it
        // first, then add the automatic rows to every selectable scene.
        timeline.SyncActiveSceneFromTimeline();
        foreach (DirectorReplayElementScene scene in timeline.Scenes)
        {
            EnsureTracks(scene.Tracks, speakers);
        }
        (timeline.ActiveScene ?? timeline.Scenes[0]).CopyTo(timeline);
    }

    public static bool TryResolveGain(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayVoiceFrame frame,
        double timeSeconds,
        out float gain)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(frame);
        gain = 1f;
        string speakerId = TrackKey(frame);
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == DirectorReplayElementTrackType.VoiceAudio
            && string.Equals(item.SpeakerId, speakerId, StringComparison.Ordinal));
        if (track is null)
        {
            // Preserve audible behaviour for old replays that have no rows.
            return true;
        }
        if (!track.Enabled)
        {
            gain = 0f;
            return false;
        }
        if (DirectorReplayElementTimeline.TrySample(
                timeline.ElementKeyframes,
                DirectorReplayElementTrackType.VoiceAudio,
                track.Id,
                timeSeconds,
                out DirectorReplayElementKeyframe sample))
        {
            gain = Math.Clamp(sample.Volume, 0f, MaximumVolume);
        }
        return gain > 0f;
    }

    public static bool IsEnabled(
        DirectorReplayViewportTimeline timeline,
        DirectorReplayVoiceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(frame);
        string speakerId = TrackKey(frame);
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == DirectorReplayElementTrackType.VoiceAudio
            && string.Equals(item.SpeakerId, speakerId, StringComparison.Ordinal));
        return track?.Enabled ?? true;
    }

    private static Dictionary<string, string> CollectSpeakers(
        IEnumerable<DirectorReplayVoiceFrame>? voiceFrames)
    {
        Dictionary<string, string> speakers = new(StringComparer.Ordinal);
        if (voiceFrames is null)
        {
            return speakers;
        }
        foreach (DirectorReplayVoiceFrame frame in voiceFrames.Where(frame => frame is not null))
        {
            string id = TrackKey(frame);
            if (id.Length == 0)
            {
                continue;
            }
            string name = string.IsNullOrWhiteSpace(frame.SpeakerName)
                ? id
                : frame.SpeakerName.Trim();
            if (!speakers.TryGetValue(id, out string? existing)
                || existing == id && name != id)
            {
                speakers[id] = name;
            }
        }
        return speakers;
    }

    private static void EnsureTracks(
        ICollection<DirectorReplayElementTrack> tracks,
        IReadOnlyDictionary<string, string> speakers)
    {
        foreach ((string speakerId, string speakerName) in speakers)
        {
            DirectorReplayElementTrack? track = tracks.FirstOrDefault(item =>
                item.TrackType == DirectorReplayElementTrackType.VoiceAudio
                && string.Equals(item.SpeakerId, speakerId, StringComparison.Ordinal));
            if (track is null)
            {
                tracks.Add(new DirectorReplayElementTrack
                {
                    TrackType = DirectorReplayElementTrackType.VoiceAudio,
                    SpeakerId = speakerId,
                    Name = speakerName,
                    Enabled = true
                });
            }
            else if (string.IsNullOrWhiteSpace(track.Name)
                || string.Equals(track.Name, track.SpeakerId, StringComparison.Ordinal))
            {
                track.Name = speakerName;
            }
        }
    }

    private static string TrackKey(DirectorReplayVoiceFrame frame)
    {
        string speakerId = frame.SpeakerId?.Trim() ?? string.Empty;
        return speakerId.Length > 0 ? speakerId : frame.SourceId?.Trim() ?? string.Empty;
    }

    private static void SynchronizeLegacyTrackLists(DirectorReplayViewportTimeline timeline)
    {
        timeline.ElementTracks = timeline.Tracks
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
        timeline.DisabledElementTracks = timeline.Tracks
            .Where(track => !track.Enabled)
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
    }
}
