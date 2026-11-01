using NeoSkin.Web.Db;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 后台 / 用户中心共享布局的变量装配（对应 Java <c>SharedLayout</c>）：
/// 侧边栏颜色、暗色模式、用户面板与三段侧边菜单。
/// </summary>
public static class SharedLayout
{
    public static void Sidebar(Dictionary<string, object?> vars, Option option, bool darkMode)
    {
        string color = SiteViewData.Cast(option.Get("sidebar_color", "dark"), "dark");
        if (darkMode)
        {
            color = color.Replace("light", "dark");
        }

        vars["sidebar_color"] = color;
    }

    public static void DarkMode(Dictionary<string, object?> vars, bool darkMode) =>
        vars["dark_mode"] = darkMode;

    /// <summary><c>shared.user-panel</c>：头像 + 昵称 + 徽章。</summary>
    public static void UserPanel(Dictionary<string, object?> vars,
        UserRepository.UserRow? user)
    {
        if (user is null)
        {
            vars["user"] = null;
            vars["avatar"] = null;
            vars["avatar_png"] = null;
            vars["panel_avatar"] = null;
            vars["panel_avatar_png"] = null;
            vars["panel_nickname"] = null;
            vars["panel_email"] = null;
            vars["badges"] = new List<object?>();
            return;
        }

        string avatar = "/avatar/" + user.Avatar + "?size=45";
        string avatarPng = "/avatar/" + user.Avatar + "?size=45&png=true";
        vars["user"] = user;
        vars["avatar"] = avatar;
        vars["avatar_png"] = avatarPng;
        vars["panel_avatar"] = avatar;
        vars["panel_avatar_png"] = avatarPng;
        vars["panel_nickname"] = user.Nickname;
        vars["panel_email"] = user.Email;

        var badges = new List<object?>();
        if (user.IsAdmin)
        {
            badges.Add(new PhpArray().With("text", "STAFF").With("color", "primary"));
        }

        vars["badges"] = FilterUserBadges(badges, PluginHost.PluginRuntime.Current);
    }

    /// <summary>
    /// 把 <c>user_badges</c> 过滤器交给原生插件链。
    ///
    /// <para>输入/输出都是 JSON 数组（`[{text,color}]`）；第一个插件没有返回结果时
    /// 保留原值。插件异常时由 <see cref="PluginHost.PluginRuntime"/> 记录并保留原值。</para>
    /// </summary>
    public static List<object?> FilterUserBadges(List<object?> badges,
        PluginHost.PluginRuntime? runtime) =>
        PluginFilters.ApplyList("user_badges", badges, runtime);

    /// <summary><c>shared.side-menu</c>：<c>user</c> / <c>explore</c> / <c>admin</c>。</summary>
    public static List<object?> SideMenu(string type, string requestPath)
    {
        var items = new List<object?>();
        foreach (PhpArray item in Menu(type))
        {
            items.Add(Transform(item, requestPath));
        }

        return PluginFilters.ApplyList("side_menu", items,
            PluginHost.PluginRuntime.Current);
    }

    private static PhpArray Transform(PhpArray item, string requestPath)
    {
        var output = new PhpArray();
        foreach (KeyValuePair<object, object?> pair in item)
        {
            output.Put(pair.Key, pair.Value);
        }

        string? link = item.Get("link")?.ToString();
        bool active = link is not null && Matches(requestPath, link);

        if (item.Get("children") is System.Collections.IEnumerable children)
        {
            var transformed = new List<object?>();
            foreach (object? child in children)
            {
                if (child is not PhpArray childArray)
                {
                    continue;
                }

                PhpArray result = Transform(childArray, requestPath);
                if (SiteViewData.Truthy(result.Get("active")))
                {
                    active = true;
                }

                transformed.Add(result);
            }

            output.Put("children", transformed);
        }

        var classes = new List<object?>();
        if (active)
        {
            output.Put("active", true);
            classes.Add("active menu-open");
        }

        output.Put("classes", classes);
        string? icon = item.Get("icon")?.ToString();
        if (icon is not null)
        {
            output.Put("iconClass",
                (icon == "fa-circle" ? "far " : "fas ") + icon);
        }

        return output;
    }

    private static bool Matches(string? path, string link)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string p = path.TrimStart('/');
        string l = link.TrimStart('/');
        return p == l || p.StartsWith(l + "/", StringComparison.Ordinal);
    }

    private static List<PhpArray> Menu(string type) => type switch
    {
        "user" => UserMenuItems(),
        "admin" => AdminMenuItems(),
        "explore" => ExploreMenuItems(),
        _ => [],
    };

    private static List<PhpArray> UserMenuItems() =>
    [
        Item("general.dashboard", "user", "fa-tachometer-alt"),
        Item("站内信", "user/inbox", "fa-envelope"),
        Item("general.my-closet", "user/closet", "fa-star"),
        Item("general.player-manage", "user/player", "fa-users"),
        Item("general.my-reports", "user/reports", "fa-flag"),
        Item("general.profile", "user/profile", "fa-user"),
        Group("general.developer", "fa-code-branch",
        [
            Item("general.oauth-manage", "user/oauth/manage", "fa-feather-alt"),
        ]),
    ];

    private static List<PhpArray> AdminMenuItems() =>
    [
        Item("general.dashboard", "admin", "fa-tachometer-alt"),
        Item("通知与公告", "admin/communications", "fa-bullhorn"),
        Item("general.user-manage", "admin/users", "fa-users"),
        Item("general.player-manage", "admin/players", "fa-gamepad"),
        Item("general.report-manage", "admin/reports", "fa-flag"),
        Item("general.customize", "admin/customize", "fa-paint-brush"),
        Item("general.i18n", "admin/i18n", "fa-globe"),
        Item("general.score-options", "admin/score", "fa-credit-card"),
        Item("general.options", "admin/options", "fa-cog"),
        Item("general.res-options", "admin/resource", "fa-atom"),
        Item("general.status", "admin/status", "fa-battery-three-quarters"),
        Item("general.plugin-manage", "admin/plugins/manage", "fa-plug"),
        Item("general.plugin-market", "admin/plugins/market", "fa-shopping-bag"),
        Group("general.plugin-configs", "fa-cogs", []),
        Item("general.check-update", "admin/update", "fa-arrow-up"),
    ];

    private static List<PhpArray> ExploreMenuItems() =>
    [
        Item("general.skinlib", "skinlib", "fa-archive"),
    ];

    private static PhpArray Item(string title, string link, string icon) =>
        new PhpArray()
            .With("title", title)
            .With("link", link)
            .With("icon", icon);

    private static PhpArray Group(string title, string icon, IEnumerable<PhpArray> children) =>
        new PhpArray()
            .With("title", title)
            .With("icon", icon)
            .With("children", children.Cast<object?>().ToList());
}
