# NeoSkin.NET

**Blessing Skin Server 6.0.11_patch 的 .NET 10 / Native AOT 移植**。

- **参考实现（权威）**：[`blessing-skin-server-Laravel13`](../blessing-skin-server-Laravel13)（PHP 6.0.11_patch）
- **已验证的参考移植**：[`NeoSkinSpringBoot`](../NeoSkinSpringBoot)（Java 21，174 路由 / 1085 tests 全绿）
- **一致性语料**：[`conformance/data`](../conformance/data)（PHP 侧生成，四语言共用）

## 当前状态

| 项 | 数值 |
| --- | --- |
| 路由接线 | **174 / 174** |
| 测试 | **446 passed / 1 skipped**（矩阵测试在无 DB 环境跳过） |
| Native AOT 发布 | `neoskin.exe` 约 **43.1 MB**（win-x64，含 3 个数据库驱动） |
| 前端 | 复用源项目 `public/app`（部署时拷到 `wwwroot/`）；`/lang/<locale>.js` 后端生成 |

已实现：认证全流程（登录/注册/找回/重置/验证/验证码/绑定）、OAuth2（Passport 兼容）、
用户中心、玩家/衣柜/皮肤库（含上传与 PNG 消毒）、Minecraft 纹理契约、举报、
站内信、管理端（用户/玩家/衣柜/通知公告/翻译/图表/选项/插件/市场/更新/状态）、
安装向导、Ignition、首页与各页面布局，以及完整的 Minecraft 3D 渲染
（SkinRenderer：2D/3D 头像、皮肤正反预览、披风预览）。**86 个模板文件
（覆盖源 104 个 Twig）已全部接入**，不再有手写业务页面。

**未实现/偏差**（详见 [STATUS.md](STATUS.md)）：

- 皮肤/头像渲染的光栅化与重采样为自研实现，与 GD/Imagick 不逐字节一致
  （尺寸一致，mean per-channel diff ≈ 2–3/255）；
- 原生插件 ABI 已实现加载/生命周期/filter/event（[PLUGIN-DESIGN.md](PLUGIN-DESIGN.md)）；
  渲染层与认证类 Hook 均已接线，插件语言包已合并；PHP 插件需按 ABI 重写；
- 更新安装执行 .NET 迁移（含旧 PHP 公告/站内信回填）。

## 构建与验证

```sh
dotnet build NeoSkin.NET.slnx
dotnet test                       # 447 tests（无 DB 时矩阵测试 Skip）
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
./publish/win-x64/neoskin.exe
```

数据库矩阵（MySQL / MariaDB / PostgreSQL / SQL Server 协议）：
本机用 `..\tools\scripts\db.cmd start all`，步骤见 [DB-MATRIX.md](DB-MATRIX.md)；
跨语言通用坑位见工作区根目录的 `../DATABASE-MATRIX-PLAYBOOK.md`（共享手册，不随本仓库提交）。

健康检查：`GET /__neoskin/health` → `ok`。

## 部署

### 1. 发布

```sh
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
# Linux: -r linux-x64
```

发布目录是自包含的 Native AOT 二进制（`neoskin` / `neoskin.exe`），
目标机不需要安装 .NET 运行时。

### 2. 前端静态资源

仓库**不提交**前端构建产物。部署时把 PHP 源项目的 `public/app/` 整体拷贝到
发布目录的 `wwwroot/app/`：

```powershell
Copy-Item blessing-skin-server-Laravel13\public\app\* publish\win-x64\wwwroot\app\ -Recurse
```

`/lang/<locale>.js` 由后端按需生成，不需要额外文件。

### 3. 环境变量

| 变量 | 说明 |
| --- | --- |
| `APP_KEY` | 必填，`base64:` + 32 字节密钥（用于 cookie/CSRF） |
| `ASPNETCORE_URLS` | 监听地址，如 `http://0.0.0.0:5000` |
| `DB_CONNECTION` | `sqlite` / `mysql` / `mariadb` / `pgsql` / `sqlsrv` |
| `DB_HOST` / `DB_PORT` / `DB_DATABASE` / `DB_USERNAME` / `DB_PASSWORD` | 数据库连接 |
| `DATABASE_URL` | 可选，优先级高于 `DB_*` |
| `NEOSKIN_STORAGE_ROOT` | 存储根目录（默认 `./storage`，需可写） |
| `APP_ENV` | `production` / `local` |
| `APP_DEBUG` | `true` / `false` |

SQLite 快速启动示例：

```powershell
$env:APP_KEY='base64:...'
$env:DB_CONNECTION='sqlite'
$env:DB_DATABASE='D:\neoskin\data\db.sqlite'
$env:NEOSKIN_STORAGE_ROOT='D:\neoskin\storage'
$env:ASPNETCORE_URLS='http://0.0.0.0:5000'
.\neoskin.exe
```

### 4. 首次安装 / 升级

- 新站点：启动后访问 `/setup`，按向导写数据库、创建管理员。
- 已有 PHP 站点：把数据库指向同一库，启动时会执行 .NET 侧 `SchemaMigrator`
  （增量迁移、旧公告/站内信回填），不会覆盖已有选项。
- 存储目录需包含 `storage/textures/`（并保持 PHP 版本的纹理文件可写）。

### 5. 反向代理

把 `ASPNETCORE_URLS` 反代到 nginx/Caddy 时，保留 `Host`、
`X-Forwarded-Proto` / `X-Forwarded-For`，并把上传大小限制调到不小于
站点选项里的 `max_upload_file_size`。健康检查用 `GET /__neoskin/health`。


## 技术栈（AOT 优先）

- ASP.NET Core minimal API + 自研路由表（按 `routes.json` 注册 174 条）
- 手写 ADO.NET + 5 种数据库方言（SQLite / MySQL/MariaDB / PostgreSQL / SQL Server）
- BCrypt.Net-Next + Konscious Argon2（兼容 `$2y$` 与历史 argon2 哈希）
- NodaTime（IANA tzdata）、YamlDotNet（语言包）
- ImageSharp（PNG 解码/编码）+ 自研 GD 风格渲染器（`Render/*`：多边形光栅化、等距投影）
- 自研 Passport 兼容 OAuth2（RS256 JWT + `laravel_token`）

**不用** EF Core / 运行时 Razor 编译 / OpenIddict / `AssemblyLoadContext` 插件加载
（均与 Native AOT 冲突）。

## 目录

- `src/NeoSkin.Web` — 应用（`PublishAot=true`，`AssemblyName=neoskin`）
- `tests/NeoSkin.Tests` — 语料 + 单元 + HTTP 契约测试
- `MIGRATION.md` — 迁移计划与阶段状态
- `PORT-NOTES.md` — 决策、偏差与踩坑
- `STATUS.md` — 实现盘点与已知偏差
- `PLUGIN-DESIGN.md` — AOT 运行时插件方案（设计）
