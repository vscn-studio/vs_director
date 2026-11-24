using System.Numerics;
using Video = VSDirector.Gui.DirectorAboutFeed.Video;
using VSImGui.API;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace VSDirector.Gui;

internal sealed class DirectorAboutDialog(ICoreClientAPI api)
    : DirectorImGuiDialog(api, Lang.Get("vsdirector:workspace-about") + " · VS Director", "about", false)
{
    private Video[] videos = DirectorAboutFeed.BundledVideos();
    private CancellationTokenSource? requests;
    private Task<Video[]?>? catalog;
    private Task<string?>? release;
    private Task<string[]?>? contributorFeed;
    private string[]? contributors;
    private static readonly Lazy<string> CopyrightNotice = new(() => ReadNotice("COPYRIGHT.txt"));
    private static readonly Lazy<string> License = new(() => ReadNotice("LICENSE"));
    private readonly Dictionary<string, Task<byte[]?>> downloads = new();
    private readonly Dictionary<string, LoadedTexture?> covers = new();
    private LoadedTexture? heading;
    private float headingScale;
    private string? publishedVersion;
    internal const string Repository = "https://github.com/vscn-studio/vs_director";
    private static readonly Lazy<DirectorReadme> Chinese = new(() => new("README.md"));
    private bool openPopup;
    internal bool IsVisible => Opened;
    private static readonly Lazy<DirectorReadme> English = new(() => new("README.en.md"));
    private static bool ChineseLocale => Lang.CurrentLocale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
    private readonly string? version = api.ModLoader.GetMod("vsdirector")?.Info.Version;

    protected override bool OnOpen()
    {
        openPopup = true;
        requests = new();
        catalog = ReadRemote(DirectorAboutFeed.FetchVideos, requests.Token);
        release = ReadRemote(DirectorAboutFeed.FetchVersion, requests.Token);
        contributorFeed = ReadRemote(DirectorAboutFeed.FetchContributors, requests.Token);
        DirectorToolWindows.Claim(this, () => Close());
        return true;
    }

    protected override bool OnClose()
    {
        requests?.Cancel();
        requests?.Dispose();
        requests = null;
        catalog = null;
        release = null;
        contributorFeed = null;
        downloads.Clear();
        foreach (var texture in covers.Values) texture?.Dispose();
        covers.Clear();
        heading?.Dispose();
        heading = null;
        DirectorToolWindows.Release(this);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Opened) Close();
        base.Dispose(disposing);
    }

    private async Task<T?> ReadRemote<T>(System.Func<CancellationToken, Task<T>> fetch, CancellationToken token) where T : class?
    {
        try { return await fetch(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return default; }
        catch (Exception e) { ClientApi.Logger?.Debug("VS Director: About feed: {0}", e.Message); return default; }
    }

    protected override CallbackGUIStatus Draw(float deltaSeconds)
    {
        string id = WindowTitle + "###vsdirector-about";
        if (!Opened && !ImGui.IsPopupOpen(id)) return DrawStatus();
        using var theme = DirectorImGuiRuntime.UseFont(dialog: false);
        if (Opened && openPopup)
        {
            ImGui.OpenPopup(id);
            openPopup = false;
        }
        var viewport = ImGui.GetMainViewport();
        float scale = DirectorImGuiTheme.Scale;
        Vector2 size = Vector2.Min(viewport.Size * new Vector2(.95f, .82f), new Vector2(1720, 800) * scale);
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(.5f));
        ImGui.SetNextWindowSize(size);
        bool open = Opened;
        if (ImGui.BeginPopupModal(id, ref open, Flags | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoBackground))
        {
            try
            {
                var draw = ImGui.GetWindowDrawList();
                draw.PushClipRect(viewport.Pos, viewport.Pos + viewport.Size, false);
                draw.AddRectFilled(viewport.Pos, viewport.Pos + viewport.Size, 0xbd201d1b);
                draw.PopClipRect();
                open = open && OnDraw();
                if (!open) ImGui.CloseCurrentPopup();
            }
            finally { ImGui.EndPopup(); }
        }
        if (!open && Opened) Close();
        return DrawStatus();
    }

    protected override unsafe bool OnDraw()
    {
        float scale = DirectorImGuiTheme.Scale;
        if (catalog is { IsCompletedSuccessfully: true })
        {
            videos = catalog.Result ?? videos;
            catalog = null;
        }
        if (release is { IsCompletedSuccessfully: true })
        {
            publishedVersion = release.Result ?? publishedVersion;
            release = null;
        }
        float gap = Math.Min(24 * scale, ImGui.GetContentRegionAvail().X * .022f);
        float width = Math.Max(3, ImGui.GetContentRegionAvail().X - gap * 2);
        float center = Math.Min(740 * scale, width * .54f);
        float side = (width - center) * .5f;
        DrawShelf(true, side);
        ImGui.SameLine(0, gap);
        uint dock = ImGui.GetID("about-documents");
        ImGuiWindowClass documents = new() { ClassId = dock };
        ImGui.DockSpace(dock, new(center, ImGui.GetContentRegionAvail().Y),
            ImGuiDockNodeFlags.NoSplit | ImGuiDockNodeFlags.NoResize, &documents);
        ImGui.SameLine(0, gap);
        DrawShelf(false, side);
        bool keepOpen = true;
        DrawDocument("workspace-about", DrawAbout, &documents, ref keepOpen, ImGui.IsWindowAppearing());
        DrawDocument("about-contributors", DrawContributors, &documents, ref keepOpen);
        DrawDocument("about-copyright", DrawCopyright, &documents, ref keepOpen);
        return keepOpen;
    }

    private static void CenterText(string text)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X) * .5f));
        ImGui.TextUnformatted(text);
    }

    private static string ReadNotice(string file)
    {
        using var stream = typeof(DirectorAboutDialog).Assembly.GetManifestResourceStream("VSDirector.About." + file)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private unsafe void DrawDocument(string key, Action content, ImGuiWindowClass* documents, ref bool keepOpen,
        bool focus = false)
    {
        ImGui.SetNextWindowClass(documents);
        ImGui.SetNextWindowDockID(documents->ClassId, ImGuiCond.Always);
        if (focus) ImGui.SetNextWindowFocus();
        float scale = DirectorImGuiTheme.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10) * scale);
        bool visible = ImGui.Begin(Lang.Get("vsdirector:" + key) + "###vsdirector-document-" + key, ref keepOpen,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.PopStyleVar();
        try
        {
            if (visible)
            {
                DrawDocumentBody(content);
            }
        }
        finally { ImGui.End(); }
    }

    private void DrawDocumentBody(Action content)
    {
        var style = ImGui.GetStyle();
        float buttonWidth = ProjectLinks.Max(link => ImGui.CalcTextSize(link.Label).X) + 2 * style.FramePadding.X;
        int columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + style.ItemSpacing.X)
            / (buttonWidth + style.ItemSpacing.X)), 1, ProjectLinks.Length);
        int rows = (ProjectLinks.Length + columns - 1) / columns;
        float footer = rows * ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginChild("document-body", new Vector2(0, -footer))) content();
        ImGui.EndChild();

        for (int i = 0; i < ProjectLinks.Length; i++)
        {
            if (i % columns == 0)
            {
                int count = Math.Min(columns, ProjectLinks.Length - i);
                float width = buttonWidth * count + style.ItemSpacing.X * (count - 1);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - width) * .5f));
            }
            else ImGui.SameLine();
            var (label, url) = ProjectLinks[i];
            if (ImGui.Button(label, new Vector2(buttonWidth, 0))) OpenWebLink(url);
        }
    }

    private void DrawAbout()
    {
        float scale = DirectorImGuiTheme.Scale;
        if ((heading is null || headingScale != scale) && ClientApi.Gui.TextTexture is { } textTexture)
        {
            heading?.Dispose();
            using var font = DirectorFont.WhiteMediumText();
            font.Color = [1, 1, 1, 1];
            heading = textTexture.GenTextTexture("VS Director", font);
            headingScale = scale;
        }
        if (heading is { TextureId: > 0 })
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - heading.Width) * .5f));
            ImGui.Image((nint)heading.TextureId, new(heading.Width, heading.Height));
        }
        else CenterText("VS Director");
        if (publishedVersion is { Length: > 0 }) CenterText("ModDB · v" + publishedVersion);
        else if (!string.IsNullOrEmpty(version)) CenterText(Lang.Get("vsdirector:about-installed-version", version));
        ImGui.Separator();
        (ChineseLocale ? Chinese : English).Value.Draw(OpenDocumentLink);
    }

    private void DrawContributors()
    {
        if (contributorFeed is { IsCompletedSuccessfully: true })
        {
            contributors = contributorFeed.Result ?? contributors;
            contributorFeed = null;
        }
        if (contributors is { Length: > 0 })
        {
            foreach (string login in contributors)
                if (ImGui.Selectable(login)) OpenWebLink("https://github.com/" + Uri.EscapeDataString(login));
        }
        else ImGui.TextWrapped(Lang.Get("vsdirector:" + (contributorFeed is null
            ? "about-contributors-unavailable" : "about-contributors-loading")));
    }

    private void DrawCopyright()
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(CopyrightNotice.Value);
        ImGui.Separator();
        ImGui.TextUnformatted(License.Value);
        ImGui.PopTextWrapPos();
        if (ImGui.Button(Lang.Get("vsdirector:about-third-party"))) OpenDocumentLink("licenses/THIRD-PARTY.txt");
        ImGui.SameLine();
        if (ImGui.Button("OBS")) OpenDocumentLink("licenses/OBS-THIRD-PARTY.txt");
    }

    private static readonly (string Label, string Url)[] ProjectLinks =
    [
        ("GitHub", Repository),
        ("Discord", "https://discord.com/channels/302152934249070593/1529965521435361420"),
        ("Issues", Repository + "/issues"),
        ("ModDB", "https://mods.vintagestory.at/vsdirector")
    ];

    private void OpenDocumentLink(string target)
    {
        var source = new Uri(Repository + "/blob/HEAD/" + (ChineseLocale ? "README.md" : "README.en.md"));
        if (Uri.TryCreate(source, target, out var url)) OpenWebLink(url.AbsoluteUri);
    }

    private void OpenWebLink(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return;
        try { NetUtil.OpenUrlInBrowser(url.AbsoluteUri); }
        catch (Exception e) { ClientApi.Logger.Warning("VS Director: could not open {0}: {1}", url, e.Message); }
    }

    private void DrawShelf(bool bilibili, float width)
    {
        bool visible = ImGui.BeginChild(bilibili ? "bilibili" : "youtube", new(width, 0));
        try
        {
            if (!visible) return;
            int count = videos.Count(v => v.Bilibili == bilibili);
            if (count == 0) return;
            float gap = 12 * DirectorImGuiTheme.Scale;
            float available = ImGui.GetContentRegionAvail().X;
            int columns = Math.Clamp((int)((available + gap) / (150 * DirectorImGuiTheme.Scale + gap)), 1, count);
            float cardWidth = Math.Min(236 * DirectorImGuiTheme.Scale, (available - (columns - 1) * gap) / columns);
            float gridWidth = cardWidth * columns + gap * (columns - 1);
            if (bilibili) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - gridWidth);
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(gap * .5f, 4 * DirectorImGuiTheme.Scale));
            if (ImGui.BeginTable("videos", columns, ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.SizingStretchSame, new(gridWidth, 0)))
            {
                foreach (var video in videos)
                {
                    if (video.Bilibili != bilibili) continue;
                    ImGui.TableNextColumn();
                    float actualWidth = Math.Min(cardWidth, ImGui.GetContentRegionAvail().X);
                    DrawCard(video, actualWidth);
                }
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawCard(Video video, float width)
    {
        width = Math.Max(1, width);
        float padding = 8 * DirectorImGuiTheme.Scale;
        float textWidth = width;
        float coverHeight = width * 9 / 16;
        float titleHeight = ImGui.CalcTextSize(video.Title, false, textWidth).Y;
        var size = new Vector2(width, coverHeight + titleHeight + ImGui.CalcTextSize(video.Author, false, textWidth).Y + padding * 3);
        var min = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton(video.Url, size)) OpenWebLink(video.Url);
        if (!ImGui.IsItemVisible()) return;
        bool hover = ImGui.IsItemHovered();
        if (hover) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var draw = ImGui.GetWindowDrawList();
        var coverMax = min + new Vector2(width, coverHeight);
        float radius = 6 * DirectorImGuiTheme.Scale;
        int texture = CoverTexture(video);
        if (texture > 0) draw.AddImageRounded((nint)texture, min, coverMax, Vector2.Zero, Vector2.One, 0xffffffff, radius);
        else draw.AddRectFilled(min, coverMax, DirectorImGuiTheme.Panel, radius);
        var title = min + new Vector2(0, coverHeight + padding);
        DrawWrapped(draw, title, hover ? 0xffffc38au : 0xfff3f3f3u, video.TitleBytes, textWidth);
        DrawWrapped(draw, title + new Vector2(0, titleHeight + padding * .5f), 0xffc3c0bd, video.AuthorBytes, textWidth);
    }

    private int CoverTexture(Video video)
        => RemoteTexture(video.Url, t => DirectorAboutFeed.FetchCover(video, t))?.TextureId ?? 0;

    private LoadedTexture? RemoteTexture(string key, System.Func<CancellationToken, Task<byte[]>> fetch)
    {
        if (covers.TryGetValue(key, out var existing)) return existing;
        if (requests is null) return null;
        if (!downloads.TryGetValue(key, out var pending))
        {
            downloads[key] = ReadRemote(fetch, requests.Token);
            return null;
        }
        if (!pending.IsCompletedSuccessfully) return null;
        downloads.Remove(key);
        LoadedTexture? texture = null;
        if (pending.Result is { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                using var bitmap = new BitmapExternal(stream, ClientApi.Logger);
                texture = new LoadedTexture(ClientApi);
                ClientApi.Render.LoadTexture(bitmap, ref texture);
            }
            catch (Exception e)
            {
                texture?.Dispose();
                texture = null;
                ClientApi.Logger?.Debug("VS Director: About thumbnail: {0}", e.Message);
            }
        }
        covers[key] = texture;
        return texture;
    }

    private static unsafe void DrawWrapped(ImDrawListPtr draw, Vector2 at, uint color, byte[] text, float width)
    {
        fixed (byte* first = text)
            ImGuiNative.ImDrawList_AddText_FontPtr(draw.NativePtr, ImGui.GetFont().NativePtr, ImGui.GetFontSize(),
                at, color, first, first + text.Length, width, null);
    }

}
