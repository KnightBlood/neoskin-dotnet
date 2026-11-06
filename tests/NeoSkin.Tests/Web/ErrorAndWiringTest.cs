using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NeoSkin.Web;
using NeoSkin.Web.Config;
using NeoSkin.Web.Web;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 错误响应契约（JSON pretty-print / HTML 占位）与中间件接线
/// （会话 cookie、CSRF 419、语言 cookie）。
/// </summary>
public sealed class ErrorAndWiringTest : IAsyncLifetime
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly string _storage = Path.Combine(Path.GetTempPath(),
        "neoskin-wiring-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_storage);
        File.WriteAllText(Path.Combine(_storage, "install.lock"), "");
        _app = NeoSkinApp.Create(["--webhost-urls=http://127.0.0.1:0"],
            new NotImplementedHandlerResolver(),
            new NeoSkinSettings { AppKey = AppKey, Storage = { Root = _storage } });
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
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
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, true);
        }
    }

    // ================================================================== JSON 错误

    [Fact]
    public async Task NotFoundJsonIsLaravelPrettyShape()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/nope");
        request.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        string expected = """
            {
                "message": "The route nope could not be found.",
                "exception": true,
                "trace": []
            }
            """.Replace("\r\n", "\n");
        Assert.Equal(expected, body.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task MethodNotAllowedJsonCarriesMessage()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/auth/login");
        request.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "The PUT method is not supported for route auth/login. Supported methods: GET, HEAD, POST.",
            json.RootElement.GetProperty("message").GetString());
        Assert.Equal("GET, HEAD, POST", string.Join(", ", response.Content.Headers.Allow));
    }

    [Fact]
    public async Task BrowserGetsHtmlError()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/nope");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("404 Not Found", body);
        Assert.Contains("The route nope could not be found.", body);
    }

    // ================================================================== 中间件接线

    [Fact]
    public async Task BrowserGetsSymfonyErrorFor405()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/auth/login");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("An Error Occurred: Method Not Allowed", body);
        Assert.Contains("405 Method Not Allowed", body);
    }

    [Fact]
    public async Task WebRouteGetsSessionAndLocaleCookies()
    {
        HttpResponseMessage response = await _client.GetAsync("/auth/login");
        Assert.Equal((HttpStatusCode)501, response.StatusCode);

        List<string> setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, v => v.StartsWith("BS_SESSION=", StringComparison.Ordinal));

        // 没有 Accept-Language / cookie 时落 fallback（en）。locale cookie 与其他
        // cookie 一样会被 EncryptCookies 加密，所以解密后比较。
        string localeCookie = setCookies.Single(v => v.StartsWith("locale=", StringComparison.Ordinal));
        Assert.Equal("en", PlainCookieValue(localeCookie));
    }

    [Fact]
    public async Task AcceptLanguageSetsLocale()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/login");
        request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
        HttpResponseMessage response = await _client.SendAsync(request);

        string localeCookie = response.Headers.GetValues("Set-Cookie")
            .Single(v => v.StartsWith("locale=", StringComparison.Ordinal));
        Assert.Equal("zh_CN", PlainCookieValue(localeCookie));
    }

    [Fact]
    public async Task PostWithoutCsrfIs419()
    {
        HttpResponseMessage response = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent([]));

        Assert.Equal((HttpStatusCode)419, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CSRF token mismatch.", json.RootElement.GetProperty("message").GetString());
    }

    /// <summary>Set-Cookie 里的会话/locale 值会被 EncryptCookies 加密，解密并去前缀。</summary>
    private static string PlainCookieValue(string setCookie)
    {
        return setCookie.Split(';')[0].Split('=', 2)[1];
    }

    [Fact]
    public async Task ApiRoutesSkipCsrfAndLocaleCookie()
    {
        // api 组不含 web 组 → 没有会话/CSRF；auth:oauth 尚未接入 Passport，
        // 因此 JSON 请求按未认证处理（401），而不是 501
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/closet")
        {
            Content = new FormUrlEncodedContent([]),
        };
        request.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies : [], v => v.StartsWith("locale=", StringComparison.Ordinal));
    }
}
