using System.Numerics;
using ImGuiNET;

namespace VSDirector.Gui;

/// <summary>Logical initial sizes with physical limits from the current game viewport.</summary>
internal static class DirectorImGuiLayout
{
    internal static Vector2 MaximumSize => Vector2.Max(new Vector2(1), ImGui.GetMainViewport().WorkSize - new Vector2(16));

    internal static void WindowSize(Vector2 preferred, Vector2 minimum)
    {
        ImGui.SetNextWindowSize(Vector2.Min(preferred * DirectorImGuiTheme.Scale, MaximumSize), ImGuiCond.FirstUseEver);
        WindowConstraints(minimum);
    }

    internal static void WindowConstraints(Vector2 minimum)
        => ImGui.SetNextWindowSizeConstraints(Vector2.Min(minimum * DirectorImGuiTheme.Scale, MaximumSize), MaximumSize);

    internal static void AutoHeight(float maximumHeight = 640, float width = 0)
    {
        float physicalWidth = width > 0 ? Math.Min(width * DirectorImGuiTheme.Scale, MaximumSize.X) : -1;
        ImGui.SetNextWindowSizeConstraints(new Vector2(physicalWidth, 0),
            new Vector2(physicalWidth, Math.Min(maximumHeight * DirectorImGuiTheme.Scale, MaximumSize.Y)));
    }

    internal static float ButtonRowsHeight(IEnumerable<string> labels)
    {
        float available = ImGui.GetContentRegionAvail().X;
        float used = 0;
        int rows = 1;
        foreach (string label in labels)
        {
            float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            if (used > 0 && used + width > available) { rows++; used = 0; }
            used += width + ImGui.GetStyle().ItemSpacing.X;
        }
        return rows * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2;
    }
}
