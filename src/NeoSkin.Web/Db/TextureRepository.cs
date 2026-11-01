using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>textures</c> 表的访问（只读部分；上传/管理随 Skinlib 控制器补齐）。
///
/// <p>纹理文件按 <c>hash</c> 存放在 <c>storage/textures/</c>，本类只负责表行。</p>
/// </summary>
public sealed class TextureRepository(Database db)
{
    /// <summary><c>public</c> 在 SQL Server 是保留字，必须按方言引用。</summary>
    private string PublicColumn => db.Dialect.Quote("public");

    private string True => "1";

    public TextureRow? FindByTid(long tid) =>
        db.QueryFirst("SELECT * FROM textures WHERE tid = @t", Map, ("@t", tid));

    public TextureRow? FindByHash(string hash) =>
        db.QueryFirst("SELECT * FROM textures WHERE hash = @h", Map, ("@h", hash));

    /// <summary><c>where('hash',...)-&gt;where(public OR uploader me)-&gt;first()</c>。</summary>
    public TextureRow? FindPublicOrMine(string hash, int uid) =>
        db.QueryFirst("SELECT * FROM textures WHERE hash = @h AND (" + PublicColumn
                      + " = " + True + " OR uploader = @u)",
            Map, ("@h", hash), ("@u", uid));

    public TextureRow? FindPublicByHash(string hash) =>
        db.QueryFirst("SELECT * FROM textures WHERE hash = @h AND " + PublicColumn + " = " + True,
            Map, ("@h", hash));

    public long CountByHash(string hash) =>
        db.Long("SELECT COUNT(*) FROM textures WHERE hash = @h", ("@h", hash));

    public long CountAll() => db.Long("SELECT COUNT(*) FROM textures");

    /// <summary><c>sum('size')</c>（单位为 KB）。</summary>
    public long SumSize() => db.Long("SELECT SUM(" + db.Dialect.Quote("size") + ") FROM textures");

    /// <summary><c>where('uploader', $uid)-&gt;sum('size')</c>（个人存储占用）。</summary>
    public long SumSizeByUploader(int uid) =>
        db.Long("SELECT COALESCE(SUM(" + db.Dialect.Quote("size") + "), 0) FROM textures"
                + " WHERE uploader = @u", ("@u", uid));

    public List<string> UploadTimesSince(string since) =>
        db.Query("SELECT upload_at FROM textures WHERE upload_at >= @s",
            reader => DbRead.Text(reader, 0), ("@s", since));

    /// <summary>上传：新增纹理行，返回自增 tid；{@code likes} 初始为 1。</summary>
    public long Insert(string name, string type, string hash, long size, int uploader,
        bool isPublic, string uploadAt)
    {
        return db.InsertReturningId("textures", "tid",
            "INSERT INTO textures (name, type, hash, " + db.Dialect.Quote("size")
            + ", uploader, " + PublicColumn + ", upload_at, likes)"
            + " VALUES (@n,@t,@h,@s,@u,@p,@at,1)",
            ("@n", name), ("@t", type), ("@h", hash), ("@s", size), ("@u", uploader),
            ("@p", isPublic ? 1 : 0), ("@at", uploadAt));
    }

    public void DeleteByTid(long tid) =>
        db.Execute("DELETE FROM textures WHERE tid = @t", ("@t", tid));

    public void UpdateName(long tid, string name) =>
        db.Execute("UPDATE textures SET name = @n WHERE tid = @t", ("@n", name), ("@t", tid));

    public void UpdateLikes(long tid, int likes) =>
        db.Execute("UPDATE textures SET likes = @l WHERE tid = @t", ("@l", likes), ("@t", tid));

    public void UpdateType(long tid, string type) =>
        db.Execute("UPDATE textures SET type = @y WHERE tid = @t", ("@y", type), ("@t", tid));

    public void UpdatePublic(long tid, bool isPublic) =>
        db.Execute("UPDATE textures SET " + PublicColumn + " = @p WHERE tid = @t",
            ("@p", isPublic ? 1 : 0), ("@t", tid));

