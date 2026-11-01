using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpString"/> 回归测试：期望值全部由 PHP 8.5.12 实测
/// （<c>precision=14</c>、<c>serialize_precision=-1</c>）。
/// </summary>
public sealed class PhpStringTest
{
    // ================================================================== trim

    [Fact]
    public void TrimRemovesPhpWhitespace()
    {
        Assert.Equal("x", PhpString.Trim(" \t\n\r\0\u000Bx\u000B\0\r\n\t "));
        Assert.Equal("", PhpString.Trim("   "));
        Assert.Equal("", PhpString.Trim(""));
        Assert.Equal("a b", PhpString.Trim(" a b "));
    }

    [Fact]
    public void TrimKeepsFormFeedAndNbsp()
    {
        // .NET 的 Trim() 会去掉 \x0C，PHP 不会
        Assert.Equal("\fabc", PhpString.Trim("\fabc"));
        Assert.Equal("abc\f", PhpString.Trim("abc\f"));
        // .NET 的 Trim() 会去掉 NBSP / 全角空格，PHP 不会
        Assert.Equal("\u00A0abc\u00A0", PhpString.Trim("\u00A0abc\u00A0"));
        Assert.Equal("\u3000abc", PhpString.Trim("\u3000abc"));
    }

    [Fact]
    public void TrimNull() => Assert.Equal("", PhpString.Trim((string?)null));

    // ================================================================== mb_substr

    [Fact]
    public void MbSubstrCountsCodePoints()
    {
        string emoji = "😀";
        string s = string.Concat(Enumerable.Repeat(emoji, 60));
        Assert.Equal(120, s.Length);

        string cut = PhpString.MbSubstr(s, 0, 50);
        Assert.Equal(50, cut.EnumerateRunes().Count());
        Assert.Equal(100, cut.Length);
        for (int i = 0; i < cut.Length; i += 2)
        {
            Assert.True(char.IsHighSurrogate(cut[i]));
        }
    }

    [Fact]
    public void MbSubstrCjk()
    {
        string s = new('中', 120);
        Assert.Equal(100, PhpString.MbSubstr(s, 0, 100).Length);
        Assert.Equal(s, PhpString.MbSubstr(s, 0, 200));
        Assert.Equal("", PhpString.MbSubstr(s, 0, 0));
    }

    [Fact]
    public void MbSubstrNegativeAndOverflow()
    {
        Assert.Equal("de", PhpString.MbSubstr("abcde", -2, 2));
        Assert.Equal("abc", PhpString.MbSubstr("abcde", 0, -2));
        Assert.Equal("", PhpString.MbSubstr("abcde", 10, 2));
        Assert.Equal("", PhpString.MbSubstr("abcde", 5, 2));
        Assert.Equal("", PhpString.MbSubstr("", 0, 5));
        Assert.Equal("", PhpString.MbSubstr(null, 0, 5));
        Assert.Equal("e", PhpString.MbSubstr("abcde", -1, 1));
        Assert.Equal("abcde", PhpString.MbSubstr("abcde", -100, 100));
    }

    // ================================================================== substr（按字节）

    [Fact]
    public void SubstrBytesAnnouncementPrefix()
    {
        Assert.Equal("", PhpString.SubstrBytes("announcement", 13));
        Assert.Equal("zh_CN", PhpString.SubstrBytes("announcement_zh_CN", 13));
        Assert.Equal("en", PhpString.SubstrBytes("announcement_en", 13));
    }

    [Fact]
    public void SubstrBytesOutOfRange()
    {
        Assert.Equal("", PhpString.SubstrBytes("abc", 3));
        Assert.Equal("", PhpString.SubstrBytes("abc", 10));
        Assert.Equal("", PhpString.SubstrBytes(null, 0));
        Assert.Equal("bc", PhpString.SubstrBytes("abc", 1));
    }

    // ================================================================== (string)

    [Fact]
    public void CastScalars()
    {
        Assert.Equal("1", PhpString.Cast(true));
        Assert.Equal("", PhpString.Cast(false));
        Assert.Equal("", PhpString.Cast(null));
        Assert.Equal("Array", PhpString.Cast(new PhpArray().With("a", 1)));
        Assert.Equal("Array", PhpString.Cast(new List<object?> { 1, 2 }));
        Assert.Equal("42", PhpString.Cast(42));
        Assert.Equal("42", PhpString.Cast(42L));
        Assert.Equal("abc", PhpString.Cast("abc"));
        Assert.Equal("", PhpString.Cast(""));
    }

    [Fact]
    public void CastFloats()
    {
        Assert.Equal("1", PhpString.Cast(1.0d));
        Assert.Equal("1.5", PhpString.Cast(1.5d));
        Assert.Equal("0.1", PhpString.Cast(0.1d));
        Assert.Equal("0.33333333333333", PhpString.Cast(1.0d / 3));
        Assert.Equal("0.66666666666667", PhpString.Cast(2.0d / 3));
        Assert.Equal("-0.33333333333333", PhpString.Cast(-1.0d / 3));
        Assert.Equal("0.3", PhpString.Cast(0.1d + 0.2d));
        Assert.Equal("-0", PhpString.Cast(-0.0d));
        Assert.Equal("100", PhpString.Cast(100.0d));
        Assert.Equal("1234.5", PhpString.Cast(1234.5d));
    }

    [Fact]
    public void CastFloatScientific()
    {
        Assert.Equal("12345678901234", PhpString.Cast(12345678901234.0d));
        Assert.Equal("1.0E+14", PhpString.Cast(1e14));
        Assert.Equal("1.0E+15", PhpString.Cast(1e15));
        Assert.Equal("1.0E+16", PhpString.Cast(1e16));
        Assert.Equal("1.0E+20", PhpString.Cast(1e20));
        Assert.Equal("-1.0E+20", PhpString.Cast(-1e20));
        Assert.Equal("1.0E+15", PhpString.Cast(999999999999999.0d));
        Assert.Equal("1.2345678901234E+14", PhpString.Cast(123456789012345.0d));
        Assert.Equal("1.2345678901235E+15", PhpString.Cast(1234567890123456.0d));
        Assert.Equal("0.0001", PhpString.Cast(1e-4));
        Assert.Equal("0.001", PhpString.Cast(1e-3));
        Assert.Equal("1.0E-5", PhpString.Cast(1e-5));
        Assert.Equal("1.0E-7", PhpString.Cast(1e-7));
        Assert.Equal("1.0E-14", PhpString.Cast(1e-14));
        Assert.Equal("4.9406564584125E-324", PhpString.Cast(double.Epsilon));
        Assert.Equal("1.7976931348623E+308", PhpString.Cast(double.MaxValue));
    }

    [Fact]
    public void CastFloatRounding()
    {
        Assert.Equal("1.2345678901235", PhpString.Cast(1.2345678901234567d));
        Assert.Equal("3.1415926535898", PhpString.Cast(Math.PI));
        Assert.Equal("0.0001234", PhpString.Cast(0.0001234d));
        Assert.Equal("1234567890123.5", PhpString.Cast(1234567890123.5d));
    }
}
