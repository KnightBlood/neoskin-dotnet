using System.Security.Cryptography;
using System.Text;
using NeoSkin.Web.Db;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service.OAuth;

/// <summary>
/// Passport 的核心服务：作用域、令牌签发、Bearer/Cookie 认证。
///
/// <p>access token 采用与 league/oauth2-server v9 相同的 RS256 JWT；
/// refresh token 使用明文随机串（源为 php-encryption 加密载荷，见 PORT-NOTES）。</p>
/// </summary>
public sealed class OAuthService
{
    public const long TokenTtlSeconds = 31536000L; // P1Y
    public const long CodeTtlSeconds = 600L;       // PT10M
    public const string CookieName = "laravel_token";

    private static readonly IReadOnlyDictionary<string, string> DefaultScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["User.Read"] = "auth.oauth.scope.user.read",
            ["Notification.Read"] = "auth.oauth.scope.notification.read",
            ["Notification.ReadWrite"] = "auth.oauth.scope.notification.readwrite",
            ["Player.Read"] = "auth.oauth.scope.player.read",
            ["Player.ReadWrite"] = "auth.oauth.scope.player.readwrite",
            ["Closet.Read"] = "auth.oauth.scope.closet.read",
            // 源里拼写就是 ReadWrtie（历史遗留），保留以兼容前端
            ["Closet.ReadWrtie"] = "auth.oauth.scope.closet.readwrite",
            ["UsersManagement.Read"] = "auth.oauth.scope.users-management.read",
            ["UsersManagement.ReadWrite"] = "auth.oauth.scope.users-management.readwrite",
            ["PlayersManagement.Read"] = "auth.oauth.scope.players-management.read",
            ["PlayersManagement.ReadWrite"] = "auth.oauth.scope.players-management.readwrite",
            ["ClosetManagement.Read"] = "auth.oauth.scope.closet-management.read",
            ["ClosetManagement.ReadWrite"] = "auth.oauth.scope.closet-management.readwrite",
            ["ReportsManagement.Read"] = "auth.oauth.scope.reports-management.read",
            ["ReportsManagement.ReadWrite"] = "auth.oauth.scope.reports-management.readwrite",
        };

    private readonly OAuthStore store;
    private readonly OAuthKeys keys;
    private readonly UserRepository users;
    private readonly ICipher cipher;
    private readonly byte[]? cookieKey;
    private readonly string timezone;
    private volatile Dictionary<string, string>? _scopes;

    public OAuthService(string storageDirectory, OAuthStore store, UserRepository users,
        ICipher cipher, byte[]? cookieKey, string timezone)
    {
        this.store = store;
        this.users = users;
        this.cipher = cipher;
        this.cookieKey = cookieKey;
        this.timezone = timezone;
        keys = new OAuthKeys(storageDirectory);
    }

    public OAuthStore Store => store;

    public OAuthKeys Keys => keys;

    // ---------------------------------------------------------------- scopes

    /// <summary>默认作用域 + <c>scopes</c> 表（插件自定义）。</summary>
    public IReadOnlyDictionary<string, string> Scopes
    {
        get
        {
            Dictionary<string, string>? cached = _scopes;
            if (cached is not null)
            {
                return cached;
            }

            var merged = new Dictionary<string, string>(DefaultScopes, StringComparer.Ordinal);
            try
            {
                foreach ((string name, string description) in store.Database.Query(
                             "SELECT name, description FROM scopes",
                             reader => (DbRead.Text(reader, 0),
                                 reader.IsDBNull(1) ? "" : DbRead.Text(reader, 1))))
                {
                    merged[name] = description;
                }
            }
            catch (System.Data.Common.DbException)
            {
                // 未安装 / 表不存在：只返回默认作用域
            }

            _scopes = merged;
            return merged;
        }
    }

    public List<Dictionary<string, object?>> ScopeJson() =>
        Scopes.Select(entry => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = entry.Key,
            ["description"] = entry.Value,
        }).ToList();

    public bool HasScope(string scope) => Scopes.ContainsKey(scope);

    // ---------------------------------------------------------------- 签发

    public sealed record Issued(string AccessToken, string? RefreshToken, string TokenId,
        long ExpiresIn);

    /// <summary>签发 access token（可选 refresh token）。</summary>
    public Issued Issue(int? userId, long clientId, string? name, List<string> scopes,
        bool withRefresh)
    {
        string tokenId = RandomHex(40);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = now + TokenTtlSeconds;
        store.InsertToken(tokenId, userId, clientId, name, scopes,
            PhpDate.At(exp * 1000, timezone));

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["aud"] = new List<string> { clientId.ToString() },
            ["jti"] = tokenId,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = exp,
            ["sub"] = userId?.ToString() ?? clientId.ToString(),
            ["scopes"] = scopes,
        };
        string accessToken = Jwt.Rs256(claims, keys);

        string? refresh = null;
        if (withRefresh)
        {
            refresh = RandomHex(40);
            store.InsertRefreshToken(refresh, tokenId, PhpDate.At(exp * 1000, timezone));
        }

        return new Issued(accessToken, refresh, tokenId, TokenTtlSeconds);
    }

    /// <summary>把 PHP 数组语义的 <see cref="PhpArray"/> 或 <see cref="IEnumerable{T}"/> 拍平。</summary>
    public static List<string> StringList(object? value)
    {
        var output = new List<string>();
        switch (value)
        {
            case PhpArray array:
                foreach (object? item in array.Values)
                {
                    output.Add(PhpString.Cast(item));
                }

                break;
            case System.Collections.IEnumerable enumerable and not string:
                foreach (object? item in enumerable)
                {
                    output.Add(PhpString.Cast(item));
                }

                break;
        }

        return output;
    }

    public static string RandomHex(int bytes)
    {
        byte[] buffer = RandomNumberGenerator.GetBytes(bytes);
        var sb = new StringBuilder(bytes * 2);
        foreach (byte b in buffer)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 认证

    /// <summary>认证结果：用户 / 客户端 / 作用域 / token id。</summary>
    public sealed record AuthResult(UserRepository.UserRow? User, long? ClientId,
        List<string> Scopes, string? TokenId)
    {
        public bool Can(string scope) =>
            Scopes.Contains("*") || Scopes.Contains(scope);
    }

    /// <summary>Bearer token 或 Passport transient cookie 认证；失败返回 null。</summary>
    public AuthResult? Authenticate(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            string jwt = authorization[6..].Trim();
            PhpArray? claims = Jwt.DecodeRs256(jwt, keys);
            if (claims is null)
            {
                return null;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Long(claims.Get("exp")) is long exp && exp < now)
            {
                return null;
            }

            if (Long(claims.Get("nbf")) is long nbf && nbf > now)
            {
                return null;
            }

            string? sub = claims.Get("sub")?.ToString();
            string? jti = claims.Get("jti")?.ToString();
            if (sub is null || jti is null)
            {
                return null;
            }

            try
            {
                OAuthStore.Token? token = store.FindToken(jti);
                if (token is null || token.Revoked)
                {
                    return null;
                }

                List<string> audiences = StringList(claims.Get("aud"));
                long? clientId = audiences.Count > 0 && long.TryParse(audiences[0], out long parsed)
                    ? parsed
                    : null;
                List<string> scopes = StringList(claims.Get("scopes"));

                UserRepository.UserRow? user = null;
                if (int.TryParse(sub, out int uid))
                {
                    try
                    {
                        user = users.FindById(uid);
                    }
                    catch (System.Data.Common.DbException)
                    {
                        user = null;
                    }
                }

                return new AuthResult(user, clientId, scopes, jti);
            }
            catch (System.Data.Common.DbException)
            {
                return null;
            }
        }

        if (cookieKey is not null
            && context.Request.Cookies.TryGetValue(CookieName, out string? cookie)
            && !string.IsNullOrEmpty(cookie))
        {
            PhpArray? claims = Jwt.DecodeHs256(cookie, cookieKey);
            if (claims is null)
            {
                return null;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Long(claims.Get("exp")) is long exp && exp < now)
            {
                return null;
            }

            string? csrf = context.Request.Headers["X-CSRF-TOKEN"].ToString();
            if (string.IsNullOrEmpty(csrf))
            {
                csrf = context.Request.Headers["X-XSRF-TOKEN"].ToString();
            }

            if (string.IsNullOrEmpty(csrf)
                || !string.Equals(csrf, claims.Get("csrf")?.ToString(), StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                if (!int.TryParse(claims.Get("sub")?.ToString(), out int uid))
                {
                    return null;
                }

                UserRepository.UserRow? user = users.FindById(uid);
                return user is null ? null : new AuthResult(user, null, ["*"], null);
            }
            catch (System.Data.Common.DbException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary><c>ApiTokenCookieFactory::make()</c>（HS256 + session 有效期）。</summary>
    public (string Value, int MaxAgeSeconds) TransientCookie(int userId, string csrf,
        int lifetimeMinutes)
    {
        if (cookieKey is null)
        {
            return ("", 0);
        }

        long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (lifetimeMinutes * 60L);
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sub"] = userId,
            ["csrf"] = csrf,
            ["exp"] = exp,
        };
        string jwt = Jwt.Hs256(claims, cookieKey);
        return (jwt, lifetimeMinutes * 60);
    }

    private static long? Long(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => (long)d,
        string s when long.TryParse(s, out long parsed) => parsed,
        _ => null,
    };
}
