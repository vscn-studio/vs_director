using VSDirector.Data;

namespace VSDirector.Recording;

internal enum DirectorRecordingState
{
    Idle,
    Requesting,
    Armed,
    Starting,
    Recording,
    Stopping,
    Error
}

internal sealed class DirectorRecordingStartContext
{
    public required string SessionId { get; init; }

    public required DirectorRecordingTrack Track { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required long StartWorldMilliseconds { get; init; }

    public required int FramesPerSecond { get; init; }

    public required DirectorRecordingContainer Container { get; init; }

    public required DirectorRecordingBackend Backend { get; init; }

    public required string OutputDirectory { get; init; }

    public required string ClientPlayerUid { get; init; }

    public string NativeCodec { get; init; } = string.Empty;

    public string FfmpegPath { get; init; } = "ffmpeg";

    public int FfmpegVideoCrf { get; init; } = 18;

    public string FfmpegVideoPreset { get; init; } = "medium";

    public int FfmpegAudioBitrateKbps { get; init; } = 192;

    public bool CaptureGameAudio { get; init; }

    public string GameAudioDevice { get; init; } = string.Empty;

    public double GameAudioVolume { get; init; } = 1d;

    public bool CaptureMicrophone { get; init; }

    public string MicrophoneDevice { get; init; } = string.Empty;

    public double MicrophoneVolume { get; init; } = 1d;

    public int AudioSampleRate { get; init; } = 48000;

    public int AudioChannels { get; init; } = 2;

    public DirectorTimecode StartTimecode
        => DirectorTimecode.FromUtc(StartUtc, FramesPerSecond);
}

internal sealed class DirectorRecordingResult
{
    public required string Backend { get; init; }

    public required string OutputPath { get; init; }

    public required DateTimeOffset StopUtc { get; init; }

    public required DateTimeOffset ActualStartUtc { get; init; }

    public required double StartOffsetSeconds { get; init; }

    public required long FrameCount { get; init; }

    public required double DurationSeconds { get; init; }

    public long TimelineFrameCount { get; init; }

    public double TimelineDurationSeconds { get; init; }

    public long DroppedFrames { get; init; }

    public long DuplicatedFrames { get; init; }

    public long PeakBacklogFrames { get; init; }

    public long BackpressureEvents { get; init; }

    public string AudioPath { get; init; } = string.Empty;

    public string AudioError { get; init; } = string.Empty;

    public string Failure { get; set; } = string.Empty;
}
