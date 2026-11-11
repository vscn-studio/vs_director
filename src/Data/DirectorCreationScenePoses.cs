namespace VSDirector.Data;

// A scene pose is one editable key, with sparse overrides relative to the actor
// base pose. It never changes a shared animation resource.
public sealed class CreationScenePoseKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Time { get; set; }
    public CreationInterpolation Interpolation { get; set; } = CreationInterpolation.Smooth;
    public Dictionary<Guid, CreationTransform> Bones { get; set; } = new();
    public HashSet<Guid> Hidden { get; set; } = new();
}

public static class DirectorCreationScenePoses
{
    public static void Save(CreationSceneObject actor, CreationPoseSnapshot pose, double time)
    {
        var key = actor.PoseKeys.Find(k => Math.Abs(k.Time-time)<1e-6);
        if(key is null) { key=new() { Time=time }; actor.PoseKeys.Add(key); }
        key.Bones=pose.Bones.Where(p=>p.Value!=(actor.Pose.GetValueOrDefault(p.Key)??new CreationTransform()))
            .ToDictionary(p=>p.Key,p=>p.Value with {});
        key.Hidden=new(pose.Hidden); actor.PoseKeys.Sort((a,b)=>a.Time.CompareTo(b.Time));
    }
    public static void Apply(CreationModel model, CreationObjectState state, double time)
    {
        var keys=state.Source.PoseKeys.OrderBy(k=>k.Time).ToArray();
        // Scene pose keys own only their authored range. A single key is held
        // after its timestamp; preceding performance remains untouched.
        if(keys.Length==0 || time<keys[0].Time) return;
        var a=keys.Last(k=>k.Time<=time);var b=keys.FirstOrDefault(k=>k.Time>time)??a;
        double t=a==b?0:Math.Clamp((time-a.Time)/(b.Time-a.Time),0,1);
        t=a.Interpolation switch { CreationInterpolation.Hold=>0,CreationInterpolation.EaseIn=>t*t,CreationInterpolation.EaseOut=>1-(1-t)*(1-t),CreationInterpolation.Smooth=>t*t*(3-2*t),_=>t };
        CreationVector Mix(CreationVector x,CreationVector y)=>x+(y-x)*t;
        foreach(var part in model.Parts)
        {
            var rest=state.Source.Pose.GetValueOrDefault(part.Id)??new();
            var x=a.Bones.GetValueOrDefault(part.Id)??rest;var y=b.Bones.GetValueOrDefault(part.Id)??rest;
            state.Pose[part.Id]=new() { Position=Mix(x.Position,y.Position),Rotation=Mix(x.Rotation,y.Rotation),Scale=Mix(x.Scale,y.Scale),Pivot=Mix(x.Pivot,y.Pivot) };
        }
        state.HiddenParts.Clear();state.HiddenParts.UnionWith(a.Hidden);state.IkWeight=0;
    }
    public static void Move(CreationSceneObject actor, Guid keyId, double time, bool copy)
    {
        var source=actor.PoseKeys.First(k=>k.Id==keyId);
        if(Math.Abs(source.Time-time)<1e-6) return;
        var key=copy?CreationJson.Clone(source):source;if(copy) key.Id=Guid.NewGuid();
        actor.PoseKeys.RemoveAll(k=>Math.Abs(k.Time-time)<1e-6);key.Time=time;if(copy)actor.PoseKeys.Add(key);
        actor.PoseKeys.Sort((a,b)=>a.Time.CompareTo(b.Time));
    }
}
