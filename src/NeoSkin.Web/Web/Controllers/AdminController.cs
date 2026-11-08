using System.Globalization;
using NeoSkin.Web.Db;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Session;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\AdminController</c> 的移植。
///
/// <p>当前落地 <c>chartData</c>（<c>GET admin/chart</c>）；
/// <c>index</c> / <c>status</c> 是 widget 网格 HTML，等模板引擎。</p>
/// </summary>
public sealed class AdminController(
    Option option,
    UserRepository users,
    PlayerRepository players,
    TextureRepository textures,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>GET admin</c>：仪表盘（<c>admin/index</c> 模板）。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        long storage = textures.SumSize();

        Dictionary<string, object?> vars = PageVars(context, user, "general.dashboard");
        vars["sum"] = new PhpArray()
            .With("users", users.CountAll())
            .With("players", players.CountAll())
            .With("textures", textures.CountAll())
            .With("storage", storage);
        vars["sum_storage_display"] = StorageDisplay(storage);
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-6", "admin/widgets/dashboard/usage"),
            PageView.Column("md-6", "admin/widgets/dashboard/chart"),
        });

        await WriteHtml(context, TemplateRenderer.Render("admin/index", vars));
    };

    /// <summary><c>GET admin/status</c>：运行状态（<c>admin/status</c> 模板）。</summary>
    public RequestHandler Status() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);

        var bs = new PhpArray()
            .With("version", settings.Version)
            .With("env", settings.Env)
            .With("debug", settings.Debug
                ? Trans(context, "general.yes")
                : Trans(context, "general.no"))
            .With("commit", "")
            .With("laravel", "13.38.0");

        string server = context.Request.Headers.Server.ToString();
        var serverInfo = new PhpArray()
            .With("php", Environment.Version.ToString())
            .With("web", string.IsNullOrEmpty(server)
                ? Trans(context, "general.unknown")
                : server)
            .With("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        string connection = settings.Database.Connection;
        bool network = connection != "sqlite";
        var db = new PhpArray()
            .With("type", connection switch
            {
                "mysql" => "MySQL/MariaDB",
                "sqlite" => "SQLite",
                "pgsql" => "PostgreSQL",
                _ => "",
            })
            .With("host", network ? settings.Database.Host : "")
            .With("port", network ? settings.Database.Port : "")
            .With("username", network ? settings.Database.Username : "")
            .With("database", settings.Database.Name)
            .With("prefix", settings.Database.Prefix);

        // 管理端 SPA 通过 Accept: application/json 或 X-Neoskin-Spa 读取 JSON。
        bool wantsJson = context.Request.Headers.ContainsKey("X-Neoskin-Spa")
            || (context.Request.Headers.Accept.ToString().Contains("application/json",
                StringComparison.OrdinalIgnoreCase));
        if (wantsJson)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(PhpJson.Encode(new PhpArray()
                .With("bs", bs)
                .With("server", serverInfo)
                .With("db", db)
                .With("plugins", new List<object?>()), PhpJson.JsonUnescapedUnicode));
            return;
        }

        Dictionary<string, object?> vars = PageVars(context, user, "general.status");
        vars["detail"] = new PhpArray()
            .With("bs", bs)
            .With("server", serverInfo)
            .With("db", db);
        vars["plugins"] = new List<object?>();
        vars["plugins_title"] = Translator(context).Trans("admin.status.plugins",
            new Dictionary<string, object?> { ["amount"] = 0 }) ?? "admin.status.plugins";
        vars["gridRows"] = PageView.Grid(new List<object?>
        {
            PageView.Column("md-6", "admin/widgets/status/info"),
            PageView.Column("md-6", "admin/widgets/status/plugins"),
        });

        await WriteHtml(context, TemplateRenderer.Render("admin/status", vars));
    };

    private Dictionary<string, object?> PageVars(HttpContext context,
        UserRepository.UserRow? user, string titleKey)
    {
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context), context.Request.Path.Value ?? "/admin",
            settings.Debug, "admin", Session(context));
        vars["pageTitle"] = Translator(context).Trans(titleKey) ?? titleKey;
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    internal static string StorageDisplay(long kilobytes)
    {
        if (kilobytes > 1024)
        {
            decimal value = Math.Round(kilobytes / 1024m, 1, MidpointRounding.AwayFromZero);
            return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "MB";
        }

        return kilobytes + "KB";
    }
    /// <summary><c>GET admin/chart</c>：最近 31 天（含今天）的注册/上传折线数据。</summary>
    public RequestHandler ChartData() => async (context, route, parameters) =>
    {
        string locale = Locale(context);
        DateOnly today = DateOnly.FromDateTime(
            DateTime.ParseExact(PhpDate.Now(settings.Timezone)[..10], "yyyy-MM-dd",
                CultureInfo.InvariantCulture));
        DateOnly oneMonthAgo = SubMonthPhp(today);
        string since = oneMonthAgo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + " 00:00:00";

        Dictionary<string, int> userRegistration = GroupByDay(users.RegisterTimesSince(since), locale);
        Dictionary<string, int> textureUploads =
            GroupByDay(textures.UploadTimesSince(since), locale);

        var xAxis = new List<string>();
        var registrationData = new List<int>();
        var uploadData = new List<int>();

        for (int i = 1; i <= 31; i++)
        {
            string day = CarbonL(today.AddDays(-(31 - i)), locale);
            xAxis.Add(day);
            registrationData.Add(userRegistration.GetValueOrDefault(day));
            uploadData.Add(textureUploads.GetValueOrDefault(day));
        }

        var payload = new PhpArray()
            .With("labels", new List<string>
            {
                Trans(context, "admin.index.user-registration"),
                Trans(context, "admin.index.texture-uploads"),
            })
            .With("xAxis", xAxis)
            .With("data", new List<List<int>> { registrationData, uploadData });

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            PhpJson.Encode(payload, PhpJson.JsonUnescapedUnicode));
    };

    // ================================================================== 辅助

    /// <summary>按本地化日期串分组计数（与源按 <c>isoFormat('l')</c> groupBy 一致）。</summary>
    private static Dictionary<string, int> GroupByDay(IEnumerable<string> timestamps, string locale)
    {
        var grouped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string timestamp in timestamps)
        {
            DateTime? parsed = PhpDate.Parse(timestamp);
            if (parsed is null)
            {
                continue;
            }

            string day = CarbonL(DateOnly.FromDateTime(parsed.Value), locale);
            grouped[day] = grouped.GetValueOrDefault(day) + 1;
        }

        return grouped;
    }

    /// <summary><c>Carbon::today()-&gt;subMonth()</c>：PHP 的月减法会溢出到下个月。</summary>
    internal static DateOnly SubMonthPhp(DateOnly date)
    {
        int totalMonths = (date.Year * 12) + (date.Month - 1) - 1;
        int year = (int)Math.Floor(totalMonths / 12.0);
        int month = ((totalMonths % 12) + 12) % 12 + 1;
        return new DateOnly(year, month, 1).AddDays(date.Day - 1);
    }

    /// <summary>Carbon <c>isoFormat('l')</c> 的 CLDR 短日期（按 locale）。</summary>
    internal static string CarbonL(DateOnly date, string locale) => (locale ?? "en") switch
    {
        "zh_CN" or "zh_TW" or "ja_JP" => date.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
        "ko_KR" => date.ToString("yyyy.M.d.", CultureInfo.InvariantCulture),
        "de_DE" or "ru_RU" => date.ToString("d.M.yyyy", CultureInfo.InvariantCulture),
        "el_GR" or "es_ES" or "fr_FR" or "it_IT" or "pt_PT" =>
            date.ToString("d/M/yyyy", CultureInfo.InvariantCulture),
        "nl_NL" => date.ToString("d-M-yyyy", CultureInfo.InvariantCulture),
        _ => date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
    };

    private UserRepository.UserRow? SessionUser(HttpContext context)
    {
        SessionStore? session = Session(context);
        return session is null ? null : new Auth.SessionGuard(session, users).User();
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static async Task WriteHtml(HttpContext context, string html)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;
}
