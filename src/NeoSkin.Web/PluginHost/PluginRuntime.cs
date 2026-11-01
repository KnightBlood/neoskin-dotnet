using NeoSkin.Web.Service;

namespace NeoSkin.Web.PluginHost;

/// <summary>
/// 运行时插件管理：发现「已启用且声明了 native 库」的插件并加载；
/// 提供 <c>filter:&lt;name&gt;</c> / <c>event:&lt;name&gt;</c> 转发。
///
/// <p>最小可用切片：启动时加载、进程退出/手动 Reload 时 <c>Shutdown</c> 并释放。
/// 插件通过 <c>package.json</c> 的 <c>"native": "文件名"</c> 声明原生库。</p>
/// </summary>
public sealed class PluginRuntime(PluginManager plugins) : IDisposable
{
    /// <summary>进程内当前实例（组合根设置；便于控制器在启用/禁用后重载）。</summary>
    public static PluginRuntime? Current { get; set; }

    private readonly List<NativePluginHost> _hosts = [];
    private readonly List<string> _errors = [];

    public IReadOnlyList<NativePluginHost> Hosts => _hosts;

    public IReadOnlyList<string> Errors => _errors;

    public bool HasPlugins => _hosts.Count > 0;

    /// <summary>扫描并加载全部已启用的原生插件；单个失败不影响其他插件。</summary>
    public void Start()
    {
        Stop();
        _errors.Clear();

        // 没有任何插件目录/子目录时完全不访问数据库（避免无插件的站点在启动时建立连接）
        bool hasCandidates = false;
        foreach (string dir in plugins.GetPluginsDirs())
        {
            if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any())
            {
                hasCandidates = true;
                break;
            }
        }

        if (!hasCandidates)
        {
            return;
        }

        Dictionary<string, Plugin> available;
        try
        {
            available = plugins.All();
        }
        catch (Exception e)
        {
            // 数据库不可用（例如尚未接入的驱动）时插件系统静默停用，不影响站点启动
            _errors.Add("插件扫描失败：" + e.Message);
            return;
        }

        foreach (Plugin plugin in available.Values)
        {
            if (!plugin.Enabled || plugin.Path is null)
            {
                continue;
            }

            string? library = plugin.NativeLibrary;
            if (string.IsNullOrWhiteSpace(library))
            {
                continue;
            }

            string path = Path.Combine(plugin.Path, library);
            try
            {
                _hosts.Add(NativePluginHost.Load(path, plugin.Name, plugin.Version));
            }
            catch (Exception e)
            {
                string message = plugin.Name + ": " + e.Message;
                _errors.Add(message);
                (NativePluginHost.LogSink ?? Console.Error.WriteLine)(message);
                try
                {
                    Event.EventRuntime.Fire(new Event.Events.PluginBootFailed(plugin));
                }
                catch (Exception)
                {
                    // 事件监听器异常不应影响启动
                }
            }
        }
    }

    /// <summary>
    /// 过滤器链：依次把上一个插件的返回值作为下一个的输入。
    /// 没有插件返回结果时返回原始 JSON。
    /// </summary>
    public string Filter(string name, string json)
    {
        string current = json;
        foreach (NativePluginHost host in _hosts)
        {
            try
            {
                string? result = host.Invoke("filter:" + name, current);
                if (result is not null)
                {
                    current = result;
                }
            }
            catch (Exception e)
            {
                _errors.Add(host.Name + " filter:" + name + " -> " + e.Message);
            }
        }

        return current;
    }

    /// <summary>事件广播（忽略返回值）。</summary>
    public void FireEvent(string name, string json)
    {
        foreach (NativePluginHost host in _hosts)
        {
            try
            {
                host.Invoke("event:" + name, json);
            }
            catch (Exception e)
            {
                _errors.Add(host.Name + " event:" + name + " -> " + e.Message);
            }
        }
    }

    /// <summary>卸载全部插件（调用 <c>Shutdown</c> 后释放库句柄）。</summary>
    public void Stop()
    {
        foreach (NativePluginHost host in _hosts)
        {
            try
            {
                host.Dispose();
            }
            catch (Exception e)
            {
                _errors.Add(host.Name + " shutdown -> " + e.Message);
            }
        }

        _hosts.Clear();
    }

    /// <summary>启用/禁用插件后重新加载。</summary>
    public void Reload() => Start();

    public void Dispose()
    {
        Stop();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
