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
            const string worldSource = "recordings/take.world.vsdreplay.gz";
            File.WriteAllText(store.AssetPath(p.Id, used.Source), JsonSerializer.Serialize(new CreationPerformance { WorldSource = worldSource }, CreationJson.Options));
            store.LoadPerformances(p, retry: true);
            Check(p.Performances.ContainsKey(used.Id) && store.PerformanceError(p, used.Source) is null, "Repaired recording did not reload");
            Check(p.Performances[used.Id].WorldSource == worldSource, "Project-owned recording reference was lost on load");
            Check(!DirectorCreationDependencies.Collect(p).Any(d => d.Source == worldSource),
                "An explicit replay background required an unused project world recording");
            p.SetBackground(Guid.Empty);
            Check(DirectorCreationDependencies.Collect(p).Any(d => d.Source == worldSource),
                "Export dependencies omitted the performance's world recording");
            p.UsePerformanceWorld = false;
            var liveDependencies = DirectorCreationDependencies.Collect(p);
            Check(!liveDependencies.Any(d => d.Source == worldSource)
                && liveDependencies.Any(d => d.Performance == used.Id),
                "Live-world export required an unused recorded world or omitted its actor performance");
            p.UsePerformanceWorld = true;
            p.SetBackground(replay, clip.Id, 2);
            var legacy = (CreationPerformance?)JsonSerializer.Deserialize("{}", typeof(CreationPerformance), CreationJson.Options);
            Check(legacy is not null && legacy.WorldSource == "", "Legacy performances require a new recording reference");
            var timedClip = new CreationClip { Start = 10, Duration = 8, SourceStart = 2, Speed = 2 };
            Check(DirectorCreationTiming.ClipSourceTime(timedClip, 11, 20) == 4,
                "Trimmed performance clock ignored the clip's offset or speed");
            Check(DirectorCreationTiming.ClipSourceTime(timedClip, 0, 20) == 2
                && DirectorCreationTiming.ClipSourceTime(timedClip, 100, 20) == 18,
                "Performance clock escaped the clip's time range");
            timedClip.Loop = true;
            Check(DirectorCreationTiming.ClipSourceTime(timedClip, 13, 6) == 2,
                "Looped performance clock did not wrap at the source duration");
            var worldProject = p.Clone();
            worldProject.SetBackground(Guid.Empty);
            var worldClip = worldProject.Clips.First(c => c.PerformanceAssetId == used.Id);
            worldClip.Start = 2; worldClip.Duration = 4; worldClip.SourceStart = 1; worldClip.Speed = 2;
            worldProject.Performances[used.Id] = new CreationPerformance
            {
                WorldSource = worldSource,
                Action = new() { Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 20 }] }] }
            };
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 0)?.SourceTime == 1,
                "Before a take, world preview did not preserve its trimmed baseline");
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 3)?.SourceTime == 3,
                "World preview did not share the actor's trimmed and scaled source clock");
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 8)?.SourceTime == 9,
                "A gap reverted the world preview to live reality instead of the take's ending state");
            worldProject.UsePerformanceWorld = false;
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 3) is null
                && !worldProject.Clone().UsePerformanceWorld, "Live-world selection was ignored or lost in history");
            worldProject.UsePerformanceWorld = true;
            worldProject.SetBackground(replay);
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 3) is null,
                "An explicitly linked background competed with project-owned world playback");
            worldProject.SetBackground(Guid.Empty);
            worldClip.Enabled = false;
            Check(DirectorCreationWorldTimeline.Resolve(worldProject, 3) is null,
                "A disabled performance continued owning the preview world");
            worldClip.Enabled = true;
            string originalWorld = store.AssetPath(worldProject.Id, worldSource);
            Directory.CreateDirectory(Path.GetDirectoryName(originalWorld)!);
            File.WriteAllText(originalWorld, "world-recording-copy-fixture");
            var copiedProject = store.Duplicate(worldProject);
            Check(File.ReadAllText(store.AssetPath(copiedProject.Id, worldSource)) == "world-recording-copy-fixture",
                "Duplicating a project omitted the performance's recorded world");
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
