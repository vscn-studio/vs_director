using System.Runtime.InteropServices;
using ImGuiNET;

namespace VSDirector.Gui;

/// <summary>Glyphs from the bundled MIT-licensed Tabler 3.34.1 font; no network assets.</summary>
internal static class DirectorImGuiIcons
{
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["add"] = "\ueb0b",
        ["adjustments"] = "\uea03",
        ["arrow-back-up"] = "\ueb77",
        ["arrow-forward-up"] = "\ueb78",
        ["arrow-left"] = "\uea19",
        ["arrows-maximize"] = "\uea28",
        ["arrows-move"] = "\uf22f",
        ["bookmark"] = "\uea3a",
        ["bookmark-filled"] = "\ufa88",
        ["bulb"] = "\uea51",
        ["camera"] = "\uea54",
        ["camera-rotate"] = "\uee22",
        ["caret-down-filled"] = "\ufb2a",
        ["check"] = "\uea5e",
        ["chevron-down"] = "\uea5f",
        ["chevron-left"] = "\uea60",
        ["chevron-right"] = "\uea61",
        ["chevron-up"] = "\uea62",
        ["circle-dot"] = "\uefb1",
        ["circle-filled"] = "\uf671",
        ["circle-plus"] = "\uea69",
        ["clipboard"] = "\uea6f",
        ["clock"] = "\uea70",
        ["clock-filled"] = "\uf73a",
        ["close"] = "\ueb55",
        ["cloud"] = "\uea76",
        ["color-swatch"] = "\ueb61",
        ["copy"] = "\uea7a",
        ["cube"] = "\ufa97",
        ["delete"] = "\ueb41",
        ["device-floppy"] = "\ueb62",
        ["download"] = "\uea96",
        ["duplicate"] = "\uea7a",
        ["edit"] = "\ueb04",
        ["export"] = "\uede9",
        ["eye"] = "\uea9a",
        ["eye-filled"] = "\uf679",
        ["eye-off"] = "\uecf0",
        ["file-export"] = "\uede9",
        ["focus-2"] = "\uebd3",
        ["folder"] = "\ueaad",
        ["folder-open"] = "\ufaf7",
        ["gauge"] = "\ueab1",
        ["grip-horizontal"] = "\uec00",
        ["grip-vertical"] = "\uec01",
        ["hourglass"] = "\uef93",
        ["hourglass-high"] = "\uf092",
        ["hourglass-low"] = "\uf093",
        ["hud"] = "\uea9a",
        ["import"] = "\uea96",
        ["keyframe"] = "\uf576",
        ["list-details"] = "\uef40",
        ["lock"] = "\ueae2",
        ["lock-open"] = "\ueae1",
        ["maximize"] = "\uf7a0",
        ["menu-2"] = "\uec42",
        ["microphone"] = "\ueaf0",
        ["minus"] = "\ueaf2",
        ["moon"] = "\ueaf8",
        ["music"] = "\ueafc",
        ["navigation"] = "\uf22f",
        ["palette"] = "\ueb01",
        ["pause"] = "\ued45",
        ["pencil"] = "\ueb04",
        ["photo"] = "\ueb0a",
        ["play"] = "\ued46",
        ["player-pause"] = "\ued45",
        ["player-play"] = "\ued46",
        ["player-play-filled"] = "\uf691",
        ["player-record"] = "\ued47",
        ["player-skip-back"] = "\ued48",
        ["player-skip-forward"] = "\ued49",
        ["player-stop"] = "\ued4a",
        ["player-track-next"] = "\ued4b",
        ["player-track-prev"] = "\ued4c",
        ["plus"] = "\ueb0b",
        ["point"] = "\ueb0c",
        ["pointer"] = "\uf265",
        ["record"] = "\ued47",
        ["redo"] = "\ueb78",
        ["refresh"] = "\ueb13",
        ["replay"] = "\ued22",
        ["reverse"] = "\uea60",
        ["rotate"] = "\ueb16",
        ["rotate-360"] = "\uef85",
        ["route"] = "\ueb17",
        ["save"] = "\ueb62",
        ["scene"] = "\ufa97",
        ["scissors"] = "\ueb1b",
        ["search"] = "\ufdaa",
        ["settings"] = "\ueb20",
        ["shoe"] = "\uefd2",
        ["skip-back"] = "\ued48",
        ["skip-forward"] = "\ued49",
        ["snowflake"] = "\uec0b",
        ["snowflake-off"] = "\uf1a8",
        ["square-rotated-filled"] = "\uf6a4",
        ["stop"] = "\ued4a",
        ["sun"] = "\ueb30",
        ["temperature"] = "\ueb38",
        ["track-next"] = "\ued4b",
        ["track-prev"] = "\ued4c",
        ["trash"] = "\ueb41",
        ["undo"] = "\ueb77",
        ["upload"] = "\ueb47",
        ["video"] = "\ued22",
        ["volume"] = "\ueb51",
        ["walk"] = "\uec87",
        ["wind"] = "\uec34",
        ["world"] = "\ueb54",
        ["x"] = "\ueb55",
        ["zoom-in"] = "\ueb56",
        ["zoom-out"] = "\ueb57",
    };

    internal static string Get(string name) => Glyphs.GetValueOrDefault(name ?? string.Empty, string.Empty);

    // cimgui packs Colored:1, Visible:1 and Codepoint:30 into ONE uint.
    // VSImGui 1.2.7's ImGui.NET incorrectly exposes three separate uints;
    // writing its X1 actually overwrites native U0 and corrupts the atlas UVs.
    // Keep this ABI adapter local and only write the two horizontal bounds.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeGlyph
    {
        [FieldOffset(0)] public uint FlagsAndCodepoint;
        [FieldOffset(4)] public float AdvanceX;
        [FieldOffset(8)] public float X0;
        [FieldOffset(16)] public float X1;
        [FieldOffset(24)] public float U0;
        [FieldOffset(28)] public float V0;
        [FieldOffset(32)] public float U1;
        [FieldOffset(36)] public float V1;
    }

    internal static unsafe void CenterGlyphs(ImFontPtr font)
    {
        // Glyph quads include asymmetric transparent padding from rasterization.
        // Center the visible atlas pixels, keeping the texture coordinates intact.
        ImGui.GetIO().Fonts.GetTexDataAsAlpha8(out nint pixels, out int width, out int height);
        if (pixels == 0 || width <= 0 || height <= 0) return;
        foreach (char code in Glyphs.Values.Select(value => value[0]).Distinct())
        {
            var glyph = (NativeGlyph*)font.FindGlyphNoFallback(code).NativePtr;
            // Never change a fallback glyph or write through an unknown layout.
            if (glyph == null || (glyph->FlagsAndCodepoint >> 2) != code) continue;
            int x0 = (int)MathF.Round(glyph->U0 * width), x1 = (int)MathF.Round(glyph->U1 * width);
            int y0 = (int)MathF.Round(glyph->V0 * height), y1 = (int)MathF.Round(glyph->V1 * height);
            if (x0 < 0 || x1 > width || y0 < 0 || y1 > height || x1 <= x0 || y1 <= y0) continue;
            int left = x1, right = x0 - 1;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                // Ignore almost transparent antialiasing fringes.
                if (((byte*)pixels)[y * width + x] < 32) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
            if (right < left) continue;
            float pixelScale = (glyph->X1 - glyph->X0) / (x1 - x0);
            float inkCenter = glyph->X0 + (left + right + 1 - 2 * x0) * .5f * pixelScale;
            float offset = glyph->AdvanceX * .5f - inkCenter;
            glyph->X0 += offset;
            glyph->X1 += offset;
        }
    }

    // ImGui retains the range pointer until its atlas is built. The runtime owns its lifetime.
    internal static nint CreateRanges()
    {
        short[] ranges = Glyphs.Values.Select(value => (ushort)value[0]).Distinct().Order()
            .SelectMany(code => new[] { unchecked((short)code), unchecked((short)code) }).Append((short)0).ToArray();
        nint pointer = Marshal.AllocHGlobal(ranges.Length * sizeof(short));
        Marshal.Copy(ranges, 0, pointer, ranges.Length);
        return pointer;
    }
}
