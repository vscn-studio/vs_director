using System.Numerics;
using System.Text.Json;
using VSDirector.Data;

internal static class CreationActorChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(Vector3 a, Vector3 b, string message) => Check(Vector3.Distance(a, b) < .002f, message + $" ({a} / {b})");

    public static void Ik()
    {
        foreach (var target in new[] { new Vector3(1, 1, 0), new Vector3(0, 4, 0), Vector3.Zero, new Vector3(0, -1, 0) })
        {
            var result = DirectorCreationIk.Solve(Vector3.Zero, target, Vector3.UnitZ, 1, 1);
            Check(Math.Abs(result.Joint.Length() - 1) < .002, "IK changed upper length");
            Check(Math.Abs(Vector3.Distance(result.Joint, result.End) - 1) < .002, "IK changed lower length");
            Check(float.IsFinite(result.End.X) && float.IsFinite(result.Joint.Z), "IK produced nonfinite coordinates");
        }
        var upper = new CreationPart { Name = "upper", From = default, To = new(1,16,1) };
        var lower = new CreationPart { Name = "lower", ParentId = upper.Id, From = new(0,16,0), Origin = new(0,16,0), To = new(1,32,1) };
        var model = new CreationModel { Parts = [upper, lower] };
        var targetObject = new CreationSceneObject { Kind = CreationObjectKind.Group, Transform = new() { Position = new(1,1,0) } };
        var pole = new CreationSceneObject { Kind = CreationObjectKind.Group, Transform = new() { Position = new(0,0,1) } };
        var actor = new CreationSceneObject { ModelId = model.Id };
        actor.IkChains.Add(new() { Upper = upper.Id, Lower = lower.Id, Target = targetObject.Id, Pole = pole.Id, EndOffset = new(0,1,0) });
        var other = new CreationSceneObject { ModelId = model.Id };
        var project = new DirectorCreationProject { Models = [model], Objects = [actor, other, targetObject, pole] };
        project.Validate();
        var frame = DirectorCreationEvaluator.Evaluate(project, 0);
        Near(Vector3.Transform(Vector3.UnitY, frame.Objects[actor.Id].Bones[lower.Id]), new(1,1,0), "IK endpoint missed target");
        Near(Vector3.Transform(Vector3.UnitY, frame.Objects[other.Id].Bones[lower.Id]), new(0,2,0), "IK changed another actor");
        var preview = DirectorCreationEvaluator.Evaluate(project, 0, propertyDrafts: new Dictionary<(Guid Object, Guid Part, CreationChannel Channel), CreationVector>
        { [(actor.Id, Guid.Empty, CreationChannel.IkWeight)] = default });
        Near(Vector3.Transform(Vector3.UnitY, preview.Objects[actor.Id].Bones[lower.Id]), new(0,2,0), "IK preview weight was applied after solving");
        var baked = new CreationAnimation { ModelId = model.Id, Duration = 1 };
        var bakedWeight = new CreationTrack { Channel = CreationChannel.IkWeight };
        DirectorCreationEdits.SetKey(bakedWeight, 0, default); baked.Tracks.Add(bakedWeight);
        project.Animations.Add(baked);
        project.Clips.Add(new() { ObjectId = actor.Id, AnimationId = baked.Id, Start = 1, Duration = 1, BakedPose = true });
        foreach (double time in new[] { 0d, .99, 2, 3 })
        {
            var outside = DirectorCreationEvaluator.Evaluate(project, time);
            Check(outside.Objects[actor.Id].IkWeight == 1, "Baked IK disabled the solver outside its clip");
        }
        Check(DirectorCreationEvaluator.Evaluate(project, 1.5).Objects[actor.Id].IkWeight == 0, "Baked IK was solved twice");
        DirectorCreationEdits.SetKey(project, actor.Id, Guid.Empty, CreationChannel.IkWeight, 0, default);
        frame = DirectorCreationEvaluator.Evaluate(project, 0);
        Near(Vector3.Transform(Vector3.UnitY, frame.Objects[actor.Id].Bones[lower.Id]), new(0,2,0), "Zero IK weight changed pose");
        var copy = DirectorCreationEdits.Duplicate(project); copy.Validate();
        var chain = copy.Objects[0].IkChains[0];
        Check(chain.Upper == copy.Models[0].Parts[0].Id && chain.Target == copy.Objects[2].Id, "IK references not remapped");
        DirectorCreationEdits.DeleteObject(copy, chain.Target); copy.Validate();
        Check(copy.Objects[0].IkChains.Count == 0, "Deleting target left invalid IK");
    }

    public static void PerformanceStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-performance-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bone = new CreationPart(); var model = new CreationModel { Parts = [bone] };
            var actor = new CreationSceneObject { ModelId = model.Id };
            var asset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/take.performance.json" };
            var track = new CreationTrack { PartId = bone.Id, Channel = CreationChannel.Rotation };
            DirectorCreationEdits.SetKey(track, 0, default); DirectorCreationEdits.SetKey(track, 2, new(0,90,0));
            var take = new CreationPerformance { Tracks = [track], Action = new() { Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 2 }] }] } };
            var project = new DirectorCreationProject { SchemaVersion = 1, Models = [model], Objects = [actor], Assets = [asset], Clips = [new() { ObjectId = actor.Id, PerformanceAssetId = asset.Id, Start = 1, Duration = 2 }] };
            var store = new DirectorCreationStore(root);
            DirectorCreationStore.AtomicWrite(store.AssetPath(project.Id, asset.Source), JsonSerializer.Serialize(take, CreationJson.Options));
            store.Save(project);
            Check(project.SchemaVersion == 2, "Version 1 project was not migrated");
            var session = new DirectorCreationSession(store); session.Open(store.Load(project.Id)); session.Seek(2);
            Check(Math.Abs(session.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y - 45) < .001, "Performance reload or source time incorrect");
            session.Seek(0); session.Seek(2);
            Check(Math.Abs(session.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y - 45) < .001, "Seek changed performance pose");
            var copy = store.Duplicate(session.Project!);
            var copiedFrame = DirectorCreationEvaluator.Evaluate(copy, 2);
            Check(Math.Abs(copiedFrame.Objects[copy.Objects[0].Id].Pose[copy.Models[0].Parts[0].Id].Rotation.Y - 45) < .001, "Copied performance retained old bone IDs");
            var position = new CreationTrack { Channel = CreationChannel.Position };
            DirectorCreationEdits.SetKey(position, 0, new(4,0,0)); DirectorCreationEdits.SetKey(position, 2, new(8,0,0));
            var second = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/position.performance.json" };
            var positionTake = new CreationPerformance { Channels = CreationRecordingChannels.Position, Tracks = [position], Action = take.Action };
            session.Project!.Assets.Add(second); session.Project.Performances[second.Id] = positionTake;
            session.Project.Clips.Add(new() { ObjectId = actor.Id, PerformanceAssetId = second.Id, Start = 1, Duration = 2, Lane = 1 });
            session.Seek(2);
            Check(Math.Abs(session.Frame!.Objects[actor.Id].Transform.Position.X - 6) < .001
                && Math.Abs(session.Frame.Objects[actor.Id].Pose[bone.Id].Rotation.Y - 45) < .001, "Position overdub erased bone pose");
            session.Seek(3); Check(session.Frame!.Objects[actor.Id].Transform.Position.X == 0, "Performance affected time outside its clip");
            session.Project!.Loop = true; session.ExternalRecording = true; session.Seek(9.9); session.Play(true); session.Tick(.2);
            Check(!session.Playing && session.Time == 10, "External recording wrapped at project end");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
