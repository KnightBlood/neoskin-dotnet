using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NeoSkin.Web.Support;

/// <summary>
/// Symfony YAML 语言文件语法的移植：<c>Symfony\Component\Yaml\Yaml::parseFile</c>
/// 的语义子集（解析 <c>Resources/lang/&lt;locale&gt;/&lt;group&gt;.yml</c>）。
///
/// <p>不能直接用 YamlDotNet 的隐式类型（resolver 默认 <c>yes</c>/<c>no</c> 是布尔），
/// Symfony 的标量规则是：</p>
/// <list type="number">
///   <item>带引号 / 块标量永远是字符串；</item>
///   <item><c>null</c>/<c>NULL</c>/<c>~</c>/空 → null；</item>
///   <item>只有 <c>true/True/TRUE/false/False/FALSE</c> 是布尔，<c>yes</c>/<c>no</c> 是字符串；</item>
///   <item><c>0o…</c> 八进制、<c>0x…</c> 十六进制 → 十进制 int；</item>
///   <item>整数要去下划线后 <c>(string)(int) === $scalar</c> 才转 int（<c>007</c> 保持字符串）；</item>
///   <item>数值形态 → double；其余字符串。</item>
/// </list>
/// </summary>
public static class SymfonyYaml
{
    private static readonly Regex Numeric = new(
        @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$");

    private static readonly Regex Hex = new(@"^0x[0-9a-fA-F_]+$");

    private static readonly Regex Octal = new(@"^(?:\+|-)?0o[0-7_]+$");

    private static readonly Regex DecimalUnderscores = new(@"^(-|\+)?[0-9][0-9_]*(\.[0-9_]+)?$");

    private static readonly Regex PlainIntegerUnderscores = new(@"^[+-]?[0-9][0-9_]*$");

    /// <summary>从工作目录加载并解析；找不到文件返回 <c>null</c>。</summary>
    public static Dictionary<string, object?>? ParseResource(string resourcePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, resourcePath.TrimStart('/', '\\'));
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    /// <summary>解析一份 YAML（顶层必须是映射）。</summary>
    public static Dictionary<string, object?> Parse(TextReader reader)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(reader);
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException("YAML 解析失败: " + e.Message, e);
        }

        if (stream.Documents.Count == 0)
        {
            return [];
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("翻译文件顶层必须是映射（YAML Map）");
        }

