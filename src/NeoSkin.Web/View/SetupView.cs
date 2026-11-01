using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 安装向导（<c>setup/*</c>）的公共变量：locale / view / cdn_base / errors /
/// 语言菜单（对应 Java <c>SetupPages.variables</c>）。
/// </summary>
public static class SetupView
{
    public static Dictionary<string, object?> Variables(Option option, Translator translator,
        string locale, string host, string requestPath, SessionStore? session,
        Config.NeoSkinSettings settings)
    {
        string urlRoot = "http://" + host;
        var view = new TemplateViewContext(
            translator, option, locale, session?.Token()?.ToString() ?? "", null, session,
            path => path.StartsWith("http", StringComparison.Ordinal)
                ? path
                : "/" + path.TrimStart('/'));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["locale"] = locale,
            ["view"] = view,
            ["cdn_base"] = SiteViewData.FalsyToNull(option.Get("cdn_address"))?.ToString() ?? "",
            ["site_name"] = option.Get("site_name", "Blessing Skin"),
            ["version"] = settings.Version,
            ["errors"] = TemplateErrors.Empty,
        };

        vars["current"] = Locales.DisplayName(locale) ?? locale;
        string path = SiteViewData.LaravelPath(requestPath);
        var langs = new PhpArray();
        foreach ((string id, IReadOnlyDictionary<string, string> info) in Locales.Available)
        {
            string url = "/" + (path == "/" ? "" : path) + "?lang=" + PhpUrl.UrlEncode(id);
            langs.With(id, new PhpArray()
                .With("name", info.GetValueOrDefault("name", id))
                .With("short_name", info.GetValueOrDefault("short_name", id))
                .With("url", url)
                .With("css", "mx-2 p-1 label label-secondary"));
        }

        vars["langs"] = langs;
        return vars;
    }

    public static string Render(string template, Dictionary<string, object?> vars) =>
        TemplateRenderer.Render(template, vars);
}
