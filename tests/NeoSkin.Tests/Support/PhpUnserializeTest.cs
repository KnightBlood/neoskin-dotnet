using System.Text;
using System.Text.Json.Nodes;
using NeoSkin.Tests.Conformance;
using NeoSkin.Web.Support;

namespace NeoSkin.Tests.Support;

/// <summary>
/// <see cref="PhpUnserialize"/> 的语料驱动测试（对应 Java 版 PhpUnserializeTest）。
/// 测三件对外可见的事：值、失败与否、错误 offset（Laravel 会把它转成 ErrorException 消息）。
/// </summary>
public sealed class PhpUnserializeTest
{
    // ------------------------------------------------------------------ 语料

    [ConformanceFact]
    public void CorpusUnserialize()
    {
        Corpus corpus = Corpus.Require("cookie");
        int checkedCases = 0;
        foreach (JsonNode c in corpus.Cases("unserialize"))
        {
            AssertCase(c);
            checkedCases++;
        }

        Assert.True(checkedCases >= 11, "unserialize 用例数量变了？请重新生成语料");
    }

    private static void AssertCase(JsonNode c)
    {
        string id = Corpus.TextOrEmpty(c, "id");
        PhpUnserialize.Result result = PhpUnserialize.Parse(Corpus.TextOrEmpty(c, "input"));

        Assert.True(Corpus.Bool(c, "failed", false) == (!result.Ok && result.Warnings.Count > 0),
            id + " failed");

        string? expectedWarning = Corpus.Text(c, "warning");
        string? prefixed = result.FirstWarning is null ? null : "unserialize(): " + result.FirstWarning;

        if (expectedWarning is not null && expectedWarning != prefixed
            && result.FirstWarning == expectedWarning)
        {
            // 裸 zend_error 警告（C: 的 has no unserializer）没有前缀，逐字匹配
            Assert.True(expectedWarning == result.FirstWarning, id + " warning");
        }
        else
        {
            Assert.True(expectedWarning == prefixed, id + " warning");
        }

        JsonNode? expected = c["expected"];
        if (Corpus.TextOrEmpty(c, "php_type") == "RECRECURSION")
        {
            Assert.True(IsSelfRecursive(result.Value), id + ": 期望自引用结构（*RECURSION*）");
            if (result.Value is PhpArray array)
            {
                int rc = Corpus.Integer(c, "recursion_count", -1);
                if (rc >= 0)
                {
                    Assert.Equal(rc, array.Count);
                }
            }

            return;
        }

        if (Corpus.TextOrEmpty(c, "php_type") == "__PHP_Incomplete_Class")
        {
            var obj = Assert.IsType<PhpObject>(result.Value);
            Assert.Equal("__PHP_Incomplete_Class", obj.ClassName);
            Assert.True(SameJson(expected, obj.Properties), id + ": IC 属性不符");
            return;
        }

        if (expected is null)
        {
            Assert.True(result.Value is null || result.Value is false, id + " value");
        }
        else
        {
            Assert.True(Matches(expected, result.Value),
                id + " value: " + (result.Value?.ToString() ?? "null"));
        }
    }

    private static bool Matches(JsonNode expected, object? actual) => expected switch
    {
        JsonValue value when value.TryGetValue(out bool b) => actual is bool ab && ab == b,
        JsonValue value when value.TryGetValue(out string? s) => actual is string a && a == s,
        JsonValue value when value.TryGetValue(out double d) =>
            actual is long l ? l == d : actual is double ad && ad == d,
        _ => actual is PhpArray array && SameJson(expected, array),
    };

    private static bool SameJson(JsonNode? expected, PhpArray actual)
    {
        if (expected is null)
        {
            return actual.Count == 0;
        }

        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Array)
        {
            if (expected.AsArray().Count != actual.Count)
            {
                return false;
            }

            long i = 0;
            foreach (JsonNode? child in expected.AsArray())
            {
                if (!CompareChild(child, actual.Get(i)))
                {
                    return false;
                }

                i++;
            }

            return true;
        }

