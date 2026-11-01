using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP <c>unserialize()</c> 的移植（语料：<c>conformance/data/cookie.json</c>）。
///
/// <p>失败时必须复现 PHP 的 <c>Error at offset N of M bytes</c>：Laravel 的错误处理器
/// 会把这条 warning 提升成 <c>ErrorException</c>，偏移量是上层可见行为的一部分。
/// 因此解析器逐分支照抄了「p 停在哪里」的差异（例如值的位置遇到 <c>}</c> 时不推进 p）。</p>
///
/// <p>不支持枚举（<c>E:</c> 一律失败）；<c>O:</c> / <c>C:</c> 解析成
/// <see cref="PhpObject"/>，未知类退化为 <c>__PHP_Incomplete_Class</c>。
/// 字符串按字节长度切但用 UTF-8 解码（非法字节变成 U+FFFD，见 PORT-NOTES）。</p>
/// </summary>
public sealed class PhpUnserialize
{
    /// <summary>PHP 哈希表容量上限 <c>HT_MAX_SIZE</c>。</summary>
    private const long HtMaxSize = 1L << 30;

    /// <summary><c>MAX_LENGTH_OF_LONG - 1</c>（64 位平台是 19）。</summary>
    private const int MaxDigits = 19;

    /// <summary><c>E:</c> 会命中「is not an enum」警告的内置类型名。</summary>
    private static readonly HashSet<string> KnownInternalTypes =
    [
        "UnitEnum", "BackedEnum", "Stringable", "Traversable", "Countable",
        "ArrayAccess", "Iterator", "IteratorAggregate", "IteratorIterator",
        "JsonSerializable", "Throwable", "Serializable", "DateTimeInterface",
        "Closure", "Generator", "WeakMap", "stdClass",
        "DateTime", "DateTimeImmutable", "ArrayObject", "SplObjectStorage",
    ];

    /// <summary>「已定义」类集合：其余 <c>O:</c> 一律退化成 <c>__PHP_Incomplete_Class</c>。</summary>
    private static readonly HashSet<string> KnownClasses = [.. KnownInternalTypes];

    /// <summary>解析结果；<c>Ok=false</c> 时 <c>Value</c> 是 <c>false</c>。</summary>
    public sealed record Result(bool Ok, object? Value, IReadOnlyList<string> Warnings)
    {
        /// <summary>第一条警告（Laravel 只把第一条变成异常），没有则 null。</summary>
        public string? FirstWarning => Warnings.Count == 0 ? null : Warnings[0];
    }

    private readonly byte[] _buf;

    private readonly List<string> _warnings = [];

    /// <summary>
    /// 引用表（php-src 的 <c>var_hash</c>）：每个值在<b>开始解析</b>时入库；
    /// 以大写 <c>R</c> 开头的值不占 slot（小写 <c>r</c> 占）。
    /// </summary>
    private readonly List<object?> _varTable = [];

    private int _p;

    private PhpUnserialize(byte[] buf) => _buf = buf;

    /// <summary>等价于 <c>unserialize($input)</c>。</summary>
    public static Result Parse(string? input) => Parse(input is null ? [] : Encoding.UTF8.GetBytes(input));

    /// <summary>等价于 <c>unserialize($input)</c>，输入是原始字节。</summary>
    public static Result Parse(byte[] input)
    {
        // php_unserialize_with_options(): buf_len == 0 → RETURN_FALSE，一条警告都不报
        if (input.Length == 0)
        {
            return new Result(false, false, []);
        }

        var parser = new PhpUnserialize(input);
        try
        {
            object? value = parser.ParseOne();

            if (parser._p < input.Length)
            {
                parser._warnings.Add(
                    $"Extra data starting at offset {parser._p} of {input.Length} bytes");
            }

            return new Result(true, value, parser._warnings);
        }
        catch (FailureException)
        {
            parser._warnings.Add($"Error at offset {parser._p} of {input.Length} bytes");
            return new Result(false, false, parser._warnings);
        }
    }

    // ================================================================== 解析

    /// <summary>控制流用异常，不携带信息：p 已经停在 PHP 会停的位置。</summary>
    private sealed class FailureException : Exception;

