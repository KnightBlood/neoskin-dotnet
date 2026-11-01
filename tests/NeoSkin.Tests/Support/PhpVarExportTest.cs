using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpVarExport"/> 的回归测试：输入是 PHP 8.5.12 用 <c>var_export()</c>
/// 生成的 <c>options.php</c>（<c>Resources/options-sample.php</c>）。
/// </summary>
public sealed class PhpVarExportTest
{
    private static string Sample() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "options-sample.php"))
            .Replace("\r\n", "\n");

    [Fact]
    public void ParsesRealPhpOptionsCache()
    {
        Dictionary<string, object?> parsed = PhpVarExport.ParseArray(Sample());

        Assert.Equal("https://skin.example.com", parsed["site_url"]);
        Assert.Equal("Blessing Skin", parsed["site_name"]);
        Assert.Equal("6.0.11_patch", parsed["version"]);
        Assert.Equal("", parsed["plugins_enabled"]);
        Assert.Equal(
            "<b>Copyright &copy; {year} <a href=\"{site_url}\">{site_name}</a>.</b> All rights reserved.",
            parsed["copyright_text"]);
        Assert.Equal("中文选项值 — 测试", parsed["unicode"]);
        Assert.Equal("it's a \"test\" with \\ backslash", parsed["quoted"]);
        Assert.Equal("line1\nline2", parsed["multi"]);
        Assert.Equal("1000", parsed["numeric_string"]);
        Assert.Equal(10, parsed.Count);
    }

    [Fact]
    public void RoundTripsThroughPhpFormat()
    {
        Dictionary<string, object?> original = PhpVarExport.ParseArray(Sample());
        string written = PhpVarExport.ToPhpFile(original);
        Dictionary<string, object?> reparsed = PhpVarExport.ParseArray(written);
        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void WritesSameHeaderAsArtisanCommand()
    {
        string output = PhpVarExport.ToPhpFile(new Dictionary<string, object?> { ["site_name"] = "Blessing Skin" });
        Assert.StartsWith("<?php\n", output);
        Assert.Contains("// This is auto-generated. DO NOT edit manually.\n", output);
        Assert.StartsWith("<?php\n// This is auto-generated. DO NOT edit manually.\nreturn array (\n", output);
        Assert.EndsWith(");", output);
        Assert.Contains("  'site_name' => 'Blessing Skin',\n", output);
    }

    [Fact]
    public void ExportsNullAndBooleansLikeVarExport()
    {
        var items = new PhpArray()
            .With("a_null", null)
            .With("a_true", true)
            .With("a_false", false)
            .With("an_int", 42L);
        string output = PhpVarExport.ToPhpFile(items.Select(kv => new KeyValuePair<string, object?>(kv.Key.ToString()!, kv.Value)));

        Assert.Contains("'a_null' => NULL,", output);
        Assert.Contains("'a_true' => true,", output);
        Assert.Contains("'a_false' => false,", output);
        Assert.Contains("'an_int' => 42,", output);

        Dictionary<string, object?> reparsed = PhpVarExport.ParseArray(output);
        Assert.True(reparsed.ContainsKey("a_null"));
        Assert.Null(reparsed["a_null"]);
        Assert.Equal(true, reparsed["a_true"]);
        Assert.Equal(false, reparsed["a_false"]);
        Assert.Equal(42L, (long)reparsed["an_int"]!);
    }

    [Fact]
    public void RejectsNonArrayInput() =>
        Assert.Throws<ArgumentException>(() => PhpVarExport.ParseArray("<?php return 'hello';"));
}

/// <summary><see cref="PhpDate"/> 的语料测试（<c>conformance/data/datetime.json</c> 的 format 用例）。</summary>
public sealed class PhpDateTest
{
    [ConformanceFact]
    public void FormatMatchesCorpus()
    {
        Corpus corpus = Corpus.Require("datetime");
        Assert.Equal("Asia/Shanghai", Corpus.Text(corpus.CaseById("const.timezone"), "expected"));
        Assert.Equal("Y-m-d H:i:s", Corpus.Text(corpus.CaseById("const.date_format"), "expected"));

        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("format"))
        {
            long unix = c["unix"]!.GetValue<long>();
            string timezone = Corpus.TextOrEmpty(c, "timezone");
            string expected = Corpus.TextOrEmpty(c, "expected");
            Assert.Equal(expected, PhpDate.At(unix * 1000, timezone));
            checkedCases++;
        }

        Assert.True(checkedCases >= 11);
    }

    [Fact]
    public void ParsesLaravelDateFormat()
    {
        Assert.Equal(new DateTime(2026, 10, 26, 12, 34, 56), PhpDate.Parse("2026-10-26 12:34:56"));
        Assert.Null(PhpDate.Parse("2026-10-26T12:34:56"));
        Assert.Null(PhpDate.Parse(null));
    }

    [Fact]
    public void InvalidTimezoneFallsBack()
    {
        Assert.Equal("Asia/Shanghai", PhpDate.Zone("Asia/Shanghai").Id);
        Assert.NotNull(PhpDate.Zone("Not/AZone"));
    }
}
