using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>
/// Native ImGui timeline surface. The workspace still builds the timeline from
/// retained commands, while this class owns rendering and pointer forwarding.
/// </summary>
internal sealed class DirectorTimelineWindow : DirectorImGuiDialog
{
    private abstract record Command(double X, double Y, double Width, double Height);
    private sealed record ShapeCommand(string Id, string Classes, string Text, string Icon, string Title,
        double X, double Y, double Width, double Height) : Command(X, Y, Width, Height);
    private sealed record WaveformCommand(string Id, string Markup,
        double X, double Y, double Width, double Height) : Command(X, Y, Width, Height);
    private sealed record ButtonCommand(string Id, string Text, string Icon, Action Click, bool Enabled, string Tone,
        double X, double Y, double Width, double Height, string Title) : Command(X, Y, Width, Height);
    private sealed record SelectCommand(string Id, string Value, IReadOnlyList<(string Value, string Label)> Options,
        Action<string> Changed, double X, double Y, double Width, double Height) : Command(X, Y, Width, Height);

    private readonly Action<MouseEvent> down;
    private readonly Action<MouseEvent> move;
    private readonly Action<MouseEvent> up;
    private readonly Action<int, int, float, bool> wheel;
    private readonly Action<KeyEvent> key;
    private readonly Action cancel;
    private readonly Action escape;
    private readonly DirectorImGuiWorkspace workspace;
    private float Scale => (float)Math.Max(.001, GuiElement.scaled(1));
    private readonly List<Command> commands = new();
    private readonly List<Rect> interactive = new();
    private bool inputEnabled = true;
    private bool dragging;
    private EnumMouseButton dragButton;
    private double panelX, panelY, panelWidth, panelHeight;
    private double playheadX, playheadLeft, playheadRight, playheadHeight;
    private bool playheadVisible;
    private string cursorLabel = "";
    private double cursorDragX = double.NaN, cursorOriginalX = double.NaN;
    private bool disposed;

