using System.Numerics;

namespace VSDirector.Data;

public sealed class CreationObjectState
{
    public required CreationSceneObject Source { get; init; }
    public CreationTransform Transform { get; set; } = new();
    public Dictionary<Guid, CreationTransform> Pose { get; } = new();
    public Dictionary<Guid, Matrix4x4> Bones { get; } = new();
    public HashSet<Guid> HiddenParts { get; } = new();
    public Matrix4x4 World { get; set; }
    public CreationVector Color { get; set; }
    public bool Visible { get; set; }
    public double Fov { get; set; }
    public double IkWeight { get; set; } = 1;
    public string? ResourceOverride { get; set; }
    public DirectorReplayActorSample? PerformanceSample { get; set; }
    public bool HasPerformance { get; set; }
    public bool HasPositionAnimation { get; set; }
}

public sealed class CreationFrame
{
    public double Time { get; init; }
    public Dictionary<Guid, CreationObjectState> Objects { get; } = new();
}

/// <summary>Absolute-time evaluation, shared by scrubbing, preview and output. No engine state is advanced here.</summary>
public static class DirectorCreationEvaluator
{
    public static CreationVector Sample(CreationTrack track, double time, CreationVector fallback)
    {
        var keys = track.Keys.OrderBy(k => k.Time).ThenBy(k => k.Id).ToArray();
        if (!track.Enabled || keys.Length == 0) return fallback;
        if (time <= keys[0].Time) return keys[0].Value;
        for (int i = 0; i < keys.Length - 1; i++)
        {
            var a = keys[i]; var b = keys[i + 1];
            if (time >= b.Time) continue;
            double t = Math.Clamp((time - a.Time) / Math.Max(1e-9, b.Time - a.Time), 0, 1);
            t = a.Interpolation switch
            {
                CreationInterpolation.Hold => 0,
                CreationInterpolation.EaseIn => t * t,
                CreationInterpolation.EaseOut => 1 - (1 - t) * (1 - t),
                CreationInterpolation.Smooth => t * t * (3 - 2 * t),
                CreationInterpolation.Bezier => BezierTime(a, t),
                _ => t
            };
            CreationVector delta = b.Value - a.Value;
            if (track.Channel == CreationChannel.Rotation)
                delta = new(Angle(delta.X), Angle(delta.Y), Angle(delta.Z));
            return a.Value + delta * t;
        }
        return keys[^1].Value;
    }

    public static double BezierTime(CreationKeyframe key, double time)
    {
        double x1 = Math.Clamp(key.HandleOutTime, 0, 1), x2 = Math.Clamp(key.HandleInTime, x1, 1);
        double low = 0, high = 1;
        static double Cubic(double u, double a, double b) => 3 * (1 - u) * (1 - u) * u * a + 3 * (1 - u) * u * u * b + u * u * u;
        for (int i = 0; i < 30; i++)
        {
            double mid = (low + high) / 2;
            if (Cubic(mid, x1, x2) < time) low = mid; else high = mid;
        }
        return Cubic((low + high) / 2, key.HandleOutValue, key.HandleInValue);
    }
    private static double Angle(double value)
    {
        double angle = (value + 180) % 360;
        return (angle < 0 ? angle + 360 : angle) - 180;
    }

