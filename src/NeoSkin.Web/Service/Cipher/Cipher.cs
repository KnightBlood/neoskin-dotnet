using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using NeoSkin.Web.Config;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Web.Service.Cipher;

/// <summary>
/// 密码算法抽象（源 <c>App\Services\Cipher\BaseCipher</c>）。
/// 由 <c>PWD_METHOD</c> 选择：BCRYPT / ARGON2I / PHP_PASSWORD_HASH /
/// MD5 / SALTED2MD5 / SHA256 / SALTED2SHA256 / SHA512 / SALTED2SHA512。
/// </summary>
public interface ICipher
{
    /// <summary>生成密码哈希；<paramref name="salt"/> 仅对 SALTED2* 生效。</summary>
    string Hash(string? value, string? salt);

    bool Verify(string? password, string? hash, string? salt);
}

/// <summary>bcrypt：<c>password_hash(PASSWORD_BCRYPT)</c> / <c>password_verify</c>。</summary>
public sealed class BcryptCipher(int rounds) : ICipher
{
    public int Cost { get; } = Math.Clamp(rounds, PhpBcrypt.MinCost, PhpBcrypt.MaxCost);

    public string Hash(string? value, string? salt) => PhpBcrypt.Hash(value, Cost);

    public bool Verify(string? password, string? hash, string? salt) =>
        hash is not null && PhpPasswordVerify.Verify(password, hash);
}

/// <summary>
/// <c>PHP_PASSWORD_HASH</c>：新哈希按 <c>PASSWORD_DEFAULT</c>（PHP 8.5 仍是 bcrypt），
/// 校验算法无关（历史哈希可能是 <c>$argon2i$</c>）。
/// </summary>
public sealed class PhpPasswordHashCipher(int rounds) : ICipher
{
    private readonly BcryptCipher _bcrypt = new(rounds);

    public string Hash(string? value, string? salt) => _bcrypt.Hash(value, salt);

    public bool Verify(string? password, string? hash, string? salt) =>
        hash is not null && PhpPasswordVerify.Verify(password, hash);
}

/// <summary>
/// PHP <c>PASSWORD_ARGON2I</c> / <c>PASSWORD_ARGON2ID</c> 的等价实现。
/// 输出 <c>$argon2i$v=19$m=65536,t=4,p=1$&lt;salt&gt;$&lt;hash&gt;</c>（无填充 base64）。
/// </summary>
public sealed class Argon2Cipher(bool argon2id = false) : ICipher
{
    private const int DefaultMemory = 65536;
    private const int DefaultTime = 4;
    private const int DefaultParallelism = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public string Hash(string? value, string? salt)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] raw = RawHash(value, saltBytes, DefaultMemory, DefaultTime, DefaultParallelism,
            argon2id, HashLength);

        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
        return "$" + (argon2id ? "argon2id" : "argon2i") + "$v=19"
            + "$m=" + DefaultMemory + ",t=" + DefaultTime + ",p=" + DefaultParallelism
            + "$" + B64(saltBytes) + "$" + B64(raw);
    }

    public bool Verify(string? password, string? hash, string? salt) =>
        hash is not null && PhpPasswordVerify.Verify(password, hash);

    internal static byte[] RawHash(string? value, byte[] salt, int memory, int time,
        int parallelism, bool argon2id, int hashLength)
    {
        byte[] input = Encoding.UTF8.GetBytes(value ?? "");
        Argon2 argon2 = argon2id ? new Argon2id(input) : new Argon2i(input);
        argon2.Salt = salt;
        argon2.MemorySize = memory;
        argon2.Iterations = time;
        argon2.DegreeOfParallelism = parallelism;
        return argon2.GetBytes(hashLength);
    }
}

/// <summary>
/// 无盐摘要（MD5 / SHA256 / SHA512）与 <c>SALTED2*</c> 双重摘要：
/// <c>digest(digest(value) . salt)</c>。
/// </summary>
public abstract class DigestCipher : ICipher
{
    protected abstract string Algorithm { get; }

    protected abstract int Passes { get; }

    public string Hash(string? value, string? salt)
    {
        string first = Digest(value ?? "");
        return Passes == 1 ? first : Digest(first + (salt ?? ""));
    }

    public bool Verify(string? password, string? hash, string? salt) =>
        ConstantTimeEquals(hash, Hash(password, salt));

    private string Digest(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] output = Algorithm switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA-256" => SHA256.HashData(bytes),
            "SHA-512" => SHA512.HashData(bytes),
            _ => throw new InvalidOperationException(Algorithm),
        };
        return Convert.ToHexString(output).ToLowerInvariant();
    }

    private static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    public sealed class Md5 : DigestCipher
    {
        protected override string Algorithm => "MD5";
        protected override int Passes => 1;
    }

    public sealed class Salted2Md5 : DigestCipher
    {
        protected override string Algorithm => "MD5";
        protected override int Passes => 2;
    }

    public sealed class Sha256 : DigestCipher
    {
        protected override string Algorithm => "SHA-256";
        protected override int Passes => 1;
    }

    public sealed class Salted2Sha256 : DigestCipher
    {
        protected override string Algorithm => "SHA-256";
        protected override int Passes => 2;
    }

    public sealed class Sha512 : DigestCipher
    {
        protected override string Algorithm => "SHA-512";
        protected override int Passes => 1;
    }

    public sealed class Salted2Sha512 : DigestCipher
    {
        protected override string Algorithm => "SHA-512";
        protected override int Passes => 2;
    }
}

/// <summary>按 <c>PWD_METHOD</c> 创建 cipher（对应源 <c>AppServiceProvider</c> 的 singleton）。</summary>
public static class CipherConfig
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "BCRYPT", "ARGON2I", "PHP_PASSWORD_HASH",
        "MD5", "SALTED2MD5", "SHA256", "SALTED2SHA256", "SHA512", "SALTED2SHA512",
    };

    public static ICipher Create(NeoSkinSettings settings)
    {
        string method = settings.Secure.Cipher;
        string normalized = string.IsNullOrWhiteSpace(method) ? "BCRYPT" : method.Trim().ToUpperInvariant();
        int rounds = settings.Hashing.PasswordRounds;

        return normalized switch
        {
            "BCRYPT" => new BcryptCipher(rounds),
            "ARGON2I" => new Argon2Cipher(false),
            "PHP_PASSWORD_HASH" => new PhpPasswordHashCipher(rounds),
            "MD5" => new DigestCipher.Md5(),
            "SALTED2MD5" => new DigestCipher.Salted2Md5(),
            "SHA256" => new DigestCipher.Sha256(),
            "SALTED2SHA256" => new DigestCipher.Salted2Sha256(),
            "SHA512" => new DigestCipher.Sha512(),
            "SALTED2SHA512" => new DigestCipher.Salted2Sha512(),
            _ => throw new ArgumentException(
                $"Unsupported PWD_METHOD: {method} (expected one of {string.Join(", ", Supported)})"),
        };
    }
}
