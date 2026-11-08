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
        Vector2 position = default, size = default, imageSize = default;
        uint previewDock = 0;
        bool open = true;
        Action draw = () =>
        {
            if (!open) return;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4));
            if (ImGui.Begin("预览相机###vsdirector-camera-preview", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
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
                visible.SetValue(workspace, false); open = false;
                for (int i = 0; i < 5; i++) frame();
                if (bounds["timeline"].Size.X * scale < io.DisplaySize.X - 4 * scale)
                    throw new Exception("Closing camera preview did not restore the timeline width.");
                io.DisplaySize = originalDisplay;
                for (int i = 0; i < 3; i++) frame();
            }
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
            io.DisplaySize = originalDisplay; io.MouseDown[0] = false;
            AccessTools.Method(type, "Remove").Invoke(workspace, [draw]);
            for (int i = 0; i < 3; i++) frame();
        }
    }
}
