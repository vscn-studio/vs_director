using System.Diagnostics;
using VSDirector.Data;

namespace VSDirector.Recording;

internal sealed record DirectorFfmpegResult(bool Success, string OutputPath, string Message);

internal static class DirectorFfmpegExporter
{
    public static async Task<DirectorFfmpegResult> ExportTimelineAsync(
        string executable,
        DirectorTimelineExportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string diagnosticInputPath = plan.Inputs.FirstOrDefault()?.InputPath ?? plan.OutputPath;
        return await RunAsync(
            executable,
            diagnosticInputPath,
            plan.OutputPath,
            plan.Arguments,
            cancellationToken,
            plan.Inputs.Select(input => input.InputPath).ToArray()).ConfigureAwait(false);
    }

    public static async Task<DirectorFfmpegResult> ExportMp4Async(
        string executable,
        string inputPath,
        string outputPath,
        int framesPerSecond,
        DirectorTimecode startTimecode,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            executable,
            inputPath,
            outputPath,
            DirectorFfmpegArguments.BuildMp4(
                inputPath,
                outputPath,
                framesPerSecond,
                startTimecode),
            cancellationToken).ConfigureAwait(false);

    public static async Task<DirectorFfmpegResult> ExportMp4WithAudioAsync(
        string executable,
        string inputPath,
        string audioPath,
        string outputPath,
        int framesPerSecond,
        DirectorTimecode startTimecode,
        int videoCrf,
        string videoPreset,
        int audioBitrateKbps,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            executable,
            inputPath,
            outputPath,
            DirectorFfmpegArguments.BuildMp4(
                inputPath,
                outputPath,
                framesPerSecond,
                startTimecode,
                audioPath,
                videoCrf,
                videoPreset,
                audioBitrateKbps),
            cancellationToken,
            new[] { inputPath, audioPath }).ConfigureAwait(false);

    public static async Task<DirectorFfmpegResult> RemuxMp4Async(
        string executable,
        string inputPath,
        string audioPath,
        string outputPath,
        DirectorTimecode startTimecode,
        int audioBitrateKbps,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            executable,
            inputPath,
            outputPath,
            DirectorFfmpegArguments.BuildMp4Remux(
                inputPath,
                outputPath,
                startTimecode,
                audioPath,
                audioBitrateKbps),
            cancellationToken,
            new[] { inputPath, audioPath }).ConfigureAwait(false);

    private static async Task<DirectorFfmpegResult> RunAsync(
        string executable,
        string inputPath,
        string outputPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? readinessPaths = null)
    {
        try
        {
            DirectorFfmpegInputReadinessResult readiness = await DirectorFfmpegInputReadiness.WaitAsync(
                readinessPaths ?? new[] { inputPath },
                cancellationToken).ConfigureAwait(false);
            if (!readiness.Ready)
            {
                return new DirectorFfmpegResult(false, inputPath, readiness.Error);
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = DirectorFfmpegLocator.Resolve(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new DirectorFfmpegResult(false, inputPath, "FFmpeg could not be started.");
            }

            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            });
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string errorText = await stderr.ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string message = string.IsNullOrWhiteSpace(errorText)
                    ? $"FFmpeg exited with code {process.ExitCode}."
                    : errorText.Trim();
                if (message.Length > 512)
                {
                    message = message[^512..];
                }

                return new DirectorFfmpegResult(false, inputPath, message);
            }

            return new DirectorFfmpegResult(true, outputPath, string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DirectorFfmpegResult(false, inputPath, exception.Message);
        }
    }
}
