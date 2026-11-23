using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Common;
using VSImGui;
using VSImGui.API;

namespace VSDirector.Gui;

/// <summary>Supplements the game's default ImGui font before texture upload.</summary>
internal static class DirectorImGuiRuntime
{
    private static Harmony? initialization;
    private static string iconFontPath = "";
    private static nint iconRanges;

    internal static void Initialize(ICoreClientAPI api, Mod mod)
    {
        // VSImGui reads this in StartPre. Changing flags during Draw is too late:
        // mouse coordinates and native platform windows already use desktop space.
        var config = api.LoadModConfig<ImGuiConfig>("imgui.json") ?? new ImGuiConfig();
        config.Version = Math.Max(2, config.Version);
        config.MultiViewportSupport = false;
        api.StoreModConfig(config, "imgui.json");

        iconFontPath = Path.Combine(((ModContainer)mod).FolderPath, "assets", "vsdirector", "fonts", "tabler", "tabler-icons.ttf");
        if (!File.Exists(iconFontPath)) throw new FileNotFoundException("VS Director icon font is missing", iconFontPath);
        DirectorImGuiTextFont.Initialize(api);
        initialization = new Harmony("vscn.vsdirector.imgui-initialization");
        InstallFontPatches(initialization);
        InstallBackendPatches(initialization);
    }

