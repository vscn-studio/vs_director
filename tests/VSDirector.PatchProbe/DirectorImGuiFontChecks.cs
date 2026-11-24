using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Config;

internal static partial class DirectorImGuiProbe
{
    private static void ConfigureProbeFonts(Assembly assembly, string project)
    {
        AccessTools.Field(assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime"), "iconFontPath").SetValue(null,
            Path.Combine(project, "assets/vsdirector/fonts/tabler/tabler-icons.ttf"));
        var textFont = assembly.GetType("VSDirector.Gui.DirectorImGuiTextFont", true)!;
        string systemFont = (string)AccessTools.Method(textFont, "FindSystemFont").Invoke(null, null)!;
        if (!File.Exists(systemFont)) throw new Exception("Font regression checks require a system Chinese font.");
        AccessTools.Field(textFont, "FontPath").SetValue(null, systemFont);
    }

    internal static void RunFontChecks()
    {
        var assembly = typeof(VSDirector.VSDirectorModSystem).Assembly;
        var runtime = assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime", true)!;
        var harmony = new Harmony("vsdirector.probe.fonts");
        try
        {
            Assembly.Load("ImGuiController_OpenTK");
            ConfigureProbeFonts(assembly, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")));
            AccessTools.Method(runtime, "InstallFontPatches").Invoke(null, [harmony]);
            CheckDefaultFontLifecycle(assembly);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            AccessTools.Method(runtime, "Dispose").Invoke(null, null);
        }
    }

    private static unsafe void CheckDefaultFontLifecycle(Assembly assembly)
    {
        var runtime = assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime", true)!;
        var useFont = AccessTools.Method(runtime, "UseFont");
        var fontPathField = AccessTools.Field(assembly.GetType("VSDirector.Gui.DirectorImGuiTextFont"), "FontPath");
        string systemFont = (string)fontPathField.GetValue(null)!;
        nint savedContext = ImGui.GetCurrentContext();
        nint context = ImGui.CreateContext();
        float savedScale = RuntimeEnv.GUIScale;
        try
        {
            ImGui.SetCurrentContext(context);
            RuntimeEnv.GUIScale = 1;
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(960, 640);
            io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null;
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            string gamePath = Environment.GetEnvironmentVariable("VINTAGE_STORY")
                ?? Path.GetFullPath(Path.Combine(project, "../Vintagestory"));
            var first = io.Fonts.AddFontDefault();
            var selected = io.Fonts.AddFontFromFileTTF(Path.Combine(gamePath,
                "assets/game/fonts/Montserrat-Regular.ttf"), 18);
            io.NativePtr->FontDefault = selected.NativePtr;
            io.Fonts.Build();
            float textAdvance = selected.IndexAdvanceX['A'];
            if (selected.FindGlyphNoFallback('导').NativePtr != null)
                throw new Exception("Font regression probe must start without a Chinese font or FontSettings.");
            var chinese = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(project, "assets/vsdirector/lang/zh-cn.json")))!;
            var captions = chinese.Values.SelectMany(s => s).Where(c => !char.IsControl(c)).Distinct().ToArray();
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new OpenTK.Mathematics.Vector2i(960, 640),
                StartVisible = false,
                API = OpenTK.Windowing.Common.ContextAPI.OpenGL,
                APIVersion = new Version(3, 3)
            });
            window.Context.MakeCurrent();
            var rendererType = typeof(ImGuiController_OpenTK.ImGuiController).Assembly.GetType("ImGuiController_OpenTK.ImGuiRenderer", true)!;
            using var renderer = (IDisposable)Activator.CreateInstance(rendererType, ImGui.GetMainViewport(), window)!;
            var upload = AccessTools.Method(rendererType, "RecreateFontDeviceTexture");
            var icons = (Dictionary<string, string>)AccessTools.Field(assembly.GetType("VSDirector.Gui.DirectorImGuiIcons"), "Glyphs").GetValue(null)!;
            CheckIcons(selected);
            CheckChinese(selected);
            if (io.Fonts.Fonts.Size != 2 || selected.IndexAdvanceX['A'] != textAdvance
                || first.FindGlyphNoFallback(icons["play"][0]).NativePtr != null
                || first.FindGlyphNoFallback('导').NativePtr != null)
                throw new Exception("Icon merge replaced game text or targeted the wrong default font.");
            float savedHostScale = io.FontGlobalScale;
            try
            {
                io.FontGlobalScale = 1.1f;
                foreach (float scale in new[] { 1f, 1.5f, 2f, 1f })
                {
                    RuntimeEnv.GUIScale = scale;
                    Frame(selected, first);
                }
            }
            finally
            {
                RuntimeEnv.GUIScale = 1;
                io.FontGlobalScale = savedHostScale;
            }

            // Match FontSettings: clear the same atlas, replace the default,
            // build it, then ask the renderer to upload its new texture.
            foreach (float size in new[] { 18f, 24f, 18f })
            {
                io.Fonts.Clear();
                io.NativePtr->FontDefault = null;
                var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
                try
                {
                    config.SizePixels = size;
                    // Also simulate an external CJK default like FontSettings.
                    selected = size == 24f
                        ? io.Fonts.AddFontFromFileTTF(systemFont, size, config, io.Fonts.GetGlyphRangesChineseFull())
                        : io.Fonts.AddFontDefault(config);
                }
                finally { config.Destroy(); }
                io.NativePtr->FontDefault = selected.NativePtr;
                io.Fonts.Build();
                float beforeA = selected.IndexAdvanceX['A'];
                float beforeChinese = size == 24f ? selected.IndexAdvanceX['中'] : 0;
                // Before upload, UseFont must already use the replacement.
                Frame(selected, selected);
                upload.Invoke(renderer, null);
                CheckIcons(selected);
                CheckChinese(selected);
                if (selected.IndexAdvanceX['A'] != beforeA
                    || size == 24f && selected.IndexAdvanceX['中'] != beforeChinese)
                    throw new Exception("Director overwrote the host's existing Latin or CJK glyph metrics.");
                Frame(selected, selected);
                int configCount = io.Fonts.ConfigData.Size;
                upload.Invoke(renderer, null);
                if (io.Fonts.ConfigData.Size != configCount || io.Fonts.Fonts.Size != 1 || selected.FontSize != size)
                    throw new Exception("Repeated texture uploads duplicated fonts or changed the game font size.");
            }

            nint other = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(other);
                var otherIO = ImGui.GetIO();
                otherIO.DisplaySize = io.DisplaySize;
                otherIO.DeltaTime = io.DeltaTime;
                otherIO.NativePtr->IniFilename = null;
                var otherFont = otherIO.Fonts.AddFontDefault();
                otherIO.Fonts.Build();
                AccessTools.Method(runtime, "PrepareFontTexture").Invoke(null, null);
                CheckChinese(otherFont);
                CheckIcons(otherFont);
                Frame(otherFont, otherFont);
            }
            finally { ImGui.DestroyContext(other); ImGui.SetCurrentContext(context); }
            Frame(selected, selected);

            nint noSystemFont = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(noSystemFont);
                fontPathField.SetValue(null, "");
                var missingIO = ImGui.GetIO();
                missingIO.DisplaySize = io.DisplaySize;
                missingIO.DeltaTime = io.DeltaTime;
                missingIO.NativePtr->IniFilename = null;
                var latin = missingIO.Fonts.AddFontDefault();
                missingIO.Fonts.Build();
                AccessTools.Method(runtime, "PrepareFontTexture").Invoke(null, null);
                CheckIcons(latin);
                Frame(latin, latin);
                if (latin.FindGlyphNoFallback('中').NativePtr != null)
                    throw new Exception("Missing system font test unexpectedly found Chinese glyphs.");

                // FontSettings can supply its own CJK font even when Director
                // cannot resolve a system fallback. Do not disturb that font.
                missingIO.Fonts.Clear();
                missingIO.NativePtr->FontDefault = null;
                var external = missingIO.Fonts.AddFontFromFileTTF(systemFont, 18,
                    default, missingIO.Fonts.GetGlyphRangesChineseFull());
                missingIO.Fonts.Build();
                float advance = external.IndexAdvanceX['中'];
                AccessTools.Method(runtime, "PrepareFontTexture").Invoke(null, null);
                CheckIcons(external);
                if (external.FindGlyphNoFallback('导').NativePtr == null || external.IndexAdvanceX['中'] != advance)
                    throw new Exception("Host-supplied Chinese font was lost when no system fallback was available.");
                Frame(external, external);
            }
            finally
            {
                fontPathField.SetValue(null, systemFont);
                ImGui.DestroyContext(noSystemFont);
                ImGui.SetCurrentContext(context);
            }
            Console.WriteLine($"PASS system Chinese font ({Path.GetFileName(systemFont)}): all {captions.Length} translated characters available; Latin/CJK metrics preserved, 3 atlas rebuilds, external CJK font, idempotent uploads, context switches and missing-system-font handling");

            void CheckChinese(ImFontPtr liveFont)
            {
                foreach (char c in captions.Concat("自定义镜头龘，。！？【】→≤≥").Distinct())
                    if (liveFont.FindGlyphNoFallback(c).NativePtr == null)
                        throw new Exception($"Missing Chinese UI/input glyph: U+{(int)c:X4}.");
                ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out nint _, out int width, out int height);
                if (width > 8192 || height > 8192)
                    throw new Exception($"Chinese font atlas exceeds 8192: {width}x{height}.");
            }

            void CheckIcons(ImFontPtr liveFont)
            {
                foreach (string glyph in icons.Values)
                    if (liveFont.FindGlyphNoFallback(glyph[0]).NativePtr == null)
                        throw new Exception("Editor icon missing from the live game default font.");
            }

            void Frame(ImFontPtr expected, ImFontPtr outer)
            {
                ImGui.NewFrame();
                ImGui.PushFont(outer);
                float hostSize = ImGui.GetFontSize();
                float target = expected.FontSize * expected.Scale * ImGui.GetIO().FontGlobalScale * RuntimeEnv.GUIScale;
                using ((IDisposable)useFont.Invoke(null, [false])!)
                {
                    if (ImGui.GetFont().NativePtr != expected.NativePtr)
                        throw new Exception("Director retained a previous atlas/context font.");
                    ImGui.Begin("font-lifecycle");
                    CheckSize(target);
                    ImGui.TextUnformatted("Game default text / 中文工作台");
                    ImGui.BeginChild("body", new Vector2(100, 60));
                    CheckSize(target);
                    ImGui.EndChild();
                    using ((IDisposable)useFont.Invoke(null, [false])!)
                    {
                        ImGui.OpenPopup("font-menu");
                        if (!ImGui.BeginPopup("font-menu")) throw new Exception("Font check popup did not open.");
                        CheckSize(target);
                        ImGui.CloseCurrentPopup();
                        ImGui.EndPopup();
                    }
                    CheckSize(target);
                    ImGui.End();
                }
                CheckSize(hostSize);
                if (ImGui.GetFont().NativePtr != outer.NativePtr) throw new Exception("Director did not restore the outer font.");
                ImGui.PopFont();
                ImGui.Render();
            }

            static void CheckSize(float expected)
            {
                if (Math.Abs(ImGui.GetFontSize() - expected) > .01f)
                    throw new Exception($"Expected font size {expected}, got {ImGui.GetFontSize()}.");
            }
        }
        finally
        {
            RuntimeEnv.GUIScale = savedScale;
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(savedContext);
        }
    }
}
