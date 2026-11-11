namespace VSDirector.Data;

public enum CreationEditTask { Placement, Animation }
public enum CreationWriteTarget { Base, ProjectTrack, ScenePose, ActionTrack, ActionPose, Recorded }
public readonly record struct CreationEditingContext(CreationEditTask Task, CreationWriteTarget Target, Guid Actor, Guid Part, Guid Animation, double Time);

public sealed partial class DirectorCreationSession
{
    public CreationEditTask EditTask { get; private set; }
    public bool AnimationEditing => EditTask == CreationEditTask.Animation || PoseEditing || EditingAnimation is not null;
    public void SetEditTask(CreationEditTask task)
    {
        if (HasTransformDraft) throw new InvalidOperationException("Resolve the preview before changing editing tasks.");
        EndGesture("Edit"); Play(false);
        if (task == CreationEditTask.Placement) { LeaveAnimation(); ScenePoseActor = Guid.Empty; SelectedPart = Guid.Empty; }
        EditTask = task; Refresh();
    }
    public CreationEditingContext EditingContext(Guid actor, Guid part, CreationChannel channel)
    {
        var target = !AnimationEditing ? CreationWriteTarget.Base
            : IsRecordedChannel(actor, part, channel) ? CreationWriteTarget.Recorded
            : PoseEditing && actor == PoseActor && part != Guid.Empty
                ? ScenePoseEditing ? CreationWriteTarget.ScenePose : CreationWriteTarget.ActionPose
                : EditingAnimation is null ? CreationWriteTarget.ProjectTrack : CreationWriteTarget.ActionTrack;
        return new(EditTask, target, actor, part, SelectedAnimation, EditTime);
    }
    public bool IsRecordedChannel(Guid actor, Guid part, CreationChannel channel)
    {
        if (EditingAnimation is not null || Project is null) return false;
        return Project.Clips.Where(c => c.Enabled && c.ObjectId == actor && c.PerformanceAssetId != Guid.Empty
            && Time >= c.Start && (Time < c.Start + c.Duration || Time == Project.Duration && Time == c.Start + c.Duration))
            .Any(c => Project.Performances.TryGetValue(c.PerformanceAssetId, out var take) &&
                (c.BakedPose ? Project.Animations.Find(a => a.Id == c.AnimationId)?.Tracks : take.Tracks)?.Any(t => t.Enabled && t.PartId == part && t.Channel == channel && t.Keys.Count > 0) == true);
    }
    private CreationVector BaseComponent(Guid actor, Guid part, CreationChannel channel)
    {
        var obj = Project!.Objects.First(o => o.Id == actor);
        var transform = part == Guid.Empty ? obj.Transform : obj.Pose.GetValueOrDefault(part) ?? new();
        return channel switch { CreationChannel.Color => obj.Color, CreationChannel.Fov => new(obj.Fov,0,0),
            CreationChannel.Visible => new(part == Guid.Empty ? (obj.Visible ? 1 : 0) : (obj.HiddenParts.Contains(part) ? 0 : 1),0,0),
            CreationChannel.IkWeight => new(obj.IkWeight,0,0), _ => Component(transform,channel) };
    }
    private void SeedKey(Guid actor, Guid part, CreationChannel channel)
    {
        if (EditTime <= 0) return;
        Guid target = EditingAnimation is not null && actor == AnimationObject ? Guid.Empty : actor;
        var track = EditTracks.Find(t => t.ObjectId == target && t.PartId == part && t.Channel == channel);
        if (track?.Keys.Count > 0) return;
        if (track is null) { track = new() { ObjectId = target, PartId = part, Channel = channel }; EditTracks.Add(track); }
        DirectorCreationEdits.SetKey(track, 0, BaseComponent(actor,part,channel));
    }
    public void CancelGesture()
    {
        if (gesture is not null) { Project = gesture; gesture = null; }
        if (clipGesture is not null && Project is not null)
        { int i = Project.Clips.FindIndex(c => c.Id == clipGesture.Id); if (i >= 0) Project.Clips[i] = clipGesture; Project.Duration = clipGestureDuration; Project.LoopEnd = clipGestureLoopEnd; clipGesture = null; }
        if (poseGesture is not null) poseDraft = poseGestureHadDraft ? CreationJson.Clone(poseGesture) : null;
        poseGesture = null; drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear();
        if(gestureDrafts is not null)
        {
            foreach(var pair in gestureDrafts)drafts[pair.Key]=pair.Value;
            foreach(var pair in gestureChannels!)draftChannels[pair.Key]=pair.Value;
            foreach(var pair in gestureProperties!)propertyDrafts[pair.Key]=pair.Value;
        }
        gestureDrafts=null;gestureChannels=null;gestureProperties=null;
        Changed();
    }
    private void CommitAutoPose()
    {
        if (!AutoKey || poseDraft is null || !PoseEditing) return;
        if (ScenePoseEditing && Frame?.Objects.GetValueOrDefault(PoseActor)?.HasPerformance == true)
            throw new InvalidOperationException("Recorded performance owns this pose time.");
        var before = poseGesture ?? CapturePose();
        if(before.Hidden.SetEquals(poseDraft.Hidden) && before.Bones.Count==poseDraft.Bones.Count
            && before.Bones.All(p=>poseDraft.Bones.TryGetValue(p.Key,out var value)&&value==p.Value))
        { ResetPoseDraft();return; }
        if (ScenePoseEditing)
        {
            var actor = Project!.Objects.First(o => o.Id == PoseActor);
            if (actor.PoseKeys.Count == 0 && Time > 0) DirectorCreationScenePoses.Save(actor,before,0);
            DirectorCreationScenePoses.Save(actor,poseDraft,Time);
        }
        else
        {
            if (!EditingAnimation!.Tracks.Any(t => t.PartId != Guid.Empty && t.Keys.Count > 0) && AnimationTime > 0) WritePose(EditingAnimation,before,0);
            WritePose(EditingAnimation,poseDraft,AnimationTime);
        }
        ResetPoseDraft();
    }
}
