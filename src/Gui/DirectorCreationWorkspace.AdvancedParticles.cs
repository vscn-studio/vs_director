using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Runtime;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Guid particleEmitter;
    private string particleSearch="",particleJson="",particleJsonId="";
    private int particleScope;
    private string particleResourceId="";
    private void DrawAdvancedParticleEditor(CreationSceneObject obj)
    {
        var effect=obj.ParticleEffect!;
        var emitter=effect.AllNodes().FirstOrDefault(e=>e.Id==particleEmitter)??effect.Emitters.FirstOrDefault();
        ImGui.TextWrapped(obj.Name+" / "+(emitter?.Name??""));
        if(effect.Version<3)
        {
            Hint(P("upgrade-hint"));
            if(Button(P("upgrade")))Session.Edit(P("upgrade"),_=>effect.Upgrade());
        }
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##parameter-search",P("search"),ref particleParameterSearch,256);
        string[] filters=[P("filter-all"),P("filter-pinned"),P("filter-modified"),P("filter-driven")];
        if(Combo(P("filter"),filters[particleFieldFilter])){for(int i=0;i<filters.Length;i++)if(ImGui.Selectable(filters[i],particleFieldFilter==i))particleFieldFilter=i;ImGui.EndCombo();}
        if(emitter is null)return;
        if(ParticleTargets(effect,emitter).Skip(1).Any())Hint(P("mixed-values"));
        particleEmitter=emitter.Id;
        if(particlePage=="files"){DrawParticleFiles(obj);return;}
        if(particlePage=="json")
        {
            string id=obj.Id+"/"+emitter.Id;
            if(particleJsonId!=id){particleJsonId=id;particleJson=emitter.Properties.ToJsonString(CreationJson.Options);}
            if(Button(L("creation-particle-json-refresh")))particleJson=emitter.Properties.ToJsonString(CreationJson.Options);
            ImGui.InputTextMultiline("##particle-json",ref particleJson,1024*1024,new(-1,Math.Max(80,ImGui.GetContentRegionAvail().Y-45)));
            if(Button(L("creation-apply")))Run(()=>{var node=JsonNode.Parse(particleJson) as JsonObject??throw new InvalidDataException("Expected particle object.");CreationParticleFields.Validate(node);Session.Edit(L("creation-apply"),_=>SetParticleNode(effect,emitter,[],node));});
            return;
        }
        if(particlePage=="modules") {DrawParticleModules(obj,emitter);return;}
        if(particlePage=="common")
        {
            TextField(L("creation-name"),emitter.Name,v=>emitter.Name=v);
            string[] scopes=[L("creation-particle-scope-emitter"),L("creation-particle-scope-variant"),L("creation-particle-scope-family")];
            if(Combo(L("creation-particle-edit-scope"),scopes[particleScope])){for(int i=0;i<scopes.Length;i++)if(ImGui.Selectable(scopes[i]))particleScope=i;ImGui.EndCombo();}
            Hint(P("targets")+": "+ParticleTargets(effect,emitter).Count());
            Number(P("game-speed"),effect.GameSpeed,v=>effect.GameSpeed=v,.05,200,.05f);
            Number(P("density"),effect.Density,v=>effect.Density=v,0,100,.05f);
        }
        void Edit(Action<CreationParticleEmitter> set){foreach(var e in ParticleTargets(effect,emitter))set(e);}
        if(particlePage is "common" or "emission" && effect.Emitters.Contains(emitter))
        {
            TimeNumber(L("creation-start-time"),emitter.Start,v=>Edit(e=>e.Start=v),0,86400);
            TimeNumber(L("creation-emission-duration"),emitter.Duration,v=>Edit(e=>e.Duration=v),0,86400);
            Number(L("creation-particle-interval"),emitter.Interval,v=>Edit(e=>e.Interval=v),.001,86400,.01f);
            Toggle(L("creation-particle-burst"),emitter.Burst,v=>Edit(e=>e.Burst=v));
        }
        if(particlePage=="emission")
        {
            VectorField(L("creation-particle-offset"),emitter.Offset,v=>Edit(e=>e.Offset=v),.01f);
            Toggle(L("creation-particle-local-follow"),emitter.LocalFollow,v=>Edit(e=>e.LocalFollow=v));
            Number(L("creation-particle-parent-velocity"),emitter.ParentVelocityWeight,v=>Edit(e=>e.ParentVelocityWeight=v),0,1000,.01f);
            TimeNumber(P("effect-start"),obj.ParticleStart,v=>obj.ParticleStart=v,0,86400);
            TimeNumber(P("effect-duration"),obj.ParticleDuration,v=>obj.ParticleDuration=v,0,86400);
            Number(L("creation-particle-seed"),effect.Seed,v=>effect.Seed=(int)v,0,int.MaxValue,1);
        }
        if(particlePage is "common" or "appearance")DrawParticleColor(effect,emitter);
        DrawNativeParticleFields(effect,emitter,emitter.Properties,[],0);
        if(particlePage=="appearance")
        {
            TextField(L("creation-particle-texture"),emitter.Texture,v=>Edit(e=>e.Texture=v.Trim()));
            if(Button(L("creation-particle-import-texture")))FileDialog(L("creation-particle-import-texture"),file=>{Session.Edit(L("creation-particle-import-texture"),p=>{var source=Session.Store.Import(p,file,CreationAssetKind.Texture).Source;Edit(e=>e.Texture=source);});Runtime.Renderer.ReloadTextures();});
            Toggle(L("creation-particle-soft"),emitter.SoftEdge,v=>Edit(e=>e.SoftEdge=v));
            Toggle(L("creation-particle-additive"),emitter.Additive,v=>Edit(e=>e.Additive=v));
            DrawParticleFlags(effect,emitter);
        }
        if(particlePage=="children")DrawParticleChildActions(effect,emitter);
    }
    private void DrawParticleFiles(CreationSceneObject obj)
    {
        var effect=obj.ParticleEffect!;
        var emitter=effect.AllNodes().FirstOrDefault(e=>e.Id==particleEmitter)??effect.Emitters.FirstOrDefault();
        foreach(var issue in ParticleCompatibility.Issues(effect))Hint(issue);
        if(emitter is not null)
        {
            if(Button(L("creation-particle-restore-original")))Session.Edit(L("creation-particle-restore-original"),_=>{foreach(var e in ParticleTargets(effect,emitter)){e.Properties=(JsonObject)(e.Original??CreationParticleFields.Defaults()).DeepClone();e.Children.Clear();e.Modules=effect.Version>=3?new():null;e.Legacy=null;if(effect.Version>=3)e.Upgrade();}});
            Runtime.ParticleResources.Start();
            var source=Runtime.ParticleResources.Entries.FirstOrDefault(r=>r.Id==emitter.Source);
            if(source is not null)
            {
                IEnumerable<DirectorParticleResource> Targets()=>Runtime.ParticleResources.Entries.Where(r=>particleScope==0?r.Id==source.Id:r.Index==source.Index&&(particleScope==1?r.Family==source.Family&&r.Variant==source.Variant:r.Family==source.Family));
                if(Button(L("creation-particle-apply-client")))Run(()=>Runtime.ParticleOverrides.Apply(Targets(),obj,emitter));
                if(Button(L("creation-particle-restore-client")))Run(()=>Runtime.ParticleOverrides.Restore(Targets()));
            }
        }
        if(Runtime.ParticleOverrides.Count>0 && Button(L("creation-particle-restore-all")))Run(()=>Runtime.ParticleOverrides.Restore());
        if(Button(L("creation-particle-import")))FileDialog(L("creation-particle-import"),file=>
        {
            var imported=CreationParticleFields.Import(File.ReadAllText(file));
            Session.Edit(L("creation-particle-import"),p=>
            {
                foreach(var pair in imported.Textures)
                {
                    string extension=Path.GetExtension(pair.Key).ToLowerInvariant();
                    if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Unsupported texture.");
                    var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileName(pair.Key)};
                    asset.Source="assets/"+asset.Id.ToString("N")+extension;
                    string destination=Session.Store.AssetPath(p.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,pair.Value);p.Assets.Add(asset);
                    foreach(var e in imported.AllNodes().Where(e=>e.Texture==pair.Key))e.Texture=asset.Source;
                }
                imported.Textures.Clear();obj.ParticleEffect=imported;
                obj.ParticleDuration=Math.Max(obj.ParticleDuration,imported.Emitters.Select(e=>e.Start+e.Duration).DefaultIfEmpty(0).Max());
            });Runtime.Renderer.ReloadTextures();
        });
        SameLineIfFits(L("creation-particle-export"));
        if(Button(L("creation-particle-export")))FileDialog(L("creation-particle-export"),file=>
        {
            var copy=CreationJson.Clone(effect);
            foreach(var e in copy.AllNodes())if(e.Texture.StartsWith("assets/",StringComparison.Ordinal))copy.Textures[e.Texture]=File.ReadAllBytes(Session.Store.AssetPath(Project!.Id,e.Texture));
            copy.Validate();DirectorCreationStore.AtomicWrite(file,JsonSerializer.Serialize(copy,CreationJson.Options));
        },"particle.director.json");
        if(Button(L("creation-particle-export-native")))FileDialog(L("creation-particle-export-native"),file=>DirectorCreationStore.AtomicWrite(file,CreationParticleFields.ExportNative(effect)),"particle.native.json");
        if(Button(P("export-compatible")))FileDialog(P("export-compatible"),file=>DirectorCreationStore.AtomicWrite(file,CreationParticleFields.ExportNative(effect,true)),"particle.compatible.json");
        Hint(L("creation-particle-native-export-hint"));
        DrawParticleLibrary(obj);
    }
    private IEnumerable<CreationParticleEmitter> ParticleTargets(CreationParticleEffect effect,CreationParticleEmitter emitter)
        =>effect.AllNodes().Where(e=>e==emitter||particleMulti.Contains(e.Id)||particleScope>0&&emitter.Source.Length>0&&e.Family==emitter.Family
            &&(particleScope==2||e.Variant==emitter.Variant));
    private static JsonNode? ParticleNodeAt(JsonObject root,IReadOnlyList<string> path)
    {
        JsonNode? node=root;foreach(string key in path)node=node is JsonObject o?CreationParticleFields.Find(o,key):node is JsonArray a&&int.TryParse(key,out int i)&&i<a.Count?a[i]:null;return node;
    }
    private void SetParticleNode(CreationParticleEffect effect,CreationParticleEmitter emitter,string[] path,JsonNode? value)
    {
        foreach(var target in ParticleTargets(effect,emitter))
        {
            if(path.Length==0){target.Properties=(JsonObject)value!.DeepClone();target.Legacy=null;if(effect.Version>=3){target.Children.Clear();target.Upgrade();}continue;}
            JsonNode? parent=ParticleNodeAt(target.Properties,path[..^1]);
            if(parent is JsonObject o)CreationParticleFields.Set(o,path[^1],value?.DeepClone());
            else if(parent is JsonArray a&&int.TryParse(path[^1],out int index)&&index<a.Count)a[index]=value?.DeepClone();
            else continue;
            target.Legacy=null;
        }
    }
    private void DrawNativeParticleFields(CreationParticleEffect effect,CreationParticleEmitter emitter,JsonObject properties,string[] path,int depth)
    {
        foreach(string group in new[]{"emission","motion","appearance","environment","children"}.Where(g=>nativeContentSection is not null || particleFieldFilter>0 || particleParameterSearch.Length>0 || particlePage=="common" || g==particlePage))
        {
            ImGui.PushID(group);
            if((nativeContentSection is null && Header(L("creation-particle-"+group))) || nativeContentSection is not null)foreach(var field in CreationParticleFields.All.Where(f=>f.Group==group && (f.Kind!="children" || effect.Version<3) && f.Name!="VertexFlags" && (nativeContentSection is null || depth>0 || NativeContentSection(f.Name)==nativeContentSection) && (nativeContentSection is not null || particleFieldFilter==0 || particleFieldFilter==1&&particlePinned.Contains(f.Name) || particleFieldFilter==2&&!JsonNode.DeepEquals(CreationParticleFields.Find(properties,f.Name),CreationParticleFields.Find(emitter.Original??CreationParticleFields.Defaults(),f.Name)) || particleFieldFilter==3&&emitter.Modules?.Drivers.ContainsKey(f.Name)==true) && (nativeContentSection is not null || (particleParameterSearch.Length>0 ? (f.Name+" "+L("creation-particle-field-"+f.Name.ToLowerInvariant())).Contains(particleParameterSearch,StringComparison.OrdinalIgnoreCase) : particleFieldFilter>0 || particlePage!="common" || new[]{"Quantity","LifeLength","Size","Velocity","GravityEffect"}.Contains(f.Name)))))
            {
                ImGui.PushID(field.Name);FocusContentField(field.Name);var node=CreationParticleFields.Find(properties,field.Name);
                string label=L("creation-particle-field-"+field.Name.ToLowerInvariant());
                ImGui.TextWrapped((particlePinned.Contains(field.Name)?"★ ":"")+label);
                if(ImGui.IsItemHovered())DirectorImGuiTooltip.Show(field.Name+" · "+P("field-context"));
                if(ImGui.BeginPopupContextItem("field-options"))
                {
                    if(ImGui.MenuItem(P("pin"),"",particlePinned.Contains(field.Name))){if(!particlePinned.Add(field.Name))particlePinned.Remove(field.Name);}
                    if(emitter.Modules is not null && ParticleParameters.All.Any(p=>p.Id==field.Name) && ImGui.MenuItem(N("drivers")))
                    {particleDriver=field.Name;NavigateContent("p.drivers",emitter.Id);}
                    if(ImGui.MenuItem(P("restore-field")))Session.Edit(P("restore-field"),_=>SetParticleNode(effect,emitter,path.Append(field.Name).ToArray(),CreationParticleFields.Find(emitter.Original??CreationParticleFields.Defaults(),field.Name)));
                    ImGui.EndPopup();
                }
                var fieldPath=path.Append(field.Name).ToArray();
                void Set(JsonNode? n)=>SetParticleNode(effect,emitter,fieldPath,n);
                if(field.Kind=="bool")Toggle(label,node?.GetValue<bool>()??field.Default!=0,v=>Set(JsonValue.Create(v)));
                else if(field.Kind is "number" or "int")Number(label,CreationParticleFields.Number(properties,field.Name,field.Default),v=>Set(JsonValue.Create(field.Kind=="int"?(int)v:v)),-int.MaxValue,int.MaxValue,field.Kind=="int"?1:.01f);
                else if(field.Kind=="model")
                {
                    if(Combo(label,node?.ToString()??"Cube")){foreach(string m in new[]{"Cube","Quad"})if(ImGui.Selectable(m))Session.Edit(label,_=>Set(JsonValue.Create(m)));ImGui.EndCombo();}
                }
                else if(field.Kind=="children")
                {
                    ImGui.TextWrapped(label);
                    var children=node as JsonArray??new JsonArray();
                    for(int i=0;i<children.Count;i++)
                    {
                        ImGui.PushID(i);int index=i;
                        if(children[i] is JsonObject child && Header(label+" "+(i+1)))
                        {
                            if(Button(L("creation-duplicate")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.Add(child.DeepClone());Set(copy);});
                            SameLineIfFits(L("creation-delete"));
                            if(Button(L("creation-delete")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.RemoveAt(index);Set(copy);});
                            if(depth<8)DrawNativeParticleFields(effect,emitter,child,fieldPath.Append(i.ToString()).ToArray(),depth+1);
                        }
                        ImGui.PopID();
                    }
                    if(depth<8&&Button(L("creation-particle-add-child")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.Add(CreationParticleFields.Defaults());Set(copy);});
                }
                else
                {
                    bool enabled=node is not null && (node is not JsonArray array||array.Any(n=>n is not null));
                    if(field.Kind=="evolve")
                    {
                        if(Checkbox(L("creation-enabled"),ref enabled))Session.Edit(label,_=>Set(enabled?(field.Length>0?new JsonArray(Enumerable.Range(0,field.Length).Select(_=>(JsonNode)CreationParticleFields.Evolution()).ToArray()):CreationParticleFields.Evolution()):null));
                        if(!enabled){ImGui.PopID();continue;}
                    }
                    var nodes=field.Length>0?node as JsonArray:null;
                    if(field.Kind=="nat"&&field.Length==3)
                    {
                        CreationVector Read(string key)=>new(Get(0,key),Get(1,key),Get(2,key));
                        double Get(int i,string key)=>nodes?[i] is JsonObject n?CreationParticleFields.Number(n,key):key=="avg"?field.Default:0;
                        void Values(string key,CreationVector v)
                        {var a=nodes is null?new JsonArray(Enumerable.Range(0,3).Select(_=>(JsonNode)CreationParticleFields.Nat(field.Default)).ToArray()):(JsonArray)nodes.DeepClone();double[] values=[v.X,v.Y,v.Z];for(int i=0;i<3;i++)CreationParticleFields.Set((JsonObject)a[i]!,key,JsonValue.Create(values[i]));Set(a);}
                        VectorField(L("creation-particle-mean"),Read("avg"),v=>Values("avg",v),.01f,inline:true);
                        VectorField(L("creation-particle-variation"),Read("var"),v=>Values("var",v),.01f,inline:true);
                    }
                    for(int i=0;i<Math.Max(1,field.Length);i++)
                    {
                        ImGui.PushID(i);int index=i;
                        var n=(field.Length>0?nodes?[i]:node) as JsonObject ?? (field.Kind=="nat"?CreationParticleFields.Nat(field.Default):CreationParticleFields.Evolution());
                        void Part(string key,JsonNode? v)
                        {var copy=(JsonObject)n.DeepClone();CreationParticleFields.Set(copy,key,v);if(field.Length==0)Set(copy);else{var a=nodes is null?new JsonArray(Enumerable.Range(0,field.Length).Select(_=>(JsonNode)(field.Kind=="nat"?CreationParticleFields.Nat(field.Default):CreationParticleFields.Evolution())).ToArray()):(JsonArray)nodes.DeepClone();a[index]=copy;Set(a);}}
                        string suffix=field.Length==0?"":field.Name=="HsvaColor"?new[]{"H","S","V","A"}[i]:new[]{"X","Y","Z"}[i];
                        if(field.Kind=="nat" && field.Length!=3)
                        {Number(suffix+" "+L("creation-particle-mean"),CreationParticleFields.Number(n,"avg",field.Default),v=>Part("avg",JsonValue.Create(v)));Number(suffix+" "+L("creation-particle-variation"),CreationParticleFields.Number(n,"var"),v=>Part("var",JsonValue.Create(v)),0,1e6);}
                        if(field.Kind=="nat"&&CreationParticleFields.Find(n,"offset") is not null)Number(suffix+" offset",CreationParticleFields.Number(n,"offset"),v=>Part("offset",JsonValue.Create(v)));
                        string key=field.Kind=="nat"?"dist":"transform";string[] choices=field.Kind=="nat"?CreationParticleFields.Distributions:CreationParticleFields.Transforms;
                        if(Combo(suffix+" "+L("creation-particle-"+key),CreationParticleFields.Find(n,key)?.ToString()??choices[0]))
                        {foreach(string choice in choices)if(ImGui.Selectable(choice))Session.Edit(label,_=>Part(key,JsonValue.Create(choice)));ImGui.EndCombo();}
                        if(field.Kind=="evolve")
                        {
                            Number(L("creation-particle-factor"),CreationParticleFields.Number(n,"factor"),v=>Part("factor",JsonValue.Create(v)));
                            bool limited=CreationParticleFields.Find(n,"maxvalue") is not null;
                            if(Checkbox(L("creation-particle-maxvalue"),ref limited))Session.Edit(label,_=>Part("maxvalue",limited?JsonValue.Create(255):null));
                            if(limited)Number(L("creation-particle-maxvalue")+"##value",CreationParticleFields.Number(n,"maxvalue"),v=>Part("maxvalue",JsonValue.Create(v)));
                        }
                        ImGui.PopID();
                    }
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }
    }
}
