using System.Numerics;
using VSDirector.Data;

internal static class CreationPoseEditingChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "director-pose-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (int version in new[] { 0, 1 })
            {
                var a = new CreationPart { Name = "A", To = new(2,16,2) };
                var b = new CreationPart { Name = "B", ParentId = a.Id, From = new(0,16,0), Origin = new(0,16,0), To = new(2,32,2) };
                var c = new CreationPart { Name = "C", ParentId = b.Id, From = new(0,16,0), Origin = new(0,16,0), To = new(2,32,2) };
                var model = new CreationModel { AnimationVersion = version, Parts = [a,b,c] };
                var actor = new CreationSceneObject { ModelId = model.Id };
                var action = new CreationAnimation { ModelId = model.Id, Duration = 3, PoseEditing = true };
                var project = new DirectorCreationProject { Models = [model], Objects = [actor], Animations = [action] };
                var session = new DirectorCreationSession(new(root)); session.Open(project); session.AutoKey=false; session.EnterAnimation(action.Id,actor.Id);
                session.SavePose(); session.Seek(1);
                void Pose(Guid part, CreationVector rotation)
                {
                    session.BeginGesture(); session.SetTransform(actor.Id,part,new() { Rotation = rotation }); session.Changed(); session.EndGesture("pose");
                }
                Pose(a.Id,new(0,0,20)); Pose(b.Id,new(0,0,-35));
                if (!session.HasPoseDraft || actor.Pose.Count != 0 || action.Tracks.Any(t => t.Keys.Any(k => k.Time == 1))) throw new Exception("Pose draft modified source data before Save pose");
                session.Undo();
                if (session.Frame!.Objects[actor.Id].Pose[b.Id].Rotation.Z != 0 || session.Frame.Objects[actor.Id].Pose[a.Id].Rotation.Z != 20) throw new Exception("Draft undo discarded unrelated part edits");
                session.Redo(); session.SavePose();
                if (session.HasPoseDraft || action.Tracks.Count(t => t.PartId != Guid.Empty && t.Keys.Any(k => k.Time == 1)) != 15) throw new Exception("Save pose did not save the complete skeleton atomically");
                session.Undo();
                if (session.EditingAnimation!.Tracks.Any(t => t.Keys.Any(k => k.Time == 1))) throw new Exception("Saved pose undo left partial keys");
                session.Redo(); session.Seek(1);
                if (session.Frame!.Objects[actor.Id].Pose[b.Id].Rotation.Z != -35) throw new Exception("Saved pose redo changed the pose");
                Pose(c.Id,new(10,0,0)); session.Tick(31); session.FlushRecovery();
                var recovered = new DirectorCreationSession(new(root)); recovered.Open(session.Store.Load(project.Id,true));
                if (!recovered.PoseEditing || !recovered.HasPoseDraft || recovered.EditTime != 1 || recovered.Frame!.Objects[actor.Id].Pose[c.Id].Rotation.X != 10) throw new Exception("Recovery lost uncommitted pose or edit context");
                if (recovered.EditingAnimation!.Tracks.First(t => t.PartId == c.Id && t.Channel == CreationChannel.Rotation).Keys.Last().Value.X != 0) throw new Exception("Recovery committed an unconfirmed pose");
                recovered.ClearTransformDrafts();
                if (recovered.Frame!.Objects[actor.Id].Pose[c.Id].Rotation.X != 0) throw new Exception("Discard pose failed");
                var rest = DirectorCreationEvaluator.Evaluate(project,0).Objects[actor.Id];
                var centre = Vector3.Transform(new Vector3(.0625f,.5f,.0625f),rest.Bones[a.Id]);
                if (DirectorCreationPoseGeometry.Pick(model,rest,centre+new Vector3(0,0,5),-Vector3.UnitZ) != a.Id) throw new Exception("Model surface picking missed the visible part");
                var target = new Vector3(1,1.6f,.2f);
                var solved = DirectorCreationPoseGeometry.Pull(model,rest,c.Id,target);
                var state = new CreationObjectState { Source = actor, World = rest.World };
                foreach (var pair in solved) state.Pose[pair.Key] = pair.Value;
                DirectorCreationEvaluator.EvaluateBones(model,state);
                var end = Vector3.Transform(((c.To-c.From)*(1d/32)).Float(),state.Bones[c.Id]);
                if (Vector3.Distance(end,target) > .06f) throw new Exception($"Generic CCD failed for anonymous hierarchy v{version}: {end} / {target}");
                if (solved.Values.Any(p => p.Scale != CreationVector.One)) throw new Exception("CCD stretched a part");
                foreach (var extreme in new[] { new Vector3(100,20,-100), new Vector3(0,-10,0), Vector3.Zero })
                {
                    var unreachable = DirectorCreationPoseGeometry.Pull(model,rest,c.Id,extreme);
                    if (unreachable.Values.Any(t => !t.Rotation.Finite() || !t.Position.Finite() || t.Scale != CreationVector.One)) throw new Exception("Unreachable CCD target stretched or corrupted the skeleton");
                }
                foreach (double sign in new[] {-1d,1d})
                {
                    var transformed = new CreationTransform { Position = new(13,-5,20), Rotation = new(40,130,-20), Scale = new(sign*2,1.5,.75) }.Matrix();
                    rest.World = transformed;
                    var worldTarget = Vector3.Transform(target,transformed);
                    var transformedPose = DirectorCreationPoseGeometry.Pull(model,rest,c.Id,worldTarget);
                    var transformedState = new CreationObjectState { Source = actor, World = transformed };
                    foreach(var pair in transformedPose) transformedState.Pose[pair.Key]=pair.Value;
                    DirectorCreationEvaluator.EvaluateBones(model,transformedState);
                    var worldEnd=Vector3.Transform(((c.To-c.From)*(1d/32)).Float(),transformedState.Bones[c.Id]*transformed);
                    if(Vector3.Distance(worldEnd,worldTarget)>.12f) throw new Exception("CCD does not respect rotated/reflected/nonuniform actor space");
                    var faceCentre=Vector3.Transform(new Vector3(.0625f,.5f,.0625f),rest.Bones[a.Id]*transformed);
                    var rayDirection=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,rest.Bones[a.Id]*transformed));
                    if(DirectorCreationPoseGeometry.Pick(model,rest,faceCentre+rayDirection*5,-rayDirection)!=a.Id) throw new Exception("Surface picking differs from transformed geometry");
                }
                rest.World=Matrix4x4.Identity;
                model.RigEditing.Anchors.Add(b.Id);
                if (DirectorCreationPoseGeometry.Chain(model,c.Id).First().Id != b.Id) throw new Exception("Generic chain ignored its anchor");
                model.RigEditing.Roots[c.Id] = b.Id; project.Validate();
                var duplicate = DirectorCreationEdits.Duplicate(project); duplicate.Validate();
                if (duplicate.Models[0].RigEditing.Roots.Values.Single() != duplicate.Models[0].Parts[1].Id) throw new Exception("Copied rig settings retain original ids");
                DirectorCreationEdits.DeletePart(duplicate,duplicate.Models[0],duplicate.Models[0].Parts[1].Id); duplicate.Validate();
                var savedAction = recovered.EditingAnimation!;
                DirectorCreationSession.CopyPoseTime(savedAction,1,2,false);
                if (savedAction.Tracks.Count(t => t.PartId != Guid.Empty && t.Keys.Any(k => k.Time == 2)) != 15) throw new Exception("Pose copy omitted channels");
                DirectorCreationSession.CopyPoseTime(savedAction,2,2.5,true);
                if (savedAction.Tracks.Any(t => t.PartId != Guid.Empty && t.Keys.Any(k => k.Time == 2))) throw new Exception("Pose move left source keys");
                var attachment = new CreationSceneObject { ParentId = actor.Id, ParentPartId = b.Id };
                recovered.Project!.Objects.Add(attachment); recovered.Seek(1);
                recovered.BeginGesture(); recovered.SetTransform(actor.Id,b.Id,new() { Rotation = new(0,0,60) }); recovered.Changed(); recovered.EndGesture("pose");
                var attached = recovered.Frame!.Objects[attachment.Id]; var parentState = recovered.Frame.Objects[actor.Id];
                if (Vector3.Distance(attached.World.Translation,(parentState.Bones[b.Id]*parentState.World).Translation) > .0001) throw new Exception("Pose draft did not update attached object world transform");
                recovered.SetProperty(actor.Id,Guid.Empty,CreationChannel.Color,new(.2,.4,.6),()=>{}); recovered.Changed(); recovered.SavePose();
                if(recovered.HasTransformDraft || recovered.Frame!.Objects[actor.Id].Color != new CreationVector(.2,.4,.6)) throw new Exception("Save pose lost object property drafts");
                var pivotPose = new CreationTransform { Pivot = new(.3,.2,-.1), Position = new(.1,-.2,.3), Rotation = new(20,70,-30), Scale = new(1.2,.8,1.1) };
                var exportAction = new CreationAnimation { ModelId = model.Id, Duration = 1 };
                DirectorCreationSession.WritePose(exportAction,new() { Bones = new() { [a.Id] = pivotPose } },0);
                var imported = DirectorCreationShape.ImportAnimation(DirectorCreationShape.ExportAnimation(exportAction,model),model);
                var importedState = DirectorCreationEvaluator.Evaluate(project,0,editingAction:imported,actionActor:actor.Id).Objects[actor.Id];
                var expected = DirectorCreationMath.BoneMatrix(a,pivotPose,version);
                foreach(var point in new[] { Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ })
                    if(Vector3.Distance(Vector3.Transform(point,expected),Vector3.Transform(point,importedState.Bones[a.Id]))>.0001f) throw new Exception("Native export lost animated pivot");
                exportAction.Tracks.First(t=>t.Channel==CreationChannel.Visible).Keys[0].Value = default;
                bool rejected = false;
                try { DirectorCreationShape.ExportAnimation(exportAction,model); } catch(InvalidDataException) { rejected=true; }
                if(!rejected) throw new Exception("Native export silently lost visibility keys");
                recovered.BeginGesture(); recovered.SetProperty(actor.Id,a.Id,CreationChannel.Visible,default,()=>{}); recovered.Changed(); recovered.EndGesture("hide"); recovered.SavePose();
                if (!recovered.Frame!.Objects[actor.Id].HiddenParts.Contains(c.Id)) throw new Exception("Parent visibility did not hide children");
                recovered.BeginGesture(); recovered.SetProperty(actor.Id,a.Id,CreationChannel.Visible,new(1,0,0),()=>{}); recovered.Changed(); recovered.EndGesture("show"); recovered.SavePose();
                if (recovered.Frame!.Objects[actor.Id].HiddenParts.Contains(c.Id)) throw new Exception("Saving inherited visibility permanently hid children");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
