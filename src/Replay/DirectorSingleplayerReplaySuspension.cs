using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace VSDirector.Replay;

/// <summary>
/// Suspends the integrated server without setting ClientMain.IsPaused. Replay
/// rendering can therefore keep its own clock while the live world is inert.
/// </summary>
internal sealed class DirectorSingleplayerReplaySuspension : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly ClientMain game;
    private GameCalendar? calendar;
    private bool restorePlatformPause;
    private bool calendarRunsWhenUnpaused;

    internal DirectorSingleplayerReplaySuspension(ICoreClientAPI capi, ClientMain game)
    {
        this.capi = capi;
        this.game = game;
    }

    internal bool IsActive { get; private set; }

    internal static bool IsEligible(
        bool isSinglePlayer,
        bool openedToLan,
        bool blocksReceivedAndLoaded)
        => isSinglePlayer && !openedToLan && blocksReceivedAndLoaded;

    internal bool TryAcquire()
    {
        if (IsActive)
        {
            return true;
        }
        if (!IsEligible(capi.IsSinglePlayer, capi.OpenedToLan, game.BlocksReceivedAndLoaded))
        {
            return false;
        }

        calendar = capi.World.Calendar as GameCalendar;
        bool platformWasPaused = game.Platform.IsGamePaused;
        bool calendarWasRunning = calendar?.IsRunning == true;
        // A normal escape-menu pause sets both flags. Preserve only a
        // pre-existing platform-only suspension; the current client pause is
        // considered again when replay cleanup runs.
        restorePlatformPause = game.Platform.IsGamePaused && !game.IsPaused;
        calendarRunsWhenUnpaused = calendar?.IsRunning == true || game.IsPaused;
        IsActive = true;
        try
        {
            game.Platform.SetGamePausedState(paused: true);
            calendar?.Stop();
            capi.Logger.Notification(
                "VS Director: singleplayer world simulation suspended for replay playback.");
            return true;
        }
        catch (Exception exception)
        {
            IsActive = false;
            try
            {
                game.Platform.SetGamePausedState(platformWasPaused);
                if (calendarWasRunning)
                {
                    calendar?.Start();
                }
            }
            catch
            {
            }
            capi.Logger.Warning(
                "VS Director: singleplayer world simulation could not be suspended: {0}",
                exception.GetBaseException().Message);
            calendar = null;
            restorePlatformPause = false;
            calendarRunsWhenUnpaused = false;
            return false;
        }
    }

    /// <summary>
    /// Reasserts ownership after vanilla UI code changes the platform pause
    /// state. False means the session became LAN-hosted and playback must end.
    /// </summary>
    internal bool Maintain()
    {
        if (!IsActive)
        {
            return true;
        }
        if (!IsEligible(capi.IsSinglePlayer, capi.OpenedToLan, game.BlocksReceivedAndLoaded))
        {
            HoldDuringPlaybackCleanup();
            return false;
        }

        try
        {
            if (!game.Platform.IsGamePaused)
            {
                game.Platform.SetGamePausedState(paused: true);
            }
            if (calendar?.IsRunning == true)
            {
                calendar.Stop();
            }
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: singleplayer replay suspension could not be maintained: {0}",
                exception.GetBaseException().Message);
            return false;
        }
    }

    private void HoldDuringPlaybackCleanup()
    {
        try
        {
            game.Platform.SetGamePausedState(paused: true);
            calendar?.Stop();
        }
        catch
        {
        }
    }

    internal void Release()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        bool keepPlatformPaused = restorePlatformPause || game.IsPaused;
        try
        {
            game.Platform.SetGamePausedState(keepPlatformPaused);
            if (game.IsPaused || !calendarRunsWhenUnpaused)
            {
                calendar?.Stop();
            }
            else
            {
                calendar?.Start();
            }
            capi.Logger.Notification(keepPlatformPaused
                ? "VS Director: replay world suspension released; the game remains paused."
                : "VS Director: singleplayer world simulation resumed after replay playback.");
        }
        catch (Exception exception)
        {
            capi.Logger.Warning(
                "VS Director: singleplayer world simulation could not be restored cleanly: {0}",
                exception.GetBaseException().Message);
        }
        finally
        {
            calendar = null;
            restorePlatformPause = false;
            calendarRunsWhenUnpaused = false;
        }
    }

    public void Dispose() => Release();
}

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.PauseGame), new[] { typeof(bool) })]
internal static class ReplaySingleplayerPauseIsolationPatch
{
    [HarmonyPostfix]
    private static void Postfix()
        => DirectorReplayRuntime.Manager?.MaintainSingleplayerWorldSuspension();
}
