using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP 的 URL 编码辅助函数：<c>rawurlencode()</c> / <c>urlencode()</c> /
/// <c>http_build_query()</c>。
///
/// <p>三者差别只在空格与转义范围：<c>rawurlencode</c> 空格是 <c>%20</c>，
/// <c>urlencode</c> 是 <c>+</c>；两者都只保留 RFC 3986 的 unreserved 字符
/// （<c>A-Za-z0-9-_.~</c>）。<c>Uri.EscapeDataString</c> 与 <c>urlencode</c>
/// 相近但不可依赖，所以这里按字节自己实现。</p>
/// </summary>
public static class PhpUrl
{
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~";

    private static readonly char[] Hex = "0123456789ABCDEF".ToCharArray();

    /// <summary>PHP 的 <c>rawurlencode($s)</c>：按 UTF-8 字节编码，空格是 <c>%20</c>。</summary>
    public static string RawUrlEncode(string? s)
    {
        if (s is null)
        {
            return "";
        }

        byte[] bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            int c = b;
            if (c < 0x80 && Unreserved.IndexOf((char)c) >= 0)
            {
                sb.Append((char)c);
            }
            else
            {
                sb.Append('%').Append(Hex[c >> 4]).Append(Hex[c & 0x0F]);
            }
        }

        return sb.ToString();
    }

    /// <summary>PHP 的 <c>urlencode($s)</c>：与 <see cref="RawUrlEncode"/> 相同，但空格是 <c>+</c>。</summary>
    public static string UrlEncode(string? s) => RawUrlEncode(s).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>PHP 的 <c>urldecode($s)</c>：<c>%XX</c> 与 <c>+</c> 都还原，非法序列原样保留。</summary>
    public static string DecodeComponent(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s ?? "";
        }

        byte[] raw = Encoding.UTF8.GetBytes(s);
        var output = new List<byte>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            int c = raw[i];
            if (c == '+')
            {
                output.Add((byte)' ');
                continue;
            }

            if (c == '%' && i + 2 < raw.Length)
            {
                int high = HexDigit(raw[i + 1]);
                int low = HexDigit(raw[i + 2]);
                if (high >= 0 && low >= 0)
                {
                    output.Add((byte)((high * 16) + low));
                    i += 2;
                    continue;
                }
            }

            output.Add((byte)c);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>PHP 的 <c>http_build_query($array)</c>：<c>null</c> 值跳过，键值各自 urlencode。</summary>
    public static string BuildQuery(System.Collections.IDictionary? query)
    {
        if (query is null || query.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (System.Collections.DictionaryEntry entry in query)
        {
            if (entry.Value is null)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(UrlEncode(PhpString.Cast(entry.Key)));
            sb.Append('=');
            sb.Append(UrlEncode(PhpString.Cast(entry.Value)));
        }

        return sb.ToString();
    }

    /// <summary>同样的逻辑，接受 <see cref="PhpArray"/>（服务端最常用）。</summary>
    public static string BuildQuery(PhpArray? query)
    {
        if (query is null || query.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (KeyValuePair<object, object?> entry in query)
        {
            if (entry.Value is null)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(UrlEncode(PhpString.Cast(entry.Key)));
            sb.Append('=');
            sb.Append(UrlEncode(PhpString.Cast(entry.Value)));
        }

        return sb.ToString();
    }

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
