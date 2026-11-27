using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSDirector.Gui;

internal static class DirectorAboutFeed
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 2 * 1024 * 1024,
        DefaultRequestHeaders = { { "User-Agent", "VSDirector/1.0 (+https://github.com/vscn-studio/vs_director)" } }
    };

    internal sealed record Video(string Title, string Author, string Url, string Id, bool Bilibili)
    {
        internal byte[] TitleBytes { get; } = Encoding.UTF8.GetBytes(Title);
        internal byte[] AuthorBytes { get; } = Encoding.UTF8.GetBytes(Author);
    }

    internal sealed record Sponsor(string Name, string NameEn, string Url);

    internal static Sponsor[] BundledSponsors()
    {
        using var stream = typeof(DirectorAboutFeed).Assembly.GetManifestResourceStream("VSDirector.About.sponsors.json")!;
        using var json = JsonDocument.Parse(stream);
        return ParseSponsors(json.RootElement);
    }

    internal static async Task<Sponsor[]> FetchSponsors(CancellationToken token)
    {
        using var json = JsonDocument.Parse(await Http.GetByteArrayAsync(
            "https://raw.githubusercontent.com/vscn-studio/vs_director/main/about/sponsors.json", token).ConfigureAwait(false));
        return ParseSponsors(json.RootElement);
    }

    private static Sponsor[] ParseSponsors(JsonElement list)
    {
        var sponsors = new List<Sponsor>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string name = Field(entry, "name");
            if (name.Length == 0 || !Uri.TryCreate(Field(entry, "url"), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps) continue;
            string nameEn = Field(entry, "nameEn");
            sponsors.Add(new(name, nameEn.Length == 0 ? name : nameEn, url.AbsoluteUri));
        }
        if (sponsors.Count == 0 && list.GetArrayLength() != 0) throw new InvalidDataException("Invalid About sponsor list");
        return sponsors.ToArray();
    }

    private static string Field(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";

    internal static string[] BundledContributors()
    {
        using var stream = typeof(DirectorAboutFeed).Assembly.GetManifestResourceStream("VSDirector.About.contributors.json")!;
        using var json = JsonDocument.Parse(stream);
        return ParseContributors(json.RootElement);
    }

    internal static async Task<string[]> FetchContributors(CancellationToken token)
    {
        using var json = JsonDocument.Parse(await Http.GetByteArrayAsync(
            "https://cdn.jsdelivr.net/gh/vscn-studio/vs_director@main/about/contributors.json", token).ConfigureAwait(false));
        return ParseContributors(json.RootElement);
    }

    private static string[] ParseContributors(JsonElement list)
    {
        var names = list.EnumerateArray().Select(entry => entry.GetString()?.Trim())
            .OfType<string>().Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) throw new InvalidDataException("Empty About contributor list");
        return names;
    }

    internal static Video[] BundledVideos()
    {
        using var stream = typeof(DirectorAboutFeed).Assembly.GetManifestResourceStream("VSDirector.About.videos.json")!;
        using var json = JsonDocument.Parse(stream);
        return ParseVideos(json.RootElement);
    }

    internal static async Task<Video[]> FetchVideos(CancellationToken token)
    {
        using var json = JsonDocument.Parse(await Http.GetByteArrayAsync(
            "https://raw.githubusercontent.com/vscn-studio/vs_director/main/about/videos.json", token).ConfigureAwait(false));
        return ParseVideos(json.RootElement);
    }

    private static Video[] ParseVideos(JsonElement list)
    {
        var videos = new List<Video>();
        var seen = new HashSet<(bool, string)>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string title = Field(entry, "title");
            if (title.Length == 0 || !Uri.TryCreate(Field(entry, "url"), UriKind.Absolute, out var url)
                || url.Scheme != "https") continue;
            string path = url.AbsolutePath.Trim('/');
            bool bili = url.Host is "www.bilibili.com" or "bilibili.com";
            string id = bili && path.StartsWith("video/", StringComparison.Ordinal)
                ? path[6..] : url.Host == "youtu.be" ? path
                : url.Host is "www.youtube.com" or "youtube.com" && path == "watch"
                    ? System.Web.HttpUtility.ParseQueryString(url.Query)["v"] ?? "" : "";
            if (!Regex.IsMatch(id, bili ? @"^BV[a-zA-Z0-9]{10}$" : @"^[a-zA-Z0-9_-]{11}$") || !seen.Add((bili, id))) continue;
            videos.Add(new(title, Field(entry, "author"), bili ? "https://www.bilibili.com/video/" + id : "https://youtu.be/" + id, id, bili));
        }
        if (videos.Count == 0 && list.GetArrayLength() != 0) throw new InvalidDataException("Invalid About video list");
        return videos.ToArray();
    }

    internal static async Task<string?> FetchVersion(CancellationToken token)
    {
        using var json = JsonDocument.Parse(await Http.GetByteArrayAsync(
            "https://mods.vintagestory.at/api/mod/vsdirector", token).ConfigureAwait(false));
        return json.RootElement.GetProperty("mod").GetProperty("releases").EnumerateArray()
            .OrderByDescending(r => r.GetProperty("releaseid").GetInt64())
            .Select(r => r.GetProperty("modversion").GetString()).FirstOrDefault();
    }

    internal static async Task<byte[]> FetchCover(Video video, CancellationToken token)
    {
        if (!video.Bilibili)
            return await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{video.Id}/mqdefault.jpg", token).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.bilibili.com/x/web-interface/view?bvid=" + video.Id);
        request.Headers.Referrer = new Uri(video.Url);
        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        var uri = new UriBuilder(json.RootElement.GetProperty("data").GetProperty("pic").GetString()!);
        if (!uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected Bilibili thumbnail host");
        uri.Scheme = "https";
        uri.Port = -1;
        return await Http.GetByteArrayAsync(uri.Uri + "@480w_270h_1c.jpg", token).ConfigureAwait(false);
    }
}
