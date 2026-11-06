using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Hash;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Browser;

/// <summary>
/// 浏览器页面 E2E：临时 SQLite + 真实 neoskin 进程 + Playwright flows。
/// 默认跳过；NEOSKIN_BROWSER_E2E=1 且存在前端产物时执行。
/// </summary>
public sealed class BrowserE2ETest
{
    [Fact]
    public void BrowserFlows()
    {
        if (Environment.GetEnvironmentVariable("NEOSKIN_BROWSER_E2E") != "1")
        {
            return;
        }

        string root = FindRepoRoot();
        string nodeModules = Path.Combine(root, "tests", "browser", "node_modules", "playwright-core");
        if (!Directory.Exists(nodeModules))
        {
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "neoskin-net-browser-" + Guid.NewGuid().ToString("N"));
        string contentRoot = Path.Combine(work, "content");
        string storage = Path.Combine(work, "storage");
        string database = Path.Combine(work, "e2e.sqlite");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(storage);

        try
        {
            Step("begin work=" + work);
            CopyFrontend(root, contentRoot);
            Step("frontend copied");
            if (!Directory.Exists(Path.Combine(contentRoot, "wwwroot", "app")))
            {
                return; // 前端产物缺失：跳过，不把浏览器测试变成硬失败
            }

            var settings = new NeoSkinSettings
            {
                AppKey = "base64:" + Convert.ToBase64String(new byte[32]),
                Env = "local",
                Debug = true,
            };
            settings.Database.Connection = "sqlite";
            settings.Database.Name = database;
            settings.Storage.Root = storage;

            var db = new Database(settings);
            var installer = new SchemaInstaller(db.Open, settings);
            installer.Install();
            var users = new UserRepository(db);
            string now = PhpDate.Now(settings.Timezone);
            users.InsertSuperAdmin("admin@example.com", "Admin", 1000,
                PhpBcrypt.Hash("secret123", 4), "127.0.0.1", now, now);
            File.WriteAllText(Path.Combine(storage, "install.lock"), "");

            int port = FreePort();
            string baseUrl = $"http://127.0.0.1:{port}";
            Step("starting server " + baseUrl);
            using var server = StartServer(root, contentRoot, database, storage, port);
            try
            {
                WaitHealthy(baseUrl, server);
                Step("server healthy");

                RunFlows(root, baseUrl);
                Step("flows finished");
            }
            finally
            {
                try
                {
                    if (!server.HasExited)
                    {
                        server.Kill(true);
                    }
                    server.WaitForExit(10_000);
                }
                catch (InvalidOperationException)
                {
                }
            }

        }
        finally
        {
            if (Environment.GetEnvironmentVariable("NEOSKIN_KEEP_BROWSER_TMP") != "1")
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            else
            {
                Console.WriteLine("browser work dir: " + work);
            }
        }
    }

    /// <summary>运行 Playwright flows；读取管道用异步，避免子进程/浏览器继承句柄时死锁。</summary>
    private static void RunFlows(string root, string baseUrl)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(root, "tests", "browser", "flows.mjs"));
        start.Environment["NEOSKIN_BASE_URL"] = baseUrl;
        string? chrome = Environment.GetEnvironmentVariable("NEOSKIN_CHROME_PATH");
        if (!string.IsNullOrEmpty(chrome))
        {
            start.Environment["NEOSKIN_CHROME_PATH"] = chrome;
        }

        using var browser = Process.Start(start)!;
        var stdoutTask = browser.StandardOutput.ReadToEndAsync();
        var stderrTask = browser.StandardError.ReadToEndAsync();
        if (!browser.WaitForExit(120_000))
        {
            try
            {
                browser.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.True(false, "playwright flows timed out after 120s");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(browser.ExitCode == 0,
            $"playwright flows failed\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }

    /// <summary>临时诊断：把测试阶段写入固定文件，便于定位挂起位置。</summary>
    private static void Step(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "neoskin-net-browser-progress.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static Process StartServer(string root, string contentRoot, string database,
        string storage, int port)
    {
        string executable = FindAppExecutable(root);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(contentRoot);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["APP_ENV"] = "local";
        start.Environment["APP_DEBUG"] = "false";
        start.Environment["APP_KEY"] = "base64:" + Convert.ToBase64String(new byte[32]);
        start.Environment["DB_CONNECTION"] = "sqlite";
        start.Environment["DB_DATABASE"] = database;
        start.Environment["NEOSKIN_STORAGE_ROOT"] = storage;
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Console.WriteLine("[server] " + args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Console.WriteLine("[server:err] " + args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string FindAppExecutable(string root)
    {
        string name = OperatingSystem.IsWindows() ? "neoskin.exe" : "neoskin";
        string bin = Path.Combine(root, "src", "NeoSkin.Web", "bin");
        if (Directory.Exists(bin))
        {
            // AOT publish 会额外生成 native/neoskin.exe；它旁边没有 Resources/，
            // 直接启动会因 routes.json 缺失退出。优先选择带 Resources 的构建产物。
            string? found = Directory.GetFiles(bin, name, SearchOption.AllDirectories)
                .Where(file => File.Exists(Path.Combine(
                    Path.GetDirectoryName(file)!, "Resources", "codegraph", "routes.json")))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }
        throw new FileNotFoundException("neoskin executable not found under " + bin);
    }

    private static void CopyFrontend(string root, string contentRoot)
    {
        string source = Path.Combine(root, "src", "NeoSkin.Web", "wwwroot", "app");
        if (!Directory.Exists(source))
        {
            source = Path.Combine(root, "..", "blessing-skin-server-Laravel13", "public", "app");
        }
        if (!Directory.Exists(source))
        {
            return;
        }
        string target = Path.Combine(contentRoot, "wwwroot", "app");
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Replace(source, target))!);
            File.Copy(file, file.Replace(source, target), true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void WaitHealthy(string baseUrl, Process server)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 80; i++)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    $"server exited before becoming healthy (code {server.ExitCode}): {baseUrl}");
            }
            try
            {
                using HttpResponseMessage response = client.GetAsync(baseUrl + "/auth/login")
                    .GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("server did not become healthy: " + baseUrl);
    }

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "NeoSkin.NET.slnx")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
