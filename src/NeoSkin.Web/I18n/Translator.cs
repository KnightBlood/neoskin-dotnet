using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.I18n;

/// <summary>
/// <c>Illuminate\Translation\Translator</c> 的移植（Web 层用到的
/// <c>get / has / setLocale / getLocale</c>）。
///
/// <p><c>get()</c> 流程照抄：先用 <c>load('*','*',$locale)</c> 的平铺表直读 key；
/// 否则 <c>parseKey</c> → 对 <c>[locale, fallback]</c> 逐个 <c>getLine</c>；
/// 仍未命中返回原 key。</p>
///
/// <p>占位符支持 <c>:name</c> / <c>:Name</c> / <c>:NAME</c> 三种，替换用
/// <c>strtr</c> 的最长匹配优先语义。</p>
/// </summary>
public sealed class Translator(TranslationFileLoader loader, string locale, string fallback)
{
    public string Locale { get; set; } = locale;

    public string Fallback { get; } = fallback;

    public string GetLocale() => Locale;

    public void SetLocale(string value) => Locale = value;

    /// <summary><c>trans($key, $replace, $locale)</c>；未命中返回原 key。</summary>
    public object? Get(string? key, IReadOnlyDictionary<string, object?>? replace = null, string? locale = null)
    {
        if (key is null)
        {
            return "";
        }

        string l = string.IsNullOrEmpty(locale) ? GetLocale() : locale;
        Dictionary<string, object?> json = loader.Load(l, "*", "*");
        if (json.TryGetValue(key, out object? directLine) && directLine is not null)
        {
            return directLine is string s ? MakeReplacements(s, replace) : directLine;
        }

        string[] parts = ParseKey(key);
        string ns = parts[0];
        string group = parts[1];
        string? item = parts[2];

        foreach (string candidate in LocaleArray(l))
        {
            object? found = GetLine(ns, group, candidate, item, replace);
            if (found is not null)
            {
                return found;
            }
        }

        return key;
    }

    /// <summary>单值视图。</summary>
    public string Trans(string? key, IReadOnlyDictionary<string, object?>? replace = null,
        string? locale = null)
    {
        object? value = Get(key, replace, locale);
        return value is null ? "" : PhpString.Cast(value);
    }

    /// <summary><c>has($key, $locale)</c>。</summary>
    public bool Has(string key, string? locale = null)
    {
        string l = string.IsNullOrEmpty(locale) ? GetLocale() : locale;
        Dictionary<string, object?> json = loader.Load(l, "*", "*");
        if (json.ContainsKey(key))
        {
            return true;
        }

        string[] parts = ParseKey(key);
        if (parts[2] is null)
        {
            return loader.Load(l, parts[1], parts[0]).Count > 0;
        }

        object? value = PhpArr.Get(loader.Load(l, parts[1], parts[0]), parts[2], null);
        return value is not null;
    }

    /// <summary><c>hasForLocale($key, $locale)</c>。</summary>
    public bool HasForLocale(string key, string locale) => Has(key, locale);

    // ================================================================= 内部

    private Dictionary<string, object?> LoadMerged(string locale, string group, string ns) =>
        loader.Load(locale, group, ns);

    private object? GetLine(string ns, string group, string locale, string? item,
        IReadOnlyDictionary<string, object?>? replace)
    {
        Dictionary<string, object?> lines = LoadMerged(locale, group, ns);
        object? line = PhpArr.Get(lines, item);
        return line switch
        {
            string s => MakeReplacements(s, replace),
            Dictionary<string, object?> map when map.Count > 0 => WalkRecursive(map, replace),
            List<object?> list when list.Count > 0 => WalkRecursive(list, replace),
            PhpArray array when array.Count > 0 => WalkRecursive(array, replace),
            _ => null,
        };
    }

    /// <summary><c>array_walk_recursive</c>：对每个叶子做占位符替换。</summary>
    private static object? WalkRecursive(object? node, IReadOnlyDictionary<string, object?>? replace)
    {
        if (replace is null || replace.Count == 0)
        {
            return node;
        }

        switch (node)
        {
            case Dictionary<string, object?> map:
                return map.ToDictionary(pair => pair.Key,
                    pair => WalkRecursive(pair.Value, replace), StringComparer.Ordinal);
            case PhpArray array:
                var copy = new PhpArray();
                foreach (KeyValuePair<object, object?> entry in array)
                {
                    copy.Put(entry.Key, WalkRecursive(entry.Value, replace));
                }

                return copy;
            case List<object?> list:
                return list.Select(item => WalkRecursive(item, replace)).ToList();
            case string s:
                return MakeReplacements(s, replace);
            default:
                return node;
        }
    }

    /// <summary><c>localeArray</c>：<c>[locale, fallback]</c>，去空去重。</summary>
    private List<string> LocaleArray(string locale)
    {
        var locales = new List<string>();
        string primary = string.IsNullOrEmpty(locale) ? GetLocale() : locale;
        if (primary.Length > 0)
        {
            locales.Add(primary);
        }

        if (!string.IsNullOrEmpty(Fallback) && !locales.Contains(Fallback))
        {
            locales.Add(Fallback);
        }

        return locales;
    }

    /// <summary><c>NamespacedItemResolver::parseKey + Translator::parseKey</c>。</summary>
    public static string[] ParseKey(string key)
    {
        int nsPos = key.IndexOf("::", StringComparison.Ordinal);
        if (nsPos >= 0)
        {
            string ns = key[..nsPos];
            string item = key[(nsPos + 2)..];
            int dot = item.IndexOf('.');
            string group = dot < 0 ? item : item[..dot];
            string? rest = dot < 0 ? null : item[(dot + 1)..];
            return [ns, group, rest!];
        }

        int firstDot = key.IndexOf('.');
        return firstDot < 0
            ? ["*", key, null!]
            : ["*", key[..firstDot], key[(firstDot + 1)..]];
    }

    /// <summary>源 <c>makeReplacements</c>：<c>:name</c> / <c>:Name</c> / <c>:NAME</c>。</summary>
    public static string MakeReplacements(string line, IReadOnlyDictionary<string, object?>? replace)
    {
        if (replace is null || replace.Count == 0)
        {
            return line;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in replace)
        {
            string key = entry.Key;
            string value = entry.Value is null ? "" : PhpString.Cast(entry.Value);
            map[":" + Ucfirst(key)] = Ucfirst(value);
            map[":" + Upper(key)] = Upper(value);
            map[":" + key] = value;
        }

        return Strtr(line, map);
    }

    /// <summary><c>strtr</c>：最长匹配优先。</summary>
    internal static string Strtr(string line, Dictionary<string, string> replace)
    {
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            string? longest = null;
            foreach (string from in replace.Keys)
            {
                if (line.AsSpan(i).StartsWith(from, StringComparison.Ordinal)
                    && (longest is null || from.Length > longest.Length))
                {
                    longest = from;
                }
            }

            if (longest is not null)
            {
                sb.Append(replace[longest]);
                i += longest.Length - 1;
            }
            else
            {
                sb.Append(line[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary><c>Str::ucfirst</c>：首个码点大写。</summary>
    internal static string Ucfirst(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        int length = char.IsHighSurrogate(value[0]) && value.Length > 1 ? 2 : 1;
        return value[..length].ToUpperInvariant() + value[length..];
    }

    /// <summary><c>Str::upper</c>：<c>mb_strtoupper</c>。</summary>
    internal static string Upper(string value) => value.ToUpperInvariant();
}
