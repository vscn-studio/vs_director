using VSDirector.Data;

internal static class CreationWorldScopeChecks
{
    internal static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Check(DirectorCreationWorldScope.Matches("world-a", 0, "world-a", 0), "Matching world rejected");
        Check(!DirectorCreationWorldScope.Matches("world-a", 0, "world-b", 0), "Other world accepted");
        Check(!DirectorCreationWorldScope.Matches("world-a", 1, "world-a", 0), "Other dimension accepted");
        Check(!DirectorCreationWorldScope.Matches("", 0, "world-a", 0), "Unbound legacy work opened implicitly");
        string root = Path.Combine(Path.GetTempPath(), "director-world-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DirectorCreationStore(root);
            var model = new CreationModel { Parts = [new()] };
            var actor = new CreationSceneObject { ModelId = model.Id, ReplacePlayerUid = "live-player",
                Transform = new() { Position = new(3, 4, 5) } };
            var source = new DirectorCreationProject { WorldId = "world-a", Dimension = 1,
                Origin = new(100, 3, 200), Models = [model], Objects = [actor], UsePerformanceWorld = true };
            source.SetBackground(Guid.NewGuid());
            var texture = new CreationAsset { Kind = CreationAssetKind.Texture, Source = "assets/skin.png" };
            source.Assets.Add(texture);
            model.Textures["all"] = texture.Source;
            Directory.CreateDirectory(Path.GetDirectoryName(store.AssetPath(source.Id, texture.Source))!);
            File.WriteAllBytes(store.AssetPath(source.Id, texture.Source), [1, 2, 3]);
            var performanceAsset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/take.json" };
            var take = new CreationPerformance { WorldSource = "recordings/take.vsdreplay.gz" };
            source.Assets.Add(performanceAsset);
            Directory.CreateDirectory(Path.GetDirectoryName(store.AssetPath(source.Id, take.WorldSource))!);
            File.WriteAllBytes(store.AssetPath(source.Id, take.WorldSource), [5, 6, 7]);
            File.WriteAllText(store.AssetPath(source.Id, performanceAsset.Source), System.Text.Json.JsonSerializer.Serialize(take, CreationJson.Options));
            store.Save(source);
            string original = File.ReadAllText(Path.Combine(store.DirectoryFor(source.Id), "project.json"));
            var entry = store.List().Single();
            Check(entry.WorldId == "world-a" && entry.Dimension == 1, "Library lost world metadata");
            var copy = store.CopyToWorld(source, "world-b", 0);
            var reopened = store.Load(copy.Id);
            Check(reopened.Id != source.Id && reopened.WorldId == "world-b" && reopened.Dimension == 0, "Copy retained old identity or binding");
            Check(reopened.Origin == source.Origin && reopened.Objects[0].Transform.Position == actor.Transform.Position, "Copy moved authored coordinates");
            Check(reopened.BackgroundReplayId == Guid.Empty && !reopened.UsePerformanceWorld
                && reopened.Objects[0].ReplacePlayerUid.Length == 0, "Copy retained automatic old-world effects");
            Check(File.ReadAllBytes(store.AssetPath(copy.Id, texture.Source)).SequenceEqual(new byte[] { 1, 2, 3 }), "Copy lost its texture");
            Check(File.ReadAllBytes(store.AssetPath(copy.Id, take.WorldSource)).SequenceEqual(new byte[] { 5, 6, 7 }), "Copy lost its local recording");
            Check(copy.Performances.Values.Single().WorldSource == take.WorldSource, "Copy disconnected its performance recording");
            Check(File.ReadAllText(Path.Combine(store.DirectoryFor(source.Id), "project.json")) == original
                && source.BackgroundReplayId != Guid.Empty && actor.ReplacePlayerUid == "live-player", "Copy modified original work");
            Check(store.List().Count(e => DirectorCreationWorldScope.Matches(e.WorldId, e.Dimension, "world-b", 0)) == 1, "World filtering includes unrelated works");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