    private static FailureException Fail() => throw new FailureException();

    /// <summary>读第 i 个字节；越界当作 C 字符串结尾的 NUL。</summary>
    private int At(int i) => i >= 0 && i < _buf.Length ? _buf[i] : 0;

    private object? ParseOne() => ParseOne(false);

    private object? ParseOne(bool keyMode)
    {
        if (_p >= _buf.Length)
        {
            return Fail();
        }

        // 键位解析不占 slot；以大写 R 开头的值也不占 slot
        int slot = !keyMode && At(_p) != 'R' ? _varTable.Count : -1;
        if (slot >= 0)
        {
            _varTable.Add(null);
        }

        object? value = ParseValue(keyMode);

        if (slot >= 0)
        {
            _varTable[slot] = value;
        }

        return value;
    }

    private object? ParseValue(bool keyMode)
    {
        int start = _p;
        return At(_p) switch
        {
            'N' => ParseNull(),
            'b' => ParseBool(),
            'i' => ParseInt(start),
            'd' => ParseDouble(),
            's' => ParseString(),
            'S' => ParseEscapedString(),
            'a' => ParseArray(keyMode),
            'O' or 'C' => ParseObject(start, keyMode),
            'R' or 'r' => ParseReference(keyMode, At(start) == 'r'),
            'E' => ParseEnum(),
            '}' => UnexpectedEnd(),
            _ => Fail(),
        };
    }

    private object? ParseNull()
    {
        if (At(_p + 1) == ';')
        {
            _p += 2;
            return null;
        }

        return Fail();
    }

    private object? UnexpectedEnd()
    {
        _warnings.Add("Unexpected end of serialized data");
        return Fail();
    }

    private object? ParseBool()
    {
        if (At(_p + 1) != ':')
        {
            return Fail();
        }

        int digit = At(_p + 2);
        if (digit != '0' && digit != '1')
        {
            return Fail();
        }

        if (At(_p + 3) != ';')
        {
            return Fail();
        }

        _p += 4;
        return digit == '1';
    }

    private object? ParseInt(int start)
    {
        int q = _p + 2;
        if (At(q) != '+' && At(q) != '-' && !IsDigit(At(q)))
        {
            return Fail();
        }

        int cursor = q;
        if (At(cursor) == '-')
        {
            cursor++;
        }
        else if (At(cursor) == '+')
        {
            cursor++;
        }

        if (!IsDigit(At(cursor)))
        {
            return Fail();
        }

        while (IsDigit(At(cursor)))
        {
            cursor++;
        }

        if (At(cursor) != ';')
        {
            return Fail();
        }

        _p = cursor + 1;
        return ParseIv(start + 2, cursor);
    }

    /// <summary>
    /// PHP 的 <c>parse_iv2()</c>：跳过符号与前导零后逐位累加；
    /// 有效数字超过 18 位或结果超过 <c>LONG_MAX + neg</c> 时饱和并报警告。
    /// </summary>
    private long ParseIv(int begin, int end)
    {
        int cursor = begin;
        bool neg = false;
        if (At(cursor) == '-')
        {
            neg = true;
            cursor++;
        }
        else if (At(cursor) == '+')
        {
            cursor++;
        }

        while (At(cursor) == '0')
        {
            cursor++;
        }

        int digits = cursor;
        long result = 0;
        bool overflow = false;

        while (cursor < end)
        {
            int digit = At(cursor) - '0';
            if (result < (long.MinValue + digit) / 10)
            {
                overflow = true;
            }
            else
            {
                result = (result * 10) - digit;
            }

            cursor++;
        }

        int length = cursor - digits;
        if (length > MaxDigits - 1)
        {
            overflow = true;
        }
        else if (!neg && result == long.MinValue)
        {
            overflow = true;
        }

        if (overflow)
        {
            _warnings.Add("Numerical result out of range");
            return neg ? long.MinValue : long.MaxValue;
        }

        return neg ? result : -result;
    }

