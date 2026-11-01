using System.Security.Cryptography;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// Laravel {@code UrlGenerator::temporarySignedRoute / hasValidSignature} 的移植。
///
/// <p>HMAC 密钥使用 {@code config('app.key')} 原字符串（含 {@code base64:} 前缀），
/// 与 Java 版 {@link com.neoskin.web.SignedUrl} 同构。</p>
///
/// <p>与 Java 版不同：这里不依赖 {@code RouteUrlGenerator}，而是直接按路径 + 查询串
/// 签名，因 .NET 侧尚未落地命名路由生成器（Phase 6 管理面板会补齐）。</p>
/// </summary>
public static class SignedUrl
{
    /// <summary>生成带临时签名的 URL：<c>{path}?{qs}&amp;expires={expires}&amp;signature={sig}</c>。</summary>
    public static string TemporarySignedRoute(string path, long expiryEpochSeconds,
        IReadOnlyDictionary<string, string> queryParams, string appKey)
    {
        var all = new Dictionary<string, string>(queryParams, StringComparer.Ordinal)
        {
            ["expires"] = expiryEpochSeconds.ToString(),
        };

        return Sign(path, all, appKey);
    }

    /// <summary>生成不带 <c>expires</c> 的签名 URL（源 {@code URL::signedRoute(name, params, null, false)}）。</summary>
    public static string SignedRoute(string path, IReadOnlyDictionary<string, string> queryParams,
        string appKey) => Sign(path, queryParams, appKey);

    private static string Sign(string path, IReadOnlyDictionary<string, string> queryParams,
        string appKey)
    {
        // 与 Java 版/源站一致：参数按 key 排序后再签名
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in queryParams)
        {
            sorted[entry.Key] = entry.Value;
        }

        string query = BuildQueryString(sorted, encode: true);
        string signed = query.Length == 0 ? path : path + "?" + query;
        string signature = HmacSha256Hex(signed, appKey);

        var withSig = new SortedDictionary<string, string>(sorted, StringComparer.Ordinal)
        {
            ["signature"] = signature,
        };
        return path + "?" + BuildQueryString(withSig, encode: true);    }

    /// <summary>验证请求签名（与源站 {@code hasValidSignature} 语义一致）。</summary>
    public static bool HasValidSignature(string requestPath,
        IReadOnlyDictionary<string, string> queryParams, long nowEpochSeconds,
        string appKey, IEnumerable<string>? previousKeys = null)
    {
        if (!queryParams.TryGetValue("signature", out string? signature)
            || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        if (queryParams.TryGetValue("expires", out string? expiresRaw))
        {
            if (!long.TryParse(expiresRaw, out long expires))
            {
                return false;
            }

            if (nowEpochSeconds > expires)
            {
                return false;
            }
        }

        // 重建原 URL：去掉 signature，其余查询参数按 key 排序后 urlencode 拼在路径后
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in queryParams)
        {
            if (entry.Key.Equals("signature", StringComparison.Ordinal))
            {
                continue;
            }

            sorted[entry.Key] = entry.Value;
        }

        var qs = new StringBuilder();
        bool first = true;
        foreach (KeyValuePair<string, string> entry in sorted)
        {
            if (!first)
            {
                qs.Append('&');
            }

            first = false;
            qs.Append(RawUrlEncode(entry.Key)).Append('=').Append(RawUrlEncode(entry.Value));
        }

        string path = requestPath.StartsWith("/") ? requestPath : "/" + requestPath;
        string original = path + (qs.Length == 0 ? "" : "?" + qs);
        while (original.EndsWith('?'))
        {
            original = original[..^1];
        }

        var keys = new List<string>();
        if (!string.IsNullOrEmpty(appKey))
        {
            keys.Add(appKey);
        }

        if (previousKeys is not null)
        {
            keys.AddRange(previousKeys);
        }

        foreach (string key in keys)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(HmacSha256Hex(original, key)),
                    Encoding.UTF8.GetBytes(signature)))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string> query, bool encode)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (KeyValuePair<string, string> entry in query)
        {
            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(encode ? RawUrlEncode(entry.Key) : entry.Key)
              .Append('=')
              .Append(encode ? RawUrlEncode(entry.Value) : entry.Value);
        }

        return sb.ToString();
    }

    private static string HmacSha256Hex(string data, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key ?? ""));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>PHP {@code rawurlencode()}：保留 RFC 3986 unreserved，空格为 {@code %20}</summary>
    private static string RawUrlEncode(string? s)
    {
        if (s is null)
        {
            return "";
        }

        const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~";
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
                sb.Append('%').Append(((c >> 4) & 0xF).ToString("X1"))
                  .Append((c & 0xF).ToString("X1"));
            }
        }

        return sb.ToString();
    }
}
