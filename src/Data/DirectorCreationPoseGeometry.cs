using System.Numerics;

namespace VSDirector.Data;

public sealed class CreationRigEditingSettings
{
    public List<CreationJointSeam> Seams { get; set; } = new();
    public HashSet<Guid> Anchors { get; set; } = new();
    public Dictionary<Guid, Guid> Roots { get; set; } = new();
}

public static class DirectorCreationPoseGeometry
{
    // Ray/box picking in the rendered part's local space. Direction is kept
    // unnormalised after inversion so distances remain comparable across scales.
    public static Guid Pick(CreationModel model, CreationObjectState state, Vector3 origin, Vector3 direction)
        => Pick(model, state, origin, direction, out _);
    public static Guid Pick(CreationModel model, CreationObjectState state, Vector3 origin, Vector3 direction, out float distance)
    {
        Guid hit = Guid.Empty; float best = float.PositiveInfinity;
        foreach (var part in model.Parts.Where(p => !p.Group && p.Visible && !state.HiddenParts.Contains(p.Id)))
        {
            if (!state.Bones.TryGetValue(part.Id, out var bone) || !Matrix4x4.Invert(bone * state.World, out var inverse)) continue;
            var o = Vector3.Transform(origin, inverse); var d = Vector3.TransformNormal(direction, inverse);
            var size = ((part.To - part.From) * (1d / 16)).Float(); float inflate = (float)part.Inflate / 16;
            var min = Vector3.Min(Vector3.Zero, size) - new Vector3(inflate); var max = Vector3.Max(Vector3.Zero, size) + new Vector3(inflate);
            float near = 0, far = float.PositiveInfinity; bool valid = true;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(d[axis]) < 1e-9) { if (o[axis] < min[axis] || o[axis] > max[axis]) valid = false; continue; }
                float a = (min[axis] - o[axis]) / d[axis], b = (max[axis] - o[axis]) / d[axis];
                near = Math.Max(near, Math.Min(a,b)); far = Math.Min(far, Math.Max(a,b));
            }
            if (valid && far >= near && near < best) { best = near; hit = part.Id; }
        }
        distance = best; return hit;
    }
    public static Guid PoseTarget(DirectorCreationProject project, CreationModel model, Guid hit)
    {
        return DirectorCreationRigSelection.Targets(project,model).GetValueOrDefault(hit,hit);
    }
    public static IReadOnlyList<CreationPart> Chain(CreationModel model, Guid selected)
    {
        var chain = new List<CreationPart>(); var part = model.Parts.Find(p => p.Id == selected);
        Guid root = model.RigEditing.Roots.GetValueOrDefault(selected);
        while (part is not null && chain.Count < 128)
        {
            chain.Add(part);
            if (part.Id == root || model.RigEditing.Anchors.Contains(part.Id)) break;
            var parent = model.Parts.Find(p => p.Id == part.ParentId);
            if (root == Guid.Empty && parent is not null && model.Parts.Count(p => p.ParentId == parent.Id) > 1) break;
            part = parent;
        }
        chain.Reverse(); return chain;
    }
    // Uses the same forward transforms as rendering, with bounded CCD passes.
    // No anatomical names or synthetic bones are required.
    public static Dictionary<Guid, CreationTransform> Pull(CreationModel model, CreationObjectState source, Guid selected, Vector3 targetWorld)
        => Pull(model,source,selected,targetWorld,Chain(model,selected));
    internal static Dictionary<Guid, CreationTransform> Pull(CreationModel model, CreationObjectState source, Guid selected, Vector3 targetWorld,IReadOnlyList<CreationPart> chain)
    {
        static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
        if (!Finite(targetWorld) || !Matrix4x4.Invert(source.World, out var inverseWorld) || !Finite(Vector3.Transform(targetWorld,inverseWorld))) throw new InvalidOperationException("Pose transform is singular or non-finite.");
        if (chain.Count == 0) return new();
        var endPart = chain[^1];
        var state = new CreationObjectState { Source = source.Source, World = source.World };
        foreach (var p in source.Pose) state.Pose[p.Key] = p.Value with { };
        DirectorCreationEvaluator.EvaluateBones(model, state);
        Vector3 endLocal = ((endPart.To - endPart.From) * (1d / 32)).Float();
        if (endPart.Group) endLocal = DirectorCreationMath.BonePivot(endPart, state.Pose.GetValueOrDefault(selected) ?? new(), model.AnimationVersion);
        var target = Vector3.Transform(targetWorld, inverseWorld);
        Vector3 End() => Vector3.Transform(endLocal, state.Bones[selected]);
        float length = 0; var previous = End();
        foreach (var part in chain.Reverse()) { var joint = DirectorCreationIk.Joint(part,state,model.AnimationVersion); length += Vector3.Distance(previous,joint); previous = joint; }
        if (!float.IsFinite(length) || length < 1e-7) throw new InvalidOperationException("Pose chain has no usable length.");
        float tolerance = Math.Max(.0001f, length * .001f), best = Vector3.Distance(End(),target);
        var bestPose = state.Pose.ToDictionary(p => p.Key,p => p.Value with { });
        for (int iteration = 0; iteration < 24 && best > tolerance; iteration++)
        {
            foreach (var part in chain.Reverse())
            {
                var joint = DirectorCreationIk.Joint(part,state,model.AnimationVersion); var from = End() - joint; var to = target - joint;
                if (from.LengthSquared() < 1e-12 || to.LengthSquared() < 1e-12) continue;
                var desired = state.Bones[part.Id] * Matrix4x4.CreateTranslation(-joint) * DirectorCreationIk.FromTo(from,to) * Matrix4x4.CreateTranslation(joint);
                var parent = state.Bones.GetValueOrDefault(part.ParentId,Matrix4x4.Identity);
                if (!Matrix4x4.Invert(parent,out var inverseParent)) throw new InvalidOperationException("Pose transform is singular.");
                var baseline = state.Pose.GetValueOrDefault(part.Id) ?? new();
                var solved = DirectorCreationMath.PoseFromMatrix(part,desired * inverseParent,baseline,model.AnimationVersion);
                // Do not stretch or silently change signed scales to fit the target.
                solved.Scale = baseline.Scale;
                var actual = DirectorCreationMath.BoneMatrix(part,solved,model.AnimationVersion) * parent;
                foreach (var point in new[] { Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ })
                    if (!Finite(Vector3.Transform(point,actual)) || !Finite(Vector3.Transform(point,desired)) || Vector3.Distance(Vector3.Transform(point,actual),Vector3.Transform(point,desired)) > .002f)
                        throw new InvalidOperationException("Pose cannot represent this transform without shear.");
                state.Pose[part.Id] = solved; state.Bones.Clear(); DirectorCreationEvaluator.EvaluateBones(model,state);
                float error = Vector3.Distance(End(),target);
                if (error < best) { best = error; bestPose = state.Pose.ToDictionary(p => p.Key,p => p.Value with { }); }
            }
        }
        return bestPose;
    }
}
