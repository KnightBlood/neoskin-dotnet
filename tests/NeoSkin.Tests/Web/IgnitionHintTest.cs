using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using NeoSkin.Web;
using NeoSkin.Web.Config;

namespace NeoSkin.Tests.Web;

/// <summary>Ignition 管理端点的门控与响应契约。</summary>
public sealed class IgnitionHintTest
{
    private const string AppKey = "base64:+8iD1vI5Bo5Vs2CSP3GMmy+Em3dwlOqeY7I5F5GvBVk=";

    private static async Task<(WebApplication App, HttpClient Client, string Dir)> StartAsync(
        bool debug, string env)
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "neoskin-ignition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new NeoSkinSettings
        {
            AppKey = AppKey,
            Storage = { Root = Path.Combine(dir, "storage") },
            Debug = debug,
            Env = env,
        };
        settings.Database.Connection = "sqlite";
        settings.Database.Name = Path.Combine(dir, "db.sqlite");
        Directory.CreateDirectory(settings.Storage.Root);
        File.WriteAllText(Path.Combine(settings.Storage.Root, "install.lock"), "");

        WebApplication app = NeoSkinApp.Create(
            ["--webhost-urls=http://127.0.0.1:0"], null, settings);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, dir);
    }

    private static async Task StopAsync(WebApplication app, HttpClient client, string dir)
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DisabledInProduction()
    {
        (WebApplication app, HttpClient client, string dir) = await StartAsync(false, "production");
        try
        {
            HttpResponseMessage response = await client.GetAsync("/_ignition/health-check");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await StopAsync(app, client, dir);
        }
    }

    [Fact]
    public async Task DisabledWhenDebugButProduction()
    {
        (WebApplication app, HttpClient client, string dir) = await StartAsync(true, "production");
        try
        {
            HttpResponseMessage response = await client.GetAsync("/_ignition/health-check");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await StopAsync(app, client, dir);
        }
    }

    [Fact]
    public async Task HealthCheckInLocalDebug()
    {
        (WebApplication app, HttpClient client, string dir) = await StartAsync(true, "local");
        try
        {
            HttpResponseMessage response = await client.GetAsync("/_ignition/health-check");
            using JsonDocument json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(json.RootElement.GetProperty("can_execute_commands").GetBoolean());
        }
        finally
        {
            await StopAsync(app, client, dir);
        }
    }

    [Fact]
    public async Task ExecuteSolutionValidatesAndReturns404()
    {
        (WebApplication app, HttpClient client, string dir) = await StartAsync(true, "local");
        try
        {
            var missing = new HttpRequestMessage(HttpMethod.Post, "/_ignition/execute-solution")
            {
                Content = new FormUrlEncodedContent([]),
            };
            HttpResponseMessage invalid = await client.SendAsync(missing);
            Assert.Equal((HttpStatusCode)422, invalid.StatusCode);

            var request = new HttpRequestMessage(HttpMethod.Post, "/_ignition/execute-solution")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["solution"] = "Some\\Solution",
                }),
            };
            HttpResponseMessage response = await client.SendAsync(request);
            using JsonDocument json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Solution could not be found",
                json.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await StopAsync(app, client, dir);
        }
    }

    [Fact]
    public async Task UpdateConfigWritesFile()
    {
        string configFile = Path.Combine(Path.GetTempPath(),
            "neoskin-ignition-" + Guid.NewGuid().ToString("N") + ".json");
        Environment.SetEnvironmentVariable("NEOSKIN_IGNITION_FILE", configFile);
        (WebApplication app, HttpClient client, string dir) = await StartAsync(true, "local");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/_ignition/update-config")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["theme"] = "dark",
                    ["editor"] = "vscode",
                    ["hide_solutions"] = "1",
                }),
            };
            HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("true", await response.Content.ReadAsStringAsync());
            Assert.True(File.Exists(configFile));
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(configFile));
            Assert.Equal("dark", saved.RootElement.GetProperty("theme").GetString());
            Assert.True(saved.RootElement.GetProperty("hide_solutions").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEOSKIN_IGNITION_FILE", null);
            if (File.Exists(configFile))
            {
                File.Delete(configFile);
            }

            await StopAsync(app, client, dir);
        }
    }
}
