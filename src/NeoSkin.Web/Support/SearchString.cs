using System.Globalization;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// 源 <c>App\Support\SearchString</c> 的实用子集：把 <c>q=status:0 sort:report_at</c>
/// 这类搜索串编译成 SQL 条件。
///
/// <p>本移植覆盖管理端当前用到的语法：</p>
/// <ul>
///   <li>条件：<c>col:value</c> / <c>col=value</c> / <c>col&gt;value</c> / <c>col&gt;=value</c> /
///       <c>col&lt;value</c> / <c>col&lt;=value</c>；逗号分隔的值为 <c>IN</c>；</li>
///   <li>关键字：<c>sort:col</c>、<c>order_by:asc|desc</c>、<c>fields:a,b</c>；
///       <c>limit/from</c> 被分页器覆盖，忽略；</li>
///   <li>多条件按空白隐式 AND。</li>
/// </ul>
///
/// <p>任何未知列/无法解析的记号都按源配置 <c>search-string.fail = all-results</c>
/// 处理：<see cref="Compile"/> 返回 <see cref="Result.Invalid"/>，调用方不加过滤。</p>
/// </summary>
public static class SearchString
{
    public sealed record ColumnSpec(string Column, bool Searchable, bool Date, bool BooleanCast);

    public sealed class Options
    {
        private readonly Dictionary<string, ColumnSpec> _columns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _keywords = new(StringComparer.Ordinal);

        public Options Column(string column)
        {
            _columns[column] = new ColumnSpec(column, false, false, false);
            return this;
        }

        /// <summary>列别名（源 <c>'tid_skin' =&gt; '/^(?:tid_)?skin$/'</c>）。</summary>
        public Options Alias(string alias, string column)
        {
            _aliases[alias] = column;
            return this;
        }

        public Options Searchable(string column)
        {
            _columns[column] = new ColumnSpec(column, true, false, false);
            return this;
        }

        public Options Date(string column)
        {
            _columns[column] = new ColumnSpec(column, false, true, true);
            return this;
        }

        public Options BooleanColumn(string column)
        {
            _columns[column] = new ColumnSpec(column, false, false, true);
            return this;
        }

        public Options Keyword(string key, string column)
        {
            _keywords[key] = column;
            return this;
        }

        internal ColumnSpec? ColumnRule(string key)
        {
            if (_columns.TryGetValue(key, out ColumnSpec? spec))
            {
                return spec;
            }

            return _aliases.TryGetValue(key, out string? column)
                ? new ColumnSpec(column, false, false, false)
                : null;
        }

        internal IReadOnlyList<string> Searchables() =>
            _columns.Values.Where(spec => spec.Searchable).Select(spec => spec.Column).ToList();

        internal string? KeywordRule(string key) =>
            _keywords.TryGetValue(key, out string? column) ? column : null;
    }

    /// <summary>编译结果；<see cref="Invalid"/> 为 true 时调用方忽略全部条件。</summary>
    public sealed record Result(string? WhereSql,
        IReadOnlyList<(string Name, object? Value)> Parameters,
        IReadOnlyList<string> Select, IReadOnlyList<string> OrderBy, bool Invalid)
    {
        public static Result AllResults() => new(null, [], [], [], true);
    }

    public static Result Compile(string? query, Options options)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new Result(null, [], [], [], false);
        }

        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();
        var select = new List<string>();
        var orderBy = new List<string>();
        string? sortColumn = null;
        string? orderDirection = null;
        int index = 0;

        foreach (string token in Tokenize(query))
        {
            (string key, string op, string value)? parsed = ParseTerm(token);
            if (parsed is null)
            {
                // 纯词：对可全文搜索的列做 LIKE（源 ColumnRule::searchable）
                List<string> searchables = options.Searchables().ToList();
                if (searchables.Count == 0)
                {
                    return Result.AllResults();
                }

                var likes = new List<string>();
                foreach (string column in searchables)
                {
                    string p = "@s" + index++;
                    likes.Add(column + " LIKE " + p);
                    parameters.Add((p, "%" + token + "%"));
                }

                conditions.Add("(" + string.Join(" OR ", likes) + ")");
                continue;
            }

            (string key, string op, string value) = parsed.Value;
            string? keywordColumn = options.KeywordRule(key);
            if (keywordColumn is not null)
            {
                switch (keywordColumn)
                {
                    case "sort":
                        sortColumn = value;
                        break;
                    case "order_by":
                        orderDirection = value;
                        break;
                    case "select":
                        select.AddRange(SplitList(value));
                        break;
                    default:
                        // limit / offset 被 paginate() 覆盖
                        break;
                }

                continue;
            }

            ColumnSpec? spec = options.ColumnRule(key);
            if (spec is null)
            {
                return Result.AllResults();
            }

            string name = "@s" + index++;
            if (op is "=" or ":")
            {
                List<string> values = SplitList(value);
                if (values.Count > 1)
                {
                    var names = new List<string>();
                    foreach (string item in values)
                    {
                        string p = "@s" + index++;
                        names.Add(p);
                        parameters.Add((p, Coerce(item, spec)));
                    }

                    conditions.Add(spec.Column + " IN (" + string.Join(", ", names) + ")");
                }
                else
                {
                    conditions.Add(spec.Column + " = " + name);
                    parameters.Add((name, Coerce(value, spec)));
                }
            }
            else
            {
                conditions.Add(spec.Column + " " + op + " " + name);
                parameters.Add((name, Coerce(value, spec)));
            }
        }

        if (sortColumn is not null)
        {
            if (options.ColumnRule(sortColumn) is null)
            {
                return Result.AllResults();
            }

            string direction = string.Equals(orderDirection, "desc", StringComparison.OrdinalIgnoreCase)
                ? "DESC"
                : "ASC";
            orderBy.Add(sortColumn + " " + direction);
        }

        return new Result(
            conditions.Count == 0 ? null : string.Join(" AND ", conditions),
            parameters, select, orderBy, false);
    }

    private static object? Coerce(string value, ColumnSpec spec)
    {
        string raw = Unquote(value);
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (spec.BooleanCast && (raw == "0" || raw == "1"
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            return raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
        {
            return number;
        }

        return raw;
    }

    private static List<string> SplitList(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (char c in value)
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == ',')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString().Trim());
        }

        return parts.Where(part => part.Length > 0).Select(Unquote).ToList();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }

    /// <summary>把 <c>key:value</c> 拆成 (key, op, value)；无运算符返回 null。</summary>
    private static (string Key, string Op, string Value)? ParseTerm(string token)
    {
        int index = 0;
        while (index < token.Length
               && (char.IsAsciiLetterOrDigit(token[index]) || token[index] is '_' or '.'))
        {
            index++;
        }

        if (index == 0 || index >= token.Length)
        {
            return null;
        }

        string key = token[..index];
        string rest = token[index..];
        string op;
        if (rest.StartsWith(">=", StringComparison.Ordinal))
        {
            op = ">=";
            rest = rest[2..];
        }
        else if (rest.StartsWith("<=", StringComparison.Ordinal))
        {
            op = "<=";
            rest = rest[2..];
        }
        else if (rest[0] is ':' or '=' or '>' or '<')
        {
            op = rest[0].ToString();
            rest = rest[1..];
        }
        else
        {
            return null;
        }

        return (key, op, rest);
    }

    /// <summary>按空白切分，保留引号内的空格。</summary>
    private static IEnumerable<string> Tokenize(string input)
    {
        var token = new StringBuilder();
        char quote = '\0';
        foreach (char c in input)
        {
            if (quote != '\0')
            {
                token.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                token.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }

                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
