using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector.Data;


namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private DirectorImGuiWindow? rmlToolbarWindow, rmlResourceWindow;
    private DirectorSceneBrowser? sceneBrowser;
    private DirectorWorkspaceSection sceneCategory = DirectorWorkspaceSection.Cameras;
    private string sceneSearch = "";

    private void EmptyMenuComposer(string key)
    {
        if (Composers[key] is null)
            Composers[key] = capi.Gui.CreateCompo(key, ElementBounds.Fixed(-2, -2, 1, 1)).Compose(false);
    }

    private void ComposeRmlToolbar()
    {
        EmptyMenuComposer(ToolbarKey);
        var commands = new List<DirectorWorkspaceMenus.Command>();
        if (SceneSections().Any()) commands.Add(new("scene", Lang.Get("vsdirector:workspace-scene"), OpenSceneWindow));
        if (IsSectionAvailable(DirectorWorkspaceSection.Replay))
            commands.Add(new("tab-replay", SectionLabel(DirectorWorkspaceSection.Replay), () => OpenRmlResourceMenu(DirectorWorkspaceSection.Replay), Selected: resourceMenuSection == DirectorWorkspaceSection.Replay));
        if (IsSectionAvailable(DirectorWorkspaceSection.Replay))
            commands.Add(new("export", Lang.Get("vsdirector:export"), () => OpenReplayExport(), owner.IsReplayPlaying));
        commands.Add(new("settings", Lang.Get("vsdirector:workspace-settings-button"), () => owner.ToggleSettings()));
        commands.Add(new("navigation", Lang.Get(layout.NavigationMode ? "vsdirector:workspace-edit-mode" : "vsdirector:workspace-navigate-mode"), () => ToggleNavigationMode()));
        commands.Add(new("hud", Lang.Get(owner.Config.HideHudOnCamera ? "vsdirector:workspace-show-hud" : "vsdirector:workspace-hide-hud"), () => ToggleCameraHud()));
        commands.Add(new("reset-layout", Lang.Get("vsdirector:workspace-reset-layout"), imGuiWorkspace.ResetLayout));
        var form = DirectorWorkspaceMenus.Toolbar(commands);
        rmlToolbarWindow ??= new DirectorImGuiWindow(capi, "workspace-menu", docked: true, escape: () => OnEscapePressed(), overlay: true, workspace: imGuiWorkspace);
        rmlToolbarWindow.Render("", form);
        rmlToolbarWindow.SetInputEnabled(!layout.NavigationMode);
        var panel = layout.Panel("toolbar");
        panel.Height = DirectorWorkspacePanelLayout.ToolbarHeight;
        rmlToolbarWindow.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        if (IsOpened()) rmlToolbarWindow.Open();
    }

    private void OpenRmlResourceMenu(DirectorWorkspaceSection target)
    {
        if (DirectorToolWindows.HasOpenTool || !IsSectionAvailable(target) || resourceMenuSection == target) return;
        PreserveInputs();
        resourceMenuSection = target;
        resourceScrollOffset = 0;
        ComposeResources();
        UpdateRmlMenuStatus();
    }

    private void ComposeRmlResources()
    {
        if (sceneBrowser?.IsOpened == true) ComposeSceneWindow();
        EmptyMenuComposer(ResourceMenuKey);
        replayResourceSplitBounds = replayResourceTopBounds = replayResourceViewBounds = null;
        if (resourceMenuSection is not { } target || !IsSectionAvailable(target))
        {
            rmlResourceWindow?.Close();
            return;
        }
        var resources = new List<DirectorWorkspaceMenus.Resource>();
        var views = new List<DirectorWorkspaceMenus.Resource>();
        void Add(Guid id, string name, string detail, string description, IEnumerable<string> tags, Action select, Action activate)
        {
            void Run(Action action)
            {
                PreserveInputs();
                ActivateResourceMenuSection(target);
                action();
                Refresh();
            }
            resources.Add(new(id.ToString(), name, detail, name + " " + description + " " + string.Join(" ", tags),
                () => Run(select), () => Run(activate), selectedId == id));
        }
        switch (target)
        {
            case DirectorWorkspaceSection.Cameras:
                foreach (var camera in owner.Cameras)
                {
                    var (x, y, z) = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
                    Add(camera.Id, camera.Name, $"{x:0.##}, {y:0.##}, {z:0.##}  FOV {camera.Fov:0.#}", camera.Description, camera.Tags,
                        () => SelectCamera(camera.Id), () => ActivateCameraFromWorkspace(camera.Id));
                }
                break;
            case DirectorWorkspaceSection.Paths:
                foreach (var path in owner.Paths)
                    Add(path.Id, path.Name, PathResourceDetails(path), path.Description, path.Tags,
                        () => SelectPath(path.Id), () => PlayPathFromWorkspace(path.Id, reverse: false));
                break;
            case DirectorWorkspaceSection.Follows:
                foreach (var rig in owner.FollowRigs)
                    Add(rig.Id, rig.Name, FollowResourceDetails(rig), rig.Description, rig.Tags,
                        () => SelectFollow(rig.Id), () => ActivateFollowFromWorkspace(rig.Id));
                break;
            case DirectorWorkspaceSection.Replay:
                foreach (var replay in owner.Replays)
                    Add(replay.Id, replay.Name, Lang.Get("vsdirector:replay-row-detail", replay.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                        replay.ActorCount, replay.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)), replay.Description, replay.Tags,
                        () => SelectReplay(replay.Id), () => PlayReplayFromWorkspace(replay.Id));
                if (HasReplayViewEditor)
                    foreach (var view in ReplayViewOptions())
                        views.Add(new(view.Id.ToString(), view.Name, ReplayViewResourceDetails(view.Id), view.Name,
                            () => { SelectReplayViewSource(view.Id, activate: false); ComposeResources(); },
                            () => { SelectReplayViewSource(view.Id, activate: true); ComposeResources(); }, view.Id == replayViewSourceId));
                break;
        }
        bool replayMenu = target == DirectorWorkspaceSection.Replay;
        var form = DirectorWorkspaceMenus.Browser(SectionLabel(target), search, replayMenu ? replayRecordingName : newName, replayMenu,
            Lang.Get("vsdirector:search"), replayMenu ? Lang.Get("vsdirector:replay-name-placeholder") : ResourceCreatePlaceholder(),
            Lang.Get("vsdirector:create"), Lang.Get("vsdirector:resource-record"), Lang.Get("vsdirector:resource-stop"), Lang.Get("vsdirector:refresh"),
            Lang.Get(replayMenu || target == DirectorWorkspaceSection.Paths ? "vsdirector:play" : "vsdirector:activate"),
            resources, views, ReplayViewResourceBlockTitle(), value => { search = value; ComposeResources(); },
            value => { if (replayMenu) replayRecordingName = value; else newName = value; }, () => OnCreateResource(),
            () => { StartReplayRecording(); ComposeResources(); }, () => { StopReplayRecording(); ComposeResources(); },
            () => { RefreshReplayIndex(); ComposeResources(); },
            !owner.IsReplayRecording && !owner.HasPendingReplaySave, owner.IsReplayRecording || owner.HasFailedReplaySave);
        if (rmlResourceWindow is null)
        {
            rmlResourceWindow = new DirectorImGuiWindow(capi, "resource-browser", docked: true, escape: () => OnEscapePressed(), overlay: true, workspace: imGuiWorkspace);
            rmlResourceWindow.Closed += CloseResourceMenu;
        }
        rmlResourceWindow.Render(SectionLabel(target), form);
        var panel = layout.Panel("resources");
        ConfigureResourceMenuPanel(panel);
        rmlResourceWindow.SetDockBounds(panel.X, panel.Y, panel.Width, panel.Height);
        if (IsOpened()) rmlResourceWindow.Open();
    }

    private void UpdateRmlMenuStatus()
    {
        if (rmlToolbarWindow?.Document is { IsDisposed: false })
        {
            rmlToolbarWindow.SetEnabled("export", owner.IsReplayPlaying);
        }
        if (rmlResourceWindow?.Document is { IsDisposed: false })
            DirectorWorkspaceMenus.UpdateRecordingButton(rmlResourceWindow,
                !owner.IsReplayRecording && !owner.HasPendingReplaySave, owner.IsReplayRecording || owner.HasFailedReplaySave);
    }

    private DirectorWorkspaceSection? RmlToolbarSectionAt(int x, int y)
    {
        if (rmlToolbarWindow?.Document is { IsDisposed: false } doc)
            foreach (var target in VisibleToolbarSections())
                if (doc.GetElementById(ToolbarTabKey(target)) is { } element && element.Bounds.Width > 0 && Contains(element.Bounds, x, y)) return target;

        return null;
    }
    private double RmlToolbarAnchor(DirectorWorkspaceSection target)
    {
        if (rmlToolbarWindow?.Document?.GetElementById(ToolbarTabKey(target)) is { } element
            && element.Bounds.Width > 0)
            return element.Bounds.X / Math.Max(.001, GuiElement.scaled(1));

        if (rmlToolbarWindow?.Document?.GetElementById("toolbar-overflow") is { } overflow
            && overflow.Bounds.Width > 0)
            return overflow.Bounds.X / Math.Max(.001, GuiElement.scaled(1));

        // Toolbar commands are rendered as a native ImGui button row.  The
        // replay resource command follows the optional scene command; account
        // for that command and ImGui's item spacing when calculating the menu
        // anchor instead of falling back to the top-left corner.
        double anchor = layout.Panel("toolbar").X;
        if (SceneSections().Any())
            anchor += ToolbarButtonWidth(Lang.Get("vsdirector:workspace-scene")) + 4d;
        return anchor;
    }
    private bool RmlResourceContains(int x, int y)
        => rmlResourceWindow?.IsOpened == true && rmlResourceWindow.ContainsScreenPoint(x, y);
    private static bool Contains(DirectorImGuiElementBounds bounds, int x, int y)
        => x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;

    private IEnumerable<DirectorWorkspaceSection> SceneSections()
        => new[] { DirectorWorkspaceSection.Cameras, DirectorWorkspaceSection.Paths, DirectorWorkspaceSection.Follows }.Where(IsSectionAvailable);

    private void OpenSceneWindow()
    {
        ComposeSceneWindow();
    }

    private void ComposeSceneWindow()
    {
        var available = SceneSections().ToArray();
        if (available.Length == 0) { sceneBrowser?.Close(); return; }
        if (!available.Contains(sceneCategory)) sceneCategory = available[0];
        var categories = new List<DirectorSceneBrowser.Category>();
        foreach (var target in available)
        {
            var items = new List<DirectorSceneBrowser.Resource>();
            void Add(Guid id, string name, string detail, string description, IEnumerable<string> tags, Action select, Action activate)
            {
                void Run(Action action)
                {
                    sceneBrowser?.Close();
                    ActivateResourceMenuSection(target);
                    action();
                    Refresh();
                }
                items.Add(new(id.ToString(), name, detail, name + " " + description + " " + string.Join(" ", tags),
                    () => Run(select), () => Run(activate), selectedId == id));
            }
            if (target == DirectorWorkspaceSection.Cameras)
                foreach (var camera in owner.Cameras)
                {
                    var (x, y, z) = owner.DisplayPosition(camera.X, camera.Y, camera.Z);
                    Add(camera.Id, camera.Name, $"{x:0.##}, {y:0.##}, {z:0.##}  FOV {camera.Fov:0.#}", camera.Description, camera.Tags,
                        () => SelectCamera(camera.Id), () => ActivateCameraFromWorkspace(camera.Id));
                }
            else if (target == DirectorWorkspaceSection.Paths)
                foreach (var path in owner.Paths)
                    Add(path.Id, path.Name, PathResourceDetails(path), path.Description, path.Tags,
                        () => SelectPath(path.Id), () => PlayPathFromWorkspace(path.Id, reverse: false));
            else
                foreach (var rig in owner.FollowRigs)
                    Add(rig.Id, rig.Name, FollowResourceDetails(rig), rig.Description, rig.Tags,
                        () => SelectFollow(rig.Id), () => ActivateFollowFromWorkspace(rig.Id));
            string placeholder = target switch
            {
                DirectorWorkspaceSection.Cameras => "escape-camera-name-placeholder",
                DirectorWorkspaceSection.Paths => "path-name-placeholder",
                _ => "follow-name-placeholder"
            };
            categories.Add(new(target.ToString(), SectionLabel(target), Lang.Get("vsdirector:" + placeholder),
                Lang.Get(target == DirectorWorkspaceSection.Paths ? "vsdirector:play" : "vsdirector:activate"), items));
        }
        sceneBrowser ??= new DirectorSceneBrowser(capi, imGuiWorkspace);
        sceneBrowser.Show(categories, sceneCategory.ToString(), sceneSearch, newName,
            Lang.Get("vsdirector:workspace-scene"),
            Lang.Get("vsdirector:scene-search"), Lang.Get("vsdirector:scene-results"), Lang.Get("vsdirector:scene-empty"), Lang.Get("vsdirector:create"),
            id => sceneCategory = Enum.Parse<DirectorWorkspaceSection>(id), value => sceneSearch = value, value => newName = value,
            () => { ActivateResourceMenuSection(sceneCategory); OnCreateResource(); });
    }
}
