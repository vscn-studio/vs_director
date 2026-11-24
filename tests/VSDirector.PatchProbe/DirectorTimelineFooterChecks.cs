using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckTimelineFooter(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        Type T(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object target, string name, params object?[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
        void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        object P(object target, string name) => AccessTools.Property(target.GetType(), name).GetValue(target)!;
        var hud = RuntimeHelpers.GetUninitializedObject(T("DirectorWorkspaceHud"));
        Set(hud, "owner", new VSDirectorModSystem()); Set(hud, "capi", api);
        Set(hud, "layout", new DirectorWorkspaceDocument()); Set(hud, "expandedReplayPresetGroups", new HashSet<Guid>());
        using var workspace = (IDisposable)Activator.CreateInstance(T("DirectorImGuiWorkspace"), api)!;
        int presses = 0;
        Action<MouseEvent> down = _ => presses++;
        Action<MouseEvent> mouse = _ => { };
        Action noop = () => { };
        using var view = (IDisposable)Activator.CreateInstance(T("DirectorTimelineWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, down, mouse, mouse, (Action<int, int, float, bool>)((_, _, _, _) => { }),
                (Action<KeyEvent>)(_ => { }), noop, noop, workspace], null)!;
        Set(hud, "imGuiTimeline", view);
        var timeline = DirectorReplayViewportTimeline.Normalize(new(), 20);
        var io = ImGui.GetIO();
        foreach (int count in new[] { 0, 2, 30 })
        foreach (double height in new[] { 160d, 280d })
        foreach (int scroll in new[] { 0, 25 })
        {
            timeline.Tracks = Enumerable.Range(0, count).Select(i => new DirectorReplayElementTrack
                { Id = Guid.NewGuid(), TrackType = DirectorReplayElementTrackType.Camera, Name = "Camera " + i }).ToList();
            Set(hud, "replayViewportDraftTimeline", timeline); Set(hud, "replayTimelineFirstVisibleTrack", scroll);
            Call(view, "Begin", 0d, 0d, 800d, height + 40);
            Call(hud, "BuildImGuiReplayTimeline", 800d, height, 340d);
            var commands = ((IEnumerable)AccessTools.Field(view.GetType(), "commands").GetValue(view)!).Cast<object>().ToArray();
            foreach (string id in new[] { "replayAddElement", "replaySceneSelector" })
            {
                var button = commands.Single(c => AccessTools.Property(c.GetType(), "Id")?.GetValue(c) as string == id);
                if ((double)P(button, "Y") != height + 4) throw new Exception("Timeline footer moves with track count or scroll");
            }
            Vector2 origin = default;
            void Frame()
            {
                ImGui.NewFrame();
                using var theme = (IDisposable)AccessTools.Method(T("DirectorImGuiTheme"), "Use").Invoke(null, null)!;
                ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new(840, (float)height + 65));
                ImGui.Begin("Timeline footer probe", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                origin = ImGui.GetCursorScreenPos(); Call(view, "DrawSurface");
                ImGui.End(); ImGui.Render();
            }
            io.MousePos = new(-100); Frame(); Frame();
            int before = presses;
            io.MousePos = origin + new Vector2(4, (float)height + 16); Frame();
            io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (presses != before) throw new Exception("Left timeline footer forwarded a pointer to the tracks");
            io.MousePos = origin + new Vector2(700, (float)height + 16); Frame();
            io.MouseDown[0] = true; Frame(); io.MouseDown[0] = false; Frame();
            if (presses != before + 1) throw new Exception("Footer blocks the right timeline");
            if (count == 30 && height == 280 && scroll == 0) capture("timeline-footer");
        }
        Console.WriteLine("PASS timeline footer stays visible for empty/full/scrolled timelines at two heights; only left footer blocks track input; right timeline remains interactive");
    }
}
