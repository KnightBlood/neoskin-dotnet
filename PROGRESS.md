# NeoSkin.NET — 进度快照

> 最后更新：2026-11-06。测试：**447 passed / 1 skipped**（数据库矩阵测试无 DB 时 Skip）；Native AOT 发布：`neoskin.exe` 约 43.1 MB（2026-11-06 实测 `win-x64` publish + CLI install/update/options:cache 通过；剩余 IL2104/IL3053 警告全部来自 `Microsoft.Data.SqlClient` 上游）
>（含 MySqlConnector / Npgsql / Microsoft.Data.SqlClient）。
> 路由覆盖：**174 / 174**（全部路由均已接线）。

---

## 1. 已实现（按模块）

| 模块 | 内容 | 路由数 |
| --- | --- | --- |
| 认证 | 登录/登出/注册/忘记密码/重置/邮箱验证/验证码/绑定邮箱（JSON + 验证码图片） | 13 |
| 认证中间件 | `auth:web` / `auth:oauth` / `guest` / `role:*` / `verified` / `scope:*` | — |
| OAuth2（Passport） | `/oauth/token`（三种 grant）、`authorize/approve/deny`、`refresh`、clients、tokens、personal tokens、scopes | 15 |
| 用户中心 | `api/user`、`score-info`、`sign`、`dark-mode`、`profile`（昵称/密码/邮箱/删除）、`avatar`、`email-verification` | 7 |
| 玩家 | 用户侧 `list/add/delete/rename/setTexture/clearTexture`；Minecraft `/{player}.json` | 7 |
| 纹理 | `textures/{hash}`、`raw/{tid}`、`avatar/*`（2D + 3D + 回退）、皮肤/披风 `preview/*` | 10 |
| 衣柜 | 分页/ids/add/rename/remove（用户侧 + 管理端） | 8 |
| 皮肤库 | 列表/详情/删除/隐私/改名/类型/上传（PNG 消毒） | 9 |
| 举报 | 提交/管理端分页/审核（拒绝/删除/封禁） | 3 |
| 管理端 | 用户管理（8）、玩家管理（5）、通知公告（5）、翻译（4）、图表（1）、插件（6）、市场（2）、选项（4） | 37 |
| 站内信 | 发送/未读/标记已读 | 3 |
| 安装向导 | `/setup/database`、`/setup/finish` + 安装锁中间件 | 2 |
| 首页 | `GET /` 由 `home.html` 模板渲染、`GET /api`、`/lang/<locale>.js` | 2 |
| Ignition | health-check / execute-solution / update-config | 3 |
| 其它 | `.well-known/change-password` 重定向 | 1 |

## 2. 剩余未实现（0 条）

| 分组 | 条数 | 原因 |
| --- | --- | --- |
| `ViewController` 页面 | 0 | ✅ 已实现（通用布局 + 前端挂载容器） |
| 认证页 HTML | 0 | ✅ 已实现（手写 `<main>` 外壳 + 服务端文案） |
| 其它 HTML 页 | 0 | ✅ 已实现（手写布局/表单） |
| `UpdateController` | 0 | ✅ 已实现（下载/校验/暂存/安装） |

> 说明：所有路由均有处理器；仅 `RouteDispatcher` 对无法解析的 action 返回 501。

## 3. 主要已知偏差

1. **模板/HTML**：通用 Thymeleaf 子集引擎已落地（`View/TemplateEngine.cs`），
   86 个模板文件（覆盖源 104 个 Twig：表单由 `OptionForm` 生成、`assets/*` 片段内联、
   错误页合并）**全部接入**（业务页面、管理端、安装向导、错误页、OAuth 授权页），
   不再有手写业务页面。DOM 以源模板为准；与 Thymeleaf 的差异仅在
   自研引擎的空白/属性输出细节。
2. **图像渲染**：`SkinRenderer` 已移植（等距投影、多边形光栅化、图层合成、透明背景修复、
   新皮肤图层叠加、Alex 模型、头部/四肢旋转）。2D/3D 头像、皮肤正反预览、披风预览
   均可用；默认输出 **WebP（quality 100，`?png` 回退 PNG）**，并带
   ETag/304/Last-Modified。光栅化与抗锯齿重采样是自研实现，与 GD/Imagick 的像素结果
   **不逐字节一致**（实测 mean per-channel diff ≈ 2–3/255，边缘少数像素差异更大）；
   WebP 编码字节同样不保证与 Imagick 一致。
3. **PNG 消毒**：编码参数与 libpng/Imagick 对齐，但压缩字节不保证逐字节一致。
4. **插件**：清单层（扫描/启用/禁用/删除/上传/市场）完整；原生插件 ABI（方案 A）
   已实现加载/生命周期/`filter:*`/`event:*` 调用；
   `head_links`/`scripts`/`user_menu`/`user_avatar`/`user_badges`/`side_menu`/`content_policy`
   与认证类过滤器（`can_login`/`verify_password`/`client_ip`/`can_register`/
   `user_password`/`auth_page_rows:*`）均已接线，插件语言包已合并；PHP 插件仍需按 ABI 重写。
