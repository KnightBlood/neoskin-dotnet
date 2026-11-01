namespace NeoSkin.Web.Middleware;

/// <summary>
/// Laravel <c>bootstrap/app.php</c> 的中间件装配 → 执行序（纯逻辑部分）。
///
/// <p>源结构：</p>
/// <list type="bullet">
///   <item>全局栈：Maintenance、ValidatePostSize、TrimStrings、ConvertEmptyStringsToNull、DetectLanguagePrefer；</item>
///   <item>web 组：EncryptCookies、AddQueuedCookiesToResponse、StartSession、
///     ShareErrorsFromSession、PreventRequestForgery、EnforceEverGreen、RedirectToSetup、
///     SubstituteBindings、（Passport）CreateFreshApiToken；</item>
///   <item>api 组：SubstituteBindings；</item>
///   <item>authorize 组：auth:web、RejectBannedUser、EnsureEmailFilled、FireUserAuthenticated；</item>
///   <item>别名：auth/guest/role/setup/verified/scope/scopes/throttle/cache.headers。</item>
/// </list>
///
/// <p><see cref="Expand"/> 产出<b>展开后的执行序</b>（组深度扁平化，保持书写顺序）。</p>
/// </summary>
public static class MiddlewareChain
{
    /// <summary>bootstrap/app.php 的全局栈（顺序敏感）。</summary>
    public static readonly IReadOnlyList<string> Global =
    [
        "Illuminate\\Foundation\\Http\\Middleware\\PreventRequestsDuringMaintenance",
        "Illuminate\\Foundation\\Http\\Middleware\\ValidatePostSize",
        "Illuminate\\Foundation\\Http\\Middleware\\TrimStrings",
        "App\\Http\\Middleware\\ConvertEmptyStringsToNull",
        "App\\Http\\Middleware\\DetectLanguagePrefer",
    ];

    /// <summary>组定义：组名 → 展开（组可以嵌组，如 authorize 里的 auth:web）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Groups =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["web"] =
            [
                "Illuminate\\Cookie\\Middleware\\EncryptCookies",
                "Illuminate\\Cookie\\Middleware\\AddQueuedCookiesToResponse",
                "Illuminate\\Session\\Middleware\\StartSession",
                "Illuminate\\View\\Middleware\\ShareErrorsFromSession",
                "Illuminate\\Foundation\\Http\\Middleware\\PreventRequestForgery",
                "App\\Http\\Middleware\\EnforceEverGreen",
                "App\\Http\\Middleware\\RedirectToSetup",
                "Illuminate\\Routing\\Middleware\\SubstituteBindings",
                // Passport 的 PassportServiceProvider 会把它追加到 web 组
                "Laravel\\Passport\\Http\\Middleware\\CreateFreshApiToken",
            ],
            ["api"] = ["Illuminate\\Routing\\Middleware\\SubstituteBindings"],
            ["authorize"] =
            [
                "auth:web",
                "App\\Http\\Middleware\\RejectBannedUser",
                "App\\Http\\Middleware\\EnsureEmailFilled",
                "App\\Http\\Middleware\\FireUserAuthenticated",
            ],
        };

    /// <summary>别名 → FQCN（具名参数留在 token 里，如 <c>role:admin</c>）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth"] = "App\\Http\\Middleware\\Authenticate",
            ["guest"] = "App\\Http\\Middleware\\RedirectIfAuthenticated",
            ["role"] = "App\\Http\\Middleware\\CheckRole",
            ["setup"] = "App\\Http\\Middleware\\CheckInstallation",
            ["verified"] = "App\\Http\\Middleware\\CheckUserVerified",
            ["scope"] = "Laravel\\Passport\\Http\\Middleware\\CheckTokenForAnyScope",
            ["scopes"] = "Laravel\\Passport\\Http\\Middleware\\CheckToken",
            ["throttle"] = "Illuminate\\Routing\\Middleware\\ThrottleRequests",
            ["cache.headers"] = "Illuminate\\Http\\Middleware\\SetCacheHeaders",
        };

    private static readonly HashSet<string> GroupTokens = new(StringComparer.Ordinal)
    {
        "web", "api", "authorize",
    };

    /// <summary>展开一条路由的中间件序：全局栈 + 路由条目（组展开、别名改写）。</summary>
    public static List<string> Expand(IEnumerable<string> routeMiddleware)
    {
        var output = new List<string>(Global);
        var queue = new LinkedList<string>(routeMiddleware);

        while (queue.Count > 0)
        {
            string token = queue.First!.Value;
            queue.RemoveFirst();

            if (GroupTokens.Contains(token))
            {
                // 组按定义展开（反向插到队首，等价 Laravel 的深度扁平化）
                IReadOnlyList<string> group = Groups[token];
                for (int i = group.Count - 1; i >= 0; i--)
                {
                    queue.AddFirst(group[i]);
                }

                continue;
            }

            output.Add(Canonical(token));
        }

        return output;
    }

    /// <summary>别名改写 + 参数保留：<c>role:admin</c> → <c>CheckRole:admin</c>。</summary>
    public static string Canonical(string token)
    {
        string name = token;
        string? parameters = null;

        int colon = token.IndexOf(':');
        if (colon >= 0)
        {
            name = token[..colon];
            parameters = token[(colon + 1)..];
        }

        string fqcn = Aliases.TryGetValue(name, out string? alias) ? alias : name;
        return parameters is null ? fqcn : fqcn + ":" + parameters;
    }

    /// <summary>路由里的组名（用于判断「本路由属于哪一组」）。</summary>
    public static IReadOnlyList<string> GroupsOf(IEnumerable<string> routeMiddleware) =>
        routeMiddleware.Where(GroupTokens.Contains).ToList();
}
