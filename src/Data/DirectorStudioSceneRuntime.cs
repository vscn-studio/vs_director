namespace VSDirector.Data;

/// <summary>Owns shared model resources while scene instances keep independent transforms.</summary>
public sealed class DirectorStudioSceneRuntime<T> : IDisposable where T : class, IDisposable
{
    private sealed record Model(T? Resource, string? Error);
    private readonly Func<DirectorStudioProject, DirectorStudioSceneObject, T> load;
    private readonly Dictionary<string, Model> models = new();
    private DirectorStudioSceneObject[] instances = [];
    private Guid projectId;
    public int ModelCount => models.Values.Count(m => m.Resource is not null);
    public int InstanceCount => instances.Length;
    public DirectorStudioSceneRuntime(Func<DirectorStudioProject, DirectorStudioSceneObject, T> load) => this.load = load;
    private static string Key(DirectorStudioSceneObject item) => (item.ResourceLink.Length > 0 ? item.ResourceLink : item.AssetId.ToString())
        + (item.Actions.Count > 0 ? "/actor/" + item.Id : "");
    public string? Error(DirectorStudioSceneObject item) => models.GetValueOrDefault(Key(item))?.Error;
    public void Synchronize(DirectorStudioProject? project)
    {
        if (project is null) { Clear(); return; }
        if (projectId != project.Id) { Clear(); projectId = project.Id; }
        instances = project.SceneObjects.Select(i => i.DeepClone()).ToArray();
        var used = instances.Select(Key).ToHashSet();
        foreach (string key in models.Keys.Where(k => !used.Contains(k)).ToArray())
        { models[key].Resource?.Dispose(); models.Remove(key); }
        foreach (var item in instances)
        {
            string key = Key(item);
            if (models.ContainsKey(key)) continue;
            try { models[key] = new(load(project, item), null); }
            catch (Exception error) { models[key] = new(null, error.GetBaseException().Message); }
        }
    }
    public void Draw(Action<DirectorStudioSceneObject, T> draw, Action<DirectorStudioSceneObject, Exception>? onError = null)
    {
        foreach (var item in instances)
        {
            string key = Key(item);
            if (!item.Visible || models[key] is not { Resource: { } resource, Error: null }) continue;
            try { draw(item, resource); }
            catch (Exception error)
            {
                // Keep the resource alive until normal cleanup, but stop retrying
                // the broken model each frame. Its error is visible in properties.
                models[key] = new(resource, error.GetBaseException().Message);
                onError?.Invoke(item, error);
            }
        }
    }
    public void Clear()
    {
        foreach (var model in models.Values) model.Resource?.Dispose();
        models.Clear(); instances = []; projectId = Guid.Empty;
    }
    public void Dispose() => Clear();
}
