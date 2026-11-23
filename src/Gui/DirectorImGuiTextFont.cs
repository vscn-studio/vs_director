using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VSDirector.Gui;

/// <summary>Supplies missing CJK glyphs without replacing the host's selected font.</summary>
internal static class DirectorImGuiTextFont
{
    // Shared-atlas rebuild handling follows FontSettings' ImGui synchronization
    // workflow (Copyright (c) 2024 神麤詭末, MIT). See licenses/FontSettings-MIT.txt.
    // Keep the host's fonts and caches intact: FontSettings owns font selection
    // when installed, and may clear/recreate the atlas at any time.
    private const string ConfigName = "VS Director CJK fallback";
    internal static string FontPath = "";
    private static nint glyphRanges;
    private static ILogger? logger;
    private static bool warnedMissingFont;

    internal static void Initialize(ICoreClientAPI api)
    {
        logger = api.Logger;
        FontPath = FindSystemFont();
    }

    internal static string FindSystemFont()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts") })
            foreach (string name in new[] { "msyh.ttc", "msyh.ttf", "simhei.ttf", "simsun.ttc",
                "NotoSansSC-Regular.ttf", "NotoSansCJK-Regular.ttc", "SourceHanSansSC-Regular.otf" })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            return "";
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (string path in new[] { "/System/Library/Fonts/PingFang.ttc",
                "/System/Library/Fonts/Hiragino Sans GB.ttc", "/System/Library/Fonts/STHeiti Light.ttc",
                "/Library/Fonts/Arial Unicode.ttf" })
                if (File.Exists(path)) return path;
        }

        // Fontconfig finds distribution-specific paths and user-installed fonts.
        // Verify its charset: with no CJK font installed it returns a Latin font.
        try
        {
            var start = new ProcessStartInfo("fc-match")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--format=%{file}\\t%{charset}\\n");
            start.ArgumentList.Add("sans-serif:lang=zh-cn");
            using var process = Process.Start(start);
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000)) { process.Kill(); return ""; }
            string match = output.GetAwaiter().GetResult().Trim();
            _ = errors.GetAwaiter().GetResult();
            int separator = match.IndexOf('\t');
            if (process.ExitCode != 0 || separator < 0) return "";
            string path = match[..separator];
            var ranges = match[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Covers(char c) => ranges.Any(range =>
            {
                var endpoints = range.Split('-');
                return int.TryParse(endpoints[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int first)
                    && int.TryParse(endpoints[^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int last)
                    && c >= first && c <= last;
            });
            return File.Exists(path) && Covers('中') && Covers('文') && Covers('导') ? path : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return "";
        }
    }

    internal static unsafe bool MergeMissingGlyphs(ImFontAtlasPtr atlas, ImFontPtr font)
    {
        // Test the live config records, not cached font pointers: an atlas clear
        // can reuse the same native addresses for completely different fonts.
        for (int i = 0; i < atlas.ConfigData.Size; i++)
        {
            var existing = atlas.ConfigData[i];
            if (existing.NativePtr->DstFont != font.NativePtr) continue;
            bool matches = true;
            for (int j = 0; j < ConfigName.Length; j++)
                if (existing.Name[j] != (byte)ConfigName[j]) { matches = false; break; }
            if (matches) return false;
        }

        if (!File.Exists(FontPath))
        {
            // A host font (e.g. FontSettings) may already provide Chinese.
            // Missing system fonts must not break atlas uploads or editor icons.
            if (!warnedMissingFont && font.FindGlyphNoFallback('中').NativePtr == null
                && Lang.CurrentLocale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true)
            {
                logger?.Warning("VS Director: no system Chinese font found. Install a CJK font or select one with FontSettings.");
                warnedMissingFont = true;
            }
            return false;
        }

        if (glyphRanges == 0)
        {
            // Request punctuation, arrows and basic CJK for editable names in
            // any locale. Only characters present in the system font are added.
            ushort[] ranges = [0x0100, 0x024F, 0x2000, 0x206F, 0x2100, 0x22FF,
                0x3000, 0x30FF, 0x31F0, 0x31FF, 0x4E00, 0x9FFF, 0xFF00, 0xFFEF, 0];
            glyphRanges = Marshal.AllocHGlobal(ranges.Length * sizeof(ushort));
            ranges.CopyTo(new Span<ushort>((void*)glyphRanges, ranges.Length));
        }

        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        try
        {
            config.MergeMode = true;
            config.NativePtr->DstFont = font.NativePtr;
            config.PixelSnapH = true;
            config.OversampleH = config.OversampleV = 1;
            for (int i = 0; i < ConfigName.Length; i++) config.Name[i] = (byte)ConfigName[i];
            config.Name[ConfigName.Length] = 0;
            // ImGui merge mode leaves all existing glyphs (including any CJK
            // supplied by FontSettings) and their advances unchanged.
            atlas.AddFontFromFileTTF(FontPath, font.FontSize, config, glyphRanges);
            return true;
        }
        finally { config.Destroy(); }
    }

    internal static void Dispose()
    {
        FontPath = "";
        logger = null;
        warnedMissingFont = false;
        if (glyphRanges != 0) Marshal.FreeHGlobal(glyphRanges);
        glyphRanges = 0;
    }
}
