using System.Text.Json.Nodes;

namespace NeoSkin.Tests.Conformance;

/// <summary>
/// 读取语言中立的语料库 <c>conformance/data/*.json</c>（与 Java 移植版共用）。
///
/// <p>语料库由 <c>conformance/oracle/</c> 在 PHP 侧生成，是<b>唯一权威</b>的期望值来源。
/// 文件不存在时（只 checkout 了子项目）用例 skip 而不是 fail。</p>
/// </summary>
public sealed class Corpus
{
    private const string DataProperty = "NEOSKIN_CONFORMANCE_DATA";

    private static readonly string? DataDir = ResolveDataDirectory();

    private readonly JsonNode _root;

    private Corpus(string name, JsonNode root)
    {
        Name = name;
        _root = root;
    }

    public string Name { get; }

    /// <summary>语料库目录是否存在（不存在时带 <see cref="ConformanceFactAttribute"/> 的用例会 skip）。</summary>
    public static bool Available => DataDir is not null;

    public static string DataDirectory => DataDir ?? "";

    /// <summary>载入一份语料，例如 <c>"json"</c>、<c>"hash"</c>、<c>"schema"</c>。</summary>
    public static Corpus Require(string name)
    {
        if (DataDir is null)
        {
            throw new ConformanceMissingException(
                $"未找到语料库目录；用环境变量 {DataProperty} 指定，或放在 ../conformance/data。");
        }

        string file = Path.Combine(DataDir, name + ".json");
        if (!File.Exists(file))
        {
            throw new ConformanceMissingException($"未找到语料文件 {file}");
        }

        JsonNode root = JsonNode.Parse(File.ReadAllText(file))
            ?? throw new InvalidDataException($"语料不是合法 JSON: {file}");
        return new Corpus(name, root);
    }

    /// <summary>全部用例，顺序与 JSON 一致。</summary>
    public IEnumerable<JsonNode> Cases() =>
        (_root["cases"] as JsonArray ?? []).Select(node => node!);

    /// <summary>按 <c>op</c> 过滤，例如 <c>"encode"</c> / <c>"verify"</c>。</summary>
    public IEnumerable<JsonNode> Cases(string op) =>
        Cases().Where(c => Text(c, "op") == op);

    public int Count => (_root["cases"] as JsonArray)?.Count ?? 0;

    /// <summary>按 id 取单条用例；id 写错必须立刻暴露，不能静默 skip。</summary>
    public JsonNode CaseById(string id)
    {
        foreach (JsonNode node in Cases())
        {
            if (Text(node, "id") == id)
            {
                return node;
            }
        }

        throw new ArgumentException($"语料 {Name}.json 里没有用例: {id}");
    }

    // ------------------------------------------------------------------ 取值辅助

    /// <summary>取字符串字段，缺失或 null 返回 null（区别于 ""）。</summary>
    public static string? Text(JsonNode? node, string field)
    {
        JsonNode? value = node?[field];
        return value is null ? null : value.GetValue<string>();
    }

    public static string TextOrEmpty(JsonNode? node, string field) => Text(node, field) ?? "";

    public static bool Bool(JsonNode? node, string field, bool fallback)
    {
        JsonNode? value = node?[field];
        return value is null ? fallback : value.GetValue<bool>();
    }

    public static int Integer(JsonNode? node, string field, int fallback)
    {
        JsonNode? value = node?[field];
        return value is null ? fallback : value.GetValue<int>();
    }

    /// <summary>取 64 位整数（cost 可能是 long 极值，不能用 <see cref="Integer"/>）。</summary>
    public static long Long(JsonNode? node, string field, long fallback)
    {
        JsonNode? value = node?[field];
        return value is null ? fallback : value.GetValue<long>();
    }

    private static string? ResolveDataDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(DataProperty);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return System.IO.Directory.Exists(fromEnv) ? Path.GetFullPath(fromEnv) : null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NeoSkin.NET.slnx")))
            {
                string candidate = Path.GetFullPath(Path.Combine(dir.FullName, "..", "conformance", "data"));
                return System.IO.Directory.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>语料库缺失时抛出（不应出现在断言失败里）。</summary>
public sealed class ConformanceMissingException(string message) : Exception(message);

/// <summary>语料库不存在时自动 skip 的 <see cref="FactAttribute"/>。</summary>
public sealed class ConformanceFactAttribute : FactAttribute
{
    public ConformanceFactAttribute()
    {
        if (!Corpus.Available)
        {
            Skip = "未找到 conformance/data；跳过（见 conformance/README.md）。";
        }
    }
}
