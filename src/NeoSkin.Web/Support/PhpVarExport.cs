using System.Globalization;
using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP <c>var_export()</c> 的读写工具。
///
/// <p><c>storage/options.php</c> 是 <c>php artisan options:cache</c> 生成的 PHP 文件，
/// 内容是 <c>return array ( 'k' =&gt; 'v', ... );</c>。必须能读懂它，否则已跑过
/// <c>options:cache</c> 的站点迁移过来后选项会全部退回默认值；反过来也要能写出同样格式，
/// 让 PHP 版仍可读取。</p>
/// </summary>
public static class PhpVarExport
{
    // ------------------------------------------------------------------ 读

    /// <summary>解析 <c>var_export()</c> 输出中的数组字面量。</summary>
    public static Dictionary<string, object?> ParseArray(string text)
    {
        var parser = new Parser(text);
        parser.SkipWhitespace();
        int array = text.IndexOf("array", parser.Position, StringComparison.Ordinal);
        if (array < 0)
        {
            throw new ArgumentException("not a PHP var_export array");
        }

        parser.Position = array;
        object? value = parser.ParseValue();
        if (value is not PhpArray map)
        {
            throw new ArgumentException("top-level value is not an array");
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<object, object?> entry in map)
        {
            result[PhpString.Cast(entry.Key)] = entry.Value;
        }

        return result;
    }

    private sealed class Parser(string text)
    {
        internal int Position { get; set; }

        internal void SkipWhitespace()
        {
            while (Position < text.Length && char.IsWhiteSpace(text[Position]))
            {
                Position++;
            }
        }

        internal object? ParseValue()
        {
            SkipWhitespace();
            if (Position >= text.Length)
            {
                throw new ArgumentException("unexpected end of input");
            }

            char c = text[Position];
            if (c == '\'')
            {
                return ParseString();
            }

            if (c == '"')
            {
                return ParseDoubleQuoted();
            }

            if (text.AsSpan(Position).StartsWith("array", StringComparison.Ordinal))
            {
                return ParseArrayValue();
            }

            if (StartsWithIgnoreCase("true"))
            {
                Position += 4;
                return true;
            }

            if (StartsWithIgnoreCase("false"))
            {
                Position += 5;
                return false;
            }

            if (StartsWithIgnoreCase("null"))
            {
                Position += 4;
                return null;
            }

            return ParseNumber();
        }

        private bool StartsWithIgnoreCase(string word) =>
            string.Compare(text, Position, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private PhpArray ParseArrayValue()
        {
            Position += "array".Length;
            SkipWhitespace();
            Expect('(');
            var map = new PhpArray();
            long autoKey = 0;
            while (true)
            {
                SkipWhitespace();
                if (Position < text.Length && text[Position] == ')')
                {
                    Position++;
                    return map;
                }

                object? key = ParseValue();
                SkipWhitespace();
                Expect('=');
                Expect('>');
                object? value = ParseValue();
                SkipWhitespace();
                if (Position < text.Length && text[Position] == ',')
                {
                    Position++;
                }

                if (key is null)
                {
                    key = autoKey++;
                }
                else if (key is long l)
                {
                    autoKey = l + 1;
                }

                map.Put(key, value);
            }
        }

        private string ParseString()
        {
            Expect('\'');
            var sb = new StringBuilder();
            while (Position < text.Length)
            {
                char c = text[Position++];
                if (c == '\\')
                {
                    // PHP 单引号串只转义 \' 和 \\
                    sb.Append(text[Position++]);
                }
                else if (c == '\'')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            throw new ArgumentException("unterminated string");
        }

        private string ParseDoubleQuoted()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (Position < text.Length)
            {
                char c = text[Position++];
                if (c == '\\')
                {
                    sb.Append(text[Position++]);
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            throw new ArgumentException("unterminated string");
        }

        private object ParseNumber()
        {
            int start = Position;
            if (Position < text.Length && (text[Position] == '-' || text[Position] == '+'))
            {
                Position++;
            }

            bool floating = false;
            while (Position < text.Length)
            {
                char c = text[Position];
                if (char.IsAsciiDigit(c))
                {
                    Position++;
                }
                else if (c is '.' or 'e' or 'E')
                {
                    floating = true;
                    Position++;
                }
                else if ((c is '-' or '+') && (text[Position - 1] is 'e' or 'E'))
                {
                    Position++;
                }
                else
                {
                    break;
                }
            }

            string token = text[start..Position];
            if (token.Length == 0 || token is "-" or "+")
            {
                throw new ArgumentException("not a number at offset " + start);
            }

            if (floating)
            {
                return double.Parse(token, CultureInfo.InvariantCulture);
            }

            return long.Parse(token, CultureInfo.InvariantCulture);
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (Position >= text.Length || text[Position] != expected)
            {
                throw new ArgumentException($"expected '{expected}' at offset {Position}");
            }

            Position++;
        }
    }

    // ------------------------------------------------------------------ 写

    /// <summary>生成与 <c>options:cache</c> 完全一致的 PHP 文件内容。</summary>
    public static string ToPhpFile(IEnumerable<KeyValuePair<string, object?>> items)
    {
        var sb = new StringBuilder("<?php\n");
        sb.Append("// This is auto-generated. DO NOT edit manually.\n");
        sb.Append("return array (\n");
        foreach (KeyValuePair<string, object?> entry in items)
        {
            sb.Append("  ").Append(Export(entry.Key)).Append(" => ")
                .Append(Export(entry.Value)).Append(",\n");
        }

        sb.Append(");");
        return sb.ToString();
    }

    /// <summary>单值导出，与 PHP <c>var_export()</c> 的写法一致。</summary>
    public static string Export(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "true" : "false",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        PhpArray map => ExportArray(map),
        System.Collections.IDictionary dict => ExportMap(dict),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "'" + value.ToString()!.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
    };

    private static string ExportArray(PhpArray map)
    {
        var sb = new StringBuilder("array (\n");
        foreach (KeyValuePair<object, object?> entry in map)
        {
            sb.Append("    ").Append(Export(entry.Key)).Append(" => ")
                .Append(Export(entry.Value)).Append(",\n");
        }

        return sb.Append("  )").ToString();
    }

    private static string ExportMap(System.Collections.IDictionary map)
    {
        var sb = new StringBuilder("array (\n");
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            sb.Append("    ").Append(Export(entry.Key)).Append(" => ")
                .Append(Export(entry.Value)).Append(",\n");
        }

        return sb.Append("  )").ToString();
    }
}
