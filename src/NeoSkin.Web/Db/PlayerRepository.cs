using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>players</c> 表的访问（登录/注册、用户中心、纹理 JSON 共用）。
/// </summary>
public sealed class PlayerRepository(Database db)
{
    private const string Columns = "pid, uid, name, tid_skin, tid_cape, last_modified";

    /// <summary>玩家 + 所属用户权限（{@code PlayerController@json} 用）。</summary>
    public sealed record PlayerDetail(int Pid, int Uid, string Name, int TidSkin, int TidCape,
        string? LastModified, int Permission);

    public PlayerRow? FindByName(string name) =>
        db.QueryFirst("SELECT " + Columns + " FROM players WHERE name = @n",
            Map, ("@n", name));

    public PlayerRow? FindById(int pid) =>
        db.QueryFirst("SELECT " + Columns + " FROM players WHERE pid = @p",
            Map, ("@p", pid));

    public PlayerDetail? FindDetailByName(string name) =>
        db.QueryFirst("SELECT p.*, u.permission AS permission FROM players p"
            + " LEFT JOIN users u ON u.uid = p.uid WHERE p.name = @n",
            reader => new PlayerDetail(
                reader.GetInt32(reader.GetOrdinal("pid")),
                reader.GetInt32(reader.GetOrdinal("uid")),
                DbRead.Text(reader, reader.GetOrdinal("name")),
                reader.GetInt32(reader.GetOrdinal("tid_skin")),
                reader.GetInt32(reader.GetOrdinal("tid_cape")),
                Str(reader, "last_modified"),
                reader.IsDBNull(reader.GetOrdinal("permission"))
                    ? 0
                    : reader.GetInt32(reader.GetOrdinal("permission"))),
            ("@n", name));

    public int CountByName(string name) =>
        (int)db.Long("SELECT COUNT(*) FROM players WHERE name = @n", ("@n", name));

    /// <summary><c>$user-&gt;players()-&gt;count()</c>。</summary>
    public int CountByUser(int uid) =>
        (int)db.Long("SELECT COUNT(*) FROM players WHERE uid = @u", ("@u", uid));

    public int CountAll() => (int)db.Long("SELECT COUNT(*) FROM players");

    /// <summary><c>Auth::user()-&gt;players</c>（按 pid 升序）。</summary>
    public List<PlayerRow> ListByUid(int uid) =>
        db.Query("SELECT " + Columns + " FROM players WHERE uid = @u ORDER BY pid ASC",
            Map, ("@u", uid));

    /// <summary>插入角色，返回 pid。</summary>
    public int Insert(int uid, string name, int tidSkin, int tidCape, string lastModified)
    {
        long pid = db.InsertReturningId("players", "pid",
            "INSERT INTO players (uid, name, tid_cape, last_modified, tid_skin)"
            + " VALUES (@u, @n, @cape, @lm, @skin)",
            ("@u", uid), ("@n", name), ("@cape", tidCape), ("@lm", lastModified), ("@skin", tidSkin));
        return (int)pid;
    }

    /// <summary>源 {@code PlayerRepository::insert(uid, name, now)}：{@code tid_skin=-1}。</summary>
    public int Insert(int uid, string name, string now) =>
        Insert(uid, name, -1, 0, now);

    public void Delete(int pid) =>
        db.Execute("DELETE FROM players WHERE pid = @p", ("@p", pid));

    public string? LastModified(int pid) =>
        db.Scalar("SELECT last_modified FROM players WHERE pid = @p", ("@p", pid))?.ToString();

    public void UpdateName(int pid, string name, string now) =>
        db.Execute("UPDATE players SET name = @n, last_modified = @l WHERE pid = @p",
            ("@n", name), ("@l", now), ("@p", pid));

    public void UpdateTextures(int pid, int tidSkin, int tidCape, string now) =>
        db.Execute("UPDATE players SET tid_skin = @s, tid_cape = @c, last_modified = @l"
            + " WHERE pid = @p",
            ("@s", tidSkin), ("@c", tidCape), ("@l", now), ("@p", pid));

    public void UpdateOwner(int pid, int uid, string now) =>
        db.Execute("UPDATE players SET uid = @u, last_modified = @l WHERE pid = @p",
            ("@u", uid), ("@l", now), ("@p", pid));

    public void UpdateTidCape(int pid, int tidCape) =>
        db.Execute("UPDATE players SET tid_cape = @c WHERE pid = @p", ("@c", tidCape), ("@p", pid));

    public void UpdateTidSkin(int pid, int tidSkin) =>
        db.Execute("UPDATE players SET tid_skin = @s WHERE pid = @p", ("@s", tidSkin), ("@p", pid));

    /// <summary><c>ResetPlayers</c>：把使用某材质的角色的 tid 置 0。</summary>
    public void ResetTexture(long tid, string column, int? excludeUid)
    {
        string col = column == "tid_cape" ? "tid_cape" : "tid_skin";
        string sql = "UPDATE players SET " + col + " = 0 WHERE " + col + " = @t"
            + (excludeUid is null ? "" : " AND uid <> @u");
        if (excludeUid is null)
        {
            db.Execute(sql, ("@t", tid));
        }
        else
        {
            db.Execute(sql, ("@t", tid), ("@u", excludeUid));
        }
    }

    /// <summary>管理端列表的分页结果。</summary>
    public sealed record AdminPageResult(List<Dictionary<string, object?>> Rows, long Total);

    /// <summary><c>Player::usingSearchString($q)-&gt;paginate(10)</c>。</summary>
    public AdminPageResult AdminPage(SearchString.Result compiled, int page, int perPage)
    {
        int current = Math.Max(page, 1);
        string where = compiled.Invalid || compiled.WhereSql is null
            ? ""
            : " WHERE " + compiled.WhereSql;
        string columns = compiled.Select.Count == 0 ? "*" : string.Join(", ", compiled.Select);
        string order = compiled.OrderBy.Count == 0
            ? ""
            : " ORDER BY " + string.Join(", ", compiled.OrderBy);

        long total = db.Long("SELECT COUNT(*) FROM players" + where,
            compiled.Parameters.ToArray());
        List<Dictionary<string, object?>> rows = db.Query(
            "SELECT " + columns + " FROM players" + where + order
            + " " + Paging(db.Dialect.Name, perPage, (current - 1) * perPage),
            ReadAdminRow, compiled.Parameters.ToArray());
        return new AdminPageResult(rows, total);
    }

    private static Dictionary<string, object?> ReadAdminRow(System.Data.Common.DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string column = reader.GetName(i);
            object? value = DbRead.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            if (column is "pid" or "uid" or "tid_skin" or "tid_cape")
            {
                value = value is null ? null : Convert.ToInt32(value);
            }

            row[column] = value;
        }

        return row;
    }

    private static PlayerRow Map(DbDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetInt32(1),
        DbRead.Text(reader, 2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        "default");

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
