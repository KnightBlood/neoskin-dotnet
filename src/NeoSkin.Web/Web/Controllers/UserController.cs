using System.Security.Cryptography;
using NeoSkin.Web.Auth;
using NeoSkin.Web.Db;
using NeoSkin.Web.Event;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\UserController</c> 的移植。
///
/// <p>JSON actions：<c>user</c> / <c>scoreInfo</c> / <c>sign</c> / <c>toggleDarkMode</c> /
/// <c>handleProfile</c> / <c>setAvatar</c> / <c>sendVerificationEmail</c>；
/// <c>index</c> / <c>profile</c> 两个视图页等 Phase 7。</p>
/// </summary>
public sealed class UserController(
    Option option,
    UserRepository users,
    PlayerRepository players,
    TextureRepository textures,
    ICipher cipher,
    IMailer mailer,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>GET api/user</c>。</summary>
    public RequestHandler User() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["uid"] = user.Uid,
            ["email"] = user.Email,
            ["nickname"] = user.Nickname,
            ["avatar"] = user.Avatar,
            ["score"] = user.Score,
            ["permission"] = user.Permission,
            ["verified"] = user.Verified,
            ["is_dark_mode"] = user.DarkMode,
            ["locale"] = user.Locale,
        };
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>GET user/score-info</c>。</summary>
    public RequestHandler ScoreInfo() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var userJson = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["score"] = user.Score,
            ["lastSignAt"] = users.LastSignAt(user.Uid),
        };
        var rate = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["storage"] = RequestInput.ToInt(option.Get("score_per_storage")),
            ["players"] = option.GetInt("score_per_player", 100),
        };
        var usage = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["players"] = players.CountByUser(user.Uid),
            ["storage"] = textures.SumSizeByUploader(user.Uid),
        };
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = userJson,
            ["rate"] = rate,
            ["usage"] = usage,
            ["signAfterZero"] = RequestInput.ToBool(option.Get("sign_after_zero")),
            ["signGapTime"] = option.GetInt("sign_gap_time", 24),
        };
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>POST user/sign</c>。</summary>
    public RequestHandler Sign() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        long remaining = RemainingSeconds(users.LastSignAt(user.Uid));
        if (remaining > 0)
        {
            await JsonAsync(context, 200, Message(1, ""));
            return;
        }

        string[] range = (option.Get("sign_score")?.ToString() ?? "0,0").Split(',');
        int min = RequestInput.ToInt(range.Length > 0 ? range[0] : "0");
        int max = RequestInput.ToInt(range.Length > 1 ? range[1] : "0");
        int acquired = RandomNumberGenerator.GetInt32(min, Math.Max(min, max) + 1);

        int newScore = user.Score + acquired;
        string now = PhpDate.Now(settings.Timezone);
        users.UpdateScoreAndSignAt(user.Uid, newScore, now);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["score"] = newScore,
        };
        await JsonAsync(context, 200, Message(0,
            Trans(context, "user.sign-success",
                new Dictionary<string, object?> { ["score"] = acquired.ToString() }), data));
    };

    /// <summary><c>PUT user/dark-mode</c> → 204。</summary>
    public RequestHandler ToggleDarkMode() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        users.UpdateDarkMode(user.Uid, !user.DarkMode);
        context.Response.StatusCode = 204;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await Task.CompletedTask;
    };

    /// <summary><c>POST user/profile</c>：nickname / password / email / delete。</summary>
    public RequestHandler HandleProfile() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string action = RequestInput.String(input, "action");

        switch (action)
        {
            case "nickname":
            {
                if (!await ValidateAsync(context, input, "new_nickname", ["required"]))
                {
                    return;
                }

                string nickname = RequestInput.String(input, "new_nickname");
                users.UpdateNickname(user.Uid, nickname);
                EventRuntime.Fire(new Events.UserProfileUpdated("nickname", user));
                await JsonAsync(context, 200, Message(0,
                    Trans(context, "user.profile.nickname.success",
                        new Dictionary<string, object?> { ["nickname"] = nickname })));
                return;
            }

            case "password":
            {
                if (!await ValidateAsync(context, input, "current_password",
                        ["required", "min:6", "max:32"])
                    || !await ValidateAsync(context, input, "new_password",
                        ["required", "min:8", "max:32"]))
                {
                    return;
                }

                string current = RequestInput.String(input, "current_password");
                if (!VerifyPassword(current, user.Password))
                {
                    await JsonAsync(context, 200, Message(1,
                        Trans(context, "user.profile.password.wrong-password")));
                    return;
                }

                string newPassword = RequestInput.String(input, "new_password");
                users.ChangePassword(user.Uid,
                    cipher.Hash(newPassword, option.GetString("pwd_salt", null)));
                EventRuntime.Fire(new Events.UserProfileUpdated("password", user));
                Logout(context);
                await JsonAsync(context, 200,
                    Message(0, Trans(context, "user.profile.password.success")));
                return;
            }

            case "email":
            {
                if (!await ValidateAsync(context, input, "email", ["required", "email"])
                    || !await ValidateAsync(context, input, "password",
                        ["required", "min:6", "max:32"]))
                {
                    return;
                }

                string email = RequestInput.String(input, "email");
                string password = RequestInput.String(input, "password");
                if (users.CountByEmail(email) > 0)
                {
                    await JsonAsync(context, 200,
                        Message(1, Trans(context, "user.profile.email.existed")));
                    return;
                }

                if (!VerifyPassword(password, user.Password))
                {
                    await JsonAsync(context, 200,
                        Message(1, Trans(context, "user.profile.email.wrong-password")));
                    return;
                }

                users.UpdateEmailAndUnverify(user.Uid, email);
                EventRuntime.Fire(new Events.UserProfileUpdated("email", user));
                Logout(context);
                await JsonAsync(context, 200,
                    Message(0, Trans(context, "user.profile.email.success")));
                return;
            }

            case "delete":
            {
                if (!await ValidateAsync(context, input, "password",
                        ["required", "min:6", "max:32"]))
                {
                    return;
                }

                if (user.IsAdmin)
                {
                    await JsonAsync(context, 200,
                        Message(1, Trans(context, "user.profile.delete.admin")));
                    return;
                }

                string password = RequestInput.String(input, "password");
                if (!VerifyPassword(password, user.Password))
                {
                    await JsonAsync(context, 200,
                        Message(1, Trans(context, "user.profile.delete.wrong-password")));
                    return;
                }

                Logout(context);
                users.DeleteWithPlayers(user.Uid);
                Session(context)?.Forget("_token");
                await JsonAsync(context, 200,
                    Message(0, Trans(context, "user.profile.delete.success")));
                return;
            }

            default:
                await JsonAsync(context, 200,
                    Message(1, Trans(context, "general.illegal-parameters")));
                return;
        }
    };

    /// <summary><c>POST user/profile/avatar</c>。</summary>
    public RequestHandler SetAvatar() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (!await ValidateAsync(context, input, "tid", ["required", "integer"]))
        {
            return;
        }

        int tid = RequestInput.Int(input, "tid");
        if (tid == 0)
        {
            users.UpdateAvatar(user.Uid, 0);
            await JsonAsync(context, 200,
                Message(0, Trans(context, "user.profile.avatar.success")));
            return;
        }

        TextureRow? texture = textures.FindByTid(tid);
        if (texture is null)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.non-existent")));
            return;
        }

        if (texture.Type == "cape")
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.profile.avatar.wrong-type")));
            return;
        }

        if (!texture.IsPublic && user.Uid != texture.Uploader && !user.IsAdmin)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "skinlib.show.private")));
            return;
        }

        users.UpdateAvatar(user.Uid, tid);
        await JsonAsync(context, 200, Message(0, Trans(context, "user.profile.avatar.success")));
    };

    /// <summary><c>POST user/email-verification</c>。</summary>
    public RequestHandler SendVerificationEmail() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (!option.GetBoolean("require_verification", false))
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.verification.disabled")));
            return;
        }

        SessionStore? session = Session(context);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long lastMail = session?.Get("last_mail_time") is { } value
            ? RequestInput.ToInt(value)
            : 0;
        if (60 + lastMail - now > 0)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.verification.frequent-mail")));
            return;
        }

        if (user.Verified)
        {
            await JsonAsync(context, 200,
                Message(1, Trans(context, "user.verification.verified")));
            return;
        }

        string host = Host(context);
        string signed = SignedUrl.SignedRoute("/auth/verify/" + user.Uid,
            new Dictionary<string, string> { ["user"] = user.Uid.ToString() }, settings.AppKey);
        string url = "http://" + host + signed;

        string sitename = option.GetLocalized("site_name", "Blessing Skin")?.ToString()
            ?? "Blessing Skin";
        string subject = Trans(context, "user.verification.mail.title",
            new Dictionary<string, object?> { ["sitename"] = sitename });
        string message = Trans(context, "user.verification.mail.message",
            new Dictionary<string, object?> { ["sitename"] = sitename });
        string link = Trans(context, "user.verification.mail.reset",
            new Dictionary<string, object?> { ["url"] = url });
        string ignore = Trans(context, "user.verification.mail.ignore");
        string body = $"<p>{message}</p><p>{link}</p><p>{ignore}</p>";

        try
        {
            mailer.Send(user.Email, subject, body);
        }
        catch (Exception e)
        {
            await JsonAsync(context, 200, Message(2,
                Trans(context, "user.verification.failed",
                    new Dictionary<string, object?> { ["msg"] = e.Message })));
            return;
        }

        session?.Put("last_mail_time", now);
        await JsonAsync(context, 200, Message(0, Trans(context, "user.verification.success")));
    };

    // ================================================================== 辅助

    private bool VerifyPassword(string plain, string hash) =>
        cipher.Verify(plain, hash, option.GetString("pwd_salt", null));

    private async Task<bool> ValidateAsync(HttpContext context,
        Dictionary<string, object?> input, string attribute, string[] rules)
    {
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        foreach (string rule in rules)
        {
            v.AddRule(attribute, rule);
        }

        if (v.Passes())
        {
            return true;
        }

        await JsonAsync(context, 422, new Dictionary<string, object?>
        {
            ["message"] = Validator.Summarize(v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value)),
            ["errors"] = v.Errors.ToDictionary(pair => pair.Key, pair => pair.Value),
        });
        return false;
    }

    private static void Logout(HttpContext context)
    {
        SessionStore? session = Session(context);
        if (session is not null)
        {
            new SessionGuard(session, null).Logout();
        }
    }

    private long RemainingSeconds(string? lastSignAt)
    {
        NodaTime.LocalDateTime? last = Parse(lastSignAt);
        if (last is null)
        {
            return -1;
        }

        NodaTime.DateTimeZone zone = PhpDate.Zone(settings.Timezone);
        NodaTime.ZonedDateTime now = NodaTime.SystemClock.Instance.GetCurrentInstant().InZone(zone);
        NodaTime.ZonedDateTime lastZoned = last.Value.InZoneLeniently(zone);
        NodaTime.ZonedDateTime target;

        if (RequestInput.ToBool(option.Get("sign_after_zero")))
        {
            NodaTime.LocalDateTime todayStart = now.Date.AtMidnight();
            target = last.Value > todayStart
                ? todayStart.PlusDays(1).InZoneLeniently(zone)
                : lastZoned;
        }
        else
        {
            target = lastZoned.PlusHours(option.GetInt("sign_gap_time", 24));
        }

        return (long)(target - now).TotalSeconds;
    }

    private static NodaTime.LocalDateTime? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string text = value.Length > 19 ? value[..19] : value;
        NodaTime.Text.ParseResult<NodaTime.LocalDateTime> result =
            NodaTime.Text.LocalDateTimePattern
                .CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss")
                .Parse(text);
        return result.Success ? result.Value : null;
    }

    private static UserRepository.UserRow? CurrentUser(HttpContext context) =>
        AuthMiddleware.CurrentUser(context);

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static string Trans(HttpContext context, string key,
        IReadOnlyDictionary<string, object?> replace) =>
        Translator(context).Trans(key, replace) ?? key;

    private static Dictionary<string, object?> Message(int code, string message) =>
        new(StringComparer.Ordinal) { ["code"] = code, ["message"] = message };

    private static Dictionary<string, object?> Message(int code, string message,
        Dictionary<string, object?> data) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = data,
        };

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }

    // ================================================================== 视图

    /// <summary><c>GET user</c>：用户面板（<c>user/index</c> 模板）。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string path = context.Request.Path.Value ?? "/user";
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context), path, settings.Debug, "user",
            Session(context));
        vars["pageTitle"] = Trans(context, "general.dashboard");
        vars["errors"] = TemplateErrors.Empty;

        string signScore = option.Get("sign_score", "0,0")?.ToString() ?? "0,0";
        string[] range = signScore.Split(',');
        string min = range.Length > 0 ? range[0] : "0";
        string max = range.Length > 1 ? range[1] : "0";
        string returnScore = option.GetBoolean("return_score", false)
            ? Trans(context, "user.score-intro.will-return-score")
            : Trans(context, "user.score-intro.no-return-score");
        string intro = Trans(context, "user.score-intro.introduction",
            new Dictionary<string, object?>
            {
                ["initial_score"] = option.Get("user_initial_score")?.ToString() ?? "",
                ["score-from"] = min,
                ["score-to"] = max,
                ["return-score"] = returnScore,
            });
        vars["score_intro"] = intro;
        vars["score_intro_html"] = intro.Replace("\n", "<br />\n", StringComparison.Ordinal);
        vars["rates"] = new PhpArray()
            .With("storage", option.Get("score_per_storage"))
            .With("player", option.Get("score_per_player"))
            .With("closet", option.Get("score_per_closet_item"));
        vars["announcement"] = PageView.Announcement(Locale(context));
        vars["gridRows"] = PageView.Grid(
            new List<object?>
            {
                PageView.Column("md-7", "user/widgets/email-verification",
                    "user/widgets/dashboard/usage"),
                PageView.Column("md-5", "user/widgets/dashboard/announcement"),
            });
        vars["extra_json"] = PhpJson.Encode(new PhpArray()
            .With("unverified",
                option.GetBoolean("require_verification", false) && !user.Verified), 0);

        await WriteHtml(context, TemplateRenderer.Render("user/index", vars));
    };

    /// <summary><c>GET user/profile</c>：个人资料（<c>user/profile</c> 模板）。</summary>
    public RequestHandler Profile() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string path = context.Request.Path.Value ?? "/user/profile";
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context), path, settings.Debug, "user",
            Session(context));
        vars["pageTitle"] = Trans(context, "general.profile");
        vars["errors"] = TemplateErrors.Empty;
        vars["user_nickname"] = user.Nickname ?? user.Email;
        vars["user_email"] = user.Email;
        vars["user_is_admin"] = user.IsAdmin;
        vars["gridRows"] = PageView.Grid(
            new List<object?>
            {
                PageView.Column("md-6", "user/widgets/profile/avatar",
                    "user/widgets/profile/password"),
                PageView.Column("md-6", "user/widgets/profile/nickname",
                    "user/widgets/profile/email", "user/widgets/profile/delete-account"),
            });

        await WriteHtml(context, TemplateRenderer.Render("user/profile", vars));
    };

    private static async Task WriteHtml(HttpContext context, string html)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }
}
