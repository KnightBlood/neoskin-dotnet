using NeoSkin.Web.Auth;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Views;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>Illuminate\Routing\ViewController</c>（<c>Route::view</c>）的简化实现。
///
/// <p>模板引擎未接入前，用 <see cref="Views.AppLayout"/> 提供管理端 / 用户中心 /
/// 皮肤库的布局与前端挂载容器。</p>
/// </summary>
public sealed class SimpleViewController(
    Option option,
    UserRepository users,
    Config.NeoSkinSettings settings)
{
    /// <summary>按 uri 返回处理器；未覆盖的视图返回 null（由分发器回 501）。</summary>
    public RequestHandler? Resolve(string uri) => uri switch
    {
        "setup" => Welcome(),
        "setup/info" => SetupInfo(),
        "auth/bind" => Bind(),
        "admin/users" => AdminPage("admin/users", "general.user-manage", true),
        "admin/players" => AdminPage("admin/players", "general.player-manage"),
        "admin/reports" => AdminPage("admin/reports", "general.report-manage"),
        "admin/i18n" => AdminPage("admin/i18n", "general.i18n"),
        "admin/plugins/manage" => AdminPage("admin/plugins", "general.plugin-manage"),
        "admin/plugins/market" => AdminPage("admin/market", "general.plugin-market"),
        "user/oauth/manage" => UserOAuth(),
        "skinlib" => Skinlib(),
        _ => null,
    };

    // ================================================================== 布局页

    private RequestHandler AdminPage(string template, string titleKey,
        bool currentUserExtra = false) => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context),
            context.Request.Path.Value ?? "/" + template, settings.Debug, "admin",
            Session(context));
        vars["pageTitle"] = Translator(context).Trans(titleKey) ?? titleKey;
        vars["errors"] = TemplateErrors.Empty;
        if (currentUserExtra)
        {
            vars["extra_json"] = PhpJson.Encode(new PhpArray()
                .With("currentUser", users.FindJsonById(user.Uid)), 0);
        }

        await WriteAsync(context, 200, TemplateRenderer.Render(template, vars));
    };

    private RequestHandler Skinlib() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context), context.Request.Path.Value ?? "/skinlib",
            settings.Debug, "skinlib", Session(context));
        vars["pageTitle"] = Translator(context).Trans("general.skinlib") ?? "皮肤库";
        vars["errors"] = TemplateErrors.Empty;
        vars["extra_json"] = PhpJson.Encode(new PhpArray()
            .With("currentUid", user?.Uid), 0);
        await WriteAsync(context, 200, TemplateRenderer.Render("skinlib/index", vars));
    };

    private RequestHandler UserOAuth() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context),
            context.Request.Path.Value ?? "/user/oauth/manage", settings.Debug, "user",
            Session(context));
        vars["pageTitle"] = Translator(context).Trans("general.oauth-manage") ?? "OAuth 管理";
        vars["errors"] = TemplateErrors.Empty;
        await WriteAsync(context, 200, TemplateRenderer.Render("user/oauth", vars));
    };

    // ================================================================== setup / bind

    private RequestHandler Welcome() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> vars = SetupView.Variables(option, Translator(context),
            Locale(context), Host(context), context.Request.Path.Value ?? "/setup",
            Session(context), settings);
        await WriteAsync(context, 200,
            SetupView.Render("setup/wizard/welcome", vars));
    };

    private RequestHandler SetupInfo() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> vars = SetupView.Variables(option, Translator(context),
            Locale(context), Host(context), context.Request.Path.Value ?? "/setup/info",
            Session(context), settings);
        await WriteAsync(context, 200, SetupView.Render("setup/wizard/info", vars));
    };

    private RequestHandler Bind() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        string html = AuthPage.Bind(option, Translator(context), Locale(context), settings,
            Session(context), user, Host(context),
            context.Request.Path.Value ?? "/auth/bind");
        await WriteAsync(context, 200, html);
    };

    // ================================================================== 辅助

    private UserRepository.UserRow? SessionUser(HttpContext context)
    {
        SessionStore? session = Session(context);
        return session is null ? null : new SessionGuard(session, users).User();
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string CsrfToken(HttpContext context) =>
        Session(context)?.Token()?.ToString() ?? "";

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;

    private static string Field(string name, string label, string value,
        string type = "text") =>
        "<label>" + Encode(label) + " <input type=\"" + type + "\" name=\"" + name
        + "\" value=\"" + Encode(value) + "\"></label><br>";

    private static string Page(string title, string content) =>
        "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>" + Encode(title)
        + "</title></head><body><h1>" + Encode(title) + "</h1>" + content + "</body></html>";

    private static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private static async Task WriteAsync(HttpContext context, int status, string html)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }
}