    internal static void InstallFontPatches(Harmony harmony)
    {
        // FontSettings rebuilds the shared atlas without calling LoadFonts.
        // Restore text and icons on initial creation and subsequent uploads.
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("ImGuiController_OpenTK.ImGuiRenderer"), "RecreateFontDeviceTexture"),
            prefix: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(PrepareFontTexture)));
    }

    internal static void InstallBackendPatches(Harmony harmony)
    {
        // Set the host order before VSImGui first registers/opens its dialog.
        harmony.Patch(AccessTools.PropertyGetter(typeof(GuiDialog), nameof(GuiDialog.DrawOrder)),
            postfix: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(HostDrawOrder)));
        // Opening Replay constructs its timeline inside a draw callback. VSImGui
        // 1.2.7 enumerates the live dictionary; registration aborts Update before
        // ImGui.Render, then its swallowed exception leaves subsequent frames blank.
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VSImGui.DrawCallbacksManager"), "Draw"),
            transpiler: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(StableDrawCallbacks)));
        // The backend queues physical keys but only assigns legacy modifier fields.
        // NewFrame replaces those fields from Mod* events, losing Ctrl+A/C/V/X.
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("ImGuiController_OpenTK.ImGuiController"), "CollectKeysInputs"),
            postfix: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(QueueModifierEvents)));
        foreach (string method in new[] { "KeyboardCaptured", "MouseCaptured", "MouseMovesCaptured" })
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VSImGui.Controller"), method),
                prefix: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(PerformanceInput)));
        harmony.Patch(AccessTools.PropertyGetter(AccessTools.TypeByName("VSImGui.VSImGuiDialog"), "PrefersUngrabbedMouse"),
            postfix: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(PerformanceMouseGrab)));
        // VSImGui 1.2.7 always adds the native window's desktop location, even
        // with ViewportsEnable off. Normalize the shared origin in input,
        // projection and clipping so moving the game window cannot offset UI.
        foreach (var (type, method) in new[]
        {
            ("ImGuiController_OpenTK.ImGuiController", "CollectMousePosition"),
            ("ImGuiController_OpenTK.ImGuiRenderer", "SetupOrthographicProjection"),
            ("ImGuiController_OpenTK.ImGuiRenderer", "SetupScissor")
        })
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName(type), method),
                transpiler: new HarmonyMethod(typeof(DirectorImGuiRuntime), nameof(ClientCoordinates)));
    }

    private static bool PerformanceInput(ref bool __result)
    {
        var creation = VSDirector.Runtime.DirectorCreationRuntime.Current;
        if (creation?.ControllingActor != true && creation?.NativeAppearanceEditorOpen != true) return true;
        __result = false; return false;
    }
    private static void PerformanceMouseGrab(ref bool __result)
    { if (VSDirector.Runtime.DirectorCreationRuntime.Current?.ControllingActor == true) __result = false; }
    private static void QueueModifierEvents()
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, io.KeyCtrl);
        io.AddKeyEvent(ImGuiKey.ModShift, io.KeyShift);
        io.AddKeyEvent(ImGuiKey.ModAlt, io.KeyAlt);
        io.AddKeyEvent(ImGuiKey.ModSuper, io.KeySuper);
    }

    private static IEnumerable<CodeInstruction> StableDrawCallbacks(IEnumerable<CodeInstruction> instructions)
    {
        var enumerate = AccessTools.Method(typeof(Dictionary<int, DrawCallbackDelegate>), "GetEnumerator");
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(enumerate))
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DirectorImGuiRuntime), nameof(SnapshotDrawCallbacks)))
                    .WithLabels(instruction.ExtractLabels()).WithBlocks(instruction.ExtractBlocks());
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("VS Director: VSImGui callback dispatch has changed.");
    }

    private static Dictionary<int, DrawCallbackDelegate> SnapshotDrawCallbacks(Dictionary<int, DrawCallbackDelegate> callbacks)
    {
        var snapshot = new Dictionary<int, DrawCallbackDelegate>(callbacks.Count);
        foreach (var (id, draw) in callbacks)
        {
            snapshot.Add(id, delta =>
            {
                // Removed/disposed windows must not draw or update _wasClosed.
                // An unrecognized status is ignored by the backend's switch.
                if (!callbacks.ContainsKey(id)) return (CallbackGUIStatus)(-1);
                var status = draw(delta);
                return callbacks.ContainsKey(id) ? status : (CallbackGUIStatus)(-1);
            });
        }
        return snapshot;
    }

    private static IEnumerable<CodeInstruction> ClientCoordinates(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is MethodInfo { Name: "get_ClientLocation", DeclaringType.FullName: "OpenTK.Windowing.Desktop.NativeWindow" })
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DirectorImGuiRuntime), nameof(ClientOrigin)));
        }
    }

    private static OpenTK.Mathematics.Vector2i ClientOrigin(OpenTK.Mathematics.Vector2i desktopLocation)
        => (ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0 ? default : desktopLocation;

    // Above the workspace backdrop (.34), below the escape menu (.89).
    private static void HostDrawOrder(GuiDialog __instance, ref double __result)
    {
        if (__instance.GetType().FullName == "VSImGui.VSImGuiDialog") __result = .85;
    }

    private static unsafe void PrepareFontTexture()
    {
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        var atlas = ImGui.GetIO().Fonts;
        if (!atlas.IsBuilt() && !atlas.Build()) throw new InvalidOperationException("Game font atlas build failed.");
        var font = CurrentDefaultFont();
        if (font.NativePtr == null) return;
        bool textAdded = DirectorImGuiTextFont.MergeMissingGlyphs(atlas, font);
        if (DirectorImGuiIcons.HasGlyphs(font))
        {
            if (textAdded && !atlas.Build()) throw new InvalidOperationException("VS Director font atlas build failed.");
            DirectorImGuiIcons.CenterGlyphs(font);
            return;
        }
        if (iconRanges == 0) iconRanges = DirectorImGuiIcons.CreateRanges();
        var config = new ImFontConfigPtr(CreateFontConfig());
        try
        {
            config.MergeMode = true;
            // Merge into the live default, not the last font in the atlas.
            config.NativePtr->DstFont = font.NativePtr;
            config.PixelSnapH = true;
            config.OversampleH = config.OversampleV = 1;
            config.GlyphMinAdvanceX = font.FontSize;
            config.GlyphMaxAdvanceX = font.FontSize;
            config.GlyphOffset = new Vector2(0, 2);
            atlas.AddFontFromFileTTF(iconFontPath, font.FontSize, config, iconRanges);
        }
        finally { config.Destroy(); }
        // Only icon quads are centered; the game's text metrics stay intact.
        if (!atlas.Build()) throw new InvalidOperationException("VS Director font atlas build failed.");
        DirectorImGuiIcons.CenterGlyphs(font);
    }

    private static unsafe ImFontPtr CurrentDefaultFont()
    {
        var io = ImGui.GetIO();
        var fonts = io.Fonts.Fonts;
        // Resolve afresh on every use. FontSettings can free/recreate every font
        // while keeping the same context and atlas, even reusing old addresses.
        // Check membership before using FontDefault, which may itself be stale.
        for (int i = 0; i < fonts.Size; i++)
            if (fonts[i].NativePtr == io.NativePtr->FontDefault) return fonts[i];
        return fonts.Size > 0 ? fonts[0] : default;
    }

    [DllImport("cimgui", EntryPoint = "ImFontConfig_ImFontConfig", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CreateFontConfig();

    internal static unsafe FontScope UseFont(bool dialog = true)
    {
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        if (dialog)
        {
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f));
        }
        var font = CurrentDefaultFont();
        bool pushed = font.NativePtr != null;
        if (pushed) ImGui.PushFont(font);
        return new FontScope(pushed, DirectorImGuiTheme.Use());
    }

    internal readonly struct FontScope(bool pushed, DirectorImGuiTheme.Scope theme) : IDisposable
    {
        public void Dispose() { theme.Dispose(); if (pushed) ImGui.PopFont(); }
    }

    internal static void Dispose()
    {
        initialization?.UnpatchAll(initialization.Id);
        initialization = null;
        iconFontPath = "";
        DirectorImGuiTextFont.Dispose();
        if (iconRanges != 0) Marshal.FreeHGlobal(iconRanges);
        iconRanges = 0;
    }
}

/// <summary>Director dialogs share the same viewport/font and always balance Begin/End.</summary>
internal abstract class DirectorImGuiDialog : ImGuiDialogWindow
{
    protected DirectorImGuiDialog(ICoreClientAPI api, ImGuiWindowFlags flags, string title, string id = "", bool includeTitleIntoId = true)
        : base(api, flags | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking, title, id, includeTitleIntoId) { }

    protected DirectorImGuiDialog(ICoreClientAPI api, string title, string id = "", bool includeTitleIntoId = true,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None) : this(api, flags, title, id, includeTitleIntoId) { }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        if (!Opened) return DrawStatus();
        using var font = DirectorImGuiRuntime.UseFont();
        PrepareWindow();
        bool opened = Opened;
        bool visible = ImGui.Begin($"{WindowTitle}###vsdirector-{WindowId}", ref opened, Flags);
        bool keepOpen = true;
        try
        {
            ImGui.SetWindowFontScale(DirectorImGuiTheme.Scale);
            if (visible) keepOpen = OnDraw();
        }
        finally { ImGui.End(); }
        if (!opened || !keepOpen) Close();
        return DrawStatus();
    }

    protected virtual void PrepareWindow() { }
}