    public static CreationFrame Evaluate(DirectorCreationProject project, double time,
        IReadOnlyDictionary<(Guid Object, Guid Part), CreationTransform>? drafts = null, CreationAnimation? editingAction = null, Guid actionActor = default, double actionTime = 0,
        IReadOnlyDictionary<(Guid Object, Guid Part, CreationChannel Channel), CreationVector>? propertyDrafts = null)
    {
        time = Math.Clamp(double.IsFinite(time) ? time : 0, 0, project.Duration);
        var frame = new CreationFrame { Time = time };
        foreach (var obj in project.Objects)
        {
            var state = new CreationObjectState { Source = obj, Transform = obj.Transform with { }, Color = obj.Color, Visible = obj.Visible, Fov = obj.Fov, IkWeight = obj.IkWeight };
            foreach (var p in obj.Pose) state.Pose[p.Key] = p.Value with { };
            state.HiddenParts.UnionWith(obj.HiddenParts);
            bool actionPreview = editingAction is not null && obj.Id == actionActor;
            var recordedRootChannels = new HashSet<CreationChannel>();
            foreach (var clip in project.Clips.Where(c => !actionPreview && c.Enabled && c.ObjectId == obj.Id && c.PerformanceAssetId != Guid.Empty
                && (c.Id == project.BackgroundClipId || time >= c.Start && (time < c.Start + c.Duration || time == project.Duration && time == c.Start + c.Duration))).OrderBy(c => c.Lane).ThenBy(c => c.Start).ThenBy(c => c.Id))
            {
                if (!project.Performances.TryGetValue(clip.PerformanceAssetId, out var take)) continue;
                state.HasPerformance = true;
                double duration = Math.Max(1d / 30, take.Action.DurationSeconds);
                double source = clip.SourceStart + Math.Clamp(time - clip.Start, 0, clip.Duration) * clip.Speed;
                source = clip.Loop ? source % duration : Math.Min(source, duration);
                if (!clip.BakedPose) foreach (var track in take.Tracks.Where(t => t.Enabled && t.Keys.Count > 0))
                {
                    Apply(state, track, source, 1);
                    if (track.PartId == Guid.Empty) recordedRootChannels.Add(track.Channel);
                }
                if (take.Channels.HasFlag(CreationRecordingChannels.Equipment)) state.PerformanceSample = take.SampleState(source);
            }
            // Clip order is deterministic. Later lanes override only their authored channels.
            foreach (var clip in project.Clips.Where(c => !actionPreview && !c.BakedPose && c.Enabled && c.ObjectId == obj.Id && c.AnimationId != Guid.Empty
                && time >= c.Start && (time < c.Start + c.Duration || time == project.Duration && time == c.Start + c.Duration)).OrderBy(c => c.Lane).ThenBy(c => c.Start).ThenBy(c => c.Id))
            {
                var animation = project.Animations.Find(a => a.Id == clip.AnimationId);
                if (animation is null || animation.ModelId != obj.ModelId) continue;
                double sourceTime = clip.SourceStart + (time - clip.Start) * clip.Speed;
                if (clip.Loop || animation.Loop) sourceTime %= Math.Max(1e-6, animation.Duration);
                else sourceTime = Math.Min(sourceTime, animation.Duration);
                double weight = Math.Min(clip.FadeIn <= 0 ? 1 : (time - clip.Start) / clip.FadeIn,
                    clip.FadeOut <= 0 ? 1 : (clip.Start + clip.Duration - time) / clip.FadeOut);
                foreach (var track in animation.Tracks.Where(t => t.Enabled)) Apply(state, track, sourceTime, Math.Clamp(weight, 0, 1));
            }
            // A take owns its recorded root channels. Initial placement keys must
            // not pin a moving performance in place; unrecorded channels remain editable.
            foreach (var track in project.Tracks.Where(t => !actionPreview && t.ObjectId == obj.Id && t.Enabled
                && (t.PartId != Guid.Empty || !recordedRootChannels.Contains(t.Channel)))) Apply(state, track, time, 1);
            foreach (var clip in project.Clips.Where(c => !actionPreview && c.BakedPose && c.Enabled && c.ObjectId == obj.Id
                && (c.Id == project.BackgroundClipId || time >= c.Start && (time < c.Start + c.Duration || time == project.Duration && time == c.Start + c.Duration))).OrderBy(c => c.Lane).ThenBy(c => c.Start).ThenBy(c => c.Id))
            {
                var animation = project.Animations.Find(a => a.Id == clip.AnimationId);
                if (animation is null) continue;
                double source = clip.SourceStart + Math.Clamp(time - clip.Start, 0, clip.Duration) * clip.Speed;
                source = clip.Loop ? source % animation.Duration : Math.Min(source, animation.Duration);
                foreach (var track in animation.Tracks.Where(t => t.Enabled)) Apply(state, track, source, 1);
            }
            if (actionPreview) foreach (var track in editingAction!.Tracks.Where(t => t.Enabled)) Apply(state, track, actionTime, 1);
            if (drafts is not null)
            {
                if (drafts.TryGetValue((obj.Id, Guid.Empty), out var draft)) state.Transform = draft with { };
                foreach (var entry in drafts.Where(d => d.Key.Object == obj.Id && d.Key.Part != Guid.Empty))
                    state.Pose[entry.Key.Part] = entry.Value with { };
            }
            if (propertyDrafts is not null && propertyDrafts.TryGetValue((obj.Id, Guid.Empty, CreationChannel.IkWeight), out var previewWeight))
                state.IkWeight = Math.Clamp(previewWeight.X, 0, 1);
            frame.Objects[obj.Id] = state;
            var model = project.Models.Find(m => m.Id == obj.ModelId);
            if (model is not null) EvaluateBones(model, state);
        }
        HashSet<Guid> resolved = new(), resolving = new();
        Matrix4x4 Resolve(Guid id)
        {
            if (!frame.Objects.TryGetValue(id, out var state)) return Matrix4x4.Identity;
            if (resolved.Contains(id)) return state.World;
            if (!resolving.Add(id)) return Matrix4x4.Identity;
            Matrix4x4 parent = Matrix4x4.Identity;
            if (state.Source.ParentId != Guid.Empty && frame.Objects.TryGetValue(state.Source.ParentId, out var p))
            {
                parent = Resolve(p.Source.Id);
                if (p.Bones.TryGetValue(state.Source.ParentPartId, out var bone)) parent = bone * parent;
                state.Visible &= p.Visible;
            }
            state.World = state.Transform.Matrix() * parent;
            resolving.Remove(id); resolved.Add(id);
            return state.World;
        }
        foreach (var id in frame.Objects.Keys) Resolve(id);
        DirectorCreationIk.Apply(project, frame);
        resolved.Clear();
        foreach (var id in frame.Objects.Keys) Resolve(id);
        return frame;
    }

