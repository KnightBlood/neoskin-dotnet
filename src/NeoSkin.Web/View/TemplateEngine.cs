using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// Thymeleaf 子集渲染器（Native AOT 友好，无反射）。
///
/// <p>支持模板集合里实际用到的方言：<c>th:text</c> / <c>th:utext</c> /
/// <c>th:if</c> / <c>th:unless</c> / <c>th:each</c> / <c>th:replace</c> /
/// <c>th:fragment</c> / <c>th:block</c> / <c>th:switch</c> / <c>th:case</c> /
/// <c>th:attr</c> / <c>th:class</c> / <c>th:classappend</c> / URL 与表单属性 /
/// <c>th:selected</c> / <c>th:inline="javascript"</c>。</p>
///
/// <p>片段表达式支持 <c>~{template :: fragment(args)}</c>、<c>~{::fragment}</c>
/// 与 <c>__${var}__</c> 预处理。变量缺失按 Thymeleaf 的
/// <c>strict_variables = false</c> 处理为 null。</p>
/// </summary>
public sealed class TemplateEngine(TemplateRepository repository)
{
    private static readonly string[] DynamicAttributes =
    [
        "href", "src", "srcset", "action", "value", "content", "placeholder",
        "id", "style", "lang",
    ];

    private sealed class SwitchState(string value)
    {
        public string Value { get; } = value;

        public bool Matched { get; set; }
    }

    /// <summary>渲染整个模板。</summary>
    public string Render(string templateName, TemplateContext context)
    {
        TemplateDocument document = repository.Get(templateName);
        var builder = new StringBuilder(8192);
        RenderNodes(document.Children, context, builder, inlineJavaScript: false, null);
        return builder.ToString();
    }

    /// <summary>渲染一个片段（测试/内部使用）。</summary>
    public string RenderFragment(TemplateFragmentRef fragment, TemplateContext context)
    {
        var builder = new StringBuilder(4096);
        RenderFragment(fragment, context, builder, inlineJavaScript: false);
        return builder.ToString();
    }

    // ================================================================== 节点

    private void RenderNodes(IReadOnlyList<TemplateNode> nodes, TemplateContext context,
        StringBuilder builder, bool inlineJavaScript, SwitchState? switchState)
    {
        foreach (TemplateNode node in nodes)
        {
            RenderNode(node, context, builder, inlineJavaScript, switchState);
        }
    }

    private void RenderNode(TemplateNode node, TemplateContext context,
        StringBuilder builder, bool inlineJavaScript, SwitchState? switchState)
    {
        switch (node)
        {
            case TemplateText text:
                if (inlineJavaScript)
                {
                    builder.Append(RenderInlineJavaScript(text.Text, context));
                }
                else
                {
                    builder.Append(text.Text);
                }

                break;
            case TemplateComment comment:
                builder.Append(comment.Raw);
                break;
            case TemplateRaw raw:
                builder.Append(raw.Raw);
                break;
            case TemplateElement element:
                RenderElement(element, context, builder, inlineJavaScript, switchState);
                break;
        }
    }