5. **Communications 正文**：Markdown（GFM 子集）与 HTML 白名单消毒均已移植
   （`Support/HtmlSanitizer`）；`blocks` 支持 text/heading/alert/collapse/divider。
   公告选择与源 `currentAnnouncement()` 一致（当前语言 → 空语言回退；`both` 取已发布）。
6. **SearchString**：管理端搜索语法为子集（无括号/or/not/关系列），非法时按
   `all-results` 处理。
7. **更新安装**：完整包解压后执行 .NET 侧 `SchemaMigrator`
   （import options / max_texture_width / OAuth secret 哈希 / 旧公告与站内信回填）。
8. **数据库驱动**：`sqlite` / `mysql` / `mariadb` / `pgsql` / `sqlsrv` 均已接入
   （`Db/Database.cs`），支持 `DATABASE_URL`、`DB_SOCKET`、`MYSQL_ATTR_SSL_CA`、
   `DB_ENCRYPT`、`DB_TRUST_SERVER_CERTIFICATE`。已在本机用
   `tools/scripts/db.cmd` 启动 MySQL 26.10 / MariaDB 13.0.2 / PostgreSQL 18.6 /
   WiltonDB（SQL Server 协议）完成 `DatabaseMatrixTest`（建表 + 用户/纹理 CRUD），
   并用 AOT 发布产物对四个库做了 HTTP 冒烟；CI 矩阵见 `.github/workflows/ci.yml`
   与 `DB-MATRIX.md`。`Microsoft.Data.SqlClient` 在 AOT 发布时产生
   IL2104/IL3053 裁剪警告（可成功发布并已跑通 WiltonDB）。
9. **缓存**：头像/预览使用进程内 `Service/ImageCache`（对应 Laravel `Cache::remember`
   语义，`enable_avatar_cache` / `enable_preview_cache` 分别控制 1 年 / 1 分钟 TTL）；
   非跨进程共享，重启后失效。
10. **插件语言包**：`/lang/<locale>.js` 仅主语言包，不合并插件命名空间。
11. **升级编排**：已安装站点启动时执行 `SchemaMigrator`（结构 + 4 个数据迁移，幂等）；
    未复刻 PHP `RedirectToSetup` 在 `version > option('version')` 时调用
    `Artisan::call('update')` 的请求期编排（视图/配置缓存清理需由部署流程处理）。

## 4. 验证方式

```sh
dotnet build NeoSkin.NET.slnx
dotnet test                       # 447 tests（无 DB 时矩阵测试 Skip）
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
```

数据库矩阵（可选，本机免安装）：

```powershell
..\tools\scripts\db.cmd start all
$env:DB_CONNECTION='mysql'; $env:DB_HOST='127.0.0.1'; $env:DB_PORT='3307'
$env:DB_DATABASE='blessing_skin'; $env:DB_USERNAME='blessing'; $env:DB_PASSWORD='123456'
dotnet test NeoSkin.NET.slnx --filter "FullyQualifiedName~DatabaseMatrixTest"
..\tools\scripts\db.cmd stop all
```

完整端口/账号/坑位见 [DB-MATRIX.md](DB-MATRIX.md)；工作区根目录的
`../DATABASE-MATRIX-PLAYBOOK.md` 是跨语言共享版（不随仓库提交）。

前端资源：把 `blessing-skin-server-Laravel13/public/app/` 拷到 `wwwroot/app/`
（约 18 MB），首页即可加载 SPA；`/lang/<locale>.js` 由后端即时生成。

---

## 7. 共享前端 P1（2026-11-06）

- 新增 `GET /api/bootstrap`（契约 v1）与 SPA 壳 `GET /app-next/{site,admin}`、`/app-next/{site,admin}/*`。
- 以终端中间件形式挂在 `RouteDispatcher` 之前，不改动 174 条 PHP 路由表；复用 `EncryptCookies` + `StartSession` 读取会话、CSRF 与登录态。
- 壳注入 `<meta name="csrf-token">`、`<meta name="neoskin-mode">` 与 `#neoskin-bootstrap`；静态资源一年 immutable 缓存；缺失扩展名文件 404。
- 实现：`src/NeoSkin.Web/Web/SpaFrontend.cs`、`src/NeoSkin.Web/NeoSkinApp.cs`。
- `.gitignore` 放开 `public/app-next`；构建产物已发布（含 `manifest.json`）。
- 联调验证：登录 → `/app-next/site/user` → `/app-next/admin/` 仪表盘，0 console error。

---

## 8. 共享前端 P3（2026-11-07）

