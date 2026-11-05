using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private sealed class VisibilityDialog : GuiDialog
    {
        public VisibilityDialog() : base(null!) { }
        public override string ToggleKeyCombinationCode => "";
        public override EnumDialogType DialogType => EnumDialogType.Dialog;
    }

    private static void CheckHiddenHudDialogs(Type runtime, GuiDialog host)
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var api = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
        var gui = (GuiAPI)RuntimeHelpers.GetUninitializedObject(typeof(GuiAPI));
        var dialogs = new List<GuiDialog>();
        AccessTools.Field(typeof(ClientMain), "api").SetValue(game, api);
        AccessTools.Field(typeof(ClientMain), "OpenedGuis").SetValue(game, dialogs);
        AccessTools.Field(typeof(ClientCoreAPI), "guiapi").SetValue(api, gui);
        AccessTools.Field(typeof(GuiAPI), "game").SetValue(gui, game);
        bool Render() => (bool)AccessTools.Method(runtime, "ShouldRenderInterface").Invoke(null, [game])!;
        bool Hidden() => (bool)AccessTools.Property(runtime, "HideGameHud").GetValue(null)!;
        game.ShouldRender2DOverlays = false;
        if (Render()) throw new Exception("Hidden HUD unexpectedly enabled without an open dialog.");
        foreach (GuiDialog dialog in new[] { host, (GuiDialog)RuntimeHelpers.GetUninitializedObject(typeof(VisibilityDialog)) })
        {
            dialogs.Add(dialog);
            if (!Render() || !Hidden()) throw new Exception("Reopened dialogs cannot render after export restores a hidden camera HUD.");
            dialogs.Clear();
            if (Render()) throw new Exception("Closed dialogs retained the GUI pass.");
        }
        game.ShouldRender2DOverlays = true;
        if (!Render() || Hidden()) throw new Exception("Restoring overlays left the game HUD hidden.");
        Console.WriteLine("PASS reopened native and game dialogs render with hidden camera HUD after workspace deactivation");
    }

    private static void CheckExportAndFooterWindows(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        var io = ImGui.GetIO();
        float savedScale = RuntimeEnv.GUIScale;
        Vector2 savedDisplay = io.DisplaySize;
        var owner = new VSDirectorModSystem();
        var workspace = RuntimeHelpers.GetUninitializedObject(Runtime("DirectorWorkspaceHud"));
        var export = Activator.CreateInstance(Runtime("DirectorReplayExportDialog"), api, owner, workspace)!;
        var settings = Activator.CreateInstance(Runtime("DirectorSettingsDialog"), api, owner)!;
        var window = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), api, "pinned-footer-probe", false, null, false, false, null)!;
        try
        {
            io.DisplaySize = new Vector2(1920, 1080);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                io.DisplaySize = new Vector2(960, 640);
                Call(export, "Open");
                Vector2 pos = default, size = default, sentinel = default;
                float parentScroll = 0;
                void Frame()
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(new Vector2(8));
                    Call(export, "Draw", 1f / 60);
                    using (var scope = (IDisposable)AccessTools.Method(Runtime("DirectorImGuiRuntime"), "UseFont").Invoke(null, [false])!)
                    {
                        ImGui.Begin("###vsdirector-vsdirector-replay-export");
                        ImGui.SetWindowFontScale(scale);
                        pos = ImGui.GetWindowPos(); size = ImGui.GetWindowSize();
                        parentScroll = ImGui.GetScrollMaxY();
                        ImGui.End();
                    }
                    ImGui.SetNextWindowPos(new Vector2(4, 4));
                    ImGui.SetNextWindowSize(new Vector2(180, 90), ImGuiCond.FirstUseEver);
                    ImGui.Begin("export-size-sentinel", ImGuiWindowFlags.NoSavedSettings);
                    sentinel = ImGui.GetWindowSize();
                    ImGui.End();
                    ImGui.Render();
                }
                Frame(); Frame(); Frame();
                if (parentScroll > 1) throw new Exception($"Export footer is inside the parent scroll area: {parentScroll}, scale {scale}.");
                if (Math.Abs(size.X - Math.Min(560 * scale, io.DisplaySize.X - 16)) > 1 || size.Y > io.DisplaySize.Y || sentinel != new Vector2(180, 90))
                    throw new Exception("Export initial sizing is not the minimum width or leaked into another window: " + size + "/" + sentinel);
                capture($"export-{scale * 100:0}");
                object?[] optionsArgs = [null];
                if (!(bool)AccessTools.Method(export.GetType(), "TryBuildOptions").Invoke(export, optionsArgs)!
                    || optionsArgs[0] is not DirectorReplayExportOptions { Backend: DirectorReplayExportBackend.Ffmpeg, Width: 1920, Height: 1080, IncludeAudio: true })
                    throw new Exception("Export dialog did not build FFmpeg video/audio options.");
                AccessTools.Field(export.GetType(), "widthText").SetValue(export, "1919");
                if ((bool)AccessTools.Method(export.GetType(), "TryBuildOptions").Invoke(export, optionsArgs)!)
                    throw new Exception("Export accepted an invalid odd FFmpeg frame width.");
                AccessTools.Field(export.GetType(), "widthText").SetValue(export, "1920");
                Call(export, "Close");
                ImGui.NewFrame(); Call(export, "Draw", 1f / 60); ImGui.Render();
                Call(export, "Open"); Frame(); Frame();
                if (ImGui.GetDrawData().TotalVtxCount == 0) throw new Exception("Export could not reopen after closing.");
                Call(export, "Close");

                io.DisplaySize = new Vector2(1920, 1080);
                var form = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
                bool scrollBody = false;
                Call(form, "AddControl", (Action)(() =>
                {
                    for (int i = 0; i < 70; i++) ImGui.TextUnformatted("属性 " + i);
                    if (scrollBody) ImGui.SetScrollY(10000);
                }));
                int clicks = 0;
                Call(form, "FooterButton", "保存", (Action)(() => clicks++), "normal", "save", true);
                var footers = (List<Action>)AccessTools.Field(form.GetType(), "footerControls").GetValue(form)!;
                var drawFooter = footers[0];
                Vector2 footerMin = default, footerMax = default, windowPos = default, windowSize = default;
                footers[0] = () => { drawFooter(); footerMin = ImGui.GetItemRectMin(); footerMax = ImGui.GetItemRectMax(); };
                Call(window, "Render", "固定操作栏", form, 420, 320);
                Call(window, "Open");
                void FooterFrame()
                {
                    ImGui.NewFrame();
                    Call(window, "Draw", 1f / 60);
                    ImGui.Begin("###vsdirector-pinned-footer-probe");
                    windowPos = ImGui.GetWindowPos(); windowSize = ImGui.GetWindowSize();
                    parentScroll = ImGui.GetScrollMaxY();
                    ImGui.End(); ImGui.Render();
                }
                io.MousePos = new Vector2(-100); FooterFrame(); FooterFrame(); FooterFrame();
                var originalMin = footerMin;
                scrollBody = true; FooterFrame(); FooterFrame();
                if (parentScroll > 1) throw new Exception("Pinned form still scrolls its parent window.");
                if (Vector2.Distance(originalMin, footerMin) > 1 || footerMin.Y < windowPos.Y
                    || footerMax.Y > windowPos.Y + windowSize.Y)
                    throw new Exception("Footer moved with the body or fell outside its window at scale " + scale);
                io.MousePos = (footerMin + footerMax) / 2; FooterFrame();
                io.MouseDown[0] = true; FooterFrame();
                io.MouseDown[0] = false; FooterFrame();
                if (clicks != 1) throw new Exception("Pinned footer is not clickable.");
                Vector2 before = windowSize;
                io.MousePos = windowPos + windowSize - new Vector2(4); FooterFrame();
                io.MouseDown[0] = true; FooterFrame();
                io.MousePos += new Vector2(80, 50); FooterFrame(); FooterFrame();
                io.MouseDown[0] = false; FooterFrame();
                if (windowSize.X < before.X + 30 || windowSize.Y < before.Y + 20)
                    throw new Exception("Native bottom-right resize grip did not resize the form.");
                Call(window, "Close");

                Call(settings, "Open");
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame(); Call(settings, "Draw", 1f / 60); ImGui.Render();
                }
                Call(settings, "Close");
            }
            Console.WriteLine("PASS FFmpeg-only export/minimum default width/reopen, settings drawing and pinned clickable footers with bottom-right resize at 100/150/200%");
        }
        finally
        {
            ((IDisposable)export).Dispose(); ((IDisposable)settings).Dispose(); ((IDisposable)window).Dispose();
            io.MouseDown[0] = false; io.MousePos = new Vector2(-100);
            io.DisplaySize = savedDisplay; RuntimeEnv.GUIScale = savedScale;
        }
    }
}
