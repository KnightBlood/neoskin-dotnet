using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpJson"/> 的语料驱动测试（对应 Java 版 PhpJsonTest）。
///
/// <p>数据源是 <c>conformance/data/json.json</c>：encode 用例同时比对
/// <c>raw</c>（<c>json_encode($v)</c>）与 <c>laravel</c>（<c>response()-&gt;json($v)</c>），
/// 并锁定 Laravel 13 的编码选项是 0。</p>
/// </summary>
public sealed class PhpJsonTest
{
    // ================================================================== 编码（语料）

    [ConformanceFact]
    public void EncodeMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("json");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("encode"))
        {
            string id = Corpus.TextOrEmpty(c, "id");
            if (id.StartsWith("writeback.", StringComparison.Ordinal))
            {
                continue;
            }

            object? value = ToPhpValue(c["value"], Corpus.Text(c, "php_type"));
            if (id == "encode.float.negzero")
            {
                // JsonNode 解析会把 -0 归一成 0，JSON 层面无法保留符号位
                value = -0.0;
            }

            Assert.True(Corpus.TextOrEmpty(c, "raw") == PhpJson.Encode(value), $"{id}: raw 不一致");
            Assert.Equal(Corpus.TextOrEmpty(c, "laravel"),
                PhpJson.Encode(value, EncodingOptions(corpus)));
            checkedCases++;
        }

        Assert.Equal(71, checkedCases);
    }

    [ConformanceFact]
    public void ResponseBodiesMatchCorpus()
    {
        Corpus corpus = Corpus.Require("json");
        int checkedCases = 0;
        foreach (string op in new[] { "helper", "response" })
        {
            foreach (JsonNode c in corpus.Cases(op))
            {
                object? value = op == "helper"
                    ? JsonHelper(c["args"]?.AsArray() ?? [])
                    : ToPhpValue(c["value"]);

                Assert.Equal(Corpus.TextOrEmpty(c, "expected_body"),
                    PhpJson.Encode(value, EncodingOptions(corpus)));
                Assert.False(string.IsNullOrEmpty(Corpus.TextOrEmpty(c, "expected_content_type")));
                checkedCases++;
            }
        }

        Assert.Equal(9, checkedCases);
    }

    [ConformanceFact]
    public void LaravelUsesBareDefaults()
    {
        Corpus corpus = Corpus.Require("json");
        JsonNode c = corpus.CaseById("const.encoding_options");
        Assert.Equal(0, c["value"]!.GetValue<int>());
        Assert.Equal(0, EncodingOptions(corpus));
        Assert.False(c["expected_flags"]!["JSON_UNESCAPED_UNICODE"]!.GetValue<bool>());
        Assert.False(c["expected_flags"]!["JSON_UNESCAPED_SLASHES"]!.GetValue<bool>());
    }

    [ConformanceFact]
    public void WritebackUsesUnescapedUnicodeOnly()
    {
        Corpus corpus = Corpus.Require("json");
        foreach (string id in new[] { "writeback.notification_data", "writeback.communication_audience" })
        {
            JsonNode c = corpus.CaseById(id);
            object? value = ToPhpValue(c["value"], "array");
            Assert.True(Corpus.TextOrEmpty(c, "raw") == PhpJson.Encode(value), $"{id}: raw 不一致");
            Assert.Equal(Corpus.TextOrEmpty(c, "laravel"),
                PhpJson.Encode(value, PhpJson.JsonUnescapedUnicode));
        }
    }

    // ================================================================== 解码（手写）

    [Fact]
    public void DecodeNumericKeys()
    {
        object? sequential = PhpJson.Decode("{\"0\":\"a\",\"1\":\"b\"}");
        Assert.IsType<PhpArray>(sequential);
        Assert.Equal("[\"a\",\"b\"]", PhpJson.Encode(sequential));

        foreach (string json in new[]
                 {
                     "{\"0\":\"a\",\"2\":\"c\"}",
                     "{\"-1\":\"a\"}",
                     "{\"01\":\"a\"}",
                     "{\"1.0\":\"a\"}",
                     "{\"\":\"a\"}",
                     "{\"1\":\"a\",\"0\":\"b\"}",
                 })
        {
            object? decoded = PhpJson.Decode(json);
            Assert.IsType<PhpArray>(decoded);
            Assert.Equal('{', PhpJson.Encode(decoded)[0]);
        }
    }

    [Fact]
    public void DecodeEmptyObjectBecomesArray()
    {
        Assert.Equal("[]", PhpJson.Encode(PhpJson.Decode("{}")));
        Assert.Equal("[]", PhpJson.Encode(PhpJson.Decode("[]")));
    }

    [Fact]
    public void DecodeFloatKeepsType()
    {
        Assert.IsType<double>(PhpJson.Decode("1.0"));
        Assert.Equal("1", PhpJson.Encode(PhpJson.Decode("1.0")));
        Assert.IsType<long>(PhpJson.Decode("1"));
        Assert.Equal("1", PhpJson.Encode(PhpJson.Decode("1")));
    }

    [Fact]
    public void DecodeInt64OverflowBecomesFloat()
    {
        Assert.IsType<long>(PhpJson.Decode("9223372036854775807"));
        Assert.IsType<double>(PhpJson.Decode("9223372036854775808"));
        Assert.Equal("9.223372036854776e+18", PhpJson.Encode(PhpJson.Decode("9223372036854775808")));
    }

    [Fact]
    public void DecodeSurrogates()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => PhpJson.Decode("\"\\ud800\""));
        Assert.Equal("Single unpaired UTF-16 surrogate in unicode escape", e.Message);
        Assert.Equal("🔐", PhpJson.Decode("\"\\ud83d\\udd10\""));
        Assert.Throws<ArgumentException>(() => PhpJson.Decode("\"\\udc00\""));
    }

    [Fact]
    public void DecodeRejectsBom()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => PhpJson.Decode("\uFEFF{}"));
        Assert.Equal("Syntax error", e.Message);
    }

    [Fact]
    public void DecodeDuplicateKeys()
    {
        Assert.Equal("{\"a\":3,\"b\":2}", PhpJson.Encode(PhpJson.Decode("{\"a\":1,\"b\":2,\"a\":3}")));
    }

    [Fact]
    public void DecodeSyntaxErrors()
    {
        foreach (string bad in new[]
                 {
                     "01", "+1", "1.", ".5", "1e", "tru", "[1,]", "{\"a\":1,}",
                     "", " ", "{", "[", "\"", "nul",
                 })
        {
            Assert.Throws<ArgumentException>(() => PhpJson.Decode(bad));
        }
    }

    [Fact]
    public void DecodeArrayContract()
    {
        Assert.Null(PhpJson.DecodeArray("\"text\""));
        Assert.Null(PhpJson.DecodeArray("42"));
        Assert.Null(PhpJson.DecodeArray("null"));
        Assert.Null(PhpJson.DecodeArray("{bad"));
        Assert.NotNull(PhpJson.DecodeArray("{\"title\":\"x\"}"));
        Assert.NotNull(PhpJson.DecodeArray("[1,2]"));
        Assert.Equal("x", PhpJson.DecodeArray("{\"title\":\"x\"}")!.Get("title"));
    }

    [Fact]
    public void ArrayWithNormalizesKeys()
    {
        var a = new PhpArray();
        a.With("1", "b").With("01", "c").With(2L, "d");
        Assert.Equal("{\"1\":\"b\",\"01\":\"c\",\"2\":\"d\"}", PhpJson.Encode(a));
        Assert.False(PhpJson.IsList(a));
        Assert.True(PhpJson.IsList((PhpArray)PhpJson.Decode("[\"x\"]")!));
    }

    [Fact]
    public void NumericEdgeCases()
    {
        Assert.Equal("0.5", PhpJson.Encode(0.5f));
        Assert.Equal("1", PhpJson.Encode(1.0m));
        Assert.Equal("2", PhpJson.Encode(2m));
        Assert.Equal("1.5", PhpJson.Encode(1.5m));
        Assert.Throws<ArgumentException>(() => PhpJson.Encode(double.NaN));
        Assert.Throws<ArgumentException>(() => PhpJson.Encode(double.PositiveInfinity));
    }

    [Fact]
    public void EncodeRejectsLoneSurrogate()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => PhpJson.Encode("\ud800"));
        Assert.Equal("Malformed UTF-8 characters, possibly incorrectly encoded", e.Message);
        Assert.Throws<ArgumentException>(() => PhpJson.Encode("a\udc00b"));
    }

    // ================================================================== 辅助

    private static int EncodingOptions(Corpus corpus) =>
        corpus.CaseById("const.encoding_options")["value"]!.GetValue<int>();

    /// <summary><c>app/helpers.php</c> 里 <c>json()</c> 的三条分支（与 Java 版一致）。</summary>
    private static object? JsonHelper(JsonArray args)
    {
        if (args.Count == 1 && args[0] is JsonArray or JsonObject)
        {
            return ToPhpValue(args[0]);
        }

        if (args.Count == 3 && args[2] is JsonArray or JsonObject)
        {
            return new PhpArray()
                .With("code", ToPhpValue(args[1]))
                .With("message", ToPhpValue(args[0]))
                .With("data", ToPhpValue(args[2]));
        }

        // Arr::get($args, 1, 1)：键不存在才用默认值 1，显式 null 原样返回
        return new PhpArray()
            .With("code", args.Count >= 2 ? ToPhpValue(args[1]) : 1L)
            .With("message", ToPhpValue(args[0]));
    }

    /// <summary>
    /// <c>JsonNode</c> → PHP 值。必须参考 <c>php_type</c>：JSON 的 <c>0</c> 既可能是
    /// PHP int 也可能是 float，只看文本无法区分（<c>1e20</c> 走错分支会输出整数）。
    /// </summary>
    private static object? ToPhpValue(JsonNode? node, string? phpType = null)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            var list = new List<object?>();
            foreach (JsonNode? item in array)
            {
                list.Add(ToPhpValue(item));
            }

            return list;
        }

        if (node is JsonObject obj)
        {
            var map = new PhpArray();
            foreach (KeyValuePair<string, JsonNode?> entry in obj)
            {
                map.Put(entry.Key, ToPhpValue(entry.Value));
            }

            return map;
        }

        JsonValue value = node.AsValue();
        if (value.TryGetValue(out bool b))
        {
            return b;
        }

        if (value.TryGetValue(out string? s))
        {
            return s;
        }

        if (phpType == "float")
        {
            return value.GetValue<double>();
        }

        // 三元表达式会把 long 分支隐式转成 double（long/double 取公共类型），
        // 9223372036854775807 会因此变成 9.223372036854776E+18 —— 必须显式分支。
        if (value.TryGetValue(out long l))
        {
            return l;
        }

        return value.GetValue<double>();
    }}
