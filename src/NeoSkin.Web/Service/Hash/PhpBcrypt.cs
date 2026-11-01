using System.Security.Cryptography;
using System.Text;
using BC = BCrypt.Net.BCrypt;

namespace NeoSkin.Web.Service.Hash;

/// <summary>
/// PHP bcrypt 的归一化与哈希（<b>bcrypt 专用</b>）。
///
/// <h2>归一化规则（顺序不可颠倒）</h2>
/// <list type="number">
///   <item>在<b>第一个 NUL 字节</b>处截断（<c>crypt()</c> 收到的是 C 字符串）；</item>
///   <item>剩下的按<b>字节</b>截断到 72（Blowfish 密钥调度只吃 72 字节）。</item>
/// </list>
///
/// <p>第 2 步按字节而不是按字符：<c>68 个 'x' + 3 个 '中'</c>（77 字节）截断后是
/// 「68 个 x + 一个完整中 + 一个中的首字节」，产生非法 UTF-8。BCrypt.Net-Next
/// 内部就是对 UTF-8 字节数组做 72 字节截断，所以这里<b>不</b>再按字符边界裁剪，
/// 否则会算出不同的哈希（语料 <c>BCRYPT.normalize.splitMultibyte</c> 锁定）。</p>
///
/// <h2><c>crypt()</c> 与 <c>password_hash()</c> 在 NUL 上的分歧</h2>
/// <list type="bullet">
///   <item><c>crypt("ab\0cd", $salt)</c> → 正常返回（等价于 <c>crypt("ab")</c>）；</item>
///   <item><c>password_hash("ab\0cd", PASSWORD_BCRYPT)</c> → ValueError
///     <c>Bcrypt password must not contain null character</c>。</item>
/// </list>
/// <para>因此 <see cref="Hash"/> 拒绝 NUL，而 <see cref="Verify"/> / <see cref="HashWithSalt"/>
/// 容忍 NUL（截断）。</para>
/// </summary>
public static class PhpBcrypt
{
    public const int MinCost = 4;
    public const int MaxCost = 31;

    /// <summary>PHP 的 <c>password_hash()</c> 输出（<c>$2y$</c>），NUL 直接拒绝。</summary>
    public static string Hash(string? value, int cost)
    {
        if (value is not null && value.Contains('\0'))
        {
            throw new ArgumentException("Bcrypt password must not contain null character");
        }

        int rounds = Math.Clamp(cost, MinCost, MaxCost);
        return BC.HashPassword(value ?? "", BC.GenerateSalt(rounds, 'y'));
    }

    /// <summary>
    /// 用指定盐生成哈希，等价于 <c>crypt($value, $salt)</c>。只用于测试与语料对拍。
    /// </summary>
    public static string HashWithSalt(string? value, string salt) =>
        BC.HashPassword(TruncateAtNul(value), salt);

    /// <summary>校验 bcrypt；<paramref name="hash"/> 必须是 60 字节且以 <c>$2</c> 开头。</summary>
    public static bool Verify(string? password, string hash)
    {
        string normalizedHash = NormalizeHashRevision(hash);
        try
        {
            return BC.Verify(TruncateAtNul(password), normalizedHash);
        }
        catch (ArgumentException)
        {
            // 载荷含非法 base64 字符等 → PHP 的 crypt 返回 "*0"，即不匹配
            return false;
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    /// <summary>
    /// PHP 的密码归一化：按第一个 NUL 截断。72 字节截断由 BCrypt.Net-Next 在
    /// UTF-8 字节上完成（与 PHP 一致，可能劈开多字节字符）。
    /// </summary>
    public static string Normalize(string? password) => TruncateAtNul(password);

    private static string TruncateAtNul(string? value)
    {
        if (value is null)
        {
            return "";
        }

        int nul = value.IndexOf('\0', StringComparison.Ordinal);
        return nul < 0 ? value : value[..nul];
    }

    /// <summary>
    /// <c>$2x$</c> 是 PHP 7.4 之前的「有符号扩展 bug」变体；BCrypt.Net-Next 不认这个 minor。
    /// 对 ASCII 密码，<c>2x</c> 与 <c>2a</c> 结果相同，所以这里改写成 <c>2a</c> 再校验。
    /// </summary>
    private static string NormalizeHashRevision(string hash) =>
        hash.StartsWith("$2x$", StringComparison.Ordinal) ? "$2a$" + hash[4..] : hash;

    /// <summary>生成随机 salt（bcrypt base64），测试/诊断用。</summary>
    public static string GenerateSalt(int cost) => BC.GenerateSalt(Math.Clamp(cost, MinCost, MaxCost), 'y');

    /// <summary>常量时间比较（与 PHP 的 <c>hash_equals</c> 等价）。</summary>
    internal static bool FixedTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}
