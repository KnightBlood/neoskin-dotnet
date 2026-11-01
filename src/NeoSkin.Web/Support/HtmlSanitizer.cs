using System.Text;
using System.Text.RegularExpressions;
using NeoSkin.Web.View;

namespace NeoSkin.Web.Support;

/// <summary>
/// 公告/站内信 HTML 消毒（对应 Java <c>CommunicationRenderer.sanitize</c>）。
///
/// <para>白名单标签/属性、id 前缀重写、class/style 过滤、链接/图片属性补充，
/// 以及 <c>data-target</c> / <c>aria-controls</c> / <c>href</c> 的 id 引用同步。
/// 使用项目自研 HTML 解析器，不依赖反射。</para>
/// </summary>
public static partial class HtmlSanitizer
{
    private static readonly HashSet<string> KeepTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "span", "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol",
        "li", "strong", "b", "em", "i", "u", "s", "del", "small", "blockquote", "pre",
        "code", "a", "button", "table", "thead", "tbody", "tr", "th", "td", "details",
        "summary", "img",
    };

    private static readonly HashSet<string> RemoveTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "svg", "math", "form", "input",
        "textarea", "select", "meta", "link", "base",
    };

    [GeneratedRegex(@"^[A-Za-z][\w-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?:https?://|mailto:|/(?!/)|#[A-Za-z][\w-]*$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"^(?:https://|/(?!/))", RegexOptions.IgnoreCase)]
    private static partial Regex SrcPattern();

    [GeneratedRegex(@"^#[A-Za-z][\w-]*$")]
    private static partial Regex DataTargetPattern();

    [GeneratedRegex(@"^[A-Za-z][\w-]*$")]
    private static partial Regex AriaControlsPattern();

    [GeneratedRegex(@"^(?:card(?:-[\w-]+)?|alert(?:-[\w-]+)?|bg-[\w-]+|text-[\w-]+"
                    + @"|font-weight-[\w-]+|btn(?:-[\w-]+)?|fa[srlbd]?|fa-[\w-]+"
                    + @"|collapse|collapsed|show|table(?:-[\w-]+)?|[mp][trblxy]?-[0-5]"
                    + @"|rounded|border(?:-[\w-]+)?)$")]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"^(?:bold|normal|[1-9]00)$")]
    private static partial Regex FontWeightPattern();

    [GeneratedRegex(@"^[0-9 px.]{1,30}$")]
    private static partial Regex PaddingPattern();

    public static string Sanitize(string? html, string prefix)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        TemplateDocument document = TemplateParser.Parse("safe-root",
            "<div id=\"safe-root\">" + html + "</div>");
        var root = document.Children.OfType<TemplateElement>().FirstOrDefault();
        if (root is null)
        {
            return "";
        }

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int[] index = [0];
        CleanChildren(root, idMap, index, prefix);
        RewriteReferences(root, idMap);

        var output = new StringBuilder();
        foreach (TemplateNode child in root.Children)
        {
            Serialize(child, output);
        }

        return output.ToString();
    }

    private static void CleanChildren(TemplateElement parent,
        Dictionary<string, string> idMap, int[] index, string prefix)
    {
        foreach (TemplateNode node in parent.Children.ToList())
        {
            if (node is TemplateComment)
            {
                parent.Children.Remove(node);
                continue;
            }

            if (node is not TemplateElement element)
            {
                continue;
            }

            string tag = element.Name.ToLowerInvariant();
            if (RemoveTags.Contains(tag))
            {
                parent.Children.Remove(element);
                continue;
            }

            CleanChildren(element, idMap, index, prefix);

            if (!KeepTags.Contains(tag))
            {
                int position = parent.Children.IndexOf(element);
                parent.Children.RemoveAt(position);
                parent.Children.InsertRange(position, element.Children);
                continue;
            }

            FilterAttributes(element, tag);

            string? id = attributeValue(element, "id");
            if (id is not null)
            {
                string fresh = prefix + "-" + (++index[0]);
                idMap.TryAdd(id, fresh);
                SetAttribute(element, "id", fresh);
            }

            string? css = attributeValue(element, "class");
            if (css is not null)
            {
                string kept = string.Join(" ", css.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Where(cls => ClassPattern().IsMatch(cls)));
                SetAttribute(element, "class", kept);
            }

            string? style = attributeValue(element, "style");
            if (style is not null)
            {
                var kept = new List<string>();
                foreach (string rule in style.Split(';'))
                {
                    int colon = rule.IndexOf(':');
                    if (colon < 0)
                    {
                        continue;
                    }

                    string key = rule[..colon].Trim();
                    string value = rule[(colon + 1)..].Trim();
                    if ((key == "font-weight" && FontWeightPattern().IsMatch(value))
                        || (key == "padding" && PaddingPattern().IsMatch(value)))
                    {
                        kept.Add(key + ":" + value);
                    }
                }

                SetAttribute(element, "style", string.Join(";", kept));
            }

            if (tag == "a")
            {
                SetAttribute(element, "rel", "noopener noreferrer");
            }
            else if (tag == "button")
            {
                SetAttribute(element, "type", "button");
            }
            else if (tag == "img")
            {
                SetAttribute(element, "class", "img-fluid");
            }
        }
    }

    private static void FilterAttributes(TemplateElement element, string tag)
    {
        foreach (TemplateAttribute attribute in element.Attributes.ToList())
        {
            string name = attribute.Name.ToLowerInvariant();
            string value = attribute.Value;
            bool keep = name switch
            {
                "id" => IdPattern().IsMatch(value),
                "class" or "title" or "alt" or "style" => true,
                "href" => tag == "a" && HrefPattern().IsMatch(value),
                "src" => tag == "img" && SrcPattern().IsMatch(value),
                "type" => tag == "button" && value == "button",
                "data-toggle" => value == "collapse",
                "data-target" => DataTargetPattern().IsMatch(value),
                "aria-controls" => AriaControlsPattern().IsMatch(value),
                "aria-expanded" => value is "true" or "false",
                "role" => value is "button" or "alert",
                "open" => tag == "details",
                _ => false,
            };
            if (!keep)
            {
                element.Attributes.Remove(attribute);
            }
        }
    }

    private static void RewriteReferences(TemplateElement root,
        IReadOnlyDictionary<string, string> idMap)
    {
        foreach (TemplateNode node in root.Children)
        {
            if (node is not TemplateElement element)
            {
                continue;
            }

            foreach (string name in new[] { "data-target", "aria-controls", "href" })
            {
                string? value = attributeValue(element, name);
                if (value is null)
                {
                    continue;
                }

                string key = value.TrimStart('#');
                bool referenced = name == "aria-controls" || value.StartsWith('#');
                if (referenced && idMap.TryGetValue(key, out string? fresh))
                {
                    SetAttribute(element, name,
                        (name == "aria-controls" ? "" : "#") + fresh);
                }
                else if (name is "data-target" or "aria-controls")
                {
                    RemoveAttribute(element, name);
                }
            }

            RewriteReferences(element, idMap);
        }
    }

    private static string? attributeValue(TemplateElement element, string name)
    {
        foreach (TemplateAttribute attribute in element.Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static void SetAttribute(TemplateElement element, string name, string value)
    {
        for (int i = 0; i < element.Attributes.Count; i++)
        {
            if (string.Equals(element.Attributes[i].Name, name,
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Attributes[i] = new TemplateAttribute(name, value);
                return;
            }
        }

        element.Attributes.Add(new TemplateAttribute(name, value));
    }

    private static void RemoveAttribute(TemplateElement element, string name)
    {
        for (int i = element.Attributes.Count - 1; i >= 0; i--)
        {
            if (string.Equals(element.Attributes[i].Name, name,
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Attributes.RemoveAt(i);
            }
        }
    }

    private static void Serialize(TemplateNode node, StringBuilder output)
    {
        switch (node)
        {
            case TemplateText text:
                output.Append(text.Text);
                break;
            case TemplateRaw raw:
                output.Append(raw.Raw);
                break;
            case TemplateComment:
                break;
            case TemplateElement element:
                output.Append('<').Append(element.Name);
                foreach (TemplateAttribute attribute in element.Attributes)
                {
                    output.Append(' ').Append(attribute.Name);
                    if (attribute.HasValue)
                    {
                        output.Append("=\"").Append(attribute.Value).Append('"');
                    }
                }

                output.Append('>');
                if (TemplateParser.IsVoidElement(element.Name))
                {
                    return;
                }

                foreach (TemplateNode child in element.Children)
                {
                    Serialize(child, output);
                }

                output.Append("</").Append(element.Name).Append('>');
                break;
        }
    }
}
