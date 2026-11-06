using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web;

/// <summary>
/// SPA 壳与契约 v1 bootstrap（见 NeoSkinFrontend/docs/BACKEND-CONTRACT.md）。
///
/// <p>以终端中间件的形式挂在路由分发器之前，避免改动 174 条 PHP 路由表。</p>
/// </summary>
public static class SpaFrontend
{
    private const int ContractVersion = 1;

    private static readonly string[] Locales =
    [
        "zh_CN", "zh_TW", "en", "de_DE", "es_ES", "fr_FR", "it_IT",
        "ja_JP", "ko_KR", "nl_NL", "pt_PT", "ru_RU", "el_GR",
    ];

    /// <summary><c>FRONTEND_MODE=legacy|next</c>。</summary>
    public static string Mode =>
        string.Equals(Environment.GetEnvironmentVariable("FRONTEND_MODE"), "next",
            StringComparison.OrdinalIgnoreCase)
            ? "next"
            : "legacy";

    /// <summary>处理 <c>/app-next/*</c> 与 <c>/api/bootstrap</c>；其余请求放行给路由分发器。</summary>
    public static async Task HandleAsync(HttpContext context, Option option,
        NeoSkinSettings settings, UserRepository users, Paths.AppPaths paths)
    {
        string path = context.Request.Path.Value ?? "/";
        if (path == "/api/bootstrap")
        {
            await WriteBootstrapAsync(context, option, settings, users, "site");
            return;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || (segments[1] != "site" && segments[1] != "admin"))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Not Found");
            return;
        }

        string mode = segments[1];
        string relative = segments.Length <= 2
            ? ""
            : string.Join('/', segments.Skip(2));
        if (relative.Length == 0 || relative.EndsWith('/'))
        {
            relative += "index.html";
        }

        string baseDir = Path.GetFullPath(Path.Combine(paths.PublicDir, "app-next", mode));
        string target = Path.GetFullPath(Path.Combine(baseDir, relative));
        if (!target.StartsWith(baseDir + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && target != baseDir)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Not Found");
            return;
        }

        if (File.Exists(target))
        {
            if (target.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                await WriteShellAsync(context, option, settings, users, mode, target);
                return;
            }

            context.Response.ContentType = MimeFor(relative);
            context.Response.Headers["Cache-Control"] =
                "public, max-age=31536000, immutable";
            await context.Response.SendFileAsync(target);
            return;
        }

