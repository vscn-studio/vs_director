using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorImGuiWindow? rmlTransformWindow;
    private CameraPreset? transformPose;
    private DirectorReplayViewportTimeline? transformTimeline;
    private Guid transformClipId, transformReplayId, transformSceneId;
    private bool transformTabDismissed;

    private void ComposeCameraTransform()
    {
        if (rmlTransformWindow is null)
        {
            rmlTransformWindow = new DirectorImGuiWindow(capi, "transform", docked: true,
                escape: () => OnEscapePressed(), workspace: imGuiWorkspace);
            rmlTransformWindow.Closed += () => { transformTabDismissed = true; FinishCameraTransform(cancel: false); };
            var form = new DirectorImGuiForm { Docked = true, ClassName = "inspector" };
            form.AddControl(DrawCameraTransform);
            rmlTransformWindow.Render(RmlLabel("camera-transform-title"), form, 320, 500);
        }
        var panel = layout.Panel("inspector");
        rmlTransformWindow.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        rmlTransformWindow.SetInputEnabled(!layout.NavigationMode);
        if (IsOpened() && !transformTabDismissed && !rmlTransformWindow.IsOpened) rmlTransformWindow.Open();
    }

    private void DrawCameraTransform()
    {
        if (section != DirectorWorkspaceSection.Replay)
        {
            ImGui.TextWrapped(RmlLabel("camera-transform-select"));
            return;
        }
        var timeline = transformTimeline ?? owner.ReplayViewportTimeline;
        var clip = timeline.Clips.FirstOrDefault(c => c.Id == selectedReplayViewportClipId
            && c.SourceType == DirectorReplayViewportSourceType.StaticCamera);
        var pose = clip is null ? null : transformPose ?? cameraDragPose ?? ReplayCameraElementPose(clip);
        if (clip is null || pose is null)
        {
            ImGui.TextWrapped(RmlLabel("camera-transform-select"));
            return;
        }
        ImGui.TextWrapped(clip.Name);
        ImGui.Separator();
        ImGui.BeginDisabled(cameraDragOriginal is not null || layout.NavigationMode);
        // Display positions in the same coordinate space as the inspector.
        var displayed = owner.DisplayPosition(pose.X, pose.Y, pose.Z);
        double dx = pose.X - displayed.Item1, dy = pose.Y - displayed.Item2, dz = pose.Z - displayed.Item3;
        Row("position", [displayed.Item1, displayed.Item2, displayed.Item3], (axis, value) =>
        {
            if (axis == 0) transformPose!.X = value + dx;
            if (axis == 1) transformPose!.Y = value + dy;
            if (axis == 2) transformPose!.Z = value + dz;
        });
        Row("rotation", [pose.Pitch, pose.Yaw, pose.Roll], (axis, value) =>
        {
            var angles = transformPose!;
            transformPose = DirectorCameraManipulation.WithEulerRotation(angles,
                axis == 0 ? (float)value : angles.Pitch,
                axis == 1 ? (float)value : angles.Yaw,
                axis == 2 ? (float)value : angles.Roll);
        });
        Row("pivot", [displayed.Item1 + pose.EditorPivotOffsetX, displayed.Item2 + pose.EditorPivotOffsetY,
            displayed.Item3 + pose.EditorPivotOffsetZ], (axis, value) =>
        {
            if (axis == 0) transformPose!.EditorPivotOffsetX = value + dx - transformPose.X;
            if (axis == 1) transformPose!.EditorPivotOffsetY = value + dy - transformPose.Y;
            if (axis == 2) transformPose!.EditorPivotOffsetZ = value + dz - transformPose.Z;
        });
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.TextDisabled(RmlLabel("camera-transform-axes"));
        ImGui.TextWrapped(RmlLabel("camera-transform-pivot-help"));
        ImGui.TextWrapped(RmlLabel("transform-number-help"));

        void Row(string name, double[] values, Action<int, double> edit)
        {
            ImGui.PushID(name);
            float labelWidth = new[] { "position", "rotation", "pivot" }
                .Max(key => ImGui.CalcTextSize(RmlLabel("camera-transform-" + key)).X);
            DrawCameraTransformAxes(RmlLabel("camera-transform-" + name), labelWidth, i =>
            {
                ImGui.PushID(i);
                uint[] colors = [0xff6161f2, 0xff66cc66, 0xfff2a666];
                double value = values[i];
                string tooltip = Lang.Get("vsdirector:camera-transform-axis-tooltip", "XYZ"[i].ToString(), RmlLabel("camera-transform-" + name));
                if (DirectorImGuiNumeric.TransformDouble("##value", ref value, 1, colors[i], tooltip, out bool finished))
                {
                    if (transformPose is null)
                    {
                        owner.PauseReplayForTimelineScrub();
                        transformPose = pose.DeepClone();
                        transformTimeline = timeline;
                        transformClipId = clip.Id;
                        transformReplayId = owner.ActiveReplayId;
                        transformSceneId = timeline.ActiveSceneId;
                    }
                    edit(i, value);
                    clip.CameraPose = transformPose;
                }
                if (finished) FinishCameraTransform(cancel: false);
                ImGui.PopID();
            });
            ImGui.PopID();
        }
    }

    private static void DrawCameraTransformAxes(string label, float labelWidth, Action<int> drawAxis)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(3, 3) * DirectorImGuiTheme.Scale);
        if (!ImGui.BeginTable("axes", 4, ImGuiTableFlags.SizingStretchSame
            | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.PopStyleVar();
            return;
        }
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed,
            Math.Min(labelWidth, ImGui.GetContentRegionAvail().X * .28f));
        // Explicit equal weights keep content-based auto sizing from growing one axis.
        for (int i = 0; i < 3; i++)
            ImGui.TableSetupColumn("XYZ"[i].ToString(), ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        for (int i = 0; i < 3; i++)
        {
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X));
            drawAxis(i);
        }
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private void FinishCameraTransform(bool cancel)
    {
        var timeline = transformTimeline;
        if (timeline is null) return;
        bool valid = transformReplayId == owner.ActiveReplayId && transformSceneId == owner.ReplayViewportTimeline.ActiveSceneId;
        transformTimeline = null;
        transformPose = null;
        transformClipId = Guid.Empty;
        if (!cancel && valid && timeline is not null)
        {
            CommitReplayViewportTimeline(timeline);
            ComposeInspector();
            ComposeTimeline();
        }
    }
}
