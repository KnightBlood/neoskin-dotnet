using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NeoSkin.PluginAbi;
using Abi = NeoSkin.PluginAbi.PluginAbi;

namespace NeoSkin.Web.PluginHost;

/// <summary>
/// 原生插件宿主（方案 A：跨平台 C ABI）。
///
/// <para>负责 <c>NativeLibrary.Load</c>、调用插件导出的
/// <c>NeoSkinPluginGetApi</c>、校验 ABI/清单，并把
/// <c>filter:&lt;name&gt;</c> / <c>event:&lt;name&gt;</c> 转发给插件。</para>
/// </summary>
public sealed unsafe class NativePluginHost : IDisposable
{
    /// <summary>日志回调（宿主默认写 stderr；测试可替换）。</summary>
    public static Action<string>? LogSink { get; set; }

    private nint _handle;
    private NeoSkinPluginApi _api;
    private bool _disposed;

    private NativePluginHost(nint handle, in NeoSkinPluginApi api)
    {
        _handle = handle;
        _api = api;
        Name = ReadString(api.Name, api.NameLength);
        Version = ReadString(api.Version, api.VersionLength);
        Description = ReadString(api.Description, api.DescriptionLength);
    }

    public string Name { get; }

    public string Version { get; }

    public string Description { get; }

    public uint AbiVersion => _api.AbiVersion;

    /// <summary>加载插件；失败抛 <see cref="PluginLoadException"/>。</summary>
    public static NativePluginHost Load(string path, string? expectedName = null,
        string? expectedVersion = null)
    {
        if (!File.Exists(path))
        {
            throw new PluginLoadException("插件文件不存在：" + path);
        }

        nint handle = 0;
        try
        {
            handle = NativeLibrary.Load(path);
            nint entry = NativeLibrary.GetExport(handle, Abi.EntryPoint);
            var getApi = (delegate* unmanaged[Cdecl]<uint, NeoSkinHostApi*, NeoSkinPluginApi*, int>)entry;

            var hostApi = new NeoSkinHostApi
            {
                AbiVersion = Abi.Version,
                Log = &LogCallback,
                HostContext = null,
            };
            NeoSkinPluginApi api = default;
            int status = getApi(Abi.Version, &hostApi, &api);
            if (status != Abi.Ok)
            {
                throw new PluginLoadException("插件 GetApi 返回错误码 " + status);
            }

            if (api.AbiVersion != Abi.Version)
            {
                throw new PluginLoadException(
                    "ABI 版本不匹配：host=" + Abi.Version
                    + " plugin=" + api.AbiVersion);
            }

            if (api.Invoke is null || api.Free is null)
            {
                throw new PluginLoadException("插件 ABI 缺少 Invoke/Free");
            }

            var host = new NativePluginHost(handle, api);
            if (expectedName is not null && !string.Equals(host.Name, expectedName,
                    StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    "插件名称不匹配：manifest=" + expectedName + " plugin=" + host.Name);
            }

            if (expectedVersion is not null && !string.Equals(host.Version, expectedVersion,
                    StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    "插件版本不匹配：manifest=" + expectedVersion + " plugin=" + host.Version);
            }

            handle = 0;
            return host;
        }
        catch
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }

            throw;
        }
    }

    /// <summary>
    /// 调用插件：<c>filter:&lt;name&gt;</c> 或 <c>event:&lt;name&gt;</c>。
    /// 返回 JSON 字符串；插件返回空结果时返回 null。
    /// </summary>
    public string? Invoke(string topic, string? json)
    {
        if (_disposed)
        {
            throw new PluginLoadException("插件已卸载：" + Name);
        }

        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json ?? "");
        fixed (byte* topicPtr = topicBytes)
        fixed (byte* jsonPtr = jsonBytes)
        {
            byte* result = null;
            int length = 0;
            int status = _api.Invoke(topicPtr, topicBytes.Length, jsonPtr, jsonBytes.Length,
                &result, &length);
            if (status != Abi.Ok)
            {
                throw new PluginInvokeException(Name, topic, status);
            }

            if (result is null || length <= 0)
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(result, length));
            }
            finally
            {
                _api.Free(result);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_api.Shutdown is not null)
            {
                _api.Shutdown();
            }
        }
        finally
        {
            if (_handle != 0)
            {
                NativeLibrary.Free(_handle);
                _handle = 0;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LogCallback(byte* message, int length)
    {
        string text = length > 0
            ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(message, length))
            : "";
        (LogSink ?? Console.Error.WriteLine)(text);
    }

    private static string ReadString(byte* pointer, int length) =>
        pointer is null || length <= 0
            ? ""
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(pointer, length));
}

public sealed class PluginLoadException(string message) : Exception(message);

public sealed class PluginInvokeException(string plugin, string topic, int status)
    : Exception($"插件 {plugin} 调用 {topic} 失败：错误码 {status}");