        // 带扩展名的缺失资源必须 404，不能回退 HTML。
        if (Path.GetExtension(relative).Length > 0)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Not Found");
            return;
        }

        string index = Path.Combine(baseDir, "index.html");
        if (!File.Exists(index))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync(
                "app-next 前端产物缺失：请先运行 pnpm -r build 并执行 scripts/publish.ps1");
            return;
        }

        await WriteShellAsync(context, option, settings, users, mode, index);
    }

    private static async Task WriteShellAsync(HttpContext context, Option option,
        NeoSkinSettings settings, UserRepository users, string mode, string index)
    {
        string html = await File.ReadAllTextAsync(index);
        JsonObject payload = await BuildPayloadAsync(context, option, settings, users, mode);
        string encoded = payload.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        string csrf = CsrfToken(context);
        string injection =
            $"<meta name=\"csrf-token\" content=\"{HtmlEscape(csrf)}\">\n" +
            $"<meta name=\"neoskin-mode\" content=\"{mode}\">\n" +
            $"<script id=\"neoskin-bootstrap\" type=\"application/json\">{EscapeScriptJson(encoded)}</script>\n";

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache";
        await context.Response.WriteAsync(html.Replace("</head>", injection + "</head>",
            StringComparison.Ordinal));
    }

    private static async Task WriteBootstrapAsync(HttpContext context, Option option,
        NeoSkinSettings settings, UserRepository users, string mode)
    {
        string requested = context.Request.Query["mode"].ToString();
        string selected = requested is "site" or "admin" ? requested : mode;
        JsonObject data = await BuildPayloadAsync(context, option, settings, users, selected);
        var envelope = new JsonObject
        {
            ["code"] = 0,
            ["message"] = "ok",
            ["data"] = data,
        };

        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await context.Response.WriteAsync(envelope.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static async Task<JsonObject> BuildPayloadAsync(HttpContext context, Option option,
        NeoSkinSettings settings, UserRepository users, string mode)
    {
        string locale = context.Items["locale"] as string ?? "zh_CN";
        string siteName = option.GetLocalized("site_name", "Blessing Skin")?.ToString()
            ?? "Blessing Skin";
        UserRepository.UserRow? user = context.Items["session"] is NeoSkin.Web.Session.SessionStore store
            ? new NeoSkin.Web.Auth.SessionGuard(store, users).User()
            : null;

        var payload = new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["apiBase"] = "",
            ["locale"] = locale,
            ["localeFallback"] = "en",
            ["siteName"] = siteName,
            ["version"] = settings.Version,
            ["basePath"] = $"/app-next/{mode}/",
            ["mode"] = mode,
            ["csrfToken"] = CsrfToken(context),
            ["locales"] = new JsonArray(Locales.Select(value => JsonValue.Create(value)).ToArray()),
            ["user"] = user is null ? null : new JsonObject
            {
                ["uid"] = user.Uid,
                ["email"] = user.Email,
                ["nickname"] = user.Nickname ?? "",
                ["avatar"] = $"/avatar/{user.Avatar}",
                ["score"] = user.Score,
                ["permission"] = user.Permission,
                ["verified"] = user.Verified,
                ["isAdmin"] = user.IsAdmin,
                ["isBanned"] = user.Permission < 0,
                ["darkMode"] = user.DarkMode,
            },
            ["options"] = new JsonObject
            {
                ["site_name"] = siteName,
                ["site_description"] = option.GetLocalized("site_description", "")
                    ?.ToString() ?? "",
                ["user_can_register"] = true,
                ["register_with_player_name"] =
                    option.GetBoolean("register_with_player_name", true),
                ["captcha"] = false,
                ["recaptcha"] = !string.IsNullOrEmpty(option.GetString("recaptcha_sitekey", "")),
                ["recaptcha_site_key"] = option.GetString("recaptcha_sitekey", ""),
                ["max_upload_file_size"] = option.GetInt("max_upload_file_size", 1024),
                ["max_texture_width"] = option.GetInt("max_texture_width", 8192),
                ["score_per_storage"] = option.GetInt("score_per_storage", 1),
                ["private_score_per_storage"] = option.GetInt("private_score_per_storage", 10),
                ["score_award_per_texture"] = option.GetInt("score_award_per_texture", 0),
                ["score_per_player"] = option.GetInt("score_per_player", 100),
                ["score_per_closet_item"] = option.GetInt("score_per_closet_item", 0),
                ["sign_score"] = option.GetString("sign_score", "10,100"),
                ["take_back_scores_after_deletion"] =
                    option.GetBoolean("take_back_scores_after_deletion", true),
                ["player_name_rule"] = option.GetString("player_name_rule", "official"),
                ["custom_player_name_regexp"] =
                    option.GetString("custom_player_name_regexp", ""),
                ["navbar_color"] = option.GetString("navbar_color", "cyan"),
                ["sidebar_color"] = option.GetString("sidebar_color", "dark-maroon"),
                ["copyright_prefer"] = option.GetString("copyright_prefer", "0"),
                ["copyright_text"] = option.GetString("copyright_text", ""),
                ["update_source"] = option.GetString("update_source", ""),
                ["plugin_market_source"] = option.GetString("plugin_market_source", ""),
            },
            ["features"] = new JsonObject
            {
                ["registration"] = true,
                ["emailVerification"] = option.GetBoolean("require_verification", false),
                ["passwordReset"] = false,
                ["oauth"] = true,
                ["reports"] = true,
                ["plugins"] = true,
            },
            ["plugins"] = new JsonObject
            {
                ["menus"] = new JsonArray(),
                ["badges"] = new JsonArray(),
                ["styles"] = new JsonArray(),
                ["scripts"] = new JsonArray(),
            },
        };

        await Task.CompletedTask;
        return payload;
    }

    private static string CsrfToken(HttpContext context) =>
        context.Items["session"] is NeoSkin.Web.Session.SessionStore session
            ? session.Token()?.ToString() ?? ""
            : "";

    private static string HtmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string EscapeScriptJson(string value) => value
        .Replace("<", "\\u003c")
        .Replace(">", "\\u003e")
        .Replace("&", "\\u0026")
        .Replace("\u2028", "\\u2028")
        .Replace("\u2029", "\\u2029");

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant()
        switch
        {
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".map" => "application/json",
            ".html" => "text/html; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };
}
