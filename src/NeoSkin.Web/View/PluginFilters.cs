using NeoSkin.Web.PluginHost;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.View;

/// <summary>
/// 渲染层插件过滤器（<c>filter:&lt;name&gt;</c>）的类型适配：
/// 列表/字符串经 <see cref="PhpJson"/> 往返，插件无结果或结果损坏时回退原值。
/// </summary>
public static class PluginFilters
{
    public static List<object?> ApplyList(string name, List<object?> items,
        PluginRuntime? runtime)
    {
        if (runtime is null || !runtime.HasPlugins)
        {
            return items;
        }

        string json = PhpJson.Encode(items, 0);
        string result = runtime.Filter(name, json);
        if (string.Equals(result, json, StringComparison.Ordinal))
        {
            return items;
        }

        try
        {
            return PhpJson.Decode(result) is PhpArray array
                ? array.Values.Cast<object?>().ToList()
                : items;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return items;
        }
    }

    public static string ApplyString(string name, string value, PluginRuntime? runtime)
    {
        if (runtime is null || !runtime.HasPlugins)
        {
            return value;
        }

        string json = PhpJson.Encode(value, 0);
        string result = runtime.Filter(name, json);
        if (string.Equals(result, json, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return PhpJson.Decode(result) is string text ? text : value;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return value;
        }
    }
}
