using System.Security.Cryptography;
using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service.Crypto;

/// <summary>Laravel <c>EncryptException</c>。</summary>
public sealed class EncryptException(string message) : Exception(message);

/// <summary>Laravel <c>DecryptException</c>。</summary>
public sealed class DecryptException(string message) : Exception(message);

/// <summary>
/// Laravel <c>Illuminate\Encryption\Encrypter</c> 的移植（13.x 语义）。
///
/// <p>语料：<c>cookie.json</c> 的 <c>constructor.*</c> / <c>encrypt.*</c> / <c>decrypt.*</c> /
/// <c>appears_encrypted.*</c>（由真实的 PHP 类生成）。</p>
///
/// <h2>与 Laravel 12 之前的关键差别</h2>
/// <list type="bullet">
///   <item>MAC 直接用<b>原始密钥字节</b>：<c>hash_hmac('sha256', $ivB64 . $valueB64, $key)</c>，
///     两段都是 base64 <b>字符串</b>；</item>
///   <item>AES-GCM 的 IV 是 12 字节（CBC 是 16），tag 16 字节进 payload 的 <c>tag</c>，<c>mac</c> 为空；</item>
///   <item>CBC 用 PKCS7，payload 的 <c>value</c> 本身是 base64 字符串。</item>
/// </list>
/// </summary>
public sealed class PhpEncrypter
{
    private sealed record Spec(int KeySize, bool Aead, int IvSize);

    private static readonly Dictionary<string, Spec> Supported = new(StringComparer.Ordinal)
    {
        ["aes-128-cbc"] = new(16, false, 16),
        ["aes-256-cbc"] = new(32, false, 16),
        ["aes-128-gcm"] = new(16, true, 12),
        ["aes-256-gcm"] = new(32, true, 12),
    };

    private static readonly string[] CipherNames =
        ["aes-128-cbc", "aes-256-cbc", "aes-128-gcm", "aes-256-gcm"];

    private readonly byte[] _key;
    private readonly Spec _spec;
    private readonly List<byte[]> _previousKeys = [];

    public PhpEncrypter(byte[] key, string cipher)
    {
        _key = key;
        if (!Supported.TryGetValue(cipher.ToLowerInvariant(), out Spec? spec) || key.Length != spec.KeySize)
        {
            throw new Exception("Unsupported cipher or incorrect key length. Supported ciphers are: "
                + string.Join(", ", CipherNames) + ".");
        }

        _spec = spec;
    }

    /// <summary><c>Encrypter::supported()</c>。</summary>
    public static bool SupportedCipher(byte[] key, string cipher) =>
        Supported.TryGetValue(cipher.ToLowerInvariant(), out Spec? spec) && key.Length == spec.KeySize;

    public byte[] Key => _key;

    /// <summary><c>getAllKeys()</c>：主密钥在前，轮换密钥在后。</summary>
    public IReadOnlyList<byte[]> AllKeys => [_key, .. _previousKeys];

    /// <summary><c>previousKeys()</c>：轮换密钥由新到旧。</summary>
    public void SetPreviousKeys(params byte[][] keys)
    {
        _previousKeys.Clear();
        _previousKeys.AddRange(keys);
    }

    // ================================================================== 加密