    public static void EvaluateBones(CreationModel model, CreationObjectState state)
    {
        HashSet<Guid> resolving = new();
        Matrix4x4 Resolve(CreationPart part)
        {
            if (state.Bones.TryGetValue(part.Id, out var cached)) return cached;
            if (!resolving.Add(part.Id)) return Matrix4x4.Identity;
            var pose = state.Pose.GetValueOrDefault(part.Id) ?? new CreationTransform();
            var matrix = DirectorCreationMath.BoneMatrix(part, pose, model.AnimationVersion);
            if (model.Parts.Find(p => p.Id == part.ParentId) is { } parent)
            {
                matrix *= Resolve(parent);
                if (state.HiddenParts.Contains(parent.Id) || !parent.Visible) state.HiddenParts.Add(part.Id);
            }
            if (!part.Visible) state.HiddenParts.Add(part.Id);
            resolving.Remove(part.Id);
            return state.Bones[part.Id] = matrix;
        }
        foreach (var part in model.Parts) Resolve(part);
    }

    private static void Apply(CreationObjectState state, CreationTrack track, double time, double weight)
    {
        if (track.Enabled && track.Keys.Count > 0 && track.PartId == Guid.Empty && track.Channel == CreationChannel.Position) state.HasPositionAnimation = true;
        bool bone = track.PartId != Guid.Empty;
        var tr = bone ? state.Pose.GetValueOrDefault(track.PartId) ?? new CreationTransform() : state.Transform;
        CreationVector original = track.Channel switch
        {
            CreationChannel.Position => tr.Position, CreationChannel.Rotation => tr.Rotation,
            CreationChannel.Scale => tr.Scale, CreationChannel.Pivot => tr.Pivot,
            CreationChannel.Color => state.Color, CreationChannel.Fov => new(state.Fov, 0, 0),
            CreationChannel.IkWeight => new(state.IkWeight, 0, 0),
            _ => new(bone ? state.HiddenParts.Contains(track.PartId) ? 0 : 1 : state.Visible ? 1 : 0, 0, 0)
        };
        var value = Sample(track, time, original);
        var delta = value - original;
        if (track.Channel == CreationChannel.Rotation) delta = new(Angle(delta.X), Angle(delta.Y), Angle(delta.Z));
        if (weight < 1) value = original + delta * weight;
        switch (track.Channel)
        {
            case CreationChannel.Position: tr.Position = value; break;
            case CreationChannel.Rotation: tr.Rotation = value; break;
            case CreationChannel.Scale: tr.Scale = value; break;
            case CreationChannel.Pivot: tr.Pivot = value; break;
            case CreationChannel.Color: state.Color = value; break;
            case CreationChannel.Fov: state.Fov = Math.Clamp(value.X, 1, 170); break;
            case CreationChannel.IkWeight: state.IkWeight = Math.Clamp(value.X, 0, 1); break;
            case CreationChannel.Visible:
                if (bone) { if (value.X >= .5) state.HiddenParts.Remove(track.PartId); else state.HiddenParts.Add(track.PartId); }
                else state.Visible = value.X >= .5;
                break;
        }
        if (bone) state.Pose[track.PartId] = tr;
    }

