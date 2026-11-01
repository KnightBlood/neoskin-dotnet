using System.Collections.Concurrent;

namespace NeoSkin.Web.Service;

/// <summary>
/// 进程内图片响应缓存（Laravel <c>Cache::remember</c> 的载体），供头像/预览端点
/// 与后台「清除缓存」共用。对应 Java 版 <c>com.neoskin.service.ImageCache</c>。
/// </summary>
public sealed class ImageCache
{
    /// <summary>缓存条目；<see cref="Body"/> 同时用于计算 ETag。</summary>
    public sealed record Entry(byte[] Body, string ContentType, string? LastModified,
        DateTimeOffset ExpiresAt = default);

    private const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>取未过期条目；过期或不存在返回 <c>null</c>。</summary>
    public Entry? Get(string key)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry;
    }

    /// <summary><c>Cache::remember</c>：命中则复用，否则执行 <paramref name="loader"/> 并写入。</summary>
    public Entry Remember(string key, TimeSpan ttl, Func<Entry> loader)
    {
        Entry? existing = Get(key);
        if (existing is not null)
        {
            return existing;
        }

        Entry fresh = loader() with { ExpiresAt = DateTimeOffset.UtcNow + ttl };
        Put(key, fresh);
        return fresh;
    }

    /// <summary>写入条目（覆盖同名键）。</summary>
    public void Put(string key, Entry entry)
    {
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear(); // 粗粒度护栏，与 Java 版一致
        }

        _entries[key] = entry;
    }

    /// <summary><c>Cache::flush()</c>：清空所有条目。</summary>
    public void Clear() => _entries.Clear();

    public int Count => _entries.Count;
}
