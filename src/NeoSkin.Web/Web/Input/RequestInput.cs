using NeoSkin.Web.Support;

namespace NeoSkin.Web.Web.Input;

/// <summary>
/// 源 <c>App\Http\Middleware\RequestInput</c> 的等价物：把 query 与请求体（form 或 JSON）
/// 合并成一个字典（体覆盖 query），并支持点号取值。
/// </summary>
public static class RequestInput
{
    /// <summary>读取并缓存到 <c>Items["neoskin.input"]</c>（同一请求只解析一次）。</summary>
    public static async Task<Dictionary<string, object?>> BagAsync(HttpContext context)
    {
        if (context.Items["neoskin.input"] is Dictionary<string, object?> cached)
        {
            return cached;
        }

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> entry
                 in context.Request.Query)
        {
            merged[entry.Key] = entry.Value.ToString();
        }

        string contentType = context.Request.ContentType ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(context.Request.Body,
                System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            string body = await reader.ReadToEndAsync();
            if (body.Length > 0)
            {
                try
                {
                    if (PhpJson.Decode(body) is PhpArray parsed)
                    {
                        foreach (KeyValuePair<object, object?> entry in parsed)
                        {
                            merged[PhpString.Cast(entry.Key)] = entry.Value;
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException or FormatException)
                {
                    // 非法 JSON：与源一致，按空输入处理
                }
            }
        }
        else if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> entry
                     in form)
            {
                merged[entry.Key] = entry.Value.ToString();
            }
        }

        context.Items["neoskin.input"] = merged;
        return merged;
    }

    /// <summary><c>input($key)</c>：缺失返回 null。</summary>
    public static object? Input(Dictionary<string, object?> bag, string key) =>
        bag.TryGetValue(key, out object? value) ? value : null;

    /// <summary><c>input($key)</c> 的字符串形态。</summary>
    public static string String(Dictionary<string, object?> bag, string key) =>
        bag.TryGetValue(key, out object? value) && value is not null
            ? PhpString.Cast(value)
            : "";

    /// <summary>PHP <c>(int)</c> 语义的近似实现。</summary>
    public static int Int(Dictionary<string, object?> bag, string key) =>
        ToInt(Input(bag, key));

    public static int ToInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        decimal m => (int)m,
        bool b => b ? 1 : 0,
        _ => int.TryParse(value.ToString()?.Trim(), out int parsed) ? parsed : 0,
    };

    /// <summary>PHP 真值语义。</summary>
    public static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s is not ("" or "0" or "false"),
        long l => l != 0,
        int i => i != 0,
        _ => true,
    };

    /// <summary>数组输入（<c>input('type')</c> 可能是数组）。</summary>
    public static List<string> List(Dictionary<string, object?> bag, string key)
    {
        var output = new List<string>();
        switch (Input(bag, key))
        {
            case PhpArray array:
                foreach (object? item in array.Values)
                {
                    output.Add(PhpString.Cast(item));
                }

                break;
            case System.Collections.IEnumerable enumerable and not string:
                foreach (object? item in enumerable)
                {
                    output.Add(PhpString.Cast(item));
                }

                break;
        }

        return output;
    }
}
