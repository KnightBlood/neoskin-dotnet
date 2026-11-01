using System.Globalization;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// 站点选项服务（源 <c>App\Services\Option</c>）。
///
/// <list type="number">
///   <item>优先读 <c>storage/options.php</c> 缓存；</item>
///   <item>没有缓存且 <c>storage/install.lock</c> 不存在（未安装）→ 全部为空；</item>
///   <item>否则从 <c>options</c> 表加载全部键值；</item>
///   <item><see cref="Get"/> 缺失时回退到 <see cref="OptionDefaults"/>，<b>并把默认值写回数据库</b>；</item>
///   <item>把 <c>'true'</c>/<c>'(true)'</c>/<c>'false'</c>/<c>'(false)'</c>/<c>'null'</c>/<c>'(null)'</c>
///     转成布尔/null（<c>raw=true</c> 不转）；</item>
///   <item><see cref="Set"/> 同时更新内存与数据库；写库失败只吞掉异常。</item>
/// </list>
/// </summary>
public sealed class Option(Database db, SchemaInstaller schema, AppPaths paths)
{
    private const string NullSentinel = "\0__NULL__\0";

    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _loaded;

    private static readonly AsyncLocal<string?> Locale = new();

    /// <summary>当前请求语言（由语言中间件设置；默认 <c>zh_CN</c>）。</summary>
    public static string CurrentLocale
    {
        get => Locale.Value ?? "zh_CN";
        set => Locale.Value = string.IsNullOrWhiteSpace(value) ? "zh_CN" : value;
    }

    public static void ClearCurrentLocale() => Locale.Value = null;

    // ------------------------------------------------------------------ 读

    public object? Get(string key) => Get(key, null, false);

    public object? Get(string key, object? defaultValue) => Get(key, defaultValue, false);

    public object? Get(string key, object? defaultValue, bool raw)
    {
        EnsureLoaded();

        if (!_items.ContainsKey(key) && OptionDefaults.Has(key))
        {
            Set(key, OptionDefaults.Get(key));
        }

        string? value = _items.TryGetValue(key, out string? stored)
            ? stored
            : defaultValue is null ? null : PhpString.Cast(defaultValue);
        if (value == NullSentinel)
        {
            value = null;
        }

        return raw ? value : Coerce(value);
    }

    public string? GetString(string key, string? defaultValue) => Get(key, defaultValue, true)?.ToString();

    public bool GetBoolean(string key, bool defaultValue)
    {
        object? value = Get(key, defaultValue);
        return value switch
        {
            bool b => b,
            null => defaultValue,
            string s when s.Length == 0 => defaultValue,
            string s when bool.TryParse(s, out bool parsed) => parsed,
            string s => s is not ("0" or "false" or "off" or "no"),
            long l => l != 0,
            int i => i != 0,
            double d => d != 0,
            _ => defaultValue,
        };
    }

