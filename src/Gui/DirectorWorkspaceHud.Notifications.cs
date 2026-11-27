using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace VSDirector.Gui;

internal sealed partial class DirectorWorkspaceHud
{
    private const double WorkspaceNotificationDurationSeconds = 3.2;
    private readonly object workspaceNotificationLock = new();
    private string workspaceNotification = string.Empty;
    private DateTimeOffset workspaceNotificationExpiresAt;

    internal void ShowWorkspaceNotification(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (workspaceNotificationLock)
        {
            workspaceNotification = message.Trim();
            workspaceNotificationExpiresAt = DateTimeOffset.UtcNow.AddSeconds(WorkspaceNotificationDurationSeconds);
        }
    }

    private void DrawWorkspaceNotification()
    {
        string message;
        DateTimeOffset expiresAt;
        lock (workspaceNotificationLock)
        {
            message = workspaceNotification;
            expiresAt = workspaceNotificationExpiresAt;
        }

        double remainingSeconds = (expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (message.Length == 0 || remainingSeconds <= 0) return;

        float fade = (float)Math.Clamp(remainingSeconds / .28, 0, 1);
        float scale = DirectorImGuiTheme.Scale;
        var viewport = ImGui.GetMainViewport();
        float margin = 18 * scale;
        Vector2 padding = new(16 * scale, 7 * scale);
        float maxCardWidth = Math.Max(1, viewport.Size.X - margin * 2);
        float maxTextWidth = Math.Max(1, Math.Min(760 * scale, maxCardWidth - padding.X * 2));
        List<string> lines = WrapNotification(message, maxTextWidth, 2);
        float lineHeight = ImGui.GetTextLineHeight();
        float textWidth = lines.Max(line => ImGui.CalcTextSize(line).X);
        Vector2 size = new(
            Math.Min(maxCardWidth, textWidth + padding.X * 2),
            lines.Count * lineHeight + padding.Y * 2);
        Vector2 center = new(
            viewport.Pos.X + viewport.Size.X * .5f,
            viewport.Pos.Y + (float)VSDirector.Data.DirectorWorkspacePanelLayout.ToolbarHeight * scale * .5f);
        Vector2 min = center - size * .5f;
        Vector2 max = min + size;
        float rounding = size.Y * .5f;
        var draw = ImGui.GetForegroundDrawList();

        draw.AddRectFilled(min, max, Color(new Vector4(.105f, .11f, .12f, .94f), fade), rounding);
        draw.AddRect(min, max, Color(new Vector4(.48f, .49f, .51f, .58f), fade), rounding,
            ImDrawFlags.None, scale);

        float y = center.Y - lines.Count * lineHeight * .5f;
        foreach (string line in lines)
        {
            float x = center.X - ImGui.CalcTextSize(line).X * .5f;
            draw.AddText(new Vector2(x, y), Color(new Vector4(.96f, .96f, .97f, 1f), fade), line);
            y += lineHeight;
        }
    }

    private static List<string> WrapNotification(string message, float maxWidth, int maximumLines)
    {
        List<string> lines = new();
        StringBuilder line = new();
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(message);
        bool truncated = false;

        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (element is "\r") continue;
            if (element is "\n")
            {
                lines.Add(line.ToString());
                line.Clear();
                if (lines.Count == maximumLines)
                {
                    truncated = elements.MoveNext();
                    break;
                }
                continue;
            }

            if (line.Length > 0 && ImGui.CalcTextSize(line.ToString() + element).X > maxWidth)
            {
                lines.Add(line.ToString());
                line.Clear();
                if (lines.Count == maximumLines)
                {
                    truncated = true;
                    break;
                }
            }
            line.Append(element);
        }

        if (line.Length > 0 && lines.Count < maximumLines) lines.Add(line.ToString());
        if (lines.Count == 0) lines.Add(message);
        if (truncated)
        {
            int last = lines.Count - 1;
            string value = lines[last];
            while (value.Length > 0 && ImGui.CalcTextSize(value + "…").X > maxWidth)
                value = value[..^1];
            lines[last] = value + "…";
        }
        return lines;
    }

    private static uint Color(Vector4 color, float opacity)
    {
        color.W *= opacity;
        return ImGui.ColorConvertFloat4ToU32(color);
    }
}
