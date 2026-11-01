# NeoSkin 原生插件示例

最小可用的 NativeAOT 原生插件（`NativeLib=Shared`），演示方案 A 的 C ABI：

- 导出 `NeoSkinPluginGetApi`；
- `filter:user_badges` 返回 `[{"text":"SAMPLE","color":"success"}]`；
- `filter:head_links` 返回空数组；
- `event:*` 直接返回成功。

## 构建

```sh
dotnet publish samples/NeoSkin.Plugin.Sample -c Release -r win-x64 \
  -o samples/NeoSkin.Plugin.Sample/publish
```

Linux/macOS 把 `-r` 换成对应 RID（`linux-x64` / `osx-arm64`）。

## 安装为站点插件

把产物放到站点插件目录：

```
plugins/sample/
  package.json
  NeoSkin.Plugin.Sample.dll
```

`package.json` 见同目录示例（`native` 字段指向原生库）。在管理端启用后重载插件运行时
（启用/禁用操作会触发 `PluginRuntime.Reload()`）。

> 当前最小切片只提供加载、生命周期与 `filter/event` 调用入口；具体页面 Hook
> 的接线（如 `user_badges` 进入用户面板）尚未在业务代码中调用。
