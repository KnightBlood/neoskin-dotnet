using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Error;

/// <summary>
/// <c>json_encode($v, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES)</c> 的等价实现。
///
/// <p>只服务 Laravel 异常处理器 <c>prepareJsonResponse()</c> 的 4 空格缩进输出：
/// 空表是 <c>[]</c> 不换行；对象每个元素一行、冒号后一个空格；
/// 字符串转义与压缩模式一致（但斜杠不转义）。</p>
/// </summary>
public static class PrettyJson
{
    private const string Indent = "    ";

    public static string Encode(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case PhpArray array:
                WriteArray(sb, array, depth);
                return;
            case System.Collections.IDictionary dictionary:
                var normalized = new PhpArray();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    normalized.Put(entry.Key ?? "", entry.Value);
                }

                WriteArray(sb, normalized, depth);
                return;
            case System.Collections.IEnumerable list when value is not string:
                var fromList = new PhpArray();
                long index = 0;
                foreach (object? item in list)
                {
                    fromList.Put(index++, item);
                }

                WriteArray(sb, fromList, depth);
                return;
            default:
                sb.Append(PhpJson.Encode(value, PhpJson.JsonUnescapedSlashes));
                return;
        }
    }

    private static void WriteArray(StringBuilder sb, PhpArray array, int depth)
    {
        if (array.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        bool isList = PhpJson.IsList(array);
        sb.Append(isList ? '[' : '{');
        bool first = true;
        foreach (KeyValuePair<object, object?> entry in array)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            Newline(sb, depth + 1);
            if (!isList)
            {
                sb.Append(PhpJson.Encode(PhpString.Cast(entry.Key), PhpJson.JsonUnescapedSlashes));
                sb.Append(": ");
            }

            Write(sb, entry.Value, depth + 1);
        }

        Newline(sb, depth);
        sb.Append(isList ? ']' : '}');
    }

    private static void Newline(StringBuilder sb, int depth) =>
        sb.Append('\n').Append(string.Concat(Enumerable.Repeat(Indent, depth)));
}

/// <summary>Symfony <c>Response::$statusTexts</c> 的子集。</summary>
public static class HttpStatus
{
    private static readonly Dictionary<int, string> Texts = new()
    {
        [200] = "OK",
        [201] = "Created",
        [204] = "No Content",
        [301] = "Moved Permanently",
        [302] = "Found",
        [304] = "Not Modified",
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [402] = "Payment Required",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [413] = "Content Too Large",
        [422] = "Unprocessable Content",
        [419] = "Page Expired",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [502] = "Bad Gateway",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout",
    };

    public static string Text(int code) => Texts.TryGetValue(code, out string? text) ? text : "unknown status";
}

/// <summary>Laravel <c>convertExceptionToArray()</c> 的 JSON 形态。</summary>
public static class ErrorPayload
{
    /// <summary><c>{message, exception:true, trace:[]}</c>（pretty-print）。</summary>
    public static string Json(string message, IReadOnlyList<Dictionary<string, object?>>? trace = null)
    {
        var payload = new PhpArray()
            .With("message", message)
            .With("exception", true);
        payload.Put("trace", trace is null ? new PhpArray() : trace);
        return PrettyJson.Encode(payload);
    }
}
