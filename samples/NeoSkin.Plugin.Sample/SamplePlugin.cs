using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NeoSkin.PluginAbi;
using Abi = NeoSkin.PluginAbi.PluginAbi;

namespace NeoSkin.Plugin.Sample;

/// <summary>
/// 最小原生插件示例（NativeAOT <c>NativeLib=Shared</c>）。
///
/// <para>演示两个 Hook：
/// <c>filter:user_badges</c> 返回一个徽章；
/// <c>filter:head_links</c> 返回空数组。事件调用直接返回成功。</para>
/// </summary>
internal static unsafe class SamplePlugin
{
    private static byte* _name;
    private static int _nameLength;
    private static byte* _version;
    private static int _versionLength;
    private static byte* _description;
    private static int _descriptionLength;

    [UnmanagedCallersOnly(EntryPoint = "NeoSkinPluginGetApi",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApi(uint hostAbiVersion, NeoSkinHostApi* host,
        NeoSkinPluginApi* api)
    {
        if (api is null || hostAbiVersion != Abi.Version)
        {
            return Abi.ErrorInvalidAbi;
        }

        if (_name is null)
        {
            _nameLength = Allocate("NeoSkin.Plugin.Sample", out _name);
            _versionLength = Allocate("1.0.0", out _version);
            _descriptionLength = Allocate("NeoSkin native plugin sample", out _description);
        }

        api->AbiVersion = Abi.Version;
        api->Name = _name;
        api->NameLength = _nameLength;
        api->Version = _version;
        api->VersionLength = _versionLength;
        api->Description = _description;
        api->DescriptionLength = _descriptionLength;
        api->Invoke = &Invoke;
        api->Free = &Free;
        api->Shutdown = &Shutdown;
        return Abi.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Invoke(byte* topic, int topicLength, byte* json, int jsonLength,
        byte** result, int* resultLength)
    {
        *result = null;
        *resultLength = 0;

        string name = Read(topic, topicLength);
        string output = name switch
        {
            "filter:user_badges" => "[{\"text\":\"SAMPLE\",\"color\":\"success\"}]",
            "filter:head_links" => "[{\"href\":\"/app/plugin-sample.css\"}]",
            "filter:scripts" => "[{\"src\":\"/app/plugin-sample.js\"}]",
            "filter:can_login" => Read(json, jsonLength).Contains("blocked@",
                StringComparison.Ordinal)
                ? "\"blocked by sample plugin\""
                : "\"\"",
            "filter:can_register" => Read(json, jsonLength).Contains("blocked@",
                StringComparison.Ordinal)
                ? "\"blocked by sample plugin\""
                : "\"\"",
            "filter:user_menu" or "filter:side_menu" or "filter:content_policy"
                or "filter:verify_password" or "filter:client_ip"
                or "filter:user_password" or "filter:auth_page_rows:login"
                or "filter:auth_page_rows:register" => Read(json, jsonLength),
            _ => "",
        };

        if (output.Length == 0)
        {
            return name.StartsWith("event:", StringComparison.Ordinal)
                ? Abi.Ok
                : Abi.ErrorUnknownTopic;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(output);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        *result = buffer;
        *resultLength = bytes.Length;
        return Abi.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Free(void* memory) => NativeMemory.Free(memory);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Shutdown()
    {
        // 示例插件没有额外资源。
    }

    private static int Allocate(string value, out byte* buffer)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        return bytes.Length;
    }

    private static string Read(byte* value, int length) =>
        value is null || length <= 0
            ? ""
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, length));
}
