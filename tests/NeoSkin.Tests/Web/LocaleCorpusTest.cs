using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Http;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 语言层语料测试（<c>locale.json</c>）：Locales 表、Accept-Language 解析、
/// <c>DetectLanguagePrefer</c> 的解析与 cookie、以及它保留的源站缺陷。
/// </summary>
public sealed class LocaleCorpusTest
{
    // ================================================================== const

    [ConformanceFact]
    public void ConstantsMatchPhp()
    {
        Corpus corpus = Corpus.Require("locale");
        Assert.Equal("zh_CN", Locales.AppLocale);
        Assert.Equal("en", Locales.FallbackLocale);
        Assert.Equal("locale", DetectLanguagePrefer.CookieName);

        foreach (JsonNode c in corpus.Cases("const"))
        {
            string id = Corpus.TextOrEmpty(c, "id");
            switch (id)
            {
                case "const.app_locale":
                    Assert.Equal("zh_CN", Locales.AppLocale);
                    break;
                case "const.fallback_locale":
                    Assert.Equal("en", Locales.FallbackLocale);
                    break;
                case "const.cookie_name":
                    Assert.Equal("locale", DetectLanguagePrefer.CookieName);
                    break;
                case "const.cookie_lifetime_minutes":
                    Assert.Equal(DetectLanguagePrefer.CookieLifetimeMinutes,
                        c["expected"]!.GetValue<int>());
                    break;
            }
        }
    }

    // ================================================================== lookup

    [ConformanceFact]
    public void LookupMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("locale");
        foreach (JsonNode c in corpus.Cases("lookup"))
        {
            object? key = c["key"] switch
            {
                null => null,
                JsonValue value when value.TryGetValue(out string? s) => s,
                JsonValue value when value.TryGetValue(out long l) => l,
                _ => null,
            };
            bool expectedHas = Corpus.Bool(c, "expected_has", false);
            JsonNode? expectedGet = c["expected_get"];

            Assert.True(expectedHas == Locales.Has(key), Corpus.TextOrEmpty(c, "id") + " has");
            object? actual = Locales.Get(key);

            if (expectedGet is null || expectedGet.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                Assert.Null(actual);
            }
            else if (expectedGet.GetValueKind() == System.Text.Json.JsonValueKind.Object)
            {
                if (key is null)
                {
                    var table = Assert.IsAssignableFrom<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(actual);
                    foreach (KeyValuePair<string, JsonNode?> entry in expectedGet.AsObject())
                    {
                        Assert.True(table.ContainsKey(entry.Key));
                        foreach (KeyValuePair<string, JsonNode?> field in entry.Value!.AsObject())
                        {
                            Assert.Equal(field.Value!.GetValue<string>(), table[entry.Key][field.Key]);
                        }
                    }
                }
                else
                {
                    var info = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(actual);
                    foreach (KeyValuePair<string, JsonNode?> field in expectedGet.AsObject())
                    {
                        Assert.Equal(field.Value!.GetValue<string>(), info[field.Key]);
                    }
                }
            }
        }
    }

    // ================================================================== accept

    [ConformanceFact]
    public void AcceptLanguageMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("locale");
        foreach (JsonNode c in corpus.Cases("accept"))
        {
            // 没有 Accept-Language 头时复现 Symfony Request::create() 的默认值
            string? header = Corpus.Text(c, "header") ?? AcceptLanguage.DefaultAcceptLanguage;
            List<string> expected = c["expected_languages"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ToList();

            Assert.True(expected.SequenceEqual(AcceptLanguage.GetLanguages(header)),
                Corpus.TextOrEmpty(c, "id") + " languages");
            Assert.Equal(Corpus.Text(c, "expected_preferred"),
                AcceptLanguage.GetPreferredLanguage(header));
        }
    }

    // ================================================================== resolve

    [ConformanceFact]
    public void ResolveMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("locale");
        foreach (JsonNode c in corpus.Cases("resolve"))
        {
            JsonNode input = c["input"]!;
            var request = new DetectLanguagePrefer.Input(
                input["query"]?["lang"]?.GetValue<string>(),
                input["cookies"]?["locale"]?.GetValue<string>(),
                Corpus.Text(input, "accept_language"),
                input["route_middleware"] is JsonArray middleware
                    ? middleware.Select(n => n!.GetValue<string>()).ToList()
                    : null,
                Corpus.Text(input, "initial_locale"));

            DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(request);
            Assert.Equal(Corpus.Text(c, "expected_locale"), result.Locale);

            JsonNode? expectedCookie = c["expected_cookie"];
            if (expectedCookie is null || expectedCookie.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                Assert.Null(result.Cookie);
            }
            else
            {
                Assert.NotNull(result.Cookie);
                Assert.Equal(expectedCookie["name"]!.GetValue<string>(), result.Cookie!.Name);
                Assert.Equal(expectedCookie["value"]!.GetValue<string>(), result.Cookie.Value);
                Assert.Equal(expectedCookie["max_age"]!.GetValue<int>(), result.Cookie.MaxAgeSeconds);
                Assert.Equal(expectedCookie["path"]!.GetValue<string>(), result.Cookie.Path);
                Assert.Equal(expectedCookie["http_only"]!.GetValue<bool>(), result.Cookie.HttpOnly);
                Assert.Equal(expectedCookie["secure"]!.GetValue<bool>(), result.Cookie.Secure);
                Assert.Equal(expectedCookie["same_site"]!.GetValue<string>(), result.Cookie.SameSite);
            }
        }
    }

    // ================================================================== 源站缺陷（不要修）

    [Fact]
    public void FallbackIsEnNotAppLocale()
    {
        DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input(null, null, null, ["web"], "zh_CN"));
        Assert.Equal("en", result.Locale);

        DetectLanguagePrefer.Result initialEn = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input(null, null, null, ["web"], "en"));
        Assert.Equal("en", initialEn.Locale);

        DetectLanguagePrefer.Result initialRu = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input(null, null, null, ["web"], "ru_RU"));
        Assert.Equal("en", initialRu.Locale);
    }

    [Fact]
    public void EmptyLangBlocksCookieAndAccept()
    {
        DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input("", "ru_RU", "zh-CN", ["web"], "zh_CN"));
        Assert.Equal("en", result.Locale);
    }

    [Fact]
    public void ApiRouteSkipsCookieButStillResolves()
    {
        DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input("ru_RU", null, null, ["api"], "zh_CN"));
        Assert.Equal("ru_RU", result.Locale);
        Assert.Null(result.Cookie);
    }

    [Fact]
    public void AliasIsNormalizedInCookie()
    {
        DetectLanguagePrefer.Result result = DetectLanguagePrefer.Resolve(
            new DetectLanguagePrefer.Input(null, "en_US", null, ["web"], "zh_CN"));
        Assert.Equal("en", result.Locale);
        Assert.Equal("en", result.Cookie!.Value);
    }

    [Fact]
    public void ZhHansIsAlias()
    {
        Assert.Equal("zh_CN", Locales.Resolve("zh_HANS_CN"));
        Assert.Equal("zh_CN", Locales.Resolve("zh_HANS_CN"));
    }
}
