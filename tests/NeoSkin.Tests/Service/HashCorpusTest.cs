using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Service.Hash;

namespace NeoSkin.Tests.Service;

/// <summary>
/// <see cref="PhpPasswordInfo"/> / <see cref="PhpPasswordVerify"/> / <see cref="LaravelHash"/>
/// 的语料测试（<c>hash.json</c> 219 条：const / get_info / needs_rehash / verify）。
/// </summary>
public sealed class HashCorpusTest
{
    // ================================================================== const

    [ConformanceFact]
    public void ConstantsMatchPhp()
    {
        Corpus corpus = Corpus.Require("hash");
        var expected = new Dictionary<string, object>
        {
            ["const.PASSWORD_BCRYPT"] = "2y",
            ["const.PASSWORD_ARGON2I"] = "argon2i",
            ["const.PASSWORD_ARGON2ID"] = "argon2id",
            ["const.PASSWORD_DEFAULT"] = "2y",
            ["const.bcryptLength"] = (long)PhpPasswordInfo.BcryptLength,
            ["const.bcryptDefaultCost"] = PhpPasswordInfo.BcryptDefaultCost,
            ["const.argon2DefaultMemory"] = PhpPasswordInfo.Argon2DefaultMemory,
            ["const.argon2DefaultTime"] = PhpPasswordInfo.Argon2DefaultTime,
            ["const.argon2DefaultThreads"] = PhpPasswordInfo.Argon2DefaultThreads,
        };

        Assert.Equal(9, corpus.Cases("const").Count());
        foreach (KeyValuePair<string, object> entry in expected)
        {
            JsonNode c = corpus.CaseById(entry.Key);
            JsonNode value = c["value"]!;
            object actual = value.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? value.GetValue<string>()
                : value.GetValue<long>();
            Assert.Equal(entry.Value, actual);
        }
    }

    // ================================================================== get_info

    [ConformanceFact]
    public void GetInfoMatchesPhp()
    {
        Corpus corpus = Corpus.Require("hash");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("get_info"))
        {
            string id = Corpus.TextOrEmpty(c, "id");
            string hash = Corpus.TextOrEmpty(c, "hash");

            Assert.True(Corpus.Text(c, "expected_algo") == PhpPasswordInfo.Algorithm(hash), id + " algo");
            Assert.True(Corpus.Text(c, "expected_algo_name") == PhpPasswordInfo.AlgorithmName(hash),
                id + " algoName");
            Assert.True(Corpus.Bool(c, "expected_is_hashed", false) == PhpPasswordInfo.IsHashed(hash),
                id + " isHashed");
            Assert.True(Corpus.Bool(c, "expected_is_hashed", false) == LaravelHash.IsHashed(hash),
                id + " Hash::isHashed");
            AssertOptions(c, hash, id);
            checkedCases++;
        }

        Assert.True(checkedCases >= 132);
    }

    private static void AssertOptions(JsonNode c, string hash, string id)
    {
        JsonNode options = c["expected_options"]!;
        if (options.GetValueKind() == System.Text.Json.JsonValueKind.Array)
        {
            Assert.Null(PhpPasswordInfo.Algorithm(hash));
            return;
        }

        if (options.AsObject().ContainsKey("cost"))
        {
            Assert.Equal(Corpus.Long(options, "cost", long.MinValue),
                PhpPasswordInfo.BcryptCost(hash));
            return;
        }

        PhpPasswordInfo.Argon2Options actual = PhpPasswordInfo.Argon2OptionsOf(hash);
        Assert.Equal(Corpus.Long(options, "memory_cost", long.MinValue), actual.MemoryCost);
        Assert.Equal(Corpus.Long(options, "time_cost", long.MinValue), actual.TimeCost);
        Assert.Equal(Corpus.Long(options, "threads", long.MinValue), actual.Threads);
    }

    // ================================================================== needs_rehash

    [ConformanceFact]
    public void NeedsRehashMatchesPhp()
    {
        Corpus corpus = Corpus.Require("hash");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("needs_rehash"))
        {
            string hash = Corpus.TextOrEmpty(c, "hash");
            long cost = Corpus.Long(c, "cost", 0);
            bool expected = Corpus.Bool(c, "expected", false);

            Assert.True(expected == PhpPasswordInfo.NeedsRehash(hash, cost),
                $"{Corpus.TextOrEmpty(c, "id")}: needsRehash(cost={cost})");
            checkedCases++;
        }

        Assert.True(checkedCases >= 54);
    }

    // ================================================================== verify

    [ConformanceFact]
    public void VerifyMatchesPhp()
    {
        Corpus corpus = Corpus.Require("hash");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("verify"))
        {
            string value = Corpus.TextOrEmpty(c, "value");
            string hash = Corpus.TextOrEmpty(c, "hash");
            bool expected = Corpus.Bool(c, "expected", false);

            Assert.True(expected == PhpPasswordVerify.Verify(value, hash),
                $"{Corpus.TextOrEmpty(c, "id")}: password_verify");
            checkedCases++;
        }

        Assert.Equal(24, checkedCases);
    }

    // ================================================================== 手写冒烟

    [Fact]
    public void MakeProducesPhpCompatibleHash()
    {
        var hash = new LaravelHash(10);
        string made = hash.Make("plain-secret");
        Assert.StartsWith("$2y$10$", made);
        Assert.Equal(60, made.Length);
        Assert.True(hash.Check("plain-secret", made));
        Assert.False(hash.Check("plain-secretX", made));
        Assert.True(LaravelHash.IsHashed(made));
        Assert.False(hash.NeedsRehash(made));
    }

    [Fact]
    public void RoundsAreClampedToPhpRange()
    {
        Assert.Equal(4, new LaravelHash(0).Rounds);
        Assert.Equal(31, new LaravelHash(99).Rounds);
    }

    [Fact]
    public void CheckRejectsEmptyHash()
    {
        var hash = new LaravelHash(10);
        Assert.False(hash.Check("x", null));
        Assert.False(hash.Check("x", ""));
    }

    [Fact]
    public void PasswordNilBehavesLikeEmptyString()
    {
        string made = new LaravelHash(4).Make("");
        Assert.True(PhpPasswordVerify.Verify("", made));
        Assert.True(PhpPasswordVerify.Verify(null, made));
    }

    [Fact]
    public void NormalizeTruncatesAtNulAndHashingTruncates72Bytes()
    {
        Assert.Equal("ab", PhpBcrypt.Normalize("ab\0cd"));

        // 72 字节截断由 BCrypt.Net-Next 在 UTF-8 字节上完成
        string made = PhpBcrypt.Hash(new string('a', 100), 4);
        Assert.True(PhpPasswordVerify.Verify(new string('a', 72), made));
        Assert.True(PhpPasswordVerify.Verify(new string('a', 100), made));

        // password_hash 拒绝 NUL；verify 与 crypt 一样截断
        Assert.Throws<ArgumentException>(() => PhpBcrypt.Hash("ab\0cd", 4));
        string crypted = PhpBcrypt.HashWithSalt("ab\0cd", PhpBcrypt.GenerateSalt(4));
        Assert.True(PhpPasswordVerify.Verify("ab", crypted));
        Assert.True(PhpPasswordVerify.Verify("ab\0zzz", crypted));
    }
}
