using System.Data.Common;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service.OAuth;

/// <summary>
/// Passport 数据库访问（旧版 <c>oauth_*</c> 表结构：clients 为自增 id / redirect 文本 /
/// personal_access_client / password_client）。
/// </summary>
public sealed class OAuthStore(Database db, string timezone)
{
    public sealed record Client(long Id, int? UserId, string Name, string? Secret,
        string? Provider, string? Redirect, bool PersonalAccessClient, bool PasswordClient,
        bool Revoked, string? CreatedAt, string? UpdatedAt)
    {
        /// <summary><c>Passport::client-&gt;redirect_uris</c>：逗号分隔。</summary>
        public List<string> RedirectUris() =>
            (Redirect ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();
    }

    public sealed record Token(string Id, int? UserId, long ClientId, string? Name,
        List<string> Scopes, bool Revoked, string? CreatedAt, string? UpdatedAt,
        string? ExpiresAt);

    public sealed record AuthCode(string Id, int UserId, long ClientId, List<string> Scopes,
        bool Revoked, string? ExpiresAt);

    public sealed record RefreshToken(string Id, string AccessTokenId, bool Revoked,
        string? ExpiresAt);

    public Database Database => db;

    // ---------------------------------------------------------------- 客户端

    public Client? FindClient(long id) =>
        db.QueryFirst("SELECT * FROM oauth_clients WHERE id = @id", MapClient, ("@id", id));

    public Client? FindActiveClient(long id) =>
        FindClient(id) is { Revoked: false } client ? client : null;

    public List<Client> ClientsForUser(int uid) =>
        db.Query("SELECT * FROM oauth_clients WHERE user_id = @u AND revoked = @r ORDER BY name",
            MapClient, ("@u", uid), ("@r", false));

    public Client? FindClientForUser(long id, int uid) =>
        db.QueryFirst(
            "SELECT * FROM oauth_clients WHERE id = @id AND user_id = @u AND revoked = @r",
            MapClient, ("@id", id), ("@u", uid), ("@r", false));

    public long CreateClient(int? userId, string name, string? secret, string? provider,
        string redirect, bool personalAccess, bool passwordClient)
    {
        string now = PhpDate.Now(timezone);
        return db.InsertReturningId("oauth_clients", "id",
            "INSERT INTO oauth_clients (user_id, name, secret, provider, redirect,"
            + " personal_access_client, password_client, revoked, created_at, updated_at)"
            + " VALUES (@u,@n,@s,@p,@r,@pa,@pc,@rev,@c,@up)",
            ("@u", userId), ("@n", name), ("@s", secret), ("@p", provider), ("@r", redirect),
            ("@pa", personalAccess), ("@pc", passwordClient), ("@rev", false),
            ("@c", now), ("@up", now));
    }

    public void UpdateClient(long id, string name, string redirect) =>
        db.Execute("UPDATE oauth_clients SET name = @n, redirect = @r, updated_at = @u"
            + " WHERE id = @id",
            ("@n", name), ("@r", redirect), ("@u", PhpDate.Now(timezone)), ("@id", id));

    /// <summary><c>ClientRepository::delete()</c>：撤销该客户端的令牌并置 revoked。</summary>
    public void DeleteClient(long id)
    {
        db.Execute("UPDATE oauth_refresh_tokens SET revoked = @r WHERE access_token_id IN"
            + " (SELECT id FROM oauth_access_tokens WHERE client_id = @id)",
            ("@r", true), ("@id", id));
        db.Execute("UPDATE oauth_access_tokens SET revoked = @r WHERE client_id = @id",
            ("@r", true), ("@id", id));
        db.Execute("UPDATE oauth_clients SET revoked = @r, updated_at = @u WHERE id = @id",
            ("@r", true), ("@u", PhpDate.Now(timezone)), ("@id", id));
    }

    /// <summary>第一个启用的 personal_access 客户端（源 <c>personalAccessClient()</c>）。</summary>
    public Client? PersonalAccessClient() =>
        db.QueryFirst("SELECT * FROM oauth_clients WHERE revoked = @r"
            + " AND personal_access_client = @p ORDER BY id DESC",
            MapClient, ("@r", false), ("@p", true));

    /// <summary>客户端 secret 校验（Passport 13 使用 bcrypt 哈希）。</summary>
    public bool VerifyClientSecret(Client client, string? secret, ICipher cipher)
    {
        if (string.IsNullOrEmpty(client.Secret))
        {
            return string.IsNullOrEmpty(secret);
        }

        if (secret is null)
        {
            return false;
        }

        return client.Secret.StartsWith("$2", StringComparison.Ordinal)
               || client.Secret.StartsWith("$argon", StringComparison.Ordinal)
            ? cipher.Verify(secret, client.Secret, "")
            : string.Equals(secret, client.Secret, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 令牌

    public void InsertToken(string id, int? userId, long clientId, string? name,
        List<string> scopes, string? expiresAt)
    {
        string now = PhpDate.Now(timezone);
        db.Execute("INSERT INTO oauth_access_tokens (id, user_id, client_id, name, scopes,"
            + " revoked, created_at, updated_at, expires_at)"
            + " VALUES (@id,@u,@c,@n,@s,@r,@ca,@ua,@e)",
            ("@id", id), ("@u", userId), ("@c", clientId), ("@n", name),
            ("@s", PhpJson.Encode(scopes, 0)), ("@r", false),
            ("@ca", now), ("@ua", now), ("@e", expiresAt));
    }

    public Token? FindToken(string id) =>
        db.QueryFirst("SELECT * FROM oauth_access_tokens WHERE id = @id", MapToken, ("@id", id));

    public void RevokeToken(string id) =>
        db.Execute("UPDATE oauth_access_tokens SET revoked = @r, updated_at = @u WHERE id = @id",
            ("@r", true), ("@u", PhpDate.Now(timezone)), ("@id", id));

    public List<Token> ActiveTokensForUser(int uid) =>
        db.Query("SELECT t.* FROM oauth_access_tokens t JOIN oauth_clients c"
            + " ON c.id = t.client_id WHERE t.user_id = @u AND t.revoked = @r"
            + " AND t.expires_at > @now AND c.revoked = @r ORDER BY t.created_at DESC",
            MapToken, ("@u", uid), ("@r", false), ("@now", PhpDate.Now(timezone)));

    public Token? FindTokenForUser(string id, int uid) =>
        db.QueryFirst("SELECT * FROM oauth_access_tokens WHERE id = @id AND user_id = @u"
            + " AND revoked = @r AND expires_at > @now",
            MapToken, ("@id", id), ("@u", uid), ("@r", false),
            ("@now", PhpDate.Now(timezone)));

    public bool HasScopesForClientAndUser(int uid, long clientId, List<string> scopes)
    {
        List<string> rows = db.Query(
            "SELECT scopes FROM oauth_access_tokens WHERE user_id = @u AND client_id = @c"
            + " AND revoked = @r AND expires_at > @now",
            reader => reader.IsDBNull(0) ? "" : DbRead.Text(reader, 0),
            ("@u", uid), ("@c", clientId), ("@r", false), ("@now", PhpDate.Now(timezone)));

        if (scopes.Count == 0)
        {
            return rows.Count > 0;
        }

        var granted = new List<string>();
        foreach (string row in rows)
        {
            granted.AddRange(ParseScopes(row));
        }

        return scopes.All(granted.Contains);
    }

    // ---------------------------------------------------------------- 授权码

    public void InsertAuthCode(string id, int userId, long clientId, List<string> scopes,
        string? expiresAt) =>
        db.Execute("INSERT INTO oauth_auth_codes (id, user_id, client_id, scopes, revoked,"
            + " expires_at) VALUES (@id,@u,@c,@s,@r,@e)",
            ("@id", id), ("@u", userId), ("@c", clientId),
            ("@s", PhpJson.Encode(scopes, 0)), ("@r", false), ("@e", expiresAt));

    public AuthCode? FindAuthCode(string id) =>
        db.QueryFirst("SELECT * FROM oauth_auth_codes WHERE id = @id",
            reader => new AuthCode(
                DbRead.Text(reader, reader.GetOrdinal("id")),
                reader.GetInt32(reader.GetOrdinal("user_id")),
                DbRead.Long(reader, reader.GetOrdinal("client_id")),
                ParseScopes(Str(reader, "scopes")),
                Bool(reader, "revoked"),
                Str(reader, "expires_at")),
            ("@id", id));

    public void RevokeAuthCode(string id) =>
        db.Execute("UPDATE oauth_auth_codes SET revoked = @r WHERE id = @id",
            ("@r", true), ("@id", id));

    // ---------------------------------------------------------------- 刷新令牌

    public void InsertRefreshToken(string id, string accessTokenId, string? expiresAt) =>
        db.Execute("INSERT INTO oauth_refresh_tokens (id, access_token_id, revoked, expires_at)"
            + " VALUES (@id,@a,@r,@e)",
            ("@id", id), ("@a", accessTokenId), ("@r", false), ("@e", expiresAt));

    public RefreshToken? FindRefreshToken(string id) =>
        db.QueryFirst("SELECT * FROM oauth_refresh_tokens WHERE id = @id", MapRefreshToken,
            ("@id", id));

    public void RevokeRefreshToken(string id) =>
        db.Execute("UPDATE oauth_refresh_tokens SET revoked = @r WHERE id = @id",
            ("@r", true), ("@id", id));

    public RefreshToken? RefreshTokenForAccessToken(string accessTokenId) =>
        db.QueryFirst("SELECT * FROM oauth_refresh_tokens WHERE access_token_id = @a",
            MapRefreshToken, ("@a", accessTokenId));

    // ---------------------------------------------------------------- 工具

    /// <summary>解析 <c>scopes</c> 列的 JSON；失败按空数组（与 PHP <c>json_decode</c> 一致）。</summary>
    public static List<string> ParseScopes(string? json)
    {
        var scopes = new List<string>();
        if (string.IsNullOrEmpty(json))
        {
            return scopes;
        }

        try
        {
            if (PhpJson.Decode(json) is PhpArray array)
            {
                foreach (object? item in array.Values)
                {
                    scopes.Add(PhpString.Cast(item));
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            // 与 PHP json_decode 失败一致：空数组
        }

        return scopes;
    }

    /// <summary>客户端 JSON（{@code Client::toArray()}：隐藏 secret）。</summary>
    public static Dictionary<string, object?> ClientJson(Client client) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = client.Id,
            ["user_id"] = client.UserId,
            ["name"] = client.Name,
            ["provider"] = client.Provider,
            ["redirect"] = client.Redirect,
            ["personal_access_client"] = client.PersonalAccessClient,
            ["password_client"] = client.PasswordClient,
            ["revoked"] = client.Revoked,
            ["created_at"] = client.CreatedAt,
            ["updated_at"] = client.UpdatedAt,
        };

    private static Client MapClient(DbDataReader reader) => new(
        DbRead.Long(reader, reader.GetOrdinal("id")),
        reader.IsDBNull(reader.GetOrdinal("user_id"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("user_id")),
        DbRead.Text(reader, reader.GetOrdinal("name")),
        Str(reader, "secret"),
        Str(reader, "provider"),
        Str(reader, "redirect"),
        Bool(reader, "personal_access_client"),
        Bool(reader, "password_client"),
        Bool(reader, "revoked"),
        Str(reader, "created_at"),
        Str(reader, "updated_at"));

    private static Token MapToken(DbDataReader reader) => new(
        DbRead.Text(reader, reader.GetOrdinal("id")),
        reader.IsDBNull(reader.GetOrdinal("user_id"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("user_id")),
        DbRead.Long(reader, reader.GetOrdinal("client_id")),
        Str(reader, "name"),
        ParseScopes(Str(reader, "scopes")),
        Bool(reader, "revoked"),
        Str(reader, "created_at"),
        Str(reader, "updated_at"),
        Str(reader, "expires_at"));

    private static RefreshToken MapRefreshToken(DbDataReader reader) => new(
        DbRead.Text(reader, reader.GetOrdinal("id")),
        DbRead.Text(reader, reader.GetOrdinal("access_token_id")),
        Bool(reader, "revoked"),
        Str(reader, "expires_at"));

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
