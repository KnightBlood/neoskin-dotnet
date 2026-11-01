using System.IO.Compression;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\PluginController</c> 的清单层移植。
///
/// <p>列表 / 启用 / 禁用 / 删除 / readme / config 可用；**插件代码不执行**
/// （AOT 无 PHP 运行时，见 PORT-NOTES）。上传 ZIP 与 wget 安装暂未实现。</p>
/// </summary>
public sealed class PluginController(
    PluginManager plugins,
    Option option,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>GET admin/plugins/data</c>。</summary>
    public RequestHandler GetPluginData() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        Translator translator = Translator(context);
        var data = new List<PhpArray>();
        foreach (Plugin plugin in plugins.All().Values)
        {
            data.Add(new PhpArray()
                .With("name", plugin.Name)
                .With("title", translator.Trans(plugin.Title, null, locale))
                .With("description", plugin.Description is null
                    ? ""
                    : translator.Trans(plugin.Description, null, locale))
                .With("version", plugin.Version)
                .With("enabled", plugin.Enabled)
                .With("readme", plugin.GetReadme() is not null)
                .With("config", plugin.HasConfig())
                .With("icon", plugin.Icon()));
        }

        await JsonAsync(context, 200, data);
    };

    /// <summary><c>POST admin/plugins/manage</c>。</summary>
    public RequestHandler Manage() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        Translator translator = Translator(context);
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string name = RequestInput.String(input, "name");
        string action = RequestInput.String(input, "action");

        Plugin? plugin = plugins.Get(name);
        if (plugin is null)
        {
            await JsonAsync(context, 200,
                Message(1, translator.Trans("admin.plugins.operations.not-found", null, locale)));
            return;
        }

        string title = translator.Trans(plugin.Title, null, locale);
        switch (action)
        {
            case "enable":
            {
                var failure = plugins.Enable(name);
                if (failure is null)
                {
                    PluginHost.PluginRuntime.Current?.Reload();
                    await JsonAsync(context, 200, Message(0,
                        translator.Trans("admin.plugins.operations.enabled",
                            new Dictionary<string, object?> { ["plugin"] = title }, locale)));
                }
                else
                {
                    List<string> lines = plugins.FormatUnresolved(failure.Value.Unsatisfied,
                        failure.Value.Conflicts);
                    await JsonAsync(context, 200, Message(1,
                        translator.Trans("admin.plugins.operations.unsatisfied.notice",
                            new Dictionary<string, object?> { ["reason"] = string.Join(" ", lines) },
                            locale)));
                }

                return;
            }

            case "disable":
                plugins.Disable(name);
                PluginHost.PluginRuntime.Current?.Reload();
                await JsonAsync(context, 200, Message(0,
                    translator.Trans("admin.plugins.operations.disabled",
                        new Dictionary<string, object?> { ["plugin"] = title }, locale)));
                return;

            case "delete":
                plugins.Delete(name);
                PluginHost.PluginRuntime.Current?.Reload();
                await JsonAsync(context, 200, Message(0,
                    translator.Trans("admin.plugins.operations.deleted", null, locale)));
                return;

            default:
                await JsonAsync(context, 200,
                    Message(1, translator.Trans("admin.invalid-action", null, locale)));
                return;
        }
    };

    /// <summary><c>GET admin/plugins/readme/{name}</c>：最小 HTML（Markdown 原样）。</summary>
    public RequestHandler Readme() => async (context, route, parameters) =>
    {
        string name = parameters.TryGetValue("name", out string? raw) ? raw ?? "" : "";
        Plugin? plugin = plugins.Get(name);
        string? readme = plugin?.GetReadme();
        if (plugin?.Path is null || readme is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(System.IO.Path.Combine(plugin.Path, readme));
        }
        catch (IOException)
        {
            context.Response.StatusCode = 404;
            return;
        }

        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            Middleware.AuthMiddleware.CurrentUser(context), Locale(context), Host(context),
            context.Request.Path.Value ?? "/admin/plugins/readme/" + name, settings.Debug,
            "admin", context.Items[NeoSkin.Web.Middleware.StartSessionMiddleware.SessionKey]
                as NeoSkin.Web.Session.SessionStore);
        vars["pageTitle"] = Translator(context).Trans(plugin.Title) ?? plugin.Title;
        vars["errors"] = TemplateErrors.Empty;
        vars["readme_html"] = HtmlSanitizer.Sanitize(
            MarkdownLite.Render(markdown), "readme");

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            TemplateRenderer.Render("admin/plugin/readme", vars));
    };

    /// <summary><c>GET|POST admin/plugins/config/{name}</c>：没有配置时 404。</summary>
    public RequestHandler Config() => async (context, route, parameters) =>
    {
        string name = parameters.TryGetValue("name", out string? raw) ? raw ?? "" : "";
        Plugin? plugin = plugins.Get(name);
        // 源会执行插件 PHP 配置类或渲染 Blade 视图；AOT 侧无法执行插件代码。
        context.Response.StatusCode = 404;
        await JsonAsync(context, 404, new PhpArray()
            .With("message", plugin is null || !plugin.HasConfig()
                ? Translator(context).Trans("admin.plugins.operations.no-config-notice")
                : Translator(context).Trans("admin.plugins.operations.no-config-notice")));
    };

    // ================================================================== 安装

    /// <summary><c>POST admin/plugins/upload</c>（multipart zip）。</summary>
    public RequestHandler Upload() => async (context, route, parameters) =>
    {
        IFormFile? file = null;
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            file = form.Files.GetFile("file");
        }

        if (file is null)
        {
            await ValidationErrorAsync(context, "file", "The file field is required.");
            return;
        }

        string mime = file.ContentType?.ToLowerInvariant() ?? "";
        if (!mime.Contains("zip", StringComparison.Ordinal))
        {
            await ValidationErrorAsync(context, "file",
                "The file must be a file of type: application/zip.");
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            InstallZip(buffer.ToArray());
            await JsonAsync(context, 200, Message(0,
                Translator(context).Trans("admin.plugins.market.install-success")));
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            await JsonAsync(context, 200, Message(1, e.Message));
        }
    };

    /// <summary><c>POST admin/plugins/wget</c>：按 URL 下载并安装。</summary>
    public RequestHandler Wget() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string url = RequestInput.String(input, "url");
        if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await ValidationErrorAsync(context, "url", "The url field is required.");
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            byte[] bytes = await client.GetByteArrayAsync(uri);
            InstallZip(bytes);
            await JsonAsync(context, 200, Message(0,
                Translator(context).Trans("admin.plugins.market.install-success")));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or InvalidDataException or IOException)
        {
            await JsonAsync(context, 200, Message(1,
                Translator(context).Trans("admin.download.errors.download",
                    new Dictionary<string, object?> { ["error"] = e.Message })));
        }
    };

    /// <summary>解压到第一个插件目录（不存在则创建；带路径穿越保护）。</summary>
    private void InstallZip(byte[] zip)
    {
        string target = plugins.GetPluginsDirs()[0];
        Directory.CreateDirectory(target);
        string root = Path.GetFullPath(target);

        using var stream = new MemoryStream(zip);
        using var archive = new System.IO.Compression.ZipArchive(stream,
            System.IO.Compression.ZipArchiveMode.Read);
        foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("解压运行包失败。");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    // ================================================================== 辅助

    private static async Task ValidationErrorAsync(HttpContext context, string field,
        string message)
    {
        await JsonAsync(context, 422, new Dictionary<string, object?>
        {
            ["message"] = message,
            ["errors"] = new Dictionary<string, List<string>>
            {
                [field] = [message],
            },
        });
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;

    private static PhpArray Message(int code, string message) =>
        new PhpArray().With("code", code).With("message", message);

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
