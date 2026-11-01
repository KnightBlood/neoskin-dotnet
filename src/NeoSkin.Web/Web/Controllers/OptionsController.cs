using System.Text;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\OptionsController</c> 的移植。
///
/// <p>四个页面（customize / score / options / resource）。表单 HTML 用本文件内的
/// 轻量构造器生成（字段名与源一致）；POST 直接写入 option 后 302 回原页面。
/// 源的 <c>OptionForm</c> HTML 结构与 Twig 模板待模板引擎阶段替换。</p>
/// </summary>
public sealed class OptionsController(
    Option option,
    Config.NeoSkinSettings settings)
{
    private static readonly string[] NavbarColors =
    [
        "primary", "secondary", "success", "danger", "indigo", "purple", "pink", "teal",
        "cyan", "dark", "gray", "fuchsia", "maroon", "olive", "navy", "lime", "light",
        "warning", "white", "orange",
    ];

    private static readonly string[] SidebarColors =
    [
        "primary", "warning", "info", "danger", "success", "indigo", "navy", "purple",
        "fuchsia", "pink", "maroon", "orange", "lime", "teal", "olive",
    ];

    // ================================================================== customize

    /// <summary><c>GET|POST admin/customize</c>。</summary>
    public RequestHandler Customize() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            Dictionary<string, object?> input = await RequestInput.BagAsync(context);
            if (RequestInput.String(input, "action") == "color")
            {
                SetIfPresent(input, "navbar", "navbar_color");
                SetIfPresent(input, "sidebar", "sidebar_color");
            }

            SetLocalized(context, input, "copyright_prefer", locale);
            SetLocalized(context, input, "copyright_text", locale);
            SetOption(input, "home_pic_url");
            SetOption(input, "favicon_url");
            SetCheckbox(input, "transparent_navbar");
            SetCheckbox(input, "hide_intro");
            SetCheckbox(input, "fixed_bg");
            SetOption(input, "custom_css");
            SetOption(input, "custom_js");
            RedirectBack(context);
            return;
        }

        OptionForm.Context fc = FormContext(context);
        OptionForm homepage = OptionForm.Create(fc, "homepage");
        homepage.Text("home_pic_url").WithHint();
        homepage.Text("favicon_url").WithHint().Describe();
        homepage.Checkbox("transparent_navbar").Label();
        homepage.Checkbox("hide_intro").Label();
        homepage.Checkbox("fixed_bg").Label();
        homepage.Select("copyright_prefer")
            .Option("0", "Powered with ❤ by Blessing Skin Server.")
            .Option("1", "Powered by Blessing Skin Server.")
            .Option("2", "Proudly powered by Blessing Skin Server.")
            .Option("3", "由 Blessing Skin Server 强力驱动。")
            .Option("4", "采用 Blessing Skin Server 搭建。")
            .Option("5", "使用 Blessing Skin Server 稳定运行。")
            .Option("6", "自豪地采用 Blessing Skin Server。")
            .Describe();
        homepage.Textarea("copyright_text").Rows(6).Describe();

        OptionForm customJsCss = OptionForm.Create(fc, "customJsCss");
        customJsCss.Textarea("custom_css", "CSS").Rows(6);
        customJsCss.Textarea("custom_js", "JavaScript").Rows(6);
        customJsCss.AddMessageAuto();

        Dictionary<string, object?> vars = PageVars(context, "general.customize");
        vars["forms"] = new PhpArray()
            .With("homepage", homepage.Render())
            .With("custom_js_css", customJsCss.Render());
        vars["colors"] = new PhpArray()
            .With("navbar", NavbarColors.Cast<object?>().ToList())
            .With("sidebar", SidebarColors.Cast<object?>().ToList());
        vars["extra_json"] = PhpJson.Encode(new PhpArray()
            .With("navbar", option.Get("navbar_color"))
            .With("sidebar", option.Get("sidebar_color")), 0);

        await RenderAsync(context, "admin/customize", vars);
    };

    // ================================================================== score

    /// <summary><c>GET|POST admin/score</c>。</summary>
    public RequestHandler Score() => async (context, route, parameters) =>
    {
        if (HttpMethods.IsPost(context.Request.Method))
        {
            Dictionary<string, object?> input = await RequestInput.BagAsync(context);
            string from = RequestInput.String(input, "sign_score_from");
            string to = RequestInput.String(input, "sign_score_to");
            if (input.ContainsKey("sign_score_from") || input.ContainsKey("sign_score_to"))
            {
                option.Set("sign_score", from + "," + to);
            }

            SetOption(input, "score_per_storage");
            SetOption(input, "private_score_per_storage");
            SetOption(input, "score_per_closet_item");
            SetOption(input, "score_per_player");
            SetOption(input, "user_initial_score");
            SetOption(input, "reporter_score_modification");
            SetOption(input, "reporter_reward_score");
            SetOption(input, "sign_gap_time");
            SetOption(input, "score_award_per_texture");
            SetOption(input, "score_award_per_like");
            SetCheckbox(input, "return_score");
            SetCheckbox(input, "sign_after_zero");
            SetCheckbox(input, "take_back_scores_after_deletion");
            RedirectBack(context);
            return;
        }

        OptionForm.Context fc = FormContext(context);
        OptionForm rate = OptionForm.Create(fc, "rate");
        rate.Group("score_per_storage").Text("score_per_storage").Addon();
        rate.Group("private_score_per_storage").Text("private_score_per_storage")
            .Addon().WithHint();
        rate.Group("score_per_closet_item").Text("score_per_closet_item").Addon();
        rate.Checkbox("return_score").Label();
        rate.Group("score_per_player").Text("score_per_player").Addon();
        rate.Text("user_initial_score");

        OptionForm report = OptionForm.Create(fc, "report");
        report.Text("reporter_score_modification").Describe();
        report.Text("reporter_reward_score");

        OptionForm sign = OptionForm.Create(fc, "sign");
        sign.Group("sign_score")
            .Text("sign_score_from")
            .Addon(Translator(context).Trans("options.sign.sign_score.addon1") ?? "")
            .Text("sign_score_to")
            .Addon(Translator(context).Trans("options.sign.sign_score.addon2") ?? "");
        sign.Group("sign_gap_time").Text("sign_gap_time").Addon();
        sign.Checkbox("sign_after_zero").Label().WithHint();
        string[] signScore = Str("sign_score").Split(',');
        sign.With("sign_score_from", signScore.Length > 0 ? signScore[0] : "");
        sign.With("sign_score_to", signScore.Length > 1 ? signScore[1] : "");

        OptionForm sharing = OptionForm.Create(fc, "sharing");
        sharing.Group("score_award_per_texture").Text("score_award_per_texture")
            .Addon(Translator(context).Trans("general.user.score") ?? "");
        sharing.Checkbox("take_back_scores_after_deletion").Label();
        sharing.Group("score_award_per_like").Text("score_award_per_like")
            .Addon(Translator(context).Trans("general.user.score") ?? "");

        Dictionary<string, object?> vars = PageVars(context, "general.score-options");
        vars["forms"] = new PhpArray()
            .With("rate", rate.Render())
            .With("report", report.Render())
            .With("sign", sign.Render())
            .With("sharing", sharing.Render());

        await RenderAsync(context, "admin/score", vars);
    };

    // ================================================================== options

    /// <summary><c>GET|POST admin/options</c>。</summary>
    public RequestHandler Options() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            Dictionary<string, object?> input = await RequestInput.BagAsync(context);

            string? updateSource = Nullable(input, "update_source");
            if (updateSource is not null && !Validator.IsUrlWithScheme(updateSource))
            {
                await ValidationAsync(context, "update_source",
                    "The update_source must be a valid URL.");
                return;
            }

            SetLocalized(context, input, "site_name", locale);
            SetLocalized(context, input, "site_description", locale);
            SetLocalized(context, input, "content_policy", locale);

            string? siteUrl = Nullable(input, "site_url");
            if (siteUrl is not null)
            {
                if (siteUrl.EndsWith('/'))
                {
                    siteUrl = siteUrl[..^1];
                }

                if (siteUrl.EndsWith("/index.php", StringComparison.Ordinal))
                {
                    siteUrl = siteUrl[..^10];
                }

                option.Set("site_url", siteUrl);
            }

            SetCheckbox(input, "register_with_player_name");
            SetCheckbox(input, "require_verification");
            SetCheckbox(input, "auto_del_invalid_texture");
            SetCheckbox(input, "allow_downloading_texture");
            SetCheckbox(input, "recaptcha_invisible");

            foreach (string key in new[]
                     {
                         "regs_per_ip", "max_upload_file_size", "max_texture_width",
                         "player_name_rule", "custom_player_name_regexp",
                         "player_name_length_min", "player_name_length_max",
                         "status_code_for_private", "texture_name_regexp",
                         "meta_keywords", "meta_description", "meta_extras",
                         "recaptcha_sitekey", "recaptcha_secretkey",
                         "update_source", "plugins_registry",
                     })
            {
                SetOption(input, key);
            }

            RedirectBack(context);
            return;
        }

        OptionForm.Context fc = FormContext(context);
        OptionForm general = OptionForm.Create(fc, "general");
        general.Text("site_name");
        general.Text("site_description").Describe();
        general.Text("site_url").WithHint();
        general.Checkbox("register_with_player_name").Label();
        general.Checkbox("require_verification").Label();
        general.Text("regs_per_ip");
        general.Group("max_upload_file_size").Text("max_upload_file_size")
            .Addon("KB")
            .WithHint(Translator(context).Trans("options.general.max_upload_file_size.hint",
                new Dictionary<string, object?> { ["size"] = "2M" }));
        general.Group("max_texture_width").Text("max_texture_width").Addon("px").WithHint();
        general.Select("player_name_rule")
            .Option("official", Translator(context).Trans(
                "options.general.player_name_rule.official"))
            .Option("cjk", Translator(context).Trans(
                "options.general.player_name_rule.cjk"))
            .Option("utf8", Translator(context).Trans(
                "options.general.player_name_rule.utf8"))
            .Option("custom", Translator(context).Trans(
                "options.general.player_name_rule.custom"));
        general.Text("custom_player_name_regexp").Placeholder().WithHint();
        general.Group("player_name_length").Text("player_name_length_min")
            .Addon("~")
            .Text("player_name_length_max")
            .Addon(Translator(context).Trans("options.general.player_name_length.suffix"));
        general.Checkbox("auto_del_invalid_texture").Label().WithHint();
        general.Checkbox("allow_downloading_texture").Label();
        general.Select("status_code_for_private")
            .Option("403", "403 Forbidden")
            .Option("404", "404 Not Found");
        general.Text("texture_name_regexp").Placeholder().WithHint();
        general.Textarea("content_policy").Rows(3).Describe();

        OptionForm meta = OptionForm.Create(fc, "meta");
        meta.Text("meta_keywords").WithHint();
        meta.Text("meta_description").WithHint();
        meta.Textarea("meta_extras").Rows(6);

        OptionForm recaptcha = OptionForm.Create(fc, "recaptcha", "reCAPTCHA");
        recaptcha.Text("recaptcha_sitekey", "sitekey");
        recaptcha.Text("recaptcha_secretkey", "secretkey");
        recaptcha.Checkbox("recaptcha_invisible").Label();

        OptionForm updates = OptionForm.Create(fc, "updates", "更新与插件仓库");
        updates.Text("update_source", "主站更新检测 URL")
            .Placeholder(settings.UpdateSource);
        updates.Text("plugins_registry", "插件仓库 URL")
            .Placeholder(settings.Plugins.Registry);
        updates.Type("danger").AddMessage(
            Translator(context).Trans("admin.update.cautions.tip"), "danger");
        updates.Always(form =>
        {
            object? updateSource = option.Get("update_source");
            form.With("update_source",
                SiteViewData.Truthy(updateSource) ? updateSource : settings.UpdateSource);
            object? registry = option.Get("plugins_registry");
            form.With("plugins_registry",
                SiteViewData.Truthy(registry) ? registry : settings.Plugins.Registry);
        });

        Dictionary<string, object?> vars = PageVars(context, "general.options");
        vars["forms"] = new PhpArray()
            .With("general", general.Render())
            .With("meta", meta.Render())
            .With("recaptcha", recaptcha.Render())
            .With("updates", updates.Render());

        await RenderAsync(context, "admin/options", vars);
    };

    // ================================================================== resource

    /// <summary><c>GET|POST admin/resource</c>。</summary>
    public RequestHandler Resource() => async (context, route, parameters) =>
    {
        if (HttpMethods.IsPost(context.Request.Method))
        {
            Dictionary<string, object?> input = await RequestInput.BagAsync(context);
            SetCheckbox(input, "force_ssl");
            SetCheckbox(input, "auto_detect_asset_url");
            SetCheckbox(input, "enable_avatar_cache");
            SetCheckbox(input, "enable_preview_cache");
            SetOption(input, "cache_expire_time");

            string? cdn = Nullable(input, "cdn_address");
            if (input.ContainsKey("cdn_address"))
            {
                option.Set("cdn_address", cdn is not null && cdn.EndsWith('/') ? cdn[..^1] : cdn ?? "");
            }

            RedirectBack(context);
            return;
        }

        OptionForm.Context fc = FormContext(context);
        OptionForm resources = OptionForm.Create(fc, "resources");
        resources.Type("primary").Hint();
        resources.Checkbox("force_ssl").Label().WithHint();
        resources.Checkbox("auto_detect_asset_url").Label().Describe();
        resources.Text("cache_expire_time").WithHint();
        resources.Text("cdn_address").WithHint().Describe();

        OptionForm cache = OptionForm.Create(fc, "cache");
        cache.Type("warning");
        cache.Checkbox("enable_avatar_cache").Label();
        cache.Checkbox("enable_preview_cache").Label();
        cache.AddButton(Translator(context).Trans("options.cache.clear"), "warning", "a",
            "", "?clear-cache");
        cache.AddMessage(Translator(context).Trans("options.cache.driver",
            new Dictionary<string, object?> { ["driver"] = "file" }), "info");

        Dictionary<string, object?> vars = PageVars(context, "general.res-options");
        vars["forms"] = new PhpArray()
            .With("resources", resources.Render())
            .With("cache", cache.Render());

        await RenderAsync(context, "admin/resource", vars);
    };

    // ================================================================== 辅助

    private OptionForm.Context FormContext(HttpContext context) =>
        new(option, Translator(context), Locale(context), CsrfField(context));

    private Dictionary<string, object?> PageVars(HttpContext context, string titleKey)
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context),
            context.Request.Path.Value ?? "/admin", settings.Debug, "admin",
            Session(context));
        vars["pageTitle"] = Translator(context).Trans(titleKey) ?? titleKey;
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    private static async Task RenderAsync(HttpContext context, string template,
        Dictionary<string, object?> vars)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(TemplateRenderer.Render(template, vars));
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string CsrfField(HttpContext context)
    {
        string token = Session(context)?.Token()?.ToString() ?? "";
        return "<input type=\"hidden\" name=\"_token\" value=\""
               + System.Net.WebUtility.HtmlEncode(token) + "\">";
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private string Str(string key) => option.GetString(key, "") ?? "";

    private bool Bool(string key) => option.GetBoolean(key, false);

    private void SetOption(Dictionary<string, object?> input, string key)
    {
        if (input.ContainsKey(key))
        {
            option.Set(key, RequestInput.String(input, key));
        }
    }

    private void SetIfPresent(Dictionary<string, object?> input, string inputKey, string optionKey)
    {
        string value = RequestInput.String(input, inputKey);
        if (value.Length > 0)
        {
            option.Set(optionKey, value);
        }
    }

    private void SetCheckbox(Dictionary<string, object?> input, string key)
    {
        option.Set(key, RequestInput.ToBool(input.GetValueOrDefault(key)) ? "true" : "false");
    }

    private void SetLocalized(HttpContext context, Dictionary<string, object?> input,
        string key, string locale)
    {
        string localizedKey = key + "_" + locale;
        if (input.ContainsKey(key))
        {
            option.Set(localizedKey, RequestInput.String(input, key));
        }
        else if (input.ContainsKey(localizedKey))
        {
            option.Set(localizedKey, RequestInput.String(input, localizedKey));
        }
    }

    private static string? Nullable(Dictionary<string, object?> input, string key)
    {
        string value = RequestInput.String(input, key);
        return value.Length == 0 ? null : value;
    }

    private static void RedirectBack(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "/admin";
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = "http://" + Host(context) + path;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    }

    private static async Task ValidationAsync(HttpContext context, string field, string message)
    {
        if (Error.ContentNegotiation.ExpectsJson(context))
        {
            context.Response.StatusCode = 422;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(PhpJson.Encode(new PhpArray()
                .With("message", message)
                .With("errors", new PhpArray().With(field, new[] { message })), 0));
            return;
        }

        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        session?.Flash("errors", new PhpArray().With(field, new[] { message }));
        RedirectBack(context);
        await Task.CompletedTask;
    }

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;

    private static async Task WriteHtmlAsync(HttpContext context, string html)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>轻量表单构造器（字段名与源一致，HTML 结构在模板引擎阶段替换）。</summary>
    private sealed class FormBuilder(HttpContext context, string title)
    {
        private readonly StringBuilder _html = new();

        public FormBuilder Section(string title)
        {
            _html.Append("<fieldset><legend>")
                .Append(System.Net.WebUtility.HtmlEncode(title))
                .Append("</legend>");
            return this;
        }

        public FormBuilder Text(string name, string label, string value) =>
            Input("text", name, label, value);

        public FormBuilder Hidden(string name, string value)
        {
            _html.Append("<input type=\"hidden\" name=\"").Append(name).Append("\" value=\"")
                .Append(System.Net.WebUtility.HtmlEncode(value)).Append("\">");
            return this;
        }

        public FormBuilder Checkbox(string name, string label, bool value)
        {
            _html.Append("<label><input type=\"checkbox\" name=\"").Append(name)
                .Append("\" value=\"1\"").Append(value ? " checked" : "").Append("> ")
                .Append(System.Net.WebUtility.HtmlEncode(label)).Append("</label><br>");
            return this;
        }

        public FormBuilder Textarea(string name, string label, string value, int rows)
        {
            _html.Append("<label>").Append(System.Net.WebUtility.HtmlEncode(label))
                .Append("<br><textarea name=\"").Append(name).Append("\" rows=\"").Append(rows)
                .Append("\">").Append(System.Net.WebUtility.HtmlEncode(value))
                .Append("</textarea></label><br>");
            return this;
        }

        public FormBuilder Select(string name, string label, string value,
            (string Value, string Label)[] options)
        {
            _html.Append("<label>").Append(System.Net.WebUtility.HtmlEncode(label))
                .Append(" <select name=\"").Append(name).Append("\">");
            foreach ((string optionValue, string optionLabel) in options)
            {
                _html.Append("<option value=\"").Append(optionValue).Append('"')
                    .Append(optionValue == value ? " selected" : "").Append('>')
                    .Append(System.Net.WebUtility.HtmlEncode(optionLabel)).Append("</option>");
            }

            _html.Append("</select></label><br>");
            return this;
        }

        private FormBuilder Input(string type, string name, string label, string value)
        {
            _html.Append("<label>").Append(System.Net.WebUtility.HtmlEncode(label))
                .Append(" <input type=\"").Append(type).Append("\" name=\"").Append(name)
                .Append("\" value=\"").Append(System.Net.WebUtility.HtmlEncode(value))
                .Append("\"></label><br>");
            return this;
        }

        public string Render(string action)
        {
            string token = context.Items[StartSessionMiddleware.SessionKey] is SessionStore store
                ? store.Token()?.ToString() ?? ""
                : "";
            return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>"
                + System.Net.WebUtility.HtmlEncode(title) + "</title></head><body><h1>"
                + System.Net.WebUtility.HtmlEncode(title) + "</h1><form method=\"post\" action=\""
                + action + "\"><input type=\"hidden\" name=\"_token\" value=\""
                + System.Net.WebUtility.HtmlEncode(token) + "\">"
                + _html + "<button type=\"submit\">保存</button></form></body></html>";
        }
    }
}
