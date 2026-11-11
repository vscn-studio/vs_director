using VSDirector.Data;
internal static class CreationSelectionChecks
{
    public static void Run()
    {
        var bone=new CreationPart { Name="joint" };var clothing=new CreationPart { Name="jacket",ParentId=bone.Id };
        var model=new CreationModel { Parts=[bone,clothing] };var animation=new CreationAnimation { ModelId=model.Id,Tracks=[new() { PartId=bone.Id,Channel=CreationChannel.Rotation }] };
        var actor=new CreationSceneObject { ModelId=model.Id };var other=new CreationSceneObject { ModelId=model.Id };
        var hand=new CreationSceneObject { Name="RightHand",ParentId=actor.Id,ParentPartId=bone.Id,ActorHand=1 };
        var prop=new CreationSceneObject { ParentId=hand.Id };var target=new CreationSceneObject();var pole=new CreationSceneObject();
        var shared=new CreationSceneObject();var sharedPole=new CreationSceneObject();
        actor.IkChains.Add(new() { Upper=bone.Id,Lower=clothing.Id,Target=target.Id,Pole=pole.Id });
        actor.IkChains.Add(new() { Upper=bone.Id,Lower=clothing.Id,Target=shared.Id,Pole=sharedPole.Id });
        other.IkChains.Add(new() { Upper=bone.Id,Lower=clothing.Id,Target=shared.Id,Pole=sharedPole.Id });
        var project=new DirectorCreationProject { Models=[model],Objects=[actor,other,hand,prop,target,pole,shared,sharedPole],Animations=[animation] };
        DirectorCreationEdits.SetKey(project,hand.Id,Guid.Empty,CreationChannel.Position,0,default);
        project.Clips.Add(new() { ObjectId=actor.Id,AnimationId=animation.Id,Duration=1 });project.Validate();
        if(DirectorCreationPoseGeometry.PoseTarget(project,model,clothing.Id)!=bone.Id) throw new Exception("Decorative surface did not resolve to its authored joint");
        var noActions=project.Clone();noActions.Animations.Clear();
        if(DirectorCreationPoseGeometry.PoseTarget(noActions,model,clothing.Id)!=clothing.Id) throw new Exception("Generic model invents a skeletal mapping");
        int count=project.Objects.Count;DirectorCreationEdits.DeleteObject(project,Guid.Empty);
        if(project.Objects.Count!=count) throw new Exception("Empty selection deletes roots");
        string root=Path.Combine(Path.GetTempPath(),"director-select-"+Guid.NewGuid().ToString("N"));
        try
        {
            var session=new DirectorCreationSession(new(root));session.Open(project);
            session.Edit("delete",p=>DirectorCreationEdits.DeleteObjects(p,[actor.Id,hand.Id]));project.Validate();
            if(project.Objects.Any(o=>new[]{actor.Id,hand.Id,prop.Id,target.Id,pole.Id}.Contains(o.Id))||project.Tracks.Count!=0||project.Clips.Count!=0) throw new Exception("Deleting actor left descendants, private controls or tracks");
            if(project.Objects.All(o=>o.Id==shared.Id)||project.Objects.Count!=3||other.IkChains.Count!=1) throw new Exception("Deleting actor removed shared IK controls");
            session.Undo();session.Project!.Validate();
            if(session.Project.Objects.Count!=count||session.Project.Clips.Count!=1) throw new Exception("Cascade deletion undo did not restore hierarchy");
            session.Redo();session.Project.Validate();
            if(session.Project.Objects.Count!=3) throw new Exception("Cascade deletion redo failed");
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
