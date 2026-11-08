using System.Diagnostics;

namespace VSDirector.Runtime;

internal sealed class DirectorVsmc2ProcessBridge
{
    public int? LastProcessId { get; private set; }

    public DateTimeOffset? LastStartedUtc { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public bool TryOpenShape(string executablePath, string shapePath, out string error)
    {
        error = string.Empty;
        string executable = executablePath?.Trim() ?? string.Empty;
        string shape = shapePath?.Trim() ?? string.Empty;
        if (executable.Length == 0)
        {
            return Fail("VSMC2 executable is not configured.", out error);
        }
        if (!File.Exists(executable))
        {
            return Fail("VSMC2 executable does not exist.", out error);
        }
        if (!File.Exists(shape))
        {
            return Fail("Shape file does not exist.", out error);
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Path.GetFullPath(executable),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty
            };
            startInfo.ArgumentList.Add(Path.GetFullPath(shape));
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The operating system did not start VSMC2.");
            LastProcessId = process.Id;
            LastStartedUtc = DateTimeOffset.UtcNow;
            LastError = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            return Fail(exception.GetBaseException().Message, out error);
        }
    }

    public bool TryOpenDirectory(string shapePath, out string error)
    {
        error = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(shapePath?.Trim() ?? string.Empty);
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, out error);
        }
        string? directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Fail("Shape directory does not exist.", out error);
        }
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("The operating system did not open the directory.");
            LastError = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            return Fail(exception.GetBaseException().Message, out error);
        }
    }

    private bool Fail(string message, out string error)
    {
        LastError = message;
        error = message;
        return false;
    }
}
