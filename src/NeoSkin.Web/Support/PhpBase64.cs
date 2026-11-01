using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP <c>base64_encode()</c> / <c>base64_decode($s)</c> / <c>base64_decode($s, true)</c>
/// 的移植。
///
/// <p>PHP 的宽松解码与 RFC 4648 差得很远，而 cookie 解密链路上每一步都用宽松模式，
/// 差一个字节就会解出不同的明文：</p>
/// <list type="bullet">
///   <item><c>"YQ"</c>（长度不是 4 的倍数）→ <c>"a"</c>；</item>
///   <item><c>"YQ==A"</c>（填充后还有数据）→ <c>"a\0"</c>；</item>
///   <item><c>"a-b_"</c> / <c>"YYYYY"</c> 等 URL 字母表或非法长度也照吃。</item>
/// </list>
///
/// <p>逐行照抄 PHP 8.5 的 <c>php_base64_decode_impl()</c>：
/// <c>=</c> 只累加 padding、不查表；反向表里 <c>\t \n \r 空格</c> 是 -1（宽松/严格都跳过），
/// 其余非法字节是 -2（宽松跳过、严格失败）；输出按 <c>i % 4</c> 累积，
/// 尾组不完整时最后多写的字节不计入结果。</p>
/// </summary>
public static class PhpBase64
{
    private static readonly char[] Table =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".ToCharArray();

    /// <summary>与 C 版 <c>base64_reverse_table</c> 逐项一致。</summary>
    private static readonly int[] Reverse = BuildReverse();

    private static int[] BuildReverse()
    {
        int[] table = new int[256];
        Array.Fill(table, -2);
        for (int i = 0; i < Table.Length; i++)
        {
            table[Table[i]] = i;
        }

        // PHP 只把 \t \n \r 和空格当空白；\v / \f 都是 -2
        table['\t'] = -1;
        table['\n'] = -1;
        table['\r'] = -1;
        table[' '] = -1;
        return table;
    }

    // ================================================================== 编码

    /// <summary>等价于 PHP 的 <c>base64_encode($bytes)</c>。</summary>
    public static string Encode(byte[]? input)
    {
        if (input is null || input.Length == 0)
        {
            return "";
        }

        var sb = new StringBuilder((input.Length + 2) / 3 * 4);
        int i = 0;
        while (input.Length - i > 2)
        {
            int b0 = input[i];
            int b1 = input[i + 1];
            int b2 = input[i + 2];
            sb.Append(Table[b0 >> 2]);
            sb.Append(Table[((b0 & 0x03) << 4) + (b1 >> 4)]);
            sb.Append(Table[((b1 & 0x0F) << 2) + (b2 >> 6)]);
            sb.Append(Table[b2 & 0x3F]);
            i += 3;
        }

        int rest = input.Length - i;
        if (rest != 0)
        {
            int b0 = input[i];
            sb.Append(Table[b0 >> 2]);
            if (rest > 1)
            {
                int b1 = input[i + 1];
                sb.Append(Table[((b0 & 0x03) << 4) + (b1 >> 4)]);
                sb.Append(Table[(b1 & 0x0F) << 2]);
                sb.Append('=');
            }
            else
            {
                sb.Append(Table[(b0 & 0x03) << 4]);
                sb.Append("==");
            }
        }

        return sb.ToString();
    }

    /// <summary>等价于 <c>base64_encode($s)</c>（<paramref name="s"/> 按 UTF-8 取字节）。</summary>
    public static string Encode(string? s) => s is null ? "" : Encode(Encoding.UTF8.GetBytes(s));

    // ================================================================== 解码

    /// <summary>等价于 <c>base64_decode($s)</c>（宽松模式，永不失败）。</summary>
    public static byte[] Decode(string? s) => Decode(s, false) ?? [];

    /// <summary>
    /// 等价于 <c>base64_decode($s, $strict)</c>；严格模式失败返回 <c>null</c>
    /// （对应 PHP 的 <c>false</c>）。
    /// </summary>
    public static byte[]? Decode(string? s, bool strict)
    {
        byte[] input = s is null ? [] : Encoding.UTF8.GetBytes(s);
        // 输出永远不超过输入长度；多留几个字节是因为 case 1/2 会「先写后提交」
        byte[] output = new byte[input.Length + 4];
        int i = 0;
        int padding = 0;
        int j = 0;

        foreach (byte raw in input)
        {
            int ch = raw;
            if (ch == '=')
            {
                padding++;
                continue;
            }

            int value = Reverse[ch];
            if (!strict)
            {
                if (value < 0)
                {
                    continue;
                }
            }
            else
            {
                if (value == -1)
                {
                    continue;
                }

                if (value == -2 || padding != 0)
                {
                    return null;
                }
            }

            switch (i % 4)
            {
                case 0:
                    output[j] = (byte)(value << 2);
                    break;
                case 1:
                    output[j] |= (byte)(value >> 4);
                    j++;
                    output[j] = (byte)((value & 0x0F) << 4);
                    break;
                case 2:
                    output[j] |= (byte)(value >> 2);
                    j++;
                    output[j] = (byte)((value & 0x03) << 6);
                    break;
                default:
                    output[j] |= (byte)value;
                    j++;
                    break;
            }

            i++;
        }

        if (strict && i % 4 == 1)
        {
            return null;
        }

        if (strict && padding != 0 && (padding > 2 || (i + padding) % 4 != 0))
        {
            return null;
        }

        return output[..j];
    }

    /// <summary>严格解码失败时返回 <c>true</c>（便于直读 PHP 语义）。</summary>
    public static bool FailsStrictly(string? s) => Decode(s, true) is null;
}
