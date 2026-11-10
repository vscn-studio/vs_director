using static VSDirector.Gui.DirectorCreationText;
using System.Text.Json;
using VSDirector.Data;
using VSDirector.Networking;
using VSDirector.Replay;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private string performanceSession = "";
    private Guid performanceActor;
    private double performanceStart, countdown = -1;
    private long performanceHeartbeat, performanceRequested;
    private CameraPreset? performanceView;
    private bool performanceRunning, ownsReplayCapture;
    private bool observingWorldActor;
    private long stoppingAt;
    private Guid previousBackground;
    private double previousBackgroundTime;
    private Guid pendingBackground;
    private double pendingBackgroundTime;
    private void LoadPerformanceBackground(Guid id, double time)
    {
        var replay = DirectorReplayRuntime.Manager;
        if (replay is null || !replay.StartPlayback(id.ToString(), out _, out var error, pauseAfterLoad: true))
        { Status = L("creation-load-the-linked-background-replay-first"); return; }
        pendingBackground = id; pendingBackgroundTime = time;
    }
    private void TickPerformanceBackground()
    {
        if (pendingBackground == Guid.Empty || PerformanceActive) return;
        if (owner.ActiveReplayId != pendingBackground || !owner.CreationBackgroundReady) return;
        owner.SeekCreationBackground(pendingBackgroundTime); pendingBackground = Guid.Empty;
        owner.TryEnableCreationFreeCamera(out _);
    }
    private DirectorActionAsset? stoppedAction;
    private long observedEntity;
    private readonly Dictionary<(Guid Bone, CreationChannel Channel), CreationTrack> recordedBones = new();
    private readonly List<DirectorPerformanceStatus> serverSamples = new();
    internal bool PerformanceActive => !string.IsNullOrEmpty(performanceSession);
    internal bool ControllingActor => performanceRunning && !observingWorldActor;
    internal bool NeedsActorAnimation(Entity entity) => performanceRunning
        && (observingWorldActor ? entity.EntityId == observedEntity : ReferenceEquals(entity, api.World.Player?.Entity));
    private void PreparePerformance()
    {
        if (Session.Project is not { } project)
            throw new InvalidOperationException(L("creation-recording-at-end"));
        previousBackground = owner.ActiveReplayId; previousBackgroundTime = owner.ReplayTimeSeconds;
        pendingBackground = Guid.Empty;
        RestoreVisualEnvironment();
        DirectorReplayRuntime.Manager?.StopPlayback();
        stoppingAt = 0; stoppedAction = null; Session.Play(false);
    }
    internal void UsePerformanceBackground(CreationClip clip)
    {
        if (Session.Project is not { } project || !project.Performances.TryGetValue(clip.PerformanceAssetId, out var take) || take.BackgroundReplayId == Guid.Empty) return;
        var replay = DirectorReplayRuntime.Manager;
        if (replay is null || !replay.StartPlayback(take.BackgroundReplayId.ToString(), out _, out var error, pauseAfterLoad: true)) throw new InvalidOperationException(L("creation-load-the-linked-background-replay-first"));
        Session.Edit(L("creation-performance-background"), p => { p.BackgroundReplayId = take.BackgroundReplayId; p.BackgroundClipId = clip.Id; p.BackgroundOffset = take.BackgroundStart + clip.SourceStart - clip.Start; });
        owner.TryEnableCreationFreeCamera(out _); Seek(Session.Time);
    }
    internal bool ReplacesReplayActor(Guid replayId, string actorKey)
    {
        if (!Enabled || PerformanceActive || Session.Project is not { } project || project.BackgroundReplayId != replayId) return false;
        return project.Clips.Any(c => c.Enabled && c.PerformanceAssetId != Guid.Empty
            && project.Performances.TryGetValue(c.PerformanceAssetId, out var take)
            && take.BackgroundReplayId == replayId && take.Action.SourceActorKey == actorKey
            && Session.Frame?.Objects.GetValueOrDefault(c.ObjectId) is { HasPerformance: true });
    }
    private double BackgroundTime(double time)
    {
        var project = Session.Project!;
        var clip = project.Clips.Find(c => c.Id == project.BackgroundClipId);
        if (clip is null || !project.Performances.TryGetValue(clip.PerformanceAssetId, out var take)) return Math.Max(0, time + project.BackgroundOffset);
        double source = clip.SourceStart + Math.Clamp(time - clip.Start, 0, clip.Duration) * clip.Speed;
        source = clip.Loop ? source % Math.Max(1d / 30, take.Action.DurationSeconds) : Math.Min(source, take.Action.DurationSeconds);
        return take.BackgroundStart + source;
    }
    internal void BakePerformance(CreationClip clip)
    {
        if (Session.Project is not { } project || !project.Performances.TryGetValue(clip.PerformanceAssetId, out var take)) return;
        Session.Edit(L("creation-performance-bake"), p =>
        {
            var animation = new CreationAnimation { Name = clip.Name, ModelId = p.Objects.First(o => o.Id == clip.ObjectId).ModelId,
                Duration = Math.Max(1d / 30, take.Action.DurationSeconds), Tracks = take.Tracks.Select(CreationJson.Clone).ToList() };
            foreach (var track in animation.Tracks)
            {
                track.Id = Guid.NewGuid(); track.ObjectId = Guid.Empty;
                foreach (var key in track.Keys) key.Id = Guid.NewGuid();
            }
            p.Animations.Add(animation); clip.AnimationId = animation.Id; clip.BakedPose = true;
        });
    }
    private double lastBoneSample = -1;
    private void CapturePerformanceBones()
    {
        var entity = observingWorldActor ? api.World.GetEntityById(observedEntity) : api.World.Player.Entity;
        if (entity is null || !entity.Alive) { StopPerformance(); return; }
        if (Session.Project is not { } project || project.Objects.Find(o => o.Id == performanceActor) is not { } actor) return;
        var model = project.Models.Find(m => m.Id == actor.ModelId);
        var animator = entity is EntityPlayer player ? player.TpAnimManager?.Animator : entity.AnimManager?.Animator;
        if (model is null || animator is null) return;
        double time = Math.Max(0, Session.Time - performanceStart);
        bool sampleNow = lastBoneSample < 0 || time - lastBoneSample >= 1d / 30;
        if (sampleNow) lastBoneSample = time;
        foreach (var bone in model.Parts)
        {
            var pose = animator.GetPosebyName(bone.Name); if (pose is null) continue;
            void Key(CreationChannel channel, CreationVector value)
            {
                if (!sampleNow) return;
                var key = (bone.Id, channel);
                if (!recordedBones.TryGetValue(key, out var track)) recordedBones[key] = track = new() { PartId = bone.Id, Channel = channel };
                DirectorCreationEdits.SetKey(track, time, value);
            }
            Key(CreationChannel.Rotation, new(pose.degX + pose.degOffX, pose.degY + pose.degOffY, pose.degZ + pose.degOffZ));
            Key(CreationChannel.Position, new(pose.translateX, pose.translateY, pose.translateZ));
            Key(CreationChannel.Scale, new(pose.scaleX, pose.scaleY, pose.scaleZ));
            if (Session.Frame?.Objects.GetValueOrDefault(actor.Id) is { } state)
                state.Pose[bone.Id] = new() { Position = new(pose.translateX,pose.translateY,pose.translateZ), Rotation = new(pose.degX+pose.degOffX,pose.degY+pose.degOffY,pose.degZ+pose.degOffZ), Scale = new(pose.scaleX,pose.scaleY,pose.scaleZ) };
        }
        if (Session.Frame?.Objects.GetValueOrDefault(actor.Id) is { } current) { current.Bones.Clear(); DirectorCreationEvaluator.EvaluateBones(model,current); }
        if (Session.Frame is { } updated) RefreshAttachments(updated);
    }
    internal void RecordWorldActor(long entityId)
    {
        if (PerformanceActive) throw new InvalidOperationException(L("creation-recording-unavailable"));
        var manager = DirectorReplayRuntime.Manager ?? throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (manager.IsRecording || manager.IsLiveActionRecording) throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (Session.EditingAnimation is not null || Exporting) throw new InvalidOperationException(L("creation-recording-root-only"));
        PreparePerformance();
        var actor = AddNativeActor("", entityId);
        if (api.World.GetEntityById(entityId) is { } sourceEntity)
            Session.Edit(L("creation-record-world-actor"), _ => actor.Transform.Position = new CreationVector(sourceEntity.Pos.X, sourceEntity.Pos.Y, sourceEntity.Pos.Z) - Session.Project!.Origin);
        if (!manager.StartLiveActionRecording(entityId.ToString(System.Globalization.CultureInfo.InvariantCulture), actor.Name, out _, out var error)) throw new InvalidOperationException(error);
        observingWorldActor = performanceRunning = true; observedEntity = entityId; performanceActor = actor.Id; performanceStart = Session.Time;
        performanceSession = Guid.NewGuid().ToString(); recordedBones.Clear(); lastBoneSample = -1; serverSamples.Clear(); Session.ExternalRecording = true; Session.Play(true);
        Status = L("creation-recording-active");
    }

    internal void TogglePerformance()
    {
        if (PerformanceActive) { StopPerformance(); return; }
        if (Session.Project is not { } project || project.Objects.Find(o => o.Id == Session.SelectedObject) is not { Kind: CreationObjectKind.Model } actor) return;
        if (actor.ParentId != Guid.Empty || Session.EditingAnimation is not null || Exporting) throw new InvalidOperationException(L("creation-recording-root-only"));
        if (actor.RecordingChannels == 0) throw new InvalidOperationException(L("creation-record-select-channel"));
        var manager = DirectorReplayRuntime.Manager;
        var channel = api.Network.GetChannel(DirectorNetwork.ChannelName);
        if (!channel.Connected || manager is null || manager.IsRecording || manager.IsLiveActionRecording) throw new InvalidOperationException(L("creation-recording-unavailable"));
        owner.TryCaptureCreationView(out performanceView!); PreparePerformance();
        observingWorldActor = false; recordedBones.Clear(); lastBoneSample = -1;
        performanceActor = actor.Id; performanceStart = Session.Time; serverSamples.Clear();
        performanceSession = Guid.NewGuid().ToString(); performanceRequested = api.World.ElapsedMilliseconds;
        channel.SetMessageHandler<DirectorPerformanceStatus>(OnPerformanceStatus);
        var position = (Session.Frame?.Objects.GetValueOrDefault(actor.Id)?.World.Translation is { } p ? CreationVector.From(p) : actor.Transform.Position) + project.Origin;
        var body = actor.PlayerBody;
        channel.SendPacket(new DirectorPerformanceRequest { Session = performanceSession, X = position.X, Y = position.Y, Z = position.Z, Dimension = project.Dimension, Yaw = (float)(actor.Transform.Rotation.Y * Math.PI / 180),
            CustomBody = body.Enabled, BodyWidth = body.Width, BodyHeight = body.Height, SneakHeight = body.SneakHeight, EyeHeight = body.EyeHeight });
        Status = L("creation-recording-waiting");
    }
    private void OnPerformanceStatus(DirectorPerformanceStatus status)
    {
        if (status.Phase == "body") { Status = L(status.Error.Length > 0 ? status.Error : "creation-player-body-applied"); return; }
        if (status.Session != performanceSession) return;
        if (status.Phase == "ready" && !performanceRunning && stoppingAt == 0) countdown = 3;
        else if (status.Phase == "sample" && (performanceRunning || stoppingAt > 0)
            && double.IsFinite(status.Seconds) && double.IsFinite(status.X) && double.IsFinite(status.Y) && double.IsFinite(status.Z)
            && (serverSamples.Count == 0 || status.Sequence > serverSamples[^1].Sequence)) serverSamples.Add(status);
        else if (status.Phase == "ended")
        {
            StopPerformance(send: false);
            if (status.Error.Length > 0) Status = L(status.Error);
        }
    }
    internal void ApplyPlayerBody(CreationPlayerBody? body)
    {
        if (PerformanceActive) throw new InvalidOperationException(L("creation-recording-unavailable"));
        var channel = api.Network.GetChannel(DirectorNetwork.ChannelName);
        if (!channel.Connected) throw new InvalidOperationException(L("creation-recording-unavailable"));
        if (body is not null && (!body.Enabled || !body.Valid())) throw new InvalidOperationException(L("creation-player-body-enable-first"));
        channel.SetMessageHandler<DirectorPerformanceStatus>(OnPerformanceStatus);
        channel.SendPacket(new DirectorPerformanceRequest { Session = Guid.NewGuid().ToString(), Action = body is null ? 5 : 4, CustomBody = body is not null,
            BodyWidth = body?.Width ?? 0, BodyHeight = body?.Height ?? 0, SneakHeight = body?.SneakHeight ?? 0, EyeHeight = body?.EyeHeight ?? 0 });
    }
    private void TickPerformance(float delta)
    {
        if (!PerformanceActive) return;
        if (stoppingAt > 0)
        {
            if (api.World.ElapsedMilliseconds - stoppingAt > 5000) StopPerformance(send: false);
            return;
        }
        if (!observingWorldActor && api.World.ElapsedMilliseconds - performanceHeartbeat > 1000)
        {
            performanceHeartbeat = api.World.ElapsedMilliseconds;
            api.Network.GetChannel(DirectorNetwork.ChannelName).SendPacket(new DirectorPerformanceRequest { Session = performanceSession, Action = 2 });
        }
        if (!performanceRunning && countdown < 0 && api.World.ElapsedMilliseconds - performanceRequested > 15000)
        { StopPerformance(); Status = L("creation-recording-timeout"); return; }
        if (countdown >= 0)
        {
            countdown -= delta; Status = L("creation-recording-countdown", Math.Max(0, (int)Math.Ceiling(countdown)));
            if (countdown < 0)
            {
                var manager = DirectorReplayRuntime.Manager!;
                // Capture the world around the actual player, after releasing
                // the editor camera, rather than around the old editing view.
                owner.BeginCreationPerformanceControl();
                if (!manager.StartRecording(Session.Project!.Name + " - performance", out var error)) throw new InvalidOperationException(error);
                ownsReplayCapture = true;
                if (!manager.StartLiveActionRecording("", Session.Project.Objects.First(o => o.Id == performanceActor).Name, out _, out error)) throw new InvalidOperationException(error);
                api.Network.GetChannel(DirectorNetwork.ChannelName).SendPacket(new DirectorPerformanceRequest { Session = performanceSession, Action = 3 });
                performanceRunning = true; Session.ExternalRecording = true; Session.Play(true); Status = L("creation-recording-active");
            }
        }
        if (performanceRunning)
        {
            if (!Session.Playing) StopPerformance();
        }
    }
    internal void StopPerformance(bool send = true)
    {
        if (!PerformanceActive) return;
        if (send && !observingWorldActor && stoppingAt == 0)
        {
            stoppingAt = Math.Max(1, api.World.ElapsedMilliseconds); countdown = -1;
            if (performanceRunning && DirectorReplayRuntime.Manager?.StopLiveActionRecording(out var action, out _, out _) == true) stoppedAction = action;
            performanceRunning = false; Session.Play(false);
            Status = L("creation-recording-finishing");
            owner.EndCreationPerformanceControl(performanceView);
            api.Network.GetChannel(DirectorNetwork.ChannelName).SendPacket(new DirectorPerformanceRequest { Session = performanceSession, Action = 1 });
            return;
        }
        if (send && stoppingAt > 0) return;
        string id = performanceSession; performanceSession = ""; countdown = -1;
        Guid recordedBackground = Guid.Empty;
        try
        {
            if (send && !observingWorldActor) api.Network.GetChannel(DirectorNetwork.ChannelName).SendPacket(new DirectorPerformanceRequest { Session = id, Action = 1 });
            var manager = DirectorReplayRuntime.Manager;
            DirectorActionAsset? action = stoppedAction;
            if (performanceRunning && manager?.StopLiveActionRecording(out var recorded, out _, out _) == true) action = recorded;
            Guid replayId = Guid.Empty;
            if (ownsReplayCapture && manager is not null)
            {
                if (!manager.StopRecording(out var info, out var error)) Status = Message(error);
                else replayId = info?.Id ?? Guid.Empty;
            }
            if (action is not null)
            {
                ImportPerformance(action, performanceActor, performanceStart, replayId, serverSamples);
                if (replayId != Guid.Empty && Session.Project is { } project)
                {
                    var clip = project.Clips.Last(c => c.ObjectId == performanceActor && c.PerformanceAssetId != Guid.Empty);
                    project.BackgroundReplayId = recordedBackground = replayId; project.BackgroundClipId = clip.Id;
                    project.BackgroundOffset = -clip.Start;
                }
                Session.Save(); Status = L("creation-recording-saved");
            }
        }
        finally
        {
            ownsReplayCapture = false; performanceRunning = false; Session.ExternalRecording = false; Session.Play(false);
            if (!observingWorldActor) owner.EndCreationPerformanceControl(performanceView);
            performanceView = null; serverSamples.Clear(); recordedBones.Clear(); observingWorldActor = false; stoppingAt = 0; stoppedAction = null;
            var background = recordedBackground != Guid.Empty ? recordedBackground : previousBackground;
            if (background != Guid.Empty) LoadPerformanceBackground(background, recordedBackground != Guid.Empty ? BackgroundTime(Session.Time) : previousBackgroundTime);
            previousBackground = Guid.Empty;
            owner.TryEnableCreationFreeCamera(out _);
            Session.Refresh();
        }
    }
    private void FinishPerformanceBeforeTransition()
    {
        if (!PerformanceActive) return;
        StopPerformance();
        if (PerformanceActive) StopPerformance(send: false);
    }
    internal void ImportPerformance(DirectorActionAsset action, Guid actorId, double start, Guid replayId = default, IReadOnlyList<DirectorPerformanceStatus>? authoritative = null, double backgroundStart = 0)
    {
        if (Session.Project is not { } project || project.Objects.Find(o => o.Id == actorId) is not { } actor) return;
        var model = project.Models.First(m => m.Id == actor.ModelId);
        var take = new CreationPerformance { Action = action, Origin = project.Origin, BackgroundReplayId = replayId, BackgroundStart = backgroundStart, Channels = actor.RecordingChannels };
        var position = new CreationTrack { Channel = CreationChannel.Position }; var rotation = new CreationTrack { Channel = CreationChannel.Rotation };
        take.Tracks.Add(position); take.Tracks.Add(rotation);
        var frames = action.Layers.SelectMany(l => l.Frames).OrderBy(f => f.TimeSeconds).ToArray();
        foreach (var frame in frames)
        {
            var sample = frame.State;
            // ActionImporter already rebases root motion to the first sample.
            DirectorCreationEdits.SetKey(position, frame.TimeSeconds, new CreationVector(sample.X, sample.Y, sample.Z) + actor.Transform.Position);
            DirectorCreationEdits.SetKey(rotation, frame.TimeSeconds, new(0, sample.BodyYaw * GameMath.RAD2DEG, 0));
            var blended = new Dictionary<(Guid Part, CreationChannel Channel), CreationVector>();
            foreach (var animationState in sample.AnimationStates)
            {
                var animation = project.Animations.FirstOrDefault(a => a.ModelId == model.Id && (string.Equals(DirectorCreationShape.S(a.Extra, "code"), animationState.Code, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, animationState.Code, StringComparison.OrdinalIgnoreCase)));
                if (animation is null) continue;
                double time = animationState.RecordedFrame is { } f ? f / 30d : animationState.Progress * Math.Max(0, animation.Duration - 1d / 30);
                foreach (var source in animation.Tracks)
                {
                    var rest = source.Channel == CreationChannel.Scale ? CreationVector.One : default;
                    var value = DirectorCreationEvaluator.Sample(source, time, rest);
                    string boneName = model.Parts.Find(p => p.Id == source.PartId)?.Name ?? "";
                    double weight = animationState.BlendedWeight * animationState.ElementWeights.GetValueOrDefault(boneName, animationState.Weight);
                    var key = (source.PartId, source.Channel);
                    blended[key] = blended.GetValueOrDefault(key, rest) + (value - rest) * Math.Clamp(weight, 0, 1);
                }
            }
            foreach (var part in model.Parts)
                foreach (var channel in new[] { CreationChannel.Position, CreationChannel.Rotation, CreationChannel.Scale })
                {
                    var track = take.Tracks.Find(t => t.PartId == part.Id && t.Channel == channel);
                    if (track is null) { track = new() { PartId = part.Id, Channel = channel }; take.Tracks.Add(track); }
                    DirectorCreationEdits.SetKey(track, frame.TimeSeconds, blended.GetValueOrDefault((part.Id, channel), channel == CreationChannel.Scale ? CreationVector.One : default));
                }
        }
        if (authoritative is { Count: > 1 })
        {
            position.Keys.Clear(); rotation.Keys.Clear();
            foreach (var sample in authoritative)
            {
                DirectorCreationEdits.SetKey(position, sample.Seconds, new CreationVector(sample.X, sample.Y, sample.Z) - project.Origin);
                DirectorCreationEdits.SetKey(rotation, sample.Seconds, new(0, sample.Yaw * GameMath.RAD2DEG, 0));
            }
        }
        if (recordedBones.Count > 0)
        {
            take.Tracks.RemoveAll(t => recordedBones.ContainsKey((t.PartId, t.Channel))); take.Tracks.AddRange(recordedBones.Values);
        }
        take.Tracks.RemoveAll(t => t.PartId != Guid.Empty ? !take.Channels.HasFlag(CreationRecordingChannels.Pose)
            : t.Channel == CreationChannel.Position ? !take.Channels.HasFlag(CreationRecordingChannels.Position)
            : t.Channel == CreationChannel.Rotation && !take.Channels.HasFlag(CreationRecordingChannels.Rotation));
        var asset = new CreationAsset { Name = action.Name, Kind = CreationAssetKind.Performance }; asset.Source = "assets/" + asset.Id.ToString("N") + ".performance.json";
        DirectorCreationStore.AtomicWrite(Session.Store.AssetPath(project.Id, asset.Source), JsonSerializer.Serialize(take, CreationJson.Options));
        Session.Edit(L("creation-performance-add"), p =>
        {
            p.Assets.Add(asset); p.Performances[asset.Id] = take;
            double duration = Math.Max(1d / 30, action.DurationSeconds); p.Duration = Math.Max(p.Duration, start + duration);
            int lane = p.Clips.Where(c => c.ObjectId == actorId && c.PerformanceAssetId != Guid.Empty).Select(c => c.Lane).DefaultIfEmpty(-1).Max() + 1;
            p.Clips.Add(new() { Name = action.Name, ObjectId = actorId, PerformanceAssetId = asset.Id, Start = start, Duration = duration, Lane = lane });
        });
    }
}
