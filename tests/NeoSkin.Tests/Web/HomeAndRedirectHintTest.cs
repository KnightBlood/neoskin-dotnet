using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;

namespace NeoSkin.Tests.Web;

/// <summary><c>GET /api</c> 与 <c>.well-known/change-password</c> 重定向的契约。</summary>
public sealed class HomeAndRedirectHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-home-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage = { Root = Path.Combine(_dir, "storage") },
        };
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        settings.Secure.Cipher = "BCRYPT";
        settings.Hashing.PasswordRounds = 4;

        Directory.CreateDirectory(settings.Storage.Root);
        var db = new Database(settings);
        new SchemaInstaller(db.Open, settings).Install();
        File.WriteAllText(Path.Combine(settings.Storage.Root, "install.lock"), "");
        db.Execute("INSERT INTO options (option_name, option_value) VALUES ('site_name', @v)",
            ("@v", "NeoSkin Test"));

        _app = NeoSkinApp.Create(["--webhost-urls=http://127.0.0.1:0"], null, settings);
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(_app.Urls.First()),
        };
        _client.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("zh-CN"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public async Task ApiRootReturnsVersionAndSiteName()
    {
        HttpResponseMessage response = await _client.GetAsync("/api");
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("6.0.11_patch",
            json.RootElement.GetProperty("blessing_skin").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("spec").GetInt32());
        Assert.Equal("NeoSkin Test",
            json.RootElement.GetProperty("site_name").GetString());
        Assert.Equal("no-cache, private",
            response.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? values)
                ? values.First() : null);
    }

    [Fact]
    public async Task ChangePasswordRedirectsToProfile()
    {
        HttpResponseMessage response = await _client.GetAsync("/.well-known/change-password");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/user/profile", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task HomeRendersSpaShell()
    {
        HttpResponseMessage response = await _client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("window.blessing = ", html);
        Assert.Contains("id=\"blessing-extra\"", html);
        Assert.Contains("/app/app.7278b90.js", html);
        Assert.Contains("/app/home.fe8cbb3.js", html);
        Assert.Contains("/app/style.1dd8a8a.css", html);
        Assert.Contains("/lang/zh_CN.js", html);
        Assert.Contains("href=\"/auth/login\"", html);
        Assert.Contains("data-toggle=\"dropdown\"", html);
    }

    [Fact]
    public async Task LanguageBundleIsGenerated()
    {
        HttpResponseMessage response = await _client.GetAsync("/lang/zh_CN.js");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("blessing.i18n = ", body);

        string json = body["blessing.i18n = ".Length..];
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.True(parsed.RootElement.EnumerateObject().Any());
    }
}
