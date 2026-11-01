using System.Security.Cryptography;
using System.Text;

namespace NeoSkin.Web.Service;

/// <summary>
/// 登录失败计数器（源 <c>Cache::get(sha1('login_fails_'.$ip))</c>）。
///
/// <p>未注入缓存时退回进程内计数器（测试/无缓存目录场景）；生产由组合根注入
/// file/redis 缓存（后续接入）。</p>
/// </summary>
public sealed class LoginFailCounter
{
    /// <summary>源：<c>Cache::put($key, $loginFails, 3600)</c>。</summary>
    public const int TtlSeconds = 3600;

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary><c>sha1('login_fails_'.$ip)</c>。</summary>
    public static string KeyForIp(string? ip) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("login_fails_" + (ip ?? ""))))
            .ToLowerInvariant();

    /// <summary><c>cache($key)</c> 的整数语义（未记录为 0）。</summary>
    public int Get(string key)
    {
        lock (_lock)
        {
            return _counts.TryGetValue(key, out int value) ? value : 0;
        }
    }

    /// <summary><c>cache($key)</c> 自增。</summary>
    public int Increment(string key)
    {
        lock (_lock)
        {
            int value = (_counts.TryGetValue(key, out int existing) ? existing : 0) + 1;
            _counts[key] = value;
            return value;
        }
    }

    /// <summary><c>Cache::forget($key)</c>（登录成功后清计数）。</summary>
    public void Forget(string key)
    {
        lock (_lock)
        {
            _counts.Remove(key);
        }
    }

    /// <summary>清空进程内计数（测试用）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _counts.Clear();
        }
    }
}
