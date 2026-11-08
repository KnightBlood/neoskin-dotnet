using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 认证页（<c>auth.login/register/forgot/reset/verify/bind</c>）的视图变量装配
/// （对应 Java <c>AuthView</c>）。
/// </summary>
public static class AuthView
{
    private static readonly string[] LoginRows =
    [
        "auth/rows/login/notice",
        "auth/rows/login/message",
        "auth/rows/login/form",
        "auth/rows/login/registration-link",
    ];

    private static readonly string[] RegisterRows =
    [
        "auth/rows/register/notice",
        "auth/rows/register/form",
    ];

    public static Dictionary<string, object?> Login(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session, string clientIp, LoginFailCounter failures)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session);
        vars["pageTitle"] = Text(translator, "auth.login.title");
        vars["rows"] = PluginFilters.ApplyList("auth_page_rows:login", ToList(LoginRows),
            PluginHost.PluginRuntime.Current);

        int fails = failures.Get(LoginFailCounter.KeyForIp(clientIp));
        vars["extra"] = PhpJson.Encode(new PhpArray()
            .With("tooManyFails", fails > 3)
            .With("recaptcha", SiteViewData.FalsyToNull(option.Get("recaptcha_sitekey")))
            .With("invisible", option.GetBoolean("recaptcha_invisible", false)), 0);
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    public static Dictionary<string, object?> Register(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session);
        vars["pageTitle"] = Text(translator, "auth.register.title");
        vars["rows"] = PluginFilters.ApplyList("auth_page_rows:register", ToList(RegisterRows),
            PluginHost.PluginRuntime.Current);
        vars["site_name"] = option.GetLocalized("site_name", "NeoSkin");
        vars["extra"] = PhpJson.Encode(new PhpArray()
            .With("player", option.GetBoolean("register_with_player_name", true))
            .With("recaptcha", SiteViewData.FalsyToNull(option.Get("recaptcha_sitekey")))
            .With("invisible", option.GetBoolean("recaptcha_invisible", false)), 0);
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    public static Dictionary<string, object?> Forgot(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session);
        vars["pageTitle"] = Text(translator, "auth.forgot.title");
        vars["extra"] = PhpJson.Encode(new PhpArray()
            .With("recaptcha", SiteViewData.FalsyToNull(option.Get("recaptcha_sitekey")))
            .With("invisible", option.GetBoolean("recaptcha_invisible", false)), 0);
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    public static Dictionary<string, object?> Reset(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session, string? nickname)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session);
        vars["pageTitle"] = Text(translator, "auth.reset.title");
        vars["user"] = new PhpArray().With("nickname", nickname ?? "");
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    public static Dictionary<string, object?> Verify(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session);
        vars["pageTitle"] = Text(translator, "auth.verify.title");
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    public static Dictionary<string, object?> Bind(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session, UserRepository.UserRow? user)
    {
        Dictionary<string, object?> vars = Base(option, translator, locale, host,
            requestPath, settings, session, user);
        vars["pageTitle"] = Text(translator, "auth.bind.title");
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    // ================================================================== 公共

    private static Dictionary<string, object?> Base(Option option, Translator translator,
        string locale, string host, string requestPath, Config.NeoSkinSettings settings,
        SessionStore? session, UserRepository.UserRow? user = null)
    {
        string urlRoot = "http://" + host;
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        SiteViewData.Base(vars, option, locale, user?.DarkMode ?? false);
        SiteViewData.Head(vars, option, locale, urlRoot, requestPath, settings.Debug);
        SiteViewData.Foot(vars, option, locale);
        SiteViewData.Copyright(vars, option);
        SiteViewData.Languages(vars, locale, requestPath);

        vars["view"] = new TemplateViewContext(
            translator, option, locale, session?.Token()?.ToString() ?? "", user, session,
            path => path.StartsWith("http", StringComparison.Ordinal)
                ? path
                : "/" + path.TrimStart('/'));

        return vars;
    }

    private static List<object?> ToList(IEnumerable<string> items) =>
        items.Cast<object?>().ToList();

    private static string Text(Translator translator, string key) =>
        translator.Trans(key) ?? "";
}
