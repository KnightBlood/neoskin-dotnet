using NeoSkin.Web.Cookie;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Middleware;

/// <summary>
/// <c>EncryptCookies</c> 的请求侧中间件：把请求 cookie 解密后放进
/// <see cref="EncryptCookies.ContextKey"/>，供 <c>StartSession</c> / CSRF 使用。
/// </summary>
public sealed class EncryptCookiesMiddleware(RequestDelegate next, EncryptCookies cookies)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var raw = new PhpArray();
        foreach (KeyValuePair<string, string> cookie in context.Request.Cookies)
        {
            raw.Put(cookie.Key, cookie.Value);
        }

        context.Items[EncryptCookies.ContextKey] = cookies.Decrypt(raw);
        await next(context);
    }
}

/// <summary>
/// Laravel <c>StartSession</c> 的 ASP.NET Core 接线。
///
/// <p>请求侧：从（已解密的）cookie 取会话 id → <see cref="SessionStore"/> →
/// <c>start()</c> → 放进 <c>Items["session"]</c>。</p>
/// <p>响应侧：<c>OnStarting</c> 里 <c>save()</c> 并回写会话 cookie
/// （path=/、HttpOnly、SameSite=Lax、Max-Age=lifetime*60，值用与
/// <c>EncryptCookies</c> 相同的方式加密）。</p>
/// </summary>
public sealed class StartSessionMiddleware(
    RequestDelegate next,
    ISessionHandler handler,
    SessionCookieConfig config,
    EncryptCookies? cookies = null)
{
    public const string SessionKey = "session";

    private readonly SessionCookieConfig _config = config.Normalized();

    public async Task InvokeAsync(HttpContext context)
    {
        string? id = null;
        if (context.Items[EncryptCookies.ContextKey] is PhpArray decrypted
            && decrypted.Get(_config.Name) is string value)
        {
            id = value;
        }

        SessionStore store = SessionStore.Begin(_config.Name, handler, id);
        store.Start();
        context.Items[SessionKey] = store;

        context.Response.OnStarting(() =>
        {
            StoreCurrentUrl(context, store);
            store.Save();
            QueueSessionCookie(context, store);
            return Task.CompletedTask;
        });

        await next(context);
    }

    /// <summary><c>StartSession::storeCurrentUrl()</c>。</summary>
    private static void StoreCurrentUrl(HttpContext context, SessionStore store)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return;
        }

        if (IsAjax(context) || IsPrefetch(context) || IsPrecognitive(context))
        {
            return;
        }

        store.SetPreviousUrl(FullUrl(context));
        if (context.Items["route.name"] is string routeName)
        {
            store.Put("_previous.route", routeName);
        }
    }

    private static bool IsAjax(HttpContext context) =>
        string.Equals(context.Request.Headers["X-Requested-With"], "XMLHttpRequest",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsPrefetch(HttpContext context)
    {
        string purpose = context.Request.Headers["X-Purpose"].ToString();
        if (purpose.Length == 0)
        {
            purpose = context.Request.Headers["Purpose"].ToString();
        }

        return purpose.Contains("prefetch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrecognitive(HttpContext context) =>
        context.Request.Headers.ContainsKey("X-Precognition");

    private static string FullUrl(HttpContext context)
    {
        string scheme = context.Request.Scheme;
        string host = context.Request.Host.Value ?? "localhost";
        string uri = context.Request.Path + context.Request.QueryString;
        return scheme + "://" + host + (uri.Length == 0 ? "/" : uri);
    }

    private void QueueSessionCookie(HttpContext context, SessionStore store)
    {
        string value = store.Id;
        if (cookies is not null && !cookies.IsDisabled(_config.Name))
        {
            value = cookies.Encrypt(_config.Name, value);
        }

        CookieWriter.Append(context, _config.Name, value, _config.Path, true, _config.Secure,
            _config.SameSite, _config.MaxAgeSeconds);
    }

    internal static SameSiteMode ParseSameSite(string? value) => (value ?? "").ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax,
    };
}

/// <summary>
/// Laravel <c>PreventRequestForgery</c> 的 ASP.NET Core 接线（在 <c>StartSession</c> 之后）。
/// 请求侧不通过回 419；响应侧补 <c>XSRF-TOKEN</c> cookie（不加密）。
/// </summary>
public sealed class VerifyCsrfTokenMiddleware(
    RequestDelegate next,
    Service.Crypto.PhpEncrypter? encrypter,
    int lifetimeMinutes = 120)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items[StartSessionMiddleware.SessionKey] is not SessionStore session)
        {
            await next(context);
            return;
        }

        string? sessionToken = session.Token() is null
            ? null
            : PhpString.Cast(session.Token());

        string? inputToken = await ReadInputTokenAsync(context);
        var request = new PreventRequestForgery.Request(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } site ? site : null,
            inputToken,
            context.Request.Headers["X-CSRF-TOKEN"].ToString() is { Length: > 0 } csrf ? csrf : null,
            context.Request.Headers["X-XSRF-TOKEN"].ToString() is { Length: > 0 } xsrf ? xsrf : null);

        PreventRequestForgery.Verdict verdict = PreventRequestForgery.Verify(request, sessionToken,
            header =>
            {
                if (encrypter is null)
                {
                    return null;
                }

                try
                {
                    // 只解密，不去前缀（getTokenFromRequest 统一做）
                    object? plain = encrypter.Decrypt(header, false);
                    return plain is null ? null : PhpString.Cast(plain);
                }
                catch (Service.Crypto.DecryptException)
                {
                    return null;
                }
            });

        if (!verdict.Allowed)
        {
            context.Response.StatusCode = 419;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"code\":419,\"message\":\"CSRF token mismatch.\"}");
            return;
        }

        context.Response.OnStarting(() =>
        {
            // 在响应阶段重新取 token：handler 里的 SessionGuard::login() 会 migrate()
            // 重新生成 CSRF token，用请求开始时捕获的值会种下旧 token（419）。
            string? currentToken = session.Token() is null
                ? null
                : PhpString.Cast(session.Token());
            PreventRequestForgery.XsrfCookie? cookie = PreventRequestForgery.BuildXsrfCookie(
                currentToken, true, lifetimeMinutes, "/", null, false, "lax");

            if (cookie is not null)
            {
                CookieWriter.Append(context, cookie.Name, cookie.Value, cookie.Path, false,
                    cookie.Secure, "lax", cookie.MaxAgeSeconds);
            }

            return Task.CompletedTask;
        });

        await next(context);
    }

    private static async Task<string?> ReadInputTokenAsync(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            string? token = form["_token"];
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        string? query = context.Request.Query["_token"];
        return string.IsNullOrEmpty(query) ? null : query;
    }
}
