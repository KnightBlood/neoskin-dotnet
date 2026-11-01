using NeoSkin.Web.Service;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.I18n;

/// <summary>
/// <c>App\Services\Translations\JavaScript</c> 的简化实现：把 <c>front-end</c> 组的
/// 翻译表编译成 <c>public/lang/&lt;locale&gt;.js</c> 的内容
/// （<c>blessing.i18n = {...}</c>）。
///
/// <p>已启用插件的 <c>lang/&lt;locale&gt;/front-end.yml</c> 会以插件名为键合并，
/// 对应 PHP 的插件命名空间语言包。</p>
/// </summary>
public sealed class JavaScriptBundle(
    TranslationFileLoader loader,
    PluginManager? plugins = null)
{
    public const string Assignment = "blessing.i18n = ";

    /// <summary>生成某个 locale 的前端语言包脚本。</summary>
    public string Generate(string locale)
    {
        var translator = new Translator(loader, locale, Locales.FallbackLocale);
        object? table = translator.Get("front-end", null, locale);
        var merged = new PhpArray();
        switch (table)
        {
            case PhpArray array:
                foreach (KeyValuePair<object, object?> entry in array)
                {
                    merged.Put(entry.Key, entry.Value);
                }

                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                foreach ((string key, object? value) in dictionary)
                {
                    merged.Put(key, value);
                }

                break;
        }

        if (plugins is not null
            && plugins.GetPluginsDirs().Any(Directory.Exists))
        {
            Dictionary<string, Plugin> available;
            try
            {
                available = plugins.All();
            }
            catch (Exception e) when (e is InvalidOperationException
                                          or NotSupportedException
                                          or System.Data.Common.DbException)
            {
                available = new Dictionary<string, Plugin>(StringComparer.Ordinal);
            }

            foreach (Plugin plugin in available.Values)
            {
                if (!plugin.Enabled || plugin.Path is null || plugin.Name is null)
                {
                    continue;
                }

                string file = Path.Combine(plugin.Path, "lang", locale, "front-end.yml");
                if (!File.Exists(file))
                {
                    continue;
                }

                Dictionary<string, object?>? parsed = SymfonyYaml.ParseResource(file);
                if (parsed is not null)
                {
                    merged.Put(plugin.Name, ToPhpArray(parsed));
                }
            }
        }

        return Assignment + PhpJson.Encode(merged, PhpJson.JsonUnescapedUnicode);
    }

    private static PhpArray ToPhpArray(IReadOnlyDictionary<string, object?> source)
    {
        var output = new PhpArray();
        foreach ((string key, object? value) in source)
        {
            output.Put(key, value is IReadOnlyDictionary<string, object?> nested
                ? ToPhpArray(nested)
                : value);
        }

        return output;
    }
}
