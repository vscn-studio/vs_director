using System.Numerics;
using ImGuiNET;
using VSImGui.API;

namespace VSDirector.Gui;

internal sealed partial class DirectorImGuiWorkspace
{
    private uint creationDock, creationLeft, creationRight, creationBottom, creationPreview;
    private bool resetCreationLayout;
    internal uint CreationToolsDockId { get; private set; }
    internal bool MigrateCreationTools { get; set; }
    private bool fitCreationTools;
    private float creationToolsHeight;
    internal void MeasureCreationToolsHeight(float height)
    { if (fitCreationTools) creationToolsHeight = height; }
    internal float CreationAspect { get; set; } = 16f / 9;
    internal void TrackCreationDock(string title)
    {
        uint id = ImGui.GetWindowDockID();
        if (id == 0) return;
        if (title.EndsWith("###vsdirector-creation-browser", StringComparison.Ordinal)) creationLeft = LeftDockId = id;
        else if (title.EndsWith("###vsdirector-creation-properties", StringComparison.Ordinal)) creationRight = InspectorDockId = id;
        else if (title.EndsWith("###vsdirector-creation-timeline", StringComparison.Ordinal)) creationBottom = TimelineDockId = id;
        else if (title.EndsWith("###vsdirector-creation-preview", StringComparison.Ordinal)) creationPreview = id;
        else if (title.EndsWith("###vsdirector-creation-tools", StringComparison.Ordinal)) CreationToolsDockId = id;
    }
    private CallbackGUIStatus DrawCreationHost()
    {
        using var font = DirectorImGuiRuntime.UseFont(dialog: false);
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        Vector2 frame = ImGui.GetIO().DisplaySize;
        if (VSDirector.Runtime.DirectorCreationRuntime.Current?.NativeAppearanceEditorOpen == true)
        {
            if (creationDock != 0) ImGui.DockSpace(creationDock, Vector2.Zero, ImGuiDockNodeFlags.KeepAliveOnly);
            previewPosition = Vector2.Zero; previewSize = frame;
            return CallbackGUIStatus.DontGrabMouse;
        }
        if (VSDirector.Runtime.DirectorCreationRuntime.Current is { ControllingActor: true } recording)
        {
            if (creationDock != 0) ImGui.DockSpace(creationDock, Vector2.Zero, ImGuiDockNodeFlags.KeepAliveOnly);
            previewPosition = Vector2.Zero; previewSize = frame;
            ImGui.SetNextWindowPos(new(12, 12));
            ImGui.Begin("###creation-recording-status", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking);
            ImGui.TextUnformatted(recording.Status);
            ImGui.TextUnformatted(DirectorCreationText.L("creation-recording-escape"));
            ImGui.End();
            return CallbackGUIStatus.DontGrabMouse;
        }
        ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(frame);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        bool visible = ImGui.Begin("###vsdirector-creation-host", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar(2);
        try
        {
            if (!visible) return DrawStatus();
            if (dockId != 0) ImGui.DockSpace(dockId, Vector2.Zero, ImGuiDockNodeFlags.KeepAliveOnly);
            Vector2 pos = new(0, (float)VSDirector.Data.DirectorWorkspacePanelLayout.ToolbarHeight * DirectorImGuiTheme.Scale);
            Vector2 size = Vector2.Max(Vector2.One, frame - pos);
            creationDock = ImGui.GetID("vsdirector-creation-dockspace");
            if (resetCreationLayout || GetNode(creationDock) == 0)
            {
                RemoveNode(creationDock); AddNode(creationDock, 1 << 10); SetNodePos(creationDock, pos); SetNodeSize(creationDock, size);
                SplitNode(creationDock, ImGuiDir.Down, .30f, out creationBottom, out uint top);
                SplitNode(top, ImGuiDir.Left, .23f, out creationLeft, out uint center);
                SplitNode(center, ImGuiDir.Right, .30f, out creationRight, out _);
                float toolsRatio=Math.Clamp(72*DirectorImGuiTheme.Scale/Math.Max(1,size.Y*.7f),.05f,.38f);
                SplitNode(creationRight, ImGuiDir.Up, toolsRatio, out uint toolsDock, out creationRight);
                CreationToolsDockId=toolsDock;DockWindow("###vsdirector-creation-tools",toolsDock);
                DockWindow("###vsdirector-creation-browser", creationLeft);
                DockWindow("###vsdirector-creation-properties", creationRight);
                DockWindow("###vsdirector-creation-bones", creationRight);
                DockWindow("###vsdirector-creation-ik", creationRight);
                DockWindow("###vsdirector-creation-actions", creationRight);
                DockWindow("###vsdirector-creation-view", creationRight);
                DockWindow("###vsdirector-creation-timeline", creationBottom);
                creationPreview = 0; resetCreationLayout = false; MigrateCreationTools=false;
                fitCreationTools=true; creationToolsHeight=0; Finish(creationDock);
            }
            if(MigrateCreationTools && CreationToolsDockId!=0 && GetNode(CreationToolsDockId)!=0)
            {
                fitCreationTools=true; creationToolsHeight=0; MigrateCreationTools=false;
            }
            if(MigrateCreationTools && creationRight!=0 && GetNode(creationRight)!=0)
            {
                NodeRect(out var rightRect,GetNode(creationRight));
                float toolsRatio=Math.Clamp(72*DirectorImGuiTheme.Scale/Math.Max(1,rightRect.Max.Y-rightRect.Min.Y),.05f,.38f);
                SplitNode(creationRight,ImGuiDir.Up,toolsRatio,out uint toolsDock,out creationRight);
                CreationToolsDockId=toolsDock;DockWindow("###vsdirector-creation-tools",toolsDock);
                Finish(creationDock);MigrateCreationTools=false;fitCreationTools=true;creationToolsHeight=0;
            }
            if (fitCreationTools && creationToolsHeight > 0 && CreationToolsDockId != creationRight
                && GetNode(CreationToolsDockId) is var toolsNode && toolsNode != 0
                && GetNode(creationRight) is var propertiesNode && propertiesNode != 0)
            {
                NodeRect(out var toolsRect, toolsNode); NodeRect(out var propertiesRect, propertiesNode);
                // Only fit the default upper-right split; preserve windows moved by the user.
                if (Math.Abs(toolsRect.Min.X-propertiesRect.Min.X)<1 && Math.Abs(toolsRect.Max.X-propertiesRect.Max.X)<1
                    && Math.Abs(toolsRect.Max.Y-propertiesRect.Min.Y)<12*DirectorImGuiTheme.Scale)
                {
                    float total = toolsRect.Max.Y-toolsRect.Min.Y + propertiesRect.Max.Y-propertiesRect.Min.Y;
                    float height = Math.Clamp(creationToolsHeight, 48*DirectorImGuiTheme.Scale, Math.Max(48*DirectorImGuiTheme.Scale,total*.45f));
                    SetNodeSize(CreationToolsDockId,new(toolsRect.Max.X-toolsRect.Min.X,height));
                    SetNodeSize(creationRight,new(propertiesRect.Max.X-propertiesRect.Min.X,total-height));
                    Finish(creationDock);
                }
                fitCreationTools=false;
            }
            LeftDockId = creationLeft; InspectorDockId = creationRight; TimelineDockId = creationBottom;
            ImGui.SetCursorScreenPos(pos);
            ImGui.DockSpace(creationDock, size, ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.NoDockingInCentralNode);
            if (CreationPreviewVisible && creationPreview == 0 && creationBottom != 0 && GetNode(creationBottom) is var bottom && bottom != 0)
            {
                NodeRect(out var rect, bottom);
                float ratio = Math.Clamp((rect.Max.Y - rect.Min.Y) * CreationAspect / Math.Max(1, rect.Max.X - rect.Min.X), .15f, .6f);
                SplitNode(creationBottom, ImGuiDir.Right, ratio, out creationPreview, out creationBottom);
                DockWindow("###vsdirector-creation-preview", creationPreview); Finish(creationDock);
                TimelineDockId = creationBottom;
            }
            nint central = GetCentralNode(creationDock);
            if (central != 0)
            {
                NodeRect(out var rect, central); previewPosition = rect.Min; previewSize = rect.Max - rect.Min;
                HasDockLayout = true;
                SetWindowHitTestHole(GetCurrentWindow(), previewPosition, previewSize);
            }
            foreach (var panel in creationPanels.ToArray()) panel();
        }
        finally { ImGui.End(); }
        return DrawStatus();
    }
}
