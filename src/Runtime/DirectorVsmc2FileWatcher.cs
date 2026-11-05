namespace VSDirector.Runtime;

internal sealed record DirectorVsmc2FileChange(Guid ModelId, string Path, bool Exists);

internal sealed class DirectorVsmc2FileWatcher : IDisposable
{
    private readonly object gate = new();
    private readonly Action<DirectorVsmc2FileChange> changed;
    private FileSystemWatcher? watcher;
    private Timer? timer;
    private CancellationTokenSource? pending;
    private Guid modelId;
    private string targetPath = string.Empty;
    private int debounceMilliseconds = 400;
    private bool disposed;

    public DirectorVsmc2FileWatcher(Action<DirectorVsmc2FileChange> changed)
    {
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public void Watch(Guid watchedModelId, string path, int debounceMs)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The Shape directory does not exist.");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            StopCore();
            modelId = watchedModelId;
            targetPath = fullPath;
            debounceMilliseconds = Math.Clamp(debounceMs, 100, 5000);
            timer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(directory)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            StopCore();
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs args)
    {
        if (PathEquals(args.FullPath))
        {
            QueueChange();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        if (PathEquals(args.FullPath) || PathEquals(args.OldFullPath))
        {
            QueueChange();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) => QueueChange();

    private bool PathEquals(string path)
    {
        lock (gate)
        {
            return !disposed && string.Equals(
                Path.GetFullPath(path),
                targetPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    private void QueueChange()
    {
        lock (gate)
        {
            if (disposed || timer is null)
            {
                return;
            }
            pending?.Cancel();
            pending?.Dispose();
            pending = new CancellationTokenSource();
            timer.Change(debounceMilliseconds, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        Guid id;
        string path;
        CancellationToken token;
        lock (gate)
        {
            if (disposed || pending is null)
            {
                return;
            }
            id = modelId;
            path = targetPath;
            token = pending.Token;
        }
        _ = WaitForStableFileAsync(id, path, token);
    }

    private async Task WaitForStableFileAsync(Guid id, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                changed(new DirectorVsmc2FileChange(id, path, false));
                return;
            }

            long previousLength = -1;
            DateTime previousWrite = DateTime.MinValue;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    FileInfo info = new(path);
                    using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length == previousLength && info.LastWriteTimeUtc == previousWrite)
                        {
                            changed(new DirectorVsmc2FileChange(id, path, true));
                            return;
                        }
                        previousLength = stream.Length;
                        previousWrite = info.LastWriteTimeUtc;
                    }
                }
                catch (IOException)
                {
                    // VSMC2 may replace or hold the file while saving; retry the final path.
                }
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
            changed(new DirectorVsmc2FileChange(id, path, File.Exists(path)));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            changed(new DirectorVsmc2FileChange(id, path, File.Exists(path)));
        }
    }

    private void StopCore()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        timer?.Dispose();
        timer = null;
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileEvent;
            watcher.Created -= OnFileEvent;
            watcher.Deleted -= OnFileEvent;
            watcher.Renamed -= OnFileRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            watcher = null;
        }
        modelId = Guid.Empty;
        targetPath = string.Empty;
    }
}
