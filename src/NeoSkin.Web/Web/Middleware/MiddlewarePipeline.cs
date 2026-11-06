using NeoSkin.Web.Cookie;
using NeoSkin.Web.Http;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Session;

namespace NeoSkin.Web.Middleware;

/// <summary>
/// 路由级中间件执行器：<see cref="MiddlewareChain.Expand"/> 展开后的 token 逐个执行。
///
/// <p>尚未移植的中间件按 Laravel 的「空实现」语义穿透（当前主力是 cookie /
/// session / CSRF / 语言四条；其余随对应控制器补齐）。</p>
/// </summary>
public sealed class MiddlewarePipeline
{
    private readonly Dictionary<string, Func<HttpContext, string?, Func<Task>, Task>> _handlers =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Registered => _handlers.Keys;

    public MiddlewarePipeline Add(string name, Func<HttpContext, Func<Task>, Task> handler) =>
        Add(name, (context, parameters, next) => handler(context, next));

    public MiddlewarePipeline Add(string name,
        Func<HttpContext, string?, Func<Task>, Task> handler)
    {
        _handlers[name] = handler;
        return this;
    }

    /// <summary>
    /// 默认装配：cookie 解密、会话、CSRF、语言检测。
    /// <paramref name="cookies"/> / <paramref name="session"/> 为 null 时跳过对应中间件。
    /// </summary>
    public static MiddlewarePipeline CreateDefault(
        EncryptCookies? cookies,
        ISessionHandler? session,
        SessionCookieConfig sessionConfig,
        PhpEncrypter? encrypter,
        string initialLocale,
        UserRepository? users = null,
        Option? option = null,
        TranslationFileLoader? loader = null,
        Service.OAuth.OAuthService? oauth = null,
        Paths.AppPaths? paths = null,
        Config.NeoSkinSettings? settings = null,
        Service.UpdateRunner? updates = null)
    {
        var pipeline = new MiddlewarePipeline();

        if (cookies is not null)
        {
            pipeline.Add("Illuminate\\Cookie\\Middleware\\EncryptCookies", (ctx, next) =>
                new EncryptCookiesMiddleware(_ => next(), cookies).InvokeAsync(ctx));
        }

        if (session is not null)
        {
            pipeline.Add("Illuminate\\Session\\Middleware\\StartSession", (ctx, next) =>
                new StartSessionMiddleware(_ => next(), session, sessionConfig, cookies).InvokeAsync(ctx));
        }

        if (session is not null && encrypter is not null)
        {
            pipeline.Add("Illuminate\\Foundation\\Http\\Middleware\\PreventRequestForgery", (ctx, next) =>
                new VerifyCsrfTokenMiddleware(_ => next(), encrypter).InvokeAsync(ctx));
        }

        pipeline.Add("App\\Http\\Middleware\\DetectLanguagePrefer",
            (ctx, next) => LanguageAsync(ctx, next, cookies, initialLocale, loader));

        if (loader is not null && users is not null)
        {
            pipeline.Add("App\\Http\\Middleware\\Authenticate",
                AuthMiddleware.Authenticate(users, loader, oauth));
            pipeline.Add("App\\Http\\Middleware\\RejectBannedUser", AuthMiddleware.RejectBannedUser(loader));
            pipeline.Add("App\\Http\\Middleware\\EnsureEmailFilled", AuthMiddleware.EnsureEmailFilled());
            pipeline.Add("App\\Http\\Middleware\\RedirectIfAuthenticated", AuthMiddleware.RedirectIfAuthenticated());
            pipeline.Add("App\\Http\\Middleware\\FireUserAuthenticated", (ctx, next) => next());
            pipeline.Add("App\\Http\\Middleware\\CheckRole", AuthMiddleware.CheckRole());
            if (option is not null)
            {
                pipeline.Add("App\\Http\\Middleware\\CheckUserVerified",
                    AuthMiddleware.CheckUserVerified(option));
            }

            if (paths is not null)
            {
                pipeline.Add("App\\Http\\Middleware\\RedirectToSetup",
                    AuthMiddleware.RedirectToSetup(paths, updates));
                pipeline.Add("App\\Http\\Middleware\\CheckInstallation",
                    AuthMiddleware.CheckInstallation(paths));
            }

            pipeline.Add("Laravel\\Passport\\Http\\Middleware\\CheckTokenForAnyScope",
                AuthMiddleware.CheckTokenForAnyScope());
            pipeline.Add("Laravel\\Passport\\Http\\Middleware\\CheckToken",
                AuthMiddleware.CheckToken());

            if (oauth is not null)
            {
                pipeline.Add("Laravel\\Passport\\Http\\Middleware\\CreateFreshApiToken",
                    (ctx, next) => CreateFreshApiTokenAsync(ctx, next, oauth, users));
            }
        }

        return pipeline;
    }

