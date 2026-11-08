using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Vintagestory.API.Client;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Native docking host with a transparent central game viewport.</summary>
internal sealed class DirectorImGuiWorkspace(ICoreClientAPI api) : ImGuiDialogBase(api)
{
    private readonly List<Action> panels = new();
    private Vector2 previewPosition, previewSize;
    private uint dockId;
    private bool resetLayout;
    internal bool HasDockLayout { get; private set; }
    internal uint InspectorDockId { get; set; }
    internal uint TimelineDockId { get; set; }
    internal bool CameraPreviewVisible { get; set; }
    private uint cameraPreviewDockId;
    private bool cameraPreviewWasVisible;
    private Vector2 previousPreviewDockSize;
    internal event Action<string, Vector2, Vector2>? BoundsChanged;
    internal void Add(Action draw) => panels.Add(draw);
    internal void Remove(Action draw) => panels.Remove(draw);
    internal void ResetLayout() => resetLayout = true;
    internal void SetPreview(double x, double y, double width, double height)
    {
        if (HasDockLayout) return;
        float scale = (float)GuiElement.scaled(1);
        previewPosition = new Vector2((float)x, (float)y) * scale;
        previewSize = new Vector2((float)width, (float)height) * scale;
    }

    internal void ReportBounds(string id, Vector2 position, Vector2 size)
    {
        float scale = DirectorImGuiTheme.Scale;
        if (size.X > 0 && size.Y > 0) BoundsChanged?.Invoke(id, position / scale, size / scale);
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (!Opened) return DrawStatus();
        using var font = DirectorImGuiRuntime.UseFont(dialog: false);
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // ImGui's display size is the coordinate space used by the current
        // client surface.  It can differ from the render API while a native
        // window is resized or during headless/UI probes, so size the dock
        // host from the active viewport and only fall back to the API size
        // when ImGui has not been initialized yet.
        Vector2 frame = ImGui.GetIO().DisplaySize;
        if (frame.X <= 0 || frame.Y <= 0)
            frame = new(ClientApi.Render.FrameWidth, ClientApi.Render.FrameHeight);
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        bool visible = ImGui.Begin("###vsdirector-workspace", ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar(2);
        try
        {
            if (visible)
            {
                float toolbarHeight = (float)VSDirector.Data.DirectorWorkspacePanelLayout.ToolbarHeight * DirectorImGuiTheme.Scale;
                Vector2 position = new(0, toolbarHeight);
                Vector2 size = Vector2.Max(new Vector2(1), frame - position);
                dockId = ImGui.GetID("vsdirector-dockspace");
                if (resetLayout || GetNode(dockId) == 0)
                {
                    RemoveNode(dockId);
                    AddNode(dockId, 1 << 10);
                    SetNodePos(dockId, position);
                    SetNodeSize(dockId, size);
                    SplitNode(dockId, ImGuiDir.Down, .30f, out uint bottom, out uint top);
                    // Keep settings, replay resources and scenes in a
                    // dedicated left-side dock so they share one tab strip.
                    float leftRatio = Math.Clamp(320 * DirectorImGuiTheme.Scale / size.X, .16f, .32f);
                    SplitNode(top, ImGuiDir.Left, leftRatio, out uint left, out uint center);
                    DockWindow("###vsdirector-settings", left);
                    DockWindow("###vsdirector-resource-browser", left);
                    DockWindow("###vsdirector-scene-browser", left);
                    float inspectorRatio = Math.Clamp(320 * DirectorImGuiTheme.Scale / size.X, .2f, .45f);
                    SplitNode(center, ImGuiDir.Right, inspectorRatio, out uint right, out _);
                    DockWindow("###vsdirector-inspector", right);
                    DockWindow("###vsdirector-view", right);
                    DockWindow("###vsdirector-transform", right);
                    DockWindow("###vsdirector-timeline", bottom);
                    TimelineDockId = bottom;
                    cameraPreviewWasVisible = false;
                    cameraPreviewDockId = 0;
                    Finish(dockId);
                    resetLayout = false;
                }
                ImGui.SetCursorScreenPos(position);
                ImGui.DockSpace(dockId, size, ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.NoDockingInCentralNode);
                HasDockLayout = true;
                UpdateCameraPreviewDock();
                nint central = GetCentralNode(dockId);
                if (central != 0)
                {
                    NodeRect(out var rect, central);
                    previewPosition = rect.Min;
                    previewSize = rect.Max - rect.Min;
                    ReportBounds("preview", previewPosition, previewSize);
                }
                // Keep game input in client coordinates and pass it through the empty centre.
                if (previewSize.X > 0 && previewSize.Y > 0)
                    SetWindowHitTestHole(GetCurrentWindow(), previewPosition, previewSize);
                foreach (var panel in panels.ToArray()) panel();
            }
        }
        finally { ImGui.End(); }
        return DrawStatus();
    }

    protected override bool OnDraw() => true;

    private void UpdateCameraPreviewDock()
    {
        if (!CameraPreviewVisible)
        {
            cameraPreviewWasVisible = false;
            cameraPreviewDockId = 0;
            return;
        }
        if (!cameraPreviewWasVisible && TimelineDockId != 0 && GetNode(TimelineDockId) is var timeline && timeline != 0)
        {
            NodeRect(out var rect, timeline);
            Vector2 size = rect.Max - rect.Min;
            float width = PreviewWidth(size.Y, size.X);
            SplitNode(TimelineDockId, ImGuiDir.Right, Math.Clamp(width / Math.Max(1, size.X), .1f, .8f),
                out cameraPreviewDockId, out _);
            DockWindow("###vsdirector-camera-preview", cameraPreviewDockId);
            Finish(dockId);
            previousPreviewDockSize = Vector2.Zero;
            cameraPreviewWasVisible = true;
        }
        if (cameraPreviewDockId == 0) return;
        nint node = GetNode(cameraPreviewDockId);
        if (node == 0) return;
        NodeRect(out var previewRect, node);
        Vector2 currentSize = previewRect.Max - previewRect.Min;
        if (GetNode(TimelineDockId) is var other && other != 0)
        {
            NodeRect(out var timelineRect, other);
            float totalWidth = currentSize.X + timelineRect.Max.X - timelineRect.Min.X;
            Vector2 desired = new(PreviewWidth(currentSize.Y, totalWidth), currentSize.Y);
            if (Vector2.DistanceSquared(desired, previousPreviewDockSize) > 1)
            {
                SetNodeSize(cameraPreviewDockId, desired);
                previousPreviewDockSize = desired;
            }
        }
    }

    private float PreviewWidth(float height, float availableWidth)
    {
        float aspect = ClientApi.Render.FrameWidth / (float)Math.Max(1, ClientApi.Render.FrameHeight);
        float imageHeight = Math.Max(16, height - ImGui.GetFrameHeight() - 8);
        return Math.Clamp(imageHeight * aspect + 8,
            80, Math.Max(80, availableWidth - 240 * DirectorImGuiTheme.Scale));
    }

    // cimgui exports these stable docking APIs; VSImGui 1.2.7's managed binding omits them.
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal Vector2 Min, Max; }
    [DllImport("cimgui", EntryPoint = "igDockBuilderGetNode", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetNode(uint id);
    [DllImport("cimgui", EntryPoint = "igDockBuilderGetCentralNode", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetCentralNode(uint id);
    [DllImport("cimgui", EntryPoint = "ImGuiDockNode_Rect", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NodeRect(out Rect result, nint node);
    [DllImport("cimgui", EntryPoint = "igDockBuilderRemoveNode", CallingConvention = CallingConvention.Cdecl)]
    private static extern void RemoveNode(uint id);
    [DllImport("cimgui", EntryPoint = "igDockBuilderAddNode", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint AddNode(uint id, int flags);
    [DllImport("cimgui", EntryPoint = "igDockBuilderSetNodePos", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetNodePos(uint id, Vector2 position);
    [DllImport("cimgui", EntryPoint = "igDockBuilderSetNodeSize", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetNodeSize(uint id, Vector2 size);
    [DllImport("cimgui", EntryPoint = "igDockBuilderSplitNode", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SplitNode(uint id, ImGuiDir direction, float ratio, out uint atDirection, out uint opposite);
    [DllImport("cimgui", EntryPoint = "igDockBuilderDockWindow", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void DockWindow(string name, uint id);
    [DllImport("cimgui", EntryPoint = "igDockBuilderFinish", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Finish(uint id);
    [DllImport("cimgui", EntryPoint = "igGetCurrentWindow", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetCurrentWindow();
    [DllImport("cimgui", EntryPoint = "igSetWindowHitTestHole", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetWindowHitTestHole(nint window, Vector2 position, Vector2 size);
}
