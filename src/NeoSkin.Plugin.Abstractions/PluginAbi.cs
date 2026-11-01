using System.Runtime.InteropServices;

namespace NeoSkin.PluginAbi;

/// <summary>
/// NeoSkin 原生插件 ABI（方案 A：跨平台 C ABI）。
///
/// <para>约定：</para>
/// <list type="bullet">
///   <item>字符串一律 UTF-8（<c>byte* + int32 长度</c>，不要求 NUL 结尾）；</item>
///   <item>JSON 使用 UTF-8 字节，语义与 <c>PhpJson</c> 一致；</item>
///   <item>返回码 0 成功，非 0 为错误；错误文本通过宿主 <c>Log</c> 输出；</item>
///   <item>插件返回的结果内存由插件分配，宿主复制后调用 <c>Free</c> 释放。</item>
/// </list>
///
/// <para>入口：插件必须导出
/// <c>int NeoSkinPluginGetApi(uint32_t hostAbiVersion, const NeoSkinHostApi* host, NeoSkinPluginApi* outPlugin)</c>。
/// </para>
/// </summary>
public static class PluginAbi
{
    /// <summary>当前 ABI 版本。</summary>
    public const uint Version = 1;

    public const int Ok = 0;
    public const int ErrorInvalidAbi = 1;
    public const int ErrorInvalidArgument = 2;
    public const int ErrorUnknownTopic = 3;
    public const int ErrorInternal = 4;

    public const string EntryPoint = "NeoSkinPluginGetApi";
}

/// <summary>宿主提供给插件的只读函数表。</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NeoSkinHostApi
{
    public uint AbiVersion;

    /// <summary><c>void Log(const uint8_t* message, int32_t length)</c>。</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, void> Log;

    /// <summary>宿主自用上下文（插件不得解引用）。</summary>
    public void* HostContext;
}

/// <summary>插件回填的函数表。</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NeoSkinPluginApi
{
    public uint AbiVersion;

    public byte* Name;
    public int NameLength;

    public byte* Version;
    public int VersionLength;

    public byte* Description;
    public int DescriptionLength;

    /// <summary>
    /// <c>int Invoke(const uint8_t* topic, int32_t topicLen,
    /// const uint8_t* json, int32_t jsonLen,
    /// uint8_t** result, int32_t* resultLen)</c>。
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte**, int*, int> Invoke;

    /// <summary><c>void Free(void* memory)</c>：释放 <c>Invoke</c> 返回的结果。</summary>
    public delegate* unmanaged[Cdecl]<void*, void> Free;

    /// <summary><c>void Shutdown()</c>：卸载前调用。</summary>
    public delegate* unmanaged[Cdecl]<void> Shutdown;
}
