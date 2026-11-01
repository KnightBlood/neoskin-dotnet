using System.Data.Common;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>user_closet</c> 关联表的仓库（对应 <c>User::closet()</c>，belongsToMany Texture，
/// withPivot <c>item_name</c>）。
/// </summary>
public sealed class ClosetRepository(Database db)
{
    /// <summary>收藏夹条目：pivot 字段 + 纹理行。</summary>
    public sealed record Item(int UserUid, long TextureTid, string? ItemName, TextureRow Texture);

    /// <summary>类型过滤条件：cape → 只披风；其余 → steve/alex。</summary>
    public static string TypeCondition(string? category) =>
        category == "cape" ? "t.type = 'cape'" : "t.type IN ('steve','alex')";

    public long Count(int uid, string? category, string? search)
    {
        string sql = "SELECT COUNT(*) FROM user_closet c JOIN textures t ON t.tid = c.texture_tid"
            + " WHERE c.user_uid = @u AND " + TypeCondition(category);
        if (search is not null)
        {
            sql += " AND c.item_name LIKE @q";
            return db.Long(sql, ("@u", uid), ("@q", "%" + search + "%"));
        }

        return db.Long(sql, ("@u", uid));
    }

    /// <summary>分页行，按 <c>texture_tid</c> 倒序。</summary>
    public List<Item> Page(int uid, string? category, string? search, int limit, int offset)
    {
        string sql = "SELECT t.*, c.user_uid AS cu, c.texture_tid AS ct, c.item_name AS ci"
            + " FROM textures t JOIN user_closet c ON t.tid = c.texture_tid"
            + " WHERE c.user_uid = @u AND " + TypeCondition(category);
        var parameters = new List<(string, object?)> { ("@u", uid) };
        if (search is not null)
        {
            sql += " AND c.item_name LIKE @q";
            parameters.Add(("@q", "%" + search + "%"));
        }

        sql += " ORDER BY t.tid DESC " + Paging(db.Dialect.Name, limit, offset);
        return db.Query(sql, MapItem, parameters.ToArray());
    }

    /// <summary>分页子句（方言差异；参数由本类生成，安全内联）。</summary>
    private static string Paging(string dialect, int limit, int offset) => dialect switch
    {
        "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
        _ => "LIMIT " + limit + " OFFSET " + offset,
    };

    /// <summary><c>allIds</c>：<c>pluck('texture_tid')</c>。</summary>
    public List<long> AllIds(int uid) =>
        db.Query("SELECT texture_tid FROM user_closet WHERE user_uid = @u",
            reader => DbRead.Long(reader, 0), ("@u", uid));

    public List<int> LikerUids(long tid, int excludeUid) =>
        db.Query("SELECT user_uid FROM user_closet WHERE texture_tid = @t AND user_uid <> @u"
            + " ORDER BY user_uid", reader => reader.GetInt32(0), ("@t", tid), ("@u", excludeUid));

    /// <summary><c>$user-&gt;closet</c>（管理端接口，不过滤类型）。</summary>
    public List<Item> All(int uid) =>
        db.Query("SELECT t.*, c.user_uid AS cu, c.texture_tid AS ct, c.item_name AS ci"
            + " FROM textures t JOIN user_closet c ON t.tid = c.texture_tid"
            + " WHERE c.user_uid = @u ORDER BY t.tid", MapItem, ("@u", uid));

    public Item? Find(int uid, long tid) =>
        db.QueryFirst("SELECT t.*, c.user_uid AS cu, c.texture_tid AS ct, c.item_name AS ci"
            + " FROM textures t JOIN user_closet c ON t.tid = c.texture_tid"
            + " WHERE c.user_uid = @u AND c.texture_tid = @t", MapItem, ("@u", uid), ("@t", tid));

    public bool Exists(int uid, long tid) =>
        db.Long("SELECT COUNT(*) FROM user_closet WHERE user_uid = @u AND texture_tid = @t",
            ("@u", uid), ("@t", tid)) > 0;

    public void Attach(int uid, long tid, string? itemName) =>
        db.Execute("INSERT INTO user_closet (user_uid, texture_tid, item_name) VALUES (@u,@t,@n)",
            ("@u", uid), ("@t", tid), ("@n", itemName));

    public void RenameItem(int uid, long tid, string? itemName) =>
        db.Execute("UPDATE user_closet SET item_name = @n WHERE user_uid = @u AND texture_tid = @t",
            ("@n", itemName), ("@u", uid), ("@t", tid));

    public void Detach(int uid, long tid) =>
        db.Execute("DELETE FROM user_closet WHERE user_uid = @u AND texture_tid = @t",
            ("@u", uid), ("@t", tid));

    private static Item MapItem(DbDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("cu")),
        DbRead.Long(reader, reader.GetOrdinal("ct")),
        reader.IsDBNull(reader.GetOrdinal("ci"))
            ? null
            : DbRead.Text(reader, reader.GetOrdinal("ci")),
        new TextureRow(
            DbRead.Long(reader, reader.GetOrdinal("tid")),
            DbRead.Text(reader, reader.GetOrdinal("name")),
            DbRead.Text(reader, reader.GetOrdinal("type")),
            DbRead.Text(reader, reader.GetOrdinal("hash")),
            DbRead.Long(reader, reader.GetOrdinal("size")),
            reader.GetInt32(reader.GetOrdinal("uploader")),
            Bool(reader, "public"),
            Str(reader, "upload_at") ?? "",
            reader.IsDBNull(reader.GetOrdinal("likes"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("likes"))));

    /// <summary><c>Texture::toArray()</c>（含 model 追加属性）。</summary>
    public static Dictionary<string, object?> TextureJson(TextureRow t) =>
        new(StringComparer.Ordinal)
        {
            ["tid"] = t.Tid,
            ["name"] = t.Name,
            ["type"] = t.Type,
            ["hash"] = t.Hash,
            ["size"] = t.Size,
            ["uploader"] = t.Uploader,
            ["public"] = t.IsPublic,
            ["upload_at"] = t.UploadAt,
            ["likes"] = t.Likes,
            ["model"] = t.TextureModel,
        };

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
