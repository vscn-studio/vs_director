using System.Numerics;

namespace VSDirector.Data;

[Flags]
public enum CreationRecordingChannels { Position = 1, Rotation = 2, Pose = 4, Equipment = 8, All = 15 }

public sealed class CreationPlayerBody
{
    public bool Enabled { get; set; }
    public double Width { get; set; } = .6;
    public double Height { get; set; } = 1.8;
    public double SneakHeight { get; set; } = 1.44;
    public double EyeHeight { get; set; } = 1.7;
    public bool Valid() => double.IsFinite(Width) && Width is >= .1 and <= 4
        && double.IsFinite(Height) && Height is >= .2 and <= 8
        && double.IsFinite(SneakHeight) && SneakHeight >= .1 && SneakHeight <= Height
        && double.IsFinite(EyeHeight) && EyeHeight >= .05 && EyeHeight <= Height;
}

public sealed class CreationActorSource
{
    public string EntityCode { get; set; } = "";
    public string ShapeCode { get; set; } = "";
    public string SourceWorld { get; set; } = "";
    public long SourceEntityId { get; set; }
    public int TextureVariant { get; set; }
    public byte[] AppearanceData { get; set; } = [];
}

public sealed class CreationIkChain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "IK";
    public Guid Upper { get; set; }
    public Guid Lower { get; set; }
    public CreationVector EndOffset { get; set; }
    public Guid Target { get; set; }
    public Guid Pole { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Stateless two-segment solver. All points are in the same coordinate space.</summary>
public static class DirectorCreationIk
{
    public static IEnumerable<(CreationPart Upper, CreationPart Lower)> SuggestChains(CreationModel model)
    {
        static string Name(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var upper in model.Parts)
        {
            string name = Name(upper.Name);
            if (!name.Contains("arm") && !name.Contains("leg") && !name.Contains("foot")) continue;
            string lowerName = name.Contains("upper") ? name.Replace("upper", "lower")
                : name.EndsWith("up") ? name[..^2] + "low" : "";
            var lower = model.Parts.FirstOrDefault(p => p.ParentId == upper.Id && Name(p.Name) == lowerName);
            if (lower is not null) yield return (upper, lower);
        }
    }
    public static (Vector3 Joint, Vector3 End) Solve(Vector3 root, Vector3 target, Vector3 pole, float upper, float lower)
    {
        if (!float.IsFinite(upper) || !float.IsFinite(lower) || upper <= 1e-6f || lower <= 1e-6f)
            throw new InvalidDataException("IK requires two nonzero bones.");
        var delta = target - root;
        var direction = delta.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(delta);
        float distance = Math.Clamp(delta.Length(), MathF.Abs(upper - lower) + 1e-6f, upper + lower);
        var bend = pole - root; bend -= direction * Vector3.Dot(bend, direction);
        if (bend.LengthSquared() < 1e-10f)
        {
            var axis = MathF.Abs(direction.Y) < .9f ? Vector3.UnitY : Vector3.UnitX;
            bend = axis - direction * Vector3.Dot(axis, direction);
        }
        bend = Vector3.Normalize(bend);
        float along = (upper * upper - lower * lower + distance * distance) / (2 * distance);
        float height = MathF.Sqrt(MathF.Max(0, upper * upper - along * along));
        return (root + direction * along + bend * height, root + direction * distance);
    }

    public static Vector3 Pivot(CreationPart part) => ((part.Origin - part.From) * (1d / 16)).Float();
    public static Vector3 Joint(CreationPart part, CreationObjectState state, int version)
        => Vector3.Transform(DirectorCreationMath.BonePivot(part, state.Pose.GetValueOrDefault(part.Id) ?? new(), version), state.Bones[part.Id]);

    // From/to safeguards adapted from InGameDevTools RigPoseEditor.RigIkMatrix3
    // (Alodh, MIT; see licenses/InGameDevTools-MIT.txt). Row-vector convention.
    public static Matrix4x4 FromTo(Vector3 from, Vector3 to, float weight = 1)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Matrix4x4.Identity;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot > .999999f) return Matrix4x4.Identity;
        Vector3 axis = Vector3.Cross(from, to);
        if (dot < -.999999f) axis = Vector3.Cross(from, MathF.Abs(from.Y) < .9f ? Vector3.UnitY : Vector3.UnitX);
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot) * Math.Clamp(weight, 0, 1));
    }

    public static (CreationSceneObject Target, CreationSceneObject Pole, CreationIkChain Chain) CreateControls(
        CreationSceneObject actor, CreationModel model, CreationObjectState state, CreationPart upper, CreationPart lower)
    {
        var pivot = DirectorCreationMath.BonePivot(lower, state.Pose.GetValueOrDefault(lower.Id) ?? new(), model.AnimationVersion);
        var size = ((lower.To - lower.From) * (1d / 16)).Float();
        var end = size * .5f;
        // Prefer an actual child joint (hand/foot), otherwise the distal face centre.
        var child = model.Parts.Where(p => p.ParentId == lower.Id && !p.Group
                && Vector3.DistanceSquared(Joint(p, state, model.AnimationVersion), Joint(lower, state, model.AnimationVersion)) > 1e-10f)
            .OrderByDescending(p => Vector3.DistanceSquared(Joint(p, state, model.AnimationVersion), Joint(lower, state, model.AnimationVersion))).FirstOrDefault();
        if (child is not null && Matrix4x4.Invert(state.Bones[lower.Id], out var inverse))
            end = Vector3.Transform(Joint(child, state, model.AnimationVersion), inverse);
        else
        {
            int axis = Math.Abs(size.X) > Math.Abs(size.Y) ? 0 : 1; if (Math.Abs(size.Z) > Math.Abs(size[axis])) axis = 2;
            end[axis] = Math.Abs(pivot[axis]) < Math.Abs(size[axis] - pivot[axis]) ? size[axis] : 0;
        }
        var a = Joint(upper, state, model.AnimationVersion); var b = Joint(lower, state, model.AnimationVersion);
        var c = Vector3.Transform(end, state.Bones[lower.Id]);
        if (Vector3.DistanceSquared(a, b) < 1e-10f || Vector3.DistanceSquared(b, c) < 1e-10f)
            throw new InvalidDataException("IK requires two nonzero bones.");
        var direction = c - a; if (direction.LengthSquared() < 1e-10f) direction = b - a; direction = Vector3.Normalize(direction);
        var bend = b - a; bend -= direction * Vector3.Dot(bend, direction);
        if (bend.LengthSquared() < 1e-8f)
        {
            // Derive the pole in model space, then transform with the actor.
            var axis = MathF.Abs(direction.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX;
            bend = axis - direction * Vector3.Dot(axis, direction);
        }
        float reach = Vector3.Distance(a, b) + Vector3.Distance(b, c);
        var target = new CreationSceneObject { Kind = CreationObjectKind.Group, Name = lower.Name + " · IK target",
            Transform = new() { Position = CreationVector.From(Vector3.Transform(c, state.World)) } };
        var pole = new CreationSceneObject { Kind = CreationObjectKind.Group, Name = lower.Name + " · IK pole",
            Transform = new() { Position = CreationVector.From(Vector3.Transform(b + Vector3.Normalize(bend) * reach, state.World)) } };
        return (target, pole, new() { Name = upper.Name, Upper = upper.Id, Lower = lower.Id, Target = target.Id, Pole = pole.Id, EndOffset = CreationVector.From(end) });
    }

    public static void Apply(DirectorCreationProject project, CreationFrame frame)
    {
        foreach (var state in frame.Objects.Values)
        {
            var model = project.Models.Find(m => m.Id == state.Source.ModelId);
            if (model is null || state.HasRecordedPose || state.IkWeight <= 0 || !Matrix4x4.Invert(state.World, out var inverseWorld)) continue;
            foreach (var chain in state.Source.IkChains.Where(c => c.Enabled))
            {
                var upper = model.Parts.Find(p => p.Id == chain.Upper);
                var lower = model.Parts.Find(p => p.Id == chain.Lower);
                if (upper is null || lower is null || lower.ParentId != upper.Id
                    || !frame.Objects.TryGetValue(chain.Target, out var target)
                    || !frame.Objects.TryGetValue(chain.Pole, out var pole)) continue;
                var a = Joint(upper, state, model.AnimationVersion);
                var b = Joint(lower, state, model.AnimationVersion);
                var c = Vector3.Transform(chain.EndOffset.Float(), state.Bones[lower.Id]);
                float l1 = Vector3.Distance(a, b), l2 = Vector3.Distance(b, c);
                if (l1 < 1e-6 || l2 < 1e-6) continue;
                var solved = Solve(a, Vector3.Transform(target.World.Translation, inverseWorld),
                    Vector3.Transform(pole.World.Translation, inverseWorld), l1, l2);
                Rotate(upper, a, b, solved.Joint);
                b = Joint(lower, state, model.AnimationVersion);
                c = Vector3.Transform(chain.EndOffset.Float(), state.Bones[lower.Id]);
                Rotate(lower, b, c, solved.End);

                void Rotate(CreationPart part, Vector3 pivot, Vector3 from, Vector3 to)
                {
                    if ((from - pivot).LengthSquared() < 1e-12f || (to - pivot).LengthSquared() < 1e-12f) return;
                    var rotation = FromTo(from - pivot, to - pivot, (float)state.IkWeight);
                    var transformed = state.Bones[part.Id] * Matrix4x4.CreateTranslation(-pivot) * rotation * Matrix4x4.CreateTranslation(pivot);
                    var parent = state.Bones.GetValueOrDefault(part.ParentId, Matrix4x4.Identity);
                    if (!Matrix4x4.Invert(parent, out var inverse)) return;
                    var pose = state.Pose.GetValueOrDefault(part.Id) ?? new();
                    state.Pose[part.Id] = DirectorCreationMath.PoseFromMatrix(part, transformed * inverse, pose, model.AnimationVersion);
                    state.Bones.Clear(); DirectorCreationEvaluator.EvaluateBones(model, state);
                }
            }
        }
    }
}
