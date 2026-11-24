using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private string particlePresetCategory="",particlePresetSelected="";
    private readonly Dictionary<string,CreationParticleEffect> particleExamples=new();
    private void DrawParticlePresetPicker()
    {
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##preset-picker-search",P("search-presets"),ref particlePresetSearch,128);
        particleLibraryCache??=ParticleLibrary.List();
        var entries=new List<(string Id,string Name,string Tags,bool Favorite,CreationParticleEffect Effect)>();
        foreach(string name in DirectorParticlePresetLibrary.Examples)
        {
            if(!particleExamples.TryGetValue(name,out var example))particleExamples[name]=example=DirectorParticlePresetLibrary.Example(name);
            entries.Add(("example/"+name,P("example-"+name),name,false,example));
        }
        entries.AddRange(particleLibraryCache.Select(p=>("preset/"+p.Id,p.Name,p.Tags,p.Favorite,p.Effect)));
        float height=PickerBodyHeight();
        ImGui.BeginChild("preset-categories",new(PickerSidebarWidth(),height),true);
        foreach(string category in new[]{"","examples","saved","favorites"})
            if(ImGui.Selectable(P("library-"+(category.Length==0?"all":category)),particlePresetCategory==category))particlePresetCategory=category;
        ImGui.EndChild();ImGui.SameLine();
        ImGui.BeginChild("preset-grid-body",new(0,height),false);
        bool grid=BeginPickerGrid("preset-grid");var work=Stopwatch.StartNew();
        if(grid)foreach(var entry in entries.Where(e=>(particlePresetCategory!="examples"||e.Id.StartsWith("example/"))
            &&(particlePresetCategory!="saved"||e.Id.StartsWith("preset/"))&&(particlePresetCategory!="favorites"||e.Favorite)
            &&(e.Name+" "+e.Tags).Contains(particlePresetSearch,StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.TableNextColumn();float size=Math.Max(1,ImGui.GetContentRegionAvail().X);
            if(!ImGui.IsRectVisible(new(size))){ImGui.Dummy(new(size));continue;}
            var min=ImGui.GetCursorScreenPos();var max=min+new Vector2(size);var previewMax=max-new Vector2(0,ImGui.GetTextLineHeightWithSpacing()+8);
            ImGui.InvisibleButton(entry.Id,new(size));bool hovered=ImGui.IsItemHovered();
            if(ImGui.IsItemClicked())particlePresetSelected=entry.Id;
            if(!particlePreviews.TryGetValue(entry.Id,out var preview)&&work.ElapsedMilliseconds<4)
            {
                preview=new(entry.Effect);particlePreviews[entry.Id]=preview;
                // Embedded textures are read directly; browsing never writes project assets.
                var project=new DirectorCreationProject();preview.TextureSource=source=>Runtime.Renderer.ParticleTexture(project,source,entry.Effect.Textures.GetValueOrDefault(source));
            }
            if(preview is not null)
            {
                preview.LastFrame=ImGui.GetFrameCount();preview.Draw(ImGui.GetWindowDrawList(),min,previewMax);
                if(hovered&&preview.Error.Length>0)DirectorImGuiTooltip.Show(preview.Error);
            }
            PickerCardLabel(ImGui.GetWindowDrawList(),min,max,previewMax.Y,entry.Name,particlePresetSelected==entry.Id,hovered);
        }
        EndPickerGrid(grid);ImGui.EndChild();
        work.Restart();
        foreach(var preview in particlePreviews.Values.Where(p=>p.LastFrame==ImGui.GetFrameCount()).OrderBy(p=>p.LastAdvancedFrame))
        {if(work.ElapsedMilliseconds>=4)break;preview.Advance();preview.LastAdvancedFrame=ImGui.GetFrameCount();}
        foreach(var key in particlePreviews.Where(p=>p.Value.LastFrame!=ImGui.GetFrameCount()).OrderByDescending(p=>p.Value.LastFrame).Skip(48).Select(p=>p.Key).ToArray())
        {particlePreviews[key].Dispose();particlePreviews.Remove(key);}
        var selected=entries.FirstOrDefault(e=>e.Id==particlePresetSelected);
        EllipsizedParticleText(error.Length>0?error:selected.Name??L("creation-particle-pick-hint"),ImGui.GetContentRegionAvail().X);
        BeginPickerActions();ImGui.BeginDisabled(selected.Effect is null);
        if(PickerAction("creation-particle-import-selected",true))LoadParticleLibraryEffect(new(){Kind=CreationObjectKind.Particle,Name=selected.Name??"Effect",ParticleStart=Session.Time,Transform=new(){Position=Runtime.CursorPosition()}},selected.Effect!,true);
        ImGui.EndDisabled();
        if(PickerAction("creation-particle-blank")){AddObject(CreationObjectKind.Particle);auxiliary="";}
        if(PickerAction("creation-cancel"))auxiliary="";
        EndPickerActions();
    }
}
