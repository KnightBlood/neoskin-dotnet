using System.Globalization;
using System.Text;

namespace NeoSkin.Web.View;

/// <summary>可求值的模板表达式。</summary>
public interface ITemplateExpression
{
    object? Evaluate(TemplateContext context);
}

/// <summary>
/// Thymeleaf 标准表达式子集：字面量、变量、属性/索引访问、方法调用、
/// 一元/二元运算、三元表达式、片段表达式 <c>~{...}</c>。
/// </summary>
public static class TemplateExpression
{
    /// <summary>解析一个表达式（不含 <c>${...}</c> 外壳）。</summary>
    public static ITemplateExpression Parse(string source)
    {
        var parser = new Parser(source);
        ITemplateExpression expression = parser.ParseExpression();
        parser.ExpectEnd();
        return expression;
    }

    /// <summary>
    /// 求值一段「模板属性值」：<c>${...}</c>、<c>${...}</c> 外的字面量、
    /// <c>~{...}</c> 片段引用、<c>|...|</c> 字面替换。
    /// </summary>
    public static object? EvaluateAttributeValue(string raw, TemplateContext context)
    {
        string value = raw.Trim();
        if (value.Length == 0)
        {
            return "";
        }

        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return Parse(value[2..^1]).Evaluate(context);
        }

        if (value.StartsWith("~{", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return Parse(value).Evaluate(context);
        }

        if (value.StartsWith('|') && value.EndsWith('|') && value.Length >= 2)
        {
            return EvaluateLiteralSubstitution(value[1..^1], context);
        }

        return value;
    }

    /// <summary><c>|...${expr}...|</c> 字面替换。</summary>
    public static string EvaluateLiteralSubstitution(string text, TemplateContext context)
    {
        var builder = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            int start = text.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);
            int end = FindExpressionEnd(text, start + 2);
            if (end < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            object? value = Parse(text[(start + 2)..end]).Evaluate(context);
            builder.Append(TemplateValues.ToDisplayString(value));
            index = end + 1;
        }

