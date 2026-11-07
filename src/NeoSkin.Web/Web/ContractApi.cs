using System.Text.RegularExpressions;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web;

/// <summary>
/// 契约 v1 的用户中心 JSON 端点（站内信 / 我的举报）。
///
/// <p>与 <see cref="SpaFrontend"/> 一样挂在 174 条 PHP 路由表之前：这些是新增契约端点，
/// 不改变既有路由。</p>
/// </summary>
public static class ContractApi
{
    private const int DefaultPerPage = 10;
    private const int MaxPerPage = 100;

    private static readonly Regex HtmlTag = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>是否为本站点新增的用户中心契约端点。</summary>
    public static bool Matches(string path) =>
        path == "/api/user/inbox"
        || path.StartsWith("/api/user/inbox/", StringComparison.Ordinal)
        || path == "/api/user/reports"
        || path == "/api/admin/dashboard";

    /// <summary>处理请求；返回 <c>false</c> 表示交给后续路由分发器。</summary>
    public static async Task<bool> HandleAsync(HttpContext context, UserRepository users,
        PlayerRepository players, NotificationRepository notifications, ReportRepository reports,
        TextureRepository textures, Communications communications, NeoSkinSettings settings)
    {
        string path = context.Request.Path.Value ?? "/";
        if (!Matches(path))
        {
            return false;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        UserRepository.UserRow? user = CurrentUser(context, users);
        if (user is null)
        {
            await WriteJsonAsync(context, 401, new Dictionary<string, object?>
            {
                ["message"] = "Unauthenticated.",
            });
            return true;
        }

        if (path == "/api/admin/dashboard")
        {
            if (!user.IsAdmin)
            {
                await WriteJsonAsync(context, 403, new Dictionary<string, object?>
                {
                    ["message"] = "Forbidden",
                });
                return true;
            }

            long storage = textures.SumSize();
            await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["users"] = users.CountAll(),
                ["players"] = players.CountAll(),
                ["textures"] = textures.CountAll(),
                ["storage"] = storage,
                ["storageHuman"] = StorageDisplay(storage),
            });
            return true;
        }

        if (path == "/api/user/reports")
        {
            await WriteReportsAsync(context, reports, textures, user.Uid);
            return true;
        }

        if (path == "/api/user/inbox")
        {
            await WriteInboxAsync(context, notifications, communications, user.Uid);
            return true;
        }

        string id = Uri.UnescapeDataString(path["/api/user/inbox/".Length..]);
        if (id.Length == 0 || id.Contains('/'))
        {
            await WriteNotFoundAsync(context);
            return true;
        }

        await WriteMessageAsync(context, notifications, communications, user.Uid, id, settings);
        return true;
    }

    private static async Task WriteInboxAsync(HttpContext context,
        NotificationRepository notifications, Communications communications, long uid)
    {
        (int page, int perPage) = Paging(context);
        long total = notifications.CountInbox(uid);
        long unread = notifications.CountUnread(uid);
        int lastPage = LastPage(total, perPage);

        var items = new List<Dictionary<string, object?>>();
        foreach (NotificationRepository.Row row in notifications.InboxPage(
                     uid, perPage, (page - 1) * perPage))
        {
            if (!communications.Visible(row))
            {
                continue;
            }

            string content = communications.Content(row);
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["title"] = communications.Title(row),
                ["preview"] = Preview(content, 120),
                ["format"] = row.Data.Get("format") is { } format ? PhpString.Cast(format) : "html",
                ["read_at"] = Nullable(row.ReadAt),
                ["created_at"] = row.CreatedAt,
                ["communication_id"] = CommunicationId(row.Data),
            });
        }

        await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["data"] = items,
            ["current_page"] = page,
            ["last_page"] = lastPage,
            ["per_page"] = perPage,
            ["total"] = total,
            ["unread"] = unread,
        });
    }

    private static async Task WriteMessageAsync(HttpContext context,
        NotificationRepository notifications, Communications communications, long uid, string id,
        NeoSkinSettings settings)
    {
        NotificationRepository.Row? row = notifications.FindByNotifiableAndId(uid, id);
        if (row is null || !communications.Visible(row))
        {
            await WriteNotFoundAsync(context);
            return;
        }

        string readAt = row.ReadAt ?? "";
        if (readAt.Length == 0)
        {
            readAt = PhpDate.Now(settings.Timezone);
            notifications.MarkAsRead(id, readAt);
        }

        await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = 0,
            ["message"] = "",
            ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["title"] = communications.Title(row),
                ["content"] = communications.Content(row),
                ["format"] = row.Data.Get("format") is { } format ? PhpString.Cast(format) : "html",
                ["read_at"] = readAt,
                ["created_at"] = row.CreatedAt,
                ["communication_id"] = CommunicationId(row.Data),
            },
        });
    }

    private static async Task WriteReportsAsync(HttpContext context, ReportRepository reports,
        TextureRepository textures, long uid)
    {
        (int page, int perPage) = Paging(context);
        ReportRepository.Page result = reports.PageByReporter((int)uid, page, perPage);

        var items = new List<Dictionary<string, object?>>();
        foreach (ReportRepository.Row row in result.Rows)
        {
            TextureRow? texture = textures.FindByTid(row.Tid);
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["tid"] = row.Tid,
                ["texture_name"] = texture?.Name ?? "",
                ["reason"] = row.Reason,
                ["status"] = row.Status,
                ["report_at"] = row.ReportAt,
            });
        }

        await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["data"] = items,
            ["current_page"] = result.CurrentPage,
            ["last_page"] = LastPage(result.Total, perPage),
            ["per_page"] = perPage,
            ["total"] = result.Total,
        });
    }

    private static (int Page, int PerPage) Paging(HttpContext context)
    {
        int page = ParsePositive(context.Request.Query["page"], 1);
        int perPage = ParsePositive(context.Request.Query["per_page"], DefaultPerPage);
        return (page, Math.Clamp(perPage, 1, MaxPerPage));
    }

    private static int ParsePositive(string? raw, int fallback) =>
        int.TryParse(raw, out int value) && value > 0 ? value : fallback;

    private static int LastPage(long total, int perPage) =>
        total <= 0 ? 1 : (int)((total + perPage - 1) / perPage);

    private static long? CommunicationId(PhpArray data)
    {
        object? raw = data.Get("communication_id");
        if (raw is null)
        {
            return null;
        }

        return long.TryParse(PhpString.Cast(raw), out long value) && value > 0 ? value : null;
    }

    private static string Preview(string content, int limit)
    {
        if (content.Length == 0)
        {
            return "";
        }

        string plain = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(content, " "));
        plain = Regex.Replace(plain, "\\s+", " ").Trim();
        return plain.Length > limit ? plain[..limit] : plain;
    }

    private static string? Nullable(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string StorageDisplay(long storage) =>
        storage > 1024
            ? (storage / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "MB"
            : storage + "KB";

    private static UserRepository.UserRow? CurrentUser(HttpContext context, UserRepository users) =>
        context.Items[StartSessionMiddleware.SessionKey] is SessionStore session
            ? new Auth.SessionGuard(session, users).User()
            : null;

    private static async Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"message\":\"Not Found\"}");
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
