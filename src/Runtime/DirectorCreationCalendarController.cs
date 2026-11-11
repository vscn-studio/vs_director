using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using VSDirector.Data;

namespace VSDirector.Runtime;

/// <summary>Owns only the calendar settings overridden by the creation view.</summary>
internal sealed class DirectorCreationCalendarController(ClientMain game, IClientGameCalendar calendar)
{
    private (float Calendar, float Current, float Step, float End)? savedTime;
    private bool hasSeason;
    private float? savedSeason;

    internal void Apply(DirectorReplayEnvironmentVisuals environment)
    {
        bool changed = false;
        if (environment.OverrideTime)
        {
            savedTime ??= (calendar.Timelapse, game.timelapsedCurrent, game.timelapse, game.timelapseEnd);
            double days = calendar.TotalHours / Math.Max(1d, calendar.HoursPerDay);
            float offset = (float)(Math.Clamp(environment.TimeOfDayTick, 0, 24000) / 24000d - (days - Math.Floor(days)));
            changed = calendar.Timelapse != offset;
            // MainRenderLoop copies timelapsedCurrent into Calendar.Timelapse
            // every frame, even between our ticks. Update its source as well.
            game.timelapse = 0;
            game.timelapsedCurrent = offset;
            game.timelapseEnd = float.MaxValue;
            calendar.Timelapse = offset;
        }
        else changed = RestoreTime();
        if (environment.OverrideSeason)
        {
            if (!hasSeason) { savedSeason = calendar.SeasonOverride; hasSeason = true; }
            changed |= calendar.SeasonOverride != environment.Season;
            calendar.SeasonOverride = environment.Season;
        }
        else changed |= RestoreSeason();
        if (changed) Refresh();
    }
    internal void Restore()
    {
        bool changed = RestoreTime();
        changed |= RestoreSeason();
        if (changed) Refresh();
    }
    private bool RestoreTime()
    {
        if (savedTime is not { } saved) return false;
        game.timelapse = saved.Step; game.timelapsedCurrent = saved.Current; game.timelapseEnd = saved.End;
        calendar.Timelapse = saved.Calendar; savedTime = null;
        return true;
    }
    private bool RestoreSeason()
    {
        if (!hasSeason) return false;
        calendar.SeasonOverride = savedSeason; hasSeason = false; savedSeason = null;
        return true;
    }
    private void Refresh()
    { if (calendar is ClientGameCalendar clientCalendar) clientCalendar.Update(); }
}
