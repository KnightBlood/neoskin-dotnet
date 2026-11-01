using System.Text;

namespace NeoSkin.Web.Support;

/// <summary>PHP 对象（<c>O:</c> / <c>C:</c> 反序列化的结果）。</summary>
public sealed class PhpObject(string className, PhpArray properties)
{
    public string ClassName { get; } = className;

    public PhpArray Properties { get; } = properties;
}

/// <summary>
/// PHP <c>serialize()</c> 的移植（语料：<c>conformance/data/cookie.json</c> 的
/// <c>serialize.*</c>）。
///
/// <p>与 <c>json_encode()</c> 的关键差别：字符串长度按<b>字节</b>算且内容完全不转义；
/// <c>true/false/null</c> 是 <c>b:1;</c> / <c>b:0;</c> / <c>N;</c>；浮点用最短往返但
/// 指数字母是<b>大写 E</b>。会话（<c>serialization = 'php'</c>）走这条路径，
/// 差一个字节 PHP 侧就解不开。</p>
/// </summary>
public static class PhpSerialize
{
    /// <summary>等价于 <c>serialize($value)</c>。</summary>
    public static string Serialize(object? value) => new Serializer().Run(value);

    private sealed class Serializer
    {
        private int _n;
        private readonly Dictionary<object, int> _objectSlots = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _openStack = [];

        internal string Run(object? root)
        {
            var sb = new StringBuilder();
            Write(sb, root);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, object? value)
        {
            int slot = ++_n;

            // 值是当前打开链上的容器（自引用数组）
            if (_openStack.Count > 0)
            {
                foreach (object open in _openStack)
                {
                    if (ReferenceEquals(open, value))
                    {
                        // 引用重现：PHP 在 add_var_hash 命中时回吐 data->n
                        _n--;
                        if (open is PhpObject)
                        {
                            int objectSlot = _objectSlots.TryGetValue(value!, out int known) ? known : slot;
                            sb.Append("r:").Append(objectSlot).Append(';');
                        }
                        else
                        {
                            sb.Append("R:").Append(slot).Append(';');
                        }

                        return;
                    }
                }
            }

            switch (value)
            {
                case null:
                    sb.Append("N;");
                    return;
                case bool b:
                    sb.Append(b ? "b:1;" : "b:0;");
                    return;
                case double d:
                    sb.Append("d:").Append(DoubleLiteral(d)).Append(';');
                    return;
                case float f:
                    sb.Append("d:").Append(DoubleLiteral(f)).Append(';');
                    return;
                case sbyte or byte or short or ushort or int or uint or long:
                    sb.Append("i:").Append(Convert.ToInt64(value)).Append(';');
                    return;
                case ulong ul:
                    sb.Append("i:").Append(ul).Append(';');
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case PhpObject obj:
                    WriteObject(sb, obj, slot);
                    return;
                case PhpArray array:
                    WriteArray(sb, array);
                    return;
                case System.Collections.IDictionary dict:
                    WriteArray(sb, FromDictionary(dict));
                    return;
                case System.Collections.IEnumerable list:
                    WriteArray(sb, FromEnumerable(list));
                    return;
                default:
                    WriteString(sb, PhpString.Cast(value));
                    return;
            }
        }

        private static PhpArray FromDictionary(System.Collections.IDictionary dict)
        {
            var array = new PhpArray();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                array.Put(entry.Key ?? "", entry.Value);
            }

            return array;
        }

        private static PhpArray FromEnumerable(System.Collections.IEnumerable list)
        {
            var array = new PhpArray();
            long index = 0;
            foreach (object? item in list)
            {
                array.Put(index++, item);
            }

            return array;
        }

        private void WriteObject(StringBuilder sb, PhpObject obj, int slot)
        {
            if (_objectSlots.TryGetValue(obj, out int seen))
            {
                sb.Append("r:").Append(seen).Append(';');
                return;
            }

            _objectSlots[obj] = slot;
            sb.Append("O:").Append(Encoding.UTF8.GetByteCount(obj.ClassName))
                .Append(":\"").Append(obj.ClassName).Append("\":");
            _openStack.Add(obj);
            sb.Append(obj.Properties.Count).Append(":{");
            foreach (KeyValuePair<object, object?> entry in obj.Properties)
            {
                WriteString(sb, PhpString.Cast(entry.Key));
                Write(sb, entry.Value);
            }

            sb.Append('}');
            _openStack.RemoveAt(_openStack.Count - 1);
        }

        private void WriteArray(StringBuilder sb, PhpArray raw)
        {
            sb.Append("a:").Append(raw.Count).Append(":{");
            _openStack.Add(raw);
            foreach (KeyValuePair<object, object?> entry in raw)
            {
                object key = PhpJson.NormalizeKey(entry.Key);
                if (key is long l)
                {
                    sb.Append("i:").Append(l).Append(';');
                }
                else
                {
                    WriteString(sb, PhpString.Cast(key));
                }

                Write(sb, entry.Value);
            }

            _openStack.RemoveAt(_openStack.Count - 1);
            sb.Append('}');
        }

        /// <summary><c>s:&lt;字节长度&gt;:"&lt;原样内容&gt;";</c> —— 内容不转义任何字符。</summary>
        private static void WriteString(StringBuilder sb, string s) =>
            sb.Append("s:").Append(Encoding.UTF8.GetByteCount(s))
                .Append(":\"").Append(s).Append("\";");

        /// <summary><c>d:</c> 的数字串：复用 <see cref="PhpJson.Number"/>，只把 E 大写。</summary>
        private static string DoubleLiteral(double d)
        {
            if (double.IsNaN(d))
            {
                return "NAN";
            }

            if (double.IsPositiveInfinity(d))
            {
                return "INF";
            }

            if (double.IsNegativeInfinity(d))
            {
                return "-INF";
            }

            return PhpJson.Number(d).Replace('e', 'E');
        }
    }
}
