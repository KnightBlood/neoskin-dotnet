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

/// <summary>管理端玩家管理（admin/players）的 HTTP 契约。</summary>
public sealed class AdminPlayersHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-adminplayers-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _adminUid;
    private int _normalUid;
    private int _superUid;

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
        _normalUid = users.Insert("normal@a.c", "Normal", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.2", "Asia/Shanghai");
        _superUid = users.Insert("super@a.c", "Super", 1000,
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

    private int SeedPlayer(int uid, string name)
    {
        var players = new PlayerRepository(Db());
        return players.Insert(uid, name, -1, 0, "2026-01-01 00:00:00");
    }

    [Fact]
    public async Task ListReturnsRawPlayerColumns()
    {
        await LoginAsync();
        SeedPlayer(_normalUid, "Steve");
        SeedPlayer(_normalUid, "Alex");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/players/list?page=1");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(10, root.GetProperty("per_page").GetInt32());
        JsonElement row = root.GetProperty("data")[0];
        Assert.True(row.TryGetProperty("pid", out _));
        Assert.True(row.TryGetProperty("tid_skin", out _));
        Assert.True(row.TryGetProperty("last_modified", out _));
        // PHP LengthAwarePaginator::toArray() 的完整字段
        Assert.True(root.TryGetProperty("first_page_url", out _));
        Assert.True(root.TryGetProperty("last_page_url", out _));
        Assert.True(root.TryGetProperty("next_page_url", out _));
        Assert.True(root.TryGetProperty("prev_page_url", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("links").ValueKind);
        Assert.EndsWith("/admin/players/list", root.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ListSearchesByName()
    {
        await LoginAsync();
        SeedPlayer(_normalUid, "Steve");
        SeedPlayer(_normalUid, "Alex");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/players/list?q=Steve");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("Steve",
            json.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task RenamePlayer()
    {
        await LoginAsync();
        int pid = SeedPlayer(_normalUid, "Before");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/players/" + pid + "/name",
            new Dictionary<string, string> { ["player_name"] = "After" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("After", new PlayerRepository(Db()).FindById(pid)!.Name);
    }

    [Fact]
    public async Task RenameToExistingNameIs422()
    {
        await LoginAsync();
        SeedPlayer(_normalUid, "Taken");
        int pid = SeedPlayer(_normalUid, "Other");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/players/" + pid + "/name",
            new Dictionary<string, string> { ["player_name"] = "Taken" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors")
            .TryGetProperty("player_name", out _));
    }

    [Fact]
    public async Task ChangeOwner()
    {
        await LoginAsync();
        int pid = SeedPlayer(_normalUid, "Mine");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/players/" + pid + "/owner",
            new Dictionary<string, string> { ["uid"] = _adminUid.ToString() });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(_adminUid, new PlayerRepository(Db()).FindById(pid)!.Uid);
    }

    [Fact]
    public async Task ChangeOwnerToUnknownUserIsCodeOne()
    {
        await LoginAsync();
        int pid = SeedPlayer(_normalUid, "Mine");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/players/" + pid + "/owner",
            new Dictionary<string, string> { ["uid"] = "9999" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SetTexture()
    {
        await LoginAsync();
        int pid = SeedPlayer(_normalUid, "Wearer");
        long tid = new TextureRepository(Db()).Insert("Skin", "steve",
            "abababababababababababababababababababababababababababababababab",
            1, _normalUid, true, "2026-01-01 00:00:00");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/players/" + pid + "/textures", new Dictionary<string, string>
            {
                ["tid"] = tid.ToString(),
                ["type"] = "skin",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal((int)tid, new PlayerRepository(Db()).FindById(pid)!.TidSkin);
    }

    [Fact]
    public async Task GuardRejectsHigherPermissionOwner()
    {
        await LoginAsync();
        int pid = SeedPlayer(_superUid, "Protected");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/admin/players/" + pid);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
        Assert.NotNull(new PlayerRepository(Db()).FindById(pid));
    }

    [Fact]
    public async Task DeletePlayer()
    {
        await LoginAsync();
        int pid = SeedPlayer(_normalUid, "Gone");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/admin/players/" + pid);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Null(new PlayerRepository(Db()).FindById(pid));
    }

    [Fact]
    public async Task UnknownPlayerIs404()
    {
        await LoginAsync();
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Delete, "/admin/players/9999");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
