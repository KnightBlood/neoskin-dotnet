using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="SymfonyYaml"/> 的标量求值回归（对齐 Symfony YAML 的
/// <c>Inline::evaluateScalar()</c>，PHP 侧已实测）。
/// </summary>
public sealed class SymfonyYamlTest
{
    [Fact]
    public void ScalarCases()
    {
        Assert.Equal("yes", SymfonyYaml.Evaluate("yes"));
        Assert.Equal("no", SymfonyYaml.Evaluate("no"));
        Assert.Equal("Yes", SymfonyYaml.Evaluate("Yes"));
        Assert.Equal("ON", SymfonyYaml.Evaluate("ON"));
        Assert.Equal("y", SymfonyYaml.Evaluate("y"));
        Assert.Equal("n", SymfonyYaml.Evaluate("n"));

        Assert.Equal(true, SymfonyYaml.Evaluate("true"));
        Assert.Equal(true, SymfonyYaml.Evaluate("True"));
        Assert.Equal(true, SymfonyYaml.Evaluate("TRUE"));
        Assert.Equal(false, SymfonyYaml.Evaluate("false"));
        Assert.Equal(false, SymfonyYaml.Evaluate("False"));
        Assert.Equal(false, SymfonyYaml.Evaluate("FALSE"));

        Assert.Null(SymfonyYaml.Evaluate("null"));
        Assert.Null(SymfonyYaml.Evaluate("Null"));
        Assert.Null(SymfonyYaml.Evaluate("NULL"));
        Assert.Null(SymfonyYaml.Evaluate("~"));
        Assert.Null(SymfonyYaml.Evaluate(""));
        Assert.Equal(true, SymfonyYaml.Evaluate(" true "));
    }

    [Fact]
    public void NumericCases()
    {
        Assert.Equal(1L, SymfonyYaml.Evaluate("1"));
        Assert.Equal(-5L, SymfonyYaml.Evaluate("-5"));
        Assert.Equal(1000L, SymfonyYaml.Evaluate("1_000"));
        Assert.Equal("007", SymfonyYaml.Evaluate("007"));
        Assert.Equal("-0", SymfonyYaml.Evaluate("-0"));
        Assert.Equal(2L, SymfonyYaml.Evaluate("+2"));
        Assert.Equal(1.5, SymfonyYaml.Evaluate("1.5"));
        Assert.Equal(0.5, SymfonyYaml.Evaluate(".5"));
        Assert.Equal(26L, SymfonyYaml.Evaluate("0x1A"));
        Assert.Equal(100000.0, SymfonyYaml.Evaluate("1e5"));
    }

    [Fact]
    public void MappingCases()
    {
        Dictionary<string, object?> doc = SymfonyYaml.Parse(new StringReader(
            "key1: \"value1\"\n"
            + "nested:\n"
            + "  inner: deep\n"
            + "list:\n"
            + "  - a\n"
            + "  - b\n"));

        Assert.Equal("value1", doc["key1"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(doc["nested"]);
        Assert.Equal("deep", nested["inner"]);
        var list = Assert.IsAssignableFrom<List<object?>>(doc["list"]);
        Assert.Equal(2, list.Count);

        Assert.Throws<InvalidOperationException>(() =>
            SymfonyYaml.Parse(new StringReader("a: 1\na: 2\n")));
    }

    [Fact]
    public void QuotedStaysString()
    {
        Dictionary<string, object?> doc = SymfonyYaml.Parse(new StringReader(
            "a: \"true\"\nb: 'null'\nc: '007'\nd: ' true '\n"));

        Assert.Equal("true", doc["a"]);
        Assert.Equal("null", doc["b"]);
        Assert.Equal("007", doc["c"]);
        Assert.Equal(" true ", doc["d"]);
    }

    [Fact]
    public void OverridesYesIsBoolean()
    {
        Dictionary<string, object?>? de = SymfonyYaml.ParseResource("Resources/lang/de_DE/general.yml");
        Assert.NotNull(de);
        Assert.Equal(true, de!["yes"]);
        Assert.Equal(false, de["no"]);

        Dictionary<string, object?>? en = SymfonyYaml.ParseResource("Resources/lang/en/general.yml");
        Assert.NotNull(en);
        Assert.Equal("Yes", en!["yes"]);
        Assert.Equal("No", en["no"]);
    }

    [Fact]
    public void UnknownTagThrows()
    {
        Assert.ThrowsAny<Exception>(() =>
            SymfonyYaml.Parse(new StringReader("a: !!weird 1\n")));
        Assert.Equal("no", SymfonyYaml.Evaluate("no"));
    }
}
