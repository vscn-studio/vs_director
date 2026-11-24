using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSImGui.API;

namespace VSDirector.Gui;

internal sealed record DirectorPreloadMapState(int CenterX, int CenterZ, int TargetX, int TargetZ, int Dimension,
    IReadOnlySet<DirectorReplayPreloadColumn> Automatic, IReadOnlySet<DirectorReplayPreloadColumn> Included,
    IReadOnlySet<DirectorReplayPreloadColumn> Excluded, IReadOnlySet<DirectorReplayPreloadColumn> Final);

/// <summary>Native ImGui preload map editor used by the workspace floating tool.</summary>
internal sealed class DirectorPreloadWindow : DirectorImGuiDialog
{
    private readonly ICoreClientAPI api;
    private readonly Func<DirectorPreloadMapState?> state;
    private readonly Action<int, int, int, int, bool> paint;
    private readonly Action<int, int> pan;
    private readonly Action<double> seek;
    private readonly bool replay;
    private readonly double duration;
    private int radius = 6, columns, rows, pointer = -1;
    private int startX, startZ, endX, endZ, panX, panZ;
    private Vector2 pointerStart;
    private double time;
    private bool erase, disposed, playing;
    private DirectorPreloadMapState? dragState;
    private string emptyMessage;

    internal bool IsOpened => Opened;
    internal event Action? Closed;
    private static string L(string key) => Lang.Get("vsdirector:" + key);

    internal DirectorPreloadWindow(ICoreClientAPI api, Func<DirectorPreloadMapState?> state,
        Action<int, int, int, int, bool> paint, Action<int, int> pan, Action<double> seek, bool replay, double duration, double time)
        : base(api, L("preload-editor-title"), "preload", false, ImGuiWindowFlags.None)
    {
        this.api = api;
        this.state = state;
        this.paint = paint;
        this.pan = pan;
        this.seek = seek;
        this.replay = replay;
        this.duration = Math.Max(0, duration);
        this.time = Math.Clamp(time, 0, this.duration);
        emptyMessage = L("preload-loading");

    }

    internal new void Open()
    {
        base.Open();
        Refresh();
    }

    internal void SetEmptyMessage(string text) => emptyMessage = text;

    internal void Tick(float dt)
    {
        if (disposed || !Opened) return;

        if (playing && state() is not null)
        {
            time = Math.Min(duration, time + Math.Max(0, dt));
            seek(time);
            if (time >= duration) SetPlaying(false);
        }
    }

    internal void Refresh()
    {
        // ImGui recalculates map geometry every frame. This method remains cheap
        // so existing workspace callers can request refreshes.
    }

    protected override bool OnClose()
    {
        SetPlaying(false);
        CancelPointer();
        Closed?.Invoke();
        return true;
    }

    protected override bool OnDraw()
    {
        using var spacing = DirectorImGuiBrowser.Use();
        Tick(Math.Min(.1f, ImGui.GetIO().DeltaTime));
        DrawToolbar();
        ImGui.Separator();
        DrawMap();
        ImGui.Separator();
        if (replay) DrawTimeControls();
        DrawLegend();
        return true;
    }

    protected override void PrepareWindow()
        => DirectorImGuiLayout.WindowSize(new Vector2(760, 580), new Vector2(440, 340));

