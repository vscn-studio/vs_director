using System.Diagnostics;

namespace VSDirector.Recording;

internal readonly record struct DirectorFfmpegInputReadinessResult(bool Ready, string Error)
{
    internal static DirectorFfmpegInputReadinessResult Success => new(true, string.Empty);
}

internal static class DirectorFfmpegInputReadiness
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    internal static Task<DirectorFfmpegInputReadinessResult> WaitAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
        => WaitAsync(
            paths,
            DefaultTimeout,
            DefaultPollInterval,
            ProbeReadable,
            cancellationToken);

    internal static async Task<DirectorFfmpegInputReadinessResult> WaitAsync(
        IEnumerable<string> paths,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Action<string> probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(probe);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string[] inputs = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(comparer)
            .ToArray();
        if (inputs.Length == 0)
        {
            return DirectorFfmpegInputReadinessResult.Success;
        }

        Dictionary<string, Exception> pending = new(comparer);
        foreach (string input in inputs)
        {
            pending[input] = new IOException("The input file has not been checked yet.");
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in pending.Keys.ToArray())
            {
                try
                {
                    probe(path);
                    pending.Remove(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    pending[path] = exception;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Failure(timeout, new Dictionary<string, Exception>(comparer)
                    {
                        [path] = exception
                    });
                }
            }

            if (pending.Count == 0)
            {
                return DirectorFfmpegInputReadinessResult.Success;
            }
            if (elapsed.Elapsed >= timeout)
            {
                return Failure(timeout, pending);
            }

            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }
            await Task.Delay(
                remaining < pollInterval ? remaining : pollInterval,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ProbeReadable(string path)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);
        if (input.Length <= 0L || input.ReadByte() < 0)
        {
            throw new InvalidDataException("The input file is empty or incomplete.");
        }
        if (input.Length > 1L)
        {
            input.Seek(-1L, SeekOrigin.End);
            if (input.ReadByte() < 0)
            {
                throw new InvalidDataException("The input file footer is not readable.");
            }
        }
    }

    private static DirectorFfmpegInputReadinessResult Failure(
        TimeSpan timeout,
        IReadOnlyDictionary<string, Exception> failures)
    {
        string detail = string.Join(
            "; ",
            failures.Select(pair => $"'{pair.Key}' ({pair.Value.GetType().Name}: {pair.Value.Message})"));
        return new DirectorFfmpegInputReadinessResult(
            false,
            $"FFmpeg input file(s) remained unavailable after {timeout.TotalSeconds:0.###} seconds; "
                + $"source files were retained. {detail}");
    }
}