    private object? ParseDouble()
    {
        int q = _p + 2;
        if (At(q) == 'N' && At(q + 1) == 'A' && At(q + 2) == 'N' && At(q + 3) == ';')
        {
            _p = q + 4;
            return double.NaN;
        }

        if (At(q) == 'I' && At(q + 1) == 'N' && At(q + 2) == 'F' && At(q + 3) == ';')
        {
            _p = q + 4;
            return double.PositiveInfinity;
        }

        if (At(q) == '-' && At(q + 1) == 'I' && At(q + 2) == 'N' && At(q + 3) == 'F' && At(q + 4) == ';')
        {
            _p = q + 5;
            return double.NegativeInfinity;
        }

        int cursor = q;
        if (At(cursor) == '+' || At(cursor) == '-')
        {
            cursor++;
        }

        int before = cursor;
        while (IsDigit(At(cursor)))
        {
            cursor++;
        }

        bool hasInt = cursor > before;
        bool hasFraction = false;

        if (At(cursor) == '.' && (hasInt || IsDigit(At(cursor + 1))))
        {
            int dot = cursor;
            cursor++;
            int fractionStart = cursor;
            while (IsDigit(At(cursor)))
            {
                cursor++;
            }

            hasFraction = cursor > fractionStart;
            if (!hasFraction && !hasInt)
            {
                _p = dot;
                return Fail();
            }
        }

        if (!hasInt && !hasFraction)
        {
            return Fail();
        }

        if (At(cursor) == 'e' || At(cursor) == 'E')
        {
            int expMark = cursor;
            cursor++;
            if (At(cursor) == '+' || At(cursor) == '-')
            {
                cursor++;
            }

            if (!IsDigit(At(cursor)))
            {
                _p = expMark;
                return Fail();
            }

            while (IsDigit(At(cursor)))
            {
                cursor++;
            }
        }

        if (At(cursor) != ';')
        {
            return Fail();
        }

        string literal = Encoding.Latin1.GetString(_buf, q, cursor - q);
        _p = cursor + 1;
        return double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsDigit(int c) => c is >= '0' and <= '9';

    private object? ParseString()
    {
        int q = _p + 2;
        int lenStart = q;
        if (!IsDigit(At(q)))
        {
            return Fail();
        }

        long len = 0;
        while (IsDigit(At(q)))
        {
            len = (len * 10) + (At(q) - '0');
            q++;
            if (len > int.MaxValue)
            {
                _p = lenStart;
                return Fail();
            }
        }

        if (At(q) != ':' || At(q + 1) != '"')
        {
            return Fail();
        }

        q += 2;
        int maxlen = _buf.Length - q;
        if (maxlen < len)
        {
            _p = lenStart;
            return Fail();
        }

        int raw = q;
        q += (int)len;
        if (At(q) != '"')
        {
            _p = q;
            return Fail();
        }

        if (At(q + 1) != ';')
        {
            _p = q + 1;
            return Fail();
        }

        _p = q + 2;
        return Encoding.UTF8.GetString(_buf, raw, (int)len);
    }

    private object? ParseEscapedString()
    {
        int q = _p + 2;
        int lenStart = q;
        if (!IsDigit(At(q)))
        {
            return Fail();
        }

        long len = 0;
        while (IsDigit(At(q)))
        {
            len = (len * 10) + (At(q) - '0');
            q++;
        }

        if (At(q) != ':' || At(q + 1) != '"')
        {
            return Fail();
        }

        q += 2;
        int maxlen = _buf.Length - q;
        if (maxlen < len)
        {
            _p = lenStart;
            return Fail();
        }

        byte[] output = new byte[(int)len];
        int written = 0;
        while (written < (int)len && q < _buf.Length)
        {
            if (At(q) != '\\')
            {
                output[written++] = _buf[q++];
                continue;
            }

            int high = HexValue(At(q + 1));
            int low = HexValue(At(q + 2));
            if (high < 0 || low < 0)
            {
                return Fail();
            }

            output[written++] = (byte)((high * 16) + low);
            q += 3;
        }

        if (At(q) != '"')
        {
            _p = q;
            return Fail();
        }

