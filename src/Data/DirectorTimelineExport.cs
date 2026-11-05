using System.Globalization;

namespace VSDirector.Data;

public enum DirectorVideoTransitionType
{
    Cut,
    CrossDissolve,
    FadeFromBlack
}

public sealed class DirectorVideoTransition
{
    public DirectorVideoTransitionType Type { get; set; }

    public double DurationSeconds { get; set; }

    public DirectorVideoTransition DeepClone() => (DirectorVideoTransition)MemberwiseClone();
}

public sealed record DirectorWorkflowTransitionIssue(
    Guid CardId,
    string CardName,
    double RequestedDurationSeconds,
    double AvailableOverlapSeconds);

/// <summary>
/// A rendered video source placed on the edit timeline. Track zero is the
/// highest visual layer; clips which start later win overlaps on one track.
/// </summary>
public sealed class DirectorTimelineVideoClip
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string InputPath { get; set; } = string.Empty;

    public int Track { get; set; }

    public double TimelineStartSeconds { get; set; }

    public double SourceStartSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public DirectorVideoTransition TransitionIn { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public DirectorTimelineVideoClip DeepClone()
    {
        DirectorTimelineVideoClip clone = (DirectorTimelineVideoClip)MemberwiseClone();
        clone.TransitionIn = (TransitionIn ?? new DirectorVideoTransition()).DeepClone();
        return clone;
    }
}

public sealed class DirectorTimelineAudioClip
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string InputPath { get; set; } = string.Empty;

    public int Track { get; set; }

    public double TimelineStartSeconds { get; set; }

    public double SourceStartSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public double GainDecibels { get; set; }

    public double FadeInSeconds { get; set; }

    public double FadeOutSeconds { get; set; }

    /// <summary>
    /// Source playback rate. One preserves pitch and duration; values above
    /// one consume source audio faster while keeping it synchronized to the
    /// output timeline.
    /// </summary>
    public double PlaybackRate { get; set; } = 1d;

    public bool Enabled { get; set; } = true;

    public DirectorTimelineAudioClip DeepClone() => (DirectorTimelineAudioClip)MemberwiseClone();
}

public sealed class DirectorTimelineProject
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Zero selects the end of the last enabled clip. A positive value fixes
    /// the exact output duration and trims clips which extend past it.
    /// </summary>
    public double DurationSeconds { get; set; }

    public DirectorTimecode StartTimecode { get; set; } = new(0L, 60);

    public List<DirectorTimelineVideoClip> VideoClips { get; set; } = new();

    public List<DirectorTimelineAudioClip> AudioClips { get; set; } = new();

    public DirectorTimelineProject DeepClone()
    {
        return new DirectorTimelineProject
        {
            Name = Name,
            DurationSeconds = DurationSeconds,
            StartTimecode = StartTimecode,
            VideoClips = (VideoClips ?? new List<DirectorTimelineVideoClip>())
                .Where(clip => clip is not null)
                .Select(clip => clip.DeepClone())
                .ToList(),
            AudioClips = (AudioClips ?? new List<DirectorTimelineAudioClip>())
                .Where(clip => clip is not null)
                .Select(clip => clip.DeepClone())
                .ToList()
        };
    }
}

public sealed class DirectorTimelineExportSettings
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int FramesPerSecond { get; set; } = 60;

    public int AudioSampleRate { get; set; } = 48000;

    public int VideoCrf { get; set; } = 18;

    public string VideoPreset { get; set; } = "medium";

    public int AudioBitrateKbps { get; set; } = 192;
}

public enum DirectorTimelineInputType
{
    Video,
    Audio
}

public sealed record DirectorTimelineInputPlan(
    int InputIndex,
    Guid ClipId,
    DirectorTimelineInputType Type,
    string InputPath,
    int Track,
    double TimelineStartSeconds,
    double SourceStartSeconds,
    double DurationSeconds,
    DirectorTimecode TimelineTimecode);

/// <summary>
/// One rendered pass containing a workflow card. TimelineOriginSeconds is
/// the replay/workflow time represented by frame zero of the material.
/// </summary>
public sealed record DirectorRenderedWorkflowSource(
    string InputPath,
    double TimelineOriginSeconds);

