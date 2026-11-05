using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using VSDirector.Networking;
using VSDirector.Recording;
using ImGuiNET;
using VSImGui.API;

namespace VSDirector.Gui;

internal sealed partial class DirectorSettingsDialog : DirectorImGuiDialog
{
    private readonly ICoreClientAPI capi;
    private readonly VSDirectorModSystem owner;
    private DirectorConfig draft;
    private SettingsPage page;
    private string status = string.Empty;
    private bool ffmpegTestRunning;
    private bool disposed;
    private string[] outlineEntityValues = [];
    private string[] outlineEntityNames = [];
    private DirectorPermissionMemberDto? permissionMember;
    private DirectorPermission permissionDraftMask;

    public DirectorSettingsDialog(ICoreClientAPI capi, VSDirectorModSystem owner)
        : base(capi, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar, Lang.Get("vsdirector:settings-title"), "vsdirector-settings", false)
    {
        this.capi = capi;
        this.owner = owner;
        draft = Clone(owner.Config);
    }

    public void ToggleOpen()
    {
        if (IsOpened()) { TryClose(); return; }
        draft = Clone(owner.Config);
        status = string.Empty;
        permissionMember = null;
        permissionsExpanded = false;
        if (!Enum.GetValues<SettingsPage>().Any(IsPageAvailable)) return;
        NormalizePage();
        Open();
    }
    public bool IsOpened() => Opened;
    public bool TryClose() { Close(); return true; }

    internal void OpenReplayStorage()
    {
        page = SettingsPage.Replay;
        if (IsOpened()) return;
        ToggleOpen();
    }

    public void RefreshPermissions()
    {
        if (!IsOpened()) return;
        if (!Enum.GetValues<SettingsPage>().Any(IsPageAvailable)) { TryClose(); return; }
        if (!owner.IsDirectorAdmin) permissionMember = null;
        NormalizePage();
    }

    public void RefreshFromConfig()
    {
        draft = Clone(owner.Config);
        if (IsOpened()) NormalizePage();
    }

    public new void Dispose() { if (disposed) return; disposed = true; base.Dispose(); }

    private bool IsPageAvailable(SettingsPage target) => target switch
    {
        SettingsPage.Recording => false,
        SettingsPage.Replay => true,
        SettingsPage.Interface => owner.HasPermission(DirectorPermission.FreeCamera) || owner.HasPermission(DirectorPermission.CameraLibrary)
            || owner.HasPermission(DirectorPermission.PathLibrary) || owner.HasPermission(DirectorPermission.FollowCameras),
        SettingsPage.Offscreen => owner.HasPermission(DirectorPermission.OffscreenCurrentView) || owner.HasPermission(DirectorPermission.OffscreenSavedSources),
        SettingsPage.Accessibility => owner.HasWorkspacePermission,
        SettingsPage.Experimental or SettingsPage.Permissions => owner.IsDirectorAdmin,
        _ => false
    };

    private void NormalizePage()
    {
        if (!IsPageAvailable(page)) page = Enum.GetValues<SettingsPage>().First(IsPageAvailable);
    }

    private bool Save()
    {
        bool applied = owner.TryApplySettings(draft, out string message);
        if (applied) draft = Clone(owner.Config);
        status = message;
        return applied;
    }

    private bool SavePermissions()
    {
        if (permissionMember is not { } member) return false;
        if (!owner.TryUpdateMemberPermissions(member.PlayerUid, permissionDraftMask, out string permissionMessage))
        {
            status = permissionMessage;
            return false;
        }
        status = permissionMessage;
        permissionMember = null;
        return true;
    }

    private bool Reload()
    {
        if (owner.TryReloadConfig(out string message)) draft = Clone(owner.Config);
        status = message;
        return true;
    }

    private bool ResetDefaults()
    {
        draft = new DirectorConfig();
        draft.Normalize();
        status = Lang.Get("vsdirector:settings-defaults-pending");
        return true;
    }

