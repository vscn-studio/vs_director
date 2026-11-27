using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector;

internal static partial class DirectorImGuiProbe
{
    private static Vector2 voiceSelectMin, voiceSelectMax, voiceWindowMin, voiceWindowMax;
    private static float voiceScroll;

    private static bool OpenVoiceProbeDialog(object __instance, ref bool __result)
    {
        if (__instance.GetType().Name != "DirectorWorkspaceHud") return true;
        __result = true;
        return false;
    }

    private static void CaptureVoiceSelect(ReadOnlySpan<char> __0)
    {
        if (!__0.SequenceEqual(Lang.Get("vsdirector:voice-open-file-dialog"))) return;
        voiceSelectMin = ImGui.GetItemRectMin();
        voiceSelectMax = ImGui.GetItemRectMax();
        voiceWindowMin = ImGui.GetWindowPos();
        voiceWindowMax = voiceWindowMin + ImGui.GetWindowSize();
        voiceScroll = ImGui.GetScrollMaxY();
    }

    private static void CheckVoiceRecorderWindow(Assembly assembly, ICoreClientAPI api, Harmony harmony)
    {
        Type hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        Type recorderType = assembly.GetType("VSDirector.Voice.DirectorMicrophoneRecorder", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(hudType);
        void Set(string field, object value) => AccessTools.Field(hudType, field).SetValue(hud, value);
        void Call(string method) => AccessTools.Method(hudType, method).Invoke(hud, null);
        var owner = new VSDirectorModSystem();
        using var recorder = (IDisposable)Activator.CreateInstance(recorderType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [api], null)!;
        AccessTools.Field(typeof(VSDirectorModSystem), "microphoneRecorder").SetValue(owner, recorder);
        Set("owner", owner);
        Set("capi", api);
        Set("voiceAudioDirectory", Path.GetTempPath());
        foreach (string field in new[] { "voiceAudioPath", "voiceFileImportError", "voiceFileBrowserPath", "voiceFileCandidatePath" }) Set(field, "");
        harmony.Patch(AccessTools.Method(typeof(GuiDialog), "IsOpened"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(OpenVoiceProbeDialog)));
        harmony.Patch(AccessTools.Method(typeof(ImGui), "Button", [typeof(ReadOnlySpan<char>), typeof(Vector2)]),
            postfix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(CaptureVoiceSelect)));
        var io = ImGui.GetIO();
        void Frame()
        {
            ImGui.NewFrame();
            using ((IDisposable)AccessTools.Method(assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime", true)!, "UseFont").Invoke(null, [false])!)
            {
                ImGui.Begin("voice-probe-host");
                Call("DrawVoiceRecorderWindow");
                ImGui.End();
            }
            ImGui.Render();
        }
        foreach (string language in new[] { "zh-cn", "en" })
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            LoadCreationProbeLanguage(language);
            RuntimeEnv.GUIScale = scale;
            io.DisplaySize = new Vector2(1920, 1080);
            Call("OpenVoiceRecorder");
            for (int frame = 0; frame < 5; frame++) Frame();
            if (voiceSelectMin.X < voiceWindowMin.X || voiceSelectMax.X > voiceWindowMax.X - 1
                || voiceSelectMax.Y > voiceWindowMax.Y || voiceScroll > 1)
                throw new Exception($"Voice selector clipped or window scrolls at {language}/{scale}: {voiceSelectMin}/{voiceSelectMax}, window {voiceWindowMin}/{voiceWindowMax}, scroll {voiceScroll}.");
            var data = ImGui.GetDrawData();
            for (int list = 0; list < data.CmdListsCount; list++)
            for (int index = 0; index < data.CmdLists[list].VtxBuffer.Size; index++)
                if (data.CmdLists[list].VtxBuffer[index].col == 0xD9FFFFFF)
                    throw new Exception("Voice recorder still draws a white veil.");
            io.MousePos = (voiceSelectMin + voiceSelectMax) * .5f;
            Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (!(bool)AccessTools.Field(hudType, "voiceFileBrowserOpen").GetValue(hud)!)
                throw new Exception("Voice file selector did not open on click.");
            Set("voiceFileBrowserOpen", false);
            Set("voiceFileBrowserPopupRequested", false);
            ImGui.NewFrame(); ImGui.Begin("voice-probe-host");
            if (ImGui.BeginPopupModal(Lang.Get("vsdirector:voice-file-browser") + "###voice-file-browser"))
            {
                ImGui.CloseCurrentPopup(); ImGui.EndPopup();
            }
            ImGui.End(); ImGui.Render();
            Set("voiceRecorderWindowOpen", false);
            ImGui.NewFrame(); ImGui.Begin("voice-probe-host");
            if (ImGui.BeginPopupModal(Lang.Get("vsdirector:voice-recorder") + "###vsdirector-voice-recorder"))
            {
                ImGui.CloseCurrentPopup(); ImGui.EndPopup();
            }
            ImGui.End(); ImGui.Render();
            Set("voiceFileBrowserOpen", false);
            Console.WriteLine($"PASS voice recorder, unclipped selector, no veil and no scroll at {language}/{scale}.");
        }
        CheckClipGainGesture(assembly, api);
    }

    private static void CheckClipGainGesture(Assembly assembly, ICoreClientAPI api)
    {
        Type T(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        using var workspace = (IDisposable)Activator.CreateInstance(T("DirectorImGuiWorkspace"), api)!;
        Action<MouseEvent> mouse = _ => { };
        Action noop = () => { };
        using var view = (IDisposable)Activator.CreateInstance(T("DirectorTimelineWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, mouse, mouse, mouse, (Action<int, int, float, bool>)((_, _, _, _) => { }),
                (Action<KeyEvent>)(_ => { }), noop, noop, workspace], null)!;
        void Call(string name, params object[] args) => AccessTools.Method(view.GetType(), name).Invoke(view, args);
        RuntimeEnv.GUIScale = 1;
        var io = ImGui.GetIO();
        var gains = new List<double>();
        var moves = new List<double>();
        Call("Begin", 0d, 0d, 480d, 160d);
        Call("AudioGainLine", "gain", 0d, (Action<double>)gains.Add, 20d, 20d, 200d, 60d);
        Call("AudioTrimClip", "clip", 0d, 2d, 0d, 0d, 100d, 20d, 20d, 200d, 60d,
            (Action<double,double,double>)((start, _, _) => moves.Add(start)), noop, false);
        Vector2 origin = default;
        void Frame(int x, int y, bool held)
        {
            io.MousePos = origin + new Vector2(x, y);
            io.MouseDown[0] = held;
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new(10, 10)); ImGui.SetNextWindowSize(new(520, 200));
            ImGui.Begin("gain-gesture-probe", ImGuiWindowFlags.NoDecoration);
            origin = ImGui.GetCursorScreenPos();
            Call("DrawSurface");
            ImGui.End(); ImGui.Render();
        }
        Frame(80, 65, false); Frame(80, 65, false); Frame(80, 65, true);
        Frame(100, 50, true); Frame(120, 40, true); Frame(120, 40, false);
        if (gains.Count != 0 || moves.Count != 1 || moves[0] <= 0)
            throw new Exception("Dragging a clip across its gain line changed the wrong control.");
        Frame(80, 50, false); Frame(80, 50, true); Frame(92, 50, true); Frame(92, 50, false);
        if (gains.Count != 0) throw new Exception("Horizontal movement on the gain line committed an edit.");
        Frame(80, 50, false); Frame(80, 50, true); Frame(80, 35, true); Frame(80, 35, false);
        if (gains.Count != 1 || Math.Abs(gains[0] - 3.01) > .02 || moves.Count != 1)
            throw new Exception("Vertical drag on an armed gain line must commit one clip gain.");
        Console.WriteLine("PASS clip body drag, horizontal gain-line gesture and vertical gain adjustment.");
    }
}
