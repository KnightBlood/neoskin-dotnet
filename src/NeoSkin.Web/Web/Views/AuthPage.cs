using NeoSkin.Web.Db;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.View;

namespace NeoSkin.Web.Web.Views;

/// <summary>
/// 认证页（<c>auth/login|register|forgot|reset|verify|bind</c>）的渲染入口。
///
/// <p>HTML 由 <see cref="TemplateRenderer"/> 渲染源模板
/// <c>Resources/views/auth/*.html</c>；表单本体由前端 React 依据
/// <c>#blessing-extra</c> 挂载到 <c>&lt;main&gt;</c>。</p>
/// </summary>
public static class AuthPage
{
    public static string Login(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string clientIp,
        LoginFailCounter failures, string host = "localhost",
        string requestPath = "/auth/login") =>
        TemplateRenderer.Render("auth/login", AuthView.Login(option, translator, locale,
            host, requestPath, settings, session, clientIp, failures));

    public static string Register(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string host = "localhost",
        string requestPath = "/auth/register") =>
        TemplateRenderer.Render("auth/register", AuthView.Register(option, translator,
            locale, host, requestPath, settings, session));

    public static string Forgot(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string host = "localhost",
        string requestPath = "/auth/forgot") =>
        TemplateRenderer.Render("auth/forgot", AuthView.Forgot(option, translator, locale,
            host, requestPath, settings, session));

    public static string Reset(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string? nickname,
        string host = "localhost", string requestPath = "/auth/reset") =>
        TemplateRenderer.Render("auth/reset", AuthView.Reset(option, translator, locale,
            host, requestPath, settings, session, nickname));

    public static string Verify(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session, string host = "localhost",
        string requestPath = "/auth/verify") =>
        TemplateRenderer.Render("auth/verify", AuthView.Verify(option, translator, locale,
            host, requestPath, settings, session));

    public static string Bind(Option option, I18n.Translator translator, string locale,
        Config.NeoSkinSettings settings, SessionStore? session,
        UserRepository.UserRow? user, string host = "localhost",
        string requestPath = "/auth/bind") =>
        TemplateRenderer.Render("auth/bind", AuthView.Bind(option, translator, locale,
            host, requestPath, settings, session, user));
}
