using System.Data.Common;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Db;

/// <summary>
/// <c>users</c> 表的访问（登录、用户中心、管理端共用）。
///
/// <p>表结构以 <c>database/migrations/2016_11_18_133939_create_all_tables.php</c> 为准，
/// 后续 migration 追加 <c>verified</c> / <c>remember_token</c> / <c>locale</c> /
/// <c>is_dark_mode</c> 等列。</p>
/// </summary>
public sealed class UserRepository
{
    private readonly Database db;

    public UserRepository(Database database)
    {
        db = database;
        Database = database;
    }

    /// <summary>底层数据库连接（用于 {@code unique}/{@code exists} 存在性校验）。</summary>
    public Database Database { get; }

    /// <summary>权限常量（源 <c>App\Models\User</c>）。</summary>
    public const int Banned = -1;

    public const int Normal = 0;

    public const int Admin = 1;

    public const int SuperAdmin = 2;

    /// <summary><c>users</c> 的一行（登录/视图用到的列）。</summary>
    public sealed record UserRow(
        int Uid,
        string Email,
        string? Nickname,
        string Password,
        int Permission,
        int Avatar,
        int Score,
        bool Verified,
        bool DarkMode,
        string? Locale)
    {
        /// <summary><c>User::isAdmin()</c>：<c>permission &gt;= ADMIN</c>。</summary>
        public bool IsAdmin => Permission >= Admin;
    }

    private const string Columns =
        "uid, email, nickname, password, permission, avatar, score, verified, is_dark_mode, locale";

    /// <summary><c>User::findByEmail</c>。</summary>
    public UserRow? FindByEmail(string email) => FindOne("SELECT " + Columns + " FROM users WHERE email = @e",
        ("@e", email));

    /// <summary><c>User::find($uid)</c>。</summary>
    public UserRow? FindById(int uid) => FindOne("SELECT " + Columns + " FROM users WHERE uid = @u",
        ("@u", uid));

    private UserRow? FindOne(string sql, params (string, object?)[] parameters) =>
        db.QueryFirst(sql, Map, parameters);

