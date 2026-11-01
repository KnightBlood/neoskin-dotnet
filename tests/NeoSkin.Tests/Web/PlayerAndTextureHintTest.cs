using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 玩家管理（用户中心 JSON）与 Minecraft 纹理契约（<c>/{player}.json</c>、<c>/textures/{hash}</c>）。
/// </summary>
public sealed class PlayerAndTextureHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-player-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(HttpStatusCode Status, JsonDocument Json)> SendJsonAsync(
        HttpMethod method, string uri, Dictionary<string, string>? form = null)
    {
        string token = XsrfToken() ?? "";
        if (form is not null)
        {
            form["_token"] = token;
        }
        else if (method != HttpMethod.Get)
        {
            // 没有请求体时用查询串携带 _token（CSRF 中间件同时读 form 与 query）
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
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    private int SeedPlayer(int uid, string name, int tidSkin = -1, int tidCape = 0)
    {
        var players = new PlayerRepository(Db());
        return players.Insert(uid, name, tidSkin, tidCape, "2026-01-01 00:00:00");
    }

    private long SeedTexture(string type = "steve", int uploader = 0)
    {
        var textures = new TextureRepository(Db());
        return textures.Insert("Test Skin", type, Hash, 1024, uploader == 0 ? _uid : uploader,
            true, "2026-01-01 00:00:00");
    }

    // ================================================================== 玩家

    [Fact]
    public async Task PlayerListReturnsOwnPlayers()
    {
        await LoginAsync();
        SeedPlayer(_uid, "Steve1");
        SeedPlayer(_uid, "Steve2");
        SeedPlayer(_otherUid, "Other");

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Get, "/user/player/list");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(2, json.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task UserPagesRenderShells()
    {
        await LoginAsync();
        SeedPlayer(_uid, "Steve1");

        HttpResponseMessage playerPage = await _client.GetAsync("/user/player");
        string playerHtml = await playerPage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, playerPage.StatusCode);
        Assert.Contains("id=\"players-list\"", playerHtml);
        Assert.Contains("\"count\":1", playerHtml);
        Assert.Contains("id=\"blessing-extra\"", playerHtml);

        HttpResponseMessage dashboard = await _client.GetAsync("/user");
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("id=\"usage-box\"", dashboardHtml);
        Assert.Contains("id=\"email-verification\"", dashboardHtml);
        Assert.Contains("data-mark=\"nickname\"", dashboardHtml);
        Assert.Contains("id=\"blessing-extra\"", dashboardHtml);

        HttpResponseMessage profile = await _client.GetAsync("/user/profile");
        string profileHtml = await profile.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Contains("id=\"change-nickname\"", profileHtml);
        Assert.Contains("id=\"change-password\"", profileHtml);
        Assert.Contains("id=\"change-email\"", profileHtml);
        Assert.Contains("id=\"reset-avatar\"", profileHtml);

        HttpResponseMessage closet = await _client.GetAsync("/user/closet");
        string closetHtml = await closet.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, closet.StatusCode);
        Assert.Contains("id=\"closet-list\"", closetHtml);
        Assert.Contains("id=\"modals\"", closetHtml);
        Assert.Contains("id=\"blessing-extra\"", closetHtml);

        HttpResponseMessage reports = await _client.GetAsync("/user/reports");
        string reportsHtml = await reports.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
        Assert.Contains("材质 ID", reportsHtml);
        Assert.Contains("举报原因", reportsHtml);
        Assert.Contains("举报时间", reportsHtml);
    }

    [Fact]
    public async Task AddPlayerDeductsScore()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Post, "/user/player",
            new Dictionary<string, string> { ["name"] = "NewPlayer" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("NewPlayer",
            json.RootElement.GetProperty("data").GetProperty("name").GetString());

        UserRepository.UserRow user = new UserRepository(Db()).FindById(_uid)!;
        Assert.Equal(900, user.Score); // 默认 score_per_player = 100
        Assert.NotNull(new PlayerRepository(Db()).FindByName("NewPlayer"));
    }

    [Fact]
    public async Task AddDuplicatePlayerIs422()
    {
        await LoginAsync();
        SeedPlayer(_uid, "Taken");

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Post, "/user/player",
            new Dictionary<string, string> { ["name"] = "Taken" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task RenamePlayerUpdatesName()
    {
        await LoginAsync();
        int pid = SeedPlayer(_uid, "Before");

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Put, "/user/player/" + pid + "/name",
            new Dictionary<string, string> { ["name"] = "After" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("After", new PlayerRepository(Db()).FindById(pid)!.Name);
    }

    [Fact]
    public async Task DeletePlayerReturnsScore()
    {
        await LoginAsync();
        int pid = SeedPlayer(_uid, "ToDelete");

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Delete, "/user/player/" + pid);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Null(new PlayerRepository(Db()).FindById(pid));
        Assert.Equal(1100, new UserRepository(Db()).FindById(_uid)!.Score);
    }

    [Fact]
    public async Task OtherUsersPlayerIs403()
    {
        await LoginAsync();
        int pid = SeedPlayer(_otherUid, "Foreign");

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Delete, "/user/player/" + pid);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());
        Assert.NotNull(new PlayerRepository(Db()).FindById(pid));
    }

    [Fact]
    public async Task SetTextureRequiresClosetMembership()
    {
        await LoginAsync();
        int pid = SeedPlayer(_uid, "Wearer");
        long tid = SeedTexture();

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Put, "/user/player/" + pid + "/textures",
            new Dictionary<string, string> { ["skin"] = tid.ToString() });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.RootElement.GetProperty("code").GetInt32());

        // 加入衣柜后可应用
        Db().Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,NULL)",
            ("@u", _uid), ("@t", tid));
        (status, json) = await SendJsonAsync(HttpMethod.Put, "/user/player/" + pid + "/textures",
            new Dictionary<string, string> { ["skin"] = tid.ToString() });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal((int)tid, new PlayerRepository(Db()).FindById(pid)!.TidSkin);
    }

    [Fact]
    public async Task ClearTextureResetsTids()
    {
        await LoginAsync();
        long tid = SeedTexture();
        int pid = SeedPlayer(_uid, "Clearer", (int)tid, 0);

        (HttpStatusCode status, JsonDocument json) = await SendJsonAsync(
            HttpMethod.Delete, "/user/player/" + pid + "/textures",
            new Dictionary<string, string> { ["skin"] = "1" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(0, new PlayerRepository(Db()).FindById(pid)!.TidSkin);
    }

    // ================================================================== 纹理 / Minecraft JSON

    [Fact]
    public async Task MinecraftJsonReturnsProfile()
    {
        SeedPlayer(_uid, "Notch", (int)SeedTexture(), 0);

        HttpResponseMessage response = await _client.GetAsync("/Notch.json");
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Notch", json.RootElement.GetProperty("username").GetString());
        Assert.Equal(Hash,
            json.RootElement.GetProperty("skins").GetProperty("default").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cape").ValueKind);
        // ASP.NET 会按自己的顺序重排 Cache-Control 指令（语义等价；见 PORT-NOTES）
        string cacheControl = response.Headers.TryGetValues("Cache-Control",
            out IEnumerable<string>? values) ? values.First() : "";
        Assert.Contains("max-age=31536000", cacheControl);
        Assert.Contains("public", cacheControl);
    }

    [Fact]
    public async Task MinecraftJsonUnknownPlayerIs404()
    {
        HttpResponseMessage response = await _client.GetAsync("/Nobody.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MinecraftJsonBannedUserIs403()
    {
        SeedPlayer(_uid, "Banned", (int)SeedTexture(), 0);
        Db().Execute("UPDATE users SET permission = -1 WHERE uid = @u", ("@u", _uid));

        HttpResponseMessage response = await _client.GetAsync("/Banned.json");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TextureBytesAreServed()
    {
        SeedTexture();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), png);

        HttpResponseMessage response = await _client.GetAsync("/textures/" + Hash);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, body);
        Assert.NotNull(response.Headers.ETag);

        // ETag 命中 → 304
        var cached = new HttpRequestMessage(HttpMethod.Get, "/textures/" + Hash);
        cached.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        HttpResponseMessage second = await _client.SendAsync(cached);
        Assert.Equal((HttpStatusCode)304, second.StatusCode);
    }

    [Fact]
    public async Task RawServesTextureByTid()
    {
        long tid = SeedTexture();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9];
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), png);

        HttpResponseMessage response = await _client.GetAsync("/raw/" + tid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
    }

    // ================================================================== 头像

    private static byte[] SkinPng()
    {
        // 整张皮肤透明；只把头部正面 8×8 涂成红色（帽子层保持透明）
        using var image = new Image<Rgba32>(64, 32, new Rgba32(0, 0, 0, 0));
        for (int y = 8; y < 16; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                image[x, y] = new Rgba32(200, 30, 40, 255);
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task AvatarByHashRendersHead()
    {
        SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());

        HttpResponseMessage response = await _client.GetAsync("/avatar/hash/" + Hash + "?size=32&png=true");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        using Image<Rgba32> avatar = Image.Load<Rgba32>(body);
        Assert.Equal(32, avatar.Width);
        Assert.Equal(32, avatar.Height);
        Rgba32 center = avatar[16, 16];
        Assert.Equal(200, center.R);
        Assert.Equal(30, center.G);
        Assert.Equal(40, center.B);
    }

    [Fact]
    public async Task AvatarDefaultsToWebp()
    {
        SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());

        HttpResponseMessage response =
            await _client.GetAsync("/avatar/hash/" + Hash + "?size=32");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(body, 8, 4));
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Avatar3dByHashRendersIsometrically()
    {
        SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());

        HttpResponseMessage flatResponse =
            await _client.GetAsync("/avatar/hash/" + Hash + "?size=32&png=true");
        HttpResponseMessage isoResponse =
            await _client.GetAsync("/avatar/hash/" + Hash + "?size=32&3d&png=true");
        byte[] flat = await flatResponse.Content.ReadAsByteArrayAsync();
        byte[] iso = await isoResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, isoResponse.StatusCode);
        Assert.Equal("image/png", isoResponse.Content.Headers.ContentType?.MediaType);

        using Image<Rgba32> avatar = Image.Load<Rgba32>(iso);
        Assert.Equal(32, avatar.Width);
        Assert.Equal(32, avatar.Height);
        Assert.NotEqual(flat, iso);
    }

    [Fact]
    public async Task AvatarUnknownHashReturnsFallback()
    {
        HttpResponseMessage response = await _client.GetAsync("/avatar/hash/deadbeef?size=24");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using Image<Rgba32> avatar = Image.Load<Rgba32>(body);
        Assert.Equal(24, avatar.Width);
        Assert.Equal(24, avatar.Height);
    }

    [Fact]
    public async Task AvatarForCapeIs422()
    {
        long tid = SeedTexture();
        Db().Execute("UPDATE textures SET type = 'cape' WHERE tid = @t", ("@t", tid));

        HttpResponseMessage response = await _client.GetAsync("/avatar/" + tid);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task AvatarByUserUsesAvatarTid()
    {
        long tid = SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());
        Db().Execute("UPDATE users SET avatar = @t WHERE uid = @u", ("@t", tid), ("@u", _uid));

        HttpResponseMessage response = await _client.GetAsync("/avatar/user/" + _uid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using Image<Rgba32> avatar = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(100, avatar.Width);
    }

    // ================================================================== 预览

    private static byte[] CapePng()
    {
        using var image = new Image<Rgba32>(64, 32, new Rgba32(0, 0, 0, 0));
        // 披风正面区域 (1,1)–(11,17) 涂成蓝色
        for (int y = 1; y < 17; y++)
        {
            for (int x = 1; x < 11; x++)
            {
                image[x, y] = new Rgba32(20, 40, 200, 255);
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task CapePreviewRendersFrontFace()
    {
        const string CapeHash =
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        long tid = new TextureRepository(Db()).Insert("Cape", "cape", CapeHash, 1, _uid,
            true, "2026-01-01 00:00:00");
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, CapeHash), CapePng());

        HttpResponseMessage response = await _client.GetAsync(
            "/preview/hash/" + CapeHash + "?height=160&png=true");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        using Image<Rgba32> preview = Image.Load<Rgba32>(body);
        Assert.Equal(180, preview.Width);   // 40*2 + 160/16*10
        Assert.Equal(200, preview.Height);  // 20*2 + 160
        Rgba32 center = preview[90, 100];
        Assert.Equal(20, center.R);
        Assert.Equal(40, center.G);
        Assert.Equal(200, center.B);
    }

    [Fact]
    public async Task SkinPreviewRendersFrontAndBack()
    {
        long tid = SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());

        HttpResponseMessage response = await _client.GetAsync("/preview/" + tid + "?png=true");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((byte)0x89, body[0]);

        using Image<Rgba32> preview = Image.Load<Rgba32>(body);
        Assert.True(preview.Width > 0 && preview.Height > 0);
        // 正反两面并排，四周是透明内边距
        Assert.Equal(0, preview[0, 0].A);
    }

    [Fact]
    public async Task PreviewMissingTextureIs404()
    {
        HttpResponseMessage response = await _client.GetAsync("/preview/9999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task UserAndSkinlibPagesRender()
    {
        await LoginAsync();
        long tid = SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash), SkinPng());

        string closet = await (await _client.GetAsync("/user/closet")).Content
            .ReadAsStringAsync();
        Assert.Contains("id=\"closet-list\"", closet);

        string reports = await (await _client.GetAsync("/user/reports")).Content
            .ReadAsStringAsync();
        Assert.Contains("我的举报", reports);

        string upload = await (await _client.GetAsync("/skinlib/upload")).Content
            .ReadAsStringAsync();
        Assert.Contains("id=\"file-input\"", upload);

        string show = await (await _client.GetAsync("/skinlib/show/" + tid)).Content
            .ReadAsStringAsync();
        Assert.Contains("id=\"side\"", show);
    }
}