    public int GetInt(string key, int defaultValue) => Get(key) switch
    {
        long l => (int)l,
        int i => i,
        null => defaultValue,
        object o when int.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int parsed) => parsed,
        _ => defaultValue,
    };

    /// <summary>
    /// <c>option_localized($key, $default, $raw)</c> =
    /// <c>option($key.'_'.locale, option($key, $default), $raw)</c>。
    /// </summary>
    public object? GetLocalized(string key, object? defaultValue = null, bool raw = false)
    {
        object? fallback = Get(key, defaultValue);
        string localized = key + "_" + CurrentLocale;

        if (!_items.ContainsKey(localized) && OptionDefaults.Has(localized))
        {
            Set(localized, OptionDefaults.Get(localized));
        }

        object? value = _items.TryGetValue(localized, out string? stored) ? stored : fallback;
        if (value is string sentinel && sentinel == NullSentinel)
        {
            value = null;
        }

        if (raw)
        {
            return value;
        }

        return value is string s ? Coerce(s) : value;
    }

    /// <summary><c>Option::all()</c>：原始字符串（不做布尔转换）。</summary>
    public IReadOnlyDictionary<string, object?> All()
    {
        EnsureLoaded();
        var plain = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in _items)
        {
            plain[entry.Key] = entry.Value == NullSentinel ? null : entry.Value;
        }

        return plain;
    }

    // ------------------------------------------------------------------ 写

    public void Set(string key, object? value)
    {
        EnsureLoaded();
        string? text = value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            _ => PhpString.Cast(value),
        };

        _items[key] = text ?? NullSentinel;
        Persist(key, text);
    }

    public void SetAll(IEnumerable<KeyValuePair<string, object?>> values)
    {
        foreach (KeyValuePair<string, object?> entry in values)
        {
            Set(entry.Key, entry.Value);
        }
    }

    public void Remove(string key)
    {
        EnsureLoaded();
        _items.Remove(key);
    }

    // ------------------------------------------------------------------ 缓存文件

    public bool IsInstalled() => File.Exists(paths.InstallLock);

    /// <summary><c>php artisan options:cache</c>：写入 <c>storage/options.php</c>。</summary>
    public void WriteCache()
    {
        EnsureLoaded();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OptionsCache)!);
        File.WriteAllText(paths.OptionsCache,
            PhpVarExport.ToPhpFile(All().Select(pair =>
                new KeyValuePair<string, object?>(pair.Key, pair.Value))));
    }

    /// <summary><c>php artisan options:clear</c>。</summary>
    public void ClearCache()
    {
        if (File.Exists(paths.OptionsCache))
        {
            File.Delete(paths.OptionsCache);
        }
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>PHP 的 <c>'true'/'false'/'null'</c>（可带括号）转换。</summary>
    public static object? Coerce(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "true" or "(true)" => true,
        "false" or "(false)" => false,
        "null" or "(null)" => null,
        _ => value,
    };

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            Load();
            _loaded = true;
        }
    }

    private void Load()
    {
        if (File.Exists(paths.OptionsCache))
        {
            try
            {
                foreach (KeyValuePair<string, object?> entry in
                         PhpVarExport.ParseArray(File.ReadAllText(paths.OptionsCache)))
                {
                    _items[entry.Key] = Stringify(entry.Value);
                }

                return;
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                // 坏缓存回退数据库（源站会抛错；这里更利于运维）
            }
        }

        if (!IsInstalled())
        {
            return;
        }

        string sql = "SELECT " + db.Dialect.Quote("option_name") + ", "
            + db.Dialect.Quote("option_value") + " FROM " + db.Dialect.Quote(schema.Table("options"));
        try
        {
            foreach ((string name, string? value) in db.Query(sql,
                         reader => (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))))
            {
                _items[name] = value ?? NullSentinel;
            }
        }
        catch (Exception e) when (e is System.Data.Common.DbException)
        {
            // options 表可能尚未创建（首次启动）
        }
    }

    private void Persist(string key, string? value)
    {
        string table = db.Dialect.Quote(schema.Table("options"));
        string update = "UPDATE " + table + " SET " + db.Dialect.Quote("option_value")
            + " = @v WHERE " + db.Dialect.Quote("option_name") + " = @k";
        string insert = "INSERT INTO " + table + " (" + db.Dialect.Quote("option_name") + ", "
            + db.Dialect.Quote("option_value") + ") VALUES (@k, @v)";
        try
        {
            if (db.Execute(update, ("@v", value), ("@k", key)) > 0)
            {
                return;
            }

            db.Execute(insert, ("@k", key), ("@v", value));
        }
        catch (System.Data.Common.DbException)
        {
            // 与 PHP 一致：写库失败不影响内存（未安装、表不存在、值 null 触发 NOT NULL）
        }
    }

    private static string Stringify(object? value) => value switch
    {
        null => NullSentinel,
        bool b => b ? "true" : "false",
        _ => PhpString.Cast(value),
    };
}
