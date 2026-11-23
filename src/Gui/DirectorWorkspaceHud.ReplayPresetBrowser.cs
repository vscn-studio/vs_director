using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

namespace VSDirector.Gui;

/// <summary>
/// The replay "add element" menu: a two-pane browser over element tracks and the preset
/// library with search, favourites, recently used and most used lists (saved in
/// ModData/VSDirector/replay-preset-usage.json) and full keyboard navigation.
/// </summary>
internal sealed partial class DirectorWorkspaceHud
{
    private const string BrowseElements = "elements";
    private const string BrowseFavorites = "favorites";
    private const string BrowseRecent = "recent";
    private const string BrowseMostUsed = "most-used";
    private const string BrowseAll = "all";
    private const string ElementUsagePrefix = "element:";

    private sealed record PresetBrowserEntry(
        string Id,
        string Label,
        string Icon,
        string Description,
        string Group,
        string GroupLabel,
        Func<bool> Apply,
        bool NeedsPlayer);

    private DirectorReplayPresetUsage? presetUsageStore;
    private string presetBrowserCategory = string.Empty;
    private string presetBrowserQuery = string.Empty;
    private int presetBrowserHighlight;
    private bool presetBrowserScroll;

    private DirectorReplayPresetUsage presetUsage => presetUsageStore ??= DirectorReplayPresetUsage.Load(
        Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "replay-preset-usage.json"));

    private void RmlReplayPresetBrowser(DirectorImGuiForm form)
    {
        if (presetBrowserCategory.Length == 0)
        {
            presetBrowserCategory = presetUsage.LastCategory.Length > 0 ? presetUsage.LastCategory : BrowseAll;
        }
        IReadOnlyList<PresetBrowserEntry> entries = PresetBrowserEntries();
        form.AddControl(() => DrawPresetBrowser(entries));
    }

    private IReadOnlyList<PresetBrowserEntry> PresetBrowserEntries()
    {
        var entries = new List<PresetBrowserEntry>();
        string elements = Lang.Get("vsdirector:replay-element-group-elements");
        foreach (ReplayViewportSourceOption option in ReplayViewportSourceOptions())
        {
            string code = option.Value;
            string helpKey = "vsdirector:replay-element-help-" + code["element-".Length..];
            entries.Add(new PresetBrowserEntry(ElementUsagePrefix + code, option.Name, "plus",
                Lang.Get(helpKey), BrowseElements, elements,
                () =>
                {
                    presetUsage.RecordUse(ElementUsagePrefix + code);
                    return AddReplayElementTrack(code);
                }, false));
        }
        foreach (ReplayPreset preset in ReplayPresets())
        {
            ReplayPreset chosen = preset;
            entries.Add(new PresetBrowserEntry(preset.Id, PresetName(preset.Id),
                PresetGroupIcon(preset.Group), PresetHelp(preset.Id), preset.Group,
                PresetGroupName(preset.Group), () => ApplyReplayPreset(chosen), preset.Needs.HasFlag(PresetNeeds.Player)));
        }
        return entries;
    }

    private static readonly string[] FixedBrowseCategories = { BrowseElements, BrowseFavorites, BrowseRecent, BrowseMostUsed, BrowseAll };

    private static IEnumerable<string> BrowseCategories() => FixedBrowseCategories.Concat(PresetGroupOrder);

    private static string BrowseCategoryIcon(string category) => category switch
    {
        BrowseElements => "plus",
        BrowseFavorites => "star-filled",
        BrowseRecent => "history",
        BrowseMostUsed => "flame",
        BrowseAll => "layout-grid",
        _ => PresetGroupIcon(category)
    };

    private static string BrowseCategoryName(string category) => category switch
    {
        BrowseElements => Lang.Get("vsdirector:replay-element-group-elements"),
        BrowseFavorites or BrowseRecent or BrowseMostUsed or BrowseAll => Lang.Get("vsdirector:replay-browser-" + category),
        _ => PresetGroupName(category)
    };

    private List<PresetBrowserEntry> BrowseCategoryEntries(IReadOnlyList<PresetBrowserEntry> entries, string category)
    {
        Dictionary<string, PresetBrowserEntry> byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        IEnumerable<PresetBrowserEntry> Lookup(IEnumerable<string> ids)
            => ids.Select(id => byId.TryGetValue(id, out PresetBrowserEntry? entry) ? entry : null)
                .Where(entry => entry is not null)!;
        return category switch
        {
            BrowseFavorites => Lookup(presetUsage.Favorites).ToList(),
            BrowseRecent => Lookup(presetUsage.Recent).ToList(),
            BrowseMostUsed => Lookup(presetUsage.MostUsed()).Take(30).ToList(),
            BrowseAll => entries.Where(entry => entry.Group != BrowseElements).ToList(),
            _ => entries.Where(entry => entry.Group == category).ToList()
        };
    }

    /// <summary>Search over everything: name matches first, then group and description.</summary>
    private static List<PresetBrowserEntry> SearchPresetEntries(IReadOnlyList<PresetBrowserEntry> entries, string query)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Score(PresetBrowserEntry entry)
        {
            int score = 0;
            foreach (string word in words)
            {
                if (entry.Label.StartsWith(word, StringComparison.OrdinalIgnoreCase)) score += 6;
                else if (entry.Label.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 4;
                else if (entry.GroupLabel.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 2;
                else if (entry.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 1;
                else return 0;
            }
            return score;
        }
        return entries.Select(entry => (Entry: entry, Score: Score(entry)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Entry)
            .ToList();
    }

    private void SetPresetBrowserCategory(string category)
    {
        presetBrowserCategory = category;
        presetBrowserHighlight = 0;
        presetBrowserScroll = true;
        presetUsage.SetLastCategory(category);
    }

    private void DrawPresetBrowser(IReadOnlyList<PresetBrowserEntry> entries)
    {
        float scale = DirectorImGuiTheme.Scale;
        ImGuiIOPtr io = ImGui.GetIO();
        bool appearing = ImGui.IsWindowAppearing();
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        string[] categories = BrowseCategories().ToArray();
        int categoryIndex = Math.Max(0, Array.IndexOf(categories, presetBrowserCategory));

        // Search box, focused when the menu opens.
        if (appearing)
        {
            ImGui.SetKeyboardFocusHere();
            presetBrowserHighlight = 0;
            presetBrowserScroll = true;
        }
        ImGui.SetNextItemWidth(-1);
        string query = presetBrowserQuery;
        bool enter = ImGui.InputTextWithHint("##preset-search", Lang.Get("vsdirector:replay-browser-search"), ref query, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        bool searchActive = ImGui.IsItemActive();
        if (query != presetBrowserQuery)
        {
            presetBrowserQuery = query;
            presetBrowserHighlight = 0;
            presetBrowserScroll = true;
        }
        bool searching = presetBrowserQuery.Trim().Length > 0;
        List<PresetBrowserEntry> shown = searching
            ? SearchPresetEntries(entries, presetBrowserQuery.Trim())
            : BrowseCategoryEntries(entries, presetBrowserCategory);

        // Keyboard navigation.
        if (focused)
        {
            int move = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) move = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) move = -1;
            if (ImGui.IsKeyPressed(ImGuiKey.PageDown)) move = 8;
            if (ImGui.IsKeyPressed(ImGuiKey.PageUp)) move = -8;
            if (!searchActive && ImGui.IsKeyPressed(ImGuiKey.Home)) move = -shown.Count;
            if (!searchActive && ImGui.IsKeyPressed(ImGuiKey.End)) move = shown.Count;
            if (move != 0 && shown.Count > 0)
            {
                presetBrowserHighlight = Math.Clamp(presetBrowserHighlight + move, 0, shown.Count - 1);
                presetBrowserScroll = true;
            }
            bool categoryKeys = !searching || io.KeyCtrl;
            if (categoryKeys && ImGui.IsKeyPressed(ImGuiKey.RightArrow) && (!searchActive || io.KeyCtrl || presetBrowserQuery.Length == 0))
            {
                presetBrowserQuery = string.Empty;
                SetPresetBrowserCategory(categories[(categoryIndex + 1) % categories.Length]);
                return;
            }
            if (categoryKeys && ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && (!searchActive || io.KeyCtrl || presetBrowserQuery.Length == 0))
            {
                presetBrowserQuery = string.Empty;
                SetPresetBrowserCategory(categories[(categoryIndex + categories.Length - 1) % categories.Length]);
                return;
            }
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && presetBrowserHighlight < shown.Count)
            {
                presetUsage.ToggleFavorite(shown[presetBrowserHighlight].Id);
            }
            if (!enter && !searchActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
            {
                enter = true;
            }
        }
        presetBrowserHighlight = shown.Count == 0 ? 0 : Math.Clamp(presetBrowserHighlight, 0, shown.Count - 1);
        if (enter && shown.Count > 0)
        {
            shown[presetBrowserHighlight].Apply();
            return;
        }

        float descriptionHeight = 92f * scale;
        float paneHeight = Math.Max(160f * scale, 420f * scale);

        // Categories.
        ImGui.BeginChild("preset-categories", new Vector2(170f * scale, paneHeight), true);
        foreach (string category in categories)
        {
            int count = BrowseCategoryEntries(entries, category).Count;
            bool selected = !searching && category == presetBrowserCategory;
            string label = DirectorImGuiForm.Icon(BrowseCategoryIcon(category)) + "  " + BrowseCategoryName(category)
                + (count > 0 && category is not BrowseAll ? "  (" + count + ")" : string.Empty)
                + "###category-" + category;
            if (category == BrowseAll || category == PresetGroupOrder[0])
            {
                ImGui.Separator();
            }
            if (ImGui.Selectable(label, selected))
            {
                presetBrowserQuery = string.Empty;
                SetPresetBrowserCategory(category);
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // Entries.
        PresetBrowserEntry? hovered = null;
        ImGui.BeginChild("preset-entries", new Vector2(0, paneHeight), true);
        if (shown.Count == 0)
        {
            ImGui.TextWrapped(Lang.Get(searching ? "vsdirector:scene-empty"
                : presetBrowserCategory switch
                {
                    BrowseFavorites => "vsdirector:replay-browser-favorites-empty",
                    BrowseRecent or BrowseMostUsed => "vsdirector:replay-browser-recent-empty",
                    _ => "vsdirector:scene-empty"
                }));
        }
        bool showGroup = searching || presetBrowserCategory is BrowseFavorites or BrowseRecent or BrowseMostUsed or BrowseAll;
        for (int index = 0; index < shown.Count; index++)
        {
            PresetBrowserEntry entry = shown[index];
            ImGui.PushID(entry.Id);
            bool favorite = presetUsage.IsFavorite(entry.Id);
            ImGui.PushStyleColor(ImGuiCol.Button, 0u);
            ImGui.PushStyleColor(ImGuiCol.Text, favorite ? 0xFF33C8FFu : ImGui.GetColorU32(ImGuiCol.TextDisabled));
            if (ImGui.SmallButton(DirectorImGuiForm.Icon(favorite ? "star-filled" : "star") + "##fav"))
            {
                presetUsage.ToggleFavorite(entry.Id);
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Lang.Get(favorite ? "vsdirector:replay-browser-unfavorite" : "vsdirector:replay-browser-favorite"));
            }
            ImGui.SameLine();
            string tag = presetBrowserCategory == BrowseMostUsed && !searching
                ? "×" + presetUsage.Count(entry.Id)
                : showGroup ? entry.GroupLabel : string.Empty;
            float tagWidth = tag.Length > 0 ? ImGui.CalcTextSize(tag).X + 8f * scale : 0f;
            float rowWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - tagWidth);
            bool highlighted = index == presetBrowserHighlight;
            if (ImGui.Selectable(DirectorImGuiForm.Icon(entry.Icon) + "  " + entry.Label + "##row", highlighted,
                    ImGuiSelectableFlags.None, new Vector2(rowWidth, 0)))
            {
                ImGui.PopID();
                ImGui.EndChild();
                entry.Apply();
                return;
            }
            if (ImGui.IsItemHovered())
            {
                hovered = entry;
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                presetUsage.ToggleFavorite(entry.Id);
            }
            if (highlighted && presetBrowserScroll)
            {
                ImGui.SetScrollHereY(0.5f);
                presetBrowserScroll = false;
            }
            if (tag.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(tag);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();

        // Description of the hovered (or highlighted) entry.
        PresetBrowserEntry? described = hovered ?? (shown.Count > 0 ? shown[presetBrowserHighlight] : null);
        ImGui.BeginChild("preset-description", new Vector2(0, descriptionHeight), true);
        if (described is not null)
        {
            ImGui.TextUnformatted(DirectorImGuiForm.Icon(described.Icon) + "  " + described.Label);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.TextWrapped(described.Description);
            if (described.NeedsPlayer)
            {
                ImGui.TextWrapped(Lang.Get("vsdirector:replay-browser-needs-player"));
            }
            ImGui.PopStyleColor();
        }
        ImGui.EndChild();
        ImGui.TextDisabled(Lang.Get("vsdirector:replay-browser-keys"));
    }
}
