using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoSkin.Web.Web;

/// <summary>
/// 单条路由定义（来自 <c>Resources/codegraph/routes.json</c>，174 条）。
/// <c>method</c> 在源数据里是 <c>GET|HEAD</c> 这样的组合，这里拆成小写集合。
/// </summary>
public sealed class RouteDefinition
{
    private RouteDefinition(IReadOnlyList<string> methods, string uri, string? name,
        string action, IReadOnlyList<string> middleware, Regex pattern)
    {
        Methods = methods;
        Uri = uri;
        Name = name;
        Action = action;
        Middleware = middleware;
        Pattern = pattern;
    }

    public IReadOnlyList<string> Methods { get; }

    public string Uri { get; }

    public string? Name { get; }

    public string Action { get; }

    public IReadOnlyList<string> Middleware { get; }

    public Regex Pattern { get; }

    public static RouteDefinition Create(string method, string uri, string? name, string action,
        IEnumerable<string> middleware) =>
        new(SplitMethods(method), uri, name, action, middleware.ToList(), ToPattern(uri));

    /// <summary><c>"GET|HEAD"</c> → <c>["get", "head"]</c>。</summary>
    public static IReadOnlyList<string> SplitMethods(string combined) =>
        combined.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.ToLowerInvariant())
            .ToList();

    /// <summary>路由的匹配方法集合（<c>query</c> 按 GET 语义处理，与 Java 版一致）。</summary>
    public IEnumerable<string> MatchMethods =>
        Methods.Select(m => m == "query" ? "get" : m);

    /// <summary>
    /// Laravel uri → 正则：<c>{name}</c> 与 <c>{name?}</c> 翻成 <c>(?&lt;name&gt;[^/]+)</c>，
    /// 其余字符按字面转义；根路由是 <c>^/$</c>。
    /// </summary>
    public static Regex ToPattern(string uri)
    {
        if (string.IsNullOrEmpty(uri) || uri == "/")
        {
            return new Regex("^/$", RegexOptions.CultureInvariant);
        }

        string raw = uri.StartsWith('/') ? uri[1..] : uri;
        var regex = new System.Text.StringBuilder("^");
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '{')
            {
                int close = raw.IndexOf('}', i);
                if (close < 0)
                {
                    regex.Append(Regex.Escape(raw[i..]));
                    break;
                }

                string name = raw[(i + 1)..close].TrimEnd('?');
                regex.Append("(?<").Append(Sanitize(name)).Append(">[^/]+)");
                i = close + 1;
                continue;
            }

            regex.Append(Regex.Escape(c.ToString()));
            i++;
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    private static string Sanitize(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }

    /// <summary>在 Laravel path 上匹配，返回路径参数（未命中返回 null）。</summary>
    public IReadOnlyDictionary<string, string>? MatchPath(string laravelPath)
    {
        Match match = Pattern.Match(laravelPath);
        if (!match.Success)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string group in Pattern.GetGroupNames())
        {
            if (group is not ("0" or "1") && match.Groups[group].Success)
            {
                parameters[group] = match.Groups[group].Value;
            }
        }

        return parameters;
    }
}

/// <summary>
/// 路由表的加载器：权威数据是 <c>codegraph/routes.json</c>（174 条；源 173 条
/// + 源码漏记的 <c>POST skinlib</c>）。不得增删改路径、方法或参数。
/// </summary>
public sealed class Routes
{
    private readonly List<RouteDefinition> _all;
    private readonly Dictionary<string, List<RouteDefinition>> _byAction = new(StringComparer.Ordinal);

    private Routes(List<RouteDefinition> all)
    {
        _all = all;
        foreach (RouteDefinition definition in all)
        {
            if (!_byAction.TryGetValue(definition.Action, out List<RouteDefinition>? list))
            {
                list = [];
                _byAction[definition.Action] = list;
            }

            list.Add(definition);
        }
    }

    /// <summary>从 <c>Resources/codegraph/routes.json</c> 加载（随构建复制到输出目录）。</summary>
    public static Routes Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "codegraph", "routes.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Resources/codegraph/routes.json 未随构建复制");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var definitions = new List<RouteDefinition>(document.RootElement.GetArrayLength());
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            string method = entry.GetProperty("method").GetString() ?? "";
            string uri = entry.GetProperty("uri").GetString() ?? "";
            string? name = entry.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
            string action = entry.GetProperty("action").GetString() ?? "";
            var middleware = new List<string>();
            if (entry.TryGetProperty("middleware", out JsonElement middlewareElement)
                && middlewareElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in middlewareElement.EnumerateArray())
                {
                    middleware.Add(item.GetString() ?? "");
                }
            }

            definitions.Add(RouteDefinition.Create(method, uri, name, action, middleware));
        }

        return new Routes(definitions);
    }

    public IReadOnlyList<RouteDefinition> All => _all;

    public int Count => _all.Count;

    public IReadOnlyList<RouteDefinition> ByAction(string action) =>
        _byAction.TryGetValue(action, out List<RouteDefinition>? list) ? list : [];

    public IEnumerable<string> Actions => _byAction.Keys;
}
