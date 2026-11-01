using System.Text;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web.Views;

/// <summary>
/// 后台 / 用户中心 / 皮肤库的通用布局（<c>admin/base</c>、<c>user/base</c>、
/// <c>skinlib/base</c> 的手写等价物）。
///
/// <p>模板引擎接入前，这些页面用简化布局提供前端需要的挂载容器
/// （<c>.content &gt; .container-fluid</c>、<c>#table</c>、<c>#usage-box</c> 等），
/// 由 <c>app.js</c> 挂载 React 组件。</p>
/// </summary>
public static class AppLayout
{
    public static string Admin(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, UserRepository.UserRow? user, SessionStore? session,
        string host, string path, string title, string content,
        string? extraJson = null)
    {
        var body = new StringBuilder();
        body.Append("<div class=\"wrapper\">\n");
        AppendTopNav(body, option, translator, user, true);
        body.Append("  <aside class=\"main-sidebar sidebar-dark-primary elevation-4\">\n")
            .Append("    <a href=\"/admin\" class=\"brand-link\">")
            .Append(PageLayout.Encode(SiteName(option))).Append("</a>\n")
            .Append("    <div class=\"sidebar\"><nav class=\"mt-2\"><ul class=\"nav nav-pills nav-sidebar flex-column\">\n");
        AppendMenu(body, AdminMenu);
        body.Append("    </ul></nav></div>\n  </aside>\n");
        body.Append("  <div class=\"content-wrapper\">\n")
            .Append("    <div class=\"content-header\"><div class=\"container-fluid\">")
            .Append("<h1 class=\"m-0\">").Append(PageLayout.Encode(title))
            .Append("</h1></div></div>\n")
            .Append("    <section class=\"content\">\n")
            .Append("      <div class=\"container-fluid\">\n")
            .Append(content).Append("\n      </div>\n    </section>\n  </div>\n");
        AppendFooter(body, option);
        body.Append("</div>\n");
        return Shell(option, translator, locale, settings, session, host, path,
            title, "hold-transition sidebar-mini", body.ToString(), extraJson);
    }

    public static string User(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, UserRepository.UserRow? user, SessionStore? session,
        string host, string path, string title, string content,
        string? extraJson = null)
    {
        var body = new StringBuilder();
        body.Append("<div class=\"wrapper\">\n");
        AppendTopNav(body, option, translator, user, false);
        body.Append("  <aside class=\"main-sidebar sidebar-dark-primary elevation-4\">\n")
            .Append("    <a href=\"/user\" class=\"brand-link\">")
            .Append(PageLayout.Encode(SiteName(option))).Append("</a>\n")
            .Append("    <div class=\"sidebar\"><nav class=\"mt-2\"><ul class=\"nav nav-pills nav-sidebar flex-column\">\n");
        AppendMenu(body, UserMenu);
        body.Append("    </ul></nav></div>\n  </aside>\n");
        body.Append("  <div class=\"content-wrapper\">\n")
            .Append("    <div class=\"content-header\"><div class=\"container-fluid\">")
            .Append("<h1 class=\"m-0\">").Append(PageLayout.Encode(title))
            .Append("</h1></div></div>\n")
            .Append("    <section class=\"content\">\n")
            .Append("      <div class=\"container-fluid\">\n")
            .Append(content).Append("\n      </div>\n    </section>\n  </div>\n");
        AppendFooter(body, option);
        body.Append("</div>\n");
        return Shell(option, translator, locale, settings, session, host, path,
            title, "hold-transition sidebar-mini", body.ToString(), extraJson);
    }

    public static string Skinlib(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, UserRepository.UserRow? user, SessionStore? session,
        string host, string path, string title, string content,
        string? extraJson = null)
    {
        string siteName = SiteName(option);
        var body = new StringBuilder();
        body.Append("<div class=\"wrapper\">\n")
            .Append("  <nav class=\"main-header navbar navbar-expand ml-0 navbar-")
            .Append(PageLayout.Encode(option.Get("navbar_color", "cyan")?.ToString() ?? "cyan"))
            .Append(" navbar-light\">\n    <div class=\"container\">\n")
            .Append("      <div class=\"navbar-header\"><a href=\"/\" class=\"navbar-brand\">")
            .Append(PageLayout.Encode(siteName)).Append("</a></div>\n")
            .Append("      <ul class=\"nav navbar-nav\"><li class=\"nav-item active\">")
            .Append("<a class=\"nav-link\" href=\"/skinlib\">")
            .Append(PageLayout.Encode(translator.Trans("general.skinlib"))).Append("</a></li>");
        if (user is not null)
        {
            body.Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"/user/closet\">")
                .Append(PageLayout.Encode(translator.Trans("general.my-closet")))
                .Append("</a></li>");
        }