public sealed class DirectorTimelineExportPlan
{
    internal DirectorTimelineExportPlan(
        string outputPath,
        double durationSeconds,
        DirectorTimecode startTimecode,
        IReadOnlyList<DirectorTimelineInputPlan> inputs,
        string filterGraph,
        IReadOnlyList<string> arguments)
    {
        OutputPath = outputPath;
        DurationSeconds = durationSeconds;
        StartTimecode = startTimecode;
        Inputs = inputs;
        FilterGraph = filterGraph;
        Arguments = arguments;
    }

    public string OutputPath { get; }

    public double DurationSeconds { get; }

    public DirectorTimecode StartTimecode { get; }

    public IReadOnlyList<DirectorTimelineInputPlan> Inputs { get; }

    public string FilterGraph { get; }

    /// <summary>Arguments ready for ProcessStartInfo.ArgumentList.</summary>
    public IReadOnlyList<string> Arguments { get; }
}

public static class DirectorTimelineFfmpegPlanner
{
    private const double Epsilon = 0.000001d;

    public static DirectorWorkflowTransitionIssue? FindUnsupportedCrossDissolve(
        DirectorWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        DirectorWorkflowCard[] cards = EligibleWorkflowCards(workflow).ToArray();
        foreach (DirectorWorkflowCard card in cards)
        {
            if (card.TransitionIn?.Type != DirectorVideoTransitionType.CrossDissolve)
            {
                continue;
            }

            double requested = double.IsFinite(card.TransitionIn.DurationSeconds)
                ? Math.Max(0d, card.TransitionIn.DurationSeconds)
                : 0d;
            double available = AvailableIncomingOverlap(cards, card);
            if (requested <= Epsilon || available + Epsilon < requested)
            {
                return new DirectorWorkflowTransitionIssue(
                    card.Id,
                    card.Name,
                    requested,
                    available);
            }
        }

        return null;
    }

    public static DirectorTimelineProject FromWorkflow(
        DirectorWorkflow workflow,
        Func<DirectorWorkflowCard, string?> resolveVideoPath,
        IEnumerable<DirectorTimelineAudioClip>? audioClips = null,
        double defaultCrossDissolveSeconds = 0.5d,
        DirectorTimecode? startTimecode = null,
        Func<DirectorWorkflowCard, DirectorVideoTransition?>? resolveTransition = null,
        Func<DirectorWorkflowCard, double>? resolveSourceStart = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(resolveVideoPath);

        DirectorWorkflowCard[] cards = EligibleWorkflowCards(workflow)
            .OrderBy(card => card.StartSeconds)
            .ThenBy(card => card.Lane)
            .ThenBy(card => card.Order)
            .ThenBy(card => card.Id)
            .ToArray();

        List<DirectorTimelineVideoClip> videoClips = new(cards.Length);
        foreach (DirectorWorkflowCard card in cards)
        {
            string? inputPath = resolveVideoPath(card)?.Trim();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new InvalidOperationException(
                    $"Workflow card '{card.Name}' does not have a rendered video source.");
            }

            double overlap = AvailableIncomingOverlap(cards, card);
            double transitionDuration = Math.Min(
                Math.Max(0d, defaultCrossDissolveSeconds),
                Math.Min(overlap, card.EndSeconds - card.StartSeconds));
            DirectorVideoTransition? explicitTransition = resolveTransition?.Invoke(card)?.DeepClone();
            DirectorVideoTransition transition = explicitTransition
                ?? new DirectorVideoTransition
                {
                    Type = transitionDuration > Epsilon
                        ? DirectorVideoTransitionType.CrossDissolve
                        : DirectorVideoTransitionType.Cut,
                    DurationSeconds = transitionDuration
                };
            transition.Type = Enum.IsDefined(transition.Type)
                ? transition.Type
                : DirectorVideoTransitionType.Cut;
            double requestedTransitionDuration = double.IsFinite(transition.DurationSeconds)
                ? Math.Max(0d, transition.DurationSeconds)
                : 0d;
            if (transition.Type == DirectorVideoTransitionType.CrossDissolve)
            {
                if (explicitTransition is not null
                    && (requestedTransitionDuration <= Epsilon
                        || overlap + Epsilon < requestedTransitionDuration))
                {
                    throw new InvalidOperationException(
                        $"Workflow card '{card.Name}' requests a {requestedTransitionDuration:0.###}s "
                        + $"cross dissolve but has only {overlap:0.###}s of incoming overlap.");
                }
                transition.DurationSeconds = Math.Min(
                    requestedTransitionDuration,
                    Math.Min(overlap, card.EndSeconds - card.StartSeconds));
                if (transition.DurationSeconds <= Epsilon)
                {
                    transition.Type = DirectorVideoTransitionType.Cut;
                    transition.DurationSeconds = 0d;
                }
            }
            else if (transition.Type == DirectorVideoTransitionType.FadeFromBlack)
            {
                transition.DurationSeconds = Math.Min(
                    requestedTransitionDuration,
                    card.EndSeconds - card.StartSeconds);
            }
            else
            {
                transition.DurationSeconds = 0d;
            }
            double requestedSourceStart = resolveSourceStart?.Invoke(card) ?? 0d;

            videoClips.Add(new DirectorTimelineVideoClip
            {
                Id = card.Id,
                Name = card.Name,
                InputPath = inputPath,
                Track = Math.Clamp(card.Lane, 0, 31),
                TimelineStartSeconds = Math.Max(0d, card.StartSeconds),
                SourceStartSeconds = double.IsFinite(requestedSourceStart)
                    ? Math.Max(0d, requestedSourceStart)
                    : 0d,
                DurationSeconds = card.EndSeconds - Math.Max(0d, card.StartSeconds),
                TransitionIn = transition
            });
        }

