using System.IO.Compression;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\MarketController</c> 的移植：插件市场列表与安装。
///
/// <p>列表来源 <c>plugins_registry</c>（选项优先，其次配置），支持逗号分隔多仓库与
/// <c>{lang}</c> 占位符；安装时检查依赖并下载 <c>dist.url</c> 解压。</p>
/// </summary>
public sealed class MarketController(
    Option option,
    PluginManager plugins,
    Config.NeoSkinSettings settings)
{
    private static readonly string[] RegistryLocales = ["en", "zh_CN"];

    /// <summary><c>GET admin/plugins/market/list</c>。</summary>
    public RequestHandler MarketData() => async (context, route, parameters) =>
    {
        List<PhpArray> packages;
        try
        {
            packages = await FetchAsync(context);
        }
        catch (MarketException e)
        {
            await JsonAsync(context, 200, new PhpArray().With("code", 1)
                .With("message", e.Message));
            return;
        }

        var data = new PhpArray();
        int index = 0;
        foreach (PhpArray item in packages)
        {
            var current = new PhpArray();
            foreach (KeyValuePair<object, object?> entry in item)
            {
                current.Put(PhpString.Cast(entry.Key), entry.Value);
            }

            string name = item.Get("name") is { } raw ? PhpString.Cast(raw) : "";
            Plugin? installed = plugins.Get(name);
            if (installed is not null)
            {
                current.With("installed", installed.Version);
                current.With("can_update", Semver.GreaterThan(
                    item.Get("version") is { } version ? PhpString.Cast(version) : null,
                    installed.Version));
            }
            else
            {
                current.With("installed", false);
            }

            PhpArray requirements = item.Get("require") is PhpArray req ? req : new PhpArray();
            current.Remove("require");

            var fake = new Plugin(null, item);
            var dependencies = new PhpArray()
                .With("all", requirements)
                .With("unsatisfied", ToJson(plugins.GetUnsatisfied(fake)));
            current.With("dependencies", dependencies);
            data.Put(index++, current);
        }

        await JsonAsync(context, 200, data);
    };

    /// <summary><c>POST admin/plugins/market/download</c>。</summary>
    public RequestHandler Download() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string name = RequestInput.String(input, "name");

        List<PhpArray> packages;
        try
        {
            packages = await FetchAsync(context);
        }
        catch (MarketException e)
        {
            await JsonAsync(context, 200, new PhpArray().With("code", 1)
                .With("message", e.Message));
            return;
        }

        PhpArray? metadata = packages.FirstOrDefault(item =>
            item.Get("name") is { } raw && PhpString.Cast(raw) == name);
        if (metadata is null)
        {
            await JsonAsync(context, 200, new PhpArray()
                .With("code", 1)
                .With("message", Translator(context).Trans(
                    "admin.plugins.market.non-existent",
                    new Dictionary<string, object?> { ["plugin"] = name }, Locale(context))));
            return;
        }

        var fake = new Plugin(null, metadata);
        Dictionary<string, (string? Version, string Constraint)> unsatisfied =
            plugins.GetUnsatisfied(fake);
        Dictionary<string, (string? Version, string Constraint)> conflicts =
            plugins.GetConflicts(fake);
        if (unsatisfied.Count > 0 || conflicts.Count > 0)
        {
            List<string> reason = plugins.FormatUnresolved(unsatisfied, conflicts);
            await JsonAsync(context, 200, new PhpArray()
                .With("code", 1)
                .With("message", Translator(context).Trans("admin.plugins.market.unresolved",
                    new Dictionary<string, object?> { ["reason"] = string.Join(" ", reason) },
                    Locale(context))));
            return;
        }

        string? url = metadata.Get("dist") is PhpArray dist && dist.Get("url") is { } rawUrl
            ? PhpString.Cast(rawUrl)
            : null;
        if (url is null)
        {
            await JsonAsync(context, 200, new PhpArray()
                .With("code", 1)
                .With("message", Translator(context).Trans("admin.plugins.market.unresolved")));
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            byte[] bytes = await client.GetByteArrayAsync(url);
            string target = plugins.GetPluginsDirs()[0];
            Directory.CreateDirectory(target);
            ExtractZip(bytes, target);
            await JsonAsync(context, 200, new PhpArray()
                .With("code", 0)
                .With("message", Translator(context).Trans(
                    "admin.plugins.market.install-success")));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or InvalidDataException or IOException)
        {
            await JsonAsync(context, 200, new PhpArray()
                .With("code", 1)
                .With("message", Translator(context).Trans("admin.download.errors.download",
                    new Dictionary<string, object?> { ["error"] = e.Message },
                    Locale(context))));
        }
    };

    // ================================================================== fetch

    private sealed class MarketException(string message) : Exception(message);

    private async Task<List<PhpArray>> FetchAsync(HttpContext context)
    {
        string raw = option.Get("plugins_registry") is { } configured
            ? PhpString.Cast(configured)
            : "";
        if (raw.Length == 0)
        {
            raw = settings.Plugins.Registry;
        }

        string lang = Locale(context);
        if (!RegistryLocales.Contains(lang))
        {
            lang = Locales.FallbackLocale;
        }

        var packages = new List<PhpArray>();
        foreach (string registry in raw.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string url = registry.Replace("{lang}", lang);
            if (url.Length == 0)
            {
                continue;
            }

            string body;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                body = await client.GetStringAsync(url);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new MarketException(Translator(context).Trans(
                    "admin.plugins.market.connection-error",
                    new Dictionary<string, object?> { ["error"] = e.Message }, Locale(context)));
            }

            if (PhpJson.Decode(body) is not PhpArray decoded)
            {
                continue;
            }

            object? rawPackages = decoded.Get("packages");
            IEnumerable<object?> items = rawPackages switch
            {
                PhpArray array => array.Values,
                _ => [],
            };
            foreach (object? item in items)
            {
                if (item is PhpArray entry)
                {
                    packages.Add(entry);
                }
            }
        }

        return packages;
    }

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

    private static PhpArray ToJson(
        Dictionary<string, (string? Version, string Constraint)> values)
    {
        var array = new PhpArray();
        foreach ((string name, (string? version, string constraint)) in values)
        {
            array.With(name, new PhpArray()
                .With("version", version)
                .With("constraint", constraint));
        }

        return array;
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? Locales.AppLocale;

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
