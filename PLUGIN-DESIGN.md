# NeoSkin.NET — AOT 运行时插件系统设计

> 状态：**方案 A 最小切片已实现**。
> 已落地：`NeoSkin.Plugin.Abstractions`（ABI v1）、`NativePluginHost`（加载/ABI 校验/
> `filter:*`、`event:*` 调用/卸载）、`PluginRuntime`（扫描已启用且声明 `native` 的插件、
> 启动加载、启用/禁用后重载、失败派发 `PluginBootFailed`），以及示例插件
> `samples/NeoSkin.Plugin.Sample`。
> **已接线**：`head_links`、`scripts`、`user_menu`、`user_avatar`、`user_badges`、`side_menu`、`content_policy`，以及插件语言包合并。
> **已接线**：认证类过滤器（`can_login`/`verify_password`/`client_ip`/`can_register`/`user_password`/`auth_page_rows:*`）。
> 现有 PHP 插件仍需按新契约重写。

> 原始设计（方案 A / 方案 B 对比）保留如下，供后续扩展参考。

---

## 1. 约束

Native AOT 产物没有 JIT，也没有 `Assembly.Load` / 反射式插件发现，因此：

- 插件必须是**原生库**（.NET AOT 的 `NativeLib=Shared`，或 C/C++/Rust 等）；
- 主程序与插件之间只能通过**原生 ABI**（函数指针 / COM 虚表）通信；
- 现有 PHP 插件（`bootstrap.php`、ServiceProvider）无法直接运行，需要按新契约重写；
- 进程内加载意味着插件与主程序同权限、同生命周期，**不做沙箱**。

## 2. 方案 A：跨平台原生 C ABI（推荐）

Windows / Linux / macOS 都支持 `NativeLibrary.Load` + `UnmanagedCallersOnly` 导出，
因此优先推荐这一方案。

### 2.1 ABI 形态

插件导出两个入口（名称固定，ABI 版本写在结构体里）：

```c
// 由插件导出
int NeoSkinPluginGetApi(uint32_t hostAbiVersion,
                        const NeoSkinHostApi* host,
                        NeoSkinPluginApi* outPlugin);
```

- `NeoSkinHostApi`：宿主提供给插件的**函数指针表**（只读）；
- `NeoSkinPluginApi`：插件回填的**函数指针表**（名称/版本/回调）；
- 所有字符串一律 UTF-8 + 长度（`byte*` + `int32`），由调用方负责生命周期；
- 结构体 `[StructLayout(LayoutKind.Sequential)]`，只用 blittable 字段；
- 返回值 `int`：0 成功，非 0 为错误码（错误文本通过 host 的 `log` 输出）。

C# 侧（宿主）：

```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NeoSkinHostApi
{
    public uint AbiVersion;
    public delegate* unmanaged[Cdecl]<byte*, int, void> Log;
    public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*> GetOption; // 返回需由 host 释放
    public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, int> SetOption;
    public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, int, byte*> InvokeHook; // name, jsonArgs
    public void* HostContext;
}
```

> 也可以把 `InvokeHook` 换成「插件注册回调」：`RegisterHook(name, fn)`，
> 由宿主在触发时回调插件；两种都可以，前者更简单（插件无状态回调），
> 后者少一次字符串往返。

### 2.2 插件接口

`NeoSkinPluginApi` 至少包含：

| 字段 | 说明 |
| --- | --- |
| `AbiVersion` | 插件支持的 ABI 版本 |
| `Name` / `Version` / `Description` | UTF-8 字符串（用于清单校验与日志） |
| `Invoke` | `int Invoke(byte* hookOrEvent, int len, byte* jsonArgs, int argsLen, byte** jsonResult, int* resultLen)` |
| `Shutdown` | 卸载前调用，释放插件资源 |

`Invoke` 的 `hookOrEvent` 覆盖现有契约：

- 7 个 Hook（`filter` 类）→ `invoke("filter:<name>", args) → result`；
- 24 个 Event → `invoke("event:<ClassName>", payload)`，返回值忽略；
- 参数/返回值统一用 JSON（`PhpJson` 兼容的字节语义），插件内部可用
  `System.Text.Json` 源生成器解析。

### 2.3 宿主加载流程

```csharp
nint handle = NativeLibrary.Load(path);
nint entry = NativeLibrary.GetExport(handle, "NeoSkinPluginGetApi");
var getApi = (delegate* unmanaged[Cdecl]<uint, NeoSkinHostApi*, NeoSkinPluginApi*, int>)entry;
// 调用 getApi(ABI, &hostApi, &pluginApi)，校验 AbiVersion 与清单 name/version
```

- 宿主保留 `handle` 直到进程结束或插件被禁用；
- 禁用时先调用 `Shutdown`，再 `NativeLibrary.Free(handle)`；
  **卸载后不能再触发该插件的回调**（需要在事件总线里移除回调）；
- 加载失败 → 派发 `PluginBootFailed`（沿用现有事件）。

### 2.4 优点 / 缺点

