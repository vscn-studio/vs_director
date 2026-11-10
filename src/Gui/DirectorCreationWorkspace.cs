using static VSDirector.Gui.DirectorCreationText;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using Vintagestory.API.Client;
using VSDirector.Data;
using VSDirector.Runtime;

namespace VSDirector.Gui;

/// <summary>Scene-first creation tools, hosted in the existing left/right/bottom docks.</summary>
internal sealed partial class DirectorCreationWorkspace
{
    private readonly ICoreClientAPI api;
    private readonly VSDirectorModSystem owner;
    private readonly DirectorImGuiWorkspace host;
    private DirectorCreationRuntime Runtime => owner.Creation;
    private DirectorCreationSession Session => Runtime.Session;
    private DirectorCreationProject? Project => Session.Project;
    internal bool Active { get; private set; }
    private bool browser = true, properties = true, timeline = true, view = true;
    private string requestedMenu = "", search = "", name = L("creation-new-project"), path = "", modal = "", modalRequest = "", error = "";
    private string modelContext = "";
    private Guid clipId, keyId, trackId;
    private Action<string>? acceptPath;
    private string dialogDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private bool pathDialog;
    private Guid modelId;
    private Guid displayedProject;
    private long displayedGeneration;
    private bool revealPanels;
    internal DirectorCreationWorkspace(ICoreClientAPI api, VSDirectorModSystem owner, DirectorImGuiWorkspace host)
    { this.api = api; this.owner = owner; this.host = host; }
    internal void Menu(string menu) { Enter(); requestedMenu = menu; browser = properties = timeline = true; }
    internal void Suspend() { if (Active) { Runtime.Suspend(force: true); owner.LeaveCreationMode(); Active = false; host.SetCreationMode(false); } host.CreationPreviewVisible = false; }
    internal void Draw()
    {
        if (!Active) return;
        DrawToolbar(); DrawPendingTransition();
        if (!Active) return;
        DrawAuxiliary();
        if (requestedMenu.Length > 0) { ImGui.OpenPopup("creation-" + requestedMenu); requestedMenu = ""; }
        foreach (string menu in new[] { "创作", "项目", "编辑", "创建", "模型", "动画", "窗口" })
        {
            ConstrainMenu();
            if (menuAnchor is { } anchor) ImGui.SetNextWindowPos(anchor, ImGuiCond.Appearing);
            if (ImGui.BeginPopup("creation-" + menu))
            {
                try { DrawMenu(menu); }
                finally { ImGui.EndPopup(); }
            }
        }
        DrawModal();
        if (!Active) return;
        DrawStructureCapture();
        if (Project is null)
        {
            if (browser) Panel(L("creation-projects") + "###vsdirector-creation-browser", ref browser, host.LeftDockId, DrawProjects);
            return;
        }
        if (displayedProject != Project.Id || displayedGeneration != Session.DocumentGeneration)
        {
            displayedProject = Project.Id;
            displayedGeneration = Session.DocumentGeneration;
            browser = properties = timeline = revealPanels = true;
            modelContext = ""; timelineZoom = 1; timelineStart = 0; timelineEditor = false;
            clipId = keyId = trackId = Guid.Empty;
            if (SelectedObject is null) Session.SelectedObject = Project.CameraId;
        }
        ImGui.BeginDisabled(Runtime.Exporting || Runtime.PerformanceActive);
        if (browser) Panel(L("creation-objects") + "###vsdirector-creation-browser", ref browser, host.LeftDockId, DrawBrowser, revealPanels);
        if (view) Panel(L("creation-view") + "###vsdirector-creation-view", ref view, host.InspectorDockId, DrawView);
        if (properties) Panel(modelContext.Length > 0 ? L("creation-model-properties") + "###vsdirector-creation-properties" : L("creation-creation-properties") + "###vsdirector-creation-properties", ref properties, host.InspectorDockId, DrawProperties, revealPanels);
        ImGui.EndDisabled();
        if (timeline && modelContext.Length == 0) Panel((SelectedAnimation is null ? L("creation-creation-timeline") : L("creation-action-prefix") + SelectedAnimation.Name) + "###vsdirector-creation-timeline", ref timeline, host.TimelineDockId, DrawTimeline, revealPanels);
        revealPanels = false;
        // Viewport tools must not prevent the document panels from being submitted.
        if (!Runtime.Exporting && !Runtime.PerformanceActive) { try { DrawViewportTools(); } catch (Exception e) { error = Message(e.Message); } }
        host.CreationAspect = Project.Output.Width / (float)Math.Max(1, Project.Output.Height);
        host.CreationPreviewVisible = Runtime.Preview;
        if (Runtime.Preview) DrawPreview();
        if (!ImGui.GetIO().WantTextInput && ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow) && !Runtime.Exporting && !Runtime.PerformanceActive)
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S)) Continue(Session.Save);
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z)) { if (io.KeyShift) Continue(Session.Redo); else Continue(Session.Undo); }
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y)) Continue(Session.Redo);
        }
    }
    private void Panel(string title, ref bool open, uint dock, Action draw, bool reveal = false)
    {
        if (dock != 0) ImGui.SetNextWindowDockID(dock, reveal ? ImGuiCond.Always : ImGuiCond.FirstUseEver);
        if (reveal) { ImGui.SetNextWindowCollapsed(false); ImGui.SetNextWindowFocus(); }
        ImGui.SetNextWindowSize(new(320, 320), ImGuiCond.FirstUseEver);
        bool visible = ImGui.Begin(title, ref open);
        ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
        host.TrackCreationDock(title);
        try { if (visible) draw(); } finally { ImGui.End(); }
    }
    private void Run(Action action)
    {
        try { action(); error = ""; } catch (Exception e) { error = Message(e.Message); Runtime.SetStatus(e.Message); }
    }
    private void Item(string title, Action action, bool enabled = true, bool selected = false)
    {
        float limit = Math.Min(330 * DirectorImGuiTheme.Scale, ImGui.GetIO().DisplaySize.X - 66);
        string display = title;
        while (display.Length > 1 && ImGui.CalcTextSize(display).X > limit) display = display[..^1];
        if (display != title) display += "…";
        if (ImGui.MenuItem(display + "###" + title, "", selected, enabled)) Continue(action);
        if (display != title && ImGui.IsItemHovered()) ImGui.SetTooltip(title);
    }
    private void DrawMenu(string menu)
    {
        if (Runtime.Exporting)
        {
            ImGui.TextUnformatted(L("creation-exporting-video"));
            Item(L("creation-cancel-video-export"), Runtime.CancelExport);
            return;
        }
        bool ready = Project is not null;
        if (menu is "创作" or "项目")
        {
            Item(L("creation-new-project-16"), () => { name = L("creation-new-project"); Ask(L("creation-new-project-16"), value => Runtime.New(value)); });
            Item(L("creation-open-project-recent"), () => auxiliary = "projects");
            Item(L("creation-save-project"), Session.Save, ready);
            Item(L("creation-duplicate-project"), () => { Session.Save(); Session.Open(Session.Store.Duplicate(Project!)); }, ready);
            Item(L("creation-rename-project"), () => Ask(L("creation-rename-project"), v => Session.Edit(L("creation-rename-project"), p => p.Name = v), Project!.Name), ready);
            Item(L("creation-project-settings"), () => auxiliary = "settings", ready);
            Item(L("creation-project-folder"), () => Ask(L("creation-project-folder"), v => Session.Edit(L("creation-project-category"), p => p.Folder = v), Project!.Folder), ready);
            ImGui.Separator();
            Item(L("creation-import-shape-model"), () => FileDialog(L("creation-import-shape-model"), ImportModel), ready);
            Item(L("creation-import-image-texture"), () => FileDialog(L("creation-import-image-texture"), p => ImportAsset(p, CreationAssetKind.Image)), ready);
            Item(L("creation-import-audio"), () => FileDialog(L("creation-import-audio"), p => ImportAsset(p, CreationAssetKind.Audio)), ready);
            Item(L("creation-export-current-model"), () => FileDialog(L("creation-export-current-model"), ExportModel, "model.json"), CurrentModel is not null);
            Item(L("creation-export-current-animation"), () => FileDialog(L("creation-export-current-animation"), ExportAnimation, "animation.json"), SelectedAnimation is not null);
            ImGui.Separator();
            Item(L("creation-screenshot"), () => FileDialog(L("creation-save-screenshot"), Runtime.Screenshot, "frame.png"), ready);
            Item(L("creation-export-video"), () => FileDialog(L("creation-export-video"), ExportProject, "film.mp4"), ready && !Runtime.Exporting);
            Item(L("creation-cancel-video-export"), Runtime.CancelExport, Runtime.Exporting);
            Item(L("creation-hide-creation-objects"), () => Runtime.Enabled = !Runtime.Enabled, ready);
        }
        if (!ready) return;
        if (menu == "编辑")
        {
            if (ImGui.BeginMenu(L("creation-model"), CurrentModel is not null)) { DrawMenu("模型"); ImGui.EndMenu(); }
            if (ImGui.BeginMenu(L("creation-animation"), CurrentModel is not null)) { DrawMenu("动画"); ImGui.EndMenu(); }
            Item(L("creation-undo"), Session.Undo, Session.History.CanUndo); Item(L("creation-redo"), Session.Redo, Session.History.CanRedo);
            Item(L("creation-duplicate-selected-object"), DuplicateObject, SelectedObject is not null);
            Item(L("creation-delete-selected-object"), () => Session.Edit(L("creation-delete-object"), p => DirectorCreationEdits.DeleteObject(p, Session.SelectedObject)), SelectedObject is not null);
            Item(L("creation-move-scene-to-observer"), () => Session.Edit(L("creation-move-scene-origin"), p => p.Origin += Runtime.CursorPosition()));
            Item(L("creation-apply-preset"), () => auxiliary = "presets", SelectedObject is not null);
            Item(L("creation-save-pose-preset"), SavePreset, SelectedObject is not null);
            Item(L("creation-link-current-replay-as-background"), () => Session.Edit(L("creation-link-background-replay"), p => p.BackgroundReplayId = owner.ActiveReplayId), owner.ActiveReplayId != Guid.Empty);
            Item(L("creation-unlink-background-replay"), () => Session.Edit(L("creation-unlink-background-replay"), p => p.BackgroundReplayId = Guid.Empty));
        }
        if (menu == "创建")
        {
            Item(L("creation-native-actors"), () => auxiliary = "actors");
            Item(L("creation-cube-actor"), AddCubeActor); Item(L("creation-camera"), () => AddObject(CreationObjectKind.Camera));
            Item(L("creation-group-empty-object"), () => AddObject(CreationObjectKind.Group));
            Item(L("creation-item-block"), () => AddObject(CreationObjectKind.Item));
            Item(L("creation-capture-scene-structure"), BeginStructureCapture);
            Item(L("creation-3d-text"), () => AddObject(CreationObjectKind.Text));
            Item(L("creation-image"), () => FileDialog(L("creation-import-image"), p => { var a = ImportAsset(p, CreationAssetKind.Image); AddObject(CreationObjectKind.Image, a.Id); }));
            Item(L("creation-game-particles"), () => AddObject(CreationObjectKind.Particle));
            Item(L("creation-add-actor-from-existing-model"), () => auxiliary = "resources");
        }
        if (menu == "模型")
        {
            Item(L("creation-new-cube-model"), AddCubeActor);
            Item(L("creation-edit-selected-model"), () => { modelId = SelectedObject!.ModelId; Session.LeaveAnimation(); modelContext = "model"; Session.SelectedPart = Guid.Empty; }, SelectedObject?.ModelId != Guid.Empty && CurrentModel is not null);
            Item(L("creation-add-cube-part"), () => AddPart(false), CurrentModel is not null);
            Item(L("creation-add-group"), () => AddPart(true), CurrentModel is not null);
            Item(L("creation-import-shape-model"), () => FileDialog(L("creation-import-shape-model"), ImportModel));
            Item(L("creation-reload-textures"), Runtime.Renderer.ReloadTextures);
            Item(L("creation-return-to-scene-editing"), ReturnToProject);
        }
        if (menu == "动画")
        {
            Item(L("creation-new-animation"), () => Ask(L("creation-animation-name"), NewAnimation), CurrentModel is not null && SelectedObject is not null);
            Item(L("creation-import-standalone-animation"), () => FileDialog(L("creation-import-standalone-animation"), ImportAnimation), CurrentModel is not null);
            Item(L("creation-save-pose-preset"), SavePreset, SelectedObject is not null);
            Item(L("creation-generate-idle-animation"), () => BasicAction(false), CurrentModel?.BodyMapping.Count > 0);
            Item(L("creation-generate-walk-animation"), () => BasicAction(true), CurrentModel?.BodyMapping.Count > 0);
            Item(Runtime.PerformanceActive ? L("creation-stop-performance-recording") : L("creation-record-player-to-selected-actor"), Runtime.TogglePerformance, SelectedObject?.Kind == CreationObjectKind.Model);
            Item(L("creation-return-to-scene-timeline"), ReturnToProject);
        }
        if (menu == "窗口")
        {
            Item(L("creation-view"), () => view = true, selected: view);
            Item(L("creation-creation-list"), () => browser = true, selected: browser);
            Item(L("creation-creation-properties"), () => properties = true, selected: properties);
            Item(L("creation-creation-timeline"), () => timeline = true, enabled: modelContext.Length == 0, selected: timeline && modelContext.Length == 0);
            Item(L("creation-camera-preview"), () => Runtime.Preview = !Runtime.Preview, selected: Runtime.Preview);
            Item(L("creation-composition-guides"), () => Runtime.Guides = !Runtime.Guides, selected: Runtime.Guides);
            Item(L("creation-reset-layout"), host.ResetLayout);
        }
    }
    private CreationSceneObject? SelectedObject => Project?.Objects.Find(o => o.Id == Session.SelectedObject);
    private CreationModel? CurrentModel => Project?.Models.Find(m => m.Id == (modelContext.Length > 0 ? modelId : SelectedObject?.ModelId));
    private CreationPart? SelectedPart => CurrentModel?.Parts.Find(p => p.Id == Session.SelectedPart);
    private CreationAnimation? SelectedAnimation => Project?.Animations.Find(a => a.Id == Session.SelectedAnimation);
    private bool structureCapture;
    private CreationVector structureFirst;
    private void Select(CreationSceneObject obj)
    { Continue(() => { if (SelectedAnimation is not null && Session.AnimationObject != obj.Id) Session.LeaveAnimation(); Session.SelectedObject = obj.Id; Session.SelectedPart = Guid.Empty; modelId = obj.ModelId; modelContext = ""; clipId = keyId = trackId = Guid.Empty; properties = true; }); }
    private void AddObject(CreationObjectKind kind, Guid asset = default)
    {
        var obj = new CreationSceneObject { Kind = kind, AssetId = asset, ResourceCode = kind == CreationObjectKind.Item ? "game:torch" : "", Name = kind switch { CreationObjectKind.Camera => L("creation-camera"), CreationObjectKind.Group => L("creation-group"), CreationObjectKind.Text => L("creation-text"), CreationObjectKind.Particle => L("creation-particles"), CreationObjectKind.Item => L("creation-item-block"), _ => L("creation-image") }, Transform = new() { Position = Runtime.CursorPosition() } };
        Session.Edit(L("creation-create-5") + obj.Name, p => { p.Objects.Add(obj); if (kind == CreationObjectKind.Camera && p.CameraId == Guid.Empty) p.CameraId = obj.Id; }); Select(obj);
        if (kind == CreationObjectKind.Camera) Runtime.CaptureCamera(obj);
    }
    private void BeginStructureCapture()
    {
        structureFirst = Runtime.SelectedBlock();
        structureCapture = true;
    }
    private void DrawStructureCapture()
    {
        if (!structureCapture) return;
        bool visible = ImGui.Begin(L("creation-capture-scene-structure") + "###creation-structure-capture", ref structureCapture, ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
        if (!visible) { ImGui.End(); return; }
        ImGui.TextWrapped(L("creation-structure-corners", structureFirst.X, structureFirst.Y, structureFirst.Z));
        if (Button(L("creation-confirm-second-corner"))) Run(() => { var second = Runtime.SelectedBlock(); Select(Runtime.CaptureStructure(structureFirst, second)); structureCapture = false; });
        ImGui.SameLine(); if (Button(L("creation-cancel"))) structureCapture = false;
        ImGui.End();
    }
    private void AddCubeActor()
    {
        var model = new CreationModel { Name = L("creation-cube-model"), Parts = [new() { Name = L("creation-part-cube"), To = new(16, 16, 16), Origin = new(8, 8, 8) }] };
        Session.Edit(L("creation-create-model"), p => p.Models.Add(model)); AddActor(model);
    }
    private void AddActor(CreationModel model)
    {
        var obj = new CreationSceneObject { Name = model.Name, Kind = CreationObjectKind.Model, ModelId = model.Id, Transform = new() { Position = Runtime.CursorPosition() } };
        Session.Edit(L("creation-add-actor"), p => p.Objects.Add(obj)); Select(obj); Runtime.Enabled = true;
    }
    private void DuplicateObject()
    {
        if (SelectedObject is not { } source) return;
        var copy = CreationJson.Clone(source); copy.Id = Guid.NewGuid(); copy.Name += L("creation-copy"); copy.Transform.Position += new CreationVector(1, 0, 0);
        Session.Edit(L("creation-duplicate-object"), p =>
        {
            p.Objects.Add(copy);
            foreach (var track in p.Tracks.Where(t => t.ObjectId == source.Id).ToArray()) { var t = CreationJson.Clone(track); t.Id = Guid.NewGuid(); t.ObjectId = copy.Id; foreach (var key in t.Keys) key.Id = Guid.NewGuid(); p.Tracks.Add(t); }
            foreach (var clip in p.Clips.Where(c => c.ObjectId == source.Id).ToArray()) { var c = CreationJson.Clone(clip); c.Id = Guid.NewGuid(); c.ObjectId = copy.Id; p.Clips.Add(c); }
        }); Select(copy);
    }
    private void ImportModel(string file)
    {
        var (model, animations) = DirectorCreationShape.Import(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
        Session.Edit(L("creation-import-model"), p =>
        {
            Session.Store.Import(p, file, CreationAssetKind.Model);
            string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
            foreach (var texture in model.Textures.ToArray())
            {
                string relative = texture.Value.Replace('\\', '/');
                if (relative.StartsWith('#')) continue;
                int colon = relative.IndexOf(':'); if (colon >= 0) relative = relative[(colon + 1)..];
                if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) relative += ".png";
                var candidates = new[] { Path.Combine(directory, relative), Path.Combine(directory, "textures", relative), Path.Combine(directory, Path.GetFileName(relative)) };
                string? source = candidates.FirstOrDefault(File.Exists);
                if (source is not null) model.Textures[texture.Key] = Session.Store.Import(p, source, CreationAssetKind.Texture).Source;
            }
            p.Models.Add(model); p.Animations.AddRange(animations);
        }); AddActor(model);
    }
    private CreationAsset ImportAsset(string file, CreationAssetKind kind)
    {
        CreationAsset? asset = null; Session.Edit(L("creation-import-asset"), p => asset = Session.Store.Import(p, file, kind)); return asset!;
    }
    private void ExportModel(string file)
    {
        if (CurrentModel is not { } model || Project is null) return;
        var root = DirectorCreationShape.Export(model, Project.Animations);
        if (exportTextures)
        {
            var textures = (JsonObject)root["textures"]!;
            foreach (var texture in model.Textures.Where(t => t.Value.StartsWith("assets/", StringComparison.Ordinal)))
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "textures"); Directory.CreateDirectory(directory);
                string filename = Path.GetFileName(texture.Value); File.Copy(Session.Store.AssetPath(Project.Id, texture.Value), Path.Combine(directory, filename), true);
                textures[texture.Key] = "textures/" + Path.GetFileNameWithoutExtension(filename);
            }
        }
        DirectorCreationStore.AtomicWrite(file, root.ToJsonString(CreationJson.Options)); Runtime.SetStatus(L("creation-model-exported") + file);
    }
    private void ExportAnimation(string file)
    {
        if (SelectedAnimation is not { } a || CurrentModel is not { } model) return;
        DirectorCreationStore.AtomicWrite(file, DirectorCreationShape.ExportAnimation(a, model).ToJsonString(CreationJson.Options));
    }
    private void ImportAnimation(string file)
    {
        if (CurrentModel is not { } model) return;
        var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException(L("creation-the-animation-file-must-contain-a-json-object"));
        var nodes = root["animations"] as JsonArray ?? new JsonArray(root.DeepClone());
        if (nodes.OfType<JsonObject>().Any(n => (int)DirectorCreationShape.N(n, "version") != model.AnimationVersion))
            throw new InvalidDataException(L("creation-the-animation-version-differs-from-this-model-import-an-animation-usin"));
        Session.Edit(L("creation-import-animation"), p => { foreach (var node in nodes.OfType<JsonObject>()) p.Animations.Add(DirectorCreationShape.ImportAnimation(node, model)); });
    }
    private void NewAnimation(string name)
    {
        if (CurrentModel is not { } model || SelectedObject is not { } obj) return;
        var animation = new CreationAnimation { Name = name, ModelId = model.Id, Duration = Math.Min(3, Project!.Duration) };
        Session.Edit(L("creation-new-animation"), p => p.Animations.Add(animation));
        EditAnimation(animation);
    }
    private void BasicAction(bool walk)
    {
        if (CurrentModel is not { } model || SelectedObject is not { } obj) return;
        var a = DirectorCreationShape.BasicAction(model, walk ? L("creation-walk") : L("creation-idle"), walk);
        Session.Edit(L("creation-generate-basic-animation"), p => p.Animations.Add(a));
        EditAnimation(a);
    }
    private void SavePreset()
    {
        if (SelectedObject is not { } obj) return;
        Ask(L("creation-save-pose-appearance-preset"), value => Session.Edit(L("creation-save-preset"), p => p.Presets.Add(new() { Name = value, ModelId = obj.ModelId, Pose = CreationJson.Clone(Session.Frame!.Objects[obj.Id].Pose), Appearance = CreationJson.Clone(obj) })), obj.Name + L("creation-pose"));
    }
    private void DrawBrowser()
    {
        ImGui.TextWrapped(Project!.Name + (Session.Dirty ? " *" : ""));
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##search", L("creation-search-names"), ref search, 256);
        if (modelContext.Length > 0 || SelectedAnimation is not null)
        {
            if (Button(L("creation-back-project"))) ReturnToProject();
            if (SelectedAnimation is { } action && Button(L("creation-add-animation-clip"))) Continue(() => AddAnimationClip(action));
        }
        DrawObjects();
        if (error.Length > 0) ImGui.TextWrapped(error);
    }
    private bool Match(string value) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static unsafe bool BrowserTab(string name, ImGuiTabItemFlags flags)
    {
        byte[] label = System.Text.Encoding.UTF8.GetBytes(Label(name) + "###" + name + "\0");
        fixed (byte* text = label) return ImGuiNative.igBeginTabItem(text, null, flags) != 0;
    }
    private void DrawObjects()
    {
        if (Project is null) return;
        void Children(Guid parent, int depth)
        {
            if (depth > 64) return;
            foreach (var obj in Project.Objects.Where(o => o.ParentId == parent).ToArray())
            {
                if (search.Length > 0 && !Match(obj.Name)) continue;
                bool hasChildren = Project.Objects.Any(o => o.ParentId == obj.Id) || Project.Models.Any(m => m.Id == obj.ModelId && m.Parts.Count > 0);
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | (obj.Id == Session.SelectedObject ? ImGuiTreeNodeFlags.Selected : 0)
                    | (hasChildren ? 0 : ImGuiTreeNodeFlags.Leaf);
                ImGui.PushID(obj.Id.ToString());
                bool open = ImGui.TreeNodeEx(obj.Name + "###" + obj.Id, flags);
                if (ImGui.IsItemClicked()) Select(obj);
                if (ImGui.BeginPopupContextItem("object-menu"))
                {
                    DrawObjectContext(obj);
                    ImGui.EndPopup();
                }
                if (open) { if (Project.Models.Find(m => m.Id == obj.ModelId) is { } m) DrawParts(m, obj.Id); Children(obj.Id, depth + 1); ImGui.TreePop(); }
                ImGui.PopID();
            }
        }
        if (modelContext.Length > 0 && CurrentModel is { } editingModel) DrawParts(editingModel, Session.SelectedObject);
        else Children(Guid.Empty, 0);
        if (Project.Objects.Count == 1)
        {
            Hint(L("creation-new-guide"));
            if (Button(L("creation-cube-actor"))) Continue(AddCubeActor);
            if (Button(L("creation-import-shape-model"))) FileDialog(L("creation-import-shape-model"), ImportModel);
        }
    }
    private void DrawObjectContext(CreationSceneObject obj)
    {
        if (obj.Kind == CreationObjectKind.Model) Item(L("creation-record-player-to-selected-actor"), () => { Session.SelectedObject = obj.Id; Runtime.TogglePerformance(); });
        Item(L("creation-rename-object"), () => Ask(L("creation-rename-object"), value => Session.Edit(L("creation-rename-object"), p => p.Objects.First(o => o.Id == obj.Id).Name = value), obj.Name));
        Item(L("creation-duplicate-selected-object"), () => { Session.SelectedObject = obj.Id; DuplicateObject(); });
        Item(L("creation-delete-selected-object"), () =>
        {
            if (Session.AnimationObject == obj.Id) Session.LeaveAnimation();
            Session.Edit(L("creation-delete-object"), p => DirectorCreationEdits.DeleteObject(p, obj.Id));
            if (Session.SelectedObject == obj.Id) Session.SelectedObject = Session.SelectedPart = Guid.Empty;
            clipId = keyId = trackId = Guid.Empty;
        });
        ImGui.Separator();
        Item(L("creation-edit-selected-model"), () => { Session.LeaveAnimation(); Session.SelectedObject = obj.Id; modelContext = "model"; modelId = obj.ModelId; }, obj.ModelId != Guid.Empty);
        Item(L("creation-new-animation"), () => { Session.SelectedObject = obj.Id; Ask(L("creation-animation-name"), NewAnimation); }, obj.ModelId != Guid.Empty);
    }
    private void DrawParts(CreationModel model, Guid actor)
    {
        void Parts(Guid parent, int depth)
        {
            if (depth > 128) return;
            foreach (var part in model.Parts.Where(p => p.ParentId == parent))
            {
                ImGui.PushID(actor.ToString());
                bool open = ImGui.TreeNodeEx(part.Name + "###part-" + part.Id, ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth
                    | (part.Id == Session.SelectedPart && actor == Session.SelectedObject ? ImGuiTreeNodeFlags.Selected : 0)
                    | (model.Parts.Any(p => p.ParentId == part.Id) ? 0 : ImGuiTreeNodeFlags.Leaf));
                if (ImGui.IsItemClicked()) Continue(() => { if (SelectedAnimation is not null && Session.AnimationObject != actor) Session.LeaveAnimation(); Session.SelectedObject = actor; Session.SelectedPart = part.Id; clipId = keyId = trackId = Guid.Empty; properties = true; });
                if (open) { Parts(part.Id, depth + 1); ImGui.TreePop(); }
                ImGui.PopID();
            }
        }
        Parts(Guid.Empty, 0);
    }
    private void DrawAssets()
    {
        if (Project is null) return;
        ImGui.SeparatorText(L("creation-models-double-click-to-add-actor"));
        foreach (var model in Project.Models.Where(m => Match(m.Name + m.Folder)))
        {
            if (ImGui.Selectable(model.Name + "##model-" + model.Id, model.Id == modelId, ImGuiSelectableFlags.AllowDoubleClick))
            { modelId = model.Id; if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Continue(() => { AddActor(model); auxiliary = ""; }); }
        }
        ImGui.SeparatorText(L("creation-animations-double-click-to-add-clip"));
        foreach (var a in Project.Animations.Where(a => Match(a.Name + a.Folder)))
        {
            ImGui.PushID(a.Id.ToString());
            if (ImGui.Selectable(a.Name, a.Id == Session.SelectedAnimation)) EditAnimation(a);
            if (ImGui.BeginPopupContextItem("action-menu"))
            {
                Item(L("creation-add-animation-clip"), () => AddAnimationClip(a));
                Item(L("creation-edit"), () => EditAnimation(a)); ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        ImGui.SeparatorText(L("creation-assets-recently-used-first"));
        foreach (var asset in Project.Assets.Where(a => Match(a.Name + a.Folder)).OrderByDescending(a => a.LastUsedUtc).ToArray())
        {
            if (ImGui.Selectable(asset.Name + " · " + L("creation-asset-" + asset.Kind.ToString().ToLowerInvariant()) + "##" + asset.Id, false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                asset.LastUsedUtc = DateTimeOffset.UtcNow;
                if (asset.Kind == CreationAssetKind.Audio) Session.Edit(L("creation-add-audio"), p => p.Clips.Add(new() { Name = asset.Name, AudioAssetId = asset.Id, Start = Session.Time, Duration = Math.Max(.1, p.Duration - Session.Time) }));
                else if (asset.Kind is CreationAssetKind.Image or CreationAssetKind.Texture) AddObject(CreationObjectKind.Image, asset.Id);
            }
            if (ImGui.BeginPopupContextItem("asset-menu-" + asset.Id))
            {
                Item(L("creation-rename"), () => Ask(L("creation-asset-name"), v => Session.Edit(L("creation-rename-asset"), _ => asset.Name = v), asset.Name));
                Item(L("creation-category-folder"), () => Ask(L("creation-asset-folder"), v => Session.Edit(L("creation-asset-category"), _ => asset.Folder = v), asset.Folder));
                ImGui.EndPopup();
            }
        }
    }
    private void DrawPresets()
    {
        if (Project is null) return;
        foreach (var preset in Project.Presets.Where(p => Match(p.Name + p.Folder)).ToArray())
        {
            if (ImGui.Selectable(preset.Name + "##" + preset.Id) && SelectedObject is { } obj && obj.ModelId == preset.ModelId)
                Session.Edit(L("creation-apply-pose-preset"), _ => obj.Pose = CreationJson.Clone(preset.Pose));
            if (ImGui.BeginPopupContextItem("preset-" + preset.Id))
            {
                Item(L("creation-apply-appearance"), () => { if (SelectedObject is { } obj && preset.Appearance is { } appearance) Session.Edit(L("creation-apply-appearance"), _ => { obj.ModelId = appearance.ModelId; obj.Color = appearance.Color; obj.Pose = CreationJson.Clone(preset.Pose); }); });
                Item(L("creation-rename"), () => Ask(L("creation-preset-name"), v => Session.Edit(L("creation-rename-preset"), _ => preset.Name = v), preset.Name));
                Item(L("creation-delete"), () => Session.Edit(L("creation-delete-preset"), p => p.Presets.Remove(preset))); ImGui.EndPopup();
            }
        }
    }
    private IReadOnlyList<CreationProjectEntry> projectEntries = Array.Empty<CreationProjectEntry>();
    private DateTime projectListAt;
    private void DrawProjects()
    {
        if (Button(L("creation-new-project-16"))) Ask(L("creation-new-project-16"), Runtime.New);
        if (DateTime.UtcNow >= projectListAt) { projectEntries = Session.Store.List(); projectListAt = DateTime.UtcNow.AddSeconds(2); }
        foreach (var entry in projectEntries.Where(p => Match(p.Name + p.Folder)))
        {
            if (ImGui.Selectable((entry.Folder.Length > 0 ? entry.Folder + "/" : "") + entry.Name + (entry.Recovery ? L("creation-recovery-available") : "") + "##" + entry.Id)) Continue(() => { Runtime.Open(entry.Id); auxiliary = ""; });
            if (ImGui.BeginPopupContextItem("project-" + entry.Id))
            {
                Item(L("creation-recover-autosave"), () => Runtime.Open(entry.Id, true), entry.Recovery);
                Item(L("creation-duplicate"), () => Session.Open(Session.Store.Duplicate(Session.Store.Load(entry.Id))));
                Item(L("creation-delete-move-to-local-trash"), () => { if (Project?.Id == entry.Id) throw new InvalidOperationException(L("creation-open-another-project-before-deleting-the-current-project")); Session.Store.Delete(entry.Id); });
                ImGui.EndPopup();
            }
        }
    }
    private void Ask(string title, Action<string> accept, string initial = "") { modalRequest = title; name = initial; acceptPath = accept; pathDialog = false; }
    private void FileDialog(string title, Action<string> accept, string filename = "")
    { modalRequest = title; path = filename.Length > 0 ? Path.Combine(dialogDirectory, filename) : ""; acceptPath = accept; pathDialog = true; }
    private void DrawModal()
    {
        if (modalRequest.Length > 0) { modal = modalRequest; modalRequest = ""; ImGui.OpenPopup(L("creation-creation") + "###creation-dialog"); }
        ImGui.SetNextWindowSize(new Vector2(580, pathDialog ? 500 : 150) * DirectorImGuiTheme.Scale, ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(L("creation-creation") + "###creation-dialog", ref open, ImGuiWindowFlags.NoSavedSettings)) return;
        ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
        try
        {
            ImGui.TextUnformatted(modal);
            if (pathDialog)
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputText("##file-path", ref path, 2048);
                if (Button(L("creation-parent-folder"))) dialogDirectory = Directory.GetParent(dialogDirectory)?.FullName ?? dialogDirectory;
                ImGui.SameLine(); ImGui.TextUnformatted(dialogDirectory);
                ImGui.BeginChild("files", new(0, -75 * DirectorImGuiTheme.Scale), true);
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dialogDirectory).OrderBy(d => d).Take(100))
                        if (ImGui.Selectable(L("creation-folder") + Path.GetFileName(d), false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { dialogDirectory = d; break; }
                    foreach (var file in Directory.EnumerateFiles(dialogDirectory).OrderBy(f => f).Take(300))
                        if (ImGui.Selectable(Path.GetFileName(file), path == file)) path = file;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ImGui.TextWrapped(e.Message); }
                finally { ImGui.EndChild(); }
            }
            else { ImGui.SetNextItemWidth(-1); ImGui.InputText("##name", ref name, 256); }
            if (error.Length > 0) ImGui.TextWrapped(error);
            if (Button(L("creation-ok")))
            {
                string value = (pathDialog ? path : name).Trim();
                if (value.Length > 0)
                {
                    var accept = acceptPath; Continue(() => accept?.Invoke(value));
                    if (error.Length == 0) { if (pathDialog) dialogDirectory = Path.GetDirectoryName(Path.GetFullPath(value))!; ImGui.CloseCurrentPopup(); }
                }
            }
            ImGui.SameLine(); if (Button(L("creation-cancel"))) { error = ""; ImGui.CloseCurrentPopup(); }
        }
        finally { ImGui.EndPopup(); }
    }
    private void DrawPreview()
    {
        bool open = Runtime.Preview;
        if (ImGui.Begin(L("creation-camera-preview") + "###vsdirector-creation-preview", ref open))
        {
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
            host.TrackCreationDock("###vsdirector-creation-preview");
            if (Runtime.PreviewTexture > 0 && Project is { } p)
            {
                var available = ImGui.GetContentRegionAvail(); float ratio = p.Output.Width / (float)p.Output.Height;
                var size = new Vector2(Math.Min(available.X, available.Y * ratio), Math.Min(available.Y, available.X / ratio));
                var start = ImGui.GetCursorScreenPos() + (available - size) / 2; ImGui.SetCursorScreenPos(start);
                ImGui.Image((nint)Runtime.PreviewTexture, size, new(0, 1), new(1, 0));
                if (Runtime.Guides)
                {
                    var draw = ImGui.GetWindowDrawList();
                    for (int i = 1; i <= 2; i++) { draw.AddLine(start + new Vector2(size.X * i / 3, 0), start + new Vector2(size.X * i / 3, size.Y), 0x88ffffff); draw.AddLine(start + new Vector2(0, size.Y * i / 3), start + new Vector2(size.X, size.Y * i / 3), 0x88ffffff); }
                }
            }
            else ImGui.TextWrapped(Runtime.Status);
        }
        ImGui.End(); Runtime.Preview = open;
    }
}
