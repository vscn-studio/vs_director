using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationPreferences(object workspace,DirectorCreationSession session)
    {
        var type=workspace.GetType();var io=ImGui.GetIO();var display=io.DisplaySize;
        float originalScale=RuntimeEnv.GUIScale;string locale=Lang.CurrentLocale;
        object? Call(string method,params object[] args)=>AccessTools.Method(type,method).Invoke(workspace,args);
        var prefs=AccessTools.Field(type,"editorPreferences").GetValue(workspace)!;
        var overlay=(CreationOverlayPreferences)AccessTools.Property(type,"Overlay").GetValue(workspace)!;
        try
        {
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float scale in new[]{1f,1.5f,2f})
            foreach(float width in new[]{360f,780f})
            {
                LoadCreationProbeLanguage(language);RuntimeEnv.GUIScale=scale;io.DisplaySize=new(1600,1200);
                for(int category=0;category<5;category++)
                for(int frame=0;frame<3;frame++)
                {
                    ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,700));
                    ImGui.Begin("##preferences-field-check",ImGuiWindowFlags.NoSavedSettings);
                    ImGui.SetWindowFontScale(scale);Call("DrawPreferenceCategory",category);
                    if(frame==2&&ImGui.GetScrollMaxX()>1)throw new Exception($"Preferences overflow: {language} {scale} {width} category {category}");
                    ImGui.End();ImGui.Render();
                }
            }
            RuntimeEnv.GUIScale=1;io.DisplaySize=new(960,640);
            // Click the real first overlay checkbox; no direct callback invocation.
            Vector2 click=default;
            void Frame(bool locate=false)
            {
                ImGui.NewFrame();ImGui.SetNextWindowPos(new(30,40));ImGui.SetNextWindowSize(new(500,500));
                ImGui.Begin("##preferences-pointer",ImGuiWindowFlags.NoSavedSettings);
                if(locate)click=ImGui.GetCursorScreenPos()+new Vector2(8,8);
                Call("DrawPreferenceCategory",1);ImGui.End();ImGui.Render();
            }
            overlay.ObjectPoints=false;Frame(true);Frame(true);io.MousePos=click;Frame();io.MouseDown[0]=true;Frame();io.MouseDown[0]=false;Frame();
            if(!overlay.ObjectPoints)throw new Exception("Preference checkbox ignored pointer click");
            Call("PersistEditorPreferences");
            string json=System.Text.Json.JsonSerializer.Serialize(prefs,prefs.GetType(),CreationJson.Options);
            var restored=System.Text.Json.JsonSerializer.Deserialize(json,prefs.GetType(),CreationJson.Options)!;
            AccessTools.Field(type,"editorPreferences").SetValue(workspace,restored);Call("RestoreEditorPreferences");
            if(!((CreationOverlayPreferences)AccessTools.Property(type,"Overlay").GetValue(workspace)!).ObjectPoints)
                throw new Exception("Overlay preference lost on reopen");
            var defaults=(CreationProjectDefaults)AccessTools.Property(restored.GetType(),"Defaults").GetValue(restored)!;
            defaults.Duration=17;var saved=CreationJson.Clone(session.Project!);
            Call("ResetPreferenceCategory",1);
            if(defaults.Duration!=17||session.Project!.Duration!=saved.Duration)throw new Exception("Category reset changed another category or current project");
            // Render the actual category window, including narrow layout and search.
            foreach(string search in new[]{"","IK"})
            {
                Call("OpenPreferences",1);AccessTools.Field(type,"preferencesSearch").SetValue(workspace,search);
                for(int i=0;i<3;i++){ImGui.NewFrame();Call("DrawPreferences");ImGui.Render();}
            }
            var empty=new DirectorCreationProject();
            var prior=session.Project;
            AccessTools.Field(typeof(DirectorCreationSession),"<Project>k__BackingField").SetValue(session,null);
            try{ImGui.NewFrame();Call("DrawPreferences");ImGui.Render();}
            finally{AccessTools.Field(typeof(DirectorCreationSession),"<Project>k__BackingField").SetValue(session,prior);}
            Console.WriteLine("PASS bilingual creation preferences: pointer edit, category reset, persistence, no-project access, search, narrow fields and 100/150/200% scale");
        }
        finally
        {
            AccessTools.Field(type,"preferencesOpen").SetValue(workspace,false);
            AccessTools.Field(type,"preferencesDirty").SetValue(workspace,false);
            AccessTools.Field(type,"preferencesSearch").SetValue(workspace,"");
            AccessTools.Field(type,"editorPreferences").SetValue(workspace,prefs);
            overlay.ObjectPoints=false;Call("RestoreEditorPreferences");
            io.MouseDown[0]=false;io.DisplaySize=display;RuntimeEnv.GUIScale=originalScale;Lang.ChangeLanguage(locale);
        }
    }
}
