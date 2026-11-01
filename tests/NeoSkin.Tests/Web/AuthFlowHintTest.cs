using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Cookie;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 注册 / 忘记密码 / 重置密码 / 验证邮箱 / 验证码的 HTTP 契约。
/// </summary>
public sealed class AuthFlowHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-authflow-" + Guid.NewGuid().ToString("N"));
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
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    // ================================================================== 辅助

    private Database Db() => new(_settings);

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    /// <summary>触发会话并返回会话里存入的验证码（从会话文件读回）。</summary>
    private async Task<string> FetchCaptchaAsync()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/captcha");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string encrypted = _cookies.GetCookies(_client.BaseAddress!)
            .FirstOrDefault(c => c.Name == "BS_SESSION")?.Value
            ?? throw new InvalidOperationException("no session cookie");

        byte[] key = _settings.KeyBytes()!;
        var cookies = new EncryptCookies(new PhpEncrypter(key, "aes-256-cbc"));
        var bag = new PhpArray();
        bag.Put("BS_SESSION", encrypted);
        string id = (string)cookies.Decrypt(bag).Get("BS_SESSION")!;

        string file = Path.Combine(_settings.Storage.Root, "framework", "sessions", id);
        PhpUnserialize.Result parsed = PhpUnserialize.Parse(File.ReadAllText(file));
        var attributes = (PhpArray)parsed.Value!;
        return (string)attributes.Get("captcha")!;
    }

    private async Task<(HttpStatusCode Status, JsonDocument Json)> PostAsync(string uri,
        Dictionary<string, string> form, string? query = null)
    {
        form["_token"] = XsrfToken() ?? "";
        string url = uri + (query is null ? "" : "?" + query);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    private int SeedUser(string email, string password)
    {
        var users = new UserRepository(Db());
        return users.Insert(email, "Player", 1000, PhpBcrypt.Hash(password, 4),
            "127.0.0.1", "Asia/Shanghai");
    }

    // ================================================================== 注册

    [Fact]
    public async Task RegisterShortPasswordIs422()
    {
        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/register",
            new Dictionary<string, string>
            {
                ["email"] = "a@b.com",
                ["password"] = "123",
                ["captcha"] = captcha,
                ["player_name"] = "Tester",
            });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task RegisterDuplicateEmailIs422()
    {
        SeedUser("taken@example.com", "secret123");
        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/register",
            new Dictionary<string, string>
            {
                ["email"] = "taken@example.com",
                ["password"] = "secret123",
                ["captcha"] = captcha,
                ["player_name"] = "Tester",
            });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task RegisterWrongCaptchaIs422()
    {
        await _client.GetAsync("/auth/captcha");
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/register",
            new Dictionary<string, string>
            {
                ["email"] = "c@d.com",
                ["password"] = "secret123",
                ["captcha"] = "definitely-wrong",
                ["player_name"] = "Tester",
            });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("captcha", out _));
    }

    [Fact]
    public async Task RegisterSucceedsAndCreatesPlayer()
    {
        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/register",
            new Dictionary<string, string>
            {
                ["email"] = "new@example.com",
                ["password"] = "secret123",
                ["captcha"] = captcha,
                ["player_name"] = "Newbie",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("注册成功，正在跳转...",
            json.RootElement.GetProperty("message").GetString());

        var users = new UserRepository(Db());
        var players = new PlayerRepository(Db());
        UserRepository.UserRow? created = users.FindByEmail("new@example.com");
        Assert.NotNull(created);
        Assert.Equal("Newbie", created!.Nickname);
        Assert.Equal(1000, created.Score);
        Assert.NotNull(players.FindByName("Newbie"));
    }

    [Fact]
    public async Task RegisterRespectsRegsPerIp()
    {
        SeedUser("first@example.com", "secret123");
        SeedUser("second@example.com", "secret123");
        SeedUser("third@example.com", "secret123");

        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/register",
            new Dictionary<string, string>
            {
                ["email"] = "fourth@example.com",
                ["password"] = "secret123",
                ["captcha"] = captcha,
                ["player_name"] = "Tester",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
    }

    // ================================================================== 忘记密码

    [Fact]
    public async Task ForgotUnknownEmailReturnsCode1()
    {
        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/forgot",
            new Dictionary<string, string>
            {
                ["email"] = "nobody@example.com",
                ["captcha"] = captcha,
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("该邮箱尚未注册", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ForgotInvalidEmailIs422()
    {
        string captcha = await FetchCaptchaAsync();
        (HttpStatusCode status, JsonDocument json) = await PostAsync("/auth/forgot",
            new Dictionary<string, string>
            {
                ["email"] = "not-an-email",
                ["captcha"] = captcha,
            });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("email", out _));
    }

    // ================================================================== 重置密码

    [Fact]
    public async Task ResetWithoutSignatureIsForbidden()
    {
        await _client.GetAsync("/auth/captcha");
        int uid = SeedUser("reset@example.com", "secret123");
        (HttpStatusCode status, _) = await PostAsync("/auth/reset/" + uid,
            new Dictionary<string, string> { ["password"] = "brand-new-pass" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task ResetWithValidSignatureChangesPassword()
    {
        await _client.GetAsync("/auth/captcha");
        int uid = SeedUser("reset2@example.com", "secret123");
        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        string url = SignedUrl.TemporarySignedRoute("/auth/reset/" + uid, expires,
            new Dictionary<string, string> { ["uid"] = uid.ToString() }, _settings.AppKey);

        // url = /auth/reset/{uid}?expires=..&uid=..&signature=..
        int q = url.IndexOf('?');
        string path = url[..q];
        string query = url[(q + 1)..];

        (HttpStatusCode status, JsonDocument json) = await PostAsync(path,
            new Dictionary<string, string> { ["password"] = "brand-new-pass" }, query);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());

        var users = new UserRepository(Db());
        UserRepository.UserRow? row = users.FindById(uid);
        Assert.NotNull(row);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
    }

    // ================================================================== 验证码

    [Fact]
    public async Task CaptchaReturnsJpegWithoutCache()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/captcha");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(body.Length > 100, "must be a real JPEG: " + body.Length);
        Assert.Equal(0xFF, body[0]);
        Assert.Equal(0xD8, body[1]);
    }

    // ================================================================== 验证邮箱

    [Fact]
    public async Task VerifyWithoutSignatureIsForbidden()
    {
        await _client.GetAsync("/auth/captcha");
        int uid = SeedUser("verify@example.com", "secret123");
        (HttpStatusCode status, _) = await PostAsync("/auth/verify/" + uid,
            new Dictionary<string, string> { ["email"] = "verify@example.com" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task VerifyWithValidSignatureRedirects()
    {
        await _client.GetAsync("/auth/captcha");
        int uid = SeedUser("verify2@example.com", "secret123");
        string url = SignedUrl.SignedRoute("/auth/verify/" + uid,
            new Dictionary<string, string> { ["user"] = uid.ToString() }, _settings.AppKey);
        int q = url.IndexOf('?');
        string path = url[..q];
        string query = url[(q + 1)..];

        HttpResponseMessage response = await _client.PostAsync(path + "?" + query,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["email"] = "verify2@example.com",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var users = new UserRepository(Db());
        Assert.True(users.FindById(uid)!.Verified);
    }
}
