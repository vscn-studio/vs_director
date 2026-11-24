using System.Numerics;
using ImGuiNET;
using VSDirector.Blueprints;
using VSDirector.Data;
using VSDirector.Rendering;
using Vintagestory.API.MathTools;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private static string B(string key)=>L("projection-"+key);
    private string blueprintSearch="",blueprintCategory="",blueprintSelected="";
    private readonly Dictionary<string,(DirectorPickerMeshPreview Renderer,int Frame,double RenderedAt)> blueprintCards=[];
    private BlueprintEntry? pendingBlueprint;
    private bool blueprintCapturing,blueprintDragging,blueprintHasRegion;
    private CreationVector blueprintFirst,blueprintSecond;
    private string blueprintName="Building";
    private IEnumerator<RegionCapture.Progress>? blueprintCaptureWork;
    private RegionCapture.Progress? blueprintCaptureProgress;
    private bool blueprintCaptureImport;
    private string blueprintCaptureName="";
    private void OpenBlueprintPicker(){CloseProjection();pendingBlueprint=null;auxiliary="projections";error="";Runtime.Blueprints.Scan();}
    private void ClearBlueprintCards(){foreach(var p in blueprintCards.Values)p.Renderer.Dispose();blueprintCards.Clear();}
    private void DrawBlueprintPicker()
    {
        var library=Runtime.Blueprints;
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##blueprint-search",L("creation-search-names"),ref blueprintSearch,256);
        pickerFooterHeight=ImGui.GetFrameHeightWithSpacing()+ImGui.GetStyle().ScrollbarSize;
        float body=Math.Max(1,ImGui.GetContentRegionAvail().Y-pickerFooterHeight-ImGui.GetTextLineHeightWithSpacing()*2);
        ImGui.BeginChild("blueprint-folders",new(PickerSidebarWidth(),body),true);
        if(ImGui.Selectable(B("all"),blueprintCategory.Length==0))blueprintCategory="";
        foreach(var root in new[]{"local","vanilla","mods"})
        {
            if(!ImGui.TreeNodeEx(B(root),ImGuiTreeNodeFlags.DefaultOpen|ImGuiTreeNodeFlags.SpanAvailWidth))continue;
            foreach(var folder in library.Entries.Where(e=>e.Category.StartsWith(root)).Select(e=>e.Category).Distinct())
                if(ImGui.Selectable(folder==root||folder==root+"/"?B("all"):folder[(root.Length+1)..],blueprintCategory==folder))blueprintCategory=folder;
            ImGui.TreePop();
        }
        if(Header(Q("preset-library")))
        {
            foreach(string path in library.PresetFiles.Where(p=>Path.GetFileName(p).Contains(blueprintSearch,StringComparison.OrdinalIgnoreCase)))
                if(ImGui.Selectable(Path.GetFileName(path)))Run(()=>{Select(Runtime.ImportProjectionPreset(path));auxiliary="";ClearBlueprintCards();});
        }
        ImGui.EndChild();ImGui.SameLine();ImGui.BeginChild("blueprint-grid",new(0,body),false);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        bool grid=BeginPickerGrid("blueprint-cards");
        if(grid)foreach(var entry in library.Entries.Where(e=>(blueprintCategory.Length==0||e.Category==blueprintCategory)&&e.Name.Contains(blueprintSearch.Trim(),StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.TableNextColumn();float size=Math.Max(1,ImGui.GetContentRegionAvail().X);var min=ImGui.GetCursorScreenPos();
            if(!ImGui.IsRectVisible(new Vector2(size))){ImGui.Dummy(new(size));continue;}
            ImGui.InvisibleButton(entry.Source,new(size));bool hover=ImGui.IsItemHovered();if(ImGui.IsItemClicked())blueprintSelected=entry.Source;
            var max=min+new Vector2(size);var imageMax=max-new Vector2(0,ImGui.GetTextLineHeightWithSpacing()+8);
            try
            {
                var build=library.Get(entry);
                if(build.Ready&&build.Parts.Count>0)
                {
                    if(!blueprintCards.TryGetValue(entry.Source,out var card)&&clock.ElapsedMilliseconds<4)
                    {card=(new(),ImGui.GetFrameCount(),double.NegativeInfinity);blueprintCards[entry.Source]=card;card.Renderer.SetProjection(build.Parts,Matrix4x4.Identity);}
                    if(card.Renderer is not null)
                    {
                        if((clock.ElapsedMilliseconds<4&&ImGui.GetTime()-card.RenderedAt>.15)||card.Renderer.Texture==0){card.Renderer.Render(ImGui.GetTime(),1);card.RenderedAt=ImGui.GetTime();}
                        blueprintCards[entry.Source]=(card.Renderer,ImGui.GetFrameCount(),card.RenderedAt);
                        ImGui.GetWindowDrawList().AddImage((nint)card.Renderer.Texture,min,imageMax,new(0,1),new(1,0));
                    }
                }
                else ImGui.GetWindowDrawList().AddText(min,0xffaaaaaa,build.Error.Length>0||build.Ready?B("failed"):B("loading"));
                if(hover&&(build.Error.Length>0||build.Issues.Count>0))DirectorImGuiTooltip.Show(string.Join("\n",build.Issues.Prepend(build.Error)));
            }
            catch(Exception e){if(hover)DirectorImGuiTooltip.Show(e.Message);}
            PickerCardLabel(ImGui.GetWindowDrawList(),min,max,imageMax.Y,entry.Name,blueprintSelected==entry.Source,hover);
        }
        EndPickerGrid(grid);ImGui.EndChild();
        foreach(var key in blueprintCards.Where(p=>p.Value.Frame!=ImGui.GetFrameCount()).OrderByDescending(p=>p.Value.Frame).Skip(24).Select(p=>p.Key).ToArray())
        {blueprintCards[key].Renderer.Dispose();blueprintCards.Remove(key);}
        var selected=library.Entries.FirstOrDefault(e=>e.Source==blueprintSelected);
        EllipsizedParticleText(error.Length>0?error:library.ScanError.Length>0?library.ScanError:library.Scanning?B("loading"):selected?.Name??B("pick-hint"),ImGui.GetContentRegionAvail().X);
        BeginPickerActions();ImGui.BeginDisabled(selected is null);
        if(PickerAction("projection-import",true))pendingBlueprint=selected;
        ImGui.EndDisabled();
        if(PickerAction("projection-browse"))FileDialog(B("browse"),file=>
        {
            var entry=new BlueprintEntry(Path.GetFileNameWithoutExtension(file),Path.GetFullPath(file),"local");
            if(!library.Entries.Any(e=>e.Source==entry.Source))library.Entries.Add(entry);blueprintSelected=entry.Source;
        });
        if(PickerAction("projection-edit-import-preset"))FileDialog(Q("import-preset"),file=>Run(()=>{Select(Runtime.ImportProjectionPreset(file));auxiliary="";ClearBlueprintCards();}));
        if(PickerAction("projection-capture")){auxiliary="";blueprintCapturing=true;blueprintHasRegion=false;ShowCreationPanel("properties");}
        if(PickerAction("creation-particle-rescan")){pendingBlueprint=null;ClearBlueprintCards();library.Clear();library.Scan();}
        if(PickerAction("creation-cancel")){pendingBlueprint=null;auxiliary="";ClearBlueprintCards();}
        EndPickerActions();
        if(pendingBlueprint is {} pending)
        {
            var build=library.Get(pending);
            if(build.Error.Length>0){error=build.Error;pendingBlueprint=null;}
            else if(build.Ready)Run(()=>{pendingBlueprint=null;Select(Runtime.ImportProjection(pending,build,Runtime.CursorPosition()));auxiliary="";ClearBlueprintCards();});
        }
    }
    private void DrawBlueprintProperties(CreationSceneObject obj)
    {
        var projection=obj.Projection!;
        if(!Header(B("add")))return;
        ImGui.TextWrapped($"{B("size")}: {projection.Size.X} × {projection.Size.Y} × {projection.Size.Z}");
        ImGui.TextWrapped(B("author")+": "+projection.Author);
        if(Button(B("preview")))Run(()=>OpenProjectionPreview(obj));SameLineIfFits(B("focus"));if(Button(B("focus")))Runtime.FocusProjection(obj);
        if(Button(B("rotate-left")))Session.Edit(B("rotate-left"),_=>obj.Transform=obj.Transform with{Rotation=obj.Transform.Rotation+new CreationVector(0,-90,0)});
        SameLineIfFits(B("rotate-right"));if(Button(B("rotate-right")))Session.Edit(B("rotate-right"),_=>obj.Transform=obj.Transform with{Rotation=obj.Transform.Rotation+new CreationVector(0,90,0)});
        if(Button(B("mirror-x")))Session.Edit(B("mirror-x"),_=>obj.Transform=obj.Transform with{Scale=new(-obj.Transform.Scale.X,obj.Transform.Scale.Y,obj.Transform.Scale.Z)});
        SameLineIfFits(B("mirror-z"));if(Button(B("mirror-z")))Session.Edit(B("mirror-z"),_=>obj.Transform=obj.Transform with{Scale=new(obj.Transform.Scale.X,obj.Transform.Scale.Y,-obj.Transform.Scale.Z)});
        Toggle(B("auto-particles"),projection.AutomaticParticles,v=>projection.AutomaticParticles=v);
        Hint(B("auto-particles-hint"));
        if(Button(B("add-particles"))){projectionParticleParent=obj.Id;OpenParticlePicker();}
        var build=Runtime.Blueprints.Get(Project!,obj);
        if(build.Error.Length>0)ImGui.TextWrapped(build.Error);
        foreach(var issue in build.Issues.Take(12))ImGui.TextWrapped(issue);
        if(!build.Ready)ImGui.TextWrapped(B("loading"));
        DrawProjectionEditor(obj);
    }
    private void DrawProjectionParticleSource(CreationSceneObject obj)
    {
        if(obj.ProjectionParticleSource.Length==0)return;
        Hint(obj.ProjectionParticleSource);
        if(Button(B("manual-particle")))Session.Edit(B("manual-particle"),_=>obj.ProjectionParticleSource="");
    }
    private Guid projectionParticleParent;
    private void AttachProjectionParticle(CreationSceneObject obj)
    {
        if(projectionParticleParent==Guid.Empty||Project?.Objects.Find(o=>o.Id==projectionParticleParent)?.Projection is null)return;
        obj.ParentId=projectionParticleParent;obj.Transform=obj.Transform with{Position=default};projectionParticleParent=Guid.Empty;
    }
    private void DrawBlueprintCapture()
    {
        if(!blueprintCapturing){blueprintCaptureWork?.Dispose();blueprintCaptureWork=null;return;}
        if(!Runtime.ProjectWorldMatches||Project is null){CancelBlueprintCapture();return;}
        if(blueprintCaptureWork is not null)AdvanceBlueprintCapture();
        if(!Runtime.ProjectWorldMatches||Project is null){blueprintCapturing=blueprintDragging=false;return;}
        var (min,size)=GameImageRect();bool hovered=ImGui.IsMouseHoveringRect(min,min+size)&&!ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel);
        if(ImGui.IsKeyPressed(ImGuiKey.Escape)){CancelBlueprintCapture();return;}
        if(hovered&&ImGui.IsMouseClicked(ImGuiMouseButton.Left)){blueprintCaptureWork?.Dispose();blueprintCaptureWork=null;blueprintCaptureProgress=null;blueprintHasRegion=blueprintDragging=false;return;}
        if(hovered&&blueprintCaptureWork is null&&ImGui.IsMouseClicked(ImGuiMouseButton.Right))Run(()=>{blueprintFirst=blueprintSecond=Runtime.SelectedBlock();blueprintDragging=true;blueprintHasRegion=true;});
        if(blueprintDragging){Run(()=>blueprintSecond=Runtime.SelectedBlock());if(!ImGui.IsMouseDown(ImGuiMouseButton.Right))blueprintDragging=false;}
        ImGui.SetNextWindowSize(new Vector2(350,330)*DirectorImGuiTheme.Scale,ImGuiCond.FirstUseEver);
        if(ImGui.Begin(B("capture")+"###projection-capture",ref blueprintCapturing,ImGuiWindowFlags.NoDocking))
        {
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);ImGui.TextWrapped(B("capture-hint"));
            ImGui.SetNextItemWidth(-1);ImGui.InputText(B("name"),ref blueprintName,128);
            void Corner(string label,ref CreationVector value)
            {
                ImGui.PushID(label); ImGui.TextUnformatted(label);
                double[] components = [value.X, value.Y, value.Z];
                float width = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3;
                bool changed = false;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (axis > 0) ImGui.SameLine();
                    ImGui.SetNextItemWidth(width);
                    changed |= DirectorImGuiNumeric.Double("##axis-" + axis, ref components[axis], 1, format: "%.0f");
                }
                if (changed) { value = new(Math.Round(components[0]), Math.Round(components[1]), Math.Round(components[2])); blueprintHasRegion = true; }
                ImGui.PopID();
            }
            Corner(B("first"),ref blueprintFirst);Corner(B("second"),ref blueprintSecond);
            var extent=Vector3.Abs((blueprintSecond-blueprintFirst).Float())+Vector3.One;
            ImGui.TextWrapped($"{B("size")}: {extent.X} × {extent.Y} × {extent.Z} = {(double)extent.X*extent.Y*extent.Z:N0}");
            if(blueprintCaptureProgress is {} progress)ImGui.TextUnformatted($"{progress.Completed:N0}/{progress.Total:N0} · {Q("cells")}: {progress.Blocks:N0}");
            ImGui.BeginDisabled(!blueprintHasRegion||blueprintDragging||blueprintCaptureWork is not null);
            if(Button(B("capture-save")))Run(()=>SaveCapturedBlueprint(true));
            if(Button(B("capture-only")))Run(()=>SaveCapturedBlueprint(false));
            ImGui.EndDisabled();
            if(Button(B("reselect"))){blueprintCaptureWork?.Dispose();blueprintCaptureWork=null;blueprintCaptureProgress=null;blueprintHasRegion=false;}
            SameLineIfFits(L("creation-cancel"));if(Button(L("creation-cancel")))CancelBlueprintCapture();
            if(error.Length>0)ImGui.TextWrapped(error);
        }
        ImGui.End();
        if(blueprintHasRegion)DrawBlueprintSelectionBox();
    }
    private void CancelBlueprintCapture()
    {blueprintCaptureWork?.Dispose();blueprintCaptureWork=null;blueprintCaptureProgress=null;blueprintCapturing=blueprintDragging=blueprintHasRegion=false;}
    private void SaveCapturedBlueprint(bool import)
    {
        string name=blueprintName.Trim();
        if(name.Length==0||name.Any(c=>!char.IsLetterOrDigit(c)&&c!='_'&&c!='-'))throw new InvalidDataException(B("invalid-name"));
        var min=new BlockPos((int)Math.Min(blueprintFirst.X,blueprintSecond.X),(int)Math.Min(blueprintFirst.Y,blueprintSecond.Y),(int)Math.Min(blueprintFirst.Z,blueprintSecond.Z),Project!.Dimension);
        var max=new BlockPos((int)Math.Max(blueprintFirst.X,blueprintSecond.X),(int)Math.Max(blueprintFirst.Y,blueprintSecond.Y),(int)Math.Max(blueprintFirst.Z,blueprintSecond.Z),Project!.Dimension);
        blueprintCaptureName=name;blueprintCaptureImport=import;blueprintCaptureProgress=null;
        blueprintCaptureWork=RegionCapture.CaptureIncremental(api.World,min,max,min,null).GetEnumerator();
    }
    private void AdvanceBlueprintCapture()
    {
        try
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(blueprintCaptureWork is not null&&clock.ElapsedMilliseconds<4)
            {
                if(!blueprintCaptureWork.MoveNext()){blueprintCaptureWork.Dispose();blueprintCaptureWork=null;break;}
                blueprintCaptureProgress=blueprintCaptureWork.Current;
                if(blueprintCaptureProgress.Document is not {} document)continue;
                DirectorBlueprintService.Validate(document);document.Metadata.Name=blueprintCaptureName;
                string path=Path.Combine(Runtime.Blueprints.Library,blueprintCaptureName+".vsbp");new BlueprintCodec().Write(path,document);
                blueprintCaptureWork.Dispose();blueprintCaptureWork=null;blueprintCapturing=false;Runtime.Blueprints.Scan();
                if(blueprintCaptureImport){pendingBlueprint=new(blueprintCaptureName,path,"local");blueprintSelected=path;auxiliary="projections";}
            }
        }
        catch(Exception e){error=e.GetBaseException().Message;blueprintCaptureWork?.Dispose();blueprintCaptureWork=null;}
    }
    private void DrawBlueprintSelectionBox()
    {
        var (min,size)=GameImageRect();var a=Vector3.Min(blueprintFirst.Float(),blueprintSecond.Float());var b=Vector3.Max(blueprintFirst.Float(),blueprintSecond.Float())+Vector3.One;
        var reference=Project!.Origin+new CreationVector(0,Project.Dimension*(double)BlockPos.DimensionBoundary,0);
        var view=DirectorCreationMath.ProjectView(api.Render.PerspectiveViewMat,reference,0);var projection=api.Render.PerspectiveProjectionMat.Select(v=>(float)v).ToArray();
        Matrix4x4 Matrix(float[] m)=>new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
        var vp=Matrix(view)*Matrix(projection);var points=new Vector2?[8];var camera=reference;
        for(int i=0;i<8;i++)
        {
            var p=new Vector3((i&1)==0?a.X:b.X,(i&2)==0?a.Y:b.Y,(i&4)==0?a.Z:b.Z)-new Vector3((float)camera.X,(float)camera.Y,(float)camera.Z);
            var v=Vector4.Transform(new Vector4(p,1),vp);if(v.W>.001)points[i]=min+new Vector2(v.X/v.W+1,1-v.Y/v.W)*size*.5f;
        }
        var draw=ImGui.GetForegroundDrawList();draw.PushClipRect(min,min+size);
        foreach(int[] face in new int[][]{[0,1,3,2],[4,5,7,6],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]})
            if(face.All(i=>points[i].HasValue))draw.AddQuadFilled(points[face[0]]!.Value,points[face[1]]!.Value,points[face[2]]!.Value,points[face[3]]!.Value,0x2000aaff);
        for(int i=0;i<8;i++)for(int bit=1;bit<=4;bit*=2)if((i&bit)==0&&points[i] is {} p&&points[i|bit] is {} q)draw.AddLine(p,q,0xff00aaff,2);
        draw.PopClipRect();
    }
}
