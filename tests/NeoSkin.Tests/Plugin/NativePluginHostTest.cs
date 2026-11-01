using NeoSkin.PluginAbi;
using NeoSkin.Web.Config;
using NeoSkin.Web.Db;
using NeoSkin.Web.Paths;
using NeoSkin.Web.Service;
using NeoSkin.Web.PluginHost;
using NeoSkin.Web.Support;
using NeoSkin.Web.View;

namespace NeoSkin.Tests.PluginHost;

/// <summary>
/// 原生插件 ABI 与宿主加载测试。
///
/// <para>示例插件需先用
/// <c>dotnet publish samples/NeoSkin.Plugin.Sample -c Release -r win-x64 -o samples/NeoSkin.Plugin.Sample/publish</c>
/// 生成；缺失时跳过（CI 可加一步构建）。</para>
/// </summary>
public sealed class NativePluginHostTest
{
    private static string SamplePluginPath
    {
        get
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));
            return Path.Combine(root, "samples", "NeoSkin.Plugin.Sample", "publish",
                "NeoSkin.Plugin.Sample.dll");
        }
    }

    [Fact]
    public void AbiStructsAreBlittable()
    {
        unsafe
        {
            Assert.True(sizeof(NeoSkinHostApi) >= sizeof(uint) + (IntPtr.Size * 2));
            Assert.True(sizeof(NeoSkinPluginApi) >= sizeof(uint) + (IntPtr.Size * 9));
            Assert.Equal(1u, PluginAbi.PluginAbi.Version);
        }
    }

    [Fact]
    public void ManifestReadsNativeField()
    {
        var plugin = new Plugin(null,
            new PhpArray().With("name", "sample").With("native", "sample.dll"));

        Assert.Equal("sample.dll", plugin.NativeLibrary);
        Assert.Null(new Plugin(null, new PhpArray().With("name", "x")).NativeLibrary);
    }

    [Fact]
    public void LoadsSamplePluginAndInvokesFilter()
    {
        if (!File.Exists(SamplePluginPath))
        {
            return; // 示例插件未构建
        }

        var logs = new List<string>();
        NativePluginHost.LogSink = logs.Add;
        using NativePluginHost host = NativePluginHost.Load(SamplePluginPath,
            "NeoSkin.Plugin.Sample", "1.0.0");

        Assert.Equal("NeoSkin.Plugin.Sample", host.Name);
        Assert.Equal("1.0.0", host.Version);
        Assert.Equal(PluginAbi.PluginAbi.Version, host.AbiVersion);

        string? badges = host.Invoke("filter:user_badges", "[]");
        Assert.NotNull(badges);
        Assert.Contains("SAMPLE", badges);

        Assert.Null(host.Invoke("event:anything", "{}"));
    }

    [Fact]
    public void UnknownTopicReturnsError()
    {
        if (!File.Exists(SamplePluginPath))
        {
            return;
        }

        using NativePluginHost host = NativePluginHost.Load(SamplePluginPath);
        Assert.Throws<PluginInvokeException>(() => host.Invoke("filter:nope", "{}"));
    }

    [Fact]
    public void RuntimeDiscoversAndLoadsEnabledNativePlugin()
    {
        if (!File.Exists(SamplePluginPath))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(),
            "neoskin-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pluginsDir = Path.Combine(root, "plugins");
            string pluginDir = Path.Combine(pluginsDir, "sample");
            Directory.CreateDirectory(pluginDir);
            File.Copy(SamplePluginPath, Path.Combine(pluginDir, "sample.dll"));
            File.WriteAllText(Path.Combine(pluginDir, "package.json"),
                "{\"name\":\"NeoSkin.Plugin.Sample\",\"version\":\"1.0.0\","
                + "\"native\":\"sample.dll\"}");

            var settings = new NeoSkinSettings
            {
                Storage = { Root = Path.Combine(root, "storage") },
            };
            settings.Database.Connection = "sqlite";
            settings.Database.Name = Path.Combine(root, "db.sqlite");
            settings.Plugins.Directory = pluginsDir;
            Directory.CreateDirectory(settings.Storage.Root);

            var db = new Database(settings);
            var schema = new SchemaInstaller(db.Open, settings);
            schema.Install();
            var option = new Option(db, schema, new AppPaths(root, settings));
            var enabled = new PhpArray();
            enabled.Put(0, new PhpArray()
                .With("name", "NeoSkin.Plugin.Sample")
                .With("version", "1.0.0"));
            option.Set("plugins_enabled", PhpJson.Encode(enabled, 0));

            var runtime = new PluginRuntime(new PluginManager(option, settings,
                new AppPaths(root, settings)));
            runtime.Start();

            Assert.True(runtime.HasPlugins);
            Assert.Single(runtime.Hosts);
            Assert.Contains("SAMPLE", runtime.Filter("user_badges", "[]"));

            var badges = SharedLayout.FilterUserBadges(new List<object?>(), runtime);
            Assert.Single(badges);
            Assert.Equal("SAMPLE", ((PhpArray)badges[0]!).Get("text")?.ToString());

            runtime.Dispose();
            Assert.False(runtime.HasPlugins);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (IOException)
            {
                // SQLite 句柄释放延迟；临时目录可留给系统清理
            }
        }
    }

    [Fact]
    public void ManifestMismatchIsRejected()
    {
        if (!File.Exists(SamplePluginPath))
        {
            return;
        }

        Assert.Throws<PluginLoadException>(() =>
            NativePluginHost.Load(SamplePluginPath, "other"));
    }
}
