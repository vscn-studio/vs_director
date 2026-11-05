using System.Diagnostics;
using VSDirector.Recording;
using Vintagestory.API.MathTools;

namespace VSDirector.Rendering;

internal sealed class DirectorLiveOffscreenSession : IDisposable
{
    private readonly object poseSync = new();
    private readonly DirectorOffscreenMultiViewRenderer renderer;
    private readonly Func<double, DirectorOffscreenCameraPose?> poseEvaluator;
    private readonly Func<Vec3f?> playerPositionEvaluator;
    private readonly Guid viewId;
    private readonly string name;
    private readonly int width;
    private readonly int height;
    private readonly DirectorObsSharedTransport? transport;
    private readonly IDirectorOffscreenFrameSink sink;
    private readonly DirectorObsSharedFrameSink? obsSink;
    private readonly DirectorOffscreenFfmpegSink? fileSink;
    private readonly DirectorOpenAlLoopbackMixer? audio;
    private readonly DirectorVoiceWaveWriter? voiceWave;
    private readonly bool captureVoiceAudio;
    private readonly Stopwatch clock = new();
    private DirectorOffscreenCameraPose currentPose;
    private bool hasPose;
    private bool started;
    private bool disposed;

    internal bool ConsumerConnected => transport?.ConsumerConnected ?? true;

    internal long PublishedVideoFrames
        => transport?.VideoSequence ?? fileSink?.Statistics.EncodedFrames ?? 0L;

    internal bool AudioEnabled => audio is not null;

    internal string VoiceWavePath => voiceWave?.Path ?? string.Empty;

    internal string AudioFailure => audio?.FailureMessage ?? string.Empty;

    internal string OutputFailure => fileSink?.FailureMessage ?? string.Empty;

    internal DirectorOffscreenFfmpegStatistics OutputStatistics
        => fileSink?.Statistics ?? default;

    internal string OutputCodec => fileSink?.SelectedCodec ?? string.Empty;

    internal DirectorOpenAlDiagnostics AudioDiagnostics => audio?.Diagnostics ?? default;

    internal DirectorOffscreenCameraPose? CurrentPose
    {
        get
        {
            lock (poseSync)
            {
                return hasPose ? currentPose : null;
            }
        }
    }