    private static UserRow Map(DbDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("uid")),
        DbRead.Text(reader, reader.GetOrdinal("email")),
        Str(reader, "nickname"),
        DbRead.Text(reader, reader.GetOrdinal("password")),
        reader.GetInt32(reader.GetOrdinal("permission")),
        reader.GetInt32(reader.GetOrdinal("avatar")),
        reader.GetInt32(reader.GetOrdinal("score")),
        Bool(reader, "verified"),
        Bool(reader, "is_dark_mode"),
        Str(reader, "locale"));

    /// <summary><c>auth_user()-&gt;toArray()</c>：完整用户行（隐藏 password/remember_token）。</summary>
    public Dictionary<string, object?>? FindJsonById(int uid)
    {
        return db.QueryFirst("SELECT * FROM users WHERE uid = @u", ReadJsonRow, ("@u", uid));
    }

    /// <summary>行 → JSON 友好字典（按表列顺序，隐藏 password/remember_token）。</summary>
    public static Dictionary<string, object?> ReadJsonRow(DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string column = reader.GetName(i);
            if (column is "password" or "remember_token")
            {
                continue;
            }

            object? value = DbRead.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            value = column switch
            {
                "uid" or "score" or "avatar" or "permission" => value is null ? null : Convert.ToInt32(value),
                "verified" or "is_dark_mode" => ToBoolean(value),
                _ => value is long l && column is "likes" or "size" or "tid" ? l : value,
            };
            row[column] = value;
        }

        return row;
    }

    private static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    public int CountByEmail(string email) =>
        (int)db.Long("SELECT COUNT(*) FROM users WHERE email = @e", ("@e", email));

    public int CountByIp(string ip) =>
        (int)db.Long("SELECT COUNT(*) FROM users WHERE ip = @ip", ("@ip", ip));

    public int CountAll() => (int)db.Long("SELECT COUNT(*) FROM users");

    /// <summary>管理端列表的分页结果。</summary>
    public sealed record AdminPageResult(List<Dictionary<string, object?>> Rows, long Total);

    /// <summary><c>User::usingSearchString($q)-&gt;paginate(10)</c>（原始列）。</summary>
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

        long total = db.Long("SELECT COUNT(*) FROM users" + where,
            compiled.Parameters.ToArray());
        List<Dictionary<string, object?>> rows = db.Query(
            "SELECT " + columns + " FROM users" + where + order
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
            value = column switch
            {
                "uid" or "avatar" or "score" or "permission" =>
                    value is null ? null : Convert.ToInt32(value),
                "verified" or "is_dark_mode" => ToBoolean(value),
                _ => value,
            };
            row[column] = value;
        }

        return row;
    }

    private static string Paging(string dialect, int limit, int offset) => dialect switch
    {
        "sqlsrv" => "OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY",
        _ => "LIMIT " + limit + " OFFSET " + offset,
    };

    /// <summary><c>NotificationsController@send</c> 的收件人集合。</summary>
    public List<int> IdsForAudience(string? receiver, int? uid, string? email)
    {
        string sql;
        var parameters = Array.Empty<(string, object?)>();
        switch (receiver ?? "all")
        {
            case "normal":
                sql = "SELECT uid FROM users WHERE permission = " + Normal + " ORDER BY uid";
                break;
            case "uid":
                sql = "SELECT uid FROM users WHERE uid = @u";
                parameters = [("@u", uid)];
                break;
            case "email":
                sql = "SELECT uid FROM users WHERE email = @e";
                parameters = [("@e", email)];
                break;
            default:
                sql = "SELECT uid FROM users ORDER BY uid";
                break;
        }

        return db.Query(sql, reader => reader.GetInt32(0), parameters);
    }

    /// <summary>仪表盘折线图：取某时间之后的注册时间列。</summary>
    public List<string> RegisterTimesSince(string since) =>
        db.Query("SELECT register_at FROM users WHERE register_at >= @s",
            reader => DbRead.Text(reader, 0), ("@s", since));

    /// <summary>插入新用户，返回生成的 uid。</summary>
    public int Insert(string email, string nickname, int score, string password, string ip,
        string timezone)
    {
        string now = PhpDate.Now(timezone);
        string yesterday = PhpDate.At(DateTimeOffset.UtcNow.AddDays(-1), timezone);
        long uid = db.InsertReturningId("users", "uid",
            "INSERT INTO users (email, nickname, score, avatar, password, ip, permission,"
            + " last_sign_at, register_at, verified, verification_token, remember_token, locale,"
            + " is_dark_mode) VALUES (@e,@n,@s,0,@p,@ip," + Normal
            + ",@last,@reg," + db.Dialect.BoolLiteral(false)
            + ",'',NULL,NULL," + db.Dialect.BoolLiteral(false) + ")",
            ("@e", email), ("@n", nickname), ("@s", score), ("@p", password), ("@ip", ip),
            ("@last", yesterday), ("@reg", now));
        return (int)uid;
    }

    /// <summary>安装向导：创建超级管理员。</summary>
    public int InsertSuperAdmin(string email, string nickname, int score, string password,
        string ip, string registerAt, string lastSignAt)
    {
        long uid = db.InsertReturningId("users", "uid",
            "INSERT INTO users (email, nickname, score, avatar, password, ip, permission,"
            + " last_sign_at, register_at, verified, verification_token, remember_token, locale,"
            + " is_dark_mode) VALUES (@e,@n,@s,0,@p,@ip," + SuperAdmin
            + ",@last,@reg,1,'',NULL,NULL,0)",
            ("@e", email), ("@n", nickname), ("@s", score), ("@p", password), ("@ip", ip),
            ("@last", lastSignAt), ("@reg", registerAt));
        return (int)uid;
    }

    public void UpdateEmail(int uid, string email) =>
        db.Execute("UPDATE users SET email = @e WHERE uid = @u", ("@e", email), ("@u", uid));

    public void UpdateLocale(int uid, string locale) =>
        db.Execute("UPDATE users SET locale = @l WHERE uid = @u", ("@l", locale), ("@u", uid));

    public void UpdateVerified(int uid, bool verified) =>
        db.Execute("UPDATE users SET verified = @v WHERE uid = @u", ("@v", verified), ("@u", uid));

    public void ChangePassword(int uid, string hashedPassword) =>
        db.Execute("UPDATE users SET password = @p WHERE uid = @u",
            ("@p", hashedPassword), ("@u", uid));

    public string? LastSignAt(int uid) =>
        (string?)db.Scalar("SELECT last_sign_at FROM users WHERE uid = @u", ("@u", uid));

    public void UpdateNickname(int uid, string nickname) =>
        db.Execute("UPDATE users SET nickname = @n WHERE uid = @u", ("@n", nickname), ("@u", uid));

    public void UpdateScoreAndSignAt(int uid, int score, string lastSignAt) =>
        db.Execute("UPDATE users SET score = @s, last_sign_at = @l WHERE uid = @u",
            ("@s", score), ("@l", lastSignAt), ("@u", uid));

    public void UpdateScore(int uid, int score) =>
        db.Execute("UPDATE users SET score = @s WHERE uid = @u", ("@s", score), ("@u", uid));

    public void UpdateDarkMode(int uid, bool darkMode) =>
        db.Execute("UPDATE users SET is_dark_mode = @d WHERE uid = @u", ("@d", darkMode), ("@u", uid));

    public void UpdateAvatar(int uid, int avatar) =>
        db.Execute("UPDATE users SET avatar = @a WHERE uid = @u", ("@a", avatar), ("@u", uid));

    public void UpdateEmailAndUnverify(int uid, string email) =>
        db.Execute("UPDATE users SET email = @e, verified = 0 WHERE uid = @u",
            ("@e", email), ("@u", uid));

    public void UpdatePermission(int uid, int permission) =>
        db.Execute("UPDATE users SET permission = @p WHERE uid = @u",
            ("@p", permission), ("@u", uid));

    /// <summary><c>$user-&gt;closet()-&gt;where('texture_tid', $tid)-&gt;exists()</c>。</summary>
    public bool InCloset(int uid, int tid) =>
        db.Long("SELECT COUNT(*) FROM user_closet WHERE user_uid = @u AND texture_tid = @t",
            ("@u", uid), ("@t", tid)) > 0;

    /// <summary><c>$user-&gt;delete()</c>：先删该用户的角色，再删用户本身。</summary>
    public void DeleteWithPlayers(int uid)
    {
        using DbConnection connection = db.Open();
        using DbTransaction transaction = connection.BeginTransaction();
        try
        {
            using (DbCommand players = Database.Command(connection, "DELETE FROM players WHERE uid = @u",
                       ("@u", uid)))
            {
                players.Transaction = transaction;
                players.ExecuteNonQuery();
            }

            using (DbCommand user = Database.Command(connection, "DELETE FROM users WHERE uid = @u",
                       ("@u", uid)))
            {
                user.Transaction = transaction;
                user.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string? Str(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DbRead.Text(reader, ordinal);
    }

    private static bool Bool(DbDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && ToBoolean(reader.GetValue(ordinal));
    }
}
