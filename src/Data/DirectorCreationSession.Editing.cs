namespace VSDirector.Data;

public sealed partial class DirectorCreationSession
{
    public bool AutoKey { get; set; }
    public double AnimationTime { get; private set; }
    public Guid AnimationObject { get; private set; }
    public CreationAnimation? EditingAnimation => Project?.Animations.Find(a => a.Id == SelectedAnimation);
    public double EditTime => EditingAnimation is null ? Time : AnimationTime;
    public double EditDuration => EditingAnimation?.Duration ?? Project?.Duration ?? 0;
    public List<CreationTrack> EditTracks => EditingAnimation?.Tracks ?? Project!.Tracks;
    private readonly Dictionary<(Guid Object, Guid Part), HashSet<CreationChannel>> draftChannels = new();
    private readonly Dictionary<(Guid Object, Guid Part, CreationChannel Channel), CreationVector> propertyDrafts = new();
    public CreationVector PropertyValue(Guid objectId, Guid partId, CreationChannel channel, CreationVector fallback)
        => propertyDrafts.GetValueOrDefault((objectId, partId, channel), fallback);
    private bool IsAnimated(Guid objectId, Guid partId, CreationChannel channel) => EditingAnimation is not null
        || Project!.Tracks.Any(t => t.Enabled && t.ObjectId == objectId && t.PartId == partId && t.Channel == channel && t.Keys.Count > 0)
        || Project.Clips.Any(c => c.Enabled && c.ObjectId == objectId && (Project.Animations.Any(a => a.Id == c.AnimationId && a.Tracks.Any(t => t.Enabled && t.PartId == partId && t.Channel == channel && t.Keys.Count > 0))
            || Project.Performances.TryGetValue(c.PerformanceAssetId, out var take) && take.Tracks.Any(t => t.Enabled && t.PartId == partId && t.Channel == channel)));
    public void SetProperty(Guid objectId, Guid partId, CreationChannel channel, CreationVector value, Action setBase)
    {
        Play(false);
        if (AutoKey) WriteKey(objectId, partId, channel, value);
        else if (IsAnimated(objectId, partId, channel)) propertyDrafts[(objectId, partId, channel)] = value;
        else setBase();
    }
    private Guid projectSelectedObject, projectSelectedPart;
    public void EnterAnimation(Guid animation, Guid actor)
    {
        if (Project?.Animations.Find(a => a.Id == animation) is not { } a || Project.Objects.Find(o => o.Id == actor)?.ModelId != a.ModelId)
            throw new InvalidOperationException("The animation and actor use different models.");
        if (EditingAnimation is null) { projectSelectedObject = SelectedObject; projectSelectedPart = SelectedPart; }
        StopRecording(); SelectedAnimation = animation; AnimationObject = SelectedObject = actor;
        SelectedPart = Guid.Empty; AnimationTime = 0; ClearTransformDrafts();
    }
    public void LeaveAnimation()
    { Play(false); if (EditingAnimation is not null) { SelectedObject = projectSelectedObject; SelectedPart = projectSelectedPart; } SelectedAnimation = AnimationObject = Guid.Empty; AnimationTime = 0; ClearTransformDrafts(); }
    private CreationFrame EvaluateEditingFrame()
    {
        var frame = DirectorCreationEvaluator.Evaluate(Project!, Time, drafts, EditingAnimation, AnimationObject, AnimationTime, propertyDrafts);
        foreach (var (key, value) in propertyDrafts)
        {
            if (!frame.Objects.TryGetValue(key.Object, out var state)) continue;
            if (key.Channel == CreationChannel.Color) state.Color = value;
            else if (key.Channel == CreationChannel.IkWeight) state.IkWeight = value.X;
            else if (key.Channel == CreationChannel.Fov) state.Fov = value.X;
            else if (key.Channel == CreationChannel.Visible)
            {
                if (key.Part == Guid.Empty) state.Visible = value.X >= .5;
                else if (value.X >= .5) state.HiddenParts.Remove(key.Part); else state.HiddenParts.Add(key.Part);
            }
        }
        return frame;
    }
    public CreationKeyframe WriteKey(Guid objectId, Guid partId, CreationChannel channel, CreationVector value)
    {
        Guid target = EditingAnimation is null ? objectId : Guid.Empty;
        var track = EditTracks.Find(t => t.ObjectId == target && t.PartId == partId && t.Channel == channel);
        if (track is null) { track = new() { ObjectId = target, PartId = partId, Channel = channel }; EditTracks.Add(track); }
        var key = DirectorCreationEdits.SetKey(track, EditTime, value);
        propertyDrafts.Remove((objectId, partId, channel));
        if (draftChannels.TryGetValue((objectId, partId), out var channels))
        { channels.Remove(channel); if (channels.Count == 0) { draftChannels.Remove((objectId, partId)); drafts.Remove((objectId, partId)); } }
        return key;
    }
    public void CommitTransformDrafts()
    {
        Edit("Record preview keys", _ =>
        {
            foreach (var entry in drafts.ToArray())
                foreach (var channel in draftChannels.GetValueOrDefault(entry.Key, new()).ToArray())
                    WriteKey(entry.Key.Object, entry.Key.Part, channel, Component(entry.Value, channel));
            foreach (var entry in propertyDrafts.ToArray()) WriteKey(entry.Key.Object, entry.Key.Part, entry.Key.Channel, entry.Value);
            drafts.Clear(); draftChannels.Clear(); propertyDrafts.Clear();
        });
    }
    private static CreationVector Component(CreationTransform value, CreationChannel channel) => channel switch
    { CreationChannel.Position => value.Position, CreationChannel.Rotation => value.Rotation, CreationChannel.Scale => value.Scale, _ => value.Pivot };
    private static void Assign(CreationTransform value, CreationChannel channel, CreationVector component)
    {
        switch (channel) { case CreationChannel.Position: value.Position = component; break; case CreationChannel.Rotation: value.Rotation = component; break; case CreationChannel.Scale: value.Scale = component; break; default: value.Pivot = component; break; }
    }
    public void SetTransform(Guid objectId, Guid partId, CreationTransform value)
    {
        var obj = Project?.Objects.Find(o => o.Id == objectId);
        if (obj is null) return;
        Play(false);
        var basis = partId == Guid.Empty ? obj.Transform : obj.Pose.GetValueOrDefault(partId) ?? new();
        var before = Frame?.Objects.GetValueOrDefault(objectId);
        var current = drafts.GetValueOrDefault((objectId, partId))
            ?? (partId == Guid.Empty ? before?.Transform : before?.Pose.GetValueOrDefault(partId)) ?? basis;
        var updated = basis with { };
        foreach (var channel in new[] { CreationChannel.Position, CreationChannel.Rotation, CreationChannel.Scale, CreationChannel.Pivot })
        {
            var component = Component(value, channel);
            if (component == Component(current, channel)) continue;
            if (AutoKey) WriteKey(objectId, partId, channel, component);
            else
            {
                bool animated = IsAnimated(objectId, partId, channel);
                if (!animated) Assign(updated, channel, component);
                else
                {
                    if (!draftChannels.TryGetValue((objectId, partId), out var channels)) draftChannels[(objectId, partId)] = channels = new();
                    channels.Add(channel); drafts[(objectId, partId)] = value with { };
                }
            }
        }
        if (partId == Guid.Empty) obj.Transform = updated; else if (updated != basis) obj.Pose[partId] = updated;
        // Keep the preview's static components in sync during mixed-channel edits.
        if (draftChannels.ContainsKey((objectId, partId))) drafts[(objectId, partId)] = value with { };
    }
}
