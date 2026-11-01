namespace NeoSkin.Web.Service.Hash;

/// <summary>
/// Laravel <c>Hash</c> 门面的等价实现（bcrypt 驱动）。
///
/// <p>只服务于 <c>oauth_clients.secret</c>；用户密码走 <c>Cipher</c>（成本因子不同）。
/// 对应关系：</p>
/// <list type="bullet">
///   <item><c>Hash::make</c> → <c>password_hash($v, PASSWORD_BCRYPT, ['cost' =&gt; $rounds])</c>；</item>
///   <item><c>Hash::check</c> → <c>password_verify</c>（算法无关）；</item>
///   <item><c>Hash::isHashed</c> → <c>password_get_info()['algo'] !== null</c>；</item>
///   <item><c>Hash::needsRehash</c> → <c>password_needs_rehash($v, PASSWORD_BCRYPT, ...)</c>。</item>
/// </list>
/// </summary>
public sealed class LaravelHash
{
    public LaravelHash(int rounds) => Rounds = Math.Clamp(rounds, PhpBcrypt.MinCost, PhpBcrypt.MaxCost);

    /// <summary>配置里的成本因子（已按 PHP 合法范围收敛）。</summary>
    public int Rounds { get; }

    public string Driver => "bcrypt";

    /// <summary><c>Hash::make($value)</c>，产出 <c>$2y$&lt;cost&gt;$…</c>。</summary>
    public string Make(string value) => PhpBcrypt.Hash(value, Rounds);

    /// <summary><c>Hash::check($value, $hash)</c>：空哈希直接 false，其余走 password_verify。</summary>
    public bool Check(string? value, string? hash) =>
        !string.IsNullOrEmpty(hash) && PhpPasswordVerify.Verify(value, hash);

    /// <summary><c>Hash::isHashed($value)</c>。</summary>
    public static bool IsHashed(string? value) => PhpPasswordInfo.IsHashed(value);

    /// <summary>
    /// <c>Hash::needsRehash($value)</c>：不是 <c>$2y$</c> 的 60 字节 bcrypt → true；
    /// 是 bcrypt 但 cost 与配置不同 → true；否则 false。
    /// </summary>
    public bool NeedsRehash(string? value) => PhpPasswordInfo.NeedsRehash(value, Rounds);

    public override string ToString() => $"LaravelHash[bcrypt, cost={Rounds}]";
}
