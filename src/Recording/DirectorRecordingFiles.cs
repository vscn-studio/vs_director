using System.Text.Encodings.Web;
using System.Text.Json;
using VSDirector.Data;

namespace VSDirector.Recording;

internal static class DirectorRecordingFiles
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string BuildBaseName(DirectorRecordingStartContext context)
    {
        string timestamp = context.StartUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss");
        string track = SanitizeFilePart(context.Track.Name, "track");
        string session = SanitizeFilePart(context.SessionId, "session");
        if (session.Length > 12)
        {
            session = session[..12];
        }

        return $"{timestamp}_{track}_{session}";
    }

    public static string SanitizeFilePart(string? value, string fallback)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string cleaned = new((value ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray());
        cleaned = cleaned.Trim('.', ' ');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80];
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    public static void WriteSidecar(
        DirectorRecordingStartContext context,
        DirectorRecordingResult result,
        string? convertedOutputPath = null,
        string? note = null)
    {
        string primaryPath = string.IsNullOrWhiteSpace(convertedOutputPath)
            ? result.OutputPath
            : convertedOutputPath;
        string sidecarPath = primaryPath + ".timecode.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sidecarPath))!);
        bool obsManagedAudio = string.Equals(result.Backend, "obs-websocket", StringComparison.OrdinalIgnoreCase);
        object payload = new
        {
            schemaVersion = 3,
            context.SessionId,
            track = new
            {
                context.Track.Id,
                context.Track.Name,
                sourceType = context.Track.SourceType.ToString(),
                context.Track.SourceId
            },
            context.ClientPlayerUid,
            capture = new
            {
                scope = "client-final-framebuffer",
                independentViewCount = 1,
                synchronizedMultiClientSession = true,
                result.TimelineFrameCount,
                result.TimelineDurationSeconds,
                result.DroppedFrames,
                result.DuplicatedFrames,
                result.PeakBacklogFrames,
                result.BackpressureEvents
            },
            audio = new
            {
                managedBy = obsManagedAudio ? "obs-scene" : "native-capture",
                gameAudioRequested = obsManagedAudio ? null : (bool?)context.CaptureGameAudio,
                microphoneRequested = obsManagedAudio ? null : (bool?)context.CaptureMicrophone,
                gameAudioDevice = obsManagedAudio ? null : context.GameAudioDevice,
                microphoneDevice = obsManagedAudio ? null : context.MicrophoneDevice,
                sampleRate = obsManagedAudio ? null : (int?)context.AudioSampleRate,
                channels = obsManagedAudio ? null : (int?)context.AudioChannels,
                captured = obsManagedAudio ? null : (bool?)!string.IsNullOrWhiteSpace(result.AudioPath),
                sourcePath = obsManagedAudio ? null : result.AudioPath,
                error = obsManagedAudio ? null : result.AudioError
            },
            backend = result.Backend,
            container = Path.GetExtension(primaryPath).TrimStart('.').ToLowerInvariant(),
            requestedContainer = obsManagedAudio
                ? "obs-profile"
                : context.Container.ToString().ToLowerInvariant(),
            framesPerSecond = context.FramesPerSecond,
            context.StartWorldMilliseconds,
            startUtc = context.StartUtc,
            startTimecode = context.StartTimecode.ToString(),
            actualStartUtc = result.ActualStartUtc,
            startOffsetMilliseconds = result.StartOffsetSeconds * 1000d,
            stopUtc = result.StopUtc,
            result.DurationSeconds,
            result.FrameCount,
            error = result.Failure,
            outputPath = primaryPath,
            sourceOutputPath = result.OutputPath,
            note = note ?? string.Empty
        };
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
