using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Tests.Web;

/// <summary>插件市场（admin/plugins/market）的契约；注册表由本地 Kestrel 提供。</summary>
public sealed class MarketHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-market-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private WebApplication _registry = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private string _pluginsDir = null!;
    private string _registryBase = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(Path.Combine(_pluginsDir, "foo"));
        File.WriteAllText(Path.Combine(_pluginsDir, "foo", "package.json"),
            "{\"name\":\"foo\",\"title\":\"Foo\",\"version\":\"1.0.0\"}");

        // 本地插件市场
        var registryBuilder = WebApplication.CreateSlimBuilder([]);
        registryBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _registry = registryBuilder.Build();
        _registry.MapGet("/registry_{lang}.json", (string lang) =>
        {
            string baseUrl = _registryBase;
            string json = """
                {"packages":[
                  {"name":"marketplug","title":"Market Plug","version":"1.2.0",
                   "require":{"blessing-skin-server":">=6.0.0"},
                   "dist":{"url":"__DIST__"}},
                  {"name":"foo","title":"Foo","version":"2.0.0",
                   "require":{"blessing-skin-server":">=6.0.0"}}
                ]}
                """.Replace("__DIST__", baseUrl + "/plugin.zip");
            return Results.Text(json, "application/json");
        });
        _registry.MapGet("/plugin.zip", () =>
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry("marketplug/package.json");
                using Stream writer = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(
                    "{\"name\":\"marketplug\",\"title\":\"Market Plug\",\"version\":\"1.2.0\"}");
                writer.Write(bytes);
            }

            return Results.Bytes(stream.ToArray(), "application/zip");
        });
        await _registry.StartAsync();
        _registryBase = _registry.Urls.First();

        _settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage = { Root = Path.Combine(_dir, "storage") },
        };
        _settings.Plugins.Directory = _pluginsDir;
        _settings.Plugins.Registry = _registryBase + "/registry_{lang}.json";
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        _settings.Secure.Cipher = "BCRYPT";
        _settings.Hashing.PasswordRounds = 4;

        Directory.CreateDirectory(_settings.Storage.Root);
        var db = new Database(_settings);
        new SchemaInstaller(db.Open, _settings).Install();
        File.WriteAllText(Path.Combine(_settings.Storage.Root, "install.lock"), "");
        int admin = new UserRepository(db).Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 2 WHERE uid = @u", ("@u", admin));

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
        _client.DefaultRequestHeaders.AcceptLanguage.Add(
            new StringWithQualityHeaderValue("zh-CN"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _registry.StopAsync();
        await _registry.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    private async Task LoginAsync()
    {
        await _client.GetAsync("/auth/captcha");
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "admin@a.c",
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MarketListAnnotatesInstalled()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/plugins/market/list");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.RootElement.GetArrayLength());

        JsonElement market = json.RootElement.EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "marketplug");
        Assert.False(market.GetProperty("installed").GetBoolean());

        JsonElement foo = json.RootElement.EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "foo");
        Assert.Equal("1.0.0", foo.GetProperty("installed").GetString());
        Assert.True(foo.GetProperty("can_update").GetBoolean());
        Assert.Equal(JsonValueKind.Object, foo.GetProperty("dependencies").ValueKind);
    }

    [Fact]
    public async Task DownloadInstallsPlugin()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/market/download")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["name"] = "marketplug",
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.True(File.Exists(Path.Combine(_pluginsDir, "marketplug", "package.json")));
    }

    [Fact]
    public async Task DownloadUnknownIsCodeOne()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/market/download")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["name"] = "no-such-plugin",
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
    }
}
