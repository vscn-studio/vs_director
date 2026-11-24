using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private bool AddReplayHeadMotionGroup(DirectorReplayViewportTimeline timeline)
    {
        var motion = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.HeadMotion };
        timeline.Tracks.Add(motion);
        var group = DirectorHeadMotion.EnsureGroup(timeline, motion, Lang.Get("vsdirector:replay-element-head-motion"));
        var frame = new DirectorReplayElementKeyframe
        {
            TrackId = motion.Id, TrackType = motion.TrackType,
            TimeSeconds = ReplayTimelineTickAt(Math.Clamp(owner.ReplayTimeSeconds, 0, owner.ReplayDurationSeconds)) / ReplayTimelineTicksPerSecond,
            HeadMotion = new DirectorHeadMotionClip()
        };
        timeline.ElementKeyframes.Add(frame);
        selectedReplayElementTrackId = motion.Id;
        selectedReplayTimelineElementTrack = ReplayTimelineElementKind.HeadMotion;
        selectedReplayElementKeyframeId = frame.Id;
        selectedReplayViewportClipId = selectedReplayMarkerId = selectedReplaySpeedKeyframeId = Guid.Empty;
        replayElementFieldsId = Guid.Empty;
        expandedReplayPresetGroups.Add(group.Id);
        if (CommitReplayViewportTimeline(timeline))
        {
            ComposeInspector(); ComposeTimeline(); OpenSelectedReplayElementKeyframeEditor();
        }
        return true;
    }

    private void ImGuiReplayHeadMotion(DirectorImGuiForm form, DirectorReplayElementKeyframe frame)
    {
        var clip = frame.HeadMotion?.DeepClone() ?? new DirectorHeadMotionClip();
        double start = frame.TimeSeconds;
        form.AddControl(() =>
        {
            ImGui.TextWrapped(owner.HeadTrackingStatus);
            ImGui.TextWrapped(Lang.Get("vsdirector:head-record-help"));
            ImGui.TextUnformatted(Lang.Get("vsdirector:head-samples", clip.Samples.Count, clip.Duration.ToString("0.00")));
            if (ImGui.Button(ImGuiLabel("head-settings"))) owner.OpenHeadTrackingSettings();
            if (owner.IsRecordingHeadMotion)
            {
                if (ImGui.Button(ImGuiLabel("head-stop"))) { owner.FinishHeadMotionRecording(true); ComposeKeyframeImGui(true); }
                ImGui.SameLine();
                if (ImGui.Button(ImGuiLabel("head-cancel"))) { owner.FinishHeadMotionRecording(false); ComposeKeyframeImGui(true); }
            }
            else if (ImGui.Button(ImGuiLabel("head-record"))) owner.StartHeadMotionRecording(frame.Id);
        });
        form.NumberStep(ImGuiLabel("replay-element-time-seconds"), start, .05f, v => start = Math.Max(0, v));
        form.NumberStep(ImGuiLabel("head-trim-start"), clip.TrimStart, .05f, v => clip.TrimStart = v);
        form.NumberStep(ImGuiLabel("head-trim-end"), clip.TrimEnd, .05f, v => clip.TrimEnd = v);
        form.NumberStep(ImGuiLabel("head-rate"), clip.PlaybackRate, .05f, v => clip.PlaybackRate = v);
        form.NumberStep(ImGuiLabel("head-strength"), clip.Strength, .05f, v => clip.Strength = (float)v);
        form.Check(ImGuiLabel("head-EnableTranslation"), clip.Translation, v => clip.Translation = v);
        form.Check(ImGuiLabel("head-EnableRotation"), clip.Rotation, v => clip.Rotation = v);
        form.Check(ImGuiLabel("head-EnableLeanToZoom"), clip.Zoom, v => clip.Zoom = v);
        KeyframeAction(form, "apply", () =>
        {
            if (owner.IsRecordingHeadMotion) return false;
            var timeline = owner.ReplayViewportTimeline;
            var target = timeline.ElementKeyframes.FirstOrDefault(f => f.Id == frame.Id);
            if (target is null) return false;
            double oldStart = target.TimeSeconds, oldDuration = target.HeadMotion?.Duration ?? 0;
            clip.Normalize(); target.HeadMotion = clip.DeepClone(); target.TimeSeconds = start;
            DirectorHeadMotion.ResizeCameraAnchor(timeline, target, oldStart, oldDuration);
            return CommitReplayViewportTimeline(timeline);
        }, "primary", refresh: true);
        KeyframeAction(form, "delete", DeleteSelectedReplayElementKeyframe);
    }
}
