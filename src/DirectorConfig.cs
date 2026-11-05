namespace VSDirector;

public sealed class DirectorConfig
{
    // Hides the in-game HUD; an open Director workspace still renders in either input mode.
    public bool HideHudOnCamera { get; set; } = true;

    public bool LockPlayerInput { get; set; } = true;

    public bool ShowSwitchNotifications { get; set; } = true;

    public bool ShowWorkflowEditors { get; set; }

    // Retained for config compatibility. Preloading is now permission-driven.
    public bool ShowRemoteChunkPreload { get; set; } = true;

    public bool ShowCameraSequences { get; set; }

    public bool ShowPackageImportExport { get; set; }

    public bool EnableVsmc2Integration { get; set; }

    public string Vsmc2ExecutablePath { get; set; } = string.Empty;

    public bool Vsmc2AutoReload { get; set; }

    public int Vsmc2FileWatchDebounceMs { get; set; } = 400;

    public bool EnableModelOutlines { get; set; }

    public string ModelOutlineColor { get; set; } = "#00FF00";

    public float ModelOutlineThickness { get; set; } = 2f;

    public bool ModelOutlineThroughObstacles { get; set; }

    public List<string> ModelOutlineEntityCodes { get; set; } = new();

    public bool EnableGreenScreenSupport { get; set; }

    public bool EnableGreenScreenSky { get; set; }

    public bool GreenScreenSkyHideClouds { get; set; } = true;

    public bool EnableGreenScreenModels { get; set; }

    public DirectorRecordingBackend RecordingBackend { get; set; } = DirectorRecordingBackend.ObsWebSocket;

    public DirectorRecordingContainer RecordingContainer { get; set; } = DirectorRecordingContainer.Mp4;

    public int RecordingFrameRate { get; set; } = 30;

    public string RecordingOutputDirectory { get; set; } = string.Empty;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int FfmpegVideoCrf { get; set; } = 18;

    public string FfmpegVideoPreset { get; set; } = "medium";

    public int FfmpegAudioBitrateKbps { get; set; } = 192;

    // Retained so archived multi-view code and old config files remain compatible.
    public bool EnableExperimentalOffscreenMultiView { get; set; }

    public DirectorLiveOffscreenBackend LiveOffscreenBackend { get; set; }
        = DirectorLiveOffscreenBackend.Obs;

    public string OffscreenOutputDirectory { get; set; } = string.Empty;

    public int OffscreenWidth { get; set; } = 1920;

    public int OffscreenHeight { get; set; } = 1080;

    public int OffscreenFrameRate { get; set; } = 60;

    public DirectorOffscreenEncoder OffscreenEncoder { get; set; } = DirectorOffscreenEncoder.Auto;

    public int OffscreenVideoQuality { get; set; } = 18;

    public int OffscreenVideoBitrateKbps { get; set; }

    // Game audio and voice audio use separate mixer paths, but both are
    // enabled by default for a complete offscreen recording.
    public bool OffscreenCaptureDirectorAudio { get; set; } = true;

    public bool OffscreenCaptureVoiceModAudio { get; set; } = true;

    public bool OffscreenMuteOtherObsAudio { get; set; } = true;

    public bool OffscreenUseIndependentPlayerRenderer { get; set; }

    public bool DeleteIntermediateAudio { get; set; } = true;

    public bool NativeCaptureGameAudio { get; set; }

    public string NativeGameAudioDevice { get; set; } = string.Empty;

    public double NativeGameAudioVolume { get; set; } = 1d;

    public bool NativeCaptureMicrophone { get; set; }

    public string NativeMicrophoneDevice { get; set; } = string.Empty;

    public double NativeMicrophoneVolume { get; set; } = 1d;

    public int NativeAudioSampleRate { get; set; } = 48000;

    public int NativeAudioChannels { get; set; } = 2;

    public string ObsWebSocketUrl { get; set; } = "ws://127.0.0.1:4455";

    public string ObsWebSocketPassword { get; set; } = string.Empty;

    public bool LockInputWhileRecording { get; set; } = true;

    public bool RecordReplayWithVideo { get; set; } = true;

    public bool AutoRecordReplay { get; set; }

    public Data.DirectorReplayCompressionProfile ReplayCompression { get; set; }
        = Data.DirectorReplayCompressionProfile.Balanced;

    public bool ReplayQuickMode { get; set; }

    public bool ReplayUseDedicatedRenderer { get; set; }

    public double ReplaySampleRate { get; set; } = 30d;

    public int ReplayMaxActors { get; set; } = 256;

