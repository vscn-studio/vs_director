namespace VSDirector.Data;

public static class DirectorFfmpegArguments
{
    public static IReadOnlyList<string> BuildMp4(
        string inputPath,
        string outputPath,
        int framesPerSecond,
        DirectorTimecode startTimecode,
        string? audioPath = null,
        int videoCrf = 18,
        string videoPreset = "medium",
        int audioBitrateKbps = 192)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        int fps = DirectorTimecode.NormalizeFrameRate(framesPerSecond);
        List<string> arguments = new()
        {
            "-hide_banner",
            "-y",
            "-i",
            inputPath
        };
        bool includeAudio = !string.IsNullOrWhiteSpace(audioPath);
        if (includeAudio)
        {
            arguments.Add("-i");
            arguments.Add(audioPath!);
            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-map");
            arguments.Add("1:a:0");
        }
        else
        {
            arguments.Add("-an");
        }
        arguments.AddRange(new[]
        {
            "-c:v",
            "libx264",
            "-preset",
            NormalizePreset(videoPreset),
            "-crf",
            Math.Clamp(videoCrf, 0, 51).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-pix_fmt",
            "yuv420p",
            "-r",
            fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-timecode",
            startTimecode.ToString(),
            "-movflags",
            "+faststart"
        });
        if (includeAudio)
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
            arguments.Add("-b:a");
            arguments.Add(Math.Clamp(audioBitrateKbps, 64, 512)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + "k");
            arguments.Add("-af");
            arguments.Add("apad");
            arguments.Add("-shortest");
        }
        arguments.Add(outputPath);
        return arguments;
    }

    public static IReadOnlyList<string> BuildMp4Remux(
        string videoPath,
        string outputPath,
        DirectorTimecode startTimecode,
        string? audioPath = null,
        int audioBitrateKbps = 192)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        bool includeAudio = !string.IsNullOrWhiteSpace(audioPath);
        List<string> arguments = new()
        {
            "-hide_banner", "-y",
            "-i", videoPath
        };
        if (includeAudio)
        {
            arguments.Add("-i");
            arguments.Add(audioPath!);
            arguments.AddRange(new[]
            {
                "-map", "0:v:0",
                "-map", "1:a:0"
            });
        }
        else
        {
            arguments.Add("-an");
        }
        arguments.AddRange(new[]
        {
            "-c:v", "copy",
            "-timecode", startTimecode.ToString()
        });
        if (includeAudio)
        {
            arguments.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", Math.Clamp(audioBitrateKbps, 64, 512)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture) + "k",
                "-af", "apad",
                "-shortest"
            });
        }
        arguments.AddRange(new[] { "-movflags", "+faststart", outputPath });
        return arguments;
    }

    private static string NormalizePreset(string? value)
    {
        string preset = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow"
                ? preset
                : "medium";
    }
}
