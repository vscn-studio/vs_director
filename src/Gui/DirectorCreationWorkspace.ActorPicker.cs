using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using VSDirector.Rendering;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private sealed record ActorPickerEntry(string Id, string Name, string Code, long Entity, string Category, Action Import, Action? Record = null);
    private readonly Dictionary<string, ActorPickerPreview> actorPickerPreviews = new();
    private string actorPickerSelected = "", actorPickerCategory = "";
    private int actorPickerCreated;
    private void OpenActorPicker()
    {
        actorPickerSelected = actorPickerCategory = search = error = "";
        ClearActorPickerPreviews();
        auxiliary = "actors";
    }

    private void DrawActorLibrary()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##actor-search", L("creation-search-names"), ref search, 256)) actorPickerCategory = "";
        var entries = new List<ActorPickerEntry>();
        foreach (var actor in Runtime.WorldActors())
        {
            var source = actor;
            entries.Add(new("world/" + source.Id, source.Name, "", source.Id, "world", () =>
                { Select(Runtime.AddNativeActor("", source.Id)); auxiliary = ""; },
                () => { Runtime.RecordWorldActor(source.Id); auxiliary = ""; }));
        }
        var replay = VSDirector.Replay.DirectorReplayRuntime.Manager;
        if (replay is { IsPlaying: true }) foreach (var source in replay.ActorPathSources)
        {
            var selectedSource = source;
            entries.Add(new("replay/" + source.ActorKey, source.DisplayName, source.EntityCode,
                replay.ResolveReplayActor(source.ActorKey, null, 0)?.EntityId ?? 0, "replay", () =>
            {
                if (!replay.TryBuildAction(selectedSource.ActorKey, replayImportStart, replayImportEnd, selectedSource.DisplayName, out var action, out _, out var message))
                    throw new InvalidOperationException(message);
                var entity = replay.ResolveReplayActor(selectedSource.ActorKey, null, 0);
                var actor = Runtime.AddNativeActor(selectedSource.EntityCode, entity?.EntityId ?? 0); Select(actor);
                Runtime.ImportPerformance(action, actor.Id, Session.Time, owner.ActiveReplayId, backgroundStart: replayImportStart); auxiliary = "";
            }));
        }
        foreach (var actor in Runtime.ActorResources(""))
        {
            var source = actor;
            entries.Add(new("native/" + source.Code, source.Name, source.Code, 0, "native/" + source.Code.Split(':')[0],
                () => { Select(Runtime.AddNativeActor(source.Code)); auxiliary = ""; }));
        }
        var filtered = entries.Where(e => search.Trim().Length == 0 || (e.Name + " " + e.Code).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        float height = PickerBodyHeight(), sidebar = PickerSidebarWidth();
        ImGui.BeginChild("actor-categories", new(sidebar, height), true);
        if (ImGui.Selectable(L("creation-all"), actorPickerCategory.Length == 0)) actorPickerCategory = "";
        foreach (var category in new[] { ("world", "creation-world-actors"), ("replay", "creation-replay-actors"), ("native", "creation-native-actors") })
        {
            if (!ImGui.TreeNodeEx(L(category.Item2) + "##" + category.Item1, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick)) continue;
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) actorPickerCategory = category.Item1;
            foreach (var group in filtered.Where(e => e.Category.Split('/')[0] == category.Item1).GroupBy(e => e.Category))
                if (ImGui.Selectable((category.Item1 == "native" ? group.Key.Split('/')[1] : L("creation-all")) + "##" + group.Key, actorPickerCategory == group.Key)) actorPickerCategory = group.Key;
            ImGui.TreePop();
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("actor-catalog", new(0, height), false);
        var work = Stopwatch.StartNew(); actorPickerCreated = 0;
        foreach (var category in filtered.Where(e => actorPickerCategory.Length == 0 || e.Category == actorPickerCategory || e.Category.StartsWith(actorPickerCategory + "/", StringComparison.Ordinal)).GroupBy(e => e.Category))
        {
            ImGui.PushID(category.Key);
            string label = category.Key == "world" ? L("creation-world-actors") : category.Key == "replay" ? L("creation-replay-actors") : L("creation-native-actors") + " / " + category.Key.Split('/')[1];
            if (Header(label))
            {
                if (category.Key == "replay" && replay is not null)
                {
                    Number(L("creation-replay-start"), replayImportStart, v => replayImportStart = v, 0, replay.PlaybackDurationSeconds);
                    Number(L("creation-replay-end"), replayImportEnd, v => replayImportEnd = v, 0, replay.PlaybackDurationSeconds);
                }
                bool grid = BeginPickerGrid("actor-grid");
                if (grid) foreach (var entry in category) { ImGui.TableNextColumn(); DrawActorPickerCard(entry, work); }
                EndPickerGrid(grid);
            }
            ImGui.PopID();
        }
        if (filtered.Length == 0) Hint(L("creation-picker-empty"));
        ImGui.EndChild();
        foreach (var key in actorPickerPreviews.Where(p => p.Value.LastFrame != ImGui.GetFrameCount()).OrderByDescending(p => p.Value.LastFrame).Skip(48).Select(p => p.Key).ToArray())
        { actorPickerPreviews[key].Dispose(); actorPickerPreviews.Remove(key); }
        var selected = entries.FirstOrDefault(e => e.Id == actorPickerSelected);
        EllipsizedParticleText(error.Length > 0 ? error : selected?.Name ?? L("creation-actor-pick-hint"), ImGui.GetContentRegionAvail().X);
        BeginPickerActions();
        ImGui.BeginDisabled(selected is null);
        if (PickerAction("creation-actor-import-selected", true)) Run(() => selected!.Import());
        ImGui.EndDisabled();
        ImGui.BeginDisabled(selected?.Record is null);
        if (PickerAction("creation-record-world-actor")) Run(() => selected!.Record!());
        ImGui.EndDisabled();
        if (PickerAction("creation-cancel")) auxiliary = "";
        EndPickerActions();
    }

    private void DrawActorPickerCard(ActorPickerEntry entry, Stopwatch work)
    {
        float size = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (!ImGui.IsRectVisible(new Vector2(size))) { ImGui.Dummy(new(size)); return; }
        Vector2 min = ImGui.GetCursorScreenPos(), max = min + new Vector2(size);
        ImGui.InvisibleButton("actor-card##" + entry.Id, new(size));
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) actorPickerSelected = entry.Id;
        if (!actorPickerPreviews.TryGetValue(entry.Id, out var preview) && actorPickerCreated == 0)
        {
            preview = new(); actorPickerPreviews[entry.Id] = preview; actorPickerCreated++;
            try { preview.SetMesh(Runtime.ActorPickerMesh(entry.Code, entry.Entity)); }
            catch (Exception e) { preview.Error = e.Message; }
        }
        var draw = ImGui.GetWindowDrawList();
        Vector2 previewMax = max - new Vector2(0, ImGui.GetTextLineHeightWithSpacing() + 8 * DirectorImGuiTheme.Scale);
        draw.PushClipRect(min, Vector2.Max(min, previewMax), true);
        if (preview is not null) { preview.LastFrame = ImGui.GetFrameCount(); preview.Draw(draw, min, previewMax); }
        if (preview is null || preview.Error.Length > 0) draw.AddText(min + new Vector2(6), ImGui.GetColorU32(ImGuiCol.TextDisabled), L(preview is null ? "creation-particle-preview-loading" : "creation-particle-preview-error"));
        draw.PopClipRect();
        PickerCardLabel(draw, min, max, previewMax.Y, entry.Name, actorPickerSelected == entry.Id, hovered);
        if (hovered)
        {
            DirectorImGuiTooltip.Begin(); ImGui.TextUnformatted(entry.Name); ImGui.TextUnformatted(entry.Code.Length == 0 ? entry.Id : entry.Code);
            if (preview?.Error.Length > 0) ImGui.TextWrapped(preview.Error);
            DirectorImGuiTooltip.End();
        }
    }

    private void ClearActorPickerPreviews()
    {
        foreach (var preview in actorPickerPreviews.Values) preview.Dispose();
        actorPickerPreviews.Clear();
    }

    private sealed class ActorPickerPreview : IDisposable
    {
        internal string Error = "";
        internal int LastFrame;
        private readonly DirectorPickerMeshPreview target = new();
        internal void SetMesh(MeshData mesh) => target.SetMesh(mesh);
        internal void Draw(ImDrawListPtr draw, Vector2 min, Vector2 max)
        {
            if (Error.Length > 0 || max.Y <= min.Y) return;
            try
            {
                target.Render(ImGui.GetTime(), (max.X-min.X)/(max.Y-min.Y));
                draw.AddImage((nint)target.Texture, min, max, new(0,1), new(1,0));
            }
            catch (Exception e) { Error = e.Message; }
        }
        public void Dispose() => target.Dispose();
    }
}