    private void RenderElement(TemplateElement element, TemplateContext context,
        StringBuilder builder, bool inlineJavaScript, SwitchState? switchState,
        bool applyEach = true)
    {
        // th:replace（优先级最高）：整段替换
        if (element.GetAttribute("th:replace") is { } replace)
        {
            object? value = TemplateExpression.EvaluateAttributeValue(replace, context);
            if (value is TemplateFragmentRef fragment)
            {
                RenderFragment(fragment, context, builder, inlineJavaScript);
            }
            else if (value is string reference && reference.StartsWith("~{", StringComparison.Ordinal))
            {
                RenderFragment(TemplateExpression.ParseFragment(reference[2..^1], context),
                    context, builder, inlineJavaScript);
            }

            return;
        }

        // th:switch 的 case 选择
        if (element.GetAttribute("th:case") is { } caseSpec && switchState is not null)
        {
            if (switchState.Matched)
            {
                return;
            }

            string wanted = TemplateValues.ToDisplayString(
                TemplateExpression.EvaluateAttributeValue(caseSpec, context));
            if (wanted != "*" && !string.Equals(wanted, switchState.Value, StringComparison.Ordinal))
            {
                return;
            }

            switchState.Matched = true;
        }

        // th:each（先于 if/unless）
        if (applyEach && element.GetAttribute("th:each") is { } eachSpec)
        {
            RenderEach(element, eachSpec, context, builder, inlineJavaScript, switchState);
            return;
        }

        if (element.GetAttribute("th:if") is { } ifSpec
            && !TemplateValues.IsTruthy(
                TemplateExpression.EvaluateAttributeValue(ifSpec, context)))
        {
            return;
        }

        if (element.GetAttribute("th:unless") is { } unlessSpec
            && TemplateValues.IsTruthy(
                TemplateExpression.EvaluateAttributeValue(unlessSpec, context)))
        {
            return;
        }

        SwitchState? childSwitch = switchState;
        if (element.GetAttribute("th:switch") is { } switchSpec)
        {
            childSwitch = new SwitchState(TemplateValues.ToDisplayString(
                TemplateExpression.EvaluateAttributeValue(switchSpec, context)));
        }

        bool inlineForChildren = inlineJavaScript
                                 || string.Equals(element.GetAttribute("th:inline"),
                                     "javascript", StringComparison.OrdinalIgnoreCase);

        // th:block 不输出标签
        if (string.Equals(element.Name, "th:block", StringComparison.OrdinalIgnoreCase))
        {
            if (RenderTextContent(element, context, builder))
            {
                return;
            }

            RenderNodes(element.Children, context, builder, inlineForChildren, childSwitch);
            return;
        }

        builder.Append('<').Append(element.Name);
        AppendAttributes(element, context, builder);

        if (element.SelfClosing)
        {
            builder.Append(" />");
            return;
        }

        builder.Append('>');

        if (TemplateParser.IsVoidElement(element.Name))
        {
            return;
        }

        if (!RenderTextContent(element, context, builder))
        {
            RenderNodes(element.Children, context, builder, inlineForChildren, childSwitch);
        }

        builder.Append("</").Append(element.Name).Append('>');
    }

    /// <summary><c>th:text</c> / <c>th:utext</c>：替换全部子节点。</summary>
    private static bool RenderTextContent(TemplateElement element, TemplateContext context,
        StringBuilder builder)
    {
        if (element.GetAttribute("th:utext") is { } unescaped)
        {
            builder.Append(TemplateValues.ToDisplayString(
                TemplateExpression.EvaluateAttributeValue(unescaped, context)));
            return true;
        }

        if (element.GetAttribute("th:text") is { } text)
        {
            builder.Append(TemplateValues.EscapeHtml(TemplateValues.ToDisplayString(
                TemplateExpression.EvaluateAttributeValue(text, context))));
            return true;
        }

        return false;
    }

    // ================================================================== each

