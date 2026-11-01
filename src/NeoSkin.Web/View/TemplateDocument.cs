using System.Collections.Concurrent;
using System.Text;

namespace NeoSkin.Web.View;

/// <summary>模板语法树节点。</summary>
public abstract class TemplateNode
{
}

/// <summary>原样输出的文本。</summary>
public sealed class TemplateText(string text) : TemplateNode
{
    public string Text { get; } = text;
}

/// <summary>HTML 注释（<c>&lt;!-- ... --&gt;</c>，含标记）。</summary>
public sealed class TemplateComment(string raw) : TemplateNode
{
    public string Raw { get; } = raw;
}

/// <summary>DOCTYPE / 处理指令等原样节点。</summary>
public sealed class TemplateRaw(string raw) : TemplateNode
{
    public string Raw { get; } = raw;
}

/// <summary>一个属性（保留原始顺序与大小写）。</summary>
public sealed class TemplateAttribute(string name, string value, bool hasValue = true)
{
    public string Name { get; } = name;

    public string Value { get; } = value;

    /// <summary>是否写成 <c>name="value"</c>（否则是 <c>required</c> 这类布尔属性）。</summary>
    public bool HasValue { get; } = hasValue;

    public bool IsThymeleaf =>
        Name.StartsWith("th:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一个元素及其子节点。</summary>
public sealed class TemplateElement(string name) : TemplateNode
{
    public string Name { get; } = name;

    public List<TemplateAttribute> Attributes { get; } = [];

    public List<TemplateNode> Children { get; } = [];

    /// <summary>源里写成 <c>/&gt;</c> 的自闭合标签。</summary>
    public bool SelfClosing { get; set; }

    public string? GetAttribute(string name)
    {
        foreach (TemplateAttribute attribute in Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    public bool HasAttribute(string name) => GetAttribute(name) is not null;
}

/// <summary>一个 <c>th:fragment</c> 定义。</summary>
public sealed record TemplateFragment(
    string Name,
    TemplateElement Element,
    IReadOnlyList<string> Parameters);

/// <summary>已解析的模板。</summary>
public sealed class TemplateDocument
{
    public required string Name { get; init; }

    public required List<TemplateNode> Children { get; init; }

    public required Dictionary<string, TemplateFragment> Fragments { get; init; }
}

/// <summary>
/// 容错 HTML 解析器：保留文本/注释/DOCTYPE 原文，元素按标签栈组装。
///
/// <para>这些模板都是 Thymeleaf 可解析的 HTML，标签闭合完整；这里不做 HTML5
/// 错误恢复，只在闭合标签不匹配时弹栈到最近的同名元素。属性值原样保存
/// （不解析实体），引擎输出时再决定转义。</para>
/// </summary>
public static class TemplateParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr",
    };

    /// <summary>内容按原文处理、不解析标签的元素（避免 CDATA / JS 里的 <c>&lt;</c>）。</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script",
    };

    public static TemplateDocument Parse(string name, string source)
    {
        var root = new List<TemplateNode>();
        var stack = new List<TemplateElement>();
        int index = 0;

        List<TemplateNode> CurrentChildren() =>
            stack.Count == 0 ? root : stack[^1].Children;

        while (index < source.Length)
        {
            int lt = source.IndexOf('<', index);
            if (lt < 0)
            {
                CurrentChildren().Add(new TemplateText(source[index..]));
                break;
            }

            if (lt > index)
            {
                CurrentChildren().Add(new TemplateText(source[index..lt]));
            }

            // <!-- comment -->
            if (source.AsSpan(lt).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = source.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                int stop = end < 0 ? source.Length : end + 3;
                CurrentChildren().Add(new TemplateComment(source[lt..stop]));
                index = stop;
                continue;
            }

            // <!DOCTYPE ...> / <![CDATA[ ... ]]> / <?...?>
            if (lt + 1 < source.Length
                && (source[lt + 1] == '!' || source[lt + 1] == '?'))
            {
                int end = source.IndexOf('>', lt + 2);
                int stop = end < 0 ? source.Length : end + 1;
                CurrentChildren().Add(new TemplateRaw(source[lt..stop]));
                index = stop;
                continue;
            }

            // </closing>
            if (lt + 1 < source.Length && source[lt + 1] == '/')
            {
                int end = source.IndexOf('>', lt + 2);
                if (end < 0)
                {
                    CurrentChildren().Add(new TemplateText(source[lt..]));
                    break;
                }

                string closeName = source[(lt + 2)..end].Trim();
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(stack[i].Name, closeName, StringComparison.OrdinalIgnoreCase))
                    {
                        stack.RemoveRange(i, stack.Count - i);
                        break;
                    }
                }

                index = end + 1;
                continue;
            }

            // <open ...>
            if (lt + 1 >= source.Length || !IsNameStart(source[lt + 1]))
            {
                CurrentChildren().Add(new TemplateText("<"));
                index = lt + 1;
                continue;
            }

            int pos = lt + 1;
            int nameStart = pos;
            while (pos < source.Length && IsNameChar(source[pos]))
            {
                pos++;
            }

            string tagName = source[nameStart..pos];
            var element = new TemplateElement(tagName);
            bool selfClosing = false;

            while (pos < source.Length)
            {
                while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                {
                    pos++;
                }

                if (pos >= source.Length)
                {
                    break;
                }

                if (source[pos] == '>')
                {
                    pos++;
                    break;
                }

                if (source[pos] == '/' && pos + 1 < source.Length && source[pos + 1] == '>')
                {
                    selfClosing = true;
                    pos += 2;
                    break;
                }

                int attrStart = pos;
                while (pos < source.Length && !char.IsWhiteSpace(source[pos])
                       && source[pos] != '=' && source[pos] != '>' && source[pos] != '/')
                {
                    pos++;
                }

                string attrName = source[attrStart..pos];
                if (attrName.Length == 0)
                {
                    pos++;
                    continue;
                }

                while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                {
                    pos++;
                }

                string attrValue = "";
                bool hasValue = false;
                if (pos < source.Length && source[pos] == '=')
                {
                    hasValue = true;
                    pos++;
                    while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                    {
                        pos++;
                    }

                    if (pos < source.Length && (source[pos] == '"' || source[pos] == '\''))
                    {
                        char quote = source[pos++];
                        int valueStart = pos;
                        while (pos < source.Length && source[pos] != quote)
                        {
                            pos++;
                        }

                        attrValue = source[valueStart..pos];
                        if (pos < source.Length)
                        {
                            pos++;
                        }
                    }
                    else
                    {
                        int valueStart = pos;
                        while (pos < source.Length && !char.IsWhiteSpace(source[pos])
                               && source[pos] != '>')
                        {
                            pos++;
                        }

                        attrValue = source[valueStart..pos];
                    }
                }

                element.Attributes.Add(new TemplateAttribute(attrName, attrValue, hasValue));
            }

            element.SelfClosing = selfClosing;
            CurrentChildren().Add(element);
            index = pos;

            if (selfClosing || VoidElements.Contains(tagName))
            {
                continue;
            }

            if (RawTextElements.Contains(tagName))
            {
                int close = FindRawClose(source, pos, tagName);
                if (close < 0)
                {
                    element.Children.Add(new TemplateText(source[pos..]));
                    index = source.Length;
                }
                else
                {
                    element.Children.Add(new TemplateText(source[pos..close]));
                    int gt = source.IndexOf('>', close);
                    index = gt < 0 ? source.Length : gt + 1;
                }

                continue;
            }

            stack.Add(element);
        }

