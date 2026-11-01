using NeoSkin.Web.I18n;

namespace NeoSkin.Tests.Web;

/// <summary><see cref="Translator"/> + <see cref="TranslationFileLoader"/> 的测试（真实语言包）。</summary>
public sealed class TranslatorTest
{
    private static Translator NewTranslator(string locale = "zh_CN", string fallback = "en") =>
        new(new TranslationFileLoader(), locale, fallback);

    [Fact]
    public void LoadsRealLanguageFiles()
    {
        Translator translator = NewTranslator();
        Assert.Equal("登录", translator.Trans("general.login"));
        Assert.Equal("用户中心", translator.Trans("general.user-center"));
        Assert.Equal("Log In", NewTranslator("en").Trans("general.login"));
    }

    [Fact]
    public void FallsBackToFallbackLocale()
    {
        var translator = new Translator(new TranslationFileLoader(), "xx_XX", "en");
        Assert.Equal("Log In", translator.Trans("general.login"));
    }

    [Fact]
    public void MissingKeyReturnsKey()
    {
        Assert.Equal("general.no-such-key", NewTranslator().Trans("general.no-such-key"));
    }

    [Fact]
    public void NestedArraysAndGroupRoot()
    {
        Translator translator = NewTranslator();
        object? features = translator.Get("index.features");
        var map = Assert.IsAssignableFrom<Dictionary<string, object?>>(features);
        Assert.Equal("优势", map["title"]);

        // 不带点号的 key → 整个 group
        object? whole = translator.Get("index");
        var group = Assert.IsAssignableFrom<Dictionary<string, object?>>(whole);
        Assert.True(group.ContainsKey("features"));
    }

    [Fact]
    public void PlaceholdersSupportThreeCasings()
    {
        string output = Translator.MakeReplacements("hi :name / :Name / :NAME",
            new Dictionary<string, object?> { ["name"] = "alice" });
        Assert.Equal("hi alice / Alice / ALICE", output);

        // 最长匹配优先（避免 :name 抢先吃掉 :namex）
        Assert.Equal("x!", Translator.MakeReplacements(":namex!",
            new Dictionary<string, object?> { ["name"] = "A", ["namex"] = "x" }));
    }

    [Fact]
    public void ParseKeyMatchesLaravel()
    {
        Assert.Equal(["*", "general", "login"], Translator.ParseKey("general.login"));
        Assert.Equal(["*", "front-end", null!], Translator.ParseKey("front-end"));
        Assert.Equal(["vendor", "group", "item"], Translator.ParseKey("vendor::group.item"));
        Assert.Equal(["vendor", "group", null!], Translator.ParseKey("vendor::group"));
    }

    [Fact]
    public void HasAndHasForLocale()
    {
        Translator translator = NewTranslator();
        Assert.True(translator.Has("general.login"));
        Assert.True(translator.HasForLocale("general.login", "en"));
        Assert.False(translator.Has("general.no-such-key"));
    }

    [Fact]
    public void SetLocaleSwitchesLanguage()
    {
        Translator translator = NewTranslator();
        translator.SetLocale("en");
        Assert.Equal("Log In", translator.Trans("general.login"));
        translator.SetLocale("zh_CN");
        Assert.Equal("登录", translator.Trans("general.login"));
    }

    [Fact]
    public void LoaderCachesParsedGroups()
    {
        // 每个 locale/group 只读盘解析一次（Laravel FileLoader::$loaded 语义），
        // 避免页面里几十次 trans() 反复解析 YAML。
        var loader = new TranslationFileLoader();

        Dictionary<string, object?> first = loader.Load("zh_CN", "general", "*");
        Dictionary<string, object?> second = loader.Load("zh_CN", "general", "*");

        Assert.Same(first, second);
        Assert.True(first.Count > 0);
    }
}
