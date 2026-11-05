using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

public enum DirectorReplayCompressionProfile
{
    Fastest,
    Balanced,
    Smallest
}

public static class DirectorReplayIO
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = false
    };

    public static DirectorReplayDocument Load(string path)
        => Load(path, CancellationToken.None);

    public static Task<DirectorReplayDocument> LoadPagedAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectorReplayDocument? cached = null;
            try { cached = DirectorReplayPages.TryOpen(path); }
            catch (Exception e) when (e is InvalidDataException or JsonException or EndOfStreamException or IOException or UnauthorizedAccessException) { /* Rebuild a damaged or inaccessible optional cache from the native file. */ }
            if (cached is not null && cached.ServerReplay is not { Materialized: false }) { progress?.Report(1); return cached; }
            var document = Load(path, cancellationToken);
            if (document.ServerReplay is { Available: true, Materialized: false })
            {
                if (!DirectorServerReplayCompanion.TryMergeAdjacent(document, path, cancellationToken))
                    throw new FileNotFoundException("This replay requires its matching server companion.");
            }
            try
            {
                // The page file is an optional acceleration index. A read-only
                // replay directory, antivirus lock, or concurrent cache rebuild
                // must never make the valid native replay unplayable.
                DirectorReplayPages.Write(path, document, token: cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                progress?.Report(1);
                return document;
            }
            progress?.Report(1);
            try
            {
                return DirectorReplayPages.TryOpen(path) ?? document;
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or EndOfStreamException or IOException or UnauthorizedAccessException)
            {
                return document;
            }
        }, cancellationToken);

    public static DirectorReplayDocument Load(string path, CancellationToken cancellationToken)
    {
        string fullPath = ValidatePath(path);
        using FileStream file = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using GZipStream gzip = new(file, CompressionMode.Decompress, leaveOpen: false);
        using var cancellable = new DirectorCancellationStream(gzip, cancellationToken);
        DirectorReplayDocument? document = JsonSerializer.Deserialize<DirectorReplayDocument>(cancellable, SerializerOptions);
        return document is null
            ? throw new InvalidDataException($"Replay '{fullPath}' did not contain a JSON document.")
            : DirectorReplayMath.Normalize(document, cancellationToken);
    }

    public static async Task<DirectorReplayDocument> LoadAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ValidatePath(path);
        await using FileStream file = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using ProgressReadStream measured = new(file, progress);
        await using GZipStream gzip = new(measured, CompressionMode.Decompress, leaveOpen: false);
        DirectorReplayDocument? document = await JsonSerializer
            .DeserializeAsync<DirectorReplayDocument>(gzip, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1d);
        return document is null
            ? throw new InvalidDataException($"Replay '{fullPath}' did not contain a JSON document.")
            : DirectorReplayMath.Normalize(document, cancellationToken);
    }

    public static void Save(
        string path,
        DirectorReplayDocument document,
        DirectorReplayCompressionProfile compression = DirectorReplayCompressionProfile.Balanced)
        => SaveCore(path, document, compression, normalize: true, CancellationToken.None);

    public static void Save(
        string path,
        DirectorReplayDocument document,
        DirectorReplayCompressionProfile compression,
        CancellationToken cancellationToken)
        => SaveCore(path, document, compression, normalize: true, cancellationToken);

    internal static void SaveNormalized(string path, DirectorReplayDocument document, DirectorReplayCompressionProfile compression, CancellationToken cancellationToken = default)
        => SaveCore(path, document, compression, normalize: false, cancellationToken);

    private static void SaveCore(string path, DirectorReplayDocument document, DirectorReplayCompressionProfile compression, bool normalize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = ValidatePath(path);
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        DirectorReplayDocument snapshot = normalize ? DirectorReplayMath.Normalize(document, cancellationToken) : document;

        try
        {
            using (FileStream file = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.WriteThrough))
            using (GZipStream gzip = new(file, ResolveCompressionLevel(compression), leaveOpen: false))
            {
                using var cancellable = new DirectorCancellationStream(gzip, cancellationToken);
                JsonSerializer.Serialize(cancellable, snapshot, SerializerOptions);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            // Build indexed playback data at save time, before the capture
            // document is released. Old files build this cache on first open.
            try { DirectorReplayPages.Write(fullPath, snapshot, token: cancellationToken); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* The native replay is safely saved; retry the optional cache on open. */ }

        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static Task SaveAsync(
        string path,
        DirectorReplayDocument document,
        DirectorReplayCompressionProfile compression = DirectorReplayCompressionProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Save(path, document, compression, cancellationToken);
            },
            cancellationToken);
    }

    public static CompressionLevel ResolveCompressionLevel(DirectorReplayCompressionProfile profile)
        => profile switch
        {
            DirectorReplayCompressionProfile.Fastest => CompressionLevel.Fastest,
            DirectorReplayCompressionProfile.Smallest => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath))
            || string.IsNullOrWhiteSpace(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException("The replay path must include a file name.", nameof(path));
        }

        return fullPath;
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream inner;
        private readonly IProgress<double>? progress;
        private readonly long length;

        public ProgressReadStream(Stream inner, IProgress<double>? progress)
        {
            this.inner = inner;
            this.progress = progress;
            length = Math.Max(1L, inner.Length);
            Report();
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Report();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            Report();
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void Report()
        {
            progress?.Report(Math.Clamp(inner.Position / (double)length, 0d, 1d));
        }
    }
}
