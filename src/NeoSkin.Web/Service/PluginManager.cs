using NeoSkin.Web.Config;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// <c>App\Services\PluginManager</c> 的清单层移植。
///
/// <p>保留：多插件目录扫描、<c>plugins_enabled</c> 持久化、依赖/冲突检查（<see cref="Semver"/>）、
/// enable/disable/delete。**不执行插件代码**（AOT 无 PHP 运行时）。</p>
/// </summary>
public sealed class PluginManager(Option option, NeoSkinSettings settings, AppPaths paths)
{
    /// <summary><c>getPluginsDirs()</c>：逗号分隔的插件目录。</summary>
    public List<string> GetPluginsDirs()
    {
        string config = settings.Plugins.Directory;
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(config))
        {
            foreach (string part in config.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                dirs.Add(System.IO.Path.IsPathRooted(part)
                    ? part
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(paths.Base, part)));
            }
        }
        else
        {
            dirs.Add(System.IO.Path.Combine(paths.Base, "plugins"));
        }

        return dirs;
    }

    /// <summary><c>plugins_enabled</c>：name → version。</summary>
    public Dictionary<string, string?> LoadEnabled()
    {
        var enabled = new Dictionary<string, string?>(StringComparer.Ordinal);
        object? raw = option.Get("plugins_enabled", "[]", true);
        if (raw is null)
        {
            return enabled;
        }

        try
        {
            if (PhpJson.Decode(PhpString.Cast(raw)) is PhpArray items)
            {
                foreach (object? item in items.Values)
                {
                    if (item is PhpArray entry && entry.Get("name") is { } name)
                    {
                        enabled[PhpString.Cast(name)] = entry.Get("version") is { } version
                            ? PhpString.Cast(version)
                            : null;
                    }
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            // 损坏的 JSON 视为空
        }

        return enabled;
    }

    private void SaveEnabled(Dictionary<string, string?> enabled)
    {
        var list = new PhpArray();
        int index = 0;
        foreach ((string name, string? version) in enabled)
        {
            list.Put(index++, new PhpArray().With("name", name).With("version", version));
        }

        option.Set("plugins_enabled", PhpJson.Encode(list, 0));
    }

    /// <summary><c>all()</c>：扫描全部插件目录。</summary>
    public Dictionary<string, Plugin> All()
    {
        Dictionary<string, string?> enabled = LoadEnabled();
        var plugins = new Dictionary<string, Plugin>(StringComparer.Ordinal);
        var changed = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (string dir in GetPluginsDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            List<string> candidates = Directory.GetDirectories(dir)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            foreach (string candidate in candidates)
            {
                string manifestFile = System.IO.Path.Combine(candidate, "package.json");
                if (!File.Exists(manifestFile))
                {
                    continue;
                }

                PhpArray? decoded;
                try
                {
                    decoded = PhpJson.Decode(File.ReadAllText(manifestFile)) as PhpArray;
                }
                catch (IOException)
                {
                    continue;
                }

                if (decoded?.Get("name") is null)
                {
                    continue;
                }

                string name = PhpString.Cast(decoded.Get("name"));
                if (plugins.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        "Duplicate plugin name: " + name + " (" + plugins[name].Path + ", "
                        + candidate + ")");
                }

                var plugin = new Plugin(candidate, decoded);
                if (GetUnsatisfied(plugin, enabled).Count > 0
                    || GetConflicts(plugin, enabled).Count > 0)
                {
                    Disable(plugin, enabled);
                }

                if (enabled.TryGetValue(name, out string? recorded))
                {
                    plugin.SetEnabled(true);
                    if (!string.Equals(plugin.Version, recorded, StringComparison.Ordinal))
                    {
                        enabled[name] = plugin.Version;
                        changed[name] = plugin.Version;
                    }
                }

                plugins[name] = plugin;
            }
        }

        if (changed.Count > 0)
        {
            SaveEnabled(enabled);
            foreach (string name in changed.Keys)
            {
                if (plugins.TryGetValue(name, out Plugin? plugin))
                {
                    Event.EventRuntime.Fire("plugin.versionChanged", plugin);
                }
            }
        }

        return plugins;
    }

    public Plugin? Get(string? name) =>
        name is not null && All().TryGetValue(name, out Plugin? plugin) ? plugin : null;

    public List<Plugin> GetEnabledPlugins() =>
        All().Values.Where(plugin => plugin.Enabled).ToList();

    /// <summary>
    /// <c>enable($plugin)</c>：成功返回 null；失败返回 (unsatisfied, conflicts)。
    /// </summary>
    public (Dictionary<string, (string? Version, string Constraint)> Unsatisfied,
        Dictionary<string, (string? Version, string Constraint)> Conflicts)? Enable(string name)
    {
        Dictionary<string, string?> enabled = LoadEnabled();
        Plugin? plugin = All().GetValueOrDefault(name);
        if (plugin is null || plugin.Enabled)
        {
            return null;
        }

        Dictionary<string, (string?, string)> unsatisfied = GetUnsatisfied(plugin, enabled);
        Dictionary<string, (string?, string)> conflicts = GetConflicts(plugin, enabled);
        if (unsatisfied.Count > 0 || conflicts.Count > 0)
        {
            return (unsatisfied, conflicts);
        }

        enabled[plugin.Name!] = plugin.Version;
        SaveEnabled(enabled);
        plugin.SetEnabled(true);
        Event.EventRuntime.Fire(new Event.Events.PluginWasEnabled(plugin));
        return null;
    }

    public void Disable(string name) => Disable(All().GetValueOrDefault(name), LoadEnabled());

    private void Disable(Plugin? plugin, Dictionary<string, string?> enabled)
    {
        if (plugin?.Name is { } name && (plugin.Enabled || enabled.ContainsKey(name)))
        {
            enabled.Remove(name);
            SaveEnabled(enabled);
            plugin.SetEnabled(false);
            Event.EventRuntime.Fire(new Event.Events.PluginWasDisabled(plugin));
        }
    }

    public void Delete(string name)
    {
        Plugin? plugin = All().GetValueOrDefault(name);
        if (plugin is null)
        {
            return;
        }

        Disable(plugin, LoadEnabled());
        if (plugin.Path is { } path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // 尽力删除
            }
        }

        Event.EventRuntime.Fire(new Event.Events.PluginWasDeleted(plugin));
    }

    /// <summary><c>getUnsatisfied($plugin)</c>。</summary>
    public Dictionary<string, (string? Version, string Constraint)> GetUnsatisfied(Plugin plugin) =>
        GetUnsatisfied(plugin, LoadEnabled());

    private Dictionary<string, (string? Version, string Constraint)> GetUnsatisfied(Plugin plugin,
        Dictionary<string, string?> enabled)
    {
        var result = new Dictionary<string, (string?, string)>(StringComparer.Ordinal);
        if (plugin.Attr("require", null) is not PhpArray requirements)
        {
            return result;
        }

        foreach (KeyValuePair<object, object?> entry in requirements)
        {
            string name = PhpString.Cast(entry.Key);
            string constraint = PhpString.Cast(entry.Value);
            if (name == "blessing-skin-server")
            {
                if (!Semver.Satisfies(settings.Version, constraint))
                {
                    result[name] = (settings.Version, constraint);
                }
            }
            else if (name == "php")
            {
                string phpVersion = Environment.GetEnvironmentVariable("NEOSKIN_PHP_VERSION")
                                    ?? "8.5.0";
                if (!Semver.Satisfies(phpVersion, constraint))
                {
                    result[name] = (phpVersion, constraint);
                }
            }
            else if (!enabled.TryGetValue(name, out string? version))
            {
                result[name] = (null, constraint);
            }
            else if (!Semver.Satisfies(version, constraint))
            {
                result[name] = (version, constraint);
            }
        }

        return result;
    }

    /// <summary><c>getConflicts($plugin)</c>。</summary>
    public Dictionary<string, (string? Version, string Constraint)> GetConflicts(Plugin plugin) =>
        GetConflicts(plugin, LoadEnabled());

    private Dictionary<string, (string? Version, string Constraint)> GetConflicts(Plugin plugin,
        Dictionary<string, string?> enabled)
    {
        var result = new Dictionary<string, (string?, string)>(StringComparer.Ordinal);
        if (plugin.Attr("enchants.conflicts", null) is not PhpArray conflicts)
        {
            return result;
        }

        foreach (KeyValuePair<object, object?> entry in conflicts)
        {
            string name = PhpString.Cast(entry.Key);
            string constraint = PhpString.Cast(entry.Value);
            if (enabled.TryGetValue(name, out string? version) && version is not null
                && Semver.Satisfies(version, constraint))
            {
                result[name] = (version, constraint);
            }
        }

        return result;
    }

    /// <summary><c>formatUnresolved($unsatisfied, $conflicts)</c>。</summary>
    public List<string> FormatUnresolved(
        Dictionary<string, (string? Version, string Constraint)> unsatisfied,
        Dictionary<string, (string? Version, string Constraint)> conflicts)
    {
        var lines = new List<string>();
        foreach ((string name, (string? version, string constraint)) in unsatisfied)
        {
            string title = name switch
            {
                "blessing-skin-server" => "Blessing Skin Server",
                "php" => "PHP",
                _ => Get(name)?.Title ?? name,
            };
            lines.Add(version is not null
                ? title + " " + constraint
                : title);
        }

        foreach (string name in conflicts.Keys)
        {
            lines.Add(Get(name)?.Title ?? name);
        }

        return lines;
    }
}
