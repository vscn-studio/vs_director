using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    internal static unsafe void RunBrowserChecks()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type T(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        nint previous = ImGui.GetCurrentContext(), context = ImGui.CreateContext();
        float previousScale = RuntimeEnv.GUIScale;
        string assets = GamePaths.AssetsPath;
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var harmony = new Harmony("vsdirector.probe.browsers");
        var system = new VSImGui.ImGuiModSystem();
        var loader = ProbeDispatchProxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? system : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        {
            "get_FrameWidth" => (int)ImGui.GetIO().DisplaySize.X,
            "get_FrameHeight" => (int)ImGui.GetIO().DisplaySize.Y,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_Render" => render,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
            var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, Path.GetFullPath(Path.Combine(project, "../Vintagestory/assets")));
            Lang.PreLoad(logger, GamePaths.AssetsPath, "zh-cn");
            LoadCreationProbeLanguage("zh-cn");
        var owner = new VSDirectorModSystem();
        var settings = Activator.CreateInstance(T("DirectorSettingsDialog"), api, owner)!;
        var hud = RuntimeHelpers.GetUninitializedObject(T("DirectorWorkspaceHud"));
        Set(hud, "owner", owner); Set(hud, "capi", api);
        var legacy = Activator.CreateInstance(T("DirectorWorkspaceFloatingEditorDialog"), api, hud)!;
        Set(hud, "floatingEditorDialog", legacy);
        Set(hud, "keyframeImGuiValues", new Dictionary<string, string>());
        Set(hud, "presetUsageStore", new DirectorReplayPresetUsage());
        Set(hud, "presetBrowserCategory", "all"); Set(hud, "presetBrowserQuery", "");
        var kind = AccessTools.Field(hud.GetType(), "replayFloatingEditorKind");
        kind.SetValue(hud, Enum.Parse(kind.FieldType, "AddElement"));
        try
        {
            var io = ImGui.GetIO();
            io.NativePtr->IniFilename = null; io.DisplaySize = new(960, 640); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF(Path.Combine(GamePaths.AssetsPath, "game/fonts/Montserrat-Regular.ttf"), 18);
            ConfigureProbeFonts(assembly, project);
            AccessTools.Method(T("DirectorImGuiRuntime"), "InstallBackendPatches").Invoke(null, [harmony]);
            AccessTools.Method(T("DirectorImGuiRuntime"), "PrepareFontTexture").Invoke(null, null);
            using var native = new NativeWindow(new NativeWindowSettings { StartVisible = false,
                ClientSize = new OpenTK.Mathematics.Vector2i(960, 640), APIVersion = new Version(3, 3) });
            native.MakeCurrent();
            var rendererType = typeof(ImGuiController_OpenTK.ImGuiController).Assembly.GetType("ImGuiController_OpenTK.ImGuiRenderer", true)!;
            using var renderer = (IDisposable)Activator.CreateInstance(rendererType, ImGui.GetMainViewport(), native)!;
            foreach (string language in new[] { "zh-cn", "en" })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (bool browser in new[] { false, true })
            {
                LoadCreationProbeLanguage(language);
                RuntimeEnv.GUIScale = scale;
                io.DisplaySize = scale == 1 ? new Vector2(960, 640) : new Vector2(1920, 1440);
                object window;
                if (browser)
                {
                    Set(hud, "keyframeAnchorX", 380f); Set(hud, "keyframeAnchorY", 300f);
                    Call(hud, "ComposeKeyframeImGui", true);
                    window = AccessTools.Field(hud.GetType(), "keyframeImGuiWindow").GetValue(hud)!;
                    var form = AccessTools.Field(window.GetType(), "form").GetValue(window)!;
                    if ((bool)AccessTools.Property(form.GetType(), "AutoHeight").GetValue(form)!)
                        throw new Exception("Element browser must allow manual resizing");
                }
                else
                {
                    window = settings;
                    Call(settings, "SelectCategory", Enum.Parse(T("DirectorSettingsDialog").GetNestedType("SettingsPage", BindingFlags.NonPublic)!, "Offscreen"));
                    Call(window, "Open");
                }
                string id = browser ? "###vsdirector-keyframe" : "###vsdirector-vsdirector-settings";
                Vector2 pos = default, size = default;
                float scroll = 0;
                Vector2? requested = null;
                void Frame()
                {
                    ImGui.NewFrame();
                    if (requested is { } bounds)
                    {
                        // Update the saved native size before the real draw callback.
                        ImGui.SetNextWindowPos(new Vector2(12)); ImGui.SetNextWindowSize(bounds);
                        ImGui.Begin(id); ImGui.End();
                        requested = null;
                    }
                    Call(window, "Draw", 1f / 60);
                    ImGui.Begin(id); pos = ImGui.GetWindowPos(); size = ImGui.GetWindowSize(); scroll = ImGui.GetScrollMaxY(); ImGui.End();
                    ImGui.Render();
                }
                io.MousePos = new Vector2(-100);
                for (int i = 0; i < 4; i++) Frame();
                foreach (var bounds in new[] { new Vector2(740, 540), new Vector2(440, 390), new Vector2(browser ? 420 : 360, 320) })
                {
                    requested = bounds * scale;
                    for (int i = 0; i < 4; i++) Frame();
                    if (scroll > 1) throw new Exception($"Browser parent scrolls instead of its content: {browser}/{language}/{bounds}/{scale}: {scroll}");
                    if (scale == 1) Capture($"{(browser ? "elements" : "settings")}-{language}-{bounds.X:0}");
                    if (browser && language == "zh-cn" && scale == 1 && bounds.X == 740)
                    {
                        io.MousePos = pos + new Vector2(size.X - 20, 47); Frame(); Frame();
                        Capture("elements-help");
                        io.MousePos = new Vector2(-100); Frame();
                    }
                }
                // A real press/drag on the native lower-right grip must resize both panes.
                for (int idle = 0; idle < 24; idle++) Frame();
                Vector2 before = size;
                io.MousePos = pos + size - new Vector2(4); Frame();
                io.MouseDown[0] = true; Frame(); io.MousePos += new Vector2(90, 65); Frame(); Frame();
                io.MouseDown[0] = false; Frame();
                if (size.X < before.X + 40 || size.Y < before.Y + 30)
                    throw new Exception($"Browser resize grip did not work: {browser}/{language}/{scale} {before} -> {size}");
                if (browser)
                {
                    Vector2 resized = size;
                    Call(hud, "ComposeKeyframeImGui", true); Frame(); Frame();
                    if (Vector2.Distance(resized, size) > 1) throw new Exception("Browser recomposition reset user size");
                }
                Call(window, "Close");
            }
            Console.WriteLine("PASS settings and actual add-element browsers: wide/narrow Chinese/English layouts, independent scrolling, native corner resize and size retention at 100/150/200% DPI");

            RuntimeEnv.GUIScale = 1; io.DisplaySize = new(960, 640);
            LoadCreationProbeLanguage("zh-cn");
            CheckPreloadWindow(assembly, api, Capture);
            CheckReplayKeyframeWindow(assembly, api, _ => { });
            CheckHeadTrackingSettings(assembly, api, Capture);
            CheckTimelineFooter(assembly, api, Capture);
            CheckTimelineEditing(assembly);
            CheckNumericControls(assembly, Capture);
            CheckTransformNumberControls(assembly, Capture);

            void Capture(string name)
            {
                GL.Viewport(0, 0, 960, 640); GL.ClearColor(.09f, .09f, .12f, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
                AccessTools.Method(rendererType, "Render").Invoke(renderer, null);
                byte[] pixels = new byte[960 * 640 * 4];
                GL.ReadPixels(0, 0, 960, 640, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                string directory = Path.Combine(project, "artifacts/browser-preview"); Directory.CreateDirectory(directory);
                using var writer = new BinaryWriter(File.Create(Path.Combine(directory, name + ".bmp")));
                writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(960); writer.Write(640); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
            }
        }
        finally
        {
            Call(hud, "DisposeKeyframeImGui"); ((IDisposable)legacy).Dispose(); ((IDisposable)settings).Dispose();
            harmony.UnpatchAll(harmony.Id);
            AccessTools.Method(T("DirectorImGuiRuntime"), "Dispose").Invoke(null, null);
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, assets);
            RuntimeEnv.GUIScale = previousScale; ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous);
        }
    }
}
