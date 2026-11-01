using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Validation;

/// <summary>自定义校验规则（对应源 <c>Rule</c> 接口）。</summary>
public interface IRule
{
    /// <summary>规则名；返回 null 表示「只执行 passes()，不查内置实现」。</summary>
    string? Name { get; }

    IReadOnlyList<string> Parameters { get; }

    /// <summary>缺省值是否触发校验（implicit）。</summary>
    bool Implicit { get; }

    /// <summary>失败时的消息；null/空表示用语言包里的标准消息。</summary>
    string? Message { get; }

    bool Passes(string attribute, object? value, Validator validator);
}

/// <summary><c>unique</c> / <c>exists</c> 需要的数据存在性查询。</summary>
public interface IPresenceVerifier
{
    int GetCount(string table, string column, object? value, object? excludeId, string idColumn);
}

/// <summary>
/// Laravel <c>Illuminate\Validation\Validator</c> 的移植（subset）。
///
/// <p>支持 <c>required / present / filled / email / string / integer / numeric /
/// min / max / unique / in / regex / url / json / confirmed / nullable / array /
/// boolean / exists / required_if</c> 与自定义规则。</p>
///
/// <p>通配符键（<c>blocks.*.type</c>）在 <see cref="Passes"/> 入口展开成具体键，
/// 错误键名因此是 <c>blocks.0.type</c>。</p>
/// </summary>
public sealed class Validator
{
    private static readonly HashSet<string> ImplicitRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "required", "required_if", "required_unless", "required_with",
        "required_with_all", "required_without", "required_without_all",
        "present", "filled", "accepted", "accepted_if", "declined",
        "declined_if", "missing", "missing_if", "missing_unless",
        "missing_with", "missing_with_all", "present_if", "present_unless",
        "present_with", "present_with_all", "required_array_keys",
        "required_if_accepted", "required_if_declined",
    };

    private static readonly HashSet<string> SizeRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "min", "max", "between", "size",
    };

    private static readonly HashSet<string> NumericRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "numeric", "integer", "int", "float", "double", "decimal",
    };

    private readonly IReadOnlyDictionary<string, object?> _data;
    private readonly Translator _translator;
    private readonly string _locale;
    private readonly Option? _option;
    private readonly IPresenceVerifier? _presenceVerifier;

    private readonly Dictionary<string, List<object>> _rulesRaw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _failedRules = new(StringComparer.Ordinal);

    private Dictionary<string, List<object>>? _rules;

    public Validator(IReadOnlyDictionary<string, object?>? data, Translator translator,
        string? locale, Option? option = null, IPresenceVerifier? presenceVerifier = null)
    {
        _data = data ?? new Dictionary<string, object?>();
        _translator = translator;
        _locale = string.IsNullOrEmpty(locale) ? "en" : locale;
        _option = option;
        _presenceVerifier = presenceVerifier;
    }

    public void AddRule(string attribute, object rule)
    {
        if (!_rulesRaw.TryGetValue(attribute, out List<object>? list))
        {
            list = [];
            _rulesRaw[attribute] = list;
        }

        list.Add(rule);
    }

    /// <summary>执行校验；返回是否全部通过。</summary>
    public bool Passes()
    {
        _rules ??= ExpandRules();
        foreach (KeyValuePair<string, List<object>> entry in _rules)
        {
            foreach (object rule in entry.Value)
            {
                ValidateAttribute(entry.Key, rule);
                if (ShouldStopValidating(entry.Key))
                {
                    break;
                }
            }
        }

        return _messages.Count == 0;
    }

    public IReadOnlyDictionary<string, List<string>> Errors => _messages;

    /// <summary>校验通过的字段（只含声明过规则的键）。</summary>
    public Dictionary<string, object?> Validated()
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string attribute in _rulesRaw.Keys)
        {
            output[attribute] = GetValue(attribute);
        }

        return output;
    }

    // ================================================================== 核心流程

    private void ValidateAttribute(string attribute, object rule)
    {
        string? ruleName;
        IReadOnlyList<string> parameters;
        IRule? custom = null;

        if (rule is IRule customRule)
        {
            custom = customRule;
            ruleName = customRule.Name;
            parameters = customRule.Parameters;
        }
        else
        {
            string[] parsed = ParseRule(rule.ToString() ?? "");
            ruleName = parsed[0];
            parameters = parsed.Skip(1).ToList();
        }

        object? value = GetValue(attribute);

        if (string.IsNullOrEmpty(ruleName))
        {
            if (custom is not null && IsValidatable("__custom", attribute, value, custom)
                && !custom.Passes(attribute, value, this))
            {
                AddFailure(attribute, "__custom", parameters, custom.Message);
            }

            return;
        }

        bool validatable = IsValidatable(ruleName, attribute, value, custom);
        if (custom is not null)
        {
            if (validatable && !custom.Passes(attribute, value, this))
            {
                AddFailure(attribute, ruleName, parameters, custom.Message);
            }

            return;
        }

        if (validatable && !InvokeBuiltin(ruleName, attribute, value, parameters))
        {
            AddFailure(attribute, ruleName, parameters, null);
        }
    }

    private bool InvokeBuiltin(string rule, string attribute, object? value,
        IReadOnlyList<string> parameters) => rule switch
    {
        "required" => ValidateRequired(value),
        "present" => ValidatePresent(attribute, value),
        "filled" => ValidateFilled(value),
        "email" => ValidateEmail(value),
        "string" => value is null or string,
        "integer" => IsInteger(value),
        "numeric" => IsNumeric(value),
        "min" => ValidateMin(attribute, value, parameters),
        "max" => ValidateMax(attribute, value, parameters),
        "unique" => ValidateUnique(attribute, value, parameters),
        "in" => ValidateIn(value, parameters),
        "regex" => ValidateRegex(value, parameters),
        "url" => ValidateUrl(value, parameters),
        "json" => ValidateJson(value),
        "confirmed" => ValidateConfirmed(attribute, value),
        "nullable" => true,
        "array" => ValidateArray(value, parameters),
        "boolean" => ValidateBoolean(value),
        "exists" => ValidateExists(attribute, value, parameters),
        "required_if" => ValidateRequiredIf(attribute, value, parameters),
        _ => true,
    };

    // ================================================================== 内置规则

    private static bool ValidateRequired(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(PhpString.Trim(s)),
        ICollection collection => collection.Count > 0,
        _ => true,
    };

    private bool ValidatePresent(string attribute, object? value) =>
        _data.ContainsKey(attribute) || PhpArr.Has(_data, attribute);

    private static bool ValidateFilled(object? value) =>
        value is not null && !(value is string s && string.IsNullOrWhiteSpace(PhpString.Trim(s)));

    private static bool ValidateEmail(object? value) =>
        value is string s && RfcEmailValidator.IsValid(s);

    private static bool IsInteger(object? value) => value switch
    {
        null => false,
        long or int or short or byte => true,
        double d => d == Math.Floor(d),
        _ => int.TryParse(value.ToString()?.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out _),
    };

    private static bool IsNumeric(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value is long or int or double or decimal or float)
        {
            return true;
        }

        return decimal.TryParse(value.ToString()?.Trim(), NumberStyles.Number,
            CultureInfo.InvariantCulture, out _);
    }

    private bool ValidateMin(string attribute, object? value, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
        {
            return true;
        }

        string size = GetSize(attribute, value);
        return decimal.TryParse(size, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal actual)
               && decimal.TryParse(parameters[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal min)
               && actual >= min;
    }

    private bool ValidateMax(string attribute, object? value, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
        {
            return true;
        }

        string size = GetSize(attribute, value);
        return decimal.TryParse(size, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal actual)
               && decimal.TryParse(parameters[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal max)
               && actual <= max;
    }

    private bool ValidateUnique(string attribute, object? value, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0 || _presenceVerifier is null)
        {
            return true;
        }

        string table = parameters[0];
        string column = parameters.Count > 1 ? parameters[1] : attribute;
        object? excludeId = parameters.Count > 2 ? parameters[2] : null;
        string idColumn = parameters.Count > 3 ? parameters[3] : "id";
        return _presenceVerifier.GetCount(table, column, value, excludeId, idColumn) == 0;
    }

    private static bool ValidateIn(object? value, IReadOnlyList<string> parameters) =>
        value is not null && parameters.Contains(value.ToString());

    /// <summary><c>regex:</c>：PHP <c>preg_match</c> 是搜索语义。</summary>
    private static bool ValidateRegex(object? value, IReadOnlyList<string> parameters)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(value.ToString() ?? "",
                parameters.Count == 0 ? "" : parameters[0]);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ValidateUrl(object? value, IReadOnlyList<string> parameters)
    {
        if (value is null)
        {
            return false;
        }

        if (!Uri.TryCreate(value.ToString(), UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        return parameters.Count == 0
            ? scheme is "http" or "https" or "ftp"
            : parameters.Any(p => p.Equals(scheme, StringComparison.OrdinalIgnoreCase)) && uri.Host.Length > 0;
    }

    /// <summary>http/https URL（授权更新源 / 插件仓库用）。</summary>
    public static bool IsUrlWithScheme(string? value) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    private static bool ValidateJson(object? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            PhpJson.Decode(value.ToString() ?? "");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool ValidateConfirmed(string attribute, object? value)
    {
        object? other = GetValue(attribute + "_confirmation");
        if (value is null && other is null)
        {
            return true;
        }

        return value is not null && other is not null
            && string.Equals(value.ToString(), other.ToString(), StringComparison.Ordinal);
    }

    private static bool ValidateArray(object? value, IReadOnlyList<string> parameters)
    {
        if (value is null)
        {
            return true;
        }

        if (value is not (IList or IDictionary))
        {
            return false;
        }

        if (parameters.Count == 0 || value is not IDictionary map)
        {
            return true;
        }

        foreach (object key in map.Keys)
        {
            if (!parameters.Contains(key.ToString() ?? ""))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateBoolean(object? value) => value switch
    {
        null => false,
        bool => true,
        long l => l is 0 or 1,
        string s => s is "true" or "false" or "1" or "0" or "on" or "off",
        _ => false,
    };

    private bool ValidateExists(string attribute, object? value, IReadOnlyList<string> parameters)
    {
        if (value is null || parameters.Count == 0 || _presenceVerifier is null)
        {
            return true;
        }

        string table = parameters[0];
        string column = parameters.Count > 1 ? parameters[1] : attribute;
        if (value is IList list)
        {
            foreach (object? item in list)
            {
                if (_presenceVerifier.GetCount(table, column, item, null, "id") == 0)
                {
                    return false;
                }
            }

            return true;
        }

        return _presenceVerifier.GetCount(table, column, value, null, "id") > 0;
    }

    private bool ValidateRequiredIf(string attribute, object? value, IReadOnlyList<string> parameters)
    {
        if (parameters.Count < 2)
        {
            return true;
        }

        string other = parameters[0];
        string expected = parameters[1];
        object? otherValue = GetValue(other);

        bool condition;
        if (otherValue is bool b)
        {
            condition = expected.Equals("true", StringComparison.OrdinalIgnoreCase) ? b
                : expected.Equals("false", StringComparison.OrdinalIgnoreCase) ? !b
                : b.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            condition = string.Equals(otherValue?.ToString(), expected, StringComparison.Ordinal);
            if (expected.Equals("true", StringComparison.OrdinalIgnoreCase)
                || expected.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                string text = otherValue?.ToString() ?? "";
                bool otherBool = text.Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || text == "1" || text.Equals("on", StringComparison.OrdinalIgnoreCase);
                condition = otherBool == expected.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return !condition || ValidateRequired(value);
    }

    // ================================================================== isValidatable / 停止

    private bool IsValidatable(string ruleName, string attribute, object? value, IRule? custom)
    {
        bool implicitRule = (custom?.Implicit ?? false) || ImplicitRules.Contains(ruleName);

        bool presentOrImplicit;
        if (value is string text && string.IsNullOrWhiteSpace(PhpString.Trim(text)))
        {
            presentOrImplicit = implicitRule;
        }
        else
        {
            presentOrImplicit = ValidatePresent(attribute, value) || implicitRule;
        }

        bool notFailedPrev = true;
        if (ruleName.Equals("unique", StringComparison.OrdinalIgnoreCase)
            || ruleName.Equals("exists", StringComparison.OrdinalIgnoreCase))
        {
            notFailedPrev = !_messages.ContainsKey(attribute);
        }

        return presentOrImplicit && notFailedPrev;
    }

    private bool ShouldStopValidating(string attribute)
    {
        if (!_messages.ContainsKey(attribute))
        {
            return false;
        }

        return _failedRules.TryGetValue(attribute, out List<string>? failed)
               && failed.Any(rule => ImplicitRules.Contains(rule) || rule == "uploaded");
    }

    // ================================================================== 消息

    private void AddFailure(string attribute, string ruleName, IReadOnlyList<string> parameters,
        string? customMessage)
    {
        string message = !string.IsNullOrEmpty(customMessage)
            ? customMessage
            : ResolveMessage(attribute, ruleName);
        message = MakeReplacements(message, attribute, ruleName, parameters);

        if (!_messages.TryGetValue(attribute, out List<string>? list))
        {
            list = [];
            _messages[attribute] = list;
        }

        list.Add(message);

        if (!_failedRules.TryGetValue(attribute, out List<string>? failed))
        {
            failed = [];
            _failedRules[attribute] = failed;
        }

        failed.Add(ruleName);
    }

    private string ResolveMessage(string attribute, string ruleName)
    {
        string lower = Snake(ruleName);
        if (SizeRules.Contains(lower))
        {
            string key = "validation." + lower + "." + GetAttributeType(attribute);
            string? value = _translator.Trans(key, null, _locale);
            if (value != key)
            {
                return value;
            }
        }

        string generic = "validation." + lower;
        string? resolved = _translator.Trans(generic, null, _locale);
        return resolved == generic ? generic : resolved;
    }

    private string GetAttributeType(string attribute)
    {
        if (HasRule(attribute, NumericRules))
        {
            return "numeric";
        }

        return GetValue(attribute) is IList or IDictionary ? "array" : "string";
    }

    private bool HasRule(string attribute, HashSet<string> names) =>
        _rulesRaw.TryGetValue(attribute, out List<object>? rules)
        && rules.Any(rule =>
        {
            string? name = rule is IRule custom ? custom.Name : ParseRule(rule.ToString() ?? "")[0];
            return name is not null && names.Contains(name);
        });

    private string GetSize(string attribute, object? value)
    {
        if (value is long or int or double && HasRule(attribute, NumericRules))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
        }

        return value switch
        {
            IDictionary map => map.Count.ToString(CultureInfo.InvariantCulture),
            ICollection collection => collection.Count.ToString(CultureInfo.InvariantCulture),
            null => "0",
            _ => (value.ToString() ?? "").EnumerateRunes().Count().ToString(CultureInfo.InvariantCulture),
        };
    }

    private string MakeReplacements(string message, string attribute, string ruleName,
        IReadOnlyList<string> parameters)
    {
        string displayable = GetDisplayableAttribute(attribute);
        message = message
            .Replace(":attribute", displayable, StringComparison.Ordinal)
            .Replace(":ATTRIBUTE", displayable.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace(":Attribute", Translator.Ucfirst(displayable), StringComparison.Ordinal);

        string lower = Snake(ruleName);
        if (lower is "min" or "max" && parameters.Count > 0)
        {
            message = message.Replace(":" + lower, parameters[0], StringComparison.Ordinal);
        }
        else if (lower == "between" && parameters.Count >= 2)
        {
            message = message.Replace(":min", parameters[0], StringComparison.Ordinal)
                .Replace(":max", parameters[1], StringComparison.Ordinal);
        }
        else if (lower == "size" && parameters.Count > 0)
        {
            message = message.Replace(":size", parameters[0], StringComparison.Ordinal);
        }
        else if (lower == "in" && parameters.Count > 0)
        {
            message = message.Replace(":values", string.Join(", ", parameters), StringComparison.Ordinal);
        }
        else if (lower == "required_if" && parameters.Count >= 2)
        {
            message = message.Replace(":other", GetDisplayableAttribute(parameters[0]),
                    StringComparison.Ordinal)
                .Replace(":value", parameters[1], StringComparison.Ordinal);
        }

        return message;
    }

    private string GetDisplayableAttribute(string attribute)
    {
        if (_translator.Get("validation.attributes", null, _locale) is Dictionary<string, object?> map
            && map.TryGetValue(attribute, out object? value) && value is not null)
        {
            return PhpString.Cast(value);
        }

        return Snake(attribute).Replace('_', ' ');
    }

    // ================================================================== 工具

    private object? GetValue(string attribute)
    {
        object? value = PhpArr.Get(_data, attribute, null);
        return value;
    }

    private static string[] ParseRule(string rule)
    {
        string[] parts = rule.Split(':');
        var result = new List<string> { parts[0] };
        if (parts.Length > 1)
        {
            // 参数可能含逗号（regex 里也可能有），与 Laravel 一致按 ',' 切
            result.AddRange(parts[1].Split(','));
        }

        return [.. result];
    }

    private static string Snake(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private Dictionary<string, List<object>> ExpandRules()
    {
        var output = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<object>> entry in _rulesRaw)
        {
            if (!entry.Key.Contains('*'))
            {
                output[entry.Key] = [.. entry.Value];
            }
        }

        foreach (KeyValuePair<string, List<object>> entry in _rulesRaw)
        {
            if (!entry.Key.Contains('*'))
            {
                continue;
            }

            foreach (string concrete in ConcreteKeys(entry.Key))
            {
                if (!output.TryGetValue(concrete, out List<object>? list))
                {
                    list = [];
                    output[concrete] = list;
                }

                list.AddRange(entry.Value);
            }
        }

        return output;
    }

    private List<string> ConcreteKeys(string pattern)
    {
        var result = new List<string>();
        ExpandRecursive(pattern, "", result);
        return result;
    }

    private void ExpandRecursive(string pattern, string path, List<string> result)
    {
        int star = pattern.IndexOf('*');
        if (star < 0)
        {
            string full = (path + pattern).TrimStart('.');
            if (full.Length > 0)
            {
                result.Add(full);
            }

            return;
        }

        string before = pattern[..star];
        string after = pattern[(star + 1)..];
        string prefixPath = (path + before).TrimEnd('.');
        object? baseValue = prefixPath.Length == 0 ? _data : PhpArr.Get(_data, prefixPath, null);

        IEnumerable<object> keys = baseValue switch
        {
            IDictionary dictionary => dictionary.Keys.Cast<object>(),
            IList list => Enumerable.Range(0, list.Count).Cast<object>(),
            _ => [],
        };

        foreach (object key in keys)
        {
            string newPath = prefixPath.Length == 0 ? key.ToString() ?? "" : prefixPath + "." + key;
            ExpandRecursive(after, newPath, result);
        }
    }

    /// <summary>
    /// Laravel <c>ValidationException</c> 的单行消息：第一条错误 + 「(and N more errors)」。
    /// </summary>
    public static string Summarize(IReadOnlyDictionary<string, List<string>> errors)
    {
        var all = new List<string>();
        foreach (List<string> messages in errors.Values)
        {
            all.AddRange(messages);
        }

        if (all.Count == 0)
        {
            return "The given data was invalid.";
        }

        string message = all[0];
        int count = all.Count - 1;
        if (count > 0)
        {
            message += " (and " + count + " more " + (count == 1 ? "error" : "errors") + ")";
        }

        return message;
    }
}

/// <summary>PHP <c>filter_var(..., FILTER_VALIDATE_EMAIL)</c> 的近似实现。</summary>
public static class RfcEmailValidator
{
    private static readonly Regex Pattern = new(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?"
        + @"(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+$",
        RegexOptions.CultureInvariant);

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 254 && Pattern.IsMatch(value);
}
