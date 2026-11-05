using VSDirector.Data;

internal static class StudioProjectChecks
{
    public static void SharedResources()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-studio-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "model.json");
            File.WriteAllText(source, "{\"elements\":[]}");
            var provider = new DirectorStudioResourceProvider(Path.Combine(root, "Studio"));
            DirectorStudioResourceLink link = provider.Import(source);
            Check.Equal("studio:models/model.json", link.ToString());
            File.Delete(source);
            Check.Equal("{\"elements\":[]}", System.Text.Encoding.UTF8.GetString(provider.Read(link)));
            Check.True(provider.List().Any(item => item.Link == link && item.Kind == DirectorStudioAssetKind.Model));
            Check.True(provider.List().Any(item => item.Link.Source == "builtin"));
            Check.True(provider.Read(DirectorStudioResourceLink.Parse("builtin:models/cube.json")).Length > 0);
            Check.Throws<InvalidDataException>(() => DirectorStudioResourceLink.Parse("studio:../outside"));
            Check.Throws<InvalidDataException>(() => DirectorStudioResourceLink.Parse("studio:/outside"));
            Check.Throws<FileNotFoundException>(() => provider.Read(DirectorStudioResourceLink.Parse("unknown:model.json")));

            string external = Path.Combine(root, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "sound.wav"), "wave");
            string prefix = provider.AttachExternal(external);
            Check.Equal("external1", prefix);
            var reloaded = new DirectorStudioResourceProvider(Path.Combine(root, "Studio"));
            Check.True(reloaded.List().Any(item => item.Link.ToString() == "external1:sound.wav"));
            Check.Equal("wave", System.Text.Encoding.UTF8.GetString(reloaded.Read(
                DirectorStudioResourceLink.Parse("external1:sound.wav"))));
            Check.True(reloaded.List().Any(item => item.Link == link));
            string offline = external + "-offline";
            Directory.Move(external, offline);
            var offlineProvider = new DirectorStudioResourceProvider(Path.Combine(root, "Studio"));
            Check.True(offlineProvider.SourceNames.Contains("external1"));
            Check.False(offlineProvider.List().Any(item => item.Link.Source == "external1"));
            Directory.Move(offline, external);
            Check.Throws<ArgumentException>(() => DirectorStudioResourceExtensions.RegisterSource("BUILTIN", external));
            Check.Throws<InvalidOperationException>(() => provider.RegisterSource("BUILTIN", external));
            string addon = "addon" + Guid.NewGuid().ToString("N")[..8];
            DirectorStudioResourceExtensions.RegisterSource(addon, external);
            DirectorStudioResourceExtensions.RegisterImporter(new StudioTestImporter());
            var extended = new DirectorStudioResourceProvider(Path.Combine(root, "Studio"));
            Check.True(extended.List().Any(item => item.Link.ToString() == addon + ":sound.wav"));
            string custom = Path.Combine(root, "custom.studioaudio");
            File.WriteAllText(custom, "addon audio");
            DirectorStudioResourceLink customLink = extended.Import(custom);
            Check.Equal("studio:audio/custom.studioaudio", customLink.ToString());
            Check.Equal("addon audio", System.Text.Encoding.UTF8.GetString(extended.Read(customLink)));

            DirectorStudioProject film = new() { Name = "Film" };
            film.SceneObjects.Add(new DirectorStudioSceneObject { ResourceLink = link.ToString(), Name = "Model" });
            string projectPath = Path.Combine(root, "film.vstudio");
            DirectorStudioProjectIO.Save(projectPath, film);
            DirectorStudioProject loaded = DirectorStudioProjectIO.Load(projectPath);
            Check.Equal(0, loaded.Assets.Count);
            Check.Equal(link.ToString(), loaded.SceneObjects[0].ResourceLink);
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Save(projectPath,
                new DirectorStudioProject { SceneObjects = [new DirectorStudioSceneObject { ResourceLink = "studio:../escape" }] }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class StudioTestImporter : IDirectorStudioImporter
    {
        public bool CanImport(string extension) => extension == ".studioaudio";
        public DirectorStudioAssetKind Kind(string path) => DirectorStudioAssetKind.Audio;
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-studio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "actor.json");
            File.WriteAllText(source, "{\"elements\":[],\"textures\":{}}");
            DirectorStudioAsset asset = DirectorStudioProject.ImportFile(source);
            Check.Equal(DirectorStudioAssetKind.Model, asset.Kind);
            Check.Equal("actor.json", asset.SourceName);

            DirectorStudioProject first = new() { Name = "First" };
            first.Assets.Add(asset);
            first.SceneObjects.Add(new DirectorStudioSceneObject { AssetId = asset.Id, Name = "Actor" });
            DirectorStudioProject second = new() { Name = "Second" };
            string firstPath = Path.Combine(root, "first.vstudio");
            string secondPath = Path.Combine(root, "second.vstudio");
            DirectorStudioProjectIO.Save(firstPath, first, overwrite: false);
            DirectorStudioProjectIO.Save(secondPath, second, overwrite: false);
            File.Delete(source);

            DirectorStudioProject loadedFirst = DirectorStudioProjectIO.Load(firstPath);
            DirectorStudioProject loadedSecond = DirectorStudioProjectIO.Load(secondPath);
            Check.Equal(first.Id, loadedFirst.Id);
            Check.Equal(1, loadedFirst.Assets.Count);
            Check.SequenceEqual(asset.Content, loadedFirst.Assets[0].Content);
            Check.Equal(1, loadedFirst.SceneObjects.Count);
            string legacy = File.ReadAllText(firstPath).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);
            string legacyPath = Path.Combine(root, "legacy.vstudio");
            File.WriteAllText(legacyPath, legacy);
            Check.Equal(asset.Id, DirectorStudioProjectIO.Load(legacyPath).SceneObjects[0].AssetId);
            Check.Equal(0, loadedSecond.Assets.Count);
            Check.Equal(0, loadedSecond.SceneObjects.Count);
            Check.False(File.ReadAllText(firstPath).Contains(source, StringComparison.OrdinalIgnoreCase));
            Check.Throws<IOException>(() => DirectorStudioProjectIO.Save(secondPath, first, overwrite: false));
            Check.Equal(second.Id, DirectorStudioProjectIO.Load(secondPath).Id);
            Check.Empty(Directory.GetFiles(root, "*.tmp"));

            DirectorStudioProject duplicate = loadedFirst.DeepClone();
            duplicate.Assets.Add(duplicate.Assets[0].DeepClone());
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Save(firstPath, duplicate));
            DirectorStudioProject missing = loadedFirst.DeepClone();
            missing.SceneObjects[0].AssetId = Guid.NewGuid();
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Save(firstPath, missing));
            DirectorStudioProject oversized = loadedFirst.DeepClone();
            oversized.SceneObjects = Enumerable.Range(0, DirectorStudioProject.MaximumSceneObjects + 1)
                .Select(_ => new DirectorStudioSceneObject { AssetId = asset.Id }).ToList();
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Save(firstPath, oversized));
            Check.Equal(first.Id, DirectorStudioProjectIO.Load(firstPath).Id);

            string original = File.ReadAllText(firstPath);
            string corrupt = original.Replace(asset.Sha256, new string('0', 64), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(root, "corrupt.vstudio"), corrupt);
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Load(Path.Combine(root, "corrupt.vstudio")));
            File.WriteAllText(Path.Combine(root, "future.vstudio"), "{\"schemaVersion\":999}");
            Check.Throws<NotSupportedException>(() => DirectorStudioProjectIO.Load(Path.Combine(root, "future.vstudio")));
            Check.Throws<InvalidDataException>(() => DirectorStudioProjectIO.Load(Path.Combine(root, "first.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
