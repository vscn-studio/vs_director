using System.Numerics;
using System.Text.Json;

namespace VSDirector.Data;

public sealed record CreationProjectEntry(Guid Id, string Name, string Folder, DateTimeOffset ModifiedUtc, bool Recovery, string WorldId = "", int Dimension = 0);

/// <summary>Independent project storage; replay files and workspace settings are never rewritten.</summary>
public sealed class DirectorCreationStore
{
    private readonly Dictionary<(Guid Project, string Source), string> performanceErrors = new();
    public string? PerformanceError(DirectorCreationProject project, string source)
        => performanceErrors.GetValueOrDefault((project.Id, source));
    public void LoadPerformances(DirectorCreationProject project, bool all = false, bool retry = false)
    {
        var needed = DirectorCreationDependencies.PerformanceClips(project).Select(c => c.PerformanceAssetId).ToHashSet();
        foreach (var asset in project.Assets.Where(a => a.Kind == CreationAssetKind.Performance && (all || needed.Contains(a.Id))))
        {
            if (project.Performances.ContainsKey(asset.Id)) continue;
            var key = (project.Id, asset.Source);
            if (!retry && performanceErrors.ContainsKey(key)) continue;
            try
            {
                string path = AssetPath(project.Id, asset.Source);
                var take = JsonSerializer.Deserialize<CreationPerformance>(File.ReadAllText(path), CreationJson.Options)
                    ?? throw new InvalidDataException("Invalid recorded performance.");
                take.Validate(); project.Performances[asset.Id] = take; performanceErrors.Remove(key);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
            { performanceErrors[key] = e.Message; }
        }
    }
    public string Root { get; }
    public DirectorCreationStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public string DirectoryFor(Guid id) => Path.Combine(Root, id.ToString("N"));
    public string AssetPath(Guid id, string relative)
    {
        string directory = DirectoryFor(id) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材路径必须在作品目录内。");
        return path;
    }
    public IReadOnlyList<CreationProjectEntry> List()
    {
        var entries = new List<CreationProjectEntry>();
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out Guid id)) continue;
            string path = Path.Combine(directory, "project.json"), recovery = Path.Combine(directory, "recovery.json");
            try
            {
                var project = Read(File.Exists(path) ? path : recovery);
                entries.Add(new(id, project.Name, project.Folder, project.ModifiedUtc,
                    File.Exists(recovery) && (!File.Exists(path) || File.GetLastWriteTimeUtc(recovery) >= File.GetLastWriteTimeUtc(path)), project.WorldId, project.Dimension));
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException) { /* Other projects remain accessible. */ }
        }
        return entries.OrderByDescending(e => e.ModifiedUtc).ToArray();
    }
    public DirectorCreationProject Load(Guid id, bool recover = false)
    {
        string directory = DirectoryFor(id);
        var project = Read(Path.Combine(directory, recover ? "recovery.json" : "project.json"));
        if (project.Id != id) throw new InvalidDataException("作品标识与目录不匹配。");
        return project;
    }
    private static DirectorCreationProject Read(string path)
    {
        var project = JsonSerializer.Deserialize<DirectorCreationProject>(File.ReadAllText(path), CreationJson.Options) ?? throw new InvalidDataException("作品为空。");
        project.Validate(); return project;
    }
    public void Save(DirectorCreationProject project, bool recovery = false)
    {
        project.Validate(); project.ModifiedUtc = DateTimeOffset.UtcNow;
        string directory = DirectoryFor(project.Id); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, recovery ? "recovery.json" : "project.json");
        AtomicWrite(path, JsonSerializer.Serialize(project, CreationJson.Options));
        if (!recovery)
        {
            string recoveryPath = Path.Combine(directory, "recovery.json");
            if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
        }
    }
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            { writer.Write(text); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public CreationAsset Import(DirectorCreationProject project, string source, CreationAssetKind kind)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到素材。", source);
        var asset = new CreationAsset { Kind = kind, Name = Path.GetFileNameWithoutExtension(source), LastUsedUtc = DateTimeOffset.UtcNow };
        asset.Source = "assets/" + asset.Id.ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        string destination = AssetPath(project.Id, asset.Source); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination); project.Assets.Add(asset); return asset;
    }
    public DirectorCreationProject Duplicate(DirectorCreationProject project)
    {
        LoadPerformances(project, all: true, retry: true);
        var copy = DirectorCreationEdits.Duplicate(project);
        string assets = Path.Combine(DirectoryFor(project.Id), "assets");
        if (Directory.Exists(assets))
            foreach (string source in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                string destination = AssetPath(copy.Id, "assets/" + Path.GetRelativePath(assets, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
            }
        var partMap = project.Models.Zip(copy.Models).SelectMany(pair => pair.First.Parts.Zip(pair.Second.Parts).Select(parts => (parts.First.Id, NewId: parts.Second.Id))).ToDictionary(p => p.Id, p => p.NewId);
        foreach (var pair in project.Assets.Zip(copy.Assets).Where(p => p.First.Kind == CreationAssetKind.Performance))
        {
            if (!project.Performances.TryGetValue(pair.First.Id, out var original)) continue;
            var take = CreationJson.Clone(original);
            if (take.WorldSource.Length > 0)
            {
                string source = AssetPath(project.Id, take.WorldSource);
                string destination = AssetPath(copy.Id, take.WorldSource);
                if (!File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
            }
            foreach (var track in take.Tracks) if (track.PartId != Guid.Empty) track.PartId = partMap.GetValueOrDefault(track.PartId, track.PartId);
            AtomicWrite(AssetPath(copy.Id, pair.Second.Source), JsonSerializer.Serialize(take, CreationJson.Options));
        }
        LoadPerformances(copy, all: true); Save(copy); return copy;
    }
    public DirectorCreationProject CopyToWorld(DirectorCreationProject source, string worldId, int dimension)
    {
        if (string.IsNullOrWhiteSpace(worldId)) throw new ArgumentException("A destination world is required.", nameof(worldId));
        var copy = Duplicate(source);
        copy.WorldId = worldId; copy.Dimension = dimension;
        // Keep motion, poses and local recording files, but do not apply another
        // world's block history or bind actors to live players on opening.
        copy.SetBackground(Guid.Empty); copy.UsePerformanceWorld = false;
        foreach (var actor in copy.Objects) actor.ReplacePlayerUid = "";
        foreach (var preset in copy.Presets)
            if (preset.Appearance is { } appearance) appearance.ReplacePlayerUid = "";
        Save(copy);
        return copy;
    }
    public void Delete(Guid id)
    {
        // Move to a local trash directory so an accidental deletion can be recovered.
        string source = DirectoryFor(id);
        if (!Directory.Exists(source)) return;
        string trash = Path.Combine(Root, ".trash"); Directory.CreateDirectory(trash);
        Directory.Move(source, Path.Combine(trash, id.ToString("N") + "-" + DateTime.UtcNow.Ticks));
    }
}

/// <summary>One clock and one edit history per open project.</summary>
public sealed partial class DirectorCreationSession
{
    public DirectorCreationStore Store { get; }
    public DirectorCreationProject? Project { get; private set; }
    public DirectorCreationHistory History { get; } = new();
    public Guid SelectedObject { get; set; }
    public Guid SelectedPart { get; set; }
    public Guid SelectedAnimation { get; set; }
    public double Time { get; private set; }
    public bool Playing { get; private set; }
    public bool Dirty { get; private set; }
    public long Revision { get; private set; }
    public long DocumentGeneration { get; private set; }
    public CreationFrame? Frame { get; private set; }
    private double sinceSave;
    private Task? recoverySave;
    public void FlushRecovery() { recoverySave?.GetAwaiter().GetResult(); recoverySave = null; }
    private readonly Dictionary<(Guid Object, Guid Part), CreationTransform> drafts = new();
    public bool HasTransformDraft => HasPoseDraft || drafts.Count > 0 || propertyDrafts.Count > 0;
    public void ClearTransformDrafts() { ResetPoseDraft(); drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Refresh(); }

    private DirectorCreationProject? gesture;
    private long gestureRevision;
    private CreationClip? clipGesture;
    private double clipGestureDuration, clipGestureLoopEnd;
    public void BeginClipGesture(CreationClip clip)
    { if (clipGesture is null && Project is not null) { EndGesture("编辑"); clipGesture = CreationJson.Clone(clip); clipGestureDuration = Project.Duration; clipGestureLoopEnd = Project.LoopEnd; } }
    private readonly List<(double Time, CreationVector Position, CreationVector Rotation)> recorded = new();
    private Guid recordingObject;
    public bool Recording { get; private set; }
    public bool ExternalRecording { get; set; }
    public DirectorCreationSession(DirectorCreationStore store) => Store = store;
    public void Open(DirectorCreationProject project)
    {
        FlushRecovery();
        DocumentGeneration++;
        StopRecording();
        if (Project is not null && Dirty) Save();
        ScenePoseActor=Guid.Empty; ResetPoseDraft(); project.Validate(); Project = project; History.Clear(); SelectedObject = SelectedPart = SelectedAnimation = Guid.Empty;
        Time = 0; Playing = Recording = Dirty = ExternalRecording = false; EditTask = CreationEditTask.Placement; AutoKey = true; AnimationTime = 0; AnimationObject = Guid.Empty; gesture = null; clipGesture = null; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Revision++; Refresh();
        RestorePoseRecovery(project.PoseRecovery); project.PoseRecovery = null;
    }
    public void Edit(string label, Action<DirectorCreationProject> edit)
    {
        if (Project is null) return;
        EndGesture("编辑");
        var before = Project.Clone();
        try { edit(Project); Project.Validate(); }
        catch { Project = before; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Revision++; Refresh(); throw; }
        History.Record(before, label); Changed();
    }
    private Dictionary<(Guid Object,Guid Part),CreationTransform>? gestureDrafts;
    private Dictionary<(Guid Object,Guid Part),HashSet<CreationChannel>>? gestureChannels;
    private Dictionary<(Guid Object,Guid Part,CreationChannel Channel),CreationVector>? gestureProperties;
    public void BeginGesture()
    {
        BeginPoseGesture();
        if (gesture is null && clipGesture is null && Project is not null)
        {
            gesture = Project.Clone(); gestureRevision = Revision;
            gestureDrafts=drafts.ToDictionary(p=>p.Key,p=>p.Value with{});
            gestureChannels=draftChannels.ToDictionary(p=>p.Key,p=>new HashSet<CreationChannel>(p.Value));
            gestureProperties=new(propertyDrafts);
        }
    }
    public void EndGesture(string label)
    {
        gestureDrafts=null;gestureChannels=null;gestureProperties=null;
        CommitAutoPose();
        bool poseChanged = EndPoseGesture();
        if (clipGesture is { } before && Project is not null)
        {
            clipGesture = null;
            var clip = Project.Clips.Find(c => c.Id == before.Id);
            if (clip is not null && JsonSerializer.Serialize(before, CreationJson.Options) != JsonSerializer.Serialize(clip, CreationJson.Options))
            {
                History.RecordClip(before, label, clipGestureDuration, clipGestureLoopEnd);
                Project.Duration = Math.Max(Project.Duration, clip.Start + clip.Duration);
                if (clipGestureLoopEnd >= clipGestureDuration) Project.LoopEnd = Project.Duration;
            }
            return;
        }
        if (gesture is null || Project is null) return;
        if (Revision == gestureRevision) { gesture = null; return; }
        try { Project.Validate(); }
        catch { Project = gesture; gesture = null; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Revision++; Refresh(); throw; }
        if ((!poseChanged && !PoseEditing) || JsonSerializer.Serialize(gesture, CreationJson.Options) != JsonSerializer.Serialize(Project, CreationJson.Options)) History.Record(gesture, label);
        gesture = null;
    }
    public void Changed() { Dirty = true; Revision++; Refresh(); }
    public void Undo() { if (UndoPose(false)) return; if (Project is null) return; EndGesture("编辑"); if (!History.CanUndo) return; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Project = History.Undo(Project); Changed(); }
    public void Redo() { if (UndoPose(true)) return; if (Project is null || !History.CanRedo) return; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Project = History.Redo(Project); Changed(); }
    public void Save() { if (Project is null) return; FlushRecovery(); StopRecording(); EndGesture("编辑"); Store.Save(Project); Dirty = false; sinceSave = 0; }
    public void Seek(double time) { ResetPoseDraft(); drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); if (EditingAnimation is { } a) AnimationTime = Math.Clamp(double.IsFinite(time) ? time : 0, 0, a.Duration); else Time = Math.Clamp(double.IsFinite(time) ? time : 0, 0, Project?.Duration ?? 0); Refresh(); }
    public void Play(bool value)
    {
        if (!value && Recording) { StopRecording(); return; }
        if (value && Project is not null && EditTime >= EditDuration) Seek(EditingAnimation is null && Project.Loop ? Project.LoopStart : 0);
        if (value) { ResetPoseDraft(); drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear(); Refresh(); }
        Playing = value;
    }
    public void Tick(double delta)
    {
        if (Project is null) return;
        if (Playing)
        {
            double next = EditTime + Math.Clamp(delta, 0, .25);
            double start = EditingAnimation is not null ? 0 : Math.Clamp(Project.LoopStart, 0, Math.Max(0, Project.Duration - .001));
            bool loop = (EditingAnimation?.Loop ?? Project.Loop) && !Recording && !ExternalRecording;
            double end = EditingAnimation?.Duration ?? (loop ? Math.Clamp(Project.LoopEnd, start + .001, Project.Duration) : Project.Duration);
            if (next >= end) { if (loop) next = start + (next - end) % (end - start); else { next = end; Playing = false; } }
            Seek(next);
        }
        sinceSave += delta;
        if (recoverySave?.IsCompleted == true) FlushRecovery();
        if (Dirty && gesture is null && clipGesture is null && !ExternalRecording && sinceSave >= 30 && recoverySave is null)
        {
            var snapshot = Project.Clone();
            snapshot.PoseRecovery = poseDraft is null ? null : CreationJson.Clone(poseDraft);
            recoverySave = Task.Run(() => Store.Save(snapshot, recovery: true)); sinceSave = 0;
        }
    }
    public void AdvancePerformance(double time)
    {
        if (!ExternalRecording || Project is null || !double.IsFinite(time)) return;
        time = Math.Clamp(time, 0, 86400);
        if (time >= Project.Duration)
        {
            bool extendLoop = Project.LoopEnd >= Project.Duration;
            Project.Duration = Math.Min(86400, Math.Max(Project.Duration, time + DirectorCreationTiming.FrameDuration));
            if (extendLoop) Project.LoopEnd = Project.Duration;
            Dirty = true;
        }
        Seek(time);
    }
    public Action<DirectorCreationProject, CreationFrame>? FrameReady { get; set; }
    public void Refresh()
    {
        if(SelectedAnimation!=Guid.Empty && (EditingAnimation is null || Project?.Objects.Any(o=>o.Id==AnimationObject)!=true))
        { SelectedAnimation=AnimationObject=Guid.Empty;AnimationTime=0;ResetPoseDraft(); }
        if (ScenePoseEditing && Project?.Objects.Any(o=>o.Id==ScenePoseActor && o.ModelId!=Guid.Empty)!=true)
        { ScenePoseActor=Guid.Empty; SelectedPart=Guid.Empty; ResetPoseDraft(); }
        if (Project is not null) Store.LoadPerformances(Project);
        Frame = Project is null ? null : EvaluateEditingFrame();
        if (Project is not null && Frame is not null) FrameReady?.Invoke(Project, Frame);
    }
    public void StartRecording()
    {
        if (EditingAnimation is not null || Project?.Objects.Find(o => o.Id == SelectedObject && o.Kind == CreationObjectKind.Model) is null) return;
        StopRecording(); recorded.Clear(); recordingObject = SelectedObject; Recording = true; Play(true);
    }
    public void RecordSample(CreationVector absolutePosition, CreationVector rotation)
    {
        if (!Recording || Project is null || Frame is null) return;
        var obj = Project.Objects.Find(o => o.Id == recordingObject);
        if (obj is null) { StopRecording(); return; }
        if (recorded.Count > 0 && Time < recorded[^1].Time) { StopRecording(); return; }
        if (recorded.Count > 0 && Time - recorded[^1].Time < 1d / 30) return;
        var transform = obj.Transform with { Position = absolutePosition - Project.Origin, Rotation = rotation };
        Matrix4x4 local = transform.Matrix();
        if (Frame.Objects.TryGetValue(obj.ParentId, out var parent))
        {
            Matrix4x4 parentWorld = parent.World;
            if (obj.ParentPartId != Guid.Empty && parent.Bones.TryGetValue(obj.ParentPartId, out var bone)) parentWorld = bone * parentWorld;
            if (!Matrix4x4.Invert(parentWorld, out var inverse)) throw new InvalidDataException("父对象缩放为零，无法录制表演。");
            local *= inverse;
        }
        var pose = DirectorCreationMath.FromMatrix(local, recorded.Count > 0 ? recorded[^1].Rotation : obj.Transform.Rotation);
        // FromMatrix returns the affine translation; remove the pivot contribution.
        var linear = local; linear.Translation = Vector3.Zero;
        var position = pose.Position - obj.Transform.Pivot + CreationVector.From(Vector3.TransformNormal(obj.Transform.Pivot.Float(), linear));
        recorded.Add((Time, position, pose.Rotation));
    }
    public void StopRecording()
    {
        Recording = false; Play(false);
        if (recorded.Count == 0) return;
        Guid id = recordingObject; recordingObject = Guid.Empty;
        if (Project?.Objects.All(o => o.Id != id) != false) { recorded.Clear(); return; }
        Edit("录制角色表演", p => { foreach (var sample in recorded) { DirectorCreationEdits.SetKey(p, id, Guid.Empty, CreationChannel.Position, sample.Time, sample.Position); DirectorCreationEdits.SetKey(p, id, Guid.Empty, CreationChannel.Rotation, sample.Time, sample.Rotation); } });
        recorded.Clear();
    }
}

// Stable extension contracts operate on evaluated local data, never on shared engine shapes.
public interface ICreationVisualProvider { string Id { get; } IEnumerable<CreationPart> Geometry(CreationSceneObject obj, double time); }
public interface ICreationCameraEffect { string Id { get; } CameraPreset Evaluate(CameraPreset pose, double time); }
public interface ICreationAnimationBehavior { string Id { get; } void Evaluate(CreationObjectState state, double time); }
public interface ICreationResourceSource { string Id { get; } IEnumerable<CreationAsset> Search(string query); }

public sealed class DirectorCreationExtensions
{
    public Dictionary<string, ICreationVisualProvider> Visuals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ICreationCameraEffect> CameraEffects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ICreationAnimationBehavior> Behaviors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ICreationResourceSource> Resources { get; } = new(StringComparer.Ordinal);
    public void Register(ICreationVisualProvider provider) => Visuals.Add(provider.Id, provider);
    public void Register(ICreationCameraEffect effect) => CameraEffects.Add(effect.Id, effect);
    public void Register(ICreationAnimationBehavior behavior) => Behaviors.Add(behavior.Id, behavior);
    public void Register(ICreationResourceSource source) => Resources.Add(source.Id, source);
}
