using System.Numerics;
using HarmonyLib;
using ImGuiNET;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCameraPreviewDock(object workspace, Action frame,
        Dictionary<string, (Vector2 Position, Vector2 Size)> bounds, float scale)
    {
        var type = workspace.GetType();
        var visible = AccessTools.Property(type, "CameraPreviewVisible");
        var observationVisible = AccessTools.Property(type, "CameraObservationVisible");
        Vector2 position = default, size = default, imageSize = default;
        uint previewDock = 0;
        uint observationDock = 0;
        bool open = true;
        bool observationOpen = false, observationActive = false, previewActive = false, focusObservation = false, focusPreview = false;
        Action draw = () =>
        {
            previewActive = observationActive = false;
            if (observationOpen)
            {
                if (focusObservation) { ImGui.SetNextWindowFocus(); focusObservation = false; }
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
                observationActive = ImGui.Begin("观察视角###vsdirector-camera-observation", ref observationOpen,
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                observationDock = ImGui.GetWindowDockID();
                if (observationActive)
                {
                    position = ImGui.GetWindowPos(); size = ImGui.GetWindowSize();
                    Vector2 available = ImGui.GetContentRegionAvail();
                    imageSize = new(Math.Min(available.X, available.Y * 16 / 9), Math.Min(available.Y, available.X * 9 / 16));
                    ImGui.Dummy(imageSize);
                }
                ImGui.End(); ImGui.PopStyleVar();
            }
            if (!open) return;
            if (focusPreview) { ImGui.SetNextWindowFocus(); focusPreview = false; }
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
            previewActive = ImGui.Begin("预览相机###vsdirector-camera-preview", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            previewDock = ImGui.GetWindowDockID();
            if (previewActive)
            {
                position = ImGui.GetWindowPos(); size = ImGui.GetWindowSize();
                previewDock = ImGui.GetWindowDockID();
                Vector2 available = ImGui.GetContentRegionAvail();
                imageSize = new(Math.Min(available.X, available.Y * 16 / 9), Math.Min(available.Y, available.X * 9 / 16));
                ImGui.Dummy(imageSize);
            }
            ImGui.End(); ImGui.PopStyleVar();
        };
        AccessTools.Method(type, "Add").Invoke(workspace, [draw]);
        var io = ImGui.GetIO();
        var originalDisplay = io.DisplaySize;
        try
        {
            for (int cycle = 0; cycle < 2; cycle++)
            {
                visible.SetValue(workspace, true); open = true;
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                io.DisplaySize = new(1600, 900);
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                // Resize the shared bottom row through the native splitter.
                Vector2 splitter = new(120 * scale, position.Y - 2);
                io.MousePos = splitter; frame();
                io.MouseDown[0] = true; frame();
                io.MousePos = splitter - new Vector2(0, 65); frame(); frame();
                io.MouseDown[0] = false;
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                observationVisible.SetValue(workspace, true); observationOpen = focusObservation = true;
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                if (observationDock == 0 || observationDock != previewDock || !observationActive || previewActive)
                    throw new Exception("Observation did not share and activate the preview dock tab.");
                visible.SetValue(workspace, false); open = false;
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                if (!observationActive) throw new Exception("Closing preview also closed observation.");
                visible.SetValue(workspace, true); open = focusPreview = true;
                for (int i = 0; i < 5; i++) frame();
                if (previewDock != observationDock || !previewActive || observationActive)
                    throw new Exception("Preview did not reopen into the observation tab group.");
                observationVisible.SetValue(workspace, false); observationOpen = false;
                for (int i = 0; i < 5; i++) frame();
                CheckLayout();
                visible.SetValue(workspace, false); open = false;
                for (int i = 0; i < 5; i++) frame();
                if (bounds["timeline"].Size.X * scale < io.DisplaySize.X - 4 * scale)
                    throw new Exception("Closing camera preview did not restore the timeline width.");
                io.DisplaySize = originalDisplay;
                for (int i = 0; i < 3; i++) frame();
            }
            // The observation window must also create the right-hand pane on its own.
            observationVisible.SetValue(workspace, true); observationOpen = focusObservation = true;
            for (int i = 0; i < 5; i++) frame();
            CheckLayout();
            if (!observationActive || observationDock == 0) throw new Exception("Observation requires preview to be opened first.");
            void CheckLayout()
            {
                var timeline = bounds["timeline"];
                Vector2 timelineEnd = (timeline.Position + timeline.Size) * scale;
                if (previewDock == 0 || position.X < timelineEnd.X - 2 || position.X > timelineEnd.X + 8
                    || Math.Abs(position.Y + size.Y - timelineEnd.Y) > 3
                    || Math.Abs(position.X + size.X - io.DisplaySize.X) > 3)
                    throw new Exception($"Camera preview is not docked to the right of the timeline at scale {scale}: preview {position}/{size}, timeline {timeline.Position * scale}/{timeline.Size * scale}.");
                if (Math.Abs(imageSize.X / imageSize.Y - 16f / 9) > .001 || size.X > imageSize.X + 16)
                    throw new Exception($"Camera preview width does not follow the bottom height/aspect at scale {scale}: window {size}, image {imageSize}.");
            }
        }
        finally
        {
            open = false; visible.SetValue(workspace, false);
            observationOpen = false; observationVisible.SetValue(workspace, false);
            io.DisplaySize = originalDisplay; io.MouseDown[0] = false;
            AccessTools.Method(type, "Remove").Invoke(workspace, [draw]);
            for (int i = 0; i < 3; i++) frame();
        }
    }
}
