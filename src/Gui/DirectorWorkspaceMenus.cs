namespace VSDirector.Gui;

/// <summary>Native VSImGui composition helpers for the workspace toolbar and resource lists.</summary>
internal static class DirectorWorkspaceMenus
{
    internal sealed record Command(string Id, string Label, Action Run, bool Enabled = true, bool Selected = false);
    internal sealed record Resource(string Id, string Name, string Detail, string SearchText, Action Select, Action Activate, bool Selected = false);

    internal static DirectorImGuiForm Toolbar(IEnumerable<Command> commands)
    {
        var form = new DirectorImGuiForm { ClassName = "workspace-menu" };
        var list = commands.ToArray();
        form.ButtonRow(list.Select(c => (c.Id, c.Label, c.Run, c.Enabled, c.Selected ? "primary" : "normal",
            string.Empty)).ToArray());
        return form;
    }

    internal static DirectorImGuiForm Browser(string title, string query, string name, bool replay,
        string searchLabel, string nameLabel, string createLabel, string recordLabel, string stopLabel, string refreshLabel,
        string activateLabel, IReadOnlyList<Resource> resources, IReadOnlyList<Resource> views, string viewTitle,
        Action<string> searchChanged, Action<string> nameChanged, Action create, Action record, Action stop, Action refresh,
        bool canRecord, bool canStop)
    {
        var form = new DirectorImGuiForm { ClassName = "resource-browser" };
        if (replay)
        {
            form.BrowserMenu(nameLabel, name, nameChanged, recordLabel, record, canRecord, stopLabel, stop, canStop,
                refreshLabel, refresh);
        }
        form.Search("resource-search", searchLabel, query, searchChanged);
        if (!replay)
        {
            form.Text(nameLabel, name, nameChanged);
            form.Button(createLabel, create, tone: "primary", icon: "plus");
        }
        if (views.Count > 0) form.Accordion(title, true, _ => { });
        // The native form is rebuilt when the search value changes. Filter both
        // resource groups before adding rows so the ImGui list has the same
        // search semantics as the former browser document.
        string normalizedQuery = query.Trim();
        IEnumerable<Resource> Matching(IEnumerable<Resource> source)
            => string.IsNullOrWhiteSpace(normalizedQuery)
                ? source
                : source.Where(resource => resource.SearchText.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        var matches = Matching(resources).ToArray();
        if (matches.Length == 0) form.Note(Vintagestory.API.Config.Lang.Get("vsdirector:scene-empty"));
        foreach (var resource in matches)
            form.Resource(resource.Id, resource.Name, resource.Detail, resource.Selected, resource.Select, resource.Activate, activateLabel, replay ? "video" : "camera");
        if (views.Count > 0)
        {
            form.Accordion(viewTitle, false, _ => { });
            foreach (var resource in Matching(views))
                form.Resource(resource.Id, resource.Name, resource.Detail, resource.Selected, resource.Select, resource.Activate, activateLabel, "camera");
        }
        form.EndSection();
        return form;
    }

    internal static void UpdateRecordingButton(DirectorImGuiWindow window, bool canRecord, bool canStop)
    {
        window.SetEnabled("resource-record", canRecord);
        window.SetEnabled("resource-stop", canStop);
    }
}
