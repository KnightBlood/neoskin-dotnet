using System.Security.Cryptography;
using NeoSkin.Web.Db;
using NeoSkin.Web.Error;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\NotificationsController</c> 的移植。
///
/// <p><c>send</c> 复用 <c>communications</c> 数据模型：先写一条
/// <c>kind=message / status=published / editor=source</c> 的通信，再按收件人集合
/// 逐个写入 <c>notifications</c>（<c>SiteMessage</c> 的 database 通道）。</p>
/// </summary>
public sealed class NotificationsController(
    Option option,
    UserRepository users,
    CommunicationRepository communications,
    NotificationRepository notifications,
    Communications service,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>POST admin/notifications/send</c> / <c>POST api/admin/notifications</c>。</summary>
    public RequestHandler Send() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("receiver", "required");
        v.AddRule("receiver", "in:all,normal,uid,email");
        v.AddRule("uid", "required_if:receiver,uid");
        v.AddRule("uid", "nullable");
        v.AddRule("uid", "integer");
        v.AddRule("uid", "exists:users");
        v.AddRule("email", "required_if:receiver,email");
        v.AddRule("email", "nullable");
        v.AddRule("email", "email");
        v.AddRule("email", "exists:users");
        v.AddRule("title", "required");
        v.AddRule("title", "max:20");
        v.AddRule("content", "string");
        v.AddRule("content", "nullable");
        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return;
        }

        string receiver = RequestInput.String(input, "receiver");
        string title = RequestInput.String(input, "title");
        string content = RequestInput.String(input, "content");
        int? uid = input.ContainsKey("uid") ? RequestInput.ToInt(input["uid"]) : null;
        string? email = input.ContainsKey("email") ? RequestInput.String(input, "email") : null;
        string now = PhpDate.Now(settings.Timezone);

        var audience = new PhpArray().With("receiver", receiver);
        if (input.ContainsKey("uid"))
        {
            audience.With("uid", input["uid"]);
        }

        if (input.ContainsKey("email"))
        {
            audience.With("email", input["email"]);
        }

        var message = new CommunicationRepository.Row(0, user.Uid, title,
            CommunicationRepository.KindMessage, CommunicationRepository.StatusPublished, "",
            "source", content, null, PhpJson.Encode(audience, 0), now, RandomToken(), now, now);
        long messageId = communications.Insert(message);

        var payload = new PhpArray()
            .With("communication_id", messageId)
            .With("delivery_token", message.DeliveryToken)
            .With("format", "html")
            .With("title", title)
            .With("content", content);
        string dataJson = PhpJson.Encode(payload, 0);

        foreach (int notifiable in users.IdsForAudience(receiver, uid, email))
        {
            notifications.Insert(Guid.NewGuid().ToString(), notifiable, dataJson, now);
        }

        Session(context)?.Put("sentResult",
            Trans(context, "admin.notifications.send.success"));
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = "http://" + Host(context) + "/admin";
        context.Response.Headers["Cache-Control"] = "no-cache, private";
        await Task.CompletedTask;
    };

    /// <summary><c>GET api/user/notifications</c>：全部未读（id + title）。</summary>
    public RequestHandler All() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var items = new List<Dictionary<string, object?>>();
        foreach (NotificationRepository.Row row in notifications.Unread(user.Uid, 1000))
        {
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["title"] = service.Title(row),
            });
        }

        await JsonAsync(context, 200, items);
    };

    /// <summary><c>POST user/notifications/{id}</c> / <c>POST api/user/notifications/{id}</c>。</summary>
    public RequestHandler Read() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string id = parameters.TryGetValue("id", out string? raw) ? raw ?? "" : "";
        NotificationRepository.Row? row = notifications.FindByNotifiableAndId(user.Uid, id);
        if (row is null || row.ReadAt is not null || !service.Visible(row))
        {
            context.Response.StatusCode = 404;
            return;
        }

        notifications.MarkAsRead(id, PhpDate.Now(settings.Timezone));

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = service.Title(row),
            ["content"] = service.Content(row),
            ["time"] = row.CreatedAt,
        };
        await JsonAsync(context, 200, body);
    };

    // ================================================================== 辅助

    private static string RandomToken() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        if (ContentNegotiation.ExpectsJson(context))
        {
            await JsonAsync(context, 422, new Dictionary<string, object?>
            {
                ["message"] = Validator.Summarize(errors),
                ["errors"] = errors,
            });
            return;
        }

        SessionStore? session = Session(context);
        session?.Flash("errors", errors);
        string previous = context.Request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(previous))
        {
            previous = "http://" + Host(context) + "/admin";
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

    private static I18n.Translator Translator(HttpContext context) =>
        context.Items["translator"] as I18n.Translator
        ?? new I18n.Translator(new I18n.TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static string Trans(HttpContext context, string key) =>
        Translator(context).Trans(key) ?? key;

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
