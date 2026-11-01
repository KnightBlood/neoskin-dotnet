using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpBase64"/> 回归测试：24 组样本来自 PHP 8.5 实测
/// （<c>bin2hex(base64_decode($s, $strict))</c>）。
/// </summary>
public sealed class PhpBase64Test
{
    private static readonly (string Input, string LooseHex, string? StrictHex)[] Samples =
    [
        ("YQ==", "61", "61"),
        ("YQ", "61", "61"),
        ("ab==", "69", "69"),
        ("abc", "69b7", "69b7"),
        ("YYYYY", "618618", null),
        ("YQ==A", "6100", null),
        ("YQ==\u0000", "61", null),
        ("YQ==\u000B", "61", null),
        ("YQ==\u000C", "61", null),
        ("====", "", null),
        ("=", "", null),
        ("a-b_", "69", null),
        ("-_-_", "", null),
        ("YQ==  x", "610C", null),
        ("YQ=", "61", null),
        ("Q", "", null),
        ("QQ", "41", "41"),
        ("QUI", "4142", "4142"),
        ("QUJD", "414243", "414243"),
        (" YQ==", "61", "61"),
        ("", "", ""),
        ("QUJDRA==", "41424344", "41424344"),
        ("QUJDRA", "41424344", "41424344"),
        ("AAAA", "000000", "000000"),
        ("+/+/", "FBFFBF", "FBFFBF"),
    ];

    [Fact]
    public void DecodeMatchesPhp()
    {
        foreach ((string input, string looseHex, string? strictHex) in Samples)
        {
            Assert.Equal(Hex(looseHex), PhpBase64.Decode(input));

            if (strictHex is null)
            {
                Assert.Null(PhpBase64.Decode(input, true));
                Assert.True(PhpBase64.FailsStrictly(input));
            }
            else
            {
                Assert.Equal(Hex(strictHex), PhpBase64.Decode(input, true));
                Assert.False(PhpBase64.FailsStrictly(input));
            }
        }
    }

    [Fact]
    public void EncodeMatchesPhp()
    {
        Assert.Equal("", PhpBase64.Encode(Array.Empty<byte>()));
        Assert.Equal("YQ==", PhpBase64.Encode("a"));
        Assert.Equal("YWI=", PhpBase64.Encode("ab"));
        Assert.Equal("YWJj", PhpBase64.Encode("abc"));
        Assert.Equal("5a+G56CB", PhpBase64.Encode("密码"));
        Assert.Equal("5a+G56CB", PhpBase64.Encode(Encoding.UTF8.GetBytes("密码")));
        Assert.Equal("+/+/", PhpBase64.Encode(new byte[] { 0xFB, 0xFF, 0xBF }));
    }

    [Fact]
    public void RoundTripAllBytes()
    {
        byte[] all = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            all[i] = (byte)i;
        }

        string encoded = PhpBase64.Encode(all);
        Assert.Equal(0, encoded.Length % 4);
        Assert.Equal(all, PhpBase64.Decode(encoded));
        Assert.Equal(all, PhpBase64.Decode(encoded, true));
    }

    [Fact]
    public void StrictPaddingRules()
    {
        Assert.Null(PhpBase64.Decode("YQ=", true));
        Assert.Null(PhpBase64.Decode("YQ===", true));
        Assert.Null(PhpBase64.Decode("Y===", true));
        Assert.Null(PhpBase64.Decode("Y", true));
        Assert.Null(PhpBase64.Decode("YYYYY", true));
        Assert.Equal("a", Encoding.Latin1.GetString(PhpBase64.Decode("YQ==", true)!));
    }

    [Fact]
    public void LooseNeverFails()
    {
        Assert.Empty(PhpBase64.Decode("🔐"));
        Assert.Empty(PhpBase64.Decode("\u00FF"));
        Assert.Empty(PhpBase64.Decode("===="));
    }

    [Fact]
    public void NullIsEmpty()
    {
        Assert.Empty(PhpBase64.Decode(null));
        Assert.Empty(PhpBase64.Decode(null, true)!);
        Assert.Equal("", PhpBase64.Encode((string?)null));
    }

    private static byte[] Hex(string hex)
    {
        byte[] output = new byte[hex.Length / 2];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return output;
    }
}
