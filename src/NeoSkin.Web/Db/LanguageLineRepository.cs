using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>Spatie\TranslationLoader\LanguageLine</c> 的写侧移植（读侧由
/// <c>I18n</c> 的语言文件/数据库覆盖实现）。
///
/// <p><c>text</c> 列是 JSON 对象（locale → 文案），保持插入顺序。</p>
/// </summary>
public sealed class LanguageLineRepository(Database db)
{
    public sealed record Row(int Id, string Group, string Key, PhpArray Text,
        string? CreatedAt, string? UpdatedAt);

    public sealed record Page(List<Row> Rows, long Total, int CurrentPage, int PerPage);

    public Row? Find(int id) =>
        db.QueryFirst("SELECT id, " + db.Dialect.Quote("group") + ", "
            + db.Dialect.Quote("key") + ", text, created_at, updated_at"
            + " FROM language_lines WHERE id = @id", Map, ("@id", id));

    /// <summary><c>LanguageLine::paginate(10)</c> 的数据部分（按 id 升序）。</summary>
    public Page List(int page)
    {
        int current = Math.Max(page, 1);
        const int perPage = 10;
        long total = db.Long("SELECT COUNT(*) FROM language_lines");
        List<Row> rows = db.Query("SELECT id, " + db.Dialect.Quote("group") + ", "
            + db.Dialect.Quote("key") + ", text, created_at, updated_at"
            + " FROM language_lines ORDER BY id " + Paging(db.Dialect.Name, perPage,
                (current - 1) * perPage), Map);
        return new Page(rows, total, current, perPage);
    }

    /// <summary><c>save()</c>：id=0 插入，否则更新 group/key/text 与 updated_at。</summary>
    public Row Save(Row row, string timezone)
    {
        string now = PhpDate.Now(timezone);
        string json = PhpJson.Encode(row.Text, 0);
        if (row.Id == 0)
        {
            long id = db.InsertReturningId("language_lines", "id",
                "INSERT INTO language_lines (" + db.Dialect.Quote("group") + ", "
                + db.Dialect.Quote("key") + ", text, created_at, updated_at)"
                + " VALUES (@g,@k,@t,@c,@u)",
                ("@g", row.Group), ("@k", row.Key), ("@t", json), ("@c", now), ("@u", now));
            return row with { Id = (int)id, CreatedAt = now, UpdatedAt = now };
        }

        db.Execute("UPDATE language_lines SET " + db.Dialect.Quote("group") + " = @g, "
            + db.Dialect.Quote("key") + " = @k, text = @t, updated_at = @u WHERE id = @id",
            ("@g", row.Group), ("@k", row.Key), ("@t", json), ("@u", now), ("@id", row.Id));
        return row with { UpdatedAt = now };
    }

    public void Delete(int id) =>
        db.Execute("DELETE FROM language_lines WHERE id = @id", ("@id", id));

    private static Row Map(DbDataReader reader)
    {
        var text = new PhpArray();
        int ordinal = reader.GetOrdinal("text");
        if (!reader.IsDBNull(ordinal))
        {
            try
            {
                if (PhpJson.Decode(DbRead.Text(reader, ordinal)) is PhpArray parsed)
                {
                    text = parsed;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                // 非法 JSON 按空对象处理
            }
        }

        return new Row(
            reader.GetInt32(reader.GetOrdinal("id")),
            DbRead.Text(reader, reader.GetOrdinal("group")),
            DbRead.Text(reader, reader.GetOrdinal("key")),
            text,
            Str(reader, "created_at"),
            Str(reader, "updated_at"));
    }

    private static string? Str(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return DbRead.TextOrNull(reader, ordinal);
    }

    private static string Paging(string dialect, int limit, int offset) => dialect switch
    {
        "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
        _ => "LIMIT " + limit + " OFFSET " + offset,
    };
}
