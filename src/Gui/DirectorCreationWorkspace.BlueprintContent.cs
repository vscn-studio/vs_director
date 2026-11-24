using System.Numerics;
using ImGuiNET;
using VSDirector.Blueprints;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private static string ProjectionContentSection(string field)=>field switch
    {
        _ when field.StartsWith("glass",StringComparison.Ordinal)=>"b."+field,
        _ when field.StartsWith("edge",StringComparison.Ordinal)=>"b."+field,
        "blockEdge" or "blockEdgeRatio"=>"b.edges",
        _ when field.StartsWith("wave",StringComparison.Ordinal)=>"b.waves",
        "fall" or "height" or "rain" or "fill"=>"b.fall",
        "rainbow" or "colorStrength" or "colorR" or "colorG" or "colorB"=>"b.particle-color",
        "outline" or "outlineR" or "outlineG" or "outlineB" or "particleGlow"=>"b.particle-outline",
        _=>"b."+(ProjectionFields.ById.GetValueOrDefault(field)?.Group??"growth")
    };
    private void DrawBlueprintTransforms(CreationSceneObject obj)
    {
        if(Button(B("rotate-left")))Session.Edit(B("rotate-left"),_=>obj.Transform=obj.Transform with{Rotation=obj.Transform.Rotation+new CreationVector(0,-90,0)});
        SameLineIfFits(B("rotate-right"));if(Button(B("rotate-right")))Session.Edit(B("rotate-right"),_=>obj.Transform=obj.Transform with{Rotation=obj.Transform.Rotation+new CreationVector(0,90,0)});
        if(Button(B("mirror-x")))Session.Edit(B("mirror-x"),_=>obj.Transform=obj.Transform with{Scale=new(-obj.Transform.Scale.X,obj.Transform.Scale.Y,obj.Transform.Scale.Z)});
        SameLineIfFits(B("mirror-z"));if(Button(B("mirror-z")))Session.Edit(B("mirror-z"),_=>obj.Transform=obj.Transform with{Scale=new(obj.Transform.Scale.X,obj.Transform.Scale.Y,-obj.Transform.Scale.Z)});
    }
    private void DrawBlueprintContent(CreationSceneObject obj)
    {
        var settings=obj.Projection!;string section=contentView.Section;
        if(section is "b.preview" or "b.export"){DrawProjectionViewContent(obj,section=="b.export");return;}
        if(section=="b.files"){DrawProjectionFiles(obj);return;}
        if(settings.Version==1)
        {Hint(Q("upgrade-hint"));if(Button(Q("upgrade")))UpgradeProjection(obj);return;}
        switch(section)
        {
            case "b.cells":DrawProjectionCellProperties(obj);return;
            case "b.range":DrawProjectionRange(obj);return;
            case "b.keys":DrawProjectionKeys(obj);return;
        }
        if(section.StartsWith("b.glass",StringComparison.Ordinal)&&int.TryParse(section[7..],out int glass)&&glass is >=0 and <5)
            ProjectionChoiceControl(settings.Glass[glass],N("resource-code"),"glass"+glass);
        if(section.StartsWith("b.edge",StringComparison.Ordinal)&&int.TryParse(section[6..],out int edge)&&edge is >=0 and <4)
            ProjectionChoiceControl(settings.Edges[edge],N("resource-code"),"edge"+edge);
        if(section=="b.particles")
        {
            Hint(Q("particle-hint"));
            if(Button(B("add-particles"))){projectionSchemeTarget=obj.Id;OpenParticlePicker();}
            if(ProjectionCombo(Q("scheme"),settings.ParticleScheme.At(Session.Time)))
            {foreach(string name in settings.ParticleSchemes.Keys)if(ImGui.Selectable(name))ProjectionEdit(Q("scheme"),s=>s.ParticleScheme.Value=name);ImGui.EndCombo();}
            if(Button(Q("key-scheme")))ProjectionEdit(Q("key-scheme"),s=>ChoiceKey(s.ParticleScheme));
            string selected=settings.ParticleScheme.At(Session.Time);
            if(settings.ParticleSchemes.ContainsKey(selected)&&Button(N("edit-scheme")))
            {contentView.Scheme=selected;particleEmitter=Guid.Empty;particleMulti.Clear();particleScope=0;NavigateContent("p.overview");}
        }
        if(section=="b.physics")Hint(Q("physics-hint"));
        if(section=="b.particle-color")DrawProjectionColor(obj,"color",Q("colorStrength"));
        if(section=="b.particle-outline")DrawProjectionColor(obj,"outline",N("outline-color"));
        foreach(var field in ProjectionFields.All.Where(f=>ProjectionContentSection(f.Id)==section && f.Id is not ("colorR" or "colorG" or "colorB" or "outlineR" or "outlineG" or "outlineB")))
            DrawProjectionField(obj,field);
        if(error.Length>0)ImGui.TextWrapped(error);
    }
    private void DrawProjectionColor(CreationSceneObject obj,string prefix,string label)
    {
        var s=obj.Projection!;
        double Value(string field)=>s.Parameters.GetValueOrDefault(field)?.Value??ProjectionFields.ById[field].Default;
        Vector3 color=new((float)Value(prefix+"R"),(float)Value(prefix+"G"),(float)Value(prefix+"B"));
        ImGui.TextWrapped(label);ImGui.SetNextItemWidth(-1);
        bool changed=ImGui.ColorEdit3("##"+prefix,ref color);
        Widget(label,changed,()=>{s.Parameter(prefix+"R").Value=color.X;s.Parameter(prefix+"G").Value=color.Y;s.Parameter(prefix+"B").Value=color.Z;});
        foreach(string axis in new[]{"R","G","B"})
        {
            FocusContentField(prefix+axis);
            SameLineIfFits(axis+" "+Q("add-key"));
            if(Button(axis+" "+Q("add-key")))KeyProjectionField(obj,prefix+axis);
        }
    }
    private void DrawProjectionField(CreationSceneObject obj,ProjectionField field)
    {
        var settings=obj.Projection!;
        ImGui.PushID(field.Id);FocusContentField(field.Id);
        double current=settings.Parameters.GetValueOrDefault(field.Id)?.Value??field.Default;
        if(field.Step&&field.Max==1)
        {
            Toggle(Q(field.Id),current>.5,v=>settings.Parameter(field.Id).Value=v?1:0);
            SameLineIfFits(Q("add-key"));if(Button(Q("add-key")))KeyProjectionField(obj,field.Id);
        }
        else if(field.Id.EndsWith("Direction",StringComparison.Ordinal))
        {
            if(ProjectionCombo(Q(field.Id),new[]{"+X","-X","+Y","-Y","+Z","-Z"}[Math.Clamp((int)current,0,5)]))
            {for(int i=0;i<6;i++){int direction=i;if(ImGui.Selectable(new[]{"+X","-X","+Y","-Y","+Z","-Z"}[i]))ProjectionEdit(Q(field.Id),s=>s.Parameter(field.Id).Value=direction);}ImGui.EndCombo();}
            if(Button(Q("add-key")))KeyProjectionField(obj,field.Id);
        }
        else Number(Q(field.Id),current,v=>settings.Parameter(field.Id).Value=v,field.Min,field.Max,field.Step?1:.01f,()=>KeyProjectionField(obj,field.Id));
        ImGui.PopID();
    }
    private void DrawProjectionViewContent(CreationSceneObject obj,bool output)
    {
        if(projectionScene is null||projectionObject!=obj.Id)
        {
            Hint(N("open-preview-help"));
            if(Button(B("preview")))Run(()=>{OpenProjectionPreview(obj);NavigateContent(output?"b.export":"b.preview");});
            return;
        }
        var scene=projectionScene;var v=scene.View;
        float footer=ImGui.GetFrameHeightWithSpacing()*2;
        ImGui.BeginChild("projection-view-fields",new(0,Math.Max(1,ImGui.GetContentRegionAvail().Y-footer)),false);
        ImGui.BeginDisabled(projectionExport is not null);
        if(!output)
        {
            ViewBool("perspective",v.Perspective,x=>v.Perspective=x);ViewBool("rotate",v.Rotate,x=>v.Rotate=x);ViewBool("clockwise",v.Clockwise,x=>v.Clockwise=x);
            ViewNumber("yaw",v.Yaw,x=>v.Yaw=x,-360,360);ViewNumber("pitch",v.Pitch,x=>v.Pitch=x,-89,89);
            ViewNumber("zoom",v.Zoom,x=>v.Zoom=x,.05,20,.05f);ViewNumber("light",v.Light,x=>v.Light=x,0,4,.05f);
            ViewBool("transparent",v.Transparent,x=>v.Transparent=x);
            var color=v.Background.Float();ImGui.TextWrapped(B("background"));ImGui.SetNextItemWidth(-1);
            if(ImGui.ColorEdit3("##background",ref color)){v.Background=CreationVector.From(color);SaveProjectionView();}
            ViewBool("particles",v.Particles,x=>v.Particles=x);ViewBool("animate",v.Animate,x=>v.Animate=x);
            ViewNumber("start",v.Start,x=>v.Start=x,0,Math.Max(0,Project!.Duration-v.Duration),.05f);
        }
        else
        {
            ViewNumber("width",v.Width,x=>v.Width=(int)x,16,4096);ViewNumber("height",v.Height,x=>v.Height=(int)x,16,4096);
            ViewNumber("fps",v.Fps,x=>{v.Fps=(int)x;v.Duration=Math.Min(v.Duration,3600d/v.Fps);},1,60);
            ViewNumber("duration",v.Duration,x=>v.Duration=x,.1,Math.Min(120,3600d/v.Fps),.1f);
            if(ImGui.RadioButton("APNG",v.Apng)){v.Apng=true;SaveProjectionView();}
            SameLineIfFits("GIF");if(ImGui.RadioButton("GIF",!v.Apng)){v.Apng=false;SaveProjectionView();}
            if(!v.Apng)Hint(B("gif-alpha"));Hint(B("loop-hint"));ImGui.TextUnformatted($"{v.Frames} {B("frames")}");
        }
        if(error.Length>0)ImGui.TextWrapped(error);
        foreach(string issue in scene.Build.Issues.Take(12))ImGui.TextWrapped(issue);
        ImGui.EndDisabled();ImGui.EndChild();
        if(output)
        {
            ImGui.BeginDisabled(projectionExport is not null);
            if(Button(B("export")))FileDialog(B("export"),path=>Run(()=>
            {
                SaveProjectionView();var snapshot=new DirectorProjectionScene(api,Project!,projectionObject,scene.Build,source=>Runtime.Renderer.ParticleTexture(Project!,source));
                try{projectionExport=new(snapshot,path,owner.Config.FfmpegPath);}catch{snapshot.Dispose();throw;}
            }),"building"+(v.Apng?".apng":".gif"));
            ImGui.EndDisabled();
            if(projectionExport is not null&&Button(L("creation-cancel-export"))){projectionExport.Dispose();projectionExport=null;}
        }
        else
        {
            if(Button(B("reset")))ResetProjectionView();
            SameLineIfFits(B("exit"));if(Button(B("exit")))CloseProjection();
        }
        void ViewBool(string key,bool value,Action<bool> set)
        {if(Checkbox(B(key),ref value)){set(value);SaveProjectionView();if(key is "animate" or "particles")projectionRevision=-1;}}
        void ViewNumber(string key,double value,Action<double> set,double min,double max,float speed=1)
        {
            ImGui.TextWrapped(B(key));ImGui.SetNextItemWidth(-1);
            if(DirectorImGuiNumeric.Double("##projection-"+key,ref value,speed,min,max))set(value);
            if(DirectorImGuiNumeric.FinishedEditing){SaveProjectionView();if(key=="start")projectionRevision=-1;}
        }
    }
}
