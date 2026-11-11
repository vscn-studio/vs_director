namespace VSDirector.Data;

public sealed class CreationPoseSnapshot
{
    public Guid Actor { get; set; }
    public Guid Animation { get; set; }
    public double Time { get; set; }
    public Dictionary<Guid, CreationTransform> Bones { get; set; } = new();
    public HashSet<Guid> Hidden { get; set; } = new();
}

public sealed partial class DirectorCreationSession
{
    public Guid ScenePoseActor { get; private set; }
    public Guid PoseActor => ScenePoseActor != Guid.Empty ? ScenePoseActor : AnimationObject;
    public bool ScenePoseEditing => ScenePoseActor != Guid.Empty;
    public bool PoseEditing => ScenePoseEditing || EditingAnimation?.PoseEditing == true;
    public void BeginScenePose(Guid actor)
    {
        if(Project?.Objects.Find(o=>o.Id==actor && o.ModelId!=Guid.Empty) is null) return;
        LeaveAnimation(); EditTask = CreationEditTask.Animation; ScenePoseActor=SelectedObject=actor; SelectedPart=Guid.Empty; Play(false); Refresh();
    }
    public void EndScenePose() { ScenePoseActor=Guid.Empty; SelectedPart=Guid.Empty; ClearTransformDrafts(); }