    public static CameraPreset? Camera(DirectorCreationProject project, CreationFrame frame)
    {
        if (!frame.Objects.TryGetValue(project.CameraId, out var state)) return null;
        Vector3 position = state.World.Translation;
        CreationVector rotation = DirectorCreationMath.NativeCameraRotation(state.World);
        var source = state.Source;
        if (frame.Objects.TryGetValue(source.TargetId, out var target))
        {
            Vector3 targetPosition = target.World.Translation;
            if (source.CameraMode == CreationCameraMode.Follow) position += targetPosition;
            if (source.CameraMode == CreationCameraMode.Orbit)
            {
                double radians = (rotation.Y + source.OrbitSpeed * frame.Time) * Math.PI / 180;
                position = targetPosition + new Vector3((float)(Math.Sin(radians) * source.OrbitRadius), position.Y, (float)(Math.Cos(radians) * source.OrbitRadius));
            }
            if (source.CameraMode is CreationCameraMode.LookAt or CreationCameraMode.Orbit or CreationCameraMode.Follow)
            {
                Vector3 delta = targetPosition - position;
                rotation = new(Math.Atan2(delta.Y, Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z)) * 180 / Math.PI,
                    Math.Atan2(-delta.X, -delta.Z) * 180 / Math.PI, rotation.Z);
            }
        }
        if (source.Shake > 0)
            rotation += new CreationVector(Math.Sin(frame.Time * 23.7), Math.Sin(frame.Time * 31.1), Math.Cos(frame.Time * 17.3)) * source.Shake;
        return new CameraPreset { X = project.Origin.X + position.X, Y = project.Origin.Y + position.Y, Z = project.Origin.Z + position.Z,
            Dimension = project.Dimension, Pitch = (float)rotation.X, Yaw = (float)rotation.Y, Roll = (float)rotation.Z, Fov = (float)state.Fov };
    }
}

