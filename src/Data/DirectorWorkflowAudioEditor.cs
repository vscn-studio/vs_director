namespace VSDirector.Data;

public static class DirectorWorkflowAudioEditor
{
    public const int MaximumTrack = 63;
    public const double MinimumGainDecibels = -96d;
    public const double MaximumGainDecibels = 24d;
    public const double MinimumPlaybackRate = 0.01d;
    public const double MaximumPlaybackRate = 1024d;

    public static bool TryCreate(
        string inputPath,
        double timelineStartSeconds,
        double sourceStartSeconds,
        double durationSeconds,
        int track,
        double gainDecibels,
        double fadeInSeconds,
        double fadeOutSeconds,
        double playbackRate,
        out DirectorTimelineAudioClip clip,
        out string error)
    {
        clip = new DirectorTimelineAudioClip();
        error = string.Empty;
        string path = inputPath?.Trim() ?? string.Empty;
        if (path.Length == 0 || path.Length > 2048)
        {
            error = "workflow-audio-path";
            return false;
        }

        if (!double.IsFinite(timelineStartSeconds)
            || timelineStartSeconds < 0d
            || timelineStartSeconds > DirectorWorkflowPlanner.MaxTimelineSeconds
                - DirectorWorkflowPlanner.MinCardDurationSeconds
            || !double.IsFinite(sourceStartSeconds)
            || sourceStartSeconds < 0d
            || sourceStartSeconds > DirectorWorkflowPlanner.MaxTimelineSeconds
            || !double.IsFinite(durationSeconds)
            || durationSeconds < DirectorWorkflowPlanner.MinCardDurationSeconds
            || timelineStartSeconds + durationSeconds > DirectorWorkflowPlanner.MaxTimelineSeconds
            || track < 0
            || track > MaximumTrack
            || !double.IsFinite(gainDecibels)
            || gainDecibels < MinimumGainDecibels
            || gainDecibels > MaximumGainDecibels
            || !double.IsFinite(fadeInSeconds)
            || fadeInSeconds < 0d
            || fadeInSeconds > durationSeconds
            || !double.IsFinite(fadeOutSeconds)
            || fadeOutSeconds < 0d
            || fadeOutSeconds > durationSeconds
            || !double.IsFinite(playbackRate)
            || playbackRate < MinimumPlaybackRate
            || playbackRate > MaximumPlaybackRate)
        {
            error = "workflow-audio-range";
            return false;
        }

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(path)?.Trim() ?? string.Empty;
        }
        catch (ArgumentException)
        {
            error = "workflow-audio-path";
            return false;
        }
        if (name.Length == 0)
        {
            name = "Audio";
        }
        if (name.Length > 96)
        {
            name = name[..96];
        }

        clip = new DirectorTimelineAudioClip
        {
            Id = Guid.NewGuid(),
            Name = name,
            InputPath = path,
            Track = track,
            TimelineStartSeconds = timelineStartSeconds,
            SourceStartSeconds = sourceStartSeconds,
            DurationSeconds = durationSeconds,
            GainDecibels = gainDecibels,
            FadeInSeconds = fadeInSeconds,
            FadeOutSeconds = fadeOutSeconds,
            PlaybackRate = playbackRate,
            Enabled = true
        };
        return true;
    }

    public static bool TryUpdate(
        DirectorTimelineAudioClip target,
        string inputPath,
        double timelineStartSeconds,
        double sourceStartSeconds,
        double durationSeconds,
        int track,
        double gainDecibels,
        double fadeInSeconds,
        double fadeOutSeconds,
        double playbackRate,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!TryCreate(
            inputPath,
            timelineStartSeconds,
            sourceStartSeconds,
            durationSeconds,
            track,
            gainDecibels,
            fadeInSeconds,
            fadeOutSeconds,
            playbackRate,
            out DirectorTimelineAudioClip replacement,
            out error))
        {
            return false;
        }

        replacement.Id = target.Id == Guid.Empty ? replacement.Id : target.Id;
        replacement.Enabled = target.Enabled;
        target.Id = replacement.Id;
        target.Name = replacement.Name;
        target.InputPath = replacement.InputPath;
        target.Track = replacement.Track;
        target.TimelineStartSeconds = replacement.TimelineStartSeconds;
        target.SourceStartSeconds = replacement.SourceStartSeconds;
        target.DurationSeconds = replacement.DurationSeconds;
        target.GainDecibels = replacement.GainDecibels;
        target.FadeInSeconds = replacement.FadeInSeconds;
        target.FadeOutSeconds = replacement.FadeOutSeconds;
        target.PlaybackRate = replacement.PlaybackRate;
        target.Enabled = replacement.Enabled;
        return true;
    }

    public static DirectorTimelineAudioClip? Resolve(DirectorWorkflow workflow, string selector)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        string value = selector?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        List<DirectorTimelineAudioClip> clips = workflow.AudioClips ?? new List<DirectorTimelineAudioClip>();
        if (int.TryParse(value, out int index) && index >= 1 && index <= clips.Count)
        {
            return clips[index - 1];
        }
        if (Guid.TryParse(value, out Guid id))
        {
            return clips.FirstOrDefault(clip => clip.Id == id);
        }
        return clips.FirstOrDefault(clip => string.Equals(
            clip.Name,
            value,
            StringComparison.OrdinalIgnoreCase));
    }
}
