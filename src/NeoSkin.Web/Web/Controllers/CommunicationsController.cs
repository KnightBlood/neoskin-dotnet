using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Middleware;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;
using NeoSkin.Web.Validation;
using NeoSkin.Web.View;
using NeoSkin.Web.Web;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Controllers;

/// <summary>
/// <c>App\Http\Controllers\CommunicationsController</c> 的写侧移植。
///
/// <p>已实现：<c>store</c> / <c>update</c> / <c>withdraw</c> / <c>preview</c> /
/// <c>importSource</c>。<c>index</c> / <c>inbox</c> / <c>message</c> 是 HTML 页面，
/// 等模板引擎。</p>
/// </summary>
public sealed class CommunicationsController(
    Option option,
    UserRepository users,
    CommunicationRepository communications,
    Communications service,
    Config.NeoSkinSettings settings)
{
    /// <summary><c>POST admin/communications</c>。</summary>
    public RequestHandler Store() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        PhpArray? data = await ValidateContentAsync(context);
        if (data is null)
        {
            return;
        }

        CommunicationRepository.Row saved = service.Save(data, null, user?.Uid ?? 0);
        string query = saved.Kind == CommunicationRepository.KindAnnouncement
            ? "?announcement=1"
            : "?edit=" + saved.Id;
        Redirect(context, "http://" + Host(context) + "/admin/communications" + query,
            "communication_notice",
            saved.Status == CommunicationRepository.StatusPublished ? "内容已发布。" : "草稿已保存。");
    };

    /// <summary><c>PUT admin/communications/{communication}</c>。</summary>
    public RequestHandler Update() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = AuthMiddleware.CurrentUser(context);
        long id = parameters.TryGetValue("communication", out string? raw)
            ? RequestInput.ToInt(raw)
            : 0;
        CommunicationRepository.Row? communication = communications.Find(id);
        if (communication is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        PhpArray? data = await ValidateContentAsync(context);
        if (data is null)
        {
            return;
        }

        // 已发布内容的受众保持不变（源：data['audience'] = communication.audience）
        if (communication.Status == CommunicationRepository.StatusPublished
            && communication.Audience is { } audienceJson)
        {
            PhpArray? audience = ParseAudience(audienceJson);
            if (audience is not null)
            {
                foreach (string key in new[] { "receiver", "uid", "email" })
                {
                    if (audience.Get(key) is { } value)
                    {
                        data.With(key, value);
                    }
                }
            }
        }

        CommunicationRepository.Row saved = service.Save(data, communication, user?.Uid ?? 0);
        string query = saved.Kind == CommunicationRepository.KindAnnouncement
            ? "?announcement=1"
            : "?edit=" + saved.Id;
        Redirect(context, "http://" + Host(context) + "/admin/communications" + query,
            "communication_notice", "内容已更新。");
    };

    /// <summary><c>POST admin/communications/{communication}/withdraw</c>。</summary>
    public RequestHandler Withdraw() => async (context, route, parameters) =>
    {
        long id = parameters.TryGetValue("communication", out string? raw)
            ? RequestInput.ToInt(raw)
            : 0;
        CommunicationRepository.Row? communication = communications.Find(id);
        if (communication is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        service.Withdraw(communication);
        Redirect(context, "http://" + Host(context) + "/admin/communications",
            "communication_notice", "内容已撤回。");
        await Task.CompletedTask;
    };

    /// <summary><c>POST admin/communications/preview</c>：返回 <c>{html}</c>。</summary>
    public RequestHandler Preview() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        if (RequestInput.String(input, "title").Length == 0)
        {
            input["title"] = "预览";
        }

        context.Items["neoskin.communication.input"] = input;
        PhpArray? data = await ValidateContentAsync(context, forcePreview: true);
        if (data is null)
        {
            return;
        }

        var body = new PhpArray().With("html", Communications.RenderData(data));
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    };

    /// <summary><c>POST admin/communications/import</c>：纯文本按空行切段 → text 块。</summary>
    public RequestHandler ImportSource() => async (context, route, parameters) =>
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);
        string text = RequestInput.String(input, "source");
        if (text.Length > 100000)
        {
            await ValidationFailedAsync(context, new Dictionary<string, List<string>>
            {
                ["source"] = ["source 不能超过 100000 个字符。"],
            });
            return;
        }

        var blocks = new PhpArray();
        int index = 0;
        foreach (string paragraph in text.Split("\n\n",
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            blocks.Put(index++, new PhpArray()
                .With("type", "text")
                .With("content", paragraph)
                .With("title", "")
                .With("tone", "")
                .With("expanded", false));
        }

        await JsonAsync(context, 200, new PhpArray().With("blocks", blocks));
    };

    // ================================================================== 校验

    private async Task<PhpArray?> ValidateContentAsync(HttpContext context, bool forcePreview = false)
    {
        Dictionary<string, object?> input = await RequestInput.BagAsync(context);

        // blocks_json → blocks（JSON 数组）
        PhpArray? blocks = null;
        string blocksJson = RequestInput.String(input, "blocks_json");
        if (blocksJson.Length > 0)
        {
            try
            {
                if (PhpJson.Decode(blocksJson) is PhpArray parsed)
                {
                    blocks = parsed;
                }
                else
                {
                    await ValidationFailedAsync(context, new Dictionary<string, List<string>>
                    {
                        ["blocks_json"] = ["blocks_json 不能为空。"],
                    });
                    return null;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                await ValidationFailedAsync(context, new Dictionary<string, List<string>>
                {
                    ["blocks_json"] = ["blocks_json 不能为空。"],
                });
                return null;
            }
        }

        if (forcePreview)
        {
            input["kind"] = "announcement";
            input["receiver"] = "all";
            input["operation"] = "draft";
        }

        var v = new Validator(input, Translator(context), Locale(context), option,
            new DbPresenceVerifier(users.Database));
        v.AddRule("title", "required");
        v.AddRule("title", "string");
        v.AddRule("title", "max:100");
        v.AddRule("kind", "required");
        v.AddRule("kind", "in:announcement,message,both");
        v.AddRule("editor", "required");
        v.AddRule("editor", "in:source,blocks");
        v.AddRule("source", "nullable");
        v.AddRule("source", "string");
        v.AddRule("source", "max:100000");
        v.AddRule("locale", "nullable");
        v.AddRule("locale", "string");
        v.AddRule("locale", "max:20");
        v.AddRule("receiver", "required");
        v.AddRule("receiver", "in:all,normal,uid,email");
        v.AddRule("uid", "required_if:receiver,uid");
        v.AddRule("uid", "nullable");
        v.AddRule("uid", "integer");
        v.AddRule("uid", "exists:users,uid");
        v.AddRule("email", "required_if:receiver,email");
        v.AddRule("email", "nullable");
        v.AddRule("email", "email");
        v.AddRule("email", "exists:users,email");
        v.AddRule("operation", "required");
        v.AddRule("operation", "in:draft,publish");

        if (!v.Passes())
        {
            await ValidationFailedAsync(context, v.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value));
            return null;
        }

        if (blocks is not null)
        {
            (bool ok, Dictionary<string, List<string>> errors) = ValidateBlocks(blocks);
            if (!ok)
            {
                await ValidationFailedAsync(context, errors);
                return null;
            }
        }

        var data = new PhpArray()
            .With("title", RequestInput.String(input, "title"))
            .With("kind", RequestInput.String(input, "kind"))
            .With("editor", RequestInput.String(input, "editor"))
            .With("source", RequestInput.String(input, "source"))
            .With("locale", RequestInput.String(input, "locale"))
            .With("receiver", RequestInput.String(input, "receiver"))
            .With("operation", RequestInput.String(input, "operation"));
        if (blocks is not null)
        {
            data.With("blocks", blocks);
        }

        if (input.ContainsKey("uid"))
        {
            data.With("uid", RequestInput.String(input, "uid"));
        }

        if (input.ContainsKey("email"))
        {
            data.With("email", RequestInput.String(input, "email"));
        }

        return data;
    }

    private static (bool Ok, Dictionary<string, List<string>> Errors) ValidateBlocks(PhpArray blocks)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (blocks.Count > 80)
        {
            errors["blocks"] = ["blocks 不能超过 80 项。"];
            return (false, errors);
        }

        int index = 0;
        foreach (object? item in blocks.Values)
        {
            if (item is not PhpArray block)
            {
                errors["blocks." + index] = ["The blocks." + index + " must be an array."];
                index++;
                continue;
            }

            string type = block.Get("type") is { } t ? PhpString.Cast(t) : "";
            if (type is not ("text" or "heading" or "alert" or "collapse" or "divider"))
            {
                errors["blocks." + index + ".type"] =
                    ["The selected blocks." + index + ".type is invalid."];
            }

            string content = block.Get("content") is { } c ? PhpString.Cast(c) : "";
            if (content.Length > 10000)
            {
                errors["blocks." + index + ".content"] =
                    ["The blocks." + index + ".content may not be greater than 10000 characters."];
            }

            string title = block.Get("title") is { } ti ? PhpString.Cast(ti) : "";
            if (title.Length > 200)
            {
                errors["blocks." + index + ".title"] =
                    ["The blocks." + index + ".title may not be greater than 200 characters."];
            }

            string tone = block.Get("tone") is { } to ? PhpString.Cast(to) : "";
            if (tone.Length > 0 && tone is not ("info" or "success" or "warning" or "danger"
                    or "primary" or "secondary"))
            {
                errors["blocks." + index + ".tone"] =
                    ["The selected blocks." + index + ".tone is invalid."];
            }

            index++;
        }

        return (errors.Count == 0, errors);
    }

    // ================================================================== 页面

    /// <summary><c>GET admin/communications</c>：通知与公告管理（最小表单 + 列表）。</summary>
    public RequestHandler Index() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = Middleware.AuthMiddleware.CurrentUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string locale = Locale(context);
        string? editRaw = context.Request.Query["edit"];
        bool announcement = context.Request.Query.ContainsKey("announcement")
                            && context.Request.Query["announcement"].ToString() is not ("0" or "");
        CommunicationRepository.Row? editing = null;
        PhpArray? announcementSeed = null;

        if (!string.IsNullOrEmpty(editRaw) && editRaw != "0")
        {
            editing = communications.Find(long.TryParse(editRaw, out long editId) ? editId : 0);
            if (editing is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
        }

        if (announcement)
        {
            // 源 currentAnnouncement()：先当前语言，再回退到「无语言」；both 需已发布。
            editing = communications.FindCurrentAnnouncement(locale) ??
                      communications.FindCurrentAnnouncement("");
            if (editing is not null && editing.Kind == CommunicationRepository.KindBoth)
            {
                announcementSeed = ToEditorData(editing);
                announcementSeed.Put("kind", CommunicationRepository.KindAnnouncement);
                editing = null;
            }
        }

        long total = communications.CountMessages();
        int page = Math.Max(1, int.TryParse(context.Request.Query["page"], out int parsedPage)
            ? parsedPage
            : 1);
        const int perPage = 15;
        List<CommunicationRepository.Row> rows =
            communications.MessagePage(perPage, (page - 1) * perPage);

        var notifications = new NotificationRepository(users.Database);
        string host = Host(context);
        string baseUrl = "http://" + host + "/admin/communications";
        var history = new List<object?>();
        foreach (CommunicationRepository.Row row in rows)
        {
            history.Add(new PhpArray()
                .With("id", row.Id)
                .With("title", row.Title)
                .With("kind", row.Kind)
                .With("status", row.Status)
                .With("read_count", notifications.CountRead(row.Id))
                .With("delivery_count", notifications.CountDelivery(row.Id))
                .With("updated_at", row.UpdatedAt)
                .With("edit_url", baseUrl + "?edit=" + row.Id)
                .With("withdraw_url", baseUrl + "/" + row.Id + "/withdraw"));
        }

        PhpArray initial;
        if (editing is not null)
        {
            initial = ToEditorData(editing);
        }
        else if (announcementSeed is not null)
        {
            initial = announcementSeed;
        }
        else
        {
            initial = new PhpArray()
                .With("title", announcement ? "站点公告" : "")
                .With("kind", announcement
                    ? CommunicationRepository.KindAnnouncement
                    : CommunicationRepository.KindMessage)
                .With("editor", "blocks")
                .With("blocks", new List<object?>())
                .With("source", "")
                .With("locale", "")
                .With("audience", new PhpArray().With("receiver", "all"));
        }

        if (initial.Get("audience") is not PhpArray audience)
        {
            audience = new PhpArray().With("receiver", "all");
            initial.Put("audience", audience);
        }

        if (audience.Get("receiver") is null)
        {
            audience.Put("receiver", "all");
        }

        Dictionary<string, object?> vars = PageVars(context, user, "通知与公告");
        vars["messages"] = history;
        vars["pagination"] = Pager(history, total, page, perPage, "/admin/communications");
        vars["index_url"] = baseUrl;
        vars["new_url"] = baseUrl + "?new=1";
        vars["announcement_url"] = baseUrl + "?announcement=1";
        vars["form_action"] = editing is not null
            ? baseUrl + "/" + editing.Id
            : baseUrl;
        vars["preview_url"] = baseUrl + "/preview";
        vars["import_url"] = baseUrl + "/import";
        vars["editing_status"] = editing?.Status ?? "";
        if (editing is not null)
        {
            vars["editing"] = new PhpArray()
                .With("id", editing.Id)
                .With("status", editing.Status)
                .With("kind", editing.Kind);
        }

        vars["editor_data"] = initial;
        vars["editor_data_json"] = PhpJson.Encode(initial,
            PhpJson.JsonHexTag | PhpJson.JsonHexAmp | PhpJson.JsonHexApos
            | PhpJson.JsonHexQuot);
        bool editorOpen = editing is not null || announcement
            || context.Request.Query.ContainsKey("new");
        vars["editor_open"] = editorOpen;

        await WriteHtml(context, TemplateRenderer.Render("admin/communications", vars));
    };

    private Dictionary<string, object?> PageVars(HttpContext context,
        UserRepository.UserRow user, string title)
    {
        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context),
            context.Request.Path.Value ?? "/admin/communications", settings.Debug, "admin",
            Session(context));
        vars["pageTitle"] = title;
        vars["errors"] = TemplateErrors.Empty;
        return vars;
    }

    private static PhpArray ToEditorData(CommunicationRepository.Row row)
    {
        object blocks = string.IsNullOrEmpty(row.Blocks)
            ? new List<object?>()
            : PhpJson.Decode(row.Blocks) ?? new List<object?>();

        PhpArray audience = string.IsNullOrEmpty(row.Audience)
            ? new PhpArray().With("receiver", "all")
            : PhpJson.Decode(row.Audience) as PhpArray
              ?? new PhpArray().With("receiver", "all");

        return new PhpArray()
            .With("title", row.Title)
            .With("kind", row.Kind)
            .With("editor", row.Editor)
            .With("source", row.Source ?? "")
            .With("blocks", blocks)
            .With("locale", row.Locale)
            .With("audience", audience);
    }

    private static PhpArray Pager(IReadOnlyList<object?> items, long total, int page,
        int perPage, string baseUrl)
    {
        int last = (int)Math.Max(1, (total + perPage - 1) / perPage);
        var pages = new List<object?>();
        for (int n = 1; n <= last; n++)
        {
            pages.Add(new PhpArray()
                .With("number", n)
                .With("url", baseUrl + "?page=" + n)
                .With("current", n == page));
        }

        return new PhpArray()
            .With("items", items)
            .With("total", total)
            .With("per_page", perPage)
            .With("current_page", page)
            .With("last_page", last)
            .With("previous_page_url", page > 1 ? baseUrl + "?page=" + (page - 1) : null)
            .With("next_page_url", page < last ? baseUrl + "?page=" + (page + 1) : null)
            .With("on_first_page", page <= 1)
            .With("has_more_pages", page < last)
            .With("pages", pages);
    }

    /// <summary><c>GET user/inbox</c>：站内信列表。</summary>
    public RequestHandler Inbox() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = Middleware.AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        int page = Math.Max(1, int.TryParse(context.Request.Query["page"], out int parsed)
            ? parsed
            : 1);
        var notifications = new NotificationRepository(users.Database);
        List<NotificationRepository.Row> rows =
            notifications.InboxPage(user.Uid, 20, (page - 1) * 20);

        var items = new List<object?>();
        foreach (NotificationRepository.Row row in rows)
        {
            items.Add(new PhpArray()
                .With("id", row.Id)
                .With("display_title", service.Title(row))
                .With("read_at", row.ReadAt)
                .With("created_at", row.CreatedAt)
                .With("message_url",
                    "http://" + Host(context) + "/user/inbox/" + row.Id));
        }

        long total = notifications.CountInbox(user.Uid);
        int last = (int)Math.Max(1, (total + 19) / 20);
        var pager = new PhpArray()
            .With("items", items)
            .With("total", total)
            .With("current_page", page)
            .With("last_page", last)
            .With("previous_page_url", page > 1
                ? "http://" + Host(context) + "/user/inbox?page=" + (page - 1)
                : null)
            .With("next_page_url", page < last
                ? "http://" + Host(context) + "/user/inbox?page=" + (page + 1)
                : null);

        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context), context.Request.Path.Value ?? "/user/inbox",
            settings.Debug, "user", Session(context));
        vars["pageTitle"] = "站内信";
        vars["errors"] = TemplateErrors.Empty;
        vars["messages"] = pager;
        string html = TemplateRenderer.Render("user/inbox", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };

    /// <summary><c>GET user/inbox/{id}</c>：单条站内信。</summary>
    public RequestHandler Message() => async (context, route, parameters) =>
    {
        UserRepository.UserRow? user = Middleware.AuthMiddleware.CurrentUser(context)
            ?? SessionUser(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        string id = parameters.TryGetValue("id", out string? raw) ? raw ?? "" : "";
        var notifications = new NotificationRepository(users.Database);
        NotificationRepository.Row? row = notifications.FindByNotifiableAndId(user.Uid, id);
        if (row is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (!service.Visible(row))
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (row.ReadAt is null)
        {
            notifications.MarkAsRead(id, PhpDate.Now(settings.Timezone));
        }

        Dictionary<string, object?> vars = PageView.Variables(option, Translator(context),
            user, Locale(context), Host(context),
            context.Request.Path.Value ?? "/user/inbox/" + id, settings.Debug, "user",
            Session(context));
        vars["pageTitle"] = service.Title(row);
        vars["errors"] = TemplateErrors.Empty;
        vars["message"] = row;
        vars["message_title"] = service.Title(row);
        vars["message_created_at"] = row.CreatedAt;
        vars["body_html"] = service.Content(row);
        vars["inbox_url"] = "http://" + Host(context) + "/user/inbox";
        string html = TemplateRenderer.Render("user/message", vars);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    };
    // ================================================================== 辅助

    static void Redirect(HttpContext context, string location, string flashKey, string message)
    {
        SessionStore? session = context.Items[StartSessionMiddleware.SessionKey] as SessionStore;
        session?.Flash(flashKey, message);
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        context.Response.Headers["Cache-Control"] = "no-cache, private";
    }

    private static PhpArray? ParseAudience(string json)
    {
        try
        {
            return PhpJson.Decode(json) as PhpArray;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static SessionStore? Session(HttpContext context) =>
        context.Items[StartSessionMiddleware.SessionKey] as SessionStore;

    private static async Task WriteHtml(HttpContext context, string html)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private UserRepository.UserRow? SessionUser(HttpContext context)
    {
        SessionStore? session = Session(context);
        return session is null ? null : new Auth.SessionGuard(session, users).User();
    }

    private static string Host(HttpContext context) =>
        context.Request.Host.Value is { Length: > 0 } host ? host : "localhost";

    private static Translator Translator(HttpContext context) =>
        context.Items["translator"] as Translator
        ?? new Translator(new TranslationFileLoader(), "en", "en");

    private static string Locale(HttpContext context) =>
        context.Items["locale"] as string ?? "zh_CN";

    private static async Task ValidationFailedAsync(HttpContext context,
        Dictionary<string, List<string>> errors)
    {
        await JsonAsync(context, 422, new Dictionary<string, object?>
        {
            ["message"] = Validator.Summarize(errors),
            ["errors"] = errors,
        });
    }

    private static async Task JsonAsync(HttpContext context, int status, object? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(PhpJson.Encode(body, 0));
    }
}
