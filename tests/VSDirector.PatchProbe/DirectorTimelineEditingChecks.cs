using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using VSDirector;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckTimelineEditing(Assembly assembly)
    {
        foreach (double dpi in new[] { 1d, 1.5, 2 })
        {
            if (DirectorTimelineCoordinateMath.TryDragTime(10, 100, 100 + 5 * dpi, 30 * dpi, dpi, false, out double clickTime) || clickTime != 10)
                throw new Exception("Keyframe wobble becomes a drag");
            if (!DirectorTimelineCoordinateMath.TryDragTime(10, 100, 100 + 30 * dpi, 30 * dpi, dpi, false, out double forward) || forward != 11)
                throw new Exception("Keyframe drag loses the original click offset");
            if (!DirectorTimelineCoordinateMath.TryDragTime(10, 100, 100, 30 * dpi, dpi, true, out double back) || back != 10)
                throw new Exception("Keyframe drag cannot return to its original time");
        }
        var hudType = assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud", true)!;
        var hud = RuntimeHelpers.GetUninitializedObject(hudType);
        var owner = new VSDirectorModSystem();
        var managerType = assembly.GetType("VSDirector.Replay.DirectorReplayManager", true)!;
        var manager = RuntimeHelpers.GetUninitializedObject(managerType);
        var playback = RuntimeHelpers.GetUninitializedObject(managerType.GetNestedType("Playback", BindingFlags.NonPublic)!);
        AccessTools.Field(playback.GetType(), "<Document>k__BackingField").SetValue(playback, new DirectorReplayDocument { DurationSeconds = 60 });
        AccessTools.Field(managerType, "playback").SetValue(manager, playback);
        AccessTools.Field(owner.GetType(), "replay").SetValue(owner, manager);
        AccessTools.Field(hudType, "owner").SetValue(hud, owner);
        float saved = RuntimeEnv.GUIScale;
        RuntimeEnv.GUIScale = 1;
        try
        {
            var bounds = ElementBounds.Fixed(0, 0, 800, 280).WithParent(ElementBounds.Empty);
            bounds.CalcWorldBounds();
            AccessTools.Field(hudType, "replayTimelineBounds").SetValue(hud, bounds);
            object? Call(string method, params object[] args) => AccessTools.Method(hudType, method).Invoke(hud, args);
            double fitted = (double)Call("ReplayTimelineFitPixelsPerTick", bounds)!;
            AccessTools.Field(hudType, "replayTimelineFitPixelsPerTick").SetValue(hud, fitted);
            double fit = (double)AccessTools.Property(hudType, "ReplayTimelinePixelsPerTick").GetValue(hud)!;
            Call("ZoomReplayTimeline", -1d, 400d);
            double small = (double)AccessTools.Property(hudType, "ReplayTimelinePixelsPerTick").GetValue(hud)!;
            if (small >= fit) throw new Exception("Timeline zoom stops at fit");
            Call("ZoomReplayTimeline", 1d, 400d);
            double restored = (double)AccessTools.Property(hudType, "ReplayTimelinePixelsPerTick").GetValue(hud)!;
            if (Math.Abs(restored - fit) > .00001) throw new Exception("Timeline zoom cannot reverse");
            var cursor = assembly.GetType("VSDirector.Gui.DirectorTimelineCursor", true)!;
            string label = (string)AccessTools.Method(cursor, "TimeLabel").Invoke(null, [60d, 30])!;
            if (!label.Contains("1800") || !label.EndsWith("01:00/0")) throw new Exception("Timeline cursor omits frames or clock");
        }
        finally { RuntimeEnv.GUIScale = saved; }
        Console.WriteLine("PASS keyframe click/drag hysteresis and offset at three DPI scales; zoom below fit and combined frame/clock cursor");
    }
}
