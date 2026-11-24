using System.Numerics;
using ImGuiNET;
using ImGuizmoNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private bool preferencesOpen,preferencesDirty;
    private int preferencesCategory;
    private string preferencesSearch="";
    private DateTime preferencesSaveAt;
    private CreationOverlayPreferences Overlay=>editorPreferences.Overlay;
    private static readonly string[] PreferencePages = ["creation-pref-operation", "creation-pref-overlay",
        "creation-pref-timeline", "creation-pref-layout", "creation-new-defaults"];
    private static readonly int[] PreferenceIndices = [0, 1, 2, 3, 4];
    private static readonly string[] PreferenceIcons = ["adjustments", "eye", "clock", "layout-grid", "movie"];
    private bool resetPreferenceScroll;
    private void SelectPreferenceCategory(int category)
    {
        preferencesCategory = category;
        preferencesSearch = "";
        resetPreferenceScroll = true;
    }
    private void OpenPreferences(int category)
    {
        SelectPreferenceCategory(category);
        preferencesOpen = true;
    }
    private void PreferenceChanged()
    {preferencesDirty=true;preferencesSaveAt=DateTime.UtcNow.AddMilliseconds(500);Runtime.NewProjectDefaults=editorPreferences.Defaults;}
    private void DrawPreferences()
    {
        if(preferencesDirty && (!preferencesOpen || DateTime.UtcNow>=preferencesSaveAt))
        {Run(PersistEditorPreferences);preferencesDirty=false;}
        if(!preferencesOpen)return;
        DirectorImGuiBrowser.SettingsWindow();
        if(ImGui.Begin(L("creation-preferences")+"###creation-preferences",ref preferencesOpen,
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            using var spacing = DirectorImGuiBrowser.Use();
            ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.InputTextWithHint("##preferences-search", L("creation-search-names"), ref preferencesSearch, 128))
                resetPreferenceScroll = true;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) DirectorImGuiTooltip.Show(L("creation-preferences-scope"));
            ImGui.Separator();
            bool locked = Runtime.Exporting || Runtime.PerformanceActive || gizmoUsing;
            float footer = ImGui.GetFrameHeightWithSpacing() + 2 * ImGui.GetStyle().ItemSpacing.Y;
            ImGui.BeginChild("preferences-content", new Vector2(0, -footer));
            DirectorImGuiBrowser.Categories(PreferenceIndices, preferencesCategory, i => L(PreferencePages[i]),
                i => PreferenceIcons[i], SelectPreferenceCategory);
            ImGui.BeginChild("settings-fields", Vector2.Zero);
            if (resetPreferenceScroll) { ImGui.SetScrollY(0); resetPreferenceScroll = false; }
            ImGui.BeginDisabled(locked);
            foreach (int i in PreferenceIndices)
            {
                if (preferencesSearch.Length == 0 && i != preferencesCategory) continue;
                ImGui.SeparatorText(L(PreferencePages[i]));
                DrawPreferenceCategory(i);
            }
            ImGui.EndDisabled();
            ImGui.EndChild();
            ImGui.EndChild();
            ImGui.Separator();
            ImGui.BeginDisabled(locked || preferencesSearch.Length != 0);
            if (Button(L("creation-pref-reset-category"))) ResetPreferenceCategory(preferencesCategory);
            ImGui.EndDisabled();
        }
        ImGui.End();
        if(!preferencesOpen && preferencesDirty){Run(PersistEditorPreferences);preferencesDirty=false;}
    }
    private bool PrefMatches(string key)=>preferencesSearch.Length==0||L(key).Contains(preferencesSearch,StringComparison.OrdinalIgnoreCase);
    private void PrefBool(string key,bool value,Action<bool> write)
    {if(PrefMatches(key)&&Checkbox(L(key),ref value)){write(value);PreferenceChanged();}}
    private void PrefNumber(string key,double value,Action<double> write,double min,double max,float step=1)
    {
        if(!PrefMatches(key))return;ImGui.TextWrapped(L(key));ImGui.SetNextItemWidth(-1);
        if(DirectorImGuiNumeric.Double("##pref-"+key,ref value,step,min,max)){write(value);PreferenceChanged();}
    }
    private void PrefText(string key,string value,Action<string> write)
    {if(!PrefMatches(key))return;ImGui.TextWrapped(L(key));ImGui.SetNextItemWidth(-1);if(ImGui.InputText("##pref-"+key,ref value,2048)){write(value);PreferenceChanged();}}
    private void PrefChoice(string key,int value,string[] labels,Action<int> write)
    {
        if(!PrefMatches(key))return;ImGui.TextWrapped(L(key));ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo("##pref-"+key,labels[Math.Clamp(value,0,labels.Length-1)]))
        {for(int i=0;i<labels.Length;i++)if(ImGui.Selectable(labels[i],value==i)){write(i);PreferenceChanged();}ImGui.EndCombo();}
    }
    private void PrefColor(string key,uint color,Action<uint> write)
    {
        if(!PrefMatches(key))return;var value=ImGui.ColorConvertU32ToFloat4(color);var rgb=new Vector3(value.X,value.Y,value.Z);
        ImGui.TextWrapped(L(key));ImGui.SetNextItemWidth(-1);
        if(ImGui.ColorEdit3("##pref-"+key,ref rgb)){write(ImGui.ColorConvertFloat4ToU32(new(rgb,1)));PreferenceChanged();}
    }
    private void DrawPreferenceCategory(int category)
    {
        if(category==0)
        {
            string[] tools=[L("creation-select-tool"),L("creation-move"),L("creation-rotation"),L("creation-scale"),L("creation-pull")];
            PrefChoice("creation-pref-object-tool",(int)objectTool,tools[..4],i=>{objectTool=(CreationTool)i;if(!PartScope)RestoreTool(false);});
            PrefChoice("creation-pref-part-tool",(int)partTool,tools,i=>{partTool=(CreationTool)i;if(PartScope)RestoreTool(true);});
            string[] spaces=[L("creation-world-axes"),L("creation-local-axes")];
            PrefChoice("creation-pref-object-space",objectSpace==MODE.LOCAL?1:0,spaces,i=>{objectSpace=i==0?MODE.WORLD:MODE.LOCAL;if(!PartScope)transformMode=objectSpace;});
            PrefChoice("creation-pref-part-space",partSpace==MODE.LOCAL?1:0,spaces,i=>{partSpace=i==0?MODE.WORLD:MODE.LOCAL;if(PartScope)transformMode=partSpace;});
            int[] axes=[-1,6,0,1,2];string[] axisNames=[L("creation-all-axes"),L("creation-screen-axis"),"X","Y","Z"];
            PrefChoice("creation-pref-object-axis",Array.IndexOf(axes,objectRotationAxis),axisNames,i=>objectRotationAxis=axes[i]);
            PrefChoice("creation-pref-part-axis",Array.IndexOf(axes,poseRotationAxis),axisNames,i=>poseRotationAxis=axes[i]);
            PrefBool("creation-auto-key",Project is null?editorPreferences.AutoKey:Session.AutoKey,v=>{editorPreferences.AutoKey=v;Continue(()=>Session.AutoKey=v);});
            PrefBool("creation-pose-exact-parts",exactPoseParts,v=>exactPoseParts=v);
            PrefBool("creation-free-pose-transform",freePoseTransform,v=>{freePoseTransform=v;if(!v&&PartScope)ChooseTool(CreationTool.Rotate);});
            PrefBool("creation-extended-chain",extendedPullChain,v=>extendedPullChain=v);
        }
        if(category==1)
        {
            PrefBool("creation-pref-object-points",Overlay.ObjectPoints,v=>Overlay.ObjectPoints=v);
            PrefBool("creation-pref-empty-hands",Overlay.EmptyHands,v=>Overlay.EmptyHands=v);
            PrefBool("creation-pref-bone-points",showBones,v=>showBones=v);
            PrefBool("creation-pref-pose-handles",Overlay.PoseHandles,v=>Overlay.PoseHandles=v);
            PrefBool("creation-pref-bone-lines",Overlay.BoneLines,v=>Overlay.BoneLines=v);
            PrefBool("creation-show-all-bones",showAllBones,v=>showAllBones=v);
            PrefBool("creation-pref-ik-targets",Overlay.IkTargets,v=>Overlay.IkTargets=v);
            PrefBool("creation-pref-ik-poles",Overlay.IkPoles,v=>Overlay.IkPoles=v);
            PrefBool("creation-pref-ik-relations",Overlay.IkRelations,v=>Overlay.IkRelations=v);
            PrefBool("creation-pref-ik-tips",Overlay.IkTips,v=>Overlay.IkTips=v);
            PrefBool("creation-pref-all-chains",Overlay.AllChains,v=>Overlay.AllChains=v);
            PrefBool("creation-composition-guides",Runtime.Guides,v=>Runtime.Guides=v);
            PrefNumber("creation-pref-marker-size",Overlay.Size,v=>Overlay.Size=(float)v,4,16,.5f);
            PrefNumber("creation-pref-opacity",Overlay.Opacity,v=>Overlay.Opacity=(float)v,.1,1,.05f);
            PrefColor("creation-pref-target-color",Overlay.TargetColor,v=>Overlay.TargetColor=v);
            PrefColor("creation-pref-pole-color",Overlay.PoleColor,v=>Overlay.PoleColor=v);
            PrefColor("creation-pref-bone-color",Overlay.BoneColor,v=>Overlay.BoneColor=v);
        }
        if(category==2)
        {
            var defaults=editorPreferences.DefaultTimeline;
            ImGui.TextWrapped(L("creation-pref-timeline-note"));
            PrefChoice("creation-pref-track-filter",defaults.Filter,[L("creation-pref-filter-all"),L("creation-properties"),L("creation-pose"),L("creation-pref-filter-clips")],i=>defaults.Filter=i);
            PrefBool("creation-pref-selected-tracks",defaults.SelectedOnly,v=>defaults.SelectedOnly=v);
            PrefBool("creation-pref-keyed-tracks",defaults.KeyedOnly,v=>defaults.KeyedOnly=v);
            PrefBool("creation-pref-curves",defaults.Curves,v=>defaults.Curves=v);
        }
        if(category==3)
        {
            PrefBool("creation-tools",toolsPanel,v=>toolsPanel=v);PrefBool("creation-object-list",browser,v=>{browser=v;if(v)ShowCreationPanel("browser");});
            PrefBool("creation-properties",properties,v=>{properties=v;if(v)ShowCreationPanel("properties");});PrefBool("creation-creation-timeline",timeline,v=>timeline=v);
            PrefBool("creation-view",view,v=>view=v);PrefBool("creation-bones-tab",bonesPanel,v=>bonesPanel=v);
            PrefBool("creation-pref-ik-panel",ikPanel,v=>ikPanel=v);PrefBool("creation-actions-tab",actionsPanel,v=>actionsPanel=v);
            PrefBool("creation-nav-content",contentPanel,v=>{contentPanel=v;if(v)ShowCreationPanel("content");});
            if(Button(L("creation-reset-layout")))ResetContentLayout();
        }
        if(category==4)
        {
            var defaults=editorPreferences.Defaults;var output=defaults.Output;
            ImGui.TextWrapped(L("creation-pref-defaults-note"));
            PrefNumber("creation-pref-duration-seconds",defaults.Duration,v=>defaults.Duration=v,DirectorCreationTiming.FrameDuration,86400,.1f);
            PrefChoice("creation-timeline-display",(int)defaults.TimeDisplay,[L("creation-display-frames"),L("creation-display-time")],i=>defaults.TimeDisplay=(CreationTimeDisplay)i);
            PrefBool("creation-loop-animation",defaults.Loop,v=>defaults.Loop=v);
            PrefNumber("creation-width",output.Width,v=>output.Width=(int)v/2*2,16,8192,2);
            PrefNumber("creation-height",output.Height,v=>output.Height=(int)v/2*2,16,8192,2);
            PrefNumber("creation-frame-rate",output.Fps,v=>output.Fps=(int)v,1,240);
            PrefNumber("creation-quality-crf",output.Quality,v=>output.Quality=(int)v,0,51);
            PrefText("creation-encoder",output.Encoder,v=>output.Encoder=v);PrefText("creation-output-directory",output.Directory,v=>output.Directory=v);
            PrefBool("creation-mix-background-replay-audio",output.SceneAudio,v=>output.SceneAudio=v);
            PrefBool("creation-include-textures-in-model-export",exportTextures,v=>{exportTextures=v;defaults.ExportTextures=v;});
        }
    }
    private void ResetPreferenceCategory(int category)
    {
        if(category==0){objectTool=CreationTool.Move;partTool=CreationTool.Rotate;objectSpace=MODE.WORLD;partSpace=MODE.LOCAL;objectRotationAxis=poseRotationAxis=-1;exactPoseParts=freePoseTransform=extendedPullChain=false;editorPreferences.AutoKey=true;Continue(()=>Session.AutoKey=true);RestoreTool(PartScope);}
        if(category==1){editorPreferences.Overlay=new();showBones=showAllBones=false;Runtime.Guides=true;}
        if(category==2)editorPreferences.DefaultTimeline=new();
        if(category==3){toolsPanel=browser=properties=timeline=view=true;particlesPanel=bonesPanel=ikPanel=actionsPanel=false;host.ResetLayout();}
        if(category==4){editorPreferences.Defaults=new();exportTextures=true;}
        PreferenceChanged();
    }
    private readonly Dictionary<Guid,bool> attachmentContent=new();
    private long attachmentRevision=-1;
    private DirectorCreationProject? attachmentProject;
    private int attachmentPerformances;
    private bool ShowAttachment(CreationSceneObject obj)
    {
        if(obj.ActorHand is not (1 or 2)||Overlay.EmptyHands||obj.Id==Session.SelectedObject)return true;
        if(Project is not {} p)return false;
        if(attachmentRevision!=Session.Revision||!ReferenceEquals(attachmentProject,p)||attachmentPerformances!=p.Performances.Count)
        {attachmentContent.Clear();attachmentRevision=Session.Revision;attachmentProject=p;attachmentPerformances=p.Performances.Count;}
        if(!attachmentContent.TryGetValue(obj.Id,out bool result))attachmentContent[obj.Id]=result=DirectorCreationAttachments.HasContent(p,obj);
        return result;
    }
}
