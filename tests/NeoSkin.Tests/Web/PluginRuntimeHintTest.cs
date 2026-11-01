using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 原生插件端到端：启动时发现/加载示例插件，`user_badges` 过滤器进入用户面板。
///
/// <para>示例插件需先构建（见 samples/NeoSkin.Plugin.Sample/README.md）；
/// 产物缺失时测试直接返回。</para>
/// </summary>
public sealed class PluginRuntimeHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-plugin-web-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication? _app;
    private HttpClient? _client;
    private NeoSkinSettings _settings = null!;
    private bool _ready;

    private static string SamplePluginPath
    {
        get
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));
            return Path.Combine(root, "samples", "NeoSkin.Plugin.Sample", "publish",
                "NeoSkin.Plugin.Sample.dll");
        }
    }

    public async Task InitializeAsync()
    {
        if (!File.Exists(SamplePluginPath))
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        _settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage = { Root = Path.Combine(_dir, "storage") },
        };
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        _settings.Secure.Cipher = "BCRYPT";
        _settings.Hashing.PasswordRounds = 4;

        string pluginsDir = Path.Combine(_dir, "plugins");
        string pluginDir = Path.Combine(pluginsDir, "sample");
        Directory.CreateDirectory(pluginDir);
        File.Copy(SamplePluginPath, Path.Combine(pluginDir, "sample.dll"));
        File.WriteAllText(Path.Combine(pluginDir, "package.json"),
            "{\"name\":\"NeoSkin.Plugin.Sample\",\"version\":\"1.0.0\","
            + "\"native\":\"sample.dll\"}");
        string langDir = Path.Combine(pluginDir, "lang", "zh_CN");
        Directory.CreateDirectory(langDir);
        File.WriteAllText(Path.Combine(langDir, "front-end.yml"),
            "sample:\n  hello: 插件你好\n");
        _settings.Plugins.Directory = pluginsDir;

        Directory.CreateDirectory(_settings.Storage.Root);
        File.WriteAllText(Path.Combine(_settings.Storage.Root, "install.lock"), "");

        var db = new Database(_settings);
        var schema = new SchemaInstaller(db.Open, _settings);
        schema.Install();
        new UserRepository(db).Insert("user@a.c", "Steve", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");

        var option = new Option(db, schema, new AppPaths(_dir, _settings));
        var enabled = new PhpArray();
        enabled.Put(0, new PhpArray()
            .With("name", "NeoSkin.Plugin.Sample")
            .With("version", "1.0.0"));
        option.Set("plugins_enabled", PhpJson.Encode(enabled, 0));

        _app = NeoSkinApp.Create(["--webhost-urls=http://127.0.0.1:0"], null, _settings);
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
        })
        {
            BaseAddress = new Uri(_app.Urls.First()),
        };
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");
        _ready = true;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // NativeAOT 共享库在部分平台上卸载后仍持有文件句柄；临时目录留给系统清理
        }
    }

    private string? XsrfToken() =>
        _cookies.GetCookies(_client!.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")
            ?.Value;

    [Fact]
    public async Task NativePluginBadgeAppearsOnUserPanel()
    {
        if (!_ready)
        {
            return;
        }

        await _client!.GetAsync("/auth/captcha");
        HttpResponseMessage login = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "user@a.c",
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        string filtered = NeoSkin.Web.PluginHost.PluginRuntime.Current!.Filter("user_badges", "[]");
        Assert.Contains("SAMPLE", filtered);
        string html = await (await _client.GetAsync("/user")).Content.ReadAsStringAsync();
        Assert.True(NeoSkin.Web.PluginHost.PluginRuntime.Current?.HasPlugins == true, "plugin runtime loaded");
        Assert.Contains("SAMPLE", html);
        Assert.Contains("badge", html);
        Assert.Contains("/app/plugin-sample.css", html);
        Assert.Contains("/app/plugin-sample.js", html);

        string bundle = await (await _client.GetAsync("/lang/zh_CN.js")).Content
            .ReadAsStringAsync();
        Assert.Contains("NeoSkin.Plugin.Sample", bundle);
        Assert.Contains("插件你好", bundle);
    }

    [Fact]
    public async Task CanLoginFilterRejectsBlockedAccount()
    {
        if (!_ready)
        {
            return;
        }

        await _client!.GetAsync("/auth/captcha");
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "blocked@a.c",
                ["password"] = "secret123",
            }));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("blocked by sample plugin",
            json.RootElement.GetProperty("message").GetString());
    }
}
