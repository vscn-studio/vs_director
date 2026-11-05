using System.Diagnostics;

namespace VSDirector.Recording;

internal sealed record DirectorAudioDeviceCatalog(
    IReadOnlyList<string> Devices,
    string Error);

internal static class DirectorFfmpegAudioDevices
{
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(6);

    public static async Task<DirectorAudioDeviceCatalog> EnumerateAsync(
        string? configuredFfmpegPath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DirectorAudioDeviceCatalog(
                Array.Empty<string>(),
                "DirectShow audio device enumeration is only available on Windows.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = DirectorFfmpegLocator.Resolve(configuredFfmpegPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-list_devices");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("dshow");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("dummy");

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new DirectorAudioDeviceCatalog(
                    Array.Empty<string>(),
                    "FFmpeg audio device enumeration could not be started.");
            }

            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(EnumerationTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new DirectorAudioDeviceCatalog(
                    Array.Empty<string>(),
                    "FFmpeg audio device enumeration timed out.");
            }

            string diagnostics = await stderr.ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            string[] devices = ParseDirectShowAudioDevices(diagnostics).ToArray();
            return devices.Length > 0
                ? new DirectorAudioDeviceCatalog(devices, string.Empty)
                : new DirectorAudioDeviceCatalog(
                    devices,
                    LastDiagnostic(diagnostics, "FFmpeg did not report any DirectShow audio devices."));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            return new DirectorAudioDeviceCatalog(Array.Empty<string>(), exception.Message);
        }
    }

    internal static IReadOnlyList<string> ParseDirectShowAudioDevices(string? diagnostics)
    {
        List<string> devices = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (diagnostics ?? string.Empty).Split('\n'))
        {
            if (!line.Contains("(audio)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int firstQuote = line.IndexOf('"');
            int secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
            if (firstQuote < 0 || secondQuote <= firstQuote + 1)
            {
                continue;
            }

            string device = line[(firstQuote + 1)..secondQuote].Trim();
            if (device.Length > 0 && seen.Add(device))
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string LastDiagnostic(string diagnostics, string fallback)
    {
        string[] lines = diagnostics
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string message = lines.LastOrDefault(line => !line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return message.Length == 0 ? fallback : message;
    }
}
