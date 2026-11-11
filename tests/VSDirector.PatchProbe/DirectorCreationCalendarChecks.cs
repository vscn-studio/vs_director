using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationCalendar(Assembly assembly)
    {
        double hours = 100; float time = .2f; float? season = null;
        int timeWrites = 0, seasonWrites = 0;
        var calendar = ProbeDispatchProxy.Create<IClientGameCalendar>((method, args) =>
        {
            switch (method.Name)
            {
                case "get_TotalHours": return hours;
                case "get_HoursPerDay": return 24f;
                case "get_Timelapse": return time;
                case "set_Timelapse": time = (float)args![0]!; timeWrites++; return null;
                case "get_SeasonOverride": return season;
                case "set_SeasonOverride": season = (float?)args![0]; seasonWrites++; return null;
                default: return ProbeDispatchProxy.DefaultValue(method.ReturnType);
            }
        });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.timelapsedCurrent = .2f; game.timelapse = .01f; game.timelapseEnd = .4f;
        var type = assembly.GetType("VSDirector.Runtime.DirectorCreationCalendarController", true)!;
        var controller = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [game, calendar], null)!;
        var environment = new DirectorReplayEnvironmentVisuals();
        void Apply() => AccessTools.Method(type, "Apply").Invoke(controller, [environment]);
        void Restore() => AccessTools.Method(type, "Restore").Invoke(controller, null);
        void Near(double actual, double expected, string message)
        { if (Math.Abs(actual - expected) > .00001) throw new Exception(message + $" ({actual} != {expected})"); }
        Apply();
        if (timeWrites != 0 || seasonWrites != 0) throw new Exception("Inactive creation overrides rewrote the world calendar");
        environment.OverrideTime = true; environment.TimeOfDayTick = 18000;
        for (int tick = 0; tick < 100; tick++)
        {
            hours += .0001; Apply();
            // The engine does this at the start of every MainRenderLoop,
            // including frames between creation ticks and while paused.
            for (int frame = 0; frame < 5; frame++)
            {
                if ((game.timelapsedCurrent += game.timelapse) > game.timelapseEnd)
                { game.timelapse = 0; game.timelapsedCurrent = 0; game.timelapseEnd = float.MaxValue; }
                time = game.timelapsedCurrent;
                Near(hours / 24 % 1 + time, .75, "Engine frame reset creation sunlight");
            }
        }
        environment.TimeOfDayTick = 6000; Apply(); Near(hours / 24 % 1 + time, .25, "Dragging time failed to update sunlight");
        if (seasonWrites != 0) throw new Exception("Time override captured or rewrote season");
        environment.OverrideSeason = true; environment.Season = .7f; Apply(); Near(season!.Value, .7, "Season override failed");
        environment.OverrideTime = false; Apply();
        Near(time, .2, "Calendar time not restored"); Near(game.timelapsedCurrent, .2, "Render-loop time not restored");
        Near(game.timelapse, .01, "Time step not restored"); Near(game.timelapseEnd, .4, "Time limit not restored");
        Near(season!.Value, .7, "Disabling time removed the season override");
        timeWrites = 0; game.timelapsedCurrent = time = .3f; Apply();
        if (timeWrites != 0) throw new Exception("Season-only override froze the world's time");
        environment.OverrideSeason = false; Apply();
        if (season is not null) throw new Exception("Nullable original season was not restored");
        timeWrites = seasonWrites = 0; Restore(); Restore();
        if (timeWrites != 0 || seasonWrites != 0) throw new Exception("Repeated cleanup overwrote the released world calendar");
        environment.OverrideTime = true; environment.OverrideSeason = true; Apply(); Restore();
        Near(time, .3, "Re-enabling override restored a stale snapshot"); Near(game.timelapsedCurrent, .3, "Exit left client time overridden");
        if (season is not null) throw new Exception("Exit left the season overridden");
        Console.WriteLine("PASS creation sunlight survives engine render-loop time writes; time/season toggle independently and restore on exit/replay handoff");
    }
}
