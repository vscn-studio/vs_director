using VSDirector.Data;

internal static class CreationEditingContextChecks
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"director-context-"+Guid.NewGuid().ToString("N"));
        try
        {
            var bone=new CreationPart();var model=new CreationModel{Parts=[bone]};var actor=new CreationSceneObject{ModelId=model.Id};
            var project=new DirectorCreationProject{Models=[model],Objects=[actor]};var s=new DirectorCreationSession(new(root));s.Open(project);
            s.SelectedObject=actor.Id;s.Seek(2);
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(3,0,0)});s.Changed();s.EndGesture("place");
            if(project.Tracks.Count!=0||actor.Transform.Position.X!=3||s.HasTransformDraft)throw new Exception("Placement created keys or pending preview");
            s.SetEditTask(CreationEditTask.Animation);
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(5,0,0)});s.Changed();s.EndGesture("animate");
            if(project.Tracks.Single().Keys.Count!=2||project.Tracks[0].Keys.First(k=>k.Time==0).Value.X!=3)throw new Exception("First animation edit lost opening state");
            s.Undo();if(s.Project!.Tracks.Count!=0)throw new Exception("Seed and destination are not one undo");s.Redo();project=s.Project!;actor=project.Objects[0];
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(8,0,0)});s.Changed();s.CancelGesture();
            if(s.Frame!.Objects[actor.Id].Transform.Position.X!=5)throw new Exception("Cancelled gesture retained authored values");
            project=s.Project!;actor=project.Objects[0];s.BeginScenePose(actor.Id);
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,50,0)});s.Changed();s.EndGesture("pose");
            if(s.HasPoseDraft||actor.PoseKeys.Count!=2||actor.PoseKeys.First(k=>k.Time==2).Bones[bone.Id].Rotation.Y!=50)throw new Exception("Automatic pose did not commit seed and target");
            s.Undo();if(s.Project!.Objects[0].PoseKeys.Count!=0)throw new Exception("Auto pose is not atomic");s.Redo();
            s.Seek(3);s.AutoKey=false;s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,75,0)});s.Changed();s.EndGesture("preview");
            if(!s.HasPoseDraft||s.Project!.Objects[0].PoseKeys.Count!=2)throw new Exception("Manual pose changed source before commit");
            bool blocked=false;try{s.SetEditTask(CreationEditTask.Placement);}catch(InvalidOperationException){blocked=true;}
            if(!blocked)throw new Exception("Task switch silently discarded preview");
            s.CommitTransformDrafts();if(s.HasPoseDraft||s.Project.Objects[0].PoseKeys.Count!=3)throw new Exception("Manual pose commit failed");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,80,0)});s.Changed();s.CancelGesture();
            if(s.HasPoseDraft)throw new Exception("Cancelling a fresh manual pose created a draft");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,82,0)});s.Changed();s.EndGesture("preview");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,95,0)});s.Changed();s.CancelGesture();
            if(!s.HasPoseDraft||s.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y!=82)throw new Exception("Cancelling a later drag lost the existing preview");
            s.ClearTransformDrafts();
            s.SetEditTask(CreationEditTask.Placement);s.Seek(2);s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(9,0,0)});s.Changed();s.EndGesture("base");
            if(s.Project.Objects[0].Transform.Position.X!=9||s.Project.Tracks[0].Keys.First(k=>k.Time==2).Value.X!=5)throw new Exception("Base edit rewrote animation");
            s.Save();s.Store.Load(project.Id).Validate();
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
