using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Tests.Web;

/// <summary>
/// OAuth2 / Passport 端点的 HTTP 契约：token、scopes、clients、Bearer 认证与 scope 校验。
/// </summary>
public sealed class PassportHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-passport-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _uid;

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

        _uid = new UserRepository(db).Insert("user@a.c", "Steve", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");

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

    private Database Db() => new(_settings);

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    private async Task LoginAsync()
    {
        await _client.GetAsync("/auth/captcha");
        var form = new Dictionary<string, string>
        {
            ["_token"] = XsrfToken() ?? "",
            ["identification"] = "user@a.c",
            ["password"] = "secret123",
        };
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private long CreateClient(string secret, bool personal = false)
    {
        var db = Db();
        string now = "2026-01-01 00:00:00";
        return db.InsertReturningId("oauth_clients", "id",
            "INSERT INTO oauth_clients (user_id, name, secret, provider, redirect,"
            + " personal_access_client, password_client, revoked, created_at, updated_at)"
            + " VALUES (NULL,@n,@s,NULL,@r,@pa,0,0,@c,@u)",
            ("@n", "Test Client"), ("@s", PhpBcrypt.Hash(secret, 4)),
            ("@r", "https://client.example/callback"), ("@pa", personal),
            ("@c", now), ("@u", now));
    }

    private async Task<(HttpStatusCode Status, JsonDocument Json)> PostTokenAsync(
        Dictionary<string, string> form)
    {
        HttpResponseMessage response = await _client.PostAsync("/oauth/token",
            new FormUrlEncodedContent(form));
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    // ================================================================== scopes

    [Fact]
    public async Task ScopesRequireAuthentication()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/scopes");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ScopesReturnsDefaults()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/scopes");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        string[] ids = json.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString() ?? "").ToArray();
        Assert.Contains("User.Read", ids);
        Assert.Contains("Player.ReadWrite", ids);
        Assert.Contains("Closet.ReadWrtie", ids); // 源里的历史拼写
    }

    // ================================================================== token

    [Fact]
    public async Task ClientCredentialsIssuesBearerToken()
    {
        long clientId = CreateClient("client-secret");
        (HttpStatusCode status, JsonDocument json) = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.ToString(),
                ["client_secret"] = "client-secret",
                ["scope"] = "Player.Read",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Bearer", json.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(31536000, json.RootElement.GetProperty("expires_in").GetInt64());
        string access = json.RootElement.GetProperty("access_token").GetString() ?? "";
        Assert.Equal(3, access.Split('.').Length);
        Assert.False(json.RootElement.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task ClientCredentialsWithBadSecretIs401()
    {
        long clientId = CreateClient("client-secret");
        (HttpStatusCode status, JsonDocument json) = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.ToString(),
                ["client_secret"] = "wrong",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("invalid_client", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnsupportedGrantIs400()
    {
        (HttpStatusCode status, JsonDocument json) = await PostTokenAsync(
            new Dictionary<string, string> { ["grant_type"] = "password" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unsupported_grant_type",
            json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task InvalidScopeIs400()
    {
        long clientId = CreateClient("client-secret");
        (HttpStatusCode status, JsonDocument json) = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.ToString(),
                ["client_secret"] = "client-secret",
                ["scope"] = "Not.A.Scope",
            });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_scope", json.RootElement.GetProperty("error").GetString());
    }

    // ================================================================== bearer + scope

    private async Task<string> IssueTokenAsync(string scope)
    {
        long clientId = CreateClient("client-secret");
        (HttpStatusCode status, JsonDocument json) = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.ToString(),
                ["client_secret"] = "client-secret",
                ["scope"] = scope,
            });
        Assert.Equal(HttpStatusCode.OK, status);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    [Fact]
    public async Task BearerTokenPassesScopeCheck()
    {
        string token = await IssueTokenAsync("Player.Read");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/players");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        // scope 校验通过 → 进入控制器（已移植，返回玩家列表）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
    }

    [Fact]
    public async Task BearerTokenWithoutScopeIs403()
    {
        string token = await IssueTokenAsync("User.Read");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/players");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Invalid scope(s) provided.",
            json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingTokenIs401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/players");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================== clients

    [Fact]
    public async Task ClientsRequireAuthentication()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/clients");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateClientReturnsPlainSecretOnce()
    {
        await LoginAsync();
        var form = new Dictionary<string, string>
        {
            ["_token"] = XsrfToken() ?? "",
            ["name"] = "My App",
            ["redirect"] = "https://app.example/cb",
        };
        HttpResponseMessage response = await _client.PostAsync("/oauth/clients",
            new FormUrlEncodedContent(form));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("My App", json.RootElement.GetProperty("name").GetString());
        Assert.True(json.RootElement.TryGetProperty("plain_secret", out JsonElement secret));
        Assert.False(string.IsNullOrEmpty(secret.GetString()));
        Assert.False(json.RootElement.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task CreateClientRequiresNameAndRedirect()
    {
        await LoginAsync();
        var form = new Dictionary<string, string>
        {
            ["_token"] = XsrfToken() ?? "",
        };
        HttpResponseMessage response = await _client.PostAsync("/oauth/clients",
            new FormUrlEncodedContent(form));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("name", out _));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("redirect", out _));
    }

    [Fact]
    public async Task CreateClientAcceptsJsonBody()
    {
        await LoginAsync();
        string html = await (await _client.GetAsync("/user")).Content.ReadAsStringAsync();
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"csrf-token\" content=\"([^\"]+)\"");
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/clients")
        {
            Content = new StringContent(
                "{\"name\":\"JSON App\",\"redirect\":\"https://app.example/cb\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", tokenMatch.Groups[1].Value);
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("JSON App", json.RootElement.GetProperty("name").GetString());
        Assert.True(json.RootElement.TryGetProperty("plain_secret", out JsonElement secret));
        Assert.False(string.IsNullOrEmpty(secret.GetString()));
    }

    // ================================================================== refresh

    [Fact]
    public async Task TransientRefreshSetsLaravelTokenCookie()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token/refresh")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
            }),
        };
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("laravel_token=", StringComparison.Ordinal));
        Assert.Equal("Refreshed.", await response.Content.ReadAsStringAsync());
    }
}
