using System.Numerics;
using VSDirector.Data;

internal static class CreationRigWorkflowChecks
{
    public static void Run()
    {
        CheckNativeInterpolation();
        string root = Path.Combine(Path.GetTempPath(), "director-rig-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (int version in new[] { 0, 1 })
            foreach (double mirror in new[] { -1d, 1 })
            {
                var upper = new CreationPart { Name = "UpperArm", To = new(2, 16, 2), Rotation = new(0, 0, 20) };
                var lower = new CreationPart { Name = "LowerArm", ParentId = upper.Id, From = new(0, 16, 0), Origin = new(0, 16, 0), To = new(2, 32, 2) };
                var model = new CreationModel { AnimationVersion = version, Parts = [upper, lower] };
                var actor = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(12, 3, -9), Rotation = new(25, 140, -15), Scale = new(mirror * 2, 1.5, .8) } };
                var p = new DirectorCreationProject { Models = [model], Objects = [actor] };
                var rest = DirectorCreationEvaluator.Evaluate(p, 0).Objects[actor.Id];
                var controls = DirectorCreationIk.CreateControls(actor, model, rest, upper, lower);
                p.Objects.Add(controls.Target); p.Objects.Add(controls.Pole); actor.IkChains.Add(controls.Chain);
                controls.Chain.EndOffset = new(0, 1, 0);
                var localTarget = new Vector3(.8f, 1.2f, .2f);
                controls.Target.Transform.Position = CreationVector.From(Vector3.Transform(localTarget, rest.World));
                controls.Pole.Transform.Position = CreationVector.From(Vector3.Transform(new Vector3(0, 1, 2), rest.World));
                var solved = DirectorCreationEvaluator.Evaluate(p, 0).Objects[actor.Id];
                Near(Vector3.Transform(controls.Chain.EndOffset.Float(), solved.Bones[lower.Id]), localTarget, "IK misses target in rotated/reflected actor space v" + version);
                var animation = new CreationAnimation { ModelId = model.Id, Duration = 2 }; p.Animations.Add(animation);
                var session = new DirectorCreationSession(new(root)); session.Open(p); session.EnterAnimation(animation.Id, actor.Id);
                if (!session.IsAnimationControl(controls.Target.Id) || !session.IsAnimationControl(controls.Pole.Id)) throw new Exception("Action editor cannot identify its IK controls");
                session.AutoKey = true; session.SelectedObject = controls.Target.Id;
                var first = controls.Target.Transform with { }; session.SetTransform(controls.Target.Id, Guid.Empty, first with { Position = first.Position + new CreationVector(.1, 0, 0) }); session.Changed();
                session.Seek(1); session.SetTransform(controls.Target.Id, Guid.Empty, first with { Position = first.Position + new CreationVector(.4, 0, 0) }); session.Changed();
                if (animation.Tracks.Count != 1 || animation.Tracks[0].ObjectId != controls.Target.Id || animation.Tracks[0].Keys.Count != 2) throw new Exception("IK control keys were written onto the actor");
                session.Seek(.5);
                var expectedControl = first.Position + new CreationVector(.25, 0, 0);
                Near(session.Frame!.Objects[controls.Target.Id].World.Translation, expectedControl.Float(), "IK control does not animate in action preview");
                Near(session.Frame.Objects[actor.Id].World.Translation, rest.World.Translation, "Control animation translated the actor");
                session.LeaveAnimation(); p.Clips.Add(new() { ObjectId = actor.Id, AnimationId = animation.Id, Start = 3, Duration = 2 }); p.Validate();
                var playback = DirectorCreationEvaluator.Evaluate(p, 3.5);
                Near(playback.Objects[controls.Target.Id].World.Translation, expectedControl.Float(), "Action clip failed to animate the IK target");
                p.Clips[0].BakedPose = true;
                var bakedPlayback = DirectorCreationEvaluator.Evaluate(p, 3.5);
                Near(bakedPlayback.Objects[controls.Target.Id].World.Translation, expectedControl.Float(), "Baked clip lost its control animation");
                Near(bakedPlayback.Objects[actor.Id].World.Translation, rest.World.Translation, "Baked control keys translated the actor");
                var restored = CreationJson.Clone(p); restored.Validate();
                Near(DirectorCreationEvaluator.Evaluate(restored, 3.5).Objects[controls.Target.Id].World.Translation, expectedControl.Float(), "Saving and loading lost the control animation");
                var copied = DirectorCreationEdits.Duplicate(p); copied.Validate();
                if (copied.Animations[0].Tracks[0].ObjectId != copied.Objects[1].Id) throw new Exception("Project copy lost IK control track references");
                DirectorCreationEdits.DeleteObject(p, controls.Target.Id); p.Validate();
                if (animation.Tracks.Count != 0 || actor.IkChains.Count != 0) throw new Exception("Deleting a control left invalid animation references");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Near(Vector3 a, Vector3 b, string message)
    { if (Vector3.Distance(a, b) > .005f) throw new Exception(message + $" ({a} / {b})"); }

    private static void CheckNativeInterpolation()
    {
        const string json = """
            {"elements":[{"name":"arm","from":["1",0,0],"to":[2,16,2],"rotationX":"25"}],
             "animations":[{"name":"turn","quantityframes":30,"onAnimationEnd":"Repeat","keyframes":[
              {"frame":6,"elements":{"arm":{"rotationY":"0"}}},
              {"frame":24,"elements":{"arm":{"rotationY":"270"}}}]}]}
            """;
        var imported = DirectorCreationShape.Import(json, "test");
        if (imported.Model.Parts[0].From.X != 1 || imported.Model.Parts[0].Rotation.X != 25)
            throw new Exception("Quoted native numeric values were discarded");
        var action = imported.Animations[0]; var track = action.Tracks.Single();
        void Check(double time, double expected)
        {
            double actual = DirectorCreationEvaluator.Sample(track, time, default).Y;
            if (Math.Abs(actual - expected) > .0001) throw new Exception($"Native interpolation at {time}: {actual}, expected {expected}");
        }
        Check(0, 135); Check(.1, 67.5); Check(.2, 0); Check(.5, 135); Check(.8, 270); Check(.9, 202.5); Check(1, 135);
        if (CreationJson.Clone(track).ShortestRotation || track.Copy().ShortestRotation)
            throw new Exception("Native rotation interpolation was lost on save/copy");
        var exported = DirectorCreationShape.ExportAnimation(action, imported.Model);
        var roundTrip = DirectorCreationShape.ImportAnimation(exported, imported.Model).Tracks.Single();
        for (int frame = 0; frame < 30; frame++)
            if (Math.Abs(DirectorCreationEvaluator.Sample(roundTrip, frame / 30d, default).Y
                - DirectorCreationEvaluator.Sample(track, frame / 30d, default).Y) > .0001)
                throw new Exception("Native rotation export changed a frame");
        var authored = new CreationTrack { Channel = CreationChannel.Rotation };
        DirectorCreationEdits.SetKey(authored, 0, default); DirectorCreationEdits.SetKey(authored, 1, new(0, 270, 0));
        if (DirectorCreationEvaluator.Sample(authored, .5, default).Y != -45)
            throw new Exception("Existing authored shortest-path rotation changed");
    }
}
