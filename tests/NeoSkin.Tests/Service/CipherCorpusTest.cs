using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Service.Cipher;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Tests.Service;

/// <summary>
/// 密码算法语料测试（<c>cipher.json</c> 144 条：hash / normalize / reject / roundtrip / verify）。
/// </summary>
public sealed class CipherCorpusTest
{
    private static ICipher Create(string algorithm) => algorithm switch
    {
        "BCRYPT" => new BcryptCipher(12),
        "ARGON2I" => new Argon2Cipher(false),
        "PHP_PASSWORD_HASH" => new PhpPasswordHashCipher(12),
        "MD5" => new DigestCipher.Md5(),
        "SALTED2MD5" => new DigestCipher.Salted2Md5(),
        "SHA256" => new DigestCipher.Sha256(),
        "SALTED2SHA256" => new DigestCipher.Salted2Sha256(),
        "SHA512" => new DigestCipher.Sha512(),
        "SALTED2SHA512" => new DigestCipher.Salted2Sha512(),
        _ => throw new ArgumentException(algorithm),
    };

    // ================================================================== hash（摘要类）

    [ConformanceFact]
    public void HashMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cipher");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("hash"))
        {
            string algorithm = Corpus.TextOrEmpty(c, "algorithm");
            string value = Corpus.TextOrEmpty(c, "value");
            string salt = Corpus.TextOrEmpty(c, "salt");

            Assert.Equal(Corpus.TextOrEmpty(c, "expected"),
                Create(algorithm).Hash(value, salt));
            checkedCases++;
        }

        Assert.Equal(48, checkedCases);
    }

    // ================================================================== normalize（bcrypt 固定盐）

    [ConformanceFact]
    public void NormalizeMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cipher");
        foreach (JsonNode c in corpus.Cases("normalize"))
        {
            string value = Corpus.TextOrEmpty(c, "value");
            string salt = Corpus.TextOrEmpty(c, "salt");
            Assert.Equal(Corpus.TextOrEmpty(c, "expected"),
                PhpBcrypt.HashWithSalt(value, salt));
        }
    }

    // ================================================================== reject

    [ConformanceFact]
    public void RejectsNulInPasswordHash()
    {
        Corpus corpus = Corpus.Require("cipher");
        foreach (JsonNode c in corpus.Cases("reject"))
        {
            string value = Corpus.TextOrEmpty(c, "value");
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new BcryptCipher(12).Hash(value, null));
            Assert.Equal(Corpus.TextOrEmpty(c, "message"), error.Message);
        }
    }

    // ================================================================== roundtrip

    [ConformanceFact]
    public void RoundTrips()
    {
        Corpus corpus = Corpus.Require("cipher");
        foreach (JsonNode c in corpus.Cases("roundtrip"))
        {
            string algorithm = Corpus.TextOrEmpty(c, "algorithm");
            string value = Corpus.TextOrEmpty(c, "value");
            ICipher cipher = Create(algorithm);

            string hash = cipher.Hash(value, Corpus.TextOrEmpty(c, "salt"));
            Assert.True(Corpus.TextOrEmpty(c, "format") is { Length: > 0 });

            Assert.True(cipher.Verify(value, hash, null), Corpus.TextOrEmpty(c, "id"));
            Assert.False(cipher.Verify(value + "X", hash, null), Corpus.TextOrEmpty(c, "id"));
        }
    }

    // ================================================================== verify

    [ConformanceFact]
    public void VerifyMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cipher");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("verify"))
        {
            string algorithm = Corpus.TextOrEmpty(c, "algorithm");
            string value = Corpus.TextOrEmpty(c, "value");
            string hash = Corpus.TextOrEmpty(c, "hash");
            string salt = Corpus.TextOrEmpty(c, "salt");
            bool expected = Corpus.Bool(c, "expected", false);

            Assert.True(expected == Create(algorithm).Verify(value, hash, salt),
                Corpus.TextOrEmpty(c, "id"));
            checkedCases++;
        }

        Assert.Equal(72, checkedCases);
    }

    [Fact]
    public void UnsupportedMethodThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            CipherConfig.Create(new NeoSkin.Web.Config.NeoSkinSettings { Secure = { Cipher = "NOPE" } }));
    }
}
