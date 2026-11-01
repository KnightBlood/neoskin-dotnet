using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 后台 / 用户中心页面的公共变量装配（<c>*.base</c> + 全部共享 composer）。
/// </summary>
public static class PageView
{
    /// <summary>未读通知提供者（组合根注入；对应 Java <c>PageView.InboxProvider</c>）。</summary>
    public static Func<UserRepository.UserRow, IReadOnlyList<object?>>? InboxProvider { get; set; }

    /// <summary>当前公告渲染器（组合根注入）。</summary>
    public static Func<string, string>? AnnouncementProvider { get; set; }

    public static string Announcement(string locale) =>
        AnnouncementProvider?.Invoke(locale) ?? "";

    public static Dictionary<string, object?> Variables(
        Option option, Translator translator, UserRepository.UserRow? user, string locale,
        string host, string requestPath, bool debug, string scope, SessionStore? session)
    {
        string urlRoot = "http://" + host;
        bool darkMode = user?.DarkMode ?? false;

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        SiteViewData.Base(vars, option, locale, darkMode);
        SiteViewData.Head(vars, option, locale, urlRoot, requestPath, debug);
        SiteViewData.Foot(vars, option, locale);
        SiteViewData.Copyright(vars, option);
        SharedLayout.Sidebar(vars, option, darkMode);
        SharedLayout.DarkMode(vars, darkMode);
        SharedLayout.UserPanel(vars, user);
        SiteViewData.UserMenu(vars, user, translator);

        vars["userItems"] = SharedLayout.SideMenu("user", requestPath);
        vars["exploreItems"] = SharedLayout.SideMenu("explore", requestPath);
        vars["adminItems"] = SharedLayout.SideMenu("admin", requestPath);
        SiteViewData.Languages(vars, locale, requestPath);

        vars["notifications"] = user is not null && InboxProvider is not null
            ? InboxProvider(user).ToList()
            : new List<object?>();
        vars["notifications_json"] = PhpJson.Encode(vars["notifications"], 0);
        vars["scope"] = scope;

        vars["view"] = new TemplateViewContext(
            translator, option, locale, session?.Token()?.ToString() ?? "", user, session,
            path => path.StartsWith("http", StringComparison.Ordinal)
                ? path
                : "/" + path.TrimStart('/'));

        return vars;
    }

    /// <summary><c>shared/grid</c> 的列描述。</summary>
    public static PhpArray Column(string css, params string[] widgets) =>
        new PhpArray()
            .With("css", css)
            .With("widgets", widgets.Cast<object?>().ToList());

    public static List<object?> Grid(params List<object?>[] rows) =>
        rows.Cast<object?>().ToList();
}
