using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;

namespace NeoSkin.Tests.Web;

/// <summary>安装向导（setup）与安装锁中间件的契约。</summary>
public sealed class SetupHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-setup-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private string _envFile = null!;

    public async Task InitializeAsync()
    {
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
        Directory.CreateDirectory(_settings.Storage.Root);

        _envFile = Path.Combine(_dir, ".env");
        File.WriteAllText(_envFile, "APP_ENV=production\nDB_CONNECTION=mysql\n");
        Environment.SetEnvironmentVariable("NEOSKIN_ENV_FILE", _envFile);

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
            new System.Net.Http.Headers.StringWithQualityHeaderValue("zh-CN"));
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("NEOSKIN_ENV_FILE", null);
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    private Database Db() => new(_settings);

    [Fact]
    public async Task UninstalledRedirectsToSetup()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/login");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SetupWelcomeAndInfoPagesRender()
    {
        HttpResponseMessage welcome = await _client.GetAsync("/setup");
        string welcomeBody = await welcome.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, welcome.StatusCode);
        Assert.Contains("欢迎", welcomeBody);
        Assert.Contains("href=\"/setup/database\"", welcomeBody);

        HttpResponseMessage info = await _client.GetAsync("/setup/info");
        string infoBody = await info.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Contains("name=\"site_name\"", infoBody);
        Assert.Contains("action=\"/setup/finish\"", infoBody);
        Assert.Contains("name=\"_token\"", infoBody);
    }

    [Fact]
    public async Task SetupLockedAfterInstall()
    {
        await InstallAsync();

        HttpResponseMessage response = await _client.GetAsync("/setup");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("已安装", body);
    }

    [Fact]
    public async Task FinishCreatesSchemaSuperAdminAndLock()
    {
        await PostDatabaseAsync();
        HttpResponseMessage response = await InstallAsync();
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("安装完成", body);

        Assert.True(File.Exists(Path.Combine(_settings.Storage.Root, "install.lock")));
        UserRepository.UserRow? admin = new UserRepository(Db()).FindByEmail("boss@a.c");
        Assert.NotNull(admin);
        Assert.Equal(UserRepository.SuperAdmin, admin!.Permission);
        Assert.True(admin.Verified);

        string? siteName = Db().Scalar(
            "SELECT option_value FROM options WHERE option_name = 'site_name'")?.ToString();
        Assert.Equal("NeoSkin Setup", siteName);

        // .env 被写回 sqlite 配置
        string env = File.ReadAllText(_envFile);
        Assert.Contains("DB_CONNECTION=sqlite", env);
    }

    [Fact]
    public async Task FinishValidationFailureRedirectsBack()
    {
        await _client.GetAsync("/setup/database");
        HttpResponseMessage response = await _client.PostAsync("/setup/finish",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["email"] = "not-an-email",
            }));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("http://" + _client.BaseAddress!.Authority + "/setup/info",
            response.Headers.Location?.ToString());
    }

    private async Task<HttpResponseMessage> InstallAsync()
    {
        // GET 取得会话与 CSRF，并提供当前配置
        HttpResponseMessage page = await _client.GetAsync("/setup/database");
        Assert.True(page.StatusCode is HttpStatusCode.Found or HttpStatusCode.OK);

        return await _client.PostAsync("/setup/finish",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["email"] = "boss@a.c",
                ["nickname"] = "Boss",
                ["password"] = "secret123",
                ["password_confirmation"] = "secret123",
                ["site_name"] = "NeoSkin Setup",
            }));
    }

    /// <summary>提交数据库配置（写回 .env）。</summary>
    private async Task PostDatabaseAsync()
    {
        await _client.GetAsync("/setup/database");
        HttpResponseMessage response = await _client.PostAsync("/setup/database",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["type"] = "sqlite",
                ["host"] = "",
                ["port"] = "",
                ["username"] = "",
                ["password"] = "",
                ["db"] = Path.Combine(_dir, "db.sqlite"),
                ["prefix"] = "",
            }));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("http://" + _client.BaseAddress!.Authority + "/setup/info",
            response.Headers.Location?.ToString());
    }
}
