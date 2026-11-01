using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>notifications</c> 表的读写（Laravel DatabaseNotification / <c>SiteMessage</c> 通道）。
///
/// <p>行契约：<c>id</c> 是带连字符的 UUIDv4；<c>type</c> =
/// <c>App\Notifications\SiteMessage</c>；<c>notifiable_type</c> = <c>App\Models\User</c>；
/// <c>data</c> 是 <c>SiteMessage::toArray()</c> 的 JSON。</p>
/// </summary>
public sealed class NotificationRepository(Database db)
{
    public const string SiteMessageType = "App\\Notifications\\SiteMessage";

    public const string NotifiableType = "App\\Models\\User";

    public sealed record Row(string Id, long NotifiableId, PhpArray Data, string? ReadAt,
        string? CreatedAt, string? UpdatedAt);

    private const string Columns =
        "\"id\", \"notifiable_type\", \"notifiable_id\", \"data\", \"read_at\","
        + " \"created_at\", \"updated_at\"";

    /// <summary>写一行通知（<c>Notification::sendNow</c> 的 database 通道）。</summary>
    public void Insert(string id, long notifiableId, string dataJson, string createdAt) =>
        db.Execute("INSERT INTO notifications (id, type, notifiable_type, notifiable_id, data,"
            + " read_at, created_at, updated_at)"
            + " VALUES (@id,@t,@nt,@nid,@d,NULL,@c,@u)",
            ("@id", id), ("@t", SiteMessageType), ("@nt", NotifiableType),
            ("@nid", notifiableId), ("@d", dataJson), ("@c", createdAt), ("@u", createdAt));

    public Row? FindByNotifiableAndId(long notifiableId, string id) =>
        db.QueryFirst("SELECT " + Columns + " FROM notifications n"
            + " WHERE n.\"notifiable_type\" = @t AND n.\"notifiable_id\" = @nid"
            + " AND n.\"id\" = @id",
            Map, ("@t", NotifiableType), ("@nid", notifiableId), ("@id", id));

    public void MarkAsRead(string id, string readAt) =>
        db.Execute("UPDATE notifications SET read_at = @r, updated_at = @r WHERE id = @id",
            ("@r", readAt), ("@id", id));

    /// <summary><c>inboxQuery($user)-&gt;whereNull('read_at')-&gt;limit($n)</c>。</summary>
    public List<Row> Unread(long notifiableId, int limit)
    {
        string commId = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "communication_id"));
        string token = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "delivery_token"));
        string commInt = db.Dialect.JsonInt("n.\"data\"", "communication_id");

        string sql = "SELECT " + Columns + " FROM notifications n"
            + " WHERE n.\"notifiable_type\" = @t AND n.\"notifiable_id\" = @nid"
            + " AND n.\"read_at\" IS NULL"
            + " AND (" + commId + " IS NULL OR EXISTS (SELECT 1 FROM communications c"
            + " WHERE c.\"id\" = " + commInt
            + " AND c.\"status\" = @published AND c.\"kind\" IN (@message, @both)"
            + " AND (c.\"delivery_token\" = " + token
            + " OR (c.\"delivery_token\" IS NULL AND " + token + " IS NULL))))"
            + " ORDER BY n.\"created_at\" DESC LIMIT " + limit;

        return db.Query(sql, Map, ("@t", NotifiableType), ("@nid", notifiableId),
            ("@published", CommunicationRepository.StatusPublished),
            ("@message", CommunicationRepository.KindMessage),
            ("@both", CommunicationRepository.KindBoth));
    }

    /// <summary><c>where(data-&gt;communication_id, $id)-&gt;count()</c>。</summary>
    public long CountDelivery(long communicationId)
    {
        string key = db.Dialect.CastText(db.Dialect.JsonText("\"data\"", "communication_id"));
        return db.Long("SELECT COUNT(*) FROM notifications WHERE " + key + " = @id",
            ("@id", communicationId.ToString()));
    }

    /// <summary><c>...-&gt;whereNotNull('read_at')-&gt;count()</c>。</summary>
    public long CountRead(long communicationId)
    {
        string key = db.Dialect.CastText(db.Dialect.JsonText("\"data\"", "communication_id"));
        return db.Long("SELECT COUNT(*) FROM notifications WHERE " + key + " = @id"
                       + " AND \"read_at\" IS NOT NULL", ("@id", communicationId.ToString()));
    }

    public long CountInbox(long notifiableId)
    {
        string commId = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "communication_id"));
        string token = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "delivery_token"));
        string commInt = db.Dialect.JsonInt("n.\"data\"", "communication_id");

        string sql = "SELECT COUNT(*) FROM notifications n"
            + " WHERE n.\"notifiable_type\" = @t AND n.\"notifiable_id\" = @nid"
            + " AND (" + commId + " IS NULL OR EXISTS (SELECT 1 FROM communications c"
            + " WHERE c.\"id\" = " + commInt
            + " AND c.\"status\" = @published AND c.\"kind\" IN (@message, @both)"
            + " AND (c.\"delivery_token\" = " + token
            + " OR (c.\"delivery_token\" IS NULL AND " + token + " IS NULL))))";

        return db.Long(sql, ("@t", NotifiableType), ("@nid", notifiableId),
            ("@published", CommunicationRepository.StatusPublished),
            ("@message", CommunicationRepository.KindMessage),
            ("@both", CommunicationRepository.KindBoth));
    }

    /// <summary><c>inboxQuery($user)-&gt;latest()-&gt;paginate(20)</c>（全部，不只看未读）。</summary>
    public List<Row> InboxPage(long notifiableId, int limit, int offset)
    {
        string commId = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "communication_id"));
        string token = db.Dialect.CastText(db.Dialect.JsonText("n.\"data\"", "delivery_token"));
        string commInt = db.Dialect.JsonInt("n.\"data\"", "communication_id");

        string sql = "SELECT " + Columns + " FROM notifications n"
            + " WHERE n.\"notifiable_type\" = @t AND n.\"notifiable_id\" = @nid"
            + " AND (" + commId + " IS NULL OR EXISTS (SELECT 1 FROM communications c"
            + " WHERE c.\"id\" = " + commInt
            + " AND c.\"status\" = @published AND c.\"kind\" IN (@message, @both)"
            + " AND (c.\"delivery_token\" = " + token
            + " OR (c.\"delivery_token\" IS NULL AND " + token + " IS NULL))))"
            + " ORDER BY n.\"created_at\" DESC LIMIT " + limit + " OFFSET " + offset;

        return db.Query(sql, Map, ("@t", NotifiableType), ("@nid", notifiableId),
            ("@published", CommunicationRepository.StatusPublished),
            ("@message", CommunicationRepository.KindMessage),
            ("@both", CommunicationRepository.KindBoth));
    }

    private static Row Map(DbDataReader reader)
    {
        var data = new PhpArray();
        int ordinal = reader.GetOrdinal("data");
        if (!reader.IsDBNull(ordinal))
        {
            try
            {
                if (PhpJson.Decode(DbRead.Text(reader, ordinal)) is PhpArray parsed)
                {
                    data = parsed;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                // 非法 JSON 按空对象
            }
        }

        return new Row(
            DbRead.Text(reader, reader.GetOrdinal("id")),
            DbRead.Long(reader, reader.GetOrdinal("notifiable_id")),
            data,
            Str(reader, "read_at"),
            Str(reader, "created_at"),
            Str(reader, "updated_at"));
    }

    private static string? Str(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return DbRead.TextOrNull(reader, ordinal);
    }
}
