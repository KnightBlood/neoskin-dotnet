using System.Globalization;
using System.Numerics;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP 字符串函数的等价实现。
///
/// <p>三处 PHP 特有语义不能用 .NET 直接对应：</p>
/// <list type="bullet">
///   <item><c>trim()</c> 的字符集是 <c>" \t\n\r\0\x0B"</c>（含垂直制表符，<b>不含</b>
///         换页 <c>\x0C</c>），.NET 的 <c>Trim()</c> 会去掉 <c>\x0C</c>；</item>
///   <item><c>mb_substr()</c> 按码点截断（emoji 不能被劈成半个代理项），
///         <c>Substring</c> 按 UTF-16 码元；</item>
///   <item><c>(string) $v</c>：<c>true → "1"</c>、<c>false → ""</c>、<c>null → ""</c>、
///         <c>1.0 → "1"</c>、数组 → <c>"Array"</c>；浮点用 <c>precision=14</c>。</item>
/// </list>
/// </summary>
public static class PhpString
{
    /// <summary>PHP <c>trim()</c> 默认去掉的字符。</summary>
    private const string TrimChars = " \t\n\r\0\u000B";

    /// <summary>PHP 的 <c>precision</c> 默认值（不是 <c>serialize_precision</c>）。</summary>
    public const int Precision = 14;

    /// <summary>等价于 PHP 的 <c>trim($s)</c>。</summary>
    public static string Trim(string? s)
    {
        if (s is null)
        {
            return "";
        }

        int start = 0;
        int end = s.Length;
        while (start < end && TrimChars.IndexOf(s[start]) >= 0)
        {
            start++;
        }

        while (end > start && TrimChars.IndexOf(s[end - 1]) >= 0)
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>
    /// 等价于 PHP 的 <c>trim($s, $characters)</c>：第二个参数是<b>字符集合</b>，
    /// 支持 <c>"a..z"</c> 区间与 <c>\x00</c> 之类转义（源项目只用到 <c>trim($url, '/')</c>）。
    /// </summary>
    public static string Trim(string? s, string? characters)
    {
        if (s is null)
        {
            return "";
        }

        bool[] strip = CharacterSet(characters);
        int start = 0;
        int end = s.Length;
        while (start < end && s[start] < 256 && strip[s[start]])
        {
            start++;
        }

        while (end > start && s[end - 1] < 256 && strip[s[end - 1]])
        {
            end--;
        }

        return s[start..end];
    }

    private static bool[] CharacterSet(string? characters)
    {
        bool[] strip = new bool[256];
        if (characters is null)
        {
            return strip;
        }

        int i = 0;
        while (i < characters.Length)
        {
            int from = CharacterAt(characters, i);
            i += from < 0 ? 1 : CharWidth(characters, i);

            if (i + 1 < characters.Length && characters[i] == '.' && characters[i + 1] == '.')
            {
                i += 2;
                if (i < characters.Length)
                {
                    int to = CharacterAt(characters, i);
                    i += to < 0 ? 1 : CharWidth(characters, i);
                    if (to >= 0 && from >= 0)
                    {
                        if (from <= to)
                        {
                            for (int c = from; c <= to; c++)
                            {
                                strip[c] = true;
                            }
                        }
                        else
                        {
                            // PHP 的区间要求 from <= to；反着写只退化掉两个端点字符
                            strip[from] = true;
                            strip[to] = true;
                        }
                    }
                }
                else
                {
                    // 悬空 ".."：PHP 警告并把点号当普通字符
                    strip['.'] = true;
                }
            }
            else if (from >= 0)
            {
                strip[from] = true;
            }
        }

        return strip;
    }

    private static int CharacterAt(string s, int index)
    {
        char c = s[index];
        if (c == '\\' && index + 1 < s.Length)
        {
            char escaped = s[index + 1];
            return escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => 0x0B,
                'f' => '\f',
                'e' => 0x1B,
                '\\' => '\\',
                '0' => 0,
                _ => escaped < 256 ? escaped : -1,
            };
        }

        return c < 256 ? c : -1;
    }

    private static int CharWidth(string s, int index) =>
        s[index] == '\\' && index + 1 < s.Length ? 2 : 1;

    /// <summary>
    /// 等价于 PHP 的 <c>mb_substr($s, $start, $length)</c>，按码点计数；
    /// 负数 <paramref name="length"/> 表示去掉末尾 N 个字符。
    /// </summary>
    public static string MbSubstr(string? s, int start, int length)
    {
        if (s is null)
        {
            return "";
        }

        Rune[] runes = s.EnumerateRunes().ToArray();
        int total = runes.Length;
        if (start < 0)
        {
            start = Math.Max(total + start, 0);
        }

        if (start > total)
        {
            return "";
        }

        int end = length < 0 ? total + length : start + length;
        end = Math.Min(end, total);
        if (end <= start)
        {
            return "";
        }

        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            sb.Append(runes[i].ToString());
        }

