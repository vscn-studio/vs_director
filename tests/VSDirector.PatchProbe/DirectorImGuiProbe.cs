using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using VSDirector;
using VSImGui;
using VSImGui.API;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;

/// <summary>Exercises real ImGui frames and a hidden GL renderer without a game.</summary>
internal static partial class DirectorImGuiProbe
{
    internal static unsafe void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        float savedScale = RuntimeEnv.GUIScale;
        string savedAssets = GamePaths.AssetsPath;
        nint context = ImGui.CreateContext();
        string probeData = Path.Combine(Path.GetTempPath(), "vsdirector-imgui-probe-" + Guid.NewGuid().ToString("N"));
        var system = new ImGuiModSystem();
        if (new VSDirectorModSystem().ExecuteOrder() >= system.ExecuteOrder())
            throw new Exception("Director must configure the viewport and font hook before VSImGui initializes.");
        var loader = ProbeDispatchProxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? system : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var viewports = new List<(int X, int Y, int Width, int Height)>();
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, args) =>
        {
            if (m.Name == "GlViewport") viewports.Add(((int)args![0]!, (int)args[1]!, (int)args[2]!, (int)args[3]!));
            return m.Name switch
            {
                "get_FrameWidth" => 1920,
                "get_FrameHeight" => 1080,
                _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
            };
        });
        var events = ProbeDispatchProxy.Create<IClientEventAPI>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader,
            "get_Render" => render,
            "get_Event" => events,
            "GetOrCreateDataPath" => probeData,
            _ => ProbeDispatchProxy.DefaultValue(m.ReturnType)
        });
        var disposables = new List<IDisposable>();
        var backendHarmony = new Harmony("vsdirector.probe.client-coordinates");
        try
        {
            Type runtime = Runtime("DirectorImGuiRuntime");
            AccessTools.Method(runtime, "InstallBackendPatches").Invoke(null, [backendHarmony]);
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            AccessTools.Field(runtime, "fontPath").SetValue(null,
                Path.Combine(project, "assets/vsdirector/fonts/noto/NotoSansSC-Regular.ttf"));
            // Exercise the same font hook used before VSImGui uploads its texture.
            AccessTools.Method(runtime, "LoadFont").Invoke(null, null);
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out nint _, out int atlasWidth, out int atlasHeight);
            var font = (ImFontPtr)AccessTools.Field(runtime, "font").GetValue(null)!;
            foreach (char c in "导演模组视图属性设置播放时间线回放删除名称")
                if (font.FindGlyphNoFallback(c).NativePtr == null) throw new Exception("Chinese glyph missing: " + c);
            var icons = (Dictionary<string, string>)AccessTools.Field(Runtime("DirectorImGuiIcons"), "Glyphs").GetValue(null)!;
            foreach (var icon in icons)
            {
                var glyph = font.FindGlyphNoFallback(icon.Value[0]);
                if (glyph.NativePtr == null)
                    throw new Exception("Merged editor icon missing: " + icon.Key);
            }
            CheckIconQuads(font, icons, atlasWidth, atlasHeight);
            string playIcon = icons["play"];
            if ((string)AccessTools.Method(Runtime("DirectorImGuiForm"), "Caption").Invoke(null, ["", "play"])! != playIcon)
                throw new Exception("Icon-only button captions contain horizontal whitespace.");
            if (atlasWidth > 8192 || atlasHeight > 8192) throw new Exception("Chinese font atlas is too large.");

            var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            string gamePath = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? Path.GetFullPath(Path.Combine(project, "../Vintagestory"));
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, Path.Combine(gamePath, "assets"));
            Lang.PreLoad(logger, Path.Combine(gamePath, "assets"), "zh-cn");
            var translations = Lang.AvailableLanguages[Lang.CurrentLocale].GetAllEntries();
            foreach (var entry in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(project, "assets/vsdirector/lang/zh-cn.json")))!)
                translations["vsdirector:" + entry.Key] = entry.Value;
            var workspace = Activator.CreateInstance(Runtime("DirectorImGuiWorkspace"), api)!;
            disposables.Add((IDisposable)workspace);
            Call(workspace, "Open");
            var hostType = typeof(ImGuiModSystem).Assembly.GetType("VSImGui.VSImGuiDialog", true)!;
            var host = (GuiDialog)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(hostType);
            if (host.DrawOrder <= .34 || host.DrawOrder >= .89)
                throw new Exception("ImGui host must render above the workspace backdrop and below the escape menu.");
            if (Harmony.GetPatchInfo(AccessTools.Method(hostType, "OnRenderGUI"))?.Prefixes.Any(p =>
                    p.PatchMethod.DeclaringType?.Name == "DirectorWorkspaceDirectorDialogViewportPatch") != true)
                throw new Exception("ImGui render override must restore the full game window viewport.");
            var viewportRuntime = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
            CheckHiddenHudDialogs(viewportRuntime, host);
            AccessTools.Method(viewportRuntime, "Configure").Invoke(null, [workspace, api, 0, 28, 500, 240, false]);
            try
            {
                // The uninitialized host skips the GL renderer through its own
                // exception guard; the real override still runs our viewport hooks.
                host.OnRenderGUI(1f / 60);
                if (viewports.Count < 2 || viewports[0] != (0, 0, 1920, 1080) || viewports[^1].Width >= 1920)
                    throw new Exception("ImGui host did not restore the full viewport before rendering, then the game HUD viewport.");
            }
            finally { AccessTools.Method(viewportRuntime, "Deactivate").Invoke(null, [workspace]); }
            var panel = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), api, "inspector", true, null, false, false, workspace)!;
            disposables.Add((IDisposable)panel);
            var form = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
            Call(form, "Note", "导演模组");
            uint inspectorDock = 0, viewDock = 0;
            bool inspectorVisible = false, viewVisible = false;
            Call(form, "AddControl", (Action)(() => { inspectorDock = ImGui.GetWindowDockID(); inspectorVisible = true; }));
            Call(panel, "Render", "属性", form, 320, 500);
            Call(panel, "Open");
            var view = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), api, "view", true, null, false, false, workspace)!;
            disposables.Add((IDisposable)view);
            var viewForm = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
            Call(viewForm, "Note", "视图选项");
            Call(viewForm, "AddControl", (Action)(() => { viewDock = ImGui.GetWindowDockID(); viewVisible = true; }));
            Call(view, "Render", "视图", viewForm, 320, 500);
            Call(view, "Open");

            Vector2 pointerDown = default;
            Action<MouseEvent> mouseDown = e => pointerDown = new Vector2(e.X, e.Y);
            Action<MouseEvent> mouse = _ => { };
            var wheels = new List<(float Delta, bool Ctrl)>();
            Action<int, int, float, bool> wheel = (_, _, delta, ctrl) => wheels.Add((delta, ctrl));
            Action<KeyEvent> key = _ => { };
            Action noop = () => { };
            var timeline = Activator.CreateInstance(Runtime("DirectorTimelineWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [api, mouseDown, mouse, mouse, wheel, key, noop, noop, workspace], null)!;
            disposables.Add((IDisposable)timeline);
            Call(timeline, "OpenTimeline");

            io.DisplaySize = new Vector2(1920, 1080);
            io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null;
            io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
            CheckDynamicReplayPanels(assembly, api, system);
            CheckExportWorkspaceRecovery(assembly, api);
            CheckNativeCoordinates(capture => { CheckIconGallery(font, icons, capture); CheckPreloadWindow(assembly, api, capture); CheckExportAndFooterWindows(assembly, api, capture); CheckReplayKeyframeWindow(assembly, api, capture); CheckCompactChoices(assembly); CheckAdaptiveBrowser(assembly, api, workspace, capture); CheckEditorControls(assembly); });
            bool emptyTimeline = false;
            var panelBounds = new Dictionary<string, (Vector2 Position, Vector2 Size)>();
            Action<string, Vector2, Vector2> boundsChanged = (id, position, size) =>
            {
                panelBounds[id] = (position, size);
                if (id != "timeline") return;
                Call(timeline, "Begin", (double)position.X, (double)position.Y, (double)size.X, (double)size.Y);
                if (emptyTimeline) { Call(timeline, "End"); return; }
                Call(timeline, "Shape", "test", "timeline-row", 0d, 0d, 100d, 28d, "导演", "", "");
                Call(timeline, "Shape", "track-icon", "timeline-track-icon", 0d, 30d, 28d, 28d, "", "camera", "相机");
                Call(timeline, "Shape", "name", "timeline-track-name", 28d, 30d, 110d, 28d, "较长的相机轨道名称", "", "");
                Call(timeline, "Shape", "key", "timeline-key selected", 150d, 30d, 16d, 28d, "", "square-rotated-filled", "");
                Call(timeline, "Shape", "freeze", "timeline-key", 200d, 30d, 16d, 28d, "", "snowflake", "");
                Call(timeline, "Playhead", 158d, 100d, 500d, 180d);
                Call(timeline, "End");
            };
            AccessTools.Event(workspace.GetType(), "BoundsChanged").GetAddMethod(true)!.Invoke(workspace, [boundsChanged]);
            void WorkspaceFrame()
            {
                ImGui.NewFrame();
                inspectorVisible = viewVisible = false;
                Call(workspace, "Draw", 1f / 60);
                // VSImGui still dispatches registered dialogs; they must not draw twice.
                Call(panel, "Draw", 1f / 60);
                Call(view, "Draw", 1f / 60);
                Call(timeline, "Draw", 1f / 60);
                ImGui.Render();
                if (ImGui.GetPlatformIO().Viewports.Size != 1) throw new Exception("Workspace created a platform window.");
            }
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                Call(workspace, "ResetLayout");
                Call(panel, "SetDockBounds", 500d, 28d, 320d, 240d);
                Call(view, "SetDockBounds", 500d, 28d, 320d, 240d);
                for (int frame = 0; frame < 3; frame++) WorkspaceFrame();
                bool committed = false;
                Call(panel, "QueueCommit", (Action)(() => committed = true));
                Call(view, "FocusNextFrame");
                WorkspaceFrame(); WorkspaceFrame();
                if (!viewVisible || inspectorVisible || !committed) throw new Exception("View did not activate its native title tab and commit the previous edits.");
                Call(panel, "FocusNextFrame");
                WorkspaceFrame(); WorkspaceFrame();
                if (!inspectorVisible || viewVisible || inspectorDock == 0 || inspectorDock != viewDock)
                    throw new Exception("Properties and View do not switch within the same native dock node.");
                var preview = panelBounds["preview"];
                var inspector = panelBounds["inspector"];
                var time = panelBounds["timeline"];
                if (preview.Size.X <= 0 || preview.Size.Y <= 0
                    || inspector.Position.X < preview.Position.X + preview.Size.X
                    || time.Position.Y < preview.Position.Y + preview.Size.Y
                    || (time.Position.X + time.Size.X) * scale > io.DisplaySize.X + 1)
                    throw new Exception("Native docking panes overlap or exceed the game viewport at scale " + scale);
                if (time.Size.X * scale < io.DisplaySize.X - 4 * scale)
                    throw new Exception("Bottom workspace pane does not span the game window at scale " + scale);
                // Cameras and follow cameras keep an empty native pane; replay restores its content.
                foreach (bool empty in new[] { true, false })
                {
                    emptyTimeline = empty;
                    boundsChanged("timeline", time.Position, time.Size);
                    WorkspaceFrame(); WorkspaceFrame();
                    if (panelBounds["preview"] != preview || panelBounds["timeline"] != time)
                        throw new Exception("Switching camera/replay content moved the viewport or bottom pane at scale " + scale);
                }
                Vector2 pointer = (time.Position + new Vector2(100, 22)) * scale;
                for (int frame = 0; frame < 3; frame++)
                {
                    io.MousePos = pointer;
                    io.MouseDown[0] = frame == 1;
                    WorkspaceFrame();
                }
                if (Vector2.Distance(pointerDown, pointer) > 1)
                    throw new Exception("Docked timeline content and mouse use different coordinates at scale " + scale);
                wheels.Clear();
                io.AddKeyEvent(ImGuiKey.LeftCtrl, true);
                io.KeyCtrl = false; // VSImGui's legacy modifier is overwritten during NewFrame.
                io.MouseWheel = 1;
                WorkspaceFrame(); WorkspaceFrame();
                io.AddKeyEvent(ImGuiKey.LeftCtrl, false);
                WorkspaceFrame();
                io.AddKeyEvent(ImGuiKey.RightCtrl, true);
                io.MouseWheel = -1;
                WorkspaceFrame(); WorkspaceFrame();
                io.AddKeyEvent(ImGuiKey.RightCtrl, false);
                WorkspaceFrame();
                io.MouseWheel = 1;
                WorkspaceFrame(); WorkspaceFrame();
                if (!wheels.SequenceEqual(new[] { (1f, true), (-1f, true), (1f, false) }))
                    throw new Exception("Timeline lost Ctrl, reversed or duplicated wheel input at scale " + scale);
                for (int frame = 0; frame < 2; frame++)
                {
                    io.MousePos = (preview.Position + preview.Size / 2) * scale;
                    io.MouseDown[0] = false;
                    WorkspaceFrame();
                    if (frame == 1 && io.WantCaptureMouse) throw new Exception("Native dockspace blocks game viewport input.");
                }
                Vector2 splitter = new((preview.Position.X + preview.Size.X) * scale + 2,
                    (preview.Position.Y + preview.Size.Y / 2) * scale);
                io.MousePos = splitter;
                WorkspaceFrame();
                io.MouseDown[0] = true;
                WorkspaceFrame();
                io.MousePos = splitter - new Vector2(80 * scale, 0);
                WorkspaceFrame();
                WorkspaceFrame();
                io.MouseDown[0] = false;
                WorkspaceFrame();
                if (Math.Abs(panelBounds["preview"].Size.X - preview.Size.X) < 20)
                    throw new Exception("Native dock splitter did not update the game viewport at scale " + scale);
            }
            RuntimeEnv.GUIScale = 1;
            Console.WriteLine("PASS ImGui native Properties/View title tabs at 100/150/200% and host layering/full viewport patch");
            Console.WriteLine($"PASS native ImGui Chinese glyphs ({atlasWidth}x{atlasHeight} atlas), centered icon glyphs, single viewport, stable full-width camera/replay bottom pane, left/right Ctrl+wheel direction and single dispatch, timeline coordinates and game mouse passthrough at 100/150/200%; collapsed dialogs balance Begin/End");
        }
        finally
        {
            foreach (var disposable in disposables.AsEnumerable().Reverse()) disposable.Dispose();
            backendHarmony.UnpatchAll(backendHarmony.Id);
            AccessTools.Method(Runtime("DirectorImGuiRuntime"), "Dispose").Invoke(null, null);
            RuntimeEnv.GUIScale = savedScale;
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, savedAssets);
            ImGui.DestroyContext(context);
            if (Directory.Exists(probeData)) Directory.Delete(probeData, true);
        }
    }

    private static void CheckDynamicReplayPanels(Assembly assembly, ICoreClientAPI api, ImGuiModSystem system)
    {
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        // Real VSImGui registration, unlike tests that call workspace.Draw directly.
        // Entering Replay from Cameras constructs its timeline during a draw callback.
        var managerField = AccessTools.Field(typeof(ImGuiModSystem), "_guiManager");
        object? savedManager = managerField.GetValue(system);
        object manager = Activator.CreateInstance(managerField.FieldType, true)!;
        managerField.SetValue(system, manager);
        var windows = new List<IDisposable>();
        int rendered = 0;
        try
        {
            object workspace = Activator.CreateInstance(Runtime("DirectorImGuiWorkspace"), api)!;
            windows.Add((IDisposable)workspace);
            object panel = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), api, "inspector", true, null, false, false, workspace)!;
            windows.Add((IDisposable)panel);
            object form = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
            bool enteredReplay = false;
            Call(form, "AddControl", (Action)(() =>
            {
                rendered++;
                ImGui.TextUnformatted("Replay workspace");
                if (enteredReplay) return;
                enteredReplay = true;
                Action<MouseEvent> mouse = _ => { };
                Action noop = () => { };
                object timeline = Activator.CreateInstance(Runtime("DirectorTimelineWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [api, mouse, mouse, mouse, (Action<int, int, float, bool>)((_, _, _, _) => { }),
                        (Action<KeyEvent>)(_ => { }), noop, noop, workspace], null)!;
                windows.Add((IDisposable)timeline);
                Call(timeline, "Begin", 0d, 700d, 1200d, 300d);
                Call(timeline, "Shape", "replay", "timeline-row", 0d, 0d, 500d, 28d, "Replay", "", "");
                Call(timeline, "End");
                Call(timeline, "OpenTimeline");
                Type stateType = Runtime("DirectorPreloadMapState");
                var state = System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(stateType),
                    System.Linq.Expressions.Expression.Constant(null, stateType)).Compile();
                object preload = Activator.CreateInstance(Runtime("DirectorPreloadRmlWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [api, state, (Action<int, int, int, int, bool>)((_, _, _, _, _) => { }), (Action<int, int>)((_, _) => { }),
                        (Action<double>)(_ => { }), true, 60d, 0d], null)!;
                windows.Add((IDisposable)preload);
                Call(preload, "Open");
            }));
            Call(panel, "Render", "Replay properties", form, 320, 500);
            Call(panel, "SetDockBounds", 1400d, 34d, 320d, 500d);
            Call(panel, "Open");
            Call(workspace, "Open");
            for (int frame = 0; frame < 4; frame++)
            {
                ImGui.NewFrame();
                try { Call(manager, "Draw", 1f / 60); }
                finally { ImGui.Render(); }
                // Newly docked windows may spend their first frame measuring.
                if (frame >= 2 && ImGui.GetDrawData().TotalVtxCount == 0)
                    throw new Exception("Replay transition left ImGui without draw data.");
            }
            if (!enteredReplay || rendered < 3) throw new Exception("Replay panels stopped drawing after registration.");
            int removedDraws = 0;
            DrawCallbackDelegate removed = _ => { removedDraws++; return CallbackGUIStatus.GrabMouse; };
            DrawCallbackDelegate remover = null!;
            remover = _ =>
            {
                system.Draw -= removed;
                system.Draw -= remover;
                return CallbackGUIStatus.GrabMouse;
            };
            system.Draw += remover;
            system.Draw += removed;
            ImGui.NewFrame();
            try
            {
                var status = ((bool Open, bool Grab, bool Close))Call(manager, "Draw", 1f / 60)!;
                if (removedDraws != 0 || status.Grab)
                    throw new Exception("Disposed callbacks still drew or retained mouse capture.");
            }
            finally { ImGui.Render(); }
            Console.WriteLine("PASS real VSImGui callback dispatch survives replay timeline/preload creation and subsequent workspace frames");
        }
        finally
        {
            for (int i = windows.Count - 1; i >= 0; i--) windows[i].Dispose();
            managerField.SetValue(system, savedManager);
        }
    }

    private static void CheckCompactChoices(Assembly assembly)
    {
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        var io = ImGui.GetIO();
        float savedScale = RuntimeEnv.GUIScale;
        Vector2 savedDisplay = io.DisplaySize;
        try
        {
            io.DisplaySize = new Vector2(960, 640);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var form = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
                var document = Activator.CreateInstance(Runtime("DirectorImGuiDocument"), true)!;
                Call(form, "AttachDocument", document);
                int clicked = -1;
                string[] labels = ["相机", "位置", "旋转", "视野", "时间", "天气", "景深", "带有较长名称的元素"];
                var choices = labels.Select((label, index) => (Id: "option-" + index, Label: label,
                    Click: (Action)(() => clicked = index), Icon: "plus")).ToArray();
                Call(form, "ChoiceList", (object)choices);
                Vector4 Bounds(int index)
                {
                    var element = Call(document, "GetElementById", "option-" + index)!;
                    var bounds = AccessTools.Property(element.GetType(), "Bounds").GetValue(element)!;
                    float Read(string property) => (float)AccessTools.Property(bounds.GetType(), property).GetValue(bounds)!;
                    return new Vector4(Read("X"), Read("Y"), Read("Width"), Read("Height"));
                }
                void Frame(bool focus = false)
                {
                    ImGui.NewFrame();
                    using (var font = (IDisposable)AccessTools.Method(Runtime("DirectorImGuiRuntime"), "UseFont").Invoke(null, [false])!)
                    {
                        ImGui.SetNextWindowPos(new Vector2(8));
                        ImGui.SetNextWindowSize(Vector2.Min(new Vector2(280, 430) * scale, io.DisplaySize - new Vector2(16)));
                        ImGui.Begin("添加元素###compact-choices-" + scale, ImGuiWindowFlags.NoSavedSettings);
                        ImGui.SetWindowFontScale(scale);
                        if (focus) ImGui.SetKeyboardFocusHere();
                        Call(form, "Draw");
                        ImGui.End();
                    }
                    ImGui.Render();
                }
                try
                {
                    io.MousePos = new Vector2(-100);
                    io.MouseDown[0] = false;
                    Frame(); Frame();
                    Vector4 first = Bounds(0), second = Bounds(1);
                    if (first.Z > 280 * scale || second.Y <= first.Y || first.Z <= 0)
                        throw new Exception("Add-element selector is not a compact single column at scale " + scale);
                    io.MousePos = new Vector2(second.X + second.Z / 2, second.Y + second.W / 2);
                    Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
                    if (clicked != 1) throw new Exception("Add-element selector clicked the wrong option.");
                    Frame(true); Frame();
                    io.AddInputCharactersUTF8("位置");
                    Frame(); Frame();
                    if (Bounds(0).Z != 0 || Bounds(1).Z <= 0 || Bounds(7).Z != 0)
                        throw new Exception("Add-element search retained filtered option bounds at scale " + scale);
                }
                finally { Call(document, "Dispose"); }
            }
            Console.WriteLine("PASS compact single-column add-element selection, click routing and search filtering at 100/150/200% scale");
        }
        finally { RuntimeEnv.GUIScale = savedScale; io.DisplaySize = savedDisplay; }
    }

    private static void CheckAdaptiveBrowser(Assembly assembly, ICoreClientAPI api, object workspace, Action<string> capture)
    {
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        var io = ImGui.GetIO();
        Vector2 savedDisplay = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        var compactRender = ProbeDispatchProxy.Create<IRenderAPI>((m, args) => m.Name switch
        {
            "get_FrameWidth" => 960,
            "get_FrameHeight" => 640,
            _ => m.Invoke(api.Render, args)
        });
        var compactApi = ProbeDispatchProxy.Create<ICoreClientAPI>((m, args) =>
            m.Name == "get_Render" ? compactRender : m.Invoke(api, args));
        var browser = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), api, "resource-browser", true, null, false, false, workspace)!;
        int closed = 0, recorded = 0, stopped = 0, refreshed = 0;
        int[] activated = new int[2];
        AccessTools.Event(browser.GetType(), "Closed").GetAddMethod(true)!.Invoke(browser, [(Action)(() => closed++)]);
        Type resourceType = Runtime("DirectorWorkspaceMenus+Resource");
        var resources = Array.CreateInstance(resourceType, 2);
        Action noop = () => { };
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            resources.SetValue(Activator.CreateInstance(resourceType,
                "replay-" + i, "回放 " + (i + 1), "00:30 · 山谷", "回放 " + i, noop,
                (Action)(() => activated[index]++), false), i);
        }
        object MakeForm(string query) => AccessTools.Method(Runtime("DirectorWorkspaceMenus"), "Browser").Invoke(null,
            ["回放列表", query, "山谷", true, "搜索回放", "录制名称", "创建", "录制", "停止", "刷新", "播放",
                resources, Array.CreateInstance(resourceType, 0), "视图", (Action<string>)(_ => { }), (Action<string>)(_ => { }),
                noop, (Action)(() => recorded++), (Action)(() => stopped++), (Action)(() => refreshed++), true, false])!;
        Vector4 Bounds(object window, string id)
        {
            var document = AccessTools.Property(window.GetType(), "Document").GetValue(window)!;
            var element = Call(document, "GetElementById", id)!;
            var bounds = AccessTools.Property(element.GetType(), "Bounds").GetValue(element)!;
            float Read(string property) => (float)AccessTools.Property(bounds.GetType(), property).GetValue(bounds)!;
            return new Vector4(Read("X"), Read("Y"), Read("Width"), Read("Height"));
        }
        void Frame()
        {
            ImGui.NewFrame();
            Call(workspace, "Draw", 1f / 60);
            ImGui.Render();
        }
        void Click(string id)
        {
            var bounds = Bounds(browser, id);
            if (bounds.Z <= 0 || bounds.W <= 0) throw new Exception("Missing replay browser item: " + id);
            io.MousePos = new Vector2(bounds.X + bounds.Z / 2, bounds.Y + bounds.W / 2);
            Frame(); io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
        }
        try
        {
            io.DisplaySize = new Vector2(960, 640);
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                Call(browser, "Render", "回放列表", MakeForm(""), 400, 500);
                Call(browser, "SetDockBounds", 8d / scale, 60d / scale, 400d, 500d);
                Call(browser, "Open");
                io.MousePos = new Vector2(-100); io.MouseDown[0] = false;
                Frame(); Frame(); Frame();
                if (scale == 1) capture("replay");
                var full = Bounds(browser, "window");
                if (full.W > 600 * scale || full.X < 0 || full.X + full.Z > io.DisplaySize.X + 1
                    || full.Y + full.W > io.DisplaySize.Y + 1)
                    throw new Exception("Replay browser exceeds the viewport at scale " + scale);
                Click("resource-activate-replay-0"); Click("resource-activate-replay-1");
                Click("resource-refresh");
                Click("resource-record-menu");
                Click("resource-stop"); // Disabled while no recording is active.
                Click("resource-record");
                Call(browser, "Render", "回放列表", MakeForm("no-match"), 400, 500);
                Frame(); Frame(); Frame();
                // Dock tabs keep the node height while filtering. Verify the
                // filtered content is removed from the shared left tab.
                if (Bounds(browser, "resource-activate-replay-0").Z > 0)
                    throw new Exception("Replay browser retained a filtered row.");
                // Exercise the close lifecycle used by a dock tab's close X.
                Call(browser, "Close");
                if ((bool)AccessTools.Property(browser.GetType(), "IsOpened").GetValue(browser)!)
                    throw new Exception("Docked replay browser close did not close the window.");

                var selector = Activator.CreateInstance(Runtime("DirectorImGuiWindow"), compactApi, "adaptive-selector", false, null, false, false, null)!;
                try
                {
                    var form = Activator.CreateInstance(Runtime("DirectorImGuiForm"), true)!;
                    Call(form, "ChoiceList", (object)new (string Id, string Label, Action Click, string Icon)[]
                        { ("camera", "相机", noop, "plus"), ("fov", "视野", noop, "plus") });
                    Call(selector, "Render", "添加元素", form, 280, 430);
                    Call(selector, "SetAnchor", 480f / scale, 560f / scale);
                    Call(selector, "Open");
                    for (int frame = 0; frame < 4; frame++)
                    {
                        ImGui.NewFrame(); Call(selector, "Draw", 1f / 60); ImGui.Render();
                    }
                    var bounds = Bounds(selector, "window");
                    if (bounds.Z > 280 * scale + 1 || bounds.W > 220 * scale || bounds.Y < 0
                        || Math.Abs(bounds.Y + bounds.W - (560 - 10 * scale)) > 2)
                        throw new Exception("Auto-height add-element window lost its compact size or pointer anchor at scale " + scale + ": " + bounds);
                }
                finally { ((IDisposable)selector).Dispose(); }
            }
            if (closed != 3 || recorded != 3 || stopped != 0 || refreshed != 3
                || activated.Any(count => count < 2))
                throw new Exception($"Replay browser command routing failed: {closed}/{recorded}/{stopped}/{refreshed}; rows {string.Join(',', activated)}");
            Console.WriteLine("PASS replay row actions, shared left Dock tab, native menu/disabled recording controls, filtering and compact anchored selectors at 100/150/200%");
        }
        finally
        {
            ((IDisposable)browser).Dispose();
            io.MouseDown[0] = false;
            RuntimeEnv.GUIScale = savedScale;
            io.DisplaySize = savedDisplay;
        }
    }

    private static void CheckEditorControls(Assembly assembly)
    {
        Type Runtime(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        object New(string name) => Activator.CreateInstance(Runtime(name), true)!;
        var toolbar = New("DirectorImGuiForm");
        var document = New("DirectorImGuiDocument");
        Call(toolbar, "AttachDocument", document);
        AccessTools.Property(toolbar.GetType(), "ClassName").SetValue(toolbar, "workspace-menu");
        int[] clicks = new int[3];
        var commands = new (string Id, string Label, Action Click, bool Enabled, string Tone, string Icon)[]
        {
            ("first", "播放", () => clicks[0]++, true, "primary", "play"),
            ("second", "播放", () => clicks[1]++, true, "normal", "play"),
            ("disabled", "删除", () => clicks[2]++, false, "danger", "trash")
        };
        Call(toolbar, "ButtonRow", (object)commands);
        var fields = New("DirectorImGuiForm");
        Action noop = () => { };
        Call(fields, "Accordion", "相机属性", true, (Action<bool>)(_ => { }));
        Call(fields, "Text", "名称", "导演相机", (Action<string>)(_ => { }), false);
        Call(fields, "Number", "位置 X", 12.5, (Action<double>)(_ => { }));
        Call(fields, "Slider", "视野", 70d, 30d, 120d, 1d, (Action<double>)(_ => { }));
        Call(fields, "Check", "显示轨迹", true, (Action<bool>)(_ => { }), true);
        AccessTools.Method(fields.GetType(), "Select").Invoke(fields,
            ["插值", "smooth", new[] { ("smooth", "平滑"), ("linear", "线性") }, (Action<string>)(_ => { })]);
        Call(fields, "Detail", "选中关键帧", "00:00:12.500");
        Call(fields, "Group", "回放资源");
        Call(fields, "Resource", "sample", "山谷回放", "1920 × 1080 · 00:30", true, noop, noop, "打开回放", "video");
        Call(fields, "Resource", "long", "带有较长名称的测试回放资源", "验证资源说明在窄窗口中能够换行显示，并且不挤占右侧操作按钮。", false, noop, noop, "打开回放", "video");
        var selector = New("DirectorImGuiForm");
        Call(selector, "ChoiceList", (object)new (string Id, string Label, Action Click, string Icon)[]
        {
            ("camera", "相机", noop, "plus"), ("position", "位置", noop, "plus"),
            ("rotation", "旋转", noop, "plus"), ("fov", "视野", noop, "plus"),
            ("time", "时间", noop, "plus"), ("weather", "天气", noop, "plus"),
            ("dof", "景深", noop, "plus")
        });

        var io = ImGui.GetIO();
        Vector2 displaySize = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        var style = ImGui.GetStyle();
        Vector2 padding = style.FramePadding, spacing = style.ItemSpacing;
        Vector2 savedAlignment = style.ButtonTextAlign;
        style.ButtonTextAlign = new Vector2(1, 0);
        float rounding = style.WindowRounding, disabledAlpha = style.DisabledAlpha;
        Vector4[] colors = Enumerable.Range(0, (int)ImGuiCol.COUNT).Select(i => style.Colors[i]).ToArray();
        Vector4 Bounds(string id)
        {
            var element = Call(document, "GetElementById", id)!;
            var bounds = AccessTools.Property(element.GetType(), "Bounds").GetValue(element)!;
            float Read(string property) => (float)AccessTools.Property(bounds.GetType(), property).GetValue(bounds)!;
            return new Vector4(Read("X"), Read("Y"), Read("Width"), Read("Height"));
        }
        void Frame(float width, Vector2 pointer, bool down)
        {
            io.MousePos = pointer;
            io.MouseDown[0] = down;
            ImGui.NewFrame();
            using (var scope = (IDisposable)AccessTools.Method(Runtime("DirectorImGuiRuntime"), "UseFont").Invoke(null, [false])!)
            {
                if (style.ButtonTextAlign != new Vector2(.5f))
                    throw new Exception("Director buttons inherited another mod's text alignment.");
                ImGui.SetNextWindowPos(new Vector2(12, 12));
                ImGui.SetNextWindowSize(new Vector2(width, 34 * RuntimeEnv.GUIScale));
                ImGui.Begin("Toolbar###editor-controls", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                ImGui.SetWindowFontScale((float)GuiElement.scaled(1));
                Call(toolbar, "Draw");
                ImGui.End();
                ImGui.SetNextWindowPos(new Vector2(12, 96));
                ImGui.SetNextWindowSize(new Vector2(430, 520));
                ImGui.Begin("VS Director · 属性###editor-fields", ImGuiWindowFlags.NoSavedSettings);
                ImGui.SetWindowFontScale((float)GuiElement.scaled(1));
                Call(fields, "Draw");
                ImGui.End();
                ImGui.SetNextWindowPos(new Vector2(460, 96));
                ImGui.SetNextWindowSize(new Vector2(280, 330));
                ImGui.Begin("添加元素###editor-selector", ImGuiWindowFlags.NoSavedSettings);
                ImGui.SetWindowFontScale((float)GuiElement.scaled(1));
                Call(selector, "Draw");
                ImGui.End();
            }
            if (style.FramePadding != padding || style.ItemSpacing != spacing || style.WindowRounding != rounding
                || style.ButtonTextAlign != new Vector2(1, 0)
                || style.DisabledAlpha != disabledAlpha || colors.Where((color, i) => color != style.Colors[i]).Any())
                throw new Exception("Director leaked its theme into the shared VSImGui context.");
            ImGui.Render();
        }
        void Click(string id, float width)
        {
            var bounds = Bounds(id);
            if (bounds.Z <= 0 || bounds.W <= 0) throw new Exception("Missing editor command bounds: " + id);
            Vector2 center = new(bounds.X + bounds.Z / 2, bounds.Y + bounds.W / 2);
            Frame(width, center, false);
            Frame(width, center, true);
            Frame(width, center, false);
        }
        try
        {
            io.DisplaySize = new Vector2(960, 640);
            int iteration = 0;
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                iteration++;
                RuntimeEnv.GUIScale = scale;
                Frame(700, new Vector2(-100), false);
                Frame(700, new Vector2(-100), false);
                var first = Bounds("first");
                float toolbarCenter = 12 + 17 * scale;
                if (Math.Abs(first.Y + first.W / 2 - toolbarCenter) > 1)
                    throw new Exception("Workbench toolbar is not vertically centered at scale " + scale + ": " + first);
                Click("first", 700);
                Click("second", 700); // Identical captions must have distinct IDs.
                Click("disabled", 700);
                Frame(60 * scale, new Vector2(-100), false);
                if (Bounds("first").Z != 0 || Bounds("second").Z != 0 || Bounds("disabled").Z != 0)
                    throw new Exception("Collapsed toolbar commands retained stale hover bounds.");
                var overflow = Bounds("toolbar-overflow");
                if (Math.Abs(overflow.Y + overflow.W / 2 - toolbarCenter) > 1)
                    throw new Exception("Workbench overflow button is not vertically centered at scale " + scale);
                Click("toolbar-overflow", 60 * scale);
                Frame(60 * scale, new Vector2(-100), false);
                Click("second", 60 * scale);
                if (clicks[0] != iteration || clicks[1] != iteration * 2 || clicks[2] != 0)
                    throw new Exception("Editor command click, overflow or disabled handling failed: " + string.Join(",", clicks));
            }
            RuntimeEnv.GUIScale = 1;
            Frame(700, new Vector2(-100), false);
            Frame(700, new Vector2(-100), false);
            Console.WriteLine("PASS merged editor icons, property/resource controls, distinct repeated buttons, vertically centered toolbar/overflow, disabled actions and shared theme restoration at 100/150/200% scale");
        }
        finally
        {
            RuntimeEnv.GUIScale = savedScale;
            io.DisplaySize = displaySize;
            Call(document, "Dispose");
            style.ButtonTextAlign = savedAlignment;
        }
    }

    private static void CheckNativeCoordinates(Action<Action<string>> checkEditor)
    {
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            ClientSize = new OpenTK.Mathematics.Vector2i(960, 640),
            APIVersion = new Version(3, 3)
        });
        window.MakeCurrent();
        var rendererType = typeof(ImGuiController_OpenTK.ImGuiController).Assembly.GetType("ImGuiController_OpenTK.ImGuiRenderer", true)!;
        using var renderer = (IDisposable)Activator.CreateInstance(rendererType, ImGui.GetMainViewport(), window)!;
        var setupProjection = AccessTools.Method(rendererType, "SetupOrthographicProjection");
        var setupScissor = AccessTools.Method(rendererType, "SetupScissor");
        var collectMouse = AccessTools.Method(typeof(ImGuiController_OpenTK.ImGuiController), "CollectMousePosition");
        var controller = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImGuiController_OpenTK.ImGuiController));
        var mainField = AccessTools.Field(controller.GetType(), "mMainWindow");
        var mainWindow = ProbeDispatchProxy.Create<ImGuiController_OpenTK.IImGuiWindow>((m, _) =>
            m.Name == "get_Native" ? window : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        mainField.SetValue(controller, mainWindow);
        ImGui.NewFrame();
        ImGui.Begin("coordinate-probe"); ImGui.TextUnformatted("probe"); ImGui.End();
        ImGui.Render();
        foreach (var offset in new[] { new OpenTK.Mathematics.Vector2i(80, 120), new OpenTK.Mathematics.Vector2i(420, 260) })
        {
            window.ClientLocation = offset;
            setupScissor.Invoke(null, [window, new Vector4(30, 40, 230, 140)]);
            int[] clip = new int[4];
            GL.GetInteger(GetPName.ScissorBox, clip);
            if (!clip.SequenceEqual(new[] { 30, 500, 200, 100 }))
                throw new Exception("Native ImGui clipping depends on the desktop window location: " + string.Join(",", clip));
            setupProjection.Invoke(renderer, [window, ImGui.GetDrawData()]);
            float[] projection = new float[16];
            GL.GetUniform((int)AccessTools.Field(rendererType, "mShader").GetValue(renderer)!,
                (int)AccessTools.Field(rendererType, "mShaderProjectionMatrixLocation").GetValue(renderer)!, projection);
            if (Math.Abs(projection[12] + 1) > .001 || Math.Abs(projection[13] - 1) > .001)
                throw new Exception("Native ImGui projection depends on the desktop window location.");
            var mouse = (Vector2)collectMouse.Invoke(controller, [false])!;
            if (Vector2.Distance(mouse, new Vector2(window.MouseState.X, window.MouseState.Y)) > 1)
                throw new Exception("Native ImGui mouse input depends on the desktop window location.");
        }
        Console.WriteLine("PASS real hidden OpenGL window: ImGui projection, clipping and mouse input stay in client coordinates after moving");
        checkEditor(Capture);
        Capture("");
        void Capture(string suffix)
        {
            string? snapshot = Environment.GetEnvironmentVariable("VSDIRECTOR_UI_SNAPSHOT");
            if (string.IsNullOrEmpty(snapshot)) return;
            if (suffix.Length > 0) snapshot = Path.Combine(Path.GetDirectoryName(snapshot)!,
                Path.GetFileNameWithoutExtension(snapshot) + "-" + suffix + Path.GetExtension(snapshot));
            GL.Viewport(0, 0, 960, 640);
            GL.ClearColor(.09f, .09f, .12f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            var displaySize = ImGui.GetIO().DisplaySize;
            ImGui.GetIO().DisplaySize = new Vector2(960, 640);
            try { AccessTools.Method(rendererType, "Render").Invoke(renderer, null); }
            finally { ImGui.GetIO().DisplaySize = displaySize; }
            byte[] pixels = new byte[960 * 640 * 4];
            GL.ReadPixels(0, 0, 960, 640, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            using var stream = File.Create(snapshot);
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(960); writer.Write(640); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(pixels);
        }
    }
}
