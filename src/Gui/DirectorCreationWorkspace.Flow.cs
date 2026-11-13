using static VSDirector.Gui.DirectorCreationText;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private string auxiliary = "";
    private Action? pendingTransition;
    private bool pendingPrompt;
    internal Action? LeaveRequested;
    internal void Enter()
    { Active = true; host.SetCreationMode(true); Runtime.Enabled = Project is not null; }
    internal void Exit()
    { PersistEditorPreferences(); Runtime.ClosePlayerInventory(); Runtime.Suspend(); Active = false; host.CreationPreviewVisible = false; host.SetCreationMode(false); }
    internal void RequestLeave()
    {
        if (pendingTransition is not null) return;
        if (Runtime.Exporting) { error = L("creation-finish-or-cancel-the-export-first"); return; }
        Continue(() => LeaveRequested?.Invoke());
    }
    internal void RequestEscape()
    {
        if (Runtime.NativeAppearanceEditorOpen) { Runtime.ClosePlayerInventory(); return; }
        if(gizmoUsing || draggedKey!=Guid.Empty || draggedClip!=Guid.Empty || scenePoseDrag!=Guid.Empty || draggingPose is not null)
        { Session.CancelGesture();gizmoUsing=false;handleAxis=-1;draggedKey=draggedClip=scenePoseDrag=Guid.Empty;draggingPose=null;return; }
        if (Runtime.PerformanceActive) { Runtime.StopPerformance(); return; }
        if (pendingTransition is not null) { ResolvePendingTransition(null); return; }
        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        if (auxiliary.Length > 0) { auxiliary = ""; return; }
        if(PosePicking) { LeaveBodySelection();return; }
        RequestLeave();
    }
    private void Continue(Action action)
    {
        if (Runtime.PerformanceActive) { error = L("creation-stop-performance-first"); return; }
        if (pendingTransition is not null) return;
        if (Session.HasTransformDraft) { pendingTransition = action; pendingPrompt = true; }
        else Run(action);
    }
    private void DrawPendingTransition()
    {
        if (pendingPrompt) { ImGui.OpenPopup("###creation-pending"); pendingPrompt = false; }
        var display = ImGui.GetIO().DisplaySize;
        float width = Math.Max(1, Math.Min(480 * DirectorImGuiTheme.Scale, display.X - 24));
        // Wrapped text and width-clamped buttons cannot determine an auto-sized
        // window's width: keep width explicit and let only the height fit content.
        ImGui.SetNextWindowSize(new(width, 0));
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, Math.Max(1, display.Y - 24)));
        ImGui.SetNextWindowPos(display * .5f, ImGuiCond.Appearing, new(.5f));
        bool open = true;
        bool visible = ImGui.BeginPopupModal(L("creation-record-preview") + "###creation-pending", ref open,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
        // The titlebar close button makes BeginPopupModal return false in the
        // same frame. Release the pending action before that early return.
        if (!open || !visible && !ImGui.IsPopupOpen("###creation-pending")) ResolvePendingTransition(null);
        if (!visible) return;
        ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
        try
        {
            if (pendingTransition is null || ImGui.IsKeyPressed(ImGuiKey.Escape))
            { ResolvePendingTransition(null); ImGui.CloseCurrentPopup(); return; }
            ImGui.TextWrapped(L("creation-unrecorded"));
            void Finish(bool record)
            {
                if (ResolvePendingTransition(record)) ImGui.CloseCurrentPopup();
            }
            if (Button(L("creation-record-continue"))) Finish(true);
            SameLineIfFits(L("creation-revert-continue")); if (Button(L("creation-revert-continue"))) Finish(false);
            SameLineIfFits(L("creation-cancel")); if (Button(L("creation-cancel"))) { ResolvePendingTransition(null); ImGui.CloseCurrentPopup(); }
            if (error.Length > 0) ImGui.TextWrapped(error);
        }
        finally { ImGui.EndPopup(); }
    }
    private bool ResolvePendingTransition(bool? record)
    {
        if (record is null) { pendingTransition = null; pendingPrompt = false; return true; }
        bool completed = false;
        Run(() =>
        {
            if (record.Value && Session.HasTransformDraft) Session.CommitTransformDrafts();
            else Session.ClearTransformDrafts();
            var next = pendingTransition;
            pendingTransition = null; pendingPrompt = false;
            next?.Invoke(); completed = true;
        });
        return completed;
    }
    private System.Numerics.Vector2? menuAnchor;
    private void DrawToolbar()
    {
        float height = (float)DirectorWorkspacePanelLayout.ToolbarHeight * DirectorImGuiTheme.Scale;
        // Match the replay/settings toolbar's borderless child surface.
        ImGui.SetCursorScreenPos(System.Numerics.Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(8, 2) * DirectorImGuiTheme.Scale);
        // The replay toolbar sits over RenderWorkspaceBackdrop (RGB 36,36,36).
        // Creation's transparent host has no such backdrop.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0xff242424);
        bool visible = ImGui.BeginChild("vsdirector-creation-toolbar", new(ImGui.GetIO().DisplaySize.X, height), false,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        try
        {
            if (!visible) return;
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
            var min = ImGui.GetWindowPos();
            ImGui.GetWindowDrawList().AddLine(min, new(min.X + ImGui.GetWindowWidth(), min.Y), DirectorImGuiTheme.Border);
            void Menu(string name) { requestedMenu = name; menuAnchor = new(ImGui.GetItemRectMin().X, height); }
            var commands = new List<DirectorWorkspaceMenus.Command>
            {
                new("back", L("creation-back-workspace"), RequestLeave),
                new("project", L("creation-work-menu"), () => Menu("项目")),
                new("edit", L("creation-edit"), () => Menu("编辑")),
                new("add", L("creation-add-menu"), () => Menu("创建")),
                new("view", L("creation-view-menu"), () => Menu("窗口")),
                new("save", L("creation-save-project"), () => Continue(Session.Save), Project is not null && Runtime.ProjectWorldMatches),
                new("export", L("creation-export-menu"), () => auxiliary = "output", Project is not null && Runtime.ProjectWorldMatches)
            };
            ImGui.BeginDisabled(Runtime.PerformanceActive);
            DirectorWorkspaceMenus.Toolbar(commands).Draw();
            ImGui.EndDisabled();
        }
        finally { ImGui.EndChild(); }
    }
    private static void ConstrainMenu()
    {
        float width = Math.Min(380 * DirectorImGuiTheme.Scale, ImGui.GetIO().DisplaySize.X - 16);
        ImGui.SetNextWindowSizeConstraints(new(Math.Min(180 * DirectorImGuiTheme.Scale, width), 0), new(width, Math.Max(100, ImGui.GetIO().DisplaySize.Y - 48 * DirectorImGuiTheme.Scale)));
    }
    private void DrawAuxiliary()
    {
        if (auxiliary.Length == 0) return;
        if (!Runtime.ProjectWorldMatches && auxiliary != "projects") auxiliary = "projects";
        bool open = true;
        string title = auxiliary switch { "actors" => L("creation-native-actors"), "resources" => L("creation-resources"), "presets" => L("creation-presets"), "projects" => L("creation-projects"), "output" => L("creation-export-menu"), _ => L("creation-project-settings") };
        ImGui.SetNextWindowSize(new(540, 480), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(title + "###creation-library", ref open, ImGuiWindowFlags.NoDocking))
        {
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
            ImGui.BeginDisabled(Runtime.Exporting || Runtime.PerformanceActive);
            if (auxiliary is "resources" or "presets" or "projects")
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##library-search", L("creation-search-names"), ref search, 256);
                if (auxiliary == "projects") DrawProjects();
                else if (auxiliary == "resources") DrawAssets(); else DrawPresets();
            }
            else if (auxiliary == "actors") DrawActorLibrary();
            else DrawProjectSettings(auxiliary == "output");
            ImGui.EndDisabled();
            if (Runtime.Exporting && Button(L("creation-cancel-export"))) Runtime.CancelExport();
            if (error.Length > 0) ImGui.TextWrapped(error);
        }
        ImGui.End(); if (!open) auxiliary = "";
    }
    private void ReturnToProject() => Continue(() =>
    {
        var savedKeys=sceneContext?.Keys;
        SaveTimelineView(); Session.LeaveAnimation();modelContext="";clipId=keyId=trackId=Guid.Empty;editingSourceClip=Guid.Empty;timelineEditor=false;
        if(sceneContext is {} saved)
        {
            Session.SetEditTask(saved.Task);
            wholeObjectSelection=saved.WholeObject;
            if(saved.PoseActor!=Guid.Empty)Session.BeginScenePose(saved.PoseActor);
            Session.SelectedObject=saved.Object;Session.SelectedPart=saved.Part;Session.Seek(saved.Time);
            selectedObjects.Clear();selectedObjects.UnionWith(saved.Objects);
            selectedBones.Clear();selectedBones.UnionWith(saved.Bones);
            keyId=saved.Key;trackId=saved.Track;clipId=saved.Clip;sceneContext=null;
        }
        RestoreTimelineView();
        RestoreTool(PartScope);
        if(savedKeys is not null)selectedTimelineKeys.UnionWith(savedKeys);
    });
    private void EditClipAnimation(Guid id) => Continue(() =>
    {
        var clip = Project!.Clips.First(c => c.Id == id);
        RememberSceneContext();editingSourceClip=id;
        var animation = clip.PerformanceAssetId != Guid.Empty ? Runtime.BakePerformance(clip)
            : Project.Animations.First(a => a.Id == clip.AnimationId);
        double sourceTime = clip.SourceStart + Math.Clamp(Session.Time - clip.Start, 0, clip.Duration) * clip.Speed;
        if (clip.Loop || animation.Loop) sourceTime %= Math.Max(1e-6, animation.Duration);
        Session.EnterAnimation(animation.Id, clip.ObjectId); Session.Seek(sourceTime);
        wholeObjectSelection=false;modelContext = ""; clipId = keyId = trackId = Guid.Empty; timelineEditor = false;
        timeline = true; RestoreTimelineView();
    });
    private void EditAnimation(CreationAnimation animation) => Continue(() =>
    {
        if (SelectedObject is not { } obj || obj.ModelId != animation.ModelId) throw new InvalidOperationException(L("creation-data-the-animation-and-actor-use-different-models"));
        RememberSceneContext(); wholeObjectSelection=false;editingSourceClip=Guid.Empty;modelContext = ""; Session.EnterAnimation(animation.Id, obj.Id); timeline = true; clipId = keyId = trackId = Guid.Empty; auxiliary = "";RestoreTimelineView();
    });
    private void AddAnimationClip(CreationAnimation animation)
    {
        if (SelectedObject is not { } obj || obj.ModelId != animation.ModelId) throw new InvalidOperationException(L("creation-data-the-animation-and-actor-use-different-models"));
        if (animation.PoseEditing)
        {
            Session.Edit(L("creation-add-animation-clip"), p =>
            {
                bool extendLoop = p.LoopEnd >= p.Duration;
                p.Duration = Math.Max(p.Duration, Session.Time + animation.Duration);
                if (extendLoop) p.LoopEnd = p.Duration;
                p.Clips.Add(new() { Name = animation.Name, ObjectId = obj.Id, AnimationId = animation.Id, Start = Session.Time, Duration = animation.Duration, Loop = animation.Loop });
            });
            return;
        }
        if (Session.Time >= Project!.Duration) throw new InvalidOperationException(L("creation-insert-end-error"));
        Session.Edit(L("creation-add-animation-clip"), p => p.Clips.Add(new() { Name = animation.Name, ObjectId = obj.Id, AnimationId = animation.Id, Start = Session.Time, Duration = Math.Min(animation.Duration, Math.Max(DirectorCreationTiming.FrameDuration, p.Duration - Session.Time)), Loop = animation.Loop }));
    }
    private void ExportProject(string path)
    {
        Session.LeaveAnimation();
        Runtime.Export(path);
    }
}