        return sb.ToString();
    }

    /// <summary>等价于 PHP 的 <c>substr($s, $start)</c>（按<b>字节</b>）。</summary>
    public static string SubstrBytes(string? s, int start)
    {
        if (s is null)
        {
            return "";
        }

        byte[] bytes = Encoding.UTF8.GetBytes(s);
        if (start >= bytes.Length)
        {
            return "";
        }

        int from = Math.Max(start, 0);
        return Encoding.UTF8.GetString(bytes, from, bytes.Length - from);
    }

    /// <summary>等价于 PHP 的 <c>(string) $value</c>。</summary>
    public static string Cast(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "1" : "",
        double d => CastDouble(d),
        float f => CastDouble(f),
        decimal m => decimal.Truncate(m) == m ? ((long)m).ToString(CultureInfo.InvariantCulture) : m.ToString(CultureInfo.InvariantCulture),
        System.Collections.IDictionary => "Array",
        PhpArray => "Array",
        System.Collections.IEnumerable => "Array",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string CastDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "NAN";
        }

        if (double.IsPositiveInfinity(d))
        {
            return "INF";
        }

        if (double.IsNegativeInfinity(d))
        {
            return "-INF";
        }

        return Precision14(d);
    }

    /// <summary>
    /// PHP <c>(string)</c> 浮点转换：等价于 <c>zend_gcvt($d, 14, '.', 'E')</c>。
    ///
    /// <p>与 <see cref="PhpJson.Number"/> 的两处不同：有效位数是 <b>14</b>（不是最短往返），
    /// 科学计数法阈值是 <c>decpt &lt; -3 || decpt &gt; 14</c>（JSON 是 17），
    /// 且指数字母是<b>大写 E</b>。</p>
    ///
    /// <p>舍入必须与 PHP 的 <c>zend_dtoa</c> 一样是「正确舍入（HALF_EVEN）」，
    /// 所以这里用 <see cref="BigInteger"/> 做精确十进制展开：
    /// <c>123456789012345</c> 在 14 位上是精确的 .5 平局，HALF_EVEN 得
    /// <c>1.2345678901234E+14</c>（若用 .NET 的 G14 会得到 ...35）。</p>
    /// </summary>
    public static string Precision14(double d)
    {
        if (d == 0.0)
        {
            return BitConverter.DoubleToInt64Bits(d) == unchecked((long)0x8000000000000000) ? "-0" : "0";
        }

        bool negative = d < 0;
        (BigInteger digits, int decpt) = ExactDecimal(Math.Abs(d));

        string ds = digits.ToString(CultureInfo.InvariantCulture);
        if (ds.Length > Precision)
        {
            int drop = ds.Length - Precision;
            BigInteger divisor = BigInteger.Pow(10, drop);
            BigInteger quotient = BigInteger.DivRem(digits, divisor, out BigInteger remainder);

            // 正确舍入：> 半进位，== 半看奇偶（HALF_EVEN）
            BigInteger twice = remainder * 2;
            if (twice > divisor || (twice == divisor && !quotient.IsEven))
            {
                quotient += BigInteger.One;
            }

            ds = quotient.ToString(CultureInfo.InvariantCulture);
            if (ds.Length > Precision)
            {
                // 999…9 进位成 1000…0：多一位，指数 +1
                ds = ds[..^1];
                decpt++;
            }
        }

        int end = ds.Length;
        while (end > 1 && ds[end - 1] == '0')
        {
            end--;
        }

        ds = ds[..end];

        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (decpt < -3 || decpt > Precision)
        {
            sb.Append(ds[0]).Append('.');
            sb.Append(ds.Length > 1 ? ds[1..] : "0");
            int exp = decpt - 1;
            sb.Append('E').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.").Append('0', -decpt).Append(ds);
        }
        else if (decpt >= ds.Length)
        {
            sb.Append(ds).Append('0', decpt - ds.Length);
        }
        else
        {
            sb.Append(ds, 0, decpt).Append('.').Append(ds, decpt, ds.Length - decpt);
        }

        return sb.ToString();
    }

    /// <summary>
    /// double 的精确十进制展开：返回（去掉小数点后的数字串，整数部分位数）。
    /// 例：<c>0.5</c> → (<c>"5"</c>, 0)；<c>1e20</c> → (<c>"1"</c>, 21)。
    /// </summary>
    private static (BigInteger Digits, int Decpt) ExactDecimal(double positive)
    {
        long bits = BitConverter.DoubleToInt64Bits(positive);
        int rawExponent = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xF_FFFF_FFFF_FFFF;

        BigInteger mantissa;
        int exponent;
        if (rawExponent == 0)
        {
            mantissa = fraction;
            exponent = -1074;
        }
        else
        {
            mantissa = fraction | (1L << 52);
            exponent = rawExponent - 1075;
        }

        if (exponent >= 0)
        {
            BigInteger value = mantissa << exponent;
            return (value, value.ToString(CultureInfo.InvariantCulture).Length);
        }

        int k = -exponent;
        BigInteger scaled = mantissa * BigInteger.Pow(5, k);
        string text = scaled.ToString(CultureInfo.InvariantCulture);
        return (scaled, text.Length - k);
    }
}
