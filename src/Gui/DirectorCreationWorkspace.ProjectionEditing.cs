using System.Numerics;
using ImGuiNET;
using VSDirector.Blueprints;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private readonly HashSet<int> projectionSelection=[];
    private Guid projectionSelectionOwner;
    private int projectionAnchor;
    private string projectionCellSearch="",projectionPage="cells";
    private bool projectionShowDeleted,projectionCellMode;
    private string projectionTrackField="growth",projectionResourceField="",projectionMaterialSearch="";
    private CreationTrack? projectionKeyClipboard;
    private ProjectionChoice? projectionChoiceClipboard;
    private ProjectionCell[] projectionList=[];
    private long projectionListRevision=-1;
    private Guid projectionListOwner;
    private string projectionListQuery="";
    private bool projectionListDeleted;
    private string[]? projectionMaterials;
    private CreationChannel projectionCellChannel=CreationChannel.Position;
    private static string Q(string key)=>L("projection-edit-"+key);
    private static bool ProjectionCombo(string label,string preview)
    {ImGui.TextWrapped(label);ImGui.SetNextItemWidth(-1);return ImGui.BeginCombo("##"+label,preview);}

    private BlueprintBuild? EditableProjection(CreationSceneObject obj)
    {var build=Runtime.Blueprints.Get(Project!,obj);return build.Ready?build:null;}
    private void UpgradeProjection(CreationSceneObject obj)
    {Session.Edit(Q("upgrade"),_=>obj.Projection!.Version=2);}
    private void ProjectionEdit(string label,Action<CreationProjectionSettings> action)
    {
        if(SelectedObject?.Projection is null)return;
        Run(()=>Session.Edit(label,_=>{SelectedObject!.Projection!.Version=2;action(SelectedObject.Projection);}));
    }
    private void DrawProjectionBrowser(CreationSceneObject obj) => DrawContentBrowser();
    private void DrawProjectionCellBrowser(CreationSceneObject obj)
    {
            if(EditableProjection(obj) is {} build)
            {
                ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##projection-cell-search",L("creation-search-names"),ref projectionCellSearch,256);
                Checkbox(Q("deleted"),ref projectionShowDeleted);
                var settings=obj.Projection!;var needle=projectionCellSearch.Trim();
                if(projectionListRevision!=Session.Revision||projectionListOwner!=obj.Id||projectionListQuery!=needle||projectionListDeleted!=projectionShowDeleted)
                {
                    projectionList=DirectorProjectionEditing.Cells(build.Cells,settings).Where(c=>(projectionShowDeleted||settings.Edits.GetValueOrDefault(c.Id)?.Deleted!=true)&&(needle.Length==0||c.Code.Contains(needle,StringComparison.OrdinalIgnoreCase)||DirectorProjectionEditing.Position(c,settings).ToString().Contains(needle,StringComparison.OrdinalIgnoreCase))).ToArray();
                    projectionListRevision=Session.Revision;projectionListOwner=obj.Id;projectionListQuery=needle;projectionListDeleted=projectionShowDeleted;
                }
                var cells=projectionList;
                ImGui.TextUnformatted($"{Q("selected")}: {projectionSelection.Count} / {cells.Length}");
                ImGui.BeginChild("projection-cell-list",new(0,0),false);
                if(contentCellRestore>=0){ImGui.SetScrollY(contentCellRestore);contentCellRestore=-1;}
                float row=ImGui.GetTextLineHeightWithSpacing();float scroll=ImGui.GetScrollY();int first=Math.Clamp((int)(scroll/row),0,cells.Length),end=Math.Min(cells.Length,first+(int)(ImGui.GetWindowHeight()/row)+3);
                if(first>0)ImGui.Dummy(new(1,first*row));
                for(int i=first;i<end;i++)
                {
                    var cell=cells[i];var p=DirectorProjectionEditing.Position(cell,settings);ImGui.PushID(cell.Id);
                    string label=$"{(settings.Edits.GetValueOrDefault(cell.Id)?.Deleted==true?"× ":"")}{cell.Code} [{p.X:g},{p.Y:g},{p.Z:g}]";
                    if(ImGui.Selectable(ContentFit(label,ImGui.GetContentRegionAvail().X-4)+"###cell",projectionSelection.Contains(cell.Id),ImGuiSelectableFlags.None,new(Math.Max(1,ImGui.GetContentRegionAvail().X),row-ImGui.GetStyle().ItemSpacing.Y)))
                    {
                        var io=ImGui.GetIO();if(io.KeyShift){int start=Array.FindIndex(cells,c=>c.Id==projectionAnchor);if(start<0)start=i;if(!io.KeyCtrl)projectionSelection.Clear();for(int j=Math.Min(start,i);j<=Math.Max(start,i);j++)projectionSelection.Add(cells[j].Id);}
                        else{if(!io.KeyCtrl)projectionSelection.Clear();if(!projectionSelection.Add(cell.Id))projectionSelection.Remove(cell.Id);projectionAnchor=cell.Id;}
                        contentView.Section="b.cells";ShowCreationPanel("properties");
                    }
                    if(ImGui.IsItemHovered())DirectorImGuiTooltip.Show(label);
                    ImGui.PopID();
                }
                if(end<cells.Length)ImGui.Dummy(new(1,(cells.Length-end)*row));contentView.CellScroll=ImGui.GetScrollY();ImGui.EndChild();
            }
            else ImGui.TextWrapped(B("loading"));
    }
    private void DrawProjectionRange(CreationSceneObject obj)
    {
        var s=obj.Projection!;Toggle(Q("range-enabled"),s.RangeEnabled,v=>s.RangeEnabled=v);
        VectorField(B("first"),s.RangeMin,v=>s.RangeMin=v,1,inline:true);VectorField(B("second"),s.RangeMax,v=>s.RangeMax=v,1,inline:true);
        if(Button(Q("range-reset")))ProjectionEdit(Q("range-reset"),s=>
        {
            var loaded=EditableProjection(obj)??throw new InvalidOperationException(B("loading"));
            var positions=DirectorProjectionEditing.Cells(loaded.Cells,s).Where(c=>s.Edits.GetValueOrDefault(c.Id)?.Deleted!=true).Select(c=>DirectorProjectionEditing.Position(c,s)).ToArray();
            if(positions.Length==0)throw new InvalidOperationException(Q("empty"));
            s.RangeMin=new(positions.Min(p=>p.X),positions.Min(p=>p.Y),positions.Min(p=>p.Z));s.RangeMax=new(positions.Max(p=>p.X),positions.Max(p=>p.Y),positions.Max(p=>p.Z));s.RangeEnabled=true;
        });
        if(EditableProjection(obj) is not {} build){ImGui.TextWrapped(B("loading"));return;}
        var ids=DirectorProjectionEditing.Cells(build.Cells,s).Where(c=>s.Edits.GetValueOrDefault(c.Id)?.Deleted!=true&&DirectorProjectionEditing.InRange(c,s)).Select(c=>c.Id).ToHashSet();
        var extent=System.Numerics.Vector3.Abs((s.RangeMax-s.RangeMin).Float())+System.Numerics.Vector3.One;
        ImGui.TextWrapped($"{B("size")}: {extent.X:g} × {extent.Y:g} × {extent.Z:g} · {Q("cells")}: {ids.Count}");
        if(Button(Q("select-range"))){projectionSelection.Clear();projectionSelection.UnionWith(ids);projectionAnchor=ids.FirstOrDefault();NavigateContent("b.cells");}
        ImGui.BeginDisabled(ids.Count==0);
        if(Button(Q("extract")))Run(()=>SplitProjection(obj,build,ids,false));
        SameLineIfFits(Q("cut"));if(Button(Q("cut")))Run(()=>SplitProjection(obj,build,ids,true));
        ImGui.EndDisabled();
    }
    private void SplitProjection(CreationSceneObject obj,BlueprintBuild build,HashSet<int> ids,bool cut)
    {
        if(ids.Count==0)throw new InvalidOperationException(Q("empty"));
        var copy=CreationJson.Clone(obj);copy.Id=Guid.NewGuid();copy.Name+=" / "+Q(cut?"cut":"extract");
        var all=DirectorProjectionEditing.Cells(build.Cells,obj.Projection!).ToArray();
        Session.Edit(Q(cut?"cut":"extract"),project=>
        {
            foreach(var cell in all){copy.Projection!.Edit(cell).Deleted=!ids.Contains(cell.Id)||obj.Projection!.Edits.GetValueOrDefault(cell.Id)?.Deleted==true;if(cut&&ids.Contains(cell.Id))obj.Projection!.Edit(cell).Deleted=true;}
            project.Objects.Add(copy);
            foreach(var track in project.Tracks.Where(t=>t.ObjectId==obj.Id).ToArray()){var t=CreationJson.Clone(track);t.Id=Guid.NewGuid();t.ObjectId=copy.Id;foreach(var key in t.Keys)key.Id=Guid.NewGuid();project.Tracks.Add(t);}
            var children=project.Objects.Where(o=>o.ParentId==obj.Id&&o.Kind==CreationObjectKind.Particle).Where(child=>child.ProjectionParticleSource.Length==0||all.Any(c=>ids.Contains(c.Id)&&(child.ProjectionParticleSource=="cell:"+c.Id||child.ProjectionParticleSource==build.Schematic!.Indices[c.Source]+":"+c.Code))).ToArray();
            var map=children.ToDictionary(c=>c.Id,_=>Guid.NewGuid());map[obj.Id]=copy.Id;
            Guid Remap(Guid id)=>id==Guid.Empty?id:map.GetValueOrDefault(id,id);
            foreach(var node in DirectorProjectionEditing.Effects(copy).SelectMany(e=>e.AllNodes()))if(node.Modules is {} mod)
                foreach(var external in new[]{mod.BindObject,mod.BindBone,mod.EndObject,mod.EndBone})if(external!=Guid.Empty)map.TryAdd(external,external);
            DirectorProjectionEditing.Remap(copy,id=>id==Guid.Empty?id:map.TryGetValue(id,out var mapped)?mapped:map[id]=Guid.NewGuid());
            foreach(var child in children)
            {
                var particle=CreationJson.Clone(child);particle.Id=map[child.Id];particle.ParentId=copy.Id;particle.TargetId=Remap(particle.TargetId);
                foreach(var effect in DirectorProjectionEditing.Effects(particle))foreach(var node in effect.AllNodes())map.TryAdd(node.Id,Guid.NewGuid());
                foreach(var effect in DirectorProjectionEditing.Effects(particle))foreach(var node in effect.AllNodes())
                {node.Id=Remap(node.Id);if(node.Modules is {} m){m.BindObject=Remap(m.BindObject);m.EndObject=Remap(m.EndObject);m.TrailEmitter=Remap(m.TrailEmitter);}}
                project.Objects.Add(particle);
                foreach(var track in project.Tracks.Where(t=>t.ObjectId==child.Id).ToArray()){var t=CreationJson.Clone(track);t.Id=Guid.NewGuid();t.ObjectId=particle.Id;foreach(var key in t.Keys)key.Id=Guid.NewGuid();project.Tracks.Add(t);}
                foreach(var clip in project.Clips.Where(c=>c.ObjectId==child.Id).ToArray()){var c=CreationJson.Clone(clip);c.Id=Guid.NewGuid();c.ObjectId=particle.Id;project.Clips.Add(c);}

            }
            foreach(var clip in project.Clips.Where(c=>c.ObjectId==obj.Id).ToArray()){var c=CreationJson.Clone(clip);c.Id=Guid.NewGuid();c.ObjectId=copy.Id;project.Clips.Add(c);}

        });
        Select(copy);projectionSelection.Clear();
        string path=Path.Combine(Runtime.Blueprints.Library,"Structure-"+copy.Id.ToString("N")+".director-structure.json");
        Runtime.ExportProjectionPreset(copy,path);Runtime.Blueprints.Scan();
    }
    private void DrawProjectionEditor(CreationSceneObject obj)
    {
        var settings=obj.Projection!;
        if(settings.Version==1){Hint(Q("upgrade-hint"));if(Button(Q("upgrade")))UpgradeProjection(obj);return;}
        switch(projectionPage)
        {
            case "cells":DrawProjectionCellProperties(obj);break;
            case "range":DrawProjectionRange(obj);break;
            case "keys":DrawProjectionKeys(obj);break;
            case "files":DrawProjectionFiles(obj);break;
            default:
                if(projectionPage=="materials")
                {
                    for(int i=0;i<5;i++){ImGui.PushID("glass"+i);ProjectionChoiceControl(settings.Glass[i],Q("glass")+" "+(i+1),"glass"+i);ImGui.PopID();}
                    for(int i=0;i<4;i++){ImGui.PushID("edge"+i);ProjectionChoiceControl(settings.Edges[i],Q("edge")+" "+(i+1),"edge"+i);ImGui.PopID();}
                }
                if(projectionPage=="particles")
                {
                    Hint(Q("particle-hint"));
                    if(Button(B("add-particles"))){projectionSchemeTarget=obj.Id;OpenParticlePicker();}
                    if(ProjectionCombo(Q("scheme"),settings.ParticleScheme.At(Session.Time)))
                    {foreach(string name in settings.ParticleSchemes.Keys)if(ImGui.Selectable(name))ProjectionEdit(Q("scheme"),s=>s.ParticleScheme.Value=name);ImGui.EndCombo();}
                    if(Button(Q("key-scheme")))ProjectionEdit(Q("key-scheme"),s=>ChoiceKey(s.ParticleScheme));
                }
                if(projectionPage=="physics")Hint(Q("physics-hint"));
                foreach(var field in ProjectionFields.All.Where(f=>f.Group==projectionPage))DrawProjectionField(obj,field);
                break;
        }
        if(error.Length>0)ImGui.TextWrapped(error);
    }
    private void ProjectionChoiceControl(ProjectionChoice choice,string label,string resource)
    {
        if(!drawingContentInspector)TextField(label,choice.Value,v=>choice.Value=v.Trim());
        if(choice.Value.Length>0&&api.World.GetBlock(new Vintagestory.API.Common.AssetLocation(choice.Value)) is null)Hint(Q("missing-material"));
        if(ProjectionCombo(Q("choose-material"),choice.Value.Length==0?Q("choose-material"):choice.Value))
        {
            ImGui.InputTextWithHint("##material-search",L("creation-search-names"),ref projectionMaterialSearch,128);
            projectionMaterials??=api.World.Blocks.Where(b=>b?.Code is not null&&b.Id>0&&!b.ForFluidsLayer).Select(b=>b.Code.ToString()).OrderBy(c=>c,StringComparer.Ordinal).ToArray();
            var matches=projectionMaterials.Where(c=>c.Contains(projectionMaterialSearch,StringComparison.OrdinalIgnoreCase)).Take(200);
            foreach(string code in matches)if(ImGui.Selectable(code,choice.Value==code))ProjectionEdit(label,_=>choice.Value=code);
            ImGui.EndCombo();
        }
        if(drawingContentInspector && Header(N("resource-code")))TextField(label,choice.Value,v=>choice.Value=v.Trim());
        if(Button(Q("key-resource")))ProjectionEdit(Q("key-resource"),_=>ChoiceKey(choice));
        SameLineIfFits(Q("keys"));if(Button(Q("keys"))){projectionResourceField=resource;projectionPage="keys";NavigateContent("b.keys");}
    }
    private void ChoiceKey(ProjectionChoice choice)
    {choice.Keys.RemoveAll(k=>Math.Abs(k.Time-Session.Time)<1e-6);choice.Keys.Add(new(){Time=Session.Time,Value=choice.Value});choice.Keys.Sort((a,b)=>a.Time.CompareTo(b.Time));}
    private void KeyProjectionField(CreationSceneObject obj,string key)
    {
        Session.Edit(Q("add-key"),_=>{var p=obj.Projection!.Parameter(key);AddProjectionKey(p.Track,new(p.Value,0,0),ProjectionFields.ById[key].Step);});projectionTrackField=key;projectionResourceField="";
    }
    private void AddProjectionKey(CreationTrack track,CreationVector value,bool step=false)
    {track.Keys.RemoveAll(k=>Math.Abs(k.Time-Session.Time)<1e-6);track.Keys.Add(new(){Time=Session.Time,Value=value,Interpolation=step?CreationInterpolation.Hold:CreationInterpolation.Linear});track.Keys.Sort((a,b)=>a.Time.CompareTo(b.Time));}
    private void DrawProjectionCellProperties(CreationSceneObject obj)
    {
        if(EditableProjection(obj) is not {} build){Hint(B("loading"));return;}
        var s=obj.Projection!;Toggle(Q("cell-keys"),s.CellKeyframes,v=>s.CellKeyframes=v);
        var selected=DirectorProjectionEditing.Cells(build.Cells,s).Where(c=>projectionSelection.Contains(c.Id)).ToArray();
        if(selected.Length==0){Hint(Q("pick-cell"));return;}
        var primary=selected.FirstOrDefault(c=>c.Id==projectionAnchor)??selected[0];var edit=s.Edits.GetValueOrDefault(primary.Id)??new ProjectionCellEdit{Source=primary.Source};
        VectorField(Q("position"),DirectorProjectionEditing.Position(primary,s),v=>{var delta=v-DirectorProjectionEditing.Position(primary,s);foreach(var c in selected)s.Edit(c).Offset+=delta;},.1f,inline:true);
        VectorField(Q("rotation"),edit.Rotation,v=>{var delta=v-edit.Rotation;foreach(var c in selected)s.Edit(c).Rotation+=delta;},1,inline:true);
        VectorField(Q("scale"),edit.Scale,v=>{v=new(Math.Clamp(v.X,.25,4),Math.Clamp(v.Y,.25,4),Math.Clamp(v.Z,.25,4));foreach(var c in selected)s.Edit(c).Scale=v;},.01f,inline:true);
        Toggle(Q("override"),edit.OverrideTransform,v=>{foreach(var c in selected)s.Edit(c).OverrideTransform=v;});
        if(Button(Q("duplicate")))ProjectionEdit(Q("duplicate"),s=>
        {
            var ids=DirectorProjectionEditing.Duplicate(build.Cells,s,projectionSelection);
            foreach(int id in ids)
            {
                var original=build.Cells[s.Edits[id].Source];string native=build.Schematic!.Indices[original.Source]+":"+original.Code;
                var child=Project!.Objects.FirstOrDefault(o=>o.ParentId==obj.Id&&o.ProjectionParticleSource==native);
                if(child is null)continue;
                var particle=CreationJson.Clone(child);particle.Id=Guid.NewGuid();particle.ProjectionParticleSource="cell:"+id;
                particle.ParticleEffect=child.ParticleEffect?.Duplicate();Project.Objects.Add(particle);
                foreach(var track in Project.Tracks.Where(t=>t.ObjectId==child.Id).ToArray()){var t=CreationJson.Clone(track);t.Id=Guid.NewGuid();t.ObjectId=particle.Id;foreach(var key in t.Keys)key.Id=Guid.NewGuid();Project.Tracks.Add(t);}
            }
            projectionSelection.Clear();projectionSelection.UnionWith(ids);projectionAnchor=ids.FirstOrDefault();
        });
        SameLineIfFits(Q("delete"));if(Button(Q("delete")))ProjectionEdit(Q("delete"),s=>{foreach(var c in selected)s.Edit(c).Deleted=true;});
        if(Button(Q("restore")))ProjectionEdit(Q("restore"),s=>{foreach(var c in selected){var e=s.Edit(c);e.Offset=e.Rotation=default;e.Scale=CreationVector.One;e.Deleted=false;}});
        SameLineIfFits(Q("clear-keys"));if(Button(Q("clear-keys")))ProjectionEdit(Q("clear-keys"),s=>{foreach(var c in selected)s.Edit(c).Tracks.Clear();});
        ImGui.BeginDisabled(!s.CellKeyframes);
        foreach(var channel in new[]{CreationChannel.Position,CreationChannel.Rotation,CreationChannel.Scale,CreationChannel.Visible,CreationChannel.Pivot})
        {
            if(Button(Q("key-"+channel)))ProjectionEdit(Q("add-key"),s=>
            {
                foreach(var c in selected){var e=s.Edit(c);if(channel is CreationChannel.Position or CreationChannel.Rotation or CreationChannel.Scale&&!e.Tracks.Any(t=>t.Channel is CreationChannel.Position or CreationChannel.Rotation or CreationChannel.Scale))e.OverrideTransform=true;var t=e.Tracks.Find(t=>t.Channel==channel);if(t is null)e.Tracks.Add(t=new(){Channel=channel});
                    var value=channel switch{CreationChannel.Position=>e.OverrideTransform?DirectorProjectionEditing.Position(c,s):default,CreationChannel.Rotation=>e.OverrideTransform?e.Rotation:default,
                        CreationChannel.Scale=>e.OverrideTransform?e.Scale:CreationVector.One,CreationChannel.Visible=>CreationVector.One,_=>new CreationVector(e.OverrideTransform?1:0,0,0)};
                    AddProjectionKey(t,value,channel is CreationChannel.Visible or CreationChannel.Pivot);}
                projectionCellChannel=channel;projectionPage="keys";projectionTrackField="";projectionResourceField="";NavigateContent("b.keys");
            });
        }
        ImGui.EndDisabled();
    }
    private void DrawProjectionKeys(CreationSceneObject obj)
    {
        var s=obj.Projection!;
        if(projectionResourceField.Length>0)
        {
            var choice=projectionResourceField=="scheme"?s.ParticleScheme:projectionResourceField.StartsWith("glass",StringComparison.Ordinal)?s.Glass[int.Parse(projectionResourceField[5..])]:s.Edges[int.Parse(projectionResourceField[4..])];
            ImGui.TextUnformatted(projectionResourceField);
            if(Button(Q("copy-keys")))projectionChoiceClipboard=CreationJson.Clone(choice);
            SameLineIfFits(Q("paste-keys"));ImGui.BeginDisabled(projectionChoiceClipboard is null);
            if(Button(Q("paste-keys")))ProjectionEdit(Q("paste-keys"),_=>choice.Keys=CreationJson.Clone(projectionChoiceClipboard!).Keys);ImGui.EndDisabled();
            foreach(var key in choice.Keys.ToArray())
            {
                ImGui.PushID(choice.Keys.IndexOf(key));Number(Q("time"),key.Time,v=>key.Time=v,0,86400);
                TextField(Q("value"),key.Value,v=>key.Value=v);
                if(Button(Q("delete")))ProjectionEdit(Q("delete"),_=>choice.Keys.Remove(key));ImGui.PopID();
            }
            if(Button(Q("track")))projectionResourceField="";
            return;
        }
        if(!drawingContentInspector && ProjectionCombo(Q("track"),projectionTrackField.Length>0?Q(projectionTrackField):Q("key-"+projectionCellChannel)))
        {
            foreach(var f in ProjectionFields.All)if(ImGui.Selectable(Q(f.Id)))projectionTrackField=f.Id;
            foreach(var c in new[]{CreationChannel.Position,CreationChannel.Rotation,CreationChannel.Scale,CreationChannel.Visible,CreationChannel.Pivot})if(ImGui.Selectable(Q("key-"+c))){projectionTrackField="";projectionCellChannel=c;}
            ImGui.EndCombo();
        }
        CreationTrack? track=projectionTrackField.Length>0?s.Parameters.GetValueOrDefault(projectionTrackField)?.Track:s.Edits.GetValueOrDefault(projectionAnchor)?.Tracks.Find(t=>t.Channel==projectionCellChannel);
        if(track is null){Hint(Q("no-keys"));return;}
        if(Button(Q("copy-keys")))projectionKeyClipboard=CreationJson.Clone(track);
        SameLineIfFits(Q("paste-keys"));ImGui.BeginDisabled(projectionKeyClipboard is null);
        if(Button(Q("paste-keys")))ProjectionEdit(Q("paste-keys"),_=>{track.Keys=CreationJson.Clone(projectionKeyClipboard!).Keys;foreach(var key in track.Keys)key.Id=Guid.NewGuid();});ImGui.EndDisabled();
        foreach(var key in track.Keys.OrderBy(k=>k.Time).ToArray())
        {
            ImGui.PushID(key.Id.ToString());Number(Q("time"),key.Time,v=>key.Time=Math.Clamp(v,0,86400),0,86400);
            VectorField(Q("value"),key.Value,v=>key.Value=v,.01f,inline:true);
            if(ProjectionCombo(Q("interpolation"),key.Interpolation.ToString())){foreach(var mode in Enum.GetValues<CreationInterpolation>())if(ImGui.Selectable(mode.ToString()))ProjectionEdit(Q("interpolation"),_=>key.Interpolation=mode);ImGui.EndCombo();}
            if(Button(Q("delete")))ProjectionEdit(Q("delete"),_=>track.Keys.Remove(key));ImGui.PopID();
        }
    }
    private void DrawProjectionFiles(CreationSceneObject obj)
    {
        Hint(Q("export-hint"));
        if(drawingContentInspector)ImGui.Dummy(new(1,Math.Max(0,ImGui.GetContentRegionAvail().Y-ImGui.GetFrameHeightWithSpacing()*4)));
        if(Button(Q("export-native")))FileDialog(Q("export-native"),file=>Run(()=>
        {
            var build=EditableProjection(obj)??throw new InvalidOperationException(B("loading"));
            var ids=DirectorProjectionEditing.Cells(build.Cells,obj.Projection!).Where(c=>obj.Projection!.Edits.GetValueOrDefault(c.Id)?.Deleted!=true).Select(c=>c.Id).ToHashSet();
            var document=DirectorProjectionDocument.Extract(build,obj.Projection!,ids,obj.Name);new BlueprintCodec().Write(file,document);
        }),obj.Name+".vsbp");
        if(Button(Q("export-preset")))FileDialog(Q("export-preset"),file=>Runtime.ExportProjectionPreset(obj,file),obj.Name+".director-structure.json");
        if(Button(Q("import-preset")))FileDialog(Q("import-preset"),file=>Select(Runtime.ImportProjectionPreset(file)));
    }
    private CreationParticleEffect ImportProjectionTextures(CreationParticleEffect effect)
    {
        foreach(var (source,data) in effect.Textures)
        {
            string extension=Path.GetExtension(source).ToLowerInvariant();
            if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Invalid texture extension.");
            var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileName(source)};asset.Source="assets/"+asset.Id.ToString("N")+extension;
            string path=Session.Store.AssetPath(Project!.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,data);Project.Assets.Add(asset);
            foreach(var node in effect.AllNodes().Where(e=>e.Texture==source))node.Texture=asset.Source;
        }
        effect.Textures.Clear();return effect;
    }
    private Guid projectionSchemeTarget;
    private bool AcceptProjectionScheme(CreationSceneObject particle)
    {
        if(projectionSchemeTarget==Guid.Empty||Project?.Objects.Find(o=>o.Id==projectionSchemeTarget)?.Projection is not {} s||particle.ParticleEffect is null)return false;
        if(particle.ParticleEffect.AllNodes().Any(e=>e.Modules is {Geometry:not ParticleGeometry.Particle}))throw new InvalidDataException(Q("scheme-geometry"));
        string name=particle.ParticleEffect.Name;Session.Edit(Q("scheme"),_=>{s.Version=2;s.ParticleSchemes[name]=ImportProjectionTextures(particle.ParticleEffect.Duplicate());s.ParticleScheme.Value=name;s.Parameter("particlesEnabled").Value=1;if(s.V("particleRatio")==0)s.Parameter("particleRatio").Value=1;});
        projectionSchemeTarget=Guid.Empty;auxiliary="";ClearParticlePreviews();return true;
    }
}