    /// <summary><c>SkinlibController@library</c> 的计数（同 <see cref="Page"/> 的过滤）。</summary>
    public long CountForLibrary(string? type, string? keyword, int? uploaderUid, int? viewerUid,
        bool admin)
    {
        var parameters = new List<(string, object?)>();
        string where = LibraryWhere(type, keyword, uploaderUid, viewerUid, admin, parameters);
        return db.Long("SELECT COUNT(*) FROM textures t WHERE 1=1" + where, parameters.ToArray());
    }

    /// <summary><c>SkinlibController@library</c> 的分页行（tid/name/type/uploader/public/likes/nickname）。</summary>
    public List<Dictionary<string, object?>> PageForLibrary(string? type, string? keyword,
        int? uploaderUid, int? viewerUid, bool admin, string? sortBy, int limit, int offset)
    {
        var parameters = new List<(string, object?)>();
        string where = LibraryWhere(type, keyword, uploaderUid, viewerUid, admin, parameters);
        string column = sortBy switch
        {
            "name" => "name",
            "likes" => "likes",
            "tid" => "tid",
            _ => "upload_at",
        };

        string sql = "SELECT t.tid, t.name, t.type, t.uploader, t." + PublicColumn
            + ", t.likes, u.nickname"
            + " FROM textures t LEFT JOIN users u ON u.uid = t.uploader WHERE 1=1" + where
            + " ORDER BY t." + column + " DESC " + Paging(db.Dialect.Name, limit, offset);

        return db.Query(sql, reader => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tid"] = DbRead.Long(reader, reader.GetOrdinal("tid")),
            ["name"] = DbRead.Text(reader, reader.GetOrdinal("name")),
            ["type"] = DbRead.Text(reader, reader.GetOrdinal("type")),
            ["uploader"] = reader.GetInt32(reader.GetOrdinal("uploader")),
            ["public"] = Bool(reader, "public"),
            ["likes"] = reader.GetInt32(reader.GetOrdinal("likes")),
            ["nickname"] = reader.IsDBNull(reader.GetOrdinal("nickname"))
                ? null
                : DbRead.Text(reader, reader.GetOrdinal("nickname")),
        }, parameters.ToArray());
    }

    private string LibraryWhere(string? type, string? keyword, int? uploaderUid,
        int? viewerUid, bool admin, List<(string, object?)> parameters)
    {
        var sql = new System.Text.StringBuilder();
        if (type == "skin")
        {
            sql.Append(" AND t.type IN ('steve','alex')");
        }
        else if (!string.IsNullOrEmpty(type))
        {
            sql.Append(" AND t.type = @type");
            parameters.Add(("@type", type));
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            sql.Append(" AND t.name LIKE @kw");
            parameters.Add(("@kw", "%" + keyword + "%"));
        }

        if (uploaderUid is not null)
        {
            sql.Append(" AND t.uploader = @uploader");
            parameters.Add(("@uploader", uploaderUid));
        }

        if (!admin)
        {
            if (viewerUid is not null)
            {
                sql.Append(" AND (t." + PublicColumn + " = " + True + " OR t.uploader = @viewer)");
                parameters.Add(("@viewer", viewerUid));
            }
            else
            {
                sql.Append(" AND t." + PublicColumn + " = " + True);
            }
        }

        return sql.ToString();
    }

    /// <summary>分页子句（方言差异；参数由本类生成，安全内联）。</summary>
    private static string Paging(string dialect, int limit, int offset) => dialect switch
    {
        "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
        _ => "LIMIT " + limit + " OFFSET " + offset,
    };

    private static TextureRow Map(DbDataReader reader) => new(
        DbRead.Long(reader, reader.GetOrdinal("tid")),
        DbRead.Text(reader, reader.GetOrdinal("name")),
        DbRead.Text(reader, reader.GetOrdinal("type")),
        DbRead.Text(reader, reader.GetOrdinal("hash")),
        DbRead.Long(reader, reader.GetOrdinal("size")),
        reader.GetInt32(reader.GetOrdinal("uploader")),
        Bool(reader, "public"),
        Str(reader, "upload_at") ?? "",
        reader.IsDBNull(reader.GetOrdinal("likes")) ? 0 : reader.GetInt32(reader.GetOrdinal("likes")));

    private static string? Str(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return DbRead.TextOrNull(reader, ordinal);
    }

    private static bool Bool(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        object value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            string s => s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}
