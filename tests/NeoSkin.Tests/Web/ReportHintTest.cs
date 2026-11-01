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

/// <summary>举报提交 / 管理端列表 / 审核的 HTTP 契约。</summary>
public sealed class ReportHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";
    private const string Hash = "1212121212121212121212121212121212121212121212121212121212121212";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-report-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _uid;
    private int _uploaderUid;

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
        _uid = users.Insert("reporter@a.c", "Reporter", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        _uploaderUid = users.Insert("uploader@a.c", "Uploader", 1000,
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

    private async Task LoginAsync(string email, bool admin = false)
    {
        if (admin)
        {
            Db().Execute("UPDATE users SET permission = 1 WHERE email = @e", ("@e", email));
        }
        else
        {
            Db().Execute("UPDATE users SET permission = 1 WHERE email = @e", ("@e", email));
        }

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

    private long SeedTexture(string hash = Hash)
    {
        var textures = new TextureRepository(Db());
        return textures.Insert("Reported Texture", "steve", hash, 1, _uploaderUid, true,
            "2026-01-01 00:00:00");
    }

    private long SeedReport(long tid, int status = ReportRepository.Pending)
    {
        var reports = new ReportRepository(Db(), new UserRepository(Db()),
            new TextureRepository(Db()));
        return reports.Insert((int)tid, _uploaderUid, _uid, "Bad skin", status,
            "2026-01-02 03:04:05");
    }

    // ================================================================== submit

    [Fact]
    public async Task SubmitCreatesPendingReport()
    {
        long tid = SeedTexture();
        await LoginAsync("reporter@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/skinlib/report", new Dictionary<string, string>
            {
                ["tid"] = tid.ToString(),
                ["reason"] = "Inappropriate",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(1, Db().Long("SELECT COUNT(*) FROM reports WHERE status = 0"));
    }

    [Fact]
    public async Task SubmitDuplicateIsCode1()
    {
        long tid = SeedTexture();
        SeedReport(tid);
        await LoginAsync("reporter@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/skinlib/report", new Dictionary<string, string>
            {
                ["tid"] = tid.ToString(),
                ["reason"] = "Again",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SubmitUnknownTextureIs422()
    {
        await LoginAsync("reporter@a.c");
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/skinlib/report", new Dictionary<string, string>
            {
                ["tid"] = "9999",
                ["reason"] = "Missing",
            });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("tid", out _));
    }

    // ================================================================== manage

    [Fact]
    public async Task ManageReturnsPagedReports()
    {
        long tid = SeedTexture();
        SeedReport(tid);
        await LoginAsync("uploader@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/reports/list?q=status:0&page=1");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(9, root.GetProperty("per_page").GetInt32());
        JsonElement row = root.GetProperty("data")[0];
        Assert.Equal("Bad skin", row.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Object, row.GetProperty("texture").ValueKind);
        Assert.Equal("uploader@a.c",
            row.GetProperty("texture_uploader").GetProperty("email").GetString());
    }

    // ================================================================== review

    [Fact]
    public async Task ReviewRejectMarksRejected()
    {
        long tid = SeedTexture();
        long reportId = SeedReport(tid);
        await LoginAsync("uploader@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/reports/" + reportId, new Dictionary<string, string>
            {
                ["action"] = "reject",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(ReportRepository.Rejected,
            json.RootElement.GetProperty("data").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ReviewDeleteRemovesTexture()
    {
        long tid = SeedTexture();
        File.WriteAllBytes(Path.Combine(_settings.Storage.Textures, Hash),
            [0x89, 0x50, 0x4E, 0x47]);
        long reportId = SeedReport(tid);
        await LoginAsync("uploader@a.c");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/reports/" + reportId, new Dictionary<string, string>
            {
                ["action"] = "delete",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Null(new TextureRepository(Db()).FindByTid(tid));
        Assert.False(File.Exists(Path.Combine(_settings.Storage.Textures, Hash)));
        Assert.Equal(ReportRepository.Resolved,
            Db().Long("SELECT status FROM reports WHERE id = @id", ("@id", reportId)));
    }

    [Fact]
    public async Task ReviewBanNeedsHigherPermission()
    {
        long tid = SeedTexture();
        long reportId = SeedReport(tid);
        // 登录一个普通用户（permission 被 LoginAsync 改成 admin=1），
        // 再手动把上传者设为 2（super-admin）→ 拒绝操作
        await LoginAsync("uploader@a.c");
        Db().Execute("UPDATE users SET permission = 2 WHERE uid = @u", ("@u", _uploaderUid));

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Put,
            "/admin/reports/" + reportId, new Dictionary<string, string>
            {
                ["action"] = "ban",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }
}