    /// <summary>
    /// Passport 的 <c>CreateFreshApiToken</c>：已登录的 web 请求下发
    /// <c>laravel_token</c>（HS256 + 会话 csrf）。
    /// </summary>
    private static async Task CreateFreshApiTokenAsync(HttpContext context, Func<Task> next,
        Service.OAuth.OAuthService oauth, UserRepository users)
    {
        SessionStore? store = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        if (store is not null)
        {
            // 必须在 handler 跑完之后再取 uid / csrf：登录会 migrate 会话并重新生成 token，
            // 请求开始时捕获的旧 token 会让 laravel_token 的 csrf 与 XSRF-TOKEN 不一致。
            context.Response.OnStarting(() =>
            {
                int? uid = new Auth.SessionGuard(store, users).Id();
                if (uid is null)
                {
                    return Task.CompletedTask;
                }

                string csrf = store.Token()?.ToString() ?? "";
                (string value, int maxAge) = oauth.TransientCookie(uid.Value, csrf, 120);
                if (value.Length > 0)
                {
                    context.Response.Headers.Append("Set-Cookie",
                        Service.OAuth.OAuthService.CookieName + "=" + value
                        + "; Path=/; Max-Age=" + maxAge
                        + "; HttpOnly; SameSite=Lax");
                }

                return Task.CompletedTask;
            });
        }

        await next();
    }

    /// <summary>按展开后的执行序跑中间件，最后调用 <paramref name="terminal"/>。</summary>
    public async Task RunAsync(HttpContext context, Web.RouteDefinition route, Func<Task> terminal)
    {
        List<string> tokens = MiddlewareChain.Expand(route.Middleware);
        Func<Task> next = terminal;

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            int colon = tokens[i].IndexOf(':');
            string name = colon < 0 ? tokens[i] : tokens[i][..colon];
            string? parameters = colon < 0 ? null : tokens[i][(colon + 1)..];
            if (_handlers.TryGetValue(name, out var handler))
            {
                Func<Task> captured = next;
                next = () => handler(context, parameters, captured);
            }
        }

        await next();
    }

    // ================================================================== 语言

    private static async Task LanguageAsync(HttpContext context, Func<Task> next,
        EncryptCookies? cookies, string initialLocale, TranslationFileLoader? loader)
    {
        // 源里 DetectLanguagePrefer 属于全局栈，包在 web 组外面：此时
        // EncryptCookies 还没解密，所以读到的就是**明文** cookie（写的时候也明文）。
        string? cookieLocale = context.Request.Cookies.TryGetValue(
            DetectLanguagePrefer.CookieName, out string? raw) ? raw : null;

        // 区分「没有 ?lang=」与「?lang=（空串）」：后者按源站语义会挡住 cookie/Accept
        string? lang = context.Request.Query.TryGetValue("lang", out var langValues)
            ? langValues.ToString()
            : null;

        var input = new DetectLanguagePrefer.Input(
            lang,
            cookieLocale,
            context.Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } accept ? accept : null,
            context.Items["route.middleware"] as IReadOnlyList<string>,
            initialLocale);

        DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(input);
        context.Items["locale"] = result.Locale;
        Service.Option.CurrentLocale = result.Locale;
        if (loader is not null)
        {
            context.Items["translator"] = new I18n.Translator(loader, result.Locale, Locales.FallbackLocale);
        }

        if (result.Cookie is not null)
        {
            context.Response.OnStarting(() =>
            {
                bool apiRoute = (context.Items["route.middleware"] as IReadOnlyList<string>)
                    ?.Contains("api") == true;
                if (!apiRoute)
                {
                    CookieWriter.Append(context, result.Cookie.Name, result.Cookie.Value,
                        result.Cookie.Path, result.Cookie.HttpOnly, result.Cookie.Secure, "lax",
                        result.Cookie.MaxAgeSeconds);
                }

                return Task.CompletedTask;
            });
        }

        await next();
    }
}