    public bool ReplayIncludeNonCreatures { get; set; } = true;

    public bool ReplayCaptureWorld { get; set; } = true;

    public bool ReplayPauseWorld { get; set; }

    public int ReplaySnapshotRadiusChunks { get; set; } = 4;
    public string ReplayOutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Radius of the moving chunk window used to collect third-party voice
    /// while a replay is recording. This is intentionally independent from
    /// the static world-snapshot radius.
    /// </summary>
    public int ReplayVoiceCaptureRadiusChunks { get; set; } = 16;

    public bool ReplayCaptureParticles { get; set; } = true;

    public bool ReplayCaptureSounds { get; set; } = true;

    public bool ReplayCaptureVoiceModAudio { get; set; } = true;

    public double ReplayEntityStateRate { get; set; } = 2d;

    public int ReplayMaxWorldEvents { get; set; } = 1000000;

    public bool ReplayApplyWorldSnapshot { get; set; } = true;

    public bool ReplaySpawnGhostActors { get; set; } = true;

    public int ReplayWorldOperationsPerFrame { get; set; } = 32768;

    public double ReplayFreeCameraSpeed { get; set; } = 12d;

    public double ReplayFreeCameraMouseSensitivity { get; set; } = 0.15d;

    public int ReplayExportWarmupFrames { get; set; } = 1;

    public bool LockInputDuringReplay { get; set; } = true;

