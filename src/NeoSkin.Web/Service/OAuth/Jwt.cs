using System.Security.Cryptography;
using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service.OAuth;

/// <summary>
/// JWT 的最小移植：RS256（access token）与 HS256（Passport 的 transient cookie）。
///
/// <p>与 Java 版 {@code Jwts} 同构；claims 用 <see cref="PhpJson"/> 编码，
/// 因为 token 里的 <c>aud</c> / <c>scopes</c> 是 PHP 数组语义。</p>
/// </summary>
public static class Jwt
{
    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };
        return Convert.FromBase64String(padded);
    }

    /// <summary>RS256 编码。</summary>
    public static string Rs256(IReadOnlyDictionary<string, object?> claims, OAuthKeys keys)
    {
        var header = new PhpArray().With("typ", "JWT").With("alg", "RS256");
        string head = B64(Encoding.UTF8.GetBytes(PhpJson.Encode(header, 0)));
        string body = B64(Encoding.UTF8.GetBytes(PhpJson.Encode(claims, 0)));
        byte[] signingInput = Encoding.UTF8.GetBytes(head + "." + body);

        return head + "." + body + "." + B64(keys.Sign(signingInput));
    }

    /// <summary>HS256 编码。</summary>
    public static string Hs256(IReadOnlyDictionary<string, object?> claims, byte[] key)
    {
        var header = new PhpArray().With("typ", "JWT").With("alg", "HS256");
        string head = B64(Encoding.UTF8.GetBytes(PhpJson.Encode(header, 0)));
        string body = B64(Encoding.UTF8.GetBytes(PhpJson.Encode(claims, 0)));
        string input = head + "." + body;

        return input + "." + B64(Hmac(Encoding.UTF8.GetBytes(input), key));
    }

    /// <summary>校验并解析 RS256；签名/格式错误返回 null（exp/nbf 由调用方检查）。</summary>
    public static PhpArray? DecodeRs256(string? token, OAuthKeys keys)
    {
        string[]? parts = Split(token);
        if (parts is null)
        {
            return null;
        }

        byte[] signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
        byte[] signature;
        try
        {
            signature = UnB64(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!keys.Verify(signingInput, signature))
        {
            return null;
        }

        return DecodePayload(parts[1]);
    }

    /// <summary>校验并解析 HS256。</summary>
    public static PhpArray? DecodeHs256(string? token, byte[] key)
    {
        string[]? parts = Split(token);
        if (parts is null)
        {
            return null;
        }

        byte[] expected = Hmac(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), key);
        byte[] actual;
        try
        {
            actual = UnB64(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return null;
        }

        return DecodePayload(parts[1]);
    }

    private static string[]? Split(string? token)
    {
        if (token is null)
        {
            return null;
        }

        string[] parts = token.Split('.');
        return parts.Length == 3 ? parts : null;
    }

    private static PhpArray? DecodePayload(string body)
    {
        try
        {
            object? decoded = PhpJson.Decode(Encoding.UTF8.GetString(UnB64(body)));
            return decoded as PhpArray;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Hmac(byte[] data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }
}
