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

    public static void Apply(DirectorCreationProject project, CreationFrame frame)
    {
        foreach (var state in frame.Objects.Values)
        {
            var model = project.Models.Find(m => m.Id == state.Source.ModelId);
            if (model is null || state.IkWeight <= 0 || !Matrix4x4.Invert(state.World, out var inverseWorld)) continue;
            foreach (var chain in state.Source.IkChains.Where(c => c.Enabled))
            {
                var upper = model.Parts.Find(p => p.Id == chain.Upper);
                var lower = model.Parts.Find(p => p.Id == chain.Lower);
                if (upper is null || lower is null || lower.ParentId != upper.Id
                    || !frame.Objects.TryGetValue(chain.Target, out var target)
                    || !frame.Objects.TryGetValue(chain.Pole, out var pole)) continue;
                var a = Vector3.Transform(Pivot(upper), state.Bones[upper.Id]);
                var b = Vector3.Transform(Pivot(lower), state.Bones[lower.Id]);
                var c = Vector3.Transform(chain.EndOffset.Float(), state.Bones[lower.Id]);
                float l1 = Vector3.Distance(a, b), l2 = Vector3.Distance(b, c);
                if (l1 < 1e-6 || l2 < 1e-6) continue;
                var solved = Solve(a, Vector3.Transform(target.World.Translation, inverseWorld),
                    Vector3.Transform(pole.World.Translation, inverseWorld), l1, l2);
                Rotate(upper, a, b, solved.Joint);
                b = Vector3.Transform(Pivot(lower), state.Bones[lower.Id]);
                c = Vector3.Transform(chain.EndOffset.Float(), state.Bones[lower.Id]);
                Rotate(lower, b, c, solved.End);

                void Rotate(CreationPart part, Vector3 pivot, Vector3 from, Vector3 to)
                {
                    if ((from - pivot).LengthSquared() < 1e-12f || (to - pivot).LengthSquared() < 1e-12f) return;
                    var u = Vector3.Normalize(from - pivot); var v = Vector3.Normalize(to - pivot);
                    float dot = Math.Clamp(Vector3.Dot(u, v), -1, 1);
                    var axis = Vector3.Cross(u, v);
                    if (axis.LengthSquared() < 1e-12f)
                    {
                        if (dot > 0) return;
                        axis = Vector3.Cross(u, MathF.Abs(u.Y) < .9f ? Vector3.UnitY : Vector3.UnitX);
                    }
                    var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot) * (float)state.IkWeight);
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
