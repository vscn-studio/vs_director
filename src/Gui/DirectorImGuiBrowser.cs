using System.Numerics;
using ImGuiNET;

namespace VSDirector.Gui;

/// <summary>Shared spacing and navigation for floating two-pane browsers.</summary>
internal static class DirectorImGuiBrowser
{
    internal static DirectorImGuiTheme.Scope Use()
    {
        float s = DirectorImGuiTheme.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 4) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0, 3) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6) * s);
        return new(4, 0);
    }

    internal static bool Narrow => ImGui.GetContentRegionAvail().X < 500 * DirectorImGuiTheme.Scale;
    internal static void SettingsWindow()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f));
        DirectorImGuiLayout.WindowSize(new Vector2(760, 560), new Vector2(360, 320));
    }

    internal static void Categories<T>(T[] pages, T current, Func<T, string> label, Func<T, string> icon, Action<T> select) where T : notnull
    {
        if (Narrow)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##settings-category", label(current)))
            {
                foreach (var page in pages)
                    if (ImGui.Selectable(label(page), EqualityComparer<T>.Default.Equals(page, current))) select(page);
                ImGui.EndCombo();
            }
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.ChildBg, DirectorImGuiTheme.Panel);
        ImGui.BeginChild("settings-categories", new Vector2(SidebarWidth, 0));
        ImGui.PopStyleColor();
        foreach (var page in pages)
            if (NavigationItem(page.ToString()!, label(page), icon(page), EqualityComparer<T>.Default.Equals(page, current))) select(page);
        ImGui.EndChild();
        ImGui.SameLine();
    }

    internal static float SidebarWidth => Math.Clamp(ImGui.GetContentRegionAvail().X * .24f,
        144 * DirectorImGuiTheme.Scale, 184 * DirectorImGuiTheme.Scale);

    internal static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        int length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length] + "…").X > width) length--;
        return text[..length] + "…";
    }

    internal static unsafe void CenterIcon(string icon, Vector2 min, Vector2 max, uint color)
    {
        string text = DirectorImGuiIcons.Get(icon);
        if (text.Length == 0) return;
        var font = ImGui.GetFont();
        var glyph = font.FindGlyphNoFallback(text[0]);
        if (glyph.NativePtr == null) return;
        float ratio = ImGui.GetFontSize() / font.FontSize;
        var inkCenter = new Vector2((glyph.X0 + glyph.X1) * .5f, (glyph.Y0 + glyph.Y1) * .5f) * ratio;
        ImGui.GetWindowDrawList().AddText((min + max) * .5f - inkCenter, color, text);
    }

    internal static bool IconButton(string id, string icon, float side = 0)
    {
        bool clicked = ImGui.Button("##" + id, new Vector2(side > 0 ? side : ImGui.GetFrameHeight()));
        CenterIcon(icon, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.Text));
        return clicked;
    }

    internal static bool NavigationItem(string id, string label, string icon, bool selected, string count = "", float width = 0)
    {
        float s = DirectorImGuiTheme.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? ImGui.GetColorU32(ImGuiCol.Header) : 0u);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.HeaderHovered));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.HeaderActive));
        bool clicked = ImGui.Button("##" + id,
            new Vector2(width > 0 ? width : ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()));
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        float x = min.X + 8 * s;
        float y = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * .5f;
        if (selected) draw.AddRectFilled(min, new Vector2(min.X + 2 * s, max.Y), DirectorImGuiTheme.Accent);
        if (icon.Length > 0)
        {
            CenterIcon(icon, new Vector2(x, min.Y), new Vector2(x + ImGui.GetTextLineHeight(), max.Y),
                selected ? DirectorImGuiTheme.Text : DirectorImGuiTheme.Muted);
            x += ImGui.GetTextLineHeight() + 8 * s;
        }
        float countWidth = count.Length == 0 ? 0 : ImGui.CalcTextSize(count).X + 12 * s;
        draw.AddText(new Vector2(x, y), DirectorImGuiTheme.Text, Fit(label, max.X - x - countWidth - 8 * s));
        if (count.Length > 0) draw.AddText(new Vector2(max.X - countWidth + 4 * s, y), DirectorImGuiTheme.Muted, count);
        return clicked;
    }
}
