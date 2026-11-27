using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private Guid resizingTimelineTrack;
    private double rowResizeY, rowResizeHeight;
    private object? waveformDocument;
    private IReadOnlyList<DirectorReplayVoiceFrame>? waveformFrames;
    private CancellationTokenSource? waveformCancellation;
    private Task<Dictionary<string, float[]>>? waveformJob;
    private string waveformKey = "";
    private Dictionary<string, float[]>? waveforms;
    private double waveformStart, waveformEnd;
    private int waveformBins;
    private bool waveformFailed;
    private readonly Dictionary<Guid, (float[] Source, int Hash, float[] Display)> waveformMarkup = new();
    private double TrackHeight(Guid id) => layout.TimelineRowHeights.GetValueOrDefault(id, DirectorTimelineRows.DefaultHeight);
    private DirectorTimelineRows ReplayRows() => new(ActiveReplayTimelineElementTracks().Select(t => TrackHeight(t.Id)));
    private DirectorTimelineRows PathRows() => new((pathEdit?.ElementTracks ?? []).Select(t => TrackHeight(t.Id)));
    private double TimelineLocalY(double mouseY, bool replay) => (mouseY - (replay ? replayTimelineBounds! : pathTimelineBounds!).renderY)
        / Math.Max(.001, GuiElement.scaled(1)) - (replay ? ReplayTimelineHeaderHeight : PathTimelineHeaderHeight);
    private int TimelineRowAt(double mouseY, bool replay)
    {
        var bounds = replay ? replayTimelineBounds : pathTimelineBounds;
        if (bounds is null || mouseY >= bounds.renderY + bounds.OuterHeight) return -1;
        var rows = replay ? ReplayRows() : PathRows();
        int first = replay ? replayTimelineFirstVisibleTrack : pathTimelineFirstVisibleTrack;
        double y = TimelineLocalY(mouseY, replay);
        return y < 0 ? -1 : rows.At(y + rows.Top(first));
    }
    private bool TryResizeTimelineRow(MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left || layout.NavigationMode) return false;
        bool replay = section == DirectorWorkspaceSection.Replay;
        var bounds = replay ? replayTimelineBounds : pathTimelineBounds;
        if (bounds is null || args.X < bounds.renderX || args.X > bounds.renderX + bounds.OuterWidth
            || args.Y >= bounds.renderY + bounds.OuterHeight - GuiElement.scaled(TimelineFooterHeight)) return false;
        var rows = replay ? ReplayRows() : PathRows();
        int first = replay ? replayTimelineFirstVisibleTrack : pathTimelineFirstVisibleTrack;
        double y = TimelineLocalY(args.Y, replay);
        if (y < 0) return false;
        int index = rows.At(y + rows.Top(first) - 4);
        if (index < first || index >= rows.Count || Math.Abs(y - (rows.Top(index + 1) - rows.Top(first))) > 4) return false;
        resizingTimelineTrack = replay ? ActiveReplayTimelineElementTracks()[index].Id : pathEdit!.ElementTracks[index].Id;
        rowResizeY = args.Y / GuiElement.scaled(1); rowResizeHeight = TrackHeight(resizingTimelineTrack);
        args.Handled = true;
        return true;
    }
    private void FinishTimelinePointer(MouseEvent args)
    {
        if (resizingTimelineTrack != Guid.Empty)
        {
            resizingTimelineTrack = Guid.Empty; SaveLayout(); args.Handled = true; return;
        }
        OnMouseUp(args);
    }
    private void RequestWaveforms(DirectorReplayViewportTimeline timeline, IEnumerable<DirectorReplayElementTrack> visible,
        double start, double end, double width)
    {
        var document = owner.TimelineDocumentIdentity;
        if (!ReferenceEquals(document, waveformDocument))
        {
            waveformCancellation?.Cancel(); waveformDocument = document;
            waveformFrames = owner.OpenTimelineVoiceView() ?? Array.Empty<DirectorReplayVoiceFrame>();
            waveforms = null; waveformKey = ""; waveformFailed = false;
            waveformMarkup.Clear();
        }
        var speakers = visible.Where(t => t.TrackType == DirectorReplayElementTrackType.VoiceAudio)
            .Select(TimelineVoiceSpeakerId).ToHashSet(StringComparer.Ordinal);
        var trackSpeakers = timeline.Tracks.ToDictionary(track => track.Id, TimelineVoiceSpeakerId);
        foreach (DirectorReplayElementKeyframe clip in timeline.ElementKeyframes.Where(frame =>
                     frame.TrackType == DirectorReplayElementTrackType.VoiceAudio
                     && !string.IsNullOrWhiteSpace(frame.AudioAsset)
                     && trackSpeakers.TryGetValue(frame.TrackId, out string? speakerId)
                     && speakers.Contains(speakerId)))
        {
            speakers.Add(AudioClipWaveformId(trackSpeakers[clip.TrackId], clip.Id));
        }
        int bins = Math.Clamp((int)Math.Ceiling(GuiElement.scaled(width)), 1, 16384);
        string audioKeyframes = string.Join("/", timeline.ElementKeyframes
            .Where(frame => frame.TrackType == DirectorReplayElementTrackType.VoiceAudio
                && trackSpeakers.TryGetValue(frame.TrackId, out string? speakerId) && speakers.Contains(speakerId))
            .OrderBy(frame => frame.TrackId).ThenBy(frame => frame.TimeSeconds)
            .Select(frame => $"{frame.TrackId:N}:{frame.TimeSeconds:R}:{frame.AudioAsset}:{frame.AudioTrimStartSeconds:R}:{frame.AudioTrimEndSeconds:R}"));
        string key = $"{start:R}/{end:R}/{bins}/" + string.Join("/", speakers.Order()) + "/" + audioKeyframes;
        if (key == waveformKey) return;
        waveformCancellation?.Cancel();
        // Allow only one PCM scan at a time. A newer request retries after the canceled job finishes.
        if (waveformJob is { IsCompleted: false }) return;
        waveformCancellation?.Dispose(); waveformCancellation = new();
        var token = waveformCancellation.Token;
        IReadOnlyList<DirectorReplayVoiceFrame> recordedFrames = waveformFrames ?? Array.Empty<DirectorReplayVoiceFrame>();
        DirectorReplayViewportTimeline timelineSnapshot = timeline.DeepClone();
        waveformKey = key; waveformStart = start; waveformEnd = end; waveformBins = bins; waveforms = null; waveformFailed = false;
        waveformJob = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<DirectorReplayVoiceFrame> fileFrames = DirectorReplayAudioFileFrames.Create(timelineSnapshot);
            token.ThrowIfCancellationRequested();
            DirectorReplayVoiceFrame[] waveformFileFrames = fileFrames.Select(frame => new DirectorReplayVoiceFrame
            {
                TimeSeconds = frame.TimeSeconds,
                SourceId = frame.SourceId,
                SpeakerId = frame.SourceId,
                SpeakerName = frame.SpeakerName,
                Volume = 1f, // Keep the source envelope independent of clip edits.
                Spatialized = frame.Spatialized,
                Pcm16 = frame.Pcm16
            }).ToArray();
            var magnitudes = DirectorVoiceWaveform.ReadAveraged(recordedFrames, speakers, start, end, bins, token);
            foreach (var clip in waveformFileFrames.GroupBy(frame => frame.SourceId))
            {
                if (!speakers.Contains(clip.Key)) continue;
                var frames = clip.ToArray();
                float reference = DirectorVoiceWaveform.AmplitudeReference(frames, token);
                magnitudes[clip.Key] = DirectorVoiceWaveform.ReadAveraged(frames,
                    new HashSet<string> { clip.Key }, start, end, bins, token, reference)[clip.Key];
            }
            return magnitudes;
        }, token);
        _ = waveformJob.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private void PollWaveforms()
    {
        if (waveformJob is not { IsCompleted: true } job) return;
        waveformJob = null;
        if (job.IsCompletedSuccessfully && waveformCancellation?.IsCancellationRequested != true) waveforms = job.Result;
        else if (job.IsFaulted)
        {
            waveformFailed = true;
            capi.Logger.Warning("VS Director waveform unavailable: {0}", job.Exception?.GetBaseException().Message);
        }
        else waveformKey = "";
        imGuiTimelineDirty = true;
    }
    private void DrawVoiceWaveform(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track,
        double y, double height, double label, double width)
    {
        if (track.SpeakerId.EndsWith(":volume", StringComparison.Ordinal)) return;
        string speakerId = TimelineVoiceSpeakerId(track);
        if (waveforms is null || !waveforms.TryGetValue(speakerId, out var peaks))
        {
            imGuiTimeline!.Shape("wave-loading-" + track.Id, "timeline-wave-message", label + 8, y + 10, width - label - 16, 20,
                Lang.Get(waveformFailed ? "vsdirector:timeline-wave-unavailable"
                    : waveforms is not null ? "vsdirector:timeline-wave-empty"
                    : waveformFrames is { Count: > 0 } || timeline.ElementKeyframes.Any(frame =>
                        frame.TrackId == track.Id && frame.TrackType == DirectorReplayElementTrackType.VoiceAudio
                        && !string.IsNullOrWhiteSpace(frame.AudioAsset))
                        ? "vsdirector:timeline-wave-loading" : "vsdirector:timeline-wave-empty"));
            return;
        }
        HashCode hash = new(); hash.Add(track.Enabled); hash.Add(height); hash.Add(width - label);
        string volumeSpeakerId = speakerId + ":volume";
        Guid volumeTrackId = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == DirectorReplayElementTrackType.VoiceAudio
            && string.Equals(item.SpeakerId, volumeSpeakerId, StringComparison.Ordinal))?.Id ?? Guid.Empty;
        foreach (var f in timeline.ElementKeyframes.Where(f => f.TrackId == track.Id || f.TrackId == volumeTrackId))
        {
            hash.Add(f.Id); hash.Add(f.TimeSeconds); hash.Add(f.Volume); hash.Add(f.InterpolationToNext);
        }
        int stamp = hash.ToHashCode();
        if (waveformMarkup.TryGetValue(track.Id, out var cached) && ReferenceEquals(cached.Source, peaks) && cached.Hash == stamp)
        {
            DrawVoiceWaveformClips(timeline, track, y, height, label, width, cached.Display);
            return;
        }
        var voiceTimeline = timeline;
        var sampleFrame = new DirectorReplayVoiceFrame { SpeakerId = speakerId, SourceId = speakerId };
        var display = new float[peaks.Length];
        // Use the exact same gain evaluator as playback and export, including the current drag draft.
        for (int i = 0; i < peaks.Length; i++)
        {
            double time = waveformStart + (i + .5) * (waveformEnd - waveformStart) / waveformBins;
            DirectorReplayVoiceTrackPolicy.TryResolveGain(voiceTimeline, sampleFrame, time, out float gain);
            display[i] = peaks[i] * gain;
        }
        if (waveformMarkup.Count > 128) waveformMarkup.Clear();
        waveformMarkup[track.Id] = (peaks, stamp, display);
        DrawVoiceWaveformClips(timeline, track, y, height, label, width, display);
    }

    private void DrawVoiceWaveformClips(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track,
        double y, double height, double label, double width, float[] magnitudes)
    {
        double pixelsPerTick = ReplayTimelinePixelsPerTick;
        double pixelsPerSecond = pixelsPerTick * ReplayTimelineTicksPerSecond;
        var clips = timeline.ElementKeyframes.Where(frame => frame.TrackId == track.Id
                && frame.TrackType == DirectorReplayElementTrackType.VoiceAudio
                && !string.IsNullOrWhiteSpace(frame.AudioAsset))
            .ToArray();
        if (clips.Length == 0)
        {
            imGuiTimeline!.Waveform("wave-" + track.Id, label, y, width - label, height, magnitudes);
            return;
        }

        double viewStart = replayTimelineScrollTicks / ReplayTimelineTicksPerSecond;
        double viewEnd = viewStart + Math.Max(0, (width - label) / pixelsPerSecond);
        for (int clipIndex = 0; clipIndex < clips.Length; clipIndex++)
        {
            DirectorReplayElementKeyframe clip = clips[clipIndex];
            double sourceDuration;
            try { sourceDuration = DirectorReplayAudioFileFrames.DurationSeconds(clip.AudioAsset); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or OverflowException or ArgumentException) { continue; }
            if (sourceDuration <= .02) continue;
            double trimStart = Math.Clamp(clip.AudioTrimStartSeconds, 0, sourceDuration);
            double trimEnd = clip.AudioTrimEndSeconds > 0
                ? Math.Clamp(clip.AudioTrimEndSeconds, trimStart, sourceDuration) : sourceDuration;
            double duration = trimEnd - trimStart;
            double clipStart = clip.TimeSeconds;
            double clipEnd = clipStart + duration;
            if (clipEnd <= viewStart || clipStart >= viewEnd) continue;
            double visibleStart = Math.Max(clipStart, viewStart);
            double visibleEnd = Math.Min(clipEnd, viewEnd);
            double visibleX = label + (visibleStart * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks) * pixelsPerTick;
            double visibleWidth = Math.Max(1, (visibleEnd - visibleStart) * pixelsPerSecond);
            // Imported-clip envelopes are indexed to the visible timeline
            // window, because their PCM frames already include the clip's
            // trim and timeline offset.
            string clipSpeakerId = AudioClipWaveformId(TimelineVoiceSpeakerId(track), clip.Id);
            if (waveforms is null || !waveforms.TryGetValue(clipSpeakerId, out var clipPeaks)) continue;
            int first = Math.Clamp((int)Math.Floor((visibleStart - waveformStart) / (waveformEnd - waveformStart) * clipPeaks.Length), 0, clipPeaks.Length - 1);
            int end = Math.Clamp((int)Math.Ceiling((visibleEnd - waveformStart) / (waveformEnd - waveformStart) * clipPeaks.Length), first + 1, clipPeaks.Length);
            float[] levels = clipPeaks[first..end];
            var clipFrame = new DirectorReplayVoiceFrame
            {
                SpeakerId = TimelineVoiceSpeakerId(track),
                SourceId = clipSpeakerId
            };
            for (int bin = 0; bin < levels.Length; bin++)
            {
                double timelineTime = visibleStart + (bin + .5) * (visibleEnd - visibleStart) / levels.Length;
                DirectorReplayVoiceTrackPolicy.TryResolveGain(timeline, clipFrame, timelineTime, out float gain);
                levels[bin] *= gain * clip.AudioClipGain;
            }
            imGuiTimeline!.Waveform("wave-" + track.Id + "-" + clipIndex, visibleX, y, visibleWidth, height, levels);
            double currentGainDb = clip.AudioClipGain > 0 ? 20 * Math.Log10(clip.AudioClipGain) : -60;
            imGuiTimeline.AudioGainLine("gain-" + clip.Id.ToString("N"), currentGainDb, targetGainDb =>
            {
                float targetGain = Math.Clamp((float)Math.Pow(10, targetGainDb / 20),
                    0, DirectorReplayVoiceTrackPolicy.MaximumVolume);
                DirectorReplayViewportTimeline edited = CurrentReplayViewportTimeline();
                DirectorReplayElementKeyframe? editableClip = edited.ElementKeyframes
                    .FirstOrDefault(candidate => candidate.Id == clip.Id);
                if (editableClip is null) return;

                editableClip.AudioClipGain = targetGain;
                if (CommitReplayViewportTimeline(edited))
                {
                    replayElementFieldsId = Guid.Empty;
                    waveformMarkup.Remove(track.Id);
                    imGuiTimelineDirty = true;
                }
            }, visibleX, y, visibleWidth, height);
        }
    }

    private static string TimelineVoiceSpeakerId(DirectorReplayElementTrack track)
        => string.IsNullOrWhiteSpace(track.SpeakerId)
            ? "vsdirector:audio-" + track.Id.ToString("N")
            : track.SpeakerId;

    private static string AudioClipWaveformId(string speakerId, Guid clipId)
        => speakerId + ":" + clipId.ToString("N");

    private void DrawVoiceAudioTrimHandles(DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrack track, double y, double height, double label, double width, double pixelsPerTick)
    {
        double pixelsPerSecond = pixelsPerTick * ReplayTimelineTicksPerSecond;
        foreach (DirectorReplayElementKeyframe frame in timeline.ElementKeyframes.Where(frame =>
                     frame.TrackId == track.Id && frame.TrackType == DirectorReplayElementTrackType.VoiceAudio
                     && !string.IsNullOrWhiteSpace(frame.AudioAsset)))
        {
            double duration;
            try { duration = DirectorReplayAudioFileFrames.DurationSeconds(frame.AudioAsset); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or OverflowException or ArgumentException)
            {
                continue;
            }
            if (duration <= .02) continue;
            double startX = label + (frame.TimeSeconds * ReplayTimelineTicksPerSecond - replayTimelineScrollTicks) * pixelsPerTick;
            double endX = startX + Math.Max(.02, duration - frame.AudioTrimStartSeconds
                - (frame.AudioTrimEndSeconds > 0 ? duration - frame.AudioTrimEndSeconds : 0)) * pixelsPerSecond;
            if (endX < label || startX > width) continue;

            imGuiTimeline!.AudioTrimClip(frame.Id.ToString("N"), frame.TimeSeconds, duration,
                frame.AudioTrimStartSeconds, frame.AudioTrimEndSeconds, pixelsPerSecond,
                startX, y + 1, Math.Max(0, width - label), Math.Max(0, height - 2),
                (newStart, trimStart, trimEnd) =>
                {
                    DirectorReplayElementKeyframe? editable = timeline.ElementKeyframes
                        .FirstOrDefault(candidate => candidate.Id == frame.Id);
                    if (editable is null) return;
                    editable.TimeSeconds = Math.Max(0, newStart);
                    editable.AudioTrimStartSeconds = trimStart;
                    editable.AudioTrimEndSeconds = trimEnd;
                    if (CommitReplayViewportTimeline(timeline.DeepClone()))
                    {
                        waveformKey = string.Empty;
                        waveformMarkup.Remove(track.Id);
                    }
                },
                () =>
                {
                    selectedReplayElementKeyframeId = frame.Id;
                    selectedReplayElementTrackId = track.Id;
                    imGuiTimelineDirty = true;
                },
                selectedReplayElementKeyframeId == frame.Id);
        }
    }

    private void DisposeTimelineMedia()
    {
        waveformCancellation?.Cancel(); waveformCancellation?.Dispose();
        waveformFrames = null; waveforms = null;
    }
}
