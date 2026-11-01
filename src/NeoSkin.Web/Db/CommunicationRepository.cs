using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>communications</c> 表的访问（公告 / 站内信）。
/// </summary>
public sealed class CommunicationRepository(Database db)
{
    public const string KindAnnouncement = "announcement";
    public const string KindMessage = "message";
    public const string KindBoth = "both";

    public const string StatusDraft = "draft";
    public const string StatusPublished = "published";

    public sealed record Row(long Id, int? AuthorId, string Title, string Kind, string Status,
        string Locale, string Editor, string? Source, string? Blocks, string? Audience,
        string? PublishedAt, string? DeliveryToken, string? CreatedAt, string? UpdatedAt);

    public long Insert(Row row)
    {
        return db.InsertReturningId("communications", "id",
            "INSERT INTO communications (author_id, legacy_key, delivery_token, title, kind,"
            + " status, locale, editor, source, blocks, audience, published_at, created_at,"
            + " updated_at) VALUES (@a,NULL,@dt,@t,@k,@s,@l,@e,@src,@b,@au,@p,@c,@u)",
            ("@a", row.AuthorId), ("@dt", row.DeliveryToken), ("@t", row.Title),
            ("@k", row.Kind), ("@s", row.Status), ("@l", row.Locale), ("@e", row.Editor),
            ("@src", row.Source), ("@b", row.Blocks), ("@au", row.Audience),
            ("@p", row.PublishedAt), ("@c", row.CreatedAt), ("@u", row.UpdatedAt));
    }

    public Row? Find(long id) =>
        db.QueryFirst("SELECT * FROM communications WHERE id = @id", Map, ("@id", id));

    /// <summary><c>save()</c>：已存在的行更新全部可变列。</summary>
    public void Update(Row row) =>
        db.Execute("UPDATE communications SET author_id = @a, delivery_token = @dt,"
            + " title = @t, kind = @k, status = @s, locale = @l, editor = @e, source = @src,"
            + " blocks = @b, audience = @au, published_at = @p, updated_at = @u WHERE id = @id",
            ("@a", row.AuthorId), ("@dt", row.DeliveryToken), ("@t", row.Title),
            ("@k", row.Kind), ("@s", row.Status), ("@l", row.Locale), ("@e", row.Editor),
            ("@src", row.Source), ("@b", row.Blocks), ("@au", row.Audience),
            ("@p", row.PublishedAt), ("@u", row.UpdatedAt), ("@id", row.Id));

    /// <summary><c>where('kind','announcement')-&gt;where('locale',$locale)-&gt;latest('id')-&gt;first()</c>。</summary>
    public Row? FindLatestAnnouncement(string locale) =>
        db.QueryFirst("SELECT * FROM communications WHERE kind = @k AND locale = @l"
            + " ORDER BY id DESC LIMIT 1", Map, ("@k", KindAnnouncement), ("@l", locale));

    /// <summary>
    /// <c>Communications::currentAnnouncement()</c>：同语言里取公告，或已发布的
    /// 「公告与站内信」（<c>kind = both</c>）；排序 <c>updated_at desc, kind, id desc</c>。
    /// </summary>
    public Row? FindCurrentAnnouncement(string locale) =>
        db.QueryFirst("SELECT * FROM communications WHERE locale = @l"
            + " AND (kind = @a OR (kind = @b AND status = @s))"
            + " ORDER BY updated_at DESC, kind ASC, id DESC LIMIT 1", Map,
            ("@l", locale), ("@a", KindAnnouncement), ("@b", KindBoth),
            ("@s", StatusPublished));

    /// <summary><c>withdrawAnnouncementsOfOrigin($id)</c>。</summary>
    public void WithdrawAnnouncementsOfOrigin(long originId)
    {
        List<Row> rows = db.Query("SELECT * FROM communications WHERE kind = @k",
            Map, ("@k", KindAnnouncement));
        foreach (Row row in rows)
        {
            if (row.Audience is null)
            {
                continue;
            }

            PhpArray? audience;
            try
            {
                audience = PhpJson.Decode(row.Audience) as PhpArray;
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                continue;
            }

            if (audience?.Get("communication_id") is { } raw
                && long.TryParse(PhpString.Cast(raw), out long id) && id == originId)
            {
                Update(row with { Status = StatusWithdrawn, UpdatedAt = Now() });
            }
        }
    }

    public const string StatusWithdrawn = "withdrawn";

    /// <summary>最近的通信（管理端列表用）。</summary>
    public List<Row> ListRecent(int limit) =>
        db.Query("SELECT * FROM communications ORDER BY id DESC LIMIT " + limit, Map);

    /// <summary><c>countMessages()</c>：<c>kind IN (message, both)</c> 的总数。</summary>
    public long CountMessages() =>
        db.Long("SELECT COUNT(*) FROM communications WHERE kind IN (@m,@b)",
            ("@m", KindMessage), ("@b", KindBoth));

    /// <summary><c>messagePage($limit, $offset)</c>：消息/双类型分页（id 倒序）。</summary>
    public List<Row> MessagePage(int limit, int offset)
    {
        string paging = db.Dialect.Name switch
        {
            "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
            _ => "LIMIT " + limit + " OFFSET " + offset,
        };
        return db.Query("SELECT * FROM communications WHERE kind IN (@m,@b)"
                        + " ORDER BY id DESC " + paging, Map,
            ("@m", KindMessage), ("@b", KindBoth));
    }

    private string Now() => PhpDate.Now("Asia/Shanghai");

    public string? FindTitle(long id) =>
        db.Scalar("SELECT title FROM communications WHERE id = @id", ("@id", id))?.ToString();

    /// <summary>
    /// <c>existsVisible</c>：已发布且 kind ∈ (message,both)，并且 delivery_token 匹配
    /// （双方都为 null 也算匹配）。
    /// </summary>
    public bool ExistsVisible(long id, string? token)
    {
        long count = token is null
            ? db.Long("SELECT COUNT(*) FROM communications WHERE id = @id"
                + " AND status = @s AND kind IN (@m,@b) AND delivery_token IS NULL",
                ("@id", id), ("@s", StatusPublished), ("@m", KindMessage), ("@b", KindBoth))
            : db.Long("SELECT COUNT(*) FROM communications WHERE id = @id"
                + " AND status = @s AND kind IN (@m,@b) AND delivery_token = @t",
                ("@id", id), ("@s", StatusPublished), ("@m", KindMessage), ("@b", KindBoth),
                ("@t", token));
        return count > 0;
    }

    private static Row Map(DbDataReader reader) => new(
        DbRead.Long(reader, reader.GetOrdinal("id")),
        reader.IsDBNull(reader.GetOrdinal("author_id"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("author_id")),
        DbRead.Text(reader, reader.GetOrdinal("title")),
        DbRead.Text(reader, reader.GetOrdinal("kind")),
        DbRead.Text(reader, reader.GetOrdinal("status")),
        DbRead.Text(reader, reader.GetOrdinal("locale")),
        DbRead.Text(reader, reader.GetOrdinal("editor")),
        Str(reader, "source"),
        Str(reader, "blocks"),
        Str(reader, "audience"),
        Str(reader, "published_at"),
        Str(reader, "delivery_token"),
        Str(reader, "created_at"),
        Str(reader, "updated_at"));

    private static string? Str(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return DbRead.TextOrNull(reader, ordinal);
    }
}
