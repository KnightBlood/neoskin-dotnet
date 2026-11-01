using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;

namespace NeoSkin.Web.Service.Hash;

/// <summary>
/// PHP <c>password_verify()</c> 的等价实现，<b>算法无关</b>。
///
/// <p><c>password_verify()</c> 不看「当前配置的算法」，只看哈希串本身的格式：
/// 把 <c>PWD_METHOD</c> 从 BCRYPT 改成 ARGON2I 后，库里原有的 <c>$2y$</c> 哈希依然能登录，
/// 反之亦然（语料 <c>cipher.json</c> 的 <c>*.cross.*</c> 用例锁定）。</p>
///
/// <p>bcrypt 接受面：长度正好 60、<c>$2</c> + minor(<c>a b x y</c>) + <c>$</c>、
/// cost 4..31；密码按 PHP 规则归一化。<c>$2x$</c> 在 .NET 侧按 <c>$2a$</c> 处理
/// （见 <see cref="PhpBcrypt"/> 的说明）。</p>
///
/// <p>argon2 接受面：<c>$argon2i$</c> / <c>$argon2id$</c>，版本只支持 19（1.3），
/// 参数与输出长度都取自哈希串，base64 必须无填充。</p>
/// </summary>
public static class PhpPasswordVerify
{
    private const int BcryptLength = 60;
    private const int MinCost = 4;
    private const int MaxCost = 31;

    /// <summary>bcrypt 结构：<c>$2&lt;minor&gt;$&lt;cost&gt;$&lt;53 字节载荷&gt;</c>。</summary>
    private static readonly Regex BcryptPattern = new(
        @"^\$2([abxy])\$([0-9]{2})\$.{53}$", RegexOptions.Singleline);

    /// <summary>argon2 结构；base64 段不允许 <c>=</c>。</summary>
    private static readonly Regex Argon2Pattern = new(
        @"^\$(argon2i|argon2id)\$v=(\d+)\$m=(\d+),t=(\d+),p=(\d+)\$([A-Za-z0-9+/]+)\$([A-Za-z0-9+/]+)$");

    private const int Argon2Version13 = 19;
    private const int MaxArgon2MemoryKib = 1 << 20;
    private const int MaxArgon2Time = 1024;
    private const int MaxArgon2Parallelism = 255;
    private const int MaxArgon2HashBytes = 1024;

    /// <summary>对应 <c>password_verify($password, $hash)</c>；任何格式问题返回 false。</summary>
    public static bool Verify(string? password, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        string plain = password ?? "";
        if (hash.StartsWith("$2", StringComparison.Ordinal))
        {
            return VerifyBcrypt(plain, hash);
        }

        if (hash.StartsWith("$argon2i$", StringComparison.Ordinal)
            || hash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            return VerifyArgon2(plain, hash);
        }

        // md5 / sha256 / 明文 / 其它算法：password_verify() 一律 false
        return false;
    }

    public static bool LooksLikeBcrypt(string? hash) => hash is not null && BcryptPattern.IsMatch(hash);

    private static bool VerifyBcrypt(string password, string hash)
    {
        if (hash.Length != BcryptLength)
        {
            return false;
        }

        Match match = BcryptPattern.Match(hash);
        if (!match.Success)
        {
            return false;
        }

        int cost = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (cost is < MinCost or > MaxCost)
        {
            return false;
        }

        return PhpBcrypt.Verify(password, hash);
    }

    private static bool VerifyArgon2(string password, string hash)
    {
        Match match = Argon2Pattern.Match(hash);
        if (!match.Success)
        {
            return false;
        }

        bool isId = match.Groups[1].Value == "argon2id";
        if (!int.TryParse(match.Groups[2].Value, out int version)
            || version != Argon2Version13)
        {
            // 版本 16（Argon2 1.0）当前库不支持；PHP 会计算 —— 记入 PORT-NOTES 偏差
            return false;
        }

        if (!int.TryParse(match.Groups[3].Value, out int memory)
            || !int.TryParse(match.Groups[4].Value, out int time)
            || !int.TryParse(match.Groups[5].Value, out int parallelism))
        {
            return false;
        }

        if (parallelism is < 1 or > MaxArgon2Parallelism)
        {
            return false;
        }

        if (memory < 8 * parallelism || memory > MaxArgon2MemoryKib)
        {
            return false;
        }

        if (time is < 1 or > MaxArgon2Time)
        {
            return false;
        }

        byte[]? salt = DecodePhpBase64(match.Groups[6].Value);
        byte[]? expected = DecodePhpBase64(match.Groups[7].Value);
        if (salt is null || expected is null || salt.Length < 8
            || expected.Length < 4 || expected.Length > MaxArgon2HashBytes)
        {
            return false;
        }

        try
        {
            byte[] actual = HashRaw(password, salt, memory, time, parallelism, isId, expected.Length);
            return PhpBcrypt.FixedTimeEquals(expected, actual);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            // 参数被库拒绝（如 memory 不是 2 的幂）→ PHP 也是不匹配
            return false;
        }
    }

    private static byte[] HashRaw(string password, byte[] salt, int memory, int time,
        int parallelism, bool isId, int length)
    {
        byte[] input = Encoding.UTF8.GetBytes(password);
        Argon2 argon2 = isId
            ? new Argon2id(input)
            : new Argon2i(input);
        argon2.Salt = salt;
        argon2.MemorySize = memory;
        argon2.Iterations = time;
        argon2.DegreeOfParallelism = parallelism;
        return argon2.GetBytes(length);
    }

    /// <summary>解码 PHP 的 base64：无填充的 standard alphabet。</summary>
    internal static byte[]? DecodePhpBase64(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }

        if (s.Length % 4 == 1)
        {
            return null;
        }

        int rem = s.Length % 4;
        string padded = rem == 0 ? s : s + new string('=', 4 - rem);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
