# NeoSkin.NET — .NET 10 / Native AOT 迁移计划

> **目标**：把 `blessing-skin-server-Laravel13`（PHP 6.0.11_patch）完整迁移到 .NET 10 + ASP.NET Core，
> **Native AOT** 发布，保持 **HTTP 接口字节级兼容** 与 **数据库结构/数据完全兼容**。
>
> **参考层级**（发生冲突时的优先级）：
>
> 1. **PHP 源项目**（`../blessing-skin-server-Laravel13`）—— 唯一权威；
> 2. `conformance/data/*.json` —— PHP 现场生成的期望值（禁止手改）；
> 3. `../NeoSkinSpringBoot`（Java 版，174 路由 / 1085 tests 全绿）—— 已验证的参考移植，
>    可复用其实现细节、视图 HTML 与契约结论。
>
> AOT 是硬约束：它只影响**运行时与库的选型**，不改变任何外部行为。

---

## 1. 硬约束（不可破坏）

1. **HTTP 接口零变更**：路由集合以 PHP 源码为准。`codegraph/routes.json` 是 173 条，
   源码里 `Route::post('', 'SkinlibController@handleUpload')` 被漏记，实际 **174 条**。
2. **数据完全兼容**：17 张表 + 存量数据 + bcrypt 密码哈希（含 `$2y$`）+ SHA-256 纹理 hash。
3. **插件 API 契约**：Hook（7 方法）、PluginManager（13 方法）、Plugin、24 个 Event。
4. **patch 定制功能**：6.0.3 ~ 6.0.11_patch 全部保留。
5. **前端直接复用**：`public/app/` 原样拷贝到 `wwwroot/app/`，不重新打包。

---

## 2. 技术栈（AOT 优先）

| 组件 | 选型 | 说明 |
| --- | --- | --- |
| 语言 / SDK | C# 14 / .NET 10 | `PublishAot=true` |
| Web | ASP.NET Core minimal API + **自研路由表** | 对应 Java `VertxRouteRegistry`：按 `routes.json` 逐条注册，中间件组按 Laravel 语义展开 |
| 数据访问 | **ADO.NET**（Microsoft.Data.Sqlite / MySqlConnector / Npgsql / Microsoft.Data.SqlClient） | 手写 `SchemaInstaller` + `SqlDialect`，对应 Java 的 `db/*` |
| 模板 | **自研 Thymeleaf 子集渲染器** | 逐字复用 Java 版 86 个已验证 HTML 模板（`th:text/utext/each/if/unless/block/replace/insert/attr/...`） |
| OAuth2 | 自研 Passport 兼容实现 | 与 Java 版同构：`oauth_clients/auth_codes/access_tokens/refresh_tokens/personal_access_clients` |
| 密码 | BCrypt.Net-Next + Konscious Argon2 | `$2y$` 兼容；`password_verify` 算法无关（bcrypt 与 argon2i/id 互认） |
| JSON | **System.Text.Json source generators** | 业务侧统一用 `PhpJson`（PHP 字节语义） |
| 时间 | **NodaTime** | IANA tzdata；Windows `TimeZoneInfo` 没有历史偏移（上海 1901 年前 +08:05:43） |
| YAML | **YamlDotNet**（节点解析）+ 自研 Symfony 标量规则 | 语言包 `Resources/lang/*.yml` |
| 图像 | ImageSharp / 自研 PNG 编码 | 目标：与 PHP Imagick 输出**字节一致**（Java 版已做到） |
| 缓存 / 会话 | file / redis / memcached / dynamodb | 对应 Java 版实现 |
| 插件 | **编译期契约 + 内置插件** | AOT 无 JIT，不能运行时加载插件 DLL（见 §8） |
| 解决方案 | `.slnx` | `src/NeoSkin.Web` + `tests/NeoSkin.Tests` |

### 2.1 为什么不用 EF Core / Razor / OpenIddict

| 候选 | 问题 |
| --- | --- |
| EF Core | 运行时表达式树 / 动态代理 / 反射映射；Native AOT 下需要 compiled model 且限制多，且 5 种数据库方言难与 PHP migration 逐字对齐 |
| Razor（MVC 视图） | 官方 AOT 支持集中在 minimal API；视图运行时编译与 AOT 冲突。改复用 Java 版已验证 HTML + 子集渲染器，保真度更高 |
| OpenIddict | 大量反射与动态注册，AOT 不友好；且 Passport 的请求/响应形状需要自定义端点，直接自研更可控 |
| `AssemblyLoadContext` 插件 | AOT 编译产物没有 JIT，无法加载 IL 插件程序集 |

---

## 3. 源系统盘点（PHP）

| 维度 | 数量 |
| --- | --- |
| 数据表 | 17（另有 Laravel 自用的 `migrations`） |
| 路由 | **174**（codegraph 173 + 源码漏记的 `POST skinlib`） |
| 控制器 | 20（另有 1 个基类 `Controller`） |
| 中间件 | 12 |
| Event | 24 |
| Listener | 9 |
| Service | 11（另有 9 个 `Services\Cipher\*` 算法类） |
| Twig 模板 | 104（另有 5 个 Blade：`master` / `mails`） |
| 语言包 | 14 |