    private void RenderEach(TemplateElement element, string spec, TemplateContext context,
        StringBuilder builder, bool inlineJavaScript, SwitchState? switchState)
    {
        int colon = spec.IndexOf(':');
        if (colon < 0)
        {
            throw new TemplateException("th:each 缺少 ':'：" + spec);
        }

        string left = spec[..colon].Trim();
        string right = spec[(colon + 1)..].Trim();

        string? statName = null;
        string varName = left;
        int comma = left.IndexOf(',');
        if (comma >= 0)
        {
            varName = left[..comma].Trim();
            statName = left[(comma + 1)..].Trim();
        }

        object? iterable = TemplateExpression.EvaluateAttributeValue(right, context);
        List<object?> items = TemplateValues.Enumerate(iterable).ToList();

        for (int i = 0; i < items.Count; i++)
        {
            var locals = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [varName] = items[i],
            };
            if (statName is not null)
            {
                locals[statName] = new TemplateStat(i, items.Count, items[i]);
            }

            RenderElement(element, context.Child(context.TemplateName, locals), builder,
                inlineJavaScript, switchState, applyEach: false);
        }
    }

    // ================================================================== 属性

    private void AppendAttributes(TemplateElement element, TemplateContext context,
        StringBuilder builder)
    {
        var attributes = new List<(string Name, string? Value, bool HasValue)>();
        string? classAppend = null;

        foreach (TemplateAttribute attribute in element.Attributes)
        {
            // Thymeleaf 的 HTML 模式会去掉方言命名空间声明
            if (string.Equals(attribute.Name, "xmlns:th", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!attribute.IsThymeleaf)
            {
                SetAttribute(attributes, attribute.Name, attribute.Value, attribute.HasValue);
                continue;
            }

            string name = attribute.Name[3..];
            string lower = name.ToLowerInvariant();

            if (lower == "classappend")
            {
                classAppend = attribute.Value;
                continue;
            }

            if (lower == "attr")
            {
                foreach (string item in SplitOutsideQuotes(attribute.Value, ','))
                {
                    int equals = item.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string attrName = item[..equals].Trim();
                    string raw = item[(equals + 1)..].Trim();
                    object? attrValue = TemplateExpression.EvaluateAttributeValue(raw, context);
                    if (attrValue is null)
                    {
                        RemoveAttribute(attributes, attrName);
                    }
                    else
                    {
                        SetAttribute(attributes, attrName,
                            TemplateValues.ToDisplayString(attrValue), true);
                    }
                }

                continue;
            }

            if (lower is "text" or "utext" or "if" or "unless" or "each" or "replace"
                or "insert" or "fragment" or "switch" or "case" or "inline" or "remove"
                or "block")
            {
                continue;
            }

            bool isDynamic = DynamicAttributes.Contains(lower, StringComparer.Ordinal)
                             || lower is "class" or "selected";
            if (!isDynamic)
            {
                continue;
            }

            object? value = TemplateExpression.EvaluateAttributeValue(attribute.Value, context);
            if (lower == "selected")
            {
                if (TemplateValues.IsTruthy(value))
                {
                    SetAttribute(attributes, "selected", "selected", true);
                }
                else
                {
                    RemoveAttribute(attributes, "selected");
                }
            }
            else if (value is null)
            {
                RemoveAttribute(attributes, lower);
            }
            else
            {
                SetAttribute(attributes, lower, TemplateValues.ToDisplayString(value), true);
            }
        }

        if (classAppend is not null)
        {
            string appended = TemplateValues.ToDisplayString(
                TemplateExpression.EvaluateAttributeValue(classAppend, context));
            if (appended.Length > 0)
            {
                string existing = ReadAttribute(attributes, "class") ?? "";
                string combined = existing.Length == 0
                    ? appended
                    : existing.TrimEnd() + " " + appended;
                SetAttribute(attributes, "class", combined, true);
            }
        }

        foreach ((string name, string? value, bool hasValue) in attributes)
        {
            builder.Append(' ').Append(name);
            if (!hasValue)
            {
                continue;
            }

            builder.Append("=\"").Append(TemplateValues.EscapeHtml(value)).Append('"');
        }
    }

    private static string? ReadAttribute(List<(string Name, string? Value, bool HasValue)> list,
        string name)
    {
        foreach ((string candidate, string? value, _) in list)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static void SetAttribute(List<(string Name, string? Value, bool HasValue)> list,
        string name, string? value, bool hasValue)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                list[i] = (name, value, hasValue);
                return;
            }
        }

        list.Add((name, value, hasValue));
    }

    private static void RemoveAttribute(List<(string Name, string? Value, bool HasValue)> list,
        string name)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
            }
        }
    }

    private static IEnumerable<string> SplitOutsideQuotes(string text, char separator)
    {
        int depth = 0;
        char quote = '\0';
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                default:
                    if (depth == 0 && c == separator)
                    {
                        yield return text[start..i];
                        start = i + 1;
                    }

                    break;
            }
        }

        yield return text[start..];
    }

    // ================================================================== 片段

    private void RenderFragment(TemplateFragmentRef reference, TemplateContext context,
        StringBuilder builder, bool inlineJavaScript)
    {
        TemplateDocument document = repository.Get(reference.Template);
        if (!document.Fragments.TryGetValue(reference.Fragment,
                out TemplateFragment? fragment))
        {
            throw new TemplateException(
                $"片段不存在：{reference.Template} :: {reference.Fragment}");
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < fragment.Parameters.Count; i++)
        {
            locals[fragment.Parameters[i]] =
                i < reference.Arguments.Count ? reference.Arguments[i] : null;
        }

        TemplateContext child = context.Child(reference.Template, locals);
        RenderElement(fragment.Element, child, builder, inlineJavaScript, null);
    }

    // ================================================================== 内联 JS

    private string RenderInlineJavaScript(string text, TemplateContext context)
    {
        var builder = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            int start = text.IndexOf("[[", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);
            int end = text.IndexOf("]]", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            string inner = text[(start + 2)..end].Trim();
            object? value = TemplateExpression.EvaluateAttributeValue(inner, context);
            builder.Append(PhpJson.Encode(value, PhpJson.JsonUnescapedUnicode));
            index = end + 2;
        }

        return builder.ToString();
    }
}
