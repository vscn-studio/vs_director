using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckTimelineCursorFeedback(Assembly assembly, ICoreClientAPI api, Action<string> capture)
    {
        Type R(string name) => assembly.GetType("VSDirector.Gui." + name, true)!;
        object? Call(object value, string name, params object[] args) => AccessTools.Method(value.GetType(), name).Invoke(value, args);
        void Set(object value, string name, object data) => AccessTools.Field(value.GetType(), name).SetValue(value, data);
        object Get(object value, string name) => AccessTools.Field(value.GetType(), name).GetValue(value)!;
        void Mode(object value, string name, string mode) => Set(value, name, Enum.Parse(AccessTools.Field(value.GetType(), name).FieldType, mode));
        using var host = (IDisposable)Activator.CreateInstance(R("DirectorImGuiWorkspace"), api)!;
        int pressed = 0, released = 0;
        Action<MouseEvent> down = _ => pressed++, up = _ => released++, move = _ => { };
        Action noop = () => { };
        using var timeline = (IDisposable)Activator.CreateInstance(R("DirectorTimelineWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, down, move, up, (Action<int, int, float, bool>)((_, _, _, _) => { }), (Action<KeyEvent>)(_ => { }), noop, noop, host], null)!;
        var hud = RuntimeHelpers.GetUninitializedObject(R("DirectorWorkspaceHud"));
        var layout = new DirectorWorkspaceDocument();
        var panel = layout.Panel("timeline"); panel.Width = 700; panel.Height = 260;
        Set(hud, "owner", new VSDirectorModSystem()); Set(hud, "layout", layout); Set(hud, "rmlTimeline", timeline);
        Set(hud, "section", DirectorWorkspaceSection.Replay);
        var key = new DirectorReplayElementKeyframe { TimeSeconds = 1 };
        Set(hud, "replayViewportDraftTimeline", new DirectorReplayViewportTimeline { ElementKeyframes = [key] });
        Mode(hud, "replayAuxiliaryKeyframeDragKind", "Element"); Set(hud, "replayAuxiliaryKeyframeDragId", key.Id);
        Set(hud, "replayKeyframeDragTime", 2d);
        string locale = Lang.CurrentLocale; float savedScale = RuntimeEnv.GUIScale;
        try
        {
            foreach (string language in new[] { "zh-cn", "en" })
            {
                LoadCreationProbeLanguage(language);
                Call(hud, "UpdateRmlTimelinePlayhead");
                if ((string)Get(timeline, "cursorLabel") != "60 " + Lang.Get("vsdirector:creation-unit-frames")
                    || (double)Get(timeline, "cursorDragX") <= (double)Get(timeline, "cursorOriginalX") || key.TimeSeconds != 1)
                    throw new Exception("Replay drag feedback lost source/target time or localized frame label");
            }
            // The path's source time must come from its pre-drag snapshot, not its updated playhead.
            Set(hud, "section", DirectorWorkspaceSection.Paths);
            var path = new CameraPath { Keyframes = [new CameraKeyframe { TimeSeconds = 1 }] };
            Set(hud, "pathTimelineDragBefore", path); Mode(hud, "pathTimelineDragKind", "Camera");
            Set(hud, "pathTimelineCameraDragIndex", 0); Set(hud, "pathTimelineDragTime", 3d);
            Call(hud, "UpdateRmlTimelinePlayhead");
            if (!((double)Get(timeline, "cursorDragX") > (double)Get(timeline, "cursorOriginalX")))
                throw new Exception("Path drag feedback lost original keyframe time");
            Set(hud, "pathTimelineDragTime", double.NaN); Call(hud, "UpdateRmlTimelinePlayhead");
            if (!double.IsNaN((double)Get(timeline, "cursorDragX"))) throw new Exception("Timeline destination line survived drag release");

            var io = ImGui.GetIO();
            Vector2 origin = default;
            List<(Vector2 Position, uint Color)> vertices = [];
            void Frame(double target, bool feedback = true)
            {
                ImGui.NewFrame(); ImGui.SetNextWindowPos(new(12, 12)); ImGui.SetNextWindowSize(new(900, 570));
                ImGui.Begin("timeline-cursor-probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar);
                origin = ImGui.GetCursorScreenPos();
                double width = 820 / RuntimeEnv.GUIScale;
                Call(timeline, "Begin", 0d, 0d, width, 180d);
                Call(timeline, "Playhead", 130d, 100d, width, 180d);
                if (feedback) Call(timeline, "CursorFeedback", "60 " + Lang.Get("vsdirector:creation-unit-frames"), target, 130d);
                Call(timeline, "DrawSurface");
                var buffer = ImGui.GetWindowDrawList().VtxBuffer;
                vertices = Enumerable.Range(0, buffer.Size).Select(i => (buffer[i].pos, buffer[i].col)).ToList();
                ImGui.End(); ImGui.Render();
            }
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                foreach (double target in new[] { 100d, 250d, 820d / scale })
                {
                    Frame(target); Frame(target);
                    var text = vertices.Where(v => v.Color == 0xff181818).ToArray();
                    if (!vertices.Any(v => v.Color == 0xff55d9ff) || !vertices.Any(v => v.Color == 0x88000000) || text.Length == 0)
                        throw new Exception("Native timeline did not draw its yellow destination, translucent source and inline label");
                    if (text.Any(v => v.Position.X < origin.X + 100 * scale - 1 || v.Position.X > origin.X + 820 + 1))
                        throw new Exception("Cursor label exceeded the time grid at its edge");
                    if (text.Any(v => v.Position.Y < origin.Y + 140 * scale || v.Position.Y > origin.Y + 180 * scale))
                        throw new Exception("Cursor label did not follow the bottom endpoint of the vertical line");
                    if (target == 250 && Math.Abs(text.Min(v => v.Position.X) - (origin.X + 256 * scale)) > 4 * scale)
                        throw new Exception("Cursor label left a gap beside its vertical line");
                }
                capture("timeline-cursor-" + scale);
                Frame(double.NaN, false);
                if (vertices.Any(v => v.Color is 0xff55d9ff or 0x88000000 or 0xff181818))
                    throw new Exception("Rebuilding the native timeline left stale cursor feedback");
            }
            RuntimeEnv.GUIScale = 1;
            Frame(250); io.MousePos = origin + new Vector2(265, 165); Frame(250); Frame(250);
            io.MouseDown[0] = true; Frame(250); io.MouseDown[0] = false; Frame(250);
            if (pressed != 1 || released != 1) throw new Exception("Cursor label intercepted the timeline pointer");
            Console.WriteLine("PASS replay/path original and destination times, bilingual inline labels, 100/150/200% edge clipping, pointer passthrough and drag cleanup");
        }
        finally { RuntimeEnv.GUIScale = savedScale; Lang.ChangeLanguage(locale); }
    }
}