    internal DirectorLiveOffscreenSession(
        DirectorOffscreenMultiViewRenderer renderer,
        string name,
        int width,
        int height,
        int framesPerSecond,
        Func<double, DirectorOffscreenCameraPose?> poseEvaluator,
        Func<Vec3f?> playerPositionEvaluator,
        bool captureDirectorAudio,
        bool captureVoiceAudio)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        viewId = Guid.NewGuid();
        this.name = string.IsNullOrWhiteSpace(name) ? "VS Director" : name.Trim();
        this.width = width;
        this.height = height;
        this.poseEvaluator = poseEvaluator ?? throw new ArgumentNullException(nameof(poseEvaluator));
        this.playerPositionEvaluator = playerPositionEvaluator
            ?? throw new ArgumentNullException(nameof(playerPositionEvaluator));
        this.captureVoiceAudio = captureVoiceAudio;
        transport = new DirectorObsSharedTransport(width, height);
        obsSink = new DirectorObsSharedFrameSink(
            viewId,
            width,
            height,
            framesPerSecond,
            transport);
        sink = obsSink;
        audio = captureDirectorAudio || captureVoiceAudio
            ? new DirectorOpenAlLoopbackMixer(transport, () => CurrentPose, captureDirectorAudio)
            : null;
    }

    internal DirectorLiveOffscreenSession(
        DirectorOffscreenMultiViewRenderer renderer,
        Guid viewId,
        string name,
        int width,
        int height,
        Func<double, DirectorOffscreenCameraPose?> poseEvaluator,
        DirectorOffscreenFfmpegSink fileSink,
        bool captureVoiceAudio,
        string voiceWavePath)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.viewId = viewId == Guid.Empty
            ? throw new ArgumentException("A live offscreen file view requires an id.", nameof(viewId))
            : viewId;
        this.name = string.IsNullOrWhiteSpace(name) ? "VS Director" : name.Trim();
        this.width = width;
        this.height = height;
        this.poseEvaluator = poseEvaluator ?? throw new ArgumentNullException(nameof(poseEvaluator));
        playerPositionEvaluator = static () => null;
        this.fileSink = fileSink ?? throw new ArgumentNullException(nameof(fileSink));
        this.captureVoiceAudio = captureVoiceAudio;
        sink = fileSink;
        voiceWave = captureVoiceAudio && !string.IsNullOrWhiteSpace(voiceWavePath)
            ? new DirectorVoiceWaveWriter(voiceWavePath)
            : null;
    }

    internal bool Start(out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        error = string.Empty;
        if (started)
        {
            error = string.Empty;
            return true;
        }
        if (poseEvaluator(0d) is not DirectorOffscreenCameraPose initial)
        {
            error = "The selected source did not produce an initial camera pose.";
            return false;
        }

        lock (poseSync)
        {
            currentPose = initial;
            hasPose = true;
        }
        if (audio is not null && playerPositionEvaluator() is Vec3f playerPosition)
        {
            audio.UpdatePlayerPosition(playerPosition);
        }
        if (audio is not null && !audio.Start(out error))
        {
            return false;
        }

        renderer.AddSink(sink);
        SetView(initial, 0L);
        renderer.Enabled = true;
        DirectorLiveOffscreenRuntime.Session = this;
        clock.Restart();
        started = true;
        return true;
    }

    internal void SetRecording(bool recording)
        => transport?.SetRecording(recording);

    internal void BeforeOffscreenRender()
    {
        if (!started || disposed)
        {
            return;
        }

        DirectorOffscreenCameraPose? evaluated = poseEvaluator(clock.Elapsed.TotalSeconds);
        DirectorOffscreenCameraPose pose;
        lock (poseSync)
        {
            if (evaluated is DirectorOffscreenCameraPose next)
            {
                currentPose = next;
                hasPose = true;
            }
            if (!hasPose)
            {
                return;
            }
            pose = currentPose;
        }

        SetView(pose, clock.ElapsedMilliseconds);
        if (audio is not null && playerPositionEvaluator() is Vec3f playerPosition)
        {
            audio.UpdatePlayerPosition(playerPosition);
        }
    }

    internal void CaptureSoundEvent(
        Vintagestory.Client.NoObf.ClientMain game,
        Vintagestory.API.Common.AssetLocation? location,
        double x,
        double y,
        double z,
        float volume,
        float pitch,
        float range,
        Vintagestory.API.Common.EnumSoundType soundType)
        => audio?.CaptureSoundEvent(game, location, x, y, z, volume, pitch, range, soundType);

    internal void CaptureVoiceFrame(DirectorVoicePcmFrame frame, long currentTimestampMilliseconds)
    {
        if (captureVoiceAudio)
        {
            audio?.CaptureVoiceFrame(frame);
        }
        if (voiceWave is not null && CurrentPose is DirectorOffscreenCameraPose pose)
        {
            double ageSeconds = frame.TimestampMilliseconds > 0L
                && currentTimestampMilliseconds > frame.TimestampMilliseconds
                ? Math.Min(2d, (currentTimestampMilliseconds - frame.TimestampMilliseconds) / 1000d)
                : 0d;
            voiceWave.Append(frame, pose, Math.Max(0d, clock.Elapsed.TotalSeconds - ageSeconds));
        }
    }

    private void SetView(DirectorOffscreenCameraPose pose, long milliseconds)
    {
        renderer.SetViews(new[]
        {
            new DirectorOffscreenViewRequest
            {
                ViewId = viewId,
                Name = name,
                Width = width,
                Height = height,
                Pose = pose,
                CaptureClockMilliseconds = milliseconds,
                RenderTerrain = true,
                RenderEntities = true,
                RenderLocalPlayer = true,
                RenderOpaqueParticles = true
            }
        });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        clock.Stop();
        if (ReferenceEquals(DirectorLiveOffscreenRuntime.Session, this))
        {
            DirectorLiveOffscreenRuntime.Session = null;
        }
        renderer.RemoveSink(sink);
        renderer.Enabled = false;
        renderer.SetViews(Array.Empty<DirectorOffscreenViewRequest>());
        audio?.Dispose();
        voiceWave?.Dispose();
        if (obsSink is null)
        {
            renderer.ReleaseViews();
            return;
        }
        try
        {
            obsSink.CompleteGpuCleanupOnRenderThread();
            renderer.ReleaseViews();
        }
        finally
        {
            obsSink.Dispose();
            transport?.Dispose();
        }
    }
}

internal static class DirectorLiveOffscreenRuntime
{
    internal static DirectorLiveOffscreenSession? Session { get; set; }
}
