using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NeoSkin.Web.Cookie;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Session;

namespace NeoSkin.Tests.Web;

/// <summary>CSRF 判定逻辑 + 会话/CSRF 中间件的 HTTP 集成测试。</summary>
public sealed class CsrfTest
{
    private static readonly byte[] Key =
        Convert.FromBase64String("+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=");

    [Fact]
    public void ReadingMethodsAndOriginPass()
    {
        Assert.True(PreventRequestForgery.IsReading("GET"));
        Assert.True(PreventRequestForgery.IsReading("head"));
        Assert.False(PreventRequestForgery.IsReading("POST"));

        var post = new PreventRequestForgery.Request("POST", "/auth/login", null, null, null, null);
        Assert.False(PreventRequestForgery.Verify(post, "tok", null).Allowed);

        var sameOrigin = post with { SecFetchSite = "same-origin" };
        Assert.True(PreventRequestForgery.Verify(sameOrigin, "tok", null).Allowed);

        var sameSite = post with { SecFetchSite = "same-site" };
        Assert.False(PreventRequestForgery.Verify(sameSite, "tok", null).Allowed);
        PreventRequestForgery.AllowSameSite(true);
        Assert.True(PreventRequestForgery.Verify(sameSite, "tok", null).Allowed);
        PreventRequestForgery.FlushState();
    }

    [Fact]
    public void TokenSources()
    {
        var post = new PreventRequestForgery.Request("POST", "/x", null, null, null, null);

        Assert.True(PreventRequestForgery.Verify(post with { InputToken = "tok" }, "tok", null).Allowed);
        Assert.True(PreventRequestForgery.Verify(post with { CsrfHeader = "tok" }, "tok", null).Allowed);
        Assert.False(PreventRequestForgery.Verify(post with { CsrfHeader = "bad" }, "tok", null).Allowed);

        // X-XSRF-TOKEN：先解密，再去前缀（模拟加密后的 token）
        var encrypter = new PhpEncrypter(Key, "aes-256-cbc");
        string encrypted = encrypter.Encrypt(CookieValuePrefix.Create("XSRF-TOKEN", Key) + "tok", false);
        bool Decrypt(string header) => PreventRequestForgery.Verify(
            post with { XsrfHeader = header }, "tok", h => (string?)encrypter.Decrypt(h, false)).Allowed;

        Assert.True(Decrypt(encrypted));
        Assert.False(PreventRequestForgery.Verify(post with { XsrfHeader = "garbage" }, "tok",
            h => null).Allowed);
    }

    [Fact]
    public void ExceptListSupportsWildcards()
    {
        PreventRequestForgery.FlushState();
        PreventRequestForgery.Except(["api/*"]);
        var post = new PreventRequestForgery.Request("POST", "/api/login", null, null, null, null);

        Assert.True(PreventRequestForgery.Verify(post, "tok", null).Allowed);
        Assert.False(PreventRequestForgery.Verify(post with { Path = "/auth/login" }, "tok", null).Allowed);
        PreventRequestForgery.FlushState();
    }

    // ================================================================== HTTP 集成

    [Fact]
    public async Task SessionAndCsrfRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neoskin-csrf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cookies = new EncryptCookies(new PhpEncrypter(Key, "aes-256-cbc"));
            var sessionConfig = SessionCookieConfig.Default;
            var handler = new FileSessionHandler(dir, 120);

            var builder = WebApplication.CreateSlimBuilder([]);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();

            app.Use((context, next) =>
                new EncryptCookiesMiddleware(next, cookies).InvokeAsync(context));
            app.Use((context, next) =>
                new StartSessionMiddleware(next, handler, sessionConfig, cookies).InvokeAsync(context));
            app.Use((context, next) =>
                new VerifyCsrfTokenMiddleware(next, new PhpEncrypter(Key, "aes-256-cbc"))
                    .InvokeAsync(context));
            app.Run(async context =>
            {
                SessionStore session = (SessionStore)context.Items[StartSessionMiddleware.SessionKey]!;
                switch (context.Request.Path.Value)
                {
                    case "/token":
                        await context.Response.WriteAsync(session.Token()!.ToString()!);
                        break;
                    case "/whoami":
                        await context.Response.WriteAsync(session.Id);
                        break;
                    default:
                        await context.Response.WriteAsync("ok");
                        break;
                }
            });

            await app.StartAsync();
            using var http = new HttpClient(new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
            })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };

            // GET：下发加密的会话 cookie + 明文 XSRF-TOKEN
            HttpResponseMessage tokenResponse = await http.GetAsync("/token");
            string token = await tokenResponse.Content.ReadAsStringAsync();
            Assert.Equal(40, token.Length);
            Assert.Contains(tokenResponse.Headers.GetValues("Set-Cookie"),
                v => v.StartsWith("BS_SESSION=", StringComparison.Ordinal));
            Assert.Contains(tokenResponse.Headers.GetValues("Set-Cookie"),
                v => v.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));

            // POST 无 token → 419
            HttpResponseMessage rejected = await http.PostAsync("/submit",
                new FormUrlEncodedContent([]));
            Assert.Equal((HttpStatusCode)419, rejected.StatusCode);
            Assert.Contains("CSRF token mismatch", await rejected.Content.ReadAsStringAsync());

            // _token 字段
            HttpResponseMessage withForm = await http.PostAsync("/submit",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["_token"] = token }));
            Assert.Equal(HttpStatusCode.OK, withForm.StatusCode);

            // X-CSRF-TOKEN 头
            var headerRequest = new HttpRequestMessage(HttpMethod.Post, "/submit")
            {
                Content = new FormUrlEncodedContent([]),
            };
            headerRequest.Headers.Add("X-CSRF-TOKEN", token);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(headerRequest)).StatusCode);

            // 会话 id 在两次请求之间保持
            string firstId = await http.GetStringAsync("/whoami");
            Assert.Equal(firstId, await http.GetStringAsync("/whoami"));

            await app.StopAsync();
            await app.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
