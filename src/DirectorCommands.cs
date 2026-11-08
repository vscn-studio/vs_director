using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector.Data;
using VSDirector.Rendering;

namespace VSDirector;

internal static class DirectorCommands
{
    public static void Register(ICoreClientAPI api, VSDirectorModSystem system)
    {
        CommandArgumentParsers parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("director")
            .WithAlias("vsdirector")
            .WithDescription("VS Director camera tools")
            .BeginSubCommand("panel")
                .WithDescription("Open the VS Director panel")
                .HandleWith(_ => { system.TogglePanel(); return TextCommandResult.Success(); })
            .EndSubCommand()
            .BeginSubCommand("add")
                .WithDescription("Capture the current view as a named camera")
                .WithArgs(parsers.All("name"))
                .HandleWith(args => HandleAdd(system, (string)args[0]))
            .EndSubCommand()
            .BeginSubCommand("list")
                .WithDescription("List cameras saved in this world")
                .HandleWith(_ => TextCommandResult.Success(system.ListText()))
            .EndSubCommand()
            .BeginSubCommand("view")
                .WithDescription("Activate a camera by name, index, or id")
                .WithArgs(parsers.All("camera"))
                .HandleWith(args => HandleView(system, (string)args[0]))
            .EndSubCommand()
            .BeginSubCommand("update")
                .WithDescription("Recapture a camera at the current view")
                .WithArgs(parsers.All("camera"))
                .HandleWith(args => HandleUpdate(system, (string)args[0]))
            .EndSubCommand()
            .BeginSubCommand("delete")
                .WithDescription("Delete a camera")
                .WithArgs(parsers.All("camera"))
                .HandleWith(args => HandleDelete(system, (string)args[0]))
            .EndSubCommand()
            .BeginSubCommand("slot")
                .WithDescription("Assign a camera to an Alt+1..Alt+9 or Alt+0 slot")
                .WithArgs(parsers.Word("camera"), parsers.IntRange("slot", QuickSlots.First, QuickSlots.Last))
                .HandleWith(args => HandleSlot(system, (string)args[0], (int)args[1]))
            .EndSubCommand()
            .BeginSubCommand("sharedcamera")
                .WithDescription("Manage the server shared camera catalog")
                .BeginSubCommand("list")
                    .HandleWith(_ =>
                    {
                        system.RequestSharedCameraList();
                        return TextCommandResult.Success(system.SharedCameraListText());
                    })
                .EndSubCommand()
                .BeginSubCommand("publish")
                    .WithArgs(parsers.All("camera"))
                    .HandleWith(args => system.TryPublishSharedCamera((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("delete")
                    .WithArgs(parsers.All("sharedCamera"))
                    .HandleWith(args => system.TryDeleteSharedCamera((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("view")
                    .WithArgs(parsers.All("sharedCamera"))
                    .HandleWith(args => system.TryActivateSharedCamera((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("import")
                    .WithArgs(parsers.All("sharedCamera"))
                    .HandleWith(args => system.TryImportSharedCamera((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("path")
                .WithDescription("Create, edit, preview, and play camera paths")
                .BeginSubCommand("panel")
                    .WithDescription("Open the camera path panel")
                    .HandleWith(_ => { system.TogglePathPanel(); return TextCommandResult.Success(); })
                .EndSubCommand()
                .BeginSubCommand("list")
                    .WithDescription("List camera paths saved in this world")
                    .HandleWith(_ => TextCommandResult.Success(system.PathListText()))
                .EndSubCommand()
                .BeginSubCommand("create")
                    .WithDescription("Create a camera path")
                    .WithArgs(parsers.All("name"))
                    .HandleWith(args => HandlePathCreate(system, (string)args[0]))
                .EndSubCommand()
                .BeginSubCommand("delete")
                    .WithDescription("Delete a camera path")
                    .WithArgs(parsers.All("path"))
                    .HandleWith(args => HandlePathDelete(system, (string)args[0]))
                .EndSubCommand()
                .BeginSubCommand("key")
                    .WithDescription("Capture the current view as a timed keyframe")
                    .WithArgs(parsers.Word("path"), parsers.Double("timeSeconds"))
                    .HandleWith(args => HandlePathKey(system, (string)args[0], (double)args[1]))
                .EndSubCommand()
                .BeginSubCommand("unkey")
                    .WithDescription("Remove a keyframe by its one-based index")
                    .WithArgs(parsers.Word("path"), parsers.IntRange("keyframe", 1, 100000))
                    .HandleWith(args => HandlePathUnkey(system, (string)args[0], (int)args[1]))
                .EndSubCommand()
                .BeginSubCommand("play")
                    .WithDescription("Play a path forward")
                    .WithArgs(parsers.All("path"))
                    .HandleWith(args => HandlePathPlay(system, (string)args[0], reverse: false))
                .EndSubCommand()
                .BeginSubCommand("reverse")
                    .WithDescription("Play a path backward")
                    .WithArgs(parsers.All("path"))
                    .HandleWith(args => HandlePathPlay(system, (string)args[0], reverse: true))
                .EndSubCommand()
                .BeginSubCommand("stop")
                    .WithDescription("Stop the active path")
                    .HandleWith(_ => { system.ExitCamera(); return TextCommandResult.Success(); })
                .EndSubCommand()
                .BeginSubCommand("interpolation")
                    .WithDescription("Set linear, ease, bezier, or step interpolation")
                    .WithArgs(parsers.Word("path"),
                        parsers.WordRange("mode", "linear", "ease", "bezier", "step"))
                    .HandleWith(args => HandlePathInterpolation(system, (string)args[0], (string)args[1]))
                .EndSubCommand()
                .BeginSubCommand("loop")
                    .WithDescription("Set once, loop, or pingpong playback")
                    .WithArgs(parsers.Word("path"), parsers.WordRange("mode", "once", "loop", "pingpong"))
                    .HandleWith(args => HandlePathLoop(system, (string)args[0], (string)args[1]))
                .EndSubCommand()
                .BeginSubCommand("preview")
                    .WithDescription("Show or hide the path in the world")
                    .WithArgs(parsers.Word("path"), parsers.WordRange("state", "on", "off"))
                    .HandleWith(args => HandlePathPreview(system, (string)args[0], (string)args[1]))
                .EndSubCommand()
                .BeginSubCommand("duration")
                    .WithDescription("Set one keyframe segment duration and shift later frames")
                    .WithArgs(
                        parsers.Word("path"),
                        parsers.IntRange("keyframe", 1, 100000),
                        parsers.Double("seconds"))
                    .HandleWith(args => HandlePathDuration(
                        system, (string)args[0], (int)args[1], (double)args[2]))
                .EndSubCommand()
                .BeginSubCommand("segment")
                    .WithDescription("Set one segment's start/end speed and marker")
                    .WithArgs(
                        parsers.Word("path"),
                        parsers.IntRange("keyframe", 1, 100000),
                        parsers.Double("startSpeed"),
                        parsers.Double("endSpeed"),
                        parsers.All("marker"))
                    .HandleWith(args => HandlePathSegment(
                        system,
                        (string)args[0],
                        (int)args[1],
                        (double)args[2],
                        (double)args[3],
                        (string)args[4]))
                .EndSubCommand()
                .BeginSubCommand("speed")
                    .WithDescription("Set the path playback speed multiplier")
                    .WithArgs(parsers.Word("path"), parsers.Double("multiplier"))
                    .HandleWith(args => HandlePathSpeed(system, (string)args[0], (double)args[1]))
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("follow")
                .WithDescription("Create, edit, and activate player or entity follow cameras")
                .BeginSubCommand("panel")
                    .WithDescription("Open the follow camera panel")
                    .HandleWith(_ => { system.ToggleFollowPanel(); return TextCommandResult.Success(); })
                .EndSubCommand()
                .BeginSubCommand("list")
                    .WithDescription("List follow cameras saved in this world")
                    .HandleWith(_ => TextCommandResult.Success(system.FollowListText()))
                .EndSubCommand()
                .BeginSubCommand("create")
                    .WithDescription("Create a follow camera for a player name, uid, or loaded entity id")
                    .WithArgs(parsers.Word("target"), parsers.All("name"))
                    .HandleWith(args => HandleFollowCreate(system, (string)args[0], (string)args[1]))
                .EndSubCommand()
                .BeginSubCommand("delete")
                    .WithDescription("Delete a follow camera")
                    .WithArgs(parsers.All("follow"))
                    .HandleWith(args => HandleFollowDelete(system, (string)args[0]))
                .EndSubCommand()
                .BeginSubCommand("play")
                    .WithDescription("Activate a follow camera")
                    .WithArgs(parsers.All("follow"))
                    .HandleWith(args => HandleFollowPlay(system, (string)args[0]))
                .EndSubCommand()
                .BeginSubCommand("slot")
                    .WithDescription("Assign a follow camera to an Alt+number slot")
                    .WithArgs(parsers.Word("follow"), parsers.IntRange("slot", QuickSlots.First, QuickSlots.Last))
                    .HandleWith(args => HandleFollowSlot(system, (string)args[0], (int)args[1]))
                .EndSubCommand()
                .BeginSubCommand("orbit")
                    .WithDescription("Set the active third-person orbit angle")
                    .WithArgs(parsers.Double("degrees"))
                    .HandleWith(args => HandleFollowOrbit(system, (double)args[0]))
                .EndSubCommand()
                .BeginSubCommand("distance")
                    .WithDescription("Set the active third-person follow distance")
                    .WithArgs(parsers.Double("distance"))
                    .HandleWith(args => HandleFollowDistance(system, (double)args[0]))
                .EndSubCommand()
                .BeginSubCommand("stop")
                    .WithDescription("Stop the active follow camera")
                    .HandleWith(_ => { system.ExitCamera(); return TextCommandResult.Success(); })
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("replay")
                .WithDescription("Record and play actor movement replays")
                .BeginSubCommand("panel")
                    .HandleWith(_ =>
                    {
                        system.ToggleReplayPanel();
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()
                .BeginSubCommand("record")
                    .WithArgs(parsers.All("name"))
                    .HandleWith(args => system.TryStartReplayRecording((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("stop")
                    .HandleWith(_ => system.TryStopReplayRecording(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("delete")
                    .WithArgs(parsers.All("replay"))
                    .HandleWith(args => system.TryDeleteReplay((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("list")
                    .HandleWith(_ => TextCommandResult.Success(system.ReplayListText()))
                .EndSubCommand()
                .BeginSubCommand("actors")
                    .HandleWith(_ => TextCommandResult.Success(system.ReplayActorListText()))
                .EndSubCommand()
                .BeginSubCommand("actorperformance")
                    .WithArgs(parsers.Word("actor"), parsers.All("performanceName"))
                    .HandleWith(args => system.TryExportReplayActorPerformance(
                        (string)args[0],
                        (string)args[1],
                        out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("actorfollow")
                    .WithArgs(parsers.All("path"))
                    .HandleWith(args => system.TryFollowReplayActorPath(
                        (string)args[0],
                        out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("play")
                    .WithArgs(parsers.All("replay"))
                    .HandleWith(args => system.TryPlayReplay((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("pause")
                    .HandleWith(_ => system.TryToggleReplayPause(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("seek")
                    .WithArgs(parsers.Double("seconds"))
                    .HandleWith(args => system.TrySeekReplay((double)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("speed")
                    .WithArgs(parsers.Double("multiplier"))
                    .HandleWith(args => system.TrySetReplaySpeed((double)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("quick")
                    .WithArgs(parsers.WordRange("state", "on", "off"))
                    .HandleWith(args => system.TrySetReplayQuickMode(
                        string.Equals((string)args[0], "on", StringComparison.OrdinalIgnoreCase),
                        out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("attach")
                    .WithArgs(
                        parsers.Word("actor"),
                        parsers.WordRange("view", "first", "third"))
                    .HandleWith(args => system.TryAttachReplayActor(
                        (string)args[0],
                        string.Equals((string)args[1], "first", StringComparison.OrdinalIgnoreCase)
                            ? CameraFollowViewMode.FirstPerson
                            : CameraFollowViewMode.ThirdPerson,
                        out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("detach")
                    .HandleWith(_ => system.TryDetachReplayActor(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("marker")
                    .BeginSubCommand("list")
                        .HandleWith(_ => TextCommandResult.Success(system.ReplayMarkerListText()))
                    .EndSubCommand()
                    .BeginSubCommand("add")
                        .WithArgs(parsers.All("label"))
                        .HandleWith(args => system.TryAddReplayMarker((string)args[0], null, out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("addat")
                        .WithArgs(parsers.Double("seconds"), parsers.All("label"))
                        .HandleWith(args => system.TryAddReplayMarker(
                            (string)args[1],
                            (double)args[0],
                            out string message)
                                ? TextCommandResult.Success(message)
                                : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("update")
                        .WithArgs(
                            parsers.Word("marker"),
                            parsers.Double("seconds"),
                            parsers.All("label"))
                        .HandleWith(args => system.TryUpdateReplayMarker(
                            (string)args[0],
                            (string)args[2],
                            (double)args[1],
                            out string message)
                                ? TextCommandResult.Success(message)
                                : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("delete")
                        .WithArgs(parsers.All("marker"))
                        .HandleWith(args => system.TryDeleteReplayMarker((string)args[0], out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("goto")
                        .WithArgs(parsers.All("marker"))
                        .HandleWith(args => system.TryJumpReplayMarker((string)args[0], out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("speedkey")
                    .BeginSubCommand("list")
                        .HandleWith(_ => TextCommandResult.Success(system.ReplaySpeedKeyframeListText()))
                    .EndSubCommand()
                    .BeginSubCommand("add")
                        .WithArgs(parsers.Double("multiplier"))
                        .HandleWith(args => system.TryAddReplaySpeedKeyframe(
                            (double)args[0],
                            null,
                            DirectorReplaySpeedInterpolation.Linear,
                            out string message)
                                ? TextCommandResult.Success(message)
                                : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("addat")
                        .WithArgs(
                            parsers.Double("seconds"),
                            parsers.Double("multiplier"),
                            parsers.WordRange("interpolation", "hold", "linear", "ease"))
                        .HandleWith(args => system.TryAddReplaySpeedKeyframe(
                            (double)args[1],
                            (double)args[0],
                            (string)args[2],
                            out string message)
                                ? TextCommandResult.Success(message)
                                : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("update")
                        .WithArgs(
                            parsers.Word("keyframe"),
                            parsers.Double("seconds"),
                            parsers.Double("multiplier"),
                            parsers.WordRange("interpolation", "hold", "linear", "ease"))
                        .HandleWith(args => system.TryUpdateReplaySpeedKeyframe(
                            (string)args[0],
                            (double)args[1],
                            (double)args[2],
                            (string)args[3] switch
                            {
                                "hold" => DirectorReplaySpeedInterpolation.Hold,
                                "ease" => DirectorReplaySpeedInterpolation.EaseInOut,
                                _ => DirectorReplaySpeedInterpolation.Linear
                            },
                            out string message)
                                ? TextCommandResult.Success(message)
                                : TextCommandResult.Error(message))
                    .EndSubCommand()
                    .BeginSubCommand("delete")
                        .WithArgs(parsers.Word("keyframe"))
                        .HandleWith(args => system.TryDeleteReplaySpeedKeyframe((string)args[0], out string message)
                            ? TextCommandResult.Success(message)
                            : TextCommandResult.Error(message))
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("free")
                    .HandleWith(_ => system.TryToggleReplayFreeCamera(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("key")
                    .WithArgs(parsers.All("path"))
                    .HandleWith(args => system.TryCaptureReplayKeyframe((string)args[0], out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("status")
                    .HandleWith(_ => TextCommandResult.Success(system.ReplayStatusText()))
                .EndSubCommand()
                .BeginSubCommand("exit")
                    .HandleWith(_ =>
                    {
                        system.StopReplay();
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("exit")
                .WithDescription("Exit the active camera")
                .HandleWith(_ => { system.ExitCamera(); return TextCommandResult.Success(); })
            .EndSubCommand()
            .BeginSubCommand("hud")
                .WithDescription("Set whether the HUD is hidden while viewing")
                .WithArgs(parsers.WordRange("state", "on", "off"))
                .HandleWith(args =>
                {
                    bool enabled = string.Equals((string)args[0], "on", StringComparison.OrdinalIgnoreCase);
                    system.SetHudHidden(enabled);
                    return TextCommandResult.Success();
                })
            .EndSubCommand()
            .BeginSubCommand("input")
                .WithDescription("Set whether player input is locked while viewing")
                .WithArgs(parsers.WordRange("state", "on", "off"))
                .HandleWith(args =>
                {
                    bool enabled = string.Equals((string)args[0], "on", StringComparison.OrdinalIgnoreCase);
                    system.SetInputLock(enabled);
                    return TextCommandResult.Success();
                })
            .EndSubCommand()
            .BeginSubCommand("config")
                .WithDescription("Reload, save, or inspect the client configuration")
                .BeginSubCommand("reload")
                    .HandleWith(_ => system.TryReloadConfig(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("save")
                    .HandleWith(_ => system.TrySaveConfig(out string message)
                        ? TextCommandResult.Success(message)
                        : TextCommandResult.Error(message))
                .EndSubCommand()
                .BeginSubCommand("status")
                    .HandleWith(_ => TextCommandResult.Success(system.ConfigStatusText()))
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show VS Director status")
                .HandleWith(_ => TextCommandResult.Success(system.StatusText()))
            .EndSubCommand()
            .HandleWith(_ => TextCommandResult.Success(Lang.Get("vsdirector:command-usage")));
    }

    private static TextCommandResult HandleAdd(VSDirectorModSystem system, string name)
    {
        return system.TryAddCamera(name, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleView(VSDirectorModSystem system, string selector)
    {
        CameraPreset? camera = system.FindCamera(selector);
        if (camera == null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:camera-not-found", selector));
        }

        return system.TryActivateCamera(camera.Id)
            ? TextCommandResult.Success()
            : TextCommandResult.Error(Lang.Get("vsdirector:camera-error", "camera could not be activated"));
    }

    private static TextCommandResult HandleUpdate(VSDirectorModSystem system, string selector)
    {
        CameraPreset? camera = system.FindCamera(selector);
        if (camera == null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:camera-not-found", selector));
        }

        return system.TryRecaptureCamera(camera.Id, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleDelete(VSDirectorModSystem system, string selector)
    {
        CameraPreset? camera = system.FindCamera(selector);
        if (camera == null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:camera-not-found", selector));
        }

        return system.TryDeleteCamera(camera.Id, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSlot(VSDirectorModSystem system, string selector, int slot)
    {
        CameraPreset? camera = system.FindCamera(selector);
        if (camera == null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:camera-not-found", selector));
        }

        return system.TryUpdateCameraMetadata(
                camera.Id, camera.Name, string.Join(",", camera.Tags), camera.Description,
                camera.Fov, camera.Roll, slot, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathCreate(VSDirectorModSystem system, string name)
    {
        return system.TryAddPath(name, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathDelete(VSDirectorModSystem system, string selector)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TryDeletePath(path.Id, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathKey(VSDirectorModSystem system, string selector, double timeSeconds)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TryCapturePathKeyframe(path.Id, timeSeconds, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathUnkey(VSDirectorModSystem system, string selector, int index)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TryRemovePathKeyframe(path.Id, index - 1, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathPlay(VSDirectorModSystem system, string selector, bool reverse)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TryPlayPath(path.Id, reverse)
            ? TextCommandResult.Success()
            : TextCommandResult.Error(Lang.Get("vsdirector:camera-error", "path could not be played"));
    }

    private static TextCommandResult HandlePathInterpolation(VSDirectorModSystem system, string selector, string mode)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        CameraPathInterpolation interpolation = mode switch
        {
            "linear" => CameraPathInterpolation.Linear,
            "bezier" => CameraPathInterpolation.Bezier,
            "step" => CameraPathInterpolation.Step,
            _ => CameraPathInterpolation.EaseInOut
        };
        return system.TryUpdatePathSettings(
                path.Id, path.Name, interpolation, path.LoopMode, path.PreviewVisible, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathLoop(VSDirectorModSystem system, string selector, string mode)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        CameraPathLoopMode loopMode = mode switch
        {
            "loop" => CameraPathLoopMode.Loop,
            "pingpong" => CameraPathLoopMode.PingPong,
            _ => CameraPathLoopMode.Once
        };
        return system.TryUpdatePathSettings(
                path.Id, path.Name, path.Interpolation, loopMode, path.PreviewVisible, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathPreview(VSDirectorModSystem system, string selector, string state)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        bool visible = string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);
        return system.TryUpdatePathSettings(
                path.Id, path.Name, path.Interpolation, path.LoopMode, visible, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathDuration(
        VSDirectorModSystem system,
        string selector,
        int keyframeIndex,
        double seconds)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TrySetPathKeyframeDuration(path.Id, keyframeIndex - 1, seconds, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathSpeed(VSDirectorModSystem system, string selector, double multiplier)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TrySetPathSpeed(path.Id, multiplier, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandlePathSegment(
        VSDirectorModSystem system,
        string selector,
        int keyframeIndex,
        double startSpeed,
        double endSpeed,
        string marker)
    {
        CameraPath? path = system.FindPath(selector);
        if (path is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:path-not-found", selector));
        }

        return system.TrySetPathSegmentMetadata(
                path.Id,
                keyframeIndex - 1,
                startSpeed,
                endSpeed,
                marker,
                out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceCreate(VSDirectorModSystem system, string name)
    {
        return system.TryAddSequence(name, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceDelete(VSDirectorModSystem system, string selector)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        return system.TryDeleteSequence(sequence.Id, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceAdd(
        VSDirectorModSystem system,
        string sequenceSelector,
        string cameraSelector,
        double seconds)
    {
        CameraSequence? sequence = system.FindSequence(sequenceSelector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", sequenceSelector));
        }

        CameraPreset? camera = system.FindCamera(cameraSelector);
        if (camera is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:camera-not-found", cameraSelector));
        }

        return system.TryAddSequenceShot(sequence.Id, camera.Id, seconds, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceRemove(
        VSDirectorModSystem system,
        string selector,
        int shotIndex)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        return system.TryRemoveSequenceShot(sequence.Id, shotIndex - 1, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceDuration(
        VSDirectorModSystem system,
        string selector,
        int shotIndex,
        double seconds)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        return system.TrySetSequenceShotDuration(sequence.Id, shotIndex - 1, seconds, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceMove(
        VSDirectorModSystem system,
        string selector,
        int fromIndex,
        int toIndex)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        return system.TryMoveSequenceShot(sequence.Id, fromIndex - 1, toIndex - 1, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequenceLoop(
        VSDirectorModSystem system,
        string selector,
        string mode)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        CameraSequenceLoopMode loopMode = mode == "loop"
            ? CameraSequenceLoopMode.Loop
            : CameraSequenceLoopMode.Once;
        return system.TrySetSequenceLoop(sequence.Id, loopMode, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleSequencePlay(VSDirectorModSystem system, string selector)
    {
        CameraSequence? sequence = system.FindSequence(selector);
        if (sequence is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:sequence-not-found", selector));
        }

        return system.TryPlaySequence(sequence.Id)
            ? TextCommandResult.Success()
            : TextCommandResult.Error(Lang.Get("vsdirector:camera-error", "sequence could not be played"));
    }

    private static TextCommandResult HandleFollowCreate(
        VSDirectorModSystem system,
        string targetSelector,
        string name)
    {
        DirectorFollowTarget? target = system.FindFollowTarget(targetSelector);
        if (target is not DirectorFollowTarget resolved)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:follow-target-not-found", targetSelector));
        }

        return system.TryAddFollowRig(name, resolved, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleFollowDelete(VSDirectorModSystem system, string selector)
    {
        CameraFollowRig? rig = system.FindFollowRig(selector);
        if (rig is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:follow-not-found", selector));
        }

        return system.TryDeleteFollowRig(rig.Id, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleFollowPlay(VSDirectorModSystem system, string selector)
    {
        CameraFollowRig? rig = system.FindFollowRig(selector);
        if (rig is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:follow-not-found", selector));
        }

        return system.TryActivateFollowRig(rig.Id)
            ? TextCommandResult.Success()
            : TextCommandResult.Error(Lang.Get("vsdirector:follow-start-failed"));
    }

    private static TextCommandResult HandleFollowSlot(VSDirectorModSystem system, string selector, int slot)
    {
        CameraFollowRig? rig = system.FindFollowRig(selector);
        if (rig is null)
        {
            return TextCommandResult.Error(Lang.Get("vsdirector:follow-not-found", selector));
        }

        return system.TryAssignFollowSlot(rig.Id, slot, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleFollowOrbit(VSDirectorModSystem system, double angleDegrees)
    {
        return system.TrySetFollowOrbit(angleDegrees, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private static TextCommandResult HandleFollowDistance(VSDirectorModSystem system, double distance)
    {
        return system.TrySetFollowDistance(distance, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

}
