using System.Numerics;
using HarmonyLib;
using ImGuiNET;
using VSDirector.Data;
using Vintagestory.API.Config;

internal static partial class DirectorImGuiProbe
{
    private static void CheckDirectCreationTools(object workspace,DirectorCreationSession session,Action viewportFrame)
    {
        object? Call(string name,params object[] args)=>AccessTools.Method(workspace.GetType(),name).Invoke(workspace,args);
        object Get(string name)=>AccessTools.Field(workspace.GetType(),name).GetValue(workspace)!;
        void Set(string name,object value)=>AccessTools.Field(workspace.GetType(),name).SetValue(workspace,value);
        void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        var type=workspace.GetType().GetNestedType("CreationTool",System.Reflection.BindingFlags.NonPublic)!;
        object Tool(string name)=>Enum.Parse(type,name);
        string Active() => AccessTools.Property(workspace.GetType(),"ActiveTool").GetValue(workspace)!.ToString()!;
        var lower=new CreationPart{Name="Lower",Origin=new(0,8,0),From=new(-3,8,-3),To=new(3,16,3)};
        var upper=new CreationPart{Name="Upper",Origin=new(0,0,0),From=new(-3,0,-3),To=new(3,8,3)};lower.ParentId=upper.Id;
        var model=new CreationModel{Parts=[upper,lower],BodyMapping=new(){{"body",upper.Id},{"head",lower.Id}}};
        var actor=new CreationSceneObject{ModelId=model.Id};
        session.Open(new(){Models=[model],Objects=[actor]});session.AutoKey=true;Call("Select",actor);
        Set("bonesPanel",false);Set("ikPanel",false);Set("actionsPanel",false);Set("showBones",false);
        Call("ChooseTool",Tool("Select"));
        var io=ImGui.GetIO();
        var rect=((Vector2 Position,Vector2 Size))Call("GameImageRect")!;
        io.MousePos=rect.Position+new Vector2(.5f*rect.Size.X,.5f*(1-.75f/5)*rect.Size.Y);
        for(int i=0;i<25;i++)viewportFrame();
        io.MouseDown[0]=true;viewportFrame();io.MouseDown[0]=false;viewportFrame();
        Require(session.SelectedObject==actor.Id&&session.SelectedPart==Guid.Empty&&!session.ScenePoseEditing,"Single surface click must select the whole actor");
        io.MouseDown[0]=true;viewportFrame();io.MouseDown[0]=false;viewportFrame();
        Require(session.ScenePoseEditing&&session.SelectedPart==lower.Id&&Active()=="Rotate","Double surface click did not enter contextual part rotation");
        Require(!(bool)Get("bonesPanel")&&!(bool)Get("ikPanel")&&!(bool)Get("actionsPanel"),"Direct posing opened optional windows");
        Call("ChooseTool",Tool("Pull"));Call("SelectBone",actor.Id,upper.Id);
        Require(Active()=="Pull","Changing parts reset the chosen tool");
        Call("ChooseTool",Tool("Scale"));Require(Active()=="Pull","Connected posing enabled stretch without opting in");
        Call("RequestEscape");
        Require(session.SelectedPart==Guid.Empty&&!session.ScenePoseEditing&&session.AnimationEditing&&Active()=="Select","Escape did not restore object tool while retaining animation task");
        Call("EnterBodySelection",actor,lower.Id);session.AutoKey=false;session.Seek(1);
        session.BeginGesture();session.SetTransform(actor.Id,lower.Id,new(){Rotation=new(0,20,0)});session.Changed();session.EndGesture("draft");
        Call("RequestEscape");Require(Get("pendingTransition") is not null&&session.HasPoseDraft,"Escape discarded an unrecorded pose");
        Call("ResolvePendingTransition",false);Require(session.SelectedPart==Guid.Empty&&!session.HasPoseDraft,"Discard-and-return did not restore object scope");
        Call("EnterBodySelection",actor,lower.Id);Call("ChooseTool",Tool("Rotate"));
        float oldScale=RuntimeEnv.GUIScale;string oldLocale=Lang.CurrentLocale;Vector2 oldDisplay=io.DisplaySize;
        try
        {
            io.DisplaySize=new(1280,1600);
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float scale in new[]{1f,1.5f,2f})
            foreach(float width in new[]{220f,320f,520f})
            {
                LoadCreationProbeLanguage(language);RuntimeEnv.GUIScale=scale;
                for(int i=0;i<3;i++)
                {
                    ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,1100));
                    ImGui.Begin("##direct-tools-probe",ImGuiWindowFlags.NoSavedSettings);ImGui.SetWindowFontScale(scale);
                    float toolsTop=ImGui.GetCursorScreenPos().Y;
                    Call("DrawTools");
                    if(i==2 && width==220)Require(ImGui.GetCursorScreenPos().Y-toolsTop>ImGui.GetFrameHeightWithSpacing(),"Narrow tools did not wrap");
                    Call("DrawTimelineContext");
                    if(i==2)Require(ImGui.GetScrollMaxX()<1,"Visible tool or filter controls overflow at "+language+"/"+width+"/"+scale+" by "+ImGui.GetScrollMaxX());
                    ImGui.End();ImGui.Render();
                }
            }
            RuntimeEnv.GUIScale=1;LoadCreationProbeLanguage("en");
            Vector2 origin=default;float side=0;
            void ToolsFrame()
            {
                ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(520,500));ImGui.Begin("##tools-click",ImGuiWindowFlags.NoSavedSettings);ImGui.SetWindowFontScale(1);
                side=ImGui.GetFrameHeight();origin=ImGui.GetCursorScreenPos();Call("DrawTools");
                origin.X+=ImGui.CalcTextSize("Object").X+ImGui.CalcTextSize("Part").X+4*ImGui.GetStyle().FramePadding.X+2*ImGui.GetStyle().ItemSpacing.X;
                Call("HandleSelectionKeys");ImGui.End();ImGui.Render();
            }
            ToolsFrame();ToolsFrame();
            io.MousePos=origin+new Vector2(side*.5f,side*.5f);ToolsFrame();ToolsFrame();
            io.MouseDown[0]=true;ToolsFrame();io.MouseDown[0]=false;ToolsFrame();
            Require(Active()=="Select","Tools Select icon click did not route the common command");
            io.AddKeyEvent(ImGuiKey.R,true);ToolsFrame();io.AddKeyEvent(ImGuiKey.R,false);ToolsFrame();
            Require(Active()=="Rotate","R shortcut differs from the rotation tool command");
            Call("SelectBone",actor.Id,upper.Id);Require(Active()=="Rotate","Selection lost keyboard tool choice");
        }
        finally{RuntimeEnv.GUIScale=oldScale;LoadCreationProbeLanguage(oldLocale);io.DisplaySize=oldDisplay;io.MouseDown[0]=false;}
        Console.WriteLine("PASS direct single/double-click posing, retained tools, Escape/manual draft protection, actual toolbar/keyboard commands and bilingual controls at 220/320/520px, 100/150/200%");
    }
}
