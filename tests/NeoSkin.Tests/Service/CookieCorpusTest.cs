using System.Text;
using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Cookie;
using NeoSkin.Web.Service.Crypto;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Service;

/// <summary>
/// cookie 加密链路的语料测试：<see cref="PhpEncrypter"/> + <see cref="CookieValuePrefix"/>
/// + <see cref="EncryptCookies"/>（<c>cookie.json</c>）。
/// </summary>
public sealed class CookieCorpusTest
{
    private static byte[] RawKey()
    {
        string appKey = Corpus.Text(Corpus.Require("cookie").CaseById("const.keys"), "app_key")!;
        return Convert.FromBase64String(appKey.StartsWith("base64:", StringComparison.Ordinal)
            ? appKey["base64:".Length..]
            : appKey);
    }

    // ================================================================== constructor

    [ConformanceFact]
    public void ConstructorRejectsBadKeys()
    {
        Corpus corpus = Corpus.Require("cookie");
        foreach (JsonNode c in corpus.Cases("constructor"))
        {
            string key = Corpus.TextOrEmpty(c, "key");
            string cipher = Corpus.TextOrEmpty(c, "cipher");

            Exception? thrown = null;
            try
            {
                _ = new PhpEncrypter(Encoding.UTF8.GetBytes(key), cipher);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Assert.NotNull(thrown);
            Assert.Equal(Corpus.Text(c, "error_message"), thrown!.Message);
        }
    }

    // ================================================================== 前缀

    [ConformanceFact]
    public void PrefixMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        byte[] key = RawKey();
        byte[] otherKey = Encoding.UTF8.GetBytes(new string('o', 32));

        foreach (JsonNode c in corpus.Cases("prefix_create"))
        {
            string prefix = CookieValuePrefix.Create(Corpus.TextOrEmpty(c, "name"), key);
            Assert.Equal(Corpus.TextOrEmpty(c, "expected"), prefix);
            Assert.Equal(Corpus.Integer(c, "expected_length", 0), prefix.Length);
        }

        foreach (JsonNode c in corpus.Cases("prefix_remove"))
        {
            Assert.Equal(Corpus.TextOrEmpty(c, "expected"),
                CookieValuePrefix.Remove(Corpus.TextOrEmpty(c, "input")));
        }

        foreach (JsonNode c in corpus.Cases("prefix_validate"))
        {
            byte[][] keys = Corpus.Integer(c, "key_count", 1) == 2 ? [key, otherKey] : [key];
            string? validated = CookieValuePrefix.Validate(
                Corpus.TextOrEmpty(c, "name"), Corpus.TextOrEmpty(c, "value"), keys);
            Assert.True(Corpus.Text(c, "expected") == validated, Corpus.TextOrEmpty(c, "id"));
        }

        Assert.Equal("hash_hmac('sha1', $name.'v2', $key).'|'",
            Corpus.Text(corpus.CaseById("const.cookie_value_prefix"), "formula"));
    }

    // ================================================================== encrypt / decrypt

