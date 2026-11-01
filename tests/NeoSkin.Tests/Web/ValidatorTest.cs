using NeoSkin.Web.I18n;
using NeoSkin.Web.Validation;

namespace NeoSkin.Tests.Web;

/// <summary>Laravel <c>Validator</c> 子集的语义测试（含语言包消息与通配符展开）。</summary>
public sealed class ValidatorTest
{
    private static readonly TranslationFileLoader Loader = new();

    private static Translator Zh() => new(Loader, "zh_CN", "en");

    private static Validator NewValidator(Dictionary<string, object?> data,
        IPresenceVerifier? verifier = null) =>
        new(data, Zh(), "zh_CN", null, verifier);

    // ================================================================== 基础规则

    [Fact]
    public void RequiredAndFilled()
    {
        var validator = NewValidator(new Dictionary<string, object?> { ["name"] = "  " });
        validator.AddRule("name", "required");
        Assert.False(validator.Passes());
        Assert.Equal("名称 不能为空。", validator.Errors["name"][0]);

        var filled = NewValidator(new Dictionary<string, object?> { ["name"] = "" });
        filled.AddRule("name", "filled");
        Assert.False(filled.Passes());

        var ok = NewValidator(new Dictionary<string, object?> { ["name"] = "x" });
        ok.AddRule("name", "required");
        ok.AddRule("name", "filled");
        Assert.True(ok.Passes());
    }

    [Fact]
    public void MissingFieldSkippedUnlessImplicit()
    {
        var validator = NewValidator(new Dictionary<string, object?>());
        validator.AddRule("nickname", "min:3");
        Assert.True(validator.Passes());

        var required = NewValidator(new Dictionary<string, object?>());
        required.AddRule("nickname", "required");
        Assert.False(required.Passes());
    }

    [Fact]
    public void EmailValidation()
    {
        var good = NewValidator(new Dictionary<string, object?> { ["email"] = "a@b.co" });
        good.AddRule("email", "email");
        Assert.True(good.Passes());

        foreach (string bad in new[] { "a@b", "a b@c.d", "@b.c", "a@@b.c", "a@b..c" })
        {
            var validator = NewValidator(new Dictionary<string, object?> { ["email"] = bad });
            validator.AddRule("email", "email");
            Assert.False(validator.Passes(), bad);
        }
    }

    [Fact]
    public void MinMaxUseStringLengthOrNumber()
    {
        var tooShort = NewValidator(new Dictionary<string, object?> { ["password"] = "abc" });
        tooShort.AddRule("password", "min:6");
        Assert.False(tooShort.Passes());
        Assert.Contains("至少为 6 个字符", tooShort.Errors["password"][0]);

        var cjk = NewValidator(new Dictionary<string, object?> { ["password"] = "密码密码密码" });
        cjk.AddRule("password", "min:6");
        Assert.True(cjk.Passes());

        var num = NewValidator(new Dictionary<string, object?> { ["score"] = 5 });
        num.AddRule("score", "numeric");
        num.AddRule("score", "min:10");
        Assert.False(num.Passes());
        Assert.Equal("score 必须大于等于 10。", num.Errors["score"][0]);

        var tooLong = NewValidator(new Dictionary<string, object?> { ["name"] = "abcdef" });
        tooLong.AddRule("name", "max:5");
        Assert.False(tooLong.Passes());
    }

    [Fact]
    public void ConfirmedInInRegexUrlJsonBoolean()
    {
        var confirmed = NewValidator(new Dictionary<string, object?>
        {
            ["password"] = "secret",
            ["password_confirmation"] = "other",
        });
        confirmed.AddRule("password", "confirmed");
        Assert.False(confirmed.Passes());

        var inRule = NewValidator(new Dictionary<string, object?> { ["type"] = "steve" });
        inRule.AddRule("type", "in:steve,alex");
        Assert.True(inRule.Passes());

        var regex = NewValidator(new Dictionary<string, object?> { ["name"] = "abc123" });
        regex.AddRule("name", "regex:^[a-z]+$");
        Assert.False(regex.Passes());

        var url = NewValidator(new Dictionary<string, object?> { ["u"] = "ftp://example.com/a" });
        url.AddRule("u", "url");
        Assert.True(url.Passes());

        var urlScheme = NewValidator(new Dictionary<string, object?> { ["u"] = "ftp://example.com" });
        urlScheme.AddRule("u", "url:http,https");
        Assert.False(urlScheme.Passes());

        var json = NewValidator(new Dictionary<string, object?> { ["j"] = "{\"a\":1}" });
        json.AddRule("j", "json");
        Assert.True(json.Passes());

        var boolean = NewValidator(new Dictionary<string, object?> { ["b"] = "on" });
        boolean.AddRule("b", "boolean");
        Assert.True(boolean.Passes());

        var array = NewValidator(new Dictionary<string, object?> { ["a"] = new List<object?> { 1 } });
        array.AddRule("a", "array");
        Assert.True(array.Passes());
    }

