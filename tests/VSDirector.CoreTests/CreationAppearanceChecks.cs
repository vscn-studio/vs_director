using VSDirector.Data;

internal static class CreationAppearanceChecks
{
    public static void Run()
    {
        var body = new CreationPart { Name = "body" };
        var arm = new CreationPart { Name = "arm", ParentId = body.Id };
        var hand = new CreationPart { Name = "RightHand", ParentId = arm.Id, Group = true, Faces = new() };
        var coat = new CreationPart { Name = "coat", ParentId = body.Id };
        var model = new CreationModel { Parts = [body, arm, hand, coat], BodyMapping = new() { ["arm"] = arm.Id } };
        var actor = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(3, 4, 5) } };
        actor.Pose[arm.Id] = new() { Rotation = new(10, 20, 30) };
        actor.PoseKeys.Add(new() { Time = 1, Bones = new() { [coat.Id] = new() { Rotation = new(0, 5, 0) } } });
        var other = new CreationSceneObject { ModelId = model.Id };
        var attachment = new CreationSceneObject { ParentId = actor.Id, ParentPartId = hand.Id, ActorHand = 1, Kind = CreationObjectKind.Item };
        var animation = new CreationAnimation { ModelId = model.Id };
        var track = new CreationTrack { PartId = arm.Id, Channel = CreationChannel.Rotation };
        DirectorCreationEdits.SetKey(track, 0, default);
        DirectorCreationEdits.SetKey(track, 1, new(0, 30, 0));
        animation.Tracks.Add(track);
        var clip = new CreationClip { ObjectId = actor.Id, AnimationId = animation.Id };
        var otherClip = new CreationClip { ObjectId = other.Id, AnimationId = animation.Id };
        var project = new DirectorCreationProject
        {
            Models = [model], Objects = [actor, other, attachment],
            Animations = [animation], Clips = [clip, otherClip]
        };
        var capturedBody = new CreationPart { Name = "body" };
        var capturedArm = new CreationPart { Name = "arm", ParentId = capturedBody.Id };
        var capturedHand = new CreationPart { Name = "RightHand", ParentId = capturedArm.Id, Group = true, Faces = new() };
        var shirt = new CreationPart { Name = "shirt", ParentId = capturedBody.Id };
        var captured = new CreationModel { Parts = [capturedBody, capturedArm, capturedHand, shirt] };
        var before = CreationJson.Clone(project);
        var actions = DirectorCreationAppearance.Apply(project, actor, captured);
        project.Validate();
        var updated = project.Models.First(m => m.Id == actor.ModelId);
        Check(actor.ModelId != model.Id && other.ModelId == model.Id, "Appearance changed a shared actor");
        Check(updated.Parts.Single(p => p.Name == "arm").Id == arm.Id, "Arm identity changed");
        Check(updated.Parts.Single(p => p.Name == "RightHand").Id == hand.Id && attachment.ParentPartId == hand.Id,
            "Hand attachment was disconnected");
        Check(updated.Parts.Single(p => p.Id == coat.Id).Group && updated.Parts.Single(p => p.Id == coat.Id).Faces.Count == 0,
            "Removed clothing still renders or lost its recorded references");
        Check(updated.Parts.Any(p => p.Name == "shirt" && !p.Group), "New garment was omitted");
        Check(actor.Transform.Position == new CreationVector(3, 4, 5) && actor.Pose[arm.Id].Rotation.Y == 20,
            "Appearance reset actor placement or pose");
        Check(clip.AnimationId == actions[animation.Id] && otherClip.AnimationId == animation.Id,
            "Appearance changed the wrong clip's action");
        var copied = project.Animations.First(a => a.Id == clip.AnimationId);
        Check(copied.Tracks[0].PartId == arm.Id && copied.Tracks[0].Id != track.Id
            && copied.Tracks[0].Keys[0].Id != track.Keys[0].Id, "Copied action references or identities are invalid");
        copied.Tracks[0].Keys[0].Value = new(0, 90, 0);
        Check(track.Keys[0].Value == default && model.Parts.Count == 4, "Appearance mutated shared source data");
        Check(captured.Parts[0].Id == capturedBody.Id && before.Objects[0].ModelId == model.Id,
            "Appearance mutated its capture or history snapshot");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
