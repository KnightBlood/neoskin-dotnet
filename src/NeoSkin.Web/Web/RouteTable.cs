namespace NeoSkin.Web.Web;

/// <summary>一条命中的路由（含路径参数）。</summary>
public sealed record RouteMatch(RouteDefinition Route, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// 路由表的匹配器：路径命中 + 方法命中，并按 Laravel 的语义算 405 的 <c>Allow</c>。
/// </summary>
public sealed class RouteTable
{
    /// <summary>Laravel <c>Router::$verbs</c> 的顺序（<c>Allow</c> 头按它排序）。</summary>
    public static readonly IReadOnlyList<string> Verbs =
        ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "QUERY"];

    private readonly IReadOnlyList<RouteDefinition> _routes;

    public RouteTable(Routes routes) => _routes = routes.All;

    /// <summary>
    /// 按注册顺序找第一条「路径 + 方法」都命中的路由（Laravel 的匹配顺序）。
    /// <paramref name="method"/> 是 HTTP 方法（大小写不敏感）。
    /// </summary>
    public RouteMatch? Match(string method, string laravelPath)
    {
        string verb = method.ToLowerInvariant();
        foreach (RouteDefinition route in _routes)
        {
            if (!route.MatchMethods.Contains(verb))
            {
                continue;
            }

            IReadOnlyDictionary<string, string>? parameters = route.MatchPath(laravelPath);
            if (parameters is not null)
            {
                return new RouteMatch(route, parameters);
            }
        }

        return null;
    }

    /// <summary>
    /// 该路径命中的<b>全部</b>方法（按 <see cref="Verbs"/> 排序）。
    /// 空表表示路径不存在（404）。
    /// </summary>
    public IReadOnlyList<string> AllowedMethods(string laravelPath)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (RouteDefinition route in _routes)
        {
            if (route.MatchPath(laravelPath) is not null)
            {
                foreach (string method in route.Methods)
                {
                    found.Add(method.ToUpperInvariant());
                }
            }
        }

        return Verbs.Where(found.Contains).ToList();
    }

    /// <summary>是否有任意路由匹配该路径（与方法无关）。</summary>
    public bool PathExists(string laravelPath) =>
        _routes.Any(route => route.MatchPath(laravelPath) is not null);

    /// <summary>
    /// Laravel <c>Request::path()</c>：剥尾斜杠、剥前导斜杠，根为 <c>"/"</c>。
    /// </summary>
    public static string LaravelPath(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath) || rawPath == "/")
        {
            return "/";
        }

        string trimmed = rawPath.TrimEnd('/').TrimStart('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>剥掉尾斜杠后是否仍含 <c>//</c>（源站对这种路径返回 404）。</summary>
    public static bool HasInnerDoubleSlash(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return false;
        }

        string trimmed = rawPath.TrimEnd('/');
        return trimmed.Contains("//", StringComparison.Ordinal);
    }
}
