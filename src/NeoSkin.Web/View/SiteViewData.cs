using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 全部视图共享的变量装配（对应 Java <c>SharedViewData</c> 与各 view composer）：
/// <c>shared/head</c>、<c>shared/foot</c>、<c>shared/copyright</c>、
/// <c>shared/languages</c>、<c>shared/user-menu</c>。
/// </summary>
public static class SiteViewData
{
    private static readonly string[] LightColors = ["light", "warning", "white", "orange"];

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

    /// <summary><c>ViewServiceProvider</c> 的匿名 composer。</summary>
    public static void Base(Dictionary<string, object?> vars, Option option, string locale,
        bool darkMode)
    {
        vars["site_name"] = option.GetLocalized("site_name", "Blessing Skin");
        string color = Cast(option.Get("navbar_color", "cyan"), "cyan");
        vars["navbar_color"] = color;
        vars["color_mode"] = LightColors.Contains(color) ? "light" : "dark";
        vars["dark_mode"] = darkMode;
        vars["locale"] = locale.Replace('_', '-');
    }

    /// <summary><c>HeadComposer</c> 的变量。</summary>
    public static void Head(Dictionary<string, object?> vars, Option option, string locale,
        string urlRoot, string requestPath, bool debug)
    {
        string color = Cast(option.Get("navbar_color", "cyan"), "cyan");
        vars["favicon"] = Favicon(option);
        vars["theme_color"] = ThemeColors.GetValueOrDefault(color, "");

        vars["seo"] = new PhpArray()
            .With("keywords", FalsyToNull(option.Get("meta_keywords")))
            .With("description", FalsyToNull(option.Get("meta_description")))
            .With("extra", FalsyToNull(option.Get("meta_extras")));
        vars["links"] = PluginFilters.ApplyList("head_links", new List<object?>(),
            PluginHost.PluginRuntime.Current);
        vars["inline_css"] = FalsyToNull(option.Get("custom_css"));

        string? cdnHost = FalsyToNull(option.Get("cdn_address"))?.ToString();
        vars["custom_cdn_host"] = cdnHost;
        vars["has_cdn_host"] = !string.IsNullOrWhiteSpace(cdnHost);
        vars["blessing"] = Blessing(option, locale, urlRoot, requestPath);
        vars["extra_head"] = null;
        vars["assets"] = AssetManifest.Urls(debug, Cast(option.Get("cdn_address", ""), ""));
    }

    /// <summary><c>FootComposer</c> 的变量。</summary>
    public static void Foot(Dictionary<string, object?> vars, Option option, string locale)
    {
        vars["i18n"] = "/lang/" + locale + ".js";
        vars["scripts"] = PluginFilters.ApplyList("scripts", new List<object?>(),
            PluginHost.PluginRuntime.Current);
        vars["inline_js"] = FalsyToNull(option.Get("custom_js"));
        vars["extra_foot"] = null;
    }

    /// <summary><c>shared.copyright</c> 的匿名 composer。</summary>
    public static void Copyright(Dictionary<string, object?> vars, Option option)
    {
        vars["copyright"] = option.GetLocalized("copyright_prefer", 0);
        vars["custom_copyright"] = CopyrightText(option);
        vars["site_url"] = option.Get("site_url");
    }

    /// <summary><c>LanguagesMenuComposer</c>：<c>current</c> + <c>langs</c>。</summary>
    public static void Languages(Dictionary<string, object?> vars, string locale,
        string requestPath)
    {
        vars["current"] = Locales.DisplayName(locale) ?? locale;
        string path = LaravelPath(requestPath);
        var langs = new PhpArray();
        foreach ((string id, IReadOnlyDictionary<string, string> info) in Locales.Available)
        {
            string url = "/" + (path == "/" ? "" : path) + "?lang=" + PhpUrl.UrlEncode(id);
            langs.With(id, new PhpArray()
                .With("name", info.GetValueOrDefault("name", id))
                .With("short_name", info.GetValueOrDefault("short_name", id))
                .With("url", url));
        }

        vars["langs"] = langs;
    }

