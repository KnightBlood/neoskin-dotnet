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

/// <summary>皮肤库（列表 / 详情 / 改名 / 类型 / 隐私 / 删除）的 HTTP 契约。</summary>
public sealed class SkinlibHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";
    private const string Hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-skinlib-" + Guid.NewGuid().ToString("N"));
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
        long size = 1, string hash = Hash)
    {
        var textures = new TextureRepository(Db());
        return textures.Insert("Test Skin", type, hash, size,
            uploader == 0 ? _uid : uploader, isPublic, "2026-01-01 00:00:00");
    }

    private void WriteTextureFile(string hash = Hash)
    {
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, hash),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    }

    // ================================================================== 列表 / 详情

    [Fact]
    public async Task ShowAndUploadPagesRenderTemplates()
    {
        long tid = SeedTexture();
        WriteTextureFile();

        HttpResponseMessage show = await _client.GetAsync("/skinlib/show/" + tid);
        string showHtml = await show.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, show.StatusCode);
        Assert.Contains("id=\"previewer\"", showHtml);
        Assert.Contains("id=\"side\"", showHtml);
        Assert.Contains("id=\"modals\"", showHtml);
        Assert.Contains("id=\"blessing-extra\"", showHtml);

        await LoginAsync();
        HttpResponseMessage upload = await _client.GetAsync("/skinlib/upload");
        string uploadHtml = await upload.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Contains("id=\"file-input\"", uploadHtml);
        Assert.Contains("id=\"previewer\"", uploadHtml);
        Assert.Contains("id=\"blessing-extra\"", uploadHtml);
        Assert.DoesNotContain("id=\"modals\"", uploadHtml);
    }

    [Fact]
    public async Task LibraryShowsPublicOnlyForGuests()
    {
        SeedTexture(isPublic: true);
        SeedTexture(isPublic: false, hash: "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/skinlib/list");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(20, json.RootElement.GetProperty("per_page").GetInt32());
    }

    [Fact]
    public async Task LibraryShowsOwnPrivateForLoggedIn()
    {
        SeedTexture(isPublic: true);
        SeedTexture(isPublic: false, hash: "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/skinlib/list");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, json!.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task InfoReturnsTextureFields()
    {
        long tid = SeedTexture();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/skinlib/info/" + tid);

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(tid, root.GetProperty("tid").GetInt64());
        Assert.Equal("Test Skin", root.GetProperty("name").GetString());
        Assert.Equal(Hash, root.GetProperty("hash").GetString());
        Assert.False(root.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task InfoForOthersPrivateIs403()
    {
        long tid = SeedTexture(isPublic: false, uploader: _otherUid);
        await LoginAsync();

        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Get, "/skinlib/info/" + tid);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task InfoMissingIs404()
    {
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Get, "/skinlib/info/9999");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ================================================================== 修改

    [Fact]
    public async Task RenameOwnTexture()
    {
        long tid = SeedTexture();
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/texture/" + tid + "/name", new Dictionary<string, string> { ["name"] = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("Renamed", new TextureRepository(Db()).FindByTid(tid)!.Name);
    }

    [Fact]
    public async Task RenameOthersTextureIs403()
    {
        long tid = SeedTexture(uploader: _otherUid);
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/texture/" + tid + "/name", new Dictionary<string, string> { ["name"] = "Hacked" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ChangeTypeRejectsInvalidValue()
    {
        long tid = SeedTexture();
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/texture/" + tid + "/type", new Dictionary<string, string> { ["type"] = "hat" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("type", out _));
    }

    [Fact]
    public async Task ChangeTypeUpdatesTexture()
    {
        long tid = SeedTexture();
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/texture/" + tid + "/type", new Dictionary<string, string> { ["type"] = "alex" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("alex", new TextureRepository(Db()).FindByTid(tid)!.Type);
    }

    [Fact]
    public async Task PrivacyToggleUpdatesScoreAndResetsPlayers()
    {
        long tid = SeedTexture(isPublic: true, size: 1);
        // 隐私切换只重置「非上传者」的角色（源 ResetPlayers 的 uid <> uploader）
        int pid = new PlayerRepository(Db()).Insert(_otherUid, "Wearer", (int)tid, 0,
            "2026-01-01 00:00:00");
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/texture/" + tid + "/privacy");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.False(new TextureRepository(Db()).FindByTid(tid)!.IsPublic);
        // 1 KB × (10 - 1) × -1 = -9
        Assert.Equal(991, new UserRepository(Db()).FindById(_uid)!.Score);
        Assert.Equal(0, new PlayerRepository(Db()).FindById(pid)!.TidSkin);
    }

    [Fact]
    public async Task DeleteOwnTextureRemovesFileAndRefunds()
    {
        long tid = SeedTexture(isPublic: true, size: 1);
        WriteTextureFile();
        int pid = new PlayerRepository(Db()).Insert(_uid, "Wearer", (int)tid, 0,
            "2026-01-01 00:00:00");
        Db().Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,NULL)",
            ("@u", _otherUid), ("@t", tid));
        await LoginAsync();

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/texture/" + tid);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Null(new TextureRepository(Db()).FindByTid(tid));
        Assert.False(File.Exists(Path.Combine(_settings.Storage.Textures, Hash)));
        Assert.Equal(0, new PlayerRepository(Db()).FindById(pid)!.TidSkin);
        Assert.Equal(0, Db().Long(
            "SELECT COUNT(*) FROM user_closet WHERE texture_tid = @t", ("@t", tid)));
        // return_score：1 KB × 1 = +1
        Assert.Equal(1001, new UserRepository(Db()).FindById(_uid)!.Score);
    }
}
