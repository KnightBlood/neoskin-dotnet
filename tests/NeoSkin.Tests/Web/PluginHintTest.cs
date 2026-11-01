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

/// <summary>插件清单层（admin/plugins）的 HTTP 契约。</summary>
public sealed class PluginHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-plugin-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private string _pluginsDir = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(_pluginsDir);
        _settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage = { Root = Path.Combine(_dir, "storage") },
        };
        _settings.Plugins.Directory = _pluginsDir;
        _settings.Database.Connection = "sqlite";
        _settings.Database.Name = Path.Combine(_dir, "db.sqlite");
        _settings.Secure.Cipher = "BCRYPT";
        _settings.Hashing.PasswordRounds = 4;

        Directory.CreateDirectory(_settings.Storage.Root);
        var db = new Database(_settings);
        new SchemaInstaller(db.Open, _settings).Install();
        File.WriteAllText(Path.Combine(_settings.Storage.Root, "install.lock"), "");

        int admin = new UserRepository(db).Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", admin));

        WritePlugin("foo", "Foo Plugin", "1.0.0",
            "{\"blessing-skin-server\":\">=6.0.0\"}", readme: "# Foo\nHello readme");
        WritePlugin("bar", "Bar Plugin", "2.0.0",
            "{\"blessing-skin-server\":\">=6.0.0\",\"foo\":\"^1.0.0\"}");
        WritePlugin("old", "Old Plugin", "1.0.0",
            "{\"blessing-skin-server\":\">=99.0.0\"}");

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

    private void WritePlugin(string name, string title, string version, string require,
        string? readme = null)
    {
        string dir = Path.Combine(_pluginsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            "{\"name\":\"" + name + "\",\"title\":\"" + title + "\",\"version\":\"" + version
            + "\",\"description\":\"" + title + " desc\",\"require\":" + require + "}");
        if (readme is not null)
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), readme);
        }
    }

    private string? XsrfToken() =>
        _cookies.GetCookies(_client.BaseAddress!).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value;

    private Database Db() => new(_settings);

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

    private Task<(HttpStatusCode Status, JsonDocument? Json)> ManageAsync(string name,
        string action) => SendAsync(HttpMethod.Post, "/admin/plugins/manage",
        new Dictionary<string, string> { ["name"] = name, ["action"] = action });

    [Fact]
    public async Task DataListsPlugins()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Get, "/admin/plugins/data");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement root = json!.RootElement;
        Assert.Equal(3, root.GetArrayLength());
        JsonElement foo = root.EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "foo");
        Assert.Equal("1.0.0", foo.GetProperty("version").GetString());
        Assert.False(foo.GetProperty("enabled").GetBoolean());
        Assert.True(foo.GetProperty("readme").GetBoolean());
        Assert.Equal("plug", foo.GetProperty("icon").GetProperty("fa").GetString());
    }

    [Fact]
    public async Task EnableAndDisablePlugin()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await ManageAsync("foo", "enable");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());

        (_, json) = await SendAsync(HttpMethod.Get, "/admin/plugins/data");
        JsonElement foo = json!.RootElement.EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "foo");
        Assert.True(foo.GetProperty("enabled").GetBoolean());

        (status, json) = await ManageAsync("foo", "disable");
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        (_, json) = await SendAsync(HttpMethod.Get, "/admin/plugins/data");
        foo = json!.RootElement.EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "foo");
        Assert.False(foo.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task EnableUnsatisfiedIsCodeOne()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await ManageAsync("old", "enable");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task EnableDependentNeedsDependencyEnabled()
    {
        await LoginAsync();
        (_, JsonDocument? json) = await ManageAsync("bar", "enable");
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());

        await ManageAsync("foo", "enable");
        (_, json) = await ManageAsync("bar", "enable");
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DeleteRemovesDirectory()
    {
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await ManageAsync("foo", "delete");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, json!.RootElement.GetProperty("code").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(_pluginsDir, "foo")));
    }

    [Fact]
    public async Task UnknownPluginIsCodeOne()
    {
        await LoginAsync();
        (_, JsonDocument? json) = await ManageAsync("missing", "enable");
        Assert.Equal(1, json!.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ReadmeReturnsHtmlAndConfigIs404()
    {
        await LoginAsync();
        HttpResponseMessage readme = await _client.GetAsync("/admin/plugins/readme/foo");
        string body = await readme.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, readme.StatusCode);
        Assert.Contains("Hello readme", body);

        HttpResponseMessage config = await _client.GetAsync("/admin/plugins/config/foo");
        Assert.Equal(HttpStatusCode.NotFound, config.StatusCode);
    }

    [Fact]
    public async Task UploadInstallsZip()
    {
        Db().Execute("UPDATE users SET permission = 2 WHERE email = 'admin@a.c'");
        await LoginAsync();

        using var archiveStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveStream,
                   System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry =
                archive.CreateEntry("foo2/package.json");
            using Stream writer = entry.Open();
            byte[] manifest = System.Text.Encoding.UTF8.GetBytes(
                "{\"name\":\"foo2\",\"title\":\"Foo2\",\"version\":\"1.0.0\"}");
            writer.Write(manifest);
        }

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(XsrfToken() ?? ""), "_token");
        var file = new ByteArrayContent(archiveStream.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "plugin.zip");

        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/upload")
        {
            Content = content,
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        Assert.True(File.Exists(Path.Combine(_pluginsDir, "foo2", "package.json")));
    }

    [Fact]
    public async Task UploadNonZipIs422()
    {
        Db().Execute("UPDATE users SET permission = 2 WHERE email = 'admin@a.c'");
        await LoginAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(XsrfToken() ?? ""), "_token");
        var file = new ByteArrayContent("not a zip"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "plugin.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/upload")
        {
            Content = content,
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task WgetRejectsInvalidUrl()
    {
        Db().Execute("UPDATE users SET permission = 2 WHERE email = 'admin@a.c'");
        await LoginAsync();
        (HttpStatusCode status, JsonDocument? json) = await SendAsync(
            HttpMethod.Post, "/admin/plugins/wget",
            new Dictionary<string, string> { ["url"] = "not-a-url" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.True(json!.RootElement.GetProperty("errors").TryGetProperty("url", out _));
    }
}
