using System.Numerics;
using ImGuiNET;

namespace VSDirector.Gui;

internal static class DirectorTimelineCursor
{
    internal const uint Destination = 0xff55d9ff;
    internal const uint Original = 0x88000000;

    internal static void DragLines(ImDrawListPtr draw, Vector2 min, Vector2 max, float target, float original, float scale)
    {
        if (max.X <= min.X || max.Y <= min.Y) return;
        draw.PushClipRect(min, max, true);
        if (float.IsFinite(original)) draw.AddLine(new(original, min.Y), new(original, max.Y), Original, 2 * scale);
        if (float.IsFinite(target)) draw.AddLine(new(target, min.Y), new(target, max.Y), Destination, 2 * scale);
        draw.PopClipRect();
    }

    // Draw on the timeline's own window, so labels cannot cover floating editors.
    internal static void Label(ImDrawListPtr draw, Vector2 min, Vector2 max, float x, string text, uint color, float scale)
    {
        if (!float.IsFinite(x) || x < min.X || x > max.X || max.X <= min.X || max.Y <= min.Y || text.Length == 0) return;
        Vector2 padding = new(6 * scale, 3 * scale);
        Vector2 size = Vector2.Min(ImGui.CalcTextSize(text) + padding * 2, max - min);
        float left = x;
        if (left + size.X > max.X) left = x - size.X;
        Vector2 pos = new(Math.Clamp(left, min.X, max.X - size.X), max.Y - size.Y);
        draw.PushClipRect(pos, pos + size, true);
        draw.AddRectFilled(pos, pos + size, color);
        draw.AddText(pos + padding, 0xff181818, text);
        draw.PopClipRect();
    }
}
