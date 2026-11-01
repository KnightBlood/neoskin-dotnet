using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// Spatie Ignition 的三个管理端点（<c>/_ignition/*</c>）。
///
/// <p>源由 <c>RunnableSolutionsEnabled</c> 中间件门控：仅 <c>APP_DEBUG=true</c> 且
/// <c>APP_ENV</c> 为 local / development 时放行。本项目没有可执行的「解决方案」注册表，
/// 因此 <c>execute-solution</c> 恒返回 404，<c>health-check</c> 的
/// <c>can_execute_commands</c> 恒为 false（见 PORT-NOTES）。</p>
/// </summary>
public sealed class IgnitionController(Config.NeoSkinSettings settings)
{
    private static readonly string[] RunnableEnvs = ["local", "development"];

    private bool Enabled =>
        settings.Debug
        && RunnableEnvs.Contains(settings.Env, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>GET /_ignition/health-check</c>。</summary>
    public RequestHandler HealthCheck() => async (context, route, parameters) =>
    {
        if (!Enabled)
        {
            context.Response.StatusCode = 403;
            return;
        }

        var body = new PhpArray().With("can_execute_commands", false);
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>POST /_ignition/execute-solution</c>：没有解决方案注册表 → 404。</summary>
    public RequestHandler ExecuteSolution() => async (context, route, parameters) =>
    {
        if (!Enabled)
        {
            context.Response.StatusCode = 403;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), null, null);
        v.AddRule("solution", "required");
        if (!v.Passes())
        {
            await JsonAsync(context, 422, new Dictionary<string, object?>
            {
                ["message"] = Validator.Summarize(v.Errors.ToDictionary(
                    pair => pair.Key, pair => pair.Value)),
                ["errors"] = v.Errors.ToDictionary(pair => pair.Key, pair => pair.Value),
            });
            return;
        }

        await JsonAsync(context, 404, new Dictionary<string, object?>
        {
            ["message"] = "Solution could not be found",
        });
    };

    /// <summary><c>POST /_ignition/update-config</c>：写 <c>~/.ignition.json</c>。</summary>
    public RequestHandler UpdateConfig() => async (context, route, parameters) =>
    {
        if (!Enabled)
        {
            context.Response.StatusCode = 403;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), null, null);
        v.AddRule("theme", "required");
        v.AddRule("theme", "in:light,dark,auto");
        v.AddRule("editor", "required");
        v.AddRule("hide_solutions", "required");
        v.AddRule("hide_solutions", "boolean");
        if (!v.Passes())
        {
            await JsonAsync(context, 422, new Dictionary<string, object?>
            {
                ["message"] = Validator.Summarize(v.Errors.ToDictionary(
                    pair => pair.Key, pair => pair.Value)),
                ["errors"] = v.Errors.ToDictionary(pair => pair.Key, pair => pair.Value),
            });
            return;
        }

        var values = new PhpArray()
            .With("theme", RequestInput.String(input, "theme"))
            .With("editor", RequestInput.String(input, "editor"))
            .With("hide_solutions", RequestInput.ToBool(input.GetValueOrDefault("hide_solutions")));

        bool saved;
        try
        {
            string path = ConfigFile();
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, PhpJson.Encode(values, 0));
            saved = true;
        }
        catch (IOException)
        {
            saved = false;
        }

        await JsonAsync(context, 200, saved);
    };

    // ================================================================== 辅助

    /// <summary><c>FileConfigManager</c> 的配置路径（可用 <c>NEOSKIN_IGNITION_FILE</c> 覆盖）。</summary>
    private static string ConfigFile() =>
        Environment.GetEnvironmentVariable("NEOSKIN_IGNITION_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ignition.json");

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }

    private static I18n.Translator Translator(HttpContext context) =>
        context.Items["translator"] as I18n.Translator
        ?? new I18n.Translator(new I18n.TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";
}
