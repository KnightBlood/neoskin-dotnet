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

/// <summary>管理端翻译（<c>admin/i18n</c>）的 HTTP 契约。</summary>
public sealed class TranslationsHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-i18n-" + Guid.NewGuid().ToString("N"));
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
        new UserRepository(db).Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE email = 'admin@a.c'");

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

    [Fact]
    public async Task CreateInsertsLanguageLine()
    {
        await LoginAsync();
        HttpResponseMessage response = await _client.PostAsync("/admin/i18n",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = XsrfToken() ?? "",
                ["group"] = "front-end",
                ["key"] = "welcome",
                ["text"] = "欢迎",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://" + _client.BaseAddress!.Authority + "/admin/i18n",
            response.Headers.Location?.ToString());
        Assert.Equal(1, Db().Long("SELECT COUNT(*) FROM language_lines WHERE "
            + "\"group\" = 'front-end' AND \"key\" = 'welcome'"));
    }

    [Fact]
    public async Task ListReturnsPagedRowsWithTextObject()
    {
        await LoginAsync();
        await SendAsync(HttpMethod.Post, "/admin/i18n", new Dictionary<string, string>
        {
            ["group"] = "front-end",
            ["key"] = "welcome",
            ["text"] = "欢迎",
        });

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/i18n/list?page=1");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(10, root.GetProperty("per_page").GetInt32());
        JsonElement row = root.GetProperty("data")[0];
        Assert.Equal("front-end", row.GetProperty("group").GetString());
        Assert.Equal("欢迎", row.GetProperty("text").GetProperty("zh_CN").GetString());
    }

    [Fact]
    public async Task UpdateOverwritesCurrentLocale()
    {
        await LoginAsync();
        await SendAsync(HttpMethod.Post, "/admin/i18n", new Dictionary<string, string>
        {
            ["group"] = "front-end",
            ["key"] = "welcome",
            ["text"] = "旧文案",
        });
        long id = Db().Long("SELECT id FROM language_lines WHERE \"key\" = 'welcome'");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Put, "/admin/i18n/" + id,
            new Dictionary<string, string> { ["text"] = "新文案" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        string stored = (string)Db().Scalar(
            "SELECT text FROM language_lines WHERE id = @id", ("@id", id))!;
        using JsonDocument text = JsonDocument.Parse(stored);
        Assert.Equal("新文案", text.RootElement.GetProperty("zh_CN").GetString());
    }

    [Fact]
    public async Task DeleteRemovesRow()
    {
        await LoginAsync();
        await SendAsync(HttpMethod.Post, "/admin/i18n", new Dictionary<string, string>
        {
            ["group"] = "front-end",
            ["key"] = "welcome",
            ["text"] = "欢迎",
        });
        long id = Db().Long("SELECT id FROM language_lines WHERE \"key\" = 'welcome'");

        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Delete, "/admin/i18n/" + id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.Null(Db().Scalar("SELECT id FROM language_lines WHERE id = @id", ("@id", id)));
    }

    [Fact]
    public async Task CreateMissingFieldsIs422()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(HttpMethod.Post,
            "/admin/i18n", new Dictionary<string, string> { ["group"] = "front-end" });

        Assert.Equal((HttpStatusCode)422, status);
        JsonElement errors = json!.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("key", out _));
        Assert.True(errors.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task UpdateUnknownLineIs404()
    {
        await LoginAsync();
        (HttpStatusCode status, _) = await SendAsync(HttpMethod.Put, "/admin/i18n/9999",
            new Dictionary<string, string> { ["text"] = "x" });

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