    private bool TestFfmpeg()
    {
        if (ffmpegTestRunning || disposed) return true;
        ffmpegTestRunning = true;
        status = Lang.Get("vsdirector:settings-ffmpeg-testing");
        string configuredPath = draft.FfmpegPath;
        _ = Task.Run(() => DirectorFfmpegLocator.Inspect(configuredPath)).ContinueWith(completed =>
        {
            capi.Event.EnqueueMainThreadTask(() =>
            {
                if (disposed) return;
                ffmpegTestRunning = false;
                DirectorFfmpegInspection inspection = completed.Status == TaskStatus.RanToCompletion
                    ? completed.Result
                    : new DirectorFfmpegInspection(false, configuredPath, string.Empty, [],
                        completed.Exception?.GetBaseException().Message ?? "FFmpeg inspection failed.");
                status = inspection.Available
                    ? Lang.Get("vsdirector:settings-ffmpeg-ok", inspection.ResolvedPath,
                        inspection.Encoders.Count == 0 ? Lang.Get("vsdirector:settings-ffmpeg-no-h264") : string.Join(", ", inspection.Encoders))
                    : Lang.Get("vsdirector:settings-ffmpeg-failed", inspection.Error);
            }, "vsdirector-ffmpeg-inspection-complete");
        }, TaskScheduler.Default);
        return true;
    }

    private bool OpenFfmpegDownload()
    {
        var download = DirectorFfmpegLocator.CurrentDownload;
        string managedPath = DirectorFfmpegLocator.ManagedExecutablePath;
        draft.FfmpegPath = managedPath;
        try
        {
            if (DirectorFfmpegLocator.ManagedDirectory.Length > 0) Directory.CreateDirectory(DirectorFfmpegLocator.ManagedDirectory);
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = download.DownloadPageUrl, UseShellExecute = true
            });
            status = Lang.Get("vsdirector:settings-ffmpeg-download-opened", download.PlatformName,
                download.RecommendedVersion, download.ArchiveName, managedPath);
        }
        catch (Exception exception) { status = Lang.Get("vsdirector:settings-ffmpeg-download-failed", exception.GetBaseException().Message); }
        return true;
    }

    private void BuildOutlineEntityOptions()
    {
        Dictionary<string, string> names = owner.ActionModels
            .Where(model => !model.PreviewOnly && !string.IsNullOrWhiteSpace(model.EntityCode))
            .GroupBy(model => model.EntityCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Name,
                StringComparer.OrdinalIgnoreCase);
        names[VSDirectorModSystem.VanillaPlayerModelCode] =
            Lang.Get("vsdirector:settings-outline-player-model");
        foreach (EntityProperties properties in capi.World.EntityTypes)
        {
            string code = properties.Code?.ToShortString() ?? string.Empty;
            string rendererName = properties.Client?.RendererName ?? string.Empty;
            if (code.Length > 0
                && rendererName.Contains("Shape", StringComparison.OrdinalIgnoreCase))
            {
                names.TryAdd(code, code);
            }
        }
        foreach (string code in draft.ModelOutlineEntityCodes)
        {
            names.TryAdd(code, code);
        }

        (string Code, string Name)[] ordered = names
            .Select(entry => (
                entry.Key,
                string.Equals(entry.Key, entry.Value, StringComparison.OrdinalIgnoreCase)
                    ? entry.Key
                    : entry.Value + " — " + entry.Key))
            .OrderBy(entry => entry.Item2, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        outlineEntityValues = ordered.Select(entry => entry.Code).ToArray();
        outlineEntityNames = ordered.Select(entry => entry.Name).ToArray();
    }

    private static DirectorConfig Clone(DirectorConfig source)
    {
        var copy = new DirectorConfig();
        copy.CopyFrom(source);
        return copy;
    }

    private enum SettingsPage { Recording, Replay, Interface, Offscreen, Accessibility, Experimental, Permissions }
}
