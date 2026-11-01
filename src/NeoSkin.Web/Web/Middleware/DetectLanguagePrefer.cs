using NeoSkin.Web.Http;
using NeoSkin.Web.I18n;

namespace NeoSkin.Web.Middleware;

/// <summary>
/// <c>App\Http\Middleware\DetectLanguagePrefer</c> 的移植。
///
/// <p>源文件里有一个<b>必须原样保留的缺陷</b>：<c>$locale ?? app()-&gt;getLocale();</c>
/// 少了赋值，所以三个来源都为空时走的是 <c>fallback_locale</c>（<c>en</c>），
/// 而不是 <c>app.locale</c>（<c>zh_CN</c>）。<b>不要「修」它</b>。</p>
///
/// <p>另外三个坑：</p>
/// <list type="number">
///   <item><c>??</c> 只跳过 null，所以 <c>?lang=</c>（空串）会挡住 cookie 与
///     Accept-Language，最后落到 fallback；</item>
///   <item>别名大小写敏感：<c>zh-Hans-CN → zh_Hans_CN</c> 不等于 <c>zh_HANS_CN</c>，
///     于是落回 fallback；</item>
///   <item><c>api</c> 路由（中间件里含 <c>api</c>）不回写 cookie，但解析照做。</item>
/// </list>
/// </summary>
public static class DetectLanguagePrefer
{
    public const string CookieName = "locale";

    /// <summary><c>$response-&gt;cookie('locale', $locale, 120)</c>。</summary>
    public const int CookieLifetimeMinutes = 120;

    public sealed record Input(
        string? Lang,
        string? CookieLocale,
        string? AcceptLanguage,
        IReadOnlyList<string>? RouteMiddleware,
        string? InitialLocale);

    public sealed record Cookie(
        string Name,
        string Value,
        int MaxAgeSeconds,
        string Path,
        bool HttpOnly,
        bool Secure,
        string SameSite,
        string? Domain);

    public sealed record Result(string Locale, Cookie? Cookie);

    public static Result Resolve(Input input)
    {
        string? locale = input.Lang
            ?? input.CookieLocale
            ?? (input.AcceptLanguage is null
                ? null
                : AcceptLanguage.GetPreferredLanguage(input.AcceptLanguage));

        locale = Locales.Resolve(locale);

        // 空语句 "$locale ?? app()->getLocale();" —— 故意不做任何事
        if (!Locales.Has(locale))
        {
            locale = Locales.FallbackLocale;
        }

        bool apiRoute = input.RouteMiddleware?.Contains("api") == true;
        return new Result(locale, apiRoute ? null : MakeCookie(locale));
    }

    /// <summary><c>CookieJar::make('locale', $locale, 120)</c> 的等价物。</summary>
    public static Cookie MakeCookie(string locale) =>
        new(CookieName, locale, CookieLifetimeMinutes * 60, "/", true, false, "lax", null);
}
