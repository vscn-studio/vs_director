using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using VSDirector;
using VSDirector.Data;
using Vintagestory.API.Common;

internal static partial class DirectorImGuiProbe
{
    private static void CheckRigViewportMapping(Assembly assembly, ICoreClientAPI hostApi)
    {
        string root = Path.Combine(Path.GetTempPath(), "director-rig-viewport-" + Guid.NewGuid().ToString("N"));
        Type R(string name) => assembly.GetType(name, true)!;
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => 1280, "get_FrameHeight" => 800,
            "get_PerspectiveViewMat" => new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,-5,1 },
            "get_PerspectiveProjectionMat" => new double[] { .625,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-.2,0 },
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_Render" ? render : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var upper = new CreationPart { Name = "Upper", Origin = new(0, 12, 0), From = new(-1, 0, -1), To = new(1, 12, 1) };
        var lower = new CreationPart { Name = "Lower", ParentId = upper.Id, Origin = new(8, 12, 0), From = new(7, 12, -1), To = new(9, 24, 1) };
        var model = new CreationModel { Parts = [upper, lower] };
        var actor = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(.6, -.3, 0) } };
        var project = new DirectorCreationProject { Models = [model], Objects = [actor] };
        var session = new DirectorCreationSession(new(root)); session.Open(project); session.SelectedObject = actor.Id;
        var editingAction = new CreationAnimation { ModelId = model.Id }; project.Animations.Add(editingAction); session.EnterAnimation(editingAction.Id,actor.Id); session.AutoKey=false;
        var runtime = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Runtime.DirectorCreationRuntime"));
        var renderer = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Rendering.DirectorCreationRenderer"));
        AccessTools.Field(runtime.GetType(), "<Session>k__BackingField").SetValue(runtime, session);
        AccessTools.Field(runtime.GetType(), "<Renderer>k__BackingField").SetValue(runtime, renderer);
        AccessTools.Property(renderer.GetType(), "Visible").SetValue(renderer, true);
        var owner = new VSDirectorModSystem(); AccessTools.Field(owner.GetType(), "creation").SetValue(owner, runtime);
        var host = Activator.CreateInstance(R("VSDirector.Gui.DirectorImGuiWorkspace"), hostApi)!;
        var workspace = Activator.CreateInstance(R("VSDirector.Gui.DirectorCreationWorkspace"), BindingFlags.Instance | BindingFlags.NonPublic, null, [api, owner, host], null)!;
        AccessTools.Field(workspace.GetType(), "showAllBones").SetValue(workspace, true);
        var viewport = R("VSDirector.Rendering.DirectorWorkspaceViewportRuntime");
        object? Call(string name, params object[] args) => AccessTools.Method(workspace.GetType(), name).Invoke(workspace, args);
        var points = (List<(Guid Id, Vector2 Point)>)AccessTools.Field(workspace.GetType(), "bonePoints").GetValue(workspace)!;
        var io = ImGui.GetIO(); var display = io.DisplaySize; io.DisplaySize = new(1280,800);
        void Frame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("##rig-projection-probe", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground);
            try { Call("DrawViewportTools"); } finally { ImGui.End(); ImGui.Render(); }
        }
        try
        {
            foreach (var rect in new[] { (220,40,500,640), (100,80,1000,300), (50,100,800,500) })
            {
                AccessTools.Field(host.GetType(), "previewPosition").SetValue(host, new Vector2(rect.Item1,rect.Item2));
                AccessTools.Field(host.GetType(), "previewSize").SetValue(host, new Vector2(rect.Item3,rect.Item4));
                AccessTools.Method(viewport, "Configure").Invoke(null, [host,api,rect.Item1,rect.Item2,rect.Item3,rect.Item4,false]);
                Frame(); Frame();
                int width = rect.Item3, height = (int)Math.Round(width / 1.6);
                if (height > rect.Item4) { height = rect.Item4; width = (int)Math.Round(height * 1.6); }
                var min = new Vector2(rect.Item1 + (rect.Item3-width)/2, rect.Item2 + (rect.Item4-height)/2);
                var state = session.Frame!.Objects[actor.Id];
                foreach (var part in model.Parts)
                {
                    var joint = Vector3.Transform(DirectorCreationIk.Joint(part,state,model.AnimationVersion),state.World);
                    var expected = min + new Vector2((joint.X * .625f / (5-joint.Z)+1)*width/2, (1-joint.Y/(5-joint.Z))*height/2);
                    if (Vector2.Distance(points.Single(p => p.Id == part.Id).Point, expected) > .01)
                        throw new Exception("Bone overlay does not align with the letterboxed world image");
                }
                var actual = ((Vector2 Position, Vector2 Size))Call("GameImageRect")!;
                if (actual.Position != min || actual.Size != new Vector2(width,height)) throw new Exception("Gizmo rectangle differs from world blit");
            }
            session.SelectedPart = Guid.Empty; io.MousePos = points.Single(p => p.Id == lower.Id).Point; Frame(); Frame();
            io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (session.SelectedPart != lower.Id || AccessTools.Field(workspace.GetType(), "operation").GetValue(workspace)!.ToString() != "ROTATE")
                throw new Exception("Clicking a bone through the viewport input hole failed to select rotation: selected=" + session.SelectedPart + " hover=" + AccessTools.Field(workspace.GetType(), "gizmoHover").GetValue(workspace) + " error=" + AccessTools.Field(workspace.GetType(), "error").GetValue(workspace));
            Console.WriteLine("PASS rig overlay and gizmo align with fitted world image in tall/wide docks; joint click works through input hole and selects rotation");
            AccessTools.Field(workspace.GetType(), "showAllBones").SetValue(workspace, false);
            session.SelectedPart = Guid.Empty; Frame();
            if (points.Count != 0) throw new Exception("Unselected rig exposes every bone by default");
            var extra = new CreationPart { Name = "Unrelated", Origin = new(-8, 0, 0) }; model.Parts.Add(extra); session.Changed();
            Call("SelectBone", actor.Id, lower.Id); Frame();
            if (points.Count != 1 || points[0].Id != lower.Id) throw new Exception("Focused bone view must show only the selected joint");
            var controls = DirectorCreationIk.CreateControls(actor, model, session.Frame!.Objects[actor.Id], upper, lower);
            project.Objects.Add(controls.Target); project.Objects.Add(controls.Pole); actor.IkChains.Add(controls.Chain); session.Changed();
            var second = DirectorCreationIk.CreateControls(actor, model, session.Frame!.Objects[actor.Id], upper, lower);
            project.Objects.Add(second.Target); project.Objects.Add(second.Pole); actor.IkChains.Add(second.Chain); session.Changed();
            Call("Select", controls.Target); Frame();
            var objectPoints = (List<(Guid Id, Vector2 Point)>)AccessTools.Field(workspace.GetType(), "objectPoints").GetValue(workspace)!;
            if (objectPoints.Any(p => p.Id == second.Target.Id || p.Id == second.Pole.Id)) throw new Exception("Inactive IK controls are still visible/pickable");
            if (!objectPoints.Any(p => p.Id == controls.Target.Id) || AccessTools.Property(workspace.GetType(), "RigActor").GetValue(workspace) != actor)
                throw new Exception("Selecting an IK control lost its actor context");
            AccessTools.Field(workspace.GetType(), "showBones").SetValue(workspace, false); Frame();
            if (points.Count != 0 || objectPoints.Any(p => p.Id == controls.Target.Id || p.Id == controls.Pole.Id)) throw new Exception("Hidden rig controls remain pickable");
            Console.WriteLine("PASS focused rig display hides unrelated bones and inactive IK controls, retains actor context and clears hidden hit targets");
            actor.IkChains.Clear(); session.SelectedObject = actor.Id; session.SelectedPart = Guid.Empty; session.Changed();
            Call("NewAnimation", "Pose workflow");
            if (!session.PoseEditing || session.EditingAnimation!.Tracks.All(t => t.PartId != lower.Id)) throw new Exception("New action did not seed the initial pose");
            session.Seek(1); Call("SelectBone", actor.Id,upper.Id);
            session.BeginGesture(); session.SetTransform(actor.Id,upper.Id,new() { Rotation = new(0,0,15) }); session.Changed(); session.EndGesture("pose");
            Call("SelectBone",actor.Id,lower.Id);
            if (AccessTools.Field(workspace.GetType(), "pendingTransition").GetValue(workspace) is not null || !session.HasPoseDraft) throw new Exception("Same-frame bone selection prompts or drops draft");
            session.BeginGesture(); session.SetTransform(actor.Id,lower.Id,new() { Rotation = new(0,0,-20) }); session.Changed(); session.EndGesture("pose");
            bool continued = false; Call("Continue",(Action)(()=>continued=true));
            if (continued) throw new Exception("Leaving a dirty pose bypassed save decision");
            Call("ResolvePendingTransition",true);
            if (!continued || session.HasPoseDraft || session.EditingAnimation!.Tracks.Count(t=>t.PartId!=Guid.Empty && t.Keys.Any(k=>k.Time==1)) != model.Parts.Count*5) throw new Exception("Save-and-continue failed to save all pose channels");
            session.SelectedPart=Guid.Empty;
            var rendered=session.Frame!.Objects[actor.Id];
            var centre=Vector3.Transform(((lower.To-lower.From)*(1d/32)).Float(),rendered.Bones[lower.Id]*rendered.World);
            var imageRect=((Vector2 Position,Vector2 Size))Call("GameImageRect")!;
            io.MousePos=imageRect.Position+new Vector2((centre.X*.625f/(5-centre.Z)+1)*imageRect.Size.X/2,(1-centre.Y/(5-centre.Z))*imageRect.Size.Y/2);
            Frame(); Frame(); io.MouseDown[0]=true; Frame(); io.MouseDown[0]=false; Frame();
            if(session.SelectedPart!=lower.Id) throw new Exception("Direct model surface click did not select the rendered body part");
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float panelWidth in new[]{220f,320f,520f})
            {
                LoadCreationProbeLanguage(language);
                for(int i=0;i<3;i++)
                {
                    ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(panelWidth,600));
                    ImGui.Begin("##pose-workflow-panel"); Call("DrawBonesPanel");
                    if(i==2 && ImGui.GetScrollMaxX()>1) throw new Exception("Pose panel overflows at "+panelWidth);
                    ImGui.End(); ImGui.Render();
                }
            }
            LoadCreationProbeLanguage("zh-cn");
            CheckCreationTransformRows(workspace);
            Vector2 poseRow = default;
            void PoseRow()
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(700,200));
                ImGui.Begin("##pose-summary-probe",ImGuiWindowFlags.NoDecoration); poseRow=ImGui.GetCursorScreenPos();
                Call("DrawPoseTimelineRow",200f,400f,192f,36f,(System.Func<double,float>)(t=>200+(float)t*100),(System.Func<float,double>)(x=>Math.Clamp((x-200)/100,0,3)));
                ImGui.End(); ImGui.Render();
            }
            PoseRow(); io.MousePos=new(300,poseRow.Y+18); PoseRow();
            io.MouseDown[0]=true; PoseRow(); io.MousePos=new(400,poseRow.Y+18); PoseRow(); io.MouseDown[0]=false; PoseRow();
            if(session.EditingAnimation!.Tracks.Any(t=>t.PartId!=Guid.Empty && t.Keys.Any(k=>k.Time==1)) || session.EditingAnimation.Tracks.Count(t=>t.PartId!=Guid.Empty && t.Keys.Any(k=>k.Time==2))!=model.Parts.Count*5) throw new Exception("Pose summary drag did not move the entire pose");
            session.Undo();
            if(session.EditingAnimation!.Tracks.Count(t=>t.PartId!=Guid.Empty && t.Keys.Any(k=>k.Time==1))!=model.Parts.Count*5) throw new Exception("Pose drag undo left partial keys");
            Console.WriteLine("PASS real pose-summary drag moves all channels in one operation and undo restores the complete pose");
            session.LeaveAnimation();
            var sceneActor=session.Project!.Objects.First(o=>o.Id==actor.Id);
            var secondObject=new CreationSceneObject { Transform=new() { Position=new(2,0,0) } };
            var childObject=new CreationSceneObject { ParentId=sceneActor.Id,ActorHand=1,Name="RightHand" };
            session.Project.Objects.Add(secondObject);session.Project.Objects.Add(childObject);session.Changed();
            Call("Select",sceneActor);Frame();
            if(points.Count!=0 || AccessTools.Field(workspace.GetType(),"hoveredPosePart").GetValue(workspace) is Guid hit && hit!=Guid.Empty) throw new Exception("Scene editing still picks skeleton parts");
            io.KeyCtrl=true;Call("SelectObjectClick",secondObject,false);io.KeyCtrl=false;
            var selection=(HashSet<Guid>)AccessTools.Field(workspace.GetType(),"selectedObjects").GetValue(workspace)!;
            if(!selection.SetEquals(new[]{sceneActor.Id,secondObject.Id})) throw new Exception("Ctrl-click failed to add object");
            var beforeA=session.Frame!.Objects[sceneActor.Id].World.Translation;var beforeB=session.Frame.Objects[secondObject.Id].World.Translation;
            session.BeginGesture();Call("TransformObjectSelection",session.Frame.Objects[secondObject.Id],session.Frame.Objects[secondObject.Id].World*Matrix4x4.CreateTranslation(1,2,3));session.Changed();session.EndGesture("move selection");
            if(Vector3.Distance(session.Frame!.Objects[sceneActor.Id].World.Translation,beforeA+new Vector3(1,2,3))>.001 || Vector3.Distance(session.Frame.Objects[secondObject.Id].World.Translation,beforeB+new Vector3(1,2,3))>.001) throw new Exception("Multi-object transform moved only the active object");
            session.Undo();Call("SyncSelection");
            if(Vector3.Distance(session.Frame!.Objects[sceneActor.Id].World.Translation,beforeA)>.001) throw new Exception("Multi-object undo is not atomic");
            Call("DeleteSelection");
            if(session.Project.Objects.Any(o=>o.Id==sceneActor.Id||o.Id==secondObject.Id||o.Id==childObject.Id)) throw new Exception("UI batch delete left actor or hand attachment");
            session.Undo();session.Project.Validate();
            if(session.Project.Objects.All(o=>o.Id==childObject.Id)) throw new Exception("Batch delete undo lost hand attachment");
            Console.WriteLine("PASS scene mode excludes part picking; Ctrl multiselect, atomic batch transform/delete and attachment restoration");
            Call("Select",session.Project.Objects.First(o=>o.Id==actor.Id));
            Matrix4x4 gizmoWorld=Matrix4x4.Identity;
            float[] gizmoView=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,-5,1];
            float[] gizmoProjection=[.625f,0,0,0,0,1,0,0,0,0,-1,-1,0,0,-.2f,0];
            void GizmoFrame()
            {
                ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("##creation-handle-probe",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoInputs);
                object[] args=[ImGui.GetWindowDrawList(),gizmoWorld,gizmoView,gizmoProjection,Vector2.Zero,io.DisplaySize,false,Matrix4x4.Identity];
                if((bool)AccessTools.Method(workspace.GetType(),"ManipulateCreation").Invoke(workspace,args)!) gizmoWorld=(Matrix4x4)args[7];
                ImGui.End();ImGui.Render();
            }
            AccessTools.Field(workspace.GetType(),"operation").SetValue(workspace,ImGuizmoNET.OPERATION.TRANSLATE);
            AccessTools.Field(workspace.GetType(),"transformMode").SetValue(workspace,ImGuizmoNET.MODE.WORLD);
            io.MousePos=new(690,400);GizmoFrame();GizmoFrame();io.MouseDown[0]=true;GizmoFrame();
            io.MousePos=new(730,400);GizmoFrame();io.MouseDown[0]=false;GizmoFrame();
            if(Vector3.Distance(gizmoWorld.Translation,new(.5f,0,0))>.002f) throw new Exception("Shared creation translation handle did not move along X");
            gizmoWorld=Matrix4x4.Identity;AccessTools.Field(workspace.GetType(),"operation").SetValue(workspace,ImGuizmoNET.OPERATION.ROTATE);
            GizmoFrame();
            var sharedHandles=AccessTools.Field(workspace.GetType(),"creationHandles").GetValue(workspace)!;
            var ring=((IEnumerable<(Vector2 A,Vector2 B,int Axis,Guid CameraId)>)AccessTools.Property(sharedHandles.GetType(),"Lines").GetValue(sharedHandles)!).Where(l=>l.Axis==6).ToArray();
            io.MousePos=ring[0].A;GizmoFrame();GizmoFrame();io.MouseDown[0]=true;GizmoFrame();
            io.MousePos=ring[32].A;GizmoFrame();io.MouseDown[0]=false;GizmoFrame();
            if(Math.Abs(Vector3.Dot(Vector3.TransformNormal(Vector3.UnitX,gizmoWorld),Vector3.UnitY))<.99f) throw new Exception("Creation screen rotation ring did not rotate in the viewport plane: "+gizmoWorld+" hover="+AccessTools.Field(workspace.GetType(),"handleHover").GetValue(workspace)+" angle="+AccessTools.Field(workspace.GetType(),"handleAngle").GetValue(workspace));
            Console.WriteLine("PASS real shared creation cone drag and outer screen-rotation ring pointer interaction");
            void DeleteKeyFrame(bool typing)
            {
                ImGui.NewFrame();ImGui.Begin("##creation-delete-key-probe");
                bool old=io.WantTextInput;io.WantTextInput=typing;Call("HandleSelectionKeys");io.WantTextInput=old;
                ImGui.End();ImGui.Render();
            }
            session.Seek(0);Call("StartScenePose",session.Project.Objects.First(o=>o.Id==actor.Id));session.SavePose();
            session.Seek(2);Call("SelectBone",actor.Id,lower.Id);
            session.BeginGesture();session.SetTransform(actor.Id,lower.Id,new() { Rotation=new(0,0,65) });session.Changed();session.EndGesture("scene pose");session.SavePose();
            if(session.SelectedAnimation!=Guid.Empty || session.Time!=2 || !session.ScenePoseEditing) throw new Exception("Scene posing entered a detached action clock");
            gizmoWorld=Matrix4x4.Identity;AccessTools.Field(workspace.GetType(),"poseRotationAxis").SetValue(workspace,0);
            GizmoFrame();
            var onlyAxis=((IEnumerable<(Vector2 A,Vector2 B,int Axis,Guid CameraId)>)AccessTools.Property(sharedHandles.GetType(),"Lines").GetValue(sharedHandles)!).ToArray();
            if(onlyAxis.Length==0 || onlyAxis.Any(l=>l.Axis!=0)) throw new Exception("Single-axis posing still draws intersecting rotation rings");
            io.MousePos=onlyAxis.Select(l=>l.A).MaxBy(p=>Vector2.DistanceSquared(p,new(640,400)));GizmoFrame();GizmoFrame();
            io.MouseDown[0]=true;GizmoFrame();io.MousePos+=new Vector2(20,0);GizmoFrame();io.MouseDown[0]=false;GizmoFrame();
            if(Math.Abs(gizmoWorld.M22-MathF.Cos(20*MathF.PI/(180*(float)GuiElement.scaled(1))))>.003f) throw new Exception("Edge-on pose axis did not follow stable pointer drag: "+gizmoWorld);
            Console.WriteLine("PASS simplified pose axis hides other rings and supports stable edge-on dragging");
            Vector2 sceneRow=default;
            void ScenePoseRow()
            {
                ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(700,200));
                ImGui.Begin("##scene-pose-summary-probe",ImGuiWindowFlags.NoDecoration);sceneRow=ImGui.GetCursorScreenPos();
                Call("DrawScenePoseRow",session.Project.Objects.First(o=>o.Id==actor.Id),200f,400f,192f,40f,(System.Func<double,float>)(t=>200+(float)t*100),(System.Func<float,double>)(x=>Math.Clamp((x-200)/100,0,4)));
                ImGui.End();ImGui.Render();
            }
            ScenePoseRow();io.MousePos=new(400,sceneRow.Y+20);ScenePoseRow();ScenePoseRow();
            io.MouseDown[0]=true;ScenePoseRow();io.MousePos=new(500,sceneRow.Y+20);ScenePoseRow();io.MouseDown[0]=false;ScenePoseRow();
            if(session.Project.Objects.First(o=>o.Id==actor.Id).PoseKeys.Last().Time!=3 || session.Time!=3) throw new Exception("Scene pose key drag failed to retain the scene clock");
            session.Undo();ScenePoseRow();
            if(session.Project.Objects.First(o=>o.Id==actor.Id).PoseKeys.Last().Time!=2) throw new Exception("Scene pose drag undo failed");
            io.MousePos=new(400,sceneRow.Y+20);ScenePoseRow();io.MouseDown[1]=true;ScenePoseRow();io.MouseDown[1]=false;ScenePoseRow();
            var scenePoseId=session.Project.Objects.First(o=>o.Id==actor.Id).PoseKeys.Last().Id;
            if((Guid)AccessTools.Field(workspace.GetType(),"contextScenePose").GetValue(workspace)! != scenePoseId) throw new Exception("Scene pose right click did not target its whole key");
            io.MousePos=new(900,700);io.MouseDown[0]=true;ScenePoseRow();io.MouseDown[0]=false;ScenePoseRow();
            AccessTools.Field(workspace.GetType(),"keyId").SetValue(workspace,scenePoseId);
            io.AddKeyEvent(ImGuiKey.Delete,true);DeleteKeyFrame(false);io.AddKeyEvent(ImGuiKey.Delete,false);DeleteKeyFrame(false);
            if(session.Project.Objects.All(o=>o.Id!=actor.Id) || session.Project.Objects.First(o=>o.Id==actor.Id).PoseKeys.Any(k=>k.Id==scenePoseId)) throw new Exception("Deleting a scene pose key deleted its actor or kept the key");
            session.Undo();session.EndScenePose();Call("Select",session.Project.Objects.First(o=>o.Id==actor.Id));
            Console.WriteLine("PASS scene pose uses the scene clock; real whole-key drag, undo, context picking and Delete preserve the actor");
            int objectCount=session.Project.Objects.Count;io.AddKeyEvent(ImGuiKey.Delete,true);DeleteKeyFrame(true);
            if(session.Project.Objects.Count!=objectCount) throw new Exception("Delete in a text field removed objects");
            io.AddKeyEvent(ImGuiKey.Delete,false);DeleteKeyFrame(false);io.AddKeyEvent(ImGuiKey.Delete,true);DeleteKeyFrame(false);
            io.AddKeyEvent(ImGuiKey.Delete,false);DeleteKeyFrame(false);
            if(session.Project.Objects.Any(o=>o.Id==actor.Id||o.Id==childObject.Id)) throw new Exception("Delete shortcut did not remove selected actor and its children");
            Console.WriteLine("PASS Delete shortcut deletes the selected hierarchy and leaves text editing alone");
            Console.WriteLine("PASS pose action creation, same-frame multibone edits without prompts, atomic save-and-continue, direct rendered part picking and bilingual pose panels");
        }
        finally { io.MouseDown[0] = false; io.DisplaySize = display; AccessTools.Method(viewport, "Deactivate").Invoke(null, [host]); ((IDisposable)host).Dispose(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    private static void CheckRigEditingWorkflow(Assembly assembly, ICoreClientAPI api)
    {
        string root = Path.Combine(Path.GetTempPath(), "director-rig-ui-" + Guid.NewGuid().ToString("N"));
        var upper = new CreationPart { Name = "UpperArm", To = new(1,16,1) };
        var lower = new CreationPart { Name = "LowerArm", ParentId = upper.Id, From = new(0,16,0), Origin = new(0,16,0), To = new(1,32,1) };
        var model = new CreationModel { Parts = [upper, lower] }; var actor = new CreationSceneObject { ModelId = model.Id };
        var p = new DirectorCreationProject { Models = [model], Objects = [actor] };
        var rest = DirectorCreationEvaluator.Evaluate(p, 0).Objects[actor.Id];
        var controls = DirectorCreationIk.CreateControls(actor, model, rest, upper, lower);
        p.Objects.Add(controls.Target); p.Objects.Add(controls.Pole); actor.IkChains.Add(controls.Chain);
        var animation = new CreationAnimation { ModelId = model.Id, Duration = 1, Loop = true };
        var targetTrack = new CreationTrack { ObjectId = controls.Target.Id, Channel = CreationChannel.Position };
        DirectorCreationEdits.SetKey(targetTrack, 0, new(.5,1.3,0)); DirectorCreationEdits.SetKey(targetTrack, 1, new(1,1,.3));
        animation.Tracks.Add(targetTrack); p.Animations.Add(animation);
        var session = new DirectorCreationSession(new(root)); session.Open(p); session.EnterAnimation(animation.Id, actor.Id); session.AutoKey = true;
        Type R(string name) => assembly.GetType(name, true)!;
        var runtime = RuntimeHelpers.GetUninitializedObject(R("VSDirector.Runtime.DirectorCreationRuntime"));
        AccessTools.Field(runtime.GetType(), "<Session>k__BackingField").SetValue(runtime, session);
        var owner = new VSDirectorModSystem(); AccessTools.Field(owner.GetType(), "creation").SetValue(owner, runtime);
        var host = Activator.CreateInstance(R("VSDirector.Gui.DirectorImGuiWorkspace"), api)!;
        var workspace = Activator.CreateInstance(R("VSDirector.Gui.DirectorCreationWorkspace"), BindingFlags.Instance | BindingFlags.NonPublic, null, [api, owner, host], null)!;
        void Call(string method, params object[] args) => AccessTools.Method(workspace.GetType(), method).Invoke(workspace, args);
        try
        {
            Call("Select", controls.Target);
            if (session.EditingAnimation != animation || session.SelectedObject != controls.Target.Id) throw new Exception("Selecting an IK control left animation editing");
            Call("Select", actor);
            var samples = Enumerable.Range(0,31).Select(i => DirectorCreationEvaluator.Evaluate(p, 0, editingAction: animation, actionActor: actor.Id, actionTime: i / 30d).Objects[actor.Id].Bones[lower.Id]).ToArray();
            Call("BakeIk", actor);
            var baked = session.EditingAnimation ?? throw new Exception("IK bake did not open editable bone keys");
            if (baked.Id == animation.Id || !baked.Loop || baked.Tracks.Any(t => t.ObjectId != Guid.Empty)) throw new Exception("IK bake lost the original action or retained target dependencies");
            for (int i = 0; i <= 30; i++)
            {
                var actual = DirectorCreationEvaluator.Evaluate(p, 0, editingAction: baked, actionActor: actor.Id, actionTime: i / 30d).Objects[actor.Id];
                if (actual.IkWeight != 0) throw new Exception("Baked animation solves IK twice");
                foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    if (Vector3.Distance(Vector3.Transform(point, actual.Bones[lower.Id]), Vector3.Transform(point, samples[i])) > .005) throw new Exception("IK bake changed bone pose at frame " + i);
            }
            var exported = DirectorCreationShape.ExportAnimation(baked, model);
            if (exported["keyframes"] is not System.Text.Json.Nodes.JsonArray { Count: 30 }) throw new Exception("Baked native export omitted bone frames");
            session.Undo(); if (session.Project!.Animations.Count != 1) throw new Exception("IK bake undo failed");
            var restoredActor = session.Project.Objects.First(o => o.Id == actor.Id);
            var restoredAction = session.Project.Animations.Single();
            session.EnterAnimation(restoredAction.Id, restoredActor.Id);
            string sourceBefore = System.Text.Json.JsonSerializer.Serialize(restoredAction, CreationJson.Options);
            Call("CreatePoseCopy", restoredActor);
            var poseCopy = session.EditingAnimation!;
            if (!poseCopy.PoseEditing || poseCopy.Id == restoredAction.Id || poseCopy.Tracks.Any(t => t.ObjectId != Guid.Empty)) throw new Exception("Pose copy retained control dependencies");
            if (sourceBefore != System.Text.Json.JsonSerializer.Serialize(restoredAction, CreationJson.Options)) throw new Exception("Pose copy changed the source animation");
            for (int i = 0; i <= 30; i++)
            {
                var actual = DirectorCreationEvaluator.Evaluate(session.Project, 0, editingAction: poseCopy, actionActor: actor.Id, actionTime: i / 30d).Objects[actor.Id];
                if (actual.IkWeight != 0) throw new Exception("Pose copy solves IK twice");
                foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    if (Vector3.Distance(Vector3.Transform(point, actual.Bones[lower.Id]), Vector3.Transform(point, samples[i])) > .005) throw new Exception("Pose copy changed sampled geometry");
            }
            DirectorCreationShape.ExportAnimation(poseCopy, session.Project.Models.Single());
            session.LeaveAnimation(); session.SelectedObject = actor.Id; session.Seek(session.Project.Duration);
            double insertAt = session.Time;
            Call("AddAnimationClip", poseCopy);
            if (session.Project.Clips.Last().Duration != poseCopy.Duration || session.Project.Duration != insertAt + poseCopy.Duration) throw new Exception("Adding a pose action truncated its duration");
            Console.WriteLine("PASS legacy IK pose copy preserves source and sampled geometry, exports without controls, and inserts its full duration at scene end");
            Console.WriteLine("PASS rig UI selection retains action editing; IK bake opens editable bone tracks, preserves all sampled poses, disables double solving and exports native frames");
        }
        finally { ((IDisposable)host).Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void CheckCreationRigMatrices()
    {
        static Matrix4x4 Matrix(float[] v) => new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
        static void Same(Matrix4x4 a, Matrix4x4 b, string name)
        {
            foreach (var v in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                if (Vector3.Distance(Vector3.Transform(v, a), Vector3.Transform(v, b)) > .004) throw new Exception(name);
        }
        foreach (int version in new[] { 0, 1 })
        foreach (double sign in new[] { -1d, 1 })
        foreach (double angle in new[] { -180d, -90, -35, 0, 89.99, 90, 170 })
        {
            var part = new CreationPart { From = new(2, 9, -3), To = new(4, 15, 2), Origin = new(3, 13, 1), Rotation = new(20, 60, -12), Scale = new(1, 1, 1) };
            var pose = new CreationTransform { Position = new(.1, -.2, .3), Rotation = new(angle, 43, -21), Scale = new(sign, 1.4, .8) };
            var native = new ShapeElement { From = [2,9,-3], To = [4,15,2], RotationOrigin = [3,13,1], RotationX = 20, RotationY = 60, RotationZ = -12, ScaleX = 1, ScaleY = 1, ScaleZ = 1 };
            var nativePose = new ElementPose { translateX = .1f, translateY = -.2f, translateZ = .3f, degX = (float)angle, degY = 43, degZ = -21, scaleX = (float)sign, scaleY = 1.4f, scaleZ = .8f };
            var local = DirectorCreationMath.BoneMatrix(part, pose, version);
            Same(local, Matrix(native.GetLocalTransformMatrix(version, tf: nativePose)), $"Bone matrix differs from native v{version} at {angle}/{sign}");
            var decoded = DirectorCreationMath.PoseFromMatrix(part, local, pose, version);
            Same(local, DirectorCreationMath.BoneMatrix(part, decoded, version), "Reflected bone changed orientation");
            if (Math.Sign(decoded.Scale.X) != Math.Sign(pose.Scale.X) || Math.Abs(decoded.Rotation.X - pose.Rotation.X) > .01) throw new Exception("Bone scale reflection caused an Euler flip");
            var actorWorld = new CreationTransform { Position = new(12,3,-9), Rotation = new(15,100,20), Scale = new(2,2,2) }.Matrix();
            var handle = DirectorCreationMath.BoneHandle(part, pose, version, local * actorWorld);
            var centre = handle.Translation;
            var delta = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateRotationY(.15f) * Matrix4x4.CreateTranslation(centre);
            var manipulated = DirectorCreationMath.BoneFromHandle(part, pose, version, handle * delta);
            Same(manipulated, local * actorWorld * delta, "Bone handle moved geometry around the wrong point");
        }
        Console.WriteLine("PASS bone matrices match native ShapeElement v0/v1, signed scale round trips and pivot-centred manipulation across inverted rotations");
    }

    private static void CheckCreationTransformRows(object workspace)
    {
        var method = AccessTools.Method(workspace.GetType(), "DrawTransformRow");
        var io = ImGui.GetIO();
        var value = new CreationVector(-30000000.125, 2000, .001);
        Vector2[] corners = new Vector2[3];
        float rowY = 0, frameHeight = 0;
        uint[] colors = [0xff5c5ce8, 0xff77cb65, 0xffe8a35c];
        void Frame(float width, string label)
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(width, 180));
            ImGui.Begin("##creation-transform-row-probe", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
            float before = ImGui.GetCursorScreenPos().Y, right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            frameHeight = ImGui.GetFrameHeight();
            var draw = ImGui.GetWindowDrawList(); int first = draw.VtxBuffer.Size;
            method.Invoke(workspace, [label, value, (Action<CreationVector>)(v => value = v), 1f, null]);
            if (ImGui.GetCursorScreenPos().Y - before > frameHeight + 16)
                throw new Exception($"Creation transform label and XYZ do not fit in a single row at {width}: height={ImGui.GetCursorScreenPos().Y - before}, frame={frameHeight}");
            for (int axis = 0; axis < 3; axis++)
            {
                var points = Enumerable.Range(first, draw.VtxBuffer.Size - first)
                    .Where(i => draw.VtxBuffer[i].col == ImGui.GetColorU32(colors[axis]))
                    .Select(i => draw.VtxBuffer[i].pos).ToArray();
                if (points.Length == 0) throw new Exception("Creation transform axis missing: " + axis);
                corners[axis] = new(points.Max(p => p.X), points.Min(p => p.Y));
            }
            rowY = corners[0].Y;
            if (Math.Abs(corners[2].X - 2 * corners[1].X + corners[0].X) > 2
                || corners.Any(p => p.X > right + 1 || Math.Abs(p.Y - rowY) > 1))
                throw new Exception("Creation transform XYZ widths or alignment differ at " + width);
            ImGui.End(); ImGui.Render();
        }
        try
        {
            io.MousePos = new(-100);
            foreach (string label in new[] { "位置", "World position" })
            foreach (float width in new[] { 220f, 320f, 520f })
                for (int i = 0; i < 4; i++) Frame(width, label);
            value = new(1, 2, 3);
            for (int axis = 0; axis < 3; axis++)
            {
                io.MousePos = new(corners[axis].X - 7, rowY + frameHeight / 2);
                Frame(520, "World position");
                io.MouseDown[0] = true; Frame(520, "World position");
                io.MouseDown[0] = false; Frame(520, "World position");
            }
            if (value != new CreationVector(2, 3, 4))
                throw new Exception("Creation transform arrow edits target the wrong XYZ component: " + value);
            Console.WriteLine("PASS creation transform label and equal XYZ fields share one bounded row at 220/320/520px; each axis arrow edits its own component");
        }
        finally { io.MouseDown[0] = false; io.MousePos = new(-100); }
    }
}