        if (At(q + 1) != ';')
        {
            _p = q + 1;
            return Fail();
        }

        _p = q + 2;
        _warnings.Add("Unserializing the 'S' format is deprecated");
        return Encoding.UTF8.GetString(output, 0, written);
    }

    private static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private object? ParseArray(bool keyMode)
    {
        int q = _p + 2;
        if (!IsDigit(At(q)))
        {
            return Fail();
        }

        int countStart = q;
        while (IsDigit(At(q)))
        {
            q++;
        }

        long elements = ParseIv(countStart, q);
        if (At(q) != ':' || At(q + 1) != '{')
        {
            return Fail();
        }

        q += 2;
        _p = q;

        if (elements >= HtMaxSize)
        {
            return Fail();
        }

        // IS_FAKE_ELEM_COUNT：每个元素至少占 2 字节
        if (elements > (_buf.Length - q) / 2)
        {
            return Fail();
        }

        if (keyMode)
        {
            return Fail();
        }

        var array = new PhpArray();

        // 容器在解析内容前写入自己的 slot（进行中的容器能被 R: 观察到）
        _varTable[^1] = array;

        for (long i = 0; i < elements; i++)
        {
            object? key = ParseOne(true);
            if (key is not long and not string)
            {
                return Fail();
            }

            object? value = ParseOne();

            if (elements > 0 && _p > 0)
            {
                int last = At(_p - 1);
                if (last != ';' && last != '}')
                {
                    _p--;
                    return Fail();
                }
            }

            array.Put(PhpJson.NormalizeKey(key), value);
        }

        if (At(_p) != '}')
        {
            return Fail();
        }

        _p++;
        return array;
    }

    private object? ParseObject(int start, bool keyMode)
    {
        bool custom = At(start) == 'C';
        int q = _p + 2;
        if (!IsDigit(At(q)))
        {
            return Fail();
        }

        int lenStart = q;
        int len = 0;
        while (IsDigit(At(q)))
        {
            len = (len * 10) + (At(q) - '0');
            q++;
        }

        if (len == 0)
        {
            return Fail();
        }

        int maxlen = _buf.Length - q;
        if (maxlen < len)
        {
            _p = lenStart;
            return Fail();
        }

        if (At(q) != ':' || At(q + 1) != '"')
        {
            return Fail();
        }

        q += 2;
        int nameStart = q;
        if (At(nameStart) == 0 || At(nameStart) == '\\')
        {
            return Fail();
        }

        q = nameStart + len;
        if (At(q) != '"')
        {
            _p = q;
            return Fail();
        }

        if (At(q + 1) != ':')
        {
            _p = q + 1;
            return Fail();
        }

        string className = Encoding.UTF8.GetString(_buf, nameStart, len);
        bool incomplete = !KnownClasses.Contains(className);
        string resolvedName = incomplete ? "__PHP_Incomplete_Class" : className;

        q++;
        if (custom)
        {
            return ParseCustomObject(resolvedName, q, keyMode);
        }

        if (keyMode)
        {
            return Fail();
        }

        int countStart = q + 1;
        int cursor = countStart;
        if (At(cursor) == '-' || At(cursor) == '+')
        {
            cursor++;
        }

        if (!IsDigit(At(cursor)))
        {
            _p = cursor;
            return Fail();
        }

        while (IsDigit(At(cursor)))
        {
            cursor++;
        }

        long elements = ParseIv(countStart, cursor);
        if (At(cursor) != ':' || At(cursor + 1) != '{')
        {
            _p = cursor;
            return Fail();
        }

        if (elements < 0 || elements >= HtMaxSize)
        {
            _p = cursor;
            return Fail();
        }

        _p = cursor + 2;

        if (elements > (_buf.Length - _p) / 2)
        {
            return Fail();
        }

        var obj = new PhpObject(resolvedName, new PhpArray());
        PhpArray props = obj.Properties;
        if (incomplete)
        {
            props.Put("__PHP_Incomplete_Class_Name", className);
        }

        _varTable[^1] = obj;

