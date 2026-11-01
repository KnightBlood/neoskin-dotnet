using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Tests.Web;

/// <summary>管理端选项页（admin/options|customize|score|resource）的契约。</summary>
public sealed class OptionsHintTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "neoskin-options-" + Guid.NewGuid().ToString("N"));
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
        int admin = new UserRepository(db).Insert("admin@a.c", "Admin", 1000,
            PhpBcrypt.Hash("secret123", 4), "127.0.0.1", "Asia/Shanghai");
        db.Execute("UPDATE users SET permission = 1 WHERE uid = @u", ("@u", admin));

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
        request.Headers.Accept.ParseAdd("text/html");
        return await _client.SendAsync(request);
    }

    private string? OptionValue(string name) =>
        Db().Scalar("SELECT option_value FROM options WHERE option_name = @n", ("@n", name))
            ?.ToString();

    [Fact]
    public async Task PagesRenderForms()
    {
        await LoginAsync();
        string options = await (await _client.GetAsync("/admin/options")).Content
            .ReadAsStringAsync();
        string customize = await (await _client.GetAsync("/admin/customize")).Content
            .ReadAsStringAsync();
        string score = await (await _client.GetAsync("/admin/score")).Content
            .ReadAsStringAsync();
        string resource = await (await _client.GetAsync("/admin/resource")).Content
            .ReadAsStringAsync();

        Assert.Contains("name=\"site_name\"", options);
        Assert.Contains("name=\"update_source\"", options);
        Assert.Contains("name=\"home_pic_url\"", customize);
        Assert.Contains("name=\"sign_score_from\"", score);
        Assert.Contains("name=\"cdn_address\"", resource);
        Assert.Contains("name=\"_token\"", options);
        Assert.Contains("main-sidebar", options);
        Assert.Contains("name=\"option\" value=\"general\"", options);
        Assert.Contains("submit_general", options);
        Assert.Contains("id=\"navbar-color-picker\"", customize);
        Assert.Contains("bg-cyan", customize);
        Assert.Contains("input-group", score);
        Assert.Contains("callout-warning", resource);
    }

    [Fact]
    public async Task SaveGeneralOptions()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/options",
            new Dictionary<string, string>
            {
                ["site_name"] = "新站点",
                ["site_url"] = "http://example.com/index.php",
                ["require_verification"] = "1",
                ["regs_per_ip"] = "5",
                ["player_name_rule"] = "cjk",
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("新站点", OptionValue("site_name_zh_CN"));
        Assert.Equal("http://example.com", OptionValue("site_url"));
        Assert.Equal("true", OptionValue("require_verification"));
        Assert.Equal("false", OptionValue("register_with_player_name"));
        Assert.Equal("5", OptionValue("regs_per_ip"));
        Assert.Equal("cjk", OptionValue("player_name_rule"));
    }

    [Fact]
    public async Task InvalidUpdateSourceRedirects()
    {
        await LoginAsync();
        HttpResponseMessage response = await PostAsync("/admin/options",
            new Dictionary<string, string>
            {
                ["update_source"] = "not-a-url",
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(OptionValue("update_source")));
    }

    [Fact]
    public async Task SaveScoreOptionsCombinesSignScore()
    {
        await LoginAsync();
        await PostAsync("/admin/score", new Dictionary<string, string>
        {
            ["sign_score_from"] = "5",
            ["sign_score_to"] = "50",
            ["score_per_player"] = "200",
            ["sign_gap_time"] = "12",
        });

        Assert.Equal("5,50", OptionValue("sign_score"));
        Assert.Equal("200", OptionValue("score_per_player"));
        Assert.Equal("12", OptionValue("sign_gap_time"));
        Assert.Equal("false", OptionValue("return_score"));
    }

    [Fact]
    public async Task SaveCustomizeColorAndCdn()
    {
        await LoginAsync();
        await PostAsync("/admin/customize", new Dictionary<string, string>
        {
            ["action"] = "color",
            ["navbar"] = "indigo",
            ["sidebar"] = "navy",
            ["home_pic_url"] = "./app/bg.webp",
            ["transparent_navbar"] = "1",
        });
        await PostAsync("/admin/resource", new Dictionary<string, string>
        {
            ["cdn_address"] = "https://cdn.example.com/",
            ["cache_expire_time"] = "600",
            ["enable_avatar_cache"] = "1",
        });

        Assert.Equal("indigo", OptionValue("navbar_color"));
        Assert.Equal("navy", OptionValue("sidebar_color"));
        Assert.Equal("true", OptionValue("transparent_navbar"));
        Assert.Equal("https://cdn.example.com", OptionValue("cdn_address"));
        Assert.Equal("600", OptionValue("cache_expire_time"));
        Assert.Equal("true", OptionValue("enable_avatar_cache"));
        Assert.Equal("false", OptionValue("enable_preview_cache"));
    }
}
