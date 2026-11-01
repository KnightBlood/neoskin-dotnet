using System.Text;
using System.Text.RegularExpressions;

namespace NeoSkin.Web.Support;

/// <summary>
/// 极简 GFM 子集渲染器（源 <c>MarkdownLite</c> 的移植）。
///
/// <p>覆盖标题、段落、列表、引用、围栏代码、分隔线、强调、行内代码、链接、图片、
/// 删除线与硬换行；未覆盖的语法按纯文本输出。</p>
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"^\s*`{3,}\s*$")]
    private static partial Regex FenceBacktick();

    [GeneratedRegex(@"^\s*~{3,}\s*$")]
    private static partial Regex FenceTilde();

    [GeneratedRegex(@"#{1,6}\s.*")]
    private static partial Regex HeadingWithText();

    [GeneratedRegex(@"#{1,6}")]
    private static partial Regex HeadingOnly();

    [GeneratedRegex(@"(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,}")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^[-*+]\s+(.*)")]
    private static partial Regex UnorderedItem();

    [GeneratedRegex(@"^\d+\.\s+(.*)")]
    private static partial Regex OrderedItem();

    [GeneratedRegex(@"^#{1,6}\s*")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^>\s*")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"!\[([^\]]*)]\(([^)\s]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldStars();

    [GeneratedRegex(@"__(.+)__")]
    private static partial Regex BoldUnderscores();

    [GeneratedRegex(@"(?<!\*)\*([^*]+)\*(?!\*)")]
    private static partial Regex ItalicStars();

    [GeneratedRegex(@"(?<!_)_([^_]+)_(?!_)")]
    private static partial Regex ItalicUnderscores();

    [GeneratedRegex(@"~~(.+)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"(  +|\\)\n")]
    private static partial Regex HardBreak();

    public static string Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return "";
        }

        string normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        bool inFence = false;
        var fenceLines = new List<string>();
        string? listKind = null;
        var listItems = new List<string>();

        foreach (string raw in lines)
        {
            if (inFence)
            {
                if (FenceBacktick().IsMatch(raw) || FenceTilde().IsMatch(raw))
                {
                    html.Append("<pre><code>").Append(Escape(string.Join("\n", fenceLines)))
                        .Append("</code></pre>\n");
                    inFence = false;
                    fenceLines.Clear();
                }
                else
                {
                    fenceLines.Add(raw);
                }

                continue;
            }

            string trimmed = raw.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph(html, paragraph);
                if (listKind is not null)
                {
                    FlushList(html, listItems, listKind);
                    listKind = null;
                }

                inFence = true;
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph(html, paragraph);
                if (listKind is not null)
                {
                    FlushList(html, listItems, listKind);
                    listKind = null;
                }

                continue;
            }

            if (HeadingWithText().IsMatch(trimmed) || HeadingOnly().IsMatch(trimmed))
            {
                FlushParagraph(html, paragraph);
                int level = trimmed.IndexOf(' ') >= 0
                    ? Math.Min(6, trimmed.IndexOf(' '))
                    : Math.Min(6, trimmed.Length);
                html.Append("<h").Append(level).Append('>')
                    .Append(Inline(HeadingPrefix().Replace(trimmed, "")))
                    .Append("</h").Append(level).Append(">\n");
                continue;
            }

            if (HorizontalRule().IsMatch(trimmed))
            {
                FlushParagraph(html, paragraph);
                if (listKind is not null)
                {
                    FlushList(html, listItems, listKind);
                    listKind = null;
                }

                html.Append("<hr />\n");
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(html, paragraph);
                if (listKind is not null)
                {
                    FlushList(html, listItems, listKind);
                    listKind = null;
                }

                html.Append("<blockquote>\n<p>")
                    .Append(Inline(QuotePrefix().Replace(trimmed, "")))
                    .Append("</p>\n</blockquote>\n");
                continue;
            }

            Match ul = UnorderedItem().Match(trimmed);
            Match ol = OrderedItem().Match(trimmed);
            if (ul.Success || ol.Success)
            {
                FlushParagraph(html, paragraph);
                listKind ??= ul.Success ? "ul" : "ol";
                listItems.Add(ul.Success ? ul.Groups[1].Value : ol.Groups[1].Value);
                continue;
            }

            if (listKind is not null)
            {
                listItems[^1] = listItems[^1] + " " + trimmed;
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph(html, paragraph);
        if (listKind is not null)
        {
            FlushList(html, listItems, listKind);
        }

        return html.ToString();
    }

    private static void FlushParagraph(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        html.Append("<p>").Append(Inline(string.Join("\n", paragraph))).Append("</p>\n");
        paragraph.Clear();
    }

    private static void FlushList(StringBuilder html, List<string> items, string kind)
    {
        if (items.Count == 0)
        {
            return;
        }

        html.Append('<').Append(kind).Append(">\n");
        foreach (string item in items)
        {
            html.Append("<li>").Append(Inline(item)).Append("</li>\n");
        }

        html.Append("</").Append(kind).Append(">\n");
        items.Clear();
    }

    private static string Inline(string text)
    {
        string s = Escape(text);
        s = InlineCode().Replace(s, m => "<code>" + m.Groups[1].Value + "</code>");
        s = Image().Replace(s, m => "<img src=\"" + m.Groups[2].Value + "\" alt=\""
                                    + m.Groups[1].Value + "\" />");
        s = Link().Replace(s, m => "<a href=\"" + m.Groups[2].Value + "\">"
                                   + m.Groups[1].Value + "</a>");
        s = BoldStars().Replace(s, "<strong>$1</strong>");
        s = BoldUnderscores().Replace(s, "<strong>$1</strong>");
        s = ItalicStars().Replace(s, "<em>$1</em>");
        s = ItalicUnderscores().Replace(s, "<em>$1</em>");
        s = Strike().Replace(s, "<del>$1</del>");
        s = HardBreak().Replace(s, m =>
            (m.Groups[1].Value.StartsWith('\\') ? "" : "<br />") + "\n");
        return s.Replace("\r", "");
    }

    private static string Escape(string? text)
    {
        if (text is null)
        {
            return "";
        }

        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
