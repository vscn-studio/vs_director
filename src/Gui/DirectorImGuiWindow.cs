using System.Globalization;
using System.Numerics;
using System.Net;
using System.Text;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Native VSImGui window used by the remaining workspace composers.</summary>
internal sealed class DirectorImGuiWindow : DirectorImGuiDialog
{
    private static readonly HashSet<object> Hosts = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<object> DockedHosts = new(ReferenceEqualityComparer.Instance);
    private readonly ICoreClientAPI api;
    private readonly string id;
    private readonly bool docked;
    private readonly DirectorImGuiWorkspace? workspace;
    private readonly Action? escape;
    private bool disposed;
    private bool inputEnabled = true;
    private string title = string.Empty;
    private string status = string.Empty;
    private DirectorImGuiForm? form;
    private Vector2? nextPosition;
    private Vector2 positionPivot;
    private float? anchoredMaximumHeight;
    private int anchorLayoutFrames;
    private Vector2? nextSize;
    private Vector2 preferredSize;
    private Vector2 lastPosition;
    private Vector2 lastSize;
    private bool hasBounds;
    private bool applyBounds;
    private bool closedNotified;
    private Action? pendingCommit;
    private bool focusNextFrame;

    internal void FocusNextFrame() => focusNextFrame = true;

    internal static bool OwnsHost(object host) => Hosts.Contains(host);
    internal static bool IsDockedHost(object host) => DockedHosts.Contains(host);
    internal static bool IsTextInputFocused => ImGui.GetIO().WantTextInput;
    internal static void TrackChildModal(object child) { }
    internal static bool ReleaseDrag(MouseEvent args) => false;

    public bool IsOpened => Opened;
    public bool IsDisposed => disposed;
    internal bool ContainsScreenPoint(float x, float y)
        => hasBounds && x >= lastPosition.X && y >= lastPosition.Y
            && x < lastPosition.X + lastSize.X && y < lastPosition.Y + lastSize.Y;
    internal DirectorImGuiDocument? Document { get; private set; }
    public event Action? Closed;