    private readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        internal bool Contains(Vector2 point) => point.X >= X && point.Y >= Y
            && point.X <= X + Width && point.Y <= Y + Height;
    }

    internal DirectorTimelineWindow(ICoreClientAPI api, Action<MouseEvent> down, Action<MouseEvent> move,
        Action<MouseEvent> up, Action<int, int, float, bool> wheel, Action<KeyEvent> key, Action cancel, Action escape,
        DirectorImGuiWorkspace workspace)
        : base(api, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, "", "vsdirector-timeline", false)
    {
        this.down = down;
        this.move = move;
        this.up = up;
        this.wheel = wheel;
        this.key = key;
        this.cancel = cancel;
        this.escape = escape;
        this.workspace = workspace;
        workspace.Add(DrawDocked);
    }

    internal bool IsOpened => Opened;

    internal void SetInputEnabled(bool enabled)
    {
        inputEnabled = enabled;
        if (!enabled && dragging) CancelPointer();
    }

    internal void OpenTimeline() => Open();

    internal void Begin(double x, double y, double width, double height)
    {
        panelX = x;
        panelY = y;
        panelWidth = Math.Max(1, width);
        panelHeight = Math.Max(1, height);
        commands.Clear();
        interactive.Clear();
        playheadVisible = false;
        cursorLabel = "";
        cursorDragX = cursorOriginalX = double.NaN;
    }

    internal void End() { }

    internal void SetResizeCorner(double x) { }

    internal void Shape(string id, string classes, double x, double y, double w, double h,
        string text = "", string icon = "", string title = "")
        => commands.Add(new ShapeCommand(id, classes, text, icon, title, x, y, w, h));

    internal void Waveform(string id, double x, double y, double w, double h, string markup)
        => commands.Add(new WaveformCommand(id, markup, x, y, w, h));

    internal void Button(string id, double x, double y, double w, string text, string icon,
        Action click, bool enabled = true, string tone = "")
        => commands.Add(new ButtonCommand(id, text, icon, click, enabled, tone, x, y, w, 28, text));

    internal void Select(string id, double x, double y, double w, string value,
        IEnumerable<(string Value, string Label)> options, Action<string> changed)
    {
        var choices = options.ToArray();
        if (!choices.Any(option => option.Value == value)) value = choices.FirstOrDefault().Value ?? "";
        commands.Add(new SelectCommand(id, value, choices, changed, x, y, w, 28));
    }

    internal void Playhead(double x, double left, double right, double height)
    {
        playheadVisible = x >= left && x <= right;
        playheadX = x;
        playheadLeft = left;
        playheadRight = right;
        playheadHeight = height;
    }

    internal void CursorFeedback(string label, double targetX, double originalX)
    {
        cursorLabel = label;
        cursorDragX = targetX;
        cursorOriginalX = originalX;
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
        => DrawStatus(); // Rendered once as a child of the workspace host.

    private void DrawDocked()
    {
        if (!Opened) return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        DirectorImGuiLayout.WindowSize(new Vector2(800, 260), new Vector2(360, 140));
        bool visible = ImGui.Begin(Vintagestory.API.Config.Lang.Get("vsdirector:workspace-timeline") + "###vsdirector-timeline",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | (inputEnabled ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs));
        ImGui.PopStyleVar();
        try
        {
            workspace.TimelineDockId = ImGui.GetWindowDockID();
            ImGui.SetWindowFontScale(Scale);
            if (visible)
            {
                workspace.ReportBounds("timeline", ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail());
                DrawSurface();
            }
        }
        finally { ImGui.End(); }
    }

    private void DrawSurface()
    {
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 canvas = new Vector2((float)panelWidth, (float)panelHeight) * Scale;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + canvas, DirectorImGuiTheme.Background);
        interactive.Clear();

        foreach (var command in commands.ToArray())
        {
            switch (command)
            {
                case ShapeCommand shape:
                    DrawShape(draw, origin, shape);
                    break;
                case WaveformCommand waveform:
                    DrawWaveform(draw, origin, waveform);
                    break;
                case ButtonCommand button:
                    DrawButton(origin, button);
                    break;
                case SelectCommand select:
                    DrawSelect(origin, select);
                    break;
            }
        }

        if (playheadVisible)
        {
            float x = (float)Math.Clamp(playheadX, playheadLeft, playheadRight);
            draw.AddLine(origin + new Vector2(x, 0) * Scale, origin + new Vector2(x, (float)playheadHeight) * Scale,
                DirectorImGuiTheme.Accent, 1.5f * Scale);
            draw.AddTriangleFilled(origin + new Vector2(x - 5, 0) * Scale,
                origin + new Vector2(x + 5, 0) * Scale, origin + new Vector2(x, 7) * Scale, DirectorImGuiTheme.Accent);
        }
        Vector2 cursorMin = origin + new Vector2((float)playheadLeft, 0) * Scale;
        Vector2 cursorMax = origin + new Vector2((float)playheadRight, (float)playheadHeight) * Scale;
        bool movingKey = double.IsFinite(cursorDragX);
        if (movingKey)
            DirectorTimelineCursor.DragLines(draw, cursorMin, cursorMax, origin.X + (float)cursorDragX * Scale,
                origin.X + (float)cursorOriginalX * Scale, Scale);
        DirectorTimelineCursor.Label(draw, cursorMin, cursorMax,
            origin.X + (float)(movingKey ? cursorDragX : playheadX) * Scale, cursorLabel,
            movingKey ? DirectorTimelineCursor.Destination : DirectorImGuiTheme.Accent, Scale);

        if (!inputEnabled) return;
        Vector2 mouse = ImGui.GetMousePos();
        bool inside = mouse.X >= origin.X && mouse.Y >= origin.Y
            && mouse.X <= origin.X + canvas.X && mouse.Y <= origin.Y + canvas.Y
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        if (!inside && !dragging) return;

        int globalX = (int)Math.Round(mouse.X);
        int globalY = (int)Math.Round(mouse.Y);
        bool onControl = interactive.Any(rect => rect.Contains((mouse - origin) / Scale));
        if (!dragging)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !onControl)
                BeginPointer(globalX, globalY, EnumMouseButton.Left);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) && !onControl)
                BeginPointer(globalX, globalY, EnumMouseButton.Middle);
        }
        else if (MouseButtonDown(dragButton))
        {
            move(new MouseEvent(globalX, globalY,
                dragButton == EnumMouseButton.Left ? 0 : 1,
                dragButton == EnumMouseButton.Left ? 0 : 1, dragButton, 0));
        }
        else
        {
            FinishPointer(globalX, globalY);
        }

        float scroll = ImGui.GetIO().MouseWheel;
        if (inside && Math.Abs(scroll) > .001f && !onControl)
            // The backend sends individual Ctrl keys, but writes the legacy
            // KeyCtrl field before NewFrame overwrites it from modifier events.
            wheel(globalX, globalY, scroll, ImGui.GetIO().KeyCtrl
                || ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl));

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CancelPointer();
            escape();
        }
    }

    private void DrawShape(ImDrawListPtr draw, Vector2 origin, ShapeCommand shape)
    {
        Vector2 min = origin + new Vector2((float)shape.X, (float)shape.Y) * Scale;
        Vector2 max = min + new Vector2((float)shape.Width, (float)shape.Height) * Scale;
        uint fill = ShapeColor(shape.Classes);
        bool selected = shape.Classes.Contains("selected", StringComparison.Ordinal);
        bool textOnly = shape.Classes.Contains("timeline-track-name", StringComparison.Ordinal)
            || shape.Classes.Contains("timeline-track-icon", StringComparison.Ordinal)
            || shape.Classes.Contains("timeline-ruler-label", StringComparison.Ordinal)
            || shape.Classes.Contains("timeline-interval-label", StringComparison.Ordinal);
        if (shape.Classes.Contains("timeline-key", StringComparison.Ordinal))
        {
            Vector2 center = (min + max) * .5f;
            uint color = selected ? DirectorImGuiTheme.Accent : Color(222, 224, 230);
            string icon = DirectorImGuiIcons.Get(shape.Icon);
            bool diamond = shape.Icon == "square-rotated-filled" || icon.Length == 0;
            if (diamond) icon = DirectorImGuiIcons.Get("square-rotated-filled");
            if (icon.Length > 0)
            {
                float fontSize = diamond ? 24 * Scale : ImGui.GetFontSize();
                Vector2 size = ImGui.CalcTextSize(icon) * (fontSize / ImGui.GetFontSize());
                if (selected && !diamond) draw.AddRectFilled(min, max, DirectorImGuiTheme.Selected, 2 * Scale);
                draw.AddText(ImGui.GetFont(), fontSize, center - size * .5f, color, icon);
            }
        }
        else if (!textOnly)
        {
            draw.AddRectFilled(min, max, fill);
            if (shape.Classes.Contains("timeline-row", StringComparison.Ordinal))
                draw.AddLine(new Vector2(min.X, max.Y), max, DirectorImGuiTheme.Border);
            else if (selected) draw.AddRect(min, max, DirectorImGuiTheme.Accent, 0, ImDrawFlags.None, 1.5f * Scale);
        }
        if (textOnly || !string.IsNullOrWhiteSpace(shape.Text))
        {
            string icon = DirectorImGuiIcons.Get(shape.Icon);
            string text = icon.Length > 0 ? icon : shape.Text;
            Vector2 size = ImGui.CalcTextSize(text);
            Vector2 position = min + new Vector2(4 * Scale, MathF.Max(0, (max.Y - min.Y - size.Y) * .5f));
            if (icon.Length > 0) position.X = min.X + (max.X - min.X - size.X) * .5f;
            draw.PushClipRect(min, max, true);
            draw.AddText(position, shape.Classes.Contains("timeline-ruler-label", StringComparison.Ordinal)
                ? DirectorImGuiTheme.Muted : DirectorImGuiTheme.Text, text);
            draw.PopClipRect();
            string tooltip = shape.Title.Length > 0 ? shape.Title : shape.Text;
            if (tooltip.Length > 0 && !shape.Classes.Contains("timeline-ruler-label", StringComparison.Ordinal)
                && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
                ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawWaveform(ImDrawListPtr draw, Vector2 origin, WaveformCommand waveform)
    {
        Vector2 min = origin + new Vector2((float)waveform.X, (float)waveform.Y) * Scale;
        Vector2 max = min + new Vector2((float)waveform.Width, (float)waveform.Height) * Scale;
        draw.AddRectFilled(min, max, Color(38, 54, 66));
        draw.AddRect(min, max, Color(72, 106, 126));
        draw.AddText(min + new Vector2(6, MathF.Max(1, (float)(waveform.Height - 16) * .5f)) * Scale,
            Color(142, 177, 196), waveform.Markup.Contains("loading", StringComparison.OrdinalIgnoreCase)
                ? "…" : "♪");
    }

    private void DrawButton(Vector2 origin, ButtonCommand button)
    {
        Vector2 pos = origin + new Vector2((float)button.X, (float)button.Y) * Scale;
        ImGui.SetCursorScreenPos(pos);
        string icon = DirectorImGuiIcons.Get(button.Tone == "reverse" ? "reverse" : button.Icon);
        string label = icon.Length == 0 ? button.Text : icon;
        using var tone = DirectorImGuiTheme.Button(button.Tone, quiet: true);
        if (!button.Enabled) ImGui.BeginDisabled();
        Vector2 size = new Vector2((float)button.Width, (float)button.Height) * Scale;
        bool clicked = icon.Length > 0
            ? DirectorImGuiIcons.Button($"{label}##timeline-{button.Id}", size)
            : ImGui.Button($"{label}##timeline-{button.Id}", size);
        if (!button.Enabled) ImGui.EndDisabled();
        if (clicked && button.Enabled) button.Click();
        interactive.Add(new Rect((float)button.X, (float)button.Y, (float)button.Width, (float)button.Height));
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(button.Title))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(button.Title);
            ImGui.EndTooltip();
        }
    }

    private void DrawSelect(Vector2 origin, SelectCommand select)
    {
        ImGui.SetCursorScreenPos(origin + new Vector2((float)select.X, (float)select.Y) * Scale);
        ImGui.SetNextItemWidth((float)select.Width * Scale);
        string label = select.Options.FirstOrDefault(option => option.Value == select.Value).Label ?? select.Value;
        if (ImGui.BeginCombo($"##timeline-select-{select.Id}", label,
                ImGuiComboFlags.HeightLargest))
        {
            foreach (var option in select.Options)
            {
                bool selected = option.Value == select.Value;
                if (ImGui.Selectable(option.Label, selected)) select.Changed(option.Value);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        interactive.Add(new Rect((float)select.X, (float)select.Y, (float)select.Width, (float)select.Height));
    }

    private void BeginPointer(int x, int y, EnumMouseButton button)
    {
        dragging = true;
        dragButton = button;
        down(new MouseEvent(x, y, button, 0));
    }

    private void FinishPointer(int x, int y)
    {
        if (!dragging) return;
        dragging = false;
        up(new MouseEvent(x, y, dragButton, 0));
    }

    private void CancelPointer()
    {
        if (!dragging) return;
        dragging = false;
        cancel();
    }

    private static bool MouseButtonDown(EnumMouseButton button)
        => button switch
        {
            EnumMouseButton.Left => ImGui.IsMouseDown(ImGuiMouseButton.Left),
            EnumMouseButton.Middle => ImGui.IsMouseDown(ImGuiMouseButton.Middle),
            _ => false
        };

    private static uint ShapeColor(string classes)
    {
        if (classes.Contains("timeline-ruler", StringComparison.Ordinal)) return DirectorImGuiTheme.Panel;
        if (classes.Contains("timeline-label-bg", StringComparison.Ordinal)) return DirectorImGuiTheme.Background;
        if (classes.Contains("timeline-row", StringComparison.Ordinal))
            return classes.Contains("selected", StringComparison.Ordinal) ? DirectorImGuiTheme.Selected
                : classes.Contains("alternate", StringComparison.Ordinal) ? Color(39, 37, 45) : Color(32, 32, 40);
        if (classes.Contains("timeline-interval", StringComparison.Ordinal))
            return classes.Contains("selected", StringComparison.Ordinal) ? Color(0, 104, 203) : Color(40, 69, 104);
        if (classes.Contains("timeline-export", StringComparison.Ordinal)) return Color(50, 94, 73, 150);
        if (classes.Contains("timeline-reorder", StringComparison.Ordinal)) return DirectorImGuiTheme.Accent;
        if (classes.Contains("timeline-tick", StringComparison.Ordinal)) return classes.Contains("major", StringComparison.Ordinal)
            ? Color(132, 132, 145) : DirectorImGuiTheme.Border;
        return DirectorImGuiTheme.Panel;
    }

    private static uint Color(byte r, byte g, byte b, byte a = 255)
        => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    protected override bool OnDraw() => true;

    protected override bool OnClose()
    {
        CancelPointer();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        workspace.Remove(DrawDocked);
        CancelPointer();
        base.Dispose(disposing);
    }
}
