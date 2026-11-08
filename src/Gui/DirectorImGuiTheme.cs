using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;

namespace VSDirector.Gui;

/// <summary>Editor styling is scoped to Director; VSImGui and other mods keep their own theme.</summary>
internal static class DirectorImGuiTheme
{
    internal const float FontSize = 20;
    internal static float Scale => (float)Math.Max(.001, GuiElement.scaled(1));
    internal const uint Background = 0xFF282020;
    internal const uint Panel = 0xFF302828;
    internal const uint Border = 0xFF483C3C;
    internal const uint Text = 0xFFFFFFFF;
    internal const uint Muted = 0xFFAAAAAA;
    internal const uint Accent = 0xFFFB8800;
    internal const uint Selected = 0xFF784820;

    internal static Scope Use()
    {
        float s = Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(5, 4) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0, 2) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 3 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 2 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 2 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 2 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, .45f);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(.5f));

        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, 0xFF382828);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Separator, Border);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, Accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, 0xFF1B1515);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, 0xFF261D1D);
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Background);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0xAA3F3838);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0xCC4B4343);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0xFF322C2C);
        ImGui.PushStyleColor(ImGuiCol.Button, 0xFF524444);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFFF37C00);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xFFA25300);
        ImGui.PushStyleColor(ImGuiCol.Header, 0x80BB5F00);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0xBBDD7200);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0xFF954C00);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, 0xFFFFAB45);
        ImGui.PushStyleColor(ImGuiCol.Tab, Panel);
        ImGui.PushStyleColor(ImGuiCol.TabActive, 0xFF584040);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, 0xFF685050);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocused, Panel);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, 0xFF483838);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Background);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, 0xFF605454);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, 0xFF807070);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, 0x887F4600);
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, 0x88000000);
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Accent);
        return new Scope(20, 36);
    }

    internal static Scope Button(string tone = "normal", bool quiet = false)
    {
        bool selected = tone is "primary" or "active" or "selected";
        uint normal = tone == "danger" ? 0xFF3E3C99u : selected ? 0xFFCB6800u : quiet ? 0u : 0xFF524444u;
        uint hovered = tone == "danger" ? 0xFF5050BBu : selected ? 0xFFF37C00u : 0xFF786050u;
        uint active = tone == "danger" ? 0xFF303078u : 0xFFA25300u;
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        return new Scope(0, 3);
    }

    internal static bool CollapsingHeader(string title, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 7) * Scale);
        ImGui.PushStyleColor(ImGuiCol.Header, Panel);
        try { return ImGui.CollapsingHeader(title, flags); }
        finally { ImGui.PopStyleColor(); ImGui.PopStyleVar(); }
    }

    internal static Scope Toolbar()
    {
        float s = Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 2) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4) * s);
        return new Scope(2, 0);
    }

    internal readonly struct Scope(int variables, int colors) : IDisposable
    {
        public void Dispose()
        {
            if (colors > 0) ImGui.PopStyleColor(colors);
            if (variables > 0) ImGui.PopStyleVar(variables);
        }
    }
}
