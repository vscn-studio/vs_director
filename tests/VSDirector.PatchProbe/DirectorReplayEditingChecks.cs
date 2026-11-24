using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static object? editManager;
    private static bool SkipReplayHudUi() => false;
    private static bool SaveReplayEditProbe(object __instance, ref string error, ref bool __result)
    {
        if (!ReferenceEquals(__instance, editManager)) return true;
        error = ""; __result = true; return false;
    }

    internal static unsafe void RunReplayEditingChecks()
    {
        nint previous = ImGui.GetCurrentContext(), context = ImGui.CreateContext();
        float previousScale = RuntimeEnv.GUIScale;
        string previousAssets = GamePaths.AssetsPath;
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var system = new VSImGui.ImGuiModSystem();
        var loader = ProbeDispatchProxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? system : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_ModLoader" ? loader : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        try
        {
            var io = ImGui.GetIO(); io.DisplaySize = new(1280, 720); io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null; io.Fonts.AddFontDefault(); io.Fonts.Build();
            var logger = ProbeDispatchProxy.Create<ILogger>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, Path.GetFullPath(Path.Combine(project, "../Vintagestory/assets")));
            Lang.PreLoad(logger, GamePaths.AssetsPath, "en");
            CheckReplayStyleEdits(assembly, api);
            CheckReplayProgressOverlay(assembly, api);
        }
        finally
        {
            AccessTools.Property(typeof(GamePaths), nameof(GamePaths.AssetsPath)).SetValue(null, previousAssets);
            RuntimeEnv.GUIScale = previousScale; ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous);
        }
    }

    private static void CheckReplayStyleEdits(Assembly assembly, ICoreClientAPI api)
    {
        Type T(string name) => assembly.GetType("VSDirector." + name, true)!;
        object Bare(string name) => RuntimeHelpers.GetUninitializedObject(T(name));
        void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        object? Call(object target, string method, params object?[] args) => AccessTools.Method(target.GetType(), method).Invoke(target, args);
        var owner = new VSDirectorModSystem();
        var manager = Bare("Replay.DirectorReplayManager");
        var playbackType = manager.GetType().GetNestedType("Playback", BindingFlags.NonPublic)!;
        var playback = RuntimeHelpers.GetUninitializedObject(playbackType);
        var controller = Bare("Runtime.DirectorCameraController");
        bool freeCameraAllowed = false;
        Set(controller, "canUseFreeCamera", (Func<bool>)(() => freeCameraAllowed));
        var exporter = Bare("Replay.DirectorReplayMultiTrackExporter");
        Set(manager, "exporter", exporter); Set(manager, "independentVoiceExportGate", new object());
        Set(manager, "independentVoiceExportStatus", "idle"); Set(manager, "playback", playback);
        Set(manager, "capi", api); Set(manager, "controller", controller);
        Set(owner, "replay", manager); Set(owner, "controller", controller); Set(owner, "capi", api); Set(owner, "library", new CameraLibrary());
        var hud = Bare("Gui.DirectorWorkspaceHud"); Set(hud, "owner", owner); Set(hud, "capi", api);
        var hudType = hud.GetType();
        var pointType = hudType.GetNestedType("PresetPoint", BindingFlags.NonPublic)!;
        var contextType = hudType.GetNestedType("ReplayPresetContext", BindingFlags.NonPublic)!;
        var actor = Activator.CreateInstance(T("Replay.DirectorReplayActorPathSource"), "player", "Player", "uid", 1L, "game:player", 2, true)!;
        var point = Activator.CreateInstance(pointType, 0d, 2d, -10d, 0)!;
        var catalog = ((IEnumerable)AccessTools.Method(hudType, "ReplayPresets").Invoke(null, null)!).Cast<object>()
            .Where(p => ((string)AccessTools.Property(p.GetType(), "Id").GetValue(p)!).StartsWith("style-") || (string)AccessTools.Property(p.GetType(), "Id").GetValue(p)! is "weather-storm-mood" or "sky-green-screen").ToArray();
        var harmony = new Harmony("vsdirector.probe.replay-edits");
        handoffController = controller; editManager = manager;
        harmony.Patch(AccessTools.Method(controller.GetType(), "TryActivatePose"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(CaptureFreeCameraActivation)));
        harmony.Patch(AccessTools.Method(manager.GetType(), "PersistTimelineEdit"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(SaveReplayEditProbe)));
        try
        {
            var observerPose = new CameraPreset { X = 4, Y = 5, Z = 6 };
            if (Call(controller, "TryStartFreeCamera", new object?[] { null }) is not false)
                throw new Exception("Free camera activated without permission");
            Call(owner, "RestoreReplayObserverPose", observerPose);
            var restoredState = (DirectorReplayCameraState)AccessTools.Field(owner.GetType(), "replayCameraState").GetValue(owner)!;
            if (restoredState.Mode != DirectorReplayCameraMode.Fixed || (bool)AccessTools.Property(controller.GetType(), "IsFreeCamera").GetValue(controller)!)
                throw new Exception("Restoring the observer pose unlocked free camera without permission");
            freeCameraAllowed = true;
            foreach (var preset in catalog)
            foreach (double start in new[] { 0d, 18d, 20d })
            {
                var document = new DirectorReplayDocument { DurationSeconds = 20, ViewportTimeline = DirectorReplayViewportTimeline.Normalize(new(), 20) };
                Set(playback, "<Document>k__BackingField", document);
                var pose = new CameraPreset { X = 12, Y = 20, Z = 3, Yaw = 42, Pitch = 180, Roll = 7, Fov = 83 };
                Set(controller, "activeCamera", pose.DeepClone()); Set(controller, "preparedCameraPose", null);
                Call(owner, "ResetReplayElementRuntime");
                string before = JsonSerializer.Serialize(DirectorReplayTimelineEditIO.Create(document));
                var timeline = document.ViewportTimeline.DeepClone();
                var c = Activator.CreateInstance(contextType, hud, timeline, start, 20d, 83f, pose, point, actor, point, 42d, document)!;
                var baseline = JsonSerializer.Serialize(timeline.Visuals);
                var groupId = (Guid)AccessTools.Method(hudType, "ApplyPresetElements").Invoke(null, [preset, c])!;
                if (JsonSerializer.Serialize(timeline.Visuals) != baseline) throw new Exception("Preset mutated the base view");
                if (timeline.Tracks.Count(t => t.TrackType == DirectorReplayElementTrackType.PresetGroup) != 1
                    || timeline.Tracks.Any(t => t.Id != groupId && t.ParentGroupId != groupId)) throw new Exception("Preset escaped its group");
                var muted = timeline.DeepClone();
                muted.Tracks.Single(t => t.Id == groupId).Enabled = false;
                if (JsonSerializer.Serialize(DirectorReplayVisualElements.Evaluate(muted, start)) != baseline
                    || DirectorReplayCameraPriority.ResolveActive(muted, start) is not null)
                    throw new Exception("Muted preset retains effects");
                // Hidden camera elements must also disappear from editor models, handles and previews.
                Set(hud, "layout", new DirectorWorkspaceDocument());
                Set(hud, "transformTimeline", muted);
                ImGui.NewFrame(); ImGui.Begin("Hidden preset models");
                int vertices = ImGui.GetWindowDrawList().VtxBuffer.Size;
                Call(hud, "DrawCameraModels");
                if (ImGui.GetWindowDrawList().VtxBuffer.Size != vertices) throw new Exception("Hidden preset still draws camera geometry");
                ImGui.End(); ImGui.Render();
                Set(hud, "transformTimeline", null);
                var originalTimeline = document.ViewportTimeline;
                document.ViewportTimeline = muted;
                foreach (var clip in muted.Clips)
                    if (Call(hud, "ReplayCameraViewSubject", clip.Id) is not null) throw new Exception("Hidden preset still supplies a preview camera");
                document.ViewportTimeline = originalTimeline;
                var deleted = timeline.DeepClone();
                DirectorReplayViewportTimeline.RemoveTrack(deleted, groupId);
                deleted = DirectorReplayViewportTimeline.Normalize(deleted, 20);
                if (deleted.Tracks.Count != 0 || deleted.Clips.Count != 0 || deleted.ElementKeyframes.Count != 0)
                    throw new Exception("Deleted preset leaves children after normalization");
                object?[] commit = [timeline, null, false, null];
                if (Call(owner, "TryReplaceReplayViewportTimeline", commit) is not true) throw new Exception("Style commit failed");
                string after = JsonSerializer.Serialize(DirectorReplayTimelineEditIO.Create(document));
                if (after == before) throw new Exception("Style generated no edit");
                // Actual rendering path at a paused time must be idempotent.
                if (!timeline.Tracks.Any(t => t.TrackType is DirectorReplayElementTrackType.FollowEntity or DirectorReplayElementTrackType.TrackEntity))
                {
                    Set(playback, "<PreparedTimeSeconds>k__BackingField", start + .3);
                    Set(playback, "<PreparedCameraTimeSeconds>k__BackingField", start + .3);
                    Call(owner, "UpdateReplayViewportTimeline");
                    var once = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
                    for (int frame = 0; frame < 60; frame++) Call(owner, "UpdateReplayViewportTimeline");
                    var repeated = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
                    if (Math.Abs(once.Yaw-repeated.Yaw) > .001 || Math.Abs(once.Pitch-repeated.Pitch) > .001 || Math.Abs(once.Roll-repeated.Roll) > .001)
                        throw new Exception("Paused style accumulates camera effects");
                }
                // Undo must recover even if the preset moved into unloaded chunks.
                Set(manager, "playbackChunkBuffering", true);
                if (Call(owner, "RestoreReplayEdit", false) is not true || JsonSerializer.Serialize(DirectorReplayTimelineEditIO.Create(document)) != before)
                    throw new Exception("Undo did not restore every style setting");
                var restored = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
                if (restored.X != pose.X || restored.Y != pose.Y || restored.Z != pose.Z || restored.Fov != pose.Fov || restored.Roll != pose.Roll)
                    throw new Exception("Undo left the preset camera or FOV active while buffering");
                Set(manager, "playbackChunkBuffering", false);
                if (Call(owner, "RestoreReplayEdit", true) is not true || JsonSerializer.Serialize(DirectorReplayTimelineEditIO.Create(document)) != after)
                    throw new Exception("Redo did not restore the style");
            }
            // Exercise the actual View setter and ImGui shortcut while the View window owns focus.
            foreach (var method in new[] { "CloseReplayFloatingEditor", "ResetWorkspacePointerInteraction", "ComposeInspector", "ComposeTimeline" })
                harmony.Patch(AccessTools.Method(hudType, method), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(SkipReplayHudUi)));
            Set(hud, "layout", DirectorWorkspaceDocument.Normalize(new()));
            Set(hud, "section", DirectorWorkspaceSection.Replay);
            var viewDocument = new DirectorReplayDocument { DurationSeconds = 20,
                ViewportTimeline = DirectorReplayViewportTimeline.Normalize(new(), 20) };
            Set(playback, "<Document>k__BackingField", viewDocument);
            var descriptors = ((IEnumerable)AccessTools.Field(hudType, "ReplayVisualDescriptors").GetValue(null)!).Cast<object>();
            var weatherDescriptor = descriptors.Single(d => ((PropertyInfo)AccessTools.Property(d.GetType(), "ValueProperty").GetValue(d)!).Name == "Weather");
            Call(hud, "SetReplayVisualValue", weatherDescriptor, DirectorReplayWeatherMode.Storm, false);
            if (viewDocument.ViewportTimeline.Visuals.Environment.Weather != DirectorReplayWeatherMode.Storm) throw new Exception("View setter did not edit weather");
            void KeyFrame(bool ctrl, bool z, bool shift = false, bool textInput = false)
            {
                var io = ImGui.GetIO();
                io.AddKeyEvent(ImGuiKey.LeftCtrl, ctrl); io.AddKeyEvent(ImGuiKey.Z, z); io.AddKeyEvent(ImGuiKey.LeftShift, shift);
                ImGui.NewFrame(); ImGui.SetNextWindowFocus(); ImGui.Begin("View shortcut test");
                io.KeyCtrl = false; // Exercise the backend's individual-key modifier path.
                io.WantTextInput = textInput;
                ImGui.End(); Call(hud, "DrawReplayHistoryShortcuts"); ImGui.Render();
            }
            KeyFrame(false, false); KeyFrame(true, true);
            if (viewDocument.ViewportTimeline.Visuals.Environment.Weather != DirectorReplayWeatherMode.Recorded) throw new Exception("Ctrl+Z with View focus did not undo");
            KeyFrame(false, false); KeyFrame(true, true, true);
            if (viewDocument.ViewportTimeline.Visuals.Environment.Weather != DirectorReplayWeatherMode.Storm) throw new Exception("Ctrl+Shift+Z with View focus did not redo");
            KeyFrame(false, false); KeyFrame(true, true, textInput: true);
            if (viewDocument.ViewportTimeline.Visuals.Environment.Weather != DirectorReplayWeatherMode.Storm) throw new Exception("Workspace shortcut stole text-editor undo");
            KeyFrame(false, false);
            Console.WriteLine("PASS actual View weather edit + Ctrl+Z / Ctrl+Shift+Z with View focus and backend modifier fallback; text undo retains ownership");
            // Removing the movement row of a combined style used to expose a
            // feedback loop: the remaining shake re-used last frame's result.
            var isolated = new DirectorReplayDocument { DurationSeconds = 20, ViewportTimeline = DirectorReplayViewportTimeline.Normalize(new(), 20) };
            Set(playback, "<Document>k__BackingField", isolated);
            var original = new CameraPreset { X = 9, Y = 30, Z = 4, Yaw = 21, Pitch = 180, Roll = 7, Fov = 83 };
            Set(controller, "activeCamera", original.DeepClone()); Set(controller, "preparedCameraPose", null);
            Call(owner, "ResetReplayElementRuntime");
            var shakeTrack = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.CameraShake };
            isolated.ViewportTimeline.Tracks.Add(shakeTrack);
            isolated.ViewportTimeline.ElementKeyframes.Add(new() { TrackId = shakeTrack.Id, TrackType = shakeTrack.TrackType,
                AmplitudeX = 3, AmplitudeY = 2, FrequencyX = 2, FrequencyY = 3, Roll = 4 });
            var lensTrack = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Fov };
            isolated.ViewportTimeline.Tracks.Add(lensTrack);
            isolated.ViewportTimeline.ElementKeyframes.Add(new() { TrackId = lensTrack.Id, TrackType = lensTrack.TrackType, Fov = 35 });
            Set(playback, "<PreparedTimeSeconds>k__BackingField", .3d); Set(playback, "<PreparedCameraTimeSeconds>k__BackingField", .3d);
            Call(owner, "UpdateReplayViewportTimeline");
            var first = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            for (int i = 0; i < 120; i++) Call(owner, "UpdateReplayViewportTimeline");
            var last = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            if (Math.Abs(first.Yaw-last.Yaw) > .001 || Math.Abs(first.Pitch-last.Pitch) > .001 || Math.Abs(first.Roll-last.Roll) > .001)
                throw new Exception("Standalone shake accumulates after the movement row is removed");
            if (last.Fov != 35) throw new Exception("Lens effect was not applied");
            isolated.ViewportTimeline.Tracks.Remove(lensTrack);
            isolated.ViewportTimeline.ElementKeyframes.RemoveAll(k => k.TrackId == lensTrack.Id);
            Call(owner, "UpdateReplayViewportTimeline");
            var lensRestored = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            if (lensRestored.Fov != original.Fov) throw new Exception("Removing FOV does not restore the original lens while shake remains");
            isolated.ViewportTimeline.Tracks.Clear(); isolated.ViewportTimeline.ElementKeyframes.Clear();
            Call(owner, "UpdateReplayViewportTimeline");
            var released = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            if (released.X != original.X || released.Fov != original.Fov || released.Roll != original.Roll
                || released.Yaw != original.Yaw || released.Pitch != original.Pitch)
                throw new Exception("Removing the final effect does not release the clean camera");
            Console.WriteLine($"PASS {catalog.Length * 3} actual style/world presets at start/end boundaries: group isolation, mute/delete, atomic undo/redo, buffering recovery and repeated paused camera frames");
            Console.WriteLine("PASS standalone shake remains stable for 120 paused frames and restores the clean camera when removed");
            var headTrack = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.HeadMotion };
            var headFrame = new DirectorReplayElementKeyframe { TrackId = headTrack.Id, TrackType = headTrack.TrackType };
            isolated.ViewportTimeline.Tracks.Add(headTrack); isolated.ViewportTimeline.ElementKeyframes.Add(headFrame);
            Set(owner, "headRecordingFrame", headFrame.Id); Set(owner, "headRecordingReplay", isolated.Id);
            Set(owner, "headRecordingBasePose", original.DeepClone());
            var samples = (List<DirectorHeadMotionSample>)AccessTools.Field(owner.GetType(), "headRecordingSamples").GetValue(owner)!;
            samples.Add(new(0, 0, 0, 0, 0, 0, 0)); samples.Add(new(1, .5f, 0, 0, 10, 0, 5, .8f, .3f));
            if (Call(owner, "FinishHeadMotionRecording", true) is not true || isolated.ViewportTimeline.ElementKeyframes.Single().HeadMotion?.Samples.Count != 2)
                throw new Exception("Stopping head motion did not persist the captured clip");
            var recordedGroup = isolated.ViewportTimeline.Tracks.Single(t => t.TrackType == DirectorReplayElementTrackType.PresetGroup);
            if (isolated.ViewportTimeline.Clips.Count != 2 || isolated.ViewportTimeline.Clips[0].CameraPose!.Pitch != original.Pitch
                || isolated.ViewportTimeline.VisibleTracks(new HashSet<Guid>()).Count() != 1
                || isolated.ViewportTimeline.VisibleTracks(new HashSet<Guid> { recordedGroup.Id }).Count() != 3)
                throw new Exception("Recording failed to create a collapsible family with a clean camera baseline");
            if (Call(owner, "RestoreReplayEdit", false) is not true || isolated.ViewportTimeline.ElementKeyframes.Single().HeadMotion is not null)
                throw new Exception("Head recording replacement cannot be undone atomically");
            if (isolated.ViewportTimeline.Clips.Count != 0) throw new Exception("Recording undo left camera keys behind");
            if (Call(owner, "RestoreReplayEdit", true) is not true || isolated.ViewportTimeline.ElementKeyframes.Single().HeadMotion?.Samples.Count != 2)
                throw new Exception("Head recording redo lost samples");
            if (isolated.ViewportTimeline.Clips.Count != 2) throw new Exception("Recording redo lost camera keys");
            Call(owner, "UpdateReplayViewportTimeline");
            var headFirst = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            for (int i = 0; i < 120; i++) Call(owner, "UpdateReplayViewportTimeline");
            var headLast = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            if (Math.Abs(headFirst.X - headLast.X) > 1e-6 || Math.Abs(headFirst.Yaw - headLast.Yaw) > .001)
                throw new Exception("Recorded head motion accumulated on paused frames");
            Set(owner, "headRecordingFrame", headFrame.Id); Set(owner, "headRecordingReplay", isolated.Id);
            samples.Add(new(0, 99, 0, 0, 0, 0, 0)); samples.Add(new(1, 99, 0, 0, 0, 0, 0));
            Call(owner, "FinishHeadMotionRecording", false);
            if (isolated.ViewportTimeline.ElementKeyframes.Single().HeadMotion!.Samples[0].X != 0)
                throw new Exception("Canceling recording changed the previous take");
            isolated.ViewportTimeline.Tracks.Single(t => t.Id == recordedGroup.Id).Enabled = false;
            Call(owner, "UpdateReplayViewportTimeline");
            var headReleased = (CameraPreset)AccessTools.Property(controller.GetType(), "CurrentPose").GetValue(controller)!;
            if (headReleased.X != original.X || headReleased.Fov != original.Fov || headReleased.Roll != original.Roll)
                throw new Exception("Muting head motion failed to restore the base camera");
            Console.WriteLine("PASS head-motion recording group/camera save/cancel, atomic undo/redo, stable paused frames and group mute restores base pose");

            var empty = new DirectorReplayDocument { DurationSeconds = 20 };
            Set(playback, "<Document>k__BackingField", empty);
            Set(hud, "expandedReplayPresetGroups", new HashSet<Guid>());
            harmony.Patch(AccessTools.Method(hudType, "OpenSelectedReplayElementKeyframeEditor"), prefix: new HarmonyMethod(typeof(DirectorImGuiProbe), nameof(SkipReplayHudUi)));
            Call(hud, "AddReplayHeadMotionGroup", empty.ViewportTimeline.DeepClone());
            if (empty.ViewportTimeline.Tracks.Count != 3 || empty.ViewportTimeline.ElementKeyframes.Count != 1)
                throw new Exception("Add head-motion UI did not create camera and motion children");
            var expanded = (HashSet<Guid>)AccessTools.Field(hudType, "expandedReplayPresetGroups").GetValue(hud)!;
            if (empty.ViewportTimeline.VisibleTracks(expanded).Count() != 3)
                throw new Exception("New recording group did not expand for editing");
            if (Call(owner, "RestoreReplayEdit", false) is not true || empty.ViewportTimeline.Tracks.Count != 0)
                throw new Exception("Adding a recording group was not a single undo step");
            Console.WriteLine("PASS head-motion add-element UI creates an expanded camera/motion family in one undo step");
        }
        finally { harmony.UnpatchAll(harmony.Id); handoffController = editManager = null; }
    }

    private static unsafe void CheckReplayProgressOverlay(Assembly assembly, ICoreClientAPI api)
    {
        var type = assembly.GetType("VSDirector.Gui.DirectorReplayProgressOverlay", true)!;
        var window = Activator.CreateInstance(type, api, new VSDirectorModSystem())!;
        var history = (DirectorOperationProgress)AccessTools.Field(type, "history").GetValue(window)!;
        var io = ImGui.GetIO();
        var oldDefault = io.NativePtr->FontDefault;
        var latin = io.Fonts.Fonts[0];
        var textFontType = assembly.GetType("VSDirector.Gui.DirectorImGuiTextFont", true)!;
        string systemFont = (string)AccessTools.Method(textFontType, "FindSystemFont").Invoke(null, null)!;
        if (!File.Exists(systemFont)) throw new Exception("Progress overlay test requires a system CJK font");
        var chinese = io.Fonts.AddFontFromFileTTF(systemFont, 18, default, io.Fonts.GetGlyphRangesChineseFull());
        io.Fonts.Build();
        io.NativePtr->FontDefault = chinese.NativePtr;
        string[] messages = ["旧消息不应显示", "分帧重建区块", "正在加载回放数据", "准备完成"];
        if (chinese.FindGlyphNoFallback('载').NativePtr == null || latin.FindGlyphNoFallback('载').NativePtr != null)
            throw new Exception("Progress font test must distinguish synchronized CJK and ambient Latin fonts");
        try
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (var display in new[] { new Vector2(1280, 720), new Vector2(640, 360) })
            {
                RuntimeEnv.GUIScale = scale; io.DisplaySize = display;
                io.MousePos = display / 2;
                history.Clear();
                for (int i = 1; i <= 4; i++)
                    history.Update(new(true, true, .4, "rendering", "加载回放中", messages[i - 1], ""), i);
                AccessTools.Method(type, "Open").Invoke(window, null);
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame();
                    // Simulate another callback leaving a Latin font active. The overlay
                    // must resolve the synchronized font and restore the caller's stack.
                    ImGui.PushFont(latin);
                    AccessTools.Method(type, "Draw").Invoke(window, [1f/60]);
                    if (ImGui.GetFont().NativePtr != latin.NativePtr) throw new Exception("Progress overlay leaks its font selection");
                    ImGui.PopFont();
                    var draw = ImGui.GetForegroundDrawList();
                    var background = Enumerable.Range(0, draw.VtxBuffer.Size).Select(i => draw.VtxBuffer[i])
                        .Where(v => v.col == 0xcc282020u).Select(v => v.pos).ToArray();
                    var min = background.Aggregate(Vector2.Min); var max = background.Aggregate(Vector2.Max);
                    if (Vector2.Distance((min+max)/2, display/2) > 1 || min.X < 0 || min.Y < 0 || max.X > display.X+1 || max.Y > display.Y+1)
                        throw new Exception("Progress bar is not centered or escapes viewport");
                    if (max.X - min.X > Math.Min(420 * scale, display.X * .65f) + 1)
                        throw new Exception("Progress bar is too wide");
                    // Each visible message must occupy its own horizontal band below the bar.
                    var text = Enumerable.Range(0, draw.VtxBuffer.Size).Select(i => draw.VtxBuffer[i])
                        .Where(v => v.col == 0xffffffffu && v.pos.Y > max.Y + scale).ToArray();
                    var rows = new List<List<ImDrawVertPtr>>();
                    for (int i = 0; i < text.Length; i += 4)
                    {
                        var quad = text.Skip(i).Take(4).ToArray();
                        if (rows.Count == 0 || quad.Min(v => v.pos.Y) > rows[^1].Max(v => v.pos.Y) + scale)
                            rows.Add(new());
                        rows[^1].AddRange(quad);
                    }
                    if (rows.Count != 3) throw new Exception($"Expected three message rows, got {rows.Count}");
                    var reference = ImGui.GetBackgroundDrawList();
                    for (int row = 0; row < 3; row++)
                    {
                        var vertices = rows[row];
                        if (Math.Abs((vertices.Min(v => v.pos.X) + vertices.Max(v => v.pos.X)) / 2 - display.X / 2) > 3 * scale)
                            throw new Exception("Progress message is not horizontally centered");
                        int first = reference.VtxBuffer.Size;
                        reference.AddText(chinese, 18 * scale * .85f, new Vector2(0, row * 50), 0xffffffffu, messages[row + 1]);
                        var expected = Enumerable.Range(first, reference.VtxBuffer.Size - first).Select(i => reference.VtxBuffer[i].uv);
                        if (!vertices.Select(v => v.uv).SequenceEqual(expected))
                            throw new Exception("Progress row lost Chinese glyphs or does not show the latest messages in order");
                    }
                    var yellow = Enumerable.Range(0, draw.VtxBuffer.Size).Select(i => draw.VtxBuffer[i])
                        .Where(v => v.col == 0xff00b3e6u).Select(v => v.pos).ToArray();
                    if (Math.Abs(yellow.Max(p => p.X) - (min.X + (max.X-min.X)*.4f)) > .1f)
                        throw new Exception("Yellow progress fill does not represent the percentage");
                    ImGui.Render();
                    if (ImGui.GetDrawData().TotalVtxCount == 0) throw new Exception("Progress overlay did not render");
                    if (io.WantCaptureMouse || io.WantCaptureKeyboard) throw new Exception("Progress overlay captures game input like a window");
                }
            }
            Console.WriteLine("PASS compact windowless progress bar: three centered latest-message rows, real Chinese glyphs with a Latin ambient font, balanced font stack and no input capture at 100/150/200% DPI");
        }
        finally { io.NativePtr->FontDefault = oldDefault; ((IDisposable)window).Dispose(); }
    }
}
