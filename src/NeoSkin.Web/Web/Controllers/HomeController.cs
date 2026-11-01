using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Views;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\HomeController</c> 的移植。
///
/// <p>已落地 <c>apiRoot</c> 与首页 <c>index</c>（由 <c>home.html</c> 模板渲染）。</p>
/// </summary>
public sealed class HomeController(
    Option option,
    UserRepository users,
    Config.NeoSkinSettings settings)
{
    private static readonly string[] Copyrights =
    [
        "Powered with ❤ by Blessing Skin Server.",
        "Powered by Blessing Skin Server.",
        "Proudly powered by Blessing Skin Server.",
        "由 Blessing Skin Server 强力驱动。",
        "采用 Blessing Skin Server 搭建。",
        "使用 Blessing Skin Server 稳定运行。",
        "自豪地采用 Blessing Skin Server。",
    ];

    /// <summary><c>GET api</c>。</summary>
    public RequestHandler ApiRoot() => async (context, route, parameters) =>
    {
        object? prefer = option.GetLocalized("copyright_prefer", 0);
        object? copyright = prefer is not null && int.TryParse(prefer.ToString(), out int index)
                            && index >= 0 && index < Copyrights.Length
            ? Copyrights[index]
            : null;

        var body = new PhpArray()
            .With("blessing_skin", "6.0.11_patch")
            .With("spec", 0)
            .With("copyright", copyright)
            .With("site_name", option.Get("site_name"));

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    };

    /// <summary><c>GET /</c>：首页（SPA 外壳）。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        UserRepository.UserRow? user;
        try
        {
            user = session is null ? null : new Auth.SessionGuard(session, users).User();
        }
        catch (System.Data.Common.DbException)
        {
            user = null;
        }

        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        string html = HomePage.Render(option, Translator(context), Locale(context), user, host,
            context.Request.Path.Value ?? "/", settings,
            session?.Token()?.ToString() ?? "", session);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;
}
