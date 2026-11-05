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
    private readonly Dictionary<Guid, (float[] Peaks, int Hash, string Markup)> waveformMarkup = new();
    private double TrackHeight(Guid id) => layout.TimelineRowHeights.GetValueOrDefault(id, DirectorTimelineRows.DefaultHeight);
    private DirectorTimelineRows ReplayRows() => new(ActiveReplayTimelineElementTracks().Select(t => TrackHeight(t.Id)));
    private DirectorTimelineRows PathRows() => new((pathEdit?.ElementTracks ?? []).Select(t => TrackHeight(t.Id)));
    private double TimelineLocalY(double mouseY, bool replay) => (mouseY - (replay ? replayTimelineBounds! : pathTimelineBounds!).renderY)
        / Math.Max(.001, GuiElement.scaled(1)) - (replay ? ReplayTimelineHeaderHeight : PathTimelineHeaderHeight);
    private int TimelineRowAt(double mouseY, bool replay)
    {
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
            || args.Y >= bounds.renderY + bounds.OuterHeight) return false;
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
            waveformFrames = owner.OpenTimelineVoiceView(); waveforms = null; waveformKey = ""; waveformFailed = false;
            waveformMarkup.Clear();
        }
        var speakers = visible.Where(t => t.TrackType == DirectorReplayElementTrackType.VoiceAudio)
            .Select(t => t.SpeakerId).ToHashSet(StringComparer.Ordinal);
        int bins = Math.Clamp((int)(width / 3), 1, 768);
        string key = $"{start:R}/{end:R}/{bins}/" + string.Join("/", speakers.Order());
        if (key == waveformKey || waveformFrames is null) return;
        waveformCancellation?.Cancel();
        // Allow only one PCM scan at a time. A newer request retries after the canceled job finishes.
        if (waveformJob is { IsCompleted: false }) return;
        waveformCancellation?.Dispose(); waveformCancellation = new();
        var token = waveformCancellation.Token;
        var frames = waveformFrames;
        waveformKey = key; waveformStart = start; waveformEnd = end; waveformBins = bins; waveforms = null; waveformFailed = false;
        waveformJob = Task.Run(() => DirectorVoiceWaveform.Read(frames, speakers, start, end, bins, token), token);
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
        rmlTimelineDirty = true;
    }
    private void DrawVoiceWaveform(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track,
        double y, double height, double label, double width)
    {
        if (waveforms is null || !waveforms.TryGetValue(track.SpeakerId, out var peaks))
        {
            rmlTimeline!.Shape("wave-loading-" + track.Id, "timeline-wave-message", label + 8, y + 10, width - label - 16, 20,
                Lang.Get(waveformFailed ? "vsdirector:timeline-wave-unavailable"
                    : waveformFrames is { Count: > 0 } ? "vsdirector:timeline-wave-loading" : "vsdirector:timeline-wave-empty"));
            return;
        }
        HashCode hash = new(); hash.Add(track.Enabled); hash.Add(height); hash.Add(width - label);
        var frames = timeline.ElementKeyframes.Where(f => f.TrackId == track.Id).ToArray();
        foreach (var f in frames) { hash.Add(f.Id); hash.Add(f.TimeSeconds); hash.Add(f.Volume); hash.Add(f.InterpolationToNext); }
        int stamp = hash.ToHashCode();
        if (waveformMarkup.TryGetValue(track.Id, out var cached) && ReferenceEquals(cached.Peaks, peaks) && cached.Hash == stamp)
        {
            rmlTimeline!.Waveform("wave-" + track.Id, label, y, width - label, height, cached.Markup); return;
        }
        var sampleFrame = new DirectorReplayVoiceFrame { SpeakerId = track.SpeakerId, SourceId = track.SpeakerId };
        var voiceTimeline = new DirectorReplayViewportTimeline { Tracks = [track], ElementKeyframes = frames.ToList() };
        var markup = new System.Text.StringBuilder();
        void Bar(string style, double x, double top, double w, double h)
            => markup.Append(System.FormattableString.Invariant($"<div class='timeline-shape {style}' style='left:{x:0.##}dp;top:{top:0.##}dp;width:{w:0.##}dp;height:{h:0.##}dp;'></div>"));
        // Use the exact same gain evaluator as playback and export, including the current drag draft.
        double step = (width - label) / waveformBins;
        for (int i = 0; i < peaks.Length; i++)
        {
            if (peaks[i] <= 0) continue;
            double time = waveformStart + (i + .5) * (waveformEnd - waveformStart) / waveformBins;
            DirectorReplayVoiceTrackPolicy.TryResolveGain(voiceTimeline, sampleFrame, time, out float gain);
            double raw = Math.Min(1, peaks[i]) * (height - 8);
            double adjusted = Math.Min(1, peaks[i] * gain) * (height - 8);
            double x = i * step;
            Bar("timeline-wave-raw", x, (height - raw) / 2, Math.Max(1, step - 1), Math.Max(1, raw));
            if (adjusted > 0)
                Bar("timeline-wave-gain", x, (height - adjusted) / 2, Math.Max(1, step - 1), Math.Max(1, adjusted));
        }
        string html = markup.ToString();
        if (waveformMarkup.Count > 128) waveformMarkup.Clear();
        waveformMarkup[track.Id] = (peaks, stamp, html);
        rmlTimeline!.Waveform("wave-" + track.Id, label, y, width - label, height, html);
    }
    private void DisposeTimelineMedia()
    {
        waveformCancellation?.Cancel(); waveformCancellation?.Dispose();
        waveformFrames = null; waveforms = null;
    }
}
