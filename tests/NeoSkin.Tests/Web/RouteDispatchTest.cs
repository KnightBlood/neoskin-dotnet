using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using NeoSkin.Web;
using NeoSkin.Web.Web;

namespace NeoSkin.Tests.Web;

/// <summary>
/// 真实 Kestrel 上的路由分发测试（不连数据库）：
/// 174 条路由未接线时统一 501；404/405/OPTIONS 与 Laravel 一致。
/// </summary>
public sealed class RouteDispatchTest : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // 无 APP_KEY：只验证路由分发（会话/CSRF 需要密钥，安装向导阶段没有）
        _app = NeoSkinApp.Create(["--webhost-urls=http://127.0.0.1:0"],
            new NotImplementedHandlerResolver(),
            new NeoSkin.Web.Config.NeoSkinSettings { Storage = { Root = Path.Combine(Path.GetTempPath(), "neoskin-routes-" + Guid.NewGuid().ToString("N")) } });
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        string address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task HealthIsAlive()
    {
        HttpResponseMessage response = await _client.GetAsync("/__neoskin/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnimplementedRoutesReturn501()
    {
        HttpResponseMessage home = await _client.GetAsync("/");
        Assert.Equal((HttpStatusCode)501, home.StatusCode);
        Assert.Contains("\"code\":501", await home.Content.ReadAsStringAsync());

        HttpResponseMessage login = await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal((HttpStatusCode)501, login.StatusCode);

        // 尾斜杠任意数量：源站 200，这里对应的 handler 未接线 → 501
        HttpResponseMessage trailing = await _client.GetAsync("/auth/login///");
        Assert.Equal((HttpStatusCode)501, trailing.StatusCode);
    }

    [Fact]
    public async Task MethodNotAllowedCarriesAllowHeader()
    {
        var putRequest = new HttpRequestMessage(HttpMethod.Put, "/auth/login")
        {
            Content = new StringContent(""),
        };
        putRequest.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await _client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD", "POST"], response.Content.Headers.Allow);
        using (System.Text.Json.JsonDocument body =
               System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("The PUT method is not supported for route auth/login. Supported methods: GET, HEAD, POST.",
                body.RootElement.GetProperty("message").GetString());
        }

        HttpResponseMessage root = await _client.DeleteAsync("/");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, root.StatusCode);
        Assert.Equal(["GET", "HEAD"], root.Content.Headers.Allow);
    }

    [Fact]
    public async Task OptionsReturnsEmpty200WithCompactAllow()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/auth/login");
        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["GET", "HEAD", "POST"], response.Content.Headers.Allow);
        Assert.Equal("no-cache, private", response.Headers.CacheControl!.ToString());
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NotFoundMessageIsLaravelShaped()
    {
        HttpResponseMessage response = await _client.GetAsync("/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("The route no-such-route could not be found.",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DoubleSlashIsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth//login");
        request.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // 响应体的 JSON 会把 / 转义成 \/（PHP 默认），所以解析后再比对消息
        using System.Text.Json.JsonDocument body =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("The route auth//login could not be found.",
            body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ParamRoutesStillDispatch()
    {
        // 参数路由命中后同样 501（handler 未接线），而不是 404
        HttpResponseMessage response = await _client.GetAsync("/avatar/123");
        Assert.Equal((HttpStatusCode)501, response.StatusCode);

        HttpResponseMessage player = await _client.GetAsync("/Steve.json");
        Assert.Equal((HttpStatusCode)501, player.StatusCode);
    }
}
