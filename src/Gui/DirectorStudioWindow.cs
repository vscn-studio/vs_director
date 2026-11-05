using System.Globalization;
using System.Text.Json;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Rendering;
using VSDirector.Runtime;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>
/// Native VSImGui implementation of the Studio workbench.
///
/// The scene renderer remains a world renderer, so the game viewport and the
/// workbench side panels keep the same rendering path while all interaction is
/// handled by the native ImGui window.
/// </summary>
internal sealed partial class DirectorStudioWindow : DirectorImGuiDialog
{
    private sealed class ProjectTab
    {
        public string? Path;
        public DirectorStudioProject? Project;
        public bool Dirty;
    }

    private enum ManagerOperation { None, Import, Export, Create, Delete }

    private readonly ICoreClientAPI capi;
    private readonly VSDirectorModSystem? owner;
    private readonly DirectorStudioResourceProvider resources;
    private readonly DirectorStudioSceneRenderer sceneRenderer;
    private readonly string studioDirectory;
    private readonly string projectDirectory;
    private readonly Action returnToDirector;
    private readonly List<ProjectTab> tabs = [new()];
    private readonly long tickListener;
    private static DirectorStudioWindow? activeStudio;
    private int activeTab;
    private bool showingProjectManager = true;
    private bool resourcePanelOpen;
    private bool navigationMode;
    private bool disposed;
    private bool modeShortcutHeld;
    private int modeShortcutKey;
    private Guid selectedObjectId;
    private Guid selectedActionId;
    private DirectorStudioResourceLink? selectedResource;
    private DirectorStudioAssetKind resourceFilter = DirectorStudioAssetKind.Model;
    private string resourceSearch = string.Empty;
    private string status = string.Empty;
    private ManagerOperation managerOperation;
    private string? selectedProjectPath;
    private string? operationProjectPath;
    private string managerValue = string.Empty;
    private double playhead;
    private bool playing;
    private double timelineInput;
    private string newProjectName = "Untitled Studio";

    private ProjectTab Active => tabs[activeTab];
    private DirectorStudioProject? Project => Active.Project;
    private bool ShowingProjectManager => showingProjectManager || Active.Project is null;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal DirectorStudioWindow(ICoreClientAPI capi, Action returnToDirector, VSDirectorModSystem? owner = null)
        : base(capi, ImGuiWindowFlags.None, Lang.Get("vsdirector:studio-title"), "vsdirector-studio", false)
    {
        this.capi = capi;
        this.owner = owner;
        this.returnToDirector = returnToDirector;
        studioDirectory = Path.Combine(capi.GetOrCreateDataPath("ModData"), "VSDirector", "Studio");
        projectDirectory = Path.Combine(studioDirectory, "projects");
        Directory.CreateDirectory(projectDirectory);
        resources = new DirectorStudioResourceProvider(studioDirectory);
        sceneRenderer = new DirectorStudioSceneRenderer(capi, resources, id => owner?.Models.FirstOrDefault(m => m.Id == id));
        tickListener = capi.Event.RegisterGameTickListener((float dt) =>
        {
            if (disposed || !Opened) return;
            if (playing && Project is { } project)
            {
                playhead = Math.Min(project.Duration, playhead + Math.Max(0, dt));
                if (playhead >= project.Duration) playing = false;
                sceneRenderer.Time = playhead;
            }
        }, 16);
        capi.Event.FileDrop += OnFileDrop;
        capi.Event.KeyDown += OnKeyDown;
        capi.Event.KeyUp += OnKeyUp;
    }

    internal new void Open()
    {
        if (disposed) return;
        owner?.EnsureVanillaActionsAvailable();
        showingProjectManager = Active.Project is null;
        activeStudio = this;
        base.Open();
        if (Project is { } project)
        {
            sceneRenderer.SetProject(project);
            sceneRenderer.DrawInWorld = true;
        }
    }

    internal void OpenProjectManager()
    {
        showingProjectManager = true;
        managerOperation = ManagerOperation.None;
        selectedProjectPath = null;
        navigationMode = false;
        Open();
    }

