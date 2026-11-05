using VSDirector.Data;

internal static class StudioSceneChecks
{
    private sealed class Model : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    public static void Run()
    {
        var loaded = new List<Model>(); int attempts = 0;
        using var runtime = new DirectorStudioSceneRuntime<Model>((project, item) =>
        {
            attempts++;
            if (item.ResourceLink == "studio:missing.json") throw new FileNotFoundException("Missing model");
            var model = new Model(); loaded.Add(model); return model;
        });
        var first = new DirectorStudioSceneObject { ResourceLink = "builtin:models/cube.json", X = 12, Y = 20, Scale = 2 };
        var second = first.DeepClone(); second.Id = Guid.NewGuid(); second.X = 25;
        var project = new DirectorStudioProject { SceneObjects = [first, second] };
        runtime.Synchronize(project);
        Check.Equal(1, runtime.ModelCount); Check.Equal(2, runtime.InstanceCount); Check.Equal(1, attempts);
        var visible = new List<DirectorStudioSceneObject>(); runtime.Draw((item, _) => visible.Add(item));
        Check.Equal(12d, visible[0].X); Check.Equal(25d, visible[1].X); Check.Equal(2f, visible[1].Scale);
        second.Visible = false; first.Scale = 3; runtime.Synchronize(project);
        visible.Clear(); runtime.Draw((item, _) => visible.Add(item));
        Check.Equal(1, visible.Count); Check.Equal(3f, visible[0].Scale); Check.Equal(1, attempts);
        project.SceneObjects.Remove(first); runtime.Synchronize(project); Check.Equal(0, loaded[0].Disposals);
        project.SceneObjects.Clear(); runtime.Synchronize(project); Check.Equal(1, loaded[0].Disposals);
        project.SceneObjects.Add(first);
        project.SceneObjects.Add(new() { ResourceLink = "studio:missing.json" });
        runtime.Synchronize(project); int before = attempts;
        runtime.Synchronize(project); Check.Equal(before, attempts);
        Check.Equal("Missing model", runtime.Error(project.SceneObjects[1]));
        visible.Clear(); runtime.Draw((item, _) => visible.Add(item)); Check.Equal(1, visible.Count);
        var other = new DirectorStudioProject { SceneObjects = [second] }; runtime.Synchronize(other);
        Check.Equal(1, loaded[1].Disposals); runtime.Synchronize(null); Check.Equal(1, loaded[2].Disposals);
        runtime.Clear(); Check.True(loaded.All(m => m.Disposals == 1));

        string directory = Path.Combine(Path.GetTempPath(), "studio-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "scene.vstudio");
            DirectorStudioProjectIO.Save(path, project);
            var restored = DirectorStudioProjectIO.Load(path);
            Check.Equal(3f, restored.SceneObjects[0].Scale); Check.Equal(12d, restored.SceneObjects[0].X);
            restored.SceneObjects[0].Scale = float.NaN;
            Check.Equal(1f, DirectorStudioProject.Normalize(restored).SceneObjects[0].Scale);
            string legacy = File.ReadAllText(path).Replace("\"scale\": 3,", ""); File.WriteAllText(path, legacy);
            Check.Equal(1f, DirectorStudioProjectIO.Load(path).SceneObjects[0].Scale);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
