namespace VSDirector.Data;

/// Replaces one actor's appearance while retaining its authored bone references.
public static class DirectorCreationAppearance
{
    public static Dictionary<Guid, Guid> Apply(DirectorCreationProject project,
        CreationSceneObject actor, CreationModel captured)
    {
        var original = project.Models.First(m => m.Id == actor.ModelId);
        var replacement = CreationJson.Clone(captured);
        replacement.Id = Guid.NewGuid();
        replacement.Name = original.Name;
        replacement.Folder = original.Folder;
        replacement.AnimationVersion = original.AnimationVersion;
        replacement.BodyMapping = new(original.BodyMapping);
        replacement.RigEditing = CreationJson.Clone(original.RigEditing);

        static string PathOf(CreationPart part, Dictionary<Guid, CreationPart> parts)
        {
            var path = new Stack<string>();
            var visited = new HashSet<Guid>();
            while (visited.Add(part.Id))
            {
                path.Push(part.Name.Length + ":" + part.Name);
                if (!parts.TryGetValue(part.ParentId, out var parent)) break;
                part = parent;
            }
            return string.Join("/", path);
        }
        var oldParts = original.Parts.ToDictionary(p => p.Id);
        var newParts = replacement.Parts.ToDictionary(p => p.Id);
        var paths = original.Parts.GroupBy(p => PathOf(p, oldParts))
            .ToDictionary(g => g.Key, g => new Queue<Guid>(g.Select(p => p.Id)));
        var remap = new Dictionary<Guid, Guid>();
        foreach (var part in replacement.Parts)
            remap[part.Id] = paths.TryGetValue(PathOf(part, newParts), out var candidates) && candidates.Count > 0
                ? candidates.Dequeue() : part.Id;
        foreach (var part in replacement.Parts)
        {
            part.ParentId = remap.GetValueOrDefault(part.ParentId, part.ParentId);
            part.Id = remap[part.Id];
        }
        var retained = replacement.Parts.Select(p => p.Id).ToHashSet();
        foreach (var previous in original.Parts.Where(p => !retained.Contains(p.Id)))
        {
            // Removed garments no longer draw. Their transform nodes remain so
            // old poses, recordings, presets and attachment references stay valid.
            var anchor = CreationJson.Clone(previous);
            anchor.Group = true; anchor.Faces = new(); anchor.Visible = true;
            replacement.Parts.Add(anchor);
        }
        var resolved = replacement.Parts.ToDictionary(p => p.Id);
        replacement.RigEditing.Seams.RemoveAll(s => resolved[s.Parent].Group
            || resolved[s.Child].Group || resolved[s.Child].ParentId != s.Parent);

        // Isolate both the model and its actions. Other actors using the previous
        // model must keep their own clothing, animation edits and clip sources.
        var animations = new Dictionary<Guid, Guid>();
        foreach (var source in project.Animations.Where(a => a.ModelId == original.Id).ToArray())
        {
            var copy = CreationJson.Clone(source);
            copy.Id = Guid.NewGuid(); copy.ModelId = replacement.Id;
            foreach (var track in copy.Tracks)
            {
                track.Id = Guid.NewGuid();
                foreach (var key in track.Keys) key.Id = Guid.NewGuid();
            }
            project.Animations.Add(copy); animations[source.Id] = copy.Id;
        }
        project.Models.Add(replacement);
        actor.ModelId = replacement.Id;
        foreach (var clip in project.Clips.Where(c => c.ObjectId == actor.Id))
            if (animations.TryGetValue(clip.AnimationId, out var animation)) clip.AnimationId = animation;
        return animations;
    }
}
