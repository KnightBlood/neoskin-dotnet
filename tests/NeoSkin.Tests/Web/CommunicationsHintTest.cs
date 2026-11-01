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

/// <summary>通知与公告（admin/communications）写侧契约。</summary>
public sealed class CommunicationsHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-comm-" + Guid.NewGuid().ToString("N"));
    private readonly CookieContainer _cookies = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private NeoSkinSettings _settings = null!;
    private int _adminUid;
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
        _normalUid = users.Insert("normal@a.c", "Normal", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.2", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", _adminUid));

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

    private async Task<HttpResponseMessage> PostAsync(string uri,
        Dictionary<string, string> form)
    {
        form["_token"] = XsrfToken() ?? "";
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");
        return await _client.SendAsync(request);
    }

    private Dictionary<string, string> MessageForm(string title, string kind = "message",
        string source = "<p>Hello</p>")
        => new()
        {
            ["title"] = title,
            ["kind"] = kind,
            ["editor"] = "source",
            ["source"] = source,
            ["receiver"] = "all",
            ["operation"] = "publish",
            ["locale"] = "",
        };

    [Fact]
    public async Task StoreMessagePublishesAndDelivers()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/communications",
            MessageForm("Hello"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://", response.Headers.Location?.ToString());
        Assert.Contains("/admin/communications?edit=", response.Headers.Location?.ToString());

        CommunicationRepository.Row? message = DbQueryMessage();
        Assert.NotNull(message);
        Assert.Equal(CommunicationRepository.StatusPublished, message!.Status);
        Assert.Equal(2, Db().Long("SELECT COUNT(*) FROM notifications"));
        Assert.False(string.IsNullOrEmpty(message.DeliveryToken));
    }

    private CommunicationRepository.Row? DbQueryMessage()
    {
        var repository = new CommunicationRepository(Db());
        long id = Db().Long("SELECT id FROM communications WHERE kind = 'message' LIMIT 1");
        return id == 0 ? null : repository.Find(id);
    }

    [Fact]
    public async Task StoreAnnouncementDoesNotDeliver()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/communications",
            MessageForm("Notice", "announcement"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("?announcement=1", response.Headers.Location?.ToString());
        Assert.Equal(0, Db().Long("SELECT COUNT(*) FROM notifications"));
        Assert.Equal(1, Db().Long(
            "SELECT COUNT(*) FROM communications WHERE kind = 'announcement' AND status = 'published'"));
    }

    [Fact]
    public async Task AnnouncementEditorFallsBackToLocaleNeutral()
    {
        await LoginAsync();
        await PostAsync("/admin/communications",
            MessageForm("Neutral notice", "announcement", "Neutral body"));

        string page = await (await _client.GetAsync("/admin/communications?announcement=1"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Neutral notice", page);
        Assert.Contains("Neutral body", page);
    }

    [Fact]
    public async Task StoreBothCreatesAnnouncement()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/communications",
            MessageForm("Both", "both"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Assert.Equal(1, Db().Long("SELECT COUNT(*) FROM communications WHERE kind = 'both'"));
        var kinds = string.Join(",",
            Db().Query("SELECT kind FROM communications ORDER BY id", r => r.GetString(0)));
        Assert.Contains("both", kinds);
        Assert.Contains("announcement", kinds);
        Assert.Equal(2, Db().Long("SELECT COUNT(*) FROM notifications"));
    }

    [Fact]
    public async Task UpdateKeepsDelivery()
    {
        await LoginAsync();
        await PostAsync("/admin/communications", MessageForm("Before"));
        long id = Db().Long("SELECT id FROM communications WHERE kind = 'message' LIMIT 1");

        var form = new Dictionary<string, string>
        {
            ["_token"] = XsrfToken() ?? "",
            ["title"] = "After",
            ["kind"] = "message",
            ["editor"] = "source",
            ["source"] = "<p>Changed</p>",
            ["receiver"] = "all",
            ["operation"] = "publish",
            ["locale"] = "",
        };
        var request = new HttpRequestMessage(HttpMethod.Put, "/admin/communications/" + id)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("After", Db().Scalar(
            "SELECT title FROM communications WHERE id = @id", ("@id", id))?.ToString());
        Assert.Equal(2, Db().Long("SELECT COUNT(*) FROM notifications"));
    }

    [Fact]
    public async Task PreviewRendersSource()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/communications/preview",
            new Dictionary<string, string>
            {
                ["title"] = "T",
                ["kind"] = "announcement",
                ["editor"] = "source",
                ["source"] = "Preview",
                ["receiver"] = "all",
                ["operation"] = "draft",
                ["locale"] = "",
            });
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<p>Preview</p>\n",
            json.RootElement.GetProperty("html").GetString());
        Assert.Equal("no-store", response.Headers.TryGetValues("Cache-Control",
            out IEnumerable<string>? values) ? values.First() : null);
    }

    [Fact]
    public async Task ImportSourceSplitsParagraphs()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/communications/import",
            new Dictionary<string, string>
            {
                ["source"] = "First paragraph\n\nSecond paragraph",
            });
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.RootElement.GetProperty("blocks").GetArrayLength());
        Assert.Equal("text",
            json.RootElement.GetProperty("blocks")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task WithdrawMarksStatus()
    {
        await LoginAsync();
        await PostAsync("/admin/communications", MessageForm("ToWithdraw"));
        long id = Db().Long("SELECT id FROM communications WHERE kind = 'message' LIMIT 1");

        HttpResponseMessage response = await PostAsync(
            "/admin/communications/" + id + "/withdraw",
            new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(CommunicationRepository.StatusWithdrawn,
            Db().Scalar("SELECT status FROM communications WHERE id = @id", ("@id", id))?.ToString());
    }
    [Fact]
    public async Task InboxPageRendersTemplate()
    {
        await LoginAsync();
        HttpResponseMessage response = await _client.GetAsync("/user/inbox");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("暂无站内信。", html);
        Assert.Contains("共 0 条", html);
        Assert.Contains("user-panel", html);
    }
    [Fact]
    public async Task UnreadNotificationsAndAnnouncementsRenderOnUserPages()
    {
        await LoginAsync();
        await PostAsync("/admin/communications", MessageForm("Hello"));
        await PostAsync("/admin/communications",
            MessageForm("Notice", "announcement", "Welcome announcement"));

        Assert.NotNull(NeoSkin.Web.View.PageView.InboxProvider);
        string dashboard = await (await _client.GetAsync("/user")).Content
            .ReadAsStringAsync();

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "opencode", "dash.html"), dashboard);
        Assert.Contains("data-notifications", dashboard);
        Assert.Contains("Hello", dashboard);
        Assert.Contains("Welcome announcement", dashboard);
        Assert.DoesNotContain("\"notifications\":[]", dashboard);
    }
}