        for (long i = 0; i < elements; i++)
        {
            object? key = ParseOne(true);
            if (key is not long and not string)
            {
                return Fail();
            }

            object? value = ParseOne();
            props.Put(PhpJson.NormalizeKey(key), value);
        }

        if (At(_p) != '}')
        {
            return Fail();
        }

        _p++;
        return obj;
    }

    private object? ParseCustomObject(string resolvedName, int q, bool keyMode)
    {
        if (keyMode)
        {
            return Fail();
        }

        if (At(q) != ':')
        {
            _p = q;
            return Fail();
        }

        int lenStart = q + 1;
        int cursor = lenStart;
        if (!IsDigit(At(cursor)))
        {
            _p = cursor;
            return Fail();
        }

        long payloadLen = 0;
        while (IsDigit(At(cursor)))
        {
            payloadLen = (payloadLen * 10) + (At(cursor) - '0');
            cursor++;
        }

        long overflowPayload = ParseIv(lenStart, cursor);

        if (At(cursor) != ':')
        {
            _p = cursor;
            return Fail();
        }

        if (At(cursor + 1) != '{')
        {
            _p = cursor + 1;
            return Fail();
        }

        _p = cursor + 2;

        long present = _buf.Length - _p;
        if (present <= payloadLen)
        {
            _warnings.Add(
                $"Insufficient data for unserializing - {overflowPayload} required, {present} present");
            return Fail();
        }

        if (At((int)(_p + payloadLen)) != '}')
        {
            _p += (int)payloadLen;
            return Fail();
        }

        _p += (int)payloadLen + 1;
        _warnings.Add($"Class {resolvedName} has no unserializer");
        return new PhpObject(resolvedName, new PhpArray());
    }

    private object? ParseReference(bool keyMode, bool lowercase)
    {
        int q = _p + 2;
        int cursor = q;
        if (!IsDigit(At(cursor)))
        {
            return Fail();
        }

        while (IsDigit(At(cursor)))
        {
            cursor++;
        }

        if (At(cursor) != ';')
        {
            return Fail();
        }

        // 步骤 1：p 推到 token 之后（失败报错也停这里）
        _p = cursor + 1;

        // 步骤 2：键位失败
        if (keyMode)
        {
            return Fail();
        }

        long id = ParseUivDigits(q, cursor) - 1;

        // 步骤 3：R:0; → id == -1；表外失败
        if (id == -1 || id >= _varTable.Count)
        {
            return Fail();
        }

        object? target = _varTable[(int)id];

        // 步骤 4（仅小写 r）：指到自己刚 push 的 slot
        if (lowercase && id == _varTable.Count - 1)
        {
            return Fail();
        }

        // 步骤 5（仅小写 r）：目标必须是对象
        if (lowercase && target is not PhpObject)
        {
            return Fail();
        }

        return target;
    }

    private long ParseUivDigits(int q, int end)
    {
        long value = 0;
        for (int i = q; i < end; i++)
        {
            value = (value * 10) + (At(i) - '0');
        }

        return value;
    }

    private object? ParseEnum()
    {
        int q = _p + 2;
        int lenStart = q;
        if (!IsDigit(At(q)))
        {
            return Fail();
        }

        int len = 0;
        while (IsDigit(At(q)))
        {
            len = (len * 10) + (At(q) - '0');
            q++;
        }

        if (len == 0)
        {
            return Fail();
        }

        if (At(q) != ':' || At(q + 1) != '"')
        {
            return Fail();
        }

        q += 2;
        if (_buf.Length - q < len)
        {
            _p = lenStart;
            return Fail();
        }

        int nameStart = q;
        q = nameStart + len;
        if (At(q) != '"')
        {
            _p = q;
            return Fail();
        }

        if (At(q + 1) != ';')
        {
            _p = q + 1;
            return Fail();
        }

        string body = Encoding.UTF8.GetString(_buf, nameStart, len);
        int colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            _warnings.Add($"Invalid enum name '{body}' (missing colon)");
            return Fail();
        }

        string className = body[..colon];
        _warnings.Add($"Class '{className}' "
            + (KnownInternalTypes.Contains(className) ? "is not an enum" : "not found"));
        return Fail();
    }
}
