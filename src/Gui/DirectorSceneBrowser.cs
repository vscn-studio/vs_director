using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Native ImGui scene browser owned by the workspace.</summary>
internal sealed class DirectorSceneBrowser : IDisposable
{
    private readonly ICoreClientAPI api;
    private Window? window;

    internal DirectorSceneBrowser(ICoreClientAPI api) => this.api = api;

    internal sealed record Category(
        string Id,
        string Label,
        string Placeholder,
        string ActivateLabel,
        IReadOnlyList<Resource> Resources);

    internal sealed record Resource(
        string Id,
        string Name,
        string Detail,
        string SearchText,
        Action Select,
        Action Activate,
        bool Selected = false);

    internal bool IsOpened => window?.IsVisible == true;

    internal void Show(IReadOnlyList<Category> categories, string selected, string query, string name, string title,
        string searchLabel, string resultsLabel, string emptyLabel, string createLabel,
        Action<string> categoryChanged, Action<string> searchChanged, Action<string> nameChanged, Action create)
    {
        window?.Dispose();
        window = new Window(api, categories, selected, query, name, title, searchLabel, resultsLabel, emptyLabel,
            createLabel, categoryChanged, searchChanged, nameChanged, create);
        window.Open();
    }

    internal void Close() => window?.Close();

    public void Dispose()
    {
        window?.Dispose();
        window = null;
    }

    private sealed class Window : DirectorImGuiDialog
    {
        private readonly IReadOnlyList<Category> categories;
        private readonly string searchLabel;
        private readonly string resultsLabel;
        private readonly string emptyLabel;
        private readonly string createLabel;
        private readonly Action<string> categoryChanged;
        private readonly Action<string> searchChanged;
        private readonly Action<string> nameChanged;
        private readonly Action create;
        private string selected;
        private string query;
        private string name;
        private bool visible;

        internal Window(ICoreClientAPI api, IReadOnlyList<Category> categories, string selected, string query,
            string name, string title, string searchLabel, string resultsLabel, string emptyLabel, string createLabel,
            Action<string> categoryChanged, Action<string> searchChanged, Action<string> nameChanged, Action create)
            : base(api, title, "vsdirector-scene-browser", false)
        {
            this.categories = categories;
            this.selected = categories.Any(c => c.Id == selected)
                ? selected
                : categories.FirstOrDefault()?.Id ?? "";
            this.query = query;
            this.name = name;
            this.searchLabel = searchLabel;
            this.resultsLabel = resultsLabel;
            this.emptyLabel = emptyLabel;
            this.createLabel = createLabel;
            this.categoryChanged = categoryChanged;
            this.searchChanged = searchChanged;
            this.nameChanged = nameChanged;
            this.create = create;
        }

        internal bool IsVisible => visible;

        protected override bool OnOpen()
        {
            visible = true;
            return true;
        }

        protected override bool OnClose()
        {
            visible = false;
            return true;
        }

        protected override CallbackGUIStatus Draw(float deltaSeconds)
        {
            if (!Opened) return DrawStatus();
            using var font = DirectorImGuiRuntime.UseFont();

            DirectorImGuiLayout.WindowSize(new Vector2(440, 300), new Vector2(280, 120));
            DirectorImGuiLayout.AutoHeight(600, 440);
            bool opened = Opened;
            bool drawContents = ImGui.Begin($"{WindowTitle}###vsdirector-scene-browser", ref opened, Flags | ImGuiWindowFlags.AlwaysAutoResize);
            try
            {
                ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
                if (drawContents) OnDraw();
            }
            finally
            {
                ImGui.End();
            }
            if (!opened) Close();
            return DrawStatus();
        }

        protected override bool OnDraw()
        {
            string input = query;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##scene-search", searchLabel, ref input, 512))
            {
                query = input;
                searchChanged(input);
            }

            bool globalSearch = !string.IsNullOrWhiteSpace(query);
            ImGui.Separator();
            foreach (var category in categories)
            {
                var matches = category.Resources.Where(r => !globalSearch
                    || r.SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (globalSearch && matches.Length == 0) continue;
                ImGui.PushID(category.Id);
                if (globalSearch) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                if (DirectorImGuiTheme.CollapsingHeader($"{category.Label} ({matches.Length})###category",
                    category.Id == selected ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                {
                    if (!globalSearch)
                    {
                        float buttonWidth = ImGui.CalcTextSize(createLabel).X + ImGui.GetStyle().FramePadding.X * 2;
                        ImGui.SetNextItemWidth(MathF.Max(1, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
                        if (ImGui.InputTextWithHint("##scene-name", category.Placeholder, ref name, 512)) nameChanged(name);
                        ImGui.SameLine();
                        if (ImGui.Button(createLabel + "##scene-create"))
                        {
                            selected = category.Id;
                            nameChanged(name);
                            categoryChanged(selected);
                            create();
                        }
                    }
                    foreach (var resource in matches) DrawResource(category, resource);
                    if (matches.Length == 0) ImGui.TextDisabled(emptyLabel);
                    ImGui.Spacing();
                }
                ImGui.PopID();
            }
            if (globalSearch && !categories.SelectMany(c => c.Resources).Any(r => r.SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)))
                ImGui.TextDisabled(emptyLabel);
            return true;
        }

        private void DrawResource(Category category, Resource resource)
        {
            ImGui.PushID(resource.Id);
            bool selectedResource = resource.Selected;
            float actionWidth = MathF.Max(70, ImGui.CalcTextSize(category.ActivateLabel).X + 18);
            float nameWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X - actionWidth);
            if (ImGui.Selectable(resource.Name, selectedResource, ImGuiSelectableFlags.AllowDoubleClick,
                    new Vector2(nameWidth, 0)))
            {
                categoryChanged(category.Id);
                resource.Select();
            }
            if (ImGui.BeginPopupContextItem("resource-actions"))
            {
                if (ImGui.MenuItem(category.ActivateLabel)) { categoryChanged(category.Id); resource.Activate(); }
                ImGui.EndPopup();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(resource.Name);
                if (!string.IsNullOrWhiteSpace(resource.Detail)) ImGui.TextDisabled(resource.Detail);
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"{category.ActivateLabel}##activate"))
            {
                categoryChanged(category.Id);
                resource.Activate();
            }
            if (!string.IsNullOrWhiteSpace(resource.Detail))
            {
                ImGui.TextWrapped(resource.Detail);
            }
            ImGui.PopID();
        }
    }
}
