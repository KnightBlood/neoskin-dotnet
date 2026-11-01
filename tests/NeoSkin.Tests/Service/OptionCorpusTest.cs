using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Service;

/// <summary>
/// <see cref="Option"/> 的语料测试（<c>option.json</c>：default / coerce / get / keys / localized）。
/// </summary>
public sealed class OptionCorpusTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-option-" + Guid.NewGuid().ToString("N"));

    private (Option Option, Database Db, AppPaths Paths) NewSite(string? subdir = null)
    {
        string dir = subdir is null ? _dir : Path.Combine(_dir, subdir);
        Directory.CreateDirectory(dir);
        var settings = new NeoSkinSettings();
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(dir, "db.sqlite");
        settings.Storage.Root = Path.Combine(dir, "storage");
        var db = new Database(settings);
        var schema = new SchemaInstaller(db.Open, settings);
        schema.Install();
        var paths = new AppPaths(dir, settings);
        Directory.CreateDirectory(paths.Storage);
        File.WriteAllText(paths.InstallLock, "");
        return (new Option(db, schema, paths), db, paths);
    }

    // ================================================================== default

    [ConformanceFact]
    public void DefaultsMatchConfigOptions()
    {
        Corpus corpus = Corpus.Require("option");
        Assert.Equal(54, OptionDefaults.All.Count);
        Assert.False(OptionDefaults.Has("max_texture_width"));

        Assert.Equal(54, corpus.Cases("default").Count());
        foreach (JsonNode c in corpus.Cases("default"))
        {
            string key = Corpus.TextOrEmpty(c, "key");
            string? actual = OptionDefaults.Get(key);
            JsonNode? expectedRaw = c["expected_raw"] ?? c["expected"];

            Assert.True(Matches(expectedRaw, actual),
                $"{Corpus.TextOrEmpty(c, "id")}: default({key}) = {actual}");
        }
    }

    // ================================================================== coerce

    [ConformanceFact]
    public void CoerceMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("option");
        foreach (JsonNode c in corpus.Cases("coerce"))
        {
            // stored_php_type 不是 string 时 PHP 原样返回（不打平），例如 int 8192
            JsonNode? storedNode = c["stored"];
            object? actual = storedNode?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => Option.Coerce(storedNode.GetValue<string>()),
                System.Text.Json.JsonValueKind.Number => storedNode.GetValue<long>(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => null,
            };
            Assert.True(Matches(c["expected"], actual),
                $"{Corpus.TextOrEmpty(c, "id")}: coerce({storedNode?.ToJsonString()}) = {actual}");
        }
    }

    // ================================================================== get

    [ConformanceFact]
    public void GetMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("option");
        (Option option, Database db, AppPaths paths) = NewSite();
        _ = db;
        _ = paths;

        foreach (JsonNode c in corpus.Cases("get"))
        {
            string key = Corpus.TextOrEmpty(c, "key");
            object? defaultValue = PhpScalar(c["default"]);

            if (key == "probe")
            {
                option.Set(key, null);
            }

            object? actual = option.Get(key, defaultValue);
            Assert.True(Matches(c["expected"], actual),
                $"{Corpus.TextOrEmpty(c, "id")}: get({key}) = {actual}");
        }
    }

    // ================================================================== keys

    [ConformanceFact]
    public void KeysMatchCorpus()
    {
        Corpus corpus = Corpus.Require("option");
        JsonNode all = corpus.CaseById("keys.all");
        List<string> expected = all["expected"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(expected, OptionDefaults.All.Keys.ToList());

        JsonNode suffixes = corpus.CaseById("keys.localized_suffixes");
        List<string> locales = suffixes["expected"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(locales, Locales.Ids.ToList());
    }

    // ================================================================== localized

    [ConformanceFact]
    public void LocalizedMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("option");

        foreach (JsonNode c in corpus.Cases("localized"))
        {
            (Option option, _, _) = NewSite(Corpus.TextOrEmpty(c, "id"));
            string locale = Corpus.TextOrEmpty(c, "locale");
            Option.CurrentLocale = locale;

            if (c["items"] is JsonObject items)
            {
                option.SetAll(items.Select(pair => new KeyValuePair<string, object?>(
                    pair.Key, PhpScalar(pair.Value))));
            }

            string key = Corpus.TextOrEmpty(c, "key");
            object? defaultValue = PhpScalar(c["default"]);
            object? actual = option.GetLocalized(key, defaultValue);

            string id = Corpus.TextOrEmpty(c, "id");
            Assert.True(Matches(c["expected"], actual),
                $"{id}: localized({key}) = [{actual}] type={actual?.GetType().Name} raw=[{option.Get(key, defaultValue)}] defaults=[{OptionDefaults.Get(key)}] locale=[{Option.CurrentLocale}]");
        }

        Option.ClearCurrentLocale();
    }

    // ================================================================== 写入 / 缓存

    [Fact]
    public void SettingPersistsToDatabaseAndCache()
    {
        (Option option, Database db, AppPaths paths) = NewSite();

        option.Set("site_name", "My Skin");
        Assert.Equal("My Skin", option.Get("site_name"));
        Assert.Equal("My Skin", db.Scalar(
            "SELECT option_value FROM options WHERE option_name = 'site_name'"));

        option.WriteCache();
        Assert.True(File.Exists(paths.OptionsCache));
        Assert.Contains("'site_name' => 'My Skin',", File.ReadAllText(paths.OptionsCache));

        // 新实例优先读缓存
        var settings = new NeoSkinSettings();
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        settings.Storage.Root = Path.Combine(_dir, "storage");
        var db2 = new Database(settings);
        var option2 = new Option(db2, new SchemaInstaller(db2.Open, settings),
            new AppPaths(_dir, settings));
        Assert.Equal("My Skin", option2.Get("site_name"));

        option.ClearCache();
        Assert.False(File.Exists(paths.OptionsCache));
    }

    [Fact]
    public void BooleanAndIntAccessors()
    {
        (Option option, _, _) = NewSite();
        Assert.True(option.GetBoolean("score_per_storage", false));
        Assert.False(option.GetBoolean("require_verification", true));
        Assert.Equal(3, option.GetInt("regs_per_ip", 0));
        Assert.Equal(1000, option.GetInt("user_initial_score", 0));
        Assert.Equal("true", option.GetString("score_per_storage", null));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Option.ClearCurrentLocale();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static object? PhpScalar(JsonNode? node) => node?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
        System.Text.Json.JsonValueKind.Number => node.GetValue<long>(),
        _ => null,
    };

    private static bool Matches(JsonNode? expected, object? actual)
    {
        if (expected is null || expected.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return actual is null;
        }

        return expected.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => actual is true,
            System.Text.Json.JsonValueKind.False => actual is false,
            System.Text.Json.JsonValueKind.String => actual is string s && s == expected.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => actual is long l && l == expected.GetValue<long>(),
            _ => false,
        };
    }
}
