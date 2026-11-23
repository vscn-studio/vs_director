using System.Text.Json;

namespace VSDirector.Data;

/// <summary>
/// Favourites, recently used and use counts of replay presets, stored in
/// ModData/VSDirector/replay-preset-usage.json. Writes are atomic (temp file + move) and
/// failures are ignored: losing usage statistics must never break the editor.
/// </summary>
public sealed class DirectorReplayPresetUsage
{
    public const int MaxRecent = 20;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? path;

    public List<string> Favorites { get; set; } = new();

    public List<string> Recent { get; set; } = new();

    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, DateTime> LastUsedUtc { get; set; } = new(StringComparer.Ordinal);

    public string LastCategory { get; set; } = string.Empty;

    public DirectorReplayPresetUsage()
    {
    }

    private DirectorReplayPresetUsage(string path) => this.path = path;

    public static DirectorReplayPresetUsage Load(string path)
    {
        var usage = new DirectorReplayPresetUsage(path);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<DirectorReplayPresetUsage>(File.ReadAllText(path), Options) is { } stored)
            {
                usage.Favorites = stored.Favorites?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new();
                usage.Recent = stored.Recent?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(MaxRecent).ToList() ?? new();
                usage.Counts = new Dictionary<string, int>(stored.Counts ?? new(), StringComparer.Ordinal);
                usage.LastUsedUtc = new Dictionary<string, DateTime>(stored.LastUsedUtc ?? new(), StringComparer.Ordinal);
                usage.LastCategory = stored.LastCategory ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // A damaged file starts over empty.
        }
        return usage;
    }

    public bool IsFavorite(string id) => Favorites.Contains(id);

    public int Count(string id) => Counts.TryGetValue(id, out int count) ? count : 0;

    public void ToggleFavorite(string id)
    {
        if (!Favorites.Remove(id))
        {
            Favorites.Add(id);
        }
        Save();
    }

    public void RecordUse(string id)
    {
        Recent.Remove(id);
        Recent.Insert(0, id);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
        Counts[id] = Count(id) + 1;
        LastUsedUtc[id] = DateTime.UtcNow;
        Save();
    }

    /// <summary>Most used first; ties go to the more recently used.</summary>
    public IEnumerable<string> MostUsed()
        => Counts.Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => LastUsedUtc.TryGetValue(pair.Key, out DateTime last) ? last : DateTime.MinValue)
            .Select(pair => pair.Key);

    public void SetLastCategory(string category)
    {
        if (LastCategory == category)
        {
            return;
        }
        LastCategory = category;
        Save();
    }

    private void Save()
    {
        if (path is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // Usage statistics are a convenience.
        }
    }
}
