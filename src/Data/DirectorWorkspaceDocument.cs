namespace VSDirector.Data;

public enum DirectorWorkspaceSection
{
    Cameras,
    Paths,
    Workflows,
    // Serialized workspaces from 0.9 used the name Sequences for value 2.
    // Keep the alias so old commands and layouts continue to resolve.
    Sequences = Workflows,
    Follows,
    Replay,
    Output,
    SequenceEditor,
    Presets,
    // Reserved for workspace layouts saved before the action-library page was removed.
    Actions,
    LiveOffscreen
}

public enum DirectorWorkspaceDock
{
    Floating,
    Left,
    Right,
    Bottom,
    Center,
    Top
}

public sealed class DirectorWorkspacePanelLayout
{
    public const double ToolbarHeight = 34d;

    public const double ToolbarMinimumWidth = 720d;

    public string Id { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 320d;

    public double Height { get; set; } = 420d;

    public double Scale { get; set; } = 1d;

    public DirectorWorkspaceDock Dock { get; set; }

    public bool Locked { get; set; }

    public bool Collapsed { get; set; }

    public bool Visible { get; set; } = true;

    public DirectorWorkspacePanelLayout DeepClone() => (DirectorWorkspacePanelLayout)MemberwiseClone();

    public void Normalize()
    {
        Id = Id?.Trim() ?? string.Empty;
        X = double.IsFinite(X) ? Math.Max(0d, X) : 0d;
        Y = double.IsFinite(Y) ? Math.Max(0d, Y) : 0d;
        bool toolbar = string.Equals(Id, "toolbar", StringComparison.OrdinalIgnoreCase);
        double minimumWidth = toolbar ? ToolbarMinimumWidth : 220d;
        Width = double.IsFinite(Width)
            ? Math.Clamp(Width, minimumWidth, 4096d)
            : toolbar ? 1020d : 320d;
        double maximumHeight = string.Equals(Id, "preview", StringComparison.OrdinalIgnoreCase)
            ? 2160d
            : 1200d;
        Height = toolbar
            ? ToolbarHeight
            : double.IsFinite(Height) ? Math.Clamp(Height, 90d, maximumHeight) : 420d;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.75d, 1.5d) : 1d;
        Dock = Enum.IsDefined(Dock) ? Dock : DirectorWorkspaceDock.Floating;
        if (toolbar || string.Equals(Id, "preview", StringComparison.OrdinalIgnoreCase))
        {
            Collapsed = false;
        }
    }
}

public sealed class DirectorWorkspaceDocument
{
    public const int CurrentSchemaVersion = 16;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DirectorWorkspaceSection ActiveSection { get; set; } = DirectorWorkspaceSection.Cameras;

    public bool NavigationMode { get; set; }

    public double WorkflowZoom { get; set; } = 1d;

    public double WorkflowPanSeconds { get; set; }

    public int WorkflowLaneOffset { get; set; }

    public double PathTimelineZoom { get; set; } = 1d;

    public double PathTimelinePanSeconds { get; set; }

    public bool PathTimelineSnap { get; set; } = true;

    public Dictionary<Guid, double> TimelineRowHeights { get; set; } = new();

    public double ReplayResourceSplitRatio { get; set; } = 0.5d;

    public double ReplayInspectorSplitRatio { get; set; } = 0.5d;

    public List<DirectorWorkspacePanelLayout> Panels { get; set; } = CreateDefaultPanels();

    public DirectorWorkspacePanelLayout Panel(string id)
    {
        string normalizedId = id?.Trim() ?? string.Empty;
        DirectorWorkspacePanelLayout? panel = Panels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (panel is not null)
        {
            return panel;
        }

        DirectorWorkspacePanelLayout created = DefaultPanel(normalizedId);
        Panels.Add(created);
        return created;
    }

    public DirectorWorkspaceDocument DeepClone()
    {
        return new DirectorWorkspaceDocument
        {
            SchemaVersion = SchemaVersion,
            ActiveSection = ActiveSection,
            NavigationMode = NavigationMode,
            WorkflowZoom = WorkflowZoom,
            WorkflowPanSeconds = WorkflowPanSeconds,
            WorkflowLaneOffset = WorkflowLaneOffset,
            PathTimelineZoom = PathTimelineZoom,
            PathTimelinePanSeconds = PathTimelinePanSeconds,
            PathTimelineSnap = PathTimelineSnap,
            TimelineRowHeights = (TimelineRowHeights ?? new()).Where(p => p.Key != Guid.Empty).Take(4096)
                .ToDictionary(p => p.Key, p => DirectorTimelineRows.NormalizeHeight(p.Value)),
            ReplayResourceSplitRatio = ReplayResourceSplitRatio,
            ReplayInspectorSplitRatio = ReplayInspectorSplitRatio,
            Panels = Panels.Select(panel => panel.DeepClone()).ToList()
        };
    }

