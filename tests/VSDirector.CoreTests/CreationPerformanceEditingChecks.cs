using System.Numerics;
using VSDirector.Data;

internal static class CreationPerformanceEditingChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "director-take-edit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bone = new CreationPart(); var model = new CreationModel { Parts = [bone] };
            var actor = new CreationSceneObject { ModelId = model.Id };
            var asset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/take.json" };
            var rootTrack = new CreationTrack { Channel = CreationChannel.Position };
            var boneTrack = new CreationTrack { PartId = bone.Id, Channel = CreationChannel.Rotation };
            DirectorCreationEdits.SetKey(rootTrack, 0, new(100, 0, 0)); DirectorCreationEdits.SetKey(rootTrack, 4, new(140, 0, 0));
            DirectorCreationEdits.SetKey(boneTrack, 0, default); DirectorCreationEdits.SetKey(boneTrack, 4, new(0, 80, 0));
            var take = new CreationPerformance { Tracks = [rootTrack, boneTrack], Action = new() { Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 4 }] }] } };
            var clip = new CreationClip { ObjectId = actor.Id, PerformanceAssetId = asset.Id, Start = 5, Duration = 2, SourceStart = .5, Speed = 2, Loop = true };
            var p = new DirectorCreationProject { Models = [model], Objects = [actor], Assets = [asset], Clips = [clip] };
            p.Performances[asset.Id] = take; p.SetBackground(Guid.NewGuid(), clip.Id);
            DirectorCreationEdits.SetKey(p, actor.Id, Guid.Empty, CreationChannel.Position, 0, default);
            DirectorCreationEdits.SetKey(p, actor.Id, Guid.Empty, CreationChannel.Position, 4, new(4, 0, 0));
            DirectorCreationEdits.SetKey(p, actor.Id, bone.Id, CreationChannel.Rotation, 0, default);
            DirectorCreationEdits.SetKey(p, actor.Id, bone.Id, CreationChannel.Rotation, 4, new(0, 40, 0));
            DirectorCreationEdits.SetKey(p, actor.Id, Guid.Empty, CreationChannel.Scale, 0, new(2, 2, 2));
            CreationObjectState At(double time) => DirectorCreationEvaluator.Evaluate(p, time).Objects[actor.Id];
            foreach (double time in new[] { 0d, 1, 3, 4.99, 7, 9 })
            {
                var state = At(time);
                Near(state.Transform.Position.X, Math.Min(time, 4), "Later take overwrote earlier/later position keys");
                Near(state.Pose[bone.Id].Rotation.Y, Math.Min(time, 4) * 10, "Later take overwrote earlier/later bone keys");
                Check(!state.HasPerformance, "Take leaked outside its clip");
            }
            Near(At(5.5).Transform.Position.X, 115, "Source offset/speed ignored");
            Near(At(5.5).Pose[bone.Id].Rotation.Y, 30, "Earlier bone keys pinned a later performance");
            Near(At(5.5).Transform.Scale.X, 2, "Take erased an unrecorded channel");
            var times = new[] { 0d, 4.99, 5, 5.5, 6.9, 7, 10 };
            var before = times.Select(At).ToArray();
            var session = new DirectorCreationSession(new(root)); session.Open(p);
            CreationAnimation animation = null!;
            session.Edit("convert", project => animation = DirectorCreationEdits.BakePerformance(project, project.Clips[0]));
            Check(animation.Tracks.Count == 2 && animation.Tracks.All(t => t.ObjectId == Guid.Empty), "Conversion lost editable channels");
            Check(animation.Tracks[0].Id != rootTrack.Id && animation.Tracks[0].Keys[0].Id != rootTrack.Keys[0].Id, "Conversion reused identities");
            Check(ReferenceEquals(animation, DirectorCreationEdits.BakePerformance(p, clip)) && p.Animations.Count == 1, "Reopening duplicated/rebaked the animation");
            for (int i = 0; i < times.Length; i++)
            {
                var after = At(times[i]);
                Check(Vector3.Distance(after.World.Translation, before[i].World.Translation) < .001f, "Conversion changed placement");
                Near(after.Pose[bone.Id].Rotation.Y, before[i].Pose[bone.Id].Rotation.Y, "Conversion changed bone priority");
            }
            var convertedBone = animation.Tracks.First(t => t.PartId == bone.Id);
            convertedBone.Keys[0].Value = new(0, 60, 0);
            Near(boneTrack.Keys[0].Value.Y, 0, "Editing converted keys mutated the original take");
            Check(At(5.5).Pose[bone.Id].Rotation.Y > 30, "Edited bone keys did not affect clip playback");
            session.Undo(); Check(!session.Project!.Clips[0].BakedPose && session.Project.Animations.Count == 0, "Conversion undo failed");
            session.Redo(); Check(session.Project!.Clips[0].BakedPose, "Conversion redo failed");
            p = session.Project; clip = p.Clips[0]; clip.Start = 8; clip.Duration = 2; clip.SourceStart = 0; clip.Speed = 1; clip.Loop = false;
            Check(At(10).HasPerformance, "Final frame at the project boundary was omitted");
            clip.Enabled = false; Check(!At(9).HasPerformance, "Disabled clip still samples a take");
            clip.Enabled = true;
            var right = DirectorCreationEdits.Split(p, clip, 9);
            Near(At(9).Transform.Position.X, 110, "Split clips double applied or lost source time at the boundary");
            Check(!DirectorCreationEvaluator.ClipActive(clip, 9, p.Duration) && DirectorCreationEvaluator.ClipActive(right, 9, p.Duration), "Adjacent clips both own their shared boundary");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double a, double b, string message) => Check(Math.Abs(a - b) < .001, message + $" ({a} / {b})");
}
