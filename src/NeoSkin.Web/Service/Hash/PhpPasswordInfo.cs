using System.Globalization;

namespace NeoSkin.Web.Service.Hash;

/// <summary>
/// PHP <c>password_get_info()</c> / <c>password_needs_rehash()</c> 的等价实现。
///
/// <p>三个函数的 bcrypt 判定不同（语料 <c>hash.json</c> 锁定）：</p>
/// <list type="table">
///   <item><term>password_verify</term><description><c>$2a$ $2b$ $2x$ $2y$</c>，长度 60，cost 4..31</description></item>
///   <item><term>password_get_info</term><description>只认 <c>$2y$</c>，长度 60，不检查 cost</description></item>
///   <item><term>password_needs_rehash</term><description>同 get_info，再比较 cost</description></item>
/// </list>
///
/// <p>直接后果：<c>$2a$12$…</c> 能通过 <c>Hash::check()</c>，但 <c>Hash::isHashed()</c>
/// 会说「不是哈希」，于是被重新哈希成 <c>$2y$10$…</c>。这是 PHP 的真实行为。</p>
///
/// <p>cost 的解析是 <c>sscanf(substr($hash, 4), "%ld", $cost)</c> 的 C 语义：
/// 跳过前导空白、可选符号、遇非数字停、读不到保持默认 12、溢出饱和、不要求后缀 <c>$</c>。</p>
/// </summary>
public static class PhpPasswordInfo
{
    public const string Bcrypt = "2y";
    public const string Argon2I = "argon2i";
    public const string Argon2Id = "argon2id";

    /// <summary>cost 段解析失败时的默认值（PHP 8.4+ 的 PASSWORD_BCRYPT 默认 cost）。</summary>
    public const long BcryptDefaultCost = 12;

    public const int BcryptLength = 60;

    public const long Argon2DefaultMemory = 65536;
    public const long Argon2DefaultTime = 4;
    public const long Argon2DefaultThreads = 1;

    private const string BcryptPrefix = "$2y$";

    /// <summary><c>password_get_info($hash)['algo']</c>；无法识别返回 null。</summary>
    public static string? Algorithm(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        if (hash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            return Argon2Id;
        }

        if (hash.StartsWith("$argon2i$", StringComparison.Ordinal))
        {
            return Argon2I;
        }

        return IsBcryptShaped(hash) ? Bcrypt : null;
    }

    /// <summary><c>password_get_info($hash)['algoName']</c>。</summary>
    public static string AlgorithmName(string? hash)
    {
        string? algorithm = Algorithm(hash);
        return algorithm switch
        {
            null => "unknown",
            Bcrypt => "bcrypt",
            _ => algorithm,
        };
    }

    /// <summary><c>password_get_info()</c> 认不认这个串（<c>Hash::isHashed()</c>）。</summary>
    public static bool IsHashed(string? hash) => Algorithm(hash) is not null;

    private static bool IsBcryptShaped(string? hash) =>
        hash is not null && hash.Length == BcryptLength
        && hash.StartsWith(BcryptPrefix, StringComparison.Ordinal);

    /// <summary>按 PHP 规则抽取 bcrypt cost（<c>sscanf("%ld")</c> 语义）。</summary>
    public static long BcryptCost(string? hash)
    {
        if (hash is null)
        {
            return BcryptDefaultCost;
        }

        return ScanLong(hash, 4)?.Value ?? BcryptDefaultCost;
    }

    /// <summary><c>password_needs_rehash($hash, PASSWORD_BCRYPT, ['cost' =&gt; $cost])</c>。</summary>
    public static bool NeedsRehash(string? hash, long cost) =>
        !IsBcryptShaped(hash) || BcryptCost(hash) != cost;

    public sealed record Argon2Options(long MemoryCost, long TimeCost, long Threads);

    /// <summary><c>password_get_info($hash)['options']</c> 里的 argon2 部分（逐段扫描）。</summary>
    public static Argon2Options Argon2OptionsOf(string? hash)
    {
        long memory = Argon2DefaultMemory;
        long time = Argon2DefaultTime;
        long threads = Argon2DefaultThreads;
        if (hash is null)
        {
            return new Argon2Options(memory, time, threads);
        }

        int i;
        if (hash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            i = "$argon2id$".Length;
        }
        else if (hash.StartsWith("$argon2i$", StringComparison.Ordinal))
        {
            i = "$argon2i$".Length;
        }
        else
        {
            return new Argon2Options(memory, time, threads);
        }

        if (!hash.AsSpan(i).StartsWith("v=", StringComparison.Ordinal))
        {
            return new Argon2Options(memory, time, threads);
        }

        i += 2;
        (long Value, int End)? version = ScanLong(hash, i);
        if (version is null)
        {
            return new Argon2Options(memory, time, threads);
        }

        i = version.Value.End;
        if (i >= hash.Length || hash[i] != '$')
        {
            return new Argon2Options(memory, time, threads);
        }

        i++;

        if (!hash.AsSpan(i).StartsWith("m=", StringComparison.Ordinal))
        {
            return new Argon2Options(memory, time, threads);
        }

        i += 2;
        (long Value, int End)? mem = ScanLong(hash, i);
        if (mem is null)
        {
            return new Argon2Options(memory, time, threads);
        }

        memory = mem.Value.Value;
        i = mem.Value.End;

        if (!hash.AsSpan(i).StartsWith(",t=", StringComparison.Ordinal))
        {
            return new Argon2Options(memory, time, threads);
        }

        i += 3;
        (long Value, int End)? t = ScanLong(hash, i);
        if (t is null)
        {
            return new Argon2Options(memory, time, threads);
        }

        time = t.Value.Value;
        i = t.Value.End;

        if (!hash.AsSpan(i).StartsWith(",p=", StringComparison.Ordinal))
        {
            return new Argon2Options(memory, time, threads);
        }

        i += 3;
        (long Value, int End)? p = ScanLong(hash, i);
        if (p is null)
        {
            return new Argon2Options(memory, time, threads);
        }

        threads = p.Value.Value;
        return new Argon2Options(memory, time, threads);
    }

    /// <summary>
    /// 复刻 C 的 <c>strtol</c> / <c>sscanf("%ld")</c> 首次转换：跳过前导空白、
    /// 可选符号、读十进制、遇非数字停、溢出饱和。<paramref name="from"/> 起没读到数字返回 null。
    /// </summary>
    internal static (long Value, int End)? ScanLong(string s, int from)
    {
        int i = from;
        int n = s.Length;
        while (i < n && IsSpace(s[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < n && (s[i] == '+' || s[i] == '-'))
        {
            negative = s[i] == '-';
            i++;
        }

        int digitsStart = i;
        long value = 0;
        bool overflow = false;
        while (i < n)
        {
            char c = s[i];
            if (c is < '0' or > '9')
            {
                break;
            }

            int digit = c - '0';
            if (!overflow)
            {
                // 用负数域累加，才能表示 |long.MinValue|
                if (value < (long.MinValue + digit) / 10)
                {
                    overflow = true;
                }
                else
                {
                    value = (value * 10) - digit;
                }
            }

            i++;
        }

        if (i == digitsStart)
        {
            return null;
        }

        long result;
        if (overflow)
        {
            result = negative ? long.MinValue : long.MaxValue;
        }
        else if (negative)
        {
            result = value;
        }
        else if (value == long.MinValue)
        {
            result = long.MaxValue;
        }
        else
        {
            result = -value;
        }

        return (result, i);
    }

    private static bool IsSpace(char c) =>
        c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    internal static string LongToString(long value) => value.ToString(CultureInfo.InvariantCulture);
}
