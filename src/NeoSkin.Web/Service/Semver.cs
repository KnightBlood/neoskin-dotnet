using System.Text.RegularExpressions;

namespace NeoSkin.Web.Service;

/// <summary>
/// Composer Semver 的最小移植（版本比较与约束检查）。
/// </summary>
public static class Semver
{
    /// <summary>数值版本比较：<c>a &gt; b</c>。</summary>
    public static bool GreaterThan(string? a, string? b) => Compare(a, b) > 0;

    public static bool LessThan(string? a, string? b) => Compare(a, b) < 0;

    public static int Compare(string? a, string? b)
    {
        List<int> left = NumericParts(a);
        List<int> right = NumericParts(b);
        int size = Math.Max(left.Count, right.Count);
        for (int i = 0; i < size; i++)
        {
            int lv = i < left.Count ? left[i] : 0;
            int rv = i < right.Count ? right[i] : 0;
            if (lv != rv)
            {
                return lv.CompareTo(rv);
            }
        }

        return 0;
    }

    private static List<int> NumericParts(string? version)
    {
        var parts = new List<int>();
        if (version is null)
        {
            return parts;
        }

        Match match = Regex.Match(version, @"^\s*v?(\d+(?:\.\d+)*)");
        if (!match.Success)
        {
            return parts;
        }

        foreach (string part in match.Groups[1].Value.Split('.'))
        {
            parts.Add(int.TryParse(part, out int value) ? value : 0);
        }

        return parts;
    }

    /// <summary>
    /// <c>Semver::satisfies</c> 的常用子集：<c>*</c> / <c>&gt;=</c> / <c>&lt;=</c> /
    /// <c>&gt;</c> / <c>&lt;</c> / <c>^X.Y.Z</c> / <c>~X.Y.Z</c> / 精确版本。
    /// </summary>
    public static bool Satisfies(string? version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim() == "*")
        {
            return true;
        }

        foreach (string token in constraint.Trim()
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SatisfiesOne(version, token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SatisfiesOne(string? version, string token)
    {
        if (token.Length == 0 || token == "*")
        {
            return true;
        }

        if (token.StartsWith(">=", StringComparison.Ordinal))
        {
            return Compare(version, token[2..]) >= 0;
        }

        if (token.StartsWith("<=", StringComparison.Ordinal))
        {
            return Compare(version, token[2..]) <= 0;
        }

        if (token.StartsWith(">", StringComparison.Ordinal))
        {
            return Compare(version, token[1..]) > 0;
        }

        if (token.StartsWith("<", StringComparison.Ordinal))
        {
            return Compare(version, token[1..]) < 0;
        }

        if (token.StartsWith("!=", StringComparison.Ordinal))
        {
            return Compare(version, token[2..]) != 0;
        }

        if (token.StartsWith('^') || token.StartsWith('~'))
        {
            string baseVersion = token[1..];
            if (Compare(version, baseVersion) < 0)
            {
                return false;
            }

            List<int> parts = NumericParts(baseVersion);
            int major = parts.Count > 0 ? parts[0] : 0;
            int minor = parts.Count > 1 ? parts[1] : 0;
            return token.StartsWith('^')
                ? Compare(version, (major + 1) + ".0.0") < 0
                : Compare(version, major + "." + (minor + 1) + ".0") < 0;
        }

        return Compare(version, token) == 0;
    }
}