    public static DirectorWorkspaceDocument Normalize(DirectorWorkspaceDocument? source)
    {
        DirectorWorkspaceDocument document = source?.DeepClone() ?? new DirectorWorkspaceDocument();
        int sourceSchemaVersion = document.SchemaVersion;
        document.SchemaVersion = CurrentSchemaVersion;
        if (sourceSchemaVersion < 16)
        {
            document.TimelineRowHeights ??= new();
        }
        document.ActiveSection = Enum.IsDefined(document.ActiveSection)
            ? document.ActiveSection
            : DirectorWorkspaceSection.Cameras;
        if (document.ActiveSection is DirectorWorkspaceSection.Actions
            or DirectorWorkspaceSection.Workflows
            or DirectorWorkspaceSection.SequenceEditor
            or DirectorWorkspaceSection.Presets)
        {
            document.ActiveSection = DirectorWorkspaceSection.Cameras;
        }
        document.WorkflowZoom = double.IsFinite(document.WorkflowZoom)
            ? Math.Clamp(document.WorkflowZoom, 0.5d, 12d)
            : 1d;
        document.WorkflowPanSeconds = double.IsFinite(document.WorkflowPanSeconds)
            ? Math.Max(0d, document.WorkflowPanSeconds)
            : 0d;
        document.WorkflowLaneOffset = Math.Clamp(document.WorkflowLaneOffset, 0, 31);
        document.PathTimelineZoom = double.IsFinite(document.PathTimelineZoom)
            ? Math.Clamp(document.PathTimelineZoom, 1d, 64d)
            : 1d;
        document.PathTimelinePanSeconds = double.IsFinite(document.PathTimelinePanSeconds)
            ? Math.Max(0d, document.PathTimelinePanSeconds)
            : 0d;
        document.ReplayResourceSplitRatio = NormalizeReplaySplitRatio(
            document.ReplayResourceSplitRatio,
            0.5d);
        document.ReplayInspectorSplitRatio = NormalizeReplaySplitRatio(
            document.ReplayInspectorSplitRatio,
            0.5d);
        document.Panels ??= new List<DirectorWorkspacePanelLayout>();
        document.Panels = document.Panels
            .Where(panel => panel is not null)
            .GroupBy(panel => panel.Id?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(panel => panel.Id.Length > 0)
            .ToList();

        foreach (DirectorWorkspacePanelLayout panel in document.Panels)
        {
            panel.Normalize();
        }

        foreach (string id in new[] { "toolbar", "preview", "resources", "inspector", "timeline", "workflow" })
        {
            _ = document.Panel(id);
        }

        // Core panes stay edge-docked. Resource lists share the new left dock;
        // the large workflow/path/preload editor uses the legacy workflow
        // layout entry as a persistent floating window.
        document.Panel("toolbar").Dock = DirectorWorkspaceDock.Top;
        document.Panel("resources").Dock = DirectorWorkspaceDock.Left;
        document.Panel("inspector").Dock = DirectorWorkspaceDock.Right;
        document.Panel("timeline").Dock = DirectorWorkspaceDock.Bottom;
        document.Panel("workflow").Dock = DirectorWorkspaceDock.Floating;
        foreach (string linkedPanel in new[] { "preview", "resources", "inspector", "timeline", "workflow" })
        {
            DirectorWorkspacePanelLayout panel = document.Panel(linkedPanel);
            panel.Collapsed = false;
            panel.Locked = false;
        }

        if (sourceSchemaVersion < 10)
        {
            DirectorWorkspacePanelLayout resources = document.Panel("resources");
            resources.X = 0d;
            resources.Y = DirectorWorkspacePanelLayout.ToolbarHeight;
            resources.Height = Math.Min(resources.Height, 620d);
            resources.Collapsed = false;
            resources.Locked = false;
        }

        if (sourceSchemaVersion < 14)
        {
            DirectorWorkspacePanelLayout editor = document.Panel("workflow");
            editor.X = 80d;
            editor.Y = 64d;
            editor.Width = 960d;
            editor.Height = 620d;
            editor.Dock = DirectorWorkspaceDock.Floating;
        }

        if (sourceSchemaVersion < 2)
        {
            document.Panel("toolbar").Locked = false;
            foreach (DirectorWorkspacePanelLayout panel in document.Panels)
            {
                panel.Scale = 1d;
            }
        }

        if (sourceSchemaVersion < 3)
        {
            DirectorWorkspacePanelLayout workflow = document.Panel("workflow");
            workflow.Collapsed = false;
            workflow.Locked = false;
        }

        return document;
    }

    private static double NormalizeReplaySplitRatio(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, 0.18d, 0.82d) : fallback;

    private static List<DirectorWorkspacePanelLayout> CreateDefaultPanels()
        => new[] { "toolbar", "preview", "resources", "inspector", "timeline", "workflow" }
            .Select(DefaultPanel)
            .ToList();

    private static DirectorWorkspacePanelLayout DefaultPanel(string id)
    {
        return id.ToLowerInvariant() switch
        {
            "toolbar" => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 180d,
                Y = 8d,
                Width = 1020d,
                Height = DirectorWorkspacePanelLayout.ToolbarHeight,
                Dock = DirectorWorkspaceDock.Top,
                Locked = false
            },
            "preview" => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 0d,
                Y = DirectorWorkspacePanelLayout.ToolbarHeight,
                Width = 960d,
                Height = 568d,
                Dock = DirectorWorkspaceDock.Floating,
                Locked = false
            },
            "inspector" => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 1390d,
                Y = 68d,
                Width = 520d,
                Height = 680d,
                Dock = DirectorWorkspaceDock.Right
            },
            "timeline" => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 330d,
                Y = 690d,
                Width = 900d,
                Height = 240d,
                Dock = DirectorWorkspaceDock.Bottom
            },
            "workflow" => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 80d,
                Y = 64d,
                Width = 960d,
                Height = 620d,
                Dock = DirectorWorkspaceDock.Floating
            },
            _ => new DirectorWorkspacePanelLayout
            {
                Id = id,
                X = 0d,
                Y = DirectorWorkspacePanelLayout.ToolbarHeight,
                Width = 420d,
                Height = 620d,
                Dock = DirectorWorkspaceDock.Floating
            }
        };
    }
}
