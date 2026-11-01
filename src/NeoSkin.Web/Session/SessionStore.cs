using System.Security.Cryptography;
using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Session;

/// <summary>
/// Laravel <c>Illuminate\Session\Store</c> 的值层移植。
///
/// <p>载荷 <c>serialization=php</c>（改 json 会作废存量会话）：读路径是
/// <c>@unserialize</c>（失败当空包），写路径是 <c>serialize($attributes)</c>。</p>
///
/// <p>对齐的语义：<c>start()</c> 读 handler + 合并 + 保底 <c>_token</c>；
/// <c>isValidId()</c> = <c>ctype_alnum</c> 且长度 40；<c>save()</c> 先
/// <c>ageFlashData()</c>；<c>migrate()</c> 换 id 并重新生成 token。</p>
/// </summary>
public sealed class SessionStore
{
    public const int SessionIdLength = 40;

    public const string TokenKey = "_token";

    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly string _name;
    private readonly ISessionHandler _handler;
    private readonly PhpArray _attributes = new();

    private string _id = "";
    private bool _started;

    public SessionStore(string name, ISessionHandler handler)
    {
        _name = name;
        _handler = handler;
    }

    public string Name => _name;

    public string Id => _id;

    public bool Started => _started;

    public PhpArray Attributes => _attributes;

    /// <summary>从 cookie id 起步（<c>StartSession</c> 的 getSession）。</summary>
    public static SessionStore Begin(string name, ISessionHandler handler, string? cookieId) =>
        new SessionStore(name, handler).SetId(cookieId);

    // ------------------------------------------------------------- 生命周期

    public SessionStore Start()
    {
        LoadSession();
        if (!Has(TokenKey))
        {
            RegenerateToken();
        }

        _started = true;
        return this;
    }

    private void LoadSession()
    {
        string data = _handler.Read(_id);
        if (data.Length == 0)
        {
            return;
        }

        PhpUnserialize.Result result = PhpUnserialize.Parse(data);
        if (result.Value is not PhpArray array)
        {
            return;
        }

        foreach (KeyValuePair<object, object?> entry in array)
        {
            _attributes.Put(PhpString.Cast(entry.Key), entry.Value);
        }
    }

    /// <summary><c>save()</c>：闪存轮转 → serialize → handler.write。</summary>
    public void Save()
    {
        AgeFlashData();
        _handler.Write(_id, PhpSerialize.Serialize(_attributes));
        _started = false;
    }

    /// <summary><c>ageFlashData()</c>：forget(old) → old←new → new←[]。</summary>
    private void AgeFlashData()
    {
        if (_attributes.Get("_flash.old") is PhpArray oldKeys)
        {
            foreach (object? key in oldKeys.Values)
            {
                _attributes.Remove(PhpString.Cast(key));
            }
        }

        if (_attributes.Get("_flash.new") is PhpArray freshKeys)
        {
            _attributes.Put("_flash.old", freshKeys);
        }

        _attributes.Put("_flash.new", new PhpArray());
    }

    /// <summary><c>migrate($destroy)</c>：换 id（可选删旧文件）并重新生成 token。</summary>
    public SessionStore Migrate(bool destroy)
    {
        string? oldId = _id;
        if (destroy && !string.IsNullOrEmpty(oldId))
        {
            _handler.Destroy(oldId);
        }

        _id = GenerateSessionId();
        _started = false;
        _attributes.Clear();
        return Start();
    }

    public SessionStore SetId(string? newId)
    {
        _id = IsValidId(newId) ? newId! : GenerateSessionId();
        return this;
    }

    /// <summary><c>isValidId()</c>：字母数字且长度恰 40。</summary>
    public static bool IsValidId(string? candidateId)
    {
        if (candidateId is null || candidateId.Length != SessionIdLength)
        {
            return false;
        }

        foreach (char c in candidateId)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>Str::random(40)</c>：字母数字。</summary>
    public static string GenerateSessionId()
    {
        var sb = new StringBuilder(SessionIdLength);
        for (int i = 0; i < SessionIdLength; i++)
        {
            sb.Append(Alnum[RandomNumberGenerator.GetInt32(Alnum.Length)]);
        }

        return sb.ToString();
    }

    public object? Token() => Get(TokenKey);

    public SessionStore RegenerateToken()
    {
        Put(TokenKey, GenerateSessionId());
        return this;
    }

    // ------------------------------------------------------------- 存取

    public SessionStore Put(string key, object? value)
    {
        _attributes.Put(key, value);
        return this;
    }

    public object? Get(string key) => Get(key, null);

    public object? Get(string key, object? defaultValue) =>
        _attributes.ContainsKey(key) ? _attributes.Get(key) : defaultValue;

    public bool Has(string key) => _attributes.ContainsKey(key) && _attributes.Get(key) is not null;

    public SessionStore Forget(string key)
    {
        _attributes.Remove(key);
        return this;
    }

    /// <summary><c>flash(key, value)</c>：进 <c>_flash.new</c>。</summary>
    public SessionStore Flash(string key, object? value)
    {
        Put(key, value);
        PushInto("_flash.new", key);
        return this;
    }

    private void PushInto(string flashBag, string key)
    {
        if (_attributes.Get(flashBag) is not PhpArray bag)
        {
            bag = new PhpArray();
            _attributes.Put(flashBag, bag);
        }

        bag.Put((long)bag.Count, key);
    }

    // ------------------------------------------------------------- redirect

    public object? PreviousUrl() => Get("_previous.url");

    public SessionStore SetPreviousUrl(string url)
    {
        Put("_previous.url", url);
        return this;
    }

    public void Clear()
    {
        _attributes.Clear();
    }
}
