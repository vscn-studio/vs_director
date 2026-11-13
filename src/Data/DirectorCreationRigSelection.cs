using System.Runtime.CompilerServices;

namespace VSDirector.Data;

// Derived editor data, never serialized into a model or animation.
internal static class DirectorCreationRigSelection
{
    private sealed class Cache
    {
        internal int Stamp;
        internal Dictionary<Guid, Guid> Targets = new();
    }
    private static readonly ConditionalWeakTable<DirectorCreationProject, ConditionalWeakTable<CreationModel, Cache>> Projects = new();
    internal static IReadOnlyDictionary<Guid, Guid> Targets(DirectorCreationProject project, CreationModel model)
    {
        var hash = new HashCode();
        foreach(var part in model.Parts){hash.Add(part.Id);hash.Add(part.ParentId);hash.Add(part.Origin);hash.Add(part.Group);}
        foreach(var pair in model.BodyMapping){hash.Add(pair.Key);hash.Add(pair.Value);}
        foreach(var action in project.Animations.Where(a=>a.ModelId==model.Id&&!a.PoseEditing))
            foreach(var track in action.Tracks){hash.Add(track.PartId);hash.Add(track.Channel);}
        var cache=Projects.GetOrCreateValue(project).GetOrCreateValue(model);int stamp=hash.ToHashCode();
        if(cache.Stamp==stamp&&cache.Targets.Count==model.Parts.Count)return cache.Targets;
        var parts=model.Parts.ToDictionary(p=>p.Id);
        var joints=project.Animations.Where(a=>a.ModelId==model.Id&&!a.PoseEditing).SelectMany(a=>a.Tracks)
            .Where(t=>t.PartId!=Guid.Empty && t.Channel is CreationChannel.Rotation or CreationChannel.Position or CreationChannel.Pivot).Select(t=>t.PartId).ToHashSet();
        var mapped=model.BodyMapping.Values.ToHashSet();joints.UnionWith(mapped);
        // A leaf with the same authored pivot as its parent is generally attached geometry.
        // Explicit body mappings always take precedence over this structural heuristic.
        foreach(var part in model.Parts)
            if(!mapped.Contains(part.Id)&&!part.Group&&parts.TryGetValue(part.ParentId,out var parent)&&joints.Contains(parent.Id)
                &&part.Origin==parent.Origin&&!model.Parts.Any(p=>p.ParentId==part.Id))joints.Remove(part.Id);
        var targets=new Dictionary<Guid,Guid>();
        foreach(var part in model.Parts)
        {
            var current=part;Guid target=part.Id;
            for(int depth=0;depth<128;depth++)
            {if(joints.Contains(current.Id)){target=current.Id;break;}if(!parts.TryGetValue(current.ParentId,out current!))break;}
            targets[part.Id]=target;
        }
        cache.Targets=targets;cache.Stamp=stamp;return targets;
    }
    internal static IReadOnlyList<CreationPart> Chain(DirectorCreationProject project,CreationModel model,Guid selected,int maxJoints)
    {
        var targets=Targets(project,model);var parts=model.Parts.ToDictionary(p=>p.Id);
        selected=targets.GetValueOrDefault(selected,selected);
        var effective=targets.Values.ToHashSet();var chain=new List<CreationPart>();
        Guid root=model.RigEditing.Roots.GetValueOrDefault(selected), current=selected;
        var trunk=model.BodyMapping.GetValueOrDefault("body");
        var children=new Dictionary<Guid,HashSet<Guid>>();
        foreach(var id in effective)
        {
            if(!parts.TryGetValue(id,out var part))continue;
            var parent=part.ParentId;
            for(int depth=0;parent!=Guid.Empty&&depth<128;depth++)
            {
                if(effective.Contains(parent)){if(!children.TryGetValue(parent,out var set))children[parent]=set=new();set.Add(id);break;}
                parent=parts.GetValueOrDefault(parent)?.ParentId??Guid.Empty;
            }
        }
        for(int depth=0;depth<128&&parts.TryGetValue(current,out var part);depth++)
        {
            if(effective.Contains(current)||current==root||model.RigEditing.Anchors.Contains(current))chain.Add(part);
            if(current==root||model.RigEditing.Anchors.Contains(current)||chain.Count>=Math.Clamp(maxJoints,1,8))break;
            var parent=part.ParentId;
            if(root==Guid.Empty && parent!=Guid.Empty && (parent==trunk || children.GetValueOrDefault(parent)?.Count>1))break;
            current=parent;
        }
        chain.Reverse();return chain;
    }
}
