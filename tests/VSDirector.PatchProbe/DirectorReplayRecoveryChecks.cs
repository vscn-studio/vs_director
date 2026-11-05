using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckReplayKeyframeWindow(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        Type hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        object hud = RuntimeHelpers.GetUninitializedObject(hudType);
        void Set(string name, object value) => AccessTools.Field(hudType, name).SetValue(hud, value);
        object? Call(object instance, string name, params object?[] args)
            => AccessTools.Method(instance.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(instance, args);
        Set("capi", api);
        Set("owner", new VSDirectorModSystem());
        object legacy = Activator.CreateInstance(assembly.GetType("VSDirector.Gui.DirectorWorkspaceFloatingEditorDialog", true)!, api, hud)!;
        Set("floatingEditorDialog", legacy);
        Set("keyframeRmlValues", new Dictionary<string, string>());
        var clip = new DirectorReplayViewportClip { Name = "Camera keyframe", SourceType = DirectorReplayViewportSourceType.StaticCamera, CameraPose = new CameraPreset() };
        Set("replayViewportDraftTimeline", new DirectorReplayViewportTimeline { Clips = [clip] });
        Set("selectedReplayViewportClipId", clip.Id);
        var kind = AccessTools.Field(hudType, "replayFloatingEditorKind");
        kind.SetValue(hud, Enum.Parse(kind.FieldType, "CameraKeyframe"));
        var io = ImGui.GetIO();
        Vector2 savedDisplay = io.DisplaySize;
        float savedScale = RuntimeEnv.GUIScale;
        try
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                io.DisplaySize = new Vector2(1920, 1080);
                Set("keyframeAnchorX", 940f / scale);
                Set("keyframeAnchorY", 800f / scale);
                kind.SetValue(hud, Enum.Parse(kind.FieldType, "CameraKeyframe"));
                Call(hud, "ComposeKeyframeRml", true);
                object window = AccessTools.Field(hudType, "keyframeRmlWindow").GetValue(hud)!;
                Vector2 position = default, size = default;
                bool collapsed = false;
                void Frame()
                {
                    ImGui.NewFrame();
                    Call(window, "Draw", 1f / 60);
                    ImGui.Begin("###vsdirector-keyframe");
                    position = ImGui.GetWindowPos(); size = ImGui.GetWindowSize();
                    collapsed = ImGui.IsWindowCollapsed();
                    ImGui.End(); ImGui.Render();
                }
                io.MousePos = new Vector2(-100);
                for (int frame = 0; frame < 40; frame++) Frame();
                if (collapsed || size.Y < Math.Min(600 * scale, io.DisplaySize.Y - 48 * scale) || size.X < 400 * scale
                    || position.Y < 0 || position.Y + size.Y > io.DisplaySize.Y + 1
                    || !(bool)AccessTools.Property(window.GetType(), "IsOpened").GetValue(window)!)
                    throw new Exception($"Replay keyframe window collapsed, shrank or escaped the screen at {scale}: {position}/{size}.");
                Vector2 before = size;
                // Shrink, leaving room even when the initial form fills the viewport.
                io.MousePos = position + size - new Vector2(4); Frame();
                io.MouseDown[0] = true; Frame();
                io.MousePos -= new Vector2(65, 80); Frame(); Frame();
                io.MouseDown[0] = false; Frame();
                if (size.X > before.X - 30 || size.Y > before.Y - 40)
                    throw new Exception("Replay property window cannot be resized from its bottom-right corner.");
                Vector2 resized = size;
                io.MousePos = position + new Vector2(100, 10); Frame();
                io.MouseDown[0] = true; Frame();
                io.MousePos += new Vector2(-100, -20); Frame(); Frame();
                io.MouseDown[0] = false; Frame();
                Vector2 moved = position;
                Call(hud, "ComposeKeyframeRml", true);
                for (int frame = 0; frame < 8; frame++) Frame();
                if (Vector2.Distance(resized, size) > 1 || Vector2.Distance(moved, position) > 1)
                    throw new Exception("Refreshing the keyframe editor reset its user-selected size or position.");
                io.MousePos = position + new Vector2(100, 10);
                for (int click = 0; click < 2; click++)
                {
                    io.MouseDown[0] = true; Frame();
                    io.MouseDown[0] = false; Frame();
                }
                if (collapsed || Vector2.Distance(resized, size) > 1)
                    throw new Exception("Double-clicking the keyframe title collapsed its property contents.");
                capture($"replay-keyframe-{scale * 100:0}");
                Call(window, "Close");
            }
            Console.WriteLine("PASS real replay keyframe composer stays open for 40 frames, fits the screen, resizes and preserves user bounds on refresh at 100/150/200%");
        }
        finally
        {
            Call(hud, "DisposeKeyframeRml");
            ((IDisposable)legacy).Dispose();
            io.MouseDown[0] = false; io.MousePos = new Vector2(-100);
            io.DisplaySize = savedDisplay; RuntimeEnv.GUIScale = savedScale;
        }
    }

    private static object? recoveryHud;
    private static Action<bool>? recoveryVisibility;
    private static bool RecoveryOpened(object __instance) => RecoveryVisibility(__instance, true);
    private static bool RecoveryClosed(object __instance) => RecoveryVisibility(__instance, false);
    private static bool RecoveryVisibility(object instance, bool opened)
    {
        if (!ReferenceEquals(instance, recoveryHud)) return true;
        recoveryVisibility!(opened);
        return false;
    }

    private static void CheckExportWorkspaceRecovery(Assembly assembly, ICoreClientAPI api)
    {
        Type TypeOf(string name) => assembly.GetType("VSDirector." + name, true)!;
        void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        object? Call(object target, string name, params object?[] args)
            => AccessTools.Method(target.GetType(), name, args.Select(a => a!.GetType()).ToArray()).Invoke(target, args);
        var loaded = new List<GuiDialog>();
        var gui = ProbeDispatchProxy.Create<IGuiAPI>((m, a) =>
        {
            if (m.Name == "get_LoadedGuis") return loaded;
            if (m.Name == "RegisterDialog") loaded.AddRange((GuiDialog[])a![0]!);
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var fixtureApi = ProbeDispatchProxy.Create<ICoreClientAPI>((m, a) => m.Name switch
        {
            "get_Gui" => gui,
            "get_Logger" => logger,
            _ => m.Invoke(api, a)
        });
        var owner = new VSDirectorModSystem();
        var hud = (GuiDialog)RuntimeHelpers.GetUninitializedObject(TypeOf("Gui.DirectorWorkspaceHud"));
        Set(hud, "capi", fixtureApi);
        object controller = RuntimeHelpers.GetUninitializedObject(TypeOf("Runtime.DirectorCameraController"));
        object permissions = RuntimeHelpers.GetUninitializedObject(TypeOf("Networking.DirectorPermissionClient"));
        Set(permissions, "<IsAdmin>k__BackingField", true);
        Set(owner, "permissionClient", permissions);
        Set(owner, "library", new CameraLibrary());
        Set(owner, "controller", controller);
        Set(owner, "workspace", hud);
        object exporter = RuntimeHelpers.GetUninitializedObject(TypeOf("Replay.DirectorReplayMultiTrackExporter"));
        Set(exporter, "capi", fixtureApi);
        Set(exporter, "statusLock", new object());
        object manager = RuntimeHelpers.GetUninitializedObject(TypeOf("Replay.DirectorReplayManager"));
        Set(manager, "exporter", exporter);
        Set(manager, "independentVoiceExportGate", new object());
        Set(owner, "replay", manager);
        object native = Activator.CreateInstance(TypeOf("Gui.DirectorImGuiWorkspace"), fixtureApi)!;
        Call(native, "Add", (Action)(() => ImGui.TextUnformatted("Recovered workspace")));
        var harmony = new Harmony("vsdirector.probe.export-recovery");
        var hook = AccessTools.Property(TypeOf("Replay.DirectorReplayRuntime"), "UpdateCameraForRenderFrame");
        object? savedHook = hook.GetValue(null);
        var offscreen = AccessTools.Field(TypeOf("Rendering.DirectorOffscreenRenderRuntime"), "currentScope");
        int opens = 0;
        recoveryHud = hud;
        // Replace only expensive workspace composition, retaining real GuiDialog
        // open/close, native ImGui visibility and controller input ownership.
        recoveryVisibility = opened =>
        {
            if (opened) opens++;
            Call(native, opened ? "Open" : "Close");
            Call(controller, "SetWorkspaceInputOverride", opened);
        };
        harmony.Patch(AccessTools.Method(hud.GetType(), "OnGuiOpened"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(RecoveryOpened)));
        harmony.Patch(AccessTools.Method(hud.GetType(), "OnGuiClosed"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(RecoveryClosed)));
        hook.SetValue(null, AccessTools.Method(typeof(VSDirectorModSystem), "PrepareReplayWorkspaceForRenderFrame").CreateDelegate<Action>(owner));
        void Frame() => AccessTools.Method(TypeOf("Runtime.ReplayBeforeCameraPatch"), "Prefix").Invoke(null, [1f / 60]);
        try
        {
            foreach (string terminal in new[] { "complete", "stop", "fail", "audio-complete", "audio-fail" })
            {
                hud.TryOpen(false);
                Type sessionType = exporter.GetType().GetNestedType("ExportSession", BindingFlags.NonPublic)!;
                object session = RuntimeHelpers.GetUninitializedObject(sessionType);
                Set(session, "<Tracks>k__BackingField", Activator.CreateInstance(AccessTools.Property(sessionType, "Tracks").PropertyType));
                Set(session, "<Cancellation>k__BackingField", new CancellationTokenSource());
                Set(session, "<CameraRestored>k__BackingField", true);
                Set(session, "<OutputDirectory>k__BackingField", Path.GetTempPath());
                Set(exporter, "session", session);
                Call(owner, "SuspendWorkspaceForReplayExport");
                Frame(); Frame();
                if (hud.IsOpened()) throw new Exception("Workspace reopened before export/audio finalization ended.");
                if (terminal == "complete") Call(exporter, "CompleteCapture", session);
                else if (terminal == "stop") Call(exporter, "Stop");
                else if (terminal == "fail") Call(exporter, "Fail", session, "probe failure");
                else
                {
                    ((CancellationTokenSource)AccessTools.Property(sessionType, "Cancellation").GetValue(session)!).Dispose();
                    Call(exporter, "CompleteAudioFinalization", session, terminal == "audio-fail" ? "probe audio failure" : "");
                }
                offscreen.SetValue(null, RuntimeHelpers.GetUninitializedObject(offscreen.FieldType));
                try { Frame(); if (hud.IsOpened()) throw new Exception("Workspace reopened inside an export render pass."); }
                finally { offscreen.SetValue(null, null); }
                int before = opens;
                Frame(); Frame();
                if (!hud.IsOpened() || opens != before + 1
                    || !(bool)AccessTools.Field(controller.GetType(), "workspaceInputOverride").GetValue(controller)!)
                    throw new Exception("Export terminal path did not restore workspace and input without a game tick: " + terminal);
                ImGui.NewFrame(); Call(native, "Draw", 1f / 60); ImGui.Render();
                if (ImGui.GetDrawData().TotalVtxCount == 0) throw new Exception("Restored workspace has no visible native UI.");
                hud.TryClose();
                Frame();
                if (hud.IsOpened()) throw new Exception("Completed recovery keeps reopening a manually closed workspace.");
                Call(hud, "ToggleOpen");
                if (!hud.IsOpened()) throw new Exception("Workspace cannot be manually reopened after export.");
                hud.TryClose();
            }
            foreach (string guard in new[] { "permission", "shutdown", "replaced" })
            {
                Set(owner, "replayExportWorkspace", hud);
                if (guard == "permission") Set(permissions, "<IsAdmin>k__BackingField", false);
                if (guard == "shutdown") Set(owner, "disposed", true);
                if (guard == "replaced") Set(owner, "workspace", null);
                Frame();
                if (hud.IsOpened()) throw new Exception("Export recovery reopened an unavailable workspace: " + guard);
                Set(permissions, "<IsAdmin>k__BackingField", true);
                Set(owner, "disposed", false);
                Set(owner, "workspace", hud);
            }
            Console.WriteLine("PASS render-frame workspace/input recovery without game ticks after completion, cancellation, failure and audio finalization; skips export passes and supports manual reopen");
        }
        finally
        {
            offscreen.SetValue(null, null);
            hud.TryClose();
            harmony.UnpatchAll(harmony.Id);
            recoveryHud = null; recoveryVisibility = null;
            hook.SetValue(null, savedHook);
            ((IDisposable)native).Dispose();
        }
    }
}
