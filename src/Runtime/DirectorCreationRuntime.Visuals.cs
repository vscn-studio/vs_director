using VSDirector.Data;
using VSDirector.Replay;
using Vintagestory.Client.NoObf;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    internal DirectorReplayVisualSettings? ActiveVisuals => Renderer?.Visible == true && !PerformanceActive ? Session.Project?.Visuals : null;
    private DirectorReplayVisualEnvironmentController? creationEnvironment;
    private (float Time, float? Season)? savedCalendar;
    private void RestoreVisualEnvironment()
    {
        creationEnvironment?.Restore(); creationEnvironment = null;
        if (savedCalendar is { } saved)
        { api.World.Calendar.Timelapse = saved.Time; api.World.Calendar.SeasonOverride = saved.Season; savedCalendar = null; RefreshCreationCalendar(); }
    }
    private void UpdateVisualEnvironment()
    {
        if (ActiveVisuals is not { } visuals || DirectorReplayRuntime.Manager?.IsPlaying == true)
        { RestoreVisualEnvironment(); return; }
        creationEnvironment ??= new(api, (ClientMain)api.World);
        creationEnvironment.Apply(visuals);
        var calendar = api.World.Calendar;
        savedCalendar ??= (calendar.Timelapse, calendar.SeasonOverride);
        calendar.SeasonOverride = visuals.Environment.OverrideSeason ? visuals.Environment.Season : savedCalendar.Value.Season;
        calendar.Timelapse = visuals.Environment.OverrideTime
            ? (float)(visuals.Environment.TimeOfDayTick / 24000d - (calendar.TotalHours / calendar.HoursPerDay % 1))
            : savedCalendar.Value.Time;
        RefreshCreationCalendar();
    }
    private void RefreshCreationCalendar()
    { if (api.World.Calendar is Vintagestory.Common.ClientGameCalendar calendar) calendar.Update(); }
}
