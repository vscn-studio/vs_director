using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Recording;

internal static class DirectorTakeFiles
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string WriteManifest(
        string sessionId,
        DirectorRecordingTrack track,
        string videoOutputPath,
        DirectorReplayInfo replay,
        DateTimeOffset startedUtc,
        DateTimeOffset videoStartedUtc,
        string videoError)
    {
        string resolvedVideoPath = videoOutputPath?.Trim() ?? string.Empty;
        string anchorPath = resolvedVideoPath.Length > 0 ? resolvedVideoPath : replay.Path;
        string manifestPath = anchorPath + ".take.json";
        DateTimeOffset effectiveVideoStart = videoStartedUtc == default ? startedUtc : videoStartedUtc;
        object payload = new
        {
            schemaVersion = 1,
            kind = "vsdirector-take",
            sessionId,
            startedUtc,
            completedUtc = DateTimeOffset.UtcNow,
            track = new
            {
                track.Id,
                track.Name,
                sourceType = track.SourceType.ToString(),
                track.SourceId
            },
            video = new
            {
                kind = "rendered-video",
                outputPath = resolvedVideoPath,
                actualStartUtc = effectiveVideoStart,
                replayStartOffsetSeconds = Math.Max(0d,
                    (effectiveVideoStart - startedUtc).TotalSeconds),
                captureScope = "client-final-framebuffer",
                independentViewCount = 1,
                error = videoError ?? string.Empty
            },
            replaySource = new
            {
                kind = "editable-replay-source",
                replay.Id,
                replay.Name,
                replay.Path,
                replay.DurationSeconds,
                replay.ActorCount,
                replay.CreatedUtc
            }
        };

        WriteAtomic(manifestPath, JsonSerializer.Serialize(payload, SerializerOptions));
        TryLinkTimecodeSidecar(resolvedVideoPath, manifestPath, replay);
        return manifestPath;
    }

    private static void TryLinkTimecodeSidecar(
        string videoOutputPath,
        string manifestPath,
        DirectorReplayInfo replay)
    {
        if (videoOutputPath.Length == 0)
        {
            return;
        }

        string sidecarPath = videoOutputPath + ".timecode.json";
        if (!File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(sidecarPath));
            if (parsed is not JsonObject root)
            {
                return;
            }

            root["take"] = new JsonObject
            {
                ["manifestPath"] = manifestPath,
                ["replaySourceId"] = replay.Id.ToString("D"),
                ["replaySourcePath"] = replay.Path
            };
            WriteAtomic(sidecarPath, root.ToJsonString(SerializerOptions));
        }
        catch (JsonException)
        {
            // The standalone take manifest remains authoritative if an older
            // or externally modified timecode sidecar cannot be extended.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
