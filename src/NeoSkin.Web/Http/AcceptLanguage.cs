using System.Text.RegularExpressions;
using NeoSkin.Web.I18n;

namespace NeoSkin.Web.Http;

/// <summary>
/// Symfony <c>Request::getLanguages()</c> / <c>getPreferredLanguage()</c> /
/// <c>formatLocale()</c> 的移植。
///
/// <p><c>DetectLanguagePrefer</c> 只用到「q 值最高的一项」，但必须连同
/// Accept 头的<b>去重与排序</b>一起搬：<c>"en;q=0.9, en;q=0.8"</c> 只剩一个 <c>en</c>，
/// <c>"zh-CN;q=0, en"</c> 因为 q=0 排到后面而返回 <c>en</c>。</p>
///
/// <p><c>formatLocale</c> 的规则（PHP 8.5 实测）：<c>zh-CN → zh_CN</c>、
/// <c>zh-Hans-CN → zh_Hans_CN</c>、<c>zh-CN- → zh</c>、<c>i-cherokee → cherokee</c>、
/// <c>en-GB-oed → en_GB</c>、<c>a → a</c>。</p>
/// </summary>
public static class AcceptLanguage
{
    /// <summary>Symfony <c>Request::create()</c> 给缺失头填的默认值（只用于复现 oracle）。</summary>
    public const string DefaultAcceptLanguage = "en-us,en;q=0.5";

    private static readonly Regex LocalePattern = new(
        "^(?<language>[a-zA-Z]{2,3}|i-[a-zA-Z]{5,})(?:-(?<script>[a-zA-Z]{4}))?"
        + "(?:-(?<region>[a-zA-Z]{2}))?(?:-(?<variant>.+))?$",
        RegexOptions.CultureInvariant);

    /// <summary><c>Request::getLanguages()</c>：按 q 值降序（同 q 保持出现顺序）。</summary>
    public static List<string> GetLanguages(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return [];
        }

        var items = new List<(string Value, double Quality, int Index)>();
        int index = 0;
        foreach (string raw in acceptLanguage.Split(','))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string[] pieces = raw.Split(';');
            string value = pieces[0].Trim();
            if (value.Length == 0)
            {
                // ";;;" 这类垃圾输入在 Symfony 里不产生条目
                continue;
            }

            double quality = 1.0;
            for (int i = 1; i < pieces.Length; i++)
            {
                string piece = pieces[i].Trim();
                if (piece.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(piece[2..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double q))
                {
                    quality = q;
                }
            }

            items.Add((value, quality, index++));
        }

        items.Sort((a, b) =>
        {
            int byQuality = b.Quality.CompareTo(a.Quality);
            return byQuality != 0 ? byQuality : a.Index.CompareTo(b.Index);
        });

        var languages = new List<string>();
        foreach ((string value, _, _) in items)
        {
            string formatted = FormatLocale(value);
            if (!languages.Contains(formatted))
            {
                languages.Add(formatted);
            }
        }

        return languages;
    }

    /// <summary><c>Request::getPreferredLanguage()</c>：列表第一项。</summary>
    public static string? GetPreferredLanguage(string? acceptLanguage)
    {
        List<string> languages = GetLanguages(acceptLanguage);
        return languages.Count == 0 ? null : languages[0];
    }

    /// <summary><c>getPreferredLanguage($locales)</c>：在候选里挑最匹配的一个。</summary>
    public static string? GetPreferredLanguage(string? acceptLanguage, IReadOnlyList<string>? locales)
    {
        List<string> preferred = GetLanguages(acceptLanguage);
        if (locales is null || locales.Count == 0)
        {
            return preferred.Count == 0 ? null : preferred[0];
        }

        List<string> normalized = locales.Select(FormatLocale).ToList();
        if (preferred.Count == 0)
        {
            return normalized[0];
        }

        var combinations = new List<string>();
        foreach (string locale in preferred)
        {
            combinations.AddRange(GetLanguageCombinations(locale));
        }

        foreach (string combination in combinations)
        {
            foreach (string locale in normalized)
            {
                if (locale.StartsWith(combination, StringComparison.Ordinal))
                {
                    return locale;
                }
            }
        }

        return normalized[0];
    }

    /// <summary><c>Request::formatLocale()</c>：<c>language_Script_REGION</c>。</summary>
    public static string FormatLocale(string? locale)
    {
        string[] parts = GetLanguageComponents(locale);
        return string.Join("_", parts.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary><c>getLanguageComponents()</c>；不匹配正则时返回 <c>[整串小写, null, null]</c>。</summary>
    internal static string[] GetLanguageComponents(string? locale)
    {
        string normalized = (locale ?? "").Replace('_', '-').ToLowerInvariant();
        Match match = LocalePattern.Match(normalized);
        if (!match.Success)
        {
            return [normalized, "", ""];
        }

        string language = match.Groups["language"].Value;
        if (language.StartsWith("i-", StringComparison.Ordinal))
        {
            language = language[2..];
        }

        string script = match.Groups["script"].Success
            ? char.ToUpperInvariant(match.Groups["script"].Value[0])
              + match.Groups["script"].Value[1..].ToLowerInvariant()
            : "";
        string region = match.Groups["region"].Success
            ? match.Groups["region"].Value.ToUpperInvariant()
            : "";

        return [language, script, region];
    }

    /// <summary><c>getLanguageCombinations()</c>：<c>fr_Latn_FR</c> 展开成 4 种。</summary>
    internal static List<string> GetLanguageCombinations(string locale)
    {
        string[] parts = GetLanguageComponents(locale);
        var output = new List<string>();
        foreach (string candidate in new[]
                 {
                     Join(parts[0], parts[1], parts[2]),
                     Join(parts[0], parts[1], ""),
                     Join(parts[0], "", parts[2]),
                     parts[0],
                 })
        {
            if (candidate.Length > 0 && !output.Contains(candidate))
            {
                output.Add(candidate);
            }
        }

        return output;
    }

    private static string Join(string language, string script, string region) =>
        string.Join("_", new[] { language, script, region }.Where(part => part.Length > 0));
}
