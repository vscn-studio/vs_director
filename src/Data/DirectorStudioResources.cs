using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;

namespace VSDirector.Data;

public readonly record struct DirectorStudioResourceLink(string Source, string Path)
{
    private static readonly Regex SourcePattern = new("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public static DirectorStudioResourceLink Parse(string value)
    {
        if (value is null) throw new InvalidDataException("Studio resource link is missing.");
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidDataException("Studio resource links must be source:relative/path.");
        string source = value[..separator].ToLowerInvariant();
        string path = value[(separator + 1)..].Replace('\\', '/');
        if (!SourcePattern.IsMatch(source) || path.StartsWith('/') || path.EndsWith('/')
            || path.Split('/').Any(segment => segment is "" or "." or ".."
                || segment.Any(ch => char.IsControl(ch) || System.IO.Path.GetInvalidFileNameChars().Contains(ch))))
            throw new InvalidDataException("Studio resource link contains an unsafe source or path.");
        return new(source, path);
    }

    public override string ToString() => Source + ":" + Path;
}

public sealed record DirectorStudioResource(DirectorStudioResourceLink Link, DirectorStudioAssetKind Kind, long SizeBytes);

public interface IDirectorStudioImporter
{
    bool CanImport(string extension);
    DirectorStudioAssetKind Kind(string path);
}

/// <summary>Third-party Studio sources and importers are registered before the workbench opens.</summary>
public static class DirectorStudioResourceExtensions
{
    private static readonly Dictionary<string, string> sources = new(StringComparer.Ordinal);
    private static readonly List<IDirectorStudioImporter> importers = new();

    public static void RegisterSource(string prefix, string directory)
    {
        string normalized = DirectorStudioResourceLink.Parse(prefix + ":probe").Source;
        if (normalized is "studio" or "builtin" || normalized.StartsWith("external", StringComparison.Ordinal))
            throw new ArgumentException("This Studio source prefix is reserved.", nameof(prefix));
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        lock (sources)
        {
            if (!sources.TryAdd(normalized, root))
                throw new InvalidOperationException($"Studio source '{normalized}' is already registered.");
        }
    }

    public static void RegisterImporter(IDirectorStudioImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        lock (sources) importers.Add(importer);
    }

    internal static void Apply(DirectorStudioResourceProvider provider)
    {
        lock (sources)
        {
            foreach (var (prefix, directory) in sources)
                provider.RegisterSource(prefix, directory, requireDirectory: false);
            foreach (IDirectorStudioImporter importer in importers)
                provider.RegisterImporter(importer);
        }
    }
}

/// <summary>Studio-only resource roots. A source is a named directory, never a Director library.</summary>
public sealed class DirectorStudioResourceProvider
{
    private readonly Dictionary<string, (string Root, bool Writable)> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> builtins = new(StringComparer.Ordinal)
    {
        ["models/cube.json"] = Encoding.UTF8.GetBytes("{\"textureWidth\":16,\"textureHeight\":16,\"elements\":[{\"name\":\"Cube\",\"from\":[0,0,0],\"to\":[16,16,16],\"faces\":{\"north\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]},\"east\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]},\"south\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]},\"west\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]},\"up\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]},\"down\":{\"texture\":\"#studio\",\"uv\":[0,0,16,16]}}}]}")
    };
    private readonly List<IDirectorStudioImporter> importers = new();
    private readonly string registryPath;
    public string AssetsDirectory { get; }
    public IReadOnlyList<string> SourceNames => sources.Keys.Append("builtin").OrderBy(name => name).ToArray();

    public DirectorStudioResourceProvider(string studioDirectory)
    {
        AssetsDirectory = Path.Combine(Path.GetFullPath(studioDirectory), "assets");
        registryPath = Path.Combine(Path.GetFullPath(studioDirectory), "sources.json");
        Directory.CreateDirectory(AssetsDirectory);
        RegisterSource("studio", AssetsDirectory, writable: true);
        RegisterImporter(new StandardImporter());
        if (File.Exists(registryPath))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(registryPath))
                ?? throw new InvalidDataException("Studio external source registry is empty.");
            foreach (var (prefix, root) in saved)
                RegisterSource(prefix, root, requireDirectory: false);
        }
        DirectorStudioResourceExtensions.Apply(this);
    }

    public void RegisterSource(string prefix, string directory, bool writable = false, bool requireDirectory = true)
    {
        var probe = DirectorStudioResourceLink.Parse(prefix + ":probe");
        if (probe.Source == "builtin" || probe.Source == "studio" && sources.ContainsKey("studio"))
            throw new InvalidOperationException("A reserved Studio source cannot be replaced.");
        string root = Path.GetFullPath(directory);
        if (requireDirectory && !Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if (!sources.TryAdd(probe.Source, (root, writable)))
            throw new InvalidOperationException($"Studio source '{probe.Source}' is already registered.");
    }

    public void RegisterImporter(IDirectorStudioImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        importers.Insert(0, importer);
    }

    public string AttachExternal(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var existing = sources.FirstOrDefault(item => item.Key != "studio"
            && string.Equals(item.Value.Root, root, StringComparison.OrdinalIgnoreCase));
        if (existing.Key is not null) return existing.Key;
        int index = 1;
        while (sources.ContainsKey("external" + index)) index++;
        string prefix = "external" + index;
        RegisterSource(prefix, root);
        try
        {
            var external = sources.Where(item => item.Key.StartsWith("external", StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value.Root);
            string temporary = registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(external));
                File.Move(temporary, registryPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch
        {
            sources.Remove(prefix);
            throw;
        }
        return prefix;
    }

    public string Resolve(DirectorStudioResourceLink link)
    {
        if (link.Source == "builtin") throw new NotSupportedException("Built-in Studio resources are read through the provider.");
        if (!sources.TryGetValue(link.Source, out var source))
            throw new FileNotFoundException($"Unknown Studio resource source '{link.Source}'.");
        var normalized = DirectorStudioResourceLink.Parse(link.ToString());
        string fullPath = Path.GetFullPath(Path.Combine(source.Root, normalized.Path.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = source.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Studio resource path escapes its source root.");
        string cursor = source.Root;
        foreach (string segment in normalized.Path.Split('/'))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Studio resource links cannot traverse symbolic links.");
        }
        return fullPath;
    }

    public byte[] Read(DirectorStudioResourceLink link)
    {
        if (link.Source == "builtin")
        {
            string builtinPath = DirectorStudioResourceLink.Parse(link.ToString()).Path;
            return builtins.TryGetValue(builtinPath, out byte[]? content)
                ? (byte[])content.Clone() : throw new FileNotFoundException("Built-in Studio resource was not found.", builtinPath);
        }
        string path = Resolve(link);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Studio resource was not found.", path);
        if (info.Length <= 0 || info.Length > DirectorStudioProject.MaximumAssetBytes)
            throw new InvalidDataException("Studio resources must be between 1 byte and 64 MiB.");
        return File.ReadAllBytes(path);
    }

    public IReadOnlyList<DirectorStudioResource> List(string? sourceName = null)
    {
        IEnumerable<KeyValuePair<string, (string Root, bool Writable)>> roots = sources;
        if (!string.IsNullOrWhiteSpace(sourceName))
            roots = roots.Where(source => source.Key == sourceName);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        IEnumerable<DirectorStudioResource> files = roots.Where(source => Directory.Exists(source.Value.Root))
            .SelectMany(source => Directory.EnumerateFiles(source.Value.Root, "*", enumeration).Take(2048)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    string relative = Path.GetRelativePath(source.Value.Root, path).Replace('\\', '/');
                    var link = DirectorStudioResourceLink.Parse(source.Key + ":" + relative);
                    string category = relative.Split('/')[0];
                    DirectorStudioAssetKind kind = category switch
                    {
                        "models" => DirectorStudioAssetKind.Model,
                        "animations" => DirectorStudioAssetKind.Animation,
                        "textures" => DirectorStudioAssetKind.Texture,
                        "audio" => DirectorStudioAssetKind.Audio,
                        _ => DirectorStudioAssetKind.Other
                    };
                    return new DirectorStudioResource(link, kind, new FileInfo(path).Length);
                }));
        if (sourceName is null or "builtin")
            files = files.Concat(builtins.Select(item => new DirectorStudioResource(
                DirectorStudioResourceLink.Parse("builtin:" + item.Key), DirectorStudioAssetKind.Model, item.Value.Length)));
        return files.OrderBy(item => item.Link.Source).ThenBy(item => item.Link.Path).ToArray();
    }

    public DirectorStudioResourceLink Import(string filePath)
    {
        string source = Path.GetFullPath(filePath);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        IDirectorStudioImporter importer = importers.FirstOrDefault(candidate => candidate.CanImport(extension))
            ?? throw new NotSupportedException($"No Studio importer handles '{extension}'.");
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("Studio import file was not found.", source);
        if (info.Length <= 0 || info.Length > DirectorStudioProject.MaximumAssetBytes)
            throw new InvalidDataException("Studio resources must be between 1 byte and 64 MiB.");
        DirectorStudioAssetKind kind = importer.Kind(source);
        string category = kind switch
        {
            DirectorStudioAssetKind.Model => "models",
            DirectorStudioAssetKind.Animation => "animations",
            DirectorStudioAssetKind.Texture => "textures",
            DirectorStudioAssetKind.Audio => "audio",
            _ => "other"
        };
        string baseName = Path.GetFileNameWithoutExtension(source);
        string safeName = new(baseName.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(80).ToArray());
        if (safeName.Length == 0) safeName = "resource";
        string folder = Path.Combine(AssetsDirectory, category);
        Directory.CreateDirectory(folder);
        for (int number = 0; number < 10000; number++)
        {
            string name = safeName + (number == 0 ? "" : "-" + number) + extension;
            string destination = Path.Combine(folder, name);
            if (File.Exists(destination)) continue;
            try
            {
                using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    using FileStream input = File.OpenRead(source);
                    input.CopyTo(target);
                }
                catch
                {
                    target.Dispose();
                    File.Delete(destination);
                    throw;
                }
                return DirectorStudioResourceLink.Parse("studio:" + category + "/" + name);
            }
            catch (IOException) when (File.Exists(destination)) { }
        }
        throw new IOException("Studio resource name space is exhausted.");
    }

    private sealed class StandardImporter : IDirectorStudioImporter
    {
        public bool CanImport(string extension) => extension is ".json" or ".png" or ".jpg" or ".jpeg"
            or ".ogg" or ".wav" or ".vsaction";
        public DirectorStudioAssetKind Kind(string path) => DirectorStudioProject.ImportFile(path).Kind;
    }
}
