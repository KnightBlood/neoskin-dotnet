using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;

namespace NeoSkin.Tests.Web;

/// <summary>登录/登出的 HTTP 契约（JSON）：成功、失败计数、校验错误、登出。</summary>
public sealed class AuthLoginHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-login-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _uid;

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
        _settings = settings;

        var db = new Database(settings);
        new SchemaInstaller(db.Open, settings).Install();
        var users = new UserRepository(db);
        _uid = users.Insert("user@a.c", "Steve", 1000, PhpBcrypt.Hash("secret123", 4),
            "127.0.0.1", "Asia/Shanghai");
        Directory.CreateDirectory(settings.Storage.Root);
        File.WriteAllText(Path.Combine(settings.Storage.Root, "install.lock"), "");

        _app = NeoSkinApp.Create(["--webhost-urls=http://127.0.0.1:0"], null, settings);
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

        // 语言：请求带 Accept-Language，登录消息才会是中文
        _client.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("zh-CN"));

        // 触发会话与 XSRF-TOKEN cookie
        await _client.GetAsync("/auth/login");


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

    private async Task<(HttpStatusCode Status, JsonDocument Json)> PostLoginAsync(
        string identification, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["_token"] = XsrfToken() ?? "",
            ["identification"] = identification,
            ["password"] = password,
        };
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(form));
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    // ================================================================== 用例

    [Fact]
    public async Task LoginSuccessReturnsRedirectAndSetsSession()
    {
        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("user@a.c", "secret123");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("http://127.0.0.1:" + new Uri(_app.Urls.First()).Port + "/user",
            json.RootElement.GetProperty("data").GetProperty("redirectTo").GetString());
    }

    [Fact]
    public async Task WrongPasswordReportsFails()
    {
        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("user@a.c", "wrong-pass");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("login_fails").GetInt32());
        Assert.Equal("密码错误", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnknownUserReturnsCodeTwo()
    {
        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("nobody@a.c", "secret123");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("用户不存在", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ShortPasswordIs422()
    {
        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("user@a.c", "123");

        Assert.Equal((HttpStatusCode)422, status);
        Assert.Equal("The given data was invalid.",
            json.RootElement.GetProperty("message").GetString());
        Assert.Equal("The password must be at least 6 characters.",
            json.RootElement.GetProperty("errors").GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task LogoutClearsSession()
    {
        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("user@a.c", "secret123");
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());

        HttpResponseMessage logout = await _client.PostAsync("/auth/logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
            }));
        using JsonDocument body = JsonDocument.Parse(await logout.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());

        // 再登出：未认证（JSON → 401）
        var again = new HttpRequestMessage(HttpMethod.Post, "/auth/logout")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
            }),
        };
        again.Headers.Add("Accept", "application/json");
        HttpResponseMessage denied = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task LoginByPlayerNameWorks()
    {
        var settings = new NeoSkinSettings();
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        var db = new Database(settings);
        new PlayerRepository(db).Insert(_uid, "Alex", -1, 0, "2026-10-26 00:00:00");

        (HttpStatusCode status, JsonDocument json) = await PostLoginAsync("Alex", "secret123");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
    }

    // ================================================================== 视图

    [Fact]
    public async Task LoginPageRendersShell()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/login");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("login-page", html);
        Assert.Contains("<main></main>", html);
        Assert.Contains("id=\"blessing-extra\"", html);
        Assert.Contains("\"tooManyFails\":false", html);
        Assert.Contains("href=\"/auth/register\"", html);
        Assert.Contains("window.blessing", html);
    }

    [Fact]
    public async Task RegisterPageRendersExtra()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/register");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<main></main>", html);
        Assert.Contains("\"player\":true", html);
        Assert.Contains("注册", html);
    }

    [Fact]
    public async Task ForgotPageRenders()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/forgot");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<main></main>", html);
        Assert.Contains("id=\"blessing-extra\"", html);
    }

    [Fact]
    public async Task ResetPageRequiresSignature()
    {
        HttpResponseMessage invalid = await _client.GetAsync("/auth/reset/" + _uid);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);

        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        string url = SignedUrl.TemporarySignedRoute("/auth/reset/" + _uid, expires,
            new Dictionary<string, string> { ["uid"] = _uid.ToString() },
            "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=");

        HttpResponseMessage valid = await _client.GetAsync(url);
        string html = await valid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Contains("<main></main>", html);
        Assert.Contains("Steve", html);
    }

    [Fact]
    public async Task VerifyPageRequiresSignature()
    {
        HttpResponseMessage invalid = await _client.GetAsync("/auth/verify/" + _uid);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
    }

    [Fact]
    public async Task BindPageRendersTemplate()
    {
        (HttpStatusCode status, _) = await PostLoginAsync("user@a.c", "secret123");
        Assert.Equal(HttpStatusCode.OK, status);

        // 已填写邮箱会被 EnsureEmailFilled 重定向到 /user；清空后才是绑定页
        new Database(_settings).Execute("UPDATE users SET email = @e WHERE uid = @u",
            ("@e", ""), ("@u", _uid));

        HttpResponseMessage response = await _client.GetAsync("/auth/bind");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("login-box-msg", html);
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("action=\"/auth/bind\"", html);
        Assert.Contains("name=\"_token\"", html);
        Assert.Contains("window.blessing", html);
    }
    [Fact]
    public async Task LoginAcceptsJsonBody()
    {
        await _client.GetAsync("/auth/captcha");
        string loginHtml = await (await _client.GetAsync("/auth/login")).Content
            .ReadAsStringAsync();
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(loginHtml,
            "name=\"csrf-token\" content=\"([^\"]+)\"");
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new StringContent(
                "{\"identification\":\"user@a.c\",\"password\":\"secret123\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", tokenMatch.Groups[1].Value);
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
    }
}