        return builder.ToString();
    }

    private static int FindExpressionEnd(string text, int from)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = from; i < text.Length; i++)
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
                case '{':
                    depth++;
                    break;
                case '}':
                    if (depth == 0)
                    {
                        return i;
                    }

                    depth--;
                    break;
            }
        }

        return -1;
    }

    // ================================================================== lexer

    private enum Kind
    {
        Number,
        String,
        Identifier,
        Operator,
        Punctuation,
        Fragment,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text, object? Value);

    private sealed class Lexer(string source)
    {
        private int _index;

        public Token Next()
        {
            while (_index < source.Length && char.IsWhiteSpace(source[_index]))
            {
                _index++;
            }

            if (_index >= source.Length)
            {
                return new Token(Kind.End, "", null);
            }

            char c = source[_index];

            if (char.IsDigit(c))
            {
                int start = _index;
                while (_index < source.Length && char.IsDigit(source[_index]))
                {
                    _index++;
                }

                if (_index < source.Length && source[_index] == '.'
                    && _index + 1 < source.Length && char.IsDigit(source[_index + 1]))
                {
                    _index++;
                    while (_index < source.Length && char.IsDigit(source[_index]))
                    {
                        _index++;
                    }

                    double real = double.Parse(source[start.._index],
                        CultureInfo.InvariantCulture);
                    return new Token(Kind.Number, source[start.._index], real);
                }

                long integer = long.Parse(source[start.._index], CultureInfo.InvariantCulture);
                return new Token(Kind.Number, source[start.._index], integer);
            }

            if (c is '\'' or '"')
            {
                return ReadString(c);
            }

            if (c == '~' && _index + 1 < source.Length && source[_index + 1] == '{')
            {
                return ReadFragment();
            }

            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                int start = _index++;
                while (_index < source.Length
                       && (char.IsLetterOrDigit(source[_index]) || source[_index] == '_'))
                {
                    _index++;
                }

                return new Token(Kind.Identifier, source[start.._index], null);
            }

            // 双目运算符
            foreach (string op in new[] { "==", "!=", ">=", "<=", "&&", "||" })
            {
                if (string.CompareOrdinal(source, _index, op, 0, op.Length) == 0)
                {
                    _index += op.Length;
                    return new Token(Kind.Operator, op, null);
                }
            }

            if (c is '+' or '-' or '*' or '/' or '%' or '!' or '>' or '<')
            {
                _index++;
                return new Token(Kind.Operator, c.ToString(), null);
            }

            if (c is '.' or ',' or '(' or ')' or '[' or ']' or '?' or ':' or '=')
            {
                _index++;
                return new Token(Kind.Punctuation, c.ToString(), null);
            }

            throw new TemplateException("无法识别的表达式字符：" + c);
        }

        private Token ReadString(char quote)
        {
            _index++;
            var builder = new StringBuilder();
            while (_index < source.Length)
            {
                char c = source[_index++];
                if (c == quote)
                {
                    return new Token(Kind.String, builder.ToString(), builder.ToString());
                }

                if (c == '\\' && _index < source.Length)
                {
                    char escaped = source[_index++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }

                builder.Append(c);
            }

            throw new TemplateException("字符串字面量未闭合");
        }

        private Token ReadFragment()
        {
            _index += 2; // ~{
            int start = _index;
            int depth = 0;
            char quote = '\0';
            while (_index < source.Length)
            {
                char c = source[_index];
                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        _index += 2;
                        continue;
                    }

                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    _index++;
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    _index++;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    _index++;
                    continue;
                }

                if (c == '}')
                {
                    if (depth == 0)
                    {
                        string inner = source[start.._index];
                        _index++;
                        return new Token(Kind.Fragment, inner, inner);
                    }

                    depth--;
                    _index++;
                    continue;
                }

                _index++;
            }

            throw new TemplateException("片段表达式未闭合");
        }
    }

    // ================================================================== parser

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private Token _current;

        public Parser(string source)
        {
            _lexer = new Lexer(source);
            _current = _lexer.Next();
        }

        public ITemplateExpression ParseExpression() => ParseTernary();

        public void ExpectEnd()
        {
            if (_current.Kind != Kind.End)
            {
                throw new TemplateException("表达式尾部有多余内容：" + _current.Text);
            }
        }

        private void Advance() => _current = _lexer.Next();

        private bool Match(string text)
        {
            if (string.Equals(_current.Text, text, StringComparison.Ordinal)
                || (string.Equals(_current.Text, text, StringComparison.OrdinalIgnoreCase)
                    && _current.Kind == Kind.Identifier))
            {
                Advance();
                return true;
            }

            return false;
        }

        private ITemplateExpression ParseTernary()
        {
            ITemplateExpression condition = ParseOr();
            if (Match("?"))
            {
                ITemplateExpression whenTrue = ParseTernary();
                if (!Match(":"))
                {
                    throw new TemplateException("三元表达式缺少 ':'");
                }

                ITemplateExpression whenFalse = ParseTernary();
                return new ConditionalExpression(condition, whenTrue, whenFalse);
            }

            return condition;
        }

        private ITemplateExpression ParseOr()
        {
            ITemplateExpression left = ParseAnd();
            while (_current.Kind is Kind.Identifier or Kind.Operator
                   && (Is("or") || Is("||")))
            {
                Advance();
                left = new BinaryExpression("or", left, ParseAnd());
            }

            return left;
        }

        private ITemplateExpression ParseAnd()
        {
            ITemplateExpression left = ParseEquality();
            while (_current.Kind is Kind.Identifier or Kind.Operator
                   && (Is("and") || Is("&&")))
            {
                Advance();
                left = new BinaryExpression("and", left, ParseEquality());
            }

            return left;
        }

        private ITemplateExpression ParseEquality()
        {
            ITemplateExpression left = ParseRelational();
            while (Is("==") || Is("!=") || Is("eq") || Is("ne"))
            {
                string op = _current.Text.ToLowerInvariant();
                Advance();
                left = new BinaryExpression(op is "eq" ? "==" : op is "ne" ? "!=" : op,
                    left, ParseRelational());
            }

            return left;
        }

        private ITemplateExpression ParseRelational()
        {
            ITemplateExpression left = ParseAdditive();
            while (Is(">") || Is("<") || Is(">=") || Is("<=")
                   || Is("gt") || Is("lt") || Is("ge") || Is("le"))
            {
                string op = _current.Text.ToLowerInvariant();
                Advance();
                op = op switch
                {
                    "gt" => ">",
                    "lt" => "<",
                    "ge" => ">=",
                    "le" => "<=",
                    _ => op,
                };
                left = new BinaryExpression(op, left, ParseAdditive());
            }

            return left;
        }

        private ITemplateExpression ParseAdditive()
        {
            ITemplateExpression left = ParseMultiplicative();
            while (Is("+") || Is("-"))
            {
                string op = _current.Text;
                Advance();
                left = new BinaryExpression(op, left, ParseMultiplicative());
            }

            return left;
        }

        private ITemplateExpression ParseMultiplicative()
        {
            ITemplateExpression left = ParseUnary();
            while (Is("*") || Is("/") || Is("%") || Is("div") || Is("mod"))
            {
                string op = _current.Text.ToLowerInvariant();
                Advance();
                left = new BinaryExpression(op switch
                {
                    "div" => "/",
                    "mod" => "%",
                    _ => op,
                }, left, ParseUnary());
            }

            return left;
        }

        private ITemplateExpression ParseUnary()
        {
            if (Is("!") || Is("not"))
            {
                Advance();
                return new NotExpression(ParseUnary());
            }

            if (Is("-"))
            {
                Advance();
                return new NegateExpression(ParseUnary());
            }

            if (Is("+"))
            {
                Advance();
                return ParseUnary();
            }

            return ParsePostfix();
        }

        private ITemplateExpression ParsePostfix()
        {
            ITemplateExpression expression = ParsePrimary();
            while (true)
            {
                if (Match("."))
                {
                    if (_current.Kind != Kind.Identifier)
                    {
                        throw new TemplateException("'.' 后需要属性名");
                    }

                    string name = _current.Text;
                    Advance();
                    if (Match("("))
                    {
                        IReadOnlyList<ITemplateExpression> arguments = ParseArguments();
                        expression = new CallExpression(expression, name, arguments);
                    }
                    else
                    {
                        expression = new MemberExpression(expression, name);
                    }

                    continue;
                }

                if (Match("["))
                {
                    ITemplateExpression index = ParseTernary();
                    if (!Match("]"))
                    {
                        throw new TemplateException("索引表达式缺少 ']'");
                    }

                    expression = new IndexExpression(expression, index);
                    continue;
                }

                break;
            }

            return expression;
        }

        private ITemplateExpression ParsePrimary()
        {
            if (_current.Kind == Kind.Number)
            {
                object value = _current.Value!;
                Advance();
                return new LiteralExpression(value);
            }

            if (_current.Kind == Kind.String)
            {
                string value = (string)_current.Value!;
                Advance();
                return new LiteralExpression(value);
            }

            if (_current.Kind == Kind.Fragment)
            {
                string spec = (string)_current.Value!;
                Advance();
                return new FragmentExpression(spec);
            }

            if (_current.Kind == Kind.Identifier)
            {
                string name = _current.Text;
                Advance();
                switch (name)
                {
                    case "true":
                        return new LiteralExpression(true);
                    case "false":
                        return new LiteralExpression(false);
                    case "null":
                        return new LiteralExpression(null);
                    default:
                        if (Match("("))
                        {
                            IReadOnlyList<ITemplateExpression> arguments = ParseArguments();
                            return new CallExpression(new VariableExpression(name),
                                "__invoke__", arguments);
                        }

                        return new VariableExpression(name);
                }
            }

            if (Match("("))
            {
                ITemplateExpression expression = ParseTernary();
                if (!Match(")"))
                {
                    throw new TemplateException("括号未闭合");
                }

                return expression;
            }

            throw new TemplateException("无法解析表达式：" + _current.Text);
        }

        private IReadOnlyList<ITemplateExpression> ParseArguments()
        {
            var arguments = new List<ITemplateExpression>();
            if (Match(")"))
            {
                return arguments;
            }

            while (true)
            {
                arguments.Add(ParseTernary());
                if (Match(")"))
                {
                    return arguments;
                }

                if (!Match(","))
                {
                    throw new TemplateException("参数列表缺少 ',' 或 ')'");
                }
            }
        }

        private bool Is(string text) =>
            string.Equals(_current.Text, text, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== AST

    private sealed class LiteralExpression(object? value) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) => value;
    }

    private sealed class VariableExpression(string name) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) => context.Get(name);
    }

    private sealed class MemberExpression(
        ITemplateExpression target, string name) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) =>
            TemplateValues.GetMember(target.Evaluate(context), name);
    }

    private sealed class IndexExpression(
        ITemplateExpression target, ITemplateExpression index) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) =>
            TemplateValues.Index(target.Evaluate(context), index.Evaluate(context));
    }

    private sealed class CallExpression(
        ITemplateExpression target, string name,
        IReadOnlyList<ITemplateExpression> arguments) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context)
        {
            object? receiver = target.Evaluate(context);
            var values = new object?[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                values[i] = arguments[i].Evaluate(context);
            }

            if (name == "__invoke__")
            {
                // 顶层函数调用：模板里没有自定义函数，按变量值当作委托/无参处理。
                return receiver is Delegate callback
                    ? callback.DynamicInvoke(values)
                    : TemplateValues.Invoke(receiver, "invoke", values);
            }

            return TemplateValues.Invoke(receiver, name, values);
        }
    }

    private sealed class NotExpression(ITemplateExpression operand) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) =>
            !TemplateValues.IsTruthy(operand.Evaluate(context));
    }

    private sealed class NegateExpression(ITemplateExpression operand) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context)
        {
            object? value = operand.Evaluate(context);
            return value switch
            {
                long integer => -integer,
                int integer => -(long)integer,
                double real => -real,
                float real => -(double)real,
                _ => 0L,
            };
        }
    }

    private sealed class BinaryExpression(
        string op, ITemplateExpression left, ITemplateExpression right) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context)
        {
            switch (op)
            {
                case "and":
                    return TemplateValues.IsTruthy(left.Evaluate(context))
                           && TemplateValues.IsTruthy(right.Evaluate(context));
                case "or":
                    return TemplateValues.IsTruthy(left.Evaluate(context))
                           || TemplateValues.IsTruthy(right.Evaluate(context));
            }

            object? l = left.Evaluate(context);
            object? r = right.Evaluate(context);

            switch (op)
            {
                case "==":
                    return TemplateValues.AreEqual(l, r);
                case "!=":
                    return !TemplateValues.AreEqual(l, r);
                case ">":
                    return TemplateValues.Compare(l, r) > 0;
                case "<":
                    return TemplateValues.Compare(l, r) < 0;
                case ">=":
                    return TemplateValues.Compare(l, r) >= 0;
                case "<=":
                    return TemplateValues.Compare(l, r) <= 0;
                case "+":
                    if (l is string || r is string)
                    {
                        return TemplateValues.ToDisplayString(l)
                               + TemplateValues.ToDisplayString(r);
                    }

                    if (IsIntegral(l) && IsIntegral(r))
                    {
                        return Convert.ToInt64(l, CultureInfo.InvariantCulture)
                               + Convert.ToInt64(r, CultureInfo.InvariantCulture);
                    }

                    return AsDouble(l) + AsDouble(r);
                case "-":
                    if (IsIntegral(l) && IsIntegral(r))
                    {
                        return Convert.ToInt64(l, CultureInfo.InvariantCulture)
                               - Convert.ToInt64(r, CultureInfo.InvariantCulture);
                    }

                    return AsDouble(l) - AsDouble(r);
                case "*":
                    if (IsIntegral(l) && IsIntegral(r))
                    {
                        return Convert.ToInt64(l, CultureInfo.InvariantCulture)
                               * Convert.ToInt64(r, CultureInfo.InvariantCulture);
                    }

                    return AsDouble(l) * AsDouble(r);
                case "/":
                    double divisor = AsDouble(r);
                    return divisor == 0 ? 0d : AsDouble(l) / divisor;
                case "%":
                    double modulo = AsDouble(r);
                    if (modulo == 0)
                    {
                        return 0L;
                    }

                    if (IsIntegral(l) && IsIntegral(r))
                    {
                        return Convert.ToInt64(l, CultureInfo.InvariantCulture)
                               % Convert.ToInt64(r, CultureInfo.InvariantCulture);
                    }

                    return AsDouble(l) % modulo;
                default:
                    return null;
            }
        }

        private static bool IsIntegral(object? value) =>
            value is sbyte or byte or short or ushort or int or uint or long or ulong;

        private static double AsDouble(object? value) =>
            value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private sealed class ConditionalExpression(
        ITemplateExpression condition, ITemplateExpression whenTrue,
        ITemplateExpression whenFalse) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) =>
            TemplateValues.IsTruthy(condition.Evaluate(context))
                ? whenTrue.Evaluate(context)
                : whenFalse.Evaluate(context);
    }

    private sealed class FragmentExpression(string specification) : ITemplateExpression
    {
        public object? Evaluate(TemplateContext context) =>
            ParseFragment(specification, context);
    }

    // ================================================================== 片段表达式

    /// <summary>解析 <c>template :: fragment(args)</c>（外层 <c>~{...}</c> 已剥离）。</summary>
    public static TemplateFragmentRef ParseFragment(string specification,
        TemplateContext context)
    {
        int separator = FindTopLevel(specification, "::");
        if (separator < 0)
        {
            return new TemplateFragmentRef(context.TemplateName,
                specification.Trim(), []);
        }

        string templatePart = specification[..separator].Trim();
        string rest = specification[(separator + 2)..].Trim();

        string template = templatePart.Length == 0
            ? context.TemplateName
            : EvaluateTemplateName(templatePart, context);

        string fragment;
        var arguments = new List<object?>();
        int paren = rest.IndexOf('(');
        if (paren < 0)
        {
            fragment = rest;
        }
        else
        {
            fragment = rest[..paren].Trim();
            int close = FindMatching(rest, paren);
            string inner = close > paren ? rest[(paren + 1)..close] : "";
            foreach (string argument in SplitTopLevel(inner, ','))
            {
                string trimmed = argument.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                arguments.Add(EvaluateArgument(trimmed, context));
            }
        }

        return new TemplateFragmentRef(template, fragment, arguments);
    }

    private static object? EvaluateArgument(string text, TemplateContext context)
    {
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
        {
            return Parse(text[2..^1]).Evaluate(context);
        }

        if (text.StartsWith("~{", StringComparison.Ordinal))
        {
            return Parse(text).Evaluate(context);
        }

        if (text.StartsWith('|') && text.EndsWith('|'))
        {
            return EvaluateLiteralSubstitution(text[1..^1], context);
        }

        // 普通表达式参数：字符串字面量、数字、true/false/null、变量
        try
        {
            return Parse(text).Evaluate(context);
        }
        catch (TemplateException)
        {
            return text;
        }
    }

    private static string EvaluateTemplateName(string text, TemplateContext context)
    {
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
        {
            return TemplateValues.ToDisplayString(Parse(text[2..^1]).Evaluate(context));
        }

        if (text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[^1] == text[0])
        {
            return text[1..^1];
        }

        // 预处理：__${w}__ → 变量值
        if (!text.Contains("__", StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder();
        int index = 0;
        while (index < text.Length)
        {
            int start = text.IndexOf("__", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);
            int end = text.IndexOf("__", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            string inner = text[(start + 2)..end].Trim();
            object? value = inner.StartsWith("${", StringComparison.Ordinal)
                            && inner.EndsWith('}')
                ? Parse(inner[2..^1]).Evaluate(context)
                : EvaluateAttributeValue(inner, context);
            builder.Append(TemplateValues.ToDisplayString(value));
            index = end + 2;
        }

        return builder.ToString();
    }

    private static int FindTopLevel(string text, string needle)
    {
        int depth = 0;
        char quote = '\0';
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
                    if (depth == 0 && c == needle[0]
                        && string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int FindMatching(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < text.Length; i++)
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
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
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
}
