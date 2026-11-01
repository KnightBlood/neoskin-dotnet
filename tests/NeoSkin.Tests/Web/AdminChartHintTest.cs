using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Web;

/// <summary>管理端折线图（<c>GET admin/chart</c>）的契约。</summary>
public sealed class AdminChartHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-chart-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;

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
        var db = new Database(_settings);
        new SchemaInstaller(db.Open, _settings).Install();
        File.WriteAllText(Path.Combine(_settings.Storage.Root, "install.lock"), "");

        var users = new UserRepository(db);
        int admin = users.Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        int normal = users.Insert("normal@a.c", "Normal", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.2", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", admin));

        string now = PhpDate.Now("Asia/Shanghai");
        string yesterday = DateTime.Parse(now).AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");
        db.Execute("UPDATE users SET register_at = @r WHERE uid = @u",
            ("@r", yesterday), ("@u", admin));
        db.Execute("UPDATE users SET register_at = @r WHERE uid = @u",
            ("@r", now), ("@u", normal));

        db.Execute("INSERT INTO textures (name, type, hash, size, uploader, public, upload_at,"
            + " likes) VALUES ('T','steve',@h,1,@u,1,@a,1)",
            ("@h", new string('a', 64)), ("@u", normal), ("@a", now));

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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    [Fact]
    public async Task ChartReturns31DaysOfCounts()
    {
        await _client.GetAsync("/auth/captcha");
        HttpResponseMessage login = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "admin@a.c",
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/chart");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement root = json.RootElement;
        Assert.Equal(2, root.GetProperty("labels").GetArrayLength());
        Assert.Equal(31, root.GetProperty("xAxis").GetArrayLength());
        Assert.Equal(2, root.GetProperty("data").GetArrayLength());

        string today = DateOnly.Parse(
            PhpDate.Now("Asia/Shanghai")[..10]).ToString("yyyy/M/d");
        Assert.Equal(today, root.GetProperty("xAxis")[30].GetString());

        // 今天注册 1 个、昨天注册 1 个
        Assert.Equal(1, root.GetProperty("data")[0][30].GetInt32());
        Assert.Equal(1, root.GetProperty("data")[0][29].GetInt32());
        // 今天上传 1 个
        Assert.Equal(1, root.GetProperty("data")[1][30].GetInt32());
    }

    [Fact]
    public async Task AdminPagesRenderLayouts()
    {
        await _client.GetAsync("/auth/captcha");
        HttpResponseMessage login = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "admin@a.c",
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        string dashboard = await (await _client.GetAsync("/admin")).Content
            .ReadAsStringAsync();
        Assert.Contains("id=\"chart-users-registration\"", dashboard);
        Assert.Contains("id=\"chart-textures-upload\"", dashboard);
        Assert.Contains("content-wrapper", dashboard);
        Assert.Contains("window.blessing", dashboard);

        string status = await (await _client.GetAsync("/admin/status")).Content
            .ReadAsStringAsync();
        Assert.Contains("运行状态", status);
        Assert.Contains("数据库", status);

        string users = await (await _client.GetAsync("/admin/users")).Content
            .ReadAsStringAsync();
        Assert.Contains("container-fluid", users);
        Assert.Contains("id=\"blessing-extra\"", users);
        Assert.Contains("\"currentUser\"", users);

        string i18n = await (await _client.GetAsync("/admin/i18n")).Content
            .ReadAsStringAsync();
        Assert.Contains("id=\"table\"", i18n);
        Assert.Contains("action=\"/admin/i18n\"", i18n);
        Assert.Contains("name=\"_token\"", i18n);

        foreach (string path in new[]
                 {
                     "/admin/players", "/admin/reports", "/admin/plugins/manage",
                     "/admin/plugins/market",
                 })
        {
            HttpResponseMessage page = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("main-sidebar", await page.Content.ReadAsStringAsync());
        }

        string oauth = await (await _client.GetAsync("/user/oauth/manage")).Content
            .ReadAsStringAsync();
        Assert.Contains("OAuth2 应用", oauth);
    }

    [Fact]
    public async Task SkinlibPageRendersShell()
    {
        HttpResponseMessage response = await _client.GetAsync("/skinlib");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("content-wrapper", html);
        Assert.Contains("/lang/zh_CN.js", html);
    }
    [Fact]
    public async Task CommunicationsPageRenders()
    {
        await _client.GetAsync("/auth/captcha");
        await _client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "admin@a.c",
                ["password"] = "secret123",
            }));

        string html = await (await _client.GetAsync("/admin/communications")).Content
            .ReadAsStringAsync();
        Assert.Contains("站内信发布与草稿记录", html);
        Assert.Contains("id=\"communication-history\"", html);
        Assert.Contains("新建站内信", html);
    }
    [Fact]
    public async Task UpdatePageRenders()
    {
        new Database(new NeoSkinSettings
        {
            Database = { Connection = "sqlite", Name = Path.Combine(_dir, "db.sqlite") },
        }).Execute("UPDATE users SET permission = 2 WHERE email = 'admin@a.c'");
        await _client.GetAsync("/auth/captcha");
        await _client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = "admin@a.c",
                ["password"] = "secret123",
            }));

        string page = await (await _client.GetAsync("/admin/update")).Content
            .ReadAsStringAsync();
        Assert.Contains("当前版本", page);
        Assert.Contains("ZIP 运行包更新", page);
        Assert.Contains("name=\"package\"", page);
        Assert.Contains("version-changelog-1", page);
        Assert.Contains("6.0.11_patch", page);
    }
}