---

## 4. 解决方案结构

```
NeoSkin.NET/
├── NeoSkin.NET.slnx
├── Directory.Build.props            # net10.0 / nullable / ICU（不做 InvariantGlobalization）
├── src/NeoSkin.Web/
│   ├── NeoSkin.Web.csproj           # PublishAot=true，AssemblyName=neoskin
│   ├── Program.cs                   # minimal API + 路由表装配
│   ├── Support/                     # PHP 语义层（PhpJson / PhpSerialize / PhpDate / ...）
│   ├── Db/                          # SchemaInstaller、SqlDialect、各 Repository
│   ├── Web/                         # 路由表、中间件链、控制器、视图渲染
│   ├── Services/                    # Option / Cipher / Cache / Storage / Mail / Plugin ...
│   └── wwwroot/app/                 # 预编译前端（构建时从源项目复制）
└── tests/NeoSkin.Tests/
    ├── Conformance/Corpus.cs        # 读取 ../conformance/data/*.json
    └── Support/ ...                 # 语料驱动测试
```

构建 / 验证：

```sh
dotnet build NeoSkin.NET.slnx
dotnet test
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
```

---

## 5. 数据层

### 5.1 表结构

`SchemaInstaller` 按 `database/migrations/` 的**最终结果**逐表建 DDL（16 张业务表 +
`migrations` 记账表），字段名/类型/默认值/索引与 PHP 对齐；`SqlDialect` 负责 5 种数据库的
差异（自增列、`TEXT`/`VARCHAR`、布尔、时间戳、UUID、分页、`LIMIT`/`TOP`）。

**已知偏差**：不回放 27 个 migration 的 `alter table` / SQLite 临时表重建，而是直接建最终
结构。表结构语义一致（由 `conformance/data/schema.json` 的 `final_tables` 与 `quirk.*`
锁定），但 `ddl.*`（迁移执行序列的逐条 SQL）与本实现的语句序列不同。另：`users.locale` /
`users.is_dark_mode` 只在 MySQL/MariaDB 插到 `after` 位置，其余方言按 PHP 实际结果落在表尾。

### 5.2 密码

BCrypt.Net-Next；校验兼容 `$2y$`（Laravel 默认）与 `$2a$`/`$2b$`。
与 PHP 的 `password_verify` 一样，**算法无关**地校验 `$argon2i$` 等存量哈希
（见 conformance `hash.json`）。

### 5.3 迁移回写

`DataMigrator` 对应 PHP 的升级流程；JSON 回写只开 `JSON_UNESCAPED_UNICODE`，
斜杠仍转义（`PhpJson.JsonUnescapedUnicode`）。

---

## 6. 接口层

### 6.1 路由

- 以 `codegraph/routes.json`（174 条）为输入，启动时注册。
- 匹配语义对齐 Laravel：`{param}` 占位、`{player}.json` 后缀、可选参数、405/404 区分。
- `--route-list-json` 提供等价于 `php artisan route:list --json` 的输出（前端 `yarn build:urls` 用）。

### 6.2 中间件

12 个中间件 + web/api/authorize 组，顺序对齐 `bootstrap/app.php`；
包括 Passport 的 `CreateFreshApiToken`（种 `laravel_token`）与 CSRF（419）。

### 6.3 Minecraft 客户端契约

`GET /{player}.json`、`GET /csl/{player}.json`、`/textures/*`、`/avatar/*`、`/preview/*`
的字段名、Content-Type 与字节输出必须与 PHP 一致。

---

## 7. 认证与 OAuth2

- 会话：cookie 名、加密（AES-256-CBC + MAC）与序列化格式对齐 Laravel。
- Passport 兼容端点：`/oauth/authorize`、`/oauth/token`、`/oauth/token/refresh`、
  `/oauth/personal-access-tokens` 等；请求/响应形状与 Java 版一致（Java 版已与 PHP 对齐）。
- Scope 表与中间件（`CheckToken` / `CheckTokenForAnyScope`）。

---

## 8. 插件系统（AOT 约束）

AOT 产物没有 JIT，**无法在运行时加载 IL 程序集**。因此：

- 保留完整契约：`Hook`（7 方法）、`PluginManager`（13 方法）、`Plugin`、24 个 Event；
- 插件通过**编译期注册**（`IPlugin` 实现 + 源生成/显式注册表）进入应用；
- 「插件市场 / 上传 zip / 启用禁用」仍管理 `plugins/` 目录与状态文件，
  但 AOT 构建只执行编译进来的插件；JIT 构建（`dotnet run`）可选支持动态加载；
- 该偏差记录在 `PORT-NOTES.md`，并在 README 的「已知限制」里说明。
- **运行时插件方案**：已调研并写入 [`PLUGIN-DESIGN.md`](PLUGIN-DESIGN.md)——
  推荐跨平台原生 C ABI（`NativeLibrary` + `UnmanagedCallersOnly`），
  Windows-only 备选为 COM + `ComWrappers` 源生成；当前阶段仅设计，不实现加载。

