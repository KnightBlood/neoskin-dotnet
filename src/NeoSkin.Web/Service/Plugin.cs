using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// <c>App\Services\Plugin</c> 的移植：插件目录 + <c>package.json</c> 清单。
///
/// <p>AOT 构建不能执行插件代码，本类只负责清单解析与文件系统元信息。</p>
/// </summary>
public sealed class Plugin
{
    public static readonly IReadOnlyList<string> ReadmeFiles =
        ["README.md", "readme.md", "README.MD"];

    private readonly PhpArray _manifest;

    public Plugin(string? path, PhpArray manifest)
    {
        Path = path;
        _manifest = manifest;
    }

    public string? Path { get; }

    public PhpArray Manifest => _manifest;

    public bool Enabled { get; private set; }

    public Plugin SetEnabled(bool enabled)
    {
        Enabled = enabled;
        return this;
    }

    public string? Name => Str("name");

    public string Title => Str("title") is { Length: > 0 } title ? title : Name ?? "";

    public string? Description => Str("description");

    public string? Version => Str("version");

    public string? Namespace => Str("namespace");

    /// <summary>原生插件库文件名（<c>package.json</c> 的 <c>native</c> 字段）。</summary>
    public string? NativeLibrary => Attr("native", null) is string value && value.Length > 0
        ? value
        : null;

    /// <summary><c>Arr::get($manifest, $name, $default)</c>（支持点路径）。</summary>
    public object? Attr(string name, object? defaultValue)
    {
        object? current = _manifest;
        foreach (string segment in name.Split('.'))
        {
            if (current is not PhpArray map)
            {
                return defaultValue;
            }

            object? next = map.Get(segment);
            if (next is null)
            {
                return defaultValue;
            }

            current = next;
        }

        return current;
    }

    public string? GetReadme()
    {
        if (Path is null)
        {
            return null;
        }

        foreach (string filename in ReadmeFiles)
        {
            if (File.Exists(System.IO.Path.Combine(Path, filename)))
            {
                return filename;
            }
        }

        return null;
    }

    /// <summary><c>enchants.config</c> 是否声明。</summary>
    public bool HasConfigClass() =>
        Attr("enchants", null) is PhpArray enchants && enchants.Get("config") is not null;

    /// <summary><c>views/&lt;config 或 config.blade.php&gt;</c> 是否存在。</summary>
    public bool HasConfigView()
    {
        if (Path is null)
        {
            return false;
        }

        object? filename = _manifest.Get("config") ?? "config.blade.php";
        if (filename is null)
        {
            return false;
        }

        return File.Exists(System.IO.Path.Combine(Path, "views", PhpString.Cast(filename)));
    }

    public bool HasConfig() => HasConfigClass() || HasConfigView();

    /// <summary><c>enchants.icon</c>：默认 <c>{fa:plug, faType:fas, bg:navy}</c>。</summary>
    public PhpArray Icon()
    {
        var icon = new PhpArray()
            .With("fa", "plug")
            .With("faType", "fas")
            .With("bg", "navy");
        if (Attr("enchants.icon", null) is PhpArray custom)
        {
            foreach (KeyValuePair<object, object?> entry in custom)
            {
                icon.Put(PhpString.Cast(entry.Key), entry.Value);
            }
        }

        return icon;
    }

    private string? Str(string key)
    {
        object? value = _manifest.Get(key);
        return value is null ? null : PhpString.Cast(value);
    }
}
