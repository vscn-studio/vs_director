using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VSDirector.Data;

public readonly record struct CreationVector(double X, double Y, double Z)
{
    public static CreationVector One => new(1, 1, 1);
    public Vector3 Float() => new((float)X, (float)Y, (float)Z);
    public static CreationVector From(Vector3 value) => new(value.X, value.Y, value.Z);
    public static CreationVector operator +(CreationVector a, CreationVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CreationVector operator -(CreationVector a, CreationVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CreationVector operator *(CreationVector a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public bool Finite() => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

public sealed record CreationTransform
{
    public CreationVector Position { get; set; }
    public CreationVector Rotation { get; set; }
    public CreationVector Scale { get; set; } = CreationVector.One;
    public CreationVector Pivot { get; set; }
    public Matrix4x4 Matrix() => Matrix4x4.CreateTranslation(-Pivot.Float())
        * Matrix4x4.CreateScale(Scale.Float()) * RotationMatrix(Rotation)
        * Matrix4x4.CreateTranslation((Pivot + Position).Float());
    public static Matrix4x4 RotationMatrix(CreationVector r)
    {
        const float d = MathF.PI / 180;
        // System.Numerics uses row vectors; the engine uses column vectors (Rz, Ry, Rx).
        return Matrix4x4.CreateRotationZ((float)r.Z * d) * Matrix4x4.CreateRotationY((float)r.Y * d)
            * Matrix4x4.CreateRotationX((float)r.X * d);
    }
}

public enum CreationObjectKind { Model, Camera, Group, Image, Text, Item, Structure, Particle }
public enum CreationAssetKind { Model, Texture, Image, Audio, Structure, Performance }
public enum CreationChannel { Position, Rotation, Scale, Pivot, Color, Visible, Fov, IkWeight }
public enum CreationInterpolation { Hold, Linear, EaseIn, EaseOut, Smooth, Bezier }
public enum CreationCameraMode { Keyframes, Linear, Path, Follow, LookAt, Orbit }

public sealed class CreationAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Asset";
    public string Folder { get; set; } = "";
    public CreationAssetKind Kind { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset LastUsedUtc { get; set; }
}

public sealed class CreationPart
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParentId { get; set; }
    public string Name { get; set; } = "Cube";
    public CreationVector From { get; set; }
    public CreationVector To { get; set; } = new(4, 4, 4);
    public CreationVector Origin { get; set; }
    public CreationVector Rotation { get; set; }
    public CreationVector Scale { get; set; } = CreationVector.One;
    public double Inflate { get; set; }
    public bool Visible { get; set; } = true;
    public bool Group { get; set; }
    public JsonObject Extra { get; set; } = new();
    public JsonObject Faces { get; set; } = DefaultFaces();
    public static JsonObject DefaultFaces()
    {
        var faces = new JsonObject();
        foreach (string side in new[] { "north", "east", "south", "west", "up", "down" })
            faces[side] = new JsonObject { ["texture"] = "#all", ["uv"] = new JsonArray(0, 0, 4, 4) };
        return faces;
    }
}

public sealed class CreationModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Model";
    public string Folder { get; set; } = "";
    public int TextureWidth { get; set; } = 16;
    public int TextureHeight { get; set; } = 16;
    public CreationRigEditingSettings RigEditing { get; set; } = new();
    public int AnimationVersion { get; set; }
    public List<CreationPart> Parts { get; set; } = new();
    public Dictionary<string, string> Textures { get; set; } = new();
    public Dictionary<string, Guid> BodyMapping { get; set; } = new();
    public JsonObject Extra { get; set; } = new();
}

public sealed class CreationSceneObject
{
    public List<CreationScenePoseKey> PoseKeys { get; set; } = new();
    public CreationActorSource? ActorSource { get; set; }
    public CreationPlayerBody PlayerBody { get; set; } = new();
    public CreationRecordingChannels RecordingChannels { get; set; } = CreationRecordingChannels.All;
    public List<CreationIkChain> IkChains { get; set; } = new();
    public double IkWeight { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Actor";
    public CreationObjectKind Kind { get; set; }
    public Guid ModelId { get; set; }
    public Guid AssetId { get; set; }
    public Guid ParentId { get; set; }
    public Guid ParentPartId { get; set; }
    public Guid TargetId { get; set; }
    public CreationTransform Transform { get; set; } = new();
    public Dictionary<Guid, CreationTransform> Pose { get; set; } = new();
    public HashSet<Guid> HiddenParts { get; set; } = new();
    public bool Visible { get; set; } = true;
    public CreationVector Color { get; set; } = CreationVector.One;
    public double Opacity { get; set; } = 1;
    public double Glow { get; set; }
    public string Text { get; set; } = "Text";
    public string ResourceCode { get; set; } = "";
    public int ActorHand { get; set; }
    public string ReplacePlayerUid { get; set; } = "";
    public CreationCameraMode CameraMode { get; set; }
    public double Fov { get; set; } = 70;
    public double OrbitRadius { get; set; } = 5;
    public double OrbitSpeed { get; set; } = 30;
    public double Shake { get; set; }
    public int ParticleCount { get; set; } = 8;
    public bool NativeParticles { get; set; } = true;
    public double ParticleStart { get; set; }
    public double ParticleDuration { get; set; } = 2;
    public double ParticleLife { get; set; } = 1;
    public CreationVector ImageSize { get; set; } = new(1, 1, 0);
    public double[] Crop { get; set; } = [0, 0, 1, 1];
    public bool Billboard { get; set; }
    public List<CreationStructureBlock> Blocks { get; set; } = new();
}

public sealed class CreationStructureBlock
{
    public string Code { get; set; } = "";
    public CreationVector Position { get; set; }
}

public sealed class CreationKeyframe
{
    internal CreationKeyframe Copy() => (CreationKeyframe)MemberwiseClone();
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Time { get; set; }
    public CreationVector Value { get; set; }
    public CreationInterpolation Interpolation { get; set; } = CreationInterpolation.Linear;
    // Bezier handles in normalized segment time/value coordinates.
    public double HandleOutTime { get; set; } = 1d / 3;
    public double HandleOutValue { get; set; } = 1d / 3;
    public double HandleInTime { get; set; } = 2d / 3;
    public double HandleInValue { get; set; } = 2d / 3;
}

public sealed class CreationTrack
{
    public bool ShortestRotation { get; set; } = true;
    internal CreationTrack Copy() { var copy = (CreationTrack)MemberwiseClone(); copy.Keys = Keys.Select(k => k.Copy()).ToList(); return copy; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ObjectId { get; set; }
    public Guid PartId { get; set; }
    public CreationChannel Channel { get; set; }
    public bool Enabled { get; set; } = true;
    public List<CreationKeyframe> Keys { get; set; } = new();
}

public sealed class CreationAnimation
{
    public bool PoseEditing { get; set; }
    internal CreationAnimation Copy() { var copy = (CreationAnimation)MemberwiseClone(); copy.Tracks = Tracks.Select(t => t.Copy()).ToList(); copy.Extra = (JsonObject)Extra.DeepClone(); return copy; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Animation";
    public string Folder { get; set; } = "";
    public Guid ModelId { get; set; }
    public double Duration { get; set; } = 1;
    public bool Loop { get; set; }
    public List<CreationTrack> Tracks { get; set; } = new();
    public JsonObject Extra { get; set; } = new();
}

public sealed class CreationClip
{
    public bool BakedPose { get; set; }
    public Guid PerformanceAssetId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Clip";
    public Guid ObjectId { get; set; }
    public Guid AnimationId { get; set; }
    public Guid AudioAssetId { get; set; }
    public int Lane { get; set; }
    public double Start { get; set; }
    public double SourceStart { get; set; }
    public double Duration { get; set; } = 1;
    public double Speed { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double Gain { get; set; }
    public bool Loop { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class CreationPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Preset";
    public string Folder { get; set; } = "";
    public Guid ModelId { get; set; }
    public Dictionary<Guid, CreationTransform> Pose { get; set; } = new();
    public CreationSceneObject? Appearance { get; set; }
}

public sealed class CreationOutput
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public int Quality { get; set; } = 18;
    public string Encoder { get; set; } = "Auto";
    public string Directory { get; set; } = "";
    public bool SceneAudio { get; set; } = true;
}

public sealed partial class DirectorCreationProject
{
    [JsonIgnore] public Dictionary<Guid, CreationPerformance> Performances { get; } = new();
    public CreationPoseSnapshot? PoseRecovery { get; set; }
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New film";
    public string Folder { get; set; } = "";
    public string WorldId { get; set; } = "";
    public int Dimension { get; set; }
    public CreationVector Origin { get; set; }
    public double Duration { get; set; } = 10;
    public CreationTimeDisplay TimeDisplay { get; set; } = CreationTimeDisplay.Frames;
    public double LoopStart { get; set; }
    public double LoopEnd { get; set; } = 10;
    public bool Loop { get; set; }
    public Guid CameraId { get; set; }
    public Guid BackgroundReplayId { get; set; }
    public Guid BackgroundClipId { get; set; }
    public double BackgroundOffset { get; set; }
    public List<string> Folders { get; set; } = new();
    public List<CreationAsset> Assets { get; set; } = new();
    public List<CreationModel> Models { get; set; } = new();
    public List<CreationSceneObject> Objects { get; set; } = new();
    public List<CreationTrack> Tracks { get; set; } = new();
    public List<CreationAnimation> Animations { get; set; } = new();
    public List<CreationClip> Clips { get; set; } = new();
    public List<CreationPreset> Presets { get; set; } = new();
    public CreationOutput Output { get; set; } = new();
    public DirectorReplayVisualSettings Visuals { get; set; } = new();
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DirectorCreationProject Clone()
    {
        // Native actors can include hundreds of thousands of keys. Do not turn
        // that graph into a huge JSON string on the UI thread for every gesture.
        var copy = new DirectorCreationProject { SchemaVersion = SchemaVersion, Id = Id, Name = Name, Folder = Folder, WorldId = WorldId, Dimension = Dimension,
            Origin = Origin, Duration = Duration, TimeDisplay = TimeDisplay, LoopStart = LoopStart, LoopEnd = LoopEnd, Loop = Loop, CameraId = CameraId,
            BackgroundReplayId = BackgroundReplayId, BackgroundClipId = BackgroundClipId, BackgroundOffset = BackgroundOffset, ModifiedUtc = ModifiedUtc,
            Folders = new(Folders), Assets = CreationJson.Clone(Assets), Models = CreationJson.Clone(Models), Objects = CreationJson.Clone(Objects),
            Tracks = Tracks.Select(t => t.Copy()).ToList(), Animations = Animations.Select(a => a.Copy()).ToList(),
            Clips = CreationJson.Clone(Clips), Presets = CreationJson.Clone(Presets), Output = CreationJson.Clone(Output), Visuals = Visuals.DeepClone() };
        // Published recordings are read-only; editing/baking copies their tracks.
        // Retain the cache across history snapshots instead of rereading takes.
        foreach (var take in Performances) copy.Performances.Add(take.Key, take.Value);
        return copy;
    }
    public void Validate()
    {
        if (!Enum.IsDefined(TimeDisplay)) TimeDisplay = CreationTimeDisplay.Frames;
        Visuals = DirectorReplayVisualSettings.Normalize(Visuals);
        if (SchemaVersion == 1) SchemaVersion = CurrentSchemaVersion;
        ValidateDocument();
        NormalizeBackground();
        if (Objects is null || Models is null || Assets is null || Animations is null || Clips is null || Tracks is null || Presets is null || Output is null || Name is null || WorldId is null || Folder is null)
            throw new InvalidDataException("作品数据缺少必要字段。");
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported creation project version.");
        if (Id == Guid.Empty || !Origin.Finite() || !double.IsFinite(Duration) || Duration <= 0 || Duration > 86400)
            throw new InvalidDataException("Invalid project identity, origin or duration.");
        if (!double.IsFinite(LoopStart) || !double.IsFinite(LoopEnd) || !double.IsFinite(BackgroundOffset)) throw new InvalidDataException("Invalid timeline range.");
        if (Output.Fps is < 1 or > 240 || Output.Width is < 16 or > 8192 || Output.Height is < 16 or > 8192 || Output.Width % 2 != 0 || Output.Height % 2 != 0)
            throw new InvalidDataException("Invalid output dimensions or frame rate.");
        static void Ids(IEnumerable<Guid> values) { var ids = values.ToArray(); if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Duplicate or empty creation identity."); }
        Ids(Objects.Select(o => o.Id)); Ids(Models.Select(m => m.Id)); Ids(Assets.Select(a => a.Id)); Ids(Animations.Select(a => a.Id)); Ids(Clips.Select(c => c.Id)); Ids(Presets.Select(p => p.Id));
        Ids(Tracks.Select(t => t.Id)); foreach (var animation in Animations) Ids(animation.Tracks.Select(t => t.Id));
        static bool TransformValid(CreationTransform? t) => t is not null && t.Position.Finite() && t.Rotation.Finite() && t.Scale.Finite() && t.Pivot.Finite();
        var objectParents = Objects.ToDictionary(o => o.Id, o => o.ParentId);
        if (Objects.Select(o => o.Id).Distinct().Count() != Objects.Count || Models.Select(o => o.Id).Distinct().Count() != Models.Count)
            throw new InvalidDataException("Duplicate creation object identity.");
        foreach (var obj in Objects)
        {
            if (!TransformValid(obj.Transform) || obj.Pose is null || obj.Pose.Values.Any(t => !TransformValid(t)) || !obj.Color.Finite() || obj.Crop is null || obj.Crop.Length != 4 || obj.Crop.Any(v => !double.IsFinite(v))
                || !double.IsFinite(obj.Opacity) || !double.IsFinite(obj.Fov) || !double.IsFinite(obj.OrbitRadius) || !double.IsFinite(obj.OrbitSpeed) || !double.IsFinite(obj.Shake)
                || !double.IsFinite(obj.ParticleLife) || !double.IsFinite(obj.ParticleStart) || !double.IsFinite(obj.ParticleDuration) || !obj.ImageSize.Finite())
                throw new InvalidDataException("Non-finite object transform.");
            ValidateParent(obj.Id, objectParents);
        }
        foreach (var model in Models)
        {
            if (model.Parts is null || model.Textures is null || model.Extra is null || model.BodyMapping is null || model.TextureWidth <= 0 || model.TextureHeight <= 0) throw new InvalidDataException("Invalid model.");
            Ids(model.Parts.Select(p => p.Id));
            var parents = model.Parts.ToDictionary(p => p.Id, p => p.ParentId);
            foreach (var part in model.Parts)
            {
                if (!part.From.Finite() || !part.To.Finite() || !part.Origin.Finite() || !part.Rotation.Finite() || !part.Scale.Finite() || !double.IsFinite(part.Inflate) || part.Extra is null || part.Faces is null) throw new InvalidDataException("Invalid model part.");
                ValidateParent(part.Id, parents);
            }
        }
        foreach (var animation in Animations) if (!double.IsFinite(animation.Duration) || animation.Duration < 1d / 1000 || animation.Tracks is null) throw new InvalidDataException("Invalid action duration.");
        foreach (var track in Tracks.Concat(Animations.SelectMany(a => a.Tracks)))
        {
            if (track.Keys is null) throw new InvalidDataException("Missing keyframe list.");
            Ids(track.Keys.Select(k => k.Id));
            foreach (var key in track.Keys)
                if (!double.IsFinite(key.Time) || key.Time < 0 || !key.Value.Finite() || !double.IsFinite(key.HandleOutTime) || !double.IsFinite(key.HandleOutValue) || !double.IsFinite(key.HandleInTime) || !double.IsFinite(key.HandleInValue)) throw new InvalidDataException("Invalid animation keyframe.");
        }
        foreach (var clip in Clips)
            if (!double.IsFinite(clip.Start) || !double.IsFinite(clip.Duration) || !double.IsFinite(clip.Speed)
                || !double.IsFinite(clip.SourceStart) || !double.IsFinite(clip.FadeIn) || !double.IsFinite(clip.FadeOut) || !double.IsFinite(clip.Gain)
                || clip.Start < 0 || clip.SourceStart < 0 || clip.Duration <= 0 || clip.Speed <= 0 || clip.FadeIn < 0 || clip.FadeOut < 0)
                throw new InvalidDataException("Invalid animation clip.");
    }
    private static void ValidateParent(Guid id, Dictionary<Guid, Guid> parents)
    {
        HashSet<Guid> visited = new();
        while (id != Guid.Empty)
        {
            if (!parents.TryGetValue(id, out Guid parent)) throw new InvalidDataException("Parent does not exist.");
            if (!visited.Add(id)) throw new InvalidDataException("Cyclic parent binding.");
            if (visited.Count > 128) throw new InvalidDataException("对象或骨骼层级不能超过 128 层。");
            id = parent;
        }
    }
}

public static class CreationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