    public void Normalize()
    {
        RecordingBackend = DirectorRecordingBackend.ObsWebSocket;
        ShowRemoteChunkPreload = true;
        EnableExperimentalOffscreenMultiView = false;
        // These fields remain readable so old config files migrate cleanly, but their
        // action/model-library UI and runtime entry points have been removed.
        EnableVsmc2Integration = false;
        EnableGreenScreenModels = false;
        Vsmc2ExecutablePath = Vsmc2ExecutablePath?.Trim() ?? string.Empty;
        Vsmc2FileWatchDebounceMs = Math.Clamp(Vsmc2FileWatchDebounceMs, 100, 5000);
        RecordingContainer = DirectorRecordingContainer.Mp4;
        RecordingFrameRate = Data.DirectorTimecode.NormalizeFrameRate(RecordingFrameRate);
        RecordingOutputDirectory = RecordingOutputDirectory?.Trim() ?? string.Empty;
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();
        FfmpegVideoCrf = Math.Clamp(FfmpegVideoCrf, 0, 51);
        FfmpegVideoPreset = NormalizeFfmpegPreset(FfmpegVideoPreset);
        FfmpegAudioBitrateKbps = Math.Clamp(FfmpegAudioBitrateKbps, 64, 512);
        LiveOffscreenBackend = Enum.IsDefined(LiveOffscreenBackend)
            ? LiveOffscreenBackend
            : DirectorLiveOffscreenBackend.Obs;
        OffscreenOutputDirectory = OffscreenOutputDirectory?.Trim() ?? string.Empty;
        OffscreenWidth = NormalizeEvenDimension(OffscreenWidth, 1920);
        OffscreenHeight = NormalizeEvenDimension(OffscreenHeight, 1080);
        OffscreenFrameRate = Math.Clamp(OffscreenFrameRate, 1, 240);
        OffscreenEncoder = Enum.IsDefined(OffscreenEncoder)
            ? OffscreenEncoder
            : DirectorOffscreenEncoder.Auto;
        OffscreenVideoQuality = Math.Clamp(OffscreenVideoQuality, 0, 51);
        OffscreenVideoBitrateKbps = Math.Clamp(OffscreenVideoBitrateKbps, 0, 1000000);
        NativeGameAudioDevice = NativeGameAudioDevice?.Trim() ?? string.Empty;
        NativeMicrophoneDevice = NativeMicrophoneDevice?.Trim() ?? string.Empty;
        NativeGameAudioVolume = NormalizeVolume(NativeGameAudioVolume);
        NativeMicrophoneVolume = NormalizeVolume(NativeMicrophoneVolume);
        NativeAudioSampleRate = Math.Clamp(NativeAudioSampleRate, 8000, 192000);
        NativeAudioChannels = Math.Clamp(NativeAudioChannels, 1, 2);
        NativeCaptureGameAudio = false;
        NativeCaptureMicrophone = false;
        ObsWebSocketUrl = string.IsNullOrWhiteSpace(ObsWebSocketUrl)
            ? "ws://127.0.0.1:4455"
            : ObsWebSocketUrl.Trim();
        ObsWebSocketPassword ??= string.Empty;
        ReplayCompression = Enum.IsDefined(ReplayCompression)
            ? ReplayCompression
            : Data.DirectorReplayCompressionProfile.Balanced;
        ReplaySampleRate = Data.DirectorReplayMath.NormalizeSampleRate(ReplaySampleRate);
        ReplayMaxActors = Math.Clamp(ReplayMaxActors, 1, 2048);
        ReplaySnapshotRadiusChunks = Math.Clamp(ReplaySnapshotRadiusChunks, 0, 16);
        ReplayOutputDirectory = ReplayOutputDirectory?.Trim().Trim('"') ?? string.Empty;
        ReplayVoiceCaptureRadiusChunks = Math.Clamp(ReplayVoiceCaptureRadiusChunks, 0, 16);
        ReplayEntityStateRate = double.IsFinite(ReplayEntityStateRate)
            ? Math.Clamp(ReplayEntityStateRate, 0.1d, 20d)
            : 2d;
        ReplayMaxWorldEvents = Math.Clamp(ReplayMaxWorldEvents, 1000, 5000000);
        ReplayWorldOperationsPerFrame = Math.Clamp(ReplayWorldOperationsPerFrame, 4096, 262144);
        ReplayFreeCameraSpeed = double.IsFinite(ReplayFreeCameraSpeed)
            ? Math.Clamp(ReplayFreeCameraSpeed, 0.1d, 256d)
            : 12d;
        ReplayFreeCameraMouseSensitivity = double.IsFinite(ReplayFreeCameraMouseSensitivity)
            ? Math.Clamp(ReplayFreeCameraMouseSensitivity, 0.01d, 2d)
            : 0.15d;
        ReplayExportWarmupFrames = Math.Clamp(ReplayExportWarmupFrames, 0, 8);
        ModelOutlineColor = NormalizeColorCode(ModelOutlineColor);
        ModelOutlineThickness = float.IsFinite(ModelOutlineThickness)
            ? Math.Clamp(ModelOutlineThickness, 0.5f, 16f)
            : 2f;
        ModelOutlineEntityCodes = (ModelOutlineEntityCodes ?? new List<string>())
            .Select(code => code?.Trim() ?? string.Empty)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CopyFrom(DirectorConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        HideHudOnCamera = source.HideHudOnCamera;
        LockPlayerInput = source.LockPlayerInput;
        ShowSwitchNotifications = source.ShowSwitchNotifications;
        ShowWorkflowEditors = source.ShowWorkflowEditors;
        ShowRemoteChunkPreload = source.ShowRemoteChunkPreload;
        ShowCameraSequences = source.ShowCameraSequences;
        ShowPackageImportExport = source.ShowPackageImportExport;
        EnableVsmc2Integration = source.EnableVsmc2Integration;
        Vsmc2ExecutablePath = source.Vsmc2ExecutablePath;
        Vsmc2AutoReload = source.Vsmc2AutoReload;
        Vsmc2FileWatchDebounceMs = source.Vsmc2FileWatchDebounceMs;
        EnableModelOutlines = source.EnableModelOutlines;
        ModelOutlineColor = source.ModelOutlineColor;
        ModelOutlineThickness = source.ModelOutlineThickness;
        ModelOutlineThroughObstacles = source.ModelOutlineThroughObstacles;
        ModelOutlineEntityCodes = source.ModelOutlineEntityCodes?.ToList() ?? new List<string>();
        EnableGreenScreenSupport = source.EnableGreenScreenSupport;
        EnableGreenScreenSky = source.EnableGreenScreenSky;
        GreenScreenSkyHideClouds = source.GreenScreenSkyHideClouds;
        EnableGreenScreenModels = source.EnableGreenScreenModels;
        RecordingBackend = source.RecordingBackend;
        RecordingContainer = source.RecordingContainer;
        RecordingFrameRate = source.RecordingFrameRate;
        RecordingOutputDirectory = source.RecordingOutputDirectory;
        FfmpegPath = source.FfmpegPath;
        FfmpegVideoCrf = source.FfmpegVideoCrf;
        FfmpegVideoPreset = source.FfmpegVideoPreset;
        FfmpegAudioBitrateKbps = source.FfmpegAudioBitrateKbps;
        EnableExperimentalOffscreenMultiView = source.EnableExperimentalOffscreenMultiView;
        LiveOffscreenBackend = source.LiveOffscreenBackend;
        OffscreenOutputDirectory = source.OffscreenOutputDirectory;
        OffscreenWidth = source.OffscreenWidth;
        OffscreenHeight = source.OffscreenHeight;
        OffscreenFrameRate = source.OffscreenFrameRate;
        OffscreenEncoder = source.OffscreenEncoder;
        OffscreenVideoQuality = source.OffscreenVideoQuality;
        OffscreenVideoBitrateKbps = source.OffscreenVideoBitrateKbps;
        OffscreenCaptureDirectorAudio = source.OffscreenCaptureDirectorAudio;
        OffscreenCaptureVoiceModAudio = source.OffscreenCaptureVoiceModAudio;
        OffscreenMuteOtherObsAudio = source.OffscreenMuteOtherObsAudio;
        OffscreenUseIndependentPlayerRenderer = source.OffscreenUseIndependentPlayerRenderer;
        DeleteIntermediateAudio = source.DeleteIntermediateAudio;
        NativeCaptureGameAudio = source.NativeCaptureGameAudio;
        NativeGameAudioDevice = source.NativeGameAudioDevice;
        NativeGameAudioVolume = source.NativeGameAudioVolume;
        NativeCaptureMicrophone = source.NativeCaptureMicrophone;
        NativeMicrophoneDevice = source.NativeMicrophoneDevice;
        NativeMicrophoneVolume = source.NativeMicrophoneVolume;
        NativeAudioSampleRate = source.NativeAudioSampleRate;
        NativeAudioChannels = source.NativeAudioChannels;
        ObsWebSocketUrl = source.ObsWebSocketUrl;
        ObsWebSocketPassword = source.ObsWebSocketPassword;
        LockInputWhileRecording = source.LockInputWhileRecording;
        RecordReplayWithVideo = source.RecordReplayWithVideo;
        AutoRecordReplay = source.AutoRecordReplay;
        ReplayCompression = source.ReplayCompression;
        ReplayQuickMode = source.ReplayQuickMode;
        ReplayUseDedicatedRenderer = source.ReplayUseDedicatedRenderer;
        ReplaySampleRate = source.ReplaySampleRate;
        ReplayMaxActors = source.ReplayMaxActors;
        ReplayIncludeNonCreatures = source.ReplayIncludeNonCreatures;
        ReplayCaptureWorld = source.ReplayCaptureWorld;
        ReplayPauseWorld = source.ReplayPauseWorld;
        ReplaySnapshotRadiusChunks = source.ReplaySnapshotRadiusChunks;
        ReplayOutputDirectory = source.ReplayOutputDirectory;
        ReplayVoiceCaptureRadiusChunks = source.ReplayVoiceCaptureRadiusChunks;
        ReplayCaptureParticles = source.ReplayCaptureParticles;
        ReplayCaptureSounds = source.ReplayCaptureSounds;
        ReplayCaptureVoiceModAudio = source.ReplayCaptureVoiceModAudio;
        ReplayEntityStateRate = source.ReplayEntityStateRate;
        ReplayMaxWorldEvents = source.ReplayMaxWorldEvents;
        ReplayApplyWorldSnapshot = source.ReplayApplyWorldSnapshot;
        ReplaySpawnGhostActors = source.ReplaySpawnGhostActors;
        ReplayWorldOperationsPerFrame = source.ReplayWorldOperationsPerFrame;
        ReplayFreeCameraSpeed = source.ReplayFreeCameraSpeed;
        ReplayFreeCameraMouseSensitivity = source.ReplayFreeCameraMouseSensitivity;
        ReplayExportWarmupFrames = source.ReplayExportWarmupFrames;
        LockInputDuringReplay = source.LockInputDuringReplay;
    }

    private static double NormalizeVolume(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 2d) : 1d;

    private static int NormalizeEvenDimension(int value, int fallback)
    {
        int normalized = value is >= 16 and <= 8192 ? value : fallback;
        return normalized % 2 == 0 ? normalized : normalized - 1;
    }

    private static string NormalizeFfmpegPreset(string? value)
    {
        string preset = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow"
                ? preset
                : "medium";
    }

    private static string NormalizeColorCode(string? value)
    {
        string code = value?.Trim().TrimStart('#') ?? string.Empty;
        if (code.Length == 3 && code.All(Uri.IsHexDigit))
        {
            code = string.Concat(code.Select(character => new string(character, 2)));
        }
        return code.Length == 6 && code.All(Uri.IsHexDigit)
            ? "#" + code.ToUpperInvariant()
            : "#00FF00";
    }
}

public enum DirectorRecordingBackend
{
    ObsWebSocket
}

public enum DirectorRecordingContainer
{
    Mp4
}

public enum DirectorLiveOffscreenBackend
{
    Obs,
    Ffmpeg
}

public enum DirectorOffscreenEncoder
{
    Auto,
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    Libx264
}