---

## 9. 前端与视图

### 9.0 前端契约（不可改）

复用 `public/app/`；以下 DOM/JS 契约必须逐字保住：
`window.blessing`、`#blessing-extra`、`#logout-button`、`data-mark="nickname"`、
`.locale`、`<main></main>`、`data-toggle` / `data-communication-*` 等。
Twig 模板是规格说明书。

### 9.1 视图渲染

- **直接复用 Java 版 86 个 HTML 模板**（它们已经被浏览器 E2E 验证过），
  在 .NET 侧实现 Thymeleaf 子集渲染器：
  `th:text` / `th:utext` / `th:each`（含 status 变量）/ `th:if` / `th:unless` /
  `th:block` / `th:replace` / `th:insert` / `th:fragment` / `th:attr` /
  `th:href` / `th:src` / `th:action` / `th:value` / `th:checked` / `th:selected` /
  `th:classappend` / `th:class` / `th:switch` / `th:case` / `th:inline`。
- 表达式子集：属性/索引访问、`view.*` 方法、`#lists` / `#strings` 工具、
  `+` 拼接、比较、`and/or/!`、三元、`|...|` 字面量替换、`~{tpl :: frag(args)}`。
- 输出必须与 Java 版逐字节一致（用快照/E2E 校验）。

### 9.2 静态资源

`wwwroot/app/` + `/lang/<locale>.js` 运行时生成；`debug=true` 时指向
`localhost:8080` 的 webpack dev-server（与源行为一致）。

---

## 10. 国际化

- 语言文件 `resources/lang/*.yml`（14 种语言）→ 保留 YAML 结构与键；
- `language_lines` 表（DB 覆盖）优先于文件；
- `/lang/<locale>.js` 生成规则与 Java 版一致。

---

## 11. 分阶段计划与状态

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| Phase 0 | `.slnx` + 两个项目 + Native AOT publish 跑通 + Corpus 读取 | ✅ 完成（9.7 MB 原生二进制，探活 200） |
| Phase 1 | PHP 兼容层：Json / Serialize / Unserialize / Base64 / Date / String / Arr / Url / Yaml / VarExport | ✅ 完成（81 tests，语料驱动） |
| Phase 2 | SchemaInstaller + SqlDialect + 16 业务表 + migrations 记账 + `UserRepository` + bcrypt/argon2（其余 Repository 随控制器补） | ✅ 完成 |
| Phase 3 | 路由表 + 分发器 ✅；中间件链装配（cookie/session/CSRF/语言）✅；错误响应：JSON 契约 ✅、HTML 错误页待模板引擎（Phase 7） | ✅ 完成（HTML 错误页挂 Phase 7） |
| Phase 4 | 认证中间件 + SessionGuard ✅；AuthController 全流程 ✅；OAuth2（Passport：token/authorize/refresh/clients/tokens/personal/scopes + Bearer 与 scope 校验）✅ | ✅ 完成（授权页 HTML 待 Phase 7 换模板） |
| Phase 5 | 控制器（用户 / 皮肤库 / 纹理 / 玩家 / 衣柜 / 举报） | ✅ 完成：玩家/纹理契约/用户中心/衣柜/皮肤库/举报/上传/2D+3D 头像/皮肤+披风预览 |
| Phase 6 | 管理面板 + 安装向导 + 更新/插件管理 | ✅ 完成（更新管理、选项、插件、市场、通知、安装向导） |
| Phase 7 | Thymeleaf 子集渲染器 + 86 模板 + 静态资源 | ✅ 引擎 + 86/86 模板全部接入（含安装向导/错误页/OAuth 授权） |
| Phase 8 | 插件契约（编译期）+ 事件总线 | 🚧 清单层 + ZIP 安装 + 市场 ✅；原生插件 ABI ✅；全部 Java Hook + 语言包 ✅ |
| Phase 9 | 全量测试（语料 + HTTP 契约 + 浏览器 E2E）与 AOT 回归 | 🚧 431 tests + AOT 回归 ✅；浏览器 E2E 待做 |

---

## 12. 验证清单

```sh
dotnet build NeoSkin.NET.slnx                 # 无错误、无 AOT/trim 警告
dotnet test                                    # 语料 + 单元 + 契约测试
dotnet publish src/NeoSkin.Web -c Release -r win-x64 -o publish/win-x64
./publish/win-x64/neoskin.exe                  # 原生二进制可启动
```

- [ ] 174 条路由全部注册且行为一致
- [ ] 17 张表 migration 与 PHP 一致，存量数据可读
- [ ] bcrypt（含 `$2y$` / 历史 argon2 哈希）校验通过
- [ ] OAuth2 端点与 Passport 响应一致
- [ ] Minecraft 客户端可正常获取皮肤/披风
- [ ] 纹理/预览 PNG 与 PHP Imagick 字节一致
- [ ] conformance 语料全部通过
