using VSDirector.Data;
using VSDirector.Replay;
using Vintagestory.Client.NoObf;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    internal DirectorReplayVisualSettings? ActiveVisuals => Renderer?.Visible == true && !PerformanceActive ? Session.Project?.Visuals : null;
    private DirectorReplayVisualEnvironmentController? creationEnvironment;
    private DirectorCreationCalendarController? creationCalendar;
    private void RestoreVisualEnvironment()
    {
        creationEnvironment?.Restore(); creationEnvironment = null;
        creationCalendar?.Restore(); creationCalendar = null;
    }
    private void UpdateVisualEnvironment()
    {
        if (ActiveVisuals is not { } visuals || DirectorReplayRuntime.Manager?.IsPlaying == true)
        { RestoreVisualEnvironment(); return; }
        creationEnvironment ??= new(api, (ClientMain)api.World);
        creationEnvironment.Apply(visuals);
        creationCalendar ??= new((ClientMain)api.World, api.World.Calendar);
        creationCalendar.Apply(visuals.Environment);
    }
}
