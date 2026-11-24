using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    // Editor-only state. Stable document IDs survive undo; no navigation data is written to a work.
    private sealed class ContentView
    {
        public string Section { get; set; } = "overview";
        public string Search { get; set; } = "";
        public string Scheme { get; set; } = "";
        public Guid Emitter { get; set; }
        public int Cell { get; set; }
        public string TrackField { get; set; } = "growth";
        public string ResourceField { get; set; } = "";
        public CreationChannel CellChannel { get; set; } = CreationChannel.Position;
        public string CellSearch { get; set; } = "";
        public float CellScroll { get; set; }
        public float BrowserScroll { get; set; }
        public string Driver { get; set; } = "Size";
        public HashSet<int> Cells { get; set; } = new();
        public Dictionary<string, bool> Expanded { get; set; } = new();
        public Dictionary<string, float> Scroll { get; set; } = new();
    }
    private sealed record ContentPage(string Id, string Icon);
    private static readonly ContentPage[] ParticleContentPages =
    [new("p.emission","clock"),new("p.shape","viewfinder"),new("p.motion","arrows-move"),
     new("p.color","palette"),new("p.material","photo"),new("p.geometry","cube"),
     new("p.environment","world"),new("p.evolution","route"),new("p.children","list-details")];
    private static readonly ContentPage[] BlueprintContentPages =
    [new("b.cells","cube"),new("b.range","viewfinder"),new("b.growth","sun"),
     new("b.waves","arrows-move"),new("b.fall","chevron-down"),new("b.edges","viewfinder"),
     new("b.particles","snowflake"),new("b.physics","arrows-move"),new("b.preview","eye"),
     new("b.export","download"),new("b.files","folder")];
    private bool contentPanel = true, drawingContentInspector;
    private ContentView contentView = new();
    private string contentOwner = "", contentScrollKey = "", contentField = "";
    private float contentScrollRestore = -1,contentBrowserRestore=-1,contentCellRestore=-1;
    private long contentValidatedRevision = -1;
    private DirectorCreationProject? contentDocument;
    private static string N(string key) => L("creation-nav-" + key);
    private string ContentKey => Project is null ? "" : Project.Id.ToString("N") + ":" + Session.SelectedObject.ToString("N");
    private CreationSceneObject? ContentParticleObject
    {
        get
        {
            if (SelectedObject is not { } obj) return null;
            if (contentView.Scheme.Length == 0) return obj.Kind == CreationObjectKind.Particle ? obj : null;
            if (obj.Projection?.ParticleSchemes.TryGetValue(contentView.Scheme, out var effect) != true) return null;
            // A view of an embedded scheme, not an extra scene object. Parameter writes edit the real effect.
            return new() { Id=obj.Id, Name=obj.Name+" / "+contentView.Scheme, Kind=CreationObjectKind.Particle, ParticleEffect=effect };
        }
    }
    private void SaveContentView()
    {
        if (contentOwner.Length == 0) return;
        contentView.Emitter=particleEmitter;contentView.Cell=projectionAnchor;contentView.Cells=new(projectionSelection);
        contentView.TrackField=projectionTrackField;contentView.ResourceField=projectionResourceField;
        contentView.CellChannel=projectionCellChannel;contentView.CellSearch=projectionCellSearch;contentView.Driver=particleDriver;
        editorPreferences.ContentViews[contentOwner]=contentView;
    }
    private void SyncContentSelection()
    {
        string key=ContentKey;
        if (key!=contentOwner)
        {
            SaveContentView();contentOwner=key;
            contentView=editorPreferences.ContentViews.GetValueOrDefault(key)??new();
            contentView.Cells??=new();contentView.Expanded??=new();contentView.Scroll??=new();
            contentView.Section??="overview";contentView.Scheme??="";contentView.Search??="";
            particleEmitter=contentView.Emitter;particleScope=0;particleMulti.Clear();
            particleSolo=Guid.Empty;Runtime.ParticleSolo=Guid.Empty;
            projectionSelection.Clear();projectionSelection.UnionWith(contentView.Cells);projectionAnchor=contentView.Cell;
            projectionTrackField=contentView.TrackField??"growth";projectionResourceField=contentView.ResourceField??"";
            projectionCellChannel=contentView.CellChannel;projectionCellSearch=contentView.CellSearch??"";
            particleDriver=contentView.Driver??"Size";
            contentBrowserRestore=contentView.BrowserScroll;contentCellRestore=contentView.CellScroll;
            projectionSelectionOwner=Session.SelectedObject;particleTreeOwner=Session.SelectedObject;
            particleParameterSearch="";particleFieldFilter=0;contentScrollKey="";contentField="";
            contentValidatedRevision=-1;contentDocument=null;particleCurveOpen=false;particleJsonOpen=false;particleGradientOpen=false;
        }
        if(contentValidatedRevision==Session.Revision && ReferenceEquals(contentDocument,Project))return;
        bool documentChanged=!ReferenceEquals(contentDocument,Project);
        contentValidatedRevision=Session.Revision;contentDocument=Project;
        if(contentView.Scheme.Length>0 && SelectedObject?.Projection?.ParticleSchemes.ContainsKey(contentView.Scheme)!=true)
        {contentView.Scheme="";contentView.Section="b.particles";}
        if(ContentParticleObject?.ParticleEffect is {} effect)
        {
            if(!effect.AllNodes().Any(e=>e.Id==particleEmitter))
            {particleEmitter=effect.Emitters.FirstOrDefault()?.Id??Guid.Empty;particleCurveOpen=false;}
            particleMulti.RemoveWhere(id=>!effect.AllNodes().Any(e=>e.Id==id));
        }
        if(documentChanged && SelectedObject?.Projection is not null && EditableProjection(SelectedObject) is {} build)
        {
            var ids=DirectorProjectionEditing.Cells(build.Cells,SelectedObject.Projection).Select(c=>c.Id).ToHashSet();
            projectionSelection.RemoveWhere(id=>!ids.Contains(id));
            if(!projectionSelection.Contains(projectionAnchor))projectionAnchor=projectionSelection.FirstOrDefault();
        }
        if(contentView.Section=="b.keys" && projectionResourceField.Length==0 && projectionTrackField.Length==0 && projectionSelection.Count==0)contentView.Section="b.cells";
        if(!ContentSections().Contains(contentView.Section))contentView.Section="overview";
        projectionCellMode=contentView.Section is "b.cells" or "b.range" or "b.keys";
    }
    private IEnumerable<string> ContentSections()
    {
        yield return "overview";yield return "object.transform";yield return "object.appearance";yield return "object.binding";yield return "view";
        if(SelectedObject?.Kind==CreationObjectKind.Model)
        {yield return "bones";yield return "ik";yield return "actions";}
        if(ContentParticleObject is {} particle)
        {
            if(particle.ParticleEffect is not null)
            {
                yield return "p.overview";yield return "p.drivers";
                foreach(var page in ParticleContentPages)yield return page.Id;
                if(contentView.Scheme.Length==0)yield return "p.files";
            }
            else {yield return "legacy.emission";yield return "legacy.motion";yield return "legacy.appearance";yield return "legacy.files";}
        }
        else if(SelectedObject?.Projection is not null)
        {
            foreach(var page in BlueprintContentPages)yield return page.Id;
            for(int i=0;i<5;i++)yield return "b.glass"+i;
            for(int i=0;i<4;i++)yield return "b.edge"+i;
            yield return "b.keys";yield return "b.particle-color";yield return "b.particle-outline";
        }
        else yield return "object.type";
    }
    private void NavigateContent(string section, Guid emitter=default, string field="")
    {
        Session.EndGesture(L("creation-edit"));
        SyncContentSelection();
        if(emitter!=Guid.Empty && emitter!=particleEmitter)particleCurveOpen=particleJsonOpen=particleGradientOpen=false;
        contentView.Section=section;contentField=field;
        if(section=="b.growth"||section=="b.waves"||section=="b.fall")contentView.Expanded["growth"]=true;
        if(section.StartsWith("b.glass",StringComparison.Ordinal))contentView.Expanded["glass"]=true;
        if(section.StartsWith("b.edge",StringComparison.Ordinal))contentView.Expanded["edges"]=true;
        if(emitter!=Guid.Empty){particleEmitter=emitter;contentView.Expanded["emitter:"+emitter]=true;}
        if(section=="b.cells")contentCellRestore=contentView.CellScroll;
        projectionCellMode=section is "b.cells" or "b.range" or "b.keys";
        contentScrollKey="";contentValidatedRevision=-1;
        ShowCreationPanel("properties");ShowCreationPanel("content");
    }
    private string ContentLabel(string section)
    {
        if(section.StartsWith("b.glass")&&int.TryParse(section[7..],out int glass))return Q("glass")+" "+(glass+1);
        if(section.StartsWith("b.edge")&&int.TryParse(section[6..],out int edge))return Q("edge")+" "+(edge+1);
        return section switch {"bones"=>L("creation-bones-tab"),"ik"=>"IK","actions"=>L("creation-actions-tab"),"view"=>L("creation-view"),_=>N(section)};
    }
    private void ContentLink(string section,string icon="settings",string? label=null,Guid emitter=default,string field="")
    {
        string text=label??ContentLabel(section);
        ImGui.PushID(section+emitter+field);
        bool active=contentView.Section==section && (emitter==Guid.Empty||particleEmitter==emitter);
        if(ImGui.Selectable(ContentFit(DirectorImGuiIcons.Get(icon)+"  "+text,ImGui.GetContentRegionAvail().X-4)+"###entry",active,ImGuiSelectableFlags.None,new(Math.Max(1,ImGui.GetContentRegionAvail().X),0)))NavigateContent(section,emitter,field);
        if(ImGui.IsItemHovered())DirectorImGuiTooltip.Show(text);
        ImGui.PopID();
    }
    private static string ContentFit(string text,float width)
    {
        if(ImGui.CalcTextSize(text).X<=width)return text;
        if(width<ImGui.CalcTextSize("…").X)return "";
        int lo=0,hi=text.Length;
        while(lo<hi){int mid=(lo+hi+1)/2;if(ImGui.CalcTextSize(text[..mid]+"…").X<=width)lo=mid;else hi=mid-1;}
        return text[..lo]+"…";
    }
    private bool ContentGroup(string id,string label,bool defaultOpen=false)
    {
        ImGui.SetNextItemOpen(contentView.Expanded.GetValueOrDefault(id,defaultOpen),ImGuiCond.Always);
        bool open=ImGui.TreeNodeEx(ContentFit(label,ImGui.GetContentRegionAvail().X-ImGui.GetTreeNodeToLabelSpacing()-8)+"###"+id,ImGuiTreeNodeFlags.SpanAvailWidth);
        contentView.Expanded[id]=open;return open;
    }
    private void DrawContentBrowser()
    {
        SyncContentSelection();
        if(contentBrowserRestore>=0){ImGui.SetScrollY(contentBrowserRestore);contentBrowserRestore=-1;}
        string ownerKey=contentOwner;
        try{DrawContentDirectory();}
        finally{if(ownerKey==contentOwner)contentView.BrowserScroll=ImGui.GetScrollY();}
    }
    private void DrawContentDirectory()
    {
        ImGui.TextWrapped(SelectedObject?.Name??N("no-object"));
        string query=contentView.Search;ImGui.SetNextItemWidth(-1);
        if(ImGui.InputTextWithHint("##content-search",N("search"),ref query,256))contentView.Search=query;
        if(ContentParticleObject?.ParticleEffect is not null)
        {
            string[] filters=[P("filter-all"),P("filter-pinned"),P("filter-modified"),P("filter-driven")];
            ImGui.SetNextItemWidth(-1);ImGui.Combo("##content-filter",ref particleFieldFilter,filters,filters.Length);
        }
        ImGui.Separator();
        if(SelectedObject is null){Hint(L("creation-no-selection"));return;}
        if(contentView.Search.Length>0||particleFieldFilter>0){DrawContentSearch();return;}
        if(contentView.Section=="b.cells" && contentView.Scheme.Length==0)
        {
            if(Button(N("back")))NavigateContent("overview");
            DrawProjectionCellBrowser(SelectedObject);return;
        }
        if(contentView.Scheme.Length>0)
        {
            if(Button(N("back-schemes"))) {contentView.Scheme="";particleScope=0;particleMulti.Clear();NavigateContent("b.particles");return;}
        }
        else
        {
            ContentLink("overview","list-details");ContentLink("object.transform","arrows-move");
            if(ContentGroup("object-options",N("object-options")))
            {ContentLink("object.appearance","palette");ContentLink("object.binding","route");ImGui.TreePop();}
        }
        if(ContentParticleObject is {} particle)
        {
            if(particle.ParticleEffect is {} effect)
            {
                ContentLink("p.overview","settings");DrawContentEmitters(effect);
                if(contentView.Scheme.Length==0)ContentLink("p.files","folder");
            }
            else foreach(string section in new[]{"legacy.emission","legacy.motion","legacy.appearance","legacy.files"})ContentLink(section);
        }
        else if(SelectedObject.Projection is not null)
        {
            foreach(var page in BlueprintContentPages.Take(2))ContentLink(page.Id,page.Icon);
            if(ContentGroup("growth",ContentLabel("b.growth")))
            {foreach(var page in BlueprintContentPages.Skip(2).Take(3))ContentLink(page.Id,page.Icon);ImGui.TreePop();}
            if(ContentGroup("glass",Q("glass")))
            {for(int i=0;i<5;i++)ContentLink("b.glass"+i,"cube");ImGui.TreePop();}
            if(ContentGroup("edges",Q("edge")))
            {ContentLink("b.edges","settings");for(int i=0;i<4;i++)ContentLink("b.edge"+i,"viewfinder");ImGui.TreePop();}
            foreach(var page in BlueprintContentPages.Skip(6))
            {
                ContentLink(page.Id,page.Icon);
                if(page.Id=="b.particles") {ContentLink("b.particle-color","palette");ContentLink("b.particle-outline","viewfinder");}
            }
            if(ContentGroup("schemes",N("schemes")))
            {
                foreach(string name in SelectedObject.Projection.ParticleSchemes.Keys)
                    if(ImGui.Selectable(ContentFit(name,ImGui.GetContentRegionAvail().X-4)+"###scheme-"+name)) {contentView.Scheme=name;particleEmitter=Guid.Empty;particleMulti.Clear();particleScope=0;NavigateContent("p.overview");}
                foreach(var child in Project!.Objects.Where(o=>o.ParentId==SelectedObject.Id&&o.Kind==CreationObjectKind.Particle))
                    if(ImGui.Selectable(ContentFit(child.Name,ImGui.GetContentRegionAvail().X-4)+"###"+child.Id))Select(child);
                ImGui.TreePop();
            }
        }
        else
        {
            ContentLink("object.type","settings");
            if(SelectedObject.Kind==CreationObjectKind.Model)
            {ContentLink("bones","list-details");ContentLink("ik","hand-move");ContentLink("actions","player-play");}
        }
        ImGui.Separator();ContentLink("view","eye");
    }
    private void DrawContentEmitters(CreationParticleEffect effect)
    {
        if(Button(P("add")))Session.Edit(P("add"),_=>
        {var node=new CreationParticleEmitter{Modules=effect.Version>=3?new():null};effect.Emitters.Add(node);particleEmitter=node.Id;NavigateContent("p.emission",node.Id);});
        if(contentView.Scheme.Length==0)
        {SameLineIfFits(P("resources"));if(Button(P("resources")))OpenParticlePicker();}
        ParticleDrop(effect,null);
        particleTreeSearch="";
        foreach(var emitter in effect.Emitters.ToArray())DrawParticleNode(effect,emitter,effect.Emitters,0);
    }
    private void DrawContentSearch()
    {
        string needle=contentView.Search.Trim();
        bool MatchContent(string value)=>value.Contains(needle,StringComparison.OrdinalIgnoreCase);
        if(particleFieldFilter==0)
            foreach(string section in ContentSections().Where(s=>MatchContent(ContentLabel(s)+" "+s)))ContentLink(section);
        if(ContentParticleObject?.ParticleEffect is {} effect)
        {
            foreach(var node in effect.AllNodes())
            {
                if(particleFieldFilter==0&&needle.Length>0&&MatchContent(node.Name))ContentLink("p.emission","snowflake",node.Name,node.Id);
                foreach(var field in CreationParticleFields.All)
                {
                    if(!MatchContent(field.Name+" "+L("creation-particle-field-"+field.Name.ToLowerInvariant())))continue;
                    if(particleFieldFilter==1&&!particlePinned.Contains(field.Name)||particleFieldFilter==2&&JsonNode.DeepEquals(CreationParticleFields.Find(node.Properties,field.Name),CreationParticleFields.Find(node.Original??CreationParticleFields.Defaults(),field.Name))||particleFieldFilter==3&&node.Modules?.Drivers.ContainsKey(field.Name)!=true)continue;
                    ContentLink(NativeContentSection(field.Name),"search",node.Name+" / "+L("creation-particle-field-"+field.Name.ToLowerInvariant()),node.Id,field.Name);
                }
                if(node.Modules is not null && particleFieldFilter==0)
                    foreach(string page in new[]{"shape","schedule","forces","material","geometry"})
                        foreach(string field in ParticleModuleFields(page).Where(f=>MatchContent(f+" "+P(f))))
                            ContentLink(ModuleContentSection(page,field),"search",node.Name+" / "+P(field),node.Id,field);
            }
        }
        if(SelectedObject?.Projection is not null && contentView.Scheme.Length==0)
            foreach(var field in ProjectionFields.All.Where(f=>MatchContent(f.Id+" "+Q(f.Id))))
                ContentLink(ProjectionContentSection(field.Id),"search",Q(field.Id),field:field.Id);
    }
    private void DrawContentProperties()
    {
        SyncContentSelection();
        if(SelectedObject is null){Hint(L("creation-no-selection"));return;}
        if(modelContext.Length>0){DrawProperties();return;}
        string section=contentView.Section;
        string path=SelectedObject.Name;
        if(contentView.Scheme.Length>0)path+=" / "+contentView.Scheme;
        if(section.StartsWith("p.")&&section is not ("p.overview" or "p.files")&&ContentParticleObject?.ParticleEffect?.AllNodes().FirstOrDefault(n=>n.Id==particleEmitter) is {} emitter)path+=" / "+emitter.Name;
        ImGui.TextWrapped(path+" / "+ContentLabel(section));
        if(section.StartsWith("p.") && ContentParticleObject?.ParticleEffect is {} effect && effect.AllNodes().FirstOrDefault(n=>n.Id==particleEmitter) is {} selected && section is not "p.overview")
        {
            string[] scopes=[L("creation-particle-scope-emitter"),L("creation-particle-scope-variant"),L("creation-particle-scope-family")];
            if(Combo(L("creation-particle-edit-scope"),scopes[particleScope]))
            {for(int i=0;i<scopes.Length;i++)if(ImGui.Selectable(scopes[i],particleScope==i))particleScope=i;ImGui.EndCombo();}
            Hint(P("targets")+": "+ParticleTargets(effect,selected).Count());
            if(ParticleTargets(effect,selected).Skip(1).Any())Hint(P("mixed-values"));
        }
        if(section=="b.cells")Hint(Q("selected")+": "+projectionSelection.Count);
        ImGui.Separator();
        string scrollKey=section+":"+contentView.Scheme+":"+particleEmitter;
        if(contentScrollKey!=scrollKey){contentScrollKey=scrollKey;contentScrollRestore=contentView.Scroll.GetValueOrDefault(scrollKey);}
        ImGui.BeginChild("content-properties-body",Vector2.Zero,false);
        if(contentScrollRestore>=0){ImGui.SetScrollY(contentScrollRestore);contentScrollRestore=-1;}
        drawingContentInspector=true;
        ImGui.BeginDisabled(projectionExport is not null && section!="b.export");
        try
        {
            if(section=="overview")DrawContentOverview();
            else if(section.StartsWith("p."))DrawParticleContent(ContentParticleObject!);
            else if(section.StartsWith("legacy."))DrawParticleEditor(SelectedObject);
            else if(section.StartsWith("b."))DrawBlueprintContent(SelectedObject);
            else if(section=="view")DrawView();
            else if(section=="bones")DrawBonesPanel();
            else if(section=="ik")DrawIkPanel();
            else if(section=="actions")DrawActionsPanel();
            else if(section=="object.transform"&&(IsIkControl(Session.SelectedObject)||PosePicking&&RigActor is not null))DrawPoseInspector(IsIkControl(Session.SelectedObject));
            else DrawObjectProperties();
        }
        finally
        {
            ImGui.EndDisabled();drawingContentInspector=false;contentView.Scroll[scrollKey]=ImGui.GetScrollY();ImGui.EndChild();
        }
    }
    private void DrawContentOverview()
    {
        var obj=SelectedObject!;
        TextField(L("creation-name"),obj.Name,v=>obj.Name=v);
        if(obj.Projection is {} projection)
        {
            ImGui.TextWrapped($"{B("size")}: {projection.Size.X} × {projection.Size.Y} × {projection.Size.Z}");
            Hint(B("author")+": "+projection.Author);
            if(Button(B("preview")))Run(()=>OpenProjectionPreview(obj));
            SameLineIfFits(B("focus"));if(Button(B("focus")))Runtime.FocusProjection(obj);
            Toggle(B("auto-particles"),projection.AutomaticParticles,v=>projection.AutomaticParticles=v);
            if(Button(B("add-particles"))){projectionParticleParent=obj.Id;OpenParticlePicker();}
            var build=Runtime.Blueprints.Get(Project!,obj);
            if(!build.Ready)Hint(B("loading"));if(build.Error.Length>0)Hint(build.Error);
            foreach(string issue in build.Issues.Take(12))Hint(issue);
        }
        else if(obj.Kind==CreationObjectKind.Particle)
        {
            if(obj.ParticleEffect is {} effect)
            {Hint(N("emitter-count")+": "+effect.AllNodes().Count());if(Button(ContentLabel("p.overview")))NavigateContent("p.overview");}
            else if(Button(L("creation-particle-convert")))Session.Edit(L("creation-particle-convert"),_=>obj.ParticleEffect=CreationParticleFields.ConvertLegacy(obj));
        }
        else if(Button(ContentLabel("object.type")))NavigateContent("object.type");
        Hint(N("overview-help"));
    }
    private void ResetContentLayout()
    {
        toolsPanel=properties=browser=contentPanel=timeline=true;
        view=bonesPanel=ikPanel=actionsPanel=particlesPanel=false;
        host.ResetLayout();
    }
    private bool ObjectSectionVisible(string section)=>!drawingContentInspector||contentView.Section=="object."+section;
    private bool LegacySectionVisible(string section)=>!drawingContentInspector||contentView.Section=="legacy."+section;
    private void FocusContentField(string field)
    {
        if(contentField.Length==0 || contentField!=field)return;
        ImGui.SetScrollHereY(.15f);contentField="";
        ImGui.TextColored(new Vector4(1,.75f,.3f,1),N("search-match"));
    }
}
