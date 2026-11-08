using System.Collections;
using System.Globalization;
using System.Text;
using NeoSkin.Web.Db;
using NeoSkin.Web.I18n;
using NeoSkin.Web.Service;
using NeoSkin.Web.Session;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>可在模板表达式里访问成员/方法的对象。</summary>
public interface ITemplateObject
{
    bool TryGetMember(string name, out object? value);

    bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value);
}

/// <summary>模板渲染上下文：链式变量作用域 + 当前模板名。</summary>
public sealed class TemplateContext
{
    private readonly TemplateContext? _parent;
    private readonly Dictionary<string, object?> _locals;
    private readonly IReadOnlyDictionary<string, object?>? _rootVariables;

    private TemplateContext(string templateName, IReadOnlyDictionary<string, object?> variables)
    {
        TemplateName = templateName;
        _rootVariables = variables;
        _locals = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private TemplateContext(TemplateContext parent, string templateName,
        Dictionary<string, object?>? locals)
    {
        _parent = parent;
        TemplateName = templateName;
        _locals = locals ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>当前模板名（相对片段 <c>~{::content}</c> 的归属）。</summary>
    public string TemplateName { get; }

    public static TemplateContext Root(string templateName,
        IReadOnlyDictionary<string, object?> variables) =>
        new(templateName, variables);

    /// <summary>进入子作用域（片段参数 / 循环变量）。</summary>
    public TemplateContext Child(string? templateName = null,
        Dictionary<string, object?>? locals = null) =>
        new(this, templateName ?? TemplateName, locals);

    public object? Get(string name) => TryGet(name, out object? value) ? value : null;

    public bool TryGet(string name, out object? value)
    {
        if (_locals.TryGetValue(name, out value))
        {
            return true;
        }

        if (_parent is not null)
        {
            return _parent.TryGet(name, out value);
        }

        if (_rootVariables is not null && _rootVariables.TryGetValue(name, out value))
        {
            return true;
        }

        // 表达式工具对象
        switch (name)
        {
            case "#strings":
                value = TemplateUtilities.Strings;
                return true;
            case "#lists":
                value = TemplateUtilities.Lists;
                return true;
            default:
                value = null;
                return false;
        }
    }
}

/// <summary>循环状态（<c>th:each="item, stat : ..."</c> 的 <c>stat</c>）。</summary>
public sealed class TemplateStat(int index, int size, object? current) : ITemplateObject
{
    public int Index => index;

    public int Count => index + 1;

    public int Size => size;

    public bool First => index == 0;

    public bool Last => index == size - 1;

    public bool Even => index % 2 == 0;

    public bool Odd => index % 2 != 0;

    public object? Current => current;

    public bool TryGetMember(string name, out object? value)
    {
        switch (name)
        {
            case "index":
                value = Index;
                return true;
            case "count":
                value = Count;
                return true;
            case "size":
                value = Size;
                return true;
            case "first":
                value = First;
                return true;
            case "last":
                value = Last;
                return true;
            case "even":
                value = Even;
                return true;
            case "odd":
                value = Odd;
                return true;
            case "current":
                value = Current;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;
        return false;
    }
}

/// <summary>片段引用值：<c>~{template :: fragment(args)}</c>。</summary>
public sealed record TemplateFragmentRef(
    string Template,
    string Fragment,
    IReadOnlyList<object?> Arguments);

/// <summary>模板值语义：成员访问、索引、迭代、真值、比较、字符串化。</summary>
public static class TemplateValues
{
    // ================================================================== 成员

    public static object? GetMember(object? target, string name)
    {
        switch (target)
        {
            case null:
                return null;
            case ITemplateObject obj when obj.TryGetMember(name, out object? value):
                return value;
            case PhpArray array:
                return array.Get(name);
            case IReadOnlyDictionary<string, object?> readOnly
                when readOnly.TryGetValue(name, out object? value):
                return value;
            case IDictionary<string, object?> dictionary
                when dictionary.TryGetValue(name, out object? value):
                return value;
            case KeyValuePair<object, object?> pair:
                return name switch
                {
                    "key" => pair.Key,
                    "value" => pair.Value,
                    _ => null,
                };
            case KeyValuePair<string, object?> stringPair:
                return name switch
                {
                    "key" => stringPair.Key,
                    "value" => stringPair.Value,
                    _ => null,
                };
            case DictionaryEntry entry:
                return name switch
                {
                    "key" => entry.Key,
                    "value" => entry.Value,
                    _ => null,
                };
            case UserRepository.UserRow user:
                return name switch
                {
                    "uid" or "id" => user.Uid,
                    "email" => user.Email,
                    "nickname" => user.Nickname,
                    "avatar" => user.Avatar,
                    "score" => user.Score,
                    "permission" => user.Permission,
                    "isAdmin" => user.IsAdmin,
                    "isDarkMode" => user.DarkMode,
                    "locale" => user.Locale,
                    _ => null,
                };
            case string text when name == "length":
                return text.Length;
            case ICollection collection when name is "size" or "count":
                return collection.Count;
            default:
                return null;
        }
    }

    public static object? Invoke(object? target, string name,
        IReadOnlyList<object?> arguments)
    {
        switch (target)
        {
            case null:
                return null;
            case ITemplateObject obj when obj.TryInvoke(name, arguments, out object? value):
                return value;
            case UserRepository.UserRow user when name == "isAdmin":
                return user.IsAdmin;
            case string text:
                return InvokeString(text, name, arguments);
            default:
                return null;
        }
    }

    private static object? InvokeString(string text, string name,
        IReadOnlyList<object?> arguments)
    {
        switch (name)
        {
            case "isEmpty":
                return text.Length == 0;
            case "length":
                return text.Length;
            case "toUpperCase":
                return text.ToUpperInvariant();
            case "toLowerCase":
                return text.ToLowerInvariant();
            case "trim":
                return text.Trim();
            case "contains":
                return arguments.Count > 0
                       && text.Contains(ToDisplayString(arguments[0]), StringComparison.Ordinal);
            case "startsWith":
                return arguments.Count > 0 && text.StartsWith(
                    ToDisplayString(arguments[0]), StringComparison.Ordinal);
            case "endsWith":
                return arguments.Count > 0 && text.EndsWith(
                    ToDisplayString(arguments[0]), StringComparison.Ordinal);
            default:
                return null;
        }
    }

    public static object? Index(object? target, object? key)
    {
        switch (target)
        {
            case null:
                return null;
            case PhpArray array:
                return array.Get(key ?? "");
            case IDictionary<string, object?> dictionary:
                return dictionary.TryGetValue(ToDisplayString(key), out object? dictValue)
                    ? dictValue
                    : null;
            case IReadOnlyDictionary<string, object?> readOnly:
                return readOnly.TryGetValue(ToDisplayString(key), out object? readValue)
                    ? readValue
                    : null;
            case IDictionary dictionary:
                return dictionary[key ?? ""];
            case IList list:
                return TryInt(key, out int listIndex) && listIndex >= 0 && listIndex < list.Count
                    ? list[listIndex]
                    : null;
            case string text:
                return TryInt(key, out int charIndex) && charIndex >= 0
                       && charIndex < text.Length
                    ? text[charIndex].ToString()
                    : null;
            default:
                return null;
        }
    }

    // ================================================================== 迭代

    /// <summary>把任意值展开成 <c>th:each</c> 的序列。</summary>
    public static IEnumerable<object?> Enumerate(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string:
                yield break;
            case PhpArray array:
                if (IsList(array))
                {
                    foreach (object? item in array.Values)
                    {
                        yield return item;
                    }
                }
                else
                {
                    foreach (KeyValuePair<object, object?> pair in array)
                    {
                        yield return pair;
                    }
                }

                yield break;
            case IReadOnlyDictionary<string, object?> readOnly:
                foreach (KeyValuePair<string, object?> pair in readOnly)
                {
                    yield return pair;
                }

                yield break;
            case IDictionary<string, object?> dictionary:
                foreach (KeyValuePair<string, object?> pair in dictionary)
                {
                    yield return pair;
                }

                yield break;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    yield return entry;
                }

                yield break;
            case IEnumerable enumerable:
                foreach (object? item in enumerable)
                {
                    yield return item;
                }

                yield break;
            default:
                yield break;
        }
    }

    private static bool IsList(PhpArray array)
    {
        if (array.Count == 0)
        {
            return true;
        }

        long expected = 0;
        foreach (object key in array.Keys)
        {
            if (key is not long number || number != expected)
            {
                return false;
            }

            expected++;
        }

        return true;
    }

    // ================================================================== 语义

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        float or double or decimal
            => Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture)) > double.Epsilon,
        PhpArray array => array.Count > 0,
        ICollection collection => collection.Count > 0,
        IEnumerable enumerable => enumerable.GetEnumerator().MoveNext(),
        _ => true,
    };

    public static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Math.Abs(ToDouble(left) - ToDouble(right)) < 0.0000001;
        }

        if (left is bool leftBool && right is bool rightBool)
        {
            return leftBool == rightBool;
        }

        return string.Equals(ToDisplayString(left), ToDisplayString(right),
            StringComparison.Ordinal);
    }

    public static int Compare(object? left, object? right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        return string.CompareOrdinal(ToDisplayString(left), ToDisplayString(right));
    }

    public static string ToDisplayString(object? value) => value switch
    {
        null => "",
        bool boolean => boolean ? "true" : "false",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string EscapeHtml(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsNumber(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private static double ToDouble(object? value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static bool TryInt(object? value, out int result)
    {
        switch (value)
        {
            case int number:
                result = number;
                return true;
            case long number:
                result = (int)number;
                return true;
            case double number:
                result = (int)number;
                return true;
            case string text when int.TryParse(text, out int parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}

/// <summary>模板表达式里的工具对象（<c>#strings</c> / <c>#lists</c>）。</summary>
public static class TemplateUtilities
{
    public static ITemplateObject Strings { get; } = new StringsUtil();

    public static ITemplateObject Lists { get; } = new ListsUtil();

    private sealed class StringsUtil : ITemplateObject
    {
        public bool TryGetMember(string name, out object? value)
        {
            value = null;
            return false;
        }

        public bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value)
        {
            value = name switch
            {
                "listJoin" => ListJoin(arguments),
                "isEmpty" => arguments.Count == 0
                             || string.IsNullOrEmpty(TemplateValues.ToDisplayString(arguments[0])),
                "join" => ListJoin(arguments),
                "toUpperCase" => TemplateValues.ToDisplayString(Arg(arguments, 0))
                    .ToUpperInvariant(),
                "toLowerCase" => TemplateValues.ToDisplayString(Arg(arguments, 0))
                    .ToLowerInvariant(),
                _ => null,
            };
            return value is not null;
        }

        private static object ListJoin(IReadOnlyList<object?> arguments)
        {
            string separator = arguments.Count > 1
                ? TemplateValues.ToDisplayString(arguments[1])
                : "";
            var parts = new List<string>();
            foreach (object? item in TemplateValues.Enumerate(Arg(arguments, 0)))
            {
                parts.Add(TemplateValues.ToDisplayString(item));
            }

            return string.Join(separator, parts);
        }
    }

    private sealed class ListsUtil : ITemplateObject
    {
        public bool TryGetMember(string name, out object? value)
        {
            value = null;
            return false;
        }

        public bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value)
        {
            object? first = Arg(arguments, 0);
            value = name switch
            {
                "isEmpty" => !TemplateValues.Enumerate(first).GetEnumerator().MoveNext(),
                "size" => TemplateValues.Enumerate(first).Count(),
                "contains" => TemplateValues.Enumerate(first)
                    .Any(item => TemplateValues.AreEqual(item, Arg(arguments, 1))),
                _ => null,
            };
            return value is not null;
        }
    }

    private static object? Arg(IReadOnlyList<object?> arguments, int index) =>
        index < arguments.Count ? arguments[index] : null;
}

/// <summary>模板里的校验错误集合（<c>errors.any</c> / <c>errors.first</c> / <c>errors.all()</c>）。</summary>
public sealed class TemplateErrors : ITemplateObject
{
    private readonly IReadOnlyDictionary<string, List<string>> _errors;

    public TemplateErrors(IReadOnlyDictionary<string, List<string>>? errors = null) =>
        _errors = errors ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public static TemplateErrors Empty { get; } = new();

    public bool Any => _errors.Values.Any(messages => messages.Count > 0);

    public string First
    {
        get
        {
            foreach (List<string> messages in _errors.Values)
            {
                if (messages.Count > 0)
                {
                    return messages[0];
                }
            }

            return "";
        }
    }

    public IReadOnlyList<string> All() =>
        _errors.Values.SelectMany(messages => messages).ToList();

    public bool TryGetMember(string name, out object? value)
    {
        value = name switch
        {
            "any" => Any,
            "first" => First,
            _ => null,
        };
        return value is not null;
    }

    public bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value)
    {
        switch (name)
        {
            case "any":
                value = Any;
                return true;
            case "first":
                value = First;
                return true;
            case "all":
                value = All();
                return true;
            default:
                value = null;
                return false;
        }
    }
}

/// <summary>
/// 模板里的 <c>view</c> 对象（对应 Java <c>ViewContext</c>）：
/// <c>trans</c> / <c>url</c> / <c>route</c> / <c>csrfToken</c> / <c>authCheck</c> /
/// <c>session*</c> 等。
/// </summary>
public sealed class TemplateViewContext(
    Translator? translator,
    Option? option,
    string locale,
    string? csrfToken = null,
    UserRepository.UserRow? currentUser = null,
    SessionStore? session = null,
    Func<string, string>? url = null,
    Func<string, string>? route = null) : ITemplateObject
{
    public string Locale => locale;

    public bool TryGetMember(string name, out object? value)
    {
        value = name switch
        {
            "locale" => locale,
            _ => null,
        };
        return value is not null;
    }

    public bool TryInvoke(string name, IReadOnlyList<object?> arguments, out object? value)
    {
        switch (name)
        {
            case "trans":
                string key = Text(arguments, 0);
                value = arguments.Count switch
                {
                    1 => translator?.Trans(key) ?? key,
                    2 => translator?.Trans(key, new Dictionary<string, object?>
                    {
                        [Text(arguments, 1)] = null,
                    }) ?? key,
                    _ => translator?.Trans(key, new Dictionary<string, object?>
                    {
                        [Text(arguments, 1)] = arguments[2],
                    }) ?? key,
                };
                return true;
            case "url":
                string path = Text(arguments, 0);
                value = url?.Invoke(path) ?? DefaultUrl(path);
                return true;
            case "route":
                string routeName = Text(arguments, 0);
                value = route?.Invoke(routeName) ?? DefaultRoute(routeName);
                return true;
            case "option":
                object? fallback = arguments.Count > 1 ? arguments[1] : null;
                value = option?.Get(Text(arguments, 0), fallback) ?? fallback;
                return true;
            case "optionLocalized":
                object? localizedFallback = arguments.Count > 1 ? arguments[1] : null;
                value = option?.GetLocalized(Text(arguments, 0), localizedFallback)
                        ?? localizedFallback;
                return true;
            case "csrfToken":
                value = csrfToken ?? "";
                return true;
            case "csrfField":
                value = "<input type=\"hidden\" name=\"_token\" value=\""
                        + TemplateValues.EscapeHtml(csrfToken ?? "") + "\">";
                return true;
            case "authCheck":
                value = currentUser is not null;
                return true;
            case "authUser":
                value = currentUser;
                return true;
            case "old":
                value = Old(Text(arguments, 0),
                    arguments.Count > 1 ? arguments[1] : null);
                return true;
            case "sessionHas":
                value = session?.Get(Text(arguments, 0)) is not null;
                return true;
            case "session":
                value = session?.Get(Text(arguments, 0));
                return true;
            case "sessionPull":
                string sessionKey = Text(arguments, 0);
                value = session?.Get(sessionKey);
                session?.Forget(sessionKey);
                return true;
            case "version":
                value = "6.0.11_patch";
                return true;
            case "blessing":
                value = Blessing();
                return true;
            default:
                value = null;
                return false;
        }
    }

    private object? Old(string key, object? fallback)
    {
        object? raw = session?.Get("_old_input");
        if (raw is null)
        {
            return fallback;
        }

        object? value = raw is PhpArray array
            ? array.Get(key)
            : TemplateValues.GetMember(raw, key);
        return value ?? fallback;
    }

    /// <summary><c>window.blessing</c> 的对象（模板里是 <c>${blessing}</c> 变量）。</summary>
    public PhpArray Blessing()
    {
        string siteName = option?.GetLocalized("site_name", "NeoSkin")?.ToString()
                          ?? "NeoSkin";
        return new PhpArray()
            .With("version", "6.0.11_patch")
            .With("locale", locale)
            .With("base_url", DefaultUrl("/"))
            .With("site_name", siteName);
    }

    private string DefaultUrl(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    private static readonly IReadOnlyDictionary<string, string> DefaultRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["admin.view"] = "/admin",
            ["admin.users.view"] = "/admin/users",
            ["admin.reports.view"] = "/admin/reports",
            ["admin.communications.index"] = "/admin/communications",
            ["admin.update.upload"] = "/admin/update/upload",
            ["admin.update.install"] = "/admin/update/install",
            ["user.home"] = "/user",
            ["user.profile.view"] = "/user/profile",
        };

    private static string DefaultRoute(string name) =>
        DefaultRoutes.TryGetValue(name, out string? path) ? path : "/" + name;

    private static string Text(IReadOnlyList<object?> arguments, int index) =>
        index < arguments.Count ? TemplateValues.ToDisplayString(arguments[index]) : "";
}
