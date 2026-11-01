using System.Security.Cryptography;
using System.Text;

namespace NeoSkin.Web.Service.Crypto;

/// <summary>
/// Laravel <c>Illuminate\Cookie\CookieValuePrefix</c> 的移植。
///
/// <p>formula：<c>hash_hmac('sha1', $name . 'v2', $key) . '|'</c> —— 40 个小写十六进制
/// 字符 + <c>|</c>，共 41 字节。密钥是 APP_KEY base64 解码后的<b>原始字节</b>。</p>
///
/// <p>语料：<c>cookie.json</c> 的 <c>prefix_create.*</c> / <c>prefix_remove.*</c> /
/// <c>prefix_validate.*</c>。</p>
/// </summary>
public static class CookieValuePrefix
{
    public const int PrefixLength = 41;

    /// <summary><c>create()</c>。</summary>
    public static string Create(string cookieName, byte[] key) =>
        HmacSha1Hex(cookieName + "v2", key) + "|";

    /// <summary><c>remove()</c>：PHP 的 <c>substr($value, 41)</c> —— 不足 41 字节直接没了。</summary>
    public static string Remove(string? cookieValue) =>
        cookieValue is null || cookieValue.Length <= PrefixLength ? "" : cookieValue[PrefixLength..];

    /// <summary>
    /// <c>validate()</c>：前缀能对上就剥掉返回，否则 null。密钥轮换时任何一个 key
    /// 对上都算合法。
    /// </summary>
    public static string? Validate(string cookieName, string? cookieValue, IEnumerable<byte[]> keys)
    {
        if (cookieValue is null)
        {
            return null;
        }

        foreach (byte[] key in keys)
        {
            if (cookieValue.StartsWith(Create(cookieName, key), StringComparison.Ordinal))
            {
                return Remove(cookieValue);
            }
        }

        return null;
    }

    private static string HmacSha1Hex(string data, byte[] key) =>
        Convert.ToHexString(HMACSHA1.HashData(key, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
}
