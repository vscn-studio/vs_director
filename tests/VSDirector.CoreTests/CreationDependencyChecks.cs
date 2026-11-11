using System.Text.Json;
using VSDirector.Data;

internal static class CreationDependencyChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-dependencies-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DirectorCreationStore(root);
            var used = new CreationAsset { Name = "Used", Kind = CreationAssetKind.Performance, Source = "assets/used.json" };
            var unused = new CreationAsset { Name = "Unused", Kind = CreationAssetKind.Performance, Source = "assets/unused.json" };
            var audio = new CreationAsset { Kind = CreationAssetKind.Audio, Source = "assets/audio.ogg" };
            var part = new CreationPart();
            var model = new CreationModel { Parts = [part], Textures = new() { ["all"] = "assets/skin.png", ["unused"] = "assets/unused.png" } };
            var actor = new CreationSceneObject { ModelId = model.Id, Visible = false };
            var clip = new CreationClip { ObjectId = actor.Id, PerformanceAssetId = used.Id };
            var p = new DirectorCreationProject { Assets = [used, unused, audio], Models = [model], Objects = [actor], Clips = [clip, new() { AudioAssetId = audio.Id, Enabled = false }] };
            var replay = Guid.NewGuid();
            p.SetBackground(replay, clip.Id, 2);
            var session = new DirectorCreationSession(store); session.Open(p);
            Check(store.PerformanceError(p, used.Source) is not null, "Missing used recording was not reported");
            Check(store.PerformanceError(p, unused.Source) is null, "Unused recording was loaded");
            var deps = DirectorCreationDependencies.Collect(p);
            Check(deps.Any(d => d.Source == "assets/skin.png"), "Hidden animated actor's texture was excluded");
            Check(deps.Any(d => d.Performance == used.Id), "Linked recording dependency was omitted");
            Check(!deps.Any(d => d.Source == unused.Source || d.Source == audio.Source || d.Source == "assets/unused.png"), "Unused resources block export");
            Directory.CreateDirectory(Path.GetDirectoryName(store.AssetPath(p.Id, used.Source))!);
            File.WriteAllText(store.AssetPath(p.Id, used.Source), "null");
            store.LoadPerformances(p, retry: true);
            Check(store.PerformanceError(p, used.Source) is not null, "Invalid recording was accepted");
            File.WriteAllText(store.AssetPath(p.Id, used.Source), JsonSerializer.Serialize(new CreationPerformance(), CreationJson.Options));
            store.LoadPerformances(p, retry: true);
            Check(p.Performances.ContainsKey(used.Id) && store.PerformanceError(p, used.Source) is null, "Repaired recording did not reload");
            session.Edit("delete linked clip", project => project.Clips.RemoveAt(0));
            Check(session.Project!.BackgroundReplayId == Guid.Empty && session.Project.BackgroundClipId == Guid.Empty && session.Project.BackgroundOffset == 0, "Deleted clip left a stale background");
            session.Undo();
            Check(session.Project!.BackgroundReplayId == replay && session.Project.BackgroundClipId == clip.Id && session.Project.BackgroundOffset == 2, "Undo failed to restore the recording link");
            session.Redo();
            session.Edit("manual background", project => project.SetBackground(replay, offset: 3));
            Check(session.Project!.BackgroundReplayId == replay && session.Project.BackgroundOffset == 3, "Manual replay link was discarded");
            session.Edit("live world", project => project.SetBackground(Guid.Empty));
            Check(session.Project!.BackgroundClipId == Guid.Empty && session.Project.BackgroundOffset == 0, "Live world retained clip timing");
            p.BackgroundReplayId = replay; p.BackgroundClipId = Guid.NewGuid(); p.Validate();
            Check(p.BackgroundReplayId == Guid.Empty, "Legacy dangling link was not normalized");
            p.SetBackground(replay); p.Validate();
            Check(p.BackgroundReplayId == replay, "Unavailable manual replay was silently discarded");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