    [ConformanceFact]
    public void EncryptMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        foreach (JsonNode c in corpus.Cases("encrypt"))
        {
            bool aes128 = Corpus.Text(c, "key") == "short";
            var encrypter = new PhpEncrypter(
                aes128 ? Encoding.UTF8.GetBytes("NeoSkin16ByteKey") : RawKey(),
                Corpus.TextOrEmpty(c, "cipher"));

            byte[] iv = Convert.FromBase64String(Corpus.TextOrEmpty(c, "iv_b64"));
            string output = encrypter.EncryptWithIv(iv, Corpus.TextOrEmpty(c, "plain"), false);

            Assert.True(Corpus.TextOrEmpty(c, "expected") == output,
                Corpus.TextOrEmpty(c, "id") + " encrypt");
            Assert.Equal(Corpus.TextOrEmpty(c, "json_payload"),
                Encoding.UTF8.GetString(Convert.FromBase64String(output)));
        }
    }

    [ConformanceFact]
    public void DecryptMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        foreach (JsonNode c in corpus.Cases("decrypt"))
        {
            var encrypter = Corpus.Text(c, "key") == new string('w', 32)
                ? new PhpEncrypter(Encoding.UTF8.GetBytes(new string('w', 32)), "aes-256-cbc")
                : new PhpEncrypter(RawKey(), "aes-256-cbc");

            string payload = Corpus.Text(c, "payload") ?? Corpus.TextOrEmpty(c, "payload_repr");
            bool unserialize = Corpus.Bool(c, "unserialize", true);
            bool ok = Corpus.Bool(c, "ok", true);
            string id = Corpus.TextOrEmpty(c, "id");

            try
            {
                object? value = encrypter.Decrypt(payload, unserialize);
                Assert.True(ok, id + " 应抛异常");
                Assert.True(Matches(c["expected"], value), id + " value");
            }
            catch (Exception e)
            {
                Assert.False(ok, id + " 不应抛异常: " + e.Message);

                string? expectedClass = Corpus.Text(c, "error_class");
                if (expectedClass == "ErrorException")
                {
                    Assert.IsType<ErrorException>(e);
                }
                else if (expectedClass is "Illuminate\\Contracts\\Encryption\\DecryptException"
                         or "Illuminate\\Contracts\\Encryption\\DecryptException")
                {
                    Assert.IsType<DecryptException>(e);
                }

                Assert.Equal(Corpus.Text(c, "error_message"), e.Message);
            }
        }
    }

    [ConformanceFact]
    public void AppearsEncryptedMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        foreach (JsonNode c in corpus.Cases("appears_encrypted"))
        {
            object? value = Corpus.Text(c, "value_php_type") == "int"
                ? long.Parse(Corpus.TextOrEmpty(c, "value_repr"), System.Globalization.CultureInfo.InvariantCulture)
                : Corpus.Text(c, "value");

            Assert.True(Corpus.Bool(c, "expected", false) == PhpEncrypter.AppearsEncrypted(value),
                Corpus.TextOrEmpty(c, "id"));
        }
    }

    // ================================================================== EncryptCookies

    [ConformanceFact]
    public void EncryptCookiesDecryptMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        var middleware = new EncryptCookies(new PhpEncrypter(RawKey(), "aes-256-cbc"));

        foreach (JsonNode c in corpus.Cases("encryptcookies_decrypt"))
        {
            var input = (PhpArray)PhpJson.Decode(c["input"]!.ToJsonString())!;
            PhpArray result = middleware.Decrypt(input);
            Assert.True(Matches(c["expected"], result), Corpus.TextOrEmpty(c, "id"));
        }
    }

    [ConformanceFact]
    public void EncryptCookiesEncryptMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("cookie");
        var middleware = new EncryptCookies(new PhpEncrypter(RawKey(), "aes-256-cbc"));

        foreach (JsonNode c in corpus.Cases("encryptcookies_encrypt"))
        {
            string name = Corpus.TextOrEmpty(c, "name");
            string value = Corpus.TextOrEmpty(c, "value");

            Assert.Equal(Corpus.TextOrEmpty(c, "prefix"), CookieValuePrefix.Create(name, RawKey()));

            string output = middleware.Encrypt(name, value);
            Assert.True(Corpus.Bool(c, "output_appears_encrypted", false)
                == PhpEncrypter.AppearsEncrypted(output), Corpus.TextOrEmpty(c, "id"));

            // expected_plain = prefix + value：解出来必须还原成它
            Assert.Equal(Corpus.TextOrEmpty(c, "expected_plain"),
                new PhpEncrypter(RawKey(), "aes-256-cbc").Decrypt(output, false));
        }
    }

    [Fact]
    public void DecryptRoundTripsArrays()
    {
        var middleware = new EncryptCookies(new PhpEncrypter(RawKey(), "aes-256-cbc"));
        string payload = middleware.Encrypt("arr[0]", "a");

        var cookies = new PhpArray();
        cookies.Put("arr", new PhpArray().With(0L, payload));

        var decrypted = (PhpArray)middleware.Decrypt(cookies).Get("arr")!;
        Assert.Equal("a", decrypted.Get(0L));
    }

    [Fact]
    public void DisabledCookiesSkipDecryption()
    {
        EncryptCookies.FlushState();
        var middleware = new EncryptCookies(new PhpEncrypter(RawKey(), "aes-256-cbc"));
        middleware.DisableFor("locale");
        EncryptCookies.Except(["XSRF-TOKEN"]);

        Assert.True(middleware.IsDisabled("locale"));
        Assert.True(middleware.IsDisabled("XSRF-TOKEN"));
        Assert.False(middleware.IsDisabled("BS_SESSION"));

        EncryptCookies.FlushState();
        var fresh = new EncryptCookies(new PhpEncrypter(RawKey(), "aes-256-cbc"));
        Assert.False(fresh.IsDisabled("locale"));
        Assert.False(fresh.IsDisabled("XSRF-TOKEN"));
    }

    // ================================================================== 辅助

    private static bool Matches(JsonNode? expected, object? actual)
    {
        if (expected is null || expected.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return actual is null;
        }

        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            return actual is string s && s == expected.GetValue<string>();
        }

        if (expected.GetValueKind() is System.Text.Json.JsonValueKind.True
            or System.Text.Json.JsonValueKind.False)
        {
            return actual is bool b && b == expected.GetValue<bool>();
        }

        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            return actual switch
            {
                long l => l == expected.GetValue<long>(),
                double d => d == expected.GetValue<double>(),
                _ => false,
            };
        }

        return actual is PhpArray array && SameJson(expected, array);
    }

    private static bool SameJson(JsonNode expected, PhpArray actual)
    {
        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Array)
        {
            JsonArray array = expected.AsArray();
            if (array.Count != actual.Count)
            {
                return false;
            }

            for (int i = 0; i < array.Count; i++)
            {
                if (!Compare(array[i], actual.Get((long)i)))
                {
                    return false;
                }
            }

            return true;
        }

        if (expected is JsonObject obj)
        {
            if (obj.Count != actual.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, JsonNode?> field in obj)
            {
                if (!Compare(field.Value, actual.Get(field.Key)))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool Compare(JsonNode? expected, object? actual) =>
        expected is not null && (expected is JsonObject or JsonArray)
            ? actual is PhpArray child && SameJson(expected, child)
            : Matches(expected, actual);
}
