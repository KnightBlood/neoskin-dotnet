using System.Security.Cryptography;
using System.Text;
using NeoSkin.Web.Db;
using NeoSkin.Web.Session;

namespace NeoSkin.Web.Auth;

/// <summary>
/// Laravel <c>SessionGuard</c> 的会话键读写（<c>Auth::login()</c> / <c>Auth::user()</c>）。
///
/// <p>键名是 <c>login_web_</c> + <c>sha1("Illuminate\Auth\SessionGuard")</c>
/// = <c>login_web_59ba36addc2b2f9401580f014c7f58ea4e30989d</c>。
/// 写错一个字符，PHP 版与 .NET 版之间的已登录状态就不互通。</p>
/// </summary>
public sealed class SessionGuard(SessionStore session, UserRepository? users)
{
    private const string GuardHash = "59ba36addc2b2f9401580f014c7f58ea4e30989d";

    /// <summary><c>login_web_&lt;sha1&gt;</c>：会话里存放用户主键的键名。</summary>
    public const string SessionKey = "login_web_" + GuardHash;

    /// <summary><c>Session::forget('login_fails')</c> 用的会话键。</summary>
    public const string LoginFailsKey = "login_fails";

    /// <summary><c>Auth::login($user)</c>：写会话键 + <c>migrate(true)</c> 防会话固定。</summary>
    public void Login(int uid)
    {
        session.Put(SessionKey, (long)uid);
        // migrate(true) 会清空属性，登录态必须重新写入
        session.Migrate(true);
        session.Put(SessionKey, (long)uid);
    }

    /// <summary><c>Auth::logout()</c>。</summary>
    public void Logout()
    {
        session.Forget(SessionKey);
        session.Migrate(true);
    }

    /// <summary><c>Auth::id()</c>。</summary>
    public int? Id()
    {
        object? value = session.Get(SessionKey);
        return value switch
        {
            long l => (int)l,
            int i => i,
            null => null,
            _ => int.TryParse(value.ToString()?.Trim(), out int parsed) ? parsed : null,
        };
    }

    /// <summary><c>Auth::user()</c>；没有登录态或用户不存在返回 null。</summary>
    public UserRepository.UserRow? User()
    {
        int? uid = Id();
        return uid is null || users is null ? null : users.FindById(uid.Value);
    }

    /// <summary><c>Auth::check()</c>。</summary>
    public bool Check() => Id() is not null;

    /// <summary>计算某个 guard 名的会话键。</summary>
    public static string SessionKeyFor(string guardName) =>
        "login_" + guardName + "_" + Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes("Illuminate\\Auth\\SessionGuard"))).ToLowerInvariant();
}