        var fragments = new Dictionary<string, TemplateFragment>(StringComparer.Ordinal);
        CollectFragments(root, fragments);

        return new TemplateDocument
        {
            Name = name,
            Children = root,
            Fragments = fragments,
        };
    }

    /// <summary>HTML 空元素（输出时不需要闭合标签）。</summary>
    public static bool IsVoidElement(string name) => VoidElements.Contains(name);

    private static int FindRawClose(string source, int from, string tagName)
    {
        string needle = "</" + tagName;
        int index = from;
        while (index < source.Length)
        {
            int found = source.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return -1;
            }

            int after = found + needle.Length;
            if (after >= source.Length || char.IsWhiteSpace(source[after]) || source[after] == '>')
            {
                return found;
            }

            index = after;
        }

        return -1;
    }

    private static void CollectFragments(IReadOnlyList<TemplateNode> nodes,
        Dictionary<string, TemplateFragment> fragments)
    {
        foreach (TemplateNode node in nodes)
        {
            if (node is not TemplateElement element)
            {
                continue;
            }

            string? spec = element.GetAttribute("th:fragment");
            if (spec is not null)
            {
                (string name, IReadOnlyList<string> parameters) = ParseFragmentSignature(spec);
                fragments.TryAdd(name, new TemplateFragment(name, element, parameters));
            }

            CollectFragments(element.Children, fragments);
        }
    }

    /// <summary><c>page(title, content, beforeFoot)</c> → 名称 + 参数列表。</summary>
    public static (string Name, IReadOnlyList<string> Parameters) ParseFragmentSignature(
        string signature)
    {
        string trimmed = signature.Trim();
        int paren = trimmed.IndexOf('(');
        if (paren < 0)
        {
            return (trimmed, []);
        }

        string name = trimmed[..paren].Trim();
        int close = trimmed.LastIndexOf(')');
        string inner = close > paren ? trimmed[(paren + 1)..close] : "";
        var parameters = new List<string>();
        foreach (string part in inner.Split(','))
        {
            string parameter = part.Trim();
            if (parameter.Length > 0)
            {
                parameters.Add(parameter);
            }
        }

        return (name, parameters);
    }

    private static bool IsNameStart(char c) =>
        char.IsLetter(c) || c == '_' || c == ':';

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.';
}

/// <summary>只读模板源（文件或内存）。</summary>
public interface ITemplateSource
{
    string? Load(string name);
}

/// <summary>从目录加载 <c>name.html</c>。</summary>
public sealed class FileTemplateSource(string root) : ITemplateSource
{
    public string Root { get; } = root;

    public string? Load(string name)
    {
        string path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar)
                                         + ".html");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }
}

/// <summary>测试/内嵌用的内存模板源。</summary>
public sealed class DictionaryTemplateSource(
    IReadOnlyDictionary<string, string> templates) : ITemplateSource
{
    public string? Load(string name) =>
        templates.TryGetValue(name, out string? source) ? source : null;
}

/// <summary>模板仓库：解析一次后缓存，供每次渲染复用。</summary>
public sealed class TemplateRepository(ITemplateSource source)
{
    private readonly ConcurrentDictionary<string, TemplateDocument> _cache =
        new(StringComparer.Ordinal);

    public TemplateDocument Get(string name)
    {
        if (_cache.TryGetValue(name, out TemplateDocument? cached))
        {
            return cached;
        }

        string? text = source.Load(name)
            ?? throw new TemplateException($"模板不存在：{name}");
        TemplateDocument document = TemplateParser.Parse(name, text);
        _cache[name] = document;

        return document;
    }
}

/// <summary>模板解析/渲染失败。</summary>
public sealed class TemplateException(string message) : Exception(message)
{
}