        return new DirectorTimelineProject
        {
            Name = string.IsNullOrWhiteSpace(workflow.Name) ? "Workflow" : workflow.Name.Trim(),
            DurationSeconds = Math.Max(0d, workflow.DurationSeconds),
            StartTimecode = startTimecode ?? new DirectorTimecode(0L, 60),
            VideoClips = videoClips,
            AudioClips = (audioClips ?? Array.Empty<DirectorTimelineAudioClip>())
                .Where(clip => clip is not null)
                .Select(clip => clip.DeepClone())
                .ToList()
        };
    }

    /// <summary>
    /// Maps rendered workflow passes back to their cards. The pass video has
    /// already been rendered at <paramref name="timeScale"/>, so card timing,
    /// source offsets and transitions are scaled without retiming the video a
    /// second time. Audio remains a source material and is tempo-adjusted.
    /// </summary>
    public static DirectorTimelineProject FromRenderedWorkflow(
        DirectorWorkflow workflow,
        Func<DirectorWorkflowCard, DirectorRenderedWorkflowSource?> resolveSource,
        double timeScale,
        DirectorTimecode? startTimecode = null,
        double defaultCrossDissolveSeconds = 0.5d)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(resolveSource);
        if (!double.IsFinite(timeScale) || timeScale < 0.05d || timeScale > 1024d)
        {
            throw new ArgumentOutOfRangeException(nameof(timeScale));
        }

        DirectorWorkflow scaled = workflow.DeepClone();
        Dictionary<Guid, DirectorRenderedWorkflowSource> sources = new();
        foreach (DirectorWorkflowCard original in workflow.Cards ?? new List<DirectorWorkflowCard>())
        {
            DirectorRenderedWorkflowSource? source = resolveSource(original);
            if (source is not null)
            {
                sources[original.Id] = source;
            }
        }

        foreach (DirectorWorkflowCard card in scaled.Cards)
        {
            card.StartSeconds /= timeScale;
            card.EndSeconds /= timeScale;
            if (card.TransitionIn is not null)
            {
                card.TransitionIn.DurationSeconds /= timeScale;
            }
        }

        scaled.AudioClips = (workflow.AudioClips ?? new List<DirectorTimelineAudioClip>())
            .Where(clip => clip is not null)
            .Select(clip =>
            {
                DirectorTimelineAudioClip result = clip.DeepClone();
                result.TimelineStartSeconds /= timeScale;
                result.DurationSeconds /= timeScale;
                result.FadeInSeconds /= timeScale;
                result.FadeOutSeconds /= timeScale;
                double existingRate = double.IsFinite(result.PlaybackRate) && result.PlaybackRate > 0d
                    ? result.PlaybackRate
                    : 1d;
                result.PlaybackRate = existingRate * timeScale;
                return result;
            })
            .ToList();

        return FromWorkflow(
            scaled,
            card => sources.TryGetValue(card.Id, out DirectorRenderedWorkflowSource? source)
                ? source.InputPath
                : null,
            scaled.AudioClips,
            defaultCrossDissolveSeconds / timeScale,
            startTimecode,
            card => card.TransitionIn,
            card => sources.TryGetValue(card.Id, out DirectorRenderedWorkflowSource? source)
                ? Math.Max(0d, card.StartSeconds - source.TimelineOriginSeconds / timeScale)
                : 0d);
    }

    public static DirectorTimelineExportPlan Build(
        DirectorTimelineProject project,
        string outputPath,
        DirectorTimelineExportSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        settings ??= new DirectorTimelineExportSettings();

        int width = Math.Clamp(settings.Width, 16, 16384);
        int height = Math.Clamp(settings.Height, 16, 16384);
        width -= width % 2;
        height -= height % 2;
        int frameRate = DirectorTimecode.NormalizeFrameRate(settings.FramesPerSecond);
        int sampleRate = Math.Clamp(settings.AudioSampleRate, 8000, 192000);
        int sourceTimecodeRate = DirectorTimecode.NormalizeFrameRate(project.StartTimecode.FramesPerSecond);
        long convertedStartFrame = (long)Math.Round(
            Math.Max(0L, project.StartTimecode.FrameNumber) * (double)frameRate / sourceTimecodeRate,
            MidpointRounding.AwayFromZero);
        DirectorTimecode startTimecode = new(convertedStartFrame, frameRate);

        double automaticDuration = Math.Max(
            EnabledVideo(project).Select(ClipEnd).DefaultIfEmpty(0d).Max(),
            EnabledAudio(project).Select(ClipEnd).DefaultIfEmpty(0d).Max());
        double duration = double.IsFinite(project.DurationSeconds) && project.DurationSeconds > Epsilon
            ? project.DurationSeconds
            : automaticDuration;
        if (!double.IsFinite(duration) || duration <= Epsilon)
        {
            throw new InvalidOperationException("The timeline does not contain a positive output duration.");
        }

        NormalizedVideoClip[] videos = EnabledVideo(project)
            .Select(clip => NormalizeVideo(clip, duration))
            .Where(clip => clip is not null)
            .Cast<NormalizedVideoClip>()
            // Higher numbered tracks are laid down first, so track zero is on top.
            .OrderByDescending(clip => clip.Track)
            .ThenBy(clip => clip.TimelineStartSeconds)
            .ThenBy(clip => clip.Id)
            .ToArray();
        if (videos.Length == 0)
        {
            throw new InvalidOperationException("The timeline does not contain an enabled video clip.");
        }

        NormalizedAudioClip[] audio = EnabledAudio(project)
            .Select(clip => NormalizeAudio(clip, duration))
            .Where(clip => clip is not null)
            .Cast<NormalizedAudioClip>()
            .OrderBy(clip => clip.Track)
            .ThenBy(clip => clip.TimelineStartSeconds)
            .ThenBy(clip => clip.Id)
            .ToArray();

        List<DirectorTimelineInputPlan> inputs = new(videos.Length + audio.Length);
        List<string> arguments = new() { "-hide_banner", "-y" };
        int inputIndex = 0;
        foreach (NormalizedVideoClip clip in videos)
        {
            arguments.Add("-i");
            arguments.Add(clip.InputPath);
            inputs.Add(ToInputPlan(inputIndex++, clip, DirectorTimelineInputType.Video, startTimecode));
        }
        foreach (NormalizedAudioClip clip in audio)
        {
            arguments.Add("-i");
            arguments.Add(clip.InputPath);
            inputs.Add(ToInputPlan(inputIndex++, clip, DirectorTimelineInputType.Audio, startTimecode));
        }

        string filterGraph = BuildFilterGraph(
            videos,
            audio,
            width,
            height,
            frameRate,
            sampleRate,
            duration);
        arguments.Add("-filter_complex");
        arguments.Add(filterGraph);
        arguments.Add("-map");
        arguments.Add("[vout]");
        if (audio.Length > 0)
        {
            arguments.Add("-map");
            arguments.Add("[aout]");
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", NormalizePreset(settings.VideoPreset),
            "-crf", Math.Clamp(settings.VideoCrf, 0, 51).ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-r", frameRate.ToString(CultureInfo.InvariantCulture)
        });
        if (audio.Length > 0)
        {
            arguments.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", Math.Clamp(settings.AudioBitrateKbps, 64, 512).ToString(CultureInfo.InvariantCulture) + "k",
                "-ar", sampleRate.ToString(CultureInfo.InvariantCulture)
            });
        }
        arguments.AddRange(new[]
        {
            "-t", Number(duration),
            "-timecode", startTimecode.ToString(),
            "-movflags", "+faststart",
            outputPath
        });

        return new DirectorTimelineExportPlan(
            outputPath,
            duration,
            startTimecode,
            inputs.ToArray(),
            filterGraph,
            arguments.ToArray());
    }

    private static IEnumerable<DirectorTimelineVideoClip> EnabledVideo(DirectorTimelineProject project)
        => (project.VideoClips ?? new List<DirectorTimelineVideoClip>())
            .Where(clip => clip is not null && clip.Enabled);

    private static IEnumerable<DirectorTimelineAudioClip> EnabledAudio(DirectorTimelineProject project)
        => (project.AudioClips ?? new List<DirectorTimelineAudioClip>())
            .Where(clip => clip is not null && clip.Enabled);

    private static IEnumerable<DirectorWorkflowCard> EligibleWorkflowCards(DirectorWorkflow workflow)
        => (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null
                && card.Enabled
                && card.Kind == DirectorWorkflowCardKind.Shot
                && DirectorWorkflowPlanner.IsExportSource(card.SourceType)
                && card.SourceId != Guid.Empty
                && double.IsFinite(card.StartSeconds)
                && double.IsFinite(card.EndSeconds)
                && card.EndSeconds - card.StartSeconds >= DirectorWorkflowPlanner.MinCardDurationSeconds);

    private static double AvailableIncomingOverlap(
        IReadOnlyList<DirectorWorkflowCard> cards,
        DirectorWorkflowCard card)
        => cards
            .Where(previous => previous.Id != card.Id
                && previous.StartSeconds < card.StartSeconds - Epsilon
                && previous.EndSeconds > card.StartSeconds + Epsilon
                && previous.Lane >= card.Lane)
            .Select(previous => previous.EndSeconds - card.StartSeconds)
            .DefaultIfEmpty(0d)
            .Max();

    private static double ClipEnd(DirectorTimelineVideoClip clip)
        => FiniteOrZero(clip.TimelineStartSeconds) + Math.Max(0d, FiniteOrZero(clip.DurationSeconds));

    private static double ClipEnd(DirectorTimelineAudioClip clip)
        => FiniteOrZero(clip.TimelineStartSeconds) + Math.Max(0d, FiniteOrZero(clip.DurationSeconds));

    private static NormalizedVideoClip? NormalizeVideo(DirectorTimelineVideoClip clip, double outputDuration)
    {
        if (string.IsNullOrWhiteSpace(clip.InputPath)
            || !double.IsFinite(clip.DurationSeconds)
            || clip.DurationSeconds <= Epsilon)
        {
            return null;
        }

        double originalTimelineStart = FiniteOrZero(clip.TimelineStartSeconds);
        double skippedAtHead = Math.Max(0d, -originalTimelineStart);
        double timelineStart = Math.Max(0d, originalTimelineStart);
        double sourceStart = Math.Max(0d, FiniteOrZero(clip.SourceStartSeconds)) + skippedAtHead;
        double duration = Math.Min(
            clip.DurationSeconds - skippedAtHead,
            outputDuration - timelineStart);
        if (duration <= Epsilon)
        {
            return null;
        }

        DirectorVideoTransition transition = clip.TransitionIn ?? new DirectorVideoTransition();
        double transitionDuration = double.IsFinite(transition.DurationSeconds)
            ? Math.Clamp(transition.DurationSeconds - skippedAtHead, 0d, duration)
            : 0d;
        DirectorVideoTransitionType transitionType = Enum.IsDefined(transition.Type)
            ? transition.Type
            : DirectorVideoTransitionType.Cut;
        if (transitionDuration <= Epsilon)
        {
            transitionType = DirectorVideoTransitionType.Cut;
        }

        return new NormalizedVideoClip(
            clip.Id,
            clip.InputPath.Trim(),
            Math.Clamp(clip.Track, 0, 31),
            timelineStart,
            sourceStart,
            duration,
            transitionType,
            transitionDuration);
    }

    private static NormalizedAudioClip? NormalizeAudio(DirectorTimelineAudioClip clip, double outputDuration)
    {
        if (string.IsNullOrWhiteSpace(clip.InputPath)
            || !double.IsFinite(clip.DurationSeconds)
            || clip.DurationSeconds <= Epsilon)
        {
            return null;
        }

        double originalTimelineStart = FiniteOrZero(clip.TimelineStartSeconds);
        double skippedAtHead = Math.Max(0d, -originalTimelineStart);
        double timelineStart = Math.Max(0d, originalTimelineStart);
        double playbackRate = double.IsFinite(clip.PlaybackRate) && clip.PlaybackRate > 0d
            ? Math.Clamp(clip.PlaybackRate, 0.01d, 1048576d)
            : 1d;
        double sourceStart = Math.Max(0d, FiniteOrZero(clip.SourceStartSeconds))
            + skippedAtHead * playbackRate;
        double duration = Math.Min(
            clip.DurationSeconds - skippedAtHead,
            outputDuration - timelineStart);
        if (duration <= Epsilon)
        {
            return null;
        }

        double fadeIn = double.IsFinite(clip.FadeInSeconds)
            ? Math.Clamp(clip.FadeInSeconds - skippedAtHead, 0d, duration)
            : 0d;
        double fadeOut = double.IsFinite(clip.FadeOutSeconds)
            ? Math.Clamp(clip.FadeOutSeconds, 0d, duration)
            : 0d;
        return new NormalizedAudioClip(
            clip.Id,
            clip.InputPath.Trim(),
            Math.Clamp(clip.Track, 0, 63),
            timelineStart,
            sourceStart,
            duration,
            double.IsFinite(clip.GainDecibels) ? Math.Clamp(clip.GainDecibels, -96d, 24d) : 0d,
            fadeIn,
            fadeOut,
            playbackRate);
    }

    private static DirectorTimelineInputPlan ToInputPlan(
        int inputIndex,
        NormalizedClip clip,
        DirectorTimelineInputType type,
        DirectorTimecode startTimecode)
        => new(
            inputIndex,
            clip.Id,
            type,
            clip.InputPath,
            clip.Track,
            clip.TimelineStartSeconds,
            clip.SourceStartSeconds,
            clip.DurationSeconds,
            startTimecode.AtElapsed(clip.TimelineStartSeconds));

    private static string BuildFilterGraph(
        IReadOnlyList<NormalizedVideoClip> videos,
        IReadOnlyList<NormalizedAudioClip> audio,
        int width,
        int height,
        int frameRate,
        int sampleRate,
        double duration)
    {
        List<string> filters = new()
        {
            $"color=c=black:s={width}x{height}:r={frameRate}:d={Number(duration)},format=yuva420p[vbase]"
        };

        for (int index = 0; index < videos.Count; index++)
        {
            NormalizedVideoClip clip = videos[index];
            List<string> chain = new()
            {
                $"[{index}:v:0]trim=start={Number(clip.SourceStartSeconds)}:duration={Number(clip.DurationSeconds)}",
                "setpts=PTS-STARTPTS",
                $"fps={frameRate}",
                $"scale={width}:{height}:force_original_aspect_ratio=decrease",
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black",
                "setsar=1"
            };
            if (clip.TransitionType == DirectorVideoTransitionType.FadeFromBlack)
            {
                chain.Add($"fade=t=in:st=0:d={Number(clip.TransitionDurationSeconds)}");
            }
            chain.Add("format=yuva420p");
            if (clip.TransitionType == DirectorVideoTransitionType.CrossDissolve)
            {
                chain.Add($"fade=t=in:st=0:d={Number(clip.TransitionDurationSeconds)}:alpha=1");
            }
            chain.Add($"setpts=PTS+{Number(clip.TimelineStartSeconds)}/TB[vclip{index}]");
            filters.Add(string.Join(',', chain));

            string previous = index == 0 ? "vbase" : $"vstage{index - 1}";
            filters.Add(
                $"[{previous}][vclip{index}]overlay=eof_action=repeat:repeatlast=1:shortest=0:" +
                $"enable='between(t,{Number(clip.TimelineStartSeconds)},{Number(clip.TimelineStartSeconds + clip.DurationSeconds)})'" +
                $"[vstage{index}]");
        }
        filters.Add($"[vstage{videos.Count - 1}]trim=duration={Number(duration)},setpts=PTS-STARTPTS,format=yuv420p[vout]");

        for (int index = 0; index < audio.Count; index++)
        {
            NormalizedAudioClip clip = audio[index];
            int ffmpegInput = videos.Count + index;
            List<string> chain = new()
            {
                $"[{ffmpegInput}:a:0]atrim=start={Number(clip.SourceStartSeconds)}:duration={Number(clip.DurationSeconds * clip.PlaybackRate)}",
                "asetpts=PTS-STARTPTS",
                $"aresample={sampleRate}",
                $"aformat=sample_rates={sampleRate}:channel_layouts=stereo"
            };
            chain.AddRange(AudioTempoFilters(clip.PlaybackRate));
            chain.Add($"volume={Number(clip.GainDecibels)}dB");
            if (clip.FadeInSeconds > Epsilon)
            {
                chain.Add($"afade=t=in:st=0:d={Number(clip.FadeInSeconds)}");
            }
            if (clip.FadeOutSeconds > Epsilon)
            {
                chain.Add(
                    $"afade=t=out:st={Number(Math.Max(0d, clip.DurationSeconds - clip.FadeOutSeconds))}:" +
                    $"d={Number(clip.FadeOutSeconds)}");
            }
            long delaySamples = (long)Math.Round(
                clip.TimelineStartSeconds * sampleRate,
                MidpointRounding.AwayFromZero);
            chain.Add($"adelay={delaySamples}S:all=1[aclip{index}]");
            filters.Add(string.Join(',', chain));
        }

        if (audio.Count == 1)
        {
            filters.Add(
                $"[aclip0]apad=pad_dur={Number(duration)},atrim=duration={Number(duration)}," +
                "asetpts=PTS-STARTPTS[aout]");
        }
        else if (audio.Count > 1)
        {
            string audioInputs = string.Concat(Enumerable.Range(0, audio.Count).Select(index => $"[aclip{index}]"));
            filters.Add(
                $"{audioInputs}amix=inputs={audio.Count}:duration=longest:dropout_transition=0:normalize=0," +
                $"apad=pad_dur={Number(duration)},atrim=duration={Number(duration)},asetpts=PTS-STARTPTS[aout]");
        }

        return string.Join(';', filters);
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0d;

    private static string Number(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NormalizePreset(string? value)
    {
        string preset = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow"
                ? preset
                : "medium";
    }

    private static IEnumerable<string> AudioTempoFilters(double playbackRate)
    {
        double remaining = Math.Clamp(playbackRate, 0.01d, 1048576d);
        while (remaining < 0.5d - Epsilon)
        {
            yield return "atempo=0.5";
            remaining /= 0.5d;
        }
        while (remaining > 2d + Epsilon)
        {
            yield return "atempo=2";
            remaining /= 2d;
        }
        if (Math.Abs(remaining - 1d) > Epsilon)
        {
            yield return $"atempo={Number(remaining)}";
        }
    }

    private abstract record NormalizedClip(
        Guid Id,
        string InputPath,
        int Track,
        double TimelineStartSeconds,
        double SourceStartSeconds,
        double DurationSeconds);

    private sealed record NormalizedVideoClip(
        Guid Id,
        string InputPath,
        int Track,
        double TimelineStartSeconds,
        double SourceStartSeconds,
        double DurationSeconds,
        DirectorVideoTransitionType TransitionType,
        double TransitionDurationSeconds)
        : NormalizedClip(Id, InputPath, Track, TimelineStartSeconds, SourceStartSeconds, DurationSeconds);

    private sealed record NormalizedAudioClip(
        Guid Id,
        string InputPath,
        int Track,
        double TimelineStartSeconds,
        double SourceStartSeconds,
        double DurationSeconds,
        double GainDecibels,
        double FadeInSeconds,
        double FadeOutSeconds,
        double PlaybackRate)
        : NormalizedClip(Id, InputPath, Track, TimelineStartSeconds, SourceStartSeconds, DurationSeconds);
}
