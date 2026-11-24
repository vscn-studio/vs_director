using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VSDirector.Gui;

internal sealed class DirectorReadme
{
    private readonly MarkdownDocument document;
    private readonly Dictionary<ContainerInline, Paragraph> paragraphs = new();

    internal DirectorReadme(string resource)
    {
        using var stream = typeof(DirectorReadme).Assembly.GetManifestResourceStream("VSDirector.About." + resource)
            ?? throw new InvalidDataException(resource);
        using var reader = new StreamReader(stream);
        string markdown = reader.ReadToEnd();
        document = Markdown.Parse(markdown, new MarkdownPipelineBuilder().UsePipeTables().Build());
        // The dialog supplies the title and selects the document from the game locale.
        if (document.FirstOrDefault() is HeadingBlock { Level: 1 } title
            && markdown[title.Span.Start..(title.Span.End + 1)].Trim() == "# VS Director")
        {
            document.RemoveAt(0);
            if (document.FirstOrDefault() is ParagraphBlock languages
                && markdown[languages.Span.Start..(languages.Span.End + 1)].Trim()
                    is "简体中文 | [English](README.en.md)" or "[简体中文](README.md) | English")
                document.RemoveAt(0);
        }
    }

    internal void Draw(Action<string> open)
    {
        foreach (var block in document) DrawBlock(block, open);
    }

    private void DrawBlock(Block block, Action<string> open)
    {
        switch (block)
        {
            case HeadingBlock heading:
                ImGui.Spacing();
                DrawInline(heading.Inline, open, heading.Level switch { 1 => 1.6f, 2 => 1.3f, _ => 1.1f }, true);
                if (heading.Level <= 2) ImGui.Separator();
                break;
            case Table table:
                if (!ImGui.BeginTable("readme-table-" + table.Line, table.ColumnDefinitions.Count,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInner | ImGuiTableFlags.SizingStretchProp)) break;
                for (int i = 0; i < table.ColumnDefinitions.Count; i++)
                    ImGui.TableSetupColumn("##" + i, ImGuiTableColumnFlags.WidthStretch, i == 0 ? 1 : 3);
                foreach (TableRow row in table)
                {
                    ImGui.TableNextRow();
                    foreach (TableCell cell in row)
                    {
                        ImGui.TableNextColumn();
                        foreach (var content in cell)
                            if (content is ParagraphBlock p) DrawInline(p.Inline, open, bold: row.IsHeader);
                            else DrawBlock(content, open);
                    }
                }
                ImGui.EndTable();
                break;
            case ListBlock list:
                int number = 0;
                foreach (ListItemBlock item in list)
                {
                    ImGui.TextUnformatted(list.IsOrdered ? (++number) + "." : "•");
                    ImGui.SameLine();
                    ImGui.BeginGroup();
                    foreach (var content in item) DrawBlock(content, open);
                    ImGui.EndGroup();
                }
                break;
            case CodeBlock code:
                ImGui.PushStyleColor(ImGuiCol.ChildBg, DirectorImGuiTheme.Background);
                ImGui.BeginChild("code-" + code.Line, new(0, ImGui.GetTextLineHeightWithSpacing() * code.Lines.Count
                    + ImGui.GetStyle().ScrollbarSize + 2 * ImGui.GetStyle().WindowPadding.Y), false,
                    ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysUseWindowPadding);
                ImGui.TextUnformatted(code.Lines.ToString());
                ImGui.EndChild();
                ImGui.PopStyleColor();
                break;
            case ParagraphBlock paragraph:
                DrawInline(paragraph.Inline, open);
                break;
        }
    }

    private void DrawInline(ContainerInline? inline, Action<string> open, float scale = 1, bool bold = false)
    {
        if (inline is null) return;
        if (!paragraphs.TryGetValue(inline, out var paragraph))
            paragraphs[inline] = paragraph = new Paragraph(inline, bold);
        paragraph.Draw(open, scale);
    }

    private sealed record Token(string Text, bool Bold, bool Code, string? Link);
    private sealed record Fragment(Token Token, Vector2 Offset, float Width);

    private sealed class Paragraph
    {
        // Keep Western words together and allow a break between CJK characters.
        private static readonly Regex Words = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}]|[^\s\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}]+|\s+");
        private readonly List<Token> tokens = new();
        private readonly List<Fragment> fragments = new();
        private float lastWidth, lastSize, height;
        private nint lastFont;

        internal Paragraph(ContainerInline inline, bool bold) => Collect(inline, bold, null);

        private void Collect(ContainerInline container, bool bold, string? link)
        {
            foreach (Inline item in container)
            {
                if (item is ContainerInline nested)
                {
                    Collect(nested, bold || nested is EmphasisInline { DelimiterCount: >= 2 },
                        nested is LinkInline target ? target.Url : link);
                    continue;
                }
                string text = item switch { LiteralInline literal => literal.Content.ToString(),
                    CodeInline code => code.Content, LineBreakInline => " ", _ => "" };
                foreach (Match match in Words.Matches(text)) tokens.Add(new(match.Value, bold, item is CodeInline, link));
            }
        }

        internal unsafe void Draw(Action<string> open, float scale)
        {
            float width = Math.Max(1, ImGui.GetContentRegionAvail().X);
            float size = ImGui.GetFontSize() * scale;
            var font = ImGui.GetFont();
            if (width != lastWidth || size != lastSize || lastFont != (nint)font.NativePtr)
            {
                fragments.Clear();
                float x = 0, y = 0;
                foreach (var token in tokens)
                {
                    float w = ImGui.CalcTextSize(token.Text).X * scale;
                    if (x > 0 && x + w > width) { x = 0; y += size + 2; }
                    if (x == 0 && string.IsNullOrWhiteSpace(token.Text)) continue;
                    if (w > width && token.Text.Length > 1)
                    {
                        foreach (var rune in token.Text.EnumerateRunes())
                        {
                            var part = rune.ToString();
                            float advance = ImGui.CalcTextSize(part).X * scale;
                            if (x > 0 && x + advance > width) { x = 0; y += size + 2; }
                            fragments.Add(new(token with { Text = part }, new(x, y), advance));
                            x += advance;
                        }
                    }
                    else { fragments.Add(new(token, new(x, y), w)); x += w; }
                }
                height = y + size + 2;
                lastWidth = width; lastSize = size; lastFont = (nint)font.NativePtr;
            }
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new(width, height));
            if (!ImGui.IsItemVisible()) return;
            var draw = ImGui.GetWindowDrawList();
            bool hovered = ImGui.IsItemHovered();
            foreach (var fragment in fragments)
            {
                var run = fragment.Token;
                var pos = origin + fragment.Offset;
                var end = pos + new Vector2(fragment.Width, size);
                if (run.Code) draw.AddRectFilled(pos, end, DirectorImGuiTheme.Background);
                uint color = run.Link is not null ? 0xffe5b872 : DirectorImGuiTheme.Text;
                draw.AddText(font, size, pos, color, run.Text);
                if (run.Bold) draw.AddText(font, size, pos + new Vector2(size / 22, 0), color, run.Text);
                if (run.Link is null) continue;
                draw.AddLine(new(pos.X, end.Y), end, color);
                if (!hovered || !ImGui.IsMouseHoveringRect(pos, end)) continue;
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) open(run.Link);
            }
        }
    }
}
