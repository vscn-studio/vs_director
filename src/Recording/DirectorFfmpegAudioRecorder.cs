using System.Diagnostics;
using System.Globalization;

namespace VSDirector.Recording;

internal readonly record struct DirectorAudioCaptureResult(
    bool Success,
    string OutputPath,
    string Error);

/// <summary>
/// Captures configured Windows DirectShow audio devices while the game's
/// framebuffer writer records video. No process is started unless at least
/// one real device is explicitly enabled and named.
/// </summary>
internal sealed class DirectorFfmpegAudioRecorder : IDisposable
{
    private Process? process;
    private Task<string>? stderrTask;
    private string outputPath = string.Empty;

    public bool IsRecording => process is not null;

    public string Start(DirectorRecordingStartContext context, string basePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        if (process is not null)
        {
            throw new InvalidOperationException("The native audio recorder is already running.");
        }

        List<(string Device, double Volume)> inputs = new();
        if (context.CaptureGameAudio)
        {
            if (string.IsNullOrWhiteSpace(context.GameAudioDevice))
            {
                throw new InvalidOperationException(
                    "Game audio capture is enabled but no FFmpeg DirectShow audio device is configured.");
            }
            inputs.Add((context.GameAudioDevice.Trim(), context.GameAudioVolume));
        }
        if (context.CaptureMicrophone)
        {
            if (string.IsNullOrWhiteSpace(context.MicrophoneDevice))
            {
                throw new InvalidOperationException(
                    "Microphone capture is enabled but no FFmpeg DirectShow audio device is configured.");
            }
            inputs.Add((context.MicrophoneDevice.Trim(), context.MicrophoneVolume));
        }
        if (inputs.Count == 0)
        {
            return string.Empty;
        }

        outputPath = basePath + ".audio.mka";
        ProcessStartInfo startInfo = new()
        {
            FileName = DirectorFfmpegLocator.Resolve(context.FfmpegPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-y");
        foreach ((string device, _) in inputs)
        {
            startInfo.ArgumentList.Add("-thread_queue_size");
            startInfo.ArgumentList.Add("4096");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("dshow");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("audio=" + device);
        }

        if (inputs.Count == 1)
        {
            startInfo.ArgumentList.Add("-filter:a");
            startInfo.ArgumentList.Add("volume=" + FormatVolume(inputs[0].Volume));
        }
        else
        {
            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add(
                $"[0:a]volume={FormatVolume(inputs[0].Volume)}[game];"
                + $"[1:a]volume={FormatVolume(inputs[1].Volume)}[mic];"
                + "[game][mic]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[a]");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("[a]");
        }
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(Math.Clamp(context.AudioSampleRate, 8000, 192000)
            .ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add(Math.Clamp(context.AudioChannels, 1, 2)
            .ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("flac");
        startInfo.ArgumentList.Add(outputPath);

        Process created = new() { StartInfo = startInfo };
        try
        {
            if (!created.Start())
            {
                throw new InvalidOperationException("FFmpeg audio capture could not be started.");
            }
            process = created;
            stderrTask = created.StandardError.ReadToEndAsync();
            return outputPath;
        }
        catch
        {
            created.Dispose();
            outputPath = string.Empty;
            throw;
        }
    }

    public DirectorAudioCaptureResult Stop()
    {
        Process? active = process;
        Task<string>? errors = stderrTask;
        process = null;
        stderrTask = null;
        if (active is null)
        {
            return new DirectorAudioCaptureResult(false, string.Empty, string.Empty);
        }

        string error = string.Empty;
        try
        {
            if (!active.HasExited)
            {
                active.StandardInput.WriteLine("q");
                active.StandardInput.Flush();
                if (!active.WaitForExit(5000))
                {
                    active.Kill(entireProcessTree: true);
                    active.WaitForExit(2000);
                    error = "FFmpeg audio capture did not stop cleanly.";
                }
            }
            string diagnostics = errors?.GetAwaiter().GetResult() ?? string.Empty;
            if (active.ExitCode != 0 && error.Length == 0)
            {
                error = LastDiagnostic(diagnostics, active.ExitCode);
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                error = error.Length == 0 ? "FFmpeg did not produce an audio track." : error;
            }
            return new DirectorAudioCaptureResult(error.Length == 0, outputPath, error);
        }
        catch (Exception exception)
        {
            return new DirectorAudioCaptureResult(false, outputPath, exception.Message);
        }
        finally
        {
            active.Dispose();
        }
    }

    public void Dispose()
    {
        if (process is not null)
        {
            _ = Stop();
        }
    }

    private static string FormatVolume(double value)
        => (double.IsFinite(value) ? Math.Clamp(value, 0d, 2d) : 1d)
            .ToString("0.###", CultureInfo.InvariantCulture);

    private static string LastDiagnostic(string diagnostics, int exitCode)
    {
        string message = diagnostics.Trim();
        if (message.Length > 768)
        {
            message = message[^768..];
        }
        return message.Length == 0 ? $"FFmpeg audio capture exited with code {exitCode}." : message;
    }
}