    [Fact]
    public void UniqueExistsAndRequiredIfUseVerifier()
    {
        var verifier = new FakeVerifier(new Dictionary<string, int> { ["users:email:a@b.c"] = 1 });
        var unique = NewValidator(new Dictionary<string, object?> { ["email"] = "a@b.c" }, verifier);
        unique.AddRule("email", "unique:users");
        Assert.False(unique.Passes());

        var exists = NewValidator(new Dictionary<string, object?> { ["uid"] = 42 }, verifier);
        exists.AddRule("uid", "exists:users,uid");
        Assert.False(exists.Passes());

        verifier.Counts["users:uid:42"] = 1;
        var existsAgain = NewValidator(new Dictionary<string, object?> { ["uid"] = 42 }, verifier);
        existsAgain.AddRule("uid", "exists:users,uid");
        Assert.True(existsAgain.Passes());

        var requiredIf = NewValidator(new Dictionary<string, object?>
        {
            ["kind"] = "email",
            ["email"] = "",
        });
        requiredIf.AddRule("email", "required_if:kind,email");
        Assert.False(requiredIf.Passes());

        var notRequired = NewValidator(new Dictionary<string, object?> { ["kind"] = "all" });
        notRequired.AddRule("email", "required_if:kind,email");
        Assert.True(notRequired.Passes());
    }

    // ================================================================== 通配符 / 自定义

    [Fact]
    public void WildcardsExpandToConcreteKeys()
    {
        var blocks = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "" },
            new Dictionary<string, object?> { ["type"] = "image" },
        };
        var validator = NewValidator(new Dictionary<string, object?> { ["blocks"] = blocks });
        validator.AddRule("blocks.*.type", "required");

        Assert.False(validator.Passes());
        Assert.True(validator.Errors.ContainsKey("blocks.0.type"));
        Assert.False(validator.Errors.ContainsKey("blocks.1.type"));
        Assert.False(validator.Errors.ContainsKey("blocks.*.type"));
    }

    [Fact]
    public void CustomRulesRunWithOwnMessage()
    {
        var validator = NewValidator(new Dictionary<string, object?> { ["captcha"] = "wrong" });
        validator.AddRule("captcha", new AlwaysFailingRule("验证码不正确。"));

        Assert.False(validator.Passes());
        Assert.Equal("验证码不正确。", validator.Errors["captcha"][0]);
    }

    [Fact]
    public void ValidatedReturnsDeclaredFields()
    {
        var validator = NewValidator(new Dictionary<string, object?>
        {
            ["email"] = "a@b.c",
            ["password"] = "secret",
            ["ignored"] = "x",
        });
        validator.AddRule("email", "required");
        validator.AddRule("password", "required");

        Assert.True(validator.Passes());
        Dictionary<string, object?> validated = validator.Validated();
        Assert.Equal(["email", "password"], validated.Keys);
    }

    private sealed class FakeVerifier(Dictionary<string, int> counts) : IPresenceVerifier
    {
        public Dictionary<string, int> Counts { get; } = counts;

        public int GetCount(string table, string column, object? value, object? excludeId, string idColumn) =>
            Counts.TryGetValue(table + ":" + column + ":" + value, out int count) ? count : 0;
    }

    private sealed class AlwaysFailingRule(string message) : IRule
    {
        public string? Name => null;
        public IReadOnlyList<string> Parameters => [];
        public bool Implicit => false;
        public string? Message => message;
        public bool Passes(string attribute, object? value, Validator validator) => false;
    }
}
