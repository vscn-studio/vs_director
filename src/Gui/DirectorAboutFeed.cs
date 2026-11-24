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

    internal static async Task<string[]> FetchContributors(CancellationToken token)
    {
        var names = new List<string>();
        for (int page = 1; ; page++)
        {
            using var json = JsonDocument.Parse(await Http.GetByteArrayAsync(
                $"https://api.github.com/repos/vscn-studio/vs_director/contributors?per_page=100&page={page}", token).ConfigureAwait(false));
            foreach (var account in json.RootElement.EnumerateArray())
                if (account.TryGetProperty("login", out var login) && login.GetString() is { Length: > 0 } name)
                    names.Add(name);
            if (json.RootElement.GetArrayLength() < 100) return names.ToArray();
        }
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
            string Field(string key) => entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!.Trim() : "";
            string title = Field("title");
            if (title.Length == 0 || !Uri.TryCreate(Field("url"), UriKind.Absolute, out var url)
                || url.Scheme != "https") continue;
            string path = url.AbsolutePath.Trim('/');
            bool bili = url.Host is "www.bilibili.com" or "bilibili.com";
            string id = bili && path.StartsWith("video/", StringComparison.Ordinal)
                ? path[6..] : url.Host == "youtu.be" ? path
                : url.Host is "www.youtube.com" or "youtube.com" && path == "watch"
                    ? System.Web.HttpUtility.ParseQueryString(url.Query)["v"] ?? "" : "";
            if (!Regex.IsMatch(id, bili ? @"^BV[a-zA-Z0-9]{10}$" : @"^[a-zA-Z0-9_-]{11}$") || !seen.Add((bili, id))) continue;
            videos.Add(new(title, Field("author"), bili ? "https://www.bilibili.com/video/" + id : "https://youtu.be/" + id, id, bili));
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