        return ConvertMapping(root);
    }

    private static readonly HashSet<string> KnownTags =
    [
        "tag:yaml.org,2002:str", "tag:yaml.org,2002:int", "tag:yaml.org,2002:float",
        "tag:yaml.org,2002:bool", "tag:yaml.org,2002:null", "tag:yaml.org,2002:seq",
        "tag:yaml.org,2002:map", "tag:yaml.org,2002:timestamp", "tag:yaml.org,2002:merge",
        "tag:yaml.org,2002:value", "tag:yaml.org,2002:binary", "tag:yaml.org,2002:omap",
        "tag:yaml.org,2002:pairs", "tag:yaml.org,2002:set",
    ];

    private static object? ConvertNode(YamlNode node)
    {
        if (!node.Tag.IsEmpty && !node.Tag.IsNonSpecific)
        {
            string tag = node.Tag.Value;
            if (!KnownTags.Contains(tag))
            {
                // Symfony 对未知 tag 直接抛 ParseException；YamlDotNet 会保留 tag 继续解析
                throw new NotSupportedException("未支持的 YAML tag: " + tag);
            }
        }

        return node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping),
            YamlSequenceNode sequence => sequence.Children.Select(ConvertNode).ToList(),
            YamlScalarNode scalar => ResolveScalar(scalar),
            _ => throw new InvalidOperationException("未知 YAML 节点: " + node),
        };
    }

    private static Dictionary<string, object?> ConvertMapping(YamlMappingNode mapping)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode)
            {
                throw new InvalidOperationException("YAML 键必须是标量");
            }

            string key = keyNode.Style == ScalarStyle.Plain
                ? PhpString.Cast(Evaluate(keyNode.Value ?? ""))
                : keyNode.Value ?? "";

            if (!result.TryAdd(key, ConvertNode(entry.Value)))
            {
                throw new InvalidOperationException("YAML 重复键: " + key);
            }
        }

        return result;
    }

    /// <summary>标量求值：引号 / 块标量保持字符串。</summary>
    public static object? ResolveScalar(YamlScalarNode node) =>
        node.Style == ScalarStyle.Plain ? Evaluate(node.Value ?? "") : node.Value;

    /// <summary>
    /// Symfony <c>Inline::evaluateScalar()</c>（flags=0）的裸标量求值。
    /// 注意 PHP 一进来先 <c>trim($scalar)</c>。
    /// </summary>
    public static object? Evaluate(string raw)
    {
        string scalar = PhpString.Trim(raw);
        string lower = scalar.ToLowerInvariant();

        if (lower == "null" || scalar.Length == 0 || scalar == "~")
        {
            return null;
        }

        if (lower == "true")
        {
            return true;
        }

        if (lower == "false")
        {
            return false;
        }

        if (scalar[0] == '!')
        {
            throw new NotSupportedException("未支持的 YAML tag: " + scalar);
        }

        if (Octal.IsMatch(scalar))
        {
            string digits = scalar.Replace("_", "", StringComparison.Ordinal);
            bool negative = scalar[0] == '-';
            string body = digits.StartsWith("0o", StringComparison.Ordinal) ? digits[2..] : digits[3..];
            long value = Convert.ToInt64(body, 8);
            return negative ? -value : value;
        }

        char first = scalar[0];
        bool numericStart = first is >= '0' and <= '9' or '+' or '-' or '.';
        if (!numericStart)
        {
            return scalar;
        }

        bool plainInt = PlainIntegerUnderscores.IsMatch(scalar);
        string normalized = scalar;
        if (plainInt)
        {
            normalized = scalar.Replace("_", "", StringComparison.Ordinal);
            if (normalized[0] == '+')
            {
                normalized = normalized[1..];
            }
        }

        if (LooksInteger(normalized))
        {
            // (string)(int) === $scalar 才返回 int → 007 / -0 保持字符串
            return PhpIntCast(normalized);
        }

        string lowerNormalized = normalized.ToLowerInvariant();
        if (Numeric.IsMatch(normalized) || Hex.IsMatch(normalized))
        {
            string stripped = normalized.Replace("_", "", StringComparison.Ordinal);
            if (stripped.StartsWith("0x", StringComparison.Ordinal))
            {
                return Convert.ToInt64(stripped[2..], 16);
            }

            return double.Parse(stripped, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (lowerNormalized.EndsWith("inf", StringComparison.Ordinal))
        {
            return lowerNormalized == "-.inf" ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (lowerNormalized == ".nan")
        {
            return double.NaN;
        }

        if (DecimalUnderscores.IsMatch(scalar))
        {
            return double.Parse(scalar.Replace("_", "", StringComparison.Ordinal),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // 日期形标量在 Symfony 里会转成 Unix 秒；语言文件没有这类叶子，保持字符串（见 PORT-NOTES）
        return scalar;
    }

    private static bool LooksInteger(string scalar)
    {
        if (scalar.Length == 0)
        {
            return false;
        }

        int start = scalar[0] is '-' or '+' ? 1 : 0;
        if (start >= scalar.Length)
        {
            return false;
        }

        for (int i = start; i < scalar.Length; i++)
        {
            if (scalar[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static object PhpIntCast(string scalar)
    {
        if (!long.TryParse(scalar, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long cast))
        {
            return scalar;
        }

        return scalar == cast.ToString(System.Globalization.CultureInfo.InvariantCulture) ? cast : scalar;
    }
}
