using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;

namespace NeoSkin.Web.Error;

/// <summary>
/// 错误页渲染中枢（对应 Java <c>ErrorPages</c>）：
/// 403/404/500/503 → <c>errors/base</c>；401/402/419/429 → <c>errors/minimal</c>；
/// 其余（如 405）→ <c>errors/symfony</c>。
/// </summary>
public sealed class ErrorPages(Option option, Config.NeoSkinSettings settings)
{
    private static readonly int[] ProjectPages = [403, 404, 500, 503];

    private static readonly IReadOnlyDictionary<int, string> MinimalPages =
        new Dictionary<int, string>
        {
            [401] = "Unauthorized",
            [402] = "Payment Required",
            [419] = "Page Expired",
            [429] = "Too Many Requests",
        };

    public async Task RenderAsync(HttpContext context, int status, string? detail)
    {
        Translator translator = Translator(context);
        string locale = Locale(context);
        SessionStore? session = Session(context);
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        string host = context.Request.Host.Value is { Length: > 0 } h ? h : "localhost";
        string path = context.Request.Path.Value ?? "/";

        string html;
        if (ProjectPages.Contains(status))
        {
            html = ProjectPage(status, detail, translator, locale, host, path, session, user);
        }
        else if (MinimalPages.TryGetValue(status, out string? title))
        {
            html = MinimalPage(status, title, translator, locale, session, user);
        }
        else
        {
            html = SymfonyPage(status);
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private string ProjectPage(int status, string? detail, Translator translator,
        string locale, string host, string path, SessionStore? session,
        UserRepository.UserRow? user)
    {
        string title = status + " " + HttpStatus.Text(status);
        string message = string.IsNullOrEmpty(detail)
            ? translator.Trans("errors.http.msg-" + status) ?? ""
            : detail;
        string text = translator.Trans("errors.exception.detail",
            new Dictionary<string, object?> { ["msg"] = message }) ?? "";

        Dictionary<string, object?> vars = BaseVariables(translator, locale, host, path,
            session, user);
        vars["title"] = title;
        vars["subtitle"] = title;
        vars["message"] = "\n  <p>\n    " + TemplateValues.EscapeHtml(text)
                                   + "\n  </p>\n";

        return TemplateRenderer.Render("errors/base", vars);
    }

    private Dictionary<string, object?> BaseVariables(Translator translator, string locale,
        string host, string path, SessionStore? session, UserRepository.UserRow? user)
    {
        string urlRoot = "http://" + host;
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        string cdnBase = "";
        try
        {
            SiteViewData.Base(vars, option, locale, false);
            cdnBase = SiteViewData.Cast(option.Get("cdn_address", ""), "");
        }
        catch (Exception)
        {
            // 数据库不可用时错误页仍要能渲染
            vars["site_name"] = "NeoSkin";
            vars["navbar_color"] = "cyan";
            vars["color_mode"] = "dark";
            vars["dark_mode"] = false;
            vars["locale"] = locale.Replace('_', '-');
        }

        vars["styles"] = new List<object?>();
        vars["scripts"] = new List<object?>();
        vars["assets"] = AssetManifest.Urls(settings.Debug, cdnBase);
        vars["view"] = new TemplateViewContext(
            translator, option, locale, session?.Token()?.ToString() ?? "", user, session,
            p => p.StartsWith("http", StringComparison.Ordinal) ? p : "/" + p.TrimStart('/'));

        string requestPath = SiteViewData.LaravelPath(path);
        var langs = new PhpArray();
        foreach ((string id, IReadOnlyDictionary<string, string> info) in Locales.Available)
        {
            string url = "/" + (requestPath == "/" ? "" : requestPath) + "?lang="
                         + PhpUrl.UrlEncode(id);
            langs.With(id, new PhpArray()
                .With("name", info.GetValueOrDefault("name", id))
                .With("short_name", info.GetValueOrDefault("short_name", id))
                .With("url", url)
                .With("css", "mx-2 p-1 label label-secondary"));
        }

        vars["langs"] = langs;
        return vars;
    }

    private string MinimalPage(int status, string title, Translator translator, string locale,
        SessionStore? session, UserRepository.UserRow? user)
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["code"] = status.ToString(),
            ["message"] = title,
            ["view"] = new TemplateViewContext(translator, option, locale, "", user, session),
        };

        return TemplateRenderer.Render("errors/minimal", vars);
    }

    private static string SymfonyPage(int status)
    {
        string statusText = HttpStatus.Text(status);
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["statusCode"] = status.ToString(),
            ["statusText"] = statusText,
            ["heading"] = "The server returned a \"" + status + " " + statusText + "\".",
        };

        return TemplateRenderer.Render("errors/symfony", vars);
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;
}
