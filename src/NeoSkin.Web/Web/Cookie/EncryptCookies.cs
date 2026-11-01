using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Cookie;

/// <summary>
/// Laravel <c>Illuminate\Cookie\Middleware\EncryptCookies</c> 的「值层」移植。
///
/// <p>语料：<c>cookie.json</c> 的 <c>encryptcookies_*</c>。</p>
///
/// <list type="bullet">
///   <item>解密抛 <see cref="DecryptException"/> → 该 cookie 置 null，<b>不影响其他 cookie</b>；
///     数组 cookie 只要一个子元素失败就整体置 null；</item>
///   <item>解出来的值要过 <see cref="CookieValuePrefix.Validate"/>，前缀对不上也是 null；</item>
///   <item>响应端加密值 = <c>create(name, key) + value</c>。</item>
/// </list>
/// </summary>
public sealed class EncryptCookies(PhpEncrypter encrypter)
{
    /// <summary>解密后的 cookie 在请求上下文里的存取键。</summary>
    public const string ContextKey = "decrypted_cookies";

    private static readonly List<string> NeverEncrypt = [];

    /// <summary>本应用 cookie 不序列化（<c>config('encrypt_cookies.serialize') = false</c>）。</summary>
    private static bool _serialize;

    private readonly List<string> _except = [];

    public PhpEncrypter Encrypter => encrypter;

    /// <summary><c>disableFor()</c>：本实例内给某个 cookie 关加密。</summary>
    public void DisableFor(string name) => _except.Add(name);

    /// <summary><c>isDisabled()</c>：实例 except ∪ 全局 neverEncrypt。</summary>
    public bool IsDisabled(string name) => _except.Contains(name) || NeverEncrypt.Contains(name);

    /// <summary><c>EncryptCookies::except()</c>：全局永不加密名单。</summary>
    public static void Except(IEnumerable<string> cookies)
    {
        foreach (string cookie in cookies)
        {
            if (!NeverEncrypt.Contains(cookie))
            {
                NeverEncrypt.Add(cookie);
            }
        }
    }

    /// <summary><c>serialized()</c>：本应用恒 false。</summary>
    public static bool Serialized(string name) => _serialize;

    /// <summary><c>flushState()</c>：静态状态复位（测试切用例用）。</summary>
    public static void FlushState()
    {
        NeverEncrypt.Clear();
        _serialize = false;
    }

    // ================================================================== 请求侧

    /// <summary><c>decrypt()</c>：逐个 cookie 解密 + 验前缀。</summary>
    public PhpArray Decrypt(PhpArray cookies)
    {
        var result = new PhpArray();
        foreach (KeyValuePair<object, object?> entry in cookies)
        {
            string name = PhpString.Cast(entry.Key);
            if (IsDisabled(name))
            {
                continue;
            }

            try
            {
                result.Put(name, ValidateValue(name, DecryptCookie(name, entry.Value)));
            }
            catch (DecryptException)
            {
                result.Put(name, null);
            }
        }

        return result;
    }

    /// <summary><c>validateValue()</c>：数组按键下标（<c>key[index]</c>）逐项递归。</summary>
    private object? ValidateValue(string key, object? value)
    {
        if (value is PhpArray array)
        {
            var validated = new PhpArray();
            foreach (KeyValuePair<object, object?> entry in array)
            {
                validated.Put(entry.Key, ValidateValue(key + "[" + PhpString.Cast(entry.Key) + "]",
                    entry.Value));
            }

            return validated;
        }

        return CookieValuePrefix.Validate(key, value as string, encrypter.AllKeys);
    }

    /// <summary><c>decryptCookie()</c>：数组 cookie 逐项解密。</summary>
    private object? DecryptCookie(string name, object? cookie)
    {
        if (cookie is PhpArray array)
        {
            var decrypted = new PhpArray();
            foreach (KeyValuePair<object, object?> entry in array)
            {
                string subKey = PhpString.Cast(entry.Key);
                if (entry.Value is string s)
                {
                    decrypted.Put(subKey, encrypter.Decrypt(s, Serialized(subKey)));
                }
                else if (entry.Value is PhpArray nested)
                {
                    decrypted.Put(subKey, DecryptCookie(subKey, nested));
                }
            }

            return decrypted;
        }

        return encrypter.Decrypt((string)cookie!, Serialized(name));
    }

    // ================================================================== 响应侧

    /// <summary><c>encrypt()</c>：加密 <c>create(name, key) + value</c>。</summary>
    public string Encrypt(string name, string value)
    {
        string prefixed = CookieValuePrefix.Create(name, encrypter.Key) + value;
        return encrypter.Encrypt(prefixed, Serialized(name));
    }
}
