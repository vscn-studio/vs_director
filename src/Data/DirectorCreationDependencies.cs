namespace VSDirector.Data;

public sealed partial class DirectorCreationProject
{
    public void SetBackground(Guid replay, Guid clip = default, double offset = 0)
    {
        BackgroundReplayId = replay;
        BackgroundClipId = replay == Guid.Empty ? Guid.Empty : clip;
        BackgroundOffset = replay == Guid.Empty ? 0 : offset;
    }

    public void NormalizeBackground()
    {
        // A manual replay link has no clip. Only discard a dangling recording link.
        if (BackgroundReplayId == Guid.Empty || BackgroundClipId != Guid.Empty
            && !Clips.Any(c => c.Id == BackgroundClipId && c.PerformanceAssetId != Guid.Empty))
            SetBackground(Guid.Empty);
    }
}

public sealed record CreationResourceDependency(string Name, string Source, bool GameTexture = false, Guid Performance = default);

/// <summary>Resources consumed by the scene, independent of unused library imports.</summary>
public static class DirectorCreationDependencies
{
    public static IEnumerable<CreationClip> PerformanceClips(DirectorCreationProject project)
        => project.Clips.Where(c => c.PerformanceAssetId != Guid.Empty
            && (c.Enabled && c.Start < project.Duration || c.Id == project.BackgroundClipId));

    public static IReadOnlyList<CreationResourceDependency> Collect(DirectorCreationProject project)
    {
        var result = new List<CreationResourceDependency>();
        var assets = project.Assets.ToDictionary(a => a.Id);
        void Asset(Guid id, string owner)
        {
            if (assets.TryGetValue(id, out var asset))
                result.Add(new(owner + " / " + asset.Name, asset.Source, Performance: asset.Kind == CreationAssetKind.Performance ? id : Guid.Empty));
        }
        // Include hidden objects and bones: visibility can be animated later.
        foreach (var obj in project.Objects)
            if (obj.Kind == CreationObjectKind.Image) Asset(obj.AssetId, obj.Name);
        var models = project.Objects.Where(o => o.Kind == CreationObjectKind.Model).Select(o => o.ModelId).ToHashSet();
        foreach (var model in project.Models.Where(m => models.Contains(m.Id)))
        {
            var slots = model.Parts.SelectMany(p => p.Faces).Select(f => DirectorCreationShape.S((System.Text.Json.Nodes.JsonObject)f.Value!, "texture").TrimStart('#')).Distinct();
            foreach (string slot in slots)
                if (model.Textures.TryGetValue(slot, out string? source) && source.Length > 0)
                    result.Add(new(model.Name + " / " + slot, source, !source.StartsWith("assets/", StringComparison.Ordinal)));
        }
        foreach (var clip in project.Clips.Where(c => c.Enabled && c.Start < project.Duration && c.AudioAssetId != Guid.Empty))
            Asset(clip.AudioAssetId, clip.Name);
        foreach (var clip in PerformanceClips(project))
        {
            Asset(clip.PerformanceAssetId, clip.Name);
            if (project.UsePerformanceWorld && project.BackgroundReplayId == Guid.Empty
                && project.Performances.TryGetValue(clip.PerformanceAssetId, out var take) && take.WorldSource.Length > 0)
                result.Add(new(clip.Name, take.WorldSource));
        }
        return result.Distinct().ToArray();
    }
}