- ✅ 跨平台；ABI 简单、可测试；插件可用任意 native 语言编写；
- ✅ 字符串/JSON 避免复杂封送规则；
- ⚠️ 需要自己管理内存与生命周期；没有 COM 的接口继承/属性便利；
- ⚠️ 每次 Hook 调用有一次 JSON 序列化开销（可后续加二进制协议）。

## 3. 方案 B：Windows COM（参考 cnblogs 文章）

文章方案：`[GeneratedComInterface]` + `[GeneratedComClass]` +
`StrategyBasedComWrappers`；插件 AOT 编译为 DLL，导出 `DllGetObject`，
宿主用 `LoadLibrary`/`GetProcAddress` 取得 CCW 指针，再转回接口。

### 3.1 形态

```csharp
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("...")]
public partial interface INeoSkinPlugin
{
    void GetInfo(out string name, out string version);
    [return: MarshalAs(UnmanagedType.Bool)]
    bool Invoke(string hookOrEvent, string jsonArgs, out string jsonResult);
    void Shutdown();
}
```

插件侧：

```csharp
[GeneratedComClass]
public partial class MyPlugin : INeoSkinPlugin { ... }

[UnmanagedCallersOnly(EntryPoint = "DllGetObject")]
private static unsafe int DllGetObject(void** ppv)
{
    *ppv = (void*)new StrategyBasedComWrappers()
        .GetOrCreateComInterfaceForObject(new MyPlugin(), CreateComInterfaceFlags.None);
    return 0;
}
```

### 3.2 限制

- **仅 Windows**（COM 是 Windows 专有）；Linux 部署时插件系统不可用；
- 接口成员只能是实例方法；`bool`/数组/`string` 需要显式封送特性；
- 异步需要 `TaskCompletionSource` 包装（文章中给了示例）；
- 需要为每个插件维护 `LoadLibrary` 句柄，卸载同样要求先 `Shutdown`。

### 3.3 何时选它

- 部署环境确定是 Windows；
- 希望用接口继承/抽象类简化插件编写，且能接受 COM 封送约束。

## 4. 与现有契约的映射

| 现有 | 运行时插件形态 |
| --- | --- |
| `Hook`（7 方法：`filter`） | `Invoke("filter:<name>", json)` → JSON 结果 |
| 24 个 `Event` | `Invoke("event:<Name>", json)`（无返回值） |
| `PluginManager`（13 方法） | 宿主内部保持不变；插件只看到 `HostApi` 的日志/选项/Hook 入口 |
| `Option` / 数据库 | 建议只经 host 的 `GetOption/SetOption`（避免插件直接碰 schema）；如需查询，另加 `Query` 函数并限只读 |
| 插件语言包 | 插件目录 `lang/<locale>/front-end.yml` 由宿主合并（`JavaScriptBundle`） |

## 5. 实施步骤（方案 A）

1. 新建 `NeoSkin.Plugin.Abstractions`（只含 ABI 结构体/常量/接口文档，无依赖）；
2. 宿主 `NativePluginHost`：`Load/Unload`、`GetApi` 校验、回调表注册；
3. 接入 `PluginManager.All()` 的启用流程：启用时加载，禁用时 `Shutdown` + `Free`；
4. 事件总线桥接：`EventRuntime` 与 Hook 过滤调用转发到插件 `Invoke`；
5. 失败处理：加载/调用异常 → `PluginBootFailed`，并把插件标记为不可用（不改 `plugins_enabled`）；
6. 测试：ABI 结构体布局测试、NullHost 行为、错误码映射；
   端到端测试需要一个原生测试插件（可用 C 编译一个最小 `.dll/.so`，在 CI 中构建）。

## 6. 已实现的最小切片（ABI v1）

| 组件 | 位置 |
| --- | --- |
| ABI 结构体/常量 | `src/NeoSkin.Plugin.Abstractions/PluginAbi.cs`（`NeoSkin.PluginAbi`） |
| 宿主加载/调用/卸载 | `src/NeoSkin.Web/PluginHost/NativePluginHost.cs` |
| 插件发现与生命周期 | `src/NeoSkin.Web/PluginHost/PluginRuntime.cs` |
| 组合根装配 | `NeoSkinApp.cs`（启动加载、`ApplicationStopping` 卸载） |
| 启用/禁用重载 | `PluginController.Manage` 调用 `PluginRuntime.Current?.Reload()` |
| 示例插件 | `samples/NeoSkin.Plugin.Sample`（`NativeLib=Shared`） |
| 测试 | `tests/.../Plugin/NativePluginHostTest.cs`（ABI、加载、filter、清单校验、运行时发现） |

ABI v1 在设计中补充了 `Free` 回调：插件返回的结果由插件分配并释放，宿主只复制字节，
避免跨模块分配器不匹配。

## 7. 结论

- **推荐方案 A（跨平台 C ABI）**：与 NeoSkin 的 Linux 部署目标一致，ABI 可控；
- 方案 B（COM）保留为 Windows-only 备选；
- 当前代码**不实现运行时加载**，插件管理仅到清单/安装层；
  后续落地时按第 5 节分阶段实现，并把差异写入 `PORT-NOTES.md`。
