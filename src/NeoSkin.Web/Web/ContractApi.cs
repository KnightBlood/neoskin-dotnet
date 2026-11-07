using System.Text.RegularExpressions;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web.Input;

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
        || path == "/api/admin/dashboard"
        || path == "/api/admin/update/info"
        || path.StartsWith("/api/admin/options/", StringComparison.Ordinal)
        || path == "/api/admin/communications"
        || path.StartsWith("/api/admin/communications/", StringComparison.Ordinal);

    /// <summary>处理请求；返回 <c>false</c> 表示交给后续路由分发器。</summary>
    public static async Task<bool> HandleAsync(HttpContext context, UserRepository users,
        PlayerRepository players, NotificationRepository notifications, ReportRepository reports,
        TextureRepository textures, Communications communications, CommunicationRepository
        communicationRepository, Option option, Controllers.UpdateController updateController,
        NeoSkinSettings settings)
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

        if (path == "/api/admin/update/info")
        {
            if (!user.IsAdmin)
            {
                await WriteJsonAsync(context, 403, new Dictionary<string, object?>
                {
                    ["message"] = "Forbidden",
                });
                return true;
            }

            await updateController.InfoAsync(context);
            return true;
        }

        if (path.StartsWith("/api/admin/options/", StringComparison.Ordinal))
        {
            if (!user.IsAdmin)
            {
                await WriteJsonAsync(context, 403, new Dictionary<string, object?>
                {
                    ["message"] = "Forbidden",
                });
                return true;
            }

            string group = path["/api/admin/options/".Length..];
            await OptionsGroupAsync(context, group, option);
            return true;
        }

        if (path == "/api/admin/communications"
            || path.StartsWith("/api/admin/communications/", StringComparison.Ordinal))
        {
            if (!user.IsAdmin)
            {
                await WriteJsonAsync(context, 403, new Dictionary<string, object?>
                {
                    ["message"] = "Forbidden",
                });
                return true;
            }

            if (path == "/api/admin/communications")
            {
                await WriteCommunicationsAsync(context, communicationRepository, notifications);
                return true;
            }

            string idRaw = path["/api/admin/communications/".Length..];
            if (!long.TryParse(idRaw, out long communicationId))
            {
                await WriteNotFoundAsync(context);
                return true;
            }

            await WriteCommunicationEditorAsync(context, communicationRepository, communicationId);
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

    private static readonly Dictionary<string, string[]> OptionGroups =
        new(StringComparer.Ordinal)
        {
            ["options"] =
            [
                "site_name", "site_description", "site_url", "register_with_player_name",
                "require_verification", "regs_per_ip", "max_upload_file_size", "max_texture_width",
                "player_name_rule", "custom_player_name_regexp", "player_name_length_min",
                "player_name_length_max", "auto_del_invalid_texture", "allow_downloading_texture",
                "status_code_for_private", "texture_name_regexp", "content_policy",
                "meta_keywords", "meta_description", "meta_extras", "recaptcha_sitekey",
                "recaptcha_secretkey", "recaptcha_invisible", "update_source",
                "plugin_market_source",
            ],
            ["customize"] =
            [
                "home_pic_url", "favicon_url", "transparent_navbar", "hide_intro", "fixed_bg",
                "copyright_prefer", "copyright_text", "custom_css", "custom_js",
                "navbar_color", "sidebar_color",
            ],
            ["score"] =
            [
                "score_per_storage", "private_score_per_storage", "score_per_closet_item",
                "return_score", "score_per_player", "user_initial_score",
                "reporter_score_modification", "reporter_reward_score",
                "sign_gap_time", "sign_after_zero", "score_award_per_texture",
                "take_back_scores_after_deletion", "score_award_per_like",
            ],
            ["resource"] =
            [
                "force_ssl", "auto_detect_asset_url", "cdn_address", "cache_expire_time",
                "enable_avatar_cache", "enable_preview_cache", "allow_downloading_texture",
            ],
        };

    private static readonly HashSet<string> BoolOptions = new(StringComparer.Ordinal)
    {
        "transparent_navbar", "hide_intro", "fixed_bg", "register_with_player_name",
        "require_verification", "auto_del_invalid_texture", "allow_downloading_texture",
        "recaptcha_invisible", "return_score", "sign_after_zero",
        "take_back_scores_after_deletion", "enable_avatar_cache", "enable_preview_cache",
        "force_ssl", "auto_detect_asset_url",
    };

    private static async Task OptionsGroupAsync(HttpContext context, string group, Option option)
    {
        if (!OptionGroups.TryGetValue(group, out string[]? keys))
        {
            await WriteNotFoundAsync(context);
            return;
        }

        string locale = context.Items["locale"] as string ?? "zh_CN";
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            if (!HttpMethods.IsPut(context.Request.Method))
            {
                context.Response.StatusCode = 405;
                return;
            }

            Dictionary<string, object?> input = await RequestInput.BagAsync(context);
            if (group == "options")
            {
                foreach (string key in new[] { "site_name", "site_description", "content_policy" })
                {
                    if (input.TryGetValue(key, out object? raw))
                    {
                        string value = PhpString.Cast(raw);
                        if (value.Length > 0)
                        {
                            option.Set(key + "_" + locale, value);
                        }
                    }
                }
            }

            if (group == "score"
                && (input.ContainsKey("sign_score_from") || input.ContainsKey("sign_score_to")))
            {
                option.Set("sign_score", InputString(input, "sign_score_from")
                    + "," + InputString(input, "sign_score_to"));
            }

            foreach (string key in keys)
            {
                if (group == "options"
                    && key is "site_name" or "site_description" or "content_policy")
                {
                    continue;
                }

                if (BoolOptions.Contains(key))
                {
                    option.Set(key, Truthy(input.TryGetValue(key, out object? raw) ? raw : null)
                        ? "true"
                        : "false");
                    continue;
                }

                if (!input.ContainsKey(key))
                {
                    continue;
                }

                string value = InputString(input, key).Trim();
                if (key == "site_url")
                {
                    value = value.TrimEnd('/');
                    if (value.EndsWith("/index.php", StringComparison.Ordinal))
                    {
                        value = value[..^10];
                    }
                }
                else if (key == "cdn_address")
                {
                    value = value.TrimEnd('/');
                }

                option.Set(key, value);
            }

            await WriteJsonAsync(context, 200, new Dictionary<string, object?>
            {
                ["code"] = 0,
                ["message"] = "保存成功",
            });
            return;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            bool localized = group == "options"
                && key is "site_name" or "site_description" or "content_policy";
            values[key] = localized
                ? option.GetLocalized(key, "")?.ToString() ?? ""
                : option.Get(key)?.ToString() ?? "";
        }

        if (group == "score")
        {
            string[] parts = (option.Get("sign_score")?.ToString() ?? "").Split(',');
            values["sign_score_from"] = parts.Length > 0 ? parts[0].Trim() : "";
            values["sign_score_to"] = parts.Length > 1 ? parts[1].Trim() : "";
        }

        await WriteJsonAsync(context, 200, values);
    }

    private static string InputString(Dictionary<string, object?> input, string key) =>
        input.TryGetValue(key, out object? value) && value is not null
            ? PhpString.Cast(value)
            : "";

    private static bool Truthy(object? value)
    {
        if (value is bool flag)
        {
            return flag;
        }

        string text = value is null ? "" : PhpString.Cast(value).Trim().ToLowerInvariant();
        return text is "1" or "true" or "on" or "yes";
    }

    private static async Task WriteCommunicationsAsync(HttpContext context,
        CommunicationRepository communications, NotificationRepository notifications)
    {
        int page = ParsePositive(context.Request.Query["page"], 1);
        const int perPage = 15;
        long total = communications.CountMessages();
        int lastPage = LastPage(total, perPage);

        var items = new List<Dictionary<string, object?>>();
        foreach (CommunicationRepository.Row row in
                 communications.MessagePage(perPage, (page - 1) * perPage))
        {
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["title"] = row.Title,
                ["kind"] = row.Kind,
                ["status"] = row.Status,
                ["read_count"] = notifications.CountRead(row.Id),
                ["delivery_count"] = notifications.CountDelivery(row.Id),
                ["updated_at"] = row.UpdatedAt,
            });
        }

        await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["data"] = items,
            ["current_page"] = page,
            ["last_page"] = lastPage,
            ["per_page"] = perPage,
            ["total"] = total,
        });
    }

    private static async Task WriteCommunicationEditorAsync(HttpContext context,
        CommunicationRepository communications, long id)
    {
        CommunicationRepository.Row? row = communications.Find(id);
        if (row is null)
        {
            await WriteNotFoundAsync(context);
            return;
        }

        object? blocks = new List<object?>();
        if (!string.IsNullOrWhiteSpace(row.Blocks))
        {
            try
            {
                blocks = PhpJson.Decode(row.Blocks) ?? new List<object?>();
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                blocks = new List<object?>();
            }
        }

        var audience = new PhpArray().With("receiver", "all");
        if (!string.IsNullOrWhiteSpace(row.Audience))
        {
            try
            {
                if (PhpJson.Decode(row.Audience) is PhpArray parsed)
                {
                    audience = parsed;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                // keep default
            }
        }

        await WriteJsonAsync(context, 200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["title"] = row.Title,
            ["kind"] = row.Kind,
            ["status"] = row.Status,
            ["locale"] = row.Locale,
            ["editor"] = string.IsNullOrEmpty(row.Editor) ? "blocks" : row.Editor,
            ["source"] = row.Source ?? "",
            ["blocks"] = blocks,
            ["audience"] = audience,
        });
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
