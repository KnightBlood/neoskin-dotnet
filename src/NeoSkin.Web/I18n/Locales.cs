namespace NeoSkin.Web.I18n;

/// <summary>
/// <c>config/locales.php</c> 的移植（8 个键，顺序即语言下拉菜单顺序）。
///
/// <p>3 个纯别名：<c>zh_HANS_CN → zh_CN</c>、<c>en_US → en</c>、<c>ru → ru_RU</c>。</p>
/// </summary>
public static class Locales
{
    /// <summary><c>config('app.locale')</c>。</summary>
    public const string AppLocale = "zh_CN";

    /// <summary><c>config('app.fallback_locale')</c>。</summary>
    public const string FallbackLocale = "en";

    /// <summary>语言表本体，保持 PHP 数组的书写顺序。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Table =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["zh_CN"] = Entry(("name", "中文 (简体)"), ("short_name", "zh-CN")),
            ["zh_HANS_CN"] = Entry(("alias", "zh_CN")),
            ["zh_TW"] = Entry(("name", "中文 (正體)"), ("short_name", "zh-TW")),
            ["en"] = Entry(("name", "English"), ("short_name", "en")),
            ["en_US"] = Entry(("alias", "en")),
            ["es_ES"] = Entry(("name", "Español"), ("short_name", "es")),
            ["ru"] = Entry(("alias", "ru_RU")),
            ["ru_RU"] = Entry(("name", "Русский язык"), ("short_name", "ru")),
        };

    private static IReadOnlyDictionary<string, string> Entry(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary><c>Arr::has(config('locales'), $locale)</c>。</summary>
    public static bool Has(object? key) => key is string s && Table.ContainsKey(s);

    /// <summary><c>Arr::get(config('locales'), $locale)</c>；key 为 null 时返回整张表。</summary>
    public static object? Get(object? key) => key is null ? Table : key is string s && Table.TryGetValue(s, out var info) ? info : null;

    /// <summary>别名归一：<c>en_US → en</c>、<c>ru → ru_RU</c>、<c>zh_HANS_CN → zh_CN</c>。</summary>
    public static string Resolve(string? locale)
    {
        if (locale is not null && Table.TryGetValue(locale, out var info)
            && info.TryGetValue("alias", out string? alias) && !string.IsNullOrEmpty(alias))
        {
            return alias;
        }

        return locale ?? "";
    }

    /// <summary>真实语言（过滤别名），顺序同源文件。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Available =>
        Table.Where(pair => !pair.Value.ContainsKey("alias"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>真实语言的键列表：<c>[zh_CN, zh_TW, en, es_ES, ru_RU]</c>。</summary>
    public static IReadOnlyList<string> Ids => Available.Keys.ToList();

    /// <summary>语言下拉菜单显示名；缺失返回 null。</summary>
    public static string? DisplayName(string id) =>
        Table.TryGetValue(id, out var info) && info.TryGetValue("name", out string? name) ? name : null;

    /// <summary>短名（<c>es_ES → es</c>）。</summary>
    public static string? ShortName(string id) =>
        Table.TryGetValue(id, out var info) && info.TryGetValue("short_name", out string? shortName)
            ? shortName
            : null;
}
