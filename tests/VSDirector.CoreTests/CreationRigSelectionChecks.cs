using System.Numerics;
using VSDirector.Data;

internal static class CreationRigSelectionChecks
{
    public static void Run()
    {
        var body=new CreationPart{Name="body",Group=true};
        var joints=Enumerable.Range(0,10).Select(i=>new CreationPart{Name="joint"+i,Origin=new(0,i*3,0),From=new(-1,i*3,-1),To=new(1,i*3+3,1)}).ToArray();
        for(int i=0;i<joints.Length;i++)joints[i].ParentId=i==0?body.Id:joints[i-1].Id;
        var coat=new CreationPart{Name="coat",ParentId=joints[3].Id,Origin=joints[3].Origin};
        var unknown=new CreationPart{Name="custom accessory",Origin=new(4,0,0)};
        var model=new CreationModel{Parts=[body,..joints,coat,unknown],BodyMapping=new(){{"body",body.Id}}};
        var action=new CreationAnimation{ModelId=model.Id,Tracks=joints.Select(p=>new CreationTrack{PartId=p.Id,Channel=CreationChannel.Rotation}).ToList()};
        var project=new DirectorCreationProject{Models=[model],Animations=[action]};
        void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        var targets=DirectorCreationRigSelection.Targets(project,model);
        Require(targets[coat.Id]==joints[3].Id && targets[unknown.Id]==unknown.Id,"Garment ownership or unknown part identity was lost");
        Require(ReferenceEquals(targets,DirectorCreationRigSelection.Targets(project,model)),"Unchanged topology rebuilds its cache");
        var chain=DirectorCreationRigSelection.Chain(project,model,joints[4].Id,4);
        Require(chain.Select(p=>p.Id).SequenceEqual(joints.Skip(1).Take(4).Select(p=>p.Id)),"Decorative branch interrupted bounded pull chain");
        Require(DirectorCreationRigSelection.Chain(project,model,joints[9].Id,8).Count==8,"Extended chain is not bounded at eight joints");
        model.RigEditing.Anchors.Add(joints[3].Id);
        Require(DirectorCreationRigSelection.Chain(project,model,joints[4].Id,8).Count==2,"Manual anchor ignored");
        model.RigEditing.Anchors.Clear();model.RigEditing.Roots[joints[4].Id]=body.Id;
        Require(DirectorCreationRigSelection.Chain(project,model,joints[4].Id,8)[0].Id==body.Id,"Explicit root did not override automatic body stop");
        model.RigEditing.Roots.Clear();
        Require(DirectorCreationRigSelection.Chain(project,model,joints[4].Id,8)[0].Id==joints[0].Id,"Automatic pull passed through body hub");
        model.BodyMapping["head"]=coat.Id;
        Require(DirectorCreationRigSelection.Targets(project,model)[coat.Id]==coat.Id,"Explicit mapping did not invalidate cached garment ownership");
        model.BodyMapping.Remove("head");coat.ParentId=unknown.Id;
        Require(DirectorCreationRigSelection.Targets(project,model)[coat.Id]==coat.Id,"Reparenting did not invalidate semantic map");
        coat.ParentId=joints[3].Id;
        foreach(int version in new[]{0,1})
        {
            model.AnimationVersion=version;
            var actor=new CreationSceneObject{ModelId=model.Id};
            var state=new CreationObjectState{Source=actor,World=Matrix4x4.CreateScale(-1,1,1)*Matrix4x4.CreateTranslation(4,2,-1)};
            DirectorCreationEvaluator.EvaluateBones(model,state);
            var last=joints[4];chain=DirectorCreationRigSelection.Chain(project,model,last.Id,4);
            var target=new Vector3(100,50,20);var local=((last.To-last.From)*(1d/32)).Float();
            float before=Vector3.Distance(Vector3.Transform(local,state.Bones[last.Id]*state.World),target);
            var result=DirectorCreationPoseGeometry.Pull(model,state,last.Id,target,chain);
            var solved=new CreationObjectState{Source=actor,World=state.World};
            foreach(var pair in result){Require(pair.Value.Scale==CreationVector.One,"Pull changed scale");solved.Pose[pair.Key]=pair.Value;}
            DirectorCreationEvaluator.EvaluateBones(model,solved);
            float after=Vector3.Distance(Vector3.Transform(local,solved.Bones[last.Id]*solved.World),target);
            Require(float.IsFinite(after)&&after<=before+.001f,"Unreachable target lost best reachable pose");
            Require(state.Pose.Count==0,"Pull mutated frozen source");
            foreach(var part in chain)
            {
                var a=Vector3.Transform(Vector3.Zero,state.Bones[part.Id]);var b=Vector3.Transform(Vector3.UnitY,state.Bones[part.Id]);
                var c=Vector3.Transform(Vector3.Zero,solved.Bones[part.Id]);var d=Vector3.Transform(Vector3.UnitY,solved.Bones[part.Id]);
                Require(Math.Abs(Vector3.Distance(a,b)-Vector3.Distance(c,d))<.001f,"Pull stretched a bone");
            }
            bool rejected=false;try{DirectorCreationPoseGeometry.Pull(model,state,last.Id,new(float.NaN,0,0),chain);}catch(InvalidOperationException){rejected=true;}
            Require(rejected,"Non-finite target was accepted");
        }
    }
}