public sealed class DirectorCreationHistory
{
    private sealed record Entry(string Name, DirectorCreationProject? Snapshot, CreationClip? Clip = null, double Duration = 0, double LoopEnd = 0);
    private readonly Stack<Entry> undo = new(), redo = new();
    public IEnumerable<string> Entries => undo.Select(e => e.Name);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void Record(DirectorCreationProject before, string name)
    {
        undo.Push(new(name, before)); redo.Clear();
        if (undo.Count > 100) { var keep = undo.Take(100).Reverse().ToArray(); undo.Clear(); foreach (var item in keep) undo.Push(item); }
    }
    public DirectorCreationProject Undo(DirectorCreationProject current) => Restore(current, undo, redo);
    public DirectorCreationProject Redo(DirectorCreationProject current) => Restore(current, redo, undo);
    public void RecordClip(CreationClip before, string name, double duration, double loopEnd)
    { undo.Push(new(name, null, CreationJson.Clone(before), duration, loopEnd)); redo.Clear(); }
    private static DirectorCreationProject Restore(DirectorCreationProject current, Stack<Entry> from, Stack<Entry> to)
    {
        if (from.Count == 0) return current;
        var entry = from.Pop();
        if (entry.Clip is { } clip)
        {
            int index = current.Clips.FindIndex(c => c.Id == clip.Id);
            if (index < 0) throw new InvalidOperationException("Missing clip for undo.");
            to.Push(new(entry.Name, null, CreationJson.Clone(current.Clips[index]), current.Duration, current.LoopEnd));
            current.Clips[index] = CreationJson.Clone(clip); current.Duration = entry.Duration; current.LoopEnd = entry.LoopEnd; return current;
        }
        to.Push(new(entry.Name, current.Clone()));
        return entry.Snapshot!.Clone();
    }
    public void Clear() { undo.Clear(); redo.Clear(); }
}

