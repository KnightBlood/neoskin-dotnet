namespace NeoSkin.Web.Event;

/// <summary>
/// 同步事件调度器（Laravel <c>Dispatcher</c> 的最小实现）。
///
/// <p>支持两种监听形式：类事件（<see cref="Listen{T}"/> / <see cref="Dispatch{T}"/>）
/// 和字符串事件（<c>texture.deleted</c> 这类）。监听者按注册顺序同步执行，
/// 异常直接向上抛（与源一致）。</p>
/// </summary>
public sealed class EventDispatcher
{
    private readonly Dictionary<Type, List<Action<IEvent>>> _classListeners = [];
    private readonly Dictionary<string, List<Action<object?[]>>> _stringListeners =
        new(StringComparer.Ordinal);
    private readonly List<Action<IEvent>> _allListeners = [];
    private readonly object _lock = new();

    /// <summary>注册类事件监听者。</summary>
    public void Listen<T>(Action<T> listener) where T : IEvent
    {
        lock (_lock)
        {
            if (!_classListeners.TryGetValue(typeof(T), out List<Action<IEvent>>? list))
            {
                list = [];
                _classListeners[typeof(T)] = list;
            }

            list.Add(e => listener((T)e));
        }
    }

    /// <summary>注册字符串事件监听者。</summary>
    public void Listen(string name, Action<object?[]> listener)
    {
        lock (_lock)
        {
            if (!_stringListeners.TryGetValue(name, out List<Action<object?[]>>? list))
            {
                list = [];
                _stringListeners[name] = list;
            }

            list.Add(listener);
        }
    }

    /// <summary>监听所有类事件（PF4J 插件扩展点用）。</summary>
    public void ListenAll(Action<IEvent> listener)
    {
        lock (_lock)
        {
            _allListeners.Add(listener);
        }
    }

    public void UnlistenAll(Action<IEvent> listener)
    {
        lock (_lock)
        {
            _allListeners.Remove(listener);
        }
    }

    /// <summary>派发类事件，返回原事件（监听者可修改事件里的可变集合）。</summary>
    public T Dispatch<T>(T evt) where T : IEvent
    {
        List<Action<IEvent>>? registered;
        List<Action<IEvent>> all;
        lock (_lock)
        {
            _classListeners.TryGetValue(evt.GetType(), out registered);
            all = [.. _allListeners];
        }

        if (registered is not null)
        {
            foreach (Action<IEvent> listener in registered)
            {
                listener(evt);
            }
        }

        foreach (Action<IEvent> listener in all)
        {
            listener(evt);
        }

        return evt;
    }

    /// <summary>派发字符串事件。</summary>
    public void Dispatch(string name, params object?[] payload)
    {
        List<Action<object?[]>>? registered;
        lock (_lock)
        {
            _stringListeners.TryGetValue(name, out registered);
        }

        if (registered is not null)
        {
            foreach (Action<object?[]> listener in registered)
            {
                listener(payload);
            }
        }
    }

    /// <summary>某个事件（类或字符串）的监听者数量。</summary>
    public int Count(object key)
    {
        lock (_lock)
        {
            if (key is Type type)
            {
                return _classListeners.TryGetValue(type, out List<Action<IEvent>>? list) ? list.Count : 0;
            }

            return _stringListeners.TryGetValue(key.ToString() ?? "", out List<Action<object?[]>>? strings)
                ? strings.Count
                : 0;
        }
    }

    /// <summary>清空全部监听者（测试隔离用）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _classListeners.Clear();
            _stringListeners.Clear();
            _allListeners.Clear();
        }
    }
}

/// <summary>
/// 进程级事件调度器单例（与 Java 的 <c>CacheRuntime</c> / <c>OAuthRuntime</c> 同模式）。
/// </summary>
public static class EventRuntime
{
    private static volatile EventDispatcher _dispatcher = new();

    public static EventDispatcher Dispatcher => _dispatcher;

    /// <summary>替换调度器（测试隔离/自定义）；传 null 重置。</summary>
    public static void Set(EventDispatcher? value) => _dispatcher = value ?? new EventDispatcher();

    public static T Fire<T>(T evt) where T : IEvent => _dispatcher.Dispatch(evt);

    public static void Fire(string name, params object?[] payload) => _dispatcher.Dispatch(name, payload);
}
