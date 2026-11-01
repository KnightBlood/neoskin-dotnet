using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NeoSkin.Web.Service.Crypto;

namespace NeoSkin.Web.Middleware;

/// <summary>
/// Laravel <c>PreventRequestForgery</c>（13.x 起取代 <c>VerifyCsrfToken</c>，语义未变）。
///
/// <p>放行判据按源顺序：</p>
/// <list type="number">
///   <item><c>isReading</c>：HEAD / GET / OPTIONS 一律放行；</item>
///   <item><c>inExceptArray</c>：URI 命中 except 清单（支持 <c>*</c>）；</item>
///   <item><c>hasValidOrigin</c>：<c>Sec-Fetch-Site: same-origin</c> 直接放行
///     （<c>same-site</c> 只在 <c>allowSameSite</c> 时放行）；</item>
///   <item><c>tokensMatch</c>：请求 token 与会话 <c>_token</c> 定长比较。</item>
/// </list>
///
/// <p>取 token 的顺序：<c>input('_token')</c> → <c>X-CSRF-TOKEN</c> →
/// <c>X-XSRF-TOKEN</c>（先解密，再去 <c>CookieValuePrefix</c>）。</p>
/// </summary>
public static class PreventRequestForgery
{
    public const string XsrfCookieName = "XSRF-TOKEN";

    private static readonly List<string> NeverVerify = [];

    private static bool _allowSameSite;
    private static bool _originOnly;

    /// <summary><c>except($uris)</c>：注册永不校验的 URI（<c>*</c> 通配）。</summary>
    public static void Except(IEnumerable<string> uris)
    {
        foreach (string uri in uris)
        {
            if (!NeverVerify.Contains(uri))
            {
                NeverVerify.Add(uri);
            }
        }
    }

    public static void AllowSameSite(bool allow) => _allowSameSite = allow;

    public static void UseOriginOnly(bool only) => _originOnly = only;

    /// <summary><c>flushState()</c>（测试用）。</summary>
    public static void FlushState()
    {
        NeverVerify.Clear();
        _allowSameSite = false;
        _originOnly = false;
    }

    /// <summary>一次校验需要的请求快照。</summary>
    public sealed record Request(
        string Method,
        string Path,
        string? SecFetchSite,
        string? InputToken,
        string? CsrfHeader,
        string? XsrfHeader);

    public sealed record Verdict(bool Allowed, string Reason);

    /// <summary><c>handle()</c> 的判定（不含响应 cookie 环节）。</summary>
    public static Verdict Verify(Request request, string? sessionToken,
        Func<string, string?>? decryptXcsrf)
    {
        if (IsReading(request.Method))
        {
            return new Verdict(true, "reading");
        }

        if (InExceptArray(request.Path))
        {
            return new Verdict(true, "except");
        }

        if (request.SecFetchSite == "same-origin")
        {
            return new Verdict(true, "origin");
        }

        if (request.SecFetchSite == "same-site" && _allowSameSite)
        {
            return new Verdict(true, "origin-same-site");
        }

        if (_originOnly)
        {
            return new Verdict(false, "origin-mismatch");
        }

        string? token = TokenFromRequest(request, decryptXcsrf);
        if (sessionToken is not null && token is not null && HashEquals(sessionToken, token))
        {
            return new Verdict(true, "token");
        }

        return new Verdict(false, "mismatch");
    }

    /// <summary><c>isReading()</c>：HEAD / GET / OPTIONS。</summary>
    public static bool IsReading(string method) =>
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>inExceptArray()</c>：<c>trim($except, '/')</c> 后做 <c>Str::is()</c> 匹配。</summary>
    internal static bool InExceptArray(string path)
    {
        // Laravel 的 $request->is() 用「不带前导斜杠」的 path 比较
        string normalized = path.TrimStart('/');
        foreach (string except in NeverVerify)
        {
            string pattern = except == "/" ? except : except.Trim('/');
            if (Is(pattern, normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Laravel <c>Str::is($pattern, $value)</c>：<c>*</c> 匹配任意串。</summary>
    internal static bool Is(string pattern, string value)
    {
        if (pattern == value)
        {
            return true;
        }

        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.Singleline);
    }

    /// <summary><c>getTokenFromRequest()</c>。</summary>
    public static string? TokenFromRequest(Request request, Func<string, string?>? decryptXcsrf)
    {
        string? token = IsTruthy(request.InputToken) ? request.InputToken : request.CsrfHeader;

        if (!IsTruthy(token) && IsTruthy(request.XsrfHeader) && decryptXcsrf is not null)
        {
            string? decrypted = decryptXcsrf(request.XsrfHeader!);
            token = decrypted is null ? "" : CookieValuePrefix.Remove(decrypted);
        }

        return token;
    }

    /// <summary>PHP 的 <c>?:</c> 真值判定（只关心字符串）。</summary>
    private static bool IsTruthy(string? value) =>
        value is not null && value.Length > 0 && value != "0";

    /// <summary><c>hash_equals()</c>：定长比较。</summary>
    internal static bool HashEquals(string known, string user) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(known), Encoding.UTF8.GetBytes(user));

    /// <summary><c>XSRF-TOKEN</c> cookie 的形状。</summary>
    public sealed record XsrfCookie(string Name, string Value, long MaxAgeSeconds, string Path,
        string? Domain, bool Secure, bool HttpOnly, string SameSite);

    /// <summary><c>newCookie()</c>：值与会话 token 相同，有效期 lifetime*60 秒。</summary>
    public static XsrfCookie? BuildXsrfCookie(string? sessionToken, bool addHttpCookie,
        int lifetimeMinutes, string path, string? domain, bool secure, string sameSite)
    {
        if (_originOnly || !addHttpCookie)
        {
            return null;
        }

        return sessionToken is null
            ? null
            : new XsrfCookie(XsrfCookieName, sessionToken, (long)lifetimeMinutes * 60,
                path, domain, secure, false, sameSite);
    }


}
