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

/// <summary>纹理上传（<c>POST texture</c>）的 HTTP 契约：校验、消毒、落库、去重。</summary>
public sealed class TextureUploadHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-upload-" + Guid.NewGuid().ToString("N"));
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
                ["identification"] = "user@a.c",
                ["password"] = "secret123",
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 40, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Json)> UploadAsync(
        byte[]? file, string name = "Skin", string type = "steve", string isPublic = "1",
        string fileName = "skin.png")
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(XsrfToken() ?? ""), "_token");
        content.Add(new StringContent(name), "name");
        content.Add(new StringContent(type), "type");
        content.Add(new StringContent(isPublic), "public");
        if (file is not null)
        {
            var fileContent = new ByteArrayContent(file);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", fileName);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/texture") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? null : JsonDocument.Parse(body));
    }

    [Fact]
    public async Task UploadSucceedsAndStoresSanitizedPng()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await UploadAsync(Png(64, 32));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        long tid = json.RootElement.GetProperty("data").GetProperty("tid").GetInt64();

        var textures = new TextureRepository(Db());
        TextureRow? row = textures.FindByTid(tid);
        Assert.NotNull(row);
        Assert.Equal("steve", row!.Type);
        Assert.True(row.IsPublic);
        Assert.Equal(64, row.Hash.Length);
        Assert.True(File.Exists(Path.Combine(_settings.Storage.Textures, row.Hash)));

        // 上传后自动加入衣柜
        Assert.True(Db().Long(
            "SELECT COUNT(*) FROM user_closet WHERE user_uid=@u AND texture_tid=@t",
            ("@u", _uid), ("@t", tid)) > 0);
    }

    [Fact]
    public async Task UploadDuplicateIsCodeTwo()
    {
        await LoginAsync();
        byte[] png = Png(64, 32);
        (_, JsonDocument? first) = await UploadAsync(png);
        long tid = first!.RootElement.GetProperty("data").GetProperty("tid").GetInt64();

        (HttpStatusCode status, JsonDocument? second) = await UploadAsync(png);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, second!.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(tid, second.RootElement.GetProperty("data").GetProperty("tid").GetInt64());
    }

    [Fact]
    public async Task UploadInvalidDimensionsIsCodeOne()
    {
        await LoginAsync();
        // 32×32：宽高不是 64/32 的倍数
        (HttpStatusCode status, JsonDocument? json) = await UploadAsync(Png(32, 32));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task UploadNonPngFailsValidation()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await UploadAsync(
            "not a png"u8.ToArray(), fileName: "fake.png");

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task UploadInvalidTypeFailsValidation()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await UploadAsync(Png(64, 32), type: "hat");

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("type", out _));
    }

    [Fact]
    public async Task UploadRequiresAuthentication()
    {
        // 先取 CSRF（仍保持未登录），POST 才会走到 auth 而不是 419
        await _client.GetAsync("/auth/captcha");
        (HttpStatusCode status, _) = await UploadAsync(Png(64, 32));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
