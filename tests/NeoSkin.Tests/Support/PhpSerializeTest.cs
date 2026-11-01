using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpSerialize"/> 的语料驱动测试（对应 Java 版 PhpSerializeTest）。
/// Laravel 会话（<c>serialization = 'php'</c>）写入 cookie 的起点。
/// </summary>
public sealed class PhpSerializeTest
{
    [ConformanceFact]
    public void CorpusSerialize()
    {
        Corpus corpus = Corpus.Require("cookie");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("serialize"))
        {
            object? input = BuildInput(c);
            string expected = Corpus.TextOrEmpty(c, "expected");
            string actual = PhpSerialize.Serialize(input);
            Assert.True(expected == actual,
                $"{Corpus.TextOrEmpty(c, "id")}: serialize 不一致\nexpected: {expected}\nactual:   {actual}");
            checkedCases++;
        }

        Assert.True(checkedCases >= 13, "serialize 用例数量变了？请重新生成语料");
    }

    private static object? BuildInput(JsonNode c)
    {
        if (Corpus.TextOrEmpty(c, "input_repr") == "rec2")
        {
            // 自引用数组：outer=[1,2,X]; X=[1,2,X]
            var inner = new PhpArray();
            inner.Put(0L, 1L);
            inner.Put(1L, 2L);
            inner.Put(2L, inner);

            var outer = new PhpArray();
            outer.Put(0L, 1L);
            outer.Put(1L, 2L);
            outer.Put(2L, inner);
            return outer;
        }

        return PhpJson.Decode(Corpus.TextOrEmpty(c, "input_json"));
    }

    [Fact]
    public void StringContentIsNotEscapedAndLengthIsBytes()
    {
        Assert.Equal("s:3:\"a\"b\";", PhpSerialize.Serialize("a\"b"));
        Assert.Equal("s:2:\"a\\\";", PhpSerialize.Serialize("a\\"));
        Assert.Equal("s:6:\"密码\";", PhpSerialize.Serialize("密码"));
        Assert.Equal("s:4:\"🔐\";", PhpSerialize.Serialize("🔐"));
        Assert.Equal("s:0:\"\";", PhpSerialize.Serialize(""));
        Assert.Equal("s:1:\"\0\";", PhpSerialize.Serialize("\0"));
    }

    [Fact]
    public void ScalarsMatchPhp()
    {
        Assert.Equal("N;", PhpSerialize.Serialize(null));
        Assert.Equal("b:1;", PhpSerialize.Serialize(true));
        Assert.Equal("b:0;", PhpSerialize.Serialize(false));
        Assert.Equal("i:42;", PhpSerialize.Serialize(42));
        Assert.Equal("i:-7;", PhpSerialize.Serialize(-7));
        Assert.Equal("i:9223372036854775807;", PhpSerialize.Serialize(long.MaxValue));
        Assert.Equal("i:-9223372036854775808;", PhpSerialize.Serialize(long.MinValue));
    }

    [Fact]
    public void DoublesUseShortestRoundTripWithUpperE()
    {
        Assert.Equal("d:1.5;", PhpSerialize.Serialize(1.5));
        Assert.Equal("d:1;", PhpSerialize.Serialize(1.0));
        Assert.Equal("d:-0;", PhpSerialize.Serialize(-0.0));
        Assert.Equal("d:INF;", PhpSerialize.Serialize(double.PositiveInfinity));
        Assert.Equal("d:-INF;", PhpSerialize.Serialize(double.NegativeInfinity));
        Assert.Equal("d:NAN;", PhpSerialize.Serialize(double.NaN));
        Assert.Equal("d:1.0E-5;", PhpSerialize.Serialize(0.00001));
    }

    [Fact]
    public void ArraysAreNormalizedAndNested()
    {
        var list = new PhpArray().With(0L, 1L).With(1L, "a");
        Assert.Equal("a:2:{i:0;i:1;i:1;s:1:\"a\";}", PhpSerialize.Serialize(list));

        var map = new PhpArray().With("a", 1L).With("b", "x");
        Assert.Equal("a:2:{s:1:\"a\";i:1;s:1:\"b\";s:1:\"x\";}", PhpSerialize.Serialize(map));

        var nested = new PhpArray().With("x", new PhpArray().With("y", true));
        Assert.Equal("a:1:{s:1:\"x\";a:1:{s:1:\"y\";b:1;}}", PhpSerialize.Serialize(nested));
        Assert.Equal("a:0:{}", PhpSerialize.Serialize(new PhpArray()));
    }

    [Fact]
    public void NumericStringKeysAreNormalized()
    {
        var map = new PhpArray().With("0", "a").With("01", "b");
        Assert.Equal("a:2:{i:0;s:1:\"a\";s:2:\"01\";s:1:\"b\";}", PhpSerialize.Serialize(map));
    }

    [Fact]
    public void IterableBecomesList()
    {
        Assert.Equal("a:2:{i:0;i:1;i:1;s:1:\"a\";}",
            PhpSerialize.Serialize(new List<object?> { 1L, "a" }));
    }
}
