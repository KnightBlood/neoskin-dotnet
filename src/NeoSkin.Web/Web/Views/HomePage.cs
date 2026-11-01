using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;

namespace NeoSkin.Web.Web.Views;

/// <summary>
/// 首页（源 <c>home</c> 模板）的视图变量装配。
///
/// <p>HTML 由 <see cref="TemplateRenderer"/> 渲染源模板
/// <c>Resources/views/home.html</c>，不再手写页面结构。</p>
/// </summary>
public static class HomePage
{
    private static readonly string[] FeatureItems = ["first", "second", "third"];

    private static readonly string[] DefaultHomePics = ["./app/bg.webp"];

    public static string Render(Option option, Translator translator, string locale,
        UserRepository.UserRow? user, string host, string requestPath,
        Config.NeoSkinSettings settings, string csrfToken, SessionStore? session = null)
    {
        string siteName = SiteViewData.Cast(
            option.GetLocalized("site_name", "Blessing Skin"), "Blessing Skin");
        string urlRoot = "http://" + host;
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        // HomeController::index() 的 with()
        variables["site_description"] = option.GetLocalized("site_description", "");
        variables["transparent_navbar"] = option.GetBoolean("transparent_navbar", false);
        variables["fixed_bg"] = SiteViewData.FalsyToNull(option.Get("fixed_bg"));
        variables["hide_intro"] = SiteViewData.FalsyToNull(option.Get("hide_intro"));
        variables["home_pic_url"] = SiteViewData.PhpOr(option.Get("home_pic_url"),
            DefaultHomePics[0]);

        // 共享 composer
        SiteViewData.Base(variables, option, locale, user?.DarkMode ?? false);
        SiteViewData.Head(variables, option, locale, urlRoot, requestPath, settings.Debug);
        SiteViewData.Foot(variables, option, locale);
        SiteViewData.Copyright(variables, option);

        // home 模板自身
        variables["featureItems"] = FeatureItems.Cast<object?>().ToList();
        variables["intro"] = translator.Trans("index.introduction",
            new Dictionary<string, object?> { ["sitename"] = siteName }) ?? "";
        variables["blessingExtra"] = PhpJson.Encode(
            new PhpArray().With("transparent_navbar",
                option.GetBoolean("transparent_navbar", false)), 0);

        SiteViewData.Languages(variables, locale, requestPath);
        SiteViewData.UserMenu(variables, user, translator);

        variables["view"] = new TemplateViewContext(
            translator, option, locale, csrfToken, user, session,
            path => path.StartsWith("http", StringComparison.Ordinal)
                ? path
                : "/" + path.TrimStart('/'));

        return TemplateRenderer.Render("home", variables);
    }
}
