using VSDirector.Data;
internal static class CreationScenePoseChecks
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"scene-poses-"+Guid.NewGuid().ToString("N"));
        try
        {
            var part=new CreationPart();var child=new CreationPart { ParentId=part.Id };var model=new CreationModel { Parts=[part,child] };
            var actor=new CreationSceneObject { ModelId=model.Id };var camera=new CreationSceneObject { Kind=CreationObjectKind.Camera };
            var p=new DirectorCreationProject { Models=[model],Objects=[actor,camera],CameraId=camera.Id };
            DirectorCreationEdits.SetKey(p,camera.Id,Guid.Empty,CreationChannel.Position,0,default);DirectorCreationEdits.SetKey(p,camera.Id,Guid.Empty,CreationChannel.Position,4,new(4,0,0));
            var s=new DirectorCreationSession(new(root));s.Open(p);s.AutoKey=false;s.Seek(1);s.BeginScenePose(actor.Id);s.SavePose();
            s.Seek(3);s.BeginGesture();s.SetTransform(actor.Id,part.Id,new() { Rotation=new(0,0,90) });s.Changed();s.EndGesture("pose");
            if(s.SelectedAnimation!=Guid.Empty||s.EditTime!=3||s.Frame!.Objects[camera.Id].Transform.Position.X!=3)throw new Exception("Scene posing detached the scene clock");
            s.SavePose();if(actor.PoseKeys.Count!=2||actor.PoseKeys[0].Bones.Count!=0||actor.PoseKeys[1].Bones.Count!=1||p.Tracks.Count!=1)throw new Exception("Scene pose exploded into per-part channels");
            s.Seek(2);if(Math.Abs(s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Z-45)>.001||s.Frame.Objects[camera.Id].Transform.Position.X!=2)throw new Exception("Scene pose interpolation or camera synchronization failed");
            s.Undo();if(s.Project!.Objects[0].PoseKeys.Count!=1)throw new Exception("Pose save is not atomic");s.Redo();
            s.Save();var loaded=s.Store.Load(p.Id);loaded.Validate();
            var copied=DirectorCreationEdits.Duplicate(loaded);copied.Validate();if(copied.Objects[0].PoseKeys[1].Bones.Keys.Single()!=copied.Models[0].Parts[0].Id)throw new Exception("Scene pose duplication failed to remap bones");
            DirectorCreationEdits.DeletePart(copied,copied.Models[0],copied.Models[0].Parts[0].Id);copied.Validate();
            var current=s.Project.Objects[0];var key=current.PoseKeys.Last();DirectorCreationScenePoses.Move(current,key.Id,4,true);if(current.PoseKeys.Count!=3||current.PoseKeys.Last().Id==key.Id)throw new Exception("Pose copy failed");
            s.Seek(.5);if(s.Frame!.Objects[actor.Id].Pose.GetValueOrDefault(part.Id)?.Rotation.Z is >0)throw new Exception("Pose leaks before first key");
            s.Seek(2);s.BeginGesture();s.SetTransform(actor.Id,part.Id,new() { Rotation=new(0,0,70) });s.Changed();s.EndGesture("draft");s.Tick(31);s.FlushRecovery();
            var recovered=new DirectorCreationSession(new(root));recovered.Open(s.Store.Load(p.Id,true));
            if(!recovered.ScenePoseEditing||recovered.EditTime!=2||!recovered.HasPoseDraft||recovered.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Z!=70)throw new Exception("Scene pose draft recovery lost context");
            recovered.ClearTransformDrafts();recovered.EndScenePose();if(recovered.SelectedAnimation!=Guid.Empty||recovered.Time!=2)throw new Exception("Leaving pose selection changed scene time");
            var performance=new CreationAsset { Kind=CreationAssetKind.Performance,Source="assets/scene-pose-take.json" };p.Assets.Add(performance);
            var recorded=new CreationTrack { PartId=part.Id,Channel=CreationChannel.Rotation };
            DirectorCreationEdits.SetKey(recorded,0,new(0,0,15));DirectorCreationEdits.SetKey(recorded,1,new(0,0,25));
            p.Performances[performance.Id]=new() { Tracks=[recorded],Action=new() { Layers=[new() { Frames=[new() { TimeSeconds=0 },new() { TimeSeconds=1 }] }] } };
            p.Clips.Add(new() { ObjectId=actor.Id,PerformanceAssetId=performance.Id,Start=4,Duration=1 });
            var recordedFrame=DirectorCreationEvaluator.Evaluate(p,4.5).Objects[actor.Id];
            if(!recordedFrame.HasPerformance || Math.Abs(recordedFrame.Pose[part.Id].Rotation.Z-20)>.001) throw new Exception("Scene poses pinned a later recorded performance");
            var resume=DirectorCreationEvaluator.Evaluate(p,5.1).Objects[actor.Id];
            if(resume.HasPerformance || Math.Abs(resume.Pose[part.Id].Rotation.Z-90)>.001) throw new Exception("Recorded pose leaked beyond its clip into scene poses");
            recovered.BeginScenePose(actor.Id);recovered.Edit("delete actor",project=>DirectorCreationEdits.DeleteObject(project,actor.Id));
            if(recovered.ScenePoseEditing)throw new Exception("Deleting a posed actor retained a stale pose context");recovered.Undo();recovered.Project!.Validate();
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
