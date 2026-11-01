namespace NeoSkin.Web.Cookie;

/// <summary>
/// 直接写 <c>Set-Cookie</c> 头（不做 URL 编码）。
///
/// <p>ASP.NET Core 的 <c>Response.Cookies.Append</c> 会对值做百分号编码，
/// 而 Laravel/Symfony 的 cookie 值（加密后的 base64）是原样写出的；
/// 为了与 PHP 的 cookie 字节一致，这里手工拼头。</p>
/// </summary>
public static class CookieWriter
{
    public static void Append(HttpContext context, string name, string value, string path,
        bool httpOnly, bool secure, string sameSite, long? maxAgeSeconds = null)
    {
        var parts = new List<string>
        {
            name + "=" + value,
            "Path=" + (string.IsNullOrEmpty(path) ? "/" : path),
        };

        if (maxAgeSeconds is not null)
        {
            parts.Add("Max-Age=" + maxAgeSeconds.Value);
        }

        if (httpOnly)
        {
            parts.Add("HttpOnly");
        }

        if (secure)
        {
            parts.Add("Secure");
        }

        parts.Add(sameSite.ToLowerInvariant() switch
        {
            "strict" => "SameSite=Strict",
            "none" => "SameSite=None",
            _ => "SameSite=Lax",
        });

        context.Response.Headers.Append("Set-Cookie", string.Join("; ", parts));
    }
}
