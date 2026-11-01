using System.Globalization;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP 关联数组在 .NET 侧的统一表示：键是 <see cref="long"/>（PHP 的 int 键）或
/// <see cref="string"/>，保持插入顺序（<c>LinkedHashMap</c> 语义）。
/// </summary>
public sealed class PhpArray : IEnumerable<KeyValuePair<object, object?>>
{
    private readonly List<object> _keys = new();
    private readonly List<object?> _values = new();
    private readonly Dictionary<object, int> _slots = new();

    public int Count => _keys.Count;

    public bool IsEmpty => _keys.Count == 0;

    /// <summary>按 PHP 的键归一化规则放入（<c>"1"</c> 会变成 int 键 1）。</summary>
    public void Put(object key, object? value)
    {
        object normalized = PhpJson.NormalizeKey(key);
        if (_slots.TryGetValue(normalized, out int slot))
        {
            _values[slot] = value;
            return;
        }

        _slots[normalized] = _keys.Count;
        _keys.Add(normalized);
        _values.Add(value);
    }

    /// <summary>链式写法：<c>new PhpArray().With("a", 1)</c>。</summary>
    public PhpArray With(object key, object? value)
    {
        Put(key, value);
        return this;
    }

    public object? Get(object key) =>
        _slots.TryGetValue(PhpJson.NormalizeKey(key), out int slot) ? _values[slot] : null;

    public bool TryGetValue(object key, out object? value)
    {
        if (_slots.TryGetValue(PhpJson.NormalizeKey(key), out int slot))
        {
            value = _values[slot];
            return true;
        }

        value = null;
        return false;
    }

    public bool ContainsKey(object key) => _slots.ContainsKey(PhpJson.NormalizeKey(key));

    /// <summary>清空（保持对象可继续使用）。</summary>
    public void Clear()
    {
        _keys.Clear();
        _values.Clear();
        _slots.Clear();
    }

    /// <summary>删除键（保持其余键的相对顺序，对应 PHP 的 <c>unset()</c>）。</summary>
    public bool Remove(object key)
    {
        object normalized = PhpJson.NormalizeKey(key);
        if (!_slots.TryGetValue(normalized, out int slot))
        {
            return false;
        }

        _keys.RemoveAt(slot);
        _values.RemoveAt(slot);
        _slots.Clear();
        for (int i = 0; i < _keys.Count; i++)
        {
            _slots[_keys[i]] = i;
        }

        return true;
    }

    public object? this[object key]
    {
        get => Get(key);
        set => Put(key, value);
    }

    public IReadOnlyList<object> Keys => _keys;

    public IReadOnlyList<object?> Values => _values;

    public IEnumerator<KeyValuePair<object, object?>> GetEnumerator()
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            yield return new KeyValuePair<object, object?>(_keys[i], _values[i]);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// PHP <c>json_encode()</c> / <c>json_decode($json, true)</c> 的等价实现。
///
/// <p>为什么不直接用 <c>System.Text.Json</c>：写库内容要能被 PHP 版原样读回，
/// 而两者的默认行为差异很大（<c>1.0</c> → <c>1</c>、<c>/</c> → <c>\/</c>、
/// 非 ASCII → <c>\uXXXX</c>、<c>0x7F</c> 原样输出……）。
/// 规则全部由 <c>conformance/data/json.json</c> 的用例锁定。</p>
/// </summary>
public static class PhpJson
{
    /// <summary>PHP 的 <c>JSON_HEX_TAG</c>：<c>&lt;</c> / <c>&gt;</c> 转成大写 <c>\u003C</c> / <c>\u003E</c>。</summary>
    public const int JsonHexTag = 1;

    /// <summary>PHP 的 <c>JSON_HEX_AMP</c>：<c>&amp;</c> 转成 <c>\u0026</c>。</summary>
    public const int JsonHexAmp = 2;

    /// <summary>PHP 的 <c>JSON_HEX_APOS</c>：<c>'</c> 转成 <c>\u0027</c>。</summary>
    public const int JsonHexApos = 4;

    /// <summary>PHP 的 <c>JSON_HEX_QUOT</c>：<c>"</c> 转成 <c>\u0022</c>。</summary>
    public const int JsonHexQuot = 8;

    /// <summary>PHP 的 <c>JSON_UNESCAPED_SLASHES</c>。</summary>
    public const int JsonUnescapedSlashes = 64;

    /// <summary>PHP 的 <c>JSON_UNESCAPED_UNICODE</c>。</summary>
    public const int JsonUnescapedUnicode = 256;

    private static readonly char[] Hex = "0123456789abcdef".ToCharArray();

    // ================================================================== 编码

    /// <summary>等价于 <c>json_encode($value)</c>（flags 为 0）。</summary>
    public static string Encode(object? value) => Encode(value, 0);

