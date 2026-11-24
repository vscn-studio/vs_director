using System.Numerics;
using ImGuiNET;

namespace VSDirector.Gui;

internal static class DirectorImGuiTooltip
{
    internal static void Begin()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, 0xF02B2B2Bu);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 7) * DirectorImGuiTheme.Scale);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
    }

    internal static void End()
    {
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor();
    }

    internal static void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Begin();
        ImGui.TextUnformatted(text);
        End();
    }
}
