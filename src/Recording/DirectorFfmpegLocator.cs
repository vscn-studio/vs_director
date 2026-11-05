using System.Reflection;
using System.Diagnostics;

namespace VSDirector.Recording;

internal readonly record struct DirectorFfmpegInspection(
    bool Available,
    string ResolvedPath,
    string Version,
    IReadOnlyList<string> Encoders,
    string Error);

internal readonly record struct DirectorFfmpegDownloadInfo(
    string PlatformName,
    string RecommendedVersion,
    string DownloadPageUrl,
    string DirectArchiveUrl,
    string ArchiveName);

internal static class DirectorFfmpegLocator
{
    private const string DefaultExecutable = "ffmpeg";
    internal const string RecommendedVersion = "8.0.x";
    internal const string OfficialDownloadUrl = "https://ffmpeg.org/download.html";
    private static string managedDirectory = string.Empty;

    internal static string ManagedDirectory => Volatile.Read(ref managedDirectory);

    internal static string ManagedExecutablePath
    {
        get
        {
            string directory = ManagedDirectory;
            return directory.Length == 0
                ? (OperatingSystem.IsWindows() ? "ffmpeg.exe" : DefaultExecutable)
                : Path.Combine(
                    directory,
                    OperatingSystem.IsWindows() ? "ffmpeg.exe" : DefaultExecutable);
        }
    }

    internal static DirectorFfmpegDownloadInfo CurrentDownload
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return new DirectorFfmpegDownloadInfo(
                    "Windows x64",
                    RecommendedVersion,
                    "https://www.gyan.dev/ffmpeg/builds/",
                    "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                    "ffmpeg-release-essentials.zip");
            }
            if (OperatingSystem.IsMacOS())
            {
                return new DirectorFfmpegDownloadInfo(
                    "macOS",
                    RecommendedVersion,
                    "https://evermeet.cx/ffmpeg/",
                    "https://evermeet.cx/ffmpeg/getrelease/zip",
                    "ffmpeg macOS release ZIP");
            }

            bool arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64;
            string architecture = arm64 ? "arm64" : "amd64";
            return new DirectorFfmpegDownloadInfo(
                arm64 ? "Linux ARM64" : "Linux x86_64",
                RecommendedVersion,
                "https://johnvansickle.com/ffmpeg/",
                $"https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-{architecture}-static.tar.xz",
                $"ffmpeg-release-{architecture}-static.tar.xz");
        }
    }

    internal static void ConfigureManagedDirectory(string? directory)
    {
        string normalized = string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : Path.GetFullPath(directory.Trim());
        Volatile.Write(ref managedDirectory, normalized);
    }

    internal static string PreferredConfiguredPath(string? configuredPath)
    {
        string configured = configuredPath?.Trim() ?? string.Empty;
        return IsDefaultName(configured)
            ? ManagedExecutablePath
            : configured;
    }

    public static string Resolve(string? configuredPath)
    {
        string configured = configuredPath?.Trim() ?? string.Empty;
        if (!IsDefaultName(configured))
        {
            return configured;
        }

        string managedExecutable = ManagedExecutablePath;
        if (ManagedDirectory.Length > 0 && File.Exists(managedExecutable))
        {
            return managedExecutable;
        }

        if (!OperatingSystem.IsWindows())
        {
            return configured.Length == 0 ? DefaultExecutable : configured;
        }

        string assemblyDirectory = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string[] bundledCandidates =
        {
            Path.Combine(assemblyDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(assemblyDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(assemblyDirectory, "ffmpeg.exe")
        };
        return bundledCandidates.FirstOrDefault(File.Exists)
            ?? (configured.Length == 0 ? DefaultExecutable : configured);
    }

    public static bool IsBundled(string executable)
    {
        string resolved = Resolve(executable);
        if (!File.Exists(resolved))
        {
            return false;
        }

        string assemblyDirectory = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string toolsDirectory = Path.GetFullPath(Path.Combine(assemblyDirectory, "tools", "ffmpeg"));
        string candidate = Path.GetFullPath(resolved);
        return candidate.StartsWith(
            toolsDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveRequired(string? configuredPath)
    {
        string resolved = Resolve(configuredPath);
        if (File.Exists(resolved))
        {
            return Path.GetFullPath(resolved);
        }

        string? pathExecutable = FindOnPath(resolved);
        if (pathExecutable is not null)
        {
            return pathExecutable;
        }

        throw new FileNotFoundException(
            $"FFmpeg executable '{resolved}' was not found. Download FFmpeg {RecommendedVersion} "
            + $"and place it at '{ManagedExecutablePath}', configure another absolute path, "
            + "or install ffmpeg on the system PATH.",
            resolved);
    }

    public static DirectorFfmpegInspection Inspect(string? configuredPath)
    {
        string executable;
        try
        {
            executable = ResolveRequired(configuredPath);
        }
        catch (Exception exception)
        {
            return new DirectorFfmpegInspection(
                false,
                PreferredConfiguredPath(configuredPath),
                string.Empty,
                Array.Empty<string>(),
                exception.GetBaseException().Message);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");
        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new DirectorFfmpegInspection(
                    false, executable, string.Empty, Array.Empty<string>(), "FFmpeg could not be started.");
            }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
                return new DirectorFfmpegInspection(
                    false, executable, string.Empty, Array.Empty<string>(), "FFmpeg inspection timed out.");
            }

            string output = stdout.GetAwaiter().GetResult();
            string diagnostics = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return new DirectorFfmpegInspection(
                    false,
                    executable,
                    string.Empty,
                    Array.Empty<string>(),
                    string.IsNullOrWhiteSpace(diagnostics)
                        ? $"FFmpeg exited with code {process.ExitCode}."
                        : diagnostics.Trim());
            }

            string[] knownEncoders = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
            string[] available = knownEncoders
                .Where(codec => output.Contains(codec, StringComparison.Ordinal)
                    && CanInitializeEncoder(executable, codec))
                .ToArray();
            string version = (output + "\n" + diagnostics)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                ?.Trim()
                ?? "FFmpeg";
            return new DirectorFfmpegInspection(
                true, executable, version, available, string.Empty);
        }
        catch (Exception exception)
        {
            return new DirectorFfmpegInspection(
                false,
                executable,
                string.Empty,
                Array.Empty<string>(),
                exception.GetBaseException().Message);
        }
    }

    private static string? FindOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
        {
            return null;
        }

        string[] names = OperatingSystem.IsWindows() && Path.GetExtension(executable).Length == 0
            ? new[] { executable + ".exe", executable }
            : new[] { executable };
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static bool CanInitializeEncoder(string executable, string codec)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=0.1",
            "-frames:v", "1", "-an", "-c:v", codec,
            "-f", "null", "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return false;
            }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(6000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
                return false;
            }
            _ = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDefaultName(string value)
        => value.Length == 0
            || string.Equals(value, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
}