    /// <summary>等价于 <c>json_encode($value, $flags)</c>。</summary>
    public static string Encode(object? value, int flags)
    {
        var sb = new StringBuilder();
        Write(sb, value, flags);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int flags)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(sb, s, flags);
                return;
            case double d:
                sb.Append(Number(d));
                return;
            case float f:
                sb.Append(Number(f));
                return;
            case decimal m:
                // 对应 Java 的 BigDecimal 分支：小数位 <= 0 输出整数，否则走浮点最短表示
                sb.Append(decimal.Truncate(m) == m
                    ? ((long)m).ToString(CultureInfo.InvariantCulture)
                    : Number((double)m));
                return;
            case sbyte or byte or short or ushort or int or uint or long:
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case ulong ul:
                sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                return;
            case PhpArray array:
                WriteArray(sb, array, flags);
                return;
            case System.Collections.IDictionary dict:
                WriteArray(sb, FromDictionary(dict), flags);
                return;
            case System.Collections.IEnumerable list:
                WriteArray(sb, FromEnumerable(list), flags);
                return;
            default:
                WriteString(sb, value.ToString() ?? "", flags);
                return;
        }
    }

    private static PhpArray FromDictionary(System.Collections.IDictionary dict)
    {
        var array = new PhpArray();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            array.Put(entry.Key ?? "", entry.Value);
        }

        return array;
    }

    private static PhpArray FromEnumerable(System.Collections.IEnumerable list)
    {
        var array = new PhpArray();
        long index = 0;
        foreach (object? item in list)
        {
            array.Put(index++, item);
        }

        return array;
    }

    private static void WriteArray(StringBuilder sb, PhpArray array, int flags)
    {
        if (array.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        bool isList = IsList(array);
        sb.Append(isList ? '[' : '{');
        bool first = true;
        foreach (KeyValuePair<object, object?> entry in array)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            if (!isList)
            {
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "", flags);
                sb.Append(':');
            }

            Write(sb, entry.Value, flags);
        }

        sb.Append(isList ? ']' : '}');
    }

    /// <summary>键是否恰为 <c>0..n-1</c>（PHP 的 <c>array_is_list()</c>）。</summary>
    public static bool IsList(PhpArray array)
    {
        long expected = 0;
        foreach (object key in array.Keys)
        {
            if (key is not long l || l != expected)
            {
                return false;
            }

            expected++;
        }

        return true;
    }

    // ================================================================== 数字

    /// <summary>
    /// PHP <c>serialize_precision=-1</c> 的浮点格式化（<c>zend_gcvt</c> + json 调整）：
    /// 最短往返十进制，<c>decpt &lt; -3 || decpt &gt; 17</c> 用科学计数法，
    /// 且科学计数法小数点后至少保留一位（<c>1.0e+20</c>）。
    /// </summary>
    public static string Number(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new ArgumentException("Inf and NaN cannot be JSON encoded");
        }

        if (d == 0.0)
        {
            return BitConverter.DoubleToInt64Bits(d) == unchecked((long)0x8000000000000000) ? "-0" : "0";
        }

        bool negative = d < 0;
        string r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);

        int exponent = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
            r = r[..e];
        }

        int dot = r.IndexOf('.');
        string digits;
        int decpt;
        if (dot >= 0)
        {
            digits = r.Remove(dot, 1);
            decpt = dot + exponent;
        }
        else
        {
            digits = r;
            decpt = r.Length + exponent;
        }

        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
            decpt--;
        }

        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (decpt < -3 || decpt > 17)
        {
            sb.Append(digits[0]).Append('.');
            sb.Append(digits.Length > 1 ? digits[1..] : "0");
            int exp = decpt - 1;
            sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -decpt);
            sb.Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', decpt - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }

        return sb.ToString();
    }

    // ================================================================== 键

    /// <summary>PHP 的数组键归一化：十进制整数字符串会变成 int 键（<see cref="long"/>）。</summary>
    public static object NormalizeKey(object key) => key switch
    {
        long l => l,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        sbyte sb => (long)sb,
        uint ui => (long)ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        string text when IsIntegerString(text) => long.Parse(text, CultureInfo.InvariantCulture),
        _ => key,
    };

    /// <summary><c>"0"</c>、<c>"12"</c>、<c>"-3"</c> 是整数键；<c>"01"</c>、<c>"+1"</c>、<c>"1.0"</c> 不是。</summary>
    private static bool IsIntegerString(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        int i = s[0] == '-' ? 1 : 0;
        if (i == 1 && s.Length == 1)
        {
            return false;
        }

        if (s[i] == '0' && s.Length - i > 1)
        {
            return false;
        }

        for (int j = i; j < s.Length; j++)
        {
            if (s[j] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    // ================================================================== 字符串

    private static void WriteString(StringBuilder sb, string s, int flags)
    {
        bool unescapedUnicode = (flags & JsonUnescapedUnicode) != 0;
        bool unescapedSlashes = (flags & JsonUnescapedSlashes) != 0;
        bool hexTag = (flags & JsonHexTag) != 0;
        bool hexAmp = (flags & JsonHexAmp) != 0;
        bool hexApos = (flags & JsonHexApos) != 0;
        bool hexQuot = (flags & JsonHexQuot) != 0;

        sb.Append('"');
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c == '<' && hexTag)
            {
                sb.Append("\\u003C");
                i++;
            }
            else if (c == '>' && hexTag)
            {
                sb.Append("\\u003E");
                i++;
            }
            else if (c == '&' && hexAmp)
            {
                sb.Append("\\u0026");
                i++;
            }
            else if (c == '\'' && hexApos)
            {
                sb.Append("\\u0027");
                i++;
            }
            else if (c == '"' && hexQuot)
            {
                sb.Append("\\u0022");
                i++;
            }
            else if (c == '"')
            {
                sb.Append("\\\"");
                i++;
            }
            else if (c == '\\')
            {
                sb.Append("\\\\");
                i++;
            }
            else if (c == '/')
            {
                sb.Append(unescapedSlashes ? "/" : "\\/");
                i++;
            }
            else if (c == '\b')
            {
                sb.Append("\\b");
                i++;
            }
            else if (c == '\f')
            {
                sb.Append("\\f");
                i++;
            }
            else if (c == '\n')
            {
                sb.Append("\\n");
                i++;
            }
            else if (c == '\r')
            {
                sb.Append("\\r");
                i++;
            }
            else if (c == '\t')
            {
                sb.Append("\\t");
                i++;
            }
            else if (c < 0x20)
            {
                AppendUnicodeEscape(sb, c);
                i++;
            }
            else if (c <= 0x7F)
            {
                // 含 DEL（0x7F）：PHP 的 ext/json 只对 < 0x20 做 U+00XX 转义
                sb.Append(c);
                i++;
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    throw Malformed();
                }

                if (unescapedUnicode)
                {
                    sb.Append(c).Append(s[i + 1]);
                }
                else
                {
                    // 补充平面在 PHP 里是代理对，两个 \uXXXX
                    AppendUnicodeEscape(sb, c);
                    AppendUnicodeEscape(sb, s[i + 1]);
                }

                i += 2;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw Malformed();
            }
            else if (!unescapedUnicode || c == '\u2028' || c == '\u2029')
            {
                AppendUnicodeEscape(sb, c);
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        sb.Append('"');
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
    {
        sb.Append("\\u");
        for (int shift = 12; shift >= 0; shift -= 4)
        {
            sb.Append(Hex[(c >> shift) & 0xF]);
        }
    }

    /// <summary>PHP 在这种情况下返回 <c>false</c> 并设置 <c>JSON_ERROR_UTF8</c>。</summary>
    public static ArgumentException Malformed() =>
        new("Malformed UTF-8 characters, possibly incorrectly encoded");

    // ================================================================== 解码

    /// <summary>
    /// 等价于 <c>json_decode($json, true)</c>：对象解成 <see cref="PhpArray"/>，
    /// 空对象与空数组无法区分（重新编码都变 <c>[]</c>），
    /// 重复键保留首次位置、取最后一次的值。
    /// </summary>
    public static object? Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            throw new ArgumentException("Syntax error");
        }

        var parser = new Parser(json);
        parser.SkipWhitespace();
        object? value = parser.Value();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw new ArgumentException("Syntax error");
        }

        return value;
    }

    /// <summary>解码并要求顶层是对象/数组，其他一律返回 <c>null</c>（对应 <c>!is_array($data)</c>）。</summary>
    public static PhpArray? DecodeArray(string json)
    {
        try
        {
            return Decode(json) as PhpArray;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;

        internal Parser(string s) => _s = s;

        internal bool AtEnd => _pos >= _s.Length;

        internal void SkipWhitespace()
        {
            while (_pos < _s.Length && _s[_pos] is ' ' or '\t' or '\n' or '\r')
            {
                _pos++;
            }
        }

        internal object? Value()
        {
            if (AtEnd)
            {
                throw new ArgumentException("Syntax error");
            }

            return _s[_pos] switch
            {
                '{' => Object(),
                '[' => Array(),
                '"' => String(),
                't' => Literal("true", true),
                'f' => Literal("false", false),
                'n' => Literal("null", null),
                _ => Number(),
            };
        }

        private object? Literal(string text, object? value)
        {
            if (!_s.AsSpan(_pos).StartsWith(text, StringComparison.Ordinal))
            {
                throw new ArgumentException("Syntax error");
            }

            _pos += text.Length;
            return value;
        }

        private PhpArray Object()
        {
            var result = new PhpArray();
            _pos++; // {
            SkipWhitespace();
            if (!AtEnd && _s[_pos] == '}')
            {
                _pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || _s[_pos] != '"')
                {
                    throw new ArgumentException("Syntax error");
                }

                string key = String();
                SkipWhitespace();
                if (AtEnd || _s[_pos] != ':')
                {
                    throw new ArgumentException("Syntax error");
                }

                _pos++;
                SkipWhitespace();
                // 重复键：保留首次插入位置、覆盖值 —— 与 PHP 相同
                result.Put(NormalizeKey(key), Value());
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new ArgumentException("Syntax error");
                }

                char c = _s[_pos];
                if (c == ',')
                {
                    _pos++;
                }
                else if (c == '}')
                {
                    _pos++;
                    return result;
                }
                else
                {
                    throw new ArgumentException("Syntax error");
                }
            }
        }

        private PhpArray Array()
        {
            var result = new PhpArray();
            _pos++; // [
            SkipWhitespace();
            if (!AtEnd && _s[_pos] == ']')
            {
                _pos++;
                return result;
            }

            long index = 0;
            while (true)
            {
                SkipWhitespace();
                result.Put(index++, Value());
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new ArgumentException("Syntax error");
                }

                char c = _s[_pos];
                if (c == ',')
                {
                    _pos++;
                }
                else if (c == ']')
                {
                    _pos++;
                    return result;
                }
                else
                {
                    throw new ArgumentException("Syntax error");
                }
            }
        }

        private string String()
        {
            _pos++; // "
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new ArgumentException("Syntax error");
                }

                char c = _s[_pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    if (AtEnd)
                    {
                        throw new ArgumentException("Syntax error");
                    }

                    char esc = _s[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append(UnicodeEscape()); break;
                        default: throw new ArgumentException("Syntax error");
                    }
                }
                else if (c < 0x20)
                {
                    throw new ArgumentException("Syntax error");
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private string UnicodeEscape()
        {
            char high = Hex4();
            if (char.IsLowSurrogate(high))
            {
                throw new ArgumentException("Single unpaired UTF-16 surrogate in unicode escape");
            }

            if (char.IsHighSurrogate(high))
            {
                if (!_s.AsSpan(_pos).StartsWith("\\u", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Single unpaired UTF-16 surrogate in unicode escape");
                }

                _pos += 2;
                char low = Hex4();
                if (!char.IsLowSurrogate(low))
                {
                    throw new ArgumentException("Single unpaired UTF-16 surrogate in unicode escape");
                }

                return new string([high, low]);
            }

            return high.ToString();
        }

        private char Hex4()
        {
            if (_pos + 4 > _s.Length)
            {
                throw new ArgumentException("Syntax error");
            }

            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexDigit(_s[_pos + i]);
                if (digit < 0)
                {
                    throw new ArgumentException("Syntax error");
                }

                value = (value * 16) + digit;
            }

            _pos += 4;
            return (char)value;
        }

        private static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        private object Number()
        {
            int start = _pos;
            if (!AtEnd && _s[_pos] == '-')
            {
                _pos++;
            }

            if (AtEnd)
            {
                throw new ArgumentException("Syntax error");
            }

            if (_s[_pos] == '0')
            {
                _pos++;
            }
            else if (_s[_pos] is >= '1' and <= '9')
            {
                while (!AtEnd && char.IsAsciiDigit(_s[_pos]))
                {
                    _pos++;
                }
            }
            else
            {
                throw new ArgumentException("Syntax error");
            }

            bool integer = true;
            if (!AtEnd && _s[_pos] == '.')
            {
                integer = false;
                _pos++;
                if (AtEnd || !char.IsAsciiDigit(_s[_pos]))
                {
                    throw new ArgumentException("Syntax error");
                }

                while (!AtEnd && char.IsAsciiDigit(_s[_pos]))
                {
                    _pos++;
                }
            }

            if (!AtEnd && (_s[_pos] is 'e' or 'E'))
            {
                integer = false;
                _pos++;
                if (!AtEnd && (_s[_pos] is '+' or '-'))
                {
                    _pos++;
                }

                if (AtEnd || !char.IsAsciiDigit(_s[_pos]))
                {
                    throw new ArgumentException("Syntax error");
                }

                while (!AtEnd && char.IsAsciiDigit(_s[_pos]))
                {
                    _pos++;
                }
            }

            string text = _s[start.._pos];
            if (integer)
            {
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
                {
                    return parsed;
                }

                // PHP：超出 int64 的整数变成 float
                return double.Parse(text, CultureInfo.InvariantCulture);
            }

            return double.Parse(text, CultureInfo.InvariantCulture);
        }
    }
}
