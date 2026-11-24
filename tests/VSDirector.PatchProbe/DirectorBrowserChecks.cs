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
    private static List<string>? aboutBrowserTargets;
    private static readonly Dictionary<string, Vector2> AboutIcons = new();
    private static readonly Dictionary<string, Vector2> AboutCards = new();
    private static string aboutPage = "";
    private static void CaptureAboutPage(MethodBase __originalMethod) => aboutPage = __originalMethod.Name;
    private static bool LocateAboutCover(object video, ref int __result)
    {
        var url = (string)AccessTools.Property(video.GetType(), "Url").GetValue(video)!;
        AboutCards[url] = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
        __result = 0;
        return false;
    }
    private static Vector2 aboutClose;
    private static readonly Dictionary<string, uint> AboutDocks = new();
    private static void CaptureAboutButtons()
    {
        AboutDocks[aboutPage] = ImGui.IsWindowDocked() ? ImGui.GetWindowDockID() : 0;
        Vector2 last = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
        float step = ImGui.GetItemRectSize().X + ImGui.GetStyle().ItemSpacing.X;
        string[] labels = ["GitHub", "Discord", "Issues", "ModDB"];
        for (int i = 0; i < labels.Length; i++) AboutIcons[labels[i]] = last - new Vector2(step * (3 - i), 0);
        aboutClose = ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowWidth() - ImGui.GetStyle().FramePadding.X
            - ImGui.GetFontSize() * .5f, ImGui.GetFrameHeight() * .5f);
    }
    private static bool CaptureAboutBrowserTarget(string __0)
    {
        aboutBrowserTargets!.Add(__0);
        return false;
    }

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
        var covers = AboutCards;
        covers.Clear();
        var links = new List<string>();
        var gui = ProbeDispatchProxy.Create<IGuiAPI>((m, a) =>
        {
            if (m.Name == "OpenLink") throw new Exception("About link used the game confirmation dialog");
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, a) =>
        {
            return m.Name switch {
            "get_FrameWidth" => (int)ImGui.GetIO().DisplaySize.X,
            "get_FrameHeight" => (int)ImGui.GetIO().DisplaySize.Y,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
            };
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_Render" => render, "get_Gui" => gui,
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

            using (var dockHost = (IDisposable)Activator.CreateInstance(T("DirectorImGuiWorkspace"), api)!)
            {
                Call(dockHost, "Open");
                CheckAdaptiveBrowser(assembly, api, dockHost, Capture);
                var runtime = RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!);
                Set(owner, "creation", runtime);
                var session = new DirectorCreationSession(new(Path.GetTempPath()));
                session.Open(new DirectorCreationProject());
                Set(runtime, "<Session>k__BackingField", session);
                var creation = Activator.CreateInstance(T("DirectorCreationWorkspace"), BindingFlags.NonPublic | BindingFlags.Instance,
                    null, [api, owner, dockHost], null)!;
                CheckCreationPreferences(creation, session);
            }

            RuntimeEnv.GUIScale = 1; io.DisplaySize = new(960, 640);
            LoadCreationProbeLanguage("zh-cn");
            aboutBrowserTargets = links;
            foreach (string method in new[] { "DrawAbout", "DrawContributors", "DrawCopyright" })
                harmony.Patch(AccessTools.Method(T("DirectorAboutDialog"), method),
                    prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(CaptureAboutPage)));
            harmony.Patch(AccessTools.Method(T("DirectorAboutDialog"), "CoverTexture"),
                prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(LocateAboutCover)));
            harmony.Patch(AccessTools.Method(T("DirectorAboutDialog"), "DrawDocumentBody"),
                postfix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(CaptureAboutButtons)));
            harmony.Patch(AccessTools.Method(typeof(Vintagestory.API.Util.NetUtil), "OpenUrlInBrowser"),
                prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(CaptureAboutBrowserTarget)));
            Call(hud, "OpenAbout");
            var about = AccessTools.Field(hud.GetType(), "aboutDialog").GetValue(hud)!;
            AccessTools.Field(about.GetType(), "contributorFeed").SetValue(about, null);
            Set(about, "contributors", new[] { "TGU-HansJack", "HanosThePlacid" });
            // Keep layout/click fixtures independent of the repository's live catalog.
            using var fixture = System.Text.Json.JsonDocument.Parse("""
                [
                  {"url":"https://www.bilibili.com/video/BV1MGKP6VEUK","title":"机位与回放","author":"A"},
                  {"url":"https://www.bilibili.com/video/BV1AAKN6MEYW","title":"离屏渲染","author":"B"},
                  {"url":"https://youtu.be/9sWgkE0pY4M","title":"Replay showcase","author":"C"},
                  {"url":"https://youtu.be/FGnppE7M0KA","title":"VS Director test","author":"D"}
                ]
                """);
            AccessTools.Field(about.GetType(), "catalog").SetValue(about, null);
            Set(about, "videos", AccessTools.Method(T("DirectorAboutFeed"), "ParseVideos").Invoke(null, [fixture.RootElement])!);
            Set(about, "version", System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "modinfo.json"))).RootElement.GetProperty("version").GetString()!);
            Vector2 origin = default, extent = default;
            void AboutFrame()
            {
                ImGui.NewFrame(); Call(about, "Draw", 1f / 60);
                ImGui.Begin("###vsdirector-about");
                origin = ImGui.GetWindowPos(); extent = ImGui.GetWindowSize();
                ImGui.End(); ImGui.Render();
            }
            void ClickAbout(Vector2 at)
            {
                io.MousePos = at; AboutFrame(); io.MouseDown[0] = true; AboutFrame(); io.MouseDown[0] = false; AboutFrame();
            }
            for (int i=0; i<3; i++) AboutFrame();
            if (aboutPage != "DrawAbout") throw new Exception("About did not open on the About window");
            if (origin.X < 10 || origin.Y < 2 || Math.Abs(origin.X + extent.X * .5f - io.DisplaySize.X * .5f) > 2
                || covers.Count != 4 || covers.Any(c => c.Key.Contains("bilibili.com")
                    ? c.Value.X >= origin.X + extent.X * .25f : c.Value.X <= origin.X + extent.X * .75f))
                throw new Exception("About content must be inset, centered and flanked by videos");
            Vector2[] leftCards = covers.Where(c => c.Key.Contains("bilibili.com")).Select(c => c.Value).ToArray();
            if (Math.Abs(leftCards[0].X - leftCards[1].X) > 1 || leftCards[1].Y <= leftCards[0].Y)
                throw new Exception("Narrow video shelf must wrap");
            io.DisplaySize = new(1920, 1080);
            for (int i = 0; i < 3; i++) AboutFrame();
            leftCards = covers.Where(c => c.Key.Contains("bilibili.com")).Select(c => c.Value).ToArray();
            if (leftCards[1].X <= leftCards[0].X + 100 || Math.Abs(leftCards[1].Y - leftCards[0].Y) > 30)
                throw new Exception("Wide video shelf must display adjacent cards");
            io.DisplaySize = new(960, 640);
            for (int i = 0; i < 3; i++) AboutFrame();
            Capture("about-zh");
            foreach (var at in covers.Values.ToArray()) ClickAbout(at);
            if (!links.SequenceEqual(new[] { "https://www.bilibili.com/video/BV1MGKP6VEUK", "https://www.bilibili.com/video/BV1AAKN6MEYW", "https://youtu.be/9sWgkE0pY4M", "https://youtu.be/FGnppE7M0KA" }))
                throw new Exception("About card links did not open the expected videos");
            foreach (string locale in new[] { "en", "zh-cn" })
            {
                LoadCreationProbeLanguage(locale); AboutFrame();
                if ((bool)AccessTools.Property(about.GetType(), "ChineseLocale").GetValue(null)! != (locale == "zh-cn"))
                    throw new Exception("About document did not follow the game locale");
            }
            links.Clear();
            AboutFrame(); AboutFrame();
            // The capture above uses the 960x640 Chinese layout at 100% scale.
            foreach (var (x, expected) in new[] { (370, "DrawContributors"), (460, "DrawCopyright"), (295, "DrawAbout") })
            {
                ClickAbout(new Vector2(x, aboutClose.Y)); AboutFrame();
                if (aboutPage != expected) throw new Exception("About tab failed to select " + expected);
            }
            if (AboutDocks.Count != 3 || AboutDocks.Values.Contains(0u) || AboutDocks.Values.Distinct().Count() != 1)
                throw new Exception("The three About windows must share one native dock node");
            foreach (string label in new[] { "GitHub", "Discord", "Issues", "ModDB" }) ClickAbout(AboutIcons[label]);
            if (!links.SequenceEqual(new[] { "https://github.com/vscn-studio/vs_director",
                "https://discord.com/channels/302152934249070593/1529965521435361420",
                "https://github.com/vscn-studio/vs_director/issues", "https://mods.vintagestory.at/vsdirector" }))
                throw new Exception("Missing or incorrect About project links: " + string.Join(", ", links));
            links.Clear();
            Call(about, "OpenDocumentLink", "licenses/THIRD-PARTY.txt");
            Call(about, "OpenDocumentLink", "file:///C:/modinfo.json");
            if (!links.SequenceEqual(new[] { "https://github.com/vscn-studio/vs_director/blob/HEAD/licenses/THIRD-PARTY.txt" }))
                throw new Exception("About document link resolution failed");
            foreach (string file in new[] { "README.md", "README.en.md", "LICENSE", "COPYRIGHT.txt" })
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream("VSDirector.About." + file)!);
                if (reader.ReadToEnd() != File.ReadAllText(Path.Combine(project, file))) throw new Exception("Stale embedded " + file);
            }
            ClickAbout(new Vector2(460, aboutClose.Y)); AboutFrame();
            if (aboutPage != "DrawCopyright") throw new Exception("Expected the Copyright window before closing");
            ClickAbout(aboutClose); AboutFrame();
            ImGui.NewFrame();
            bool stillModal = ImGui.IsPopupOpen("###vsdirector-about");
            ImGui.Render();
            if (stillModal) throw new Exception("About left a modal blocking the workspace");
            Call(about, "Open");
            aboutPage = "";
            for (int i = 0; i < 4; i++) AboutFrame();
            if (aboutPage != "DrawAbout") throw new Exception("Reopening must select the About window");
            ClickAbout(aboutClose); AboutFrame();
            ((IDisposable)about).Dispose();
            Console.WriteLine("PASS native About window tabs, shared dock node, close and reopen, responsive video shelves, localized Markdown and eight external links");
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
            aboutBrowserTargets = null;
            Call(hud, "DisposeKeyframeImGui"); ((IDisposable)legacy).Dispose(); ((IDisposable)settings).Dispose();
            harmony.UnpatchAll(harmony.Id);
            AccessTools.Method(T("DirectorImGuiRuntime"), "Dispose").Invoke(null, null);
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, assets);
            RuntimeEnv.GUIScale = previousScale; ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous);
        }
    }
}
