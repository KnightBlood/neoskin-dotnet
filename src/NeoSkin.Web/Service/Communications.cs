using System.Security.Cryptography;
using NeoSkin.Web.Db;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// <c>App\Services\Communications</c> 的移植（发布 / 投递 / 撤回 / 读取）。
///
/// <p>正文渲染只处理 <c>editor=source</c>（原样 HTML/Markdown）与常见的
/// <c>blocks</c> 块（text / heading / alert / collapse / divider）；
/// Markdown 已按 GFM 子集渲染（<c>Support/MarkdownLite</c>），输出经\n/// <c>Support/HtmlSanitizer</c> 白名单消毒。</p>
/// </summary>
public sealed class Communications(
    CommunicationRepository communications,
    NotificationRepository notifications,
    UserRepository users,
    Config.NeoSkinSettings settings)
{
    // ================================================================== 保存 / 投递

    /// <summary><c>save($data, $message, $author)</c>。</summary>
    public CommunicationRepository.Row Save(PhpArray data,
        CommunicationRepository.Row? existing, int author)
    {
        string kind = Str(data.Get("kind"));
        if (kind == CommunicationRepository.KindAnnouncement)
        {
            return SaveAnnouncement(data, author, null);
        }

        string now = PhpDate.Now(settings.Timezone);
        bool wasPublished = existing?.Status == CommunicationRepository.StatusPublished;
        bool wasInbox = wasPublished
            && existing!.Kind is CommunicationRepository.KindMessage or CommunicationRepository.KindBoth;

        CommunicationRepository.Row target = existing ?? new CommunicationRepository.Row(
            0, author, "", CommunicationRepository.KindMessage,
            CommunicationRepository.StatusDraft, "", "source", null, null, null, null, null,
            now, now);

        bool publish = Str(data.Get("operation")) == "publish";
        target = target with
        {
            Title = Str(data.Get("title")),
            Kind = kind,
            Editor = Str(data.Get("editor")),
            Source = Nullable(data.Get("source")),
            Blocks = data.Get("blocks") is null ? null : PhpJson.Encode(data.Get("blocks"), 0),
            Locale = Str(data.Get("locale")),
            Audience = BuildAudienceJson(data),
            AuthorId = author,
            Status = publish
                ? CommunicationRepository.StatusPublished
                : CommunicationRepository.StatusDraft,
            UpdatedAt = now,
        };

        if (publish && !wasPublished)
        {
            target = target with { PublishedAt = now };
        }

        bool hasInbox = kind is CommunicationRepository.KindMessage or CommunicationRepository.KindBoth;
        bool startDelivery = publish && hasInbox && !wasInbox;
        if (startDelivery)
        {
            target = target with { DeliveryToken = RandomToken() };
        }

        if (existing is null)
        {
            long id = communications.Insert(target);
            target = target with { Id = id };
        }
        else
        {
            communications.Update(target);
        }

        if (publish && hasInbox && startDelivery)
        {
            Deliver(target);
        }

        if (kind == CommunicationRepository.KindBoth
            && publish)
        {
            SaveAnnouncement(data, author, target.Id);
        }

        return target;
    }

    /// <summary><c>withdraw($message)</c>。</summary>
    public void Withdraw(CommunicationRepository.Row message)
    {
        communications.Update(message with
        {
            Status = CommunicationRepository.StatusWithdrawn,
            UpdatedAt = PhpDate.Now(settings.Timezone),
        });

        if (message.Kind == CommunicationRepository.KindBoth)
        {
            communications.WithdrawAnnouncementsOfOrigin(message.Id);
        }
    }

    /// <summary><c>deliver($message)</c>：按 audience 选收件人并逐条写通知。</summary>
    private void Deliver(CommunicationRepository.Row message)
    {
        PhpArray? audience = ParseArray(message.Audience);
        string receiver = audience?.Get("receiver") is { } raw ? PhpString.Cast(raw) : "all";
        int? uid = audience?.Get("uid") is { } rawUid && int.TryParse(PhpString.Cast(rawUid),
            out int parsedUid) ? parsedUid : null;
        string? email = audience?.Get("email") is { } rawEmail ? PhpString.Cast(rawEmail) : null;

        var payload = new PhpArray()
            .With("title", message.Title)
            .With("content", Render(message))
            .With("format", "html")
            .With("communication_id", message.Id)
            .With("delivery_token", message.DeliveryToken);
        string dataJson = PhpJson.Encode(payload, PhpJson.JsonUnescapedUnicode);
        string now = PhpDate.Now(settings.Timezone);

        foreach (int notifiable in users.IdsForAudience(receiver, uid, email))
        {
            notifications.Insert(Guid.NewGuid().ToString(), notifiable, dataJson, now);
        }
    }

    /// <summary><c>saveAnnouncement($data, $author, $origin)</c>：同语言最新一条覆盖写。</summary>
    private CommunicationRepository.Row SaveAnnouncement(PhpArray data, int author, long? origin)
    {
        string locale = data.Get("locale") is { } raw ? PhpString.Cast(raw) : "";
        string now = PhpDate.Now(settings.Timezone);
        CommunicationRepository.Row announcement =
            communications.FindLatestAnnouncement(locale) ?? new CommunicationRepository.Row(
                0, author, "", CommunicationRepository.KindAnnouncement,
                CommunicationRepository.StatusDraft, locale, "source", null, null, null, null, null,
                now, now);

        bool publish = Str(data.Get("operation")) == "publish";
        announcement = announcement with
        {
            Title = Str(data.Get("title")),
            Editor = Str(data.Get("editor")),
            Source = Nullable(data.Get("source")),
            Blocks = data.Get("blocks") is null ? null : PhpJson.Encode(data.Get("blocks"), 0),
            Kind = CommunicationRepository.KindAnnouncement,
            Locale = locale,
            AuthorId = author,
            Audience = origin is null
                ? null
                : PhpJson.Encode(new PhpArray().With("communication_id", origin.Value), 0),
            Status = publish
                ? CommunicationRepository.StatusPublished
                : CommunicationRepository.StatusDraft,
            PublishedAt = publish ? now : announcement.PublishedAt,
            UpdatedAt = now,
        };

        if (announcement.Id == 0)
        {
            long id = communications.Insert(announcement);
            announcement = announcement with { Id = id };
        }
        else
        {
            communications.Update(announcement);
        }

        return announcement;
    }

    // ================================================================== 读取

    /// <summary><c>visible($notification)</c>：有 communication_id 时要求通信仍可见。</summary>
    public bool Visible(NotificationRepository.Row notification)
    {
        long? id = CommunicationId(notification.Data);
        if (id is null or 0)
        {
            return true;
        }

        string? token = notification.Data.Get("delivery_token") is { } value
            ? PhpString.Cast(value)
            : null;
        return communications.ExistsVisible(id.Value, token);
    }

    /// <summary><c>title($notification)</c>：优先取通信的当前标题，其次 <c>data.title</c>。</summary>
    public string Title(NotificationRepository.Row notification)
    {
        long? id = CommunicationId(notification.Data);
        if (id is > 0)
        {
            return communications.FindTitle(id.Value) ?? "";
        }

        return notification.Data.Get("title") is { } title ? PhpString.Cast(title) : "站内信";
    }

    /// <summary><c>content($notification)</c>。</summary>
    public string Content(NotificationRepository.Row notification)
    {
        long? id = CommunicationId(notification.Data);
        if (id is > 0)
        {
            CommunicationRepository.Row? message = communications.Find(id.Value);
            return message is null ? "" : Render(message);
        }

        return notification.Data.Get("content") is { } content ? PhpString.Cast(content) : "";
    }

    /// <summary><c>announcements($locale)</c>：当前公告已发布则渲染，否则空串。</summary>
    public string Announcements(string locale)
    {
        CommunicationRepository.Row? announcement =
            communications.FindCurrentAnnouncement(locale ?? "") ??
            communications.FindCurrentAnnouncement("");
        return announcement is not null
               && announcement.Status == CommunicationRepository.StatusPublished
            ? Render(announcement, "announcement-" + announcement.Id)
            : "";
    }

    /// <summary><c>renderer.render($data, $prefix)</c> 的移植（默认 <c>mail-&lt;id&gt;</c>）。</summary>
    public static string Render(CommunicationRepository.Row message, string? prefix = null)
    {
        var data = new PhpArray()
            .With("editor", message.Editor)
            .With("source", message.Source)
            .With("blocks", ParseArray(message.Blocks));
        return RenderData(data, prefix ?? "mail-" + message.Id);
    }

    /// <summary>渲染 <c>editor</c> + <c>source</c> / <c>blocks</c>，并消毒。</summary>
    public static string RenderData(PhpArray data, string? prefix = null)
    {
        string editor = data.Get("editor") is { } value ? PhpString.Cast(value) : "source";
        string html;
        if (editor != "blocks")
        {
            html = MarkdownLite.Render(data.Get("source") is { } source
                ? PhpString.Cast(source)
                : "");
        }
        else
        {
            var builder = new System.Text.StringBuilder();
            if (data.Get("blocks") is PhpArray blocks)
            {
                int index = 0;
                foreach (object? item in blocks.Values)
                {
                    if (item is not PhpArray block)
                    {
                        continue;
                    }

                    string type = block.Get("type") is { } t ? PhpString.Cast(t) : "text";
                    string content = MarkdownLite.Render(block.Get("content") is { } c
                        ? PhpString.Cast(c)
                        : "");
                    string title = block.Get("title") is { } ti ? PhpString.Cast(ti) : "";
                    string tone = NormalizeTone(block.Get("tone") is { } to
                        ? PhpString.Cast(to)
                        : "");

                    switch (type)
                    {
                        case "heading":
                            builder.Append("<h3>")
                                .Append(System.Net.WebUtility.HtmlEncode(title))
                                .Append("</h3>");
                            break;
                        case "alert":
                            builder.Append("<div class=\"alert alert-").Append(tone)
                                .Append("\">").Append(content).Append("</div>");
                            break;
                        case "collapse":
                            bool expanded = block.Get("expanded") is { } e
                                            && SiteViewDataTruthy(e);
                            builder.Append("<div class=\"card card-outline card-")
                                .Append(tone)
                                .Append("\"><div class=\"card-header\"><button type=\"button\"")
                                .Append(" class=\"btn btn-link text-left p-0\"")
                                .Append(" data-toggle=\"collapse\" data-target=\"#block-")
                                .Append(index).Append("\" aria-expanded=\"")
                                .Append(expanded ? "true" : "false").Append("\">")
                                .Append(System.Net.WebUtility.HtmlEncode(title))
                                .Append("</button></div><div id=\"block-").Append(index)
                                .Append("\" class=\"card-body collapse")
                                .Append(expanded ? " show" : "").Append("\">")
                                .Append(content).Append("</div></div>");
                            break;
                        case "divider":
                            builder.Append("<hr>");
                            break;
                        default:
                            builder.Append(content);
                            break;
                    }

                    index++;
                }
            }

            html = builder.ToString();
        }

        return HtmlSanitizer.Sanitize(html,
            prefix ?? "notice-" + RandomPrefix());
    }

    private static string NormalizeTone(string tone) => tone is "info" or "success"
        or "warning" or "danger" or "primary" or "secondary"
        ? tone
        : "info";

    private static bool SiteViewDataTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0 && s != "0",
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToInt64(value) != 0,
        _ => true,
    };

    private static string RandomPrefix()
    {
        Span<byte> bytes = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ================================================================== 工具

    private static string? BuildAudienceJson(PhpArray data)
    {
        var audience = new PhpArray();
        foreach (string key in new[] { "receiver", "uid", "email" })
        {
            object? value = data.Get(key);
            if (value is not null && PhpString.Cast(value).Length > 0)
            {
                audience.With(key, value);
            }
        }

        return audience.Count == 0 ? null : PhpJson.Encode(audience, 0);
    }

    private static string Str(object? value) => value is null ? "" : PhpString.Cast(value);

    private static string? Nullable(object? value) =>
        value is null || PhpString.Cast(value).Length == 0 ? null : PhpString.Cast(value);

    private static PhpArray? ParseArray(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return PhpJson.Decode(json) as PhpArray;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static PhpArray? ParseArray(object? value) => value switch
    {
        PhpArray array => array,
        string json => ParseArray(json),
        _ => null,
    };

    private static long? CommunicationId(PhpArray data) => data.Get("communication_id") switch
    {
        null => null,
        long l => l,
        int i => i,
        string s when long.TryParse(s, out long parsed) => parsed,
        object other => long.TryParse(PhpString.Cast(other), out long parsed) ? parsed : null,
    };

    private static string RandomToken() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
