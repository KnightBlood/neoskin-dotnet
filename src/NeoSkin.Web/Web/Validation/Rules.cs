using System.Text;
using System.Text.RegularExpressions;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;

namespace NeoSkin.Web.Validation;

/// <summary>验证码规则（对应源 <c>App\Rules\Captcha</c>）：reCAPTCHA 或会话验证码。</summary>
public sealed class CaptchaRule : IRule
{
    private readonly Option? _option;
    private readonly SessionStore? _session;
    private readonly I18n.Translator? _translator;
    private readonly string _locale;

    public CaptchaRule(Option? option, SessionStore? session, I18n.Translator? translator,
        string? locale)
    {
        _option = option;
        _session = session;
        _translator = translator;
        _locale = string.IsNullOrEmpty(locale) ? "en" : locale;
    }

    public string? Name => null;

    public IReadOnlyList<string> Parameters => [];

    public bool Implicit => false;

    public string? Message
    {
        get
        {
            string secret = _option is null ? "" : Support.PhpString.Cast(_option.Get("recaptcha_secretkey", ""));
            string key = secret.Length > 0 && secret != "null" ? "validation.recaptcha" : "validation.captcha";
            if (_translator is null)
            {
                return key;
            }

            string value = _translator.Trans(key, null, _locale);
            return value == key ? "Incorrect captcha." : value;
        }
    }

    public bool Passes(string attribute, object? value, Validator validator)
    {
        string input = value is null ? "" : value.ToString() ?? "";
        string secret = _option is null ? "" : Support.PhpString.Cast(_option.Get("recaptcha_secretkey", ""));

        if (secret.Length > 0 && secret != "null")
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["secret"] = secret,
                    ["response"] = input,
                });
                string body = client.PostAsync("https://www.recaptcha.net/recaptcha/api/siteverify", content)
                    .GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("\"success\"", StringComparison.Ordinal)
                       && body.Contains("true", StringComparison.Ordinal);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        // 会话验证码：读取后立即删除（pull 语义）
        string? stored = null;
        if (_session is not null)
        {
            stored = _session.Get("captcha") is { } value2 ? Support.PhpString.Cast(value2) : null;
            _session.Forget("captcha");
        }

        return stored is not null
               && Niceize(input).Equals(Niceize(stored), StringComparison.Ordinal);
    }

    private static string Niceize(string? value) =>
        (value ?? "").ToLowerInvariant().Replace('0', 'o').Replace('1', 'l');
}

/// <summary>角色名格式规则（<c>official</c> / <c>cjk</c> / <c>utf8</c> / <c>custom</c>）。</summary>
public sealed class PlayerNameRule(Option? option, I18n.Translator? translator, string? locale) : IRule
{
    private readonly string _locale = string.IsNullOrEmpty(locale) ? "en" : locale;

    public string? Name => null;

    public IReadOnlyList<string> Parameters => [];

    public bool Implicit => false;

    public string? Message
    {
        get
        {
            string rule = option is null ? "official" : Support.PhpString.Cast(option.Get("player_name_rule", "official"));
            string key = "user.player.player-name-rule." + rule;
            if (translator is null)
            {
                return key;
            }

            string value = translator.Trans(key, null, _locale);
            return value == key ? "The player name format is invalid." : value;
        }
    }

    public bool Passes(string attribute, object? value, Validator validator)
    {
        if (value is null)
        {
            return false;
        }

        string name = value.ToString() ?? "";
        string rule = option is null ? "official" : Support.PhpString.Cast(option.Get("player_name_rule", "official"));
        try
        {
            return rule switch
            {
                "official" or "" => Regex.IsMatch(name, "^[A-Za-z0-9_]+$"),
                "cjk" => Regex.IsMatch(name, "^[A-Za-z0-9_§\u4e00-\u9fff]+$"),
                "utf8" => !Regex.IsMatch(name, ".*\\s.*"),
                "custom" => Regex.IsMatch(name, CustomPattern()),
                _ => Regex.IsMatch(name, "^[A-Za-z0-9_]+$"),
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string CustomPattern()
    {
        string regexp = option is null ? "" : Support.PhpString.Cast(option.Get("custom_player_name_regexp", ""));
        return regexp.Length == 0 ? ".*" : regexp;
    }
}
