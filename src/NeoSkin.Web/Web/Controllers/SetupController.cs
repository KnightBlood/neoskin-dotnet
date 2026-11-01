using System.Data.Common;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\SetupController</c> 的移植（数据库配置 + 完成安装）。
///
/// <p>与源一致：<c>/setup/database</c> 先探测当前连接，可用则跳到 <c>/setup/info</c>；
/// 提交时试连数据库并把结果写回 <c>.env</c>。<c>/setup/finish</c> 执行建表、
/// 写入站点选项、创建超级管理员并写 <c>storage/install.lock</c>。</p>
///
/// <p>偏差：安装向导的 HTML 页面（welcome/info/database/finish/locked）依赖模板引擎，
/// 这里返回最小可用页面；表单字段名与源一致。</p>
/// </summary>
public sealed class SetupController(
    Option option,
    UserRepository users,
    ICipher cipher,
    Config.NeoSkinSettings settings,
    AppPaths paths,
    Database database)
{
    // ---------------------------------------------------------------- database

    /// <summary><c>GET|POST /setup/database</c>。</summary>
    public RequestHandler Database() => async (context, route, parameters) =>
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (DatabaseReachable())
            {
                Redirect(context, "http://" + Host(context) + "/setup/info");
                return;
            }

            await WriteHtmlAsync(context, 200, SetupView.Render("setup/wizard/database",
                DatabaseVariables(context)));
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string type = RequestInput.String(input, "type");
        string host = RequestInput.String(input, "host");
        string port = RequestInput.String(input, "port");
        string username = RequestInput.String(input, "username");
        string password = RequestInput.String(input, "password");
        string dbName = RequestInput.String(input, "db");
        string prefix = RequestInput.String(input, "prefix");

        try
        {
            TestConnection(type, host, port, username, password, dbName);
        }
        catch (Exception e)
        {
            string dbType = type switch
            {
                "mysql" => "MySQL/MariaDB",
                "sqlite" => "SQLite",
                "pgsql" => "PostgreSQL",
                _ => "",
            };
            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<p>" + System.Net.WebUtility.HtmlEncode(
                    Trans(context, "setup.database.connection-error",
                        new Dictionary<string, object?>
                        {
                            ["msg"] = e.Message,
                            ["type"] = dbType,
                        })) + "</p>");
            return;
        }

        WriteEnv(new Dictionary<string, string>
        {
            ["DB_CONNECTION"] = type,
            ["DB_HOST"] = host,
            ["DB_PORT"] = port,
            ["DB_DATABASE"] = dbName,
            ["DB_USERNAME"] = username,
            ["DB_PASSWORD"] = password,
            ["DB_PREFIX"] = prefix,
        });
        Redirect(context, "http://" + Host(context) + "/setup/info");
    };

    // ---------------------------------------------------------------- finish

    /// <summary><c>POST /setup/finish</c>。</summary>
    public RequestHandler Finish() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option, null);
        v.AddRule("email", "required");
        v.AddRule("email", "email");
        v.AddRule("nickname", "required");
        v.AddRule("password", "required");
        v.AddRule("password", "min:8");
        v.AddRule("password", "max:32");
        v.AddRule("password", "confirmed");
        v.AddRule("site_name", "required");
        if (!v.Passes())
        {
            SessionStore? session = context.Items[StartSessionMiddleware.SessionKey]
                as SessionStore;
            session?.Flash("errors", v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            Redirect(context, "http://" + Host(context) + "/setup/info");
            return;
        }

        // 源：php artisan migrate + passport:keys；本工程无 Passport 版本记录表
        new SchemaInstaller(database.Open, settings).Install();

        string siteUrl = "http://" + Host(context) + "/";
        if (siteUrl.EndsWith("/index.php", StringComparison.Ordinal))
        {
            siteUrl = siteUrl[..^10];
        }

        option.Set("site_name", RequestInput.String(input, "site_name"));
        option.Set("site_url", siteUrl);

        string now = PhpDate.Now(settings.Timezone);
        int initialScore = option.GetInt("user_initial_score", 0);
        users.InsertSuperAdmin(
            RequestInput.String(input, "email"),
            RequestInput.String(input, "nickname"),
            initialScore,
            cipher.Hash(RequestInput.String(input, "password"),
                option.GetString("pwd_salt", null)),
            ClientIp(context),
            now,
            PhpDate.At(DateTimeOffset.UtcNow.AddDays(-1), settings.Timezone));

        Directory.CreateDirectory(Path.GetDirectoryName(paths.InstallLock)!);
        File.WriteAllText(paths.InstallLock, "");

        await WriteHtmlAsync(context, 200,
            SetupView.Render("setup/wizard/finish", SetupVariables(context)));
    };

    // ---------------------------------------------------------------- 辅助

    private Dictionary<string, object?> SetupVariables(HttpContext context) =>
        SetupView.Variables(option, Translator(context), Locale(context), Host(context),
            context.Request.Path.Value ?? "/setup", Session(context), settings);

    private Dictionary<string, object?> DatabaseVariables(HttpContext context)
    {
        Dictionary<string, object?> vars = SetupVariables(context);
        vars["host"] = settings.Database.Host;
        vars["port"] = settings.Database.Port;
        vars["username"] = settings.Database.Username;
        vars["password"] = settings.Database.Password;
        vars["database"] = settings.Database.Name;
        vars["prefix"] = settings.Database.Prefix;
        return vars;
    }

    private bool DatabaseReachable()
    {
        try
        {
            using DbConnection connection = database.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TestConnection(string type, string host, string port, string username,
        string password, string database)
    {
        var temp = new Config.NeoSkinSettings();
        temp.Database.Connection = type;
        temp.Database.Host = host;
        temp.Database.Port = port;
        temp.Database.Username = username;
        temp.Database.Password = password;
        temp.Database.Name = database;

        using DbConnection connection = new Database(temp).Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.ExecuteScalar();
    }

    /// <summary><c>preg_replace('/KEY.+/', 'KEY=value', $env)</c>：逐行替换，缺失则追加。</summary>
    private void WriteEnv(Dictionary<string, string> values)
    {
        string envPath = Environment.GetEnvironmentVariable("NEOSKIN_ENV_FILE")
                         ?? Path.Combine(paths.Base, ".env");
        if (!File.Exists(envPath))
        {
            throw new InvalidOperationException(".env 文件不存在，请先创建后再安装。");
        }

        string content = File.ReadAllText(envPath);
        foreach ((string key, string value) in values)
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(key) + "[^\\r\\n]*");
            string replacement = key + "=" + value;
            content = pattern.IsMatch(content)
                ? pattern.Replace(content, replacement, 1)
                : content + (content.EndsWith('\n') ? "" : "\n") + replacement + "\n";
        }

        File.WriteAllText(envPath, content);
    }

    private static string FormPage(string title, (string Name, string Label, string Value)[] fields,
        string action, string button)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>")
          .Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title></head><body><h1>")
          .Append(System.Net.WebUtility.HtmlEncode(title)).Append("</h1><form method=\"post\" action=\"")
          .Append(action).Append("\">");
        foreach ((string name, string label, string value) in fields)
        {
            sb.Append("<label>").Append(System.Net.WebUtility.HtmlEncode(label))
              .Append(" <input name=\"").Append(name).Append("\" value=\"")
              .Append(System.Net.WebUtility.HtmlEncode(value)).Append("\"></label><br>");
        }

        sb.Append("<button type=\"submit\">").Append(System.Net.WebUtility.HtmlEncode(button))
          .Append("</button></form></body></html>");
        return sb.ToString();
    }

    private static async Task WriteHtmlAsync(HttpContext context, int status, string html)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    }

    private static string ClientIp(HttpContext context)
    {
        string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (forwarded.Length > 0)
        {
            return forwarded.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static string Trans(HttpContext context, string key,
        IReadOnlyDictionary<string, object?> replace) =>
        Translator(context).Trans(key, replace) ?? key;
}
