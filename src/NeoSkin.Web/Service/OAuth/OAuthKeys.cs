using System.Security.Cryptography;
using System.Text;

namespace NeoSkin.Web.Service.OAuth;

/// <summary>
/// Passport 的 RSA 密钥（源 {@code php artisan passport:keys} 的等价物）。
///
/// <p>密钥文件位于 <c>storage/oauth-private.key</c> 与 <c>storage/oauth-public.key</c>。
/// 源（PHP/OpenSSL）默认写 PKCS#1（<c>BEGIN RSA PRIVATE KEY</c>），Passport 也用
/// PKCS#1 公钥；这里两种格式都读取，缺文件时生成 <b>PKCS#1</b> 以保持与 PHP 互通。</p>
/// </summary>
public sealed class OAuthKeys(string storageDirectory)
{
    private readonly object _lock = new();
    private RSA? _rsa;

    private string PrivatePath => Path.Combine(storageDirectory, "oauth-private.key");

    private string PublicPath => Path.Combine(storageDirectory, "oauth-public.key");

    /// <summary>确保密钥存在并可加载（首次使用 token 端点时调用）。</summary>
    public void EnsureGenerated()
    {
        lock (_lock)
        {
            if (_rsa is not null)
            {
                return;
            }

            if (File.Exists(PrivatePath) && File.Exists(PublicPath))
            {
                try
                {
                    var loaded = RSA.Create();
                    if (TryImportPrivate(loaded, File.ReadAllText(PrivatePath)))
                    {
                        _rsa = loaded;
                        return;
                    }

                    loaded.Dispose();
                }
                catch (IOException)
                {
                    // 共享读失败视为「没有密钥」，下面重新生成
                }
                catch (CryptographicException)
                {
                }
            }

            var rsa = RSA.Create(2048);
            Directory.CreateDirectory(storageDirectory);
            File.WriteAllText(PrivatePath, ToPem("RSA PRIVATE KEY", rsa.ExportRSAPrivateKey()));
            File.WriteAllText(PublicPath, ToPem("RSA PUBLIC KEY", rsa.ExportRSAPublicKey()));
            _rsa = rsa;
        }
    }

    private RSA Rsa
    {
        get
        {
            EnsureGenerated();
            return _rsa!;
        }
    }

    /// <summary>RS256 签名。</summary>
    public byte[] Sign(byte[] data) => Rsa.SignData(data, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    /// <summary>RS256 验签。</summary>
    public bool Verify(byte[] data, byte[] signature)
    {
        try
        {
            return Rsa.VerifyData(data, signature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryImportPrivate(RSA rsa, string pem)
    {
        byte[] der = DecodePem(pem);
        try
        {
            if (pem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
            {
                rsa.ImportRSAPrivateKey(der, out _);
            }
            else
            {
                rsa.ImportPkcs8PrivateKey(der, out _);
            }

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] DecodePem(string pem)
    {
        var sb = new StringBuilder();
        foreach (string line in pem.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("-----", StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append(trimmed);
        }

        return Convert.FromBase64String(sb.ToString());
    }

    private static string ToPem(string type, byte[] der)
    {
        string base64 = Convert.ToBase64String(der);
        var sb = new StringBuilder("-----BEGIN " + type + "-----\n");
        for (int i = 0; i < base64.Length; i += 64)
        {
            sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        sb.Append("-----END ").Append(type).Append("-----\n");
        return sb.ToString();
    }
}