    public DirectorImGuiWindow(ICoreClientAPI api, string id, bool docked = false,
        Action? escape = null, bool overlay = false, bool fullscreen = false, DirectorImGuiWorkspace? workspace = null)
        : base(api, docked ? DockedFlags : overlay ? ImGuiWindowFlags.NoSavedSettings : ImGuiWindowFlags.None,
            "VS Director", id, false)
    {
        this.api = api;
        this.id = id;
        this.docked = docked;
        this.workspace = workspace;
        if (docked)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            workspace.Add(DrawDocked);
        }
        this.escape = escape;
        Hosts.Add(this);
        if (docked) DockedHosts.Add(this);
        if (fullscreen) SetFullscreen(true);
    }

    internal void SetFullscreen(bool enabled)
    {
        if (enabled)
        {
            nextPosition = Vector2.Zero;
            nextSize = new Vector2(api.Render.FrameWidth, api.Render.FrameHeight);
            applyBounds = true;
        }
    }

    public void Render(string title, DirectorImGuiForm form, int preferredWidth = 680, int preferredHeight = 640)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        FlushCommit();
        this.title = title;
        this.form = form;
        form.Docked = docked;
        form.BeforeAction = FlushCommit;
        Document ??= new DirectorImGuiDocument();
        Document.ResetBounds();
        form.AttachDocument(Document);
        form.SetText("menu-status", status);
        if (nextSize is null || preferredSize != new Vector2(preferredWidth, preferredHeight))
        {
            preferredSize = new Vector2(preferredWidth, preferredHeight);
            float scale = (float)Math.Max(0.001, GuiElement.scaled(1));
            nextSize = scale * new Vector2(
                Math.Clamp(preferredWidth, 220, Math.Max(220, api.Render.FrameWidth / scale - 24)),
                Math.Clamp(preferredHeight, 160, Math.Max(160, api.Render.FrameHeight / scale - 48)));
            applyBounds = true;
        }
    }

    public new void Open()
    {
        if (disposed) return;
        closedNotified = true;
        base.Open();
        if (!docked) DirectorToolWindows.Claim(this, () => { Close(); });
    }

    public new bool Close()
    {
        if (disposed) return true;
        FlushCommit();
        base.Close();
        return true;
    }

    protected override bool OnClose()
    {
        FlushCommit();
        DirectorToolWindows.Release(this);
        if (closedNotified)
        {
            closedNotified = false;
            Closed?.Invoke();
        }
        return true;
    }

    internal void SetInputEnabled(bool enabled) { inputEnabled = enabled; if (!enabled) FlushCommit(); }
    internal void SetDockBounds(double x, double y, double w, double h)
    {
        if (!docked) return;
        float scale = (float)Math.Max(0.001, GuiElement.scaled(1));
        nextPosition = new Vector2((float)(x * scale), (float)(y * scale));
        nextSize = new Vector2((float)(w * scale), (float)(h * scale));
        applyBounds = true;
    }
    internal void SetAnchor(float x, float y)
    {
        float scale = (float)Math.Max(0.001, GuiElement.scaled(1));
        float preferredWidth = nextSize is { } size ? size.X / scale : 680;
        float preferredHeight = nextSize is { } current ? current.Y / scale : 640;
        var bounds = AnchoredBounds(x, y, preferredWidth, preferredHeight,
            api.Render.FrameWidth / scale, api.Render.FrameHeight / scale);
        if (form?.AutoHeight != true)
        {
            // Property editors can overlap their anchor. Keep their full height
            // instead of squeezing a long form into half of the screen.
            float screenHeight = api.Render.FrameHeight / scale;
            float height = Math.Max(1, Math.Min(preferredHeight, screenHeight - 44));
            bounds = (bounds.X, Math.Clamp(bounds.Y, 36, Math.Max(36, screenHeight - height - 8)), bounds.Width, height);
        }
        bool above = bounds.Y < y;
        positionPivot = form?.AutoHeight == true && above ? new Vector2(0, 1) : Vector2.Zero;
        nextPosition = new Vector2(bounds.X * scale, (bounds.Y + bounds.Height * positionPivot.Y) * scale);
        nextSize = new Vector2(bounds.Width * scale, bounds.Height * scale);
        anchoredMaximumHeight = above ? bounds.Y + bounds.Height - 36 : api.Render.FrameHeight / scale - bounds.Y - 8;
        // Auto-fit settles over several frames; keep the window beside its anchor while it settles.
        anchorLayoutFrames = 3;
        applyBounds = true;
    }
    internal void SetEnabled(string id, bool enabled)
    {
        form?.SetEnabled(id, enabled);
        Document?.GetElementById(id)?.SetEnabled(enabled);
    }
    internal void SetText(string id, string text)
    {
        form?.SetText(id, text);
        if (Document is not null) Document.GetElementById(id)!.Text = text;
    }
    public void QueueCommit(Action commit) { pendingCommit = commit; }
    private void FlushCommit() { var commit = pendingCommit; pendingCommit = null; commit?.Invoke(); }
    public void Status(string text)
    {
        status = text;
        form?.SetText("menu-status", text);
        if (Document is not null) Document.GetElementById("menu-status")!.Text = text;
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (!Opened) return DrawStatus();
        if (docked) return DrawStatus(); // The workspace owns all child Begin/End calls.
        using var font = DirectorImGuiRuntime.UseFont();

        ImGuiCond condition = docked || applyBounds ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        if (nextPosition is { } position) ImGui.SetNextWindowPos(position, condition, positionPivot);
        if (nextSize is { } size) ImGui.SetNextWindowSize(size, condition);
        applyBounds = false;
        if (form?.AutoHeight == true) DirectorImGuiLayout.AutoHeight(
            Math.Max(1, Math.Min(640, anchoredMaximumHeight ?? 640)), (nextSize?.X ?? 360) / DirectorImGuiTheme.Scale);
        else DirectorImGuiLayout.WindowConstraints(new Vector2(form?.MinimumWidth ?? 220, form?.MinimumHeight ?? 120));

        bool opened = Opened;
        string displayTitle = string.IsNullOrWhiteSpace(title) ? "VS Director" : title;
        bool visible = ImGui.Begin($"{displayTitle}###vsdirector-{id}", ref opened,
            Flags | (form?.AutoHeight == true ? ImGuiWindowFlags.AlwaysAutoResize : ImGuiWindowFlags.None)
                | ImGuiWindowFlags.NoCollapse
                | (form?.MenuBar == true ? ImGuiWindowFlags.MenuBar : ImGuiWindowFlags.None)
                | (form?.HasFooter == true ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None));
        try
        {
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
            if (visible && form is not null && Document is not null)
            {
                if (anchorLayoutFrames > 0 && nextPosition is { } anchor)
                {
                    ImGui.SetWindowPos(anchor - ImGui.GetWindowSize() * positionPivot);
                    anchorLayoutFrames--;
                }
                lastPosition = ImGui.GetWindowPos();
                lastSize = ImGui.GetWindowSize();
                hasBounds = true;
                DirectorImGuiElementBounds windowBounds = Document.GetElementById("window")!.Bounds;
                windowBounds.X = lastPosition.X;
                windowBounds.Y = lastPosition.Y;
                windowBounds.Width = lastSize.X;
                windowBounds.Height = lastSize.Y;
                Document.Root.Bounds.X = lastPosition.X;
                Document.Root.Bounds.Y = lastPosition.Y;
                Document.Root.Bounds.Width = lastSize.X;
                Document.Root.Bounds.Height = lastSize.Y;

                if (!inputEnabled) ImGui.BeginDisabled();
                form.Draw();
                if (!inputEnabled) ImGui.EndDisabled();
                if (status.Length > 0 && !form.HasText("menu-status")) { ImGui.Spacing(); ImGui.TextWrapped(status); }
            }
        }
        finally
        {
            ImGui.End();
        }
        if (!opened) Close();
        return DrawStatus();
    }

    private void DrawDocked()
    {
        if (!Opened || nextPosition is not { } position || nextSize is not { } size) return;
        bool nativeDock = id is "inspector" or "view" or "transform" or "resource-browser";
        bool nativeWindow = nativeDock;
        if (!nativeWindow) ImGui.SetCursorScreenPos(position);
        bool toolbar = id == "workspace-menu";
        float scale = (float)GuiElement.scaled(1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, toolbar ? 2 : 6) * scale);
        ImGuiWindowFlags flags = DockedFlags | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (toolbar) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!inputEnabled) flags |= ImGuiWindowFlags.NoInputs;
        bool visible;
        bool opened = Opened;
        if (nativeDock)
        {
            if (focusNextFrame) { ImGui.SetNextWindowFocus(); focusNextFrame = false; }
            if (id is "view" or "transform" && workspace!.InspectorDockId != 0)
                ImGui.SetNextWindowDockID(workspace.InspectorDockId, ImGuiCond.FirstUseEver);
            DirectorImGuiLayout.WindowSize(new Vector2(320, 500), new Vector2(220, form?.MinimumHeight ?? 140));
            visible = ImGui.Begin($"{title}###vsdirector-{id}", ref opened, ImGuiWindowFlags.NoBringToFrontOnFocus
                | (form?.MenuBar == true ? ImGuiWindowFlags.MenuBar : ImGuiWindowFlags.None)
                | (form?.HasFooter == true ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None)
                | (!inputEnabled ? ImGuiWindowFlags.NoInputs : ImGuiWindowFlags.None));
            if (id == "inspector") workspace!.InspectorDockId = ImGui.GetWindowDockID();
        }
        else visible = ImGui.BeginChild($"vsdirector-{id}", size, false, flags);
        ImGui.PopStyleVar();
        try
        {
            ImGui.SetWindowFontScale(scale);
            if (nativeDock && !visible) FlushCommit();
            if (!visible || form is null || Document is null) return;
            if (nativeDock)
            {
                Vector2 scroll = new(ImGui.GetScrollX(), ImGui.GetScrollY());
                workspace!.ReportBounds(id, ImGui.GetCursorScreenPos() + scroll, ImGui.GetContentRegionAvail() - scroll);
            }
            var draw = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            if (!nativeWindow) draw.AddLine(min, new Vector2(max.X, min.Y), DirectorImGuiTheme.Border);
            lastPosition = ImGui.GetWindowPos();
            lastSize = ImGui.GetWindowSize();
            hasBounds = true;
            foreach (string element in new[] { "window", "root" })
            {
                var bounds = Document.GetElementById(element)!.Bounds;
                bounds.X = lastPosition.X; bounds.Y = lastPosition.Y;
                bounds.Width = lastSize.X; bounds.Height = lastSize.Y;
            }
            if (!inputEnabled) ImGui.BeginDisabled();
            try { form.Draw(); }
            finally { if (!inputEnabled) ImGui.EndDisabled(); }
        }
        finally
        {
            if (nativeWindow) ImGui.End(); else ImGui.EndChild();
            if (!opened) Close();
        }
    }

    // The custom Draw implementation owns Begin/End. The abstract base hook
    // remains a no-op for compatibility with ImGuiDialogBase.
    protected override bool OnDraw() => true;

    public new void Dispose()
    {
        if (disposed) return;
        disposed = true;
        workspace?.Remove(DrawDocked);
        FlushCommit();
        Hosts.Remove(this); DockedHosts.Remove(this);
        DirectorToolWindows.Release(this);
        Document?.Dispose();
        base.Dispose();
    }

    internal static (float X, float Y, float Width, float Height) AnchoredBounds(
        float x, float y, float preferredWidth, float preferredHeight, float screenWidth, float screenHeight)
    {
        const float margin = 8, topMargin = 36, gap = 10;
        y = Math.Clamp(y, topMargin + gap, Math.Max(topMargin + gap, screenHeight - margin - gap));
        float above = y - gap - topMargin, below = screenHeight - margin - y - gap;
        bool upper = above >= preferredHeight || above >= below;
        float height = Math.Max(1, Math.Min(preferredHeight, upper ? above : below));
        float width = Math.Max(1, Math.Min(preferredWidth, screenWidth - margin * 2));
        return (Math.Clamp(x - width / 2, margin, Math.Max(margin, screenWidth - margin - width)), upper ? y - gap - height : y + gap, width, height);
    }
    internal static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    internal static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    internal static string FormatStepValue(double value, double step) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static ImGuiWindowFlags DockedFlags =>
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoSavedSettings;
}

