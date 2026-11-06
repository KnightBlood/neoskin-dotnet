using System.Security.Cryptography;
using System.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using NeoSkin.Web.Auth;
using NeoSkin.Web.Db;
using NeoSkin.Web.Error;
using NeoSkin.Web.Event;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.PluginHost;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\AuthController</c> 的完整移植。
///
/// <p>JSON 契约（前端走 <c>Accept: application/json</c>）：</p>
/// <ul>
///   <li>{@code handleLogin} / {@code logout}：已落地；</li>
///   <li>{@code handleRegister} / {@code handleForgot} / {@code handleReset} /
///       {@code handleVerify} / {@code fillEmail}：本阶段落地；</li>
///   <li>{@code captcha}：生成会话验证码并返回 JPEG 图片。</li>
/// </ul>
///
/// <p>HTML 视图（login/register/forgot/reset/verify/bind）等模板引擎（Phase 7）接入后补齐。</p>
/// </summary>
public sealed class AuthController(
    UserRepository users,
    PlayerRepository players,
    ICipher cipher,
    Option option,
    NeoSkin.Web.Config.NeoSkinSettings settings,
    IMailer mailer,
    LoginFailCounter failures)
{
    private const string LastRequestedPath = "last_requested_path";
    private const int PasswordMin = 6;
    private const int PasswordMax = 32;
    private readonly string _appKey = settings.AppKey;
    private readonly string _timezone = settings.Timezone;

    // ================================================================== 登录

    /// <summary><c>handleLogin()</c>。</summary>
    public RequestHandler HandleLogin() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        (string identification, string password, string captcha) = await ReadCredentialsAsync(context);

        Dictionary<string, List<string>> errors = ValidateCredentials(identification, password);
        if (errors.Count > 0)
        {
            await ValidationFailedAsync(context, errors);
            return;
        }

        string? rejected = ApplyCanLogin(identification, password);
        if (!string.IsNullOrEmpty(rejected))
        {
            await JsonAsync(context, 200, Message(1, rejected));
            return;
        }

        string clientIp = ClientIp(context);
        string failKey = LoginFailCounter.KeyForIp(clientIp);
        int loginFails = failures.Get(failKey);

        UserRepository.UserRow? user = FindUser(identification);
        EventRuntime.Fire(new Events.UserTryToLogin(identification,
            LooksLikeEmail(identification) ? "email" : "username"));

        // 失败次数 > 3 时强制验证码（源先校验 captcha，再判断用户是否存在）
        if (loginFails > 3)
        {
            var captchaInput = new Dictionary<string, object?> { ["captcha"] = captcha };
            var captchaValidator = new Validator(captchaInput, Translator(context), Locale(context),
                option, null);
            captchaValidator.AddRule("captcha", "required");
            captchaValidator.AddRule("captcha",
                new CaptchaRule(option, session, Translator(context), Locale(context)));
            if (!captchaValidator.Passes())
            {
                await ValidationFailedAsync(context, captchaValidator.Errors.ToDictionary(
                    pair => pair.Key, pair => pair.Value));
                return;
            }
        }

        if (user is null)
        {
            await JsonAsync(context, 200, Message(2, Trans(context, "auth.validation.user")));
            return;
        }

        if (!VerifyPassword(user, password))
        {
            int next = failures.Increment(failKey);
            session?.Put(SessionGuard.LoginFailsKey, (long)next);
            await JsonAsync(context, 200, Message(1, Trans(context, "auth.validation.password"),
                new Dictionary<string, object?> { ["login_fails"] = next }));
            return;
        }

        // 成功
        session?.Forget(SessionGuard.LoginFailsKey);
        failures.Forget(failKey);
        if (session is null)
        {
            context.Response.StatusCode = 500;
            return;
        }

        new SessionGuard(session, users).Login(user.Uid);
        EventRuntime.Fire(new Events.UserLoggedIn(user));

        object? intended = session.Get(LastRequestedPath);
        if (intended is not null)
        {
            session.Forget(LastRequestedPath);
        }

        string redirectTo = intended is null
            ? "http://" + Host(context) + "/user"
            : PhpString.Cast(intended);

        await JsonAsync(context, 200, Message(0, Trans(context, "auth.login.success"),
            new Dictionary<string, object?> { ["redirectTo"] = redirectTo }));
    };

    /// <summary><c>logout()</c>。</summary>
    public RequestHandler Logout() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        if (session is not null)
        {
            new SessionGuard(session, users).Logout();
        }

        await JsonAsync(context, 200, Message(0, Trans(context, "auth.logout.success")));
    };

    // ================================================================== 注册

    /// <summary><c>handleRegister()</c>。</summary>
    public RequestHandler HandleRegister() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        string locale = Locale(context);

        // can_register 钩子：{@see ApplyCanRegister()}（源里 BlessingFilter）

        // 读取输入
        string email = await InputStringAsync(context, "email");
        string password = await InputStringAsync(context, "password");
        string captcha = await InputStringAsync(context, "captcha");
        string playerName = await InputStringAsync(context, "player_name");
        string nickname = await InputStringAsync(context, "nickname");

        bool withPlayer = option.GetBoolean("register_with_player_name", true);

        var input = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["password"] = password,
            ["captcha"] = captcha,
        };
        if (withPlayer)
        {
            input["player_name"] = playerName;
        }
        else
        {
            input["nickname"] = nickname;
        }

        var v = new Validator(input, Translator(context), locale, option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("email", "required");
        v.AddRule("email", "email");
        v.AddRule("email", "unique:users");
        v.AddRule("password", "required");
        v.AddRule("password", "min:8");
        v.AddRule("password", "max:32");
        v.AddRule("captcha", "required");
        v.AddRule("captcha", new CaptchaRule(option, session, Translator(context), locale));

        if (withPlayer)
        {
            v.AddRule("player_name", "required");
            v.AddRule("player_name", new PlayerNameRule(option, Translator(context), locale));
            int nameMin = option.GetInt("player_name_length_min", 3);
            int nameMax = option.GetInt("player_name_length_max", 16);
            v.AddRule("player_name", "min:" + nameMin);
            v.AddRule("player_name", "max:" + nameMax);
        }
        else
        {
            v.AddRule("nickname", "required");
            v.AddRule("nickname", "max:255");
        }

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        // 角色名重复检查
        if (withPlayer && players.CountByName(playerName) > 0)
        {
            await JsonAsync(context, 200, Message(1,
                Trans(context, "user.player.add.repeated")));
            return;
        }

        // regs_per_ip（按客户端 IP 统计已注册用户数）
        string clientIp = ClientIp(context);
        int regsPerIp = option.GetInt("regs_per_ip", 3);
        int registered = users.CountByIp(clientIp);
        if (registered >= regsPerIp)
        {
            await JsonAsync(context, 200, Message(1, Trans(context, "auth.register.max",
                new Dictionary<string, object?> { ["regs"] = regsPerIp })));
            return;
        }

        // 创建用户
        string hashed = ApplyUserPassword(cipher.Hash(password,
            option.GetString("pwd_salt", null)));
        int initialScore = option.GetInt("user_initial_score", 1000);
        string nicknameVal = withPlayer ? playerName : nickname;
        string timezone = _timezone;
        int uid = users.Insert(email, nicknameVal, initialScore, hashed, clientIp, timezone);

        // 创建角色
        if (withPlayer)
        {
            int pid = players.Insert(uid, playerName, -1, 0, PhpDate.Now(timezone));
            var createdPlayer = players.FindById(pid);
            if (createdPlayer is not null)
            {
                EventRuntime.Fire(new Events.PlayerWasAdded(createdPlayer));
            }
        }

        // 登录
        if (session is not null)
        {
            new SessionGuard(session, users).Login(uid);
        }

        // 发送验证邮件（发信失败只记日志，不影响注册结果）
        try
        {
            var created = users.FindById(uid);
            if (created is not null)
            {
                EventRuntime.Fire(new Events.UserRegistered(created));
                await SendVerificationEmailIfRequiredAsync(context, created, email, locale);
            }
        }
        catch
        {
            // 事件观察者与发信不影响注册结果
        }

        await JsonAsync(context, 200, Message(0, Trans(context, "auth.register.success")));
    };

    // ================================================================== 忘记密码

    /// <summary><c>handleForgot()</c>。</summary>
    public RequestHandler HandleForgot() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        SessionStore? session = Session(context);

        string email = await InputStringAsync(context, "email");
        string captcha = await InputStringAsync(context, "captcha");

        var v = new Validator(new Dictionary<string, object?>
        {
            ["email"] = email,
            ["captcha"] = captcha,
        }, Translator(context), locale, option, null);
        v.AddRule("email", "required");
        v.AddRule("email", "email");
        v.AddRule("captcha", "required");
        v.AddRule("captcha", new CaptchaRule(option, session, Translator(context), locale));

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        // 频率限制：180 秒内同一 IP 不能重复请求
        string clientIp = ClientIp(context);
        string lastMailKey = "last_mail_" + Sha1Hex(clientIp);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long lastTime = 0;
        if (session is not null && session.Get(lastMailKey) is long last)
        {
            lastTime = last;
        }

        long remain = 180 + lastTime - now;
        if (remain > 0)
        {
            await JsonAsync(context, 200, Message(2,
                Trans(context, "auth.forgot.frequent-mail")));
            return;
        }

        UserRepository.UserRow? user = users.FindByEmail(email);
        if (user is null)
        {
            await JsonAsync(context, 200, Message(1,
                Trans(context, "auth.forgot.unregistered")));
            return;
        }

        // 临时签名链接：auth.reset/{uid}，1 小时有效
        long expiry = now + 3600;
        string path = "/auth/reset/" + user.Uid;
        string url = SignedUrl.TemporarySignedRoute(path, expiry,
            new Dictionary<string, string> { ["uid"] = user.Uid.ToString() }, _appKey);
        string fullUrl = "http://" + Host(context) + url;

        string sitename = option.GetLocalized("site_name", "Blessing Skin")?.ToString() ?? "Blessing Skin";
        string subject = Trans(context, "user.verification.mail.title",
            new Dictionary<string, object?> { ["sitename"] = sitename });
        string body = BuildForgotMailBody(option, Translator(context), locale, fullUrl, sitename);

        try
        {
            mailer.Send(email, subject, body);
        }
        catch (Exception e)
        {
            await JsonAsync(context, 200, Message(2, Trans(context, "auth.forgot.failed",
                new Dictionary<string, object?> { ["msg"] = e.Message })));
            return;
        }

        if (session is not null)
        {
            session.Put(lastMailKey, now);
        }

        await JsonAsync(context, 200, Message(0, Trans(context, "auth.forgot.success")));
    };

    // ================================================================== 重置密码

    /// <summary><c>handleReset({uid})</c>。</summary>
    public RequestHandler HandleReset() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        string path = context.Request.Path.Value ?? "/";

        // 解析查询参数（含 signature / expires）
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in context.Request.Query)
        {
            query[kv.Key] = kv.Value.ToString() ?? "";
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignedUrl.HasValidSignature(path, query, now, _appKey))
        {
            await AbortAsync(context, 403, Trans(context, "auth.reset.invalid"));
            return;
        }

        string uidRaw = parameters.TryGetValue("uid", out var uidStr) ? uidStr! : "";
        if (!int.TryParse(uidRaw, out int uidInt))
        {
            uidInt = 0;
        }

        var v = new Validator(new Dictionary<string, object?>
        {
            ["password"] = await InputStringAsync(context, "password"),
        }, Translator(context), locale, option, null);
        v.AddRule("password", "required");
        v.AddRule("password", "min:8");
        v.AddRule("password", "max:32");

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string password = await InputStringAsync(context, "password");
        string hashed = ApplyUserPassword(cipher.Hash(password,
            option.GetString("pwd_salt", null)));
        users.ChangePassword(uidInt, hashed);

        await JsonAsync(context, 200, Message(0, Trans(context, "auth.reset.success")));
    };

    // ================================================================== 验证邮箱

    /// <summary><c>handleVerify({user})</c>。</summary>
    public RequestHandler HandleVerify() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        string path = context.Request.Path.Value ?? "/";

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in context.Request.Query)
        {
            query[kv.Key] = kv.Value.ToString() ?? "";
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignedUrl.HasValidSignature(path, query, now, _appKey))
        {
            await AbortAsync(context, 403, Trans(context, "auth.verify.invalid"));
            return;
        }

        string userRaw = parameters.TryGetValue("user", out var userStr) ? userStr! : "";
        if (!int.TryParse(userRaw, out int uidInt))
        {
            uidInt = 0;
        }

        string email = await InputStringAsync(context, "email");

        var v = new Validator(new Dictionary<string, object?>
        {
            ["email"] = email,
        }, Translator(context), locale, option, null);
        v.AddRule("email", "required");
        v.AddRule("email", "email");

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        UserRepository.UserRow? user;
        try
        {
            user = users.FindById(uidInt);
        }
        catch
        {
            context.Response.StatusCode = 500;
            return;
        }

        if (user is null || !string.Equals(email, user.Email, StringComparison.Ordinal))
        {
            SessionStore? session = Session(context);
            if (session is not null)
            {
                session.Flash("errorMessage", Trans(context, "auth.verify.not-matched"));
            }

            string previous = context.Request.Headers["Referer"].ToString();
            if (string.IsNullOrEmpty(previous))
            {
                previous = session?.PreviousUrl()?.ToString() ?? "";
            }
            if (string.IsNullOrEmpty(previous))
            {
                previous = "http://" + Host(context) + "/";
            }

            context.Response.StatusCode = 302;
            context.Response.Headers["Location"] = previous;
            context.Response.Headers["Cache-Control"] = "no-cache, private";
            return;
        }

        users.UpdateVerified(uidInt, true);

        context.Response.StatusCode = 302;
        context.Response.Headers["Location"] = "http://" + Host(context) + "/user";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    };

    // ================================================================== 验证码

    /// <summary><c>captcha()</c>：生成 5 位随机短语，存入会话，返回 100x34 JPEG。</summary>
    public RequestHandler Captcha() => async (context, route, parameters) =>
    {
        const string Charset = "abcdefghijklmnpqrstuvwxyz123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var sb = new StringBuilder(5);
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[5];
        rng.GetBytes(bytes);
        for (int i = 0; i < 5; i++)
        {
            sb.Append(Charset[bytes[i] % Charset.Length]);
        }

        string phrase = sb.ToString();
        SessionStore? session = Session(context);
        if (session is not null)
        {
            session.Put("captcha", phrase);
        }

        // 生成 100x34 JPEG（源用 java.awt 绘制；这里用 ImageSharp 渲染同等效果）
        using var image = new Image<Rgba32>(100, 34, new Rgba32(220, 220, 220));
        if (TryPickFontFamily(out FontFamily family))
        {
            Font font = family.CreateFont(20, FontStyle.Bold);
            image.Mutate(ctx => ctx.DrawText(phrase, font, new Rgba32(50, 50, 50),
                new PointF(15, 6)));
        }

        context.Response.ContentType = "image/jpeg";
        context.Response.Headers["Cache-Control"] = "no-store";
        await image.SaveAsJpegAsync(context.Response.Body);
    };

    // ================================================================== 绑定邮箱

    /// <summary><c>fillEmail()</c> (POST /auth/bind)：绑定/修改邮箱，成功 302 到 /user。</summary>
    public RequestHandler FillEmail() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        string locale = Locale(context);

        string email = await InputStringAsync(context, "email");

        var v = new Validator(new Dictionary<string, object?>
        {
            ["email"] = email,
        }, Translator(context), locale, option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("email", "required");
        v.AddRule("email", "email");
        v.AddRule("email", "unique:users");

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        int uid = session is not null
            ? new SessionGuard(session, users).Id() ?? 0
            : 0;

        users.UpdateEmail(uid, email);

        context.Response.StatusCode = 302;
        context.Response.Headers["Location"] = "http://" + Host(context) + "/user";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    };

    // ================================================================== 视图

    /// <summary><c>login()</c>：登录页。</summary>
    public RequestHandler Login() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        string html = Web.Views.AuthPage.Login(option, Translator(context), Locale(context),
            settings, session, ClientIp(context), failures, Host(context),
            context.Request.Path.Value ?? "/auth/login");
        await WriteHtmlAsync(context, 200, html);
    };

    /// <summary><c>register()</c>：注册页。</summary>
    public RequestHandler Register() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        string html = Web.Views.AuthPage.Register(option, Translator(context), Locale(context),
            settings, session, Host(context), context.Request.Path.Value ?? "/auth/register");
        await WriteHtmlAsync(context, 200, html);
    };

    /// <summary><c>forgot()</c>：忘记密码页。</summary>
    public RequestHandler Forgot() => async (context, route, parameters) =>
    {
        string mailer = settings.Mail.Mailer;
        if (string.IsNullOrEmpty(mailer))
        {
            await AbortAsync(context, 500,
                Trans(context, "auth.forgot.disabled"));
            return;
        }

        SessionStore? session = Session(context);
        string html = Web.Views.AuthPage.Forgot(option, Translator(context), Locale(context),
            settings, session, Host(context), context.Request.Path.Value ?? "/auth/forgot");
        await WriteHtmlAsync(context, 200, html);
    };

    /// <summary><c>reset({uid})</c>：重置密码页（需有效签名）。</summary>
    public RequestHandler Reset() => async (context, route, parameters) =>
    {
        string uid = parameters.TryGetValue("uid", out string? raw) ? raw ?? "" : "";
        string path = context.Request.Path.Value ?? "/";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> entry
                 in context.Request.Query)
        {
            query[entry.Key] = entry.Value.ToString() ?? "";
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignedUrl.HasValidSignature(path, query, now, _appKey))
        {
            await AbortAsync(context, 403, Trans(context, "auth.reset.invalid"));
            return;
        }

        UserRepository.UserRow? user = int.TryParse(uid, out int uidInt)
            ? users.FindById(uidInt)
            : null;
        string? nickname = user?.Nickname ?? user?.Email;
        SessionStore? session = Session(context);
        string html = Web.Views.AuthPage.Reset(option, Translator(context), Locale(context),
            settings, session, nickname, Host(context), context.Request.Path.Value ?? "/auth/reset");
        await WriteHtmlAsync(context, 200, html);
    };

    /// <summary><c>verify({user})</c>：邮箱验证页（需有效签名与开启验证）。</summary>
    public RequestHandler Verify() => async (context, route, parameters) =>
    {
        if (!option.GetBoolean("require_verification", false))
        {
            await AbortAsync(context, 403, Trans(context, "user.verification.disabled"));
            return;
        }

        string path = context.Request.Path.Value ?? "/";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> entry
                 in context.Request.Query)
        {
            query[entry.Key] = entry.Value.ToString() ?? "";
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignedUrl.HasValidSignature(path, query, now, _appKey))
        {
            await AbortAsync(context, 403, Trans(context, "auth.verify.invalid"));
            return;
        }

        SessionStore? session = Session(context);
        string html = Web.Views.AuthPage.Verify(option, Translator(context), Locale(context),
            settings, session, Host(context), context.Request.Path.Value ?? "/auth/verify");
        await WriteHtmlAsync(context, 200, html);
    };

    // ================================================================== 内部辅助

    private async Task SendVerificationEmailIfRequiredAsync(HttpContext context,
        UserRepository.UserRow user, string email, string locale)
    {
        if (!option.GetBoolean("require_verification", false))
        {
            return;
        }

        string path = "/auth/verify/" + user.Uid;
        // 非临时签名（无 expires）
        string signed = SignedUrl.SignedRoute(path,
            new Dictionary<string, string> { ["user"] = user.Uid.ToString() }, _appKey);
        string fullUrl = "http://" + Host(context) + signed;

        string sitename = option.GetLocalized("site_name", "Blessing Skin")?.ToString() ?? "Blessing Skin";
        string subject = Trans(context, "user.verification.mail.title",
            new Dictionary<string, object?> { ["sitename"] = sitename });
        string body = BuildVerifyMailBody(option, Translator(context), locale, fullUrl, sitename);

        try
        {
            mailer.Send(email, subject, body);
        }
        catch
        {
            // 发信失败只记日志，不阻断注册流程
        }
    }

    private static string BuildForgotMailBody(Option option, Translator translator, string locale,
        string url, string sitename)
    {
        string body = translator.Trans("auth.forgot.mail.message",
            new Dictionary<string, object?> { ["sitename"] = sitename }, locale)?.ToString()
            ?? $"有人申请重置您在 {sitename} 的密码。";
        string link = translator.Trans("auth.forgot.mail.reset",
            new Dictionary<string, object?> { ["url"] = url }, locale)?.ToString()
            ?? $"点击此链接重置密码：{url}";
        string ignore = translator.Trans("auth.forgot.mail.ignore", null, locale)?.ToString()
            ?? "如果您没有进行上述操作，请忽略这封邮件。";
        string notice = translator.Trans("auth.forgot.notice", null, locale)?.ToString() ?? "";

        return $"<p>{body}</p><p>{link}</p><p>{ignore}</p>"
            + (notice.Length == 0 ? "" : $"<p>{notice}</p>");
    }

    private static string BuildVerifyMailBody(Option option, Translator translator, string locale,
        string url, string sitename)
    {
        string message = translator.Trans("user.verification.mail.message",
            new Dictionary<string, object?> { ["sitename"] = sitename }, locale)?.ToString()
            ?? $"欢迎注册 {sitename}";
        string link = translator.Trans("user.verification.mail.reset",
            new Dictionary<string, object?> { ["url"] = url }, locale)?.ToString()
            ?? $"点击验证：{url}";
        string ignore = translator.Trans("user.verification.mail.ignore", null, locale)?.ToString()
            ?? "如果您未注册，请忽略此邮件。";

        return $"<p>{message}</p><p>{link}</p><p>{ignore}</p>";
    }

    /// <summary>选取一个可用字体族（Arial → DejaVu Sans → 系统首个）。</summary>
    private static bool TryPickFontFamily(out FontFamily family)
    {
        if (SystemFonts.TryGet("Arial", out family)
            || SystemFonts.TryGet("DejaVu Sans", out family)
            || SystemFonts.TryGet("Liberation Sans", out family))
        {
            return true;
        }

        family = default;
        foreach (FontFamily candidate in SystemFonts.Families)
        {
            family = candidate;
            return true;
        }

        return false;
    }

    private static string Sha1Hex(string input)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private UserRepository.UserRow? FindUser(string identification)
    {
        if (LooksLikeEmail(identification))
        {
            return users.FindByEmail(identification);
        }

        PlayerRow? player = players.FindByName(identification);
        return player is null ? null : users.FindById(player.Uid);
    }

    /// <summary><c>filter_var($id, FILTER_VALIDATE_EMAIL)</c> 的近似实现。</summary>
    internal static bool LooksLikeEmail(string? value)
    {
        if (value is null)
        {
            return false;
        }

        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return false;
        }

        int dot = value.IndexOf('.', at);
        return dot > at + 1 && dot < value.Length - 1;
    }

    private bool VerifyPassword(UserRepository.UserRow user, string raw)
    {
        bool passed = cipher.Verify(raw, user.Password, option.GetString("pwd_salt", null));
        return ApplyVerifyPassword(passed, raw, user);
    }

    private static async Task<string> InputStringAsync(HttpContext context, string name) =>
        RequestInput.String(await RequestInput.BagAsync(context), name);

    private static async Task<(string Identification, string Password, string Captcha)>
        ReadCredentialsAsync(HttpContext context)
    {
        Dictionary<string, object?> bag = await RequestInput.BagAsync(context);
        return (RequestInput.String(bag, "identification"),
            RequestInput.String(bag, "password"), RequestInput.String(bag, "captcha"));
    }

    private static Dictionary<string, List<string>> ValidateCredentials(string identification,
        string password)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(identification))
        {
            errors["identification"] = ["The identification field is required."];
        }

        if (string.IsNullOrEmpty(password))
        {
            errors["password"] = ["The password field is required."];
        }
        else if (password.Length < PasswordMin)
        {
            errors["password"] = ["The password must be at least " + PasswordMin + " characters."];
        }
        else if (password.Length > PasswordMax)
        {
            errors["password"] = ["The password may not be greater than " + PasswordMax + " characters."];
        }

        return errors;
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key)?.ToString() ?? key;

    private static string Trans(HttpContext context, string key,
        IReadOnlyDictionary<string, object?> replace) =>
        Translator(context).Trans(key, replace)?.ToString() ?? key;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    /// <summary>
    /// 客户端 IP：<c>X-Forwarded-For</c> 首段，回退 TCP 对端地址。
    /// （源用 Whip 做可信代理白名单，这部分仍未移植，见 PORT-NOTES。）
    /// </summary>
    private static string? ApplyCanLogin(string identification, string password)
    {
        PluginRuntime? runtime = PluginRuntime.Current;
        if (runtime is null || !runtime.HasPlugins)
        {
            return null;
        }

        string json = PhpJson.Encode(new PhpArray()
            .With("identification", identification)
            .With("password", password), 0);
        string result = runtime.Filter("can_login", json);
        if (string.Equals(result, json, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return PhpJson.Decode(result) is string reason && reason.Length > 0
                ? reason
                : null;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static string? ApplyCanRegister()
    {
        PluginRuntime? runtime = PluginRuntime.Current;
        if (runtime is null || !runtime.HasPlugins)
        {
            return null;
        }

        string result = runtime.Filter("can_register", "{}");
        if (result == "{}")
        {
            return null;
        }

        try
        {
            return PhpJson.Decode(result) is string reason && reason.Length > 0
                ? reason
                : null;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool ApplyVerifyPassword(bool passed, string raw,
        UserRepository.UserRow user)
    {
        PluginRuntime? runtime = PluginRuntime.Current;
        if (runtime is null || !runtime.HasPlugins)
        {
            return passed;
        }

        string json = PhpJson.Encode(new PhpArray()
            .With("passed", passed)
            .With("password", raw)
            .With("user", new PhpArray()
                .With("uid", user.Uid)
                .With("email", user.Email)
                .With("nickname", user.Nickname)), 0);
        string result = runtime.Filter("verify_password", json);
        if (string.Equals(result, json, StringComparison.Ordinal))
        {
            return passed;
        }

        try
        {
            return PhpJson.Decode(result) is bool value ? value : passed;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return passed;
        }
    }

    private static string ApplyUserPassword(string hashed) =>
        PluginFilters.ApplyString("user_password", hashed, PluginRuntime.Current);

    private static string ClientIp(HttpContext context)
    {
        string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        string ip;
        if (forwarded.Length > 0)
        {
            ip = forwarded.Split(',')[0].Trim();
        }
        else
        {
            ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
        }

        return PluginFilters.ApplyString("client_ip", ip, PluginRuntime.Current);
    }

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

    /// <summary><c>ValidationException</c>：前端按 422 处理。</summary>
    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        var body = new Dictionary<string, object?>
        {
            ["message"] = "The given data was invalid.",
            ["errors"] = errors,
        };
        await JsonAsync(context, 422, body);
    }

    /// <summary><c>response()-&gt;json($data)</c>。</summary>
    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }

    private static async Task WriteHtmlAsync(HttpContext context, int status, string html)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    /// 源站 <c>abort()</c> / <c>WebException</c>：JSON 请求返回
    /// <c>{message, exception:true, trace:[]}</c>，其余返回状态页。
    /// </summary>
    private static async Task AbortAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-cache, private";

        if (ContentNegotiation.ExpectsJson(context))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(ErrorPayload.Json(message));
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        string title = status + " " + HttpStatus.Text(status);
        await context.Response.WriteAsync(
            "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>" + title
            + "</title></head><body><h1>" + title + "</h1><p>"
            + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>");
    }
}