        if (expected is JsonObject obj)
        {
            if (obj.Count != actual.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, JsonNode?> field in obj)
            {
                if (!CompareChild(field.Value, actual.Get(field.Key)))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool CompareChild(JsonNode? expected, object? actual)
    {
        if (expected is JsonArray or JsonObject)
        {
            return actual is PhpArray child && SameJson(expected, child);
        }

        return expected is not null && Matches(expected, actual);
    }

    private static bool IsSelfRecursive(object? value) =>
        ReachesRoot(value, value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static bool ReachesRoot(object? node, object? root, HashSet<object> seen)
    {
        if (ReferenceEquals(node, root))
        {
            return true;
        }

        if (node is null || !seen.Add(node))
        {
            return false;
        }

        if (node is PhpArray array)
        {
            return array.Any(child => ReachesRoot(child.Value, root, seen));
        }

        if (node is PhpObject obj)
        {
            return obj.Properties.Any(child => ReachesRoot(child.Value, root, seen));
        }

        return false;
    }

    // ------------------------------------------------------------------ 值

    [Fact]
    public void Scalars()
    {
        Assert.Equal(true, PhpUnserialize.Parse("b:1;").Value);
        Assert.Equal(false, PhpUnserialize.Parse("b:0;").Value);
        Assert.Equal(42L, PhpUnserialize.Parse("i:42;").Value);
        Assert.Equal(-7L, PhpUnserialize.Parse("i:-7;").Value);
        Assert.Equal(1.5, PhpUnserialize.Parse("d:1.5;").Value);
        Assert.Equal("abc", PhpUnserialize.Parse("s:3:\"abc\";").Value);
        Assert.Equal("密码", PhpUnserialize.Parse("s:6:\"密码\";").Value);
        Assert.True(PhpUnserialize.Parse("N;").Ok);
    }

    [Fact]
    public void IntOverflowSaturatesWithWarning()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("i:9223372036854775808;");
        Assert.True(result.Ok);
        Assert.Equal(long.MaxValue, result.Value);
        Assert.Equal(["Numerical result out of range"], result.Warnings);
    }

    [Fact]
    public void IntNineteenDigitsIsExact()
    {
        Assert.Equal(long.MaxValue, PhpUnserialize.Parse("i:9223372036854775807;").Value);
        Assert.Equal(long.MinValue, PhpUnserialize.Parse("i:-9223372036854775808;").Value);
    }

    [Fact]
    public void DoublesAcceptShortForms()
    {
        Assert.Equal(0.5, PhpUnserialize.Parse("d:.5;").Value);
        Assert.Equal(1.0, PhpUnserialize.Parse("d:1;").Value);
        Assert.False(PhpUnserialize.Parse("d:.;").Ok);
        Assert.True(PhpUnserialize.Parse("d:1.;").Ok);
    }

    [Fact]
    public void Arrays()
    {
        PhpUnserialize.Result result =
            PhpUnserialize.Parse("a:2:{s:1:\"a\";i:1;s:1:\"b\";s:1:\"x\";}");
        Assert.True(result.Ok);
        var array = Assert.IsType<PhpArray>(result.Value);
        Assert.Equal(2, array.Count);
        Assert.Equal(1L, array.Get("a"));
        Assert.Equal("x", array.Get("b"));
    }

    [Fact]
    public void ArrayCountRejectsLeadingSign()
    {
        Assert.False(PhpUnserialize.Parse("a:-1:{};").Ok);
        Assert.False(PhpUnserialize.Parse("a:+1:{i:0;i:1;}").Ok);
    }

    [Fact]
    public void FakeElementCountFails() =>
        Assert.False(PhpUnserialize.Parse("a:100:{i:0;i:1;}").Ok);

    [Fact]
    public void ShortArrayFailsWithUnexpectedEnd()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("a:2:{i:0;i:1;}");
        Assert.False(result.Ok);
        Assert.Equal("Unexpected end of serialized data", result.Warnings[0]);
        Assert.Equal("Error at offset 13 of 14 bytes", result.Warnings[1]);
    }

    // ------------------------------------------------------------------ 失败与 offset

    [Fact]
    public void TrailingJunkWarnsButSucceeds()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("i:42;junk");
        Assert.True(result.Ok);
        Assert.Equal(42L, result.Value);
        Assert.Equal("Extra data starting at offset 5 of 9 bytes", result.FirstWarning);
    }

