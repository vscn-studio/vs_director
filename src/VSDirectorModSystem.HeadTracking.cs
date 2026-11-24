using System.Diagnostics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Gui;
using VSDirector.HeadTracking;

namespace VSDirector;

public sealed partial class VSDirectorModSystem
{
    private DirectorImGuiWindow? headTrackingWindow;
    private Guid headRecordingFrame, headRecordingReplay;
    private long headRecordingStart;
    private double headRecordingLimit;
    private CameraPreset? headRecordingBasePose;
    private readonly List<DirectorHeadMotionSample> headRecordingSamples = [];
    internal bool IsRecordingHeadMotion => headRecordingFrame != Guid.Empty;
    internal string HeadTrackingStatus => Lang.Get("vsdirector:head-status-" +
        (!OperatingSystem.IsWindows() ? "unsupported" : !VsdofModSystem.Config.EnableTracking ? "disabled"
        : VsdofModSystem.State.HasTracking ? "connected" : "waiting"));

    private void StartHeadTracking(ICoreClientAPI api)
    {
        VsdofModSystem.Start(api, () => !disposed && !IsReplayExporting
            && !Rendering.DirectorOffscreenRenderRuntime.IsRendering);
        controller!.LivePoseTransform = ApplyLiveHeadMotion;
        api.Input.RegisterHotKey("vsdirector-headtracking", Lang.Get("vsdirector:head-settings"), GlKeys.KeypadMultiply,
            HotkeyType.GUIOrOtherControls, false, VsdofModSystem.ExternalMod, false);
        api.Input.SetHotKeyHandler("vsdirector-headtracking", _ => { OpenHeadTrackingSettings(); return true; });
        api.Input.RegisterHotKey("vsdirector-headcenter", Lang.Get("vsdirector:head-recenter"), GlKeys.Keypad5, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler("vsdirector-headcenter", _ => { VsdofModSystem.Recenter(); return true; });
        api.Input.RegisterHotKey("vsdirector-headpause", Lang.Get("vsdirector:head-toggle"), GlKeys.Keypad0, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler("vsdirector-headpause", _ =>
        {
            VsdofModSystem.Config.EnableTracking = !VsdofModSystem.Config.EnableTracking;
            VsdofModSystem.Save(); return true;
        });
    }

    private CameraPreset ApplyLiveHeadMotion(CameraPreset pose)
    {
        if (IsReplayPlaying && !IsRecordingHeadMotion)
        {
            if (VsdofModSystem.ZoomOverlay is { } overlay) overlay.PercentZoomed = 0;
            return pose;
        }
        if (IsRecordingHeadMotion)
        {
            if (replay?.IsLoading == true) return pose;
            bool cameraOwnsView = replay is not null && DirectorReplayCameraPriority.ResolveActive(
                replay.ViewportTimeline, replay.PreparedCameraTimeSeconds) is not null;
            headRecordingBasePose ??= ((cameraOwnsView ? replayShootingBasePose : null) ?? replayElementReturnPose ?? pose).DeepClone();
            pose = replay is null ? headRecordingBasePose : ApplyReplayShootingCameraVisuals(
                headRecordingBasePose, replay.ViewportTimeline, replay.PreparedCameraTimeSeconds);
        }
        if (!VsdofModSystem.TrySample(pose.Fov, out var sample)) return pose;
        if (IsRecordingHeadMotion)
        {
            if (headRecordingStart == 0) headRecordingStart = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(headRecordingStart).TotalSeconds;
            if (headRecordingSamples.Count == 0 || elapsed - headRecordingSamples[^1].Time >= 1d / 60)
                headRecordingSamples.Add(sample with { Time = headRecordingSamples.Count == 0 ? 0 : Math.Min(elapsed, headRecordingLimit) });
        }
        return DirectorHeadMotion.Apply(pose, sample);
    }

    internal bool StartHeadMotionRecording(Guid frameId)
    {
        if (IsRecordingHeadMotion || !IsReplayPlaying || IsReplayExporting || !EnsureEditAccess(out _)) return false;
        var frame = ReplayViewportTimeline.ElementKeyframes.FirstOrDefault(f => f.Id == frameId && f.TrackType == DirectorReplayElementTrackType.HeadMotion);
        if (frame is null) return false;
        headRecordingLimit = Math.Min(3600, Math.Max(0, ReplayDurationSeconds - frame.TimeSeconds));
        if (headRecordingLimit < 1d / 30) { Show(Lang.Get("vsdirector:head-no-duration")); return false; }
        if (controller?.IsActive != true) { Show(Lang.Get("vsdirector:head-camera-required")); return false; }
        if (!VsdofModSystem.TrySample(out _)) { Show(HeadTrackingStatus); return false; }
        replay!.SetPaused(true);
        headRecordingReplay = ActiveReplayId;
        headRecordingFrame = frameId;
        headRecordingStart = 0;
        headRecordingSamples.Clear();
        headRecordingBasePose = null;
        if (!replay.Seek(frame.TimeSeconds, out _)) { FinishHeadMotionRecording(false); return false; }
        UpdateReplayViewportTimeline();
        return true;
    }

    internal bool FinishHeadMotionRecording(bool save)
    {
        if (!IsRecordingHeadMotion) return false;
        Guid id = headRecordingFrame, replayId = headRecordingReplay;
        headRecordingFrame = headRecordingReplay = Guid.Empty;
        var timeline = ReplayViewportTimeline;
        var frame = timeline.ElementKeyframes.FirstOrDefault(f => f.Id == id);
        bool result = false;
        if (save && ActiveReplayId == replayId && frame is not null && headRecordingSamples.Count > 1)
        {
            double oldDuration = frame.HeadMotion?.Duration ?? 0;
            frame.HeadMotion = new DirectorHeadMotionClip { Samples = headRecordingSamples.ToList(), TrimEnd = headRecordingSamples[^1].Time };
            DirectorHeadMotion.ResizeCameraAnchor(timeline, frame, frame.TimeSeconds, oldDuration);
            if (headRecordingBasePose is { } basePose && timeline.Tracks.FirstOrDefault(t => t.Id == frame.TrackId) is { } motion)
                DirectorHeadMotion.AddCameraAnchor(timeline, motion, frame, basePose);
            result = TryReplaceReplayViewportTimeline(timeline, out _);
        }
        headRecordingSamples.Clear();
        headRecordingBasePose = null;
        return result;
    }

    private void UpdateHeadMotionRecording()
    {
        if (!IsRecordingHeadMotion) return;
        if (!IsReplayPlaying || ActiveReplayId != headRecordingReplay || IsReplayExporting || OffscreenCaptureBusy
            || !ReplayViewportTimeline.ElementKeyframes.Any(f => f.Id == headRecordingFrame))
        { FinishHeadMotionRecording(false); return; }
        if (controller?.IsActive != true || !VsdofModSystem.Config.EnableTracking || !VsdofModSystem.State.HasTracking
            || capi is null || PlayerCameraTrackingPatch.IsTrackingSuspended(capi)
            || headRecordingStart != 0 && (replay?.IsLoading == true
                || Stopwatch.GetElapsedTime(headRecordingStart).TotalSeconds >= headRecordingLimit))
            FinishHeadMotionRecording(true);
    }

    internal void OpenHeadTrackingSettings()
    {
        if (capi is null) return;
        headTrackingWindow ??= new DirectorImGuiWindow(capi, "head-tracking");
        var draft = VsdofModSystem.Config.Copy();
        var form = new DirectorImGuiForm { MinimumWidth = 360 };
        form.AddControl(() =>
        {
            ImGui.TextWrapped(HeadTrackingStatus);
            ImGui.TextWrapped(Lang.Get("vsdirector:head-setup"));
            if (VsdofModSystem.ExternalMod) ImGui.TextWrapped(Lang.Get("vsdirector:head-external"));
            if (IsRecordingHeadMotion)
            {
                ImGui.TextUnformatted(Lang.Get("vsdirector:head-recording", (headRecordingStart == 0 ? 0 : Stopwatch.GetElapsedTime(headRecordingStart).TotalSeconds).ToString("0.0")));
                if (ImGui.Button(Lang.Get("vsdirector:head-stop"))) FinishHeadMotionRecording(true);
                ImGui.SameLine();
                if (ImGui.Button(Lang.Get("vsdirector:head-cancel"))) FinishHeadMotionRecording(false);
            }
        });
        foreach (var field in typeof(HeadTrackingConfig).GetFields())
        {
            if (field.Name is "TranslationGain" or "MaxTranslation") continue; // Legacy combined values; per-axis controls supersede them.
            string label = Lang.Get("vsdirector:head-" + field.Name);
            if (field.FieldType == typeof(bool)) form.Check(label, (bool)field.GetValue(draft)!, value => field.SetValue(draft, value));
            else if (field.FieldType == typeof(float)) form.NumberStep(label, (float)field.GetValue(draft)!, .05f, value => field.SetValue(draft, (float)value));
            else if (field.Name == "CrouchAxis") form.Select(label, draft.CrouchAxis, new[] { ("X", "X"), ("Y", "Y"), ("Z", "Z") }, value => draft.CrouchAxis = value);
            else if (field.Name == "CrouchMode") form.Select(label, draft.CrouchMode,
                new[] { ("hold", Lang.Get("vsdirector:head-hold")), ("toggle", Lang.Get("vsdirector:head-toggle-mode")) }, value => draft.CrouchMode = value);
        }
        form.FooterButton(Lang.Get("vsdirector:apply"), () => { VsdofModSystem.Config = draft.Copy(); VsdofModSystem.Save(); }, "primary");
        form.FooterButton(Lang.Get("vsdirector:head-recenter"), VsdofModSystem.Recenter);
        headTrackingWindow.Render(Lang.Get("vsdirector:head-settings"), form, 480, 600);
        headTrackingWindow.Open();
    }
}