internal sealed class DirectorImGuiDocument
{
    private readonly Dictionary<string, DirectorImGuiElement> elements = new(StringComparer.Ordinal);
    public bool IsDisposed { get; private set; }
    public DirectorImGuiElement? GetElementById(string id) => elements.TryGetValue(id, out var e) ? e : elements[id] = new DirectorImGuiElement();
    public DirectorImGuiElement Root => GetElementById("root")!;
    public DirectorImGuiElement? QuerySelector(string selector) => null;
    public IEnumerable<DirectorImGuiElement> QuerySelectorAll(string selector) => Array.Empty<DirectorImGuiElement>();
    internal void ResetBounds()
    {
        foreach (var element in elements.Values) element.Bounds.Clear();
    }
    internal void Dispose() => IsDisposed = true;
}
internal sealed class DirectorImGuiElement
{
    public bool IsDisposed { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DirectorImGuiElementBounds Bounds { get; } = new();
    public void SetProperty(string name, string value) { }
    public void SetClass(string name, bool enabled) { }
    public void SetEnabled(bool enabled) { }
    public void On(string eventName, Action<object?> callback) { }
    public string GetAttribute(string name) => string.Empty;
    public void SetAttribute(string name, string value) { }
    public void RemoveAttribute(string name) { }
    public IEnumerable<DirectorImGuiElement> QuerySelectorAll(string selector) => Array.Empty<DirectorImGuiElement>();
    public void SetScrollOffset(float x, float y) => Bounds.ScrollY = y;
}
internal sealed class DirectorImGuiElementBounds
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float ScrollY { get; set; }
    public bool Contains(float x, float y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    internal void Clear() { X = Y = Width = Height = ScrollY = 0; }
}

internal sealed class DirectorImGuiForm
{
    internal readonly StringBuilder Body = new(), Tabs = new(), Footer = new(), Overlay = new();
    private readonly List<Action> controls = new();
    private readonly List<Action> footerControls = new();
    private readonly List<(string Label, bool Selected, Action Click, string Icon)> tabs = new();
    private readonly Dictionary<string, Action<bool>> enabledSetters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<string>> textSetters = new(StringComparer.Ordinal);
    private DirectorImGuiDocument? document;
    private Func<bool> controlVisibility = static () => true;
    internal bool Docked { get; set; }
    internal bool AutoHeight { get; set; }
    internal float MinimumWidth { get; set; } = 220;
    internal bool MenuBar { get; set; }
    internal string? Stylesheet { get; set; }
    internal string ClassName { get; set; } = string.Empty;
    internal string SearchText { get; set; } = string.Empty;
    internal bool Sidebar { get; set; }
    internal Action<string>? SearchChanged { get; set; }
    internal Action? BeforeAction { get; set; }
    internal int FieldCount { get; private set; }
    internal bool HasFooter => footerControls.Count > 0;
    internal float MinimumHeight => Math.Max(120, 100 + footerControls.Count * 34);
    internal void AttachDocument(DirectorImGuiDocument document) => this.document = document;
    internal void BindElement(Action<DirectorImGuiDocument> bind) { }
    internal void SetEnabled(string id, bool enabled)
    {
        if (enabledSetters.TryGetValue(id, out var setter)) setter(enabled);
    }
    internal void SetText(string id, string text)
    {
        if (textSetters.TryGetValue(id, out var setter)) setter(text ?? string.Empty);
    }
    internal bool HasText(string id) => textSetters.ContainsKey(id);
    public void Draw()
    {
        using var layout = ClassName == "workspace-menu" ? DirectorImGuiTheme.Toolbar() : new DirectorImGuiTheme.Scope(0, 0);
        if (ClassName == "workspace-menu")
        {
            float height = ImGui.GetFrameHeight();
            ImGui.SetCursorPosY(Math.Max(0, (ImGui.GetWindowHeight() - height) * .5f));
        }
        float tabWidth = tabs.Count == 0 ? 0 : Math.Max(1, (ImGui.GetContentRegionAvail().X
            - ImGui.GetStyle().ItemSpacing.X * (tabs.Count - 1)) / tabs.Count);
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            string label = Caption(tabs[i].Label, tabs[i].Icon) + $"###director-tab-{i}";
            using var tone = DirectorImGuiTheme.Button(tabs[i].Selected ? "primary" : "normal", quiet: true);
            bool clicked = ImGui.Button(label, new Vector2(tabWidth, 0));
            if (tabs[i].Selected)
            {
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, DirectorImGuiTheme.Accent, 2 * DirectorImGuiTheme.Scale);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tabs[i].Label);
            if (clicked) { BeforeAction?.Invoke(); tabs[i].Click(); }
        }
        if (tabs.Count > 0) ImGui.Separator();
        bool visible = true;
        if (HasFooter)
        {
            float footerHeight = footerControls.Count * ImGui.GetFrameHeightWithSpacing()
                + ImGui.GetStyle().ItemSpacing.Y * 2;
            visible = ImGui.BeginChild("form-content", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y - footerHeight)), false);
        }
        try
        {
            if (visible)
                for (int i = 0; i < controls.Count; i++)
                {
                    ImGui.PushID(i);
                    try { controls[i](); }
                    finally { ImGui.PopID(); }
                }
        }
        finally { if (HasFooter) ImGui.EndChild(); }
        if (HasFooter)
        {
            ImGui.Separator();
            ImGui.PushID("footer-actions");
            try
            {
                for (int i = 0; i < footerControls.Count; i++)
                {
                    ImGui.PushID(i);
                    try { footerControls[i](); }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.PopID(); }
        }
    }
    internal void AddControl(Action draw)
    {
        Func<bool> visible = controlVisibility;
        controls.Add(() => { if (visible()) draw(); });
    }
    public void Accordion(string title, bool expanded, Action<bool> changed)
    {
        bool open = expanded;
        controls.Add(() =>
        {
            bool next = DirectorImGuiTheme.CollapsingHeader(title, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (next != open) { open = next; changed(next); }
        });
        controlVisibility = () => open;
    }
    public void EndSection() => controlVisibility = static () => true;

    public void ChoiceList(params (string Id, string Label, Action Click, string Icon)[] choices)
    {
        AutoHeight = true;
        string query = string.Empty;
        AddControl(() =>
        {
            if (choices.Length > 6)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##choice-search", Lang.Get("vsdirector:element-search"), ref query, 128);
                ImGui.Separator();
            }
            int visible = 0;
            foreach (var choice in choices)
            {
                ClearItemBounds(choice.Id);
                if (!choice.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                visible++;
                if (ImGui.Selectable(Caption(choice.Label, choice.Icon) + "###choice-" + choice.Id, false,
                    ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
                {
                    BeforeAction?.Invoke();
                    choice.Click();
                }
                UpdateItemBounds(choice.Id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(choice.Label);
            }
            if (visible == 0) ImGui.TextDisabled(Lang.Get("vsdirector:scene-empty"));
        });
    }

    public void Section(string title, bool visible) { if (visible) Group(title); }
    public void Tab(string label, bool selected, Action click, string keywords = "", string icon = "") => tabs.Add((label, selected, click, icon));
    public void Category(string label, bool selected, bool expanded, Action select, Action<bool> expandedChanged, IEnumerable<(string Label, bool Selected, Action Click)> children) { tabs.Add((label, selected, select, "")); foreach (var c in children) tabs.Add(("  " + c.Label, c.Selected, c.Click, "")); }
    public void Group(string title) => AddControl(() => { ImGui.Spacing(); ImGui.SeparatorText(title); });
    public void Search(string id, string hint, string value, Action<string> changed)
    {
        string text = value;
        AddControl(() =>
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##" + id, hint, ref text, 512)) changed(text);
        });
    }
    public void BrowserMenu(string nameLabel, string initialName, Action<string> nameChanged,
        string recordLabel, Action record, bool canRecord, string stopLabel, Action stop, bool canStop,
        string refreshLabel, Action refresh)
    {
        MenuBar = true;
        string name = initialName;
        AddControl(() =>
        {
            if (!ImGui.BeginMenuBar()) return;
            try
            {
                bool recordingMenu = ImGui.BeginMenu(recordLabel);
                UpdateItemBounds("resource-record-menu");
                if (recordingMenu)
                {
                    ImGui.SetNextItemWidth(Math.Min(240 * DirectorImGuiTheme.Scale, DirectorImGuiLayout.MaximumSize.X - 32 * DirectorImGuiTheme.Scale));
                    if (ImGui.InputTextWithHint("##record-name", nameLabel, ref name, 512)) nameChanged(name);
                    bool start = ImGui.MenuItem(Caption(recordLabel, "record"), "", false, canRecord);
                    UpdateItemBounds("resource-record");
                    if (start) { BeforeAction?.Invoke(); record(); }
                    bool finish = ImGui.MenuItem(Caption(stopLabel, "stop"), "", false, canStop);
                    UpdateItemBounds("resource-stop");
                    if (finish) { BeforeAction?.Invoke(); stop(); }
                    ImGui.EndMenu();
                }
                else { ClearItemBounds("resource-record"); ClearItemBounds("resource-stop"); }
                if (ImGui.MenuItem(Caption(refreshLabel, "refresh"))) { BeforeAction?.Invoke(); refresh(); }
                UpdateItemBounds("resource-refresh");
            }
            finally { ImGui.EndMenuBar(); }
        });
        enabledSetters["resource-record"] = value => canRecord = value;
        enabledSetters["resource-stop"] = value => canStop = value;
    }
    public void Note(string text) => AddNote(null, text);
    public void Note(string id, string text) => AddNote(id, text);
    private void AddNote(string? id, string text)
    {
        string current = text ?? string.Empty;
        if (id is not null) textSetters[id] = value => current = value ?? string.Empty;
        AddControl(() => ImGui.TextWrapped(current));
    }
    public void Detail(string label, string value)
    {
        string displayLabel = label ?? string.Empty;
        string displayValue = string.IsNullOrWhiteSpace(value) ? "-" : value;
        AddField(displayLabel, () => ImGui.TextWrapped(displayValue));
    }
    public void Button(string label, Action click, bool enabled = true, string tone = "normal", string icon = "") => ButtonRow((label, click, enabled, tone, icon));
    public void Button(string id, string label, Action click, bool enabled = true, string tone = "normal", string icon = "")
        => ButtonRow((id, label, click, enabled, tone, icon));
    public void ButtonRow(params (string Label, Action Click, bool Enabled, string Tone, string Icon)[] buttons)
        => AddButtonRow(buttons.Select(button => (string.Empty, button.Label, button.Click, button.Enabled, button.Tone, button.Icon)).ToArray());
    public void ButtonRow(params (string Id, string Label, Action Click, bool Enabled, string Tone, string Icon)[] buttons)
        => AddButtonRow(buttons);
    private void AddButtonRow((string Id, string Label, Action Click, bool Enabled, string Tone, string Icon)[] buttons)
    {
        bool[] enabled = buttons.Select(button => button.Enabled).ToArray();
        string[] labels = buttons.Select(button => button.Label).ToArray();
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            if (!string.IsNullOrEmpty(buttons[i].Id)) enabledSetters[buttons[i].Id] = value => enabled[index] = value;
            if (!string.IsNullOrEmpty(buttons[i].Id)) textSetters[buttons[i].Id] = value => labels[index] = value ?? string.Empty;
        }
        AddControl(() =>
        {
            bool toolbar = ClassName == "workspace-menu";
            float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            // Hidden commands must not retain hover targets from a wider layout.
            foreach (var button in buttons) ClearItemBounds(button.Id);
            if (toolbar) ClearItemBounds("toolbar-overflow");
            for (int i = 0; i < buttons.Length; i++)
            {
                string caption = Caption(labels[i], buttons[i].Icon);
                float width = ImGui.CalcTextSize(caption).X + ImGui.GetStyle().FramePadding.X * 2;
                float reserve = i < buttons.Length - 1 ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0;
                float left = i == 0 ? ImGui.GetCursorScreenPos().X : ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
                if (toolbar && left + width + reserve > right)
                {
                    if (i > 0) ImGui.SameLine();
                    using var overflowTone = DirectorImGuiTheme.Button(quiet: true);
                    if (DirectorImGuiIcons.Button(Icon("chevron-down") + "###overflow", new Vector2(ImGui.GetFrameHeight(), 0)))
                        ImGui.OpenPopup("overflow");
                    UpdateItemBounds("toolbar-overflow");
                    if (ImGui.BeginPopup("overflow"))
                    {
                        try
                        {
                            for (int j = i; j < buttons.Length; j++)
                            {
                                ImGui.PushID(j);
                                bool clicked = ImGui.MenuItem(Caption(labels[j], buttons[j].Icon) + "###command", "", buttons[j].Tone == "primary", enabled[j]);
                                UpdateItemBounds(buttons[j].Id);
                                ImGui.PopID();
                                if (clicked) { BeforeAction?.Invoke(); buttons[j].Click(); }
                            }
                        }
                        finally { ImGui.EndPopup(); }
                    }
                    break;
                }
                if (i > 0 && left + width <= right) ImGui.SameLine();
                ImGui.PushID(i);
                using var tone = DirectorImGuiTheme.Button(buttons[i].Tone, quiet: toolbar);
                ImGui.BeginDisabled(!enabled[i]);
                bool pressed = ImGui.Button(caption + "###button");
                UpdateItemBounds(buttons[i].Id);
                if (pressed) { BeforeAction?.Invoke(); buttons[i].Click(); }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        });
    }
    private void ClearItemBounds(string id)
    {
        if (string.IsNullOrEmpty(id) || document is null) return;
        var bounds = document.GetElementById(id)!.Bounds;
        bounds.Width = bounds.Height = 0;
    }
    private void UpdateItemBounds(string id)
    {
        if (string.IsNullOrEmpty(id) || document is null) return;
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        DirectorImGuiElementBounds bounds = document.GetElementById(id)!.Bounds;
        bounds.X = min.X; bounds.Y = min.Y; bounds.Width = max.X - min.X; bounds.Height = max.Y - min.Y;
    }
    public string FooterButton(string label, Action click, string tone = "normal", string icon = "", bool enabled = true)
    {
        footerControls.Add(() =>
        {
            using var style = DirectorImGuiTheme.Button(tone);
            ImGui.BeginDisabled(!enabled);
            bool pressed = ImGui.Button(Caption(label, icon) + "###footer", new Vector2(-1, 0));
            ImGui.EndDisabled();
            if (pressed) { BeforeAction?.Invoke(); click(); }
        });
        return "footer";
    }

    private void AddField(string label, Action draw)
    {
        AddControl(() =>
        {
            if (!ImGui.BeginTable("field", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.NoPadOuterX)) return;
            try
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed,
                    Math.Clamp(ImGui.GetContentRegionAvail().X * .4f, 60 * DirectorImGuiTheme.Scale, 126 * DirectorImGuiTheme.Scale));
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextWrapped(label);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                draw();
            }
            finally { ImGui.EndTable(); }
        });
    }

    public void Resource(string id, string name, string detail, bool selected, Action select, Action activate, string activateLabel, string icon)
    {
        AddControl(() =>
        {
            ImGui.PushID(id);
            if (!ImGui.BeginTable("resource", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.PopID();
                return;
            }
            try
            {
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(Caption(name, icon) + "###resource-" + id, selected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    BeforeAction?.Invoke();
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) activate(); else select();
                }
                UpdateItemBounds("resource-select-" + id);
                if (ImGui.BeginPopupContextItem("resource-actions"))
                {
                    if (ImGui.MenuItem(activateLabel)) { BeforeAction?.Invoke(); activate(); }
                    ImGui.EndPopup();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(name + (detail.Length > 0 ? "\n" + detail : ""));
                if (detail.Length > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, DirectorImGuiTheme.Muted);
                    ImGui.TextWrapped(detail);
                    ImGui.PopStyleColor();
                }
                ImGui.TableNextColumn();
                using var tone = DirectorImGuiTheme.Button(quiet: true);
                if (DirectorImGuiIcons.Button(Icon("play") + "###activate", new Vector2(ImGui.GetFrameHeight())))
                { BeforeAction?.Invoke(); activate(); }
                UpdateItemBounds("resource-activate-" + id);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(activateLabel);
            }
            finally { ImGui.EndTable(); ImGui.PopID(); }
        });
    }
    public void Text(string label, string value, Action<string> changed, bool password = false)
    {
        FieldCount++;
        // Keep the editing buffer in the control closure. ImGui mutates the
        // buffer in place and a local declared inside the draw callback would
        // reset it on every frame, dropping partially typed text.
        string text = value ?? string.Empty;
        ImGuiInputTextFlags flags = password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None;
        AddField(label, () => { if (ImGui.InputText("##value", ref text, 4096, flags)) changed(text); });
    }
    public void Folder(string label, string value, ICoreClientAPI api, Action<string> changed) => Text(label, value, changed);
    public void Number(string label, double value, Action<double> changed)
        => NumberStep(label, value, 1, changed);
    public void NumberStep(string label, double value, float step, Action<double> changed)
    {
        FieldCount++;
        double number = value;
        AddField(label, () => { if (DirectorImGuiNumeric.Double("##value", ref number, step)) changed(number); });
    }
    public void NumericText(string label, string value, Action<string> changed, float step = 1)
        => NumberStep(label, double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number) ? number : 0, step, v => changed(DirectorNumberFormatting.Format(v)));
    public void Slider(string label, double value, double min, double max, double step, Action<double> changed)
    {
        FieldCount++;
        double number = value;
        AddField(label, () => { if (DirectorImGuiNumeric.Double("##value", ref number, (float)step, min, max,
            $"%.{Math.Clamp((int)Math.Round(Math.Max(0, -Math.Log10(Math.Max(step, 0.000001)))), 0, 6)}f")) changed(number); });
    }
    public void Color(string label, string value, Action<string> changed) => Text(label, value, changed);
    public void Time(string label, TimeSpan value, Action<TimeSpan> changed, bool includeSeconds = false) => Text(label, value.ToString(), text => { if (TimeSpan.TryParse(text, out var parsed)) changed(parsed); });
    public void Check(string label, bool value, Action<bool> changed, bool enabled = true)
    {
        FieldCount++;
        bool state = value;
        AddField(label, () =>
        {
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Checkbox("##value", ref state)) changed(state);
            ImGui.EndDisabled();
        });
    }
    public void Select(string label, string value, IEnumerable<(string Value, string Name)> options, Action<string> changed)
    {
        var choices = options.ToArray();
        string currentValue = value;
        AddField(label, () =>
        {
            string current = choices.FirstOrDefault(c => c.Value == currentValue).Name ?? currentValue;
            if (!ImGui.BeginCombo("##value", current)) return;
            foreach (var c in choices)
            {
                bool selected = c.Value == currentValue;
                if (ImGui.Selectable(c.Name, selected))
                {
                    currentValue = c.Value;
                    changed(c.Value);
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        });
    }
    public void TagSelect(string label, IEnumerable<string> selectedValues, IEnumerable<(string Value, string Name)> options, Action<string, bool> changed) { var selected = new HashSet<string>(selectedValues ?? [], StringComparer.OrdinalIgnoreCase); Select(label, selected.FirstOrDefault() ?? string.Empty, options, value => { bool on = selected.Add(value); if (!on) selected.Remove(value); changed(value, on); }); }
    public void Enum<T>(string label, T value, Action<T> changed, Func<T, string>? display = null) where T : struct, Enum => Select(label, value.ToString(), System.Enum.GetValues<T>().Select(v => (v.ToString(), display?.Invoke(v) ?? v.ToString())), text => { if (System.Enum.TryParse<T>(text, out var parsed)) changed(parsed); });
    internal static string Icon(string name) => DirectorImGuiIcons.Get(name);
    private static string Caption(string label, string icon)
    {
        string glyph = Icon(icon);
        return glyph.Length == 0 ? label : label.Length == 0 ? glyph : glyph + "  " + label;
    }
}