    /// <summary><c>UserMenuComposer</c> 的变量（未登录时给出空值）。</summary>
    public static void UserMenu(Dictionary<string, object?> vars, UserRepository.UserRow? user,
        Translator translator)
    {
        if (user is null)
        {
            vars["user"] = null;
            vars["user_nickname"] = null;
            vars["avatar"] = "";
            vars["avatar_png"] = "";
            vars["panel_avatar"] = "";
            vars["panel_avatar_png"] = "";
            vars["panel_nickname"] = null;
            vars["panel_email"] = null;
            vars["menu"] = new List<object?>();
            vars["notifications"] = new List<object?>();
            vars["notifications_json"] = "[]";
            return;
        }

        string avatar = PluginFilters.ApplyString("user_avatar",
            "/avatar/" + user.Avatar + "?size=36", PluginHost.PluginRuntime.Current);
        string avatarPng = PluginFilters.ApplyString("user_avatar",
            "/avatar/" + user.Avatar + "?size=36&png=true", PluginHost.PluginRuntime.Current);
        string nickname = user.Nickname ?? user.Email;

        vars["user"] = user;
        vars["user_nickname"] = nickname;
        vars["avatar"] = avatar;
        vars["avatar_png"] = avatarPng;
        vars["panel_avatar"] = avatar;
        vars["panel_avatar_png"] = avatarPng;
        vars["panel_nickname"] = user.Nickname;
        vars["panel_email"] = user.Email;
        vars["notifications"] = new List<object?>();
        vars["notifications_json"] = "[]";

        var menu = new List<object?>
        {
            MenuItem(translator, "general.user-center", "/user"),
            MenuItem(translator, "general.profile", "/user/profile"),
        };
        if (user.IsAdmin)
        {
            menu.Add(Divider());
            menu.Add(MenuItem(translator, "general.admin-panel", "/admin"));
            menu.Add(MenuItem(translator, "general.user-manage", "/admin/users"));
            menu.Add(MenuItem(translator, "general.report-manage", "/admin/reports"));
            menu.Add(MenuItem(null, "Web CLI", "#launch-cli"));
        }

        vars["menu"] = PluginFilters.ApplyList("user_menu", menu,
            PluginHost.PluginRuntime.Current);
    }

    private static PhpArray MenuItem(Translator? translator, string label, string link) =>
        new PhpArray()
            .With("label", translator?.Trans(label) ?? label)
            .With("link", link);

    private static PhpArray Divider() => new PhpArray().With("link", "#divider");

    /// <summary><c>HeadComposer::serializeGlobals</c>（模板里的 <c>${blessing}</c>）。</summary>
    public static PhpArray Blessing(Option option, string locale, string urlRoot,
        string requestPath) =>
        new PhpArray()
            .With("version", "6.0.11_patch")
            .With("locale", locale)
            .With("base_url", urlRoot)
            .With("site_name", option.GetLocalized("site_name", "Blessing Skin"))
            .With("route", LaravelPath(requestPath));

    /// <summary><c>HeadComposer::addFavicon</c>。</summary>
    public static string Favicon(Option option)
    {
        string url = Cast(option.Get("favicon_url", "app/favicon.ico"), "app/favicon.ico");
        return url.StartsWith("http", StringComparison.Ordinal) ? url : "/" + url;
    }

    /// <summary>Laravel <c>request()-&gt;path()</c>：根是 <c>/</c>，其余不带前导斜杠。</summary>
    public static string LaravelPath(string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || requestPath == "/")
        {
            return "/";
        }

        return requestPath.TrimStart('/');
    }

    private static string? CopyrightText(Option option)
    {
        object? raw = option.GetLocalized("copyright_text", null);
        if (!Truthy(raw))
        {
            return null;
        }

        return Cast(raw, "")
            .Replace("{site_name}",
                Cast(option.GetLocalized("site_name", "Blessing Skin"), ""))
            .Replace("{site_url}", Cast(option.Get("site_url", ""), ""));
    }

    public static bool Truthy(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        string text = value.ToString() ?? "";
        return text.Length > 0 && text != "0";
    }

    public static object? FalsyToNull(object? value) => Truthy(value) ? value : null;

    public static object? PhpOr(object? left, object? right) => Truthy(left) ? left : right;

    public static string Cast(object? value, string fallback) =>
        value is null ? fallback : value.ToString() ?? fallback;
}