- 新增用户中心契约端点，挂载在 174 条 PHP 路由表之前（`ContractApi`），不改路由表：
  - `GET /api/user/inbox?page=&per_page=`：分页 + `unread`，条目含 `preview`/`format`/`communication_id`；
  - `GET /api/user/inbox/{id}`：`{code,data}`，自动已读；
  - `GET /api/user/reports?page=&per_page=`：举报分页，含 `texture_name`。
- 实现：`src/NeoSkin.Web/Web/ContractApi.cs`、`src/NeoSkin.Web/NeoSkinApp.cs`
  （`isSpa` 分支 + `Service.Communications` 装配）、`src/NeoSkin.Web/Db/NotificationRepository.cs`
  （`CountUnread`）。
- 验证：`dotnet build NeoSkin.NET.slnx` 通过；`dotnet test` 447 passed / 1 skipped；CI 验证待推送。

### 2026-11-07（P4 增量）
- 新增 `GET /api/admin/dashboard`：用户/角色/材质数量与存储空间。
- 实现：`src/NeoSkin.Web/Web/ContractApi.cs`（管理员校验 + 统计）、`NeoSkinApp.cs`。
- 验证：`dotnet build` 通过；`dotnet test` 447 passed / 1 skipped。

- `POST /admin/i18n`：当 `Accept: application/json` 时返回 `{code:0,message}`（共享前端直接调用），否则保持原 302 跳转。

- 新增 `GET|PUT /api/admin/options/{group}`（`options|customize|score|resource`）：返回/保存站点配置、配色、积分、资源选项，供管理端 SPA 使用；布尔项统一 `"true"/"false"`。

- 新增 `GET /api/admin/communications`、`GET /api/admin/communications/{id}`（列表 / 编辑器数据）；`POST|PUT /admin/communications*` 与撤回在 `Accept: application/json` 时返回 JSON（否则保持 302）。

- 管理端 SPA 通过请求头 `X-Neoskin-Spa: 1` 标识；`POST|PUT /admin/communications*` 与撤回据此返回 JSON，其他请求保持 302（不改变 legacy 行为）。

- 新增 `GET /api/admin/update/info`：返回 `latest/current/error/can_update/package/upload_limits`，管理端更新页直接展示最新版本与可更新状态。

### 2026-11-08（bootstrap 开关 + 验证门禁确认）

- `Web/SpaFrontend.cs` 的 bootstrap options 增加 `allow_downloading_texture`、
  `require_verification`、`player_name_length_min/max`、`user_initial_score`、`return_score`、
  `sign_after_zero`、`sign_gap_time`、`reporter_score_modification`、`reporter_reward_score`。
- `verified` 门禁已由 `routes.json` 中间件（`CheckUserVerified`）覆盖
  `/user/player*`、`/user/oauth/manage`、`/texture*`、`/skinlib/upload`、`POST /skinlib`、
  `POST /skinlib/report`；`/api/*` 不门禁，本轮补充回归验证。
- 174 条 PHP 路由保持不变。
- 验证：`dotnet test` 447 passed / 1 skipped。
- 共享前端产物已同步到 `public/app-next`（site `index-pnUYroLe.js` / admin `index-8f1tgAaz.js`）。

### 2026-11-08（P6 切换验收，.NET 本机）

- `FRONTEND_MODE=next`：`GET /` → 302 `/app-next/site/`，`GET /admin` → 302 `/app-next/admin/`，
  两个 SPA 壳与 `/api/bootstrap` 均 200；站点/管理端页面 0 console error。
- `FRONTEND_MODE=legacy` 回滚：`GET /` 返回 legacy 页面（含 `#blessing-extra`），
  `/app-next/site/` 仍可访问。
- 需把 `public/app-next` 放在进程 BaseDirectory 下（`bin/.../public`）后再运行自包含 exe。
- Playwright 浏览器 E2E（`NEOSKIN_BROWSER_E2E=1`）通过。

### 2026-11-08（NeoSkin 品牌与发布）

- 默认站点名/描述/公告、安装向导与更新包提示改为 NeoSkin；版权选项与页脚署名保留
  Blessing Skin Server 上游归属（见 `PORT-NOTES.md`）。
- 新增 `.github/workflows/release.yml`：推送 `v*` tag 后在 GitHub runner 构建并发布
  `neoskin-dotnet_<version>_<rid>.zip`，包含 Native AOT 二进制、`Resources`、`public/app-next` 与部署文档。
- 共享前端产物更新为 site `index-pnUYroLe.js` / admin `index-8f1tgAaz.js`。
- 验证：`dotnet test` 447 passed / 1 skipped（品牌默认值断言已按 NeoSkin 调整）。
- GitHub Release `v6.0.11` 已发布：6 个 RID 的 `neoskin-dotnet_6.0.11_*.zip`。
