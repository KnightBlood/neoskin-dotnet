using NeoSkin.Web.Auth;
using System.Text.Json;
using NeoSkin.Web.Db;
using NeoSkin.Web.Error;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;

namespace NeoSkin.Web.Middleware;

/// <summary>
/// 认证相关中间件（源 <c>App\Http\Middleware\*</c>）：
/// <c>Authenticate</c>（auth:web）、<c>RejectBannedUser</c>、<c>EnsureEmailFilled</c>、
/// <c>RedirectIfAuthenticated</c>（guest）、<c>CheckRole</c>（role:*）、
/// <c>CheckUserVerified</c>（verified）。
/// </summary>
public static class AuthMiddleware
{
    /// <summary>当前登录用户在 <c>Items</c> 里的键。</summary>
    public const string CurrentUserKey = "neoskin.current_user";

    /// <summary>Passport 认证结果（Bearer token / transient cookie）的键。</summary>
    public const string OAuthResultKey = "neoskin.oauth_result";

    /// <summary><c>last_requested_path</c>（登录成功后跳回）。</summary>
    public const string LastRequestedPath = "last_requested_path";

    private static readonly IReadOnlyDictionary<string, int> Roles = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["banned"] = UserRepository.Banned,
        ["normal"] = UserRepository.Normal,
        ["admin"] = UserRepository.Admin,
        ["super-admin"] = UserRepository.SuperAdmin,
    };

    // ================================================================== Authenticate

    /// <summary><c>auth:web</c>：未登录 JSON → 401，HTML → 302 到登录页并写会话。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> Authenticate(
        UserRepository users, TranslationFileLoader loader,
        Service.OAuth.OAuthService? oauth = null) => async (context, parameters, next) =>
    {
        // auth:oauth：Bearer token / Passport transient cookie（源 Passport 的 oauth guard）
        if (string.Equals(parameters, "oauth", StringComparison.Ordinal))
        {
            Service.OAuth.OAuthService.AuthResult? result = oauth?.Authenticate(context);
            if (result is null)
            {
                await DenyAsync(context, null);
                return;
            }

            context.Items[OAuthResultKey] = result;
            if (result.User is not null)
            {
                context.Items[CurrentUserKey] = result.User;
            }

            await next();
            return;
        }

        SessionStore? session = Session(context);
        if (session is null)
        {
            await DenyAsync(context, null);
            return;
        }

        var guard = new SessionGuard(session, users);
        UserRepository.UserRow? user;
        try
        {
            user = guard.User();
        }
        catch (System.Data.Common.DbException)
        {
            // 表不存在（未安装）等价于未登录
            user = null;
        }
        if (user is null)
        {
            await DenyAsync(context, session);
            return;
        }

        context.Items[CurrentUserKey] = user;
        await next();
    };

    private static async Task DenyAsync(HttpContext context, SessionStore? session)
    {
        if (ContentNegotiation.ExpectsJson(context))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"message\":\"Unauthenticated.\"}");
            return;
        }

        if (session is not null)
        {
            session.SetPreviousUrl(FullUrl(context));
            session.Put(LastRequestedPath, FullUrl(context));
            session.Put("msg", Translate(context, "auth.check.anonymous"));
        }

        Redirect(context, "/auth/login");
        await Task.CompletedTask;
    }

    // ================================================================== RejectBannedUser

    /// <summary><c>RejectBannedUser</c>：封禁用户 403，但 <c>auth.logout</c> 放行。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> RejectBannedUser(
        TranslationFileLoader loader) => async (context, parameters, next) =>
    {
        if (context.Items["route.name"] as string == "auth.logout")
        {
            await next();
            return;
        }

        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null || user.Permission != UserRepository.Banned)
        {
            await next();
            return;
        }

        string message = Translate(context, "auth.check.banned");
        if (ContentNegotiation.ExpectsJson(context))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"code\":-1,\"message\":"
                + Support.PhpJson.Encode(message) + "}");
            return;
        }

        await WriteForbiddenAsync(context, message);
    };

    // ================================================================== EnsureEmailFilled

    /// <summary>
    /// <c>EnsureEmailFilled</c>：邮箱已填不允许进 <c>auth/bind</c>；邮箱空不允许去别处。
    /// </summary>
    public static Func<HttpContext, string?, Func<Task>, Task> EnsureEmailFilled() =>
        async (context, parameters, next) =>
        {
            UserRepository.UserRow? user = CurrentUser(context);
            if (user is null)
            {
                await next();
                return;
            }

            bool filled = !string.IsNullOrEmpty(user.Email);
            bool isBind = IsBindPath(context.Request.Path.Value);

            if (filled && isBind)
            {
                Redirect(context, "/user");
                return;
            }

            if (!filled && !isBind)
            {
                Redirect(context, "/auth/bind");
                return;
            }

            await next();
        };

    internal static bool IsBindPath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        string normalized = path.TrimEnd('/').TrimStart('/');
        return normalized == "auth/bind";
    }

    // ================================================================== RedirectIfAuthenticated

    /// <summary><c>guest</c>：已登录 → 302 <c>/user</c>。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> RedirectIfAuthenticated() =>
        async (context, parameters, next) =>
        {
            SessionStore? session = Session(context);
            if (session is not null && new SessionGuard(session, null).Check())
            {
                Redirect(context, "/user");
                return;
            }

            await next();
        };

    // ================================================================== CheckRole

    /// <summary><c>role:*</c>：<c>permission &lt; 阈值</c> → 403（message 为空）。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> CheckRole() =>
        async (context, parameters, next) =>
        {
            if (string.IsNullOrEmpty(parameters))
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(
                    "Too few arguments to function CheckRole::handle(): 2 passed, 3 expected");
                return;
            }

            UserRepository.UserRow? user = CurrentUser(context);
            int permission = user?.Permission ?? -1;
            bool deny = Roles.TryGetValue(parameters, out int threshold)
                ? permission < threshold
                : permission < 0;

            if (deny)
            {
                if (ContentNegotiation.ExpectsJson(context))
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync("{\"message\":\"\"}");
                    return;
                }

                context.Response.StatusCode = 403;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>403 Forbidden</title>"
                    + "</head><body><h1>403 Forbidden</h1></body></html>");
                return;
            }

            await next();
        };

    // ================================================================== CheckUserVerified

    /// <summary><c>verified</c>：<c>require_verification</c> 打开且用户未验证 → 403。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> CheckUserVerified(Option option) =>
        async (context, parameters, next) =>
        {
            if (!option.GetBoolean("require_verification", false))
            {
                await next();
                return;
            }

            UserRepository.UserRow? user = CurrentUser(context);
            if (user is null || user.Verified)
            {
                await next();
                return;
            }

            string message = Translate(context, "auth.check.verified");
            if (ContentNegotiation.ExpectsJson(context))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"message\":" + Support.PhpJson.Encode(message) + "}");
                return;
            }

            await WriteForbiddenAsync(context, message);
        };

    // ================================================================== 安装锁

    /// <summary><c>CheckInstallation</c>：已安装（存在 install.lock）→ 显示「已安装」页。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> CheckInstallation(
        Paths.AppPaths paths, Service.Option? option = null,
        Config.NeoSkinSettings? settings = null) => async (context, parameters, next) =>
    {
        if (File.Exists(paths.InstallLock))
        {
            if (option is not null && settings is not null)
            {
                Translator translator = context.Items["translator"] as Translator
                    ?? new Translator(new TranslationFileLoader(), "en", "en");
                string locale = context.Items["locale"] as string ?? Locales.AppLocale;
                SessionStore? currentSession =
                    context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
                string host = context.Request.Host.Value is { Length: > 0 } h
                    ? h
                    : "localhost";
                Dictionary<string, object?> vars = View.SetupView.Variables(option,
                    translator, locale, host, context.Request.Path.Value ?? "/setup",
                    currentSession, settings);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    View.SetupView.Render("setup/locked", vars));
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>Already Installed</title>"
                + "</head><body><h1>已安装</h1><p>本站已经安装完成。如需重新安装，"
                + "请删除 <code>storage/install.lock</code>。</p></body></html>");
            return;
        }

        await next();
    };

    /// <summary><c>RedirectToSetup</c>：未安装且访问的不是 <c>setup*</c> → 302 <c>/setup</c>。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> RedirectToSetup(
        Paths.AppPaths paths) => async (context, parameters, next) =>
    {
        bool isSetup = (context.Request.Path.Value ?? "/").TrimStart('/')
            .StartsWith("setup", StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(paths.InstallLock) && !isSetup)
        {
            Redirect(context, "/setup");
            return;
        }

        await next();
    };

    // ================================================================== Passport scopes

    /// <summary><c>scope:*</c>（CheckTokenForAnyScope）：任一 scope 满足即可。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> CheckTokenForAnyScope() =>
        CheckToken(anyScope: true);

    /// <summary><c>scopes:*</c>（CheckToken）：全部 scope 都要满足。</summary>
    public static Func<HttpContext, string?, Func<Task>, Task> CheckToken() =>
        CheckToken(anyScope: false);

    private static Func<HttpContext, string?, Func<Task>, Task> CheckToken(bool anyScope) =>
        async (context, parameters, next) =>
        {
            if (context.Items[OAuthResultKey] is not Service.OAuth.OAuthService.AuthResult result)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("{\"message\":\"Unauthenticated.\"}");
                return;
            }

            var required = new List<string>();
            if (!string.IsNullOrEmpty(parameters))
            {
                foreach (string part in parameters.Split(','))
                {
                    string scope = part.Trim();
                    if (scope.Length > 0)
                    {
                        required.Add(scope);
                    }
                }
            }

            bool allowed = anyScope
                ? required.Any(result.Can)
                : required.All(result.Can);
            if (!allowed)
            {
                await WriteScopeDeniedAsync(context);
                return;
            }

            await next();
        };

    private static async Task WriteScopeDeniedAsync(HttpContext context)
    {
        const string Message = "Invalid scope(s) provided.";
        context.Response.StatusCode = 403;
        context.Response.Headers.CacheControl = "no-cache, private";

        if (ContentNegotiation.ExpectsJson(context))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(ErrorPayload.Json(Message));
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>403 Forbidden</title>"
            + "</head><body><h1>403 Forbidden</h1><p>" + Message + "</p></body></html>");
    }

    // ================================================================== 辅助

    /// <summary>当前登录用户（未登录为 null）。</summary>
    public static UserRepository.UserRow? CurrentUser(HttpContext context) =>
        context.Items[CurrentUserKey] as UserRepository.UserRow;

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string Translate(HttpContext context, string key) =>
        context.Items["translator"] as Translator is { } translator
            ? translator.Trans(key)
            : key;

    private static void Redirect(HttpContext context, string path)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = path;
        context.Response.Headers.CacheControl = "no-cache, private";
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>403 Forbidden</title>"
            + "</head><body><h1>403 Forbidden</h1><p>"
            + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>");
    }

    private static string FullUrl(HttpContext context)
    {
        string scheme = context.Request.Scheme is { Length: > 0 } value ? value : "http";
        return scheme + "://" + context.Request.Host
            + (context.Request.Path + context.Request.QueryString);
    }
}
