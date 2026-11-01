using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Service.Cipher;

namespace NeoSkin.Tests.Web;

/// <summary>管理端用户与衣柜（admin/users、admin/closet）的 HTTP 契约。</summary>
public sealed class AdminUsersHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-adminusers-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _adminUid;
    private int _superUid;
    private int _normalUid;

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
        _adminUid = users.Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        _superUid = users.Insert("super@a.c", "Super", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.2", "Asia/Shanghai");
        _normalUid = users.Insert("normal@a.c", "Normal", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.3", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", _adminUid));
        db.Execute("UPDATE users SET permission = 2 WHERE uid = @u", ("@u", _superUid));

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

    private async Task LoginAsync(string email)
    {
        await _client.GetAsync("/auth/captcha");
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["identification"] = email,
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Json)> SendAsync(
        HttpMethod method, string uri, Dictionary<string, string>? form = null)
    {
        string token = XsrfToken() ?? "";
        if (form is not null)
        {
            form["_token"] = token;
        }
        else if (method != HttpMethod.Get)
        {
            uri += (uri.Contains('?') ? "&" : "?") + "_token=" + Uri.EscapeDataString(token);
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd("application/json");
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? null : JsonDocument.Parse(body));
    }

    // ================================================================== 列表 / 修改

    [Fact]
    public async Task ListReturnsRawUserColumns()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/users/list?q=normal");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        JsonElement row = root.GetProperty("data")[0];
        Assert.Equal("normal@a.c", row.GetProperty("email").GetString());
        Assert.Equal(0, row.GetProperty("permission").GetInt32());
        Assert.True(row.TryGetProperty("verified", out _));
    }

    [Fact]
    public async Task ChangeEmailRejectsDuplicate()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/email",
            new Dictionary<string, string> { ["email"] = "super@a.c" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task ChangeEmailSucceeds()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/email",
            new Dictionary<string, string> { ["email"] = "new@a.c" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("new@a.c", new UserRepository(Db()).FindById(_normalUid)!.Email);
    }

    [Fact]
    public async Task ToggleVerification()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Put, "/admin/users/" + _normalUid + "/verification");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.True(new UserRepository(Db()).FindById(_normalUid)!.Verified);
    }

    [Fact]
    public async Task ChangeNickname()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/nickname",
            new Dictionary<string, string> { ["nickname"] = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("Renamed", new UserRepository(Db()).FindById(_normalUid)!.Nickname);
    }

    [Fact]
    public async Task ChangePasswordHashesValue()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/password",
            new Dictionary<string, string> { ["password"] = "new-secret-1" });

        Assert.Equal(HttpStatusCode.OK, status);
        string hash = new UserRepository(Db()).FindById(_normalUid)!.Password;
        Assert.True(PhpPasswordVerify.Verify("new-secret-1", hash));
    }

    [Fact]
    public async Task ChangePasswordTooShortIs422()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/password",
            new Dictionary<string, string> { ["password"] = "short" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task ChangeScore()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/score",
            new Dictionary<string, string> { ["score"] = "42" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(42, new UserRepository(Db()).FindById(_normalUid)!.Score);
    }

    [Fact]
    public async Task AdminCannotGrantAdmin()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/permission",
            new Dictionary<string, string> { ["permission"] = "1" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SuperAdminCanGrantAdmin()
    {
        await LoginAsync("super@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _normalUid + "/permission",
            new Dictionary<string, string> { ["permission"] = "1" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(1, new UserRepository(Db()).FindById(_normalUid)!.Permission);
    }

    [Fact]
    public async Task CannotChangeOwnPermission()
    {
        await LoginAsync("super@a.c");
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Put,
            "/admin/users/" + _superUid + "/permission",
            new Dictionary<string, string> { ["permission"] = "0" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task GuardRejectsHigherPermissionTarget()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/admin/users/" + _superUid);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DeleteUserRemovesPlayers()
    {
        Db().Execute("INSERT INTO players (uid, name, tid_cape, last_modified, tid_skin)"
            + " VALUES (@u,'P',0,'2026-01-01 00:00:00',-1)", ("@u", _normalUid));
        await LoginAsync("admin@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/admin/users/" + _normalUid);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Null(new UserRepository(Db()).FindById(_normalUid));
        Assert.Equal(0, Db().Long("SELECT COUNT(*) FROM players WHERE uid = @u",
            ("@u", _normalUid)));
    }

    // ================================================================== 管理端衣柜

    private async Task<(HttpStatusCode Status, JsonDocument? Json)> SendApiAsync(
        HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-CSRF-TOKEN", XsrfToken());
        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? null : JsonDocument.Parse(body));
    }

    [Fact]
    public async Task AdminClosetListAddRemove()
    {
        long tid = new TextureRepository(Db()).Insert("Fav", "steve",
            "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
            1, _normalUid, true, "2026-01-01 00:00:00");
        await LoginAsync("admin@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/admin/closet/" + _normalUid,
            new Dictionary<string, string> { ["tid"] = tid.ToString() });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.True(json.RootElement.GetProperty("data").TryGetProperty("user", out _));

        (status, json) = await SendApiAsync(HttpMethod.Get, "/api/admin/closet/" + _normalUid);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetArrayLength());
        Assert.Equal("Fav",
            json.RootElement[0].GetProperty("pivot").GetProperty("item_name").GetString());

        (status, json) = await SendAsync(HttpMethod.Delete, "/admin/closet/" + _normalUid,
            new Dictionary<string, string> { ["tid"] = tid.ToString() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());

        (status, json) = await SendApiAsync(HttpMethod.Get, "/api/admin/closet/" + _normalUid);
        Assert.Equal(0, json!.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task AdminClosetUnknownUserIs404()
    {
        await LoginAsync("admin@a.c");
        (HttpStatusCode status, _) = await SendApiAsync(
            HttpMethod.Get, "/api/admin/closet/9999");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