    private CreationPoseSnapshot? poseDraft, poseGesture;
    private bool poseGestureHadDraft;
    private readonly Stack<CreationPoseSnapshot> poseUndo = new(), poseRedo = new();
    public bool HasPoseDraft => poseDraft is not null;
    public CreationPoseSnapshot CapturePose()
    {
        if (Frame?.Objects.GetValueOrDefault(PoseActor) is not { } state) throw new InvalidOperationException("Select an animation actor first.");
        var model = Project!.Models.First(m => m.Id == state.Source.ModelId);
        return new() { Actor = PoseActor, Animation = SelectedAnimation, Time = EditTime,
            Bones = model.Parts.ToDictionary(p => p.Id, p => (state.Pose.GetValueOrDefault(p.Id) ?? new()) with { }), Hidden = new(state.ExplicitHiddenParts) };
    }
    private void ResetPoseDraft()
    { poseDraft = poseGesture = null; poseUndo.Clear(); poseRedo.Clear(); }
    private bool BeginPoseGesture()
    {
        if (!PoseEditing) return false;
        if(poseGesture is null) { poseGestureHadDraft=poseDraft is not null;poseGesture=CreationJson.Clone(poseDraft ?? CapturePose()); } return true;
    }
    private bool EndPoseGesture()
    {
        if (poseGesture is null) return false;
        if (poseDraft is not null && System.Text.Json.JsonSerializer.Serialize(poseGesture, CreationJson.Options) != System.Text.Json.JsonSerializer.Serialize(poseDraft, CreationJson.Options))
        { poseUndo.Push(poseGesture); poseRedo.Clear(); }
        poseGesture = null; return true;
    }
    public void SetPosePart(Guid part, CreationTransform transform)
    {
        if(ScenePoseEditing && Frame?.Objects.GetValueOrDefault(PoseActor)?.HasPerformance==true) throw new InvalidOperationException("Recorded performance owns this pose time.");
        if (!PoseEditing || Project!.Models.First(m => m.Id == Project.Objects.First(o=>o.Id==PoseActor).ModelId).Parts.All(p => p.Id != part)) return;
        if (!transform.Position.Finite() || !transform.Rotation.Finite() || !transform.Scale.Finite() || !transform.Pivot.Finite()) throw new InvalidDataException("Non-finite pose transform.");
        Playing = false; poseDraft ??= CapturePose(); poseDraft.Bones[part] = transform with { };
    }
    public void ApplyPose(CreationPoseSnapshot pose)
    {
        BeginGesture(); var current = CapturePose();
        foreach (var id in current.Bones.Keys.ToArray())
            if (pose.Bones.TryGetValue(id, out var value)) current.Bones[id] = value with { };
        current.Hidden = pose.Hidden.Where(current.Bones.ContainsKey).ToHashSet(); poseDraft = current;
        Changed(); EndGesture("Pose");
    }
    public void RestoreRestPose()
    { var pose = CapturePose(); foreach (var id in pose.Bones.Keys.ToArray()) pose.Bones[id] = new(); pose.Hidden.Clear(); ApplyPose(pose); }
    public void SavePose()
    {
        if (!PoseEditing) return;
        if(ScenePoseEditing && Frame?.Objects.GetValueOrDefault(PoseActor)?.HasPerformance==true) throw new InvalidOperationException("Recorded performance owns this pose time.");
        EndGesture("Pose"); var pose = CapturePose();
        Edit("Save pose", _ =>
        {
            if(ScenePoseEditing) DirectorCreationScenePoses.Save(Project!.Objects.First(o=>o.Id==PoseActor),pose,Time);
            else WritePose(EditingAnimation!, pose, AnimationTime);
            foreach (var entry in drafts.ToArray())
                foreach (var channel in draftChannels.GetValueOrDefault(entry.Key, new()).ToArray())
                    WriteKey(entry.Key.Object, entry.Key.Part, channel, Component(entry.Value, channel));
            foreach (var entry in propertyDrafts.ToArray())
                WriteKey(entry.Key.Object, entry.Key.Part, entry.Key.Channel, entry.Value);
        });
        // Retain the pose draft until validation succeeds, and commit other preview
        // properties in the same undoable operation instead of leaving stale drafts.
        ClearTransformDrafts();
    }
    public static void WritePose(CreationAnimation animation, CreationPoseSnapshot pose, double time)
    {
        void Key(Guid part, CreationChannel channel, CreationVector value)
        {
            var track = animation.Tracks.Find(t => t.ObjectId == Guid.Empty && t.PartId == part && t.Channel == channel);
            if (track is null) { track = new() { PartId = part, Channel = channel }; animation.Tracks.Add(track); }
            track.Enabled = true;
            DirectorCreationEdits.SetKey(track, time, value);
        }
        foreach (var (part, tr) in pose.Bones)
        {
            Key(part, CreationChannel.Position, tr.Position); Key(part, CreationChannel.Rotation, tr.Rotation);
            Key(part, CreationChannel.Scale, tr.Scale); Key(part, CreationChannel.Pivot, tr.Pivot);
            Key(part, CreationChannel.Visible, new(pose.Hidden.Contains(part) ? 0 : 1, 0, 0));
        }
        Key(Guid.Empty, CreationChannel.IkWeight, default);
    }
    public static void CopyPoseTime(CreationAnimation animation, double source, double target, bool move)
    {
        if (!double.IsFinite(target) || target < 0 || target > animation.Duration) throw new InvalidDataException("Invalid pose time.");
        if (Math.Abs(source-target)<1e-6) return;
        foreach (var track in animation.Tracks.Where(t=>t.PartId!=Guid.Empty))
        {
            var keys=track.Keys.Where(k=>Math.Abs(k.Time-source)<1e-6).Select(k=>k.Copy()).ToArray();
            if(keys.Length==0) continue;
            track.Keys.RemoveAll(k=>Math.Abs(k.Time-target)<1e-6 || move && Math.Abs(k.Time-source)<1e-6);
            foreach(var key in keys) { if(!move) key.Id=Guid.NewGuid(); key.Time=target; track.Keys.Add(key); }
        }
    }
    private bool UndoPose(bool redo)
    {
        if (!PoseEditing) return false;
        EndPoseGesture(); var from = redo ? poseRedo : poseUndo; var to = redo ? poseUndo : poseRedo;
        if (from.Count == 0) return poseDraft is not null;
        to.Push(CreationJson.Clone(poseDraft ?? CapturePose())); poseDraft = from.Pop(); Changed(); return true;
    }
    private void RestorePoseRecovery(CreationPoseSnapshot? recovery)
    {
        ResetPoseDraft();
        if (recovery is null || Project!.Objects.All(o => o.Id != recovery.Actor || o.ModelId==Guid.Empty)) return;
        if(recovery.Animation==Guid.Empty) { BeginScenePose(recovery.Actor); Seek(recovery.Time); }
        else
        {
            if(Project.Animations.All(a=>a.Id!=recovery.Animation || !a.PoseEditing)) return;
            EnterAnimation(recovery.Animation,recovery.Actor); AnimationTime=Math.Clamp(recovery.Time,0,EditDuration);
        }
        var model = Project.Models.First(m => m.Id == Project.Objects.First(o=>o.Id==PoseActor).ModelId);
        if (recovery.Bones.Keys.Any(id => model.Parts.All(p => p.Id != id))) return;
        if (recovery.Bones.Values.Any(t => !t.Position.Finite() || !t.Rotation.Finite() || !t.Scale.Finite() || !t.Pivot.Finite())) return;
        poseDraft = CreationJson.Clone(recovery); Dirty = true; Refresh();
    }
}
