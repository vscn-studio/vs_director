using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationWorkflow(object workspace, DirectorCreationSession session)
    {
        var original=session.Project!;
        object? Call(string method,params object[] args)=>AccessTools.Method(workspace.GetType(),method).Invoke(workspace,args);
        object Get(string field)=>AccessTools.Field(workspace.GetType(),field).GetValue(workspace)!;
        void Set(string field,object? value)=>AccessTools.Field(workspace.GetType(),field).SetValue(workspace,value);
        void Require(bool condition,string why){if(!condition)throw new Exception(why);}
        try
        {
            var first=new CreationPart{Name="head"};var second=new CreationPart{Name="body"};
            var model=new CreationModel{Parts=[first,second],BodyMapping=new(){{"head",first.Id},{"body",second.Id}}};
            var actor=new CreationSceneObject{ModelId=model.Id};
            var action=new CreationAnimation{Name="Shared",ModelId=model.Id,Duration=4,PoseEditing=true};
            var clip=new CreationClip{ObjectId=actor.Id,AnimationId=action.Id,Start=3,Duration=2,SourceStart=.5,Speed=1.5};
            var other=new CreationClip{ObjectId=actor.Id,AnimationId=action.Id,Start=6,Duration=2};
            var project=new DirectorCreationProject{Models=[model],Objects=[actor],Animations=[action],Clips=[clip,other]};
            session.Open(project);session.BeginScenePose(actor.Id);session.SelectedPart=first.Id;session.Seek(4);
            var bones=(HashSet<Guid>)Get("selectedBones");bones.UnionWith([first.Id,second.Id]);
            var objects=(HashSet<Guid>)Get("selectedObjects");objects.Add(actor.Id);
            ((HashSet<Guid>)Get("pinnedTimelineObjects")).Add(actor.Id);
            Set("timelineZoom",3d);Set("timelineStart",1d);Call("RestoreTimelineView");
            Set("timelineZoom",3d);Set("timelineStart",1d);((HashSet<Guid>)Get("pinnedTimelineObjects")).Add(actor.Id);
            Call("EditClipAnimation",clip.Id);
            Require(session.SelectedAnimation==action.Id&&Math.Abs(session.EditTime-2)<1e-8,"Clip source mapping lost offset/speed");
            Set("timelineZoom",2d);Call("MakeActionIndependent");
            Require(clip.AnimationId!=action.Id&&other.AnimationId==action.Id&&session.SelectedAnimation==clip.AnimationId,"Independent action rewired another shared clip");
            Require(Math.Abs(session.EditTime-2)<1e-8,"Action copy lost source cursor");
            Call("ReturnToProject");
            Require(session.ScenePoseActor==actor.Id&&session.SelectedPart==first.Id&&session.Time==4&&bones.SetEquals([first.Id,second.Id]),"Returning from source lost scene pose selection/time");
            Require((double)Get("timelineZoom")==3&&(double)Get("timelineStart")==1&&((HashSet<Guid>)Get("pinnedTimelineObjects")).Contains(actor.Id),"Returning from source lost timeline view");
            session.BeginGesture();Call("SetTransform",actor,new CreationTransform{Rotation=new(0,20,0)});session.Changed();session.EndGesture("two bones");
            Require(actor.PoseKeys.Last().Bones[first.Id].Rotation.Y==20&&actor.PoseKeys.Last().Bones[second.Id].Rotation.Y==20,"Multi-bone delta was not applied together");
            session.Undo();Require(session.Project!.Objects[0].PoseKeys.Count==0,"Multi-bone auto pose was not atomic");session.Redo();
            project=session.Project!;actor=project.Objects[0];
            Set("bonesPanel",false);Set("properties",false);Set("ikPanel",false);Call("SelectBone",actor.Id,first.Id);
            Require(!(bool)Get("bonesPanel")&&!(bool)Get("properties")&&!(bool)Get("ikPanel"),"Selection reopened a closed panel");
            var hand=new CreationSceneObject{Name="RightHand",ActorHand=1};
            Require((string)Call("ObjectName",hand)! != "RightHand","Internal hand name was exposed");hand.Name="Custom glove";
            Require((string)Call("ObjectName",hand)! == "Custom glove","Custom attachment name was replaced");
            session.EndScenePose();session.SetEditTask(CreationEditTask.Animation);session.SelectedObject=actor.Id;
            var track=new CreationTrack{ObjectId=actor.Id,Channel=CreationChannel.Position};project.Tracks.Add(track);
            var a=DirectorCreationEdits.SetKey(track,1,default);var b=DirectorCreationEdits.SetKey(track,2,new(1,0,0));var c=DirectorCreationEdits.SetKey(track,4,new(2,0,0));
            var keys=track.Keys.Select(k=>(k.Id,k.Time)).ToArray();
            Call("SelectTimelineKey",a.Id,keys,false,false);Call("SelectTimelineKey",b.Id,keys,false,true);
            Require(((HashSet<Guid>)Get("selectedTimelineKeys")).SetEquals([a.Id,b.Id]),"Range selection did not include both keys");
            Call("BeginKeyMove");Call("MoveSelectedKeys",-100d);Call("FinishKeyMove");
            Require(a.Time==0&&b.Time==1&&c.Time==4,"Group move did not clamp as a group or changed unrelated key");
            session.Undo();project=session.Project!;track=project.Tracks.Last();
            Require(track.Keys.Any(k=>k.Id==a.Id&&k.Time==1)&&track.Keys.Any(k=>k.Id==b.Id&&k.Time==2),"Group move did not undo atomically");
            Call("DeleteTimelineSelection");Require(project.Objects.Count==1&&track.Keys.Count==1&&track.Keys[0].Id==c.Id,"Timeline Delete removed the actor or unrelated keys");
            session.Undo();project=session.Project!;
            track=project.Tracks.Last();keys=track.Keys.Select(k=>(k.Id,k.Time)).ToArray();
            Call("SelectTimelineKey",a.Id,keys,false,false);Call("SelectTimelineKey",b.Id,keys,false,true);
            var io=ImGui.GetIO();string clipboard="";
            nint oldRead=io.GetClipboardTextFn,oldWrite=io.SetClipboardTextFn,buffer=0;
            ClipboardRead read=_=>{if(buffer!=0)System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer);return buffer=System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(clipboard);};
            ClipboardWrite write=(_,text)=>clipboard=System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text)??"";
            io.GetClipboardTextFn=System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(read);
            io.SetClipboardTextFn=System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(write);
            var modifiers=AccessTools.Method(workspace.GetType().Assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime"),"QueueModifierEvents");
            void KeyboardFrame()
            {
                ImGui.NewFrame();ImGui.Begin("workflow-clipboard");Call("HandleTimelineKeys");ImGui.End();ImGui.Render();
            }
            void Press(ImGuiKey key)
            {io.AddKeyEvent(key,true);KeyboardFrame();io.AddKeyEvent(key,false);KeyboardFrame();}
            try
            {
                io.AddKeyEvent(ImGuiKey.LeftCtrl,true);io.KeyCtrl=true;modifiers.Invoke(null,null);KeyboardFrame();Press(ImGuiKey.C);
                Require(ImGui.GetClipboardText().Contains("selection"),"Multi-key copy lost the selection payload");
                session.Seek(6);Press(ImGuiKey.V);
                Require(track.Keys.Count==5&&track.Keys.Any(k=>k.Time==6)&&track.Keys.Any(k=>k.Time==7),"Group paste lost relative key spacing");
                session.Undo();Require(session.Project!.Tracks.Last().Keys.Count==3,"Group paste was not one undo");
            }
            finally
            {
                io.AddKeyEvent(ImGuiKey.LeftCtrl,false);io.KeyCtrl=false;modifiers.Invoke(null,null);KeyboardFrame();
                io.GetClipboardTextFn=oldRead;io.SetClipboardTextFn=oldWrite;
                if(buffer!=0)System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer);GC.KeepAlive(read);GC.KeepAlive(write);
            }
            Set("timelineCurves",true);Set("curveAxis",1);Call("SaveTimelineView");Set("timelineCurves",false);Set("curveAxis",0);Call("RestoreTimelineView");
            Require((bool)Get("timelineCurves")&&(int)Get("curveAxis")==1,"Curve preferences were not restored");
            Console.WriteLine("PASS creation context: shared clip source/copy, scene return with time/selection/view, atomic multi-bone pose, closed panels, attachment labels, range/group keys and safe Delete");
        }
        finally
        {
            session.Open(original);session.SelectedObject=original.Objects[0].Id;
            Set("sceneContext",null);Set("activeViewKey",null);Set("timelineZoom",1d);Set("timelineStart",0d);Set("timelineCurves",false);
            Set("bonesPanel",true);Set("properties",true);Set("keyId",Guid.Empty);Set("clipId",Guid.Empty);Set("trackId",Guid.Empty);
            foreach(string field in new[]{"selectedBones","selectedObjects","selectedTimelineKeys","pinnedTimelineObjects"})((HashSet<Guid>)Get(field)).Clear();
        }
    }
}
