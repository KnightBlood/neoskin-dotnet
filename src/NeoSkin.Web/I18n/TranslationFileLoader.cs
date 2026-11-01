using NeoSkin.Web.Support;

namespace NeoSkin.Web.I18n;

/// <summary>
/// 翻译文件加载器（源 <c>Spatie\TranslationLoader\TranslationLoaderManager extends FileLoader</c>，
/// 应用侧把解析从 <c>.php</c> 换成 <c>.yml</c>）。
///
/// <list type="number">
///   <item>安全：locale/group 含 <c>..</c>、<c>\</c>、<c>\0</c>、<c>/</c> 时返回空表；</item>
///   <item><c>group='*'</c> 且 <c>namespace='*'</c> → <c>lang/&lt;locale&gt;.json</c>；</item>
///   <item>namespace 为 null/`*` → <c>lang/&lt;locale&gt;/&lt;group&gt;.yml</c>；</item>
///   <item>其他 namespace → <c>lang/vendor/&lt;ns&gt;/&lt;locale&gt;/&lt;group&gt;.yml</c>。</item>
/// </list>
/// </summary>
public sealed class TranslationFileLoader
{
    /// <summary>语言包根目录（相对输出目录），对应源 <c>path.lang</c>。</summary>
    public const string LangPrefix = "Resources/lang";

    private readonly string _root;
    private readonly List<string> _paths = [];
    private readonly List<string> _jsonPaths = [];

    /// <summary>
    /// 语言包解析缓存（对应 Laravel <c>FileLoader::$loaded</c>）：
    /// 每个 locale/group 只读盘解析一次，之后直接复用字典。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        Dictionary<string, object?>> _cache = new(StringComparer.Ordinal);

    /// <summary>DB 覆盖层（Spatie 的 loader 列表）；null 表示未接库。</summary>
    public Func<string, string, Dictionary<string, object?>?>? DbLoader { get; set; }

    public TranslationFileLoader(string? root = null) =>
        _root = root ?? Path.Combine(AppContext.BaseDirectory, LangPrefix);

    /// <summary>源 <c>addPath()</c>（插件用）。</summary>
    public void AddPath(string child)
    {
        _paths.Add(child);
        _cache.Clear();
    }

    /// <summary>源 <c>addJsonPath()</c>。</summary>
    public void AddJsonPath(string child)
    {
        _jsonPaths.Add(child);
        _cache.Clear();
    }

    public Dictionary<string, object?> Load(string locale, string group, string? ns)
    {
        if (Unsafe(locale) || (group != "*" && Unsafe(group)))
        {
            return [];
        }

        if (group == "*" && ns == "*")
        {
            return LoadJson(locale);
        }

        Dictionary<string, object?> files = ns is null or "*" ? LoadYamlGroup(locale, group) : LoadNamespaced(locale, group, ns);
        if (DbLoader is null || (ns is not null && ns != "*"))
        {
            return files;
        }

        Dictionary<string, object?>? db = DbLoader(locale, group);
        return db is null || db.Count == 0 ? files : Merge(files, db);
    }

    private Dictionary<string, object?> LoadYamlGroup(string locale, string group) =>
        _cache.GetOrAdd("group|" + locale + "|" + group + "|" + _paths.Count,
            _ => LoadYamlGroupUncached(locale, group));

    private Dictionary<string, object?> LoadYamlGroupUncached(string locale, string group)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string prefix in _paths.Count == 0 ? new[] { LangPrefix } : _paths.Select(p => Path.Combine(LangPrefix, p)))
        {
            Dictionary<string, object?>? file = ParseYaml(prefix, locale, group);
            if (file is not null)
            {
                output = Merge(output, file);
            }
        }

        return output;
    }

    private Dictionary<string, object?> LoadNamespaced(string locale, string group, string ns) =>
        _cache.GetOrAdd("vendor|" + ns + "|" + locale + "|" + group, _ =>
        {
            Dictionary<string, object?>? file = ParseYaml(Path.Combine(LangPrefix, "vendor", ns), locale, group);
            return file ?? [];
        });

    private Dictionary<string, object?> LoadJson(string locale)
    {
        if (Unsafe(locale))
        {
            return [];
        }

        return _cache.GetOrAdd("json|" + locale, _ => LoadJsonUncached(locale));
    }

    private Dictionary<string, object?> LoadJsonUncached(string locale)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        string file = Path.Combine(_root, locale + ".json");
        if (File.Exists(file))
        {
            try
            {
                if (PhpJson.Decode(File.ReadAllText(file)) is PhpArray array)
                {
                    foreach (KeyValuePair<object, object?> entry in array)
                    {
                        output[PhpString.Cast(entry.Key)] = entry.Value;
                    }
                }
            }
            catch (ArgumentException)
            {
                output.Clear();
            }
        }

        return output;
    }

    private Dictionary<string, object?>? ParseYaml(string prefix, string locale, string group)
    {
        string relative = Path.Combine(prefix, locale, group + ".yml");
        string file = Path.Combine(AppContext.BaseDirectory, relative);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(file);
            return SymfonyYaml.Parse(reader);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Unsafe(string segment) =>
        segment.Contains("..", StringComparison.Ordinal)
        || segment.Contains('\\')
        || segment.Contains('\0')
        || segment.Contains('/');

    /// <summary><c>array_replace_recursive($a, $b)</c>：a 的顺序保留，b 新增的追加在后。</summary>
    internal static Dictionary<string, object?> Merge(Dictionary<string, object?> baseMap,
        Dictionary<string, object?> overrideMap)
    {
        var output = new Dictionary<string, object?>(baseMap, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in overrideMap)
        {
            if (output.TryGetValue(entry.Key, out object? existing)
                && existing is Dictionary<string, object?> a
                && entry.Value is Dictionary<string, object?> b)
            {
                output[entry.Key] = Merge(a, b);
            }
            else
            {
                output[entry.Key] = entry.Value;
            }
        }

        return output;
    }
}