    [Fact]
    public void GarbageFailsAtZero()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("hello");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 0 of 5 bytes", result.FirstWarning);
    }

    [Fact]
    public void EmptyInputReturnsFalseWithoutWarning()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse(Array.Empty<byte>());
        Assert.False(result.Ok);
        Assert.Equal(false, result.Value);
        Assert.Empty(result.Warnings);
        Assert.Equal(result.Warnings, PhpUnserialize.Parse("").Warnings);
    }

    [Fact]
    public void TruncatedStringOffsets()
    {
        PhpUnserialize.Result tooLong = PhpUnserialize.Parse("s:10:\"ab\";");
        Assert.False(tooLong.Ok);
        Assert.Equal("Error at offset 2 of 10 bytes", tooLong.FirstWarning);

        PhpUnserialize.Result badTail = PhpUnserialize.Parse("s:3:\"ab\";");
        Assert.False(badTail.Ok);
        Assert.Equal("Error at offset 8 of 9 bytes", badTail.FirstWarning);
    }

    [Fact]
    public void MissingSemicolonFallsBack()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("i:42");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 0 of 4 bytes", result.FirstWarning);
    }

    // ------------------------------------------------------------------ 引用与对象

    [Fact]
    public void ReferencesFailButAdvance()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("R:1;");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 4 of 4 bytes", result.FirstWarning);
    }

    [Fact]
    public void ObjectBecomesPhpObject()
    {
        PhpUnserialize.Result result =
            PhpUnserialize.Parse("O:7:\"Example\":2:{s:3:\"foo\";i:1;s:3:\"bar\";b:1;}");
        Assert.True(result.Ok);
        var obj = Assert.IsType<PhpObject>(result.Value);
        Assert.Equal("__PHP_Incomplete_Class", obj.ClassName);
        Assert.Equal("Example", obj.Properties.Get("__PHP_Incomplete_Class_Name"));
        Assert.Equal(1L, obj.Properties.Get("foo"));
        Assert.Equal(true, obj.Properties.Get("bar"));
    }

    [Fact]
    public void CustomObjectSkipsPayloadAndWarns()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("C:8:\"stdClass\":5:{hello}");
        Assert.True(result.Ok);
        var obj = Assert.IsType<PhpObject>(result.Value);
        Assert.Equal("stdClass", obj.ClassName);
        Assert.Equal(0, obj.Properties.Count);
        Assert.Equal("Class stdClass has no unserializer", result.FirstWarning);
    }

    [Fact]
    public void CustomObjectInsufficientDataWarns()
    {
        PhpUnserialize.Result short1 = PhpUnserialize.Parse("C:8:\"stdClass\":6:{hello}");
        Assert.False(short1.Ok);
        Assert.Equal("Insufficient data for unserializing - 6 required, 6 present", short1.Warnings[0]);
        Assert.Equal("Error at offset 18 of 24 bytes", short1.Warnings[1]);
    }

    [Fact]
    public void EnumFailsWithWarnings()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("E:9:\"Some:Case\";");
        Assert.False(result.Ok);
        Assert.Equal("Class 'Some' not found", result.Warnings[0]);
        Assert.Equal("Error at offset 0 of 16 bytes", result.Warnings[1]);
    }

    [Fact]
    public void EnumLengthMismatchFailsAtLenStart()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("E:8:\"M:One\";");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 2 of 12 bytes", result.FirstWarning);
    }

    [Fact]
    public void EnumWithoutColonWarns()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("E:10:\"SomeCase\";");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 16 of 16 bytes", result.FirstWarning);
    }

    [Fact]
    public void ArrayInKeyPositionFailsAtContentStart()
    {
        PhpUnserialize.Result array = PhpUnserialize.Parse("a:1:{a:0:{}i:1;}");
        Assert.False(array.Ok);
        Assert.Equal("Error at offset 10 of 16 bytes", array.FirstWarning);

        PhpUnserialize.Result nested = PhpUnserialize.Parse("a:1:{a:2:{i:3;i:4;}i:1;}");
        Assert.False(nested.Ok);
        Assert.Equal("Error at offset 10 of 24 bytes", nested.FirstWarning);
    }

    [Fact]
    public void ObjectInKeyPositionFailsAtKeyStart()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("a:1:{O:1:\"X\":0:{}i:1;}");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 5 of 22 bytes", result.FirstWarning);
    }

    [Fact]
    public void ScalarInKeyPositionFailsAtValueStart()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("a:1:{b:1;i:1;}");
        Assert.False(result.Ok);
        Assert.Equal("Error at offset 9 of 14 bytes", result.FirstWarning);
    }

    [Fact]
    public void ArrayCountBeyondInt64WarnsThenFailsAtContentStart()
    {
        PhpUnserialize.Result result = PhpUnserialize.Parse("a:9999999999999999999:{}");
        Assert.False(result.Ok);
        Assert.Equal("Numerical result out of range", result.Warnings[0]);
        Assert.Equal("Error at offset 23 of 24 bytes", result.Warnings[1]);
    }

    [Fact]
    public void Utf8IsDecodedButByteLengthsRule()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("s:6:\"密码\";");
        Assert.True(PhpUnserialize.Parse(bytes).Ok);
        Assert.Equal("密码", PhpUnserialize.Parse(bytes).Value);
    }
}
