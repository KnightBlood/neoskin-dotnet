# NeoSkin.NET — 验收清单

> 最后更新：2026-11-06
> 基准：`blessing-skin-server-Laravel13`（6.0.11_patch，只读）

## 1. 路由

| 项 | 结果 |
| --- | --- |
| PHP 源路由 | 174 条 / 145 唯一路径 |
| .NET 注册 | **174 / 174**（含源码漏记的 `POST skinlib`） |
| 未接通 | 0 |

## 2. 数据与兼容性

| 项 | 结果 |
| --- | --- |
| 表 | 17 / 17（16 业务 + `migrations`） |
| 字段/主键 | `uid` / `pid` / `tid`；方言映射 |
| 密码 | bcrypt `$2y$` / `$2a$` |
| 纹理 | SHA-256 hash |
| 时间 | `Y-m-d H:i:s`，Asia/Shanghai |
| 数据迁移 | import_options / max_texture_width / create_communications（含旧公告/站内信回填）/ hash_oauth_client_secrets |

## 3. 认证与授权

- Session、CSRF、记住我、验证码、找回/重置/邮箱验证/绑定
- `auth:web`、`guest`、`role:*`、`verified`、`auth:oauth`、`scope:*`
- 插件 Filter：`client_ip`、`can_login`、`can_register`、`verify_password`、`user_password`

## 4. OAuth2（Passport 兼容）

- scopes / clients / tokens / personal access tokens
- `authorize` / `approve` / `deny`
- `POST /oauth/token`：`authorization_code`、`refresh_token`、`client_credentials`
- Bearer + scope 鉴权；refresh token 一次性

## 5. 视图 / 前端契约

- `window.blessing`、`#blessing-extra`、`#logout-button`、`data-mark="nickname"`
- `.locale`、`<main></main>`、`data-*` 钩子
- `/lang/<locale>.js` 由后端生成
- 86 个模板；静态资源 `wwwroot/app`
- 浏览器 E2E：首页/登录/用户中心/资料/衣柜/皮肤库/上传/管理端/用户管理/更新页

## 6. 插件

- 原生 C ABI（`NativeLibrary.Load`）+ 清单层
- 加载/生命周期/filter/event、插件语言包合并
- PHP 插件不可执行（D-001），需按 ABI 重写
- 设计细节见 `PORT-NOTES.md` §61

## 7. 更新

- 检测更新源、ZIP 下载/上传/暂存/安装
- 请求期自动 `update`（`RedirectToSetup` → `UpdateRunner`）
- CLI：`--neoskin.command=update`

## 8. 测试与 CI

| 项 | 结果 |
| --- | --- |
| `dotnet test` | **447 passed / 1 skipped** |
| 数据库矩阵 | 5 库（SQL Server 本机 WiltonDB + CI 真库） |
| Native AOT | `win-x64` publish 成功，CLI smoke 通过 |
| 浏览器 E2E | `BrowserE2ETest` 通过 |
| CI | build/test、五库矩阵、browser-e2e、AOT publish |

## 9. 已知限制

1. AOT 下 `Microsoft.Data.SqlClient` 有上游 IL2104/IL3053 警告。
2. 渲染字节与 PHP GD/Imagick 不逐字节一致。
3. PHP 插件不可执行；只支持原生 C ABI。
4. 更新包无签名/回滚。
5. 部分后台/业务提示未完全本地化。