    /// <summary><c>encrypt($value, $serialize)</c>（随机 IV）。</summary>
    public string Encrypt(object? value, bool serialize)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(_spec.IvSize);
        return EncryptWithIv(iv, value, serialize);
    }

    /// <summary>带固定 IV 的加密（语料入口）。</summary>
    public string EncryptWithIv(byte[] iv, object? value, bool serialize)
    {
        string plain = serialize ? PhpSerialize.Serialize(value) : (string)value!;
        byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
        byte[] cipherText;
        byte[]? tag = null;

        try
        {
            if (_spec.Aead)
            {
                cipherText = new byte[plainBytes.Length];
                tag = new byte[16];
                using var gcm = new AesGcm(_key, 16);
                gcm.Encrypt(iv, plainBytes, cipherText, tag);
            }
            else
            {
                using Aes aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _key;
                aes.IV = iv;
                using ICryptoTransform encryptor = aes.CreateEncryptor();
                cipherText = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            }
        }
        catch (CryptographicException)
        {
            throw new EncryptException("Could not encrypt the data.");
        }

        string ivB64 = Convert.ToBase64String(iv);
        string valueB64 = Convert.ToBase64String(cipherText);
        string mac = _spec.Aead ? "" : Mac(ivB64, valueB64, _key);
        string tagB64 = Convert.ToBase64String(tag ?? []);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload(ivB64, valueB64, mac, tagB64)));
    }

    // ================================================================== 解密

    /// <summary><c>decrypt($payload, $unserialize)</c>。</summary>
    public object? Decrypt(string payload, bool unserialize)
    {
        PhpArray parsed = GetJsonPayload(payload);
        string ivB64 = Text(parsed, "iv") ?? "";
        byte[]? iv = StrictBase64(ivB64);
        string? tagText = Text(parsed, "tag");
        byte[]? tag = string.IsNullOrEmpty(tagText) ? null : StrictBase64(tagText);
        EnsureTagIsValid(tag);

        string valueB64 = Text(parsed, "value") ?? "";
        byte[]? validKey = null;
        if (!_spec.Aead)
        {
            string mac = Text(parsed, "mac") ?? "";
            foreach (byte[] candidate in AllKeys)
            {
                if (Mac(ivB64, valueB64, candidate) == mac)
                {
                    validKey = candidate;
                    break;
                }
            }

            if (validKey is null)
            {
                throw new DecryptException("The MAC is invalid.");
            }
        }

        byte[]? plain = null;
        byte[] data = Convert.FromBase64String(valueB64);
        foreach (byte[] candidate in AllKeys)
        {
            if (validKey is not null && !ReferenceEquals(candidate, validKey))
            {
                continue;
            }

            try
            {
                plain = CipherDecrypt(data, candidate, iv!, tag);
                break;
            }
            catch (CryptographicException)
            {
                // openssl_decrypt 返回 false → 'Could not decrypt the data.'
            }
        }

        if (plain is null)
        {
            throw new DecryptException("Could not decrypt the data.");
        }

        string text = Encoding.UTF8.GetString(plain);
        if (!unserialize)
        {
            return text;
        }

        PhpUnserialize.Result result = PhpUnserialize.Parse(text);
        if (result.Warnings.Count > 0)
        {
            throw new ErrorException("unserialize(): " + result.FirstWarning);
        }

        return result.Value;
    }

    /// <summary><c>appearsEncrypted()</c>：只判结构（<c>isset()</c>，不管类型）。</summary>
    public static bool AppearsEncrypted(object? value)
    {
        if (value is not string s)
        {
            return false;
        }

        byte[]? decoded = StrictBase64(s);
        if (decoded is null)
        {
            return false;
        }

        PhpArray? payload = DecodeMap(decoded);
        return payload is not null
            && payload.ContainsKey("iv") && payload.Get("iv") is not null
            && payload.ContainsKey("value") && payload.Get("value") is not null
            && payload.ContainsKey("mac") && payload.Get("mac") is not null;
    }

    // ================================================================== 内部

    private PhpArray GetJsonPayload(string payload)
    {
        byte[]? bytes = LooseBase64(payload);
        PhpArray? map = bytes is null ? null : DecodeMap(bytes);
        if (map is null || !ValidPayload(map))
        {
            throw new DecryptException("The payload is invalid.");
        }

        return map;
    }

    private static PhpArray? DecodeMap(byte[] bytes)
    {
        try
        {
            return PhpJson.Decode(Encoding.UTF8.GetString(bytes)) as PhpArray;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool ValidPayload(PhpArray payload)
    {
        foreach (string item in new[] { "iv", "value", "mac" })
        {
            if (payload.Get(item) is not string)
            {
                return false;
            }
        }

        object? tag = payload.Get("tag");
        if (tag is not null and not string)
        {
            return false;
        }

        byte[]? iv = StrictBase64(Text(payload, "iv"));
        return iv is not null && iv.Length == _spec.IvSize;
    }

    private void EnsureTagIsValid(byte[]? tag)
    {
        if (_spec.Aead && (tag is null || tag.Length != 16))
        {
            throw new DecryptException("Could not decrypt the data.");
        }

        if (!_spec.Aead && tag is not null)
        {
            throw new DecryptException(
                "Unable to use tag because the cipher algorithm does not support AEAD.");
        }
    }

    private byte[] CipherDecrypt(byte[] data, byte[] aesKey, byte[] iv, byte[]? tag)
    {
        if (_spec.Aead)
        {
            var plain = new byte[data.Length];
            using var gcm = new AesGcm(aesKey, 16);
            gcm.Decrypt(iv, data, tag!, plain);
            return plain;
        }

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = aesKey;
        aes.IV = iv;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static string Mac(string ivB64, string valueB64, byte[] key) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ivB64 + valueB64)))
            .ToLowerInvariant();

    /// <summary>生成 <c>{iv,value,mac,tag}</c>：手动拼接，与 <c>JSON_UNESCAPED_SLASHES</c> 逐字节一致。</summary>
    private static string Payload(string iv, string value, string mac, string tag) =>
        "{\"iv\":\"" + iv + "\",\"value\":\"" + value + "\",\"mac\":\"" + mac
        + "\",\"tag\":\"" + tag + "\"}";

    private static string? Text(PhpArray map, string key) => map.Get(key) as string;

    private static byte[]? StrictBase64(string? s)
    {
        if (s is null)
        {
            return null;
        }

        if (s.Length == 0)
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>base64_decode($s, false)</c>：删掉所有非 base64 字符、截断到 <c>=</c> 之前、
    /// 补齐后解码；长度模 4 余 1 时返回 null（任何编码都不可能产生）。
    /// </summary>
    private static byte[]? LooseBase64(string? s)
    {
        if (s is null)
        {
            return null;
        }

        var filtered = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
            {
                filtered.Append(c);
            }
        }

        string body = filtered.ToString();
        int rest = body.Length % 4;
        if (rest == 1)
        {
            return null;
        }

        body += rest switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };

        try
        {
            return Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
