using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;
using NeoSkin.Web.Web.Pagination;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\TranslationsController</c> 的移植（管理端翻译管理）。
/// </summary>
public sealed class TranslationsController(
    LanguageLineRepository lines,
    Config.NeoSkinSettings settings)
{
    private const string FrontEndGroup = "front-end";

    /// <summary><c>GET admin/i18n/list</c>：分页 JSON。</summary>
    public RequestHandler List() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        int page = RequestInput.Int(input, "page") is > 0 and int parsed ? parsed : 1;

        LanguageLineRepository.Page result = lines.List(page);
        var data = new List<Dictionary<string, object?>>();
        foreach (LanguageLineRepository.Row row in result.Rows)
        {
            data.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["group"] = row.Group,
                ["key"] = row.Key,
                ["text"] = row.Text,
                ["created_at"] = row.CreatedAt,
                ["updated_at"] = row.UpdatedAt,
            });
        }

        string path = "http://" + Host(context) + (context.Request.Path.Value ?? "/");
        Dictionary<string, object?> body = Paginator.Body(path, result.CurrentPage,
            result.PerPage, result.Total);
        body["data"] = data;
        await JsonAsync(context, 200, body);
    };

    /// <summary><c>POST admin/i18n</c>：新建一条翻译，成功后 302 到 <c>/admin/i18n</c>。</summary>
    public RequestHandler Create() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string group = RequestInput.String(input, "group");
        string key = RequestInput.String(input, "key");
        string text = RequestInput.String(input, "text");

        Dictionary<string, List<string>> errors = RequiredStrings(
            ("group", group), ("key", key), ("text", text));
        if (errors.Count > 0)
        {
            await ValidationFailedAsync(context, errors);
            return;
        }

        string locale = Locale(context);
        var row = new LanguageLineRepository.Row(0, group, key,
            new PhpArray().With(locale, text), null, null);
        lines.Save(row, settings.Timezone);

        Session(context)?.Put("success", true);
        if (context.Request.Headers.Accept.ToString()
            .Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(context, 200, Message(0, Trans(context, "admin.i18n.added")));
            return;
        }

        context.Response.StatusCode = 302;
        context.Response.Headers.Location = "http://" + Host(context) + "/admin/i18n";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await Task.CompletedTask;
    };

    /// <summary><c>PUT admin/i18n/{line}</c>：改当前 locale 的文案。</summary>
    public RequestHandler Update() => async (context, route, parameters) =>
    {
        LanguageLineRepository.Row? line = Bind(parameters);
        if (line is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string text = RequestInput.String(input, "text");
        Dictionary<string, List<string>> errors = RequiredStrings(("text", text));
        if (errors.Count > 0)
        {
            await ValidationFailedAsync(context, errors);
            return;
        }

        // array_merge 语义：已有 locale 覆盖，新 locale 追加到末尾
        line.Text.Put(Locale(context), text);
        lines.Save(line, settings.Timezone);

        await JsonAsync(context, 200, Message(0, Trans(context, "admin.i18n.updated")));
    };

    /// <summary><c>DELETE admin/i18n/{line}</c>。</summary>
    public RequestHandler Delete() => async (context, route, parameters) =>
    {
        LanguageLineRepository.Row? line = Bind(parameters);
        if (line is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        lines.Delete(line.Id);
        await JsonAsync(context, 200, Message(0, Trans(context, "admin.i18n.deleted")));
    };

    // ================================================================== 辅助

    private LanguageLineRepository.Row? Bind(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("line", out string? raw)
            || !int.TryParse(raw?.Trim(), out int id))
        {
            return null;
        }

        return lines.Find(id);
    }

    private static Dictionary<string, List<string>> RequiredStrings(
        params (string Field, string Value)[] fields)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string field, string value) in fields)
        {
            if (string.IsNullOrEmpty(value))
            {
                errors[field] = ["The " + field + " field is required."];
            }
        }

        return errors;
    }

    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        if (Error.ContentNegotiation.ExpectsJson(context))
        {
            await JsonAsync(context, 422, new Dictionary<string, object?>
            {
                ["message"] = "The given data was invalid.",
                ["errors"] = errors,
            });
            return;
        }

        SessionStore? session = Session(context);
        session?.Flash("errors", errors);
        string previous = context.Request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(previous))
        {
            previous = "http://" + Host(context) + "/admin/i18n";
        }

        context.Response.StatusCode = 302;
        context.Response.Headers.Location = previous;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await Task.CompletedTask;
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

    private static Dictionary<string, object?> Message(int code, string message) =>
        new(StringComparer.Ordinal) { ["code"] = code, ["message"] = message };

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
