using System.IO.Compression;
using System.Text.RegularExpressions;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\UpdateController</c> 的移植（ZIP 在线更新）。
///
/// <p>更新信息来自 <c>option('update_source')</c> 或配置 <c>UPDATE_SOURCE</c>，
/// 返回 <c>{spec:2, latest, php, url, package, sha256}</c>。完整包下载后校验 SHA-256
/// 并暂存到会话，安装时解压；旧式补丁包直接解压到站点根目录。</p>
///
/// <p>偏差：不执行数据库迁移脚本（由新版本的迁移负责），仅解压文件；页面为手写布局。</p>
/// </summary>
public sealed class UpdateController(
    Option option,
    Config.NeoSkinSettings settings,
    Paths.AppPaths paths)
{
    public const int Spec = 2;

    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}$");

    private static string PendingKey => "system_update_package";

    private string UpdateDir => Path.Combine(paths.Storage, "framework", "update");

    // ================================================================== 页面

    /// <summary><c>GET admin/update</c>。</summary>
    public RequestHandler ShowUpdatePage() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        (bool ok, PhpArray? info, string? error) = await FetchInfoAsync(context);
        bool canUpdate = info is not null && CanUpdate(context, info);
        SessionStore? session = Session(context);

        string locale = Locale(context);
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, locale, Host(context), context.Request.Path.Value ?? "/admin/update",
            settings.Debug, "admin", session);
        vars["pageTitle"] = Trans(context, "general.check-update");
        vars["errors"] = TemplateErrors.Empty;
        vars["info"] = new PhpArray()
            .With("latest", info?.Get("latest")?.ToString())
            .With("current", settings.Version);
        if (!string.IsNullOrEmpty(error))
        {
            vars["error"] = error;
            vars["connection_error"] = Trans(context, "admin.update.errors.connection",
                new Dictionary<string, object?> { ["error"] = error });
        }

        vars["can_update"] = canUpdate;
        if (session?.Get(PendingKey) is PhpArray pending)
        {
            if (pending.Get("count") is null)
            {
                pending.Put("count", 0L);
            }

            if (pending.Get("id") is null)
            {
                pending.Put("id", 1L);
            }

            vars["pending_package"] = pending;
        }

        var releases = new List<object?>();
        foreach (PatchReleases.Release release in PatchReleases.All())
        {
            releases.Add(new PhpArray()
                .With("version", release.Version)
                .With("date", release.Date)
                .With("notes", release.Notes.Cast<object?>().ToList()));
        }

        vars["patch_releases"] = releases;
        vars["upload_limits"] = new PhpArray()
            .With("enabled", true)
            .With("application", "64 MB")
            .With("php_post", "64 MB")
            .With("php_upload", "64 MB")
            .With("effective", "64 MB");
        vars["cautions_html"] = Trans(context, "admin.update.cautions.text")
            .Replace("\n", "<br />\n", StringComparison.Ordinal);

        string html = TemplateRenderer.Render("admin/update", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };

    /// <summary><c>GET /api/admin/update/info</c>：更新信息 JSON（管理端 SPA）。</summary>
    public async Task InfoAsync(HttpContext context)
    {
        (bool ok, PhpArray? info, string? error) = await FetchInfoAsync(context);
        bool canUpdate = info is not null && CanUpdate(context, info);
        SessionStore? session = Session(context);
        string notice = "";
        if (session?.Get("update_notice") is string pendingNotice)
        {
            notice = pendingNotice;
            session.Forget("update_notice");
        }

        var body = new PhpArray()
            .With("latest", info?.Get("latest")?.ToString() ?? "")
            .With("current", settings.Version)
            .With("error", error ?? "")
            .With("can_update", canUpdate)
            .With("package", session?.Get(PendingKey))
            .With("notice", notice)
            .With("upload_limits", new PhpArray()
                .With("application", "64 MB")
                .With("upload", "64 MB"));

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }

    // ================================================================== download

    /// <summary><c>POST admin/update/download</c>。</summary>
    public RequestHandler Download() => async (context, route, parameters) =>
    {
        (bool ok, PhpArray? info, string? error) = await FetchInfoAsync(context);
        if (!ok || info is null || !CanUpdate(context, info))
        {
            await JsonAsync(context, 200, Message(1,
                Trans(context, "admin.update.info.up-to-date")));
            return;
        }

        string url = info.Get("url")?.ToString() ?? "";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            byte[] zip = await client.GetByteArrayAsync(url);
            string package = info.Get("package")?.ToString() ?? "";
            if (package == "full")
            {
                string checksum = info.Get("sha256")?.ToString() ?? "";
                if (!Sha256Pattern.IsMatch(checksum)
                    || !string.Equals(checksum, Sha256Hex(zip), StringComparison.OrdinalIgnoreCase))
                {
                    await JsonAsync(context, 200, Message(1, "运行包校验值与更新信息不一致。"));
                    return;
                }

                string? version = ReadVersionFromZip(zip) ?? info.Get("latest")?.ToString();
                Directory.CreateDirectory(UpdateDir);
                string file = Path.Combine(UpdateDir, "package.zip");
                await File.WriteAllBytesAsync(file, zip);
                Session(context)?.Put(PendingKey, new PhpArray()
                    .With("path", file)
                    .With("version", version)
                    .With("sha256", checksum));

                await JsonAsync(context, 200, Message(0, "运行包已下载并校验，请确认后安装。",
                    new PhpArray().With("review", true)));
                return;
            }

            ExtractZip(zip, paths.Base);
            RunMigrations();
            await JsonAsync(context, 200, Message(0, Trans(context, "admin.update.complete")));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or InvalidDataException or IOException)
        {
            await JsonAsync(context, 200, Message(1, Trans(context,
                "admin.download.errors.download",
                new Dictionary<string, object?> { ["error"] = e.Message })));
        }
    };

    // ================================================================== upload

    /// <summary><c>POST admin/update/upload</c>（multipart）。</summary>
    public RequestHandler Upload() => async (context, route, parameters) =>
    {
        IFormFile? file = null;
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            file = form.Files.GetFile("package");
        }

        SessionStore? session = Session(context);
        if (file is null)
        {
            session?.Flash("update_error", "请选择 ZIP 运行包。");
            Redirect(context, "/admin/update");
            return;
        }

        if (file.Length > 64L * 1024 * 1024)
        {
            session?.Flash("update_error", "运行包超过上传上限。");
            Redirect(context, "/admin/update");
            return;
        }

        string name = file.FileName ?? "";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            session?.Flash("update_error", "请选择 ZIP 运行包。");
            Redirect(context, "/admin/update");
            return;
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        byte[] zip = buffer.ToArray();
        Directory.CreateDirectory(UpdateDir);
        string path = Path.Combine(UpdateDir, "package.zip");
        await File.WriteAllBytesAsync(path, zip);
        session?.Put(PendingKey, new PhpArray()
            .With("path", path)
            .With("version", ReadVersionFromZip(zip) ?? "unknown")
            .With("sha256", Sha256Hex(zip)));
        session?.Flash("update_notice", "运行包校验完成，请确认版本与校验值后安装。");
        Redirect(context, "/admin/update");
    };

    // ================================================================== install

    /// <summary><c>POST admin/update/install</c>。</summary>
    public RequestHandler Install() => async (context, route, parameters) =>
    {
        SessionStore? session = Session(context);
        if (session?.Get(PendingKey) is not PhpArray pending
            || pending.Get("path")?.ToString() is not { Length: > 0 } path
            || !File.Exists(path))
        {
            await JsonAsync(context, 200, Message(1, "没有待安装的运行包。"));
            return;
        }

        try
        {
            byte[] zip = await File.ReadAllBytesAsync(path);
            ExtractZip(zip, paths.Base);
            RunMigrations();
            File.Delete(path);
            session.Forget(PendingKey);
            await JsonAsync(context, 200, Message(0, Trans(context, "admin.update.complete")));
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            await JsonAsync(context, 200, Message(1, e.Message));
        }
    };

    /// <summary>解压完成后执行 .NET 侧数据库迁移（对应 Java DataMigrator）。</summary>
    private void RunMigrations()
    {
        var database = new Database(settings);
        var schema = new SchemaInstaller(database.Open, settings);
        if (!schema.IsInstalled())
        {
            return;
        }

        new SchemaMigrator(database, schema, option, settings).Migrate();
    }

    // ================================================================== 辅助

    private async Task<(bool Ok, PhpArray? Info, string? Error)> FetchInfoAsync(
        HttpContext context)
    {
        string source = option.Get("update_source") is { } configured
            ? PhpString.Cast(configured)
            : "";
        if (source.Length == 0)
        {
            source = settings.UpdateSource;
        }

        if (source.Length == 0)
        {
            return (false, null, "更新源未配置");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string body = await client.GetStringAsync(source);
            if (PhpJson.Decode(body) is PhpArray info
                && info.Get("spec") is { } spec
                && long.TryParse(PhpString.Cast(spec), out long specValue)
                && specValue == Spec)
            {
                return (true, info, null);
            }

            return (false, null, Trans(context, "admin.update.errors.spec"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, null, "更新源连接失败：" + e.Message);
        }
    }

    private bool CanUpdate(HttpContext context, PhpArray info)
    {
        if (info.Get("latest") is not { } latest)
        {
            return false;
        }

        return Semver.GreaterThan(PhpString.Cast(latest), settings.Version);
    }

    /// <summary>安全解压：条目不得逃逸目标目录。</summary>
    private static void ExtractZip(byte[] zip, string target)
    {
        string root = Path.GetFullPath(target);
        using var stream = new MemoryStream(zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
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

    /// <summary>从 ZIP 里读版本：<c>version.txt</c> 或 <c>config/app.php</c> 的 version。</summary>
    private static string? ReadVersionFromZip(byte[] zip)
    {
        try
        {
            using var stream = new MemoryStream(zip);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            ZipArchiveEntry? versionFile = archive.Entries
                .FirstOrDefault(e => e.FullName.Equals("version.txt",
                    StringComparison.OrdinalIgnoreCase));
            if (versionFile is not null)
            {
                using var reader = new StreamReader(versionFile.Open());
                string text = reader.ReadToEnd().Trim();
                return text.Length == 0 ? null : text;
            }

            ZipArchiveEntry? appConfig = archive.Entries
                .FirstOrDefault(e => e.FullName.EndsWith("config/app.php",
                    StringComparison.OrdinalIgnoreCase));
            if (appConfig is not null)
            {
                using var reader = new StreamReader(appConfig.Open());
                Match match = Regex.Match(reader.ReadToEnd(),
                    @"'version'\s*=>\s*'([^']+)'");
                return match.Success ? match.Groups[1].Value : null;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }

        return null;
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static string PageLayoutVersion(string version) =>
        System.Net.WebUtility.HtmlEncode(version);

    private static string HiddenToken(HttpContext context) =>
        "<input type=\"hidden\" name=\"_token\" value=\""
        + System.Net.WebUtility.HtmlEncode(Session(context)?.Token()?.ToString() ?? "")
        + "\">";

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static string Trans(HttpContext context, string key,
        IReadOnlyDictionary<string, object?> replace) =>
        Translator(context).Trans(key, replace) ?? key;

    private static PhpArray Message(int code, string message) =>
        new PhpArray().With("code", code).With("message", message);

    private static PhpArray Message(int code, string message, PhpArray data) =>
        new PhpArray().With("code", code).With("message", message).With("data", data);

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
