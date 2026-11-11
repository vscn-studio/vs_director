using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSDirector.Data;

internal static class CreationChecks
{
    public static void InteractionAndRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-interaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new CreationModel(); var actor = new CreationSceneObject { ModelId = model.Id };
            var clip = new CreationClip { ObjectId = actor.Id, Start = 1, Duration = 2 };
            var animation = new CreationAnimation { ModelId = model.Id, Tracks = [new CreationTrack { Keys = Enumerable.Range(0, 100000).Select(i => new CreationKeyframe { Time = i / 30d }).ToList() }] };
            var p = new DirectorCreationProject { Models = [model], Objects = [actor], Clips = [clip], Animations = [animation], Loop = true };
            var session = new DirectorCreationSession(new(root)); session.Open(p);
            session.BeginClipGesture(clip); session.EndGesture("click");
            Check(!session.History.CanUndo, "Selecting a clip created an undo entry");
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            session.BeginClipGesture(clip); clip.Start = 3; session.Changed(); session.EndGesture("drag");
            allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
            Check(allocation < 1000000, "Clip gesture copied unrelated animation data: " + allocation);
            session.Undo(); Near(session.Project!.Clips[0].Start, 1, "Clip undo");
            session.Redo(); Near(session.Project.Clips[0].Start, 3, "Clip redo");
            Check(ReferenceEquals(animation, session.Project.Animations[0]), "Clip undo rebuilt the animation library");
            var shifted = session.Project.Clips[0];
            session.BeginClipGesture(shifted); shifted.Start = 12; session.Changed(); session.EndGesture("extend");
            Near(session.Project.Duration, 14, "Moving past the end did not extend the timeline");
            session.Undo(); Near(session.Project.Duration, 10, "Undo did not restore the original timeline range");
            session.Redo(); Near(session.Project.Duration, 14, "Redo lost the extended timeline range");
            var clone = p.Clone(); clone.Animations[0].Tracks[0].Keys[0].Value = new(9, 0, 0);
            Check(animation.Tracks[0].Keys[0].Value == default, "History clone shares mutable keys");
            Check(clone.Id == p.Id && clone.Output.Fps == p.Output.Fps, "History clone lost settings");
            session.Edit("rename", document => document.Name = "Changed");
            allocation = GC.GetAllocatedBytesForCurrentThread();
            session.Undo(); session.Redo();
            allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
            Check(allocation < 1000000, "Full undo/redo recopied the large animation library: " + allocation);
            Check(session.Project.Name == "Changed", "Full redo lost its state");
            session.Undo(); session.Edit("branch", document => document.Name = "Branched");
            Check(!session.History.CanRedo, "Editing after undo retained the old branch");
            session.Undo(); Check(session.Project.Name != "Branched", "History snapshot was mutated by a later edit");
            p.Visuals.World.ShowNameTags = false; p.Visuals.Environment.OverrideTime = true; p.Visuals.Environment.TimeOfDayTick = 18000;
            var visualCopy = p.Clone(); visualCopy.Visuals.World.ShowNameTags = true;
            Check(!p.Visuals.World.ShowNameTags, "History shares mutable view settings");
            var roundTrip = CreationJson.Clone(p);
            Check(roundTrip.Visuals.Environment.TimeOfDayTick == 18000 && !roundTrip.Visuals.World.ShowNameTags, "View settings lost on save/load");
            session.ExternalRecording = true; session.Play(true);
            foreach (double time in new[] { 9d, 10, 10.5, 30, 65 })
            { session.AdvancePerformance(time); Near(session.Time, time, "Recording clamped at project end"); Check(session.Playing, "Recording stopped or looped at ten seconds"); }
            Check(session.Project.Duration > 65 && session.Project.LoopEnd == session.Project.Duration, "Recording did not extend duration");
            session.Play(false); session.ExternalRecording = false; session.Seek(0);
            session.Play(true); session.Tick(.1); Near(session.Time, .1, "Normal playback broken after recording");
            var origin = new CreationVector(512001.0244217447, 6.4444863433475215, 512043.2761506238);
            var nativeView = new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, -512010,-10,-512040,1 };
            var view = DirectorCreationMath.ProjectView(nativeView, origin, 0);
            var local = new CreationVector(512010, 3, 512040) - origin;
            Near(local.Y + view[13], -7, "World Y was mixed with project-local Y", .00001);
            nativeView[13] -= 32768;
            var dimensionView = DirectorCreationMath.ProjectView(nativeView, origin, 32768);
            Near(dimensionView[13], view[13], "Dimension offset applied twice");
            if (Environment.GetEnvironmentVariable("VSDIRECTOR_PROBE_PROJECT") is { Length: > 0 } source)
            {
                var document = JsonSerializer.Deserialize<DirectorCreationProject>(File.ReadAllText(source), CreationJson.Options)!;
                var large = new DirectorCreationSession(new(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
                large.Open(document);
                var sample = document.Clips.First(); double original = sample.Start;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                large.BeginClipGesture(sample); sample.Start += .1; large.Changed(); large.EndGesture("drag");
                Console.WriteLine($"Measured existing project clip gesture: {watch.Elapsed.TotalMilliseconds:0.0} ms");
                large.Undo(); Near(large.Project!.Clips[0].Start, original, "Existing project clip undo");
                watch.Restart(); _ = document.Clone();
                Console.WriteLine($"Measured existing project full history snapshot: {watch.Elapsed.TotalMilliseconds:0.0} ms");
                large.Edit("benchmark rename", project => project.Name += " benchmark");
                watch.Restart(); large.Undo();
                Console.WriteLine($"Measured existing project full undo: {watch.Elapsed.TotalMilliseconds:0.0} ms");
                watch.Restart(); large.Redo();
                Console.WriteLine($"Measured existing project full redo: {watch.Elapsed.TotalMilliseconds:0.0} ms");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static void EditingWorkflow()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-edit-flow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bone = new CreationPart(); var model = new CreationModel { Parts = [bone] };
            var actor = new CreationSceneObject { ModelId = model.Id };
            var other = new CreationSceneObject { ModelId = model.Id };
            var action = new CreationAnimation { ModelId = model.Id, Duration = 2 };
            var project = new DirectorCreationProject { Objects = [actor, other], Models = [model], Animations = [action] };
            var session = new DirectorCreationSession(new(root)); session.Open(project); session.SelectedObject = actor.Id;
            Check(session.AutoKey && session.EditTask == CreationEditTask.Placement, "Placement must default to base editing with automatic animation recording available");
            session.BeginGesture(); session.SetTransform(actor.Id, Guid.Empty, new() { Position = new(3, 0, 0) }); session.Changed(); session.EndGesture("place");
            Near(actor.Transform.Position.X, 3, "Static placement not saved"); Check(project.Tracks.Count == 0 && !session.HasTransformDraft, "Static placement created animation");
            session.SetEditTask(CreationEditTask.Animation); session.AutoKey = true; session.Seek(1); session.BeginGesture();
            session.SetTransform(actor.Id, Guid.Empty, new() { Position = new(4, 0, 0) }); session.Changed();
            session.SetTransform(actor.Id, Guid.Empty, new() { Position = new(6, 0, 0) }); session.Changed(); session.EndGesture("drag");
            Check(project.Tracks.Count == 1 && project.Tracks[0].Keys.Count == 2, "Auto key duplicated keys or unchanged channels");
            Near(actor.Transform.Position.X, 3, "Auto key changed base placement");
            session.Undo(); Check(session.Project!.Tracks.Count == 0, "Drag was not a single undo"); session.Redo();
            project = session.Project!; actor = project.Objects[0]; action = project.Animations[0];
            session.AutoKey = false; session.BeginGesture(); session.SetTransform(actor.Id, Guid.Empty, new() { Position = new(9, 0, 0) }); session.Changed(); session.EndGesture("preview");
            Check(session.HasTransformDraft, "Animated edit must be a pending preview"); Near(actor.Transform.Position.X, 3, "Preview changed base placement");
            session.CommitTransformDrafts(); Check(!session.HasTransformDraft, "Recording did not clear preview");
            Near(project.Tracks[0].Keys.First(k=>k.Time==1).Value.X, 9, "Preview key not recorded");
            session.Seek(7); session.EnterAnimation(action.Id, actor.Id); session.AutoKey = true; session.Seek(1.5);
            session.BeginGesture(); session.SetTransform(actor.Id, bone.Id, new() { Rotation = new(0, 45, 0) }); session.Changed(); session.EndGesture("bone key");
            Near(session.Time, 7, "Action editing moved project clock"); Near(session.AnimationTime, 1.5, "Action clock used clip offset");
            Check(action.Tracks.Count == 1 && action.Tracks[0].ObjectId == Guid.Empty && project.Tracks.Count == 1, "Action key leaked to scene");
            Near(session.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y, 45, "Action without scene clip cannot be previewed");
            Check(!session.Frame.Objects[other.Id].Pose.ContainsKey(bone.Id), "Action preview affected another actor");
            action.Loop = true; session.Seek(1.95); session.Play(true); session.Tick(.1); Near(session.AnimationTime, .05, "Action did not loop at its own duration");
            session.LeaveAnimation(); Near(session.Time, 7, "Returning from action lost project time");
            Check(!session.Frame!.Objects[actor.Id].Pose.ContainsKey(bone.Id), "Action preview survived returning to project");
            session.Save(); var loaded = session.Store.Load(project.Id); Check(loaded.Animations[0].Tracks.Count == 1, "Action keys were not saved");
            session.Edit("clip", p => p.Clips.Add(new() { ObjectId = actor.Id, AnimationId = action.Id, Start = 2, Duration = 2 }));
            session.Seek(3.5); Near(session.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y, 45, "Reusable action clip did not play in scene");
            session.AutoKey = false;
            var transform = session.Frame.Objects[actor.Id].Transform with { Position = new(12, 0, 0) };
            session.SetTransform(actor.Id, Guid.Empty, transform); session.Changed();
            transform = transform with { Scale = new(2, 2, 2) };
            session.SetTransform(actor.Id, Guid.Empty, transform); session.Changed();
            Near(session.Frame.Objects[actor.Id].Transform.Scale.X, 2, "Static channel remained stale behind animated preview");
            session.ClearTransformDrafts();
            Near(session.Frame.Objects[actor.Id].Transform.Position.X, 9, "Revert failed to restore animated position");
            Near(actor.Transform.Scale.X, 1, "Animation preview changed base scale");
            foreach (var channel in new[] { CreationChannel.Color, CreationChannel.Visible, CreationChannel.Fov })
            {
                session.AutoKey = true;
                session.Edit("property key", _ => session.SetProperty(actor.Id, Guid.Empty, channel, new(1, 1, 1), () => throw new Exception("Auto key changed base property")));
                session.AutoKey = false;
                session.SetProperty(actor.Id, Guid.Empty, channel, new(0, 0, 0), () => throw new Exception("Animated preview changed base property"));
                session.Changed(); Check(session.HasTransformDraft, "Property preview was not marked pending");
                session.CommitTransformDrafts();
                Near(project.Tracks.Single(t => t.Channel == channel).Keys.First(k=>k.Time==session.EditTime).Value.X, 0, "Property preview was not recorded");
                session.SetProperty(actor.Id, Guid.Empty, channel, new(2, 2, 2), () => { });
                session.ClearTransformDrafts();
                Check(!session.HasTransformDraft, "Revert retained a property preview");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double a, double b, string message, double tolerance = .0001) => Check(Math.Abs(a - b) < tolerance, $"{message}: {a} vs {b}");
    private static void MatrixNear(Matrix4x4 a, Matrix4x4 b, string message)
    {
        foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Check(Vector3.Distance(Vector3.Transform(p, a), Vector3.Transform(p, b)) < .003, message);
    }

    public static void Transforms()
    {
        var random = new Random(2301);
        for (int i = 0; i < 1000; i++)
        {
            var tr = new CreationTransform { Position = new(12, -5, .75), Scale = new(.5, 2, 3),
                Rotation = new(random.NextDouble() * 720 - 360, i < 4 ? new[] { 90d, -90, 89.9999, -89.9999 }[i] : random.NextDouble() * 720 - 360, random.NextDouble() * 720 - 360) };
            MatrixNear(tr.Matrix(), DirectorCreationMath.FromMatrix(tr.Matrix(), tr.Rotation).Matrix(), $"Euler round trip changed orientation {i}: {tr.Rotation} -> {DirectorCreationMath.FromMatrix(tr.Matrix(), tr.Rotation).Rotation}");
        }
        foreach (int version in new[] { 0, 1 })
        {
            var part = new CreationPart { From = new(2, 3, 4), Origin = new(1, 2, 3), Rotation = new(23, 140, -16), Scale = new(.8, 1.5, 2) };
            var pose = new CreationTransform { Position = new(.6, -.4, 2), Rotation = new(14, 25, 39), Scale = new(1.2, .8, 1.5), Pivot = new(.1, .4, -.3) };
            var local = DirectorCreationMath.BoneMatrix(part, pose, version);
            var decoded = DirectorCreationMath.PoseFromMatrix(part, local, pose, version);
            MatrixNear(local, DirectorCreationMath.BoneMatrix(part, decoded, version), "Native bone pose inverse failed");
        }
        foreach (float pitch in new[] { 0f, 75, 90, 180, 210, 270, 300 })
        foreach (float yaw in new[] { 0f, 46, 180, -125 })
        foreach (float roll in new[] { 0f, 37, -143 })
        {
            var native = new CameraPreset { Pitch = pitch, Yaw = yaw, Roll = roll };
            var camera = new CreationSceneObject { Kind = CreationObjectKind.Camera, Transform = new() { Rotation = DirectorCreationMath.CameraRotationFromNative(native) } };
            var p = new DirectorCreationProject { Objects = [camera], CameraId = camera.Id };
            var decoded = DirectorCreationEvaluator.Camera(p, DirectorCreationEvaluator.Evaluate(p, 0))!;
            var a = DirectorCameraManipulation.CameraHeadBasis(native); var b = DirectorCameraManipulation.CameraHeadBasis(decoded);
            Check(Vector3.Distance(a.Right, b.Right) < .002 && Vector3.Distance(a.Up, b.Up) < .002 && Vector3.Distance(a.Back, b.Back) < .002,
                $"Creation camera image reversed: {pitch}/{yaw}/{roll} -> {decoded.Pitch}/{decoded.Yaw}/{decoded.Roll}");
        }
    }

    public static void ActorsAndTimeline()
    {
        var part = new CreationPart { From = new(16, 0, 0), To = new(32, 16, 16) };
        var model = new CreationModel { Parts = [part] };
        var left = new CreationSceneObject { ModelId = model.Id, Pose = { [part.Id] = new() { Rotation = new(0, 0, 45) } } };
        var right = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(5, 0, 0) } };
        var prop = new CreationSceneObject { ParentId = left.Id, ParentPartId = part.Id, Transform = new() { Position = new(.5, 0, 0) } };
        var project = new DirectorCreationProject { Models = [model], Objects = [left, right, prop] };
        string shared = JsonSerializer.Serialize(model, CreationJson.Options);
        var frame = DirectorCreationEvaluator.Evaluate(project, 0);
        Check(frame.Objects[left.Id].Bones[part.Id] != frame.Objects[right.Id].Bones[part.Id], "Two actors shared a mutable pose");
        MatrixNear(frame.Objects[prop.Id].World, prop.Transform.Matrix() * frame.Objects[left.Id].Bones[part.Id] * frame.Objects[left.Id].World, "Attachment did not follow bone");
        Check(JsonSerializer.Serialize(model, CreationJson.Options) == shared, "Evaluation mutated the shared model");

        var track = new CreationTrack { Channel = CreationChannel.Rotation };
        DirectorCreationEdits.SetKey(track, 0, new(0, 350, 0)); DirectorCreationEdits.SetKey(track, 2, new(0, 10, 0));
        Near(DirectorCreationEvaluator.Sample(track, 1, default).Y, 360, "Rotation took the long route");
        track.Channel = CreationChannel.Position; track.Keys[0].Value = default; track.Keys[1].Value = new(10, 0, 0);
        track.Keys[0].Interpolation = CreationInterpolation.Hold;
        Near(DirectorCreationEvaluator.Sample(track, 1.999, default).X, 0, "Hold interpolated");
        Near(DirectorCreationEvaluator.Sample(track, 2, default).X, 10, "Exact key boundary missed");
        track.Keys[0].Interpolation = CreationInterpolation.Bezier;
        Near(DirectorCreationEvaluator.Sample(track, 1, default).X, 5, "Default Bezier is not linear");

        var animation = new CreationAnimation { ModelId = model.Id, Duration = 2, Tracks = [track] };
        var clip = new CreationClip { ObjectId = right.Id, AnimationId = animation.Id, Duration = 4, Speed = .5 };
        project.Animations.Add(animation); project.Clips.Add(clip); project.Duration = 4;
        var before = DirectorCreationEvaluator.Evaluate(project, 3).Objects[right.Id].World;
        DirectorCreationEdits.Split(project, clip, 2);
        MatrixNear(before, DirectorCreationEvaluator.Evaluate(project, 3).Objects[right.Id].World, "Split changed source time");
        Near(DirectorCreationEvaluator.Evaluate(project, 4).Objects[right.Id].Transform.Position.X, 10, "Final project frame lost its clip pose");
        project.Validate();
    }

    public static void ShapeRoundTrip()
    {
        const string source = """
            {"textureWidth":32,"textureHeight":16,"customRoot":{"keep":true},"textures":{"all":"game:block/stone"},
            "elements":[{"name":"head","from":[1,2,3],"to":[5,6,7],"rotationOrigin":[2,3,4],"customPart":42,
                "attachmentpoints":[{"code":"hand","posX":2}],"faces":{"north":{"texture":"#all","uv":[0,0,4,4],"glow":30}}}],
            "animations":[{"name":"turn","version":1,"quantityframes":30,"onAnimationEnd":"Repeat","customAnimation":"keep",
                "keyframes":[{"frame":0,"elements":{"head":{"offsetX":16,"rotationY":20,"stretchZ":2}}}]}]}
            """;
        var (model, animations) = DirectorCreationShape.Import(source, "test");
        Check(model.AnimationVersion == 1, "Native animation version lost");
        Near(animations[0].Tracks.Find(t => t.Channel == CreationChannel.Position)!.Keys[0].Value.X, 1, "Native offset units wrong");
        var output = DirectorCreationShape.Export(model, animations);
        Check(output["customRoot"]!["keep"]!.GetValue<bool>(), "Unknown root field lost");
        Check(output["elements"]![0]!["customPart"]!.GetValue<int>() == 42, "Unknown element field lost");
        Check(output["elements"]![0]!["attachmentpoints"]![0]!["code"]!.GetValue<string>() == "hand", "Native attachment lost");
        Check(output["animations"]![0]!["customAnimation"]!.GetValue<string>() == "keep", "Unknown animation field lost");
        var imported = DirectorCreationShape.Import(output.ToJsonString(), "again");
        Near(imported.Animations[0].Tracks.Find(t => t.Channel == CreationChannel.Scale)!.Keys[0].Value.Z, 2, "Animation scale changed on round trip");
        Check(((JsonArray)output["animations"]![0]!["keyframes"]!).Count == 30, "Native animation not baked at 30 Hz");
    }

    public static void StoreAndSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-creation-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DirectorCreationStore(root); var session = new DirectorCreationSession(store);
            var actor = new CreationSceneObject(); var project = new DirectorCreationProject { Name = "动画作品", Objects = [actor] };
            session.Open(project); session.Save();
            session.Edit("move", p => p.Objects[0].Transform.Position = new(1, 2, 3));
            session.Undo(); Near(session.Project!.Objects[0].Transform.Position.X, 0, "Undo failed");
            session.Redo(); Near(session.Project!.Objects[0].Transform.Position.X, 1, "Redo failed");
            bool rejected = false;
            try { session.Edit("cycle", p => p.Objects[0].ParentId = p.Objects[0].Id); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && session.Project!.Objects[0].ParentId == Guid.Empty, "Invalid transaction was not rolled back");
            session.BeginGesture(); session.Project!.Objects[0].Transform.Position = new(5, 2, 3); session.Changed();
            session.Project.Objects[0].Transform.Position = new(8, 2, 3); session.Changed(); session.EndGesture("drag");
            session.Undo(); Near(session.Project.Objects[0].Transform.Position.X, 1, "Gesture was not a single undo step");

            session.Edit("key", p => DirectorCreationEdits.SetKey(p, actor.Id, Guid.Empty, CreationChannel.Position, 0, new(20, 0, 0)));
            session.SetEditTask(CreationEditTask.Animation); session.AutoKey=false; session.BeginGesture(); session.SetTransform(actor.Id, Guid.Empty, new() { Position = new(30, 0, 0) }); session.Changed(); session.EndGesture("draft");
            Near(session.Frame!.Objects[actor.Id].Transform.Position.X, 30, "Authored key hid the transform draft");
            session.Seek(0); Near(session.Frame!.Objects[actor.Id].Transform.Position.X, 20, "Seeking did not restore authored animation");
            session.Tick(31);
            session.FlushRecovery();
            // Filesystem timestamps can coincide when a recovery is saved immediately.
            File.SetLastWriteTimeUtc(Path.Combine(store.DirectoryFor(project.Id), "recovery.json"), File.GetLastWriteTimeUtc(Path.Combine(store.DirectoryFor(project.Id), "project.json")));
            Check(store.List().Single().Recovery, "Automatic recovery not offered");
            Check(store.Load(project.Id, true).Name == "动画作品", "Recovery load failed");
            session.Save(); Check(!store.List().Single().Recovery, "Save did not clear recovery");
            Check(File.Exists(Path.Combine(store.DirectoryFor(project.Id), "project.json.bak")), "Save did not preserve backup");
            bool escaped = false; try { store.AssetPath(project.Id, "../escape.png"); } catch (InvalidDataException) { escaped = true; }
            Check(escaped, "Asset path escaped project directory");
            string assetFile = Path.Combine(root, "image.png"); File.WriteAllBytes(assetFile, [1, 2, 3]);
            var asset = store.Import(session.Project!, assetFile, CreationAssetKind.Image); session.Save();
            var copy = store.Duplicate(session.Project!);
            Check(copy.Id != project.Id && copy.Objects[0].Id != actor.Id && copy.Tracks[0].ObjectId == copy.Objects[0].Id, "Project duplication did not remap references");
            Check(File.ReadAllBytes(store.AssetPath(copy.Id, copy.Assets[0].Source)).SequenceEqual(new byte[] { 1, 2, 3 }), "Duplicated project lost assets");
            var part = new CreationPart(); var model = new CreationModel { Parts = [part] };
            var appearance = new CreationSceneObject { ModelId = model.Id, HiddenParts = [part.Id], Pose = { [part.Id] = new() { Rotation = new(0, 45, 0) } } };
            var presetProject = new DirectorCreationProject { Models = [model], Objects = [appearance],
                Presets = [new() { ModelId = model.Id, Appearance = CreationJson.Clone(appearance) }] };
            var presetCopy = store.Duplicate(presetProject);
            Check(presetCopy.Presets[0].Appearance!.HiddenParts.SetEquals([presetCopy.Models[0].Parts[0].Id]), "Duplicated appearance preset retained old hidden-part IDs");
            Near(presetCopy.Presets[0].Appearance!.Pose[presetCopy.Models[0].Parts[0].Id].Rotation.Y, 45, "Duplication replaced appearance pose with a different preset pose");

            var parent = new CreationSceneObject { Kind = CreationObjectKind.Group, Transform = new() { Position = new(5, 0, 0), Rotation = new(0, 90, 0) } };
            var target = new CreationSceneObject { ParentId = parent.Id };
            var other = new CreationSceneObject();
            session.Open(new() { Duration = 1, Loop = true, Objects = [parent, target, other] });
            session.SelectedObject = target.Id; session.StartRecording();
            session.RecordSample(new(10, 2, 3), default);
            session.SelectedObject = other.Id; session.Tick(.1); session.RecordSample(new(11, 2, 3), default);
            session.Play(false);
            Check(!session.Recording && session.Project!.Tracks.All(t => t.ObjectId == target.Id), "Selection change redirected recording or pausing lost the recording");
            session.Seek(.1);
            var recordedWorld = session.Frame!.Objects[target.Id].World;
            Near(recordedWorld.Translation.X, 11, "Attached actor recording used world coordinates as local coordinates");
            Near(recordedWorld.Translation.Z, 3, "Attached actor recording changed Z position");
            session.StartRecording(); session.RecordSample(new(12, 2, 3), default); session.Save();
            Check(!session.Recording && !session.Dirty, "Saving did not commit the active recording");
            session.Seek(.9); session.StartRecording(); session.Tick(.2);
            Check(!session.Playing && session.Time == 1, "Recording wrapped the loop instead of reaching project end");
            session.StopRecording();
            store.Delete(copy.Id); Check(!Directory.Exists(store.DirectoryFor(copy.Id)) && Directory.Exists(Path.Combine(root, ".trash")), "Delete did not move to trash");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static void Particles()
    {
        var emitter = new CreationSceneObject { Kind = CreationObjectKind.Particle, ParticleStart = 1, ParticleDuration = 10, ParticleLife = 2, ParticleCount = 10 };
        Check(!DirectorCreationParticles.Sample(emitter, .9).Any(), "Particles born before emitter starts");
        var at5 = DirectorCreationParticles.Sample(emitter, 5).ToArray();
        Check(at5.Length == 20 && at5[0].Index == 21, "Continuous emitter stopped after initial lifetime");
        _ = DirectorCreationParticles.Sample(emitter, 9).ToArray();
        Check(at5.SequenceEqual(DirectorCreationParticles.Sample(emitter, 5)), "Backwards seek changed particle output");
        Check(DirectorCreationParticles.Sample(emitter, 11.5).Any(), "Particles vanished before their lifetime ended");
        Check(!DirectorCreationParticles.Sample(emitter, 13).Any(), "Particles outlived the tail");
    }
}
