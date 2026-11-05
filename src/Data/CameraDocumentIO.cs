using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

/// <summary>
/// UTF-8 JSON persistence for camera documents. Writes use a temporary file in
/// the destination directory and publish it with one atomic rename.
/// </summary>
public static class CameraDocumentIO
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static CameraLibraryDocument Load(string path) => Read(path);

    public static CameraLibraryDocument Read(string path)
    {
        string fullPath = ValidatePath(path);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        CameraLibraryDocument? document = JsonSerializer.Deserialize<CameraLibraryDocument>(stream, SerializerOptions);
        return NormalizeDeserialized(document, fullPath);
    }

    public static async Task<CameraLibraryDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
        => await ReadAsync(path, cancellationToken).ConfigureAwait(false);

    public static async Task<CameraLibraryDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ValidatePath(path);
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        CameraLibraryDocument? document = await JsonSerializer
            .DeserializeAsync<CameraLibraryDocument>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        return NormalizeDeserialized(document, fullPath);
    }

    public static void Save(string path, CameraLibraryDocument document) => Write(path, document);

    public static void Save(CameraLibraryDocument document, string path) => Write(path, document);

    public static void Write(string path, CameraLibraryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = ValidatePath(path);
        string temporaryPath = CreateTemporaryPath(fullPath);
        CameraLibraryDocument snapshot = CameraLibrary.Normalize(document);

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    public static Task SaveAsync(
        string path,
        CameraLibraryDocument document,
        CancellationToken cancellationToken = default)
        => WriteAsync(path, document, cancellationToken);

    public static Task SaveAsync(
        CameraLibraryDocument document,
        string path,
        CancellationToken cancellationToken = default)
        => WriteAsync(path, document, cancellationToken);

    public static async Task WriteAsync(
        string path,
        CameraLibraryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = ValidatePath(path);
        string temporaryPath = CreateTemporaryPath(fullPath);
        CameraLibraryDocument snapshot = CameraLibrary.Normalize(document);

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer
                    .SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private static CameraLibraryDocument NormalizeDeserialized(CameraLibraryDocument? document, string path)
    {
        if (document is null)
        {
            throw new InvalidDataException($"Camera library '{path}' did not contain a JSON document.");
        }

        return CameraLibrary.Normalize(document);
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The camera document path must include a file name.", nameof(path));
        }

        return fullPath;
    }

    private static string CreateTemporaryPath(string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The camera document path must include a file name.", nameof(fullPath));
        }

        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // The successful write has already been published. A later save can
            // safely ignore an independently locked temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // See IOException handling above.
        }
    }
}