        body.Append("</ul>\n      <ul class=\"nav navbar-nav ml-auto\">");
        if (user is not null)
        {
            body.Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"/skinlib/upload\">")
                .Append(PageLayout.Encode(translator.Trans("skinlib.general.upload-new-skin")))
                .Append("</a></li><li class=\"nav-item\"><a class=\"nav-link\" href=\"/user\">")
                .Append(PageLayout.Encode(user.Nickname ?? user.Email)).Append("</a></li>");
        }
        else
        {
            body.Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"/auth/login\">")
                .Append(PageLayout.Encode(translator.Trans("general.anonymous")))
                .Append("</a></li>");
        }

        body.Append("</ul>\n    </div>\n  </nav>\n")
            .Append(content).Append('\n')
            .Append("  <footer class=\"main-footer\"><div class=\"container\">")
            .Append(PageLayout.Encode(SiteName(option))).Append("</div></footer>\n</div>\n");
        return Shell(option, translator, locale, settings, session, host, path,
            title, "hold-transition layout-top-nav", body.ToString(), extraJson);
    }

    // ================================================================== 片段

    private static void AppendTopNav(StringBuilder body, Option option,
        I18n.Translator translator, UserRepository.UserRow? user, bool admin)
    {
        body.Append("  <nav class=\"main-header navbar navbar-expand navbar-white navbar-light\">\n")
            .Append("    <ul class=\"navbar-nav\">")
            .Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"/\">")
            .Append(PageLayout.Encode(translator.Trans("general.home")))
            .Append("</a></li></ul>\n")
            .Append("    <ul class=\"navbar-nav ml-auto\">");
        if (user is not null)
        {
            body.Append("<li class=\"nav-item\"><span class=\"nav-link\" data-mark=\"nickname\">")
                .Append(PageLayout.Encode(user.Nickname ?? user.Email))
                .Append("</span></li>")
                .Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"#\" id=\"logout-button\">")
                .Append(PageLayout.Encode(translator.Trans("general.logout")))
                .Append("</a></li>");
        }
        else
        {
            body.Append("<li class=\"nav-item\"><a class=\"nav-link\" href=\"/auth/login\">")
                .Append(PageLayout.Encode(translator.Trans("general.login")))
                .Append("</a></li>");
        }

        body.Append("</ul>\n  </nav>\n");
    }

    private static void AppendMenu(StringBuilder body,
        IReadOnlyList<(string TitleKey, string Link)> menu)
    {
        foreach ((string titleKey, string link) in menu)
        {
            body.Append("      <li class=\"nav-item\"><a class=\"nav-link\" href=\"")
                .Append(link).Append("\"><p>").Append(PageLayout.Encode(titleKey))
                .Append("</p></a></li>\n");
        }
    }

    private static void AppendFooter(StringBuilder body, Option option)
    {
        body.Append("  <footer class=\"main-footer\"><div class=\"float-right d-none d-sm-inline\">")
            .Append(PageLayout.Encode(SiteName(option)))
            .Append("</div></footer>\n");
    }

    private static string Shell(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string host, string path,
        string title, string bodyClass, string body, string? extraJson)
    {
        string siteName = SiteName(option);
        string token = session?.Token()?.ToString() ?? "";
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(PageLayout.Encode(locale))
            .Append("\">\n<head>\n")
            .Append(PageLayout.Head(option, translator, locale, settings, token,
                title + " - " + siteName))
            .Append(PageLayout.BlessingScript(option, locale, siteName, "http://" + host,
                path.TrimStart('/'), settings))
            .Append("</head>\n<body class=\"").Append(bodyClass).Append("\">\n")
            .Append(body);
        if (extraJson is not null)
        {
            html.Append("  <script type=\"application/json\" id=\"blessing-extra\">")
                .Append(extraJson).Append("</script>\n");
        }

        html.Append(PageLayout.Foot(option, locale, settings))
            .Append("</body>\n</html>");
        return html.ToString();
    }

    private static string SiteName(Option option) =>
        option.GetLocalized("site_name", "Blessing Skin")?.ToString() ?? "Blessing Skin";

    private static readonly IReadOnlyList<(string TitleKey, string Link)> AdminMenu =
    [
        ("仪表盘", "/admin"),
        ("通知与公告", "/admin/communications"),
        ("用户管理", "/admin/users"),
        ("玩家管理", "/admin/players"),
        ("举报管理", "/admin/reports"),
        ("个性化", "/admin/customize"),
        ("国际化", "/admin/i18n"),
        ("积分设置", "/admin/score"),
        ("站点设置", "/admin/options"),
        ("资源设置", "/admin/resource"),
        ("运行状态", "/admin/status"),
        ("插件管理", "/admin/plugins/manage"),
        ("插件市场", "/admin/plugins/market"),
        ("检查更新", "/admin/update"),
    ];

    private static readonly IReadOnlyList<(string TitleKey, string Link)> UserMenu =
    [
        ("我的面板", "/user"),
        ("站内信", "/user/inbox"),
        ("我的衣柜", "/user/closet"),
        ("角色管理", "/user/player"),
        ("我的举报", "/user/reports"),
        ("个人资料", "/user/profile"),
        ("OAuth 管理", "/user/oauth/manage"),
    ];
}