    private void DrawToolbar()
    {
        using (DirectorImGuiTheme.Button(!erase ? "primary" : "normal", quiet: true))
            if (ImGui.Button(L("preload-add-short") + "##add")) SetTool(false);
        ImGui.SameLine();
        using (DirectorImGuiTheme.Button(erase ? "primary" : "normal", quiet: true))
            if (ImGui.Button(L("preload-remove-short") + "##erase")) SetTool(true);
        ImGui.SameLine();
        if (ToolButton("preload-out", "zoom-out", L("workspace-zoom-out"))) Zoom(1);
        ImGui.SameLine();
        if (ToolButton("preload-in", "zoom-in", L("workspace-zoom-in"))) Zoom(-1);
        ImGui.SameLine();
        if (ToolButton("preload-fit", "arrows-maximize", L("workspace-zoom-fit")))
        {
            CancelPointer(); radius = 6; pan(0, 0);
        }
        ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered()) DirectorImGuiTooltip.Show(L("preload-map-help"));
        if (state() is { } data)
            ImGui.TextDisabled(Lang.Get("vsdirector:preload-map-count", data.Final.Count, data.CenterX, data.CenterZ));
        else ImGui.TextDisabled(emptyMessage);
    }

    private void DrawTimeControls()
    {
        if (ToolButton("preload-start", "track-prev", L("preload-start"))) Seek(0);
        ImGui.SameLine();
        if (ToolButton("preload-play", playing ? "pause" : "play", L(playing ? "preload-pause" : "preload-play")))
        {
            if (!playing && time >= duration) Seek(0);
            SetPlaying(!playing);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Clock(time) + " / " + Clock(duration)).X - ImGui.GetStyle().ItemSpacing.X));
        float position = (float)time;
        if (ImGui.SliderFloat("##preload-time", ref position, 0, (float)duration, "")) Seek(position);
        ImGui.SameLine(); ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Clock(time) + " / " + Clock(duration));
    }

    private static bool ToolButton(string id, string icon, string tip)
    {
        bool clicked = DirectorImGuiBrowser.IconButton(id, icon);
        if (ImGui.IsItemHovered()) DirectorImGuiTooltip.Show(tip);
        return clicked;
    }

    private void DrawMap()
    {
        Vector2 available = ImGui.GetContentRegionAvail();
        available.Y = Math.Max(1, available.Y
            - ImGui.GetTextLineHeightWithSpacing() * 2 - ImGui.GetStyle().ItemSpacing.Y * 4
            - (replay ? ImGui.GetFrameHeightWithSpacing() : 0));
        if (!ImGui.BeginChild("preload-map-area", new Vector2(0, available.Y), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            return;
        }

        Vector2 area = ImGui.GetContentRegionAvail();
        rows = radius * 2 + 1;
        columns = Math.Max(1, (int)Math.Ceiling(area.X / Math.Max(1, area.Y / rows)));
        if (columns % 2 == 0) columns++;
        float cellSize = Math.Max(1, Math.Min(area.X / columns, area.Y / rows));
        Vector2 origin = ImGui.GetCursorScreenPos()
            + new Vector2((area.X - columns * cellSize) / 2, (area.Y - rows * cellSize) / 2);
        Vector2 canvas = new(columns * cellSize, rows * cellSize);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + canvas, Color(25, 27, 31));
        var data = state();
        if (data is null)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(12, 12));
            ImGui.TextWrapped(emptyMessage);
            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton("preload-grid-empty", canvas);
            ImGui.EndChild();
            return;
        }

        for (int z = 0; z < rows; z++)
        for (int x = 0; x < columns; x++)
        {
            var column = new DirectorReplayPreloadColumn(
                data.CenterX + x - columns / 2,
                data.CenterZ + z - rows / 2,
                data.Dimension);
            bool excluded = data.Excluded.Contains(column);
            bool included = data.Included.Contains(column);
            bool automatic = data.Final.Contains(column);
            bool selecting = pointer == 0
                && column.X >= Math.Min(startX, endX) && column.X <= Math.Max(startX, endX)
                && column.Z >= Math.Min(startZ, endZ) && column.Z <= Math.Max(startZ, endZ);
            uint fill = excluded ? Color(201, 81, 96) : included ? Color(101, 205, 145)
                : automatic ? Color(50, 139, 101) : Color(53, 56, 62);
            if (selecting) fill = Color(148, 170, 100);
            Vector2 min = origin + new Vector2(x * cellSize + 1, z * cellSize + 1);
            Vector2 max = origin + new Vector2((x + 1) * cellSize - 1, (z + 1) * cellSize - 1);
            draw.AddRectFilled(min, max, fill);
            if (column.X == data.TargetX && column.Z == data.TargetZ)
                draw.AddRect(min, max, Color(244, 211, 94), 0, ImDrawFlags.None, Math.Max(1, cellSize * .08f));
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("preload-grid", canvas,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        if (hovered && Math.Abs(ImGui.GetIO().MouseWheel) > .001f)
            Zoom(ImGui.GetIO().MouseWheel > 0 ? -1 : 1);

        if (hovered && pointer < 0)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) BeginPointer(0, ImGui.GetMousePos(), origin, cellSize, data);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) BeginPointer(1, ImGui.GetMousePos(), origin, cellSize, data);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) BeginPointer(2, ImGui.GetMousePos(), origin, cellSize, data);
        }
        if (pointer >= 0 && dragState is not null && ImGui.IsMouseDown((ImGuiMouseButton)pointer))
        {
            Vector2 mouse = ImGui.GetMousePos();
            if (pointer == 0)
                (endX, endZ) = Column(mouse, origin, cellSize, dragState);
            else
                pan(panX + (int)Math.Round((pointerStart.X - mouse.X) / Math.Max(1, cellSize)),
                    panZ + (int)Math.Round((pointerStart.Y - mouse.Y) / Math.Max(1, cellSize)));
        }
        if (pointer >= 0 && !ImGui.IsMouseDown((ImGuiMouseButton)pointer)) FinishPointer();
        ImGui.EndChild();
    }

    private void BeginPointer(int button, Vector2 mouse, Vector2 origin, float cellSize, DirectorPreloadMapState data)
    {
        SetPlaying(false);
        CancelPointer();
        pointer = button;
        dragState = data;
        pointerStart = mouse;
        panX = data.CenterX - data.TargetX;
        panZ = data.CenterZ - data.TargetZ;
        (startX, startZ) = Column(mouse, origin, cellSize, data);
        endX = startX;
        endZ = startZ;
    }

    private (int, int) Column(Vector2 mouse, Vector2 origin, float cellSize, DirectorPreloadMapState data)
    {
        int x = Math.Clamp((int)Math.Floor((mouse.X - origin.X) / Math.Max(1, cellSize)), 0, columns - 1);
        int z = Math.Clamp((int)Math.Floor((mouse.Y - origin.Y) / Math.Max(1, cellSize)), 0, rows - 1);
        return (data.CenterX + x - columns / 2, data.CenterZ + z - rows / 2);
    }

    private void FinishPointer()
    {
        bool apply = pointer == 0;
        DirectorPreloadMapState? data = dragState;
        CancelPointer();
        if (apply && data is not null)
            paint(Math.Min(startX, endX), Math.Min(startZ, endZ), Math.Max(startX, endX), Math.Max(startZ, endZ), erase);
    }

    private void CancelPointer()
    {
        pointer = -1;
        dragState = null;
    }

    private void SetTool(bool value)
    {
        CancelPointer();
        erase = value;
    }

    private void Zoom(int change)
    {
        CancelPointer();
        radius = Math.Clamp(radius + change, 3, 30);
    }

    private void Seek(double value)
    {
        SetPlaying(false);
        CancelPointer();
        time = Math.Clamp(value, 0, duration);
        seek(time);
    }

    private void SetPlaying(bool value) => playing = value && replay && duration > 0;

    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    private static uint Color(byte r, byte g, byte b, byte a = 255)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, a / 255f));

    private static void DrawLegend()
    {
        LegendItem(L("preload-legend-auto"), new Vector4(50 / 255f, 139 / 255f, 101 / 255f, 1));
        ImGui.SameLine();
        LegendItem(L("preload-legend-manual"), new Vector4(101 / 255f, 205 / 255f, 145 / 255f, 1));
        LegendItem(L("preload-legend-excluded"), new Vector4(201 / 255f, 81 / 255f, 96 / 255f, 1));
        ImGui.SameLine();
        LegendItem(L("preload-legend-other"), new Vector4(53 / 255f, 56 / 255f, 62 / 255f, 1));
    }

    private static void LegendItem(string label, Vector4 color)
    {
        ImGui.TextColored(color, DirectorImGuiIcons.Get("circle-filled"));
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextDisabled(label);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Opened) Close();
            disposed = true;
        }
        base.Dispose(disposing);
    }
}