public static class DirectorCreationEdits
{
    public static CreationKeyframe SetKey(DirectorCreationProject project, Guid objectId, Guid partId, CreationChannel channel,
        double time, CreationVector value)
    {
        var track = project.Tracks.Find(t => t.ObjectId == objectId && t.PartId == partId && t.Channel == channel);
        if (track is null) { track = new() { ObjectId = objectId, PartId = partId, Channel = channel }; project.Tracks.Add(track); }
        return SetKey(track, time, value);
    }
    public static CreationKeyframe SetKey(CreationTrack track, double time, CreationVector value)
    {
        var key = track.Keys.Find(k => Math.Abs(k.Time - time) < 1e-6);
        if (key is null) { key = new() { Time = Math.Max(0, time) }; track.Keys.Add(key); }
        key.Value = value; track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time)); return key;
    }
    public static CreationClip Split(DirectorCreationProject project, CreationClip clip, double time)
    {
        if (time <= clip.Start || time >= clip.Start + clip.Duration) throw new ArgumentOutOfRangeException(nameof(time));
        var right = CreationJson.Clone(clip); right.Id = Guid.NewGuid(); right.Start = time;
        right.Duration = clip.Start + clip.Duration - time; right.SourceStart += (time - clip.Start) * clip.Speed;
        clip.Duration = time - clip.Start; clip.FadeOut = 0; right.FadeIn = 0;
        project.Clips.Add(right); return right;
    }
    public static void DeleteObject(DirectorCreationProject project, Guid id)
    {
        var frame = DirectorCreationEvaluator.Evaluate(project, 0);
        foreach (var child in project.Objects.Where(o => o.ParentId == id))
        {
            child.ParentId = child.ParentPartId = Guid.Empty;
            if (frame.Objects.TryGetValue(child.Id, out var state)) child.Transform = DirectorCreationMath.FromMatrix(state.World, child.Transform.Rotation);
        }
        project.Objects.RemoveAll(o => o.Id == id); project.Tracks.RemoveAll(t => t.ObjectId == id); project.Clips.RemoveAll(c => c.ObjectId == id);
        foreach (var obj in project.Objects) if (obj.TargetId == id) obj.TargetId = Guid.Empty;
        foreach (var obj in project.Objects) obj.IkChains.RemoveAll(c => c.Target == id || c.Pole == id);
        if (project.CameraId == id) project.CameraId = Guid.Empty;
    }
    public static void DeletePart(DirectorCreationProject project, CreationModel model, Guid id)
    {
        var part = model.Parts.Find(p => p.Id == id); if (part is null) return;
        foreach (var child in model.Parts.Where(p => p.ParentId == id)) child.ParentId = part.ParentId;
        model.Parts.Remove(part);
        foreach (var role in model.BodyMapping.Where(p => p.Value == id).Select(p => p.Key).ToArray()) model.BodyMapping.Remove(role);
        foreach (var obj in project.Objects)
        {
            if (obj.ModelId == model.Id) { obj.Pose.Remove(id); obj.HiddenParts.Remove(id); project.Tracks.RemoveAll(t => t.ObjectId == obj.Id && t.PartId == id); }
            if (obj.ModelId == model.Id) obj.IkChains.RemoveAll(c => c.Upper == id || c.Lower == id);
            if (obj.ParentPartId == id) obj.ParentPartId = Guid.Empty;
        }
        foreach (var animation in project.Animations.Where(a => a.ModelId == model.Id)) animation.Tracks.RemoveAll(t => t.PartId == id);
        foreach (var preset in project.Presets.Where(p => p.ModelId == model.Id))
        { preset.Pose.Remove(id); preset.Appearance?.Pose.Remove(id); preset.Appearance?.HiddenParts.Remove(id); }
    }
    public static DirectorCreationProject Duplicate(DirectorCreationProject source)
    {
        var copy = source.Clone(); copy.Id = Guid.NewGuid(); copy.Name += " (copy)";
        var map = new Dictionary<Guid, Guid>();
        Guid New(Guid id) { if (id == Guid.Empty) return id; if (!map.TryGetValue(id, out var n)) map[id] = n = Guid.NewGuid(); return n; }
        foreach (var a in copy.Assets) a.Id = New(a.Id);
        foreach (var m in copy.Models)
        {
            m.Id = New(m.Id);
            foreach (var p in m.Parts) { p.Id = New(p.Id); p.ParentId = New(p.ParentId); }
            foreach (string k in m.BodyMapping.Keys.ToArray()) m.BodyMapping[k] = New(m.BodyMapping[k]);
        }
        foreach (var o in copy.Objects)
        {
            o.Id = New(o.Id); o.ParentId = New(o.ParentId); o.ParentPartId = New(o.ParentPartId); o.TargetId = New(o.TargetId);
            o.ModelId = New(o.ModelId); o.AssetId = New(o.AssetId);
            foreach (var chain in o.IkChains) { chain.Id = New(chain.Id); chain.Upper = New(chain.Upper); chain.Lower = New(chain.Lower); chain.Target = New(chain.Target); chain.Pole = New(chain.Pole); }
            o.Pose = o.Pose.ToDictionary(p => New(p.Key), p => p.Value); o.HiddenParts = o.HiddenParts.Select(New).ToHashSet();
        }
        void Track(CreationTrack t) { t.Id = New(t.Id); t.ObjectId = New(t.ObjectId); t.PartId = New(t.PartId); foreach (var k in t.Keys) k.Id = New(k.Id); }
        foreach (var t in copy.Tracks) Track(t);
        foreach (var a in copy.Animations) { a.Id = New(a.Id); a.ModelId = New(a.ModelId); foreach (var t in a.Tracks) Track(t); }
        foreach (var c in copy.Clips) { c.Id = New(c.Id); c.ObjectId = New(c.ObjectId); c.AnimationId = New(c.AnimationId); c.AudioAssetId = New(c.AudioAssetId); c.PerformanceAssetId = New(c.PerformanceAssetId); }
        foreach (var p in copy.Presets)
        {
            p.Id = New(p.Id); p.ModelId = New(p.ModelId); p.Pose = p.Pose.ToDictionary(x => New(x.Key), x => x.Value);
            if (p.Appearance is { } o)
            {
                o.Id = New(o.Id); o.ParentId = New(o.ParentId); o.ParentPartId = New(o.ParentPartId); o.TargetId = New(o.TargetId);
                o.ModelId = New(o.ModelId); o.AssetId = New(o.AssetId);
                o.Pose = o.Pose.ToDictionary(x => New(x.Key), x => x.Value);
                o.HiddenParts = o.HiddenParts.Select(New).ToHashSet();
            }
        }
        copy.CameraId = New(copy.CameraId); copy.BackgroundClipId = New(copy.BackgroundClipId); return copy;
    }
}