    private static string L(string key) => Lang.Get("vsdirector:studio-" + key);
    private static string ResourceName(DirectorStudioResourceLink link) => Path.GetFileNameWithoutExtension(link.Path);

    protected override void PrepareWindow()
    {
        // Child regions use the available height; auto-sizing a new window
        // leaves no room for the manager list or the workbench panels.
        var available = ImGui.GetMainViewport().WorkSize;
        ImGui.SetNextWindowSize(System.Numerics.Vector2.Min(available,
            new System.Numerics.Vector2(1280, 800) * (float)GuiElement.scaled(1)), ImGuiCond.Appearing);
    }

    protected override bool OnDraw()
    {
        if (disposed) return false;
        if (ShowingProjectManager) DrawProjectManager();
        else DrawWorkbench(Project!);
        return true;
    }

    private void DrawProjectManager()
    {
        sceneRenderer.SetProject(null);
        ImGui.TextUnformatted(L("manager-title"));
        ImGui.Separator();
        ImGui.BeginChild("studio-manager-list", new System.Numerics.Vector2(0, -96), true);
        foreach (string path in LocalProjectPaths())
        {
            bool selected = string.Equals(selectedProjectPath, path, PathComparison);
            string label = ProjectDisplayName(path);
            if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                selectedProjectPath = path;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) OpenProject(path);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        }
        if (!LocalProjectPaths().Any()) ImGui.TextDisabled(L("projects-empty"));
        ImGui.EndChild();
        if (!string.IsNullOrWhiteSpace(status)) ImGui.TextWrapped(status);
        bool hasSelection = selectedProjectPath is not null;
        if (ImGui.Button(L("import-project"))) BeginManagerOperation(ManagerOperation.Import);
        ImGui.SameLine();
        if (ImGui.Button(L("new"))) BeginManagerOperation(ManagerOperation.Create);
        ImGui.SameLine();
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button(L("export-project"))) BeginManagerOperation(ManagerOperation.Export);
        ImGui.SameLine();
        if (ImGui.Button(L("duplicate-project"))) CopyManagerProject();
        ImGui.SameLine();
        if (ImGui.Button(L("delete-project"))) BeginManagerOperation(ManagerOperation.Delete);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(L("switch"))) SwitchToDirector();
        DrawManagerOperation();
    }

    private void DrawManagerOperation()
    {
        if (managerOperation == ManagerOperation.None) return;
        ImGui.Separator();
        string title = managerOperation switch
        {
            ManagerOperation.Import => L("import-project"),
            ManagerOperation.Export => L("export-project"),
            ManagerOperation.Create => L("new"),
            _ => L("delete-project")
        };
        ImGui.TextUnformatted(title);
        if (managerOperation == ManagerOperation.Delete)
            ImGui.TextWrapped($"{L("delete-project-confirm")} {ProjectDisplayName(operationProjectPath ?? string.Empty)}");
        else ImGui.InputText("##studio-manager-value", ref managerValue, 1024);
        if (ImGui.Button(L("cancel"))) { managerOperation = ManagerOperation.None; managerValue = string.Empty; return; }
        ImGui.SameLine();
        if (ImGui.Button(title))
        {
            string value = managerValue.Trim();
            switch (managerOperation)
            {
                case ManagerOperation.Create: NewProject(value); break;
                case ManagerOperation.Import: ImportManagerProject(value); break;
                case ManagerOperation.Export: ExportManagerProject(value); break;
                case ManagerOperation.Delete: DeleteManagerProject(); break;
            }
            managerValue = string.Empty;
        }
    }

    private void DrawWorkbench(DirectorStudioProject project)
    {
        sceneRenderer.SetProject(project);
        sceneRenderer.Time = playhead;
        DrawTopbar();
        if (!string.IsNullOrWhiteSpace(status)) ImGui.TextWrapped(status);

        float panel = Math.Clamp(ImGui.GetContentRegionAvail().X * .22f, 190, 300);
        if (resourcePanelOpen)
        {
            DrawResourcePanel(project, panel);
            ImGui.SameLine();
        }
        ImGui.BeginChild("studio-stage", new System.Numerics.Vector2(-panel - 8, -190), true);
        ImGui.TextUnformatted(L("title"));
        ImGui.Separator();
        ImGui.TextWrapped(navigationMode ? L("navigation-mode") : L("viewport-preview"));
        ImGui.TextDisabled(L("viewport-world-render"));
        ImGui.Spacing();
        DrawSceneObjects(project);
        ImGui.EndChild();
        ImGui.SameLine();
        DrawPropertiesPanel(project, panel);
        DrawTimeline(project);
    }

    private void DrawTopbar()
    {
        if (ImGui.Button(L("switch"))) SwitchToDirector();
        ImGui.SameLine();
        if (ImGui.Button(L("resources"))) resourcePanelOpen = !resourcePanelOpen;
        ImGui.SameLine();
        if (ImGui.Button(L("save"))) SaveActive();
        ImGui.SameLine();
        if (ImGui.Button(L("project"))) OpenProjectManager();
        ImGui.SameLine();
        if (ImGui.Button("+") && Project is not null) { tabs.Add(new()); activeTab = tabs.Count - 1; OpenProjectManager(); }
        ImGui.Separator();
        if (ImGui.BeginTabBar("studio-projects"))
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                string label = (tabs[i].Project?.Name ?? L("empty-tab")) + (tabs[i].Dirty ? " *" : "") + "###project-" + i;
                if (ImGui.BeginTabItem(label))
                {
                    activeTab = i;
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawSceneObjects(DirectorStudioProject project)
    {
        ImGui.TextUnformatted($"{L("scene")} ({project.SceneObjects.Count})");
        foreach (DirectorStudioSceneObject item in project.SceneObjects)
        {
            bool selected = item.Id == selectedObjectId;
            if (ImGui.Selectable(item.Name, selected))
            {
                selectedObjectId = item.Id;
                selectedResource = null;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(item.Visible ? "◉" : "○")) { item.Visible = !item.Visible; Active.Dirty = true; }
        }
        if (project.SceneObjects.Count == 0) ImGui.TextDisabled(L("timeline-placeholder"));
    }

    private void DrawResourcePanel(DirectorStudioProject project, float width)
    {
        ImGui.BeginChild("studio-resources", new System.Numerics.Vector2(width, -190), true);
        ImGui.TextUnformatted(L("resources"));
        ImGui.InputTextWithHint("##studio-resource-search", L("resource-search"), ref resourceSearch, 256);
        foreach (DirectorStudioAssetKind kind in Enum.GetValues<DirectorStudioAssetKind>())
        {
            if (ImGui.RadioButton(L(kind.ToString().ToLowerInvariant()), resourceFilter == kind)) resourceFilter = kind;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        foreach (DirectorStudioResource resource in resources.List().Where(r => r.Kind == resourceFilter))
        {
            string name = ResourceName(resource.Link);
            if (resourceSearch.Length > 0 && !name.Contains(resourceSearch, StringComparison.OrdinalIgnoreCase)) continue;
            if (ImGui.Selectable(name, selectedResource == resource.Link)) selectedResource = resource.Link;
            ImGui.SameLine();
            if (resource.Kind == DirectorStudioAssetKind.Model && ImGui.SmallButton("+##" + resource.Link)) AddSceneObject(resource.Link);
        }
        foreach (DirectorStudioAsset asset in project.Assets.Where(a => a.Kind == resourceFilter))
            if (ImGui.Selectable(asset.Name, selectedResource is null && selectedObjectId == Guid.Empty)) AddLegacySceneObject(asset.Id);
        if (resourceFilter == DirectorStudioAssetKind.Animation && owner is not null)
        {
            foreach (DirectorActionAsset action in owner.Actions)
            {
                ImGui.Selectable(action.Name, selectedActionId == action.Id);
                if (ImGui.IsItemClicked()) selectedActionId = action.Id;
                ImGui.SameLine();
                if (ImGui.SmallButton("▶##action-" + action.Id.ToString("N"))) PreviewLibraryAction(action);
            }
        }
        if (resourceFilter == DirectorStudioAssetKind.Model && owner is not null)
        {
            foreach (DirectorModelAsset model in owner.Models)
            {
                ImGui.Selectable(model.Name, false);
                if (ImGui.IsItemClicked()) selectedResource = null;
                ImGui.SameLine();
                if (ImGui.SmallButton("+##model-" + model.Id.ToString("N"))) AddLibraryModelSceneObject(model);
            }
            foreach (DirectorExternalModelInfo model in owner.ActionModels.Where(m => !m.PreviewOnly && m.EntityCode.Length > 0).DistinctBy(m => m.EntityCode))
            {
                ImGui.Selectable(model.Name, false);
                if (ImGui.IsItemClicked()) selectedResource = null;
            }
        }
        ImGui.EndChild();
    }

    private void DrawPropertiesPanel(DirectorStudioProject project, float width)
    {
        ImGui.BeginChild("studio-properties", new System.Numerics.Vector2(width, -190), true);
        if (DirectorImGuiTheme.CollapsingHeader(L("property-panel"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            DirectorStudioSceneObject? item = project.SceneObjects.FirstOrDefault(o => o.Id == selectedObjectId);
            if (item is null) ImGui.TextDisabled(L("select-object"));
            else DrawObjectProperties(item);
        }
        if (DirectorImGuiTheme.CollapsingHeader(L("keyframes"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted($"{playhead:0.00} s");
            ImGui.TextDisabled(L("keyframe-none"));
        }
        ImGui.EndChild();
    }

    private void DrawObjectProperties(DirectorStudioSceneObject item)
    {
        string name = item.Name;
        if (ImGui.InputText(L("property-object"), ref name, 96)) { item.Name = name; Active.Dirty = true; }
        float x = (float)item.X, y = (float)item.Y, z = (float)item.Z;
        float yaw = item.Yaw, pitch = item.Pitch, roll = item.Roll, scale = item.Scale;
        bool changed = ImGui.InputFloat("X", ref x) | ImGui.InputFloat("Y", ref y) | ImGui.InputFloat("Z", ref z)
            | ImGui.InputFloat("Yaw", ref yaw) | ImGui.InputFloat("Pitch", ref pitch) | ImGui.InputFloat("Roll", ref roll)
            | ImGui.InputFloat("Scale", ref scale);
        if (changed)
        {
            item.X = x; item.Y = y; item.Z = z; item.Yaw = yaw; item.Pitch = pitch; item.Roll = roll; item.Scale = Math.Clamp(scale, .01f, 100f); Active.Dirty = true;
        }
        if (ImGui.Button(L("reset-transform"))) ResetObject(item.Id);
        ImGui.SameLine();
        if (ImGui.Button(L("duplicate-short"))) DuplicateObject(item.Id);
        ImGui.SameLine();
        if (ImGui.Button(L("remove-object"))) RemoveObject(item.Id);
    }

    private void DrawTimeline(DirectorStudioProject project)
    {
        ImGui.BeginChild("studio-timeline", new System.Numerics.Vector2(0, 160), true);
        if (ImGui.Button("|<")) SeekStudio(0);
        ImGui.SameLine();
        if (ImGui.Button(playing ? "||" : ">")) playing = !playing;
        ImGui.SameLine();
        if (ImGui.Button(">|")) SeekStudio(playhead + 1d / project.FramesPerSecond);
        ImGui.SameLine();
        timelineInput = playhead;
        if (ImGui.InputDouble(L("time-seconds"), ref timelineInput, 0.1, 1, "%.2f")) SeekStudio(timelineInput);
        ImGui.SameLine();
        if (ImGui.Button(L("add-key"))) AddTransformKey();
        ImGui.SameLine();
        ImGui.TextUnformatted($"/ {project.Duration:0.00}s · {project.FramesPerSecond} fps");
        ImGui.Separator();
        float playheadValue = (float)playhead;
        if (ImGui.SliderFloat("##studio-playhead", ref playheadValue, 0, (float)project.Duration, "%.2f s")) SeekStudio(playheadValue);
        ImGui.TextUnformatted($"{L("camera-track")}: {project.CameraKeys.Count}");
        foreach (DirectorStudioSceneObject item in project.SceneObjects)
            ImGui.TextUnformatted($"{item.Name}: {item.Keys.Count} {L("keyframes")}");
        ImGui.EndChild();
    }

    private IEnumerable<string> LocalProjectPaths()
        => Directory.EnumerateFiles(projectDirectory, "*" + DirectorStudioProjectIO.Extension)
            .Concat(Directory.EnumerateFiles(studioDirectory, "*" + DirectorStudioProjectIO.Extension))
            .Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderByDescending(File.GetLastWriteTimeUtc);

    private static string ProjectDisplayName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var project = JsonSerializer.Deserialize<DirectorStudioProject>(stream, DirectorStudioProject.Options);
            return project?.Name ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Path.GetFileNameWithoutExtension(path); }
    }

    private void BeginManagerOperation(ManagerOperation operation)
    {
        if (operation is ManagerOperation.Export or ManagerOperation.Delete && selectedProjectPath is null) return;
        managerOperation = operation;
        operationProjectPath = selectedProjectPath;
        managerValue = operation == ManagerOperation.Create ? newProjectName : string.Empty;
    }

    private string UniqueProjectPath(string name)
    {
        string safe = new(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(64).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "Project";
        string path = Path.Combine(projectDirectory, safe + DirectorStudioProjectIO.Extension);
        for (int suffix = 2; File.Exists(path); suffix++) path = Path.Combine(projectDirectory, safe + " (" + suffix + ")" + DirectorStudioProjectIO.Extension);
        return path;
    }

    private DirectorStudioProject SelectedProjectSnapshot(string path)
        => tabs.FirstOrDefault(t => string.Equals(t.Path, path, PathComparison))?.Project?.DeepClone() ?? DirectorStudioProjectIO.Load(path);

    private void NewProject(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 96) { SetStatus(L("name-required")); return; }
        try
        {
            string path = UniqueProjectPath(name);
            DirectorStudioProject project = new() { Name = name };
            DirectorStudioProjectIO.Save(path, project, overwrite: false);
            if (Active.Project is not null) { tabs.Add(new()); activeTab = tabs.Count - 1; }
            Active.Project = project; Active.Path = path; Active.Dirty = false; showingProjectManager = false; managerOperation = ManagerOperation.None; status = L("created");
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private void OpenProject(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            int existing = tabs.FindIndex(tab => string.Equals(tab.Path, fullPath, PathComparison));
            if (existing >= 0) { activeTab = existing; showingProjectManager = false; return; }
            DirectorStudioProject project = DirectorStudioProjectIO.Load(fullPath);
            if (Active.Project is not null) { tabs.Add(new()); activeTab = tabs.Count - 1; }
            Active.Project = project; Active.Path = fullPath; Active.Dirty = false; selectedObjectId = Guid.Empty; showingProjectManager = false; status = L("opened");
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private void ImportManagerProject(string path)
    {
        try
        {
            DirectorStudioProject project = DirectorStudioProjectIO.Load(path);
            string destination = UniqueProjectPath(project.Name);
            project.Id = Guid.NewGuid();
            DirectorStudioProjectIO.Save(destination, project, overwrite: false);
            selectedProjectPath = destination; status = L("project-imported"); managerOperation = ManagerOperation.None;
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private void ExportManagerProject(string destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new InvalidDataException(L("project-path-required"));
            DirectorStudioProjectIO.Save(destination, SelectedProjectSnapshot(operationProjectPath!), overwrite: false);
            status = L("project-exported"); managerOperation = ManagerOperation.None;
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private void CopyManagerProject()
    {
        if (selectedProjectPath is null) return;
        try
        {
            DirectorStudioProject copy = SelectedProjectSnapshot(selectedProjectPath);
            copy.Id = Guid.NewGuid(); copy.Name += " " + L("copy-suffix");
            string destination = UniqueProjectPath(copy.Name);
            DirectorStudioProjectIO.Save(destination, copy, overwrite: false);
            selectedProjectPath = destination; status = L("created");
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private void DeleteManagerProject()
    {
        try
        {
            if (operationProjectPath is null || !LocalProjectPaths().Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(operationProjectPath), PathComparison))) throw new FileNotFoundException(L("project-missing"));
            File.Delete(operationProjectPath);
            tabs.RemoveAll(t => string.Equals(t.Path, operationProjectPath, PathComparison));
            if (tabs.Count == 0) tabs.Add(new());
            activeTab = Math.Clamp(activeTab, 0, tabs.Count - 1); selectedProjectPath = null; managerOperation = ManagerOperation.None; status = string.Empty;
        }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }

    private bool SaveTab(ProjectTab tab)
    {
        if (!tab.Dirty || tab.Project is null || tab.Path is null) return true;
        try { DirectorStudioProjectIO.Save(tab.Path, tab.Project); tab.Dirty = false; return true; }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); return false; }
    }

    private void SaveActive() { if (Active.Project is null) { SetStatus(L("open-first")); return; } if (SaveTab(Active)) status = L("saved"); }
    private void SeekStudio(double time) { if (Project is not { } p) return; playing = false; playhead = Math.Clamp(DirectorStudioTimeline.Snap(time, p.FramesPerSecond), 0, p.Duration); sceneRenderer.Time = playhead; }
    private void AddTransformKey()
    {
        if (Project?.SceneObjects.FirstOrDefault(o => o.Id == selectedObjectId) is not { } item || Project is null) { SetStatus(L("select-object")); return; }
        DirectorStudioTimeline.Upsert(item, StudioTransformKey.Capture(item, playhead)); Active.Dirty = true; sceneRenderer.Invalidate();
    }

    private void SelectResource(DirectorStudioResourceLink link) { selectedResource = link; selectedObjectId = Guid.Empty; }
    private void AddSceneObject(DirectorStudioResourceLink link)
    {
        if (Project is not { } project) return;
        try { resources.Read(link); var item = new DirectorStudioSceneObject { ResourceLink = link.ToString(), Name = ResourceName(link) }; project.SceneObjects.Add(item); selectedObjectId = item.Id; selectedResource = null; Active.Dirty = true; }
        catch (Exception error) { SetStatus(error.GetBaseException().Message); }
    }
    private void AddLegacySceneObject(Guid assetId)
    {
        if (Project?.Assets.FirstOrDefault(a => a.Id == assetId) is not { } asset) return;
        var item = new DirectorStudioSceneObject { AssetId = assetId, Name = asset.Name }; Project.SceneObjects.Add(item); selectedObjectId = item.Id; Active.Dirty = true;
    }
    private void AddLibraryModelSceneObject(DirectorModelAsset model)
    {
        if (Project is not { } project || project.SceneObjects.Count >= DirectorStudioProject.MaximumSceneObjects) return;
        var item = new DirectorStudioSceneObject
        {
            ResourceLink = DirectorStudioResourceLink.Parse("library:model/" + model.Id.ToString("N")).ToString(),
            Name = model.Name, Scale = Math.Clamp(model.Size, .01f, 100f)
        };
        project.SceneObjects.Add(item); selectedObjectId = item.Id; Active.Dirty = true;
    }
    private void PreviewLibraryAction(DirectorActionAsset action)
    {
        if (owner is null) return;
        string actor = owner.ActionActorSources.FirstOrDefault().ActorKey ?? string.Empty;
        string model = action.PreviewModelCode.Length > 0 ? action.PreviewModelCode : action.CompatibleModelCodes.FirstOrDefault() ?? string.Empty;
        if (!owner.TryPreviewLibraryAction(action.Id, actor, model, action.PlaybackSpeed, action.LoopMode, action.LoopCount, 1f, out string message)) SetStatus(message);
        else SetStatus(message);
    }
    private void UpdateObject(DirectorStudioSceneObject item) { item.Scale = Math.Clamp(item.Scale, .01f, 100f); Active.Dirty = true; }
    private void ResetObject(Guid id) { if (Project?.SceneObjects.FirstOrDefault(o => o.Id == id) is not { } item) return; item.X = item.Y = item.Z = 0; item.Yaw = item.Pitch = item.Roll = 0; item.Scale = 1; Active.Dirty = true; }
    private void RemoveObject(Guid id) { if (Project?.SceneObjects.RemoveAll(o => o.Id == id) > 0) { selectedObjectId = Guid.Empty; Active.Dirty = true; } }
    private void DuplicateObject(Guid id) { if (Project?.SceneObjects.FirstOrDefault(o => o.Id == id) is not { } item || Project.SceneObjects.Count >= DirectorStudioProject.MaximumSceneObjects) return; var copy = item.DeepClone(); copy.Id = Guid.NewGuid(); copy.Name += " copy"; copy.X += 1; Project.SceneObjects.Add(copy); selectedObjectId = copy.Id; Active.Dirty = true; }

    private void SetStatus(string value) => status = value;
    private void OnFileDrop(FileDropEvent dropped) { if (!Opened || dropped.Handled) return; if (ShowingProjectManager) ImportManagerProject(dropped.Filename); else ImportFile(dropped.Filename); dropped.Handled = true; }
    private void ImportFile(string path) { try { DirectorStudioResourceLink link = resources.Import(path); resourceFilter = resources.List("studio").First(r => r.Link == link).Kind; resourcePanelOpen = true; status = L("imported") + " " + link; } catch (Exception error) { SetStatus(error.GetBaseException().Message); } }
    private void OnKeyDown(KeyEvent args) { if (navigationMode && args.KeyCode == (int)GlKeys.Escape) { args.Handled = true; navigationMode = false; } }
    private void OnKeyUp(KeyEvent args) { if (args.KeyCode == modeShortcutKey) modeShortcutHeld = false; }

    // ImGui windows do not have a GuiDialog host. Keep the compatibility hook
    // false so Escape routing never claims an unrelated game dialog; the
    // registered workspace hotkey calls TryToggleModeShortcut directly.
    internal static bool OwnsActiveHost(GuiDialog host) => false;
    internal static bool RouteModeShortcut(KeyEvent args)
    {
        if (activeStudio is not { } studio || args.Handled) return false;
        var mapping = studio.capi.Input.GetHotKeyByCode("vsdirector-workspace-mode")?.CurrentMapping ?? new KeyCombination { KeyCode = (int)GlKeys.F8 };
        if (!DirectorWorkspaceHud.MatchesModeShortcut(args, mapping)) return false;
        args.Handled = studio.TryToggleModeShortcut(); return args.Handled;
    }
    internal bool TryToggleModeShortcut()
    {
        if (!Opened || disposed) return false;
        if (modeShortcutHeld) return true;
        modeShortcutHeld = true; modeShortcutKey = capi.Input.GetHotKeyByCode("vsdirector-workspace-mode")?.CurrentMapping.KeyCode ?? (int)GlKeys.F8;
        if (!ShowingProjectManager && !resourcePanelOpen) navigationMode = !navigationMode;
        return true;
    }

    private void SwitchToDirector()
    {
        foreach (ProjectTab tab in tabs) if (!SaveTab(tab)) return;
        sceneRenderer.SetProject(null); Close(); activeStudio = null; returnToDirector();
    }

    internal static bool TryCloseActive()
    {
        if (activeStudio is not { } studio || !studio.Opened) return false;
        if (studio.resourcePanelOpen) { studio.resourcePanelOpen = false; return true; }
        if (studio.managerOperation != ManagerOperation.None) { studio.managerOperation = ManagerOperation.None; return true; }
        foreach (ProjectTab tab in studio.tabs) if (!studio.SaveTab(tab)) return true;
        studio.sceneRenderer.SetProject(null);
        studio.Close();
        activeStudio = null;
        return true;
    }

    public new void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capi.Event.UnregisterGameTickListener(tickListener);
        capi.Event.FileDrop -= OnFileDrop;
        capi.Event.KeyDown -= OnKeyDown;
        capi.Event.KeyUp -= OnKeyUp;
        foreach (ProjectTab tab in tabs) SaveTab(tab);
        sceneRenderer.SetProject(null);
        sceneRenderer.Dispose();
        activeStudio = null;
        base.Dispose();
    }
}
