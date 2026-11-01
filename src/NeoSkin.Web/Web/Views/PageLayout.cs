using System.Text;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web.Views;

/// <summary>
/// 页面公共片段（<c>shared/head</c> / <c>shared/foot</c>）的手写等价物。
///
/// <p>正式模板引擎接入前，认证页与简单页面共用这里的 head/foot 生成。</p>
/// </summary>
public static class PageLayout
{
    private static readonly IReadOnlyDictionary<string, string> ThemeColors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary"] = "#007bff",
            ["secondary"] = "#6c757d",
            ["success"] = "#28a745",
            ["warning"] = "#ffc107",
            ["danger"] = "#dc3545",
            ["navy"] = "#001f3f",
            ["olive"] = "#3d9970",
            ["lime"] = "#01ff70",
            ["fuchsia"] = "#f012be",
            ["maroon"] = "#d81b60",
            ["indigo"] = "#6610f2",
            ["purple"] = "#6f42c1",
            ["pink"] = "#e83e8c",
            ["orange"] = "#fd7e14",
            ["teal"] = "#20c997",
            ["cyan"] = "#17a2b8",
            ["gray"] = "#6c757d",
        };

    public static string Head(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, string csrfToken, string title)
    {
        string siteName = option.GetLocalized("site_name", "Blessing Skin")?.ToString()
            ?? "Blessing Skin";
        string navbarColor = option.Get("navbar_color", "cyan")?.ToString() ?? "cyan";
        string themeColor = ThemeColors.TryGetValue(navbarColor, out string? color)
            ? color
            : "";
        string favicon = Favicon(option);
        IReadOnlyDictionary<string, string> assets =
            AssetManifest.Urls(settings.Debug, option.Get("cdn_address", "")?.ToString());


        var html = new StringBuilder();
        html.Append("  <meta charset=\"utf-8\">\n")
            .Append("  <meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n")
            .Append("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no\">\n")
            .Append("  <meta name=\"csrf-token\" content=\"").Append(Encode(csrfToken))
            .Append("\">\n")
            .Append("  <meta name=\"theme-color\" content=\"").Append(Encode(themeColor))
            .Append("\">\n");

        AppendMeta(html, "keywords", option.Get("meta_keywords"));
        AppendMeta(html, "description", option.Get("meta_description"));
        if (Truthy(option.Get("meta_extras")))
        {
            html.Append("  ").Append(option.Get("meta_extras")).Append('\n');
        }


        html.Append("  <link rel=\"stylesheet\" href=\"").Append(Encode(assets["style"]))
            .Append("\" crossorigin=\"anonymous\">\n");
        html.Append("  <link rel=\"shortcut icon\" href=\"").Append(Encode(favicon)).Append("\">\n");
        html.Append("  <link rel=\"icon\" type=\"image/png\" href=\"").Append(Encode(favicon))
            .Append("\" sizes=\"192x192\">\n");
        html.Append("  <link rel=\"apple-touch-icon\" href=\"").Append(Encode(favicon))
            .Append("\" sizes=\"180x180\">\n");
        if (Truthy(option.Get("custom_css")))
        {
            html.Append("  <style>").Append(option.Get("custom_css"))
                .Append("</style>\n");
        }

        html.Append("  <title>").Append(Encode(title)).Append("</title>\n");
        return html.ToString();
    }

    public static string Foot(Option option, string locale,
        Config.NeoSkinSettings settings)
    {
        IReadOnlyDictionary<string, string> assets =
            AssetManifest.Urls(settings.Debug, option.Get("cdn_address", "")?.ToString());
        var html = new StringBuilder();
        html.Append("  <script src=\"/lang/").Append(Encode(locale))
            .Append(".js\" crossorigin=\"anonymous\"></script>\n");
        html.Append("  <script src=\"").Append(Encode(assets["app"]))
            .Append("\" crossorigin=\"anonymous\"></script>\n");
        if (Truthy(option.Get("custom_js")))
        {
            html.Append("  <script>").Append(option.Get("custom_js"))
                .Append("</script>\n");
        }

        return html.ToString();
    }

    /// <summary><c>window.blessing</c>（route 由调用方传入）。</summary>
    public static string BlessingScript(Option option, string locale, string siteName,
        string baseUrl, string route, Config.NeoSkinSettings settings)
    {
        var blessing = new PhpArray()
            .With("version", settings.Version)
            .With("locale", locale)
            .With("base_url", baseUrl)
            .With("site_name", siteName)
            .With("route", route);
        return "  <script>/*<![CDATA[*/ window.blessing = "
               + PhpJson.Encode(blessing, 0) + "; /*]]>*/</script>\n";
    }

    public static string Favicon(Option option)
    {
        string url = option.Get("favicon_url", "app/favicon.ico")?.ToString() ?? "app/favicon.ico";
        return url.StartsWith("http", StringComparison.Ordinal) ? url : "/" + url;
    }

    public static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    public static bool Truthy(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value is bool b)
        {
            return b;
        }

        string s = value.ToString() ?? "";
        return s.Length > 0 && s != "0";
    }

    private static void AppendMeta(StringBuilder html, string name, object? value)
    {
        if (Truthy(value))
        {
            html.Append("  <meta name=\"").Append(name).Append("\" content=\"")
                .Append(Encode(value?.ToString())).Append("\">\n");
        }
    }


}
