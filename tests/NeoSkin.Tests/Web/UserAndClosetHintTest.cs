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
/// 用户中心（profile / score / sign / avatar）与衣柜（分页、增删改）的 HTTP 契约。
/// </summary>
public sealed class UserAndClosetHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";
    private const string Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-user-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _uid;
    private int _otherUid;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage =
            {
                Root = Path.Combine(_dir, "storage"),
                Textures = Path.Combine(_dir, "storage", "textures"),
            },
        };
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        _settings.Secure.Cipher = "BCRYPT";
        _settings.Hashing.PasswordRounds = 4;

        Directory.CreateDirectory(_settings.Storage.Root);
        Directory.CreateDirectory(_settings.Storage.Textures);
        var db = new Database(_settings);
        new SchemaInstaller(db.Open, _settings).Install();
        File.WriteAllText(Path.Combine(_settings.Storage.Root, "install.lock"), "");

        var users = new UserRepository(db);
        _uid = users.Insert("user@a.c", "Steve", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        _otherUid = users.Insert("other@a.c", "Alex", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.2", "Asia/Shanghai");

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

    private async Task LoginAsync(string email = "user@a.c")
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
        HttpMethod method, string uri, Dictionary<string, string>? form = null,
        bool jsonBody = true)
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
        if (!jsonBody || body.Length == 0)
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode, JsonDocument.Parse(body));
    }

    private long SeedTexture(bool isPublic = true, int uploader = 0, string type = "steve",
        string? hash = null)
    {
        var textures = new TextureRepository(Db());
        return textures.Insert("Test", type, hash ?? Hash, 1024,
            uploader == 0 ? _uid : uploader, isPublic, "2026-01-01 00:00:00");
    }

    // ================================================================== user API

    [Fact]
    public async Task ApiUserAcceptsTransientCookie()
    {
        await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-CSRF-TOKEN", XsrfToken());

        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user@a.c", json.RootElement.GetProperty("email").GetString());
        Assert.Equal(1000, json.RootElement.GetProperty("score").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("verified", out _));
        Assert.True(json.RootElement.TryGetProperty("is_dark_mode", out _));
    }

    [Fact]
    public async Task ScoreInfoShape()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/user/score-info");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(1000, root.GetProperty("user").GetProperty("score").GetInt32());
        Assert.Equal(100, root.GetProperty("rate").GetProperty("players").GetInt32());
        Assert.True(root.TryGetProperty("usage", out _));
        Assert.True(root.TryGetProperty("signAfterZero", out _));
        Assert.True(root.TryGetProperty("signGapTime", out _));
    }

    [Fact]
    public async Task SignAwardsScoreOnce()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post, "/user/sign");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("score").GetInt32() >= 1010);

        (status, json) = await SendAsync(HttpMethod.Post, "/user/sign");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToggleDarkModeReturns204()
    {
        await LoginAsync();
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Put, "/user/dark-mode");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.True(new UserRepository(Db()).FindById(_uid)!.DarkMode);
    }

    [Fact]
    public async Task UpdateNickname()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile", new Dictionary<string, string>
            {
                ["action"] = "nickname",
                ["new_nickname"] = "NewName",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("NewName", new UserRepository(Db()).FindById(_uid)!.Nickname);
    }

    [Fact]
    public async Task ChangePasswordWithWrongCurrentPassword()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile", new Dictionary<string, string>
            {
                ["action"] = "password",
                ["current_password"] = "wrong-pass",
                ["new_password"] = "brand-new-pass",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ChangePasswordLogsOut()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile", new Dictionary<string, string>
            {
                ["action"] = "password",
                ["current_password"] = "secret123",
                ["new_password"] = "brand-new-pass",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());

        (status, _) = await SendAsync(HttpMethod.Get, "/user/score-info");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ChangeEmailToExistingReturnsCode1()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile", new Dictionary<string, string>
            {
                ["action"] = "email",
                ["email"] = "other@a.c",
                ["password"] = "secret123",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AdminCannotDeleteAccount()
    {
        Db().Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", _uid));
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile", new Dictionary<string, string>
            {
                ["action"] = "delete",
                ["password"] = "secret123",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SetAvatarRejectsCape()
    {
        await LoginAsync();
        long capeTid = SeedTexture(type: "cape", hash: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/profile/avatar", new Dictionary<string, string>
            {
                ["tid"] = capeTid.ToString(),
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(0, new UserRepository(Db()).FindById(_uid)!.Avatar);
    }

    // ================================================================== closet

    [Fact]
    public async Task ClosetListIsPaginated()
    {
        await LoginAsync();
        long tid = SeedTexture();
        Db().Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,@n)",
            ("@u", _uid), ("@t", tid), ("@n", "My Skin"));

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/user/closet/list?perPage=6");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(1, root.GetProperty("current_page").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("last_page").GetInt32());
        JsonElement row = root.GetProperty("data")[0];
        Assert.Equal("My Skin", row.GetProperty("pivot").GetProperty("item_name").GetString());
        Assert.Equal("default", row.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("links").ValueKind);
    }

    [Fact]
    public async Task ClosetAddAndRemoveUpdatesLikesAndScore()
    {
        await LoginAsync();
        long tid = SeedTexture();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/closet", new Dictionary<string, string>
            {
                ["tid"] = tid.ToString(),
                ["name"] = "Fav",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(2, new TextureRepository(Db()).FindByTid(tid)!.Likes);

        // 重复收藏 → code 1
        (status, json) = await SendAsync(HttpMethod.Post, "/user/closet",
            new Dictionary<string, string> { ["tid"] = tid.ToString(), ["name"] = "Again" });
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());

        (status, json) = await SendAsync(HttpMethod.Delete, "/user/closet/" + tid);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(1, new TextureRepository(Db()).FindByTid(tid)!.Likes);
    }

    [Fact]
    public async Task ClosetAddPrivateTextureOfOthersIsRejected()
    {
        await LoginAsync();
        long tid = SeedTexture(isPublic: false, uploader: _otherUid,
            hash: "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/user/closet", new Dictionary<string, string>
            {
                ["tid"] = tid.ToString(),
                ["name"] = "Private",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ClosetRename()
    {
        await LoginAsync();
        long tid = SeedTexture();
        Db().Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,@n)",
            ("@u", _uid), ("@t", tid), ("@n", "Old"));

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/user/closet/" + tid, new Dictionary<string, string> { ["name"] = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("Renamed",
            Db().Scalar("SELECT item_name FROM user_closet WHERE user_uid=@u AND texture_tid=@t",
                ("@u", _uid), ("@t", tid))?.ToString());
    }

    [Fact]
    public async Task ClosetIdsAreListed()
    {
        await LoginAsync();
        long tid = SeedTexture();
        Db().Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,NULL)",
            ("@u", _uid), ("@t", tid));

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/user/closet/ids");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(tid, json!.RootElement[0].GetInt64());
    }
}
