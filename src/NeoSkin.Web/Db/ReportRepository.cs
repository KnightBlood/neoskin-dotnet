using System.Data.Common;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>reports</c> 表的访问（举报提交 / 我的举报 / 管理端列表与审核）。
/// </summary>
public sealed class ReportRepository(Database db, UserRepository users,
    TextureRepository textures)
{
    public const int Pending = 0;
    public const int Resolved = 1;
    public const int Rejected = 2;

    public sealed record Row(long Id, int Tid, int Uploader, int Reporter, string Reason,
        int Status, string ReportAt);

    public sealed record Page(List<Row> Rows, long Total, int CurrentPage, int PerPage);

    public sealed record AdminPageResult(List<Dictionary<string, object?>> Rows, long Total);

    public Row? FindById(long id) =>
        db.QueryFirst("SELECT * FROM reports WHERE id = @id", Map, ("@id", id));

    public long CountByReporterAndTid(int reporter, int tid) =>
        db.Long("SELECT COUNT(*) FROM reports WHERE reporter = @r AND tid = @t",
            ("@r", reporter), ("@t", tid));

    public long Insert(int tid, int uploader, int reporter, string reason, int status,
        string reportAt) =>
        db.InsertReturningId("reports", "id",
            "INSERT INTO reports (tid, uploader, reporter, reason, status, report_at)"
            + " VALUES (@t,@u,@r,@why,@s,@at)",
            ("@t", tid), ("@u", uploader), ("@r", reporter), ("@why", reason),
            ("@s", status), ("@at", reportAt));

    public void UpdateStatus(long id, int status) =>
        db.Execute("UPDATE reports SET status = @s WHERE id = @id", ("@s", status), ("@id", id));

    /// <summary><c>Report::where('reporter',$uid)-&gt;orderBy('report_at','desc')-&gt;paginate(10)</c>。</summary>
    public Page PageByReporter(int reporter, int page, int perPage)
    {
        int current = Math.Max(page, 1);
        long total = db.Long("SELECT COUNT(*) FROM reports WHERE reporter = @r",
            ("@r", reporter));
        List<Row> rows = db.Query("SELECT * FROM reports WHERE reporter = @r"
            + " ORDER BY report_at DESC " + Paging(db.Dialect.Name, perPage,
                (current - 1) * perPage), Map, ("@r", reporter));
        return new Page(rows, total, current, perPage);
    }

    /// <summary>
    /// <c>Report::usingSearchString($q)-&gt;with([...])-&gt;paginate(9)</c>：行内嵌
    /// <c>texture</c> / <c>texture_uploader</c> / <c>informer</c>（缺失为 null）。
    /// </summary>
    public AdminPageResult AdminPage(Support.SearchString.Result compiled, int page, int perPage)
    {
        int current = Math.Max(page, 1);
        string where = compiled.Invalid || compiled.WhereSql is null
            ? ""
            : " WHERE " + compiled.WhereSql;
        string columns = compiled.Select.Count == 0 ? "*" : string.Join(", ", compiled.Select);
        string order = compiled.OrderBy.Count == 0
            ? ""
            : " ORDER BY " + string.Join(", ", compiled.OrderBy);

        long total = db.Long("SELECT COUNT(*) FROM reports" + where,
            compiled.Parameters.ToArray());
        List<Row> rows = db.Query("SELECT " + columns + " FROM reports" + where + order
            + " " + Paging(db.Dialect.Name, perPage, (current - 1) * perPage),
            Map, compiled.Parameters.ToArray());

        var json = new List<Dictionary<string, object?>>();
        foreach (Row row in rows)
        {
            json.Add(ToJson(row));
        }

        return new AdminPageResult(json, total);
    }

    private Dictionary<string, object?> ToJson(Row row)
    {
        var json = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["tid"] = row.Tid,
            ["uploader"] = row.Uploader,
            ["reporter"] = row.Reporter,
            ["reason"] = row.Reason,
            ["status"] = row.Status,
            ["report_at"] = row.ReportAt,
        };

        TextureRow? texture = textures.FindByTid(row.Tid);
        json["texture"] = texture is null ? null : ClosetRepository.TextureJson(texture);
        json["texture_uploader"] = users.FindJsonById(row.Uploader);
        json["informer"] = users.FindJsonById(row.Reporter);
        return json;
    }

    private static string Paging(string dialect, int limit, int offset) => dialect switch
    {
        "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
        _ => "LIMIT " + limit + " OFFSET " + offset,
    };

    private static Row Map(DbDataReader reader) => new(
        DbRead.Long(reader, reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("tid")),
        reader.GetInt32(reader.GetOrdinal("uploader")),
        reader.GetInt32(reader.GetOrdinal("reporter")),
        DbRead.Text(reader, reader.GetOrdinal("reason")),
        reader.GetInt32(reader.GetOrdinal("status")),
        DbRead.Text(reader, reader.GetOrdinal("report_at")));
}
