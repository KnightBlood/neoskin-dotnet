namespace NeoSkin.Web.Support;

/// <summary>
/// Laravel <c>Arr::get()</c> / <c>Arr::has()</c> / <c>Arr::exists()</c> 的移植。
///
/// <p>照抄的关键点（由 <c>conformance/data/locale.json</c> 的 <c>lookup.*</c> 锁定）：</p>
/// <list type="number">
///   <item><c>get</c> 在 <c>key === null</c> 时直接返回整个数组，不看默认值；</item>
///   <item><c>get</c> 先做「整体键」命中：数组里真有一个叫 <c>"a.b"</c> 的键时优先于点号下钻；</item>
///   <item><c>get</c> 的键没有 <c>'.'</c> 时返回默认值，不会把键当路径；</item>
///   <item><c>has</c> 在数组为空或 keys 为空时返回 false；</item>
///   <item><c>has</c> 每一段都要求 <c>accessible</c>，中途遇到标量即整体失败。</item>
/// </list>
/// </summary>
public static class PhpArr
{
    /// <summary><c>Arr::accessible()</c>：PHP 的 array 即可（对应 <see cref="PhpArray"/> 与 <see cref="List{T}"/>）。</summary>
    public static bool Accessible(object? value) => value is PhpArray or System.Collections.IDictionary or System.Collections.IList;

    /// <summary><c>Arr::exists()</c>。</summary>
    public static bool Exists(object? array, object? key)
    {
        if (!Accessible(array))
        {
            return false;
        }

        object? raw = key is double or float or null ? PhpString.Cast(key) : key;
        object normalized = PhpJson.NormalizeKey(raw!);
        if (array is PhpArray map)
        {
            return map.ContainsKey(normalized);
        }

        if (array is System.Collections.IDictionary dictionary)
        {
            return dictionary.Contains(normalized);
        }

        var list = (System.Collections.IList)array!;
        return normalized is long l && l >= 0 && l < list.Count;
    }

    /// <summary><c>Arr::get($array, $key, $default)</c>。</summary>
    public static object? Get(object? array, object? key, object? fallback)
    {
        if (!Accessible(array))
        {
            return fallback;
        }

        if (key is null)
        {
            return array;
        }

        if (Exists(array, key))
        {
            return Value(array!, key);
        }

        string path = PhpString.Cast(key);
        if (!path.Contains('.', StringComparison.Ordinal))
        {
            return fallback;
        }

        object? cursor = array;
        foreach (string segment in path.Split('.'))
        {
            if (Accessible(cursor) && Exists(cursor, segment))
            {
                cursor = Value(cursor!, segment);
            }
            else
            {
                return fallback;
            }
        }

        return cursor;
    }

    /// <summary>便捷重载：无默认值。</summary>
    public static object? Get(object? array, object? key) => Get(array, key, null);

    /// <summary><c>Arr::has($array, $keys)</c>：数组形式的多键要求全部命中。</summary>
    public static bool Has(object? array, object? keys)
    {
        List<object?> keyList = keys switch
        {
            System.Collections.IList list => list.Cast<object?>().ToList(),
            null => [],
            _ => [keys],
        };

        if (!Truthy(array) || keyList.Count == 0)
        {
            return false;
        }

        foreach (object? key in keyList)
        {
            if (Exists(array, key))
            {
                continue;
            }

            object? cursor = array;
            bool ok = true;
            foreach (string segment in PhpString.Cast(key).Split('.'))
            {
                if (Accessible(cursor) && Exists(cursor, segment))
                {
                    cursor = Value(cursor!, segment);
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>PHP 的 <c>(bool)</c>：空数组、空串、<c>"0"</c>、<c>0</c>、<c>null</c> 都是 false。</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        PhpArray map => map.Count > 0,
        System.Collections.ICollection collection => collection.Count > 0,
        string s => s.Length > 0 && s != "0",
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value) != 0,
        ulong ul => ul != 0,
        float f => f != 0,
        double d => d != 0,
        decimal m => m != 0,
        _ => PhpString.Cast(value).Length > 0 && PhpString.Cast(value) != "0",
    };

    private static object? Value(object array, object? key)
    {
        object normalized = PhpJson.NormalizeKey(
            key is double or float or null ? PhpString.Cast(key) : key!);
        if (array is PhpArray map)
        {
            return map.Get(normalized);
        }

        if (array is System.Collections.IDictionary dictionary)
        {
            return dictionary[normalized];
        }

        var list = (System.Collections.IList)array;
        return list[Convert.ToInt32(normalized)];
    }
}
